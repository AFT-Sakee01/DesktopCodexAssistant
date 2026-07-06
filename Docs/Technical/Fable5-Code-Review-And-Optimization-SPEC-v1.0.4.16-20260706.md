# Fable5-Code-Review-And-Optimization-Spec — v1.0.4.16 全量代码审查、技术说明与优化执行规格

生成时间：2026-07-06。审查基线：`ProductIdentity.Version = 1.0.4.16`，git HEAD `66ecfd8`（Release 1.0.2.97）。
本文档面向**执行 AI**：每个任务给出前置检测、精确步骤、禁止事项、可机器判定的验收标准与回滚方案。**验收标准全部是硬性 Gate，任何一条不满足即判定该任务失败，必须回滚后重试或上报，不允许"基本通过"。**

---

## 0. 执行 AI 必读

1. 先读根目录 `AGENTS.md` 全部内容并遵守，特别是：ARM64 默认、禁止未经要求编译 x64、部署规则（构建→备份正式 exe→覆盖→重启）、CHANGELOG/索引维护规则。
2. 本仓库处于并发编辑状态。**每次修改文件前必须重新读取该文件当前内容**，不得凭本文档中的行号盲改——行号仅是审查时的证据定位，执行时以内容搜索为准。
3. 逐任务执行、逐任务验收、逐任务提交 git。禁止把多个任务混在一个提交里。
4. 所有命令在 PowerShell 7 下执行，工作目录为仓库根。构建命令统一为：
   ```powershell
   powershell -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\spec-task-<任务号>.exe
   ```
5. 自检命令统一模式（`<exe>` 为刚构建的产物）：
   ```powershell
   & <exe> --test-logger;  & <exe> --test-layout;  & <exe> --test-settings-bindings
   & <exe> --test-display-recovery;  & <exe> --test-operation-panel
   & <exe> --test-radar-display-lifecycle --iterations 100
   ```
   期望输出分别包含：`Logger storage policy: PASS`、`Layout scaling policy: PASS`、`Settings binding policy: PASS`、`Display recovery layered surface policy: PASS`、`Operation panel interaction and performance policy: PASS`、`Radar display lifecycle policy: PASS`。任何一个退出码非 0 或缺少 PASS 字样即失败。
   （`--test` 含真实网络探测，仅在 T0 基线与最终 Gate 各跑一次，网络失败不算任务失败但须记录。）
6. 渲染验收统一模式：
   ```powershell
   & <exe> --render-codexradar --out _build\spec-render\<tag>\codex
   & <exe> --render-clauderadar --out _build\spec-render\<tag>\claude
   & <exe> --render-connectioncheck --out _build\spec-render\<tag>\conn
   & <exe> --render-networkmonitor --out _build\spec-render\<tag>\net
   & <exe> --render-powerthermal --out _build\spec-render\<tag>\power
   & <exe> --render-widget --out _build\spec-render\<tag>\widget
   & <exe> --render-operation --out _build\spec-render\<tag>\op
   ```
7. 像素对比工具：T0 中先创建 `_validation/Compare-RenderSamples.py`（规格见 T0-C）。"渲染无回归"的机器判定 = 该脚本对 before/after 两个目录输出 `RESULT: PASS`。

---

## 1. 当前版本技术说明

### 1.1 产品定位与硬约束

- ASUS UX3407N / UX3607O 专用 Windows on Arm 桌面小组件（Snapdragon X，ARM64）。产品名 `Desktop Codex Assistant UX3407N/UX3607O`，可执行文件 `DesktopCodexAssistant.exe`，数据根目录 `%LOCALAPPDATA%\DesktopCodexAssistant`。
- .NET Framework 4.x + WinForms + GDI+，**无 csproj**，由 `Build-Arm64.ps1` 直接调用 VS Build Tools 的 `csc.exe /platform:arm64 /optimize+` 编译 `DesktopCodexAssistant.cs` + `Core/ Settings/ Performance/ Interop/` 下全部 `*.cs`（递归、按文件名排序）。因此 **任何放进这四个目录的 .cs 文件都会被编译**；备份文件绝不能以 `.cs` 结尾放在这四个目录里。
- Dock、Launchpad、顶栏、Direct2D 项目已被产品决定禁用，不得恢复。
- 无第三方依赖、无 NuGet；JSON 用 `System.Web.Script.Serialization.JavaScriptSerializer`，HTTP 用 `HttpWebRequest`/`WebClient`（.NET FX 下为既定选型，不做框架级替换）。

### 1.2 进程生命周期（`DesktopCodexAssistant.cs`, 1136 行）

- `Main`：迁移旧存储目录 → 初始化两个 JSONL 历史记录器 → 解析命令行。命令行分三类：
  - 控制类：`--stop / --install / --uninstall / --restart-after-pid <pid> / --desktop-parent`
  - 自检类：`--test / --test-logger / --test-layout / --test-settings-bindings / --test-settings-open-close / --test-display-recovery / --test-radar-display-lifecycle / --test-operation-panel`
  - 诊断与渲染类：`--diagnose-idle-cpu / --diagnose-radar-runtime / --render-{codexradar,clauderadar,connectioncheck,networkmonitor,powerthermal,widget,operation} --out <dir>`
