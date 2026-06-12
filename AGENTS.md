# Codex Project Notes

This file is the primary Codex-readable maintenance log for Desktop Codex Assistant. Read it before making changes in this program directory.

Current version: 1.0.2.41

Version rule: start at 1.0.0.0. After each completed fix, increment the last segment by 1. The last segment can go up to 99. After 1.0.0.99, carry to 1.0.1.00. Keep `Core/ProductIdentity.cs` assembly, file, and informational versions synchronized with the maintenance log version.

Build rule: 除非用户明确要求，不要自行编译 x64；当前默认只进行 ARM64 构建与测试。

Maintenance log rule: every completed fix or confirmed issue record starts with one metadata line in this format:

`时间: yyyy-MM-dd HH:mm:ss zzz | 版本: x.x.x.x | 窗口: window name | 模块: module name`

From the second line onward, write the fix details or discovered issue. Leave one blank line after each record.

## Maintenance Log

时间: 2026-06-07 23:06:44 +09:00 | 版本: 1.0.0.0 | 窗口: Codex Radar | 模块: Model IQ 数据源
发现问题: https://codexradar.com/model-iq.json 已返回 HTTP 410 Gone。
当前 https://codexradar.com/current.json 正常返回 HTTP 200，Model IQ 数据已迁移到 model_iq 字段。
待修正: 移除独立 Model IQ 接口请求，改为从 current.json 解析 model_iq，并兼容 recent_days 基准数据，避免重复请求和错误日志污染。

时间: 2026-06-07 23:18:35 +09:00 | 版本: 1.0.0.0 | 窗口: Codex Radar | 模块: Model IQ 数据源
修正内容: 删除 CodexRadarForm 中已下线的 https://codexradar.com/model-iq.json 常量、独立刷新字段、timer 调用、独立 HTTP 请求函数和 3 小时 IQ 调度。
修正内容: current.json 解析成功时读取 model_iq 字段并填充 IQ、通过数、pass rate、Token 效率和时间效率。
修正内容: 效率基准兼容 current.json.model_iq.recent_days，同时保留旧 history 字段兼容逻辑。
修正内容: current.json 暂时缺少 model_iq 时保留上一份 IQ 快照，避免短暂字段缺失导致界面清空。
修正内容: 更新 CodexRadar 技术文档和总运行文档，移除旧的独立 Model IQ 请求周期说明。
验证结果: ARM64 测试构建和正式构建均通过；正式 exe 已重启；代码中已无 model-iq.json 请求路径；重启后 error.log 未继续写入 410 Gone。

时间: 2026-06-07 23:35:09 +09:00 | 版本: 1.0.0.0 | 窗口: 全局 | 模块: 版本与维护日志
修正内容: 定义当前程序版本为 1.0.0.0，并写入程序集版本、文件版本和信息版本。
修正内容: 将维护日志迁移为 Codex 默认会读取的 AGENTS.md，并在文件开头写入版本递增规则和维护记录格式。
维护规则: 后续每次完成修复后，先按版本规则递增版本，再同步更新 Core/ProductIdentity.cs 与本文件维护记录。

时间: 2026-06-08 02:38:50 +09:00 | 版本: 1.0.1.1 | 窗口: 设置窗口 | 模块: 现代化设置面板
修正内容: 将设置面板测试版覆盖到正式版，保留正式版 ProductIdentity 与运行入口。
修正内容: 重做设置窗口为统一深色现代界面，加入顶部标题与搜索框、左侧导航、右侧卡片式分组、固定底部操作区，并将窗口改为可调整大小。
修正内容: 增加设置页和左侧导航的滚轮滚动处理，包含对子控件的鼠标滚轮转发，避免内容较多的页面显示不全。
修正内容: 扩大设置项标签列宽并统一按钮、输入框、滑块、组合框、分组卡片和页面背景样式，使设置窗口和其他深色窗口风格一致。
验证结果: 测试版设置窗口截图验证显示完整，CodexRadar 页滚轮测试记录 ScrollTop 从 0 变为 336。
验证结果: 正式版 ARM64 构建通过并覆盖 DesktopCodexAssistant.exe；正式 exe SHA1 为 BCAC1EED6EE6A3D5F5F806B528001E31A0EE18F9；程序集版本为 1.0.1.1。
验证结果: 正式版 --test 基础采样正常返回，应用 error.log 未新增错误记录；正式源码中已无 test-settings-ui、Settings UI Test、测试版截图和设置诊断日志字符串。

时间: 2026-06-08 02:51:51 +09:00 | 版本: 1.0.1.2 | 窗口: 设置窗口 | 模块: 细节样式调整
修正内容: 将设置窗口下拉可选框内文字缩小 30%，控件行高保持不变，避免影响整体布局密度。
修正内容: 增高设置主页顶部标题区，并调整“性能小窗设置”标题垂直对齐和搜索框底部间距，改善标题上下空间不足的问题。
修正内容: 将底部左侧按钮文案改为“退出”和“强杀”，保留原有退出动作映射。
验证结果: ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe；正式 exe SHA1 为 D2A63A8620EFCC282038C0A9BD804EEA4A1CA471；程序集版本为 1.0.1.2。
验证结果: 正式版 --test 基础采样正常返回，正式程序已重新启动；应用 error.log 未新增错误记录。

