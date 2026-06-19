# Goal 实施说明：操作面板交互与性能优化

## 1. 元数据

| 字段 | 值 |
| --- | --- |
| Goal | 按指定 Spec 完成左下角操作面板隐藏命中、SeelenUI 异步调用和空闲性能优化，构建并重启 ARM64 正式程序 |
| Goal thread | `019e90d7-103a-7101-a810-d24e4b82fcd9` |
| 状态 | `complete` |
| 产品版本 | `1.0.2.64` |
| 目标架构 | `ARM64` |
| 开始时间 | `2026-06-16T03:20:12+09:00` |
| 完成验证时间 | `2026-06-16T03:37:20+09:00` |
| 时区 | `Asia/Tokyo (+09:00)` |

## 2. Spec 基线

- Spec：`Docs/Technical/OperationPanel-Interaction-And-Performance-SPEC-v1.0.2.64-20260616-030809.md`
- SHA-256：`D0744BB6FD3011E362D2F14221D53BBF30B0B6A1534D437EAF99DA9EC7FC273A`
- 基线版本：`1.0.2.63`
- 目标版本：`1.0.2.64`

执行期间未修改输入 Spec。

## 3. 需求映射

| Spec 需求 | 实现 |
| --- | --- |
| 隐藏模式图标区域不可穿透 | `OperationForm` 在隐藏反色后应用缓存的按钮 tile 命中遮罩，只把按钮内最终 Alpha=0 的像素改为预乘合法的 `BGRA=(0,0,0,1)`；按钮间隙、圆角外部和隐藏按钮保持透明。 |
| SeelenUI 电源菜单不得阻塞 UI | UI 入口改为后台 `Task.Run` 单飞任务；`Process.Start`、运行状态检查和 `WaitForExit(1500)` 均在工作线程，UI 线程只清除忙碌状态、回退 Windows 安全菜单、通知和重绘。 |
| 动画稳定后停止 | 定时器只在悬停进度与目标值存在差异或按压动画未结束时运行，稳定悬停不再持续唤醒。 |
| 删除 SeelenUI 进程轮询 | 删除 2.5 秒定时器、常驻状态字段及刷新方法；只在显式操作时实时检测。 |
| 共享低频维护 | `WidgetForm` 现有协调 tick 调用 `OperationForm.ProcessSharedMaintenanceTick()`，只在防烧屏 slot 变化时重新定位。 |
| 全屏隐藏和显示恢复 | 隐藏或显示挂起时停止动画与 FPS 调度；恢复时重置分层资源、恢复位置并按可见性重新调度。 |
| 分层窗口资源复用 | `OperationForm` 持有窗口级 `NativeMethods.LayeredBitmapSurface`，显示恢复时 `Reset()`，关闭时 `Dispose()`，并通过内容有效标志减少原生位图刷新。 |
| 字体和布局缓存 | 接入窗口级 `UiFontCache`；按钮矩形由绘制、命中和命中遮罩共享，尺寸或可见项变化时统一失效。 |
| FPS 回退优化 | 使用后台单飞采样；性能/均衡/省电间隔分别为 1000/2000/5000 ms；隐藏或面板不可见时停止；数值未变化不重绘；reader 优先复用候选，读取失败且满足冷却条件后才重新发现。 |
| 可重复回归验证 | 新增 `--test-operation-panel`，覆盖命中 Alpha、动画目标、单飞、三档 FPS 间隔和 SeelenUI 结果映射。 |

## 4. 架构与线程边界

```mermaid
flowchart LR
    A["鼠标点击"] --> B["UI 标记单飞忙碌"]
    B --> C["Task.Run 外部进程操作"]
    C --> D["运行状态和 slu.exe 路径检测"]
    D --> E["后台 WaitForExit(1500)"]
    E --> F["不可变结果"]
    F --> G["BeginInvoke 回 UI"]
    G --> H["清除忙碌、回退、通知、重绘"]
```

后台任务不访问控件、`Graphics`、`Bitmap` 或窗口句柄。窗体关闭后的迟到结果通过 `formClosing`、`IsDisposed` 和句柄检查丢弃。超时不终止 `slu.exe`。

隐藏模式渲染顺序固定为：

1. 绘制正常内容。
2. 应用隐藏反色和灰白透明化。
3. 对可见按钮 tile 应用最小非零 Alpha 命中遮罩。
4. 通过缓存的 `LayeredBitmapSurface` 提交。

## 5. 关键模块和接口

- `Core/OperationForm.cs`
  - 命中遮罩、异步 SeelenUI 命令、动画停机、FPS 单飞、缓存和自测。
- `Core/ForegroundFpsReader.cs`
  - 最近成功候选优先和失败后的受控重新发现。
- `Core/WidgetForm.cs`
  - 将操作面板防烧屏维护并入已有主协调 tick。
- `DesktopCodexAssistant.cs`
  - 新增 `--test-operation-panel`。
