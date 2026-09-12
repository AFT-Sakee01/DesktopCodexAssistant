# Replace LiveCaptionsTranslator with an in-process translation pipeline

适用版本：2.0.0.81（创建时快照，正文不再随后续版本更新——见 §进度 的独立追加记录）
一句话定位：把字幕板依赖的第三方 LiveCaptionsTranslator.exe + sanitize-proxy.js 换成本仓库自己的原文抓取 + GenieX 直连翻译，继续沿用 Windows 自带 Live Captions 做语音识别，也继续沿用现有的字幕渲染/看板/文章累积代码。

本文件取代 `Fable5-CaptionsTranslator-Replacement-SPEC-v2.0.0.81-20260913-005343.md`（同一版本号，仅补充背景调查过程，架构/决策/构建顺序内容不变；旧文件保留，`Docs/Technical/INDEX.jsonl` 中旧行已标记 superseded）。补充原因：接手执行的模型不共享产生这份规格的对话上下文，下面的"前因后果"把当时逐步调查出的结论按发生顺序完整写出来，而不是只给结论——很多结论本身反直觉，只有看到调查过程才知道为什么能信、信到什么程度、以及哪些是本项目机器特有的（换一台机器/换一个模型可能就不成立）。

## 前因后果（背景调查全过程）

### 0. 起点

用户已经在这台机器上部署了 LiveCaptionsTranslator（第三方开源应用，`SakiRinn/LiveCaptions-Translator`），原因是 **Windows 11 自带的 Live Captions 翻译功能质量太差**——微软自己的实时字幕引擎（识别）在这台 Snapdragon X NPU 上是真的好，但它可以顺手做的翻译（把 `HKCU\Software\Microsoft\LiveCaptions\UI\CaptionLanguage` 设成 `zh-CN`）只是直译，跟专门的 LLM 翻译没法比。用户想要更好的翻译，于是搭了 LiveCaptionsTranslator + 本地大模型的组合，本仓库（DesktopCodexAssistant）的 `TranslatorControlReader`/`TranslatorCaptionReader` 负责监控这套外部栈并把结果渲染到自己的字幕看板/悬浮字幕上。

之后用户开始追问这套外部工具到底支持什么参数、能不能调得更好，一路问下去，发现了好几个绕不过去的硬伤（见下），最终决定：与其在一个改不了源码的第三方 exe 上打补丁，不如自己把"抓字幕→翻译"这一段接过来做。**本规格就是那个决定的落地方案。**

### 1. 部署拓扑（这台机器上实际在跑什么）

```
Windows LiveCaptions.exe（系统自带，System32，NPU 加速的语音识别）
        │  UI Automation 读取 AutomationId="CaptionsTextBlock"
        ▼
LiveCaptionsTranslator.exe（第三方，D:\E_Drive_Files\Codexproject\LiveCaptions-Translator\）
        │  HTTP POST /v1/chat/completions
        ▼
sanitize-proxy.js（本地 Node.js 脚本，同目录，监听 127.0.0.1:18182）
        │  转发并修补请求/响应
        ▼
geniex.exe serve（Qualcomm 官方 NPU 推理运行时，监听 127.0.0.1:18181）
        │  实际跑的模型
        ▼
qualcomm/Qwen3-VL-8B-Instruct:W4A16（本机唯一实际使用的模型）
```

