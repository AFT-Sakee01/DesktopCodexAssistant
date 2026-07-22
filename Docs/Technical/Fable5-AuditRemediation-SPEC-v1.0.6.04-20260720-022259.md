# 多代理审计问题修复实施规格（Audit Remediation SPEC）

- 版本：1.0.6.04
- 生成模型：Claude Fable 5
- 生成时间：2026-07-20T02:22:59+09:00（UTC 2026-07-19T17:22:59Z）
- 主题：修复 Codex 多代理审计报告确认的 8 项 P1、12 项 P2、2 项 P3，外加复核时发现的 6 项延伸问题；按四个批次交付，每批可独立执行、验收与部署
- 来源：`Docs/Reports/Audits/Codex-MultiAgent-Code-Audit-Report-v1.0.6.04-20260719.md`（审计原文），Fable5 已逐条独立复核——所有直接核验的条目均属实，未发现误报；审计第 8 节排除清单与 Fable5 此前审查结论一致

---

## 1. 复核结论与延伸发现

Fable5 对审计报告的独立复核结果：**P1-01 至 P1-08 全部属实**（P1-08 为 Fable5 验收时亲历）；P2-01/02/04/05/07/08/09/11/12 直接核验属实，P2-03/06/10 与 P3-01 机理成立、按"执行时先复核再修"处理。P2-05 的槽位矛盾是上一轮 SPEC（P3-13）执行引入的回归。

复核过程中发现审计未覆盖的 6 项延伸问题，已并入对应交付项：

| 延伸 | 内容 | 并入 |
|---|---|---|
| D1 | Claude 令牌**清除**路径只删 `.bin`，不删 legacy 明文 `.txt`/`.migrated*`——下次读取时旧令牌被迁移复活 | A1 |
| D2 | 已被 P1-01 污染的 `.bin`（内容为明文）需要自愈：读取端检测非 DPAPI 内容时应视为明文令牌、原地重加密，而不是报 `SECRET_FORMAT` 让用户令牌失效 | A1 |
| D3 | `GetStringArg` 贪婪消费影响**全部**可选值参数（`--out`/`--height`/`--diagnose-label`/`--correlation-id`…），不止 render mode 两处；修复须是解析器级通用规则 | A4 |
| D4 | `CurrentSettings.Save()` 全仓仅 `OperationForm.GuardBoard.cs` 一处（已核实），P1-05 修复面收敛为单点改造 | B1 |
| D5 | 看板 Show 路径（`ToggleGuardBoardWindow` 等）与 Tab 同病：挂起/全屏下仍可被调起 | B2 |
| D6 | `WaitForRestartTargetExit` 的 `Kill()` 与崩溃自拉起同链：身份校验须同时覆盖"等待旧进程退出"与"杀死超时旧进程"两个动作 | A3 |

**验收方式硬约束（与前一 SPEC 相同，优先级高于一切实现偏好）**：

1. 全部验收必须是命令行可完成的操作：构建脚本、`--test*` 自检、`--render-*` 采样 PNG、grep/脚本断言、文件哈希比对。
2. **禁止** computer-use、GUI 自动化、桌面截图、鼠标键盘驱动等任何"操作用户电脑"的验收手段。
3. 任何单条验收步骤预期耗时 ≤ 2 分钟；每批次全套验收（含 ARM64 构建与部署）总预算 ≤ 10 分钟。禁止真实慢速服务器/soak/过夜类验收——网络边界测试一律用内存流或回环短超时模拟。
4. 崩溃预算与进程身份校验用"持久化文件注入 + 纯函数断言"模拟跨进程，不做真实崩溃演练、不杀正式实例。
5. 涉及真实用户数据路径（令牌文件、设置文件）的自检必须通过路径注入 seam 指向临时目录，**严禁触碰用户正式令牌与设置**。

---

## 2. 范围外

| 事项 | 处置 |
|---|---|
| 审计 6.1 的"统一请求上下文"抽象为共享类 | 不强制统一抽象；C1 按 reader 逐个落地行为等价的校验，抽象是否共享由执行者决断 |
| WidgetSettings 属性表驱动重构、.NET 迁移 | 维持前一 SPEC 的范围外结论 |
| 审计 6.5 第 8 条"像素级验收" | 与硬约束冲突的部分（窗口样式运行态检查）以 grep + 自检断言替代 |
| D3'（P3-02 文档 Gate 语义扩展） | 列入 D 批可选项，允许放弃 |

