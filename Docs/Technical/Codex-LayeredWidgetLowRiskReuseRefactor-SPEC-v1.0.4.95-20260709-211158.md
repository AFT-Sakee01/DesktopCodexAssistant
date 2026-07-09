# Layered Widget Low-Risk Reuse Refactor SPEC

## Metadata

- Document: `Codex-LayeredWidgetLowRiskReuseRefactor-SPEC-v1.0.4.95-20260709-211158.md`
- Generated model: Codex
- Timestamp UTC: `2026-07-09T12:11:58Z`
- Timestamp local: `2026-07-09T21:11:58+09:00`
- Timezone: `Asia/Tokyo (+09:00)`
- Current version: `1.0.4.95`
- Target implementation version: `1.0.4.96`
- Status: draft

## Goal

在不改变窗口视觉、刷新节奏、隐藏规则、日志语义和发布策略的前提下，只对已重新验证为低风险的 4 个重复实现做复用整理：

1. 增加共享 UTF-8 no BOM 编码常量。
2. 将分层窗口共同持有的当前设置引用提升到 `LayeredWidgetFormBase`。
3. 将分层窗口共同持有的防烧屏像素微迁移刷新槽提升到 `LayeredWidgetFormBase`。
4. 将重复透明度百分比到 alpha 的计算公式提升到 `LayeredWidgetFormBase`。

本规格是实现前约束文件，不直接修改源码、不编译、不部署。

## Revalidation Findings

### Shared UTF-8 No BOM Encoding

结论：合理。

当前代码没有统一的 `SharedEncoding`，但存在多个等价实现：

- `Core/NetworkCheckHistoryLogger.cs`
- `Core/QuotaDecisionHistoryLogger.cs`
- `Core/SecretStore.cs`
- `Core/UiHangWatchdog.cs`
- `Core/ClaudeCodeUsageReader.cs`
- `Core/ClaudeRadarReader.cs`
- `Core/CodexQuotaGoalPlanner.cs`
- `Core/CodexRadarForm.cs`
- `Core/CodexRadarModelCatalog.cs`
- `Core/DeepSeekBalanceMonitor.cs`
- `Settings/Win11SettingsForm.cs`
- `ClaudeRadarForm.cs`

这些位置使用 `new UTF8Encoding(false)` 或私有 `Utf8NoBom` 字段，语义一致，统一为只读共享常量不会改变写入格式。该项应只替换 no-BOM 显式写入路径，不顺手替换所有 `Encoding.UTF8`，避免改变依赖默认 UTF-8 行为的读取或兼容逻辑。

### CurrentSettings In Base Class

结论：合理。

下列 7 个 `LayeredWidgetFormBase` 子类各自声明 `private WidgetSettings currentSettings`：

- `ClaudeRadarForm`
- `CodexRadarForm`
- `ConnectionCheckForm`
- `NetworkMonitorForm`
- `OperationForm`
- `PowerThermalForm`
- `WidgetForm`

这些字段都表示窗口运行时的当前设置快照，生命周期与窗口实例一致。提升为 `LayeredWidgetFormBase.CurrentSettings` 可以减少重复字段，不改变设置来源、保存逻辑或构造参数。实现时必须保留 `WidgetForm` 中与保存相关的 `savedSettings` 等其他设置字段，不得把设置窗口或非分层窗口纳入本次变更。

### Burn-In Shift Slot In Base Class

结论：合理。

同一批 7 个分层窗口都维护 `private long burnInShiftSlot`，并通过 `BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot)` 判断是否刷新像素微迁移偏移。刷新槽本身不携带窗口差异，窗口差异由各自调用 `BurnInProtection.ApplyRuntimeOffset(..., BurnInProtection.<Window>Salt)` 时的 salt 决定。

因此可以把槽位和刷新判断封装到基类，但必须保留每个窗口自己的 salt，不得把窗口位移 salt 合并为统一值。

### ComputeOpacityAlpha

结论：合理，但需要保留窗口级边界。

多个窗口重复使用同一类公式：

```csharp
255 - ClampByte(<transparencyPercent> * 255 / 100)
```

该公式可以提升为 `LayeredWidgetFormBase.ComputeOpacityAlpha(int transparencyPercent)`。但 `OperationForm.GetBackgroundOpacityAlpha` 额外先对 `OperationBackgroundTransparencyPercent` 做 min/max 边界限制，该语义应保留，改为先 clamp transparency，再调用共享公式。

## Explicit Exclusions

以下内容来自外部方案，但本次重新验证后不纳入此 SPEC：