- 单实例：命名 Mutex `Local\DesktopCodexAssistant`；跨进程停止：命名 Event（含旧产品名的 legacy 事件兼容）。
- 正常运行路径：`WidgetSettings.Load()` → `ApplyPerformanceMode`（EcoQoS / PowerThrottling + BelowNormal 优先级）→ `UiHangWatchdog.Start()` → `new PdhSampler()` → `Application.Run(new WidgetForm(...))`。
- 开机自启：HKCU Run 键写入 exe 路径。

### 1.3 窗口清单与职责（全部为 WS_EX_LAYERED 分层窗口，自绘 GDI+，无子控件）

| 窗口 | 文件 | 行数 | 职责 |
|---|---|---|---|
| WidgetForm（主窗口/协调者） | Core/WidgetForm.cs | ~3.7k | CPU/内存/磁盘/GPU/NPU/网络仪表；**拥有**设置分发、刷新、全屏隐藏、挂起/恢复、显示器恢复、所有子窗口生命周期 |
| CodexRadarForm | Core/CodexRadarForm.cs + 9 个 partial | ~18k 合计 | Codex 配额/效率/IQ/服务健康雷达；也承载 Claude 用量模式（SoftwareMode 切换） |
| ClaudeRadarForm | Core/ClaudeRadarForm.cs | ~3.6k | Claude 模型评级/配额雷达（EvenRow 布局） |
| NetworkMonitorForm | Core/NetworkMonitorForm.cs | ~3.0k | 滚动 ping、DNS、IPv6、速率 |
| PowerThermalForm | Core/PowerThermalForm.cs | ~2.7k | 功耗/热（UX3407N/UX3607O 校准，禁止通用 fallback 静默替换） |
| ConnectionCheckForm | Core/ConnectionCheckForm.cs | ~1.8k | 三徽章连通性 + CleanIP |
| OperationForm | Core/OperationForm.cs | ~4.2k | 操作按钮面板 |
| Win11SettingsForm | Settings/Win11SettingsForm.cs | ~4.9k | 设置窗口（普通窗体，WarmCard 主题） |
| 编辑器 | GlobalLayoutEditorForm / ClaudeRadarModelMapEditorForm / AiQuickMenuForm | — | 布局编辑、模型映射编辑、AI 快捷菜单 |

- 渲染变体系统：每个窗口有 `<Form>RenderVariant` 枚举（Classic + Typographic/AmberHud/WarmCard/Phosphor 四个 OLED 安全方案；CodexRadar 另有 EvenGrid/EvenRow）。变体绘制在 `<Form>.<Variant>.cs` 兄弟 partial 中，仅切换 paint 方法，不碰数据层。共享调色/绘制助手在 `OledVariantPainting.cs`（25 处引用）与 `DesignTokens.cs`。
- 烧屏防护：`BurnInProtection` 周期性整窗位移 + 隐藏模式低亮度色；分层位图统一走 `NativeMethods.LayeredBitmapSurface`；字体统一 `UiFontCache`。

### 1.4 数据读取器与线程模型

- 硬件采样：`Performance/PdhSampler`（PDH 计数器，`Performance/PdhNative` P/Invoke）。
- 网络类：`NetworkMonitorReader`（滚动 ping）、`GfwProbeReader`、`CloudEndpointProbe(+Reader)`、`CleanIpConnectionReader` —— GFW 与云端探测**独立调度，互不抑制**（运行时不变量）。
- AI 服务类：`ClaudeRadarReader`（claudecoderadar 数据 + 首页 fallback）、`ClaudeCodeUsageReader`（`api.anthropic.com/api/oauth/usage`，setup-token 文件 `claude-code-oauth-token.txt`）、CodexRadar 系列（codexradar.com JSON/HTML/RSS 多级 fallback）、DeepSeek 余额（env 或 `deepseek-api-key.txt`）。
- 线程模型：UI 定时器（WinForms Timer）到期 → `Task.Run` 后台抓取 → 结果以**克隆快照**发布 → `BeginInvoke` 回 UI 应用。刷新用 single-flight 标志防重入，错误退避（正常/错误/限流三档间隔）。UI 不得阻塞网络、不得改读取器状态（运行时不变量）。
- 看门狗：`UiHangWatchdog`（独立后台线程）。

### 1.5 设置系统（`Settings/WidgetSettings.cs`, 5137 行）

- 约 382 个公共属性；INI 风格 `settings.ini`（`Key=Value` 行）存于数据根目录。
- **每个设置项当前需要手工维护 6 处**：属性声明、`CreateDefaults`、`Clone()`（约 1188 行起的逐属性拷贝）、`Save()`（约 1933 行起的逐行拼接）、`ApplyValue()`（约 2158 行起的 ~380 个 `string.Equals` if 块线性链）、`Normalize()` 钳制，再加设置 UI 绑定与 `--test-settings-bindings`。AGENTS.md 的"新设置必须覆盖 7 处"规则即因此而生。
- 版本迁移：`Version=` 键 + 一系列 `Apply*Migration` 函数；若干 legacy 键别名（如 `ContentTransparencyPercent`→`ApplicationTransparencyPercent`、`CtrlDRecoveryPulseEnabled`→`WinDRecoveryPulseEnabled`）。
- 自检 `RunCompatibilitySelfTest` 为**抽查式**（特定键的解析/钳制/迁移断言），**不存在全属性 round-trip 完整性测试**——漏写 Clone/Save/ApplyValue 任一处不会被现有测试发现。