时间: 2026-06-08 03:05:45 +09:00 | 版本: 1.0.2.0 | 窗口: 全局 | 模块: 产品重命名与仓库维护
修正内容: 将产品身份、主源码文件、构建脚本、安装脚本、卸载脚本、启动测试脚本、README、LICENSE 和技术文档统一迁移为 Desktop Codex Assistant。
修正内容: 标注当前分支为 UX3407N / UX3607O 专用版本，并在 README 中声明软件全由 Codex 创作。
修正内容: 普通启动和测试路径不再执行启动项迁移，避免改名后自动写入注册表；安装和卸载脚本仍只在用户主动运行时维护当前与旧版启动项。
修正内容: 将根目录中 UUID 命名和旧产品名历史 exe 合并归档到 Artifacts/LegacyExecutables，并按产品名、构建时间和 SHA1 短码重命名。
验证结果: ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe；正式 exe SHA1 为 1185C4A6FCF083B0F4C63A14BAA0922259978666；程序集版本为 1.0.2.0。
验证结果: 正式版 --test-logger 返回 PASS；正式版 --test 返回退出码 0；本次未运行安装/卸载脚本，未截图，未主动写入注册表。

时间: 2026-06-08 03:26:43 +09:00 | 版本: 1.0.2.1 | 窗口: 左下角操作面板 | 模块: 电源与设备限定按钮
修正内容: 电源按钮优先通过 SeelenUI CLI 触发 @seelen/power-menu，SeelenUI 未运行或触发失败时回退到 Windows Security / Ctrl+Alt+Delete 菜单。
修正内容: 增加 ASUS Zenbook 硬件识别，非 ASUS Zenbook 设备隐藏“解除 80% 充电限制 24 小时”和“恢复 80% 充电限制”两个按钮，并压缩操作面板宽度。
修正内容: 增加 SeelenUI 主进程状态探测；seelen-ui 未运行时“退出 SeelenUI”按钮显示为灰色禁用态，鼠标悬停不显示用途提示，也不接受点击。
验证结果: ARM64 临时测试构建通过；正式构建通过并覆盖 DesktopCodexAssistant.exe；正式 exe SHA1 为 CD4B66BF7085E00250B13867F0375A156070563A；程序集版本为 1.0.2.1。
验证结果: 正式版 --test 与 --test-logger 均返回退出码 0；正式程序已按原参数重新启动；应用 error.log 未新增错误记录。

时间: 2026-06-08 12:23:02 +09:00 | 版本: 1.0.2.2 | 窗口: 全局 | 模块: 分辨率比例适配与 x64 构建
修正内容: 设置文件升级到 Version=9，保存当前工作区基准，并在分辨率、任务栏工作区或屏幕恢复后按宽高比例换算性能面板、CodexRadar、功耗、网络、连接检测和操作面板的尺寸与位置。
修正内容: 显示变化、系统设置变化、息屏恢复和解锁恢复时先执行工作区比例适配，再重新定位和重绘所有子面板，保持当前屏占比。
修正内容: 设置窗口初始尺寸和最小尺寸改为按当前工作区自适应，小分辨率下保留滚动访问能力，避免固定 920x620 撑出屏幕。
修正内容: Build-Arm64.ps1 增加 -Platform arm64/x64 参数，并新增 Build-X64.ps1，生成 DesktopCodexAssistant-x64.exe。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 AE2FEAE77A07FE52800770F81096682FE94B977A；PE Machine=ARM64；程序集版本为 1.0.2.2。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 196154D99A0089B7CEB7DBB367036CD4E991EE84；PE Machine=x64；程序集版本为 1.0.2.2。
验证结果: ARM64 和 x64 的 --test-layout、--test-logger、--test 均返回退出码 0；ARM64 与 x64 正常启动-停止烟测均返回退出码 0；应用 error.log 未新增错误记录。

时间: 2026-06-08 13:06:22 +09:00 | 版本: 1.0.2.3 | 窗口: 全局 | 模块: 息屏/休眠恢复
定位结论: 息屏/休眠后无法恢复的高风险点是 layered window 复用的 native memory DC/HBITMAP 可能跨显示驱动或 DWM 恢复后失效，而原恢复路径只将托管 Bitmap 标记为需要重绘；另一个风险是 `--desktop-parent` 模式下旧 WorkerW 父窗口失效后没有先脱离旧父窗口再重新挂接。
修正内容: `NativeMethods.LayeredBitmapSurface` 增加 Reset，恢复时释放并重建 native DC/HBITMAP；主窗口、CodexRadar、功耗、网络、连接检测均在恢复时重建托管渲染缓存并重置 native surface，操作面板重建托管 Bitmap 并恢复失败日志状态。
修正内容: 屏幕关闭或系统挂起时主动释放主窗口和子窗口显示资源；显示恢复改为三轮延迟恢复，覆盖 DWM、显示驱动和 WorkerW 稍晚恢复的情况。
修正内容: `--desktop-parent` 恢复时先 `SetParent(..., IntPtr.Zero)` 脱离旧桌面宿主，恢复普通顶层窗口样式，再尝试挂接新的桌面宿主；后续恢复轮继续重试挂接。
修正内容: 新增 `--test-display-recovery`，使用真实 layered-window API 验证 native surface reset 前后均可更新窗口。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 44293426BF026A813ED6989783E94007CE546186；PE Machine=ARM64；程序集版本为 1.0.2.3。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 8987E69C1576BE64D082B80F4540D0DA43BCD97A；PE Machine=x64；程序集版本为 1.0.2.3。
验证结果: ARM64 与 x64 的 --test-display-recovery、--test-layout、--test-logger、--test 均返回退出码 0；ARM64 普通模式、ARM64 --desktop-parent 模式、x64 普通模式的恢复消息烟测均返回退出码 0，日志记录三轮恢复和 desktop-parent 脱离/重挂接；ARM64 suspend/resume 消息烟测返回退出码 0，日志记录 Display resources released. Reason=power suspend 与三轮恢复；应用 error.log 未新增错误记录。

