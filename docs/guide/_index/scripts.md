<!-- inventory-fingerprint: fe67a2a0379d4d23 -->
# 脚本台账（`scripts/`）

> 本文件由 [`scripts/script-inventory.ps1`](../../../scripts/script-inventory.ps1) **生成**，不要手改；
> `pwsh scripts/script-inventory.ps1 -Check` 在 CI 里校验它与脚本目录一致（新增脚本后没重新生成即红）。
> **目录制度与已退役脚本见 [`scripts/README.md`](../../../scripts/README.md)**——退役清单只有那一处。
>
> **类别是按证据推导的**，不是逐条人工标注：在 CI 里跑 → 门禁（CI）；`check-` / `verify-` 前缀 →
> 门禁（本地按需）；一次性前缀（`adr*` / `batch-` / `clean-` / `restore-` / `rollback-` / `migrate-` /
> `backfill-` / `materialize-` / `metadata-disposition` / `compare-` / `analyze-`）→ 一次性批次；其余 → 工具。
>
> **作用**取自脚本自身：优先 `.SYNOPSIS`，其次文件头 `<# … #>` 块，再其次文件头 `#` 注释。
> **引用**是"CI / 手册 / 技能 / 其它脚本里提到过它的文件名"的点数——扫描范围排除 `docs/plans/`（不随仓库
> 分发）与本台账自身；它是**生成时刻的快照**，随时可能因别处提一句而变，不必为它重生成。
>
> 文件开头的 `inventory-fingerprint` 是**造册语义**的指纹（脚本集合 / 类别 / 说明 / 锚点 / 独立工具状态 /
> 白名单 / 无人引用集合 / 退役集合），`-Check` 比的就是它——**不含**引用点数与时间戳，
> 否则一次无关提交或别处的一句提及就会让 CI 乱红。
> **一次性脚本用完即废**：新的一次性脚本落 `scripts/oneshot/`（目录即状态，可整目录清空），头部写一行
> `# 一次性｜锚点：<迁移 NNN / ADR-0xx / 模块号>`；脚本根目录的历史一次性脚本按数量棘轮执行（只减不增）。

## 一、总览

- 入库脚本 **87** 个：门禁（CI） 15 · 门禁（本地按需） 19 · 一次性批次 21 · 工具 32
- 说明缺失（三种头注释都没有）：**0** 个
- 非标准头（有说明但不是 `.SYNOPSIS`，`Get-Help` 取不到）：**0** 个
- 无人引用（不在 CI / 手册 / 技能 / 其它脚本里）：**13** 个，见第七节
- `scripts/oneshot/` 待清理的一次性脚本：**5** 个，见第二节
- 本机不入库的验收脚本（不入库）：`EOS.API.Tests/*.ps1` 等 **66** 个——被 `.git/info/exclude` 排除，
  别人的克隆里没有它们，故不参与上面的引用统计。

## 二、一次性脚本落点（`scripts/oneshot/`，用完即删）

这里的一次性脚本随时可以整目录清空，不影响任何门禁。**锚点**说明它随哪件收口完成而失效；
锚点指向的迁移/收口一旦完成，脚本就该删掉（正文在 git 历史里，不必留在工作区）。

| 脚本 | 锚点 | 作用 |
|---|---|---|
| `gen-numeric-batch-migration.ps1` | ADR-038（各批 | 一次性脚本：为指定业务域生成分片的类型迁移 SQL。 |
| `numeric-batch-prescan.ps1` | ADR-038（各批类型迁移前的溢出预扫描） | 一次性脚本：指定业务域的 float 列溢出预扫描，只读不写。 |
| `numeric-role-registry.ps1` | ADR-038 | 一次性脚本：推断 float 列的语义族并生成迁移 357（随 ADR-038 收口失效） |
| `rebuild-workbench-snapshots.ps1` | 迁移 | 重建全部工作台快照：备份当前快照 → 清空版本历史 → 全量重新发布。 |
| `sync-bootstrap-numeric-types.ps1` | ADR-038 | 一次性脚本：把建库基线里的数值列类型同步为语义族的 decimal。 |

