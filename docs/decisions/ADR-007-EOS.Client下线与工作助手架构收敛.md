# ADR-007：EOS.Client 下线与工作助手架构收敛

- 状态：已接受（2026-08-25 用户拍板）
- 日期：2026-08-25
- 决策者：用户（Larry）+ 开发 Agent

## 背景

`EOS.Client`（WinUI 3 桌面客户端）承载了 Agent 对话、内部 IM（EOS.IM）、邮件任务（EOS.Mail）三大能力，
导致客户端过重、开发困难，且不符合用户实际操作习惯。用户决定现阶段彻底放弃 IM 与邮件功能，
下线 `EOS.Client`，把「工作助手」（原 Agent 对话的产品化形态）改在 `EOS.Web` 中实现。

同时，为只读问答保留的三进程 AI 链路（`EOS.Web` → `EOS.AgentRuntime.Host`(5290) → `EOS.Gateway`(5288) → 模型厂商）
是为「Agent 平台化」预制的编排能力，而平台化按 blueprint 被明确推迟到阶段 F。
Client 下线后该链路失去主要消费方，继续维护三个服务端进程属于过度设计。

本决策允许推翻既有约束，但**第 5 节的安全不变式不在推翻范围内**。

## 决策

### 1. 物理删除范围与 git 考古标记

删除前先打 tag 留存完整快照，再物理删除：

```bash
git tag archive/eos-client-im-mail <删除前commit> && git push origin archive/eos-client-im-mail
```

删除 commit message 注明「见 tag archive/eos-client-im-mail」。删除对象：

| 对象 | 内容 |
|---|---|
| `EOS.Client/` | 整个目录（含 IM UI、邮件任务 UI、AgentRuntime 进程内嵌入） |
| IM 后端 | `ImHub`、`ImHubService`、`/api/v1/im/*`、`/api/hubs/im`、`Data/Migrations/`（EOS.IM 迁移）、`ConnectionStrings:ImDatabase` |
| 邮件任务后端 | `MailTasksController`、`MailTaskRepository`、`MailTaskInputValidator`、`Data/MailMigrations/`（EOS.Mail 迁移）、`ConnectionStrings:MailDatabase` |
| `EOS.Gateway/` | 整个目录（5288 进程） |
| `EOS.AgentRuntime/` | 类库整个目录 |
| `EOS.AgentRuntime.Host/` | 宿主整个目录（5290 进程） |
| `EOS.Eval/` | 评估链路（评估集 CSV 可迁入 `docs/plans/` 或随删，见执行准则） |

数据库 `EOS.IM` / `EOS.Mail` 本体由 DBA 处理，代码侧清干净即可。
`dev-services.ps1` 收敛为仅管理 `EOS.API` 一个进程。

### 2. 工作助手入口：悬浮按钮 + 抽屉

- 入口为 `EOS.Web` 全局右下角悬浮球，快捷键 `Ctrl+/` 唤起；
- 点开后为右侧可停靠抽屉面板，两档宽度（360/520px），记住展开/收起状态，可最小化回悬浮球；
- 不采用宠物形态（遮挡数据、分散注意力、成本高）；不放菜单导航项（入口深、跳转丢失列表状态）；
- 抽屉挂在 `AppShell` 最外层，全局路由可用。

### 3. 上下文注入：分层 + 可见 + 可控

| 层 | 内容 | 方式 |
|---|---|---|
| 自动轻量 | 当前 `moduleId`、页面类型（列表/查看/编辑）、单号或选中行 key | 打开抽屉即注入，前端显示为可移除的上下文芯片 |
| 显式引用 | 正在编辑的未保存表单值、选中的明细行 | 默认不上传；提供「附上当前表单」按钮或字段级入口 |
| 会话内记忆 | 本次会话中用户说过的需求 | 常规多轮对话 |

配套规则：

- 编辑态默认只带「已填字段的结构摘要」，脏值须用户主动附上；
- 上下文芯片始终可见可移除，用户一眼知道助手知道什么；
- 前端实现 `contextProvider` 注册表：document-workbench 注册列表上下文、FormEditorPage 注册表单上下文，
  AssistantDock 从当前路由取 active provider，新页面按需接入不改 dock。

### 4. AI 链路收敛：助手是 EOS.API 的一个领域模块

**推翻「`EOS.API` 不承担 LLM 推理」约束（修订 ADR-002 相应条款）。**
该约束本意是防止 API 变成 Agent 编排平台；工作助手没有计划/确认点/恢复那套编排需求，
一条 ChatService 足够。换来的是：Cookie 认证、权限缓存、审计、结构化日志全部直接复用，
且消除服务间身份传递问题（原 AgentRuntime 身份传递技术债随之消亡）。

