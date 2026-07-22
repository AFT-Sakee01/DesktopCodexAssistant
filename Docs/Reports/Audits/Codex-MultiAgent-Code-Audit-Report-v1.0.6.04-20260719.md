# DesktopCodexAssistant 多代理代码审查报告

报告类型：安全、可靠性、性能与 SPEC 完成后回归审计  
适用版本：1.0.6.04  
生成时间：2026-07-19 23:21:39 +09:00  
项目路径：`D:\E_Drive_Files\Codexproject\desktopdata\DesktopCodexAssistant`  
审查基线：`a1adccfcca7f781775c31e008ede3cf74e0b43df` 加当前未提交工作树  
目标 SPEC：`Docs/Technical/Fable5-ReviewFindingsRemediation-SPEC-v1.0.6.02-20260719-171953.md`  
生成模型：Codex  

> 本报告是一次性审计快照。代码定位以“文件 + 类/函数/常量”为准，行号仅对应报告生成时的工作树，后续修改后可能漂移。

## 1. 执行摘要

本次使用六个只读审查代理，分别从以下方向检查项目：

1. 安全、凭据与外部输入边界。
2. 并发、生命周期、异常恢复与日志可靠性。
3. 网络探测调度、缓存、切网时序与超时边界。
4. WinForms、Layered Window、GDI 资源与窗口交互性能。
5. 设置绑定、迁移、多显示器与停靠布局。
6. SPEC 验收、发布产物、文档 Gate 与测试覆盖。

主线程同时对当前差异和高风险路径进行了交叉核对。审查覆盖 107 个 C# 文件及当前 27 个未提交文件变化。

结论：

- 未发现可直接判定为 P0 的问题。
- 确认 8 项 P1，建议在下一次正式发布前修复。
- 确认 12 项 P2 和 2 项 P3，可按模块分批处理。
- 本次 SPEC 的主要风险不是编译失败，而是“孤立自测通过，但真实生产链路仍断开或互相矛盾”。
- 当前原子设置保存、本地化 `JavaScriptSerializer`、GUARD Version 80 属性迁移和固定命令型提升路径未发现新的高置信度缺陷。

## 2. P1：发布前建议修复

### P1-01 Claude OAuth 令牌被明文写入 DPAPI 文件

定位：

- `Settings/Win11SettingsForm.cs`：`TrySaveClaudeSetupTokenFile`，当前约第 1127-1152 行。
- `Core/ClaudeCodeUsageReader.cs`：`ReadConfiguredSetupToken`，当前约第 1478-1497 行。

现象：设置窗口将令牌通过 `File.WriteAllText` 直接写入 `claude-code-oauth-token.bin`，读取端却将同一文件按 DPAPI Base64 内容调用 `SecretStore.TryReadOrMigrateSecret` 解密。

影响：

- 用户令牌以明文留在 LocalAppData，可被同用户进程、备份或诊断包读取。
- 后续读取会因格式错误返回空令牌，设置界面保存入口实际失效。

测试遗漏：现有测试只直接验证 `SecretStore`，没有经过设置窗口的真实保存入口。

建议：保存统一调用 `SecretStore.WriteSecret`；清除统一调用 `DeleteSecretFiles`。测试必须覆盖实际设置保存、磁盘无明文、DPAPI 往返和旧文件清理。

### P1-02 致命异常的 15 分钟重启抑制跨进程失效

定位：

- `DesktopCodexAssistant.cs`：`lastFatalExceptionUtc`、`ShouldRestartAfterFatalException`、`OnCurrentDomainUnhandledException`，当前约第 34、615、646 行。

现象：最近一次致命异常时间只保存在进程静态字段中。新进程启动后字段重新变为 `DateTime.MinValue`，每次确定性异常都会被当作首次崩溃。

影响：持续异常时可形成无限退出、重启和日志写入链。

测试遗漏：当前自测只向纯函数传入人工时间，没有模拟两个独立进程。

建议：原子持久化重启时间或重启预算，并在稳定运行一段时间后清除。子进程测试应验证 15 分钟内第二个进程不会再启动后继。

### P1-03 Spec Board 外部数据边界仍可绕过

定位：