本仓库的 `Core/TranslatorControlReader.cs` 负责监控/拉起上面除 Windows LiveCaptions 语音识别本体逻辑之外的四个外部进程（LiveCaptionsTranslator.exe、geniex.exe、node.exe 跑的 sanitize-proxy.js、LiveCaptions.exe 本身），`Core/TranslatorCaptionReader.cs` 通过 UI Automation 读 LiveCaptionsTranslator 主窗口里已经翻译好的文字（`AutomationId="OriginalCaption"`/`"TranslatedCaption"`），渲染进 `CaptionOverlayForm`/`CaptionsBoardForm`。这一整条链路和四个外部进程都**不在本仓库范围内、不可修改**（AGENTS.md 明确要求 `D:\E_Drive_Files\Codexproject\LiveCaptions-Translator\` 目录只读监控，不能改）——这也是"为什么要自己重写"而不是"去 upstream 提个 PR"的原因：那是一个通用工具，不会为了一台机器上的一个模型改变自己的架构，而且改了也没法部署到这台机器（第三方 exe，非本仓库产物）。

### 2. setting.json 字段调查：文档内 vs 文档外

用户第一次问的是"这个翻译软件支持什么参数，包括'关闭思考'这种官方文档没有的"。调查方法是三路对照：LiveCaptionsTranslator 的 README/Wiki（官方文档）、`setting.json` 的实际内容（运行时状态）、GitHub 源码（`SakiRinn/LiveCaptions-Translator` 仓库，`src/models/Setting.cs` 等）。

结论：
- README 只泛泛提到"高度可配置"，没有列全部字段。
- `setting.json` 里真正存在的字段比 README 多得多：`MaxIdleInterval`/`MaxSyncInterval`（识别节流）、`NumContexts`/`ContextAware`（上下文感知翻译）、`DisplaySentences`（悬浮窗历史句数）、`ConfigIndices`（同一引擎可以存多组配置来回切）、`WindowBounds`（窗口位置持久化）等。
- 挖到一个"看着能改、其实改不动"的坑：`Setting.cs` 里 `MaxIdleInterval` 是 `public int MaxIdleInterval => maxIdleInterval;`——只有 getter 没有 setter，`System.Text.Json` 反序列化时会跳过只读属性，所以哪怕 `setting.json` 文件里写了这一项、UI 上甚至有对应控件，**这个值实际上永远是硬编码的默认值 50，改了也不生效**。类似的隐藏行为是本次调查的主线索之一：官方看起来"可调"的东西，不一定真的可调。

### 3. "关闭思考"机制深挖（这是最关键的一段调查，直接决定了本规格的架构）

用户点名要问"关闭思考"，因为这台机器上跑的模型（Qwen 系列）有些版本自带"思考"（chain-of-thought，`<think>...</think>` 块）能力，思考会显著拖慢首字延迟——对实时字幕这种场景是致命的（读一句话花 15-35 秒思考完全不能用）。

**上游的做法**（读源码 `src/models/RequestData.cs` 得到）：upstream 完全没有给用户暴露"要不要思考"这个开关，而是在代码里**默认强制关闭**、且**同时尝试好几种不同厂商的关闭字段**，因为不同 LLM 服务商用不同的字段名表达"别思考"：
- Ollama → `think: false`
- 阿里云百炼/SiliconFlow → `enable_thinking: false`
- Anthropic/智谱 → `thinking: {type: "disabled"}`
- OpenRouter → `reasoning: {exclude: true, enabled: false}`
- OpenAI 系/SiliconFlow(推理模型) → `reasoning: {effort: "low"}`
- xAI(Grok) → `reasoning_effort: "low"`

对着一个"OpenAI 兼容"端点（本机 GenieX 走的就是这条），upstream 会按 `LLMRequestDataFactory` 里固定的顺序挨个尝试上面这些写法，服务端返回 400/422 就换下一种；如果服务端"来者不拒、不认识的字段直接忽略"（源码注释原话），最终会落到 `IntegratedLLMRequestData`——把上面全部字段一次性塞进同一个请求体，图省事，不管哪个字段真正被吃。

**GenieX 实际认哪个字段**：本仓库另外找到一份此前会话（同一台机器上更早的 AI 会话，从 `sanitize-proxy.js` 文件头部注释可以看出）已经调查过的结论——`sanitize-proxy.js` 的第一个作用就是"往每个请求里注入 `enable_think: false`"，注释原文写着"这是一个没写进文档的 GenieX 专属字段（靠 `GENIEX_LOG=debug` 输出反查到 Go 结构体字段 `EnableThink`，JSON tag `enable_think`）；标准的 OpenAI/vLLM 风格的 `enable_thinking`/`chat_template_kwargs` 字段会被 GenieX 的 serve 命令直接忽略；这个才是真正生效的那个"。也就是说，upstream 那一整套"多套写法轮询"里，**真正对 GenieX 有效的只有 `enable_think`（不带 -ing，且是最外层字段，不是 `extra_body` 里）**，其余全是无效的冗余尝试。

**这个模型上，"关闭思考"这件事本身可能是句空话**：进一步查了本机 GenieX 模型缓存（`%USERPROFILE%\.cache\geniex\models\qualcomm\Qwen3-VL-8B-Instruct\tokenizer_config.json` 的 `chat_template`，Jinja 模板），发现**这个模型的对话模板里根本没有任何 `enable_thinking`/`<think>` 相关的判断逻辑**。对比同一台机器上还装着的 `Qwen3-8B`（纯文本、非 VL 版），它的模板结尾明确写着：

```jinja
{%- if enable_thinking is defined and enable_thinking is false %}
    {{- '<think>\n\n</think>\n\n' }}
{%- endif %}
```

——这才是真正"关闭思考"生效的地方：检测到 `enable_thinking=false` 就预先塞一个空 `<think></think>`，逼模型跳过思考直接回答。**`Qwen3-VL-8B-Instruct` 的模板里完全没有这一段**，说明这个 Instruct 变体本来就不是"思考模型"。也就是说，无论是 upstream 发的那堆字段，还是 sanitize-proxy 注入的 `enable_think`，对**当前实际部署的模型**来说都是**无操作、被安静忽略的**——不是"关掉了"，而是"本来就没有思考可关"。

（背景细节：本机 `setting.json.bak-qwen3-8b` 这个备份文件的存在证实了这台机器之前确实用过会思考的 `Qwen3-8B`，后来才切到不会思考的 `Qwen3-VL-8B-Instruct`——"关闭思考"这套代码是为前一个模型准备的遗留逻辑。）

**给本规格的启示**：`GenieXTranslationClient`（见下面架构部分）在请求体里发 `enable_think: false` 是"以防万一/面向未来换模型时仍然有效"的正确姿势，但不要指望它对当前模型有任何可观测的效果——当前的低延迟表现本来就是因为模型本身不会思考，不是这个字段起的作用。如果将来换成一个真正的思考模型（比如换回 Qwen3-8B 或类似），这个字段就会开始真正生效，届时行为会变化，这不是 bug。

### 4. GenieX 真实能力面 vs 实际用到的

查了 Qualcomm 官方文档（`https://geniex.aihub.qualcomm.com/en/run/cli/local-server` 与 `qualcomm/GenieX` 仓库 README），GenieX 的 OpenAI 兼容 Chat Completions 接口实际支持：

