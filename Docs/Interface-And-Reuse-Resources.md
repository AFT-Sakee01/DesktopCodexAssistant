# 接口与复用资源汇总

适用版本：1.0.5.39

## 1. 文档用途

本汇总以当前源码为准，帮助后续修改在新增实现前优先找到可复用接口、服务、组件、命令和持久化资源。

机器可检索的完整索引位于：

`Docs/Interfaces/INTERFACE_INDEX.jsonl`

JSONL 中每行是一个独立对象，稳定 ID 用于后续检索、更新和废弃标记。

## 2. 复用顺序

1. 查询 `Docs/Interfaces/INTERFACE_INDEX.jsonl` 的 `id`、`name`、`purpose` 和 `reuse`。
2. 优先调用已有内部 API，不在窗口类中重复实现系统调用或网络调度。
3. 外部请求复用已有 reader、单飞、缓存、取消和过期结果保护。
4. 新设置完整接入默认值、Clone、Load、Save、Normalize、设置 UI 和绑定自测。
5. 新 layered 窗口复用窗口生命周期、`LayeredBitmapSurface`、字体缓存和防烧屏处理。
6. 新持久化文件放在 `%LOCALAPPDATA%\DesktopCodexAssistant`，不写入程序目录。

## 3. 外部服务

| 索引 ID | 服务 | 所属模块 | 复用重点 |
| --- | --- | --- | --- |
| `external_api.codex_radar.current` | Codex Radar current.json | `CodexRadarForm` | 北京时间整点调度、半日 IQ 窗口、速蹬 `opened_at/closed_at`、RSS 链接、动态模型目录、JSON/HTML 回退和模型缓存 |
| `external_api.codex_radar.feed` | Codex Radar RSS | `CodexRadarForm` | 只跟随 current.json 成功响应读取，用 GUID/pubDate 去重额外重置 |
| `external_api.claude.status` | Claude Statuspage | `CodexRadarForm` | 单行 API 摘要和服务健康状态映射 |
| `external_api.codex_provider.usage` | ChatGPT Codex usage | `CodexRadarForm` | 当前软件为 `CODEX` 时优先读取 5h/7d 用量；单飞、5 min 正常周期、429 15 min 冷却；旧 session/quota.ini 作为 fallback |
| `external_api.claude_code.usage` | Claude Code OAuth usage | `CodexRadarForm` | 保留的非默认回退路径；默认 Claude 用量读取改走 statusline 本地缓存，避免检测动作额外消耗 Claude token |
| `external_api.openai.status` | OpenAI Statuspage | `CodexRadarForm` | 五阶段连接诊断的回滚接口；当前 `CodexConnectionFlowEnabled=false` 时不调度 |
| `external_api.deepseek.balance` | DeepSeek user balance | `CodexRadarForm` | DeepSeek 余额行、API 摘要异常候选和本地 24 小时消耗估算 |
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
| `--stop` | 通过命名事件停止现有实例 |
| `--install` / `--uninstall` | 维护当前用户启动项 |
| `--no-start` | 安装后不启动 |
| `--restart-after-pid PID` | 等待旧进程退出后重启 |
| `--test` | 基础采样测试 |
| `--test-logger` | 日志存储策略测试 |
| `--test-layout` | 分辨率布局换算测试 |
| `--test-settings-bindings` | 设置控件绑定测试 |
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

## 5. Windows 系统接口

| 索引 ID | 能力 | 主要复用位置 |
| --- | --- | --- |
| `service.windows.pdh` | CPU、磁盘、网络、GPU、NPU 计数器 | `PdhSampler` |
| `service.windows.wmi_hardware` | CPU、内存、磁盘、GPU、NPU 硬件信息 | `PdhSampler` |
| `service.windows.wmi_power_thermal` | 电池功耗、温度区、电源计划 | `PowerThermalForm` |
| `service.windows.layered_window` | 透明分层窗口提交和缓存 | 所有监控窗口 |
| `event.windows.power_display` | 显示、电源、电量和电源模式通知 | `WidgetForm`、`PowerThermalForm` |
| `service.windows.wlan` | Wi-Fi SSID、信号和链路信息 | `NetworkMonitorReader` |
| `service.windows.ui_automation` | 开始按钮、隐藏托盘等系统控件 | `NativeMethods` |
| `service.windows.input_language` | 前台输入法语言和模式 | `NativeMethods` |
| `event.network.change` | 网络地址与可用性变化 | 网络 readers |
| `event.keyboard.ctrl_d` | 全局 Win+D（保留旧稳定 ID） | `GlobalWinDWatcher` |
| `event.settings.file_watcher` | 外部设置热加载 | `WidgetForm` |
| `event.codex.sessions_watcher` | Codex rollout JSONL 更新 | `CodexRadarForm` |
| `resource.windows_icon_fonts` | Segoe Fluent Icons / Segoe MDL2 系统图标字体 | `Win11SettingsForm`、`SettingsFluentResources` |

