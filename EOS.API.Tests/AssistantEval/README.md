# 助手评估集（种子）

> M4 前置产出：`functional/`（检索）与 `business/`（回答）首批各 30 条种子问答。
> M7 补齐：`authz/`（越权，20 条，工具级回归 Runner 自动断言，通过率须为 0 泄露）与
> `inference/`（推断，20 条，答案需跨 ≥3 源 + 至少 1 个口径引用）。
> S5 补齐：`situation/`、`diagnosis/`、`action/`、`config/` 四层。

## 分层与跑法

**不新建第二套评测框架**：四层共用下面这一条命令，各层只加自己的种子文件与断言。

```bash
dotnet test EOS.API.Tests/EOS.API.Tests.csproj --filter FullyQualifiedName~AssistantEval
```

| 层 | 文件 | 断言什么 | 断言实现 |
|---|---|---|---|
| `situation/` | `seed-01.jsonl` | 摘要类别取自 `SituationDigestKinds` 闭集；打开即见声明 `zeroModel=true` | `AssistantEvalLayersTests.处境层的摘要类别来自代码闭集` |
| `diagnosis/` | `golden.jsonl` | 样本可追溯到元数据（`moduleId`/`stage`/`validationKey`/`message`）；证据段与 stage 搭配合法 | `AssistantEvalLayersTests.诊断层的黄金集可追溯到元数据` |
| `action/` | `seed-01.jsonl` | 动作在 `insert`/`update`/`delete` 之内；模块级原因码取自 `WorkbenchDenialCodes` 闭集 | `AssistantEvalLayersTests.动作层的原因码来自策略层闭集` |
| `config/` | `seed-01.jsonl` | 配置面在四个之内；不一致原因码取自 `ConfigConsistencyRules` 的三个码 | `AssistantEvalLayersTests.配置层的原因码来自不一致规则闭集` |
| `authz/` | `seed-01.jsonl` | 越权必须被拒（泄露 = 0 硬门槛） | `AssistantEvalRunnerTests` |
| `inference/` | `seed-01.jsonl` | 格式与必要来源声明（跨 ≥3 源） | `AssistantEvalRunnerTests` |
| `functional/` | `seed-01.jsonl` | 检索类种子（人工抽样） | — |
| `business/` | `seed-01.jsonl` | 回答类种子（人工抽样） | — |

## 行格式

- 通用：每行一个 JSON `{question, expected, sources[]}`。
- `situation/`：`{id, situation{moduleId,pageType,docNo,filters[],selection[],formDirty[]}, expect{kinds[],zeroModel}, note}`。
- `action/`：`{id, moduleId, action, rows[], expect{moduleCode}, note}`。
- `config/`：`{id, surface, effectKey, observed{}, expectCode, note}`。
- `diagnosis/`：`{id, moduleId, moduleName, stage, seq, validationKey, enabled, message, triggerSummary, suggestedClass, source, reviewed}`（由 `MODULE_VALIDATION_RULE` 枚举，**每条规则天然是一个"应当能被诊断出来"的样本**）。
- `inference/` 另带 `required_sources[]`；`authz/` 行格式为 `{tool, args, expect, note}`（`expect` ∈ deny/empty/all_only/ok）。

## 判定方式（诚实标注）

- 确定性项（越权拒绝、闭集对齐、格式与来源声明）进 `dotnet test` 自动回归，不达标阻断发布；其中越权泄露为硬门槛（=0）。
- 正确率与拒答率需 LLM-as-judge（judge 模型、rubric、季度校准均未就绪），现阶段为人工抽样
  （每轮 ≥30 条，结果落本目录留痕），暂不阻断发布。

## 种子说明（诚实标注）

- 本批为**代码 grounded 种子**：每条期望答案都能在 `sources` 指向的代码/迁移/文档中找到依据，
  由 Agent 按当前实现编写，不是顾问真实提问。
- **未达商用门槛**：上线前必须用顾问真实提问替换/扩充，推断集另需 ≥20 条跨 ≥3 源题目。
- 维护者（角色③管理员/实施顾问）每轮功能验收后沉淀真实失败样本；越权样本每次安全审查后补充。
