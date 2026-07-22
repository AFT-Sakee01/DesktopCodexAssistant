# Desktop Codex Assistant（UX3407N / UX3607O 专调版）

适用版本：2.0.0.1

A Windows-on-Arm desktop workspace for AI-assisted development: ten right-edge metric/quota tiles, five left-edge dock tabs and boards, an operation panel, and an on-demand settings window. Sampling and Radar coordination run in hidden owners. Tuned for ASUS UX3407N / UX3607O; ARM64 is the formal build target.

本程序把 AI 辅助开发时最常盯的信息固定到桌面两侧：右侧 10 个独立指标方块，左侧 5 个停靠标签/看板，另有操作面板与按需打开的设置窗口。`WidgetForm` 只做隐藏协调，`CodexRadarForm` 与 `PowerThermalForm` 只做永久 headless 数据所有者；它们不再作为可见窗口。整个项目由 AI 编写与维护——OpenAI Codex 主创，Anthropic Claude（Opus / Fable）参与功能开发、审查与修复。

## 运行表面与数据所有者

| 类别 | 当前内容 |
|---|---|
| 右侧 10 个方块 | CPU、MEM、DISK、NET、GPU、NPU、PWR、GUARD、Codex 额度、Claude 额度；每项是独立 `MetricTileForm`，悬停详情只消费同一份快照 |
| 左侧 5 个停靠位 | Network、Spec Board、Codex Task、GUARD、Codex IQ；常驻 tab 与展开 board 组成一套停靠拓扑 |
| Network 看板 | 接口、DNS、公网连通性、GFW、云服务、PathPing、固定 Ping，以及共享 Clean IP 出口画像；只以 Dock 形态展示 |
| Operation | 扇形速控盘、常用开关、电池保养、CTF 重启与 SeelenUI 联动 |
| Settings | Win11 风格设置窗口，按需显示；全局布局编辑只编辑上述 10 个方块、5 个 tab 与 Operation，共 16 项 |
| 隐藏协调与采样 | `WidgetForm` 是隐藏宿主；`CodexRadarForm` 维护 Codex 公共 Radar 与 Codex/Claude 官方额度/服务状态，`PowerThermalForm` 维护功耗/温度；可见表面只读缓存快照 |

右侧方块的自动排列仍以“主显示器/主工作区”设置为基线；这些设置继续服务当前 tile 拓扑。

## 硬件支持

本分支以 ASUS UX3407N / UX3607O（Windows on Arm）为主要校准对象，正式构建与验收默认只覆盖 ARM64。其他 Windows 机器可以运行，但温度、NPU、GPU、电池与厂商控制类数据可能缺失或降级；x64 仅在明确要求时单独构建。详见 [Hardware-Support.md](Docs/Hardware-Support.md)。

## 构建与运行

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1