```
EOS.API/
├─ features/assistant/
│  ├─ AssistantController.cs        # /api/v1/assistant/sessions|messages|chat(SSE)
│  ├─ ChatService.cs                # 会话编排：组上下文 → 调模型 → 落库
│  ├─ Tools/                        # 受控工具（模型 function calling）
│  │  ├─ SearchRecordsTool.cs       # 包装 WorkbenchQueryComposer，走全套权限
│  │  ├─ GetRecordDetailTool.cs     # 同上，含 DATA_FILTER/EXEC_TAG/字段隐藏
│  │  └─ ToolRegistry.cs            # 白名单注册，schema 显式声明
│  └─ ModelAccess/
│     ├─ IChatModel.cs              # 供应商无关抽象（stream/messages/tool-call）
│     └─ DeepSeekChatModel.cs       # 唯一实现起步
```

流式响应走 SSE（比 SignalR 轻；IM Hub 已随 IM 删除，无历史包袱）。
成本统计（token/耗时）记入消息表，替代原 Gateway 用量汇总职责。

### 5. 安全不变式（不因本决策放松）

1. **API 是最终权限边界**：所有取数经 `CurrentUserContext` 重授权，
   成本/保密/DENY 字段、DATA_FILTER、EXEC_TAG 过滤照常生效；
2. **模型输出永不进入 SQL/表达式解析器**：模型只能调 `ToolRegistry` 白名单工具，
   业务数据以「内容」注入 prompt 并标注来源，做提示注入隔离；
3. **密钥只在服务端配置**（user-secrets/环境变量），日志与消息落库前过现有 redact 脱敏；
4. **每次 AI 调用写 AUDIT_EVENT**（复用阶段 4 统一审计）；
5. 高风险动作禁止仅凭自然语言确认（如「好的」放行），必须结构化确认界面。

### 6. 写能力分级：草拟 → 结构化确认 → 现有管线执行

工作助手不是只读机器人，目标是帮用户做具体工作。写路径按风险分级：

| 级别 | 说明 | 确认方式 |
|---|---|---|
| `read` | 查单据/查元数据 | 无需确认 |
| `draft` | 产出草稿/建议（默认级别） | 用户手动采纳 |
| `write` | 触发业务写入 | 结构化确认卡片（字段级 diff 预览） |
| `admin-write` | 元数据/菜单/配置变更 | 同上 + 会话需具备对应模块 CanSetup |

两条具体场景的执行边界：

- **帮业务用户录单**：助手产出的是「`SaveRecordRequest` 草稿」，前端渲染确认卡片，
  用户确认后经现有统一表单保存管线执行（`WorkbenchCommandHandler`：幂等键、必填/长度校验、
  金额复算、审计全部复用）。**助手不产生任何新的写代码路径**——AI 干的活是「替用户填表单」，
  不是「替系统写库」。
- **帮运维搭模块/元数据**：「加一个供应商联系人维护页」= 一批 `FIELDS`/`MODULES`/`SYSQL_DEFAULT`
  结构化记录。流程：读 `sys.*` 元数据与现有模块定义做参考（CanSetup 门内）→ 生成元数据变更集
  （**不是 SQL**）→ 预览（form-definition 试算）→ 确认后经现有 `FieldAdminRepository` /
  管理端点执行 → 自动触发 `workbench-definitions/validate|publish`。

### 7. 数据模型（DbUp 迁移，EOS.ERP 内，对象名全大写）

```
ASSISTANT_SESSION  (ID, USER_ID, TITLE, CREATED_AT, LAST_ACTIVE_AT)
ASSISTANT_MESSAGE  (ID, SESSION_ID, ROLE, CONTENT, TOOL_CALLS_JSON,
                    MODEL_NAME, PROMPT_TOKENS, COMPLETION_TOKENS,
                    ELAPSED_MS, CORRELATION_ID, CREATED_AT)
```

会话落库收益：换浏览器/机器不断线、管理员可审计、留存策略挂进 `audit-retention.ps1` 同款模式。

## 选择理由

- 三进程链路（Web → RuntimeHost → Gateway）服务于尚未出现的平台化需求，违反蓝图
  「条件触发、不提前实现」原则；Client 下线后维护成本纯亏；
- 推理进 API 消除服务间身份传递（原技术债）、复用整套认证/权限/审计/可观测性基建；
  只读问答到受控写草拟的复杂度远够不着独立编排器的门槛；
- 悬浮抽屉保留工作区可见性，「边看单据边问」是助手的核心价值；
  分层上下文让「问当前单据」零成本，同时避免隐式上传半截表单的观感与 token 浪费;
- 写能力复用现有确定性管线，AI 只产草稿不碰 SQL，阶段 D/E 的安全模式天然成立。

## 考虑过的替代方案

### 方案 A：保留 Web → RuntimeHost → Gateway 三层链路

