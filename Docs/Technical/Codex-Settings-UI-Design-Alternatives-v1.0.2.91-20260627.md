# Settings UI Design Alternatives

Version: `1.0.2.91`
Date: `2026-06-27`
Model: `Codex`
Status: selected proposal implemented in `Settings/Win11SettingsForm.cs`

## Design Brief

The settings window is a desktop WinForms configuration surface for Desktop Codex Assistant UX3407N/UX3607O. It must keep all current `WidgetSettings` bindings and save/preview behavior while using the current `Win11SettingsForm` as the only settings window.

Product Design plugin context was checked for this task. No saved Product Design user context exists, so the implementation uses the repository's existing `DesignTokens`, WinForms controls, and the selected direction below as the visual source. The plugin's image-to-code workflow was not used because there was no selected screenshot, Figma frame, or generated image target.

## Selected Proposal 1: Fluent Control Center

This proposal is the implemented direction.

Core idea:

- Default settings entry opens a "控制中心" dashboard instead of a long first settings list.
- The dashboard uses module cards as the first decision layer, with icon, status label, short description, and a "配置" action.
- Detailed setting groups remain in the existing secondary pages, so all current settings bindings, preview debounce, save/cancel, search, and wheel scrolling remain intact.
- As of `1.0.2.94`, the old `SettingsForm` fallback and `DESKTOP_CODEX_LEGACY_SETTINGS` switch are removed; the current `Win11SettingsForm` is the only settings window.

Visual language:

- Windows 11 dark Fluent: deep mica-like base, low-contrast cards, 1 px strokes, small accent bars, compact Segoe UI text.
- Cards use restrained 6-8 px radius, not large rounded marketing cards.
- Iconography uses Segoe Fluent Icons / MDL2 fallback already used by the project.
- Layout keeps dense operational scanning: navigation on the left, content on the right, footer actions fixed.

Implementation choices:

- Add `AddDashboardPage()` before all grouped settings pages.
- Add `DashboardModuleCard`, a small custom WinForms panel that draws the module card and contains one real button.
- Add `SelectPageByTitle()` so cards navigate to existing pages without duplicating settings controls.
- Extend search so the dashboard card list filters by title, status, description, and target page.
- Extend layout and self-test so the dashboard is validated alongside existing grouped pages.

Risk boundaries:

- No new settings keys.
- No runtime service changes.
- No persistence behavior changes.
- No dependency on WinUI runtime.
- No x64 build required by this change.

## Module Structure Alternatives

### A. Settings Center

Concept: keep the left category navigation as the primary structure and polish each category page.

Strengths:

- Lowest code risk.
- Users who already understand settings categories need little relearning.
- Search and binding tests remain almost unchanged.

Weaknesses:

- Still feels like a long settings list.
- Does not solve the "first screen lacks hierarchy" problem.
- Visual improvement is limited because the information architecture remains old.

Best use:

- Emergency fallback if module cards cause too much maintenance friction.

### B. Module Dashboard

Concept: first screen is a dashboard of functional modules, each leading to detail settings.

Strengths:

- Better for this app because the program is naturally split into main monitor, Codex Radar, power/thermal, network, connection check, operation panel, and recovery behavior.
- Reduces first-screen clutter while preserving full settings depth.
- Works well with current WinForms implementation because cards can navigate to existing pages.

Weaknesses:

- Requires dashboard search and layout handling in addition to existing page layout.
- If cards start showing live runtime status later, they may need refresh rules and should avoid high-frequency polling.

Best use:

- Selected current direction. It gives the biggest visual/UX improvement with limited code risk.

### C. Desktop Preview Plus Properties

Concept: show a live or static preview of the desktop widget layout on the left or top, with a properties panel for the selected module.

Strengths:

- Best for layout-heavy settings like width, height, offsets, transparency, and module visibility.
- Helps users understand screen occupation and overlap.

Weaknesses:

- Higher rendering and synchronization risk in WinForms.
- Live preview can accidentally increase CPU/GDI work in a settings window.
- More likely to conflict with ongoing module edits because it touches multiple window layout concepts.

Best use:

- Future layout editor, not the default settings rewrite.

### D. Search-First Settings

Concept: settings opens with a search field and result groups; categories are secondary.

Strengths:

- Efficient for expert users who know setting names.
- Good when the number of settings grows further.

Weaknesses:

- Poor discoverability for users who do not know exact terms.
- Does not create a strong visual identity.
- Search must handle Chinese aliases, old setting names, property keys, and module names to feel complete.

Best use:

- As an enhancement to the current dashboard search, not as the whole interface.

### E. Task-Based Settings

Concept: group settings by user goals, such as "降低功耗", "防烧屏", "修复唤醒", "调整窗口位置", and "调试网络".

Strengths:

- Very user-friendly for repeated maintenance tasks.
- Can hide technical property names behind outcomes.

Weaknesses:

