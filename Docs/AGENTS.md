# Docs 维护规则（Documentation AGENTS.md）

本文件是**全项目文档维护的唯一权威规则**，约束所有在本仓库工作的 AI 代理与人。根目录 `AGENTS.md` 管项目与代码；本文件管 `Docs/`、`README.md` 与三套 JSONL 索引。两者冲突时以根目录 `AGENTS.md` 为准。

生效版本：1.0.4.17 起。本文件本身遵循"活文档"规则（§4）：只描述现行规则，不记录历史；历史入 CHANGELOG。

---

## 1. 铁律（先读这七条，其余都是展开）

1. **单一事实源**：每个主题只有一个 owner 文档（见 §2 地图）。其他文档需要提及时只写一句话加"见 `<owner 文档>`"，禁止复制表格或段落。发现重复内容时，保留 owner、删除副本。
2. **索引先行**：改代码前按关键字搜索 `FEATURE_INDEX.jsonl` 与 `INTERFACE_INDEX.jsonl`，只读命中行；不要为找一个入口通读大文件。新增 API/服务/命令/事件/持久化文件/渲染器/可复用助手前，必须先搜索接口索引确认不存在同类物。
3. **活文档写现状，CHANGELOG 写历史**：架构/规则文档里禁止出现"本次修改""此前版本曾…"这类叙事；它们只描述当前版本的事实。变更过程、原因、验证证据全部进 `CHANGELOG.jsonl`。
4. **JSONL 只追加**：三个索引可以修改既有行（它们是现状登记表）；`CHANGELOG.jsonl` **只允许追加，永不修改或删除既有行**。写错了就追加一条更正记录（`change_type: "correction"`，正文引用被更正条目的 `id`）。
5. **同版本原则**：一次完成的变更，其 `ProductIdentity.Version`、CHANGELOG 记录的 `version`、受影响索引行的 `updated_version` 必须是同一个版本号。
6. **改完即验**：任何触碰 `Docs/**` 的提交，必须先通过 §8 的验证 Gate（JSONL 可解析、id 唯一、引用路径存在）。
7. **并发假设**：编辑任何文档前重新读取其当前内容；行号会漂移，定位以内容搜索为准。

---

## 2. 文档地图与单一事实源分配

| 文档 | 类型 | 唯一负责的主题（owner） |
|---|---|---|
| `AGENTS.md`（根目录） | 规则 | 项目约束、运行时不变量、构建/部署/验证规则、当前版本号 |
| `Docs/AGENTS.md`（本文件） | 规则 | 文档命名、索引、CHANGELOG、文档生命周期与验证 |
| `README.md` | 概览 | 产品是什么、安装/卸载、命令行入口一览 |
| `Docs/Component-Refresh-Rules.md` | 规则 | **所有**刷新间隔、定时器归属、手动刷新 token、网络事件、单飞、冷却、挂起/恢复刷新策略 |
| `Docs/Performance-And-Window-Runtime.md` | 架构 | 分层窗口运行时、渲染缓冲、烧屏防护、性能模式、显示恢复 |
| `Docs/CodexRadar-Architecture.md` | 架构 | Codex Radar 窗口结构与绘制 |
| `Docs/Codex-ClaudeRadar-Architecture.md` | 架构 | 独立 Claude Radar 窗口 |
| `Docs/Claude-EvenRow-DialCard-Technical.md` | 架构 | Claude EvenRow/DialCard 细节 |
| `Docs/NetworkMonitor-Architecture.md` | 架构 | 网络监控窗口 |
| `Docs/PowerThermal-Architecture.md` | 架构 | 功耗温度窗口 |
| `Docs/Hardware-Support.md` | 参考 | 目标硬件与设备家族差异 |
| `Docs/Interface-And-Reuse-Resources.md` | 参考 | 可复用资源/助手清单（人读版；机器版是接口索引） |
| `Docs/Fable5-Data-Sources-And-Caching-Technical.md` | 参考 | 数据源 URL、fallback 链、缓存文件位置 |
| `Docs/Fable5-Frontend-Rendering-Technical.md` | 规则 | 前端渲染管线、变体系统、sample/current 采样语义、绘制禁改清单与查验流程 |
| `Docs/Indexes/FEATURE_INDEX.jsonl` | 索引 | 功能 → 文件/入口/设置键/接口/推荐测试 的机器索引 |
| `Docs/Interfaces/INTERFACE_INDEX.jsonl` | 索引 | 全部接口与持久化资源的机器索引 |
| `Docs/Technical/` + 其中 `INDEX.jsonl` | 快照 | 版本化执行规格（SPEC/GoalSpec），一经执行不可变 |
| `Docs/Maintenance/CHANGELOG.jsonl` | 历史 | 全部变更、部署与已确认问题的记录 |
| `Docs/Reports/` | 快照 | 诊断/性能报告归档 |

