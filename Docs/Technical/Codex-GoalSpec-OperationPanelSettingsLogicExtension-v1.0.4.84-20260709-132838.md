# GoalSpec: Operation Panel Settings Logic Extension

适用版本：1.0.4.84

## Goal

执行 `执行spec。` 目标，落实 `Docs/Technical/Codex-OperationPanel-SettingsLogicExtension-SPEC-v1.0.4.83-20260709-130425.md` 中的操作面板设置逻辑扩展方案，并保持“不改之前写好的操作面板逻辑”作为硬边界。

## Spec Fingerprint

- Spec 路径：`Docs/Technical/Codex-OperationPanel-SettingsLogicExtension-SPEC-v1.0.4.83-20260709-130425.md`
- SHA256：`61C9EAC810C03E61C5AA89E436FD67C95CD4172127B852C179012E74035105C4`
- 执行版本：`1.0.4.84`
- 记录时间：`2026-07-09T13:28:38+09:00`

## 需求映射

- 新增设置 `OperationSettingsLogicExtensionEnabled`，默认关闭，迁移旧配置时保持原 3 项设置分支。
- 开启后「设置」分支从 3 项扩展到 5 项：程序设置、特殊设置、Windows 设置、常用逻辑、全部开关。
- 常用逻辑复用已有按钮：AI/额度、Radar、系统快捷、网络功耗、维护和辅助入口。
- 全部开关目录按系统、隐藏与防烧屏、主指标、Radar 通用、Codex、Claude、网络功耗测试分层；层级容量按 3/5/7/9/11/13 递增，不要求每层塞满。
- 所有新增项通过既有 `RadialNode`、`NewBranch`、`NewLeaf`、`NewToggle` 和 `OperationForm.ExecuteRadialSettingToggle` 接入，不新建几何、命中、绘制或点击分发路径。

## 实现范围

- `Settings/WidgetSettings.cs`：`CurrentSettingsVersion=59`，新增持久化布尔设置 `OperationSettingsLogicExtensionEnabled`，覆盖默认值、Clone、Save/Load、ApplyValue 和迁移。
- `Settings/Win11SettingsForm.cs`：在操作面板「按钮与面板」分组暴露开关，并加入设置绑定自检必需项。
- `Core/OperationForm.cs` 与 `Core/WidgetForm.cs`：增加从操作面板保存布尔设置的宿主回调；AI 阻断和额度计划仍走既有专用回调。
- `Core/OperationForm.RadialDial.cs`：只扩展树构建、开关节点描述符、状态读取、自检和图标绘制；保留既有布局、路径解析、命中测试、绘制和鼠标分发代码。
- `Core/OperationForm.RenderSample.cs`：补齐新增构造参数，保证渲染样例路径可构建。
- `Docs/Indexes/FEATURE_INDEX.jsonl`、`Docs/Interfaces/INTERFACE_INDEX.jsonl`、`Docs/Performance-And-Window-Runtime.md`、`Docs/Technical/INDEX.jsonl`、`Docs/Maintenance/CHANGELOG.jsonl`：同步功能定位、接口、架构说明、spec 状态和维护记录。

## 硬边界审计

实现没有为扩展设置新增独立的扇形几何算法、命中遮罩算法、绘制分发器或鼠标分发器。扩展入口集中在根树缓存条件、`BuildRadialCommonLogicBranch`、`BuildRadialAllSettingsBranch`、`NewSettingToggle` 和 `ExecuteRadialSettingToggle`。既有 `ComputeRadialLayout`、`ResolveSelectionPath`、`HandleRadialMouseDown`、`HandleRadialMouseUp`、`HandleRadialMouseMove`、`DrawOperationWindowRadialDial` 和 `PaintRadialHitMask` 的操作逻辑边界保持不变。

## 验证证据

- `powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Arm64.ps1 -OutputPath .\Release\DesktopCodexAssistant-arm64.exe -Platform arm64`：成功生成 Release ARM64。
- `.\Release\DesktopCodexAssistant-arm64.exe --test-operation-panel`：退出码 0。
- `.\Release\DesktopCodexAssistant-arm64.exe --test-settings-bindings`：退出码 0。
- `.\Release\DesktopCodexAssistant-arm64.exe --test`：退出码 0。
- `.\Release\DesktopCodexAssistant-arm64.exe --test-layout`：退出码 0。
- `.\Release\DesktopCodexAssistant-arm64.exe --render-operation --out .\_build\radial-render`：退出码 0。
- 正式部署：备份旧 E 正式 exe 到 `E:/Codexproject/desktopdata/DesktopCodexAssistant/_build/formal-backups/20260709-132629-operation-settings-logic-extension-1.0.4.84/E`，旧 SHA256 `ACA210F0A11BB07EE5DF04F51F6C1FBF213D47B49F6CF91B00BF3FD96BAF0BBF`；覆盖 E/D 正式 exe 后 SHA256 `5BDA0C54B5DFC64C702BD8F43F2D83D34BB6EBC50D22B655D6CEC2493E3F3EAA`。
- 重启验证：`DesktopCodexAssistant.exe` PID 22624，路径 `E:\Codexproject\desktopdata\DesktopCodexAssistant\DesktopCodexAssistant.exe`，FileVersion/ProductVersion 均为 `1.0.4.84`，Responding=True。

## Spec 偏离

无需求偏离。x64 未构建，因为项目规则将 UX3407N/UX3607O 分支的默认验证和部署架构限定为 ARM64，除非用户明确要求 x64。

## 限制与后续

- 新开关默认关闭，用户启用前运行时仍显示旧的 3 项设置分支。
- 「全部开关」只覆盖当前可安全以布尔设置表达的选项；需要输入值、枚举、文本、敏感凭据或需要专用确认流程的设置仍保留在普通设置页。
- 开机启动项开关保留确认框，因为它会修改当前用户 Windows 开机启动项。
