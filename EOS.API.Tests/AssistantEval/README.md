# 助手评估集（种子）

> M4 前置产出：`functional/`（检索）与 `business/`（回答）首批各 30 条种子问答。
> M7 补齐：`authz/`（越权，20 条，工具级回归 Runner 自动断言，通过率须为 0 泄露）与
> `inference/`（推断，20 条，答案需跨 ≥3 源 + 至少 1 个口径引用）。

## 行格式

每行一个 JSON：`{question, expected, sources[]}`，与 ADR-011 附录 C 一致。
`inference/` 另带 `required_sources[]`（必要数据源清单，用于依据链完整性断言）；
`authz/` 行格式为 `{tool, args, expect, note}`（`expect` ∈ deny/empty/all_only/ok）。

## 判定方式（诚实标注）

- 确定性项（越权拒绝、引用存在性、推断题格式与必要来源声明）进 `dotnet test` 自动回归，
  不达标阻断发布；其中越权泄露为硬门槛（=0）。
- 正确率与拒答率需 LLM-as-judge（judge 模型、rubric、季度校准均未就绪），现阶段为人工抽样
  （每轮 ≥30 条，结果落本目录留痕），暂不阻断发布。

## 行格式

每行一个 JSON：`{question, expected, sources[]}`，与 ADR-011 附录 C 一致。

## 种子说明（诚实标注）

- 本批为**代码 grounded 种子**：每条期望答案都能在 `sources` 指向的代码/迁移/文档中找到依据，
  由 Agent 按当前实现编写，不是顾问真实提问。
- **未达商用门槛**：上线前必须用顾问真实提问替换/扩充，推断集另需 ≥20 条跨 ≥3 源题目。
- 维护者（角色③管理员/实施顾问）每轮功能验收后沉淀真实失败样本；越权样本每次安全审查后补充。
