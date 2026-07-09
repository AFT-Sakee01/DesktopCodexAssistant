# Operation Panel Settings Logic Extension SPEC

Version: 1.0.4.83  
Status: draft  
Generated model: Codex  
Generated at: 2026-07-09 13:04:25 +09:00  
Baseline operation panel file hash: `Core/OperationForm.RadialDial.cs` SHA256 `694F42E7B098AFDFD21B25E3AD7DD53538549A82DAE9F18EA547CBD55B963B6F`

## Goal

Add an optional setting named `OperationSettingsLogicExtensionEnabled` ("设置扩展到操作逻辑"). When enabled, the operation panel's RadialDial `设置` branch exposes:

- more existing operation-panel actions in contextually useful places;
- all feasible settings-window toggle options as RadialDial toggle leaves;
- deeper but human-readable branches, allowing level caps up to 11 or 13 when needed.

The extension must be off by default. With the setting off, the operation panel tree must be byte-behavior compatible with the current RadialDial menu structure from the user's perspective.

## Hard Boundaries

This spec has one non-negotiable boundary: do not rewrite or disturb the operation panel logic already designed for RadialDial.

The implementation must not change:

- `RadialNode` tree semantics: `Id`, `Label`, `BaseColor`, `DrawIcon`, `Children`, `Execute`, `GetToggleState`, `IsUnavailable`, `IsBusy`, `GetTooltip`.
- `NewBranch`, `NewLeaf`, and `NewToggle` as the public-in-file quick-add node factories, except for additive overloads if absolutely necessary.
- `ComputeRadialLayout`, `ComputeRingRadius`, `ComputeArcOffsets`, `BuildRectsFromOffsets`, or the 8-82 degree quadrant distribution behavior.
- `RadialHitTest`, mouse dispatch, branch drill-in behavior, path resolution, selected-path drawing, sibling rails, core open/close behavior, or idle collapse behavior.
- `DrawRadialNode` fill precedence: unavailable > busy > toggle-state green/red > selected blue > `BaseColor`.
- Existing node ids and existing actions unless the change is strictly additive and preserves the old path.

Allowed changes are limited to:

- adding a persisted setting and settings UI binding;
- adding a generic boolean-setting toggle callback from `OperationForm` to `WidgetForm`;
- adding metadata/helper methods that create new `RadialNode` entries through the existing factories;
- extending `BuildRadialRoots()` by adding nodes when `OperationSettingsLogicExtensionEnabled` is true;
- extending tests and docs to cover the new branches.

## Current Implementation Facts

The current operation panel already provides the quick-add path this feature should use:

- `BuildRadialRoots(bool includeBattery)` is the only place that should define the menu tree.
- `NewBranch`, `NewLeaf`, and `NewToggle` are sufficient to add normal actions, branches, and setting-like toggles.
- Layout, hit testing, drawing, path highlighting, tooltip routing, and click dispatch walk the tree generically.
- Sibling placement is automatic distribution by list order, not semantic sorting. Therefore the implementation must intentionally order children in human-friendly order.

The current `设置` branch has exactly three children:

1. `程序设置`
2. `特殊设置`
3. `系统设置`

With this extension disabled, this must remain exactly true.

## Settings Contract

Add `WidgetSettings.OperationSettingsLogicExtensionEnabled`.

Required behavior:

- Default: `false`.
- Persisted in `settings.ini`.
- Included in `Clone`, `Load`, `Save`, `Normalize`, and `WidgetSettings.RunFullRoundTripSelfTest`.
- Exposed in `Win11SettingsForm` under `操作面板 -> 按钮与面板`.
- Included in `Win11SettingsForm.VerifySelfTest` required bindings.
- Changing it must invalidate/rebuild the cached RadialDial roots immediately.

Root cache must include at least these cache keys:

- `includeBattery`
- `OperationSettingsLogicExtensionEnabled`

If later branch visibility depends on other settings, those dependencies must be added to the cache key instead of allowing stale menus.

## Tree Shape

### Disabled

When `OperationSettingsLogicExtensionEnabled == false`, keep the current tree:

- Root level: `设置`, `系统`, `辅助`
- `设置`: `程序设置`, `特殊设置`, `系统设置`
- `特殊设置`: `链接阻断`, `额度计划`, `CTF 重启`

### Enabled

When enabled, only the `设置` branch expands from 3 children to 5 children:

1. `程序设置`
2. `特殊设置`
3. `系统设置`
4. `常用逻辑`
5. `全部开关`

The existing first three items stay in their current relative order. New items are appended after them to preserve muscle memory.

## Level Caps

Use an explicit recursive cap rule:

| Logical level | Max siblings | Notes |
|---|---:|---|
| Root | 3 | Existing `设置/系统/辅助` cap remains. |
| Level 2 | 5 | `设置` can expand to five children only when enabled. |
| Level 3 | 7 | Category selection level. |
| Level 4 | 9 | Normal leaf/toggle groups. |
| Level 5 | 11 | Dense leaf groups only. |
| Level 6+ | 13 | Absolute upper cap; do not exceed. |

