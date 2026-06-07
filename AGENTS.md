# Codex Project Notes

This file is the primary Codex-readable maintenance log for Codex Developer Assistant Window on WOA. Read it before making changes in this program directory.

Current version: 1.0.0.0

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