时间: 2026-06-09 02:45:58 +09:00 | 版本: 1.0.2.4 | 窗口: 左下角操作面板 | 模块: 快捷入口按钮
修正内容: 将电源按钮悬停说明改为“打开电源菜单”。
修正内容: 在操作面板最右侧新增“打开任务管理器”和“打开 AI Studio”两个按钮，任务管理器使用性能窗口图标，AI Studio 使用芯片星光图标。
修正内容: 任务管理器按钮调用 taskmgr.exe；AI Studio 按钮打开 Click to Do 的 URI，并回退到 AppsFolder AUMID。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 A610C454D9079DB8D15C5AEF196C652830C52CD4；PE 目标为 ARM64；程序集版本为 1.0.2.4。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 23A52DABE994FED17233E6B6312A47F87CA9B22E；PE 目标为 x64；程序集版本为 1.0.2.4。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0；本次未进行截图验证。

时间: 2026-06-09 03:02:43 +09:00 | 版本: 1.0.2.5 | 窗口: 左下角操作面板 | 模块: SeelenUI 联动重启与按钮重排
修正内容: “重启本程序”按钮在重启本程序前检查 seelen-ui 主进程；仅当 SeelenUI 已运行时，才结束 SeelenUI/slu-service 并从原 seelen-ui.exe 路径重新启动，SeelenUI 未运行时不会额外启动。
修正内容: 删除独立“退出 SeelenUI”按钮；将“打开任务管理器”移动到原退出 SeelenUI 的位置。
修正内容: 原“打开任务管理器”位置改为全高 AI Studio 按钮，继续使用 Click to Do URI 与 AppsFolder AUMID 打开。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 CC00247D0FFC194CCB6972A24FDEC7A02EAE5DED；PE Machine=ARM64；程序集版本为 1.0.2.5。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 7E606C9FB12A4014ABFBC7A6D6638DC84E332A22；PE Machine=x64；程序集版本为 1.0.2.5。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0；临时测试 exe 已清理。

时间: 2026-06-09 03:14:05 +09:00 | 版本: 1.0.2.6 | 窗口: 左下角操作面板 | 模块: 快速设置按钮
修正内容: 将最右侧全高 AI Studio 按钮改回上半格按钮，保留 Click to Do URI 与 AppsFolder AUMID 打开逻辑。
修正内容: 在操作面板右下角新增“打开快速设置”按钮，调用现有 NativeMethods.OpenQuickSettings()，通过 Win+A 打开 Windows 原生快速设置窗口。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 7F6F684A3D7118A905808C7DDBB0E7561BBB13E4；PE Machine=ARM64；程序集版本为 1.0.2.6。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 B4EC37344F22636ADE5BA10D52291C4FFEE940B1；PE Machine=x64；程序集版本为 1.0.2.6。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0。
验证结果: 临时测试 exe 已清理；正式 ARM64 程序已重新启动；error.log 未新增本次错误记录。

时间: 2026-06-09 12:57:27 +09:00 | 版本: 1.0.2.7 | 窗口: Codex Radar | 模块: Model IQ 数据新鲜度
定位结论: “是否更新”模块没有丢失数据，current.json.model_iq 当前正常返回 2026-06-09 的记录；模块消失是因为绘制条件仍限制为仅在“低效”或“降智”时显示，当前 IQ 恢复正常后被条件隐藏。
修正内容: 将 Updated/Unupdated/Outdated 改为独立的 model_iq 新鲜度状态，只要 model_iq 提供记录日期就显示，不再依赖低效或降智状态。
修正内容: 删除已失效的低效/降智绘制门控 helper，更新 CodexRadar 技术文档中的数据新鲜度说明。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 74E2D7421F63E94EDB4E4787705F85DCFEC456FA；PE Machine=ARM64；程序集版本为 1.0.2.7。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 087A804C0139F63585CC887260A1997710E5C920；PE Machine=x64；程序集版本为 1.0.2.7。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0。
验证结果: 临时测试 exe 已清理；正式 ARM64 程序已重新启动；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录。

时间: 2026-06-09 13:36:00 +09:00 | 版本: 1.0.2.10 | 窗口: 左下角操作面板 | 模块: 实时字幕系统入口与透明度切换
修正内容: 将“打开实时字幕”改为系统入口启动，优先使用 shell:AppsFolder 中的 LiveCaptions.exe，再尝试 System32/路径解析，不再发送 Win+Ctrl+L，也不做快捷键回退。
修正内容: 在操作面板最右侧新增“切换到悬停透明度/恢复模块透明度”按钮，使用运行时 ForceHoverOpacityActive 状态让主窗口、Codex Radar、功耗温度、网络监控和连接检测统一切换到鼠标悬停时的透明度，再按一次恢复。
修正内容: ForceHoverOpacityActive 只通过 WidgetSettings.Clone() 在运行时传播，不写入 settings.ini；保存设置时会清除该临时状态。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 04A64DDA416E5E7F59B8CDF695CD655B1CF1CB04；PE Machine=ARM64；程序集版本为 1.0.2.10。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 C46F551AE6490BF31B794C29E1943F23A44E1516；PE Machine=x64；程序集版本为 1.0.2.10。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0。
验证结果: 临时测试 exe 已清理；正式 ARM64 程序已重新启动；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录。

