# 组件刷新规则

Last reviewed version: `1.0.2.80`

本文集中记录 Desktop Codex Assistant 各组件的刷新、轮询、手动刷新、网络事件、单飞任务和暂停恢复规则。修改定时器、刷新 token、网络事件、后台任务节流、测试模式或显示恢复逻辑时，应同步更新本文。

## 1. 维护边界

需要同步更新本文的变更：

- `WidgetSettings.Get*Interval*` 返回值、性能模式语义或默认刷新间隔变化。
- 任一窗口 `Timer`、`FileSystemWatcher`、`NetworkChange`、全局热键、显示恢复、手动刷新或测试模式行为变化。
- 任一 reader 的 `requestRunning`、缓存 TTL、冷却、状态滞后确认、generation 或取消策略变化。
- 隐藏、息屏、会话锁定、系统挂起、显示恢复和分辨率切换期间刷新策略变化。
- 操作面板刷新按钮、设置页手动刷新按钮或 `ForceRefresh` 覆盖范围变化。

不需要同步更新本文的变更：

- 纯视觉颜色、字体、文案、局部坐标变化，且不影响刷新频率或触发条件。
- 不改变外部行为的内部重命名。

## 2. 全局时间基准

主要时间策略集中在 `Settings/WidgetSettings.cs`。

| 策略 | 性能 | 均衡 | 省电 | 说明 |
| --- | ---: | ---: | ---: | --- |
| 主性能采样 | 500 ms | 1000 ms | 2500 ms | 主窗口 PDH 快照调度。 |
| 普通面板调度 | 500 ms | 1000 ms | 3000 ms | 子面板检查上限；无显示变化时不重绘。 |
| GPU/NPU 昂贵采样 | 1000 ms | 2000 ms | 5000 ms | 避免高频枚举大量 GPU Engine 计数器。 |
| 悬停动画 | 16 ms | 33 ms | 100 ms | 只在透明度或按压动画未完成时使用。 |
| 静止交互轮询 | 30 ms | 100 ms | 250 ms | 鼠标位置、自动穿透和防遮挡轮询。 |
| 本地网络信息 | 2 s | 5 s | 网络事件驱动 | 省电模式依赖 `NetworkChange`。 |
| 公网 IP | 5 min | 10 min | 15 min | 仅真实网络为 `Online` 时执行。 |

连通性检测：

| 网络状态 | 性能 | 均衡 | 省电 |
| --- | ---: | ---: | ---: |
| `Online` | 10 s | 30 s | 60 s |
| `NeedsValidation` | 5 s | 10 s | 30 s |
| `Offline` | 3 s | 5 s | 10 s |
| `AdapterMissing` | 不轮询 | 不轮询 | 不轮询 |
| `Unknown` | 立即 | 立即 | 立即 |

DNS 检测：

| DNS 最差状态 | 性能 | 均衡 | 省电 |
| --- | ---: | ---: | ---: |
| `Unknown` | 15 s | 30 s | 60 s |
| 异常、劫持或不可用 | 30 s | 60 s | 120 s |
| 全正常 | 5 min | 10 min | 15 min |

## 3. 主窗口与全局协调

源码：`Core/WidgetForm.cs`、`Performance/PdhSampler.cs`

| 项目 | 规则 |
| --- | --- |
| 主控制 tick | 使用主性能采样间隔；每次检查停止事件、设置热加载、全屏可见性、PDH 采样和绘制。 |
| 隐藏状态 | 控制循环保留。省电模式隐藏时跳过昂贵 PDH 采样；性能/均衡仍按当前策略保持必要状态。 |
| 昂贵硬件采样 | GPU/NPU 等按 1/2/5 秒独立节流，不跟随主 CPU/磁盘快照高频刷新。 |
| 设置热加载 | `settings.ini` 由 `FileSystemWatcher` 和主 tick 修改时间检查共同覆盖。 |
| 显示恢复 | 首次延迟 350 ms，最多 3 次；后续重试间隔 1500 ms。恢复会重建 layered-window 资源、重定位并强制刷新。 |
| SeelenUI 拉前 | 设置开启时按本地整点和半点计划；最大化或全屏前台场景按现有逻辑跳过。 |
| Ctrl+D 恢复 | 全局 Ctrl+D 后延迟 2000 ms 执行本程序和 SeelenUI 拉前；设置可关闭。 |
| 休眠唤醒重启 | `PBT_APMRESUMEAUTOMATIC`、`PBT_APMRESUMESUSPEND` 或 `PBT_APMRESUMECRITICAL` 后先完成 3 轮显示恢复，再按设置重启 SeelenUI 和本程序；设置可关闭，30 s 内重复恢复事件只处理一次。 |
| 防烧屏位移 | 每 7 min 生成新 slot；主窗口和子窗口按各自 salt 微位移。 |
| 操作面板刷新 | `ForceRefreshAllModules()` 触发主采样、磁盘用量刷新、Codex、功耗、网络和 CleanIP 刷新。 |
| 诊断日志 | 主采样诊断最多每 15 min 写一次；`TimingStats12h` 耗时摘要也最多每 15 min 写一次，具体样本只保存在内存滚动窗口中。 |