### 1.6 持久化与日志

- 数据根 `%LOCALAPPDATA%\DesktopCodexAssistant`：`settings.ini`、`widget.log`（3MB 轮转、目录 10MB 上限）、`error.log`、`gfw-probe.log`、网络检查 JSONL（`NetworkCheckHistoryLogger`）、配额决策 JSONL（`QuotaDecisionHistoryLogger`）、token/api-key 明文文件、雷达通知状态等。
- `Logger`：INFO 缓冲 64KB/5min 批量落盘，ERROR 即时；轮转为 O(1) rename。

### 1.7 验收工具链

- 自检矩阵见 §0.5；渲染采样见 §0.6（每窗口输出各变体 PNG，是布局验证的唯一权威手段，不用截屏）。
- 诊断：`--diagnose-idle-cpu`、`--diagnose-radar-runtime`（句柄/GDI/USER 计数）。
- 资源泄漏 Gate 已内建：`--test-radar-display-lifecycle` 100 次挂起/恢复循环后 Handles Δ≤100、GDI Δ≤10、USER Δ≤20，超限自动 FAIL。

### 1.8 文档与索引

`Docs/` 下各窗口架构文档 + `Docs/Indexes/FEATURE_INDEX.jsonl`、`Docs/Interfaces/INTERFACE_INDEX.jsonl`、`Docs/Maintenance/CHANGELOG.jsonl`。任何功能/接口变更须同步索引与 changelog（见 AGENTS.md Records 节）。

### 1.9 代码规模基线（2026-07-06，不含 _build 与 .bak）

主源码约 86k 行；最大文件：CodexRadarForm.cs 15,491 / WidgetSettings.cs 5,137 / Win11SettingsForm.cs 4,865 / NativeMethods.cs 4,837 / OperationForm.cs 4,192 / WidgetForm.cs 3,712 / ClaudeRadarForm.cs 3,570。

---

## 2. 审查发现

### P0（数据安全/工程风险，立即处理）

- **P0-1 大量源码未纳入版本控制。** git 仅跟踪 32 个 .cs（HEAD 停在 1.0.2.97），工作区另有 **57 个未跟踪 .cs** ——包括整个 ClaudeRadar 功能、全部渲染变体 partial、UiHangWatchdog、AiQuickMenu、设置重构成果等约 1.0.2.97→1.0.4.16 之间的全部工作。磁盘故障或误删 = 数月工作丢失。这也是源码里大量"保留以便回滚"死代码存在的根因（git 才应是回滚机制）。→ T1
- **P0-2 凭据明文落盘。** `claude-code-oauth-token.txt`（ClaudeCodeUsageReader.cs，`SetupTokenFileName`）与 `deepseek-api-key.txt`（CodexRadarForm.cs，`DeepSeekApiKeyFileName`）为明文文件。→ T8（DPAPI）

### P1（结构性问题，高杠杆重构）

- **P1-1 七窗口分层渲染样板重复。** `RenderLayeredWindow()`/`RenderLayeredWindow(bool)`/`EnsureRenderBuffer`/`DisposeRenderBuffer(s)`/`GetBackgroundOpacityAlpha`/`GetApplicationOpacityAlpha`/`ConfigureGraphics`/`S(int)`/`RoundedRectangle` 在 WidgetForm、CodexRadarForm、ClaudeRadarForm、NetworkMonitorForm、PowerThermalForm、ConnectionCheckForm、OperationForm 七处近似复制（约 1.5k+ 行），且已出现漂移（NetworkMonitorForm 是 `DisposeRenderBuffers` 复数版；OperationForm 缺 `GetApplicationOpacityAlpha`）。→ T4
- **P1-2 WidgetSettings 六重手工维护。** 382 属性 × (Clone/Save/ApplyValue/Normalize/UI/测试)；ApplyValue 为 O(键数×属性数) 线性 if 链；漏一处即静默 bug 且现有测试查不出。→ T5（先补全量 round-trip 测试，再做反射序列化收敛）
- **P1-3 Claude 用量消费逻辑双份。** `CodexRadarForm.ClaudeUsage.cs`（926 行，含独立锁/single-flight/三档退避）与 `ClaudeRadarForm`（RequestClaudeCodeUsageRefresh/RefreshClaudeCodeUsageIfDue/...）各自实现一套对 `ClaudeCodeUsageReader` 的调度与缓存。→ T6
- **P1-4 死特性路径长期内联保留。** `CodexRadarForm.cs` 顶部 `ServiceHealthPanelEnabled = false`（legacy 健康面板，"retained for rollback"）与 `CodexConnectionFlowEnabled = false`（五段连接诊断线，每个调度入口都被此旗标门控）。git 提交后这些代码的回滚价值由历史承担，应删除。→ T3
- **P1-5 CodexRadar 布局变体 Classic/EvenGrid 疑似不可达。** 默认与迁移均强制 `EvenRow`（WidgetSettings.cs 中 `CodexRadarRenderVariant.EvenRow` 为默认并有强制迁移逻辑），Classic 巨型绘制树与 EvenGrid partial 可能已无真实用户。**需用户裁决后**再删。→ T9（默认仅报告，不删）