时间: 2026-06-09 13:53:33 +09:00 | 版本: 1.0.2.11 | 窗口: 左下角操作面板 | 模块: 前台窗口 FPS 与按钮重排
修正内容: 操作面板小按钮改为 6 列两行：上排依次为 Windows 设置、强制隐藏、程序设置、重启程序、关闭电源保护、开启电源保护；下排依次为电源、强制刷新、任务管理器、快捷设置、即时字幕、AI Studio。
修正内容: 新增 ForegroundFpsReader，通过系统性能计数器探测 Xbox/Game Bar/Present/Frame/FPS 相关计数器并按当前前台窗口进程匹配读取 FPS；无法匹配时显示 FPS=-。
修正内容: FPS 只在两个电源保护按钮不显示时占用上排空位；设置页新增“强制显示FPS模式”，启用后隐藏电源保护按钮并显示 FPS。
修正内容: 强制隐藏模式激活时，强制隐藏按钮背景改为淡蓝色，左下角操作面板也随模块一起进入更高可见度的透明状态；即时字幕和 AI Studio 按钮背景改为淡黄色。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 B4E157C85285EDB644CD29ADB36800CE8BA8DE8B；PE Machine=ARM64；程序集版本为 1.0.2.11。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 8451DFF4A2234C08818276A5BA514762EBDE91F8；PE Machine=x64；程序集版本为 1.0.2.11。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0。
验证结果: 临时测试 exe 已清理；正式 ARM64 程序已重新启动；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录。

时间: 2026-06-09 13:22:55 +09:00 | 版本: 1.0.2.9 | 窗口: 左下角操作面板 | 模块: 实时字幕按钮
修正内容: 在操作面板右侧追加“打开实时字幕”按钮，保留前 5 列既有按钮顺序；按钮使用字幕气泡图标。
修正内容: 新增 NativeMethods.OpenLiveCaptions()，通过 Windows 官方实时字幕快捷键 Win+Ctrl+L 启动实时字幕。
修正内容: 新增第 6 列后，电池按钮可见时上方显示 FPS；电池按钮隐藏时 FPS 继续扩展到上排右侧空位。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 98AF6D1A997351E588904AC6CB07A7C96CF479E6；PE Machine=ARM64；程序集版本为 1.0.2.9。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 FBEBFFB034D58593B63F7A9C1DF7646DC78D8FE0；PE Machine=x64；程序集版本为 1.0.2.9。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0。
验证结果: 临时测试 exe 已清理；正式 ARM64 程序已重新启动；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录。

时间: 2026-06-09 13:11:36 +09:00 | 版本: 1.0.2.8 | 窗口: 左下角操作面板 | 模块: 按钮重排与 MyASUS 探测
修正内容: 将操作面板右侧小按钮固定为 5 列两行：上排依次为 Windows 设置、程序设置、程序重启、打开暂停电池保护、关闭暂停电池保护；下排依次为电源、数据刷新、任务管理器、快速设置、AI Studio。
修正内容: 程序启动时检测 MyASUS 安装状态；点击数据刷新按钮时重新检测 MyASUS，并继续执行原有全模块刷新。
修正内容: 电池保护按钮现在同时要求 ASUS Zenbook 设备和 MyASUS 可用；不满足时隐藏两个电池按钮，并在其上排空位显示当前操作面板帧率 `FPS=n`。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 0DC27BD3714D414F1C7B9526FDA36A88052399E0；PE Machine=ARM64；程序集版本为 1.0.2.8。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 2391ECEF48C7072DD931D163532114CBE872EAF0；PE Machine=x64；程序集版本为 1.0.2.8。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0。
验证结果: 临时测试 exe 已清理；正式 ARM64 程序已重新启动；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录。

时间: 2026-06-11 13:37:38 +09:00 | 版本: 1.0.2.17 | 窗口: 全局 | 模块: 防烧屏自动透明度
修正内容: 设置模型新增鼠标空闲自动增高透明度开关、1-300 秒空闲阈值，以及前台最大化窗口自动增高透明度开关；保存文件版本升级到 Version=12。
修正内容: 设置窗口运行页新增“防烧屏空闲”“空闲秒数”“最大化窗口”三项，并纳入设置绑定自测；空闲秒数在空闲触发关闭时禁用。
修正内容: 主窗口在共享 hover 轮询里检测全局鼠标移动/按键，超过配置秒数后自动激活高透明度；鼠标恢复活动后自动关闭该自动源。
修正内容: 主窗口检测前台窗口最大化或全屏时自动激活高透明度，前台窗口恢复窗口化后自动关闭；前台窗口属于当前程序或 SeelenUI 时不触发。
修正内容: 自动触发源与左下角操作面板的手动“切换到悬停透明度”状态合并，仍通过 ForceHoverOpacityActive 传播到主窗口、Codex Radar、功耗、网络和连接检测模块，但自动状态不写入 settings.ini。
验证结果: ARM64 临时产物和正式产物的 --test-settings-bindings、--test-layout、--test-display-recovery、--test-logger、--test 均返回退出码 0。
验证结果: ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe；SHA1 为 BC9F12311DDF41FCE4B5D82CAE326E297F2BE418；程序集版本为 1.0.2.17。
验证结果: 按当前构建规则本次未编译 x64；临时测试目录已清理；正式 ARM64 程序已重新启动；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录。