- 标准字段：`model`、`messages`、`max_tokens`、`temperature`、`top_p`、`top_k`、`min_p`、`repetition_penalty`、`seed`、`stream`、`stop`
- 思考相关（走 `extra_body`）：`enable_think`（bool）、`reasoning_format`（`none`/`deepseek`/`deepseek-legacy`/`auto`）
- VLM 专属：`image_url`/`input_audio` 内容块（音频输入仅 llama.cpp 后端支持，qairt/NPU 后端不支持）

而 LiveCaptionsTranslator 现在实际发的请求体（`src/models/RequestData.cs` 的 `BaseLLMRequestData`）**只用了一小部分**：固定写死 `max_tokens=128`、`stream=false`、`keep_alive=600`，`temperature` 来自配置（本机是 0.3），`top_p`/`top_k`/`min_p`/`repetition_penalty`/`seed`/`stop` 完全没有暴露，缺省时会吃 GenieX 模型编译时内置的默认值（见下一节）。

**给本规格的启示**：`GenieXTranslationClient` 从零写请求体，不必受 upstream 那套"多引擎兼容"逻辑束缚，可以直接、干净地只发 GenieX 真正认识、真正需要的字段（`model`/`messages`/`temperature`/`max_tokens`/`stream:false`/`enable_think:false`），这也是"架构"一节里明确不做多引擎兼容的原因之一——那套复杂度是为了适配 upstream 自己也不确定后端是谁的场景，我们这里后端固定是 GenieX，不需要这层抽象。

### 5. `stream=false` 是干什么的

用户单独问过这个。简单说：`stream:true` 是 SSE 分块推送（ChatGPT 网页那种一个字一个字蹦出来的效果），`stream:false` 是等模型说完整句话，一次性返回一个完整 JSON。LiveCaptionsTranslator 写死 `stream=false` 不只是偏好，是**代码实现要求**——它收到响应后直接 `JsonSerializer.Deserialize<OpenAIConfig.Response>(responseString)`，只认得"响应体是一整个 JSON 对象"，完全没写处理 SSE 分块流的逻辑，如果改成 `stream:true` 会直接反序列化失败。