### P2（卫生/小优化）

- **P2-1 仓库根垃圾**：7 个 GUID 前缀 exe（6/9–7/2 的部署残留副本）、`replace.py`~`replace7.py`（一次性脚本）、`DesktopCodexAssistant-x64.exe`（本分支明确 ARM64-only）。→ T2
- **P2-2 `Core/CodexRadarForm.cs.bak`（452KB, 13k 行）+ `.bak.zip`**：不参与编译但污染全文检索/索引与 AI 上下文。`_build/source-backups/` 另有同物旧版。→ T2
- **P2-3 `_build/` 已膨胀至 417MB**（150+ 测试 exe、数百渲染目录）。已在 .gitignore，但需保留策略。→ T2
- **P2-4 Logger 每次落盘都全目录扫描**：`AppendImmediate` → `EnforceLogDirectoryLimit` 每次 `GetFiles("*.log")`+求和。INFO 已批量所以频率不高，但 ERROR 风暴时放大 IO。→ T7
- **P2-5 巨型复制粘贴 using 头**：多数文件带同一套 17 行 using（Logger.cs 里 WMI/WinForms/GDI+ 全部未用）。纯卫生。→ T7
- **P2-6 `JavaScriptSerializer` 分散在 13 个文件**各自 new；可包一层但**不值得替换框架**（.NET FX 约束）。→ T7（仅统计与文档化，不强制改）

---

## 3. 优化任务规格

> 通用禁止事项（适用于所有任务）：
> - 禁止改动运行时不变量（AGENTS.md Runtime Invariants 全部条目）。
> - 禁止编译/发布 x64。禁止恢复 Dock/Launchpad/顶栏/Direct2D。
> - 禁止改变 `settings.ini` 已有键名与已有语义（新增别名允许，删除/改名不允许）。
> - 禁止在渲染重构类任务中改变任何可见像素（以 Compare-RenderSamples 判定）。
> - 每个任务完成后必须：更新 `Docs/Maintenance/CHANGELOG.jsonl`（追加一行 JSON 对象，含 version/date/task/summary）、按需更新两个 INDEX.jsonl、`git add` 相关文件并独立提交，提交信息格式 `Spec-T<n>: <一句话>`。
> - 涉及运行时行为的任务按 AGENTS.md 默认部署规则：构建 ARM64 → 备份正式 exe → 覆盖 → 重启。

### T0 — 基线固化（必须最先执行）

**目的**：为后续所有"无回归"判定建立可比对基线。

步骤：
1. `git status --porcelain > _build\spec-baseline\git-status-before.txt`（目录不存在则创建）。
2. 构建基线 exe：`Build-Arm64.ps1 -OutputPath _build\spec-baseline\baseline.exe`。记录 csc 警告行数：构建输出重定向到 `_build\spec-baseline\build-warnings.txt`，统计含 `warning` 的行数写入 `_build\spec-baseline\warning-count.txt`。
3. 用 baseline.exe 跑 §0.5 全部自检（`--test` 也跑，网络项失败仅记录），输出保存到 `_build\spec-baseline\selftest\*.txt`。
4. 用 baseline.exe 跑 §0.6 全部渲染，`<tag>=baseline`。
5. 复制当前 `%LOCALAPPDATA%\DesktopCodexAssistant\settings.ini` 到 `_build\spec-baseline\settings-fixture.ini`（若不存在，用 baseline.exe 正常启动一次再退出以生成，或用 `WidgetSettings.CreateDefaults` 路径说明记录缺失）。
6. **T0-C 创建 `_validation/Compare-RenderSamples.py`**，行为规格：
   - 用法：`python _validation/Compare-RenderSamples.py <dirA> <dirB> [--threshold 0.001]`
   - 递归匹配两目录下同名 PNG；文件集合不一致 → 打印缺失清单并 `RESULT: FAIL`。
   - 每对图片：尺寸必须完全一致，否则 FAIL；逐像素比较 RGBA，统计不同像素占比；占比 > threshold（默认 0.1%）→ 该文件 FAIL。
   - 全部通过打印 `RESULT: PASS` 且退出码 0；任何 FAIL 退出码 1 并逐文件打印 `<file> diff=<占比>`。
   - 仅用标准库 + PIL（若无 PIL，则读取 PNG 用 `zlib`+手写解码不现实——允许 `pip install pillow`；安装失败则改用 .NET：写等价的 `_validation/CompareRenderSamples.ps1` 用 `System.Drawing.Bitmap` 实现同一规格）。