时间: 2026-06-09 14:19:16 +09:00 | 版本: 1.0.2.12 | 窗口: 设置窗口 | 模块: 默认设置与保存状态
修正内容: 将当前本机 settings.ini 中的主窗口、CodexRadar、功耗模块、网络监控、连接检测、操作按钮、透明度、显示模式、栏目顺序和性能模式固化为 WidgetSettings.CreateDefaults() 默认值。
修正内容: 设置窗口保存成功或失败时不再弹出需要确认的保存结果对话框，改为在底部状态区域显示 5 秒；失败信息继续写入 error.log。
修正内容: 性能模式下拉框新增“根据 Windows 电源模式自动切换”，保存为 WindowsPowerMode，运行时复用功耗模块读取到的 Windows 电源模式文本并映射为性能、均衡或省电。
修正内容: 主窗口新增 AC/DC、电量、电源方案和 EffectivePowerMode 变化监听；自动性能模式下收到系统电源变化会重新应用进程省电策略和各模块采样/渲染间隔。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 38E359591E97E5A4A88F60F03541739936E4D28A；程序集版本为 1.0.2.12。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 D1781F84353CB748C502FE977CDC57BDA00A0CAC；程序集版本为 1.0.2.12。
验证结果: ARM64 与 x64 的 --test、--test-layout、--test-logger 均返回退出码 0；临时测试 exe 已清理；正式 ARM64 程序已重新启动；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录。

时间: 2026-06-09 18:11:51 +09:00 | 版本: 1.0.2.13 | 窗口: Codex Radar | 模块: 网站状态小叉
修正内容: Rader/Codex/Reseter 三行服务状态在 Offline 状态下也绘制小叉，颜色使用灰色 GlyphMuted；Unavailable 继续显示黄叉，Unreachable 继续显示红叉。
修正内容: 更新 CodexRadar 技术文档，将 Offline 说明改为灰字和灰色小叉，用于表示该元素当前不能正常发起请求。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 6EE787E41927935B82DC6E924AE8D8D127651233；程序集版本为 1.0.2.13。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 00F5CE77BA556DC3DB15542B9BD2F59450387C0F；程序集版本为 1.0.2.13。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0；临时测试 exe 已清理；正式 ARM64 程序已重新启动。

时间: 2026-06-09 18:14:17 +09:00 | 版本: 1.0.2.14 | 窗口: 左下角操作面板 | 模块: 快捷键回退收敛
修正内容: 除快速设置和电源用户关机菜单外，移除系统入口失败后的键盘快捷键回退；显示桌面改为 Shell.Application.ToggleDesktop，开始菜单和开始右键菜单仅使用任务栏 UI Automation，输入切换器仅使用 ms-inputapp: URI，Windows 安全菜单仅使用 Shell.Application.WindowsSecurity。
修正内容: 左下角操作面板中开始菜单、开始右键菜单和电源菜单的系统入口失败时显示通知，不再静默发送快捷键；快速设置按钮悬停提示明确标注“使用快捷键 Win+A”，开始和电源按钮提示标注无快捷键回退。
修正内容: 电源用户关机菜单保留 Win+X 后 U 的既有实现，快速设置继续保留 Win+A。
验证结果: ARM64 正式构建通过；DesktopCodexAssistant.exe SHA1 为 18BDA3F58B7BC47AFC8369F788EB06C98A13F9F7；PE Machine=ARM64；程序集版本为 1.0.2.14。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 4BEF7F140AA46F5B3166BDA3D23F5AAF690F41C5；PE Machine=x64；程序集版本为 1.0.2.14。
验证结果: ARM64 与 x64 的 --test、--test-logger、--test-layout、--test-display-recovery 均返回退出码 0。
验证结果: 临时测试 exe 已清理；正式 ARM64 程序已重新启动；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录。

时间: 2026-06-09 18:18:03 +09:00 | 版本: 1.0.2.15 | 窗口: 设置窗口 | 模块: 控件绑定与运行时生效
定位结论: 连接检测页已经初始化、读取和保存 `ConnectionCheckIntervalSeconds`，但页面未显示该控件，`CleanIpConnectionReader` 也没有使用该间隔，属于设置链路存在但运行时不生效。
定位结论: 主窗口、CodexRadar、功耗、网络监控和连接检测的位置滑块使用 `Screen.Bounds` 建范围，而运行时定位按 `WorkingArea` 夹紧；在任务栏或系统保留区域附近会出现数值变化但窗口不移动的死区。
修正内容: 连接检测页新增“自动刷新秒”滑块，并让 `CleanIpConnectionReader` 按 `ConnectionCheckIntervalSeconds` 触发定时间隔刷新，手动刷新、网络变化、错误重试和首次刷新逻辑保持可用。
修正内容: 设置页所有位置滑块范围改为使用可用工作区，和各窗口实际定位的 `WorkingArea` 夹紧规则一致，避免不可达坐标造成预览无反应。
修正内容: 新增 `--test-settings-bindings`，在程序内部创建设置面板对象并覆盖所有可见设置的读回、工作区位置范围、连接检测间隔控件挂载和栏目顺序，不使用 PowerShell 反射，也不生成截图。
验证结果: ARM64 临时验证构建通过；_build\DesktopCodexAssistant-arm64-test.exe SHA1 为 36EAE84B46F9BE1E9FC7C6E30172329A8C6EEAA4；PE Machine=ARM64；程序集版本为 1.0.2.15；临时产物已清理。正式 ARM64 exe 正在运行，本次未覆盖正式 ARM64 文件。
验证结果: x64 正式构建通过；DesktopCodexAssistant-x64.exe SHA1 为 425B488B4A8ECCB06C43CC7C4F03F77131B42F17；PE Machine=x64；程序集版本为 1.0.2.15。
验证结果: ARM64 临时产物与 x64 正式产物的 --test-settings-bindings、--test-layout、--test-logger、--test-display-recovery、--test 均返回退出码 0；git diff --check 仅有既有 CRLF 提示；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录。

