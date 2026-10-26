# EOS 系统现状（唯一现状事实源）

> 本文件是项目**当前状态**的唯一权威来源：基线、近期收口、进行中、待决策、技术债都只在这里维护。
> 历史细节不再在本文复述，一律以 git 历史与维护者本机的内部归档为准。
> 文中「内部资料 / 内部归档 / 本机证据」指引的是**维护者本机**的内部文档与运行输出（不入库、不随仓库分发），不是本仓库中的路径。

## 1. 一句话现状

EOS 现代化重构已进入「**全面替代收尾 + 长尾验收**」阶段：核心框架（工作台/统一表单/报表/打印/权限/工作流/SP 等价移植）已闭环，
剩余以逐模块业务验收、元数据复核与少量专项深化为主；AI 试点与客户端/Agent 属重构完成后另立项。

## 2. 当前基线（各行为其证据日期，主体实测于 2026-08-19）

> 下表的数字是**最后一次实际测量的读数**，以各行括注的日期为准；未重新测量前不猜测、不重写。
> 已知此后有变化的项（如工作台白名单）在行内注明；要刷新请重跑对应证据并同步本表。

| 项 | 值 | 证据 |
|---|---|---|
| 模块总账 | 550（472 可操作） | 内部归档《替代进度报告-2026-08-16.md》（归档基线） |
| 统一表单：可写模块 | **259**（口径于 2026-10-16 变更：迁移 355 把"能不能写"从 `appsettings.json` 的两个名单搬进 `MODULES.UNIFIED_FORM_ENABLED` / `UNIFIED_FORM_EDIT_ENABLED`，现读、免重启；同日实测 **263 个启用表单 = 259 可写 + 4 仅浏览**（审计 2316 / 库存余额 1303 / 批次账 1302 / 应收未收明细 14997）。历史读数同口径：2026-10-09 迁移 341 曾把 2315 加入名单、342 改成定制承载页后移出；2026-10-08 已退役模块 2308 的残留 ID 已清；261 系 2026-08-28 2205 归类定制页移出；2026-08-27 2101/2305/2306 归类特殊页移出；2026-08-23 六模块放量；P1 元数据治理后 1304/110308 放量；2201-2204 特殊管理页仍剔除） | `dbo.MODULES` 两列（`EOS.API/Data/Workbench/UnifiedFormCapability.cs` 是唯一读取点；门禁 `check-module-filter-columns` 实测 259、`check-module-node-kind` 实测 263） |
| 验收扫描 | 143/143 全过、候选修复 0 | 本机证据：acceptance-scan/（2026-08-19 重跑） |
| 元数据审计 | 144 模块/207 表、异常 385 条、**需人工 0 条** | 本机证据：goal/phase1/metadata-exceptions.csv |
| 语义复核 | 643 候选 = MATCH 638 + HIDDEN_READONLY 3 + DIFF 2（SEX 已拍板不改） | 本机证据：goal/phase1/semantics-candidates.csv |
| 切片模块 CRUD | 7/7 全过（1209/1305/1414/1501/1901/1905/180202） | 本机证据：goal/phase1/crud.json |
| 放量候选预筛 | 146 候选（1415 已下线）；**单表批 30 成功、主子表批 34 成功、AUTO_APPROVE/FILTER 批 51 成功、数据前置批 6 成功（2026-08-23）、元数据治理批 2 成功（1304/110308，2026-08-23）**；剩余仅 2201-2204（特殊管理页剔除，非统一表单）；**二次预筛（2026-08-23）：候选-2 共 7 项全部为已实现特殊页（2201-2204/2504/2506/180218），无新增可放量候选**（`统一表单白名单候选-2.csv`、`whitelist-review-20260823-130600`） | 本机证据：auto-enable/（20260819-222142 等） |
| SP 移植台账 | 108 行全部有结论（107 移植 + 2306 决策 A），字段收敛已实施 | 内部资料《sp-porting-inventory.csv》 |
| 回归 | 4/4 全绿（单测 + 前端 + E2E 四链 + UI 冒烟） | 本机证据：goal/regression/regression-20260826-212709.json（2026-08-26 刷新，修复 E2E 业务链 UNIT_ID 回归） |
| 单据版式 | 24/24 PDF + 24/24 程序化体检 | `EOS.API.Tests/VerifyPrintLayouts.ps1`、`scripts/check-print-layouts.py` |

## 3. 近期收口（自 `v0.2.0` 起）

> 2026-08-16 ~ 2026-10-03 的往期收口已随 `v0.2.0` 出货，逐条移入本机存档
> 内部归档《status-流水账-2026-08-31至10-03.md》（不随仓库分发）。
> `v0.2.0` ~ `v0.4.0` 周期（2026-10-03 ~ 2026-10-13）的收口行已随 `v0.5.0` 出货，
> 移入本机归档 `docs/plans/archive/status-流水账-2026-10-03至10-13.md`（同样不随仓库分发）。
> `v0.5.0` ~ `v0.6.0` 周期（2026-10-13 ~ 2026-10-16）的收口行已随 `v0.6.0` 出货，
> 移入本机归档 `docs/plans/archive/status-流水账-2026-10-13至10-16.md`（同样不随仓库分发）。
> `v0.6.0` ~ `v0.7.0` 周期（2026-10-16 ~ 2026-10-19）的收口行已随 `v0.7.0` 出货，
> 移入本机归档 `docs/plans/archive/status-流水账-2026-10-16至10-19.md`（同样不随仓库分发）。
> `v0.7.0` ~ `v0.8.0` 周期（2026-10-19 ~ 2026-10-22）的收口行已随 `v0.8.0` 出货，
> 移入本机归档 `docs/plans/archive/status-流水账-2026-10-19至10-22.md`（同样不随仓库分发）。
> 本节只保留**当前版本周期**内的收口，发布即归档。

