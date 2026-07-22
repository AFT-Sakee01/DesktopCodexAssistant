# 接口与复用资源汇总

适用版本：2.0.0.8

## 1. 文档用途

本汇总以当前源码为准，帮助后续修改在新增实现前优先找到可复用接口、服务、组件、命令和持久化资源。现行用户界面是右侧 10 个 metric tiles、左侧 5 个固定 Dock、`OperationForm` 与按需打开的设置窗口；`WidgetForm` 是隐藏宿主，`CodexRadarForm` 与 `PowerThermalForm` 是永久 headless 数据所有者。

机器可检索的完整索引位于：

`Docs/Interfaces/INTERFACE_INDEX.jsonl`

JSONL 中每行是一个独立对象，稳定 ID 用于后续检索、更新和废弃标记。

## 2. 复用顺序

1. 查询 `Docs/Interfaces/INTERFACE_INDEX.jsonl` 的 `id`、`name`、`purpose` 和 `reuse`。
2. 优先调用已有内部 API，不在窗口类中重复实现系统调用或网络调度。
3. 外部请求复用已有 reader、单飞、缓存、取消和过期结果保护。
4. 新设置完整接入默认值、Clone、Load、Save、Normalize、设置 UI 和绑定自测。
5. 新可见 layered surface 复用窗口生命周期、`LayeredBitmapSurface`、字体缓存和防烧屏处理；不要把 headless owner 重新变成窗口。
6. 新持久化文件放在 `%LOCALAPPDATA%\DesktopCodexAssistant`，不写入程序目录。

## 3. 外部服务

| 索引 ID | 服务 | 所属模块 | 复用重点 |
| --- | --- | --- | --- |
| `external_api.codex_radar.current` | Codex Radar current.json | `CodexRadarForm` | 北京时间整点调度、schema 2 模型 IQ、速蹬 `opened_at/closed_at`、RSS 链接、动态模型目录和模型缓存；HTML 只补缺失的速蹬窗口 |
| `external_api.codex_radar.feed` | Codex Radar RSS | `CodexRadarForm` | 只跟随 current.json 成功响应读取，用 GUID/pubDate 去重额外重置 |
| `external_api.claude.status` | Claude Statuspage | `CodexRadarForm` | 单行 API 摘要和服务健康状态映射 |
| `external_api.codex_provider.usage` | ChatGPT Codex usage | `CodexRadarForm` | 当前软件为 `CODEX` 时优先读取 5h/7d 用量；单飞、5 min 正常周期、429 15 min 冷却；旧 session/quota.ini 作为 fallback |
| `external_api.claude_code.usage` | Claude Code OAuth usage | `ClaudeCodeUsageReader` / `CodexRadarForm` | CLD tile 的官方额度来源；无 token 时消费 Claude Code statusline 本地快照，结果统一提交到官方额度缓存 |
| `external_api.openai.status` | OpenAI Statuspage | `CodexRadarForm` | 五阶段连接诊断的回滚接口；当前 `CodexConnectionFlowEnabled=false` 时不调度 |
| `external_api.deepseek.service_health` | DeepSeek service gateway | `DeepSeekServiceMonitor` / `CodexRadarForm` | 无凭据服务可达性探测；只输出 known/available/error，不查询、解析或保存余额 |
| `external_api.chatgpt.probe` | ChatGPT HTTPS | `CodexRadarForm` | 五阶段连接诊断的回滚接口；当前停用时不调度 |
| `external_api.cleanip.me` | CleanIP | `CleanIpConnectionReader` | 整点抖动、错误重试和测试快照 |
| `external_api.ipify.public_ip` | ipify | `NetworkMonitorReader` | 单飞和 network generation 校验 |
| `external_api.microsoft.connecttest` | Microsoft NCSI | `NetworkMonitorReader` | 与 Ping 组合判断门户和离线 |
| `external_api.cloudflare.doh` | Cloudflare DoH | `GfwProbeReader` | 独立 DNS 对照 |
| `external_api.gfw.probe_hosts` | GFW 控制组与候选组 | `GfwProbeReader` | DNS/TCP/TLS/HTTP 多阶段语义 |
| `external_api.cloud.health_targets` | 六个云服务端点 | `CloudEndpointProbe` | 并发上限、缓存、三次采样和取消 |
| `external_api.cloudflare.status_v2` | Cloudflare Statuspage v2 | `CloudEndpointProbe` | `Cf` 方块官方状态源，复用 Statuspage 解析、地区过滤、条件请求和状态滞后确认 |
| `external_api.akamai.status_v2` | Akamai Statuspage v2 | `CloudEndpointProbe` | `Ak` 方块官方状态源，复用 Statuspage 解析和地区文本过滤 |
| `external_api.github.status_v2` | GitHub Statuspage v2 | `CloudEndpointProbe` | `Gi` 方块官方状态源，复用 Statuspage 解析、条件请求和异常缓存 |
| `external_api.aws.home_reachability` | AWS public HTTPS reachability | `CloudEndpointProbe` | `Aw` 方块只代表 `https://aws.amazon.com/` 可达性，不等同 AWS Health 官方状态 |
| `external_api.azure.status_rss` | Azure Status RSS | `CloudEndpointProbe` | `Az` 方块公开 RSS 源，按标题/描述关键字和地区文本过滤 |
| `external_api.google_cloud.service_health` | Google Cloud Service Health | `CloudEndpointProbe` | `Go` 方块公开 incidents JSON，按 impact 和 affected locations 判断 |