时间: 2026-06-11 13:24:29 +09:00 | 版本: 1.0.2.16 | 窗口: 左下角操作面板 | 模块: 按钮可用性与状态样式
修正内容: AI Studio 与实时字幕入口启动前新增无副作用可用性检测；不可用时按钮保留占位但进入灰色不可点击状态，并显示对应不可用提示。
修正内容: 电源保护入口不可用仍按既有规则由 FPS 面板替代，不再作为灰色电源按钮显示；强制显示 FPS 时，FPS 面板绘制稳定蓝色激活态，被动替代时保持普通样式。
修正内容: 悬停透明度按钮改为统一状态按钮激活样式，激活时保留淡蓝填充和内描边；灰态按钮图标同步压暗，避免只改变背景但仍像可点击。
修正内容: 刷新按钮会重新探测 MyASUS、AI Studio 与实时字幕可用性；维护规则新增“除非用户明确要求，不要自行编译 x64；当前默认只进行 ARM64 构建与测试”。
验证结果: ARM64 临时验证构建通过；_build\DesktopCodexAssistant-arm64-test.exe SHA1 为 94C8D97AF5617212BC291BBD496224AC702776F9；PE Machine=ARM64；程序集版本为 1.0.2.16。
验证结果: ARM64 临时产物的 --test-settings-bindings、--test-layout、--test-display-recovery、--test-logger、--test 均返回退出码 0；本次未编译 x64，未覆盖正式 ARM64 exe。
验证结果: git diff --check 仅有既有 LF/CRLF 提示；error.log 最后更新时间仍为 2026-06-07 23:19:42，未新增本次错误记录；临时产物已清理。

时间: 2026-06-11 16:25:33 +09:00 | 版本: 1.0.2.29 | 窗口: 左下角操作面板 | 模块: Seelen Dock 前台恢复
修正内容: 新增 Seelen Dock foreground pulse，不重启 SeelenUI；通过枚举 seelen-ui 的 Dock/Tauri 顶层窗口并执行 SetWindowPos(HWND_TOPMOST) 将 Dock 拉回 topmost 组前列。
修正内容: pulse 后重新对本程序主窗口、Codex Radar、功耗、网络、连接检测和左下角操作面板执行 topmost pulse，保持本程序小窗口优先于 Seelen Dock 的现有层级策略。
修正内容: 显示恢复、显示点亮、电源恢复和会话解锁的 display recovery 完成后自动 pulse 一次；由 settings change 触发的恢复不做 Seelen pulse。
修正内容: 新增半点/整点自动 pulse 定时器；每小时 00 分和 30 分触发，若前台窗口最大化或全屏则跳过本次。
修正内容: 设置页操作模块新增“Seelen Dock 自动拉前”开关；关闭后停止自动 display recovery 与整点/半点 pulse，左下角手动按钮仍可执行显式 pulse。
修正内容: 左下角操作面板“重启”按钮改为单击拉前 Seelen Dock，双击才执行原来的 SeelenUI 联动重启和本程序重启。
验证结果: 按用户要求本次未编译；仅做文本级 git diff --check 静态检查。

时间: 2026-06-11 16:28:43 +09:00 | 版本: 1.0.2.19 | 窗口: 全局 | 模块: OLED 防烧屏
修正内容: 新增 BurnInProtection 运行时偏移 helper，每 7 分钟为主窗口、Codex Radar、功耗温度、网络监控、连接检测和操作面板应用不同的 1-3 像素微位移；偏移只存在于内存，不写入 settings.ini。
修正内容: 各窗口复用既有低频 tick 检查微位移 slot，slot 未变化时不重复定位，不新增高频常驻定时器。
修正内容: 调低 DesignTokens 中静态文字、弱线条、Glyph 和通用 White() 的亮度上限，避免长期纯白固定元素；告警、成功、危险等动态状态色保持原有可见度。
验证结果: 按用户要求本次未进行 ARM64 或 x64 编译；未运行二进制自检，仅完成源码差异检查。

时间: 2026-06-11 16:33:26 +09:00 | 版本: 1.0.2.29 | 窗口: 网络监控 | 模块: 公网云服务方块
修正内容: 新增 CloudEndpointProbe，随 GFW 刷新并行检测 Cloudflare、AWS、Google、微软、阿里云和腾讯云；每轮执行 3 次采样，间隔 10 秒，取两次最接近的可接受响应延迟作为代表值。
修正内容: 公网标题下方新增 Cf/Aw/Go/Ms/Al/Tx 六个状态方块；离线时全部灰色，GFW 未通过时海外四项灰色，刷新中和延迟过高黄色，无法连接红色，状态异常橙色，阿里云和腾讯云正常态使用淡绿色。
修正内容: 公网文字左侧新增红色或橙色异常提示，红色优先；云服务状态变化会触发 GFW 详细日志，日志内记录三轮采样和最终判定。
修正内容: 更新网络监控技术文档，补充云服务目标、三次采样规则、颜色覆盖优先级和日志内容。
验证结果: 按用户要求本次未编译，等待其他模块修改完成后统一编译；当前仅完成源码级检查。