| 事项 | 证据 |
|---|---|
| **生命周期列命名收敛落地（ADR-043，2026-10-26）**：物理列、字段元数据与配置引用一次改齐，改名走 `sp_rename`（`ERP_SCHEMA_JOURNAL` 未变、数据与索引/默认约束随列对象走）。**实测**：物理列 **2052 列 / 309 张表**（`MODULE_BUSINESS_ACTION.CONFIRM_TAG` 按 ADR 明确排除——那是自定义动作的"点击前确认"配置位）；`FIELDS.F_ID` 1958 行 + 标签按 §2.3 归一（`AUDIT_EVENT.CREATED_TIME` 仍叫"落库时间"）；`SYS_DEFAULT_COLUMN` 1767 / `SYS_USER_COLUMN` 1616 / `SYS_USER_CHOOSER_MEMORY` 5 行的 `F_ID`；`MODULE_FORM_LAYOUT` 删 **2047** 行生命周期行（ADR-042 起它们本就不参与版式）；10 个视图 + `P_HRM_WAGE_CALC` 重建（`ALTER VIEW/PROCEDURE`）。**ADR 漏记的引用面已一并处理**：`MODULE_BUSINESS_ACTION_OP` 的 `TARGET_FIELD`(236)/`SOURCE_FIELD`(7)/`CONDITION_STRUCT`(108)、`REPORT_METRIC.ROW_FILTER`(8)、`REPORT_SORT` 分组排序(28)、`MODULE_GROUPS.GROUP_EXP`、`TABLES.DF_CONDITION`、`SYS_USER_MODULE_PERMISSION.DATA_FILTER`。**历史审计不改写**（`AUDIT_FIELD_CHANGE`、`AUDIT_EVENT.DETAIL_JSON` 保留旧名）。改名把框架表的新列名带进了 `check-lifecycle-columns` 的建立组口径，故补迁移 **452** 把 `FIELD_DATASOURCE`/`KB_DOCUMENT`/`ASSISTANT_PROVIDER|MODEL` 的 `CREATED_BY` 与 `AUDIT_EVENT.CREATED_TIME` 补齐 NOT NULL + DEFAULT。代码/脚本/夹具同步（含 `EOS.API.Tests/fixtures/baseline-sprocs` 这批**会被执行**的旧过程正文）；`db/bootstrap` 三件套按库重新导出（`20_metadata.sql` 顺带补上迁移 444 的模块 34「查询中心」，该行此前只是种子陈旧）。**验证**：后端 **2771/2771**（含真库集成）、前端 **1327/1327**、`lint`/`build` 通过；`check-lifecycle-columns`/`check-field-labels`/`check-required-columns`/`check-form-layout`/`check-module-filter-columns`/`check-master-amount-rollup`/`check-system-params`/`check-base-data`/`check-retired-db-objects`/`check-migration-numbers`/`check-migration-journal`/`check-di-registrations`/`check-css-classes`/`check-page-shell` 等 PASS。**未做（ADR 明确可选）**：329 个默认约束名、3 张表的旧列索引名仍带旧列名（不影响列名与运行）。**重启后收尾实测（2026-10-26 02:12 重启，commit `a780852`）**：`republish-all-whitelist.ps1` 重发布 **258 个模块**（漂移 258 / 无漂移 4 / 失败 0），`check-snapshot-staleness` **262/262 fresh**，`IS_CURRENT=1` 快照**含旧字段名 0 份**（历史 361 份按 ADR §4.5 保留旧名）；`SmokeEndpoints` **145 PASS / 0 FAIL**；接口实测 1606 记录返回 `CREATED_BY/CREATED_TIME/MODIFIED_*/APPROVED_*/APPROVED/FINISHED`，`form-definition?mode=view` 的 `lifecycleFields` 为 10 列、`masterFields` 已剔除；界面实测 1606 单据浏览页工具栏状态徽标「已批核 · 管理员 · 2026-10-14 11:16」+ 主卡底部「单据与经办信息」四组齐备；单据版式 **24/24 PDF**。**一处验证脚本坑（非产品问题）**：`VerifyPrintLayouts.ps1` 直接 `SELECT TOP 1` 取样会取到模块 `MODULES.FILTER` 之外的行，打印读取叠加该谓词后必然 404（首次跑出 19/24 即此原因，按过滤条件取样后 24/24），已修本地脚本并记入 `LESSONS.md` L127 | `docs/decisions/ADR-043-生命周期列命名收敛.md`、`EOS.API/Data/Migrations/{451,452}*.sql`、`EOS.API/Data/WorkflowStates.cs`、`EOS.Web/src/features/document-workbench/lifecycleKeys.ts`、`db/bootstrap/{10,20,25,40}*.sql`、`LESSONS.md` L123–L126 |
| **部署与运维篇落地 + 未登录访问边界修复（2026-10-25）**：新增 [`docs/guide/75-部署与环境配置.md`](guide/75-部署与环境配置.md)（唯一受支持形态=单实例+同源+HTTPS；产物与必须随包的资产；配置项清单与真值来源；首次部署；升级与回滚口径；备份三件套=库/附件/DataProtection 密钥；健康检查与 8 项上线验收；日志与磁盘；**多实例不支持的三处进程内状态**；例行运维；故障入口）。取证时抓到一个**会让生产形态首次访问就失败**的缺陷：SPA 外壳挂了 `RequireAuthorization()`，而 Cookie 登录跳转指向从 SPA 接管界面起就不存在的 `/login.html` ⇒ 未登录打开任意深链是 `302 → 自己`，浏览器 `ERR_TOO_MANY_REDIRECTS`（开发形态由 Vite 托管前端，看不出来）。已改 `Program.cs` 两处（外壳 `AllowAnonymous()`、跳转指向 `/login`），权限边界仍在 `/api`；新增 `EOS.API.Tests/E2eSpaShellAuth.ps1`（关自动跟随跳转的匿名探针，并入 `E2eAll.ps1`；`EOS.API.Tests/*.ps1` 是本机资产、不入库），教训记 `LESSONS.md` L122（含"只删 `RequireAuthorization()` 不够，`FallbackPolicy` 会接管无授权元数据的端点"这条坑）。**重启后已实测**：`/workbench/1401` 与 `/login` 未登录不再 302；临时放入 `index.html` 复现部署形态后深链返回 **200 + text/html**、`/login.html` 一跳 `/login` 后 200（无环），探针文件已删；`E2eSpaShellAuth.ps1` 全绿（`/api` 仍 401） | `docs/guide/75-部署与环境配置.md`、`docs/guide/_map.md`、`EOS.API/Program.cs`、`EOS.API.Tests/E2eSpaShellAuth.ps1`、`EOS.API.Tests/E2eAll.ps1`、`LESSONS.md` |
| **4 个「金额汇总与旧过程一致」真库用例随迁移 447 失修（2026-10-25 修复）**：`CopAccountCatalogLiveTests`（170101/170103）、`PurchaseDueCatalogLiveTests`（170201）、`PurPrepayCatalogLiveTests`（170203）原先读模块的 `detail-rollup` 动作再跑它——170103/170203 的动作已被迁移 447 **整条移除**（框架已覆盖），170101/170201 只剩 `QTY_TOTAL`/`SUM_AMOUNT` 两条赋值，于是四条用例报「模块缺少 SAVE 期动作」或数值对不上。**四条用例不是被删、也不是被放宽**：改为「框架原语 `MasterAmountRollup` 先跑，动作后跑」，断言的仍是"结果与旧过程语句一致"的值（170101/170201 仍是 `(150.75, 19.6, 170.35, 171.6, 13)`、170103/170203 仍是 `3.333`），并加了"原语必须发现到列"的前置断言，发现不到即红。修复后真库全套 **2758/2758 全过**（此前 2754/2758）。**这类漏检的机制**：CI 只跑离线用例，真库用例没人跑就没人发现配置面改了而用例没改 | `EOS.API.Tests/CopAccountCatalogLiveTests.cs`、`EOS.API.Tests/PurchaseDueCatalogLiveTests.cs`、`EOS.API.Tests/PurPrepayCatalogLiveTests.cs`、`EOS.API/Data/Workbench/MasterAmountRollup.cs`、`EOS.API/Data/Migrations/447_effect_rollup_convergence.sql` |
| **统一表单生命周期字段收拢（ADR-042，2026-10-25）**：建立/修改/批核/结案 10 列不再参与版式，浏览态改为工具栏状态徽标 + 主卡底部固定区（无结案能力只显示批核、三组等分；有结案能力四组等分）；设计器字段池/画布移除、固定区预览；空页签运行态不渲染。**覆盖库内 258 个带生命周期行的模块：71 个有结案能力（四组）、187 个无结案（三组且只显示批核），73 个空页签不再渲染**。`FormDefinition` 增 `LifecycleFields`/`HasFinishCapability`，记录读取补生命周期列；存量版式行读取忽略、保存丢弃，不做数据迁移。前端 **1327** 测试全过、`lint` 0 error、`build` 通过；后端全量 **2770** 测试全过（含真库集成）；`check-css-classes` / `check-form-layout` / `check-lifecycle-columns` / `check-field-labels` / `check-required-columns` / `check-docs` / `check-di-registrations` PASS。**运行实例仍跑旧版本**，需重启 EOS.API 后验收界面 | `docs/decisions/ADR-042-生命周期字段的呈现收敛.md`、`EOS.API/Models/WorkbenchModels.cs`、`EOS.API/Data/WorkbenchDefinitionBuilder.cs`、`EOS.API/Data/Forms/`、`EOS.Web/src/features/document-workbench/{LifecycleInfo,lifecycleGroups}.tsx`、`EOS.Web/src/features/form-designer/` |
| **统一表单工具栏改纯图标（2026-10-25）**：与统一工作台列表工具栏同款 `ErpCommandBar` `iconOnly` + `.erp-command-icon-btn`（28×28 方形圆角）。保存/取消、浏览态全部单据动作、附件、弹窗「更多」都改为纯图标；动作名只作 `title` 悬停提示与 `aria-label`，弹窗菜单项仍带文字。`FormEditorPage` 现有测试新增纯图标断言（按钮无文字、无 `.erp-command-btn`），既有按可访问名/顺序的用例改读 `aria-label`。`AGENTS.md` 与 03/40/42 篇同步 | `AGENTS.md`、`docs/guide/{03,40,42}`、`EOS.Web/src/components/common/ErpCommandBar.tsx`、`EOS.Web/src/features/document-workbench/FormEditorPage.tsx`、`EOS.Web/src/features/document-workbench/FormEditorPage.test.tsx`、`EOS.Web/src/styles/app.css` |
| **v0.9.0 出货后第一次全量体检 + 广扫 B 层造值补 `FIELDS.REGEX`（2026-10-25）**：A 层全量 **281 模块 / 成功对拍 258 / FAIL 0 / 观察项 0**（940s，基线仍为空）。B 层原卡在 `REGEX_MISMATCH` 的 21 个考勤/工资模块（时间 `HH:mm`、月份 `yyyyMM`）改为**按字段正则从候选表筛值**，定向复跑 **20/21 走完全环**；**全量复跑（含负面边界）281 模块 / 走完回环 122 / FAIL 0（相对基线新增 0）/ 观察项 509**，14 个边界因子全部覆盖；`DETAIL_PRUNED_BY_SIBLING` 只在 180310/1803101 命中（2 条，info），证明这条豁免没有外溢。仍走不完的 16 个模块全是"缺前置单据链"（制令单/采购订单/模具等），属业务前置、不硬凑；唯一有明确数据前置的 180206 拒因逐字为"未生成 202610 月度出勤参数"。**纪律**：广扫与离线单测都写库，禁止并发——首跑把 `1405` 打成 500 就是两条同时段写库撞出的 `SqlException 1205` 死锁（`ActionDryRunTests` 断言审计 `MAX(EVENT_ID)` 不增长，也会因此必红）；分开跑后同一模块 PASS | `scripts/record-roundtrip.mjs`、`scripts/sweep-all.ps1`、`logs/sweep-all/sweep-all-20261025111305.md`、`docs/guide/06-测试与质量门禁.md` |
| **发布 v0.9.0（2026-10-24）**：标签 `v0.9.0` 打在 CHANGELOG 定稿提交 `eaa0de8` 上（Release https://github.com/cnlarry/EOS/releases/tag/v0.9.0 ，附件 MANIFEST.txt + SHA256SUMS.txt）；版本 0.8.0 → 0.9.0（自 `v0.8.0` 起 31 条提交含 3 条 feat，按 ADR-026 §2.6 走 MINOR），**本版含迁移 446–449（共 4 个）**，部署时由 EOS.API 启动自动执行。本版主要交付：报表绑定开单公司（ADR-041，迁移 449）/ 报表中心与查看页界面重构（三区磁贴、单卡工具栏、条件与打印弹窗、页签显示报表名）/ 主表金额汇总按元数据发现金额列（迁移 446–447）。**发布门禁实况**：本地先跑 CI 等价全套——`dotnet build EOS.slnx -c Release` 0 错误、API 离线测试 **2076** 条全过、前端 `lint`/`build`/**1307** 条测试全过；CI run `40106015488`（提交 `b123b9c`）与 `40106281385`（发布提交 `eaa0de8`）三个 job 全绿；`release.ps1 -Bump auto -BuildOutput` 跑完编译与静态门禁，`-TagOnly` 校验迁移台账 448 个脚本、CHANGELOG 已定稿、标签未占用；**未使用 `-SkipBuild` / `-SkipMigrationGate`**。手册新鲜度非阻断、**落后 18 篇**，均为映射目录宽导致的既有机械落后，本篇涉及的 03/50 已同步；**运行实例仍跑旧版本**，需重启 `EOS.API` 使 `/health/version` 与 v0.9.0 一致。下个版本判定依据：最新标签 `v0.9.0`，其后提交按类型递增（`feat`→MINOR、`fix`/`perf`→PATCH） | `CHANGELOG.md`、`version.json`、`README.md`、`artifacts/release-0.9.0/`、`EOS.API/Data/Migrations/{446,447,448,449}*.sql`、`.github/workflows/ci.yml` |
| **报表中心与查看页界面重构 + 页签标题修复（2026-10-24，随 v0.9.0）**：报表中心收藏、最近使用、展开的业务域统一为自适应磁贴网格（名称 + 模块 + 编号/最近时间 + 默认标记），去掉整行只有报表名的长条；报表查看页把标题、报表切换、查询/打印/返回收进结果卡工具栏，条件与打印设置改弹窗，页根占满且只在表格内滚动。页签标题改由页面经 `usePageBreadcrumb` 上抛当前报表名；外壳原先按「当前路径不是定制页」无条件清空活动标签面包屑，切回报表页会把页签清成默认「报表」，现改为只在离开定制子页时清理该子页所在标签。离线回归见 `AppShell.test.tsx` 的「页面自报页签标题后切走再切回」用例 | `EOS.Web/src/features/reports/{ReportCenterPage,ReportViewerPage}.tsx`、`EOS.Web/src/components/layout/{AppShell.tsx,AppShell.test.tsx}`、`EOS.Web/src/styles/app.css`、`docs/guide/{03,50}*.md` |
| **v0.8.0 发布时 CI 是红的：「页面外壳」把组件自带的用例判成手抄骨架**（2026-10-22 修复）：红灯起于 `0701d24`（标准表格统一隔行底色）新增的 `ErpDataTable.test.tsx`——它断言组件产出的 `erp-data-table` 骨架，而门禁判据是「这个类名只由统一组件产出」，用例恰恰是在守这条契约，属误报。红着还能发布的原因是**发布流程漏跑前端两条硬门禁**（`release.ps1` 第 2 步只跑 `lint`/`build`，而 `check-page-shell` 在 CI 里是硬门禁）。处置：① 门禁把该用例并入 `$classNameOwners`——只豁免「组件自己的契约用例」，**没有**放宽到所有 `.test.tsx`；② `release.ps1` 第 2 步补跑 `check-page-shell.ps1` 与 `check-css-classes.ps1`，发布与 CI 从此同口径。修复后 CI 全绿（run `39785186243`，三个 job 全过） | `scripts/check-page-shell.ps1`、`scripts/release.ps1`、`EOS.Web/src/components/common/ErpDataTable.test.tsx`、`.github/workflows/ci.yml` |
| **迁移编号重号收口（430 → 445）**（2026-10-22，随 v0.8.0 之后的首批提交）：`430_chooser_self_return_targets.sql` 与 `430_retire_attendance_jobs_menu.sql` 同号（并行开发各取「最大 +1」所致）。改名 `445_chooser_self_return_targets.sql`、**内容逐字保留**，脚本末尾按旧资源名删除台账行（否则留幽灵名）。已建库重启时该脚本被重放一次，**幂等已验证**（在真库事务内执行后回滚：补齐 0 行、断言全过、台账删除 1 行）。新库不再执行它（内容已在 `20_metadata.sql`）。配套门禁 `scripts/check-migration-numbers.ps1`（编号唯一 + 命名格式，`-SelfTest` 正反样本）已进 CI 与 06 篇；`db/bootstrap/40_journal_baseline.sql` 删掉旧名登记（种子仍到 438，445 由启动补齐）。**待重启**：`check-migration-journal.ps1` 在 445 落库前会如实报 1 条幽灵名 + 1 条未落库 | `EOS.API/Data/Migrations/445_chooser_self_return_targets.sql`、`db/bootstrap/40_journal_baseline.sql`、`scripts/check-migration-numbers.ps1`、`.github/workflows/ci.yml`、`docs/guide/{05,06}`、`AGENTS.md` |
| **发布 v0.8.0（2026-10-22）**：标签 `v0.8.0` 打在 CHANGELOG 定稿提交 `31502ae` 上（Release https://github.com/cnlarry/EOS/releases/tag/v0.8.0 ，附件 MANIFEST.txt + SHA256SUMS.txt）；版本 0.7.0 → 0.8.0（自 `v0.7.0` 起 57 条提交含 12 条 feat，按 ADR-026 §2.6 走 MINOR），**本版含迁移 427–444（共 19 个；430 有两个脚本，编号只是排序键）**，部署时由 EOS.API 启动自动执行。本版主要交付：工作区标签改按「模块窗口」导航（ADR-040）/ 查询中心恢复一级菜单并收口两处权限旁路（迁移 444，决策 169）/ 业务流程图改按模块出图（迁移 443）/ 电子表格补齐 Excel 式交互与滚动分块 / 列表工具栏纯图标 + 条件数角标 / 选择器记住上次筛选条件 / 数值列千分位（迁移 441–442）。**破坏性变更**：框架表改名（迁移 436–438，17 张 `SYSDL`→`SYS_USER_ACCOUNT` 等），按旧表名写的自定义 SQL 需跟着改，界面与 API 不受影响。**发布时的门禁实况**：迁移台账与仓库内嵌脚本一一对应（实测 **444 个**）；编译与静态检查真跑（`dotnet build` 0 警告 0 错误、`npm run lint` 0 error、`npm run build` 通过；API 产物落仓库外 `-BuildOutput`，避开被运行实例锁住的 `bin`）；手册新鲜度非阻断、**落后 13 篇**，逐篇核过均为映射目录宽导致的机械落后（每篇只差 1 个提交：页面骨架收敛 `d56ccfb` 或检索中心 `f3e2dbf`），无内容需改。**正式流程未跳过任何门禁**（未用 `-SkipBuild` / `-SkipMigrationGate`）。**运行实例仍跑旧版本**：`/health/version` 要等重启 `EOS.API` 才与 v0.8.0 一致（长驻服务由维护者重启）。下个版本的判定依据：最新标签 `v0.8.0`，其后提交按类型递增（`feat`→MINOR、`fix`/`perf`→PATCH） | `CHANGELOG.md`、`version.json`、`README.md`、`artifacts/release-0.8.0/`、`EOS.API/Data/Migrations/{427,444}*.sql`、`docs/decisions/ADR-040-工作区标签的导航模型.md`、`EOS.Web/src/components/layout/`、`EOS.Web/src/features/search-center/` |
| **报表绑定开单公司：公司表成为打印第三数据源（ADR-041，2026-10-24）**：`REPORT.COMPANY_ID`（迁移 449）+ 版式 `COMPANY.*` 命名空间；打印公司名/英文名/地址一律取 110101（`COMPANY`），存量 460 张报表一律回填系统默认公司编号 `DEFAULT`（137 张默认报表里 88 张页头为空、21 张指向已删页头的问题由此消除）。报表维护新增「开单公司」下拉（`GET /report-admin/companies`）；版式设计器新增公司字段下拉、当前公司徽标，样例预览也用真实公司。页头字典不再保存公司文本（只留 LOGO/文本）。**待用户重启 API 后做真打印验证**；本机库旧页头文本按用户决定保留、未参与打印 | `docs/decisions/ADR-041-报表公司绑定与COMPANY字段命名空间.md`、`EOS.API/Data/Migrations/449_report_company_binding.sql`、`EOS.API/Data/Reports/CompanyPrintData.cs`、`EOS.API/Data/QuestPdfLayoutRenderer.cs`、`EOS.API/Data/ReportFormatValidator.cs`、`EOS.Web/src/features/{admin/ReportAdminPage,layout-designer,reports/ReportViewerPage}.tsx` |