---

## 3. 交付项

四个批次按依赖与风险排序，允许每批独立执行、验收、部署（每批一次版本号提升）。引用代码一律"文件 + 成员名"。禁止新增命令行参数。

### 批次 A（发布阻断）

#### A1 Claude 令牌保存入口统一 + 明文复活防止 + 污染自愈（审计 P1-01 + 延伸 D1/D2）

**现状（已核实）**：`Win11SettingsForm.TrySaveClaudeSetupTokenFile` 用 `File.WriteAllText` 把明文令牌直写 DPAPI 路径 `ClaudeCodeUsageReader.SetupTokenFilePath`（`.bin`）；读取端 `SecretStore.TryReadOrMigrateSecret` 按 DPAPI Base64 解密——保存即落明文且读取报 `SECRET_FORMAT`。清除路径只 `File.Delete(.bin)`，legacy 明文 `.txt` 残留会在下次读取时迁移复活。

**要求**：
1. 保存统一走 `SecretStore.WriteSecret`；清除统一走 `SecretStore.DeleteSecretFiles`（含 legacy 与 `.migrated*`）。
2. 读取端自愈：`.bin` 内容非法（Base64/DPAPI 解码失败）时，将文件内容按明文令牌规范化（复用 `NormalizeSetupToken`）；结果非空则原地 `WriteSecret` 重加密并继续使用该令牌，结果为空才报错。自愈逻辑放在 `SecretStore` 或 reader 层均可，但必须对 DeepSeek 与 Claude 两个调用方行为一致。
3. `TrySaveClaudeSetupTokenFile` 与清除路径提供路径注入 seam（参数化目标路径），使自检可指向临时目录。

**验收**：
1. 新自检（并入 `--test-settings-bindings`）在临时目录断言：经真实保存入口写入令牌 → 磁盘文件不含明文（`IndexOf(token) < 0`）→ 经真实读取路径读回原值；清除后 `.bin`、legacy、`.migrated*` 全部不存在；预置 legacy 明文再清除 → 不复活。
2. 自愈自检：预置内容为明文令牌的 `.bin` → 读取成功返回该令牌，且文件已被重加密（重读文件不含明文）。
3. grep 断言：`TrySaveClaudeSetupTokenFile` 方法体内不存在对令牌路径的直接 `File.WriteAllText`；清除路径不存在裸 `File.Delete` 单删 `.bin` 的模式。
4. `--test`（含 `ClaudeCodeUsageReader.RunSelfTest`）PASS。

#### A2 Spec Board 信任边界统一（审计 P1-03）

**现状（已核实）**：`SpecBoardManagerForm.OpenSelectedFile` 仅查存在性即 `UseShellExecute=true` 打开，未走 `ResolveOpenTarget` 白名单；`SpecBoardReader.NormalizeRelativePath` 只修剪与统一斜杠，不拒绝 `..`、盘符绝对路径、UNC、设备路径（`Path.Combine` 遇 rooted 第二参直接返回第二参，可越出项目根）；紧凑窗口回退接受文件型 `projectRoot`。