## 三、门禁（CI 自动跑，不需要你动手）

| 脚本 | 作用 | 依赖 | 引用 |
|---|---|---|---|
| `check-agent-skills.ps1` | Agent 技能门禁：frontmatter 名与目录名一致、引用资产存在、引用资产未陈旧。 | git | 5 |
| `check-assistant-action-limits.ps1` | 助手动作阈值门禁：阈值只声明一次（业务代码里不得写死），红线开关恒为开且覆盖尝试启动即失败。 | 仅静态 | 7 |
| `check-assistant-action-registry.ps1` | 助手可代理动作注册表门禁：六要素齐全、动作名唯一、且注册表内不含职权类动作。 | 仅静态 | 7 |
| `check-commit-msg.ps1` | 提交信息门禁：拦「只有本次会话才看得懂」的内容——对话上下文、任务条目、验证流水。 | git | 8 |
| `check-css-classes.ps1` | 前端样式类门禁：JSX 里用的 erp-* 类名必须在 CSS 里有定义（禁止悬空类名）。 | 仅静态 | 5 |
| `check-data-layer-boundary.ps1` | 分层边界门禁：EOS.API/Data/ 根目录的 .cs 文件集合不得新增（白名单棘轮，只减不增）。 | 仅静态 | 7 |
| `check-di-registrations.ps1` | DI quality gate: every controller dependency must be registered in Program.cs. | 仅静态 | 11 |
| `check-numeric-column-types.ps1` | 静态门禁：迁移脚本不得新增 float 列。 | 仅静态 | 4 |
| `check-page-shell.ps1` | 前端「页面外壳只有一套」门禁：JSX 里不得再手写字面意义上的壳。 | 仅静态 | 6 |
| `check-retired-db-objects.ps1` | 退役库对象门禁：已退役的库列 / 库对象不得再被生产代码与脚本引用。 | 仅静态 | 8 |
| `check-sensitive-terms.ps1` | 扫描文本文件中的敏感信息（真实企业名、客户名、个人身份信息），拦截其随代码提交扩散。 | git | 2 |
| `check-workbench-policy-single-entry.ps1` | 统一工作台策略层单入口门禁：授权/名单/幂等判定不得回到控制器，且策略服务必须是共享入口。 | 仅静态 | 5 |
| `gen-adr-index.ps1` | 决策记录索引：把 docs/decisions/ADR-*.md 抽成一张表（编号 / 标题 / 状态 / 日期 / 决策者 / 交叉引用），并守住它与决策目录一致。 | 仅静态 | 7 |
| `gen-code-map.ps1` | 代码地图：从源码生成「后端路由」与「前端页面」两张表，并守住它们与源码一致。 | 需 API 在线 | 6 |
| `script-inventory.ps1` | 脚本台账：把 scripts/ 下每个 ps1 的作用、类别、依赖与引用点汇成一张表，并守住"不写说明、无人引用"的堆积。 | 连库、需 API 在线、git、工具链 | 6 |

## 四、门禁（本地按需，多数需连库或服务在线）

