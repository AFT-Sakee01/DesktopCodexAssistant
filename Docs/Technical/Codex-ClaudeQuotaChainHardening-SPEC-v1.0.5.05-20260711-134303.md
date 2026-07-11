# Claude Quota Chain Hardening SPEC

## Metadata

- Document: `Codex-ClaudeQuotaChainHardening-SPEC-v1.0.5.05-20260711-134303.md`
- Generated model: Claude Fable 5
- Timestamp local: `2026-07-11T13:43:03+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.5.05`
- Target implementation version: `1.0.5.07`（在 `Codex-RadarHardeningFollowUp-SPEC-v1.0.5.05-20260711-132944.md`（1.0.5.06）之后串行实施——其 Phase 6 与本 SPEC Phase 4 相邻，见衔接说明）
- Status: approved（用户 2026-07-11 批复：站点来源额度的重置时间变红 + 链路审计修复一并成篇）
- Source: Claude 个人额度获取链路完整性审计（2026-07-11）

## Goal

让 Claude 额度环的每一个显示值都能回答"这是谁的额度、多新鲜"：站点公开额度获得红色重置时间标记，个人链路（statusline 缓存 / setup-token + oauth/usage / Messages 头兜底）的来源顺序、新鲜度门槛、无 token 短路与 token 失效识别全部补齐。

## 链路现状（审计结论）

个人额度链路设计上是三层完整的，但存在 5 个缺陷：

```
触发（两窗 TryStartOrJoin 单飞节流：定时间隔/前台软件切换/强制刷新）
  → ClaudeCodeUsageReader.Read：
      ① statusline 缓存（≤360 分钟新鲜度门槛；桌面端无终端 CLI 时恒空）
      ② 首次失败时安装 statusline 桥（进程内防重）再读一次
      ③ 有 setup-token → oauth/usage（免费权威源）；reset 缺失或失败 → Messages 头兜底（花微量额度）
      ④ 全无 → NO_STATUSLINE_CACHE 错误
  → 成功 → TryWriteClaudeCodeQuotaCache（个人额度缓存）
  → ClaudeRadarReader 抓站点时用个人缓存覆盖 snapshot.Quota（ClaudeCodeState=Normal）
  → 两窗环显示 snapshot.Quota / quota.ini / TryReadClaudeRadarPublicQuotaSnapshot 兜底
```

### F1（修复，用户拍板）：站点公开额度冒充个人额度且无任何标记

无 token 时个人链路全断，环上显示的是站点 JSON `quota.usage[]`（`ParseQuotaSnapshot`，`Core/ClaudeRadarReader.cs` 约 1522 行）——**站点发布方自己账号的公开额度**。`ClaudeCodeState` 字段能区分个人/未知，但环渲染不消费它，个人与站点额度视觉完全相同。共享窗口 `TryReadClaudeRadarPublicQuotaSnapshot`（`Core/CodexRadarForm.cs` 约 14403 行）兜底同样无标记。

### F2（修复）：来源优先级反了——陈旧 statusline 缓存压过权威 oauth

`Read()` 先读 statusline 缓存再考虑 token；曾在终端跑过 CLI 的机器上，≤6 小时旧的 statusline 快照会持续压过本可实时的 oauth 数据。代码注释自认 oauth 是权威源（"community monitors use the OAuth usage endpoint as the authoritative source"），顺序应对调。

### F3（修复）：个人额度缓存无新鲜度门槛

`TryReadClaudeCodeQuotaCache`（`Core/ClaudeRadarReader.cs` 约 1576 行）解析了 `SourceUpdatedUtc` 却不校验年龄——几天前的个人缓存照样覆盖站点额度并以"个人额度"面目展示，同样不准确。

### F4（修复）：无 token 时链路空转与错误码混淆

无 token 时每轮完整走"读缓存失败→桥检查→再失败"，以 `NO_STATUSLINE_CACHE` 报错——该错误码描述的是手段（statusline 缓存缺失）而非根因（未绑定 token），UI 与日志都无法引导用户去绑定。1.0.5.06 Phase 6 只做日志降噪，未解决语义。