- `Settings/SpecBoardManagerForm.cs`：`OpenSelectedFile`，当前约第 1418-1424 行。
- `Core/SpecBoardReader.cs`：`SpecBoardRow.AbsolutePath`、`NormalizeRelativePath`，当前约第 29-40、401-404 行。
- `Core/SpecBoardForm.cs`：`ResolveOpenTarget`，当前约第 1789-1828 行。
- `Core/SpecBoardLedgerStore.cs`：`TryRemoveRowAndRecycleFile`，当前约第 94-125 行。

确认的三条路径：

1. 管理窗口只检查文件存在，随后直接 `UseShellExecute=true`，没有使用新白名单。
2. `spec_path` 未拒绝 `..`、绝对路径、UNC 和设备路径，规范化后可越出项目根目录。
3. 紧凑窗口回退接受 `File.Exists(projectRoot)`，受污染的 `PROJECTS.json` 可将可执行文件作为回退目标。

影响：外部 AI 或被篡改账本可诱导用户执行 `.exe/.bat/.cmd/.lnk`，或在危险区确认后回收项目目录外文件。

建议：建立唯一共享的安全路径解析器；只接受项目根内的允许文档类型；回退只允许目录；删除操作再次执行根目录包含关系验证。增加遍历、绝对路径、UNC、设备路径、重解析点和文件型 project root 测试。

### P1-04 全屏或显示挂起后停靠标签可被设置重载重新显示

定位：

- `Core/EdgeDockTabForm.cs`：`ShowTab`、`SetDisplaySuspended`，当前约第 252-304 行。
- `Core/SpecBoardForm.cs`：`SyncLeftDockTab`，当前约第 175-197 行。
- `Core/GuardBoardForm.cs`：`SyncLeftDockTab`，当前约第 169-191 行。
- `Core/NetworkMonitorForm.Dock.cs`：`SyncLeftDockTab`，当前约第 78-102 行。

现象：`ShowTab` 不检查 `displaySuspended`，仍调用 `Show()`、`SetWindowPos(...SWP_SHOWWINDOW)` 并启动 120ms 悬停计时器。各 owner 又采用“先设置挂起、随后调用 ShowTab”的同步顺序。

影响：进入全屏后发生设置热加载或状态重算时，标签可能重新覆盖全屏程序；隐藏标志没有变化时，后续隐藏调用还可能提前返回。

建议：挂起时 `ShowTab` 只能更新锚点，必须保持隐藏并停止计时器；为四种角色增加 `Hide/Suspend -> Sync -> ShowTab` 窗体级测试。

### P1-05 GUARD 状态持久化会提交尚未保存的设置预览

定位：

- `Settings/Win11SettingsForm.cs`：`OnPreviewTimerTick`，当前约第 2588-2595 行。
- `Core/OperationForm.GuardBoard.cs`：`PersistGuardStateFromBoard`，当前约第 149-172 行。
- `Core/GuardBoardForm.cs`：`PersistRuntimeState`、`OnMaintenanceTick`。

现象：设置预览会替换 `OperationForm.CurrentSettings`。GUARD 状态变化时虽然只修改六个 GUARD 字段，却随后保存整个 `CurrentSettings` 对象。

影响：用户在设置窗口预览值 B、随后点击取消时，磁盘已经被 GUARD 状态保存成 B；重载或重启后取消的值会重新出现。

建议：将 GUARD 运行态字段合并进 `WidgetForm.savedSettings` 的克隆后保存，禁止保存预览对象。增加“已提交 A、预览 B、GUARD 状态变化、取消后仍为 A”的集成测试。

### P1-06 云服务强制刷新和切网刷新仍可命中旧缓存

定位：

- `Performance/CloudEndpointProbeReader.cs`：`RequestRefresh`、`GetSnapshot`、`StartProbe`，当前约第 33-47、50-137、146-193 行。
- `Performance/CloudEndpointProbe.cs`：`RunAsync`、`TryGetCachedSnapshot`，当前约第 175-207、884-921 行。

现象：`RequestRefresh` 虽然清除调度时间并取消旧请求，但传给探测器的 `forceRefresh` 只取 `manualAccepted || targetsChanged`。面板强制刷新和网络身份变化因此仍可直接命中旧网络缓存。

影响：切换网络后可能继续显示网络 A 的结果，在网络 B 上完全不发起请求。

建议：为 `RequestRefresh` 建立单调 request epoch 和一次性的强制绕过缓存标志；只有成功占用 single-flight 后才能消费该标志；提交前校验 epoch。