## 4. 命令与进程接口

### 4.1 主程序 CLI

入口：`DesktopCodexAssistant.exe`

| 参数 | 用途 |
| --- | --- |
| `--desktop-parent` / `--workerw` | 尝试挂接桌面 WorkerW |
| `--stop` | 通过命名事件停止现有实例；宿主注册后台等待并直接投递 `WM_CLOSE`，主 tick 轮询只作兜底 |
| `--install` / `--uninstall` | 维护当前用户启动项 |
| `--no-start` | 安装后不启动 |
| `--restart-after-pid PID` | 等待旧进程退出后重启 |
| `--test` | 基础采样测试 |
| `--test-logger` | 日志存储策略测试 |
| `--test-layout` | 分辨率布局换算测试 |
| `--test-settings-bindings` | 设置控件绑定测试 |
| `--test-codex-task-monitor` | Codex 任务后端隔离 fixture 自测 |
| `--dump-codex-tasks` | 只读输出当前任务快照 JSON；只含工作区末级名，不含正文和完整路径 |
| `--test-display-recovery` | 分层窗口显示恢复测试 |
| `--test-operation-panel` | 操作面板命中遮罩、动画、单飞、FPS 间隔和 SeelenUI 结果映射测试 |
| `--render-specboard sample/current --out DIR` | 输出 Spec Board 确定性 fixture 或真实只读账本画面 |
| `--test-specboard-manager` | 验证五态解析、SeenState、管理窗布局/读写、冲突、备份、原子替换、批量、回收站和锁定文件回滚 |
| `--diagnose-idle-cpu --diagnose-minutes N` | 一次性空闲 CPU/息屏发热归因诊断 |

对应索引：`command.application.cli`

### 4.2 构建与安装

| 索引 ID | 入口 | 约束 |
| --- | --- | --- |
| `command.build.arm64` | `Build-Arm64.ps1` | 默认构建入口 |
| `command.build.x64` | `Build-X64.ps1` | 仅在用户明确要求时调用 |
| `command.install` | `Install.ps1` | 写启动项并启动程序 |
| `command.uninstall` | `Uninstall.ps1` | 删除启动项并按需停止程序 |

### 4.3 IPC 与外部程序