| 脚本 | 作用 | 依赖 | 引用 |
|---|---|---|---|
| `check-base-data.ps1` | 基础数据一致性门禁：模块清单、建库种子与库内实际三者必须一致。 | 连库 | 4 |
| `check-button-remarks.ps1` | 自定义按钮备注巡检：MANUAL 行的 REMARK 必须是人话，不得为空、不得是写给开发者的编号。 | 连库 | 2 |
| `check-chooser-self-mapping.ps1` | 连库门禁：标了「数据仅可选入」（FIELDS.ONLY_CHOOSE=1）且可见的字段，必须有一个 启用中的数据源把**它自己**列进回填目标（RETURN_ITEMS），否则用户选完拿不到值。 | 连库 | 4 |
| `check-docs.ps1` | 文档陈旧检查：确认 docs/status.md 未长期未刷新、README 未复述里程碑、 活跃计划未长期未刷新、活跃文档未残留指向已归档文件的死链。 | git | 8 |
| `check-field-labels.ps1` | 字段元数据巡检：FIELDS 的字段中文名不得是占位文本，批核/结案状态位不得进入复合格。 | 连库 | 2 |
| `check-field-type-alignment.ps1` | 连库门禁：FIELDS.F_TYPE 必须与物理列的数值族一致（声明得比物理宽，脏值会绕过校验变成 500）。 | 连库 | 1 |
| `check-forbidden-problem-filter.ps1` | 拒绝出口门禁：保证 403 权限拒绝始终带结构化错误体。 | 仅静态 | 3 |
| `check-form-layout.ps1` | 模块级表单版式巡检：版式行不得悬空、越界或把不该藏的字段藏起来。 | 连库 | 5 |
| `check-lifecycle-columns.ps1` | 单据生命周期列结构巡检：状态位/建立组必须 NOT NULL DEFAULT，经办人/日期列必须保持可空。 | 连库 | 10 |
| `check-live-fixture-hygiene.ps1` | 真库用例夹具卫生门禁：写入只能落在用例**自己造**的键上。 | 仅静态 | 5 |
| `check-migration-journal.ps1` | 迁移脚本与 DbUp 台账的一致性门禁：幽灵名、重复行、未落库。 | 连库 | 5 |
| `check-module-filter-columns.ps1` | 声明可写的模块（`MODULES.UNIFIED_FORM_ENABLED` + `UNIFIED_FORM_EDIT_ENABLED`）的模块过滤条件（`MODULES.FILTER`）必须能被新记录满足。 | 连库 | 1 |
| `check-module-node-kind.ps1` | 菜单节点形态（dbo.V_MODULE_NODE）与形态外配置残留巡检。 | 连库 | 9 |
| `check-numeric-role.ps1` | 连库门禁：FIELDS.NUMERIC_ROLE 的完整性与一致性。 | 连库 | 3 |
| `check-required-columns.ps1` | 必填口径巡检：明细主键里「用户能填、库里却非空」的列必须标必填。 | 连库 | 3 |
| `check-snapshot-staleness.ps1` | 快照落后常态检查：已发布快照是否落后于"按当前配置重建的定义"，尤其是**没有任何标记提示**的那种。 | 需 API 在线 | 10 |
| `check-system-params.ps1` | 系统参数门禁：参数定义完整性 + 引用键存在性 + 归属模块合法性。 | 连库 | 4 |
| `check-validation-rule-parity.ps1` | ADR-016 S0 ① 校验规则口径核对：MODULE_VALIDATION_RULE ↔ 代码闭集 ↔ 当前快照 ↔ 运行时加载器。 | 连库、工具链 | 4 |
| `verify-permission-gate.ps1` | 权限门端到端验证：确认"没有该模块权限的账号"在真实 HTTP 上被拒（403；选择器类 403/404），而不是 200。 | 连库、需 API 在线 | 7 |

## 五、一次性批次（使命已完结，保留作留痕）