## 4. 进行中

> 已收口的批次（ADR-005 架构治理五阶段、自动放量第 1/2a 批、7 个切片验收、147 候选预筛、
> ADR-011 M1–M9、ADR-016 S0–S6）不再逐条复述：流水见 git 历史与本机归档
> `docs/plans/archive/status-已收口登记-2026-10-21.md`，结论已并入 §2 基线与 §3 收口。
> 本节只列**今天还在推进**的事。

### 4.1 统一表单放量（放量本身已收口，只剩外部闸）

- 可写模块数与白名单规模见 §2；流水线工具保留可复用：`scripts/auto-enable-batch.ps1`
  （预检 → 启用 → 重启 → 真实数据 CRUD → 语义/扫描 → 回归 → 失败回滚/部分放量）、
  `scripts/report-form-meta.ps1`、`scripts/review-whitelist-candidates.ps1`。
- **剩余唯一闸门是外部人工验收**：实施顾问逐表验收 + 与旧系统并行对账，见 §5 同名条目。

### 4.2 AI 试点复核（已指示暂缓）

评估集基线为 2026-08-07（38/38，100%）；ADR-011 M86–M93、SP 移植与权限大改后**未复跑**。
2026-08-23 用户指示忽略，需要时再启动（`EOS.Eval/run_eval.py`，前置只需 `EOS.API` 在线；
`EOS.Gateway` / `EOS.AgentRuntime` 已随 ADR-007 删除，不再是前置）。

### 4.3 字段数据来源模型重构（ADR-008，迁移已落库，余 48 行分诊）

- **迁移 017 / 018 / 020 / 021 均已落库**（`ERP_SCHEMA_JOURNAL`：2026-08-28 20:55 ~ 2026-08-29 13:18）——
  原「待用户重启执行」的口径已失效，不再保留。
- **48 行分诊：政策已拍板（2026-10-25，台账 #173）——按「旧系统基线」分治**：
  **旧系统能用的新系统必须能用；旧系统也不能用的不强行补**。实测分桶（`logs/fields-chooser-rerun/triage.csv`，
  38 组 48 行）：**6 行**旧系统可用（真回归，必修）/ **14 行**旧系统同败（保持无过滤，已定不做）/
  **28 行**无外部表引用（逐条复核，能修就修）。**两处台账口径同时订正**：① 原记的「25 T2 失败 /
  21 跨表不可达」是另一次运行的读数，不再使用；② 「补 `QUERY_RELATION` 后重跑工具即可回填」**已不可执行**
  ——`ChooserBackfillRerunTool` 头部写明 `CHOOSER_FILTER_MIGRATION_LOG` 于迁移 041 删除、迁移通道关闭，
  现状唯一落库路径是直接写 `FIELD_DATASOURCE.FILTER_STRUCT` 的迁移并用 `ChooserFilterValidator` 试编译。
  **2026-10-25 复核后的事实订正**：那 6 行"旧系统可用"的**早在迁移 021 就已转换落库**（`HR_WAGE_D/HRM_WAGE_D.EMP_NO`、`PUR_PURCHASE_D.APPLY_NO/PRO_NO`、`QC_LOSS_D/QC_SCRAP_D.ANALYSIS_NO` 逐条核对均有真实结构），不是待修项；真正修掉的是**5 条编译不过的结构**（运行期 fail-closed ⇒ 静默退化成"没有过滤"）：270/1460 把列对列比较写进 `value`、1074 引用不存在的列 `R_M_IDX`、1078/1079 子查询引用历史表名 `SYS_USER_GROUP_USER`，已由迁移 `450_chooser_filter_struct_compile_fixes.sql` 修正。同时新增门禁用例 `EOS.API.Tests/ChooserFilterStructCompileLiveTests.cs`：存量 847 条非空过滤结构逐条走保存路径同一个校验器，**编不过即红**。逐条处置见本机台账 `docs/plans/48行分诊-按旧系统基线.md`。
  新旧对拍：452 行 = 424 MATCH + 0 MISMATCH + 28 UNTESTABLE（绑定类型问题）。