| 索引 ID | 接口 |
| --- | --- |
| `event.application.stop` | `Local\DesktopCodexAssistantStop` 命名事件 |
| `service.application.single_instance` | `Local\DesktopCodexAssistant` 命名 Mutex |
| `command.seelen.cli` | SeelenUI `slu.exe`，电源菜单调用在后台单飞执行，UI 线程只处理结果和回退 |
| `command.seelen.process_control` | 仅对用户单独安装的 SeelenUI 做进程检测、拉前、`taskkill` 重启和 top bar/dock 层级协作；不包含、修改、链接或再分发 SeelenUI 代码 |
| `command.asus_keyboard_host.battery_care` | MyASUS / ASUS PC Assistant 的 `AsusKeyboardHost.exe -HWSettingsToast acin_set/acin80` 厂商电池维护入口；不可用时按 UI 灰显或 FPS fallback 处理 |
| `command.windows.shell_actions` | Windows URI、AppsFolder、Shell.Application、UI Automation、键盘回退和系统进程入口，包含 Live Captions 与 `ms-clicktodo` / CoreAI AI Studio |
| `external_api.codex_app_server` | Codex app-server 的换行分隔 stdio RPC；只从经过绝对路径、位置、reparse point、OpenAI Authenticode 发布者及打开文件 lease 验证的 `codex.exe` 启动 |

## 5. Windows 系统接口

| 索引 ID | 能力 | 主要复用位置 |
| --- | --- | --- |
| `service.windows.pdh` | CPU、内存提交/换出、磁盘、网络、GPU、NPU 计数器 | `PdhSampler`；MEM 压力复用主 query，页面读入不进入压力公式，UI 不重采样 |
| `service.windows.wmi_hardware` | CPU、内存、磁盘、GPU、NPU 硬件信息 | `PdhSampler` |
| `service.windows.wmi_power_thermal` | 电池功耗、温度区、电源计划 | `PowerThermalForm` |
| `service.windows.layered_window` | 透明分层窗口提交和缓存 | 10 个 tiles、5 组 Dock tab/board 与 `OperationForm` 等可见分层表面；headless owners 不提交位图 |
| `event.windows.power_display` | 显示、电源、电量和电源模式通知 | `WidgetForm`、`PowerThermalForm` |
| `service.windows.wlan` | Wi-Fi SSID、信号和链路信息 | `NetworkMonitorReader` |
| `service.windows.ui_automation` | 开始按钮、隐藏托盘等系统控件 | `NativeMethods` |
| `service.windows.input_language` | 前台输入法语言和模式 | `NativeMethods` |
| `event.network.change` | 网络地址与可用性变化 | 网络 readers |
| `event.keyboard.ctrl_d` | 全局 Win+D（保留旧稳定 ID） | `GlobalWinDWatcher` |
| `event.settings.file_watcher` | 外部设置热加载 | `WidgetForm` |
| `event.codex.sessions_watcher` | Codex rollout JSONL 更新；额度失效和任务增量读取共用唯一 watcher | `CodexRadarForm` |
| `resource.windows_icon_fonts` | Segoe Fluent Icons / Segoe MDL2 系统图标字体 | `Win11SettingsForm`、`SettingsFluentResources` |

新增 P/Invoke、COM、WinRT 或 Shell 调用优先放入 `Interop/NativeMethods.cs`。

## 6. 内部公共接口

