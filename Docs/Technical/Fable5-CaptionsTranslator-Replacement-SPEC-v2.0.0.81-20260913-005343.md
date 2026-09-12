# Replace LiveCaptionsTranslator with an in-process translation pipeline

适用版本：2.0.0.81（创建时快照，正文不再随后续版本更新——见 §进度 的独立追加记录）
一句话定位：把字幕板依赖的第三方 LiveCaptionsTranslator.exe + sanitize-proxy.js 换成本仓库自己的原文抓取 + GenieX 直连翻译，继续沿用 Windows 自带 Live Captions 做语音识别，也继续沿用现有的字幕渲染/看板/文章累积代码。

本文件是一份可执行的实现规格（`doc_type: implementation_spec`），供任何执行模型（Codex/Fable5/Dsv4 均可）在没有本次对话上下文的情况下接手继续实现。开始执行前先读 §进度，确认哪些步骤已完成、已验证，避免重做。

## 背景（为什么要换）

Windows 自带 Live Captions 的语音识别在这台机器（Snapdragon X NPU）上是微软专门优化过的，质量没问题；有问题的是翻译。`LiveCaptionsTranslator.exe`（不在本仓库、不可修改的第三方开源应用）过去解决了翻译问题，但本次调查发现它有几个改不了的硬伤：

- 上下文感知翻译硬编码上限 **10** 句对话（上游 `Caption.cs` 的 `Caption.MAX_CONTEXTS = 10`）——我们在它的 `setting.json` 里设的 `NumContexts: 64` 从来没真正生效超过 10。
- 它的"关闭思考"字段（`enable_thinking`、`reasoning.*` 等）对当前部署的 `Qwen3-VL-8B-Instruct` 模型是死代码——这个模型的 chat template 里根本没有思考逻辑。
- 一个手写的 Node.js `sanitize-proxy.js` 夹在它和 GenieX 之间，纯粹是为了修补*它自己*那套死板 C# JSON 模型的 bug（GenieX 返回的 `logprobs` 是对象但它的模型声明成 `string`；以及把 GenieX 真正认识的 `enable_think` 字段塞进请求）。

本仓库已经拥有这条管线除"抓字幕→调用大模型→拿回译文"之外的每一块：`TranslatorControlReader` 管理 GenieX/LiveCaptions.exe 进程生命周期，`CaptionOverlayForm`/`CaptionsBoardForm`/`CaptionTranscript` 已经在渲染翻译后的字幕。本次会话已经完整逆向出上游的抓字幕逻辑（`LiveCaptionsHandler.cs`）、断句节流逻辑（`Translator.SyncLoop`）、乱序结果保护（`TranslationTaskQueue`）。这份规格只替换中间那一层，两端（Windows ASR、现有渲染管线）都不动。

## 本规格锁定的决策（如需变更先跟用户确认）

- **只做 GenieX 一条通道**。不重建上游其余 8 个翻译引擎（DeepL/百度/Ollama/…）——这台机器实际只用本地 OpenAI 兼容的 GenieX 端点。
- **直连 GenieX 18181 端口**，完全绕过 `sanitize-proxy.js`。我们自己的响应解析走动态 `Dictionary<string,object>`（见下），`logprobs` 类型不匹配的问题不会出现在我们身上；`enable_think:false` 由我们自己发送。
- **一次性切换，不做永久开关**——不加 `LiveCaptionsTranslatorFallbackEnabled` 这类兼容旗标。下面的构建顺序会先在旧管线仍运行时把新管线跑通验证，验证通过后再一步性移除旧管线。不新增持久化（不建 SQLite 历史库）——继续沿用 `CaptionTranscript` 现有的"仅内存"不变量。
- **真正可配置的上下文窗口**（新增一个 `WidgetSettings` 值，而不是硬编码 10）在本次范围内，因为这是自己写请求构造器时顺手就能做到的。真正的**压缩/摘要**（把被挤出去的历史浓缩成摘要）明确**不在本次范围**——是更大的独立功能。

## 架构

### 1. `Core/NativeCaptionReader.cs`（新增）—— 自己抓字幕 ✅ 已实现并验证