- 技术债（单列在 §6）：跨表 JOIN 依赖 `TABLES.QUERY_RELATION`，变更需重启 API 生效；
  多来源回显暂取首个启用来源映射。

### 4.4 ADR-011 工作助手认知层（M1–M9 已交付，仅余独立轨道）

M1 模块认知组 / M2 全局 schema / M3 记忆 L1-L2 + 主动摘要 / M4 RAG 前置 / M5 知识库落地 /
M6 单据流转适配 / M7 质量门 / M8a 只读诊断探针 / M8 配置写 / M9 自动长期记忆**均已交付**；
迁移 043 / 044 已于 2026-09-05 落库。
**未启动**：M10 语义推断轨道，按 ADR-011 的 M10a/M10b/M10c 高风险分阶段推进（不作为既有能力的前置）。

### 4.5 ADR-016 工作助手处境感知与任务闭环（已结题）

S0–S6 + 四个收尾批全部验收通过（2026-09-30），工具数收敛在 27。
**未结项以 ADR-016 正文「已知缺口与待办」为唯一事实源**（当前 5 条），本文件不复述。

### 4.6 ADR-036 共享原语单一真源（已拍板，批 1 收口、批 2 起步）

§8 五项按建议落定（分页上限统一 200、分页响应形状本轮统一、门禁独立成脚本）。
**批 1 五项已收口**：标识符白名单回接 `WorkbenchSql.Identifier`；`WorkflowEngine` / `PrintService`
的物理列查询与主键读行回接共享原语；页大小口径收敛到 `PageWindow`（10–200，子表 500 为具名例外）；
控制器内联 SQL 全部下沉到 `Data/` 对应的仓储；报表中心目录与用户状态下沉后，`REPORT_TAG` 可见性判据
从报表权、打印面板清单、报表中心目录三份收成 `Data/Reports/ModuleReportVisibility.cs` 一处
（离线用例守着"只许一处"）。
**门禁 A/B/C 三条均已落位并进 CI**（`check-controller-sql-boundary` / `check-controller-permission-boundary` /
`check-identifier-single-source`，均带 `-SelfTest`），B 的存量从 25 个控制器减到 21 个，按具名冻结、只减不增。
**批 2 第 6 项（权限门收敛）已起步**：`IPermissionService` 增 `GetReportAsync`，报表系三个控制器与
`AssistantAdminController` 不再直接持有权限仓储，`ReportController` 的 7 处权限门收成一处
（403 / 防探测 404 / 401 三种口径保持原样）。
**第 8 项（`FIELDS` 读取原语）已收口**：新增 `Data/FieldMetadataReader.cs` 作为字段清单与
"成本/保密/禁止字段"过滤的唯一实现，打印、查询中心、权限矩阵三处回接（差异只由入参表达：
只取可见 / 排除虚拟 / 要求物理列 / 能否看成本与保密 / 禁止字段集合 / 排序口径），
各自的返回类型投影保留。**第 7 项（异常→错误码）控制器侧已收口**：`ApiExceptionFilter.Map` 成为
异常→(状态码/稳定码/文案) 的唯一映射表（纯函数，`ApiExceptionMappingTests` 8 条钉住），
知识库、导入、效果预演、助手变更集四类"带自己原因码的拒绝"改为异常类型携带码，
控制器手工 `catch` 从 18 处降到 4 处（剩下的是解析辅助、文件删除 best-effort、
检索中心把 `ArgumentException` 映射成它自己的 `SEARCH_QUERY_INVALID`、BOM 导入把
`InvalidOperationException` 映射成 `IMPORT_REJECTED`——这四处是端点级契约，不是可机械收敛的重复）。
**未开工**：批 3 全部。**批 2 第 9 项（分页响应形状统一）已收口**：分页信封一律
`items`/`total`/`page`/`pageSize`——工作台 `WorkbenchData.Rows`、查询中心 `SearchQueryResult.Rows` 与报表查询
`ReportQueryResult.Rows` 三处都改为 `Items`（第三处是事后补的：报表查看页先按 `items` 改了前端，实测 `/reports/{m}/query`
仍返回 `rows`，等于给它留了个"列表空"的回归，随即补齐），前端工作台、查询中心、报表三处页面与用例同批跟改（这道改动是**对外契约变更**，
后端与前端必须同批部署）；不带 `page`/`pageSize` 的结果集（统一选择器 `columns/rows/total`、
审批历史 `{ rows }`）不算分页，键名不变。

## 5. 待决策 / 需顾问确认

| 项 | 状态/进入条件 |
|---|---|
| 2201-2204 报表设置四页 | 特殊管理页（/admin/report-setup 等），从表单放量候选剔除，不做统一表单 |
| 未解析选择器观察（非阻塞）：2701 TYPE_ID_SUPERIOR、180104 DIMISSION_ID | 源表暂无数据，真实数据建设后自动复核；~~3305/3306 COMPLAIN_NO/EXCEPTION_NO~~ ✅ 已解析（2026-08-23：EOSDEV-CP-0001/EOSDEV-EX-0001 链，完整度 100%） |
| 实施顾问逐表验收 + 与旧系统并行对账（全面替代收尾的最后人工闸） | 待顾问执行：7 个切片模块（1209/1305/1414/1501/1901/1905/180202）CRUD 机制已全过、1305 已决只读（已移出表单白名单）；并行对账工具 `EOS.API.Tests/E2eSlice*.ps1`/`E2eSliceDetail.ps1` 已就绪，对账结论由顾问确认。2026-08-26 放量候选池已收口（二次预筛无新增，当前规模见 §2），此项是剩余唯一外部阻塞，代码侧无待办 |
| 多语言扩展（L1 界面文案 + 架构级 Languages 表方案，含英语/越南语；COMPANY_NAME_EN 已接入打印页头） | 可行性评估已完成（2026-08-21，内部资料《多语言扩展可行性评估.md》）；翻译模式已确认（开发期编程助手 Agent 生成 + 发布后甲方用字段设置微调）、语言粒度已确认（组织/公司级）；**前提：中文环境完全顺畅、重构完成后才立项**，当前仅方案储备 |
| PDF 中文复制/搜索为部首字形（B3，显示正常，仅复制粘贴受影响） | 低优先，暂缓 |
| 助手成本/用量看板 | **管理面已落地**（模块 3102「模型与用量」，2026-10-01）：当日总览 + 近 7/30/90 天的「按模型」与「按天」两张表，成本按**当前模型单价**算（`GET /admin/assistant/usage`）；**仅剩个人侧待决策**——`GET /assistant/usage`（本人）与 `GET /assistant/metrics` 经 2026-10-01 复核**均在位**（未登录 401 = 已挂载），但个人侧尚无页面消费它。**原条目理由"网关汇总 API 已可用"已失效**——`EOS.Gateway` 随 ADR-007 于 2026-08-25 物理删除，模型调用内聚在 `EOS.API/features/assistant` |
| I4 历史推送清理（`appsettings.Development.json` 明文 SA 密码确已存在于 origin/main 历史） | ✅ 忽略（2026-08-23 用户指示）：不轮换、不清理；风险已登记知晓 |
| 车辆汇总打印版式深化（199901） | 真实车辆数据建设后 |
| 考勤滚动计算引擎移植（约 800 行旧逻辑，含规则 18） | 考勤/工资真实数据建设后 |
| ERROR_NO_SAVE 不实现 | 已决策（旧系统无消费逻辑） |
| 其它客户升级管道（update.sql 已冻结） | update.sql 冻结为升级史（2026-08-21）；EOS.ERP 唯一库约定确立后，新库对象走 DbUp 版本化迁移（`Migrations/`，2026-08-22 启用）；客户专属升级「还原→差异分析→方案→dry-run」管道待拿到需升级客户库后设计 |

## 6. 技术债

> 详细历史背景见 内部归档《技术债-2026-08-16.md》；本文只维护**状态与进入条件**。
> 本节只留**仍成立**的债：⏸ 登记 / 🔄 进行中 / 或虽已收口但备注里仍有未结项（待拍板、仍未做、建议单独立项）的。
> **已收口且无残留的行于 2026-10-21 移出**，逐字存于 `docs/plans/archive/status-已收口登记-2026-10-21.md`
> （本机，不入库）。上一版写的"于 2026-10-03 移出"当时并未执行，故 18 行完工记录直到本日才真正清出。

