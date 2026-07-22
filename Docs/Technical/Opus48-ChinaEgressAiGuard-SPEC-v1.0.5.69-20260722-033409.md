# 中国大陆出口 AI 访问保护实施规格（China-Egress AI Guard SPEC）

- 版本：1.0.5.69
- 生成模型：Claude Opus 4.8
- 生成时间：2026-07-22T03:34:09+09:00（UTC 2026-07-21T18:34:09Z）
- 主题：出口 IP 为中国大陆时，阻止本程序自身访问 Anthropic/OpenAI 并全屏警告用户

---

## 1. 场景与目标

用户长期在日本使用本机（出口=日本 IP，合规访问 Anthropic/OpenAI 正常），偶尔把本机带回中国大陆。届时出口变为大陆 IP，从大陆 IP 访问 Anthropic/OpenAI 违反其用户协议。用户无代理，目标是**防止自己在大陆无意中触发访问**。

用户在三个方案中选择了**最小方案**：只做

1. **widget 自锁**：出口=大陆时，本程序停止它自己发往 Anthropic/OpenAI 的全部轮询（额度/状态雷达）。本程序是唯一确定已安装且开机自启、会主动访问这些主机的 AI 程序，它自己就是首要的意外违规源。
2. **全屏警告**：出口=大陆时弹出挡不住的全屏警告，提示用户手动断网或改接境外网络；不代替用户拦截别的程序。

**不做**：不改 hosts、不装提权帮手、不拦别的进程。这三项属于被否决的更重方案。

## 2. 设计原则

- **按出口 IP 国别触发，不靠手动开关**：手动开关会被忘记，正好是要防的事故。判据是出口 IP 国别（服务商实际看到、决定是否违规的那个事实）。
- **Fail-closed（自锁方向）**：只有在**确认出口不是中国大陆**时才允许本程序访问 AI；出口=大陆、出口未知、或数据过期，一律自锁。日本冷启动时首个定位结果返回前会短暂自锁（仅影响本程序自己的雷达，几秒，无害）。
- **警告只在正向确认时弹（精确方向）**：仅当确认出口=大陆，或 GFW 探测确认在墙内时才弹全屏警告；"未知"不弹，避免日本每次启动误报。
- **一次设定，长期自动**：设置项持久化，日本为无操作，回国自动生效，无需每次记得。

## 3. 现状约束（已核实）

- 本程序以普通用户权限运行（无 manifest 提权，开机项 `HKCU:\...\Run`）——写不了 hosts，也不在网络路径上。故最小方案只覆盖本程序自身。
- 出口国别数据已有来源：`CleanIpConnectionReader` 解析 cleanip.io 的 `geo.country`（当前仅拼进 `Location` 显示串）。cleanip.io 非 AI 服务商主机，从大陆调用它本身不构成违规；若它在大陆也不可达则返回"未知"→ fail-closed 自锁。
- GFW"墙内"信号已存在：`NetworkMonitorReader` 计算 `insideWall` 并调用 `AiRequestProtection.UpdateGfwSignal`。可作为出口未知时的警告备用信号。
- `AiRequestProtection.ShouldBlock` 已是本程序 6 个自身 AI 请求（Claude/Codex 额度、Claude/OpenAI 状态页）的统一门控点。本 SPEC 在其中新增一路条件即可覆盖全部。

## 4. 交付项

### 4.1 出口国别（`Performance/PdhModels.cs` + `Performance/CleanIpConnectionReader.cs`）

- `CleanIpConnectionSnapshot` 新增 `CountryRaw`（string，来自 `geo.country` 原值），加入 `Clone`、测试快照。
- 静态判定 `AiRequestProtection.IsMainlandChinaEgress(string country)`：大小写无关匹配 `cn` / `china` / 含 `中国`，且**排除** 香港/澳门/台湾（`hong kong`/`macau`/`macao`/`taiwan`/`hk`/`mo`/`tw`/`香港`/`澳门`/`台湾`）。

### 4.2 设置（`Settings/WidgetSettings.cs` + `Settings/Win11SettingsForm.cs`）