### F5（修复）：oauth 认证类失败仍走 Messages 头兜底

`ReadViaSetupToken` 中 oauth 失败（含 401/403 token 失效/吊销）会无差别转 Messages 头兜底——token 坏时兜底同样必败，还可能重复计费尝试；且 token 失效没有专属错误码，设置页无从提示"重新绑定"。

### F6（小修）：来源切换时 delta 基线不重置

个人额度成功过一次后 token 失效回落站点额度（或反向），Claude family 的额度 delta 跟踪把两个不同账号的数值当同一序列比较，产生虚假的消耗/回升判定。Claude 链路无 Codex 式窗口身份判别，来源切换时应重建基线。

## Implementation Plan

### Phase 0: Baseline Checks

```powershell
rg -n "Version = \"1\.0\.5\.0[56]\"" Core\ProductIdentity.cs
rg -n "QuotaSourceKind|quota_source" Core\ClaudeRadarReader.cs Core\CodexRadarForm.cs
rg -n "TOKEN_INVALID" Core\ClaudeCodeUsageReader.cs
```

后两条实施前应无结果；确认 1.0.5.06 已完成部署（其 Phase 6 决定本 SPEC Phase 4 的落点）。

### Phase 1: 站点来源额度的红色重置时间（F1）

1. `ClaudeRadarQuotaSnapshot` 新增 `Source` 字段（`"personal"` / `"site"` / 空 = 未知）：`ParseQuotaSnapshot` 产出标 `site`；`TryReadClaudeCodeQuotaCache` 覆盖时标 `personal`。
2. `CodexQuotaSnapshot` 传播：`TryReadClaudeRadarPublicQuotaSnapshot` 输出经 `MarkQuotaSnapshotSource(snapshot, "claude_site_public")` 标记；个人链路（scheduler 成功路径）维持既有 sourceKind。
3. 环渲染（两窗调用方，不动 `QuotaRingPresentation` 共享层）：来源为站点公开额度时，5h/weekly 两环的 `ResetDisplayText` 颜色改为 `DesignTokens.Colors.Danger`（alpha 245），并置 `ForceResetDisplayColor = true`（复用 1.0.5.00 机制，防止 `!Running` 灰化覆盖红色）；个人来源保持现状颜色。
4. 适用范围：独立 `ClaudeRadarForm` 与共享窗口 Claude family 两处环。

约束：只改重置时间文字颜色，不改额度数字、环几何与其余配色；Codex family 不受影响。

自测：站点源快照 → 两环 ResetDisplayColor 为 Danger 且 Force 位真；个人源 → 原色。渲染样本：新增或复用 Claude 环样本各截一张站点源/个人源对照。

验收：

```powershell
rg -n "claude_site_public|Source = \"site\"|Source = \"personal\"" Core
```

命中标记产出与两处环调用方；渲染样本红字可见。

### Phase 2: oauth 优先（F2）

`ClaudeCodeUsageReader.Read` 顺序改为：token 已配置 → 先 `ReadViaSetupToken`，其失败（非认证类）时才回落 statusline 缓存（含桥安装分支）；无 token → 维持 statusline 路径。statusline 的 360 分钟新鲜度门槛不变。

自测：mock token 存在 + statusline 缓存同时可用 → 返回 oauth 结果；oauth 网络失败 + statusline 新鲜 → 回落 statusline；两者皆无 → 错误码见 Phase 4。

### Phase 3: 个人额度缓存新鲜度门槛（F3）

`TryReadClaudeCodeQuotaCache` 增加 `PersonalQuotaCacheMaxAgeMinutes = 360`（常量，与 statusline 门槛对齐）：`UpdatedAtUtc` 缺失或超龄 → 返回 false → 展示自动降级站点源（Phase 1 红字随之生效）。

自测：构造 361 分钟前的缓存 → 拒读；359 分钟 → 接受；无 `SourceUpdatedUtc` 行 → 拒读。

### Phase 4: 无 token 短路与 token 失效识别（F4、F5）