## 4. Codex Radar

源码：`Core/CodexRadarForm.cs`、`Core/CodexRadarModelCatalog.cs`

| 项目 | 规则 |
| --- | --- |
| UI 调度 | 使用普通面板调度 500/1000/3000 ms，并贴近秒边界；网站请求完成后可立即绘制。 |
| 随机整窗测试 | 测试开启时暂停真实网站、Claude、额度和连接流程轮询；手动 token 立即重建，自动刷新最多 1 s 一次。 |
| 额度进程检查 | Codex 进程检查性能/均衡/省电为 3/5/10 s。 |
| 活跃额度刷新 | Codex 正在运行时 10/15/30 s 刷新本地额度。 |
| 非活跃额度刷新 | Codex 不运行时 10/20/60 min 刷新；reset 到期或进程状态变化可立即刷新。 |
| 会话文件 | `%USERPROFILE%\.codex\sessions` watcher 只置失效标记，真实读取仍走额度刷新路径。 |
| CodexRadar 网站 | 启动、恢复、模型切换和手动刷新仍触发一次错峰请求；常规自动刷新在北京时间每小时整点执行一次，网站未出新批次不再加密轮询；失败、超时或不可用 10 min 重试；RSS 重置提醒跟随 current.json 成功响应读取，不建立独立轮询器。 |
| 模型切换 | 优先加载对应模型缓存，并安排约 1 s 后请求；模型目录由成功 `model_iq` 响应更新。 |
| Claude 状态 | 正常 15 min；非正常或失败 2 min 重试；单飞。 |
| 五阶段连接 | 正常性能/均衡/省电为 3/5/10 min；离线或任一阶段异常 1 min 重试；单飞。 |
| 网络事件 | `NetworkChange` 只标记服务网络失效，并请求服务健康和连接流程刷新。 |
| 挂起/锁屏 | 显示器关闭、会话锁定或系统挂起时停止 Codex 轮询；恢复后额度立即到期，远程请求错峰启动。 |

## 5. 功耗与温度

源码：`Core/PowerThermalForm.cs`

| 项目 | 性能 | 均衡 | 省电 |
| --- | ---: | ---: | ---: |
| 功耗 | 1 s | 2 s | 5 s |
| 温度低于 65 C | 2 s | 5 s | 10 s |
| 65 C 至 69.9 C | 1.5 s | 3 s | 5 s |
| 70 C 至 89.9 C | 1 s | 2 s | 3 s |
| 90 C 及以上 | 1 s | 1 s | 1 s |

规则：

- 功耗和温度有独立 deadline，但由同一个后台采样任务合并满足。
- 采样运行中再次到期时只合并为一个待处理请求，不堆积任务。
- 严重温度采样优先于省电策略。
- 显示器关闭、会话锁定或系统挂起时停止采样；恢复后清空缓存并立即采样。

## 6. 网络监控

源码：`Core/NetworkMonitorForm.cs`、`Performance/NetworkMonitorReader.cs`、`Performance/GfwProbeReader.cs`、`Performance/CloudEndpointProbeReader.cs`

| 项目 | 规则 |
| --- | --- |
| 网络窗口 UI tick | 使用普通面板调度 500/1000/3000 ms；只在显示字段变化、尺寸变化或动画需要时重绘。 |
| 本地网卡 | 首次、手动刷新、网卡选择变化、网络事件或 2 s/5 s 到期刷新；省电模式仅事件驱动。 |
| 连通性 | 根据实际状态使用全局连通性表；`AdapterMissing` 不周期请求。 |
| 公网 IP | 仅 `Online` 时按 5/10/15 min；失败先尝试 `api64.ipify.org`，再回退 `api.ipify.org`。 |
| DNS | DNS 地址签名变化立即测；否则按 DNS 最差状态自适应周期；单轮最多 2 个 DNS 并发。 |
| 网络事件 | 只置失效标记、增加 generation、清空旧公网 IP/连通性 deadline，并请求 GFW/云服务刷新。 |
| 过期结果 | 公网 IP、DNS 和连通性任务提交前必须验证 generation 和 `InterfaceId`，不匹配则丢弃并设为可重试。 |
| 手动刷新 | 网络窗口 `ForceRefresh()` 重置本地、公网、连通性、DNS、GFW 和云服务 deadline。 |

GFW：