| 条目 | 状态 | 进入条件/备注 | 证据 |
|---|---|---|---|
| **解批/批核缺"按单据引用关系"的上下游状态校验**（2026-10-07 用户拍板口径） | ✅ **已实现（2026-10-07）** | 口径与实现见 [48-生命周期与审批流](guide/48-生命周期与审批流.md)：解批不删任何下游单据；只有「无下游」或「下游都未批核」才允许解批，引用链逐级解开；批核前必须查上游批核状态；已结案不许解批。唯一依据是 `FIELD_RELATION` 中 `RELATION_KIND='EFFECT'` 的边（方向：被引用方是上游），实现在 `DocumentChainGuard.cs`（终端码 `UPSTREAM_NOT_CONFIRMED` / `DOWNSTREAM_CONFIRMED`）。**仍未收口的只有存量数据**：发货单 **1322** 行、采购单 **338** 行引用了未批核主档，守卫生效后这些单据批核会被拦——那是数据没审核，不是误拦 | `EOS.API/Data/WorkbenchApprovalService.cs`、`EOS.API/Data/WorkflowEngine.cs`、[48-生命周期与审批流](guide/48-生命周期与审批流.md) |
| **维护型主档开着"自动批核" ⇒ 新建即被锁死，改/删都得先解批**（2026-10-07 暴露，2026-10-08 定口径） | ✅ **已澄清：不是产品缺陷** | **口径（用户拍板）：自动批核是合理的，要改就解批**——「建单即批核、要改先解批、删除前再解批一次」就是这类模块的正常用法。当年看到的「解批不生效」是验收脚本把 key 传成数组导致 **400 `INVALID_MODEL`**（2026-10-07 已修），真库端点链路实测正常（`CONFIRM_TAG` 1→0→1 全程可复现）。**不再改动任何模块的 `AUTO_APPROVE` 标志** | 迁移 `195_depot_location_disable_auto_approve.sql`（同现象先例）、`EOS.API.Tests/E2eBaseDataCrud.ps1`（本机，不入库） |
| **解批的库存语义：冲销留痕（保留原流水）**（2026-09-26 发现；2026-10-08 定口径） | ✅ **已定口径** | 解批**不改写**已关账期流水，只另写一条反向流水（`IN_OUT='I'`、`QTY=-1`），原流水保留。**两条必然结果**：① 解批过的单据**此后不能再删除**（400 `INVENTORY_LOG_EXISTS`）——这是设计结果，不是可绕过的中间态，提示语已改口径；② **期内解批**才会让 `V_INV_MONTH_SNAPSHOT_RECON` 不平，处置是重新生成并批核该期快照；**期后解批天然抵消，不产生差异键**（按视图「期后流水取净额」的定义推演，尚未做端到端构造，登记为验证缺口） | 本机证据：a1-month-close/28~31-*.txt |
| **真库用例借真实库别做「整库归位」再把搬走的行删掉 ⇒ 库存数量被物理销毁（且审计被用例删除）**（2026-09-27 A2 批定性） | ✅ **已修 + 已加门禁 + 已清残留（2026-09-27 A3 批）** | 用例改为**自造专用库别**、清理收窄到自造料号、**不再删审计**；新增门禁 `scripts/check-live-fixture-hygiene.ps1`（`-SelfTest` 12 例；真库用例不得借真实主档取样值写入、不得删审计、数量承载表不得按库别整体删）。**仍是活项的两点**：① 门禁「删审计」为**零容忍 + 具名存量棘轮**——存量已随各批修复降到 **0 文件**（`check-live-fixture-hygiene` 实测三项：借真实主档 3 个文件、删审计 0、按库别删数量承载表 0）；② **仍有一条同类风险具名登记、本批不修**——`CompareAfterSave.ps1:738`（对拍脚本改真实 `FIELDS` 行后按原值还原）；原登记的另两条（`DocumentActionGenerateAdjustmentLiveTests` 借真实库别过账、`E2ePermissionMatrix.ps1:121` 改真实采购单 `OWNER`）已收口或转为"先捕获后还原"并纳管 | `scripts/check-live-fixture-hygiene.ps1`、`EOS.API.Tests/DepotStockPolicyRelocateLiveTests.cs`、内部资料《A3-真库用例污染修复-执行报告.md》 |
| **E2E 夹具短 ID 按前缀前 6 位截断 + 基础资料清理先于因子 ⇒ 上一轮残留毒化下一轮**（2026-09-26 全量扫描暴露） | ✅ **已修（2026-09-27 小项清理批）**：短 ID 改为「前缀前 3 位 + 本次运行标识（GUID 前 3 位）」——进程内稳定（收尾可对账）、跨运行不同（不再重号），长度仍 ≤10 字符；`Reset-E2eBaseData` 已是逆序删除。`E2eMoldDomain` 真跑 rc=0。**注意**：该脚本每跑一次会留下 1 个料号 × 2 个库别的库存不平键（本批验证时使"清单外不平键"4 → 6），建议单独立项 | **现象**：`E2eSelfContained.ps1` 的 `Get-E2eShortId` 取前缀**前 6 位**再拼后缀（`LINE_ID` 等列限 10 字符），而默认前缀是 `E2EPD<HHmmss>` ⇒ **第 6 位之后的时间戳被丢掉**，同一脚本任意两次运行得到同一个短 ID（如 `E2EPD0L01`）；`Reset-E2eBaseData` 又**按登记（插入）顺序**删除（线别先于引用它的工单），上一轮失败留下的行清不掉。**实测**：`E2eProcessDomain` 首次复跑直接 `自建线别失败：该编号已存在：E2EPD0L01`（PASS=1）；手工清掉 `E2EPD` 前缀残留（`LINE`/`MOC_WORK_M`/`MOC_WORK_D`/`SFC_PLAN_M`/`SFC_PLAN_D`/`PRODUCT`×2/`MOU_MOULD`/`SFC_PROCEDURE`×2/`SFC_PROCEDURE_TYPE`×2）后转绿（PASS=36）。**处置建议**：短 ID 改用运行时刻的**低位熵**（取后 6 位）或对限长列用哈希后缀；`Reset-E2eBaseData` 改为**逆序删除**（后登记的先删）。与本节 `E2ePrerequisiteCrud` 那条（固定前缀 ⇒ 不可重跑）同族 |
| **全量重发布的副作用：ADR-012 对拍证据会判过期**（2026-09-19 记录） | ⏸ 登记（操作纪律） | `scripts/adr012-acceptance-ledger.ps1` 按"对拍报告的定义版本 ≥ 当前快照版本"判新鲜度 ⇒ **任何让模块定义版本顶起的重发布，都会把该模块既有的对拍 PASS 证据降级为过期**。本轮为修 2708 做全量重发布时未先想到这一层，152 个模块版本被顶起（其中绝大多数只因迁移 `199` 的字段元数据变化）。**纪律**：重发布前先明确"这次重发布会不会顶起版本、顶起多少、对拍证据是否在意"；单纯的字段元数据变化**不必**重发布（`FIELDS` 是运行期实时读取的） |
| **`E2eInventoryRestDomain` 没有清理段** | ⏸ 登记（2026-09-19）→ **2026-09-26 复核：结论与口径均不变** | **2026-09-26 复核**：脚本头部声明"数据带 `E2EIV` 前缀，**默认保留用于演示**"**仍在**（`:13`），脚本内**仍无** `-Cleanup` 之类可选开关；本轮 rc=0 / PASS=17，符合 `1. 一句话现状` 之外的用户 2026-09-19 决议（只清"声明用后清理"的族别）。**未做改动**（要做只能加可选开关，不得改默认行为）。以下为原登记正文：跑完留下测试产品与单据；实测 `INV_OCCUR_OUT_M` 已积累 **11 行** `OCCUR_TYPE='E2E'` 的早期残留。清理要按外键序撤单/撤库存/撤批次账，属独立小项（同域的 `E2eMoldDomain` 已有清理段可参照）。**2026-09-20 定性（本条不是漏做）**：该脚本头部即声明"数据带 E2EIV 前缀，**默认保留用于演示**"，而 2026-09-19 用户拍板的清理范围（选 A）明确**只清"脚本声明用后清理"的族别、保留"默认保留用于演示"的族别**（`E2EIV`/`E2EBS`/`E2ECU`/`E2EEH`/`E2EIV`/`E2EOS`/`E2EPR`/`E2ESL`/`E2ESH`/`E2EWA` 等）⇒ 给它加"跑完自动清理"反而与已定口径冲突。若将来要能一键清场，应做成**可选开关**（`-Cleanup`）而不是改默认行为 |
| **本机 SQL Server 读 `master.mdf` 曾间歇失败（Msg 823 / 错误 21）** | **故障窗口约 20:24–20:26，已自愈；但 `DBCC CHECKDB` 未做（待你/DBA）** | **当时记录**：存储与文件在操作系统侧**可读且健康**，故障发生在 SQL Server 进程的 I/O 路径上。`master` 与 `EOS.ERP` 在 **20:26:51 已恢复可读**，API 已由我按本会话授权重启（health 200）；**全量单测 1491/1491、五门禁、冒烟 92/0 均已补跑通过**。**仍待办（不是我能替你决定的）**：Msg 823 的官方要求是跑 `DBCC CHECKDB` 确认完整性——故障期间有过 Level 24/21 报错，**不要把"现在能用了"当成"库没受损"**；另建议把 `D:\Database` 加入安全软件/EDR 的实时扫描排除，防复发 |
| React Hook Form 重构 | ⏸ 登记 | 现有受控表单稳定且校验规则与服务端一致，收益/风险不划算；留待需要复杂联动时评估（2026-08-26 自 `统一表单编辑器待办.md` D 节归档迁入） |
| 受控表达式工作流 P1–P7 + DATA_FILTER 完整化 | ✅ 已实施 | 更广消费面（DATASOURCE_SQL 接入选择器等）待产品决策 |
| 逐模块元数据复核（必填/默认值/FORM_OPTIONS/布局/选择器） | 🔄 进行中 | 放量主线，顾问按矩阵确认 |
| **`110310 库存策略` 直接发布会绕过策略校验**（ADR-014 §6 风险行） | ✅ **已收口（2026-09-19，迁移 206）** | 写路径已封：保存侧本就由统一表单白名单挡在 404；批核侧 `RunWorkflow`/`RunFinish` 补齐同一道闸门后四个端点 **400 → 404**；迁移 `201` 置 0 死 `AUTO_APPROVE`（不补 `CONFIRM_TAG`——配置表不该伪装成单据），发布 dry-run 随之转 `passed=true`；`check-lifecycle-columns.ps1` 补了「能力→列」断言。**唯一残留**：`110310` 的界面仍按 P2-21 挂起——写路径已封、发布在数据安全上不再是雷，但**界面做好前仍不发布** | 迁移 `201`、`EOS.API.Tests/E2eDepotPolicyWritePath.ps1`、ADR-014 §4.22 |
| **`HR_ENACTMENT_M`（每月出勤参数）为空 ⇒ 180206 加班申请单必然存不进去**（2026-09-19 实测核查） | ⏸ 登记（**数据前置缺口**，非代码缺陷） | **实测**：`HR_ENACTMENT_M` **0 行**、`HR_ENACTMENT_D` **0 行**。180206 的保存期自定义校验 `hr-apply-check`（判据①）在**出勤日所属月份未维护「每月出勤参数」时直接拒绝**——这是**设计如此**（先决条件型拒绝，文案带 `yyyyMM`），产品侧无缺陷。**后果**：`E2ePrerequisiteCrud` 的 180206 段在该前置数据缺失时**无法通过**（返回 `400 BUSINESS_VALIDATION_FAILED`），该脚本因此整跑 `rc=1`。**处置建议**：要么把"每月出勤参数"纳入 E2E 前置夹具（先建参数主档 + 额度明细再建加班申请），要么把该段移出该脚本；**不要**为了让它绿而去掉产品侧的判据。**2026-10-25 复核（广扫 B 层正则造值修好后）**：考勤族 21 个模块里 20 个走完全环，180206 是唯一仍被拒的一个，拒因逐字就是本条——`未生成 202610 月度出勤参数，加班申请不能保存；请先在「每月出勤参数」维护本月的加班额度` |
| 审计写放大监控（单据保存 P95） | ⏸ 登记 | 每写事务 +1 AUDIT_EVENT（+UPDATE 字段行）；降级开关已配置（`Audit:FieldChangesEnabled`，关闭即停写字段级明细）；达复审阈值时启用 |
| 明细字段元数据逐表修正（TAX_TYPE 等默认值） | 🔄 随验收推进 | 放量主线 |
| 2 个报表超万行上限（139811/149805） | ⏸ 设计上限（**2026-09-19 口径收窄**：屏幕路径已分页 `OFFSET/FETCH`；**PDF/导出**路径仍为 10000 行硬上限） | 缩小条件即可；如需导出全量须另立流式/分批方案 |
| ~~12 个特殊页（运维类 2307 等、考勤计算类）~~ | 🔄 **口径已更新（2026-09-19 回填）** | 现行权威清单是 内部资料《定制页面清单.md》：**§一 系统管理类仅 2205 一项 ⬜ 待验收**（页面已随 ADR-009 P4 下线，迁移 025），**§二 业务特殊页 7 项 ⬜ 待验收**（230902/110111/180213/180662/230901 组/2102/199901；原 129802 已于 2026-10-17 退役）。原口径里的 **2307 是"停用模块"**（数据库备份，明确不移植），今天是全库唯一 `/legacy/%` 占位页，不应列入待办；"考勤计算类"与 §5「考勤滚动计算引擎移植」为同一件事，避免重复登记 |
| 旧库小写命名遗留对象（**2026-09-19 实测 85 个**：`f_*` 函数、`vw_*` 视图、2 张 `U` 表；**存储过程 0**——全库 `dbo` 过程仅剩 `P_HRM_WAGE_CALC` 1 个，原 108 个里的 `xp_*`/`p_*`/`proc_*` 已随 ADR-012 系列迁移下线） | ⏸ 登记 | 命名规范只约束新建对象；被点名依赖的 9 个 `f_*` 函数（`f_get_dept_desc`/`f_get_under_depts`/`f_get_underling`/`f_get_sirs`/`f_get_emp_name_by_id`/`f_get_table_kind_desc`/`f_get_approve_state_desc`/`f_get_pro_moulds`/`f_get_unit_type_desc`）**经 2026-09-19 复核全部仍存在**、C# 侧约 30 处引用，重命名需同步改代码 + 回归；2026-08-22 确认暂时忽略不动 |
| **旧过程基准对拍用例缺夹具 ⇒ 9 个真库用例常红**（2026-09-28 ADR-024 收尾发现，**用户拍板：保持红、具名登记**） | ✅ **已修** | **处置（本次）**：夹具目录 `EOS.API.Tests/Fixtures/legacy-sprocs` 改名 `baseline-sprocs`，与两个用例读取的路径一致；改后 `HrAnalysisReportPortLiveTests` + `InventoryReportPortLiveTests` **9 例全绿**。该目录由 `.git/info/exclude` 排除，属**本机资产、不入版本控制**，故改名不改仓库内容。**仍属环境依赖**：夹具不在仓库里，换机/CI 上这两个用例照旧会红——要根治得把 25 个 `P_RPT_*.sql` 纳入仓库或另建可分发前置。以下为 **2026-10-03 复核订正**：夹具**其实在仓库里**——测试夹具目录 legacy-sprocs（本机） 实测 **25 个** `P_RPT_*.sql`（含 `P_RPT_HR_*` 9 个与 `P_RPT_INV_PRO_DEPOT_1`），而两个用例读的是**另一个目录名** `baseline-sprocs`（`HrAnalysisReportPortLiveTests.cs:169`、`InventoryReportPortLiveTests.cs:237`）⇒ **只是目录名不符，改名即转绿**，不是"不可恢复"（归档 tag 确不存在这一点不变）。以下为原文（保留于后）：`HrAnalysisReportPortLiveTests` / `InventoryReportPortLiveTests` 需要 `baseline-sprocs/` 下的 `P_RPT_*.sql`（旧过程正文，与聚合注册表逐行对拍用）。该目录**不在仓库里**：仓库既有的旧过程资产只有 `scripts/legacy-groundtruth/sprocs/` 的 **22 个 `*_After_Save`**（ADR-012 的保存钩子基准），**没有 `P_RPT_*`**；`AGENTS.md` 记载的两个归档 tag（`archive/legacy-ssdt-full-snapshot` / `archive/legacy-ssdt-snapshot`）**在本仓一个都不存在**（`git tag` 输出为空）⇒ 夹具**不可恢复**。**处置（用户 2026-09-28）**：不改成 Skip、不删用例，**保持红并登记为环境缺口**——与本仓"无连接跳过≠已验证"的一贯口径一致（缺前置就失败，不静默跳过）；谁若重新拿到 SSDT 快照，把 `P_RPT_HR_*`（7 张 HR 分析报表）与 `P_RPT_INV_PRO_DEPOT_1` 正文按对象名落到该目录即可转绿 |
| **重复筛选条件：3 个模块两份都带用户填值，未去重**（2026-09-28 ADR-024 收尾发现，**用户拍板：暂不处理、具名登记**） | ⏸ 登记（**业务语义待定**，不是技术债） | 条件从承载页归位到归属模块时的规则是"序号沿用原值、被占则接在目标当前最大序号之后"，只避免主键撞车、**未与目标已有的同义条件比对** ⇒ 全库 **7 个模块 23 组**出现"同字段同模板各两份"（`1405`/`1502`/`180102`/`180105`/`180214`/`1803091`，另有 4 行空模板条件）。**已去重**（迁移 281，判据是"被删的那一份在 `SYSQR_USER` 里零填值"）：`180102` 删 8/9/10/12、`180105` 删 8/10、`1803091` 删 1/2/3，另清 3 行空模板。**未去重**：`1405`（两份各有 12/11 个账号填值）、`1502`（3 个 vs 1 个）、`180214`（2 个 vs 1 个）——**两份都带用户填值**，同一条件两份填了不同值时两条谓词同时生效，结果比用户以为的更窄；保留哪一份、另一份的填值算不算数属**业务语义**，迁移 281 专门断言这三个模块"一行未改"。要收口需先给"以哪一份为准"的规则。**2026-10-25 复核（只读查库）：三个模块已无重复**——按「同字段」「同描述」「同模板」三种口径各查一遍，`SYS_REPORT_PARAMETER` 里 1405 / 1502 / 180214 均无成对行（1405 现存 7 条，用户填值 20/20/20/20/5/2/9，不是"两份各 12/11"那种形态），即本项在库里已不成立，建议按「已消解」关闭；同片区域另发现 **14 条"状态下拉型"条件的参数名为空**（`{p.}` + `parameterName:""`，分布 1405/1607/1608/1201/130101-130107/1803101/1908081），是否导致"选了状态却没过滤"待验证，未动 |
| **状态类筛选条件里有 4 条「选了也不按预期过滤」**（2026-10-25 复核，**待产品决策**） | ⏸ 登记 | 全库 **14 条**条件模板写成 `{p.}` + 空 `parameterName`。逐条核过：其中 **10 条「批核状态」不影响功能**——选项值是字面量 `true`/`false`，用户选值按序号存在 `SYS_USER_REPORT_PARAMETER.F_VALUE` 并作为 `defaultValue` 进入查询，而 `parameterName` 在前后端都**没有消费方**（前端只在类型里声明、后端解析后直接下发）；但 **4 条「退出/对帐状态」是真问题**：`1405:6`（下生产单状态）、`1405:7`（送货状态）、`1607:6`（对帐状态）、`1608:7`（对账状态）的**选项值本身是列表达式**（如 `{COP_ORDER_D.FINISHED_PLAN_QTY}<{COP_ORDER_D.QTY}`），这类条件解析成固定单选（Type=2），运行期是「字段 = 用户选值」的等值比较，**表达不了列对列比较** ⇒ 选了得不到期望的候选集。处置需产品决策：给状态下拉加「表达式选项」（要动条件模板与查询构建器），还是把这 4 条改写成普通条件；**未改动** | `EOS.API/Data/ConditionTemplateParser.cs`、`EOS.API/Data/ReportRepository.cs`、`EOS.Web/src/features/reports/ReportViewerPage.tsx`、`dbo.SYS_REPORT_PARAMETER` |
| **若干判别性验证从未构造**（ADR-024/027/028/029 收尾时如实登记，原先只存在于流水里） | 🔄 **部分已补（2026-10-16）**：D15 与助手 ⑥⑦ 已复验，余项仍登记（**验证缺口**，非代码缺陷） | ① **D15 端点级复验已通过（2026-10-16）**——归属更正：D15 是 **ADR-024** 第 4 版 C1 新增的「汇总报表（`ReportAggregateRegistry`）不过字段级权限」硬前置，此前误记在 ADR-028 名下。复验：受试账号 `larry` 对归属模块 `1305` 有浏览权、无价格位，`GET /reports/1305/definition?reportId=INV_Pro_Depot_1`（`dataSource=aggregate`）与 `POST /reports/1305/query`（191 行）**双双无 `PRICE_*`**；对照 admin 的同一 definition 含 `PRICE_Q`/`PRICE_J`/`PRICE_X`，而数量类 `QTY_*` 两者都可见 ⇒ 对照成立。余 **ADR-028**：错误页 / 提示条 / 复制报障编号需**浏览器实测**（人工触发，未做）。② **ADR-027**：日志目录不可写时的降级、真实 50MB 轮转未构造。③ **ADR-029**：打包瞬间轮转未构造（无权限 403 与超 100MB 413 已实测通过）。④ **诊断事实读取**（定义快照规则、效果行、失败审计的真库 SQL）未配 live 用例，助手只读可判定范围目前只有 `reference-exists` 一条键。⑤ `scripts/verify-permission-gate.ps1` **已有可用受试账号 `larry`**（该账号对含 `PRICE_*` 的模块本就无权限，属脚本描述的"零数据改动"形态）；但脚本自带探针表只有 110310 / 110309 两条，不覆盖 D15 那类字段级口径，故 D15 按上面的端点调用单独复验。⑥ **助手就地操作卡渲染已实测（2026-10-16）**：操作卡**随回复直接渲染**（`diagnose_record` → 读单据详情 → `preview_record_action` → 成卡），**不需要**先点「准备请求卡」；卡内含预演结论与「重新预演 / 确认执行 / 复制」，未点确认即不落库。⑦ **会话重命名 / 归档已实测（2026-10-16）**：在 `/assistant/sessions` 改名与归档**双落库**（`ASSISTANT_SESSION.TITLE` / `ARCHIVED_AT`），「在列」计数 9 → 8 → 取消归档回到 9，并还原名称；顺带验到**删除只对已归档会话开放**。⑧ **（原 ⑧）另有一条能力边界（非缺陷）**：半成品账**任意时点重建不可得**——该账没有流水，要做得先给它一本流水，超出相关 ADR 范围。⑧ **另有一条能力边界（非缺陷）**：半成品账**任意时点重建不可得**——该账没有流水，要做得先给它一本流水，超出相关 ADR 范围 |
| **纯主表模块静默丢弃提交的 `details`**（2026-09-20 E2E 收口发现） | ⏸ 登记 | **现象**：向 `DETAIL_TABLE` 为空的模块（如 3302 抱怨退货处理单）提交 `details` 数组，保存返回 **200** 且不报错，明细**整批消失**——调用方以为写进去了。**根因**：`WorkbenchCommandHandler.SaveDetailsAsync` 在「`definition.DetailTable` 为空**或** `form.DetailFields` 无字段」时直接 `return null`（`EOS.API/Data/WorkbenchCommandHandler.cs`），既不落库也不提示。**发现路径**：`E2eAdr006FormRework` 链③ 挂在 3302 上，断言"坏行被拒 400"永远不可达（保存 200），且每跑一次在 `QC_COMPLAIN_M` 留 1 行残留。**为何算缺陷**：这与 Q2「编辑主表即静默清空整单明细」同属**静默数据丢失**一类——该缺陷的处置结论是"不能静默"，本条同理。**修法建议**：`details` 非空而模块无明细表时 fail-closed 返回 400（新码如 `DETAIL_NOT_SUPPORTED`），并在发布期/表单定义层拒绝该组合；`details` 为 `null` 或空数组维持现状（纯主表模块本来就允许省略）。**✅ 已修（2026-09-20）**：按上述建议落地——`SaveDetailsAsync` 对"模块收不下明细"（无明细表 / 明细字段未开放）在 `details` 非空时 fail-closed，错误码 **`DETAIL_NOT_SUPPORTED`**（新增 `DetailFailure` 统一出口，与"明细逐行校验失败"分开给码，`ApiProblem` 顶层码即该码）；主表 INSERT 在事务内，返回 400 时整单回滚、不留半张单据。回归脚本 `EOS.API.Tests/E2eMasterOnlyModuleDetails.ps1`（真库双向 + finally 无条件清理）：**改前实测 HTTP 200（静默丢弃）**，改后待重启 API 复跑转绿 |
| **开发手册与决策记录的知识库重灌未完成**（2026-10-11 登记） | ⏸ 登记（**环境缺口**，非代码缺陷） | 本轮改了 `docs/guide/03` 与 12 篇 ADR（决策记录头部规范化、提交信息真源收敛），这两类内容都要推回知识库；`scripts/sync-guide-to-kb.ps1` 在运行实例上 49 篇**一篇未成**——**46 篇报 `KB_EMBEDDING_NOT_CONFIGURED`（HTTP 503，向量模型未接线）**，另 **3 篇**（`ADR-004` / `ADR-025` / `ADR-029`）被入库敏感拦截（`KB_SENSITIVE_BLOCKED`，判为 CREDENTIAL）；**这 3 篇都不在本轮改动范围内**，属既有内容问题。向量模型接线（`ADR-031` 的落地）后重跑该脚本即可：幂等，按内容哈希比对，未变的自动跳过 | `scripts/sync-guide-to-kb.ps1`（`-Plan` 只算不推、`-Verify` 只核对口径） |
| **主表金额汇总口径不统一：通用保存用裸 `SUM`，取价/变更回写按汇率折算**（2026-10-18 登记；2026-10-22 核查并拍板） | ✅ **已落地（2026-10-22，台账 #170 拍板 ⒜）** | **实测结论（只读核查，连键按通用保存路径的"主表前两个主键列"）**：全库 147 组主从表对里，主表与明细都有 `CURR_RATE` 的 21 组（明细共 130,328 行）中，「明细汇率 ≠ 主表汇率」的行**确实存在但极少**——两者都有值且不等 **49 行**（`PUR_RECEIVE` 19 / `PUR_PURCHASE` 6 / `PUR_CALLBACK`·`PUR_DUE` 各 4 / `PUR_QUOTE`·`COP_ACCOUNT`·`COP_QUOTE`·`COP_SEND` 各 3 / `COP_ORDER` 2 / `COP_CALLBACK`·`MOC_OUT_PRODUCT_IN` 各 1），另有明细汇率为空而主表有值 **71 行**（`COP_QUOTE` 58 / `COP_ORDER` 9 / `MOC_OUT_PRODUCT_IN` 4）⇒ 本条债写的那个"既未断言、也未约束"的前提**不成立**。**但库里没有任何差异行**：主表 `AMOUNT` 对裸 `SUM` 在 11 组上全为 0 差异，`AMOUNT_TAX`/`TAX_SUM` 在 35 组列对上全为 0 差异 ⇒ 实际落地的口径就是裸 `SUM`。**两套算法各自的生效时机**（2026-10-22 校正）：通用裸 `SUM` 在保存事务里**最后**执行（`WorkbenchCommandHandler.cs:353`/`:648`，位于 SAVE 效果链之后）；两处折算**都不在 SAVE 阶段**——`order-change-apply`（1418 销售订单变更）/ `purchase-change-apply`（1609 采购单变更）挂在**批核效果**，`purchase-reprice`（1606 采购单）是**手工动作**，之后没有通用重算 ⇒ 折算结果**会留在库里**（目标主表 = 模块主表名去掉 `_CHANGE`，即 `COP_ORDER_M` / `PUR_PURCHASE_M`），直到该单据下一次经统一表单保存被裸 `SUM` 覆盖。故这是**两种口径轮流生效**，不是死写入（`produce-change-apply` 的配置无 `totals`，生产单变更不涉及金额汇总）。**影响面分两层**：⒜ 裸 `SUM` 汇总本身是**通用机制**——147 组主从表对里 **29 组**主从都有 `AMOUNT`（其中 10 组没有汇率列，两套公式天然等价）；⒝ 真正存在"第二套算法"的只有 **2 张目标单据**：客户订单（经 1418 变单批核）与采购单（经 1609 变单批核、1606 取价）。**已落地（⒜，2026-10-22）**：① `ChangeApplyHandler.RecalcMasterTotalsAsync`、② `PurchaseRepriceHandler.RollupMasterAsync`、③ **`CallbackRepriceHandler.RecalcMasterAsync`（本条登记时漏掉的第三处：`callback-reprice` 在 1413 送货单回执 / 1610 收料核价单上以批核效果启用）** 三处全部改裸 `SUM`（主表汇率不再参与汇总）；④ 取价里那道"主表汇率为 0/空即拒绝"的守卫**一并退休**——它唯一的理由是除零，留着会把币别数据问题报成"取价失败"；⑤ 新增两条真库用例：`MasterAmounts_UseBareSum_WhenMasterRateDiffers`（主表汇率改成 2，折算算式得 27÷2=13.5、裸 `SUM` 得 27，改回折算即红）与 `BrokenMasterRate_DoesNotBlockReprice`。验证：取价真库用例 **6/6**、离线测试 **2077/2077**、`dotnet build` 0 警 0 错。**已收口为发现式机制（2026-10-23）**：汇总列不再由代码枚举，改由 `MasterAmountRollup` 按「主表列与明细列**同名** + **数据类型相同** + 双方 `FIELDS.NUMERIC_ROLE='AMOUNT'`」从元数据发现——三条判据缺一不可，只认同名会把 `CURR_RATE`（21 组）、`TAX_RATE`（17）、`REBATE`（6）、`PRICE`（4）、`QTY`（4）也加总。同一原语接在三个收尾点：统一表单保存、单据动作执行后（`DocumentActionExecutor`）、批核/解批效果后（`WorkbenchApprovalService`，预演跳过）；跨单据回写（变更单 → 原单）带目标键调用同一原语，回执核价因一次涉及多张目标单据仍保留自己的 SQL（裸 `SUM` 口径相同）。实测发现 **65 条规则 / 31 组主从表对**，比对 **133,463 张单据 0 不一致**；迁移 `446` 把这些列置为只读（可写即"假可写"，用户填了会被覆盖）。门禁 `scripts/check-master-amount-rollup.ps1` 断言三件事：逐键对账、被汇总列必须只读、效果动作不得把它们当写入目标。**效果链里的汇总同时收敛（迁移 447）**：配置型效果 `detail-rollup` 写的是本单主表，与框架重复——170103 预收帐款单 / 170203 预付帐款单的唯一赋值（`AMOUNT ← SUM(AMOUNT)`）已被框架覆盖，整条移除；170101 应收货款单 / 170201 应付货款单保留 `QTY_TOTAL ← SUM(QTY)` 与 `SUM_AMOUNT ← SUM(AMOUNT_TAX) + 主表 OTHER_PRICE`（框架管不到），只移除被覆盖的三条金额赋值。四个模块已标脏，**需重新发布该模块（模块管理保存即发布）才生效**。跨单据回写的 `order/purchase-change-apply`、`callback-reprice` 不在范围内（写的是别的单据）。**仍余**：`ChangeApply` / `CallbackReprice` 两处无真库用例（**验证缺口**）；"明细汇率若填写必须等于主表汇率"的断言未做；`CUS_MANUAL_M` 主键只有一列，运行期与门禁都按"不汇总"跳过 | `EOS.API/Data/Workbench/MasterAmountRollup.cs`、`EOS.API/Data/WorkbenchCommandHandler.cs`（`RecalculateMasterAmountsAsync`、调用点 `:353`/`:648`）、`EOS.API/Data/DocumentActions/DocumentActionExecutor.cs`、`EOS.API/Data/WorkbenchApprovalService.cs`（`RunApprovalCoreAsync`）、`EOS.API/Data/DocumentActions/Handlers/PurchaseRepriceHandler.cs`、`EOS.API/Data/Effects/ServiceEffectHandlers/{ChangeApplyHandler,CallbackRepriceHandler}.cs`、`EOS.API/Data/Migrations/446_master_amount_rollup_readonly.sql`、`EOS.API/Data/Migrations/447_effect_rollup_convergence.sql`、`scripts/check-master-amount-rollup.ps1` |
| **4 个「别名金额列」不参与自动汇总：主表金额列名与明细不同名，且旧系统里它们是主表手填字段**（2026-10-23 登记） | ⏸ **保持现状（不汇总）**；业务确认需要计算时再单独立项 | 发现式汇总要求「主表列与明细列同名」，这 4 列明细侧没有同名列 ⇒ 不命中、保持可写、不汇总——**与旧系统行为一致**：`ERP/CUS/Export.aspx`、`ERP/CUS/Process_Invoice.aspx`、`ERP/CUS/Process_Pay.aspx`、`ERP/MOU/BatchIn.aspx` 里它们都是主表 `DxTextBox`（用户自填），不是汇总结果。数据也不支持"它就是明细之和"：`CUS_PROCESS_INVOICE`、`MOU_BATCHIN` 均为空表；`CUS_EXPORT` 8 张单据该列全为 0；只有 `CUS_PROCESS_PAY` 有 1 行且恰好等于 `SUM(明细.AMOUNT)`（1 行不足以定性）。**若业务确认要改为计算**：先在明细侧加同名列并回填、证明在真实数据上恒等，再把主表列改名对齐——两步各自单独一批、单独验收；直接改名会牵动 `FIELDS`、已发布快照、报表定义与打印版式。**待确认清单**：`CUS_EXPORT_M.DRAWBACK_AMOUNT_TAX`（已退税含税金额）、`CUS_PROCESS_INVOICE_M.AMOUNT_TAX`（退税含税金额）、`CUS_PROCESS_PAY_M.AMOUNT_TAX`（应收加工费）、`MOU_BATCHIN_M.SUPPLIER_AMOUNT`（代工费用） | `ERP/CUS/{Export,Process_Invoice,Process_Pay}.aspx`、`ERP/MOU/BatchIn.aspx`（主表 `DxTextBox`）、`dbo.CUS_EXPORT_M` 等四张主表 |
| **改名后仍带旧列名的默认约束名与索引名**（2026-10-26 登记，ADR-043 §4.3 明确可选、本批未做） | ⏸ 登记 | 迁移 451 只改列名：**329 个默认约束**（如 `DF_ACC_ACCOUNT_CREATE_PERSON` 现在挂在 `CREATED_BY` 上）与 **3 张表的旧列索引名**仍是旧名。不影响列名解析、查询与运行，只影响"读 `sys` 时看见的名字"与新建库的观感。**要做时按一次性脚本处理**：`sp_rename` 改约束/索引名是纯元数据操作，但必须与 `db/bootstrap/10_schema.sql` 同一批重导出，否则基线和新库继续带旧名 | `EOS.API/Data/Migrations/451_lifecycle_column_rename.sql`、`db/bootstrap/10_schema.sql` |