新建文档前先问：**这个主题已有 owner 吗？** 有 → 更新 owner，不新建。没有 → 归入上表某一类型，并把新文档登记进本表（修改本文件属于文档变更，走正常流程）。

---

## 3. 命名规则

| 类别 | 位置 | 命名 | 示例 |
|---|---|---|---|
| 活文档（架构/规则/参考） | `Docs/` | `<主题>-<类型>.md`，PascalCase-连字符，**不带版本号和日期**（内容首部标 `适用版本`） | `NetworkMonitor-Architecture.md` |
| 执行规格（给执行 AI 的 SPEC） | `Docs/Technical/` | `<模型前缀>-<主题>-SPEC-v<版本>-<yyyyMMdd-HHmmss>.md` | `Codex-ClaudeRadar-Window-SPEC-v1.0.3.68-20260704-224807.md` |
| 目标规格（GoalSpec） | `Docs/Technical/` | `<模型前缀>-GoalSpec-<主题>-v<版本>-<时间戳>.md` | — |
| 报告 | `Docs/Reports/<分类>/` | `<主题>-v<版本>-<yyyyMMdd>.md` | — |
| 索引/日志 | 固定路径 | 不改名、不分卷 | — |

- **模型前缀**：Codex 是项目主 AI，可不带前缀；其他模型新建的文档一律加自己的短名前缀（`Fable5-`、`Dsv4-` 等）。`AGENTS.md`/`README.md` 这类功能性文件名除外。
- 版本号 = 创建时的 `ProductIdentity.Version`；时间戳用本地时间。
- 文件名内禁止空格与中文（正文可以中文）；禁止 `-final`、`-new`、`-v2` 这类相对词，版本号就是版本词。

---

## 4. 活文档的更新规则

**触发表**——发生左列变更时，必须检查并同步右列文档（这是"改代码必须带文档"的最小集合）：

| 代码变更 | 必查文档 |
|---|---|
| 任何刷新间隔/定时器/单飞/冷却/手动 token/挂起恢复策略 | `Component-Refresh-Rules.md`（唯一 owner，别处不得有间隔表） |
| 新增/修改/删除 设置键 | `FEATURE_INDEX` 对应行的 `setting_keys`；相关窗口架构文档 |
| 新增/修改 外部 URL、持久化文件、命令行参数、进程间接口 | `INTERFACE_INDEX`（必须）+ `Fable5-Data-Sources-And-Caching-Technical.md`（若涉数据源/缓存） |
| 窗口布局/绘制结构变化（非纯配色） | 对应 `*-Architecture.md` |
| 功能新增/移动/改名/废弃、推荐测试变化 | `FEATURE_INDEX` |
| 版本号提升 | 根 `AGENTS.md` 的 `Current version` |

**写法要求**：
- 每篇活文档首部固定两行：`适用版本：x.y.z.w`（最后一次核对的版本）与一句话定位。核对过内容仍准确时也要把版本号刷新——它表达的是"截至此版本本文属实"。
- 描述必须可定位：引用 `文件名` + 成员名（如 `CodexRadarForm.LoadCodexRadarCache`），不写裸行号（行号必漂移）。
- 常量写值也写名：`StatusLineCacheMaxAgeMinutes = 360`，这样代码改了能 grep 到文档。
- 段落删除优于段落标注：功能删除后，活文档直接删掉对应内容（历史在 CHANGELOG 和 git），不保留"已废弃：…"的尸块；唯一例外是刻意保留的回滚路径，须注明门控旗标名。

---

## 5. 两个现状索引（FEATURE / INTERFACE）

### 5.1 FEATURE_INDEX.jsonl —— "这个功能在哪"

一行一个功能。字段（schema_version 1，全部沿用现有 schema，不新增私有字段）：