| 索引 ID | 组件 | 复用规则 |
| --- | --- | --- |
| `internal_api.widget_settings` | 设置、迁移、布局与性能策略 | 新设置完整接入读写和自测链 |
| `internal_api.codex_task_monitor_reader` | 任务状态增量读取、不可变快照和注意事件 | 由 `CodexRadarForm` 唯一构造/释放；消费现有 watcher 转发，不自行遍历目录或创建 timer |
| `internal_api.software_runtime_presence` | Codex/Claude 运行态与软件身份分类 | 复用包路径、进程名、产品元数据和受限标题回退；常规查询走缓存快照，未知进程名只允许使用 60 s 漏判发现，不在绘制路径枚举进程 |
| `internal_api.logger` | 缓冲日志、错误日志和 GFW 日志 | 高频事件聚合或只记录状态变化；目录大小扫描默认 10 分钟节流，活动日志轮转时强制执行 |
| `internal_api.timing_stats` | 12 小时滚动耗时统计 | 新增性能计时点复用内存滚动窗口和 15 分钟 P95/P99/max 摘要，不逐样本写盘 |
| `internal_api.idle_cpu_diagnostics` | 空闲 CPU 飙升归因 | 复用一次性 CPU/进程采样、事件日志扫描和公式化归因规则 |
| `internal_api.pdh_sampler` | 性能快照与内存压力采样 | UI 不直接访问 PDH/WMI；可用内存、换出速率和 Commit 安全下限统一进入 `PerfSnapshot` |
| `internal_api.snapshot_models` | `PerfSnapshot`、`MemoryPressureTracker` 与 `MemoryPressureHistoryPoint` 快照契约 | MEM tile/展开窗只消费已经计算和防抖的三态压力及 60 秒时间历史，不复制阈值 |
| `internal_api.network_monitor_reader` | 网络状态总快照 | UI 只读取 Clone |
| `internal_api.gfw_probe_reader` | GFW 调度 | 与云检测保持解耦 |
| `internal_api.cloud_endpoint_probe` | 云服务异步探测 | 复用取消、缓存和异常确认 |
| `internal_api.clean_ip_reader` | 出口身份快照 | 复用单飞和网络事件 |
| `internal_api.ai_request_protection` | OpenAI/Anthropic 敏感请求门控 | 复用 GFW 与 Clean IP 出口信号；未知/过期 fail-closed，不写 hosts、不拦其它进程 |
| `internal_api.china_egress_warning_form` | 中国大陆出口全屏警告 | 仅明确大陆/GFW 墙内显示；复用 owner tick 与 60 秒抑制，不新增 timer |
| `internal_api.native_methods` | Windows 互操作门面 | 避免散落 P/Invoke |
| `internal_api.design_tokens` | 色彩、透明度、圆角和字体 | 禁止重复硬编码语义色 |
| `internal_api.radar_clock_dial` | Radar 周期状态 helper | 只保留周期边界、状态、刷新点角度、标签和 12 h/24 h 纯逻辑自测；绘制上下文/API 已删除，不能据此恢复独立 Radar 窗口 |
| `internal_api.bounded_http_text_reader` | 有界 HTTP 文本读取 | 所有活动远端文本读取复用正文上限、总时限、取消、解压上限和严格 UTF-8；禁止在调用点无界读流 |
| `internal_api.codex_radar_url_policy` | Radar URL/SSRF 策略 | 仅接受登记的精确 HTTPS endpoint，DNS 解析命中 loopback、链路本地、私网或保留地址即拒绝 |
| `internal_api.codex_executable_path_policy` | Codex CLI 可执行文件信任策略 | 所有 app-server 启动入口复用失败关闭的路径/位置/签名策略；禁止裸 `codex`、PATH 搜索、当前目录/TEMP/UNC、reparse point 或可替换文件 |
| `internal_api.owner_operation_generation` | owner generation/取消边界 | Start/恢复建立 lease，Stop/挂起先取消并失效；迟到 completion 用 `TryExecuteCurrent` 拒绝全部副作用 |
| `internal_api.codex_radar_published_projection` | Radar 同代 published state | producer 一次原子替换，tile/IQ 只 clone 一份已发布状态，锁内禁止 I/O、日志、绘制和 UI dispatch |
| `internal_api.deepseek_service_monitor` | DeepSeek 服务健康单飞监控 | timer、网络事件和手动刷新 join 同一无凭据请求；健康状态只以 clone 快照交付 |
| `internal_api.ui_font_cache` | 字体缓存 | 每个窗口生命周期内复用 |
| `internal_api.shared_encoding` | UTF-8 no BOM 编码常量 | 持久化文本写入复用 `SharedEncoding.Utf8NoBom`，不在调用点重复 `new UTF8Encoding(false)` |
| `internal_api.burn_in_protection` | 像素微迁移和夜间亮度 | 新窗口分配独立 salt；自动列共享相位，左缘表面固定 X；不得在此重新引入隐藏颜色变换 |
| `internal_api.application_window_state_tracker` | 前台/对象窗口状态跟踪 | 按可见性策略动态启停对象 Hook，事件先按 HWND 有界合并再由 UI 线程批量消费 |
| `internal_api.ui_hang_watchdog` | UI 心跳与窗口事件诊断 | 卡死、重复卡死和恢复均写独立 JSONL，并携带队列累计计数 |
| `internal_api.hover_interaction_policy` | 鼠标隐藏命中策略 | 敏感鼠标范围、延迟显现、覆盖开启和反向隐藏统一复用，不在窗口中重复点命中或倒计时逻辑 |
| `internal_api.time_zone_utilities` | 北京时间调度和显示时区 | 区分业务时间与显示时间 |
| `internal_api.secret_store` | DPAPI CurrentUser 密钥文件保护 | 统一读写 `dpapi-v1:` envelope；旧格式只有严格 validator 通过后才原子迁移，损坏/未知 Base64 fail-closed 且原字节不变 |
| `internal_api.window_runtime_contract` | 设置、刷新、全屏、挂起、恢复和共享维护 | 新模块实现同等生命周期方法，低频维护复用主协调 tick |
| `internal_api.snapshot_models` | 跨线程快照契约 | 后台状态通过 Clone 交付 |
| `internal_api.drawing_and_rate_formatters` | Alpha 绘图和速率格式 | 不重复实现单位换算 |

