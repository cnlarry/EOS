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
| `functional/` | `seed-01.jsonl` | **来源锚定**（`sources[]` 必须在仓库里真实存在）＋ 检索类种子（答案对错：人工抽样） | `AssistantEvalLayersTests.检索与回答层的样本来源锚定到代码或仓库` |
| `business/` | `seed-01.jsonl` | **来源锚定**（同上）＋ 回答类种子（答案对错：人工抽样） | `AssistantEvalLayersTests.检索与回答层的样本来源锚定到代码或仓库` |

## 行格式

- 通用：每行一个 JSON `{question, expected, sources[]}`。
- **可选字段（2026-10-31 起）**：
  - `expect_tools[]`：期望工具链。**必须是 `AssistantToolKeys.ToolNames` 里真实存在的工具**；
  - `page`：这条问句发生在哪类页面（`list` / `view` / `edit` / `new` / `copy` / `config-*`）。
    声明后，`expect_tools` 里的每个工具都必须在该页面类型下被 `AssistantToolSetPolicy` 发给模型
    （声明了 `page` 却没写 `expect_tools` 直接判错）；
  - `reviewed`：样本是否已由顾问/产品审阅。`true` 时**必须**同时具备 `expected` 与 `expect_tools`。
  这三条由 `AssistantEvalToolExpectationTests` 断言（含"批核族/权限授予类不得出现在 `expect_tools`"）。
- `situation/`：`{id, situation{moduleId,pageType,docNo,filters[],selection[],formDirty[]}, expect{kinds[],zeroModel}, note}`。
- `action/`：`{id, moduleId, action, rows[], expect{moduleCode}, note}`。
- `config/`：`{id, surface, effectKey, observed{}, expectCode, note}`。
- `diagnosis/`：`{id, moduleId, moduleName, stage, seq, validationKey, enabled, message, messageSource, triggerSummary, evidenceSource, suggestedClass, source, reviewed, reviewedBy, reviewedAt}`（由 `MODULE_VALIDATION_RULE` 枚举，**每条规则天然是一个"应当能被诊断出来"的样本**）。`reviewed` 表示用户是否已逐条审阅；`true` 时必须带 `reviewedBy` 与 `reviewedAt`（`yyyy-MM-dd`），由 `DiagnosisGoldenSetTests` 断言。
- `inference/` 另带 `required_sources[]`；`authz/` 行格式为 `{tool, args, expect, note}`（`expect` ∈ deny/empty/all_only/ok）。

## 判定方式（诚实标注）

- 确定性项（越权拒绝、闭集对齐、格式与来源声明）进 `dotnet test` 自动回归，不达标阻断发布；其中越权泄露为硬门槛（=0）。
- **来源锚定**（2026-09-30 补）覆盖 `functional/` 与 `business/`：每条样本的 `sources[]` 必须在仓库里真实存在——
  模块映射已登记（`ModuleBusinessMap`）、工具名在工具源码里被声明过、文件在仓库里、类型或成员在 `EOS.API`
  程序集里、库对象名/错误码能在业务代码与 SQL 里找到。它判的是"**样本没有自造依据**"，
  **不是**"模型的自然语言回答对不对"——后者仍需 judge，这一层只是把人工抽样前的"依据可核对"自动化。
- 正确率与拒答率需 LLM-as-judge（judge 模型、rubric、季度校准均未就绪），这两层的**答案对错**现阶段为人工抽样
  （每轮 ≥30 条，结果落本目录留痕），暂不阻断发布。
- **期望工具链**（2026-10-31 补）：`expect_tools` + `page` 让"这条问句该用哪个工具、那个工具在这页上摆不摆得出"
  变成离线可判定的断言——工具改名、被页面策略裁掉、或样本把批核族写进期望，都会当场红。
  它判的仍是**样本自身一致性**，不是模型的选择是否正确（那要真跑模型 + judge）。

### 顾问问句怎么进来

真实问句先落本机候选池（`docs/plans/assistant-real-questions-*.jsonl`，`origin=agent-smoke` 表示
由开发方冒烟产生、**不是**顾问提问）。顾问审阅时给每条补 `expected`、`expect_tools`（必要时补 `page`），
`reviewed` 置 `true`，再把该行移入本目录对应层；移入后跑一次
`dotnet test --filter FullyQualifiedName~AssistantEval` 即可确认没有漏填与漂移。
- 锚定的实际收益（本次抓出并订正 5 处）：1 条**空来源**（`[""]`）；2 条把依据指向**项目外**的遗留实现
  （`Admin.GetUserRightDetail` / `Admin.GetRepeat`，实为 `Right.cs` 的参考语义，已改指 `ModuleRightsRepository` /
  `RightsAdminRepository`）；2 条用**中文描述**当依据（`删除补偿守卫`，已改指
  `WorkbenchApprovalService.EnsureDeletionAllowedAsync`）。

## 种子说明（诚实标注）

- 本批为**代码 grounded 种子**：每条期望答案都能在 `sources` 指向的代码/迁移/文档中找到依据，
  由 Agent 按当前实现编写，不是顾问真实提问。
- **未达商用门槛**：上线前必须用顾问真实提问替换/扩充，推断集另需 ≥20 条跨 ≥3 源题目。
- 维护者（角色③管理员/实施顾问）每轮功能验收后沉淀真实失败样本；越权样本每次安全审查后补充。