**给本规格的启示**：`GenieXTranslationClient` 同样应该用 `stream:false`——不是因为受这个限制约束（我们自己写解析逻辑，想支持流式也可以），而是因为翻译的输出本来就短（一句字幕，`max_tokens` 量级），流式带来的"边出字边显示"收益很小，换来的是要多维护一套 SSE 增量拼接逻辑，不值得。

### 6. 上下文窗口调查：`NumContexts: 64` 从未真正生效过

用户观察到"大概 96 句话后开始滚动"，问能不能对上下文做压缩而不是丢弃。调查过程：

1. 先查了 `Caption.cs`（upstream 源码）：`AwareContexts => GetPreviousContexts(Translator.Setting.NumContexts)`——看起来 `NumContexts`（本机设的 64）决定上下文条数。
2. 但再查 `Translator.cs` 里真正往队列里塞数据的地方：

```csharp
// Caption.cs
public const int MAX_CONTEXTS = 10;
public Queue<TranslationHistoryEntry> Contexts { get; } = new(MAX_CONTEXTS);

// Translator.cs —— 每次翻译完一句话之后
if (Caption?.Contexts.Count >= Caption.MAX_CONTEXTS)
    Caption.Contexts.Dequeue();     // 队满了就把最老的一条直接扔掉
Caption?.Contexts.Enqueue(lastLog);
```

真正存储上下文的队列容量被**硬编码死为 10**，`GetPreviousContexts(64)` 在一个最多装 10 条的队列上 `Take(64)`，多要的 54 条根本不存在。也就是说 `NumContexts: 64` 这个设置从第 11 句话开始就已经没有意义了，"滚动"（丢弃最老一条）从第 11 句就在发生，不是第 96 句——用户观察到的"96"这个数字大概率是按一个不成立的假设（`NumContexts=64` 真的生效）估算出来的，实际瓶颈是这个硬编码 10。

3. 另外查了 GenieX 侧的模型编译配置（`genie_config.json`）：这个模型编译进 NPU 二进制时的**实际上下文窗口只有 4096 token**（跟 Qwen3-VL 宣传的 262144 无关，是编译成 QNN HTP 二进制时定死的 KV cache 预算）。GenieX 有个 `--sliding-window` 参数可以在超出这个窗口时"驱逐最老 token 而不是报错"，但官方文档只在 `geniex infer`（交互式 CLI 会话）底下提到这个参数，**`geniex serve`（我们实际用的 HTTP 服务模式）的文档完全没提超限行为**——不确定 serve 模式支不支持这个参数、超限时是报错还是自动处理。
4. 但因为 upstream 每次翻译都是**无状态的单次 HTTP 请求**（每次重新拼一个 `messages` 数组：系统提示 + 最多 10 条历史 + 当前这句，不是一个持续增长的会话），单次请求的 token 量本来就被那个硬编码 10 条上限锁死了，理论上不会因为"说得越久"而滚雪球撞上 GenieX 那 4096 token 的墙。

**给本规格的启示**（对应"架构"第 2 条 `GenieXTranslationClient` 里"自己的环形缓冲区"）：只要自己写请求构造器，"让上下文条数变得真正可配置"这件事几乎是免费的（本来就要自己维护一个 (原文,译文) 列表塞进 `messages`），所以纳入本次范围；但"驱逐最老的" vs "压缩/摘要"是两件事——GenieX 和 upstream 能做到的最多是前者（不管是硬编码 10 还是未来配置成更大的数字，本质都是丢弃/驱逐），**真正的压缩/摘要在这条链路的任何一层都不存在**，如果要做是全新的、更大的功能，本规格明确排除在外。

### 7. 找替代品：结论是没有更好的同类

用户问过"LiveCaptionsTranslator 有没有替代品"。调查了同类开源项目（`bryanphandhy/LiveCaptions-Translator-MT` 等 fork，查证是无实质改动的个人练手 fork）和不依赖 Windows Live Captions、自己做语音识别+翻译的独立方案（`phuc-nt/my-translator`——本地离线模式只支持 Apple Silicon，Windows 上必须联网调云端 API；`Live Subtitles`、`SonicCaption`——闭源或浏览器插件形态，不适配"任意系统音频+本地 NPU"的诉求）。结论：**LiveCaptionsTranslator 在"劫持 Windows Live Captions 文字再翻译"这个细分赛道里已经是事实标准，没有做得更好的同类可以直接换**，这也是"为什么选择自己重写一部分而不是换个软件"的原因。