- 仅真实网络为 `Online` 时启动。
- 设置范围 15-240 min，默认 30 min。
- 手动刷新由 `GfwProbeManualRefreshToken` 触发。
- 同一时间最多一个探测任务。
- 详细日志在手动、首次、状态变化或每 6 h 写一次。

云服务：

- 与 GFW 结论解耦，但复用 GFW 间隔和手动 token。
- 手动刷新有 45 s 冷却；地区设置变化强制刷新相关官方状态源。
- 正常官方 API 缓存 30 min，普通 HTTPS 正常缓存 15 min，异常/慢响应缓存 2 min，无法连接缓存 45 s，未知缓存 30 s。
- 状态变化需 30 s 滞后确认，避免网络抖动造成频繁日志和 UI 变色。
- 同一时间最多一个云服务探测任务；新强制刷新会取消旧请求。

## 7. CleanIP 连接检测

源码：`Core/ConnectionCheckForm.cs`、`Performance/CleanIpConnectionReader.cs`

| 项目 | 规则 |
| --- | --- |
| UI tick | 使用普通面板调度 500/1000/3000 ms；快照无显示变化时不重绘。 |
| 设置间隔 | `ConnectionCheckIntervalSeconds` 范围 15-600 s；当前默认设置值为 600 s，代码 fallback 为 60 s。 |
| 首次和联网 | 首次启动或从断网变为联网时立即检测。 |
| 每小时计划 | 每小时一次，随机偏移正负 5 min 并包含秒，避免整点集中请求。 |
| 错误重试 | 失败状态下在每 10 min 时间槽重试，同一时间槽只试一次。 |
| 手动刷新 | 设置页 token 触发，操作面板强制刷新走 `RequestRefresh()`。 |
| 网络事件 | 只使网络状态缓存失效；真实网络判断和请求在 reader 调度路径执行。 |
| 测试模式 | 测试快照稳定缓存；只有测试模式或手动 token 变化时重建。 |
| 单飞 | `requestRunning` 防止 CleanIP 请求并发。 |

## 8. 操作面板

源码：`Core/OperationForm.cs`、`Core/ForegroundFpsReader.cs`

| 项目 | 规则 |
| --- | --- |
| 动画定时器 | 只在按压或悬停进度未到目标时运行；间隔使用全局悬停动画 16/33/100 ms。 |
| 全屏/挂起 | 全屏隐藏和显示挂起时停止动画与 FPS 定时器。 |
| 刷新按钮 | 刷新 MyASUS、系统按钮可用性，并调用主窗口 `ForceRefreshAllModules()`。 |
| SeelenUI 电源菜单 | 后台单飞执行 `slu.exe` 并最多等待 1500 ms；UI 线程不等待外部进程。 |
| 单击/双击重启按钮 | 单击行为用 `SystemInformation.DoubleClickTime` 延迟确认，避免和双击重启冲突。 |
| FPS 回退 | 仅当 FPS 面板应显示时运行；性能/均衡/省电间隔 1/2/5 s；值未变化时不重绘。 |
| FPS counter 发现 | 首次或候选缺失时发现；前台进程变化后的重新发现冷却 30 s；完整发现间隔 60 s。 |
| 防烧屏维护 | 位置位移检查复用主窗口共享维护 tick，不建立额外高频定时器。 |

## 9. 设置窗口

源码：`Settings/SettingsForm.cs`

| 项目 | 规则 |
| --- | --- |
| 实时预览 | 设置变更后 75 ms debounce 应用预览，避免每个控件事件立即写入运行窗口。 |
| 页脚状态 | 保存成功或失败提示显示 5 s 后自动隐藏。 |
| 手动刷新 token | GFW、CleanIP 和 Codex Radar 随机测试通过 token 传回 `WidgetSettings`，由对应窗口/reader 识别变化。 |
| 保存 | 点击保存写入 `settings.ini`，主窗口 watcher 和修改时间检查负责外部热加载。 |
| 取消 | 恢复打开设置前的 baseline，不应触发额外持久化写入。 |

## 10. 修改检查清单

修改刷新规则后至少检查：

1. `Docs/Component-Refresh-Rules.md` 是否需要同步。
2. `Docs/INTERFACE_INDEX.jsonl` 中对应接口、配置、命令或资源是否需要更新。
3. `Docs/Performance-And-Window-Runtime.md`、`Docs/CodexRadar-Architecture.md` 或 `Docs/NetworkMonitor-Architecture.md` 是否有重复表格需要同步。
4. 是否仍满足“同类网络请求单飞、过期结果不可覆盖新状态、隐藏/挂起时停止不必要绘制”的约束。
5. 是否需要运行 `--test-settings-bindings`、`--test-layout`、`--test-display-recovery`、`--test-operation-panel` 或网络/窗口截图验证。