- `FileHelper` / 原子写入助手：外部方案包含删除目标文件再移动的路径，可能削弱当前 `File.Replace` 类写入保障。
- `fontCache` 上移：当前 `WidgetForm`、`NetworkMonitorForm` 等缓存结构和使用范围不一致，强行上移会扩大耦合。
- `RoundedRectangle` 合并：现有绘制方法存在四角开关、路径复用和 OLED 变体语义差异，不能按简单公共方法合并。
- `IsBurnInColorProtectionActive` / `ConfigureGraphics` 合并：防烧屏反色、抗锯齿关闭和窗口绘制路径存在历史修复，必须单独验证。
- x64 编译或部署：本项目默认 ARM64；除非用户明确要求，否则不自行编译 x64。

## Implementation Plan

### Phase 0: Baseline Checks

实施前先运行定位检查，确认没有其他代理正在改同一批文件，且当前版本仍为 `1.0.4.95`。

推荐命令：

```powershell
rg -n "new UTF8Encoding\(false\)|private static readonly Encoding Utf8NoBom|private WidgetSettings currentSettings|private long burnInShiftSlot|GetBackgroundOpacityAlpha|GetContentOpacityAlpha|GetBorderOpacityAlpha" Core Settings *.cs
rg -n "Version = \"1\.0\.4\.95\"|Current version: 1\.0\.4\.95" Core\ProductIdentity.cs AGENTS.md
```

### Phase 1: SharedEncoding

新增 `Core/SharedEncoding.cs`：

```csharp
using System.Text;

internal static class SharedEncoding
{
    internal static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
}
```

然后将私有 `Utf8NoBom` 字段和显式 `new UTF8Encoding(false)` 写入参数替换为 `SharedEncoding.Utf8NoBom`。

约束：

- 仅替换显式 no-BOM 语义。
- 不替换 `SharedEncoding.cs` 自身的构造表达式。
- 不批量替换 `Encoding.UTF8`。
- 不改变日志、缓存、设置、模型映射文件的路径和格式。

验收：

```powershell
rg -n "private static readonly Encoding Utf8NoBom" Core Settings *.cs
rg -n "new UTF8Encoding\(false\)" Core Settings *.cs
```

第一条应无结果；第二条应只剩 `Core/SharedEncoding.cs`。

### Phase 2: CurrentSettings

在 `LayeredWidgetFormBase` 增加：

```csharp
protected WidgetSettings CurrentSettings { get; set; }
```

移除 7 个子类中的 `private WidgetSettings currentSettings`，并把该字段引用迁移到 `CurrentSettings`。

约束：

- 替换时区分字段、构造参数、局部变量和方法参数。
- `this.currentSettings` 可直接改为 `this.CurrentSettings`。
- 裸 `currentSettings` 只在确认为字段引用时替换。
- 不修改 `WidgetForm.savedSettings` 和设置保存路径。
- 不修改设置窗口类。

验收：

```powershell
rg -n "private WidgetSettings currentSettings|this\.currentSettings|[^A-Za-z_]currentSettings" ClaudeRadarForm.cs Core\CodexRadarForm.cs Core\ConnectionCheckForm.cs Core\NetworkMonitorForm.cs Core\OperationForm.cs Core\PowerThermalForm.cs Core\WidgetForm.cs
```

剩余命中只能是构造参数、局部变量或注释中明确允许的文本。

### Phase 3: Burn-In Refresh Slot

在 `LayeredWidgetFormBase` 增加：

```csharp
private long burnInShiftSlot = long.MinValue;

protected bool ShouldRefreshBurnInPosition()
{
    return BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot);
}
```

移除 7 个子类中的 `private long burnInShiftSlot`，并把 `BurnInProtection.ShouldRefreshPosition(ref this.burnInShiftSlot)` 替换为 `ShouldRefreshBurnInPosition()`。

约束：

- 保留各窗口 `ApplyRuntimeOffset` 中的 `BurnInProtection.<Window>Salt`。
- 不改变像素微迁移周期、偏移算法和开关设置。
- 不改变隐藏、反色、覆盖开启、反向隐藏等规则。

验收：

```powershell
rg -n "private long burnInShiftSlot|ShouldRefreshPosition\(ref this\.burnInShiftSlot\)" ClaudeRadarForm.cs Core
rg -n "ApplyRuntimeOffset" ClaudeRadarForm.cs Core\CodexRadarForm.cs Core\ConnectionCheckForm.cs Core\NetworkMonitorForm.cs Core\OperationForm.cs Core\PowerThermalForm.cs Core\WidgetForm.cs
```