## 7. 持久化资源

根目录：`%LOCALAPPDATA%\DesktopCodexAssistant`

| 索引 ID | 文件/目录 | 用途 |
| --- | --- | --- |
| `config.settings_ini` | `settings.ini` | 全部运行设置和布局 |
| `file_format.runtime_logs` | 主日志、错误日志、GFW 日志 | 运行和诊断 |
| `file_format.idle_cpu_diagnosis_report` | `idle-cpu-diagnosis-*.txt/.json` | 空闲 CPU 归因报告 |
| `file_format.codex_radar_cache` | `codex-radar-cache.ini` | 动态模型快照和历史基准 |
| `file_format.codex_radar_model_catalog` | `codex-radar-models.ini` | 模型下拉目录、可用状态和增删去重 |
| `file_format.codex_auth_json` | `%USERPROFILE%\.codex\auth.json` 或 `CODEX_HOME\auth.json` | Codex access token 只读来源；不写回、不刷新、不记录敏感内容 |
| `file_format.claude_statusline_quota` | `claude-statusline-quota.ini` | Claude Code statusline 桥接脚本写入的只读额度快照；默认 Claude 用量来源，不含 token |
| `command.claude_statusline_bridge` | `%USERPROFILE%\.claude\desktop-codex-statusline-bridge.ps1` | Claude Code `statusLine` 命令；程序仅在没有自定义 statusline 时自动安装，不覆盖用户已有命令 |
| `file_format.claude_code_credentials` | `CLAUDE_CODE_OAUTH_TOKEN` 或 `%LOCALAPPDATA%\DesktopCodexAssistant\claude-code-oauth-token.bin` | `claude setup-token` 生成 token 的保留回退来源；本地文件为 DPAPI 密文，旧 `.txt` 只作迁移来源；默认调度不调用，不自动执行命令、不读取 `.credentials.json`、不写回、不刷新、不记录敏感内容 |
| `file_format.application_icon_ico` | `Assets/AppIcon.ico` | 编译时嵌入 exe 的 Win32 图标，和 `ApplicationIcon` 运行时绘制保持同款 |
| `file_format.codex_quota` | `quota.ini` | Codex 额度缓存 |
| `file_format.claude_quota` | `claude-quota.ini` | 只由 `ClaudeCodeUsageReader` 官方 usage/statusline 链原子写入的 CLD 额度缓存；包含 5h/周额度、两个 reset 与来源更新时间 |
| `file_format.quota_reset_state` | `quota-reset-state.ini` | 本地 reset 保护、RSS 重置和速蹬开启去重 |
| `file_format.install_log` | `install.log` | 安装和卸载记录 |
| `resource_directory.codex_sessions` | `%USERPROFILE%\.codex\sessions` | 只读 Codex rollout 数据源；额度 fallback 与任务状态后端共用递归枚举，禁止写入会话文件 |
| `resource_directory.docs` | `Docs` | 技术文档和接口索引 |
| `resource_directory.legacy_executables` | `Artifacts/LegacyExecutables` | 历史归档，不参与运行 |
| `resource_directory.build_outputs` | 根目录 EXE 与 `Release/` 正式产物 | ARM64 默认、x64 显式产物、GitHub 发布资产 |
| `file_format.build_sources_manifest` | 项目根 `Build-Sources.json` | 正式编译的唯一 C# 源集合；清单外/缺失/重复源码立即失败，发布前再要求全部源码已被 Git 跟踪 |
| `file_format.spec_board.ledger` | `D:\E_Drive_Files\Codexproject\_spec_board\SPEC_BOARD.jsonl` | 跨项目 spec 现状账本，只读；属于用户开发环境资产，不是程序运行态数据 |
| `file_format.spec_board.projects` | 账本同目录 `PROJECTS.json` | 项目根目录、显示名和 `spec_glob` 注册表，只读 |

