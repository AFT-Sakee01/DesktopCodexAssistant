# GoalSpec: Radar Hardening Consolidated Final Acceptance

## Goal

完成 `Codex-RadarHardeningConsolidated-SPEC-v1.0.5.03-20260711-094500.md` 明确拆分出的两个实施子 Spec，并给出连续版本、测试、文档和 ARM64 正式发布证据。

## Spec Chain

- Consolidated Spec SHA-256: `3BF7748943A5AD6B9520A46763E999CFCA4034FA5A247DE75F5E7FC1607C3547`
- Codex data child SHA-256: `0D6251EA6EEE13AF8B73E4A5B8882AA198457112A54C598D08C5AD3F97A22389`
- Claude/clock child SHA-256: `EF1B6250219F07DE5FA6F3EB31227F79769AAFB4E88E027BD759EAB83838405E`

合并 Spec 顶部已声明拆分版为执行依据。Claude 模型适配、`n2` 徽标和获批时钟阶梯在 `1.0.5.04` 完成；Codex 目录、缓存、额度身份和通知加固在 `1.0.5.05` 完成。

## Child Results

- `Docs/Technical/Codex-GoalSpec-RadarClaudeAdaptationAndClockLadder-v1.0.5.04-20260711-112603.md`
- `Docs/Technical/Codex-GoalSpec-RadarCodexDataHardening-v1.0.5.05-20260711-113355.md`

`.04` 从恢复 Codex 数据独占文件的隔离树构建，确保没有夹带 `.05`；`.05` 再从主工作树构建并覆盖 `.04`。两版各自通过六项 ARM64 测试和 Radar 像素门禁，均完成 D/E 备份、覆盖和单实例重启。

## Final Acceptance

- 当前程序、项目规则和活文档版本为 `1.0.5.05`。
- 最终 Release/D/E SHA-256 一致：`E29CB0229FA7A4089706BB3EE3A2F0FBE0C54BDA0A7A2264A769862C09427ADD`。
- 跨重启缓存签名验收通过，部署后没有新错误日志。
- 功能索引、接口/资源索引、刷新规则、缓存技术文档和架构文档已同步。
- 只构建和部署 ARM64；按项目规则未构建 x64。

## Status

Consolidated Spec 的拆分实施、集成、文档与正式验收完成，最终版本 `1.0.5.05`。