## 7. 里程碑速览（M4A–M94）

| 里程碑 | 能力 | 状态 |
|---|---|---|
| M4A–M4B | 真实认证（ADR-004）、权限与动态导航 | ✅ |
| M5–M6 | DocumentWorkbench + 统一表单编辑页 | ✅ |
| M7–M8 | 领域保存管线/批核 + 业务闭环 E2E | ✅ |
| M9–M10 | FILTER 行级过滤 + EXEC_TAG 执行范围 | ✅ |
| M11–M12 | UI 验证 + 带 SP 模块批量领域注册 | ✅ |
| M13–M19 | 模块审计/报表 MVP/排序/查询中心/导入/汇总 SP | ✅ |
| M20–M28 | 通用打印/特殊页解锁/BOM/域 E2E/UI 冒烟 | ✅ |
| M29–M40 | 特殊页工作台映射/批处理/专用报表/权限矩阵/库存域 | ✅ |
| M41–M53 | 全链 E2E/打印版式/对账/考勤/员工作业/验证基线 | ✅ |
| M54–M67 | 数据表数据维护/各域抽查/最终验收审计 | ✅ |
| M68–M71 | 白名单权限闭环/字段审计/考勤生成/我的任务 | ✅ |
| M72–M79 | 车辆汇总/打印版式扩展/薪资考勤/F_TYPE 3/FILTER 受控/元数据一致 | ✅ |
| M80–M85 | QuestPDF 打印体系/报表维护/LOGO/M_URL 现代化/分组小计 | ✅ |
| M86 | URL 驱动路由契约（NEW_URL/MODI_URL 全量消费） | ✅ 2026-08-15 |
| M87 | 解批前置 NOT_BACK_FIELDS + 表单浏览模式批核/解批/打印 + 页头英文名 | ✅ 2026-08-15 |
| M88 | 1515 打印版式闭环 + LOGO 路径兼容（24/24） | ✅ 2026-08-15 |
| M89 | 通用工作流引擎 v2（动态条件/会签/跳转/自动执行） | ✅ 2026-08-15 |
| M90 | 工作台业务增强（FORM_BUTTONS/IF_COPY/SEARCH_1/2）+ 报表 SP 放行 | ✅ 2026-08-15 |
| M91 | HR 分析报表 7 张全恢复 + 一模块多报表按 REPORT_ID 解析 | ✅ 2026-08-15 |
| M92 | 完整权限管理（2306 + 2305） | ✅ 2026-08-16 |
| M93 | DATA_FILTER 完整化 | ✅ 2026-08-16 |
| M94 | SP 领域等价移植收官 + 字段收敛 | ✅ 2026-08-16 |