- `Core/ProductIdentity.cs`
  - 产品版本同步为 `1.0.2.64`。

复用接口索引项：

- `command.application.cli`
- `command.seelen.cli`
- `service.windows.layered_window`
- `internal_api.ui_font_cache`
- `internal_api.burn_in_protection`
- `internal_api.window_runtime_contract`

## 6. 数据、配置和兼容性

- 未新增或修改注册表。
- 未新增持久化配置键、缓存格式或网络接口。
- 未修改 Dock、Launchpad、顶部栏或 Direct2D 项目。
- 未改变按钮布局、名称和既有主要功能。
- 仍保留 SeelenUI 命令 1500 ms 兼容超时和 Windows 安全菜单回退语义。
- 本次只构建 ARM64，未构建 x64。

## 7. 日志和错误处理

- 外部命令异常被规范化为失败结果，后台任务不会把异常传播到 UI 消息泵。
- 单飞状态在所有完成路径清除，避免按钮永久忙碌。
- 分层窗口提交继续使用现有失败去重日志。
- 定时器 tick、稳定悬停和无变化 FPS 不写高频日志。
- 正式实例启动和三轮性能采样后，`error.log` 长度仍为 `38515` 字节，最后修改时间仍为 `2026-06-14T11:20:48+09:00`，本次没有新增错误。

## 8. 验证证据

### 8.1 ARM64 临时构建

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 `
  -OutputPath .\_build\DesktopCodexAssistant-arm64-operation-final-test.exe `
  -Platform arm64
```

结果：

- 文件版本和产品版本：`1.0.2.64`
- PE Machine：`0xAA64`
- SHA-256：`90F4B4D89F130D900DC7F8C33BFE2B8CE2B3B03293774FCEB0B6017E1474B002`

### 8.2 自测

以下命令均返回退出码 `0`：

- `--test-operation-panel`
- `--test-settings-bindings`
- `--test-layout`
- `--test-display-recovery`
- `--test-logger`
- `--test`

### 8.3 静态检查

- `git diff --check` 通过，仅有工作区既有 LF/CRLF 转换警告。
- `CHANGELOG.jsonl` 和 `Docs/INTERFACE_INDEX.jsonl` 在写入前均为 61 行、61 个唯一 ID、零 JSON 解析错误。
- `SeelenStatusRefreshIntervalMs`、`seelenStatusTimer`、`RefreshSeelenUiStatus` 和 `SetSeelenUiRunning` 均不存在。
- `OperationForm` 未新增 `WS_EX_TRANSPARENT`。

### 8.4 正式构建和重启

旧正式实例 PID `15092` 已按要求强制结束。正式构建命令：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 `
  -OutputPath .\DesktopCodexAssistant.exe `
  -Platform arm64
```

正式产物：

- 文件版本和产品版本：`1.0.2.64`
- PE Machine：`0xAA64`
- SHA-256：`BA60537C2C2FB29D8ED91FBF065F4212E5FF48BC87BC36C2213F8130493C0B03`
- 文件长度：`586240` 字节

自动重启后只有一个正式实例：

- PID：`52120`
- `Responding=True`
- 启动参数：无附加参数

### 8.5 资源稳定性

正式实例热身后执行三轮 60 秒采样：

| 轮次 | 单核等效 CPU | 工作集变化 | Private 变化 | 线程变化 | 句柄变化 | GDI | USER |
| --- | ---: | ---: | ---: | ---: | ---: | --- | --- |
| 1 | 2.232% | -5.12 MiB | -5.66 MiB | 0 | -11 | 76 -> 77 | 72 -> 74 |
| 2 | 2.597% | -2.43 MiB | -2.37 MiB | +2 | +5 | 77 -> 77 | 71 -> 72 |
| 3 | 2.388% | -6.27 MiB | -10.09 MiB | -3 | -15 | 77 -> 77 | 72 -> 71 |

三轮均全程响应。GDI 在后两轮稳定，USER 未持续增长，工作集、Private、线程和句柄没有单调增长。

## 9. Spec 偏离和限制

- 没有自动执行真实 SeelenUI 电源菜单或 Windows 安全菜单，以免验证过程打断当前桌面会话；线程边界、结果映射、超时语义和单飞由自测与静态检查覆盖。
- 没有使用物理鼠标逐个点击隐藏模式按钮。命中修复通过像素级 Alpha 自测验证按钮内透明像素、已有非零像素和遮罩外透明区域，实际 Windows 分层窗口命中仍建议在日常使用中继续观察。
- 性能采样记录整个程序，不是只记录 `OperationForm`；网络和传感器后台任务会影响 CPU，因此结果用于确认无明显回退和资源无持续增长，不作为操作面板独占开销。

## 10. 交付结论

Spec 的代码、文档、版本、自测、ARM64 正式构建、强制停止旧实例、自动重启和运行稳定性要求均已完成。正式程序当前以版本 `1.0.2.64`、PID `52120` 单实例运行。