- Requires a stable task taxonomy and more explanatory copy.
- Easy to duplicate the same setting across multiple tasks.
- More difficult to keep synchronized with low-level `WidgetSettings` bindings.

Best use:

- Future quick-fix assistant or setup wizard, not the main settings shell.

## Art Direction Alternatives

### 1. Fluent Control Center

Selected and implemented.

Palette:

- Base: near-black mica gray.
- Surface: slightly lighter charcoal card.
- Stroke: soft gray with low alpha.
- Accent: cool cyan/blue from existing settings tokens.
- Text: high-contrast primary, muted secondary, tertiary hints.

Why selected:

- Fits Windows 11 expectations without requiring WinUI.
- Keeps an operational desktop-tool feel.
- Avoids decorative gradients and oversized hero composition.
- Works with existing `DesignTokens.SettingsTheme`.

### 2. OLED Command Deck

Concept: a very dark, high-contrast panel for OLED screens, with bright status accents and compact control density.

Palette:

- Base: pure or near-pure black.
- Cards: black with thin gray strokes.
- Accent: green/cyan status colors.
- Text: bright white with stronger muted gray tiers.

Pros:

- Strong burn-in-aware visual identity.
- Very readable in dark environments.

Cons:

- Too close to a one-note dark technical panel.
- More likely to show anti-aliased white edge residue on OLED if hidden-mode color protection is active.

### 3. Dev Home Dashboard

Concept: mimic a developer dashboard with dense tiles, logs, recent checks, and module health summaries.

Palette:

- Base: neutral dark gray.
- Cards: layered panels with status chips.
- Accent: blue, green, amber, red semantic colors.

Pros:

- Good for future diagnostics and performance timing summaries.
- Makes module health visible.

Cons:

- Live status cards could introduce polling or refresh-rule complexity.
- More visual density may recreate the old clutter problem.

### 4. Precision Panel

Concept: flat, compact, almost instrument-like settings table with tight spacing and minimal decoration.

Palette:

- Base: dark neutral.
- Surfaces: same brightness with separators instead of cards.
- Accent: single thin focus indicator.

Pros:

- Very efficient for repeated expert edits.
- Low rendering overhead.

Cons:

- Less visually fresh.
- Harder to distinguish modules at a glance.

### 5. Light Technical Console

Concept: light Windows settings style with pale surfaces and strong dark text.

Palette:

- Base: off-white or light gray.
- Cards: white.
- Stroke: subtle gray.
- Accent: blue.

Pros:

- Strong daytime readability.
- Aligns with default Windows Settings light mode.

Cons:

- The application is dark desktop overlay software; switching settings to a bright surface feels disconnected.
- More likely to be visually harsh during night use.

## Current Implementation Acceptance

Implemented in version `1.0.2.91`:

- Dashboard page named `控制中心`.
- Eight module cards: 系统, 隐藏与鼠标, 主窗口, Codex Radar, 功耗与温度, 网络, 操作面板, 恢复与诊断.
- Cards navigate to existing detail pages rather than duplicating setting controls.
- Search filters both dashboard cards and existing grouped setting rows.
- Minimum-window clipping self-test includes the dashboard cards.

Validation target:

- ARM64 compile succeeds.
- `--test-settings-bindings` succeeds.
- `--test-layout` succeeds.
- JSONL indexes and maintenance log parse line by line.

## 1.0.2.92 Product Design Correction

User review rejected the first implementation quality level. The specific problems were:

- The control center looked like a compressed card list rather than a mature tool settings surface.
- Icon glyphs were oversized Segoe character drawings and visually collided with text.
- Line spacing and card spacing were far too tight; a comfortable value should be at least roughly doubled from the first pass.
- Icons must come from `E:\phosphor-icons`, not from manually drawn shapes or Segoe glyphs.

Correction applied in `1.0.2.92`:

- The dashboard changed from two-column cards to full-width module panels, closer to the reference image's heavy right-side section cards.
- Navigation rows were enlarged to 56 px and now use Phosphor PNG icons.
- Dashboard panels were enlarged to 176 px with wider text lanes, right-side action buttons, and larger vertical gaps.
- Grouped setting pages also received larger section-title spacing and taller setting rows, so the detail pages do not revert to the cramped 1.0.2.91 rhythm.
- `Win11SettingsForm` now loads Phosphor PNG assets from `E:\phosphor-icons\PNGs\regular` and `E:\phosphor-icons\PNGs\bold`, then recolors them at runtime to match the dark theme.
- `--test-settings-bindings` checks the required Phosphor icon files exist.

## 1.0.2.94 Source Audit Note

The current integrated `Win11SettingsForm.cs` no longer contains the Phosphor PNG loader or the icon-file assertion described above. Navigation icons are currently drawn with Segoe Fluent Icons / MDL2 glyphs. Restoring Phosphor PNG loading and its self-test coverage remains a follow-up optimization if the visual requirement still applies.