## 8. 文档维护纪律（防滞后）

- **事实单一归属**：git = 历史；本文 = 现状；`docs/decisions/` = 为什么；内部资料目录 = 下一步。
- **任务流水不进本文**：不追加「状态更新：<日期>」流水条目，不写"改了哪些文件、跑了哪条命令、第几次实测失败"。
  过程与证据归 git 历史、`docs/decisions/`（为什么）与 内部资料目录（下一步）；本文只落**结论与当前状态**。
  （本文件曾因流水累积到 1.49 MB、其中 80% 是流水，故立此条。）
- **收尾即刷新口径**：任务结束按结论更新对应节——基线（§2）/ 收口（§3）/ 进行中（§4）/ 待决策（§5）/ 技术债（§6），
  并把完成计划移入 内部归档目录；没有结论变化就不动本文。
  `check-docs.ps1` 按 git 元数据校验「status.md 落后 HEAD 多少个提交」，超过阈值（默认 30）才提示——
  本文按结论刷新，**不为每个提交都改动本文**（本文已纳入版本控制，见 `.git/info/exclude` 里
  对 `docs/status.md` 的显式放行；未入库的工作副本会跳过此项校验）。
- **收口随版本归档**：§3 只保留**当前版本周期**（最近一次发布之后）的收口；每次发布后把已出货的收口行移入
  内部归档目录，本文不留历史批次。