1. `Read()` 无 token 且 statusline 不可用时，错误码改为 `NO_SETUP_TOKEN`（语义：未绑定），不再报 `NO_STATUSLINE_CACHE`；若 1.0.5.06 Phase 6 已实施日志降噪，保持其"同一错误码只记首次"行为，仅替换错误码。
2. `FetchUsageEndpoint` 对 401/403 返回专属错误码 `TOKEN_INVALID`，此时**不再**走 Messages 头兜底（认证坏时兜底必败且可能计费）；其余失败维持兜底。
3. 设置页 `Claude Code 用量令牌` 按钮态（`GetClaudeSetupTokenButtonText`/`GetClaudeSetupTokenAccentColor` 既有机制）在最近一次结果为 `TOKEN_INVALID` 时显示"令牌已失效，请重新绑定"提示色。

自测：401 响应 mock → `TOKEN_INVALID` 且无 header 兜底调用；网络超时 → 仍走兜底；无 token → `NO_SETUP_TOKEN`。

### Phase 5: 来源切换基线重置（F6）

Claude family 的额度应用路径检测 `SourceKind` 变化（`claude_site_public` ↔ 个人来源）时，调用既有 `InitializeQuotaReadDeltaTracking` 重建基线，并在决策日志 detail 记 `source_switch`。

自测：站点源样本序列后接个人源样本 → 基线重建、无"消耗/回升"误判 reason。

### Phase 6: 版本与文档同步

版本 → `1.0.5.07`，同步：

- `Core/ProductIdentity.cs`、`AGENTS.md`
- `Docs/Fable5-Data-Sources-And-Caching-Technical.md`：Claude 额度链路图（本 SPEC"链路现状"节收编）、来源标记与新鲜度规则
- `Docs/Codex-ClaudeRadar-Architecture.md`：红色重置时间的含义（站点公开额度）写入显示语义章节
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`：Claude 用量读取器条目（来源顺序、错误码、缓存门槛）
- `Docs/Maintenance/CHANGELOG.jsonl` change + deploy 记录
- `Docs/Technical/INDEX.jsonl`：登记本 SPEC 与对应 GoalSpec

## Verification Matrix

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-claude-quota-chain-v1.0.5.07.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-claude-quota-chain-v1.0.5.07.exe --test
.\_build\DesktopCodexAssistant-arm64-claude-quota-chain-v1.0.5.07.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-claude-quota-chain-v1.0.5.07.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-claude-quota-chain-v1.0.5.07.exe --test-radar-display-lifecycle --iterations 100
```

渲染样本：Claude 环站点源（红字）/个人源（原色）对照留档。人工验收（本机当前即"未绑定"状态，天然测试环境）：部署后共享窗口 Claude 模式与独立窗口的重置时间均为红色；绑定 setup-token 后转回原色且数值变为本人额度；日志出现 `NO_SETUP_TOKEN` 而非 `NO_STATUSLINE_CACHE`。

全部通过后按正式部署流程（Release 复测 → 备份 → D/E 双路径 → 重启 D 实例 → 三方核对 → CHANGELOG deploy 记录）。

## Acceptance Summary

| 项 | 验收条件 |
| --- | --- |
| F1 红色标记 | 站点源两环重置时间 Danger 红 + Force 位自测通过；两窗渲染样本对照留档；个人源原色 |
| F2 oauth 优先 | mock 自测三场景通过；token 机器上 statusline 陈旧数据不再压过 oauth |
| F3 缓存门槛 | 361/359 分钟边界自测通过；超龄缓存降级站点源且带红字 |
| F4 短路与错误码 | 无 token → `NO_SETUP_TOKEN`；401/403 → `TOKEN_INVALID` 且不走头兜底；设置页失效提示 |
| F5 兜底收紧 | 认证类失败无 Messages 头调用的自测通过 |
| F6 基线重置 | 来源切换后无虚假 delta reason，detail 含 `source_switch` |
| 发布 | 版本 `1.0.5.07` 三方 SHA256 与版本一致，D 实例重启后 Responding |
