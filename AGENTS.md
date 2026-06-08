# Codex Project Notes

This file is the primary Codex-readable maintenance log for Desktop Codex Assistant. Read it before making changes in this program directory.

Current version: 1.0.2.3

Version rule: start at 1.0.0.0. After each completed fix, increment the last segment by 1. The last segment can go up to 99. After 1.0.0.99, carry to 1.0.1.00. Keep `Core/ProductIdentity.cs` assembly, file, and informational versions synchronized with the maintenance log version.

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