These are maximums, not targets. Prefer 3-7 items for decision branches. Use 9/11/13 only for leaf-heavy toggle groups where splitting further would make the action harder to find.

## Human-Oriented Branch Design

### `设置 -> 常用逻辑`

Purpose: high-frequency actions and toggles, including more existing buttons, grouped by user intent.

Recommended children, up to 7:

1. `显示隐藏`
2. `AI/额度`
3. `Radar 窗口`
4. `系统快捷`
5. `辅助入口`
6. `网络功耗`
7. `维护调试`

Suggested leaves:

- `显示隐藏`
  - existing action: `悬停透明度`
  - toggles: `鼠标靠近时隐藏`, `空闲自动隐藏`, `最大化自动隐藏`, `圆圈悬停保持显示`, `隐藏反色防烧屏`
- `AI/额度`
  - existing actions/toggles: `链接阻断`, `额度计划`
  - toggles: `AI 自动阻断`, `恢复上次暂停`
- `Radar 窗口`
  - toggles: `启用共享 Radar 小窗`, `启用独立 Claude Radar`, `过期自动切换模型`
- `系统快捷`
  - existing actions: `任务管理器`, `快速设置`, `系统工具菜单`, `电源菜单`, `刷新`, `重启程序`, `置顶 Dock`
- `辅助入口`
  - existing actions: `AI Studio`, `实时字幕`, `电池保护暂停`, `电池保护恢复`
  - battery actions remain conditional on `ShouldShowBatteryCareButtons()`
- `网络功耗`
  - toggles: `启用 GFW 检测`, `功耗模块自动大小`
  - existing action: `刷新`
- `维护调试`
  - toggles: `告警测试`, `强制显示 FPS`
  - test/debug toggles should stay deeper than daily toggles to reduce accidental activation.

Existing actions duplicated into `常用逻辑` must call the same existing execution path and show the same busy/unavailable conditions. Use new ids with a clear prefix, for example `common_task_manager`, but keep labels/tooltips consistent.

### `设置 -> 全部开关`

Purpose: complete catalog of settings-window toggle switches. Do not include numeric inputs, combo boxes, text boxes, refresh-token buttons, service-probe buttons, or secret/key dialogs.

Recommended children, up to 7:

1. `系统启动`
2. `隐藏防烧屏`
3. `主窗口指标`
4. `Radar 通用`
5. `Codex 设置`
6. `Claude 设置`
7. `网络功耗测试`

Suggested deeper structure:

- `系统启动`
  - `开机启动`
  - `遮挡忽略操作面板`
  - `断开后回退显示器`
  - `分辨率兼容模式`
  - `Seelen Dock 自动拉前`
  - `Win+D 后延迟拉前`
  - `休眠唤醒后重启`
- `隐藏防烧屏`
  - `鼠标靠近时隐藏`
  - `敏感鼠标模式`
  - `延迟显现`
  - `覆盖开启`
  - `反向隐藏`
  - `空闲自动隐藏`
  - `最大化自动隐藏`
  - `圆圈悬停保持显示`
  - `隐藏反色防烧屏`
- `主窗口指标`
  - `显示 CPU`
  - `显示内存`
  - `显示磁盘`
  - `显示网络`
  - `显示 GPU`
  - `显示 NPU`
- `Radar 通用`
  - `过期自动切换模型`
  - `启用共享 Radar 小窗`
  - `启用独立 Claude Radar`
- `Codex 设置`
  - `Codex 链路`
    - `Codex 公开 JSON`
    - `Codex 首页 HTML 回退`
    - `Codex RSS 重置提醒`
  - `额度计划`
    - `启用额度计划`
    - `恢复上次暂停`
  - `额度保护`
    - `到期重置保护`
    - `RSS 重置保护`
    - `Provider 零值保护`
    - `相同余额保留消耗环`
    - `5h 提前满额保护`
    - `周额度突增保护`
    - `严格 5h 边界`
    - `周基线自动修复`
  - `测试覆盖`
    - `用测试值代替实时 IQ`
    - `IQ 基准自动跟随网站`
    - `用测试值代替实时效率`
    - `随机测试`
    - `随机测试自动刷新`
- `Claude 设置`
  - `Claude 站点 JSON`
  - `Claude 社区体感分`
  - `本地 7 天额度线回退`
  - `首页模型元数据回退`
  - `Claude 随机测试`
  - `Claude 随机测试自动刷新`
- `网络功耗测试`
  - `启用 GFW 检测`
  - `功耗模块自动大小`
  - `告警测试`
  - `强制显示 FPS`

The `Codex 设置` branch intentionally uses one more depth. This keeps the high-risk quota protection toggles together without placing 8-13 unrelated switches on one ring.

## Toggle Catalog Source

Use an explicit metadata catalog rather than parsing `Win11SettingsForm.BuildPages()` at runtime.

Recommended structure:

```csharp
private sealed class RadialSettingToggleDescriptor
{
    public string PropertyName;
    public string Id;
    public string Label;
    public Color Color;
    public Action<Graphics, RectangleF> DrawIcon;
    public string TooltipOn;
    public string TooltipOff;
    public bool RequiresConfirmation;
}
```