## 8. 运行表面与 owner 复用契约

`WidgetForm` 只承担隐藏消息循环、性能采样和生命周期协调，不绘制经典主窗口。它协调的现行对象分为三类：

| 类别 | 对象与边界 |
| --- | --- |
| 右侧可见表面 | CPU、MEM、DISK、NET、GPU、NPU、PWR、GUARD、Codex 额度、Claude 额度共 10 个 `MetricTileForm`；悬停详情复用一个 `MetricTileExpandForm`，只读同一 `MetricTileFeed`。MEM 外环/中心显示物理占用，内环显示三态压力，底部色带显示最近 60 秒压力历史 |
| 左侧固定 Dock | Network、Spec Board、Codex Task、GUARD、Codex IQ 共 5 枚 `EdgeDockTabForm` 及对应 board；五角色始终存在，只允许排序，不允许恢复浮动窗或禁用角色 |
| 其他用户界面 | `OperationForm` 常驻；`Win11SettingsForm` 与 `SpecBoardManagerForm` 按需打开，后二者是可聚焦的普通工具窗口 |
| headless owners | `CodexRadarForm` 统一拥有 Codex/Claude 双 family；`PowerThermalForm` 拥有功耗、温度和电池采样。两者只运行 scheduler、缓存和 cache-only snapshot builder，不调用 `Show()`、不参与布局编辑；旧 Radar/Power renderer 已物理删除 |

Network 的网络、GFW、云服务与 Clean IP 数据继续复用 `NetworkMonitorReader`、`GfwProbeReader`、`CloudEndpointProbe` 和 `CleanIpConnectionReader.Shared`，只投影到 Network Dock board；不存在独立连接检查窗口。`OperationForm` 启动时持有 Spec、Codex Task、GUARD、Codex IQ 四个 board，`NetworkMonitorForm` 持有 Network board，五者共同遵守固定左 Dock 拓扑。

可见分层表面按职责复用 `ApplyRuntimeSettings`、`ForceRefresh`、`SetHiddenForFullscreen`、`RecoverAfterDisplayResume`、`PrepareForDisplaySuspend` 及共享交互/维护 tick。它们同时复用 `NativeMethods.LayeredBitmapSurface`、`UiFontCache`、`DesignTokens`、`BurnInProtection` 的位移/两级视觉策略、`LayeredWidgetFormBase.CurrentSettings` 与 `PresentationLuminancePercent`、内容变化判断和透明度-only 提交。防烧屏状态只由 hidden `WidgetForm` 发布：左 tab 在自身既有 poll 中消费，右 tile/expand 由 host 按整组命中分发；不得为单窗新增 timer 或恢复旧 `BurnInHiddenModeColorProtectionEnabled`。headless owners 另走显式 `StartHeadlessDataOwner` / `StopHeadlessDataOwner` 生命周期；Radar owner 还复用 `OwnerOperationGeneration` 和原子 published projection，迟到任务不能提交副作用。

## 9. 索引维护规则

1. 更名尽量保留已有稳定 `id`。
2. 废弃项将 `status` 改为 `deprecated`，不直接删除。
3. 更新 `updated_version` 和 `updated_at`。
4. 确认 `location` 与 `references` 指向现有项目路径。
5. 逐行解析 JSONL，检查唯一 ID 和必填字段。
6. 不登记密码、Token、Cookie、私钥或完整连接串。

## 10. 待议项

- JSON 统一入口待议：当前仍保留各模块现有 `JavaScriptSerializer` / 轻量解析路径，不在本轮替换；后续若统一 JSON 门面，需要先做 settings、quota、网络历史、外部 API payload 的兼容性夹具和回归测试。