| 字段 | 规则 |
|---|---|
| `feature_id` | 全局唯一，`<模块>.<功能>` 小写下划线（如 `codex_radar.quota_consumption_ring`），一经发布不改名——改名等于删一条加一条 |
| `feature_name` / `aliases` | 名称中文；aliases 是**搜索词**，中英混排，站在"下次会用什么词找它"的角度写，宁多勿少 |
| `window_page` / `module` | 所属窗口区域 / 主类 |
| `primary_files` / `entrypoints` | 主要文件与入口方法名；文件路径必须真实存在（Gate 校验） |
| `setting_keys` / `interface_ids` | 关联设置键、关联接口 id（必须能在 INTERFACE_INDEX 找到） |
| `recommended_test_commands` | 改这个功能后该跑什么（`--test-layout` 等） |
| `status` | `active` / `deprecated` / `removed` 三值封闭 |
| `added_version` / `updated_version` / `updated_at` / `timestamp_utc` | 版本与时间戳；每次实质修改行内容必须刷新 `updated_*` |

更新时机：功能新增、移动、改名、废弃、其主文件迁移、推荐测试变化。**纯视觉微调不动索引。**

### 5.2 INTERFACE_INDEX.jsonl —— "这个接口/资源是什么"

一行一个接口或持久化资源。`kind` 封闭词表（现状即规范）：`external_api` / `internal_api` / `command` / `event` / `service` / `config` / `file_format` / `resource_directory`。

- `id`：`<kind 前缀>.<归属>.<名字>` 小写点分（如 `external_api.codex_radar.current`、`file_format.codex_quota`），唯一且稳定。
- 必填：`name`、`direction`（consume/provide/both）、`owner_module`、`location`（源码文件）、`entrypoint`（URL/路径/方法）、`purpose`（一句话）、`lifecycle`（调度/TTL 摘要）、`status`、版本戳字段。
- 视情况填：`protocol`、`inputs`、`outputs`、`dependencies`、`authentication`、`stability`、`references`、`reuse`。
- **登记门槛**：凡是新的外部 URL、`<DATA>` 目录新文件、新命令行参数、新的跨窗口服务/事件，一律登记；纯类内私有方法不登记。
- 废弃流程：`status` 改 `deprecated` 并在 `lifecycle` 注明替代物 → 代码删除后改 `removed`（保留行，作废弃备查）。

---

## 6. CHANGELOG.jsonl

### 6.1 何时追加

| 场景 | 记录 |
|---|---|
| 完成一项源码/行为/文档变更 | 一条变更记录 |
| 部署正式 exe | 一条 `deployment` 记录（与变更记录分开） |
| 确认了一个暂不修复的问题 | 一条 `confirmed_issue` 记录 |
| 更正历史记录错误 | 一条 `correction` 记录，正文引用原条目 `id` |

### 6.2 change_type 封闭词表（新规则）

历史条目已出现 51 种随手命名（`fix_and_ui_rewrite`、`spec_progress_validation_and_release`…），**自本文件生效起收敛为以下 12 个值，禁止再造新词**；一次提交涉及多类时取最主要的一个，其余语义放进 `scope` 文字：

`feature` · `fix` · `behavior_change` · `ui_change` · `perf` · `refactor` · `documentation` · `spec` · `release` · `deployment` · `revert` · `confirmed_issue`（另加 `correction` 仅用于更正条目）

历史条目**不回改**（铁律 4）。

### 6.3 必填字段与格式

沿用现有 schema（`schema_version: 1`）。最小必填集：

- `id`：`change-<UTC 时间 yyyyMMddTHHmmssZ>-<版本号点转横线>-<短 slug>`；deployment 记录用 `deploy-` 前缀。全文件唯一（Gate 校验）。
- `timestamp_utc` / `timestamp_local` / `timezone` / `version` / `scope`（一句话）/ `module` / `change_type` / `author_model`。
- `diagnosis`（为什么改）/ `method`（怎么改的）/ `changed_locations`（文件清单）/ `verification_commands` + `verification_evidence`（跑了什么、看到了什么——**写实际输出摘要，不写"应该会通过"**）/ `residual_risks`。
- 部署记录必须含备份路径、目标路径、版本/哈希核对证据。

### 6.4 禁止事项

- 禁止把维护历史写进根 `AGENTS.md` 或任何活文档。
- 禁止一条记录covering多个不相关变更——一事一条。
- 禁止在验证证据里写未执行的命令。

---

## 7. Docs/Technical 与 INDEX.jsonl（版本化快照）