时间: 2026-06-11 19:03:04 +09:00 | 版本: 1.0.2.30 | 窗口: 网络监控 | 模块: 公网云服务方块布局
修正内容: 云服务目标新增 GitHub，显示为 `GI`，探测主机为 `github.com`，顺序插入在微软 `Ms` 与阿里云 `Al` 之间。
修正内容: 云服务方块从公网标题下方移到 `IF` 行前方，不再额外占用头部第二行；头部恢复为单行布局。
修正内容: 方块尺寸上限从 16 像素提高到 20 像素，文字占比提高；刷新中状态随网络窗口刷新 tick 在绿色/淡绿色和黄色之间切换。
修正内容: 更新网络监控技术文档，补充 GitHub 目标、7 站点采样和 `IF` 行前方布局说明。
验证结果: 按用户要求本次未编译，等待其他模块修改完成后统一编译；当前仅完成源码级检查。

时间: 2026-06-11 19:17:50 +09:00 | 版本: 1.0.2.31 | 窗口: 网络监控 | 模块: 公网云服务方块层级
修正内容: 将云服务方块改为覆盖层绘制，`IF` 行标签和值仍按原坐标绘制，方块在该行内容之后叠加，不再改变 `IF` 行布局。
修正内容: 更新网络监控技术文档，将云服务方块位置说明改为 `IF` 行上方覆盖层。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 E1125BD74E475D0F2D2140198034119FB6B5B7F2；正式程序已重新启动，PID=11272。

时间: 2026-06-11 19:23:04 +09:00 | 版本: 1.0.2.32 | 窗口: 网络监控 | 模块: 公网云服务方块位置
修正内容: 将 `IF` 行上方的云服务方块覆盖层移动到窗口右侧内边距，并保持方块组右对齐；`IF` 行布局继续保持原始坐标，不参与方块排版。
修正内容: 更新网络监控技术文档，补充云服务方块右侧右对齐说明。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 C3BCB8244CC124E1EEF27FC908401D7257B403E1；正式程序已重新启动，PID=17588。

时间: 2026-06-11 19:46:47 +09:00 | 版本: 1.0.2.33 | 窗口: 网络监控 | 模块: 云服务状态与测试
修正内容: GitHub 方块文字从 `GI` 改为 `Gi`；AWS 方块固定使用 `Aw`，并为云服务方块使用专用居中文字收缩逻辑，避免两字符标签被截断。
修正内容: 公网左侧异常提示改为英文服务名，映射为 Cloudflare、AWS、Google、Microsoft、Github、Ali、Tencent，并统一使用英文感叹号。
修正内容: 当多个云服务为红色或橙色时，公网左侧提示在 `注意!` 与服务名之间随刷新切换；服务名按方块从左到右轮换，`注意!` 颜色跟随下一次要显示的服务状态。
修正内容: 网络监控设置页新增“云服务测试”按钮，实时时点击生成非检测中的随机云服务状态并预览，随机状态下点击恢复实时；settings.ini 升级到 Version=13 并新增 `CloudEndpointTestSeed`。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，`--test-settings-bindings` 返回 PASS，SHA1 为 1796CDD4A15F83CC47FC2C064F8DC51CD53C69FB；正式程序已重新启动，PID=30284。

时间: 2026-06-11 19:57:32 +09:00 | 版本: 1.0.2.34 | 窗口: 网络监控 | 模块: 公网异常提示
修正内容: 公网文字区域改为固定右侧区域绘制，不再因 `注意!` 或服务名提示出现而缩窄；异常提示模块独立叠画在公网区域左侧。
修正内容: 阿里云英文异常提示改为 `Aliyun!`，并加宽提示模块以避免 Aliyun 被截断。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 EF41FA5C468A8A713674D5F7E4C14761E93398E4；正式程序已重新启动，PID=19980。

时间: 2026-06-11 20:03:36 +09:00 | 版本: 1.0.2.35 | 窗口: 网络监控 | 模块: 云服务提示位置
修正内容: 云服务异常提示不再以公网文字左侧为参照，改为绘制在网络状态文字右侧，公网 IP 区域继续固定在标题栏右侧。
修正内容: 云服务处于检测中时，状态文字右侧显示黄色 `云服务测试中`，优先于红色或橙色异常服务名提示。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 D88BADFED44D3EFE1C67AF9806D284864DB2606D；程序集版本为 1.0.2.35；正式程序已重新启动，PID=27620。

时间: 2026-06-12 02:41:10 +09:00 | 版本: 1.0.2.36 | 窗口: 网络监控 | 模块: 云服务检测
修正内容: 云服务检测改为公开状态 API 优先：Cloudflare/GitHub 使用 Statuspage JSON，Google 使用 Google Cloud Service Health 降级数据，AWS/阿里云/腾讯云和无凭证不可查的入口按严格 HTTP 状态码分类。
修正内容: Microsoft `Ms` 方块改为聚合 Microsoft 365、Azure Status RSS 和 Azure DevOps Status API 三个子项，并按最差结果显示；异常告警名称区分为 `Ms 365!`、`Ms Azure!`、`Ms Azure DevOps!`。
修正内容: HTTP 分类收紧为 `2xx/3xx` 正常，`401/403` 拒绝访问，`429` 访问限流，`451` 地区受限，`404/410` 入口异常，`5xx` 服务异常，DNS/TCP/TLS/超时类失败显示为无法连接。
修正内容: 网络状态文字右侧告警删除 `注意!`，改为按“服务名!”、“失败层级或原因!”顺序随刷新切换，并将云服务短原因纳入重绘和日志状态比较。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 临时产物的 `--test`、`--test-settings-bindings`、`--test-layout`、`--test-logger`、`--test-display-recovery` 均返回退出码 0；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 953100328A06FBD077A21EEEDF3784ECCC3A4F03；程序集版本为 1.0.2.36；正式程序已重新启动，PID=33936。