### 8. 要不要连语音识别（ASR）也自己做：结论是不要

用户问过"如果从抓音频开始都自己实现呢"。结论是**不建议**，原因两条：
1. **技术上**：Whisper 本质是批处理模型（一次吃最长 30 秒音频），做成"边说边出字、还能不断回改上一句"的流式效果需要自己搭一整套语音端点检测（VAD）+ 滑动窗口分段 + 重叠片段拼接去重，这是完全独立于"抓字幕+调翻译"的工程量级，Qualcomm 官方确实有编译好给 NPU 跑的 Whisper 系列模型（`qualcomm/Whisper-Base` 等，走同一套 GenieX 基础设施），但流式包装工作是我们从零开始，没有现成参考实现可抄。
2. **收益上**：Windows 自己这个 Live Captions 语音识别引擎在这台 Copilot+ / Snapdragon X 硬件上是微软专门优化过的、独立评价认为"a real engineering achievement"——用户最初的不满是**翻译**烂，不是**听写**烂。自己重做 ASR 大概率是拿一个没有经过流式工程打磨的 Whisper 替换掉一个已经被专门优化的商用流式模型，识别质量多半倒退，而翻译质量的提升跟换不换 ASR 完全无关。

**给本规格的启示**：Windows LiveCaptions.exe（语音识别本体）保持不动，只换它下游"翻译"这一段——这正是本规格"架构"部分限定的边界，任何执行者都不应该在这个范围之外去动语音识别部分。

### 9. 最终决定

综合以上调查：LiveCaptionsTranslator 改不了源码、有几处硬伤（上下文硬编码 10、思考开关对当前模型是空转、多了一层 Node.js 代理专门修补它自己的 bug）、没有更好的同类替代品、Windows 的语音识别不需要换——于是决定自己接管"抓原文→调用大模型→拿回译文"这一层，两端（Windows ASR、现有的字幕渲染/看板/文章累积代码）都不动。下面"架构"一节就是这个决定的具体拆解。

---

## 决策（本规格锁定，如需变更先跟用户确认）

- **只做 GenieX 一条通道**。不重建上游其余 8 个翻译引擎（DeepL/百度/Ollama/…）——这台机器实际只用本地 OpenAI 兼容的 GenieX 端点（见 §4）。
- **直连 GenieX 18181 端口**，完全绕过 `sanitize-proxy.js`。我们自己的响应解析走动态 `Dictionary<string,object>`（见下），`logprobs` 类型不匹配的问题不会出现在我们身上（那是 upstream 自己用了死板 C# POCO 才踩的坑，见下面 `GenieXTranslationClient` 一节）；`enable_think:false` 由我们自己发送（见 §3，虽然对当前模型是空操作，但换模型后仍然正确）。
- **一次性切换，不做永久开关**——不加 `LiveCaptionsTranslatorFallbackEnabled` 这类兼容旗标。下面的构建顺序会先在旧管线仍运行时把新管线跑通验证，验证通过后再一步性移除旧管线。不新增持久化（不建 SQLite 历史库）——继续沿用 `CaptionTranscript` 现有的"仅内存"不变量。
- **真正可配置的上下文窗口**（新增一个 `WidgetSettings` 值，而不是硬编码 10，见 §6）在本次范围内，因为这是自己写请求构造器时顺手就能做到的。真正的**压缩/摘要**（把被挤出去的历史浓缩成摘要）明确**不在本次范围**——是更大的独立功能（见 §6 结尾）。

## 架构

### 1. `Core/NativeCaptionReader.cs`（新增）—— 自己抓字幕 ✅ 已实现并验证