- `Docs/Technical/` 存**执行规格**：写给执行 AI 的 SPEC、GoalSpec、一次性变更总结。它们是快照：**开始执行后正文不可再改**；要改需求就另发新版本文件（新时间戳），旧文件保留。
- 每个文件必须在 `Docs/Technical/INDEX.jsonl` 登记一行：`id`（`<doc_type>.<主题>.<版本下划线>.<时间戳>`）、`doc_path`、`doc_type`（`implementation_spec` / `goal_spec` / `change_summary` / `design_notes`）、`title`、`goal`、`version`、`generated_model`、时间戳字段、`status`。
- `status` 词表按 doc_type 区分：执行类（`implementation_spec` / `goal_spec`）走生命周期 `draft` →（用户确认）`approved` →（执行完）`implemented` / `abandoned`；记录类（`change_summary` / `design_notes`）固定 `complete`。执行完成时**必须回填 status**——这是目前最常被遗漏的一步。
- SPEC 被 GoalSpec 引用时记录 `spec_path` + `spec_sha256`（内容寻址，防止执行时规格被偷改）。
- 存量执行规格已全部迁入本目录（2026-07-06 文档整理），Docs/ 根不再保留任何执行规格。

---

## 8. 验证 Gate（任何 Docs 变更提交前必须全绿）

```powershell
# 1) 三索引 + CHANGELOG 逐行可解析、id 唯一
python - <<'EOF'
import json, sys, collections
files = {
 'Docs/Indexes/FEATURE_INDEX.jsonl': 'feature_id',
 'Docs/Interfaces/INTERFACE_INDEX.jsonl': 'id',
 'Docs/Technical/INDEX.jsonl': 'id',
 'Docs/Maintenance/CHANGELOG.jsonl': 'id',
}
fail = False
for path, key in files.items():
    seen = collections.Counter()
    for i, line in enumerate(open(path, encoding='utf-8'), 1):
        if not line.strip(): continue
        try: obj = json.loads(line)
        except Exception as e: print(f"FAIL {path}:{i} bad json: {e}"); fail = True; continue
        seen[obj.get(key, '')] += 1
    dup = [k for k, v in seen.items() if v > 1 and k]
    if dup: print(f"FAIL {path} duplicate {key}: {dup}"); fail = True
print("FAIL" if fail else "PASS: jsonl parse + id uniqueness")
sys.exit(1 if fail else 0)
EOF

# 2) 索引引用的源码文件与文档路径必须存在
python - <<'EOF'
import json, os, sys
fail = False
for line in open('Docs/Indexes/FEATURE_INDEX.jsonl', encoding='utf-8'):
    if not line.strip(): continue
    o = json.loads(line)
    if o.get('status') == 'removed': continue
    for f in o.get('primary_files', []):
        if not os.path.exists(f): print("FAIL missing", o['feature_id'], f); fail = True
for line in open('Docs/Technical/INDEX.jsonl', encoding='utf-8'):
    if not line.strip(): continue
    o = json.loads(line)
    for k in ('doc_path', 'spec_path'):
        p = o.get(k)
        if p and not os.path.exists(p): print("FAIL missing", o['id'], p); fail = True
print("FAIL" if fail else "PASS: path existence")
sys.exit(1 if fail else 0)
EOF

# 3) 版本一致性：根 AGENTS.md 的 Current version == ProductIdentity.Version
#    （grep 两处版本号人工比对，或写入提交说明）
# 4) git diff --check（无空白错误）
```

新增 CHANGELOG 条目时额外自查：`change_type` 在 §6.2 词表内、`version` 等于当前 `ProductIdentity.Version`、验证证据是实跑结果。

---

## 9. 代理查阅流程（省 token 的读法）

1. 接到任务 → 关键字搜 `FEATURE_INDEX.jsonl`（aliases 就是为此准备的）→ 得到 `primary_files` / `entrypoints` / `interface_ids` / 推荐测试。
2. 涉及接口/持久化文件 → 按 `interface_ids` 精确读 `INTERFACE_INDEX.jsonl` 对应行。
3. 需要背景 → 只读该功能 owner 架构文档的相关小节；刷新/调度问题直接查 `Component-Refresh-Rules.md`；数据源/缓存问题直接查 `Fable5-Data-Sources-And-Caching-Technical.md`。
4. 需要维护脉络 → 关键字搜 `CHANGELOG.jsonl`，只读命中行，**不要从头读**。
5. 完成变更 → 按 §4 触发表同步文档 → 按 §5/§6 更新索引与日志 → 跑 §8 Gate。
