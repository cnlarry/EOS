# 更新日志

本项目遵循[语义化版本](https://semver.org/lang/zh-CN/)；版本真源是仓库根 `version.json`，
发布标签为 `v<version>`。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。

递增规则（详见 `docs/decisions/ADR-026-版本管理与发布流程.md` §2.6）：
新功能 → MINOR +1；缺陷修复与性能优化 → PATCH +1；重构/文档/构建 → 不单独涨；
破坏性变更 → `0.x` 期间用 MINOR +1 表达并在本节标注 **BREAKING**。

## [0.1.0] - 2026-09-29

**基线版本**：本版为版本治理的起点，不对应某一次功能交付。此前全部提交记录的变更叙事见
`git log`（提交信息一律采用 Conventional Commits）与开发手册 `docs/guide/`。

### Added

- 产品版本真源 `version.json`（后端程序集、前端产物、发布标签、本文件同源派生）。

### Changed

- 文件日志改为按「大小 + 天数」双门保留（默认 14 天），默认目录改为程序自身目录下的 `logs`。
- 排障关联键（correlationId）收敛为单一真源：异常出口与业务拒绝日志不再使用 `TraceIdentifier`。
- 文件日志写入前统一脱敏（`LogRedactor` 正式接入 JSONL 落盘管道）。
- 贡献指南补充架构决策记录（ADR）流程，并订正两处与现状不符的表述（提交信息规范、发布节奏）。

### Security

- 日志脱敏接入后，凭据形态内容不再原样落盘，为后续「一键日志打包」提供隐私前提。

<!-- 新增版本时在文件顶部（本行之上）追加一节，勿修改历史节。 -->