| 脚本 | 作用 | 依赖 | 引用 |
|---|---|---|---|
| `adr012-acceptance-ledger.ps1` | Recomputes the ADR-012 effect-engine acceptance ledger from logs/shadow reports. | 连库 | 7 |
| `adr012-dead-sproc-report.ps1` | ADR-012 旧过程"安全下线集"报告：按三方引用核查算出可以 DROP 的过程与必须保留的过程。 | 连库 | 3 |
| `analyze-button-coverage.ps1` | Inventory of legacy Web Forms page buttons that are candidates for the user-triggered custom-button mechanism described… | 仅静态 | 独立工具 |
| `backfill-chooser.ps1` | 选择器缺口回填（第一层业务复核推进）：对 AcceptanceSemantics.ps1 输出的 MISSING 选择器候选， 按同名列众数样例复制 CHOOSE_* 配置，生成幂等 SQL（update.sql EOS-13 节）。 | 仅静态 | 独立工具 |
| `batch-validate-form-rework.ps1` | ADR-006 步骤 5：存量统一表单白名单模块批量 validate，产出机器可读报告 + 豁免清单。 | 需 API 在线 | 独立工具 |
| `clean-e2e-fixture-residue.ps1` | 清理开发库 EOS.ERP 里的 E2E 夹具残留（按键删除，不按时间范围 + 表名裸删）。 | 连库 | 1 |
| `clean-placeholder-formula-rows.ps1` | 删除服务型效果下遗留的全空占位公式行，删除前把每一行**逐列**备份成可回退的数据文件。 | 连库 | 1 |
| `compare-form-layout-parity.ps1` | 模块级表单版式改造的「观感零变化」对拍：改造前抓基线，改造后逐模块比对。 | 连库、需 API 在线、工具链 | 1 |
| `compare-report-rowset-parity.ps1` | 报表归属迁移的「取数结果零变化」对拍：改造前抓基线，改造后逐张比对。 | 连库、需 API 在线 | 独立工具 |
| `gen-numeric-batch-migration.ps1` | 一次性脚本：为指定业务域生成分片的类型迁移 SQL。 | 连库 | 1 |
| `materialize-form-layouts.ps1` | 把「推导默认版式」落成真实的版式行（模块级版式退役字段级配置的前置数据搬运；服务端设计态读法生成，本脚本只做"读出来 → 原样回写"）。 | 需 API 在线 | 独立工具 |
| `metadata-disposition.ps1` | 阶段 1 元数据异常清单处置：生成逐条处置记录（disposition.csv）并对机械性 TYPE_MISMATCH 直接修复（F_TYPE 对齐物理列类型族）。 | 仅静态 | 独立工具 |
| `migrate-menu-filters.ps1` | 存量菜单过滤规则语法归一：把 MODULES.FILTER 中的旧系统写法更新为现代写法， 使前端构建器可识别、后端解析语义不变。 | 连库 | 独立工具 |
| `numeric-batch-prescan.ps1` | 一次性脚本：指定业务域的 float 列溢出预扫描，只读不写。 | 连库 | 1 |
| `numeric-role-registry.ps1` | 一次性脚本：推断 float 列的语义族并生成迁移 357（随 ADR-038 收口失效） | 仅静态 | 1 |
| `rebuild-workbench-snapshots.ps1` | 重建全部工作台快照：备份当前快照 → 清空版本历史 → 全量重新发布。 | 连库、需 API 在线 | 独立工具 |
| `restore-legacy-groundtruth-sprocs.ps1` | 还原"旧过程基准"过程：CompareAfterSave.ps1 用来做特征化的遗留保存后钩子。 | 连库 | 2 |
| `restore-placeholder-formula-rows.ps1` | 按清理脚本留下的备份文件，把占位公式行**逐列按原值**插回。 | 连库 | 1 |
| `rollback-report-attribution.ps1` | 回滚报表归属列（REPORT.M_IDX）的落库：删列 + 卸约束 + 撤销迁移台账行。 | 连库 | 独立工具 |
| `sync-bootstrap-numeric-types.ps1` | 一次性脚本：把建库基线里的数值列类型同步为语义族的 decimal。 | 仅静态 | 2 |
| `verify-adr024-remainder.ps1` | ADR-024 收尾批的落库与运行态复验（重启 EOS.API 之后跑）。 | 连库、需 API 在线 | 独立工具 |

## 六、工具（可重复使用）