第一条应无子类直接持有槽位或直接调用；第二条确认窗口 salt 仍然保留。

### Phase 4: Shared Opacity Alpha Formula

在 `LayeredWidgetFormBase` 增加：

```csharp
protected static int ComputeOpacityAlpha(int transparencyPercent)
{
    return 255 - ClampByte(transparencyPercent * 255 / 100);
}
```

将标准透明度公式替换为 `ComputeOpacityAlpha(...)`。

约束：

- `OperationForm.GetBackgroundOpacityAlpha` 必须保留自身的 min/max clamp，再调用共享公式。
- `ScaleAlpha`、`ClampByte` 等非透明度百分比用途不纳入本次变更。
- 不改变隐藏态 95% 透明、反色、防烧屏和面板手动透明度逻辑。

验收：

```powershell
rg -n "255 - ClampByte\(.*255 / 100\)|GetBackgroundOpacityAlpha|GetContentOpacityAlpha|GetBorderOpacityAlpha" ClaudeRadarForm.cs Core
```

剩余的标准透明度计算应统一指向 `ComputeOpacityAlpha`，保留的特殊分支必须有明确原因。

### Phase 5: Version And Documentation

实现通过后，将版本从 `1.0.4.95` 提升到 `1.0.4.96`，并同步：

- `Core/ProductIdentity.cs`
- `AGENTS.md`
- `Docs/Interfaces/INTERFACE_INDEX.jsonl`
- `Docs/Interface-And-Reuse-Resources.md`
- `Docs/Performance-And-Window-Runtime.md`
- `Docs/Maintenance/CHANGELOG.jsonl`

接口索引建议新增或更新：

- `internal_api.shared_encoding`
- `internal_api.layered_widget_form_base`

功能索引通常不需要新增功能项；若实现改变测试入口、窗口入口或维护定位说明，则同步更新 `Docs/Indexes/FEATURE_INDEX.jsonl`。

## Verification Matrix

实现后至少执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe -Platform arm64
.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test
.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-layout
.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-settings-bindings
.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-display-recovery
.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-logger
.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-operation-panel
.\_build\DesktopCodexAssistant-arm64-layered-low-risk-reuse-v1.0.4.96.exe --test-radar-display-lifecycle --iterations 100
```

渲染采样应覆盖所有受 `CurrentSettings`、防烧屏位移和透明度公式影响的分层窗口。若当前可执行文件支持对应命令，至少生成并检查：

- Codex Radar
- Claude Radar
- Operation panel
- Network monitor
- Power thermal
- Connection check
- Main widget

PNG 验收要求：

- 文件存在且非空。
- 像素采样非全透明、非全黑、非全白。
- 隐藏态与普通态下边框和文字没有新增白边、误导色或错位。

文档和索引验收：

```powershell
python - <<'PY'
import json, pathlib
for p in [
    pathlib.Path('Docs/Indexes/FEATURE_INDEX.jsonl'),
    pathlib.Path('Docs/Interfaces/INTERFACE_INDEX.jsonl'),
    pathlib.Path('Docs/Technical/INDEX.jsonl'),
    pathlib.Path('Docs/Maintenance/CHANGELOG.jsonl'),
]:
    ids = set()
    with p.open('r', encoding='utf-8') as f:
        for i, line in enumerate(f, 1):
            if not line.strip():
                continue
            obj = json.loads(line)
            assert obj.get('schema_version') == 1, (p, i)
            key = obj.get('id') or obj.get('feature_id')
            if key:
                assert key not in ids, (p, i, key)
                ids.add(key)
print('jsonl-ok')
PY
git diff --check -- Core Settings Docs AGENTS.md
```

## Rollback Plan

本 SPEC 对应实现应是机械复用整理。若任一窗口显示、隐藏或防烧屏行为异常，按以下顺序回退：

1. 回退 `ComputeOpacityAlpha` 调用，恢复窗口本地透明度方法。
2. 回退 `ShouldRefreshBurnInPosition`，恢复子类 `burnInShiftSlot`。
3. 回退 `CurrentSettings`，恢复子类字段。
4. 最后回退 `SharedEncoding`。

回退时仍需保持用户或其他代理的并发修改，不得使用破坏性 git 操作。

## Acceptance Criteria

- 4 个低风险复用项全部通过 ARM64 构建和相关自测。
- 所有受影响分层窗口渲染采样通过。
- 版本、维护日志、接口索引和相关技术文档同步。
- 没有引入 x64 构建或部署产物。
- 没有纳入本 SPEC 排除的高风险重构项。