### P1-07 Captive Portal 与 DNS TCP 缺少整轮截止时间

定位：

- `Performance/NetworkMonitorReader.cs`：`CheckCaptivePortal`、`SendDnsTcp`、`ReadExact`，当前约第 2908-2960、2097-2143 行。

现象：NCSI 响应通过无大小限制的 `StreamReader.ReadToEnd` 读取；DNS TCP 的 `ReadTimeout` 只约束单次读取，没有约束整轮总时长。

影响：恶意门户可持续发送小块数据并让内存增长；恶意 DHCP DNS 可逐字节发送响应，长时间占住 single-flight，使后续连通性或 DNS 刷新无法启动。

建议：NCSI body 限制为小型固定上限，例如 4 KiB；HTTP 和 DNS TCP 都使用绝对 deadline，并按剩余时间读取。使用本地慢速服务器测试持续 chunk、超大 body 和逐字节 DNS。

### P1-08 SPEC 原始渲染命令会被参数解析器误判

定位：

- `DesktopCodexAssistant.cs`：`RenderGuardBoardSamples`、`RenderSpecBoardSamples`、`GetStringArg`，当前约第 1254-1319、1356-1367 行。

现象：执行 SPEC 中的 `--render-guard --out <dir>` 或 `--render-specboard --out <dir>` 时，`GetStringArg` 会把下一个 `--out` 当成 render mode，随后因 mode 非 `sample/current` 而失败。

测试遗漏：此前验收额外传入了 `sample`，绕开了 SPEC 原始命令。

建议：可选值只有在下一参数不以 `--` 开头时才消费；使用参数表测试，并原样执行 SPEC 中的两条命令。

## 3. P2：重要可靠性与一致性问题

### P2-01 PathPing 旧网络结果可提交到新网络

`Performance/PathPingProbe.cs` 的 `RunRound`、`SampleHops`、`PublishSnapshot` 只检查 `disposed`，没有在提交和采样阶段核对 generation、接口和目标。样本又只按 hop number 存储，路由改变后可能把旧地址的延迟和丢包归给新地址。

### P2-02 FixedPing 会把断网前结果作为重连后的当前状态

`Performance/FixedPingProbe.cs` 的 `RunRound` 只核对配置签名，不核对网络身份。旧轮次可覆盖“网络不可用”，并成为新网络的 `lastCompletedSnapshot`。

### P2-03 GFW 调度器会吞掉手动刷新并提交旧任务

`Performance/GfwProbeReader.cs` 在判定需要刷新时先消费 token/trigger，再调用 `StartProbe`；若已有任务运行，`StartProbe` 直接返回。禁用、断网或切网也没有 request epoch/cancellation，旧任务完成后仍可提交和记录。

### P2-04 Network 标签与展开面板可能位于不同显示器

`Core/EdgeDockTabForm.cs` 的 `PositionAtLeftEdge` 无条件使用 `ModuleOperation` 工作区；`Core/NetworkMonitorForm.Dock.cs` 的面板使用 `ModuleNetworkMonitor`。两个模块配置到不同显示器后，鼠标无法从标签连续移入面板。

### P2-05 Network 标签和面板读取不同的缩放/透明度槽位

`EdgeDockTabForm.ResolveTransparencyOverride/ResolveScaleOverride` 对 Network 读取 Network 槽位；停靠后的 `NetworkMonitorForm.WindowTransparencyOverridePercent/WindowScaleOverridePercent` 仍读取 Spec Board 槽位。两套孤立自测互相矛盾但都能通过。

### P2-06 混合缩放会使停靠标签队列重排或重叠

Network、Spec、Codex Task、GUARD 分别缩放固定中心偏移和标签高度。40%、100%、200% 混合时，中心顺序不再保证与实际高度一致，可造成标签重叠或不可访问。

### P2-07 显示挂起未完整释放 EdgeDock layered/GDI 资源

`EdgeDockTabForm.SetDisplaySuspended(true)` 只停止计时器，没有调用 `ResetDisplayRenderResources`。`NetworkMonitorForm.PrepareForDisplaySuspend` 也没有通知 dock tab，Network 标签计时器和旧 Bitmap/DC/HBITMAP 可能跨显示重建继续保留。

### P2-08 Claude Radar 未接入全局鼠标穿透策略