替换读取 LiveCaptionsTranslator 转显示窗口的做法，直接读 Windows 原生 `LiveCaptions.exe`：
- 窗口发现复用 `NativeMethods.TryFindWindowByClassName("LiveCaptionsDesktopWindow")`（`LiveCaptionsWindowTidy.cs` 已经在用）。
- UI Automation 复用 `TranslatorCaptionReader.cs` 里已有的手法（`AutomationElement.FromHandle`、`FindFirst(Descendants, AutomationIdProperty)`、`.Current.Name`），目标是 **`AutomationId="CaptionsTextBlock"`**（只有一块文本，不是两块——这是未翻译的原文）。
- 移植了上游的文本清洗（`Acronym`/`AcronymWithWords`/`PunctuationSpace`/`CJPunctuationSpace` 正则、`ReplaceNewlines`）和 `SyncLoop` 的 idle/sync 节流状态机（idle 计数/sync 计数、`PUNC_EOS` 断句检测、过短句子回并逻辑）——完全按本次会话读到的上游 `Translator.cs` + `TextUtil.cs` 移植。
- 正则改用普通 `static readonly Regex(..., RegexOptions.Compiled)`，不是上游的 C# `[GeneratedRegex]` 源生成器写法——本项目是经典 .NET Framework 4.x 手工 csc.exe 编译，没有 SDK 风格的源生成器步骤。
- 进程生命周期继续用 `TranslatorControlReader.IsLiveCaptionsRunning`/`TryStartMonitoredService(LiveCaptions)`，不变。
- 新增诊断命令 `--diagnose-native-captions [--duration <seconds>]`（`DesktopCodexAssistant.cs`），独立于翻译验证抓字幕+断句是否正确。

### 2. `Core/GenieXTranslationClient.cs`（新增，未实现）—— 自己发翻译请求

- HTTP：`HttpWebRequest`（`WebRequest.Create(...)`）+ `BoundedHttpTextReader.Execute(request, maxBytes, deadlineMs, cancellationToken)`，从后台 `Task.Run` 里调用——这是 `Core/` 目录已经确立的惯例（模板见 `DeepSeekBalanceMonitor.cs:326-380`），不要引入新的 async `HttpClient` 路径。
- 请求体用 `Dictionary<string, object>` 搭建（跟 `TranslatorControlReader` 自己改写 JSON 的写法一致）：`model`、`messages`、`temperature`、`max_tokens`、`stream:false`、`enable_think:false`。
- 响应解析走 `BoundedHttpTextReader.CreateJsonSerializer(...).DeserializeObject(...)`，当 `Dictionary<string,object>`/`object[]` 走 key 取 `choices[0].message.content`——不用死板 POCO，所以不会有 `logprobs` 崩溃问题。
- 用一个正则（等价于 sanitize-proxy 的 `<\|[a-zA-Z0-9_./-]+\|>`）剥离泄漏的 chat-template 特殊 token。
- 移植 `TranslationTaskQueue` 的"最新赢"模式：每句新文本立刻起一个翻译任务；谁先跑完就取消所有比它旧、还在飞的任务。**不能省略这一步**——省略后网络一抖字幕就会诡异地跳回上一句。
- 自己持有一个 (原文, 译文) 环形缓冲区做上下文，容量由新设置项控制，不再硬编码成 10。

### 3. `Core/TranslatorCaptionReader.cs`（改内部实现，对外契约不变）

保持 `GetSnapshot()`、`TranslatorCaptionSnapshot`、`CaptionTranscript` 的定稿/闩住逻辑完全不变——`CaptionOverlayForm`、`CaptionsBoardForm.Article.cs`、`--render-captionoverlay` CLI 已经能正确消费这些。只换喂给它的数据源：从"读 LiveCaptionsTranslator 的 `OriginalCaption`/`TranslatedCaption` AutomationElement"换成"`NativeCaptionReader` + `GenieXTranslationClient`"。这是整个方案里收益最大的一步——下游不需要改一行代码。

### 4. 退役不再由本程序运行的东西

- **`Core/TranslatorControlReader.cs`**：删除 `TryStartTranslator`、`TryToggleTranslatorRunning`、`TryRestartTranslatorForSettingsChange`、`TryMutateSettingsJson`/`ReadSettingsFile`（setting.json）、`ReadHistory`（translation_history.db）、`MonitoredServiceKind.SanitizeProxy` + `TryStartSanitizeProxy`/`IsSanitizeProxyRunning`。保留 `IsLiveCaptionsRunning`/`TryStartMonitoredService(LiveCaptions)`、`IsGenieXRunning`/`TryStartMonitoredService(GenieX)`、`CaptionLanguage` HKCU 读写不变。`TryEnsureStackAlive` 精简到 2 个成员。
- **`Core/TranslatorOverlayController.cs`**：整个删除——LiveCaptionsTranslator 不再运行，没有按钮可按了。
- **`Core/LiveCaptionsWindowTidy.cs`**：去掉 `TryHideTranslatorMainWindow`/`TryFindTranslatorMainWindow`；保留 `TryHideNewCaptionWindow`，改成以"本程序自己的管线在运行"为门控条件。
- **`Core/CaptionsBoardForm.Layout.cs` / `.cs`**：`DrawServiceStatusRow`（现在 4 个 chip，`:170-183`）减到 2 个（`GenieX`、`实时字幕`）；`CaptionsHitAction.SanitizeProxyStart` 删除；`TranslatorStart`/`IsRunning` 含义改成"自己这条管线是否健康"而不是"外部 exe 是否存在"（UX 形状不变，含义变了）。
- **新设置**（上下文对话对数；可选温度覆盖）加进 `Settings/WidgetSettings.cs`，照搬本次会话核实过的 `CaptionOverlaySettledLines` 九步套路（常量 → 属性 → 构造函数复制默认值 → `CreateDefaults()` → `Clone()` → `Normalize()` → `SaveToPath` 那一行 → `ApplyValue` 的 case），加上 `Win11SettingsForm.cs` 的 UI 绑定（`NumericRanges`/`SettingTitles`/`SettingHints`/分组数组），让 `--test-settings-bindings` 的反射覆盖率检查不需要额外豁免就能过。