.\Release\DesktopCodexAssistant-arm64.exe
```

零第三方依赖：直接用 Roslyn csc 编译，运行时为 Windows 自带 .NET Framework，单 exe 分发。正式源码集合由 `Build-Sources.json` 精确登记；发布前使用 `Build-Arm64.ps1 -RequireTrackedSources` 验证所有源码已进入本地 Git 提交。

安装 / 卸载（写入 / 移除 HKCU 自启动项）：

```powershell
.\Install.cmd      # 或 DesktopCodexAssistant.exe --install [--no-start]
.\Uninstall.cmd    # 或 DesktopCodexAssistant.exe --uninstall
DesktopCodexAssistant.exe --stop   # 停止正在运行的实例
```

## 命令行入口一览

| 类别 | 参数 |
|---|---|
| 运行模式 | `--desktop-parent` / `--workerw`（桌面宿主层）、`--night-proof`、`--restart-after-pid <pid>` |
| 自检 | `--test`、`--test-logger`、`--test-layout`、`--test-settings-bindings`、`--test-display-recovery`、`--test-operation-panel`、`--test-codex-task-monitor`、`--test-specboard-manager`、`--test-settings-open-close [--iterations N]`、`--test-radar-display-lifecycle [--iterations N]` |
| 渲染采样（离屏出 PNG） | `--render-networkmonitor` / `--render-operation` / `--render-tilecolumn`，以及带模式参数的 `--render-specboard <sample|current>`、`--render-specboardmanager <sample|current>`、`--render-guard <sample|current>`；均支持 `--out <目录>` |
| 诊断 | `--diagnose-idle-cpu [--diagnose-minutes N]`、`--diagnose-radar-runtime [--diagnose-seconds N]`、`--dump-codex-tasks`（只读，输出任务状态 / 模型 / token 数字与官方会话标题，不含提示词、回复或完整会话路径） |

## 数据与隐私

- 设置、日志与缓存全部位于 `%LOCALAPPDATA%\DesktopCodexAssistant`，不写注册表（自启动项除外）、不上传任何数据。
- Claude setup-token 以 Windows DPAPI（当前用户范围）加密存储在本地；DeepSeek 服务探测不读取凭据或账户数据。
- “大陆出口保护”默认开启：出口未知或结果过期时静默阻断本程序自身的 OpenAI/Anthropic 请求；明确检测到中国大陆出口或 GFW 墙内信号时显示可暂时隐藏 60 秒的全屏警告。它不修改 hosts、不提权，也不拦截其它程序。
- 下节列出的全部网络请求均为只读探测 / 查询，可在设置中逐项关闭。
- 旧版本目录（`CodexDeveloperAssistantWindowOnWOA`、`DesktopPerfWidget-Lite`）的数据首次启动时自动迁移，不覆盖新文件。

## 引用的网站与在线服务

本程序运行时会访问以下第三方站点与服务，在此一并致谢。URL 与持久化资源的唯一权威登记见 [INTERFACE_INDEX.jsonl](Docs/Interfaces/INTERFACE_INDEX.jsonl)，调度见 [组件刷新规则](Docs/Component-Refresh-Rules.md)，Radar 数据所有权见两份现行 Radar 架构文档。

**AI 服务状态与社区数据**

| 服务 | 用途 |
|---|---|
| [Codex Radar](https://codexradar.com/)（codexradar.com） | Codex 公共状态、模型 IQ、速蹬窗口与 RSS；结构化 `current.json` 为主源，首页仅补缺失的速蹬窗口 |
| [OpenAI Status](https://status.openai.com/) | OpenAI 官方服务状态（Statuspage v2 API） |
| [Anthropic Status](https://status.claude.com/) | Claude 官方服务状态（Statuspage v2 API） |
| [DeepSeek API](https://api.deepseek.com/) | 无凭据服务可达性探测；只判断网关/服务健康，不读取余额 |

**个人账户额度（需用户自行绑定凭据）**

| 服务 | 用途 |
|---|---|
| [Anthropic API](https://api.anthropic.com/)（`/api/oauth/usage`，`/v1/messages` 限额头兜底） | Claude Code setup-token 个人额度 |
| [ChatGPT 后端](https://chatgpt.com/)（`backend-api/wham/*`） | Codex / ChatGPT 个人额度与重置积分 |

**网络探测基础设施**

| 服务 | 用途 |
|---|---|
| [Microsoft NCSI](http://www.msftconnecttest.com/connecttest.txt) | 联网 / 强制门户检测（与 Windows 系统同款机制） |
| [ipify](https://api.ipify.org/) | 公网 IP 查询 |
| [cleanip.io](https://cleanip.io/) | 出口 IP 画像（纯净度评分 / 原生 IP / 住宅 IP） |
| [Cloudflare DoH](https://cloudflare-dns.com/dns-query) | DNS over HTTPS 解析（出境探测） |

云厂商健康探测站点与固定 ping 目标均可在设置中自定义增删，默认目标清单不在此列出，见接口索引登记。

## 借鉴与互操作的第三方程序

- **[Seelen UI](https://github.com/eythaann/Seelen-UI)**（AGPL-3.0）：本项目**不包含、不修改、不链接、不再分发** Seelen UI 的任何代码。可选的联动功能仅通过进程/窗口检测和用户已安装的 `slu.exe` 命令行与独立安装的 Seelen UI 实例互操作（电源菜单触发、Dock 拉前、唤醒后重启、Z-order 协调）；全屏窗口的判定策略也参考了 Seelen UI 的同类做法。Seelen UI 名称仅用于描述互操作关系。
  - *This project does not include, modify, link, or redistribute Seelen UI code. Optional actions only interoperate with a separately installed instance through process/window detection and the installed `slu.exe` CLI. Seelen UI is a separate AGPL-3.0 project; its name is used only to describe interoperability.*
- **OpenAI Codex CLI**：额度计划功能通过 `codex app-server`（stdio JSON 协议）读取和暂停/恢复 goal；任务看板以只读方式解析 `~/.codex/sessions` 的会话 rollout 文件格式。
- **Anthropic Claude Code CLI**：额度链路使用其 `setup-token` 授权流程与 `statusLine` 配置桥（本程序生成一个只读桥接脚本，把状态行额度数据落到本地缓存）。
- **[codex-monitor-hud](https://github.com/LH-03/codex-monitor-hud)**（LH-03，MIT License，Copyright (c) 2026 Codex Monitor HUD Contributors）：Codex 用量/状态桌面 HUD 的先行实现，本项目的 Codex 监控 HUD 形态与部分实现思路参考了它。
- **[codex-island](https://github.com/ericjypark/codex-island)**（ericjypark，MIT License，Copyright © 2026 Eric Park）：灵动岛式 Codex 状态浮窗，界面形态与功能取舍上有所参考。
- **Claude / Codex 用量监控实践**：Claude 以官方 OAuth usage 为权威额度源，并以 Claude Code statusline 桥接缓存作为无额外请求路径。
- **Windows `pathping`**：网络停靠板的逐跳诊断借鉴其"路径发现 + 逐跳统计"语义，并重写了归因逻辑——区分中间路由器对 ICMP 的限速与真实链路丢包，避免传统 traceroute 式误报。
- **MyASUS / ASUS System Control Interface**：电池保养暂停 / 恢复通过 ASUS 键盘宿主程序转发 `acin_set` / `acin80` 指令实现，行为与 MyASUS 的 24 小时暂停语义一致。
- **CodexSleepGuard**（作者前作 PowerShell 工具）：GUARD 电源守护板是它的进程内 C# 重实现，补充了倒计时环形界面。
- **OpenAI Codex 桌面宠物**：Z-order 保护会识别其浮层窗口并保持其显示在本程序小窗之上（可在设置关闭）。
- **Atlassian Statuspage**：多个厂商状态页共用其 v2 API 格式，状态解析按该格式实现。

## 技术文档

- [维护历史](Docs/Maintenance/CHANGELOG.jsonl)
- [硬件支持策略](Docs/Hardware-Support.md)
- [性能采样与可见表面运行时](Docs/Performance-And-Window-Runtime.md)
- [组件刷新规则](Docs/Component-Refresh-Rules.md)
- [Codex / Claude Radar 数据所有者架构](Docs/CodexRadar-Architecture.md)
- [Claude CLD 官方额度链架构](Docs/Codex-ClaudeRadar-Architecture.md)
- [功耗温度数据所有者架构](Docs/PowerThermal-Architecture.md)
- [网络监控停靠架构](Docs/NetworkMonitor-Architecture.md)
- [Spec Board 架构](Docs/SpecBoard-Architecture.md)
- [GUARD 看板架构](Docs/GuardBoard-Architecture.md)
- [接口与可复用资源](Docs/Interface-And-Reuse-Resources.md)
- [机器可读接口索引](Docs/Interfaces/INTERFACE_INDEX.jsonl)
- [机器可读功能索引](Docs/Indexes/FEATURE_INDEX.jsonl)

当前可见拓扑以本页的“10 + 5 + Operation + Settings”为准；隐藏宿主与 headless owners 只提供协调和数据。Dock 启动器、Launchpad、顶栏与 Direct2D 工程不属于本产品范围。

历史设计快照（superseded，仅供追溯，不作为当前实现依据）：[旧数据源与缓存说明](Docs/Fable5-Data-Sources-And-Caching-Technical.md)、[旧多悬浮窗渲染规程](Docs/Fable5-Frontend-Rendering-Technical.md)、[旧 EvenRow 表盘说明](Docs/Claude-EvenRow-DialCard-Technical.md)。