新增 P/Invoke、COM、WinRT 或 Shell 调用优先放入 `Interop/NativeMethods.cs`。

## 6. 内部公共接口

| 索引 ID | 组件 | 复用规则 |
| --- | --- | --- |
| `internal_api.widget_settings` | 设置、迁移、布局与性能策略 | 新设置完整接入读写和自测链 |
| `internal_api.software_runtime_presence` | Codex/Claude 运行态与软件身份分类 | 复用包路径、进程名、产品元数据和受限标题回退；常规查询走缓存快照，未知进程名只允许使用 60 s 漏判发现，不在绘制路径枚举进程 |
| `internal_api.logger` | 缓冲日志、错误日志和 GFW 日志 | 高频事件聚合或只记录状态变化；目录大小扫描默认 10 分钟节流，活动日志轮转时强制执行 |
| `internal_api.timing_stats` | 12 小时滚动耗时统计 | 新增性能计时点复用内存滚动窗口和 15 分钟摘要日志 |
| `internal_api.idle_cpu_diagnostics` | 空闲 CPU 飙升归因 | 复用一次性 CPU/进程采样、事件日志扫描和公式化归因规则 |
| `internal_api.pdh_sampler` | 性能快照 | UI 不直接访问 PDH/WMI |
| `internal_api.network_monitor_reader` | 网络状态总快照 | UI 只读取 Clone |
| `internal_api.gfw_probe_reader` | GFW 调度 | 与云检测保持解耦 |
| `internal_api.cloud_endpoint_probe` | 云服务异步探测 | 复用取消、缓存和异常确认 |
| `internal_api.clean_ip_reader` | 出口身份快照 | 复用单飞和网络事件 |
| `internal_api.native_methods` | Windows 互操作门面 | 避免散落 P/Invoke |
| `internal_api.design_tokens` | 色彩、透明度、圆角和字体 | 禁止重复硬编码语义色 |
| `internal_api.quota_ring_presentation` | Codex/Claude 共用额度环绘制 | 余额环、消耗环、数字和重置文本统一绘制；仅 Codex 速蹬时间可用显式标志绕过未运行降亮，数字规则不得一起绕过 |
| `internal_api.radar_clock_dial` | Codex/Claude 共用 IQ 时钟状态机与绘制 | 周期边界、状态色、刷新点、弧线、日期/时间标签和 12 h/24 h 自测统一复用；窗体只注入快照字段、字体与 fitted-text 委托 |
| `internal_api.claude_radar_clock_auto_switch_selector` | Claude 时钟模型选择器 | 共享窗和独立窗都传入完整候选集；全局最新已是当前模型或并列包含当前模型时禁止写设置，独立窗启用时共享窗不得争用写入权 |
| `internal_api.ui_font_cache` | 字体缓存 | 每个窗口生命周期内复用 |
| `internal_api.shared_encoding` | UTF-8 no BOM 编码常量 | 持久化文本写入复用 `SharedEncoding.Utf8NoBom`，不在调用点重复 `new UTF8Encoding(false)` |
| `internal_api.burn_in_protection` | 像素位移和隐藏反色 | 新窗口分配独立 salt；操作面板隐藏态只为可见按钮恢复命中 Alpha |
| `internal_api.hover_interaction_policy` | 鼠标隐藏命中策略 | 敏感鼠标范围、延迟显现、覆盖开启和反向隐藏统一复用，不在窗口中重复点命中或倒计时逻辑 |
| `internal_api.time_zone_utilities` | 北京时间调度和显示时区 | 区分业务时间与显示时间 |
| `internal_api.secret_store` | DPAPI CurrentUser 密钥文件保护 | 统一读写 `.bin` Base64 密文、迁移旧 `.txt` 到 `.txt.migrated`，不要在调用点手写明文 key 文件 |
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
| `file_format.deepseek_api_key` | `deepseek-api-key.bin` | DeepSeek 本地 API key 的 DPAPI 密文；设置页可写入/清除，禁止写入日志和 settings.ini；旧 `.txt` 只作迁移来源 |
| `file_format.codex_auth_json` | `%USERPROFILE%\.codex\auth.json` 或 `CODEX_HOME\auth.json` | Codex access token 只读来源；不写回、不刷新、不记录敏感内容 |
| `file_format.claude_statusline_quota` | `claude-statusline-quota.ini` | Claude Code statusline 桥接脚本写入的只读额度快照；默认 Claude 用量来源，不含 token |
| `command.claude_statusline_bridge` | `%USERPROFILE%\.claude\desktop-codex-statusline-bridge.ps1` | Claude Code `statusLine` 命令；程序仅在没有自定义 statusline 时自动安装，不覆盖用户已有命令 |
| `file_format.claude_code_credentials` | `CLAUDE_CODE_OAUTH_TOKEN` 或 `%LOCALAPPDATA%\DesktopCodexAssistant\claude-code-oauth-token.bin` | `claude setup-token` 生成 token 的保留回退来源；本地文件为 DPAPI 密文，旧 `.txt` 只作迁移来源；默认调度不调用，不自动执行命令、不读取 `.credentials.json`、不写回、不刷新、不记录敏感内容 |
| `file_format.application_icon_ico` | `Assets/AppIcon.ico` | 编译时嵌入 exe 的 Win32 图标，和 `ApplicationIcon` 运行时绘制保持同款 |
| `file_format.codex_quota` | `quota.ini` | Codex 额度缓存 |
| `file_format.claude_quota` | `claude-quota.ini` | Claude Code 用量缓存；格式与 `quota.ini` 相同但文件隔离 |
| `file_format.quota_reset_state` | `quota-reset-state.ini` | 本地 reset 保护、RSS 重置和速蹬开启去重 |
| `file_format.install_log` | `install.log` | 安装和卸载记录 |
| `resource_directory.codex_sessions` | `%USERPROFILE%\.codex\sessions` | 只读 Codex rollout 数据源 |
| `resource_directory.docs` | `Docs` | 技术文档和接口索引 |
| `resource_directory.legacy_executables` | `Artifacts/LegacyExecutables` | 历史归档，不参与运行 |
| `resource_directory.build_outputs` | 根目录 EXE 与 `Release/` 正式产物 | ARM64 默认、x64 显式产物、GitHub 发布资产 |
| `file_format.spec_board.ledger` | `D:\E_Drive_Files\Codexproject\_spec_board\SPEC_BOARD.jsonl` | 跨项目 spec 现状账本，只读；属于用户开发环境资产，不是程序运行态数据 |
| `file_format.spec_board.projects` | 账本同目录 `PROJECTS.json` | 项目根目录、显示名和 `spec_glob` 注册表，只读 |