| 脚本 | 作用 | 依赖 | 引用 |
|---|---|---|---|
| `audit-retention.ps1` | 统一审计留存与归档（ADR-005 §8）：DETAIL_JSON 30 天截断、AUDIT_FIELD_CHANGE 90 天归档、 AUDIT_EVENT 180 天归档（导出 JSONL 后清理）。 | 连库 | 2 |
| `auto-enable-batch.ps1` | 统一表单自动放量流水线：一批候选模块 → 预检 → 启用（写模块声明）→ 真实数据 CRUD 回路 （尽量完整、模拟真实业务数据）→ 语义/元数据/扫描核对 → 全量回归。 全绿则保留启用并出报告；任一失败则整批回滚并出差异报告（不静默修… | 需 API 在线 | 5 |
| `dev-services.ps1` | EOS 本地开发服务管理：EOS.API 一键启动、停止、重启、状态与日志。 | 需 API 在线、工具链 | 4 |
| `eos-sql-selftest.ps1` | eos-sql.ps1 托管回退路径的结果集回归自检。 | 连库 | 1 |
| `eos-sql.ps1` | 连接 EOS.ERP 的 sqlcmd 参数解析与执行入口（供本仓脚本复用）。 | 连库 | 44 |
| `export-bootstrap-baseline.ps1` | 重建迁移基线登记 `db/bootstrap/40_journal_baseline.sql`。 | 连库 | 2 |
| `export-bootstrap-metadata.ps1` | 从当前库重建元数据种子 `db/bootstrap/20_metadata.sql`。 | 连库 | 1 |
| `export-bootstrap-schema.ps1` | 从当前库重建结构种子 `db/bootstrap/10_schema.sql`。 | 连库 | 2 |
| `export-business-actions.ps1` | Exports the effect-engine configuration currently stored in EOS.ERP into the seed manifest. | 连库 | 2 |
| `export-config-snapshots.ps1` | 统一表单配置快照导出（D6 固化）：用固定 SQL 口径重新生成 logs/module-list.tsv / fields-config.tsv / modules-config.tsv / columns-config.tsv， 消除… | 连库 | 独立工具 |
| `export-diagnosis-golden-candidates.ps1` | Export diagnosis golden-set candidates from MODULE_VALIDATION_RULE (read-only). | 连库 | 1 |
| `export-sysdf-archive.ps1` | 把 dbo.SYSDF（旧操作日志表）整表导出为 CSV，供迁移 325 删表后考古备查。 | 连库 | 3 |
| `impact.ps1` | 改动影响面报告：给定本次改动文件，列出命中哪些手册篇目、必须同批跟改哪些登记面、该跑哪些验证。 | git、工具链 | 5 |
| `init-im-db.ps1` | EOS.IM local dev database initializer (dev-only helper): reads the ERP connection string from the local Codex config, s… | 连库 | 独立工具 |
| `module-config-fuzz.ps1` | D 层：自造"极端但合法"的模块配置，断言接口要么正常、要么带原因码拒绝，绝不 5xx。 | 连库、需 API 在线 | 2 |
| `probe-env.ps1` | 开工前环境预检：库可达性、API 端口与构建一致性、bin 产物是否落后于 HEAD。 | 连库、需 API 在线、git | 2 |
| `publish-workbench-snapshots.ps1` | 发布工作台定义快照（开发库自助发布，替代人工在 230101 点「发布配置」；走同一服务端接口：校验 → 版本递增 → 写快照 → 清脏）。 | 需 API 在线 | 3 |
| `register-field-relation-edges.ps1` | 单据关系效果边登记器（FIELD_RELATION 方案 A，迁移 058 后运行）：从业务动作的定位键派生并幂等登记 EFFECT 边。 | 连库 | 独立工具 |
| `regression.ps1` | EOS 一键回归护栏：单测 + 前端 lint/build/test + E2E 四链 + UI 冒烟。 | 需 API 在线、工具链 | 5 |
| `release.ps1` | EOS 发布脚本：跑门禁 → 校验版本一致性 → 升 version.json → 生成 CHANGELOG 节 → 产出清单； 定稿提交之后用 -TagOnly 打标签。**分两步是刻意的**：标签必须落在「CHANGELOG 定稿」那… | 连库、git、工具链 | 8 |
| `report-form-meta.ps1` | 统一表单切片模块字段矩阵报告：按模块输出主/明细表字段的 必填 / 默认值 / FORM_OPTIONS / 选择器 / 隐藏必填 / 只读联动 清单，供实施顾问逐表确认。 | 需 API 在线 | 1 |
| `report-inventory-imbalance-keys.ps1` | 逐键定性库存不平键（余额对流水累计），并回答「是否仍在扩大」。 | 连库 | 1 |
| `republish-all-whitelist.ps1` | 全量白名单模块重发布 + 快照差异清单（发布即重派生的验收工具）。 | 连库、需 API 在线 | 4 |
| `review-whitelist-candidates.ps1` | 统一表单放量候选预筛：合并 候选 CSV + 实时 MODULES 元数据 + SP 移植台账 + 当前可写模块（MODULES.UNIFIED_FORM_ENABLED / UNIFIED_FORM_EDIT_ENABLED）， 逐模块… | 连库 | 3 |
| `seed-business-actions.ps1` | 业务动作/校验配置一次性导入器（幂等，可重复执行）：按清单生成幂等 T-SQL 并单次执行，只写种子表。 | 连库 | 2 |
| `seed-dev-data.ps1` | EOS 开发/测试语义化测试数据种子（用户拍板 2026-08-23，决策清单 §17）。 | 需 API 在线 | 2 |
| `set-eos-env.ps1` | 把应用需要的凭据设成 User 级环境变量（配置里 ${VAR} 引用的真值来源）。 | 连库 | 3 |
| `set-mssql-mcp-env.ps1` | 为 MCP 的 SQL Server 服务器维护 User 级环境变量（连接串走 SQL 认证，密码不进仓库）。 | 连库 | 3 |
| `sweep-all.ps1` | 全系统广扫的单一入口：按顺序跑四层体检，逐层对比基线，最后汇总一份报告。 | 仅静态 | 1 |
| `sync-agent-skills.ps1` | Sync agent skills: create Cursor mirror junctions from the canonical .agents/skills/ (idempotent). | 仅静态 | 1 |
| `sync-guide-to-kb.ps1` | 开发手册与决策记录 → 知识库同步（按内容三分推送）。 | 连库、需 API 在线 | 9 |
| `test-migration-dryrun.ps1` | DbUp 迁移干跑：把迁移脚本放进一层事务里执行再整体回滚，验证语法与命中行数。 | 连库 | 5 |