时间: 2026-06-12 12:06:41 +09:00 | 版本: 1.0.2.37 | 窗口: 网络监控 | 模块: 云服务告警文字
修正内容: 网络状态文字右侧的云服务告警改为使用和 `ONLINE` 相同的字体直接绘制，不再因告警文字较长而自动缩小字号。
修正内容: 告警布局改为让 `ONLINE` 只占真实文字宽度，云服务告警使用状态文字到公网文字之间的剩余空间，公网文字仍固定右侧对齐。
说明: `官方降级` 继续映射为橙色状态异常，表示官方 API 报告服务 degraded/impact 且本机仍可连接；红色仅保留给 DNS/TCP/TLS/超时等无法连接或官方重大故障。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 临时产物的 `--test`、`--test-layout`、`--test-logger`、`--test-settings-bindings`、`--test-display-recovery` 均返回退出码 0；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 5D95225ADECF6473372197914C45B355CD25111F；程序集版本为 1.0.2.37；正式程序已重新启动，PID=2512。

时间: 2026-06-12 12:39:09 +09:00 | 版本: 1.0.2.38 | 窗口: 网络监控 | 模块: Microsoft 云服务检测
修正内容: 删除 Microsoft 365 无凭证 HTTP 检测，避免 `status.cloud.microsoft/m365` 返回 `401` 时被误判为服务异常。
修正内容: `Ms` 方块改为仅聚合 Azure Status RSS 和 Azure DevOps Status API，并继续按最差子项显示；告警名称保留 `Ms Azure!` 与 `Ms Azure DevOps!`。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 临时产物的 `--test`、`--test-layout`、`--test-settings-bindings`、`--test-display-recovery` 均返回退出码 0；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 067EAB08B3B890E5F00D45416F440D8DBF08BAC9；程序集版本为 1.0.2.38；正式程序已重新启动，PID=10980。

时间: 2026-06-12 13:02:42 +09:00 | 版本: 1.0.2.39 | 窗口: 网络监控 | 模块: 云服务地区过滤
修正内容: 删除 Microsoft `Ms` 方块及 Azure/Azure DevOps 聚合探测，云服务方块缩减为 Cf/Aw/Go/Gi/Al/Tx 六项。
修正内容: Cloudflare 与 Google Cloud 官方状态源新增地区过滤，设置页“官方地区”可勾选日本、亚太、北美、欧洲，默认日本；地区设置变化会触发下一轮 GFW/云服务检测。
修正内容: `Go` 项显示名称调整为 Google Cloud，网络监控技术文档同步更新为 6 方块与地区过滤说明；settings.ini 升级到 Version=14 并新增 `CloudStatusRegionMask`。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 临时产物的 `--test`、`--test-settings-bindings`、`--test-layout`、`--test-display-recovery`、`--test-logger` 均返回退出码 0；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 49ACB37C263475370280A96085F46FA96E271A6F；程序集版本为 1.0.2.39；正式程序已重新启动，PID=22488。

时间: 2026-06-12 13:21:20 +09:00 | 版本: 1.0.2.40 | 窗口: 网络监控 | 模块: 云服务负载优化
修正内容: 新增 `CloudEndpointProbeReader`，云服务检测从 `GfwProbeReader` 中拆出独立单飞调度；GFW 疑似异常不再导致海外云服务跳过检测或强制置灰。
修正内容: 云服务刷新改为首轮单次采样，只有异常、无法连接或延迟过高的目标才追加 2 次确认；采样并发限制为 3，手动刷新加入 45 秒冷却。
修正内容: 云服务缓存按状态分层：官方 API 正常 30 分钟、普通 HTTPS 正常 15 分钟、异常/慢响应 2 分钟、无法连接 45 秒、未知 30 秒；Cloudflare、Google Cloud、GitHub 官方 API 支持 `ETag` / `If-Modified-Since` 和 304 正文复用。
修正内容: 地区设置变化只强制刷新 Cloudflare 与 Google Cloud，其他服务在 TTL 内继续复用缓存；设置页地区勾选不再受 GFW 开关禁用。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 临时产物的 `--test`、`--test-settings-bindings`、`--test-layout`、`--test-display-recovery`、`--test-logger` 均返回退出码 0；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 B5FF6F65307B158BAA2A7C9D7DECB4E153A7860D；程序集版本为 1.0.2.40；正式程序已重新启动，PID=34160。

时间: 2026-06-12 17:53:10 +09:00 | 版本: 1.0.2.41 | 窗口: 网络监控 | 模块: 当前网卡选择
修正内容: 设置页网络监控新增“网卡选择”，空值为自动选择，非空时按网卡 ID 或名称固定当前网络监控接口；settings.ini 升级到 Version=15 并新增 `NetworkMonitorAdapterId`。
修正内容: 网络监控窗口右下角新增黄色当前网卡名称覆盖层，与云服务方块同属内容覆盖层，不改变 `IF`、公网、GFW 或云服务方块既有坐标和大小。
修正内容: 手动选择的网卡即使未连接也会保留为当前接口显示，连通性则进入离线/未连接判定，避免被自动切换到其他可用虚拟网卡。
验证结果: `git diff --check` 仅有既有 LF/CRLF 提示；ARM64 临时产物和正式产物的 `--test`、`--test-settings-bindings`、`--test-layout`、`--test-display-recovery`、`--test-logger` 均返回退出码 0；ARM64 正式构建通过并覆盖 DesktopCodexAssistant.exe，SHA1 为 38FA703879CA037BB860E3B1A04B60B46F71F32E；程序集版本为 1.0.2.41；正式程序已重新启动，PID=35456。