`ClaudeRadarForm.ProcessSharedInteractionTick` 只处理 hover，没有 Codex Radar 等被动窗口已有的 `ApplyClickThroughStyle`、`NeedsClickThroughPolling` 和 `WS_EX_TRANSPARENT` 路径。仅 Claude Radar 会在全局穿透模式下继续截获鼠标。

### P2-09 SecretStore 删除明文失败后不会再次清理

`SecretStore.TryReadOrMigrateSecret` 先写密文、再删除明文。若删除暂时失败，下次进入“密文已存在”分支时只删除 `.migrated*`，不会重试原始 legacy 文件。

### P2-10 Spec Board 外部输入和扫描任务无界

`SpecBoardReader` 无界读取 `PROJECTS.json`、全部 JSONL 和全部匹配文件；管理窗口在 UI 线程同步读取；紧凑窗口 3 秒超时后不会取消内部扫描。大账本、长行或断开的网络目录可造成卡顿、内存增长和后台任务堆积。

### P2-11 `--restart-after-pid` 未验证目标身份

程序公开接受 PID，等待 10 秒后直接 `Kill()`。没有核对目标可执行文件、启动时间、父子关系或一次性 nonce，PID 复用或外部调用可能终止无关进程。

### P2-12 Logger 跨进程追加与轮转缺少协调

`Core/Logger.cs` 的 `SyncRoot` 只保护单进程；新旧进程并发使用 `FileShare.ReadWrite` 时，“检查大小 -> 轮转 -> 追加”仍可竞争并丢失日志。现有测试只覆盖单进程锁文件失败恢复。

## 4. P3：维护性与低风险一致性问题

### P3-01 缩放控件允许模型不支持的 0-39

设置控件允许 `-1..200`，模型只接受 `-1` 或 `40..200`。输入 20 时界面继续显示 20，预览和保存却被 `Normalize` 改为 40，造成所见值与生效值不一致。

### P3-02 文档 Gate 未覆盖语义完整性

当前索引仍存在已失效符号、缺少必需 `status`、空 `added_version` 及过期渲染产物名称。现有 Gate 主要验证 JSON、ID、路径和版本，没有验证设置属性、必需字段和 render manifest 与实际输出的一致性。

## 5. SPEC 大改后的主要风险映射

| SPEC 交付方向 | 已确认状态 | 遗留风险 |
|---|---|---|
| 设置文件原子保存 | 基本正确 | 可补多进程并发保存测试，但未发现当前生产并发入口 |
| Logger IO 防御 | 不再向调用方传播常见存储异常 | 跨进程追加和轮转完整性未保证 |
| 全局异常与自愈 | 事件已注册，日志和拉起路径存在 | 重启风暴抑制只在单进程内有效 |
| SecretStore 迁移 | DPAPI 迁移主体成立 | 设置保存入口仍写明文；明文删除失败后不重试 |
| Spec Board 打开白名单 | 紧凑窗口正常文档路径已有白名单 | 管理窗口、路径越界和 projectRoot 回退仍可绕过 |
| JSON 解析线程安全 | 共享 serializer 已改为调用内实例 | 未发现当前共享实例竞态 |
| FixedPing 稳定显示 | 刷新期间保留完成快照逻辑成立 | 快照没有绑定网络身份，切网可显示旧结果 |
| Captive Portal 原因净化 | 重定向文案只使用受限 host | 响应体大小和整轮截止时间仍无边界 |
| PathPing DNS 一次解析 | 每轮只解析一次目标 | 路径、样本和提交没有完整 request epoch |
| Claude hover 链 | 已复用共享 tick，没有新增 Timer | 自测绕过真实分发；鼠标穿透仍缺失 |
| 停靠设置与 GUARD 独立覆盖 | GUARD Version 80 迁移完整 | Network 标签/面板覆盖契约冲突，多显示器和混合缩放未验收 |
| CLI/渲染验收 | 使用显式 mode 的实际命令通过 | SPEC 写明的无 mode 命令本身失败 |

## 6. 推荐优化方案

### 6.1 统一网络请求上下文

为所有网络 reader 复用一个轻量请求上下文：

```text
request_epoch
network_generation
interface_id
target_signature
CancellationToken
absolute_deadline
trigger
```