## 七、无人引用的脚本（每条的白名单理由）

| 脚本 | 白名单 | 理由 |
|---|---|---|
| `analyze-button-coverage.ps1` | 已登记 | 旧 Web Forms 页面按钮盘点（参考系统分析），一次性 |
| `backfill-chooser.ps1` | 已登记 | 选择器缺口回填（存量推进），一次性 |
| `batch-validate-form-rework.ps1` | 已登记 | ADR-006 步骤 5 存量表单批量校验，一次性 |
| `compare-report-rowset-parity.ps1` | 已登记 | 报表归属迁移的取数对拍，迁移已完结 |
| `export-config-snapshots.ps1` | 已登记 | 统一表单配置快照导出（D6 固化），一次性 |
| `init-im-db.ps1` | 已登记 | EOS.IM 开发库初始化（读本机 Codex 配置里的连接串），dev-only 辅助，非通用流程 |
| `materialize-form-layouts.ps1` | 已登记 | 把推导默认版式落成版式行，字段级排布列退役的前置，已完成 |
| `metadata-disposition.ps1` | 已登记 | 元数据异常清单的阶段 1 处置，一次性 |
| `migrate-menu-filters.ps1` | 已登记 | 存量菜单过滤规则语法归一，一次性 |
| `rebuild-workbench-snapshots.ps1` | 已登记 | 迁移 436 框架表改名后重建全部工作台快照：先备份再清版本历史并全量重发布，保留作同类元数据大改后的重建手段 |
| `register-field-relation-edges.ps1` | 已登记 | 单据关系效果边登记器：与已退役的全量核验脚本成对，保留作重登记手段（保存 lint 只在保存时校验，不覆盖存量重登记） |
| `rollback-report-attribution.ps1` | 已登记 | 报表归属迁移的回滚脚本，保留作回退手段 |
| `verify-adr024-remainder.ps1` | 已登记 | ADR-024 收尾批的落库与运行态复验，使命已完结 |

## 八、待处置：门禁类脚本既不在 CI、也没在 `docs/guide/06` 里登记

这些脚本自称门禁，但**没有任何地方会跑到它**——要么把它登记进 CI 或 [06 篇](../06-测试与质量门禁.md)（说明它守什么、
怎么跑），要么确认其约束已失效并退役。本节**只提示不失败**：处置需要业务判断。

（无）

## 九、白名单条目复核（只减不增）

（无：白名单条目都还是"无人引用"状态）