**要求**：
1. 建立唯一共享的安全路径解析器（建议静态类 `SpecBoardPathPolicy`）：输入 projectRoot + spec_path，输出经 `Path.GetFullPath` 规范化并验证**位于项目根目录内**的绝对路径，拒绝 `..` 越界、rooted spec_path、UNC（`\\`）与设备路径（`\\.\`、`\\?\`）；projectRoot 必须是已存在目录。
2. 打开（紧凑窗口 + 管理窗口 `OpenSelectedFile`/`RevealSelectedFile`）、定位、删除/回收（`TryRemoveRowAndRecycleFile`）全部经该策略；打开继续叠加既有文档扩展白名单；回退目标只允许目录。
3. 删除/回收在执行前再次验证目标位于项目根内。

**验收**：
1. 新自检（并入 `--test-specboard-manager` 与 `--test-layout` 现有 SpecBoard 自检）覆盖：`../` 遍历、`C:\` 绝对、`\\server\share` UNC、`\\?\` 设备路径、文件型 projectRoot——全部拒绝；正常相对文档路径通过。
2. grep 断言：`OpenSelectedFile`/`RevealSelectedFile` 方法体内出现策略调用；`Process.Start` 前无裸 `row.AbsolutePath` 直用。
3. `--test-specboard-manager`、`--test-layout` PASS。

#### A3 跨进程崩溃预算 + 重启目标身份校验（审计 P1-02、P2-11 + 延伸 D6）

**现状（已核实）**：`lastFatalExceptionUtc` 仅进程静态字段——崩溃后重启的是**新进程**，字段归零，确定性崩溃形成无限"崩溃→拉起"链。`WaitForRestartTargetExit` 对外部传入 PID 等待 10 秒后直接 `Kill()`，不校验目标身份，PID 复用时可杀无关进程。

**要求**：
1. 崩溃时间戳持久化：`ShouldRestartAfterFatalException` 判定改以 `%LOCALAPPDATA%\DesktopCodexAssistant` 下的状态文件为准（原子写复用 tmp+Replace 模式；读写失败按"允许重启"降级并记日志）。判定与文件 IO 分离为可注入路径的纯函数。
2. `WaitForRestartTargetExit`：`Kill()` 前校验目标进程主模块路径与当前进程可执行路径一致（不区分大小写；`Process.MainModule` 取失败按不匹配处理）；不匹配则只等待、不杀。
3. 状态文件登记进 `INTERFACE_INDEX.jsonl`（新持久化文件）。

**验收**：
1. `--test-display-recovery` 自检扩展（临时目录注入状态文件）：首次崩溃（无文件）→ 允许重启且文件生成；"新进程"模拟（重新调用判定，不重置内存态）读同一文件、5 分钟内 → 拒绝；16 分钟前的旧文件 → 允许；文件内容损坏 → 允许（降级）。
2. 身份校验自检：以当前进程 PID + 自身路径 → 匹配；以当前进程 PID + 伪造路径 → 不匹配。
3. grep 断言：`WaitForRestartTargetExit` 中 `Kill` 调用点之前存在身份校验调用。

#### A4 参数解析器通用修复（审计 P1-08 + 延伸 D3）

**现状（已核实，Fable5 验收时亲历）**：`GetStringArg` 无条件取下一参数为值，`--render-guard --out <dir>` 中 `--out` 被当作 render mode 而报错。同模式影响全部可选值参数。

**要求**：`GetStringArg` 仅当下一参数不以 `--` 开头时才消费为值，否则返回 null；`--render-guard`/`--render-specboard`/`--render-specboardmanager` 无 mode 时保持"sample+current 双渲染"的既有缺省语义。`TryGetStringArg`/`TryGetIntArg` 同规则核查（`--correlation-id` 等现有调用不受语义破坏——值以 `--` 开头的合法场景不存在，须在自测中固化）。

**验收**：
1. 参数表自检（并入 `--test-operation-panel` 的现有参数自检）：`["--render-guard","--out","X"]` 解析 mode=null；`["--render-guard","sample","--out","X"]` 解析 mode=sample；`--correlation-id abc` 正常取值。
2. **原样执行 SPEC 级命令**：`--render-guard --out <临时目录>` 与 `--render-specboard --out <临时目录>` 退出码 0 且 PNG 生成。

### 批次 B（状态一致性）

#### B1 GUARD 运行态写盘改走已提交快照（审计 P1-05 + 延伸 D4）

**现状（已核实）**：设置预览经 `WidgetForm.PreviewSettings` 替换各窗口 `CurrentSettings`；`OperationForm.PersistGuardStateFromBoard` 把六个 GUARD 字段并入 `this.CurrentSettings` 后整体 `Save()`——预览值被连带落盘，取消失效。全仓 `CurrentSettings.Save()` 仅此一处。

**要求**：GUARD 运行态持久化改为回调 `WidgetForm`：将六个 GUARD 字段合并进 `savedSettings.Clone()` 后走 `SaveSettings`（与操作面板布尔开关同模式）；`OperationForm` 不再直接 `Save()` 任何 `CurrentSettings`。

**验收**：
1. 新自检（并入 `--test-settings-bindings` 或 `--test-operation-panel`）：模拟"已提交 A → 预览 B → GUARD 字段变化并持久化 → 取消预览"，断言磁盘文件为 A + 新 GUARD 态，不含 B 的非 GUARD 值（临时目录注入）。
2. grep 断言：`Core` 下 `CurrentSettings.Save()` 0 命中。

#### B2 挂起/全屏下 Tab 与看板显示纪律 + 资源释放（审计 P1-04、P2-07 + 延伸 D5）

**现状（已核实）**：`EdgeDockTabForm.ShowTab` 不检查 `displaySuspended`，无条件 `Show` + `SWP_SHOWWINDOW` + 启动计时器；各 owner 存在"先 `SetDisplaySuspended(true)` 随后 `ShowTab`"的调用顺序（如 `NetworkMonitorForm` 的 dock 同步）。`SetDisplaySuspended(true)` 只停计时器，不释放 layered/GDI 资源；`NetworkMonitorForm.PrepareForDisplaySuspend` 不通知 dock tab。

**要求**：
1. `ShowTab` 在挂起或全屏隐藏态只更新锚点与尺寸缓存，保持隐藏、不启动计时器；解除挂起/全屏时由 owner 重新 `ShowTab`。
2. `SetDisplaySuspended(true)` 调用 `ResetDisplayRenderResources`；`NetworkMonitorForm.PrepareForDisplaySuspend`/`RecoverAfterDisplayResume` 级联其 dock tab。
3. 看板 Show 入口（`ShowBoard`/`ToggleGuardBoardWindow` 等）同样拒绝在挂起/全屏隐藏态弹出。

**验收**：
1. 窗体级自检（并入 `--test-display-recovery`，四种角色）：`SetDisplaySuspended(true) → ShowTab → 断言 !Visible 且计时器未启动 → SetDisplaySuspended(false) → ShowTab → 断言 Visible`。
2. 资源断言：挂起→恢复循环沿用 `--test-radar-display-lifecycle` 的句柄/GDI 阈值门模式，对 EdgeDockTab 增加同型断言。
3. `--test` PASS。

#### B3 停靠布局与槽位契约统一（审计 P2-04、P2-05、P2-06）

**现状（已核实）**：`EdgeDockTabForm.PositionAtLeftEdge` 全部 Tab 用 `ModuleOperation` 工作区，Network 面板用自身模块工作区——双显示器配置分裂时 Tab 与面板不同屏。Network Tab 读 Network 槽位而停靠面板读 SpecBoard 槽位（上轮 P3-13 执行引入的回归）。四 Tab 各自缩放固定中心偏移，混合缩放下队列可重叠。

**要求**：
1. 建立共享停靠布局器（建议静态类 `LeftDockLayout`）：按角色返回工作区模块、透明度/缩放槽位、缩放后 Tab 尺寸、按**累计实际高度**计算的队列中心 Y、全屏/挂起策略；四个 owner 与 `EdgeDockTabForm` 全部改经它取值，删除各自复制的偏移常量。
2. 槽位契约：**Tab 与其面板读同一窗口的槽位**（Network 面板停靠态改读 Network 槽位；停靠尺寸跟随 `SpecBoardWidth/Height` 的既有契约保留并在文档写明）。迁移保视觉：Network 槽位为哨兵而 SpecBoard 槽位有值时，迁移把 Network 槽位初始化为 SpecBoard 当时值（与 GUARD Version 80 迁移同模式）。
3. 显示器路由：Tab 与对应面板使用同一工作区模块（决策：统一取 `ModuleOperation`——左停靠队列是操作面板屏的从属物；在 `Docs/Performance-And-Window-Runtime.md` 写明该契约）。

**验收**：
1. 布局器自检（并入 `--test-layout`）：40%/100%/200% 混合缩放矩阵下四 Tab 队列区间互不重叠且顺序稳定；Tab 与面板槽位解析函数对四角色逐一相等。
2. 迁移断言（`--test-settings-bindings`）：预置 SpecBoard 覆盖=60、Network 哨兵的旧版本文件 → 迁移后 Network 槽位=60。
3. `--render-networkmonitor` 停靠样张生成，退出码 0。
4. grep 断言：`EdgeDockTabForm`/四 owner 中不再有各自的 `LeftDockTabAutoOffsetY` 硬编码乘数。

#### B4 缩放编辑器范围与模型一致（审计 P3-01，执行前先复核）

**现状（审计断言，执行时先核实）**：缩放覆盖编辑器允许 `-1..200` 连续输入，模型只接受 `-1` 或 `40..200`；输入 20 界面显示 20、生效 40。

**要求**：编辑器约束为哨兵（跟随全局）或 `40..200`；或在失焦/保存时立即规范化回显。二选一，所见即所得。

**验收**：`--test-settings-bindings` 新增断言：编辑器写入 20 → 读回值 ∈ {哨兵, 40}，界面值与模型值一致。

### 批次 C（网络可靠性）

#### C1 探测请求身份与刷新语义（审计 P1-06、P2-01、P2-02、P2-03）

**现状（已核实/机理成立）**：`CloudEndpointProbeReader.StartProbe` 的 `forceRefresh` 仅取 `manualAccepted || targetsChanged`，网络身份变化后仍可命中旧网络缓存；`PathPingProbeReader` 采样/提交阶段不复核 generation/接口/目标；`FixedPingProbeReader.RunRound` 仅核对配置签名，切网后旧轮可成为新网络的当前快照；`GfwProbeReader` 在占用 single-flight 之前消费手动 token，已有任务运行时 token 被吞掉。

**要求**（按 reader 逐个落地，行为等价即可，不强制共享抽象）：
1. Cloud：`RequestRefresh` 携带"一次性强制绕缓存"标志，网络身份变化与手动刷新都置位；标志只在成功占用 single-flight 后消费；提交前校验请求发起时的身份签名。
2. PathPing/FixedPing：轮启动时捕获（generation, interfaceId, targetSignature），提交前校验，不匹配则丢弃；FixedPing 快照记录其网络身份，重连后旧身份快照不得作为当前状态展示。
3. GFW：token/trigger 只在成功启动任务后消费；禁用/断网/切网使在途任务的提交失效（epoch 或等价判据）。

**验收**：
1. 每个 reader 的 `RunSelfTest` 新增纯逻辑断言：旧身份提交被丢弃；token 在占用失败时保留、下轮仍可用；force 标志恰好消费一次。
2. `--test` 全部探测自检 PASS。

#### C2 网络读取上限与绝对截止（审计 P1-07）

**现状（已核实）**：NCSI 响应 `StreamReader.ReadToEnd` 无大小上限；`SendDnsTcp` 的 `ReadTimeout` 只约束单次 `Read`，`ReadExact` 循环下逐字节慢发可把一轮拖到无界时长。

**要求**：NCSI body 读取上限 4 KiB（超限即按门户内容替换处理）；DNS TCP 与 captive-portal HTTP 引入整轮绝对 deadline——每次读取前按剩余时间设置超时，剩余 ≤0 即失败。读取循环提取为可测纯函数（以 `Stream` 为参）。

**验收**：
1. 内存流自检（并入 `--test` 网络自检）：超大 body 截断于上限；模拟分块慢流在 deadline 内失败而非挂起（用极短 deadline，测试耗时 <2 秒）。
2. `--test` PASS。

#### C3 Claude Radar 接入鼠标穿透（审计 P2-08）

**现状（已核实）**：`CodexRadarForm` 有 `ApplyClickThroughStyle`/`NeedsClickThroughPolling`/`WS_EX_TRANSPARENT` 路径（8 处引用），`ClaudeRadarForm` 零引用——全局穿透模式下仅 Claude Radar 截获鼠标。

**要求**：对齐 CodexRadar 的接入方式，复用共享交互 tick，不新增计时器。

**验收**：grep 断言 `ClaudeRadarForm.cs` 含 `ApplyClickThroughStyle`（或等价共享入口）≥1 处；`--test-layout` 中 ClaudeRadar 自检扩展穿透样式断言；`--render-clauderadar` 出图。

#### C4 SecretStore 明文清理重试（审计 P2-09）

**现状（已核实）**：`TryReadOrMigrateSecret` 密文已存在分支只清 `.migrated*`，此前删除失败遗留的原始 legacy 明文永不重试。

**要求**：密文读取成功分支同时尝试删除 legacy 明文文件（best-effort，失败静默）。

**验收**：`SecretStore.RunSelfTest` 新增断言：预置密文 + 残留 legacy 明文 → 读取后 legacy 消失。

### 批次 D（长期，允许拆分或延后）

#### D1 Spec Board 有界读取与取消（审计 P2-10）

**要求**：`SpecBoardReader` 对 `PROJECTS.json`、账本 JSONL、目录扫描设上限（建议：文件 ≤2 MiB、行长 ≤64 KiB、行数 ≤5000、项目数 ≤64、扫描文件数 ≤512，超限截断并计入 `MalformedLines`/日志）；管理窗口读取移出 UI 线程；紧凑窗口 3 秒超时须取消底层枚举（CancellationToken 贯穿）。

**验收**：`--test-specboard-manager` 新增超限截断断言（临时目录构造超大账本，构造与断言总耗时 <30 秒）；UI 线程无同步读取（grep `SpecBoardReader.Read` 调用点不在事件处理器同步路径——由执行者以调用点清单证明）。

#### D2 Logger 跨进程协调（审计 P2-12）

**要求**：追加与轮转窗口用命名互斥（`Global\` 或 `Local\` + 产品名）短暂持锁；拿锁超时（≤200ms）降级为仅追加、跳过轮转并记一次性告警。保持"日志绝不抛异常"不变量。

**验收**：`--test-logger` 新增断言：并发双线程持互斥追加+轮转无异常、轮转后总字节不丢（单进程内模拟双写者）；现有锁定降级自检保持 PASS。

#### D3 文档 Gate 语义扩展（审计 P3-02，可选，允许放弃）

**要求**：Gate 增查：索引 `status`/`added_version` 必填非空；`entrypoints` 符号在 `primary_files` 中可 grep 命中（警告级）。

**验收**：Gate 脚本对当前仓库全绿或仅警告；植入一条缺 `status` 的测试行能被检出（测试后移除）。

---

## 4. 每批次验收流水线（全部 CLI，每批 ≤ 10 分钟）

每批执行完毕依次：① `Build-Arm64.ps1` 构建；② 五组自检（`--test`、`--test-logger`、`--test-layout`、`--test-settings-bindings`、`--test-display-recovery`）+ 涉及批次的 `--test-specboard-manager`/`--test-operation-panel`，全部退出码 0；③ 渲染：A 批跑 `--render-guard --out <dir>` 与 `--render-specboard --out <dir>`（**原样命令**，验证 A4），B/C 批加 `--render-networkmonitor`/`--render-clauderadar`；④ 本批全部 grep 断言；⑤ 版本号提升 + CHANGELOG 一事一条 + 文档 Gate 两项 PASS；⑥ 按根 `AGENTS.md` 默认规则部署并核对 SHA256；⑦ 最后一批完成后回填 Technical INDEX `implemented` 与 Spec Board `awaiting_verify`。

**再次重申**：不得使用 GUI 自动化；不得引入 >10 分钟等待；不做真实崩溃演练；自检严禁触碰用户正式令牌与设置文件。

---

## 5. 文档同步要求

- `INTERFACE_INDEX.jsonl`：A3 崩溃预算状态文件（新持久化文件，必登记）；其余批次无新接口。
- `FEATURE_INDEX.jsonl`：B3 槽位契约变化涉及的停靠功能行 `setting_keys` 核对；推荐测试变化的行同步。
- `Docs/Performance-And-Window-Runtime.md`：B2 挂起纪律、B3 停靠布局器与显示器路由契约、C3 穿透参与清单。
- `Docs/SpecBoard-Architecture.md`：A2 信任边界策略、D1 有界读取。
- `Docs/GuardBoard-Architecture.md`：B1 运行态持久化路径。
- `Docs/Component-Refresh-Rules.md`：C1 刷新语义（force 标志、token 消费时机）必须同步——这是该文档的 owner 主题。
- CHANGELOG：每交付项一条；每批部署一条 `deployment`。