## 需要同步的文档（按 Docs/AGENTS.md）

- 新建 **`Docs/Captions-Translator-Architecture.md`**——本次会话已确认没有现成的 `*-Architecture.md` 覆盖这条管线（现在只有刷新节奏写在 `Component-Refresh-Rules.md` §8.6）。
- `Component-Refresh-Rules.md` §8.6——监控进程从 4 个精简到 2 个。
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`——把 `service.live_captions_translator.process`、`service.live_captions_translator.sanitize_proxy_process`、`file_format.live_captions_translator.setting_json`、`file_format.live_captions_translator.translation_history_db`、`internal_api.translator_overlay_controller` 标记为 `removed`；原地更新 `internal_api.translator_caption_reader`；为新增的 reader/client 补登记（含本次已新增的 `NativeCaptionReader`）。
- `Docs/Indexes/FEATURE_INDEX.jsonl`——更新字幕板对应功能行与新设置键。
- 版本号同步 + `Docs/Maintenance/CHANGELOG.jsonl` 追加记录，然后跑 `python Docs/validate_docs.py`。

## 构建顺序（每一步都要有可验证的产物）

1. `NativeCaptionReader` 单独验证——新增诊断开关，直接从 `LiveCaptions.exe` 读原始字幕打印到控制台。先把 UI Automation 正确性隔离验证掉，再碰翻译。**已完成，见 §进度。**
2. `GenieXTranslationClient` 单独验证——拿一句写死的测试句子打真实 GenieX 服务器。
3. 把两者接进 `TranslatorCaptionReader`；用现成的 `--render-captionoverlay current` 验证——**LiveCaptionsTranslator.exe 完全不运行**的情况下，端到端出真实字幕。
4. 到这一步才退役旧管线（见"退役"一节）；验证看板只剩 2 个 chip、旧的交互动作都不见了。
5. 设置项 + `Win11SettingsForm` 绑定；跑 `--test-settings-bindings`。
6. 文档同步；跑 `validate_docs.py`。
7. 真人多分钟实时语音会话：翻译质量/延迟不低于现在，网络抖动时字幕不会诡异跳回上一句。
8. 正式 ARM64 构建 + 备份 + 部署，按 AGENTS.md 默认规则执行。

## 验证方式

- `--render-captionoverlay current`：看新管线渲染出来的实际效果。
- 一个轻量诊断命令（扩展 `--diagnose-translator-stack` 或新增 `--diagnose-captions`）：不经 UI 跑一次"抓原文→翻译"的完整往返。
- `--test-settings-bindings`：新设置项的绑定自检。
- `python Docs/validate_docs.py`：文档改动的校验门。
- 真人实时语音测试——这是没法只靠单元测试验证的实时行为，必须现场盯着看。
- `Build-Arm64.ps1 -RequireTrackedSources`：部署前的正式构建。

## 进度（只追加，不改写上面的正文——上面是规格，这里是执行记录）

- **2026-09-13**：完成并验证步骤 1（`NativeCaptionReader.cs`、`NativeCaptionSnapshot.cs` 已创建；`DesktopCodexAssistant.cs` 新增 `--diagnose-native-captions` 诊断命令；`Build-Sources.json` 已登记两个新文件）。`Build-Arm64.ps1` 编译通过。用 `--diagnose-native-captions --duration 8` 对着真实运行中的 `LiveCaptions.exe` 实测，成功读到真实增长字幕并正确断句（含 sync-tick 提前 flush 的那一条分支），全程未启动 LiveCaptionsTranslator.exe。**尚未**：提交、部署、更新 Docs/索引/CHANGELOG（§需要同步的文档 与 §构建顺序 步骤 4-8 均未开始）。
- **并发编辑提醒**：执行到这里时，`git status` 显示 `Core/NetworkCheckHistoryLogger.cs`、`Performance/GfwProbeReader.cs`、`Performance/NetworkMonitorReader.cs`、`Performance/PdhModels.cs` 及若干 Docs 文件被另一个并发会话修改中（与本规格无关）。接手的执行模型在构建/部署前应重新跑 `git status` 确认这些文件的状态，必要时用 `git archive HEAD` + 只覆盖自己改动的文件的方式隔离构建，避免把别人未提交、未审查的改动一起编译进正式可执行文件。