## 8. 窗口模块复用契约

主窗口通过 `WidgetForm` 协调：

- `CodexRadarForm`
- `PowerThermalForm`
- `NetworkMonitorForm`
- `ConnectionCheckForm`
- `OperationForm`
- `SpecBoardForm`（自动弹窗开启时由 `OperationForm` 启动即持有并在隐藏状态监测；关闭时仍可按需创建）
- `SpecBoardManagerForm`（由 Spec Board footer 或双击两按钮启动器直接打开的独立原生管理工具窗）

监控窗口按职责实现：

- `ApplyRuntimeSettings`
- `ForceRefresh`
- `SetHiddenForFullscreen`
- `RecoverAfterDisplayResume`
- `PrepareForDisplaySuspend`
- `SetSharedInteractionPolling`
- `ProcessSharedInteractionTick`
- `ProcessSharedMaintenanceTick`，仅由需要共享低频维护的模块实现

分层窗口同时复用 `NativeMethods.LayeredBitmapSurface`、`UiFontCache`、`DesignTokens`、`BurnInProtection`、`LayeredWidgetFormBase.CurrentSettings`、`LayeredWidgetFormBase.ShouldRefreshBurnInPosition`、`LayeredWidgetFormBase.ComputeOpacityAlpha`、内容变化判断和透明度-only 提交。

## 9. 索引维护规则

1. 更名尽量保留已有稳定 `id`。
2. 废弃项将 `status` 改为 `deprecated`，不直接删除。
3. 更新 `updated_version` 和 `updated_at`。
4. 确认 `location` 与 `references` 指向现有项目路径。
5. 逐行解析 JSONL，检查唯一 ID 和必填字段。
6. 不登记密码、Token、Cookie、私钥或完整连接串。

## 10. 待议项

- JSON 统一入口待议：当前仍保留各模块现有 `JavaScriptSerializer` / 轻量解析路径，不在本轮替换；后续若统一 JSON 门面，需要先做 settings、quota、网络历史、外部 API payload 的兼容性夹具和回归测试。