替换读取 LiveCaptionsTranslator 转显示窗口的做法，直接读 Windows 原生 `LiveCaptions.exe`（对应 §1 拓扑图里的第一段箭头）：
- 窗口发现复用 `NativeMethods.TryFindWindowByClassName("LiveCaptionsDesktopWindow")`（`LiveCaptionsWindowTidy.cs` 已经在用）。
- UI Automation 复用 `TranslatorCaptionReader.cs` 里已有的手法（`AutomationElement.FromHandle`、`FindFirst(Descendants, AutomationIdProperty)`、`.Current.Name`），目标是 **`AutomationId="CaptionsTextBlock"`**（只有一块文本，不是两块——这是未翻译的原文；upstream 自己读同一个 AutomationId，见 `LiveCaptionsHandler.cs`）。
- 移植了上游的文本清洗（`Acronym`/`AcronymWithWords`/`PunctuationSpace`/`CJPunctuationSpace` 正则、`ReplaceNewlines`）和 `SyncLoop` 的 idle/sync 节流状态机（idle 计数/sync 计数、`PUNC_EOS` 断句检测、过短句子回并逻辑）——完全按上游 `Translator.cs` + `TextUtil.cs` 移植，阈值（`MaxIdleTicks=50`、`MaxSyncTicks=3`、`ShortSentenceByteThreshold=10`）是 upstream 调好的经验值，接手后大概率还需要对着真实语音重新微调。
- 正则改用普通 `static readonly Regex(..., RegexOptions.Compiled)`，不是上游的 C# `[GeneratedRegex]` 源生成器写法——本项目是经典 .NET Framework 4.x 手工 csc.exe 编译，没有 SDK 风格的源生成器步骤。
- 进程生命周期继续用 `TranslatorControlReader.IsLiveCaptionsRunning`/`TryStartMonitoredService(LiveCaptions)`，不变。
- 新增诊断命令 `--diagnose-native-captions [--duration <seconds>]`（`DesktopCodexAssistant.cs`），独立于翻译验证抓字幕+断句是否正确。

### 2. `Core/GenieXTranslationClient.cs`（新增，未实现）—— 自己发翻译请求

- HTTP：`HttpWebRequest`（`WebRequest.Create(...)`）+ `BoundedHttpTextReader.Execute(request, maxBytes, deadlineMs, cancellationToken)`，从后台 `Task.Run` 里调用——这是 `Core/` 目录已经确立的惯例（模板见 `DeepSeekBalanceMonitor.cs:326-380`），不要引入新的 async `HttpClient` 路径。
- 请求体用 `Dictionary<string, object>` 搭建（跟 `TranslatorControlReader` 自己改写 JSON 的写法一致）：`model`、`messages`、`temperature`、`max_tokens`、`stream:false`、`enable_think:false`（后者按 §3 的结论，对当前模型是空操作，但保留发送以兼容未来换模型）。
- 响应解析走 `BoundedHttpTextReader.CreateJsonSerializer(...).DeserializeObject(...)`，当 `Dictionary<string,object>`/`object[]` 走 key 取 `choices[0].message.content`——不用死板 POCO，所以不会有 §4/`sanitize-proxy.js` 那种 `logprobs` 崩溃问题（upstream 的 `OpenAIConfig.Response.Choice.logprobs` 声明成 C# `string`，但 GenieX 实际返回一个对象 `{"content":null,"refusal":null}`，`System.Text.Json` 会直接抛异常；我们用动态字典天然不会有这个问题）。
- 用一个正则（等价于 sanitize-proxy 的 `<\|[a-zA-Z0-9_./-]+\|>`，见 §3/`sanitize-proxy.js` 的第二个作用）剥离泄漏的 chat-template 特殊 token（比如 `<|im_start|>`）。
- 移植 `TranslationTaskQueue` 的"最新赢"模式：每句新文本立刻起一个翻译任务；谁先跑完就取消所有比它旧、还在飞的任务。**不能省略这一步**——省略后网络一抖字幕就会诡异地跳回上一句（这是 upstream 用来对付"一句话在增长过程中被反复重新翻译、结果乱序返回"这个问题的关键设计，不是可选的润色）。
- 自己持有一个 (原文, 译文) 环形缓冲区做上下文，容量由新设置项控制，不再硬编码成 10（对应 §6 的调查结论）。

### 3. `Core/TranslatorCaptionReader.cs`（改内部实现，对外契约不变）

保持 `GetSnapshot()`、`TranslatorCaptionSnapshot`、`CaptionTranscript` 的定稿/闩住逻辑完全不变——`CaptionOverlayForm`、`CaptionsBoardForm.Article.cs`、`--render-captionoverlay` CLI 已经能正确消费这些。只换喂给它的数据源：从"读 LiveCaptionsTranslator 的 `OriginalCaption`/`TranslatedCaption` AutomationElement"换成"`NativeCaptionReader` + `GenieXTranslationClient`"。这是整个方案里收益最大的一步——下游不需要改一行代码。

### 4. 退役不再由本程序运行的东西

