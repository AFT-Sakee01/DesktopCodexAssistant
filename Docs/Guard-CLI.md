# GUARD CLI 使用说明

适用版本：2.0.0.39

本文是 GUARD 命令行入口、退出码、JSON 字段和外部代理调用方式的唯一说明；GUARD 状态机与 UI 架构见 `Docs/GuardBoard-Architecture.md`。

## 查看帮助

以下帮助命令不连接常驻实例，也不会初始化或迁移程序数据：

```powershell
DesktopCodexAssistant.exe --help
DesktopCodexAssistant.exe --guard help
```

全局帮助也接受 `help`、`-h`、`/?`；GUARD 帮助也接受 `--guard --help`、`--guard -h`、`--guard /?`。

## 前提与命令

除帮助以外，命令要求 Desktop Codex Assistant 常驻实例正在同一 Windows 用户会话中运行。CLI 本身不持有 Windows 电源请求，而是通过当前用户专用命名管道把固定 allow-list 请求交给常驻 UI 线程执行。

| 命令 | 作用 |
|---|---|
| `--guard status` | 查询当前状态，不修改设置 |
| `--guard sleep on` | 开启防睡眠，并同步 SystemRequired 与 ExecutionRequired |
| `--guard sleep off` | 关闭防睡眠，不停止亮屏计时 |
| `--guard display start` | 使用已保存的小时预设启动亮屏计时 |
| `--guard display start N` | 将预设设为 `N` 小时并启动亮屏计时；`N` 为 1–24 的整数 |
| `--guard display stop` | 停止亮屏计时，不改变防睡眠 |
| `--guard display hours N` | 只修改小时预设；`N` 为 1–24 的整数 |

亮屏与防睡眠彼此独立。需要两种保护时，调用方必须分别执行 `sleep on` 与 `display start`。

## 输出与退出码

成功响应写入 stdout，错误响应写入 stderr；协议响应均为 UTF-8 单行 JSON。退出码如下：

| 退出码 | 含义 |
|---:|---|
| `0` | 命令成功或帮助已显示 |
| `2` | 参数错误、常驻实例不可用、无权访问或管道失败 |
| `3` | 常驻实例收到请求，但拒绝执行或执行失败 |

成功响应示例：

```json
{"schema_version":1,"product":"DesktopCodexAssistant","product_version":"2.0.0.39","ok":true,"changed":false,"action":"status","state":{"generated_at_utc":"2026-09-10T15:30:00.0000000Z","sleep_enabled":false,"sleep_since_utc":null,"sleep_power_request_ready":true,"display_active":false,"display_hours":2,"display_until_utc":null,"display_remaining_seconds":0,"display_power_request_ready":true,"system_power_request_active":false,"execution_power_request_active":false,"display_power_request_active":false,"on_ac_power":true,"last_action_detail":null}}
```

根字段：

| 字段 | 类型 | 含义 |
|---|---|---|
| `schema_version` | integer | 当前协议为 `1` |
| `product` / `product_version` | string | 产品机器名与版本 |
| `ok` | boolean | 主实例是否接受并成功处理请求 |
| `changed` | boolean | 本次请求是否改变持久状态；幂等命令可为 `false` |
| `action` | string | 实际执行的 allow-list 动作 |
| `state` | object | 成功后的完整 GUARD 状态 |
| `error.code` / `error.message` | string | 失败代码与说明，仅失败响应出现 |

`state` 字段：

| 字段 | 含义 |
|---|---|
| `generated_at_utc` | 快照生成时间，ISO 8601 UTC |
| `sleep_enabled` / `sleep_since_utc` | 防睡眠开关及开始时间；未开启时开始时间为 `null` |
| `display_active` / `display_hours` | 亮屏是否活动及当前保存的整小时预设 |
| `display_until_utc` / `display_remaining_seconds` | 活动截止时间与剩余秒数；未活动时截止时间为 `null`、剩余为 `0` |
| `system_power_request_active` | Windows 是否实际接受 SystemRequired |
| `execution_power_request_active` | Windows 是否实际接受 ExecutionRequired |
| `display_power_request_active` | Windows 是否实际接受 DisplayRequired |
| `sleep_power_request_ready` | 防睡眠关闭时为 `true`；开启时仅当 SystemRequired 与 ExecutionRequired 均已接受才为 `true` |
| `display_power_request_ready` | 亮屏关闭时为 `true`；开启时仅当 DisplayRequired 已接受才为 `true` |
| `on_ac_power` | `true` 为交流电，`false` 为电池，无法判断时为 `null` |
| `last_action_detail` | 最近动作细节；无可用细节时为 `null` |

## 代理调用示例

PowerShell：

```powershell
$raw = & .\DesktopCodexAssistant.exe --guard status
if ($LASTEXITCODE -ne 0) { throw "GUARD status failed: exit $LASTEXITCODE" }
$state = ($raw | ConvertFrom-Json).state
if (-not $state.sleep_power_request_ready) { throw "Windows 防睡眠请求尚未就绪" }
```

Python：

```python
import json
import subprocess

result = subprocess.run(
    ["DesktopCodexAssistant.exe", "--guard", "status"],
    capture_output=True,
    text=True,
    encoding="utf-8",
    check=False,
)
if result.returncode != 0:
    raise RuntimeError(result.stderr.strip())
state = json.loads(result.stdout)["state"]
```

自动化在修改状态后应再次查询 `status`，并根据对应的 `*_power_request_ready` 判断 Windows 请求是否真实就绪；不要仅凭 `sleep_enabled` 或 `display_active` 推断 OS 已接受请求。字段为 `null` 时应保留未知语义，不要强制转换为成功、`0` 或 `false`。

## 安全与兼容边界

- 命名管道由当前用户 SID 派生命名，并只授权同一用户；不接受凭据输入，也不需要提权。
- 请求只允许本文列出的动作，小时数严格限制为 1–24 的整数。
- 单次连接最长等待 3 秒；常驻实例未运行时以退出码 `2` 失败关闭。
- readiness 表示 Windows API 已接受请求，但企业电源策略、管理员策略或设备待机规则仍可能拥有更高优先级。
- schema 1 的字段名是稳定集成契约；调用方应忽略不认识的新增字段，并对缺失或 `null` 值保持保守处理。