The catalog can be split by branch helper methods, for example:

- `BuildRadialCommonLogicBranch()`
- `BuildRadialAllSettingsBranch()`
- `NewSettingToggleNode(RadialSettingToggleDescriptor descriptor)`
- `NewExistingActionShortcut(...)`

Each generated setting leaf must still be a normal `NewToggle` node.

Self-tests should use reflection only to verify that every descriptor's `PropertyName` exists on `WidgetSettings` and is `bool`. Runtime rendering/clicking should not use reflection per frame.

## Toggle Execution

Add one generic callback from `OperationForm` to `WidgetForm`, for example:

```csharp
Func<string, bool, bool> setBooleanSettingAction
```

Behavior:

1. Read the current saved settings clone.
2. Verify the property exists and is `bool`.
3. Set it to the requested value.
4. Normalize.
5. Save through the existing `WidgetForm.SaveSettings`.
6. Show a Windows notification on success/failure.

Special cases:

- `AiRequestProtectionManualBlockEnabled` must continue to use `SetAiRequestBlockingFromOperationPanel` because it stops known AI tools and has dedicated notifications.
- `CodexQuotaPlanEnabled` must continue to use `SetCodexQuotaPlanFromOperationPanel` because it has quota-plan-specific notification semantics.
- `StartupEnabled` writes the HKCU Run entry through `Program.SetStartupEnabled`; require an explicit confirmation prompt before toggling it from RadialDial. Reading is allowed without confirmation; writing is not.

If a setting is known to be dangerous or test-only, keep it in `全部开关` and/or `维护调试`, not in the first ring of `常用逻辑`.

## Color Policy

Use existing color mechanics:

- Branches use category `BaseColor`.
- Normal leaves use category `BaseColor`.
- Toggle leaves use existing green/red state fill from `DrawRadialNode`.
- Existing action shortcuts should keep their semantic category color:
  - settings: `RadialSettingsColor`
  - system: `RadialSystemColor`
  - power: `RadialPowerColor`
  - assist: `RadialAssistColor`
  - battery: `RadialBatteryColor`
  - advanced/high-risk: `RadialAdvancedColor`

Do not introduce user-configurable button colors in this feature. The existing code-level color parameter is enough.

## Implementation Tasks

1. Add `OperationSettingsLogicExtensionEnabled` to `WidgetSettings`, settings UI, round-trip persistence, normalization, and `--test-settings-bindings`.
2. Add a generic boolean-setting toggle callback from `WidgetForm` into `OperationForm`.
3. Add descriptor helpers in `OperationForm.RadialDial.cs` that generate branches and toggles exclusively through `NewBranch`, `NewLeaf`, and `NewToggle`.
4. Extend `BuildRadialRoots()`:
   - disabled: exact current tree;
   - enabled: append `常用逻辑` and `全部开关` to `设置`.
5. Extend cache invalidation so the RadialDial root cache rebuilds when the extension setting changes.
6. Extend `RunRadialDialSelfTest()`:
   - disabled settings branch count remains 3;
   - enabled settings branch count is 5;
   - recursive cap rule passes for all branches;
   - generated descriptors all target existing bool properties;
   - special-case toggles route through dedicated callbacks.
7. Update `Docs/Indexes/FEATURE_INDEX.jsonl` and `Docs/Interfaces/INTERFACE_INDEX.jsonl` for the new setting and callback.
8. Append `Docs/Maintenance/CHANGELOG.jsonl` after implementation.
9. Build ARM64, run `--test-operation-panel`, `--test-settings-bindings`, `--test`, and `--test-layout`, then deploy per project rule if source/runtime behavior changes.

## Verification Gate

Minimum required commands after implementation:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\Release\DesktopCodexAssistant-arm64.exe -Platform arm64
.\Release\DesktopCodexAssistant-arm64.exe --test-operation-panel
.\Release\DesktopCodexAssistant-arm64.exe --test-settings-bindings
.\Release\DesktopCodexAssistant-arm64.exe --test
.\Release\DesktopCodexAssistant-arm64.exe --test-layout
```

Documentation/index gate:

- JSONL parse and id uniqueness for `FEATURE_INDEX`, `INTERFACE_INDEX`, `Technical/INDEX`, and `CHANGELOG`.
- Indexed source/doc paths exist.
- `AGENTS.md` current version and `Core/ProductIdentity.cs` version match.
- `git diff --check` has no whitespace errors.

## Acceptance Criteria

- With extension off, the visible operation panel menu remains the existing three-root tree and `设置` still has three children.
- With extension on, `设置` has five children; old three children keep their order and behavior.
- Existing RadialDial geometry, drawing, hit testing, path highlighting, and idle-collapse logic are not changed.
- Existing buttons added into new branches call the same execution paths as their original locations.
- All setting-toggle leaves reflect current state through existing green/red toggle rendering.
- No runtime source parsing is used to build the menu.
- The branch layout is human-oriented: frequent actions near `常用逻辑`, full catalog under `全部开关`, test/high-risk toggles deeper.