**验收（Gate）**：
- [ ] `_build\spec-baseline\` 下存在：baseline.exe、warning-count.txt、selftest 全部输出（6 个自检文件均含 PASS 字样）、7 个渲染子目录且每个至少 1 个 PNG、settings-fixture.ini（或缺失说明文件）。
- [ ] `Compare-RenderSamples` 自校验：对 `spec-render\baseline\codex` 与其自身副本运行 → `RESULT: PASS`；人为把任一 PNG 用图像工具改 1000 个像素后运行 → `RESULT: FAIL`。两个结果都必须演示并把输出存入 `_build\spec-baseline\compare-selfcheck.txt`。

### T1 — 源码全量入库（P0-1）

步骤：
1. 更新 `.gitignore` 追加：`*.bak`、`*.bak.zip`、`replace*.py`、`*_DesktopCodexAssistant.exe`。
2. `git add` 以下内容（**白名单式，逐目录确认**）：`Core/ Settings/ Performance/ Interop/ Docs/ Assets/ _validation/*.cs _validation/*.py AGENTS.md README.md Build-*.ps1 Install.* Uninstall.* *.cmd DesktopCodexAssistant.cs LICENSE`。**明确禁止 add**：任何 `.exe`（`Release/` 下两个已跟踪的除外，维持现状）、`.bak`、`.zip`、`replace*.py`、`_build/`。
3. 提交：`Spec-T1: track all 1.0.2.97..1.0.4.16 sources (57 files)`。

**验收（Gate）**：
- [ ] `git status --porcelain` 中不再出现任何 `?? *.cs`。
- [ ] `git ls-files '*.cs' | Measure-Object -Line` ≥ 89（32+57）。
- [ ] `git show --stat HEAD` 中不包含任何 `.exe/.bak/.zip/.py`（Release/ 现状除外——本次提交不得触碰 Release/）。
- [ ] 提交后 `Build-Arm64.ps1` 仍成功（证明没有漏 add 编译所需文件：用 `git stash -u` 把未跟踪文件暂存后构建通过，再 `git stash pop`；或在临时 `git worktree` 中构建通过——**必须实际执行其一并保存构建输出**）。

### T2 — 仓库垃圾清理（P2-1/2/3）

步骤：
1. 删除仓库根 7 个 `<GUID>_DesktopCodexAssistant.exe`、`replace.py`~`replace7.py`、`DesktopCodexAssistant-x64.exe`。
2. 删除 `Core/CodexRadarForm.cs.bak`、`Core/CodexRadarForm.cs.bak.zip`（T1 已入库真身，历史即回滚）。
3. `_build/` 清理：保留 `formal-backups/`、`settings-backups/`、`settings-default-backups/`、`source-backups/` 中**最近 30 天**内容与 `last-deploy-stamp.txt`、`spec-baseline/`、`spec-render/`；其余（旧测试 exe、旧渲染目录、散落 txt）移入单个归档 `_build\archive-20260706.zip` 后删除原件。**先归档后删除，禁止直接删除。**

**验收（Gate）**：
- [ ] `Get-ChildItem -File *.exe` 在仓库根仅剩 `DesktopCodexAssistant.exe`（正式运行副本）。
- [ ] `Get-ChildItem Core -Filter *.bak*` 为空。
- [ ] `_build` 目录大小 < 100MB（`(Get-ChildItem _build -Recurse -File | Measure-Object Length -Sum).Sum / 1MB`），且 `_build\archive-20260706.zip` 存在。
- [ ] 清理后完整构建 + §0.5 六项自检全 PASS（证明未误删编译或测试依赖物）。

### T3 — 删除死特性路径（P1-4，依赖 T1）

步骤：
1. 在 `Core/CodexRadarForm.cs` 定位 `ServiceHealthPanelEnabled` 与 `CodexConnectionFlowEnabled` 两个 `static readonly bool ... = false` 旗标。
2. 对每个旗标：找出全部引用点；删除旗标恒 false 分支下**只被该分支使用**的方法、字段、常量、嵌套类型（例如五段连接诊断的调度入口、绘制与状态字段；legacy 健康面板绘制）。注意 `ServiceHealthProbeEnabled = true` 的探测本体**保留**（API 一行摘要仍消费其状态——见源码注释）。
3. 删除后旗标本身也删除。编译器是裁判：删干净的标准是 0 error 0 新增 warning。
4. 同步更新 `Docs/CodexRadar-Architecture.md` 与 FEATURE_INDEX 中相关条目（标记 removed）。

**验收（Gate）**：
- [ ] `grep -rn "ServiceHealthPanelEnabled\|CodexConnectionFlowEnabled" Core Settings Performance Interop DesktopCodexAssistant.cs` 零命中。
- [ ] 构建成功且 csc 警告行数 ≤ T0 记录值。
- [ ] §0.5 六项自检全 PASS。
- [ ] 渲染对比：`<tag>=t3` 全部 7 组 vs baseline → `RESULT: PASS`（死代码删除不得改变任何像素）。
- [ ] `Core/CodexRadarForm.cs` 行数比 T0 时减少 ≥ 300 行（未达标说明没删干净或删错对象，重查）。
- [ ] 部署后正式 exe 正常运行 ≥ 10 分钟无 error.log 新增（对比部署前后 error.log 大小）。

### T4 — 分层窗口渲染基类提取（P1-1，依赖 T1；建议在 T3 后）

**目的**：把七个窗体重复的分层渲染样板收敛到一个基类，消除漂移。

步骤：
1. 新建 `Core/LayeredWidgetFormBase.cs`：`internal abstract class LayeredWidgetFormBase : Form`。上收成员（以 CodexRadarForm 版本为准逐一比对七份实现，**差异点必须先列成对照表存入 `_build\spec-t4\diff-matrix.md` 再动手**）：
   - `LayeredBitmapSurface` 字段与 `EnsureRenderBuffer/DisposeRenderBuffer`；
   - `RenderLayeredWindow()` / `RenderLayeredWindow(bool redrawContent)` 模板方法，内容绘制通过 `protected abstract void DrawWindowContent(Graphics g)`（或与现有各窗体 DrawWindow 签名最贴合的抽象点——以最小改动为准）；
   - `GetBackgroundOpacityAlpha/GetApplicationOpacityAlpha`（透明度来源各窗不同 → 做成 `protected abstract int BackgroundTransparencyPercent { get; }` 等抽象属性）；
   - `S(int)` 缩放、`static RoundedRectangle`、`ConfigureGraphics`、`CreateParams` 的 `WS_EX_LAYERED|TOOLWINDOW|NOACTIVATE` 组合、`ShowWithoutActivation`；
   - 挂起/恢复资源释放钩子（`PrepareForDisplaySuspend/RecoverAfterDisplayResume` 的公共部分）。
2. 七个窗体逐个改为继承该基类并删除本地副本。**一次只改一个窗体，改完一个立即构建+该窗体渲染对比通过后再改下一个**；每个窗体一个独立 commit（`Spec-T4.<n>: <Form> onto LayeredWidgetFormBase`）。
3. NetworkMonitorForm 的双缓冲差异（`DisposeRenderBuffers`）若确有第二缓冲，保留其派生类私有部分，不强行上收。
4. 更新 `Docs/Performance-And-Window-Runtime.md` 与 INTERFACE_INDEX（新增 LayeredWidgetFormBase 条目）。

**禁止**：改变任何窗体的 CreateParams 位组合、透明度语义、绘制顺序；在基类里引入虚调用热路径以外的行为差异。

**验收（Gate，每个窗体迁移后 + 全部完成后各跑一次）**：
- [ ] 渲染对比：全部 7 组 vs baseline → `RESULT: PASS`（阈值 0.1%，**期望为 0 差异**；0 < diff ≤ 0.1% 时必须人工说明原因并记录，>0.1% 直接 FAIL）。
- [ ] §0.5 六项自检全 PASS，其中 `--test-radar-display-lifecycle --iterations 100` 的三个 delta 数值 ≤ baseline 各自数值 +0（不得变差；输出中带具体数字，逐项比较）。
- [ ] `--test-display-recovery` PASS。
- [ ] 代码量：`Core/` 七个窗体主文件总行数比 T0 基线减少 ≥ 800 行，同时新基类 ≤ 600 行。
- [ ] `grep -c "private void RenderLayeredWindow" Core/*.cs` 为 0（全部走基类）。
- [ ] 部署后手动验证清单（写入 `_build\spec-t4\manual-check.md`，逐项打勾）：7 窗口全部可见、透明度设置滑块生效、显示器睡眠唤醒后 7 窗口全部恢复、全屏应用时按设置隐藏。

### T5 — WidgetSettings 完整性测试先行 + 序列化收敛（P1-2，依赖 T1）

**分两个必须独立提交的阶段。阶段 B 风险高，阶段 A 无论如何都要做。**

**阶段 A：全属性 round-trip 自检（纯新增，零行为变化）**
1. 在 `WidgetSettings` 新增 `internal static void RunFullRoundTripSelfTest()`，并挂入 `--test-settings-bindings` 流程：
   - 反射枚举全部 public 可读写实例属性（int/bool/double/string/enum 类型）；
   - 对每个属性写入**非默认哨兵值**（int: 默认值+7 后经 Normalize 允许被钳制——因此断言在 Normalize 之前比较 Save/Load 原始值；enum: 取第二个成员；bool: 取反；string: `"rt-"+属性名`；对会被 Normalize 改写的属性维护一张显式豁免表，豁免表中每项必须附注原因）；
   - 断言 1（Save/Load）：`Save()` 到临时目录 → `Load()` → 全部属性值一致，列出所有不一致属性名后抛异常；
   - 断言 2（Clone）：`Clone()` 后全部属性值一致，同样列名报错；
   - 断言 3（Save 覆盖）：`Save()` 输出文本中每个非豁免属性名都作为键出现恰好一次。
2. 运行——**预期此测试会立即揪出现存的漏项**；修复所有被揪出的 Clone/Save/ApplyValue 缺口（这是本任务的直接收益）。

阶段 A 验收（Gate）：
- [ ] `--test-settings-bindings` PASS 且输出含新增测试的通过标记（自检打印 `Settings full round-trip: PASS <N> properties`，N ≥ 350）。
- [ ] 用 T0 的 `settings-fixture.ini` 做真实文件回归：`Load()` fixture → `Save()` → 再 `Load()`，两次内存对象全属性一致（临时驱动代码或临时自检参数实现，验完可删）。
- [ ] 豁免表 ≤ 20 项且每项有注释原因。
- [ ] 渲染对比 vs baseline 全 PASS（设置默认值不得被此任务改变）。

**阶段 B：ApplyValue/Save 反射化（可选执行——若阶段 A 后判断风险过高，允许放弃并在 CHANGELOG 注明，不算失败）**
1. 设计：属性上不加特性（csc 老版本兼容风险低但仍避免复杂化），改为**单一注册表**：`private static readonly SettingDescriptor[] Schema`，每项含属性名、legacy 别名数组、类型、解析/格式化委托。`Save()` 与 `ApplyValue()` 都遍历 Schema 生成/解析；`Clone()` 改为 `MemberwiseClone()` + 引用类型字段显式处理。
2. legacy 别名与全部 `Apply*Migration` **原样保留**；`Save()` 输出的键**顺序与键名必须与旧实现完全一致**（阶段 B 的核心验收）。
3. `Normalize()` 保持手写（业务钳制逻辑不适合表驱动）。

阶段 B 验收（Gate）：
- [ ] **字节级等价**：用旧 exe（T0 baseline.exe）与新 exe 分别 `Load(settings-fixture.ini)` → `Save()`，两个输出文件**逐行完全相同**（`Compare-Object` 零差异）。
- [ ] 阶段 A 的 round-trip 测试继续 PASS。
- [ ] `RunCompatibilitySelfTest` 全部既有断言不改动且 PASS（迁移/别名/钳制行为不变的证明）。
- [ ] `WidgetSettings.cs` 行数 ≤ 3200（收敛 ≥ 1900 行）。
- [ ] §0.5 全 PASS + 渲染对比全 PASS + 部署后正式运行 30 分钟：改动 5 个不同设置项（含一个 enum、一个滑块、一个开关）→ 重启进程 → 设置全部保留（写入 `_build\spec-t5\manual-check.md`）。

### T6 — Claude 用量调度去重（P1-3，依赖 T1、T4）

步骤：
1. 新建 `Core/ClaudeCodeUsageScheduler.cs`：封装对 `ClaudeCodeUsageReader` 的 single-flight、三档退避（300/600/900s 常量迁入）、快照缓存与"较新者保留"合并逻辑（`PreserveCurrentClaudeCodeQuotaIfNewer` 语义）。对外：`RequestRefresh(trigger)`、`TryGetSnapshot(out ...)`、线程安全，回调经调用方 `BeginInvoke`。
2. `CodexRadarForm.ClaudeUsage.cs` 与 `ClaudeRadarForm` 改为共用同一实例（进程级单例即可——两个窗口消费同一账号数据，共享缓存还能减半 API 调用）。
3. 保留各窗体自己的展示层转换；删除两处重复的锁/标志/退避字段。
4. 更新 `Docs/Codex-ClaudeRadar-Architecture.md`、`Docs/Component-Refresh-Rules.md`（刷新间隔/single-flight 归属变更必须记录——AGENTS.md Records 要求）、INTERFACE_INDEX。

**验收（Gate）**：
- [ ] `grep -rn "claudeUsageLock\|claudeUsageRequestRunning" Core` 仅在新 Scheduler 内命中（或零命中，取决于命名）。
- [ ] §0.5 全 PASS；渲染对比全 PASS。
- [ ] 行为验证：部署后同时开启 CodexRadar（Claude 模式）与 ClaudeRadar，在 `widget.log` 中统计 10 分钟内对 `api.anthropic.com` 的请求日志条数 ≤ 3（原实现两窗口独立 300s 各刷一次约 4 条；共享后应减半。若日志不含该 URL 记录，先在 Scheduler 加一条 INFO 日志，此为允许的新增行为）。
- [ ] 断网测试：断开网络 → 两窗口均进入错误退避且 UI 无卡顿（`UiHangWatchdog` 无告警日志）；恢复网络 → 10 分钟内两窗口数据恢复。结果写入 `_build\spec-t6\manual-check.md`。

### T7 — 微优化与卫生（P2-4/5/6，依赖 T1）

步骤：
1. `Logger.AppendImmediate`：`EnforceLogDirectoryLimit` 改为节流执行——距上次执行 < 10 分钟且未发生轮转则跳过（静态时间戳字段 + 轮转时强制）。
2. using 头清理：仅处理**本轮已触碰过的文件**（T3–T6 改过的）+ `Core/Logger.cs`；删除未使用 using。禁止为此专门大规模刷未触碰文件（噪音 > 收益）。
3. `JavaScriptSerializer`：不替换。仅在 `Docs/Interface-And-Reuse-Resources.md` 记录"JSON 统一入口待议"一行。

**验收（Gate）**：
- [ ] `--test-logger` PASS（其自检直接调用轮转与目录清理内部函数，节流不得破坏 `RunStoragePolicySelfTest`——若自检走的是内部函数而非 AppendImmediate 路径则天然不受影响，须在提交信息中说明验证方式）。
- [ ] 构建 0 error，警告数 ≤ 基线。
- [ ] 渲染对比全 PASS；§0.5 全 PASS。

### T8 — 凭据 DPAPI 加密（P0-2，依赖 T1）

步骤：
1. 新建 `Core/SecretStore.cs`：`Protect/Unprotect` 用 `System.Security.Cryptography.ProtectedData`（DataProtectionScope.CurrentUser，需在 Build-Arm64.ps1 增加 `/reference:System.Security.dll`）。文件格式：新文件 `claude-code-oauth-token.bin` / `deepseek-api-key.bin`，内容 = DPAPI 密文 Base64。
2. 读取顺序：`.bin` 存在 → 解密使用；否则 `.txt` 存在 → 读取明文、立即写 `.bin`、**将 `.txt` 改名为 `.txt.migrated`**（不直接删除，防解密环境问题导致锁死；下个版本再清理）。DeepSeek 的环境变量路径优先级保持不变。
3. 设置窗口中粘贴 token 的入口（若存在）改写 `.bin`。
4. `ClaudeCodeUsageReader.RunSelfTest` 增加：Protect→Unprotect 往返断言 + 明文迁移断言（临时目录模拟 .txt → 调用读取 → 断言 .bin 生成且内容可解密、.txt 已改名）。

**验收（Gate）**：
- [ ] `& <exe> --test` 输出中 ClaudeCodeUsageReader 自检通过（网络部分失败可豁免，加密自检不可豁免）。
- [ ] 真机迁移演练：在数据目录放一个假 token `.txt` → 启动正式 exe → 退出 → 断言 `.bin` 存在、`.txt.migrated` 存在、`.bin` 内容非明文（`Select-String` 搜不到原文）→ 还原用户真实 token 状态。全过程命令与输出存 `_build\spec-t8\migration-proof.txt`。
- [ ] 用户真实 token 场景：部署后 Claude 用量窗口 10 分钟内出现真实数据（非 NO_SETUP_TOKEN 错误）。
- [ ] `grep -rn "claude-code-oauth-token.txt\|deepseek-api-key.txt"` 仅剩迁移代码中的引用。

### T9 — Classic/EvenGrid 变体裁决（P1-5，报告任务，禁止擅自删除）

步骤：
1. 产出报告 `_build\spec-t9\variant-usage-report.md`：Classic 绘制树与 EvenGrid partial 的行数、入口、当前 settings.ini 中各窗口实际 RenderVariant 值、删除后可减行数估计（预计 ≥ 2500 行）。
2. 报告结论仅两个选项呈报用户：(a) 删除 CodexRadar 的 Classic+EvenGrid 绘制路径并把枚举成员标记 legacy→映射到 EvenRow；(b) 维持现状。**未获用户明确批准前，本任务到报告为止。**

**验收（Gate）**：报告存在、数据来自实测（含 grep/wc 命令原文），无任何源码改动（`git status` 干净）。

### T10 — 收尾：版本、文档、最终 Gate

步骤：
1. `ProductIdentity.Version` 升一位 patch；CHANGELOG.jsonl 汇总条目；两个 INDEX.jsonl 与受影响的 `Docs/*.md` 全部核对一遍。
2. 跑**最终全量 Gate**（见 §5）。
3. 按 AGENTS.md 部署正式 exe 并重启。

---

## 4. 执行顺序与依赖

```
T0 (基线) → T1 (入库) → T2 (清理)
                ├→ T3 (死代码) → T4 (渲染基类) → T6 (Claude调度)
                ├→ T5A (round-trip测试) → T5B (可选序列化收敛)
                ├→ T7 (微优化, 在T3–T6之后)
                ├→ T8 (DPAPI, 独立)
                └→ T9 (报告, 独立)
全部完成 → T10 (收尾)
```
串行执行即可：T0→T1→T2→T3→T4→T5A→(T5B)→T6→T7→T8→T9→T10。

## 5. 最终全量 Gate（T10 中执行，全部满足才算交付）

1. `git status --porcelain` 为空（全部提交）；`git log --oneline` 中每个任务一个或多个 `Spec-T*` 提交。
2. 全量构建 0 error，csc 警告行数 ≤ T0 基线。
3. §0.5 六项自检全 PASS + `--test` 跑通（网络项失败需附当时网络状态说明）。
4. `--test-settings-open-close --iterations 50` 输出正常摘要。
5. `--test-radar-display-lifecycle --iterations 200`（加倍）PASS。
6. 渲染对比：final vs baseline，7 组全部 `RESULT: PASS`；T3/T4/T5/T6/T7 的中间对比记录齐全存于 `_build\spec-render\`。
7. 主源码总行数比 T0 基线减少 ≥ 2,500 行（T3≥300 + T4≥800 + T5B≥1900 或 T5B 放弃时 ≥ 1,100 并注明）。
8. 正式 exe 部署后连续运行 ≥ 60 分钟：error.log 零新增、`--diagnose-radar-runtime --diagnose-seconds 30` 的句柄/GDI 数值与运行 5 分钟时相比增幅 < 5%。
9. 数据目录中不存在明文 token/api-key（`.txt` 均已 `.migrated`）。
10. 所有 manual-check.md（T4/T5/T6/T8）逐项勾选完毕并附时间戳。

任何一条失败：定位 → 修复 → 重跑该条及其上游依赖条目；连续两次同一条失败 → 停止并向用户报告，禁止降低阈值或跳过。