优点：不动存量代码，未来接 Agent 平台顺滑。
未选择原因：两个进程失去主要消费方仍需运维；身份传递技术债仍在；平台化被推迟到阶段 F，YAGNI。

### 方案 B：删除 Client 但保留 Gateway 作为纯转发代理

优点：密钥集中保管，将来多供应商扩展方便。
未选择原因：为一个薄转发维护独立进程不值；`IChatModel` 抽象已隔离供应商细节，
将来真有多供应商/多租户计费需求时再拆独立服务（见复审条件）。

### 方案 C：AI 直接生成并执行 SQL（含元数据 DML）

优点：实现最省事。
未选择原因：违反安全不变式第 2 条；元数据表间强耦合（FIELDS/TABLES/SYSQL_* 引用完整性）
手工拼 SQL 极易产生幽灵字段——这正是过去多次清理过的事故模式。变更集必须由确定性代码翻译为参数化 SQL。

### 方案 D：宠物形态 / 菜单页导航入口

未选择原因：见决策 2。

### 方案 E：IM/邮件归档分支保留

未选择原因：git tag 已满足考古需求；长期分支会腐化且误导后续检索。物理删除最干净。

## 结果与影响

### 正面影响

- 解决方案运行时工程从 4 个收敛为 2 个（`EOS.API` + `EOS.Web`），部署/调试/回归面大幅缩小；
- IM/邮件两大重负卸除，`EOS.API` 删除约两个领域的控制器/仓储/迁移/HUB 代码；
- 工作助手获得完整的现成基建（认证、权限、审计、脱敏、结构化日志、Definition 校验）；
- 服务间身份传递技术债消亡。

### 成本与风险

- 密钥保管责任转移到 `EOS.API` 配置管理（原由 Gateway 承担），需确保 user-secrets/环境变量纪律；
- 「API 不做推理」的心智约束被打破，后续评审需警惕 API 内 assistant feature 膨胀成编排器
  （膨胀信号见复审条件）；
- 模型厂商 SDK/HTTP 细节进入 `EOS.API` 代码库（仅 `ModelAccess/` 子目录，抽象隔离）；
- 蓝图「两个界面一套系统」表述失效，相关文档需批量修订。

### 需同步修订的文档

`AGENTS.md`、`README.md`、`blueprint.md`、`docs/status.md`、`docs/架构.md`、
`docs/plans/即时通讯.md`（标注废弃）、`EOS.API/README.md`、`EOS.Web/README.md`、
`scripts/dev-services.ps1` 及各处提及 Client/Gateway/RuntimeHost 的 E2E 与测试。

## 执行准则（分期）

| 期 | 内容 | 退出条件 |
|---|---|---|
| M1 清理 | 打 tag → 删 Client/IM/Mail/RuntimeHost/Gateway/Eval → slnx/dev-services/文档同步 | lint/build/test 绿，仓库无残留引用 |
| M2 骨架 | IChatModel + 配置 + ASSISTANT_SESSION/MESSAGE 迁移 + SSE 抽屉（纯聊天） | 多轮对话、历史恢复、断流重连 |
| M3 业务感知 | contextProvider 注入 + SearchRecords/GetRecordDetail 工具 + 来源引用链接 | 「帮我找 XX 单」可用且越权测试通过 |
| M4 写能力 | draft/write/admin-write 分级 + 确认卡片 + 表单预填与元数据变更集 | 顾问试用验收 |

## 复审条件

出现以下情况时复审本决策：

- 出现第二个真实 AI 场景需要多供应商路由、租户级计费或独立扩缩容 → 重评独立网关服务；
- assistant feature 开始出现计划/多步确认点/定时自动化需求 → 重评独立编排器（即原 AgentRuntime 形态）;
- 需要服务端无人值守自动化（定时任务型 Agent）→ 届时按 ADR-003 §3 的服务凭据 + 用户委托方案重开；
- IM/邮件需求复活 → 从 tag `archive/eos-client-im-mail` 考古后另立新 ADR，不做原地复活。

> **〔2026-09-30 回填〕「assistant feature 出现计划 / 多步确认点 → 重评独立编排器」这一条的当前结论：
> ADR-016 明确不触发（论证沿用）。** ADR-016 把组织轴从"能力"换成"用户时刻"（处境 / 诊断 / 代办），
> 并明确**不建通用编排器**：多步任务只以「AI 发起操作请求卡 + 用户**逐行**勾选确认」的形态交付，
> 用户确认后**由前端直接调既有端点**（批核族在 AI 侧不存在的注册项）；写能力仍只经既有写管线。
> 本 ADR 决策 6 的 `write` 级（结构化确认卡片、复用现有确定性管线）由 ADR-016 的 S3/S4/S5 承接，
> 节奏按 ADR-016 的批次划分推进，安全不变式（§5）与宿主形态边界（ADR-016 决策 11）未放宽。