对应 §1 拓扑图：LiveCaptionsTranslator.exe 和 sanitize-proxy.js 这两段整体退役，GenieX 和 Windows LiveCaptions.exe 保留（只是换了个调用方）。

- **`Core/TranslatorControlReader.cs`**：删除 `TryStartTranslator`、`TryToggleTranslatorRunning`、`TryRestartTranslatorForSettingsChange`、`TryMutateSettingsJson`/`ReadSettingsFile`（setting.json，对应 §2 调查的那些字段——它们的意义随 LiveCaptionsTranslator 一起退役）、`ReadHistory`（translation_history.db）、`MonitoredServiceKind.SanitizeProxy` + `TryStartSanitizeProxy`/`IsSanitizeProxyRunning`。保留 `IsLiveCaptionsRunning`/`TryStartMonitoredService(LiveCaptions)`、`IsGenieXRunning`/`TryStartMonitoredService(GenieX)`、`CaptionLanguage` HKCU 读写（这是 Windows 原生翻译开关，跟本次改动无关，继续保留给用户当"零依赖降级选项"）不变。`TryEnsureStackAlive` 精简到 2 个成员。
- **`Core/TranslatorOverlayController.cs`**：整个删除——LiveCaptionsTranslator 不再运行，没有按钮可按了。
- **`Core/LiveCaptionsWindowTidy.cs`**：去掉 `TryHideTranslatorMainWindow`/`TryFindTranslatorMainWindow`；保留 `TryHideNewCaptionWindow`，改成以"本程序自己的管线在运行"为门控条件。
- **`Core/CaptionsBoardForm.Layout.cs` / `.cs`**：`DrawServiceStatusRow`（现在 4 个 chip，`:170-183`）减到 2 个（`GenieX`、`实时字幕`）；`CaptionsHitAction.SanitizeProxyStart` 删除；`TranslatorStart`/`IsRunning` 含义改成"自己这条管线是否健康"而不是"外部 exe 是否存在"（UX 形状不变，含义变了）。
- **新设置**（上下文对话对数；可选温度覆盖）加进 `Settings/WidgetSettings.cs`，照搬本次会话核实过的 `CaptionOverlaySettledLines` 九步套路（常量 → 属性 → 构造函数复制默认值 → `CreateDefaults()` → `Clone()` → `Normalize()` → `SaveToPath` 那一行 → `ApplyValue` 的 case），加上 `Win11SettingsForm.cs` 的 UI 绑定（`NumericRanges`/`SettingTitles`/`SettingHints`/分组数组），让 `--test-settings-bindings` 的反射覆盖率检查不需要额外豁免就能过。

## 需要同步的文档（按 Docs/AGENTS.md）

- 新建 **`Docs/Captions-Translator-Architecture.md`**——本次会话已确认没有现成的 `*-Architecture.md` 覆盖这条管线（现在只有刷新节奏写在 `Component-Refresh-Rules.md` §8.6）。建议把 §1 的拓扑图和 §3/§6 的关键调查结论（当前模型无思考逻辑、上下文硬编码 10、GenieX 真实端口）作为这份新架构文档的背景小节，避免以后又要重新调查一遍。
- `Component-Refresh-Rules.md` §8.6——监控进程从 4 个精简到 2 个。
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`——把 `service.live_captions_translator.process`、`service.live_captions_translator.sanitize_proxy_process`、`file_format.live_captions_translator.setting_json`、`file_format.live_captions_translator.translation_history_db`、`internal_api.translator_overlay_controller` 标记为 `removed`；原地更新 `internal_api.translator_caption_reader`；为新增的 reader/client 补登记（含本次已新增的 `NativeCaptionReader`）。
- `Docs/Indexes/FEATURE_INDEX.jsonl`——更新字幕板对应功能行与新设置键。
- 版本号同步 + `Docs/Maintenance/CHANGELOG.jsonl` 追加记录，然后跑 `python Docs/validate_docs.py`。

## 构建顺序（每一步都要有可验证的产物）

1. `NativeCaptionReader` 单独验证——新增诊断开关，直接从 `LiveCaptions.exe` 读原始字幕打印到控制台。先把 UI Automation 正确性隔离验证掉，再碰翻译。**已完成，见 §进度。**
2. `GenieXTranslationClient` 单独验证——拿一句写死的测试句子打真实 GenieX 服务器（直连 18181，不经过 sanitize-proxy 的 18182）。
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