- **台账只留活项**：§5 状态列已变 ✅、§6 已收口且无残留的行，**当场移出**到
  `docs/plans/archive/status-已收口登记-<日期>.md`，不在"待决策 / 技术债"节里留完工纪念。
  判据是"这条今天还限制我的做法吗"——不限制就是历史，历史归 git 与归档。
  移出的只是叙述；**仍生效的约束必须先落到 `LESSONS.md` / `docs/guide/` / `docs/decisions/` 再删**。
- **数字不手抄**：回归/扫描数字一律引用 `本机证据` 下自动生成的报告。
- **决策即文档**：任何拍板当场写 `内部资料目录decision-ledger.md`，本文只引用。
- **陈旧自动查**：开工前运行 `pwsh scripts/check-docs.ps1`，发现本文落后 HEAD 或活跃计划长期未刷新会报错。
- **README 不复述里程碑**：项目级概述见 `README.md`；里程碑历史在 git + 归档，不在 README 累积。

## 9. 文档导航

| 想了解 | 读 |
|---|---|
| 项目是什么/怎么跑 | `README.md` |
| 必须遵守的规则与验证命令 | `AGENTS.md` |
| 当前状态/待决策/技术债 | 本文 |
| 为什么这么设计 | `docs/架构.md`、`docs/decisions/ADR-001-*.md` |
| 业务决策 ledger | `内部资料目录decision-ledger.md` |
| 进行中的计划 | 内部资料目录（仅活跃；完成即归档到 内部归档目录） |
| 旧系统行为/风险 | `本机资料` |
| 产品愿景与阶段路线 | `本机资料` |
| 数据字典/模块树 | 不落文档，**以库内实测为准**（`MODULES`/`TABLES`/`FIELDS`，或 `EOS.API` 只读元数据接口） |
| 历史明细 | git log + 内部归档目录 |