- 新增 `AiChinaEgressGuardEnabled`（bool，**默认 true**）。覆盖 defaults / clone / load / save / normalize / 设置 UI 分组 / migration / `--test-settings-bindings`。
- 缺键的既有配置经 Load 落到默认值 true（无需专门迁移分支，沿用现有"defaults 起底再覆盖"路径）。

### 4.3 保护逻辑（`Core/AiRequestProtection.cs`）

- 新增静态状态与 `UpdateEgressSignal(bool egressKnown, bool mainlandChina, string country, DateTime nowUtc)`；带 TTL（沿用 6h 量级，实际每 tick 刷新）。
- `ShouldBlock` 在原有 manual / GFW-auto 两路之外新增第三路：当 `settings.AiChinaEgressGuardEnabled` 且 **非** `IsEgressConfirmedOutsideChina()` 时返回 true，reason=`"中国大陆出口保护"`。
  - `IsEgressConfirmedOutsideChina()` = 出口已知 且 非大陆 且 非 GFW-墙内 且 数据新鲜。
  - 即：出口=大陆 / 未知 / 过期 / GFW-墙内 → 自锁（fail-closed）。
- 新增 `ShouldWarnChinaEgress(WidgetSettings, out reason)`：`AiChinaEgressGuardEnabled` 且（确认大陆 或 GFW-墙内确认）。供警告窗使用。
- 新增自测覆盖：日本出口不锁不警告；大陆出口锁且警告；未知出口锁但不警告；GFW-墙内锁且警告；HK/TW 出口不判为大陆；guard 关闭时回到原有行为。

### 4.4 全屏警告窗（`Core/ChinaEgressWarningForm.cs`）

- 全屏、置顶、红底大字的 `Form`（`FormBorderStyle.None`，覆盖工作区，`TopMost`）。内容：说明当前处于大陆网络、本程序已停止访问 Anthropic/OpenAI、请勿手动访问、断网或改接境外网络后消失。
- 一个"暂时隐藏"按钮：隐藏约 60s 冷却；条件仍成立则再次弹出。非模态、不锁死系统（用户选的是"警告"而非"锁死"）。

### 4.5 中央驱动（`Core/WidgetForm.cs`）

- 在 `OnTimerTick` 每 tick（`CleanIpConnectionReader.Shared` 自带节流，调用廉价）拉取出口快照，算出 `egressKnown/mainlandChina`，调用 `AiRequestProtection.UpdateEgressSignal(...)`。
- 据 `AiRequestProtection.ShouldWarnChinaEgress(...)` 显示/隐藏 `ChinaEgressWarningForm`（尊重冷却）。
- guard 开启会使 cleanip.io 定位在任何窗口未开时也按其既有节流发生——须在刷新规则文档登记。

## 5. 验证要求

1. `AiRequestProtection.RunSelfTest`（纯函数）：§4.3 六个场景断言。
2. `--test-settings-bindings` 覆盖 `AiChinaEgressGuardEnabled`。
3. `--test` / `--test-layout` / `--test-operation-panel` 全绿。
4. 手动构造：设置 guard 开、注入出口国别=CN，确认 `ShouldBlock` 对 6 个 URL 全 true 且 `ShouldWarnChinaEgress` 为真；出口=JP 时两者皆假。
5. 按根 `AGENTS.md`：构建 ARM64 → 备份 → 覆盖 → 重启。

## 6. 文档同步

- `Docs/NetworkMonitor-Architecture.md`：AI 请求保护新增中国出口 fail-closed 一节（若 AI 保护 owner 在此文；否则记入对应 owner）。
- `Docs/Component-Refresh-Rules.md`：guard 开启时 cleanip 定位的驱动来源与节流。
- `Docs/Indexes/FEATURE_INDEX.jsonl`：新增中国出口保护功能行。
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`：`AiChinaEgressGuardEnabled` 设置、`ChinaEgressWarningForm`。
- `Docs/Maintenance/CHANGELOG.jsonl`：一条 `feature` + 一条 `deployment`。
- 根 `AGENTS.md`：`Current version` → 1.0.5.69。