只有成功占用 single-flight 后才消费刷新 token；禁用、断网、切网、配置变化和强制刷新统一递增 epoch 并取消旧请求；所有进度、样本和最终提交都校验上下文。

### 6.2 集中停靠布局与显示器路由

建立一个共享布局器，根据角色返回：

- 所属模块工作区。
- 有效透明度和缩放槽位。
- 实际缩放后的标签尺寸。
- 按累计高度计算的队列中心。
- 全屏、显示挂起和恢复策略。

避免每个 owner 分别复制固定偏移和显示器选择逻辑。

### 6.3 收紧 Spec Board 信任边界

- 流式读取 JSONL，并限制总文件大小、行长、行数、项目数和扫描文件数。
- 所有路径先规范化，再验证位于项目根目录内。
- 打开、定位和删除共用同一策略对象。
- 超时必须取消底层枚举，不能只让外层等待返回。

### 6.4 分离已提交设置、预览设置和运行态

至少维护三类状态：

- `savedSettings`：磁盘单一事实源。
- `previewSettings`：设置窗口临时预览。
- `runtimeState`：GUARD、手动隐藏等运行态。

运行态保存只能合并到已提交快照，不能直接保存任意子窗口的 `CurrentSettings`。

### 6.5 提升验收质量

下一轮测试应优先增加：

1. 两个独立进程的崩溃预算与 Logger 并发测试。
2. 设置窗口真实令牌保存入口测试。
3. 可阻塞假探测器驱动的切网时序测试。
4. 双显示器、负坐标和 40%/100%/200% 混合缩放矩阵。
5. `Hide/Suspend -> ApplySettings -> Resume` 的真实窗体生命周期测试。
6. 慢速 HTTP/DNS、超大 body 和取消测试。
7. 原样执行 SPEC 命令，而不是测试中补充未声明参数。
8. GUARD 非默认透明度/缩放和 Claude 完整交互链的像素/窗口样式验收。

## 7. 建议修复顺序

第一批，发布阻断：

1. Claude 令牌保存。
2. Spec Board 路径与打开策略。
3. 跨进程崩溃预算。
4. 原始 SPEC 渲染命令。

第二批，状态一致性：

1. GUARD 预览误保存。
2. EdgeDock 全屏/挂起重显。
3. Network 标签显示器和覆盖槽位。
4. 混合缩放队列。

第三批，网络可靠性：

1. Cloud 强制刷新缓存。
2. PathPing、FixedPing、GFW request epoch。
3. HTTP/DNS 总截止时间与读取上限。

第四批，长期优化：

1. Spec Board 流式有界读取和取消。
2. Logger 跨进程协调。
3. Claude click-through。
4. 文档 Gate 语义校验。

## 8. 已排除或暂不成立的候选

- `WidgetSettings.SaveToPath` 已使用同目录临时文件和原子替换，锁定目标时旧文件保持不变。
- 当前 Cloud JSON 解析使用局部 `JavaScriptSerializer`，未发现共享实例线程安全问题。
- GUARD 两个独立覆盖属性的默认值、克隆、读写、规范化和 Version 80 迁移完整。
- PathPing 当前每轮只解析一次目标 DNS，不再按每个 TTL 重复解析。
- Captive Portal 重定向文本只使用 `Uri.Host` 且限制长度，未发现文本注入路径。
- 公网 IP、DNS、连通性和滚动 Ping 的普通提交路径已有 generation/interface 校验；主要缺口集中在 PathPing、FixedPing 和 GFW。
- CTF、ASUS、SeelenUI taskkill 等已检查路径使用固定命令或受约束参数，未发现可控字符串命令注入。
- 未发现 `BinaryFormatter` 或多态类型实例化类反序列化 RCE。
- Claude hover 新实现复用了 `WidgetForm` 的共享 UI tick，没有新增每窗计时器。

## 9. 审查限制与证据说明

- 本次为只读审查，没有修改项目文件、编译、部署、重启或推送 GitHub。
- 执行了 `git diff --check`，无空白错误，仅有现有换行转换警告。
- 当前 SPEC 实施前保留的 ARM64 构建、自检和渲染均曾通过；本报告重点解释这些测试为何仍会漏掉生产链路问题。
- 未进行真实崩溃演练、网络攻击流量、长时间 soak 或用户桌面自动化，因此运行时影响程度应在修复时通过确定性测试进一步量化。

