# Decision Ledger（决策台账）

> 最近更新 2026-10-10（创建 2026-08-16）：改名 `decision-ledger.md`、表格归位并按 `#N` 排序、超长行压缩；**编号与小节号一律未动**。
> 用途：把需要业务/顾问拍板的存量事项一次性列清，逐项确认后登记处置（实现 / 登记 / 下线）。
> 背景：自动化验收已全绿（CRUD / 语义 / 元数据），剩下的是**业务规则**，不能由 Agent 臆断。
> 文中「内部资料 / 内部归档 / 本机证据」指引的是**维护者本机**的内部文档与运行输出（不入库、不随仓库分发），不是本仓库中的路径。
> **编号纪律**：ADR 等文档按「决策 #N」「决策清单 §N」引用，**不得重编号、不得回收空号**；新增一律取当前最大号 +1。
> 口径：本文只登记**结论与处置**；过程叙述（对拍证据、重跑读数、夹具与脚本路径、逐轮追加的原文分层）归各 ADR 正文，
> 原文级明细另存于维护者本机（不随仓库分发）。

## 决策记录（确认后在此登记）

| # | 事项 | 确认结果 | 处置 |
|---|---|---|---|
| 1 | BANK 空表 | ✅ 生成测试数据 | EOS-17 幂等插入 3 条常用银行（CMB/ICBC/BOC），可被真实数据替换 |
| 2 | PRE_RECEIVE_DATE 推算 | ✅ A：确认基准后服务端实现 | 旧公式确认：对帐月下月首日 + CLIENT.PAYMENT_DAY 天数； |
| 3 | 1408 默认单别 | ✅ 默认 CHTZ | EOS-17：CHTZ（历史挂幽灵模块 131、无历史使用）改挂 1408 并设默认，CHPC 置非默认； |
| 4 | 170203 预付约束 | ✅ 允许无采购单 | 现代 DomainRuleService 仅校验"引用了采购单"的行（INNER JOIN），无采购单行不校验——已符合，登记 |
| 5 | 1515 返工覆盖语义 | ⚠️ 用户提参数化疑问 | 调研：旧 SP P_WF_MOC_PRODUCT_OUT 批核时固定**扣减** |
| 6 | 1610/1616 报表权限 | ✅ ADMIN 全部权限 | EOS-17：admin SYSDD 更新为 EXEC_TAG=Z + 全权限位=1， |
| 7 | ERROR_NO_SAVE | ✅ A：登记不实现 | 旧系统无消费逻辑（38 模块标记但从未实现），不揣测规则 |
| 8 | 1305 库存日志可写性 | ✅ 只读 | 旧 View_Master 双 URL 为空即只读；1305 移出表单白名单， |
| 9 | 1403/1415 客户询价单重复 | ✅ 下线 1415、保留 1403 | EOS-18：M_TAG=0 幂等迁移； |
| 10 | AUTO_APPROVE=1 的 57 模块 | ✅ 保存即确认逻辑正确 | 无需逐模块确认，解除放量门， |
| 11 | 带 FILTER 的 4 个候选 | ✅ 系统校验通过即放行 | 受控解析已验证（M9/M93）， |
| 12 | update.sql 冻结为唯一升级入口 | ✅ 冻结，不再追加（2026-08-22 修订：EOS.ERP 唯一库，新库对象走 DbUp 迁移） | update.sql 保留为本客户升级史 + 模式库；EOS.ERP 引入 DbUp 版本化迁移（`ErpDatabaseInitializer` + `Migrations/`）作为新库对象通道（2026-08-22 已启用） |
| 13 | 统一工具栏组件（ErpCommandBar） | ✅ 封装 | 新建 `ErpCommandBar` 中央动作注册表（新增/编辑/查看/复制/删除/批核/解批/结案/未结案/打印/导出/通用查询/附件/刷新等统一图标+标题），工作台与统一表单共用同一组件与表意；FORM_BUTTONS 白名单补 endcase/unendcase/delete（2026-08-22） |
| 14 | 表单工具栏按钮样式 | ✅ 图标+文字 | **2026-08-24 推翻「30×30 纯图标」约定（纯图标不直观）**：`ErpCommandBar` 业务动作按钮统一为「图标 16px + 动作名」（`.erp-command-btn`，hover `title` 保留动作名说明，**统一高 28px、宽度随内容自适应、左右各 10px 内边距**），工作台工具栏与统一表单工具栏（浏览态 back/prior/next/new/copy/edit/help/批核\|解批/结案\|未结案/打印/附件）一并生效；保存/取消（`label` 文字按钮）不变；工具栏纯图标下拉触发器（分组/导出分割）用 `.erp-command-icon-btn`（28×28）；导出主按钮=图标+「导出\|导出所选 (N)」文字，旁置 ChevronDown 分割按钮选 CSV/Excel |
| 15 | 统一表单结案/未结案 | ✅ 实现 | 新增 `POST /api/document-workbench/{moduleId}/endcase\|unendcase`（FINISHED_TAG/FINISHED_PERSON/FINISHED_DATE 参数化更新 + sys.columns 列存在校验 + ENDCASE/UNENDCASE_TAG 权限门，对齐旧 DoFinishOne type=1/2） |
| 16 | 统一表单附件 | ✅ EOS.ERP 唯一库内新表 | EOS.ERP 内新增 `ATTACHMENT` 表（DbUp 迁移 `Migrations/001_attachments.sql`，全大写对象：模块/主表/结构化主键 JSON/项次/文件名/客户端名/MIME/大小/SHA256/备注/上传人/上传时间），文件存 `Attachment:StorageRoot`；端点 list/upload/download/delete/remark 均按 FILE_VIEW/UPDA/EDIT/DELE_TAG 服务端强制校验；**不展示旧 SYS_FILE 存量附件**（决策：只做新附件）；后续向量化经 EOS.API 授权读取 |
| 17 | 侧边栏默认闭合 | ✅ 默认闭合 | AppShell 进入系统初始不再自动展开第一个分组（基本参数），仅用户点击或导航命中时展开 |
| 18 | ADR-005 架构治理 | ✅ 已提交（用户指示 2026-08-23）；阶段 1~4 + 四个挂起项全部完成（2026-08-23） | ADR-005 草案修订并入档（方向批准）；阶段 1~4 全部完成；四个挂起项落地（/api/v1 整体切换、权限缓存、运行时 Definition 快照 + definitionVersion 贯穿、日志 MCP 生产访问开关），回归 4/4 + 八链 E2E 全过；ADR 全部条款落地，按复审条件触发复审 |
| 19 | 1515 返工覆盖语义 | ✅ 对齐旧系统固定扣减 | 批核固定 `FINISHED_QTY -= 出库 QTY`（解批反向），不做参数化；运行态已实现（2026-08-23 确认） |
| 20 | 3302/170204/2002/3001-3004 数据前置 | ✅ 语义化测试数据解除 | 保持旧系统必填/引用约束；按业务语义生成测试数据后重跑自动放量（2026-08-23 立项，见 §17） |
| 21 | I4 历史推送清理 | ✅ 忽略（用户指示 2026-08-23） | 不轮换密码、不清理历史；含本机 SQL Server SA 明文密码的历史提交仍在 origin/main 上，风险已登记知晓 |
| 22 | AI 试点评估复核 | ✅ 忽略（用户指示 2026-08-23） | 暂不复跑 `EOS.Eval/run_eval.py`；后续需要时再启动 |
| 23 | 语义化测试数据执行 + 6 模块放量 | ✅ 完成（2026-08-23） | 3302/170204/2002/3001/3002/3004 全部 crud-pass（白名单 264）；回归 4/4；8 个复跑模块数据待后续 |
| 24 | P0 复跑验收 + 3305/3306 引用链 | ✅ 完成（2026-08-23） | 7 个复跑模块（1404/1411/1412/1517/1519/2910/180206）验收全过；1207 已下线剔除；3305/3306 完整度 100% |
| 25 | P1 元数据治理：1304/110308 | ✅ 完成（2026-08-23） | 两个模块放量成功（白名单 266）；1304 主表 PRO_NO 为旧 SYSQL_DEFAULT 虚拟字段残留、MONTH_DATE 校验已修复；回归 4/4 |
| 26 | P3 技术债：f_get_company_desc 清理 + 候选池收口 | ✅ 完成（2026-08-23） | 死代码已 DROP（无引用，源码留 VS 项目）；候选 CSV 清理 1305/2405/290103/290104/290105，预筛 137/137 已启用 |
| 27 | 1404/1416 报价单重复 + 1415 询价单物理删除 | ✅ 保留 1404/1403、物理删除 1416/1415（2026-08-23 用户拍板） | 用户确认「同类单据只保留一个入口」：1404/1416 报价单保留 1404（使用习惯 ID），1416 规则（自动单号/工作流 P_WF_COP_QUOTE/打印/批核联动客户计价表/单据性质/报表）迁移至 1404；1415 客户询价单（2026-08-19 已下线）与 1416 一并物理删除。EOS-19 DbUp 迁移 `Migrations/005_quote_module_consolidation.sql`：BILLKIND/SYSDD/SYSDH/WFFORM/REPORT/FIELDS/ATTACHMENT 归属归并，MODULES 1416/1415 物理删除；业务数据与审计日志保留 |
| 28 | 工作台导出收敛 | ✅ 仅导出所选（2026-08-24 用户拍板） | 同一导出按钮按选中行切换：有选中显示「导出所选 (N)」、无选中时主按钮/格式分割按钮禁用（不再直出当前筛选全部）；`handleExport` 未选中分支移除（测试改为断言禁用） |
| 29 | 面包屑层级与单号 | ✅ 表单页上抛单号（2026-08-24 用户拍板） | 标题格式「模块名-编辑/查看/新增」；编辑/查看经 `FormBreadcrumbContext` 由 `FormEditorPage` 上抛具体单号（`extractDocNo` 跳过单别/类别类主键列），新增/复制不显示单号 |
| 30 | 单据状态按钮禁用 + 工具条权限矩阵 | ✅ 禁用而非隐藏（2026-08-24，旧系统 DxAuthentication 求证） | 已结案（FINISHED_TAG=true）结案/解批/编辑/删除禁用；已审批（CONFIRM_TAG=true）批核/编辑/删除禁用；批核/解批共用一个按钮位互斥（未批核显「批核」、已批核显「解批」，旧系统 chk_CONFIRM_TAG 切换文本语义）；**按钮文字**：已结案显「取消结案」、未结案显「结案」（`unendcase` 文案「未结案」更正）；**工具条权限矩阵**：新增/复制 `canAddNew`、编辑 `canEdit`、删除 `canDelete`、批核 `canApprove`、解批 `canDeapprove`、结案 `canEndCase`、取消结案 `canUnEndCase`、附件 `canFileView`，打印沿用旧系统 Browse 即打印（浏览态隐含 CanBrowse）；**服务端补强**：`WorkbenchCommandHandler.UpdateRecordAsync` 增已结案/已批核禁编辑校验（`FINISHED_EDIT_FORBIDDEN`/`CONFIRMED_EDIT_FORBIDDEN`，对齐删除补偿守卫） |
| 31 | 180218 员工批量发卡归类特殊页 | ✅ 归类特殊页 + 清空 MODI_URL/NEW_URL（2026-08-26 用户拍板） | 180218 实际以批量作业页交付（`/jobs/card-batch` + JobPage，`JobsController` 权限门 180218），但 MODULES.MODI_URL 指向 `/document-workbench/{moduleId}/edit`（统一表单）且不在白名单，经工作台触发编辑即落入未移植统一表单。EOS-20 DbUp 迁移 `Migrations/007_employee_card_batch_special_page.sql`：清空 180218 的 MODI_URL/NEW_URL、REMARK 登记结论、防御性清理脏标记/快照（幂等，已实测执行）；单卡维护 180208 走 employee-card 领域规则并在白名单内，不受影响。SP 台账补登 180218 结论（特殊页，不移植，见 `sp-porting-inventory.csv`） |
| 32 | 跨模块关联字段浏览（BROWSE_URL） | ✅ 同页签导航 + 键不可解析降级列表链接 + 一次性全量（2026-08-26 用户拍板） | 替换旧系统 BROWSE_URL 双击查看：服务端 `WorkbenchBrowseResolver` 在 Definition 构建时把 `BROWSE_M_IDX + BROWSE_URL` 模板解析为 `browseModuleId + browseKeyFields`（目标工作台可达性 + 主键按 key_ordinal 序映射 + 来源列 sys.* 校验 + 白名单判定）；特殊页目标不下发（纯文本）；键无法解析或目标未启用统一表单 → 降级目标模块列表链接；键源列不在可见列时查询组件隐藏随行下发（复合主键可用）。权限：前端 bootstrap 权限位（UX 门）+ 目标 `/view`、`/record` 端点 CanBrowse + 数据范围最终授权（修正旧系统无目标权限门越权漏洞）。见 `docs/status.md` §3 |
| 33 | 工作台 URL 重构（A 档 + /workbench 前缀） | ✅ 采用 A 档 + 前缀定名 /workbench，否决 B/C（2026-08-26 用户拍板） | 浏览器路由 `/document-workbench` → `/workbench`，view/edit 记录主键从 JSON query 迁入路径段（`/workbench/{m}/view/{主键段...}`，主键序、天然支持复合键），`from` 留 query；否决根级 `/1405`（跨端契约变更大）与人类可读 slug（过度设计）。EOS-21 DbUp 迁移 008（MODULES 元数据 + Definition 快照 JSON）、后端 ModuleRouteValidator/MenuAdminRepository/SnapshotService、前端 workbenchPath.ts 统一组装、API 路径 `/api/v1/document-workbench` 不变；一刀切不保留旧浏览器路径兼容层。见 内部归档《工作台URL重构.md》 |
| 34 | 工作流启用试点（在途守卫 + 1906 二级审批） | ✅ 试点 1906 + 在途守卫/查看单据/flowState 撤回落地（2026-08-26 用户指示执行） | 工作流引擎 v2/v2.1 此前零生产配置（WFFORM 空）。本次落地：① 在途流程守卫——编辑/删除/重复送审按 `WF_MONITOR WF_STATE='0'` 拒绝（`FLOW_IN_PROGRESS_EDIT_FORBIDDEN`/`FLOW_IN_PROGRESS_DELETE_FORBIDDEN`/`FLOW_IN_PROGRESS`，须先撤回）；② 审批待办「查看单据」——FlowTask 返回主键值数组 + `/workbench/{m}/view/{主键段}` 链接；③ 表单流程状态感知——record 端点返回 flowState（None/InProgress/Completed/Withdrawn），浏览态在途时禁批核/编辑/删除 + 显示撤回；④ **Withdraw API 安全加固**——`WithdrawRequest` 由传 KEY_VALUE 条件串改为主键值数组，服务端经主键元数据重建（消除 SQL 注入隐患）；⑤ 试点选型 **1906 油卡充值单**（主子表 + P_WF_CAR_FEE + 白名单 + 不在 E2E 直接批核断言清单），二级审批 001 admin / 002 aaron（迁移 `010_pilot_workflow_1906.sql`，REMARK 幂等）。见 `docs/status.md` §3 |
| 35 | 工作流商用级收口（2101 流程设计器 + 2103 流程监控 + 表单审批体验） | ✅ A→B→C 全做（2026-08-27） | 流程设计器 2101（FlowDefinitionController/FlowDefinitionService，按 CONFIRM_TAG/SYSDL 校验）、流程监控 2103；批核改送审弹窗写 AUDIT_EVENT FLOW_SUBMIT、浏览态审批历史弹窗；迁移 013 收敛 M_URL；2101 移出统一表单白名单（266→265）。 |
| 36 | 数据选择统一走统一选择器 | ✅ 强制规则（2026-08-27 用户拍板） | AGENTS.md「统一选择器规范（强制）」+ docs/架构.md「统一选择器」；员工选择器重构为 `user-admin.employees` sourceKey（元数据列/默认列/未开户过滤/2306 权限门，提交 9712b22）；`loader` 仅存量过渡、新代码禁止新增 |
| 37 | 2310/2312 数据表数据维护下线 | ✅ 整体下线、物理删除（2026-08-28 用户拍板） | 旧系统两菜单是数据清理工具（2310=整表 TRUNCATE；2312=按日期/客户/供应商区间清历史+子表孤儿清理），现代不移植任意表破坏性清理（清库属 DBA/脚本操作）；现代受控只读浏览页是全系统唯一绕过成本/保密/禁止字段过滤与 EXEC_TAG/DATA_FILTER 数据范围的原始数据窗口，一并删除；表结构巡检由 2302/2303 承担、业务数据查看走各模块工作台。EOS-23：DbUp 迁移 `016_table_data_modules_offline.sql`（MODULES 2310/2312 + SYSDD/SYSDH 权限行物理删除，审计留档不删）；代码删 `TableDataPage`/`TableDataController` tables+data 端点/`/admin/table-data` 路由与 ui-verify 段（同控制器 2303 field-audit 端点保留）。连带：2202/2203/2204 打印版式设置按用户口径验收通过，右上角按钮补图标+文字标准样式（`.erp-command-btn`）收口 |
| 38 | 字段数据来源模型重构（数据选取） | ✅ 彻底重构（2026-08-28 用户拍板，ADR-008） | 独立数据源表 `FIELD_DATASOURCE`（任意数量+FK 级联）+ 多数据源各是各的入口（弹菜单选来源）+ 过滤条件纯结构化（无 SQL 后门，保存即校验）+ 回填映射有序 JSON + FIELDS 四组 24 列物理删除（`CHOOSE_PAGE/MULTI/ONLY_CHOOSE` 保留）+ 字段设置 UI 统一选择器选表/两步式构建。存量 `CHOOSE_FILTER` 三档规则化转换（实测 1008 单元格/291 去重：~70% 直转、~28% 待 P3 算子扩展、~4% 人工清单）+ 验证护栏（试编译+对拍）。ADR-008 已接受；**实施已全部完成（2026-09-19 回填）**——P1 存储迁移 + P2 字段设置 UI 构建器 + P3 条件编译器 + P4 回填映射构建器 + 字段设置全页化 + 治理收口（迁移 028/037/038/039/040/041）均已落地 |
| 39 | 2205 报表过滤条件设置归类定制页 | ✅ 移出统一表单白名单，定制页承载（2026-08-28 用户拍板） | SYSQR_DA + SYSQR_DEFAULT 按模块维护报表查看器条件面板默认条件；工作台承载错位：主表无业务字段（单据骨架空壳）、F_EXPR 类型化 DSL 无写侧校验（坏配置运行时静默降级）、与 2201/2202-2204 同族。粒度为模块（M_IDX）与 2201 报表粒度不同维，独立成页 `/admin/report-conditions` 不并入 2201。写侧校验镜像 ReportRepository 运行时解析（字段白名单/选项 DSL/数据源语句+物理存在 fail-closed）；新 sourceKey `report-conditions.fields`（权限门 2205）；迁移 019 M_URL 收敛；白名单 262→261。已知缺口：报表查看器暂不渲染 F_TYPE 5 条件，另行立项。见 内部资料《定制页面清单.md》 2205 归类记录 |
| 40 | 2205 页面改版单表 + 孤儿模块条件处置 | ✅ 改单表落地；~~孤儿 12 行已登记待决策~~ → **孤儿行已按用户答复删除**（2026-09-15 答复"关闭并物理删除"，删 `SYSQR_DA` 2 行 + `SYSQR_DEFAULT` 12 行）；**2026-09-19 库内复核：两表中模块 `149812`/`18069806` 命中均为 0 行** | 页面 /admin/report-conditions 取消主子表改单张大表（弹窗选模块、新增 GET /report-conditions/conditions/all）；旧系统 RPT/SysqrDft.aspx 证实 2205 非只读；孤儿条件系 SYSQR_DA/SYSQR_DEFAULT 中模块 149812、18069806 共 12 行。 |
| 41 | 报表渲染器定案（P6 关键选型） | ✅ QuestPDF + 可视化设计器，放弃 Typst（2026-08-30 用户拍板；2026-08-31 ADR-010 决策 6 补充） | 放弃 Typst（外部 Rust 引擎、与 C# 不契合）；**继续使用 QuestPDF**（C# 强类型；**24 张内置版式按 ADR-010 决策 6 统一迁移为 `layout.json`、对拍后 C# 命令式版式退役，非"零重写"**），L3 客户自助版式以「**可视化拖拽设计器 + 布局 JSON 解释层**」交付——QuestPDF 是命令式绘图库可被声明式描述驱动，写 `QuestPdfLayoutRenderer` 读 JSON 布局逐元素绘制，推翻"编译期绑定无法满足 L3"旧论断（§9.7.1/§10.3）。P6 只做 L0/L1/L2 内置版式 + 渲染抽象接口 + `layout.json` schema 骨架，**不做设计器 UI**（L3 已于 2026-08-31 由 ADR-010 定案）。ADR-009 已同步修改（§9.3 澄清、§9.7.1 定案、§10.3 选型、§12 格式包 template.typ→layout.json、P6 执行准则、替代方案 F、复审条件） |
| 42 | ADR-008 字段数据源治理（残余技术债清偿） | ✅ **四桶全部收口（2026-09-19 回填）**：迁移链 `028`（A 豁免 10 + D 清空 3）→ `037`（B 补 `QUERY_RELATION`，8 张源表）→ `038`（重跑回填 15 行 `FILTER_STRUCT`）→ `039`（死配置清理）→ `040`（21 MANUAL + 10 EXEMPTED 共 26 条按业务意图重配，`ChooserFilterValidator` 26/26 PASS）→ `041`（按守卫 DROP 一次性审计队列 `CHOOSER_FILTER_MIGRATION_LOG`，前提为无 `PENDING_P3`/`MANUAL`/`DRIFT` 行）；归档 本机证据：fields-chooser-migration/migration-log-archive.csv 实测 **CONVERTED 966 / EXEMPTED 4 / CLEANED 3、MANUAL 0** ⇒ **原计划的 C 桶"顾问用 CanSetup 重建"不再需要**。原文：A/D 已落迁移（2026-08-30 用户拍板：**A+D 先清干净 → B 出清单 → C 顾问重建**） | 分诊 ~50 行 MANUAL 为 A/B/C/D 四桶：A 豁免 10 行（引用表不在 QUERY_RELATION，fail-closed）、D 脏数据 3 行（FILTER_STRUCT 清空）、B 补 QUERY_RELATION ~21 行、C 人工重建 ~15 行（HR/PUR/FIELDS 复杂条件）；ParseReturnMapping 死代码已删。 |
| 43 | ADR-009 P3 T_SQL 四表物理删除（SYSQD/SYSQL/LISTREPORT/LISTREPORT_CONDITION） | ✅ 物理删除（2026-08-30 用户拍板） | ADR-009 §3.3 三张同构「用户存 SQL」表 + LISTREPORT 条件树子表一并物理删除。审计（本机证据：ADR-009-P3-tsql-tables-audit.md）证实现代系统零消费、旧系统消费页（QueryDetail/QueryAnalyser/RptList）在现代重构中未移植，ADR §4「不再新增用户运行时存 SQL」→ 物理删除。落地：DbUp 迁移 **`030_tsql_tables_drop.sql`**（幂等 DROP 四表）；代码清理 `FieldAdminRepository` 移除 `LISTREPORT_CONDITION` 引用（计数列+DELETE），`SYSQD_CONDITION`/`SYSQL_FIELDS`/`SYSQL_CONDITION`/`SYSQL_COND_DFT` 为活表保留。遗留：旧系统 SP `xp_user_listrpt_fields` 引用已删表（现代零调用，旧库小写遗留对象暂不处理）。**运行时生效待用户重启 EOS.API**（DbUp 自动执行 030） |
| 44 | 单据页头 Title 语义（开单方主体）+ P6 实现偏差修正 | ✅ 用户拍板 + 已修（2026-08-30；优先级链 2026-08-31 拍板） | 单据页头 Title = 开单方主体（非往来单位名），CLIENT.HEADER_ID = 默认开单方页头；P6 偏差：DocumentPdfService 曾以 CLIENT.FULL_NAME_CN/EN 覆盖页头公司名，已删；优先级链：用户选择（SYSQR/HeaderId）→ 客户默认（CLIENT.HEADER_ID）→ 报表默认（REPORT.HEADER_ID）；ADR-009/ADR-010 同步。 |
| 45 | ADR-010 可视化版式设计器（L3 专项） | ✅ 全部 6 项决策拍板（2026-08-31） | ADR-010 由草案转「已接受」：① 布局=绝对定位（一期只做 document）；② 形态=自建 UI + 开源拖拽原语（dnd-kit / react-moveable），否决引入报表设计器组件；③ 存储=内置版式 Git 资产 + 客户定制库表（REPORT_FORM_LAYOUT + REPORT_FORM_BINDING）；④ 安全=保存即校验 + 渲染二次校验 + 三档权限门（开发 Git 门 / 实施顾问 CanDesign / 客户维护 CanAdjust）；⑤ 能力=双模式（完整 / 微调）；⑥ 单一真源=layout.json 唯一真源、24 张 C# 命令式版式 S1 迁移后退役。目标用户三类（开发 / 实施顾问 / 客户维护）；节奏=上线前必备、S1 门槛=L1/L2 + format.json 落地。ADR-009 §9.7.1 待决策清单同步闭合 |
| 46 | 提交信息格式规范（Conventional Commits） | ✅ 纳入强制规范（2026-09-05 用户拍板） | AGENTS.md 新增「提交信息格式规范（强制，2026-09-05）」：提交信息一律 `<type>(<scope 可选>): <描述>`，type 用英文小写（feat/fix/refactor/perf/test/docs/style/chore/build/ci/revert），描述用简体中文可携任务标识，破坏性变更加 `!`，一次提交只做一类事；**向前生效，不要求改写历史提交**（用户个人历史手工提交保持原样） |
| 47 | ADR-011 M10 语义层轨道立项 | ✅ 立项（2026-09-05 用户拍板） | M1-M9 两轮验收完工后启动高风险语义推断轨道：按 ADR-011 复杂度重估拆 M10a（可行性探针：受控子集 parser/validator，编译与拒绝、不取业务数据）→ M10b（最小闭环：首条业务确认指标真实计算 + 权限注入 + 依据链 + `resolve_metric`）→ M10c（逐类扩展 + 业务对账）；`REPORT_METRIC` 增业务确认状态列（`CANDIDATE`/`CONFIRMED`，未经确认拒答）；`DEFINITION` 原文永不执行、不引入通用 SQL 执行器、不新建平行口径表。施工蓝图见 内部资料《M10-语义层立项计划.md》（M10b 开工前置：用户/顾问对 `sales_amount` 完成对账确认） |
| 48 | M10b 首条确认指标 `sales_amount` 口径 | ✅ 选项 A：只算已批核单据（2026-09-05 用户拍板） | `sales_amount`（销售额）= `SUM(COP_ORDER_D.AMOUNT_TAX)` 且主单 `COP_ORDER_M.CONFIRM_TAG=1`，不含草稿/未批核单据；含税（`sales_amount`）/未税（`sales_amount_ex`）两个种子并存的拆分确认无误。"仅已批核"行过滤属口径语义的一部分，须以结构化方式（非 SQL 文本拼接）进入 M10b 受控执行计划并纳入依据链过滤摘要；未落地前 `sales_amount` 维持 `CANDIDATE` 拒答。对账流程（Agent 手工 SQL 出 2~3 个场景期望值 → 实现后自动比对 → 一致置 `CONFIRMED`）见 内部资料《M10-语义层立项计划.md》 §3/§7 |
| 49 | 操作日志库 v2：去 SYSDF 兼容、原地升级 AUDIT_EVENT | ✅ 原地升级（2026-09-05 用户拍板，不新建平行表） | 迁移 047：`AUDIT_EVENT` 增 `TRACE_ID`/`ACTOR_DISPLAY_NAME`/`CLIENT_IP`/`USER_AGENT`/`REQUEST_METHOD`/`REQUEST_PATH`/`ERROR_CODE` + 执行者/结果索引；`WorkbenchAuditWriter` 删 `SYSDF` 双写（`SYSDF` 只读保留历史）；`GET /api/v1/audit` 改查 `AUDIT_EVENT`（模块/动作/执行者/结果/时间过滤）。运行时生效待用户重启 EOS.API |
| 50 | 文件日志只保留 Warning+（正常请求不落盘） | ✅ Warning+ 落盘、慢阈值 1s（2026-09-05 用户拍板） | `JsonFileLoggerProvider` 加最低级别门（文件 Warning+，控制台仍全量）；慢成功请求（≥1s）记 Warning；`LogQueryService` 补读 `.2` 轮转文件；正常 `correlationId` 的 trace 在文件侧查不到属预期。运行时生效待用户重启 EOS.API |
| 51 | eos-api-log MCP 独立性 + 前端错误上报 | ✅ MCP 暂不拆（2026-09-05 用户拍板）；前端错误上报落地 | MCP 继续随 EOS.API 进程内承载（文件级查询本身不依赖 DB，审计留痕已是 best-effort）；独立进程另立任务。前端全局 error/unhandledrejection 经 `POST /api/v1/client-errors`（匿名、严格限长）记 Warning（`client_error` 事件）入同一 JSONL 管道供 MCP 关联 |
| 52 | 日志 MCP（含 REST 查询栈）整体拆除 | ✅ 全部拆除（2026-09-05 用户拍板，推翻 ADR-005 §5.4） | 进程内 MCP 在 API 挂掉时恰恰不可用（排障工具依赖被排障对象），且功能被文件直读 + mssql 查 `AUDIT_EVENT` 完全覆盖；每次 MCP 查询还写 `LOG_QUERY` 审计行、污染操作日志。删除 `LogMcpTools`/`LogQueryService`/`LogQueryController`/`LogQueryModels`、`LogMcp` 授权策略、`MapMcp` 端点、`ModelContextProtocol.AspNetCore` 包依赖、`E2eAuditLogs.ps1` 查询/MCP 段、`.codebuddy` 本地排障技能及三端 MCP 配置；`LogRedactor` 保留（记忆敏感值脱敏仍在用）。排障 SOP 改为文件尾部 + mssql（见 AGENTS.md「日志排障」）。ADR-005 §5.4 标记为被本决策替代 |
| 53 | 日志相关表退役（SYSDF / LOG_QUERY 行） | ⏸ 暂不处理（2026-09-05 分析完成，用户拍板先不动） | 分析结论：现代代码对 `SYSDF` 已零读写（仅注释与带 `OBJECT_ID` 守卫的旧迁移 PRINT）、无存储过程引用、无 FK 被引用；旧系统已确认下线，技术上已具备归档后 DROP 条件。`SYSDF` 14,377 行历史、`AUDIT_EVENT` 2,163 行中 86 行 `LOG_QUERY`（MCP 已删、不再新增）。动手时：先归档备份 → 迁移 DROP `SYSDF` → `DELETE AUDIT_EVENT WHERE ACTION='LOG_QUERY'`。此前迁移 005/016“历史审计保留供追溯”的登记随 DROP 失效，需同步更新 |
| 54 | M10c 幽灵口径处置（销售毛利/库存周转率/收料/生产/完工数量） | ✅ 用户授权 Agent 决定（2026-09-05）：全部维持 CANDIDATE 暂缓放开 | 5 条种子口径的 DEFINITION 均引用来源表不存在的列（经 sys.columns 核实：sales_gross_margin→COP_ORDER_D 无 COST_AMOUNT；inventory_turnover→INV_PRO_DEPOT 无 IN_QTY；receive_qty→PUR_RECEIVE_D 无 RECEIVE_QTY；produce_qty/produce_finished_qty→MOC_PRODUCE_D 无 QTY/FINISHED_QTY，该表实为用料组件明细）。validator 生产库确定性拒绝，运行时不会产出错误数值；真实业务口径（毛利成本来源、周转率分母、制令/收料数量定义）待出现实际应用场景后由业务澄清，经新迁移修正 DEFINITION + 对账后再放开。已同步：inference 评估集对应 6 条样本改为「如实说明尚无定义」拒答题（迁移 052 放开的 account_receivable/payment_amount/purchase_amount/employee_count/inventory_qty 五条沿用已批核先例/无状态直放，已真库对账） |
| 55 | ADR-012 审核 R3：效果目录契约（24 键 vs ADR §2.3 初版 8 键） | ✅ 补契约（2026-09-06 用户拍板） | ADR-012 §18 效果目录 v0.2 正式登记 24 个已落库效果键（另保留 meta-link/flow-trigger/job-enqueue/legacy-sproc 四键）；每键参数 Schema、承载形态（公式行/服务参数二选一）与代码注册随 P2/P3 定义；P2 前禁止新增目录外键 |
| 56 | ADR-012 审核 R1：存量参数/反向结构契约（PARAM_STRUCT 内字符串表达式与 REVERSE_STRUCT 自由结构） | ✅ 先认账、后收口（2026-09-06 用户拍板） | ADR-012 §13.6 登记：存量 PARAM_STRUCT（服务效果参数，含 inventory-move.fieldMap）与 REVERSE_STRUCT 为翻译期占位形态；正式 Schema 于 P2 定义并随 2301 收口存量，定稿前不作为引擎输入；不提前返工 |
| 57 | ADR-012 审核 R2：效果定位键与 FIELD_RELATION 关系边 | ✅ 先做扩展设计、登记跟着界面走（2026-09-06 用户拍板） | 新建 内部资料《单据关系效果侧扩展设计.md》（草稿）：先定 FIELD_RELATION 效果侧扩展模型（复合键组/方向/键映射）与引用语法；79 模块存量 MATCH_STRUCT 不提前重写，随 P2 2301 首次编辑按模块登记并收口 |
| 58 | ADR-012 P3 灰度开关归属 | ✅ Agent 拍板（2026-09-06，用户授权）：方案 B + 全局总闸 | 开关=发布快照 Definition JSON 扩展段 `effectEngine.enabled`（随 2301 配置→保存即校验→发布→运行时全链一致，definitionVersion 即引擎行为版本锚，回滚=重发布关闭版快照）；另设 appsettings `EffectEngine:Enabled` 全局总闸（默认 false），灰度期出系统性问题一行配置止血。发布校验增一条：开关开启的模块其动作链所有 EFFECT_KEY 必须 `EffectRegistry.IsImplemented`。详见 内部资料《灰度开关选型.md》 |
| 59 | ADR-012 P3 影子对拍协议 | ✅ Agent 拍板（2026-09-06，用户授权）：按协议稿执行 | 旧 SP 与新引擎各自独立事务顺序执行整体回滚；差异归一化（时间截分钟、数量 0.01、单价金额 0.0001、解批反向流水按镜像取负折算、审计只比事件集合）；报告 JSON 落 本机证据：shadow/，PASS 判据=两侧同 status 且未归一化差异为 0。协议全文见 内部资料《影子对拍协议.md》，脚本随 Step 5 落地 |
| 60 | ADR-012 P3 服务键 Handler 证据缺口处置 | ✅ Agent 拍板（2026-09-06，用户授权）：考古定证据、缺证据键 fail-closed | 经旧 SP 考古：balance-adjust 金额来源=主表 AMOUNT_TAX（否则 AMOUNT，均缺则拒载）；BANK 余额列=BANK.AMOUNT；credit occupy/release=按单据金额±可用额度（解批反向）；prepay=±PREPAY_SUM；net-replace（1609/1418 订单变更单，主表无金额列，语义=释放旧占用+按变更重占）暂不实现、运行时明确报错；link-stamp finish=true 解释为回写目标 FINISHED_TAG=1（台账复核后可修订）；field-copy 形态自明直接实现。已实现：inventory-move/set-state/balance-adjust/link-stamp/field-copy 六键中五键 + 库存移动（净替换待证据） |
| 61 | ADR-012 效果引擎空值语义：空备品/空数量按 0 计算 | ✅ 确认引擎行为为预期改进（2026-09-07 用户拍板，与 1607 RECEIVE_QTY 空值修正同类） | 旧 SP `QTY+SPARE_QTY` 类跨列相加遇 NULL 整单作废（如 1406 解批 PRODUCT.NOT_SEND_QTY 回写），引擎按项 COALESCE 正常计算；公式加减项、校验 usage/limit 列表、条件 terms 求和一律空值按 0。此后此类差异记预期改进行为，不再逐项确认 |
| 62 | ADR-012 mrp-plan-alloc：1405 游标退化 vs intended 分段语义 | ✅ 采用 intended 分段语义（方案 A，2026-09-09 发布放行确认；否决复刻退化） | 旧 SP `P_WF_COP_ORDER` 游标段 `@pro_no` 声明后未初始化（ANSI_NULLS ON 下首行 IF 恒 UNKNOWN），实际恒 `PLAN_QTY=QTY/PLAN_SPARE_QTY=SPARE_QTY/DEPOT_QTY=0`，属偶发缺陷而非业务规则；按翻译台账 intended 语义实现（order-open 三字段分段；material-provide 制令族 `DEPOT_QTY=min(MRP,NEED)` 本单范围，SYSSS.PRO_MRP 门控）。对拍证据：shadow-1405-20260909-001（DD17110164，4 处差异全为 PLAN/DEPOT 新旧语义差，CLIENT/PRODUCT/主表 diff=0）、-003（DD16120140，2 处同源差异）、引擎单跑 -002 PASS 零残留。差异按「旧码退化差」登记关闭，不做归一化 |
| 63 | ADR-012 1418 订单变更：PRODUCT 净替换 2 倍差 + 批核无条件结案 | ✅ 引擎行为为预期改进（2026-09-09 对拍证据登记，沿用 #61/#62 口径） | ① 旧 SP `P_WF_COP_ORDER_CHANGE` 批核路径对 `PRODUCT.NOT_SEND_QTY/MRP_QTY` 施加 2×DELTA（覆盖前 L51 与覆盖后 L129 同号加总 `Σ(QTY+SPARE−OLD)`）；引擎 adjust-projection 施加 1×DELTA，即正确净替换（原单占用 9350 → 变更 9650 = +300；旧 600/−291、−375 精确命中 2×，新 300/9、−75 精确命中 1×）。② 旧 SP 尾部 `P_COP_ORDER_FINISHED` 批核无条件置 FINISHED_TAG=1（变更单明细 FINISHED_SEND_QTY=0 也置 1）；引擎 completion-close 按当前量重算（§16.9，9650>0 → 置 0）。对拍证据：shadow-1418-20260909-001（6 处差异全属上述两族，其余表 diff=0）；引擎单跑 -002 PASS 零残留。1418 已 v4 发布 EE=ON，运行中即引擎行为；差异按「旧码缺陷 vs 预期改进」登记关闭 |
| 64 | ADR-012 1405 订单批核：CLIENT.CREDIT_LIMIT_NUM 反号缺陷 | ✅ 引擎 usable-quota 方向正确（2026-09-09 对拍证据 + 旧库多 SP 佐证登记） | `P_COP_ORDER_CHECK` 以 `CREDIT_LIMIT_NUM < 订单金额` 判「客户信用余额不足」——字段即**可用额度**（越高越可用）；批核订单应消耗额度（下降）、解批恢复（上升）。旧 `P_WF_COP_ORDER` 却 `limit += 金额×@approve_tag`（批核上升）——反号缺陷，且与全库其它信用 SP 约定相悖：收款单 `+=`（收款恢复额度）、变更单释放 `+`/占用 `−`、采购单 `−=`（占用下降）。引擎 balance-adjust「usable quota：occupy=−/release=+，解批翻转」为正确语义。对拍证据：shadow-1405-20260909-005（DEAPPROVE，E2E148601：旧 1000000904−1356=999999548，引擎 +1356=1000002260；批核三样例信用全 NULL 故此前未暴露）。差异按「旧码缺陷 vs 预期改进」登记关闭 |
| 65 | ADR-012 1502 field-accumulate 制令号回写缺口 | ✅ 确认补回写（2026-09-09 用户拍板） | 旧 `P_WF_MOC_PRODUCE` 批核回写 `COP_ORDER_D/MOC_PLAN_MOC.PRODUCE_TYPE/PRODUCE_NO`（订单/计划行制令号，72 行历史数据在库），引擎 1502 链仅有数量累加行、保存链也不补——有订单引用制令批核时订单行制令号恒空、下游关联断裂。按旧语义补回写（link-stamp/field-copy 形态收敛夹具，主会话执行），当前库 MOC_PRODUCE_M.ORDER_TYPE 全空故潜伏，修复不改变现有数据 |
| 66 | ADR-012 B 桶三键行为拍板（H1–H4，2026-09-09 用户按建议确认） | ✅ 已定 | H1 hr-usage：**修正为按员工汇总（SUM）**——旧「join 仅 emp 后行覆盖」属缺陷（同员工多明细丢数量），不复刻；H2 mould 解批 else 分支：**修正为不减 `FINISHED_QTY`**（批核未加、解批减会负漂移），差异如实记「旧码漂移差」；H3 mould 解批 sort='2' 不回退 PRODUCT：**保留复刻**（批核/解批均不碰，无漂移）；H4 `recompute-excluding-self`：**登记入 `EffectStructSchemas.ReverseKinds`**（contract 解批语义精确需要，随 Handler 实现落地）。三键均需真 Handler，公式不可表达 |
| 67 | ADR-012 2815 生产出库 SEQ2 完工码方向反转（D1） | ⛔ **已撤销（2026-09-12，见 #75）** / 原结论：采纳状态对 pair（2026-09-09 拍板） | 旧 `P_WF_MOC_PRODUCT_OUT` 批核**无条件清** END_TAG=0；当初按 §16「状态标志按当前量重算」取状态对 pair（足量置 1、不足置 0，解批同式重算）。**2026-09-12 对拍推翻**：在「批核后制令仍足量」场景下与旧分叉（旧清 0 / 引擎置 1），旧过程实为**事件分支**（批核无条件清；解批足量才置 1 + 写日期取大）。用户选严格口径 A → #67 的 intended 偏差撤销，1515/2815/2816 改按旧事件分支对齐，详见 **#75** |
| 68 | ADR-012 2707 工序发料 M 侧 clearing（D2） | ✅ 接受 M 侧只写结案方向（2026-09-09 拍板） | 旧分支含 M 侧 `TAG=0/PERSON=''/DATE=NULL`，条件类型仅 `not-exists` 无可表达「exists」→ M 侧只写结案方向（1509/1609 同例）。登记技术债：条件类型缺 `exists`（marker-handler 或条件扩展时补） |
| 69 | ADR-012 2913 模房耗料 clear-finish 解批（D3） | ✅ 实施方案 C（2026-09-09 拍板） | 旧 `P_WF_MOU_GET2` 解批清 `MOU_APPLY_M/MOU_BATCH_M.FINISHED_TAG=0`。实施：`clear-finish` 登记入 ReverseKinds + `SetStateHandler` 解批消费（clear-finish=逆状态：bool 翻转/字符串清空/now→NULL；none/no-reverse=返回 0——顺带修正 1503/1514/1517/2805/2806 解批重写开工码的潜伏缺陷），提交 b33f752；2914 配置保持 clear-finish |
| 70 | ADR-012 报关族批核 field-copy 明细来源不足（300301~300305） | ✅ 已拍板并执行（2026-09-10，方案 A） | 引擎 field-copy 仅支持主表来源，sourceField=PRO_DESC 实引用不存在的 CUS_EXPORT_M.PRO_DESC（PRO_DESC 仅存明细）→ 四模块批核 blocked（EE=ON 下潜伏）。方案 A：删除 4 模块 SEQ1 field-copy、顺序号前移补缺口，business-action-seed.json 同步，5 模块标脏待重发布。 |
| 71 | ADR-012 1423 公式行条件旧格式与引用本单字段能力缺失 | ✅ 已拍板并执行（2026-09-11，方案 A + NULL 口径=算不良） | 1423 SEQ1 两条公式行条件用平铺 field:BACK_CODE，而编译要求 field 为 {scope,field} 对象 → 批核抛 InvalidOperationException 阻断。方案 A：EffectFormulaExecutor.BuildUpdate 为公式行条件补本单 MASTER 字段支持；EffectConditionCompiler 的 value-neq 新增 nullAsMatch 复刻旧 NULL→BACK_BAD；顺带修 value-neq 误用 EndsWith("EQ") 判等的潜伏 bug；新增 3 单测，待重发布验证。 |
| 72 | ADR-012 1423 退料单 inventory-move 引用不存在的 AMOUNT 列 | ✅ 已拍板并执行（2026-09-10，方案 A：detail 支持闭式常量项） | 1423 inventory-move.fieldMap.detail 含 AMOUNT，但 COP_BACK_M/D 无 AMOUNT 列（旧 SP 中 AMOUNT/CURR_ID/CURR_RATE/PRICE 是字面量，翻译误作列名）→ 批核 blocked、库存零写入。方案 A：fieldMap.detail 新增闭式常量项 {column,constant}（值参数绑定、常量项跳过列存在性检查），1423 四格按旧 SP 字面量 0/''/0/0 复刻，已带单测。 |
| 73 | ADR-012 300301/300304 出口报关 link-stamp ref 隐式主键→显式 refs（2903 同源缺陷） | ✅ 已修配置（2026-09-11，待重发布验证） | 300301/300304 SEQ1 link-stamp 用 Shape B 隐式 `ref:["ACCOUNT_TYPE","ACCOUNT_NO"]`（无 source，按语义映射到主表主键 CUS_EXPORT_M PK=EXPORT_TYPE/EXPORT_NO）→ 生成 `CUS_ACCOUNT_M.ACCOUNT_TYPE=M.EXPORT_NO AND CUS_ACCOUNT_M.ACCOUNT_NO=M.EXPORT_TYPE`，定位不到对账/关封单（旧 SP 按 `CUS_EXPORT_M.ACCOUNT_TYPE/ACCOUNT_NO = CUS_ACCOUNT_M.ACCOUNT_TYPE/NO` 关联），批核/解批不盖章——与 2903 同源；已迁移为显式 refs（`target←source` 同名列），DB 配置（`fixture/adr012-config-fix-d.sql`，幂等+自检+标脏）+ 种子文件同步；**待 2301 重发布 300301/300304 后双路对拍验证**。 |
| 74 | ADR-012 采购退料族（1608/1612）解批数量差一个单据量 | ✅ 已拍板并执行（2026-09-12，方案 A：重算口径唯一化） | 旧 P_WF_PUR_CANCEL 解批在库存移动（内部 P_UPDATE_PRO_MRP_ALL 会重算 IN_BUY_QTY/MRP_QTY）之后仍 PRODUCT.IN_BUY_QTY-=QTY，与引擎 inventory-move→adjust-projection 差一个单据量。方案 A：重算口径为权威，手工增量统一排到库存移动之前（重算不跑时增量兜底）；1608/1612 换序重发布，同形 1607/1406 同批换序（adr012-config-fix-j/p/q.sql）；差异登记不归一，inventory-move.mrp 保留并注明为装饰参数。 |
| 75 | ADR-012 制令完工码（1515/2815/2816）改按旧过程事件分支对齐 | ✅ 已拍板并执行（2026-09-12，对拍证据推翻 #67） | 2815 对拍复现：旧 P_WF_MOC_PRODUCT_OUT 批核段无条件清 END_TAG=0/INFACT_END=NULL、解批段按条件置 END_TAG=1 写单据日期，引擎按量重算置 1 → 推翻 #67。决定改按旧事件分支对齐：2815/2816 结案判据改回制令产量，完工码改事件分支（批核无条件 SET_WHEN + no-reverse；新增 DEAPPROVE 动作载解批 END_TAG=1+INFACT_END=ASSIGN_MAX）；解批 REVERSE_STRUCT 改 recompute（否则 DEAPPROVE 下判 no-op）。落地 adr012-config-fix-m/n/o.sql，1515/2815/2816 重跑 PASS。 |
| 76 | ADR-012 1404 客户报价单解批 COP_CHAFFER_D 引用差异 | ✅ 已拍板（2026-09-13，新语义：解批清空；差异不归一） | 旧 P_WF_COP_QUOTE 解批不清询价单 COP_CHAFFER_D 的报价单引用（QUOTE_TYPE/QUOTE_NO/QUOTE_SERIAL_NO 保留），引擎 SEQ3 link-stamp 配 reverse=clear-refs 解批清空。拍板：按 link-stamp 新语义——自己写入的引用撤销时一并清空，追溯交审计，不在主表保留指向已作废报价单的编号；差异不归一。配套修 PriceSyncExecutor.DeapproveAsync 的 DataReader 未关闭致解批 blocked。 |
| 77 | ADR-012 170101/170201 对账单解批 `PRE_RECEIVE_DATE` / `PRE_PAYOUT_DATE` 差异（clear-on-deapprove vs 旧不回滚） | ✅ 已拍板（2026-09-13，新语义：解批清空；与 ADR §16-5 口径一致，并确认同样适用于 170201） | 旧 `P_WF_COP_ACCOUNT` / `P_WF_PUR_DUE` 解批 = 还原送货/退货（收料/退料）数量金额 + 取消结案标志，**不回滚预计收/付款日期**；引擎 `payment-date-calc` 配 `reverse=clear-on-deapprove`，解批清空、再批核时重算（与 1505 的 `FINISHED_*` 同一语义）。双路取证（`account-settle-approve-fixture.sql`、`shadow-{170101,170201}-20260913-{001,002}`）：批核各 6 表 PASS，解批各 FAIL 1 处——`COP_ACCOUNT_M.PRE_RECEIVE_DATE` / `PUR_DUE_M.PRE_PAYOUT_DATE` 旧 2026-10-01 / 引擎 NULL，其余（数量金额、结案标志）全一致。**拍板**：沿用 ADR §16-5 已定口径（对账单解批即清空预计收款日，再批核时重算），并确认同口径适用于厂商对账单 170201；旧「不回滚」属旧实现未做全、非业务诉求。差异不归一。 |
| 78 | ADR-012 2403 打样入库单解批 `SAMPLE_PRO.QTY` 同号累加差异（intended DEACCUM vs 旧同号 +=） | ✅ 对拍证据登记（2026-09-15，新语义：解批冲减；差异不归一） | 旧 `P_WF_SAM_IN` 解批分支 `SAMPLE_PRO.QTY=isnull(QTY,0)+d.QTY`——与批核分支**同号**（批核 50+5=55，解批 55+5=60），而同分支 `AMOUNT` 正确反向（`-d.AMOUNT`），且同族 2404（`P_WF_SAM_OUT`）解批为反向——属旧实现笔误（复制批核分支漏改符号），非业务语义。引擎 SEQ1 `field-accumulate` 配 `auto-reverse`，解批 `DEACCUM` 回到 50（intended）。双路取证（`bridge-takeover-samin-fixture.sql`、`shadow-2403-20260915-{001,002}`）：批核 3 表 PASS（QTY/PRICE/AMOUNT 全一致），解批 FAIL 1 处——仅 `SAMPLE_PRO.QTY` 不同（旧 60 / 引擎 50），`AMOUNT` 与 `PRICE`（解批不动）全一致。**处置**：按 intended 实现，差异不归一（与 #62/#63/#64「旧码缺陷 vs 预期改进」同口径）。 |
| 79 | ADR-012 1201/1311 版次域条件性无需移植 | ✅ 已拍板（2026-09-15，用户按建议确认） | 旧 `P_WF_PRODUCT`（1201/1311 共用）解批本就无分支（直接 return 1）；批核副作用（`PRODUCT.EDITION` 递增 + 动态 `insert PRODUCT_EDITION`，经 `dbo.f_get_same_fieldlist` 取列）被 `SYSSS.PRO_EDITION_TAG=1` 门控，而本库该开关为 0 → 现实中批核亦无副作用。按「条件性无需移植」处置并移出翻译队列；若开关打开需重估（届时需新 Handler：版次递增 + 整行快照 INSERT，动态列需受控化）。 |
| 80 | ADR-012 1202 商编变更单保持下线 | ✅ 已拍板（2026-09-15，用户按建议确认） | 1202（`BOM_REDEPLOY_M/D`，两表 0 行）旧批核 SP `P_WF_BOM_REDEPLOY` 在本机任何库均不存在、仓库无源码（旧页面为通用 `BillPageBase` 无定制逻辑）；D1 迁移 072 已清空悬空 `UPDATE_SP` 引用（批核只剩置 `CONFIRM_TAG` + 审计），D3 迁移 073 已 `M_TAG=0` 隐藏菜单入口（`M_URL`、权限单元 `SYSDD`、快照清理保留，可逆）。维持 `M_TAG=0` 下线，不占翻译队列；恢复只需置回 `M_TAG=1` 并重发布。 |
| 81 | ADR-012 2401 样品转正式只做版次戳记（首转创建链不移植） | ✅ 已拍板（2026-09-15，用户选方案 A） | 旧 `P_WF_SAMPLE_PRO` 首转分支（`PRODUCT` 整行复制 + `P_WF_RUN` 驱动产品审核）在新系统不可复刻：`P_WF_RUN` 已随旧工作流框架退役（库内无此过程，旧首转必报错），且库内唯一流程定义是 1906 的、1201 根本没有产品审核流定义。引擎只做版次戳记（空→`'01'` 否则 +1 补零，解批空操作）；"首次且无产品行"时直接拒绝（fail-closed，不静默跳过创建）。双路取证：增量单与首转成功单批核/解批 PASS + 失败分支负对照（旧缺过程 / 引擎首转门，双双 blocked 零残留）。 |
| 82 | 保存侧悬空 AFTERSAVE_SP 清理（2205/2306/180218） | ✅ 已执行（2026-09-15，用户拍板清理） | 现代保存链不执行遗留 AFTERSAVE_SP：WorkbenchDefinitionBuilder 三级回落＝静态规则表→DomainRuleMap→否则 SprocPendingPorting=true，WorkbenchCommandHandler 以 SP_NOT_PORTED 拒存。2205/2306/180218 的 NEW_URL/MODI_URL 已清空、不在统一表单白名单 → 库内 AFTERSAVE_SP 属悬空配置。已出 DbUp 迁移 076_clear_unreachable_aftersave_sprocs.sql（幂等+库名守卫+事务，可逆），三者无已发布快照，生效待 API 重启。 |
| 83 | 180218 员工批量发卡补旧 SP 卡片日期行为 | ✅ 已执行（2026-09-15，业务确认"发新卡即作废旧卡"） | 旧 P_Employee_Card_After_Save 三项：①失效日期不得早于生效日期校验；②同卡号他员工旧卡到期日置生效日前一天；③同员工其他卡到期日同上。批量作业 /jobs/card-batch（HumanResourceJobsService.BatchCardsAsync）原均未实现。已补①校验（JobsController.CardBatch 返回 INVALID_DATE）；业务确认后把 180208 规则两条到期日联动抽为 HrDomainRules.CloseConflictingCardsAsync，单卡与批量共用；回滚夹具 5 项断言全 PASS，端到端待 API 重启复跑。 |
| 84 | 保存侧 10 处 DIVERGENT + 3 处校验语义差异是否归一 | ✅ **已结项（2026-09-19 回填）**：D/E/C 三类已按"分堆处置"完成（D 类定性为有意设计、E 类 1608/1612 按旧语义修正、C 类维持新语义并登记）；A/B 类显示口径经 2026-09-15 顾问答复 **Q1/Q3/Q4/Q5/Q6 全部关闭**（Q2 转入 #92 并已落地），只读实测 373 条退料明细 0 条命中相关场景。原文：🟡 部分执行（2026-09-15，安全网已补；显示口径待顾问） | 23 个含业务表写的旧过程中 EQUIVALENT 10 / DIVERGENT 10 / 缺陷 1（1606 已修）/ 未替代 2。D 类（主表金额管线汇总为有意设计，注释登记权威、覆盖领域规则同名列）不改；E 类 1608/1612 按旧语义修正（ISNULL 求和、以采购行 RECEIVE_QTY=0 为基线）；C 类（1406 跨单据写、1204 底数校验全表、2911 全表重算）与 180207 维持新语义只登记不改；A/B 类显示口径与 180206 分支留待顾问确认。证据见 内部资料目录AFTERSAVE_SP保存侧审计.md §4。 |
| 85 | 遗留 P_WF_* 旧批核过程逐域退役（含 2401 负对照保留） | ✅ 已拍板（2026-09-15，用户决定：暂不退役，纳入 ADR-012 完结后清理） | 库内 P_WF_* 仍 64 个（062–074 退役 4 个），全部模块 UPDATE_SP 引用经 sys.sql_expression_dependencies 检查无其它库对象依赖（deps=0）。本轮 D7 候选 11 个旧批核过程；P_WF_SAMPLE_PRO(2401) 建议保留（仍是失败分支负对照参照物）。权衡：退役消除"回落旧实现"隐患，但失去双路对拍复现能力（夹具 Confirm/Reopen 依赖旧过程物化）。用户拍板暂不退役，待 ADR-012 彻底完结后统一清理，届时连 MODULES.UPDATE_SP/AFTERSAVE_SP 一并删除，并先归档夹具与报告、沿用依赖守卫。 |
| 86 | `SysdlAfterSaveAsync` 死代码处置 | ✅ 已执行（2026-09-15，用户拍板删除） | `EOS.API/Data/SysDomainRules.cs:126-174` 的 `sysdl` 规则实现完整（入默认组 + `SYSDD`/`SYSDD_REPORT` 孤儿清理），但 `DomainRuleMap` 无任何模块映射（2306 按决策 A 不注册）→ 不可达；且其"物化 `SYSDD_REPORT`"分支与 ADR-009 P1 语义（`SYSDD.REPORT_TAG` 为真源、override 表降级）冲突。建议删除该规则与 `DomainRuleService` 的 `sysdl` 分支（或改写为仅保留孤儿清理并登记到 2306），避免将来越界启用时回灌 override 表。**执行（2026-09-15）**：已删除 `SysDomainRules.SysdlAfterSaveAsync` 与 `DomainRuleService` 的 `sysdl` 分派项（全仓无其它引用、无测试引用）；`sysdg`（2305）保留不动。孤儿清理能力如需恢复，应另立只做清理、不做物化写入的规则。 |
| 87 | 保存侧对拍脚本 CompareAfterSave.ps1 失效用例改造 | ✅ 已执行（2026-09-15，用户拍板改造而非删除） | 2205（Test-Sysqr）与 2305（Test-Sysdg）走统一表单保存，而两模块已移出白名单 → DocumentWorkbenchController 返回 404，断言必失败。处置：两条改为与 2306 同款的旧过程特征化（事务内造数→执行旧 SP→断言→回滚），注释指向 内部资料目录AFTERSAVE_SP保存侧审计.md。顺带修复三处先于本次改动的脚本缺陷：响应体解码（application/problem+json 被给成 byte[]）、模块 FILTER 造数单别被拒（RECORD_OUT_OF_MODULE_FILTER）、1404 报价单补必填明细。整跑 170 PASS / 0 失败、零残留。 |
| 88 | 字段变更历史端点全量 500（JSON_QUERY 嵌套 FOR JSON 非法） | ✅ 已修复（2026-09-15，冒烟工具首跑发现；待重启生效复验） | GET /api/v1/admin/fields/{table}/{field}/history 对任意输入均 500，日志记 SqlException（FROM/with 附近语法错误、FETCH 中 NEXT 用法无效）。根因：FieldAdminRepository.GetFieldHistoryAsync 在标量子查询里用 ISNULL((SELECT JSON_QUERY((SELECT … FOR JSON PATH)), '[]'))，SQL Server 不允许子查询中用 FOR JSON。处置：改为 TOP 200 的 CTE 取事件 + LEFT JOIN AUDIT_FIELD_CHANGE 取明细，由 C# 按 EVENT_ID 分组装配。新 SQL 直连通过、dotnet build 0 错 0 警；端到端待 API 重启复验。 |
| 89 | /api/v1/jobs/* 全量 500：HumanResourceJobsService 未注册 DI | ✅ 已修复（2026-09-15，随 #83 验证发现；已在运行构建复验） | 探测 /api/v1/jobs/card-batch 返回 500：Unable to resolve service for type 'HumanResourceJobsService' while attempting to activate 'JobsController'（JobsController 全量端点不可用：批量发卡 180218、MRP 重算 230901、考勤生成/计算、薪资调考勤）。根因：Controller 瘦身重构只改构造注入、未在 Program.cs 补 AddScoped。处置：补 builder.Services.AddScoped<HumanResourceJobsService>()；新增质量门 scripts/check-di-registrations.ps1（解析控制器依赖 vs Program.cs 注册表，缺失 exit 1，含正反向自检）。复验：jobs 三个安全 POST 已能进入控制器，card-batch 端到端 PASS（INVALID_DATE 拒绝+批量写入 200+到期日收口+零残留）。 |
| 90 | 存量「全空占位公式行 + 空反向结构」清理时机 | ✅ **清理代价已解除**（2026-09-25 拍板 ADR-023；库内清理本身仍按批次执行） | 盘点：服务型效果下全空公式行 82 行 / 68 模块（inventory-move 44 / link-stamp 10 / balance-adjust 9…）+ 空 REVERSE_STRUCT 2 行（1610/1413 callback-reprice）。运行时安全（EffectPlanLoader.ParseOp 对全空行返回 null 跳过），但清理会标脏；快照版本原为 MAX(VERSION)+1，重发布会把对拍报告降级为 C 类，故按批次做。ADR-023 把版本判据升级为规范化后语义等价（发布与落后检测共用同一比较器）⇒ 清占位行、空串统一 NULL 不再顶版本、不作废证据。已交付 clean/restore-placeholder-formula-rows.ps1 并在 1406 试点（版本不动、证据类别不变，试点后复原）。剩余：库内 82 行清理 + 导出器过滤全空行 + 种子重生成 + 导入器守卫口径同步。 |
| 91 | duplicate-check 模板双重缺陷（entity 语句不可绑定+参数矛盾） | ✅ 已修复并落配置（2026-09-15，待重发布生效） | 缺陷 A：EffectValidationExecutor.CheckDuplicateAsync 的 entity 分支生成 FROM dbo.<候选表> X WHERE X.键=M.键，但 M/D 别名不在 FROM 中 → 报 Msg 4104 无法绑定（2914 配置行晚于快照 v2 发布，规则未进运行时定义，故"配了但没跑"）。缺陷 B：excludeSelf.source 同时作唯一键与自身排除来源，keyFields 与 source.fields 数量不一致。处置：语句改为 FROM 主表 M [CROSS JOIN 明细 D] CROSS JOIN 候选表 X 并以主表主键限定（缺上下文 fail-closed）；参数拆为 keyFields/keySource/excludeSelf.keyFields/filter/diagnostics；within-doc 补单据范围限定。新增 12 单测（全量 1197/1199）；迁移 077_seed_duplicate_check_rules.sql 落 2914/180102/180105/180110/180111/110103，待重发布生效。 |
| 92 | 无明细资料时主表能否保存（MODULES.DETAIL_NO_SAVE 语义分叉） | ✅ A+B+C 完整落地（2026-09-15 用户"按建议照准"） | 现象：WorkbenchCommandHandler.SaveDetailsAsync 在明细数为 0 时按 DETAIL_NO_SAVE 分叉——=1 拒绝，=0 删除该单全部明细行。顾问：预收/预付（170103/170203）明细必填、无明细不可保存。A：迁移 078 置 170103/170203 DETAIL_NO_SAVE=1。B：代码区分"未提供明细（null，保留原样）"与"显式空数组（按模块拒绝或清空）"——此前 request.Details ?? [] 会在编辑主表时静默清空整单明细（数据丢失级）。C：迁移 084 把交易单据类 32 个模块置 DETAIL_NO_SAVE=1、附属集合类 13 个保持允许（180111 对齐为允许）；已重发布 31 模块，170103 端到端实测通过。 |
| 93 | 校验规则的"空键不校验"语义（规则级适用条件） | ✅ 已实现并已下发配置（2026-09-15） | 现象：C# 各族普遍先读当前单据键值、为空则放行（string.IsNullOrWhiteSpace），目录配置只能表达"候选行键=当前单据键"。落地：①所有模板通用参数键 params.when（结构化条件，走 EffectConditionCompiler，闭式算子+参数化，不成立即跳过），由 EffectValidationExecutor.RuleAppliesAsync 分派前求值，缺主键上下文 fail-closed；②条件类型集补 blank（NULLIF(LTRIM(RTRIM(列)),'') IS NULL，支持 negate），与 EffectStructSchemas.ConditionTypes 同步，精确对应旧实现去空格后为空（NULL 与空白串等价）；③迁移 080_seed_rule_applicability.sql 落配置（员工工号/料号/本位币按本单键、出勤参数/排班/工资按 COUNT_MONTH、加班申请按 COUNT_DATE）。单测相关过滤集 89/89、全量 1217/1219；迁移干跑+整库回滚 PASS。 |
| 94 | 1606 采购单 SAVE 引用存在性校验断言与真实数据不符 | ✅ 已重建并按 C# 判据收口（2026-09-15） | 目录校验接线修复后该规则首次真跑，真库集成测试即刻报警——现存采购单被拒。量化：PUR_PURCHASE_M 8729 张中"申购单存在"断言不通过 6746 张（77%，含 678 张明细申购单号为空）、"产品存在"146 张、"厂商存在且未停止交易"0 张。根因：规则自建立起从未执行，参数未核对；断言写成强制关联，allowEmpty:true 只对 refKey 生效、对 join 无效；C# 域规则 PurPurchaseAfterSaveAsync 引用类判据只有"厂商存在且未停止交易"，申购单/产品存在性属臆造。处置：迁移 081 先停用止血，迁移 083 按 C# 判据重建（只留 refTable=SUPPLIER + refKey MASTER.SUPPLIER_ID + activeTag BUSINESS_TAG=0），重发布 1606 v9，8729 张 0 例不通过；真库集成 16/16、全量 1233/1235。 |
| 95 | 自动单号推导不应依赖"模块是否有保存后钩子" | ✅ **已解（2026-09-19 回填）**：自动编号判定已与"模块有没有钩子"彻底解耦——规则唯一来源是 `BILLKIND`（`BillNoGenerator.HasAutoBillNoAsync`），并配独立发号器 `BILL_NO_SEQUENCE`（迁移 098/099）；`MODULES.UPDATE_SP`/`AFTERSAVE_SP` 两列已于 2026-09-18 物理删除，`CatalogAfterSaveMap` 的空壳钩子同批清零。原文：⏳ 待定（2026-09-15 删码时发现，已用最小改动规避） | 现象：WorkbenchDefinitionBuilder 只在模块有 UPDATE_SP/AFTERSAVE_SP 的分支里推导 AutoBillNo/BillNoField，而运行时自动单号完全依赖 BusinessRule.AutoBillNo（WorkbenchCommandHandler:161）⇒ 删保存后钩子会静默关掉自动单号。影响面：库内 136 模块配默认自动单别，其中 45 个当前无任何 SP，简单解耦会让这 45 个突然自动编号，故未在本批动。本批规避：新增 CatalogAfterSaveMap（已迁校验目录的 11 模块），定义装配保留钩子字段推导但置 AfterSaveSproc=null。待办：单独评估自动单号按 BILLKIND 独立推导后再清理钩子字段。 |
| 96 | 离职工资表（180310/1803101）保留 C# 实现，不迁校验目录 | ✅ 已定（2026-09-15，迁移 082 停用其目录实例） | **原因（顺序冲突）**：该族保存后行为是"**先删同月旧档，再按每月每人一份判重**"——删除本身就是为了让判重通过（离职补发替换同月同人旧明细，这是旧 SP 的既定语义）。而校验目录实例在保存路径上**先于**保存后行为执行，等于"先判重后删旧档"，会把本应由删除化解的情形判成重复而**假拒绝**。**处置**：该族保留 `HrDomainRules.HrWageAfterSaveAsync`（现为离职专用，已去掉不再使用的 `deleteDup` 参数），目录实例 `ENABLED=0`（迁移 082，干跑+回滚 PASS，已应用并已重发布）。**同批其它模块**：`curr`（110103）保留 C# 的"本位币汇率必须为 1"、`hr-apply`（180206）保留 C# 的"每月加班额"（两条都不是查重，目录不承载）；`hr-employee`/`mou-assess`/`hr-enactment`/`hr-plan`/`hrm-plan`/`hr-wage`/`hrm-wage` 共 11 模块的 C# 规则已删除，权威交给目录。 |
| 97 | 发布继承快照基线：代码侧业务装配改动进不了新快照 | ✅ **已修（2026-09-19 回填）**：发布/校验路径改为「**代码 + 元数据重派生**」——`forPublish=true` 时一律走 `BuildFromMetadataAsync` 而忽略快照基线，删族/换钩子/自动单号等注册表类改动即时生效；原"置未发布 + 触发刷新 + 重发布"绕行已明文作废。原文：⏳ 待修（2026-09-15 切换时发现，已用"置未发布 + 触发刷新 + 重发布"绕行） | 现象：WorkbenchDefinitionBuilder.GetDefinitionAsync 优先用已发布快照基线构建（BuildFromBaselineAsync 返回 baseline with {字段/视图}），不重推导 BusinessRule（族名、保存后钩子、自动单号）；基线为启动载入、发布后 RefreshAsync 的内存缓存 ⇒ 发布自我延续，代码侧删/改领域族重发布后仍是旧值。发现于删 11 模块 C# 族后重发布、快照 DomainRule 依旧为已删的 hr-employee 等。危害：注册表类改动永不生效，以未登记领域规则拒存形式爆炸。影响面：105 个挂保存后钩子模块中"快照 DomainRule 空且 SprocPending=false"者 0 个。 |
| 98 | 公式行空串被当作非法闭式值，导致 44 个模块保存 500 | ✅ 已修（2026-09-15，双层处置） | SAVE 目录校验改无条件执行后每次保存加载整份效果计划，暴露 1502 制令单保存 500：EffectConfigException 公式行聚合 '' 不在封闭聚合集内。量化：MODULE_BUSINESS_ACTION_OP 中 SOURCE_AGG='' 409 行、SOURCE_SCOPE='' 100 行（空串非 NULL），分布 44 模块（1406/1407/1502/1505/1606/170101…）。处置：①加载器把空白串按未设置处理（与缺省同义）；②迁移 085_normalize_empty_op_fields.sql 把历史空串归一（SOURCE_AGG→NULL；SOURCE_SCOPE→MASTER）。新增加载器用例，全量 1235/1237。不为 44 模块批量重发布，避免无谓降级证据。 |
| 99 | ADR-013 单点清单落点与发布门形态 | ✅ 已定并落地（2026-09-16） | ①单点清单落 `WorkflowStates`（`LifecycleColumns` 12 列/`LifecycleTagColumns`/`LifecycleActorColumns`/`RecordStatusColumns` 6 列），`AuditColumns`/`ImportService.IsAuditColumn`/`HiddenStatusTags` 均委托，不新建类（ADR §6.6 关闭）；②发布门 `lifecycle_columns` 只认后端事实（AUTO_APPROVE/UPDATE_SP/工作流/启用的批核-解批效果链），FORM_BUTTONS 纯显示不计入；结案端点运行时已显式（`ENDCASE_NOT_SUPPORTED`）故发布门只拦批核；③全库扫描确认除 AUTO_APPROVE 死标记外无其它"有能力无列"模块（UPDATE_SP/流程/效果链三路均为 0），门禁不误伤现行发布。 |
| 100 | 死 AUTO_APPROVE 标记清理（21 模块，迁移 087） | ✅ **087 已执行，但 2026-09-19 库内复核仍有 7 个残余**（非"清零"）：6 个为查询中心/基本资料设定类特殊页（2501/2502/2503/2508/3000/3003，无 `/workbench` 承载、休眠），**另有 1 个是新增的 `110310 库存策略`**——它随迁移 189"逐列复制 110306 全部 bit 标志位"带入了 `AUTO_APPROVE=1`，而主表 `DEPOT_STOCK_POLICY` 无 `CONFIRM_TAG`；与 `docs/status.md` §6「110310 发布前必须先收口写路径」是同一颗雷（发布门 `lifecycle_columns` 也会因此拒绝发布） | 21 个模块 `AUTO_APPROVE=1` 但主表无 `CONFIRM_TAG` 且无批核能力（白名单 2003/2004/2005，余 18 只读/特殊页），保存改报 `LIFECYCLE_COLUMN_MISSING`。处置：迁移 `087_clear_dead_autoapprove_without_confirm_tag.sql` 置 0，重启后重发布 2003/2004/2005。**2026-10-05 更新**：残余中的 2501/2502/2503/2508 已随迁移 314 物理删除（查询中心壳模块下线，决策 #138），现存残余为 3000/3003 两类基本资料设定页。 |
| 101 | ADR-013 列表默认列维持现状（§3.6 半句收回） | ✅ 已定（2026-09-16 用户拍板） | `SYSQL_DEFAULT` 现存系统列默认上百行（`CONFIRM_TAG`×43、`CONFIRM_PERSON`×26、`CREATE_*`×30 等）；删默认 = 几十个模块列表默认视图变更（批核人/日期列消失），属全员可见行为变更。用户拍板维持现状，ADR §3.6"默认不勾选"收回；个人已勾选（`SYSQL_FIELDS`）本就不受影响。 |
| 102 | ADR-013 空值口径精炼（B3）：只收紧状态位与建立组，其余永久可空 | ✅ 已定（2026-09-16 用户指正） | ADR §3.3.2/§7 原"存量 NULL 批量补 0 后一律收紧" overreach：人/日期列补不了 0（`LAST_UPDATE/CONFIRM/FINISHED` 的 NULL = 事件未发生：未修改/未批核/未结案，硬填即伪造历史）。精炼口径：TAG 位 `NOT NULL DEFAULT 0`（CONFIRM_TAG 存量仅 59 行、FINISHED_TAG 零存量）；`CREATE_*` 为 `NOT NULL`（新建服务端必写，存量约 9 千行 best-effort 回填：人填空串表未知、日期回溯修改日期）；其余 6 列永久可空，只接受事件写入。ADR 措辞已同步修订。 |
| 103 | ADR-013 新表模板形态（§6.2）："拒绝发布 + 配置期提示"，不拦保存、不自动补列 | ✅ 已定并落地（2026-09-16） | 用户提问保存校验还是发布校验：①拒绝保存否——保存是草稿工作区，模块按主表→能力逐步配置，拦保存打断流程；②拒绝发布是——发布是唯一运行时门（`lifecycle_columns` 已实现，能力条件式）；③配置期提示是——菜单管理主表页签按主表物理列实时预警（仅提示，用既有 `/admin/tables/{table}/columns` 接口）；④一键自动补列否——物理列变更走 DbUp 版本化迁移纪律，不开第二 DDL 入口（随意加列会绕过干跑/回滚/审计）。 |
| 104 | EXEC_TAG 未知取值静默全可见缺口收官 | ✅ 已修（2026-09-16，考古发现） | 旧 `DxAuthentication.cs:82-113` 的 switch 无 default：EXEC_TAG 为空串或 A/B/C/D/E/Z 之外字母时**不加任何条件**（静默全可见）；新 `WorkbenchScopeFilter.ApplyExecTagScope` 逐字继承了该缺口。处置：补 `default` 直接抛 `DataFilterUnsupportedException`（fail-closed，不返回越权数据）。影响面：真库 EXEC_TAG 仅 A/B/C/D/Z（1523 行，无 E/未知/空串），零现存行为变更；单测 `ExecTagUnknown_ThrowsFailClosed` 锁定。附带核实：个人覆盖组/布尔 OR/max/交集/无行默认A 均与旧实现一致，无分叉。 |
| 105 | 归属列考古结论：USER_ID 不合并、CI 不启用 | ✅ 已定（2026-09-16 双 Agent 考古 + 真库盘点） | ① `USER_ID`：18 张含该列的表无一是业务单据归属列——6 张新系统表（ASSISTANT_*/REPORT_*，归属外键）+ 1 账号主键（SYSDL，动它等于删账号体系）+ 11 张权限/个人配置键（SYSDD/SYSDH/SYSDG_USER/SYSQL_*/SYSQR_*，键而非归属）；业务单据早已统一用 OWNER/OWNER_G，**无尾巴可收，不合并**（"禁 USER_ID"若含 SYSDL 将是灾难，已明确排除）；② `CI`=公司ID（旧单号函数形参，本部署单据行 100% 空、调用链已死、实际走 BILLKIND 取号、权限无公司隔离）：**不启用多公司**（产品级范围），维持现状，缺口是"死列"不是"未竟事业"；物理删除 257 列不做（旧 SP 依赖未知）。 |
| 106 | CI/OWNER/OWNER_G 服务端持有（用户拍板三条） | ✅ **已生效（2026-09-19 库内复核）**：库内 `CI` 列共 **259** 个，其中 **256** 个为 `NOT NULL`，余 3 个可空者正是 SYSDD 系三表（另有 3 个 `CI` 属另一语义域）——与"254 表收紧 + SYSDD 系保持可空"的口径一致 | ① CI=行公司：新建按当前用户所属公司覆盖回填（SYSDL→SYSDN，无归属 fallback HDR_A 并记警告，CK-01/HS01 待补公司资料），更新保持；存量 70877 行按归属关系回填（OWNER→公司，无归属→HDR_A，C1 孤儿原样保留）+ DEFAULT + NOT NULL（254 表，SYSDD 系三表除外：其 CI 另有语义，保持可空不碰）；② OWNER/OWNER_G：新建一律覆盖（修复 TryAdd 可被伪造缺口），更新忽略提交值（此前更新路径直接写客户端值）；表单新增/编辑态隐藏；导入同口径回填。提交载荷三者一律拒绝（READONLY_FIELD）。 |
| 107 | 删除 COMPANY.CI 冗余列（迁移 090） | ✅ **已生效（2026-09-19 库内复核）**：`dbo.COMPANY` 上 `CI` 列**已不存在**（`sys.columns` 命中 0） | 考证：公司主键是 COMPANY_ID（HDR_A/默认 2 行）；CI（值 ''/'Default'）无 FK、无库内对象依赖、新代码零引用、旧 MagCompany 页无该控件、4 个 COMPANY 数据源只用 ID/名称/地址列。处置：引用熔断（新增引用即中止）+ FIELDS 级联清理 + DROP 物理列。SYSDD/SYSDH/SYSDF 的 CI 不动（另一语义域）。 |
| 108 | 自动批核无副作用模块的显式批核/解批（110101 解批失败） | ✅ **代码已落地，真机复验待跑（2026-09-19 回填）**：服务端无副作用分支 + 前端 `HasStatelessApprove` 并集均已实现；"真机验证"属验收留痕（API 在线时按 `E2ePhase2.ps1` 类脚本复核一次即可） | 现象：110101（AUTO_APPROVE=1，无 UPDATE_SP/效果链/流程）显式解批恒败 `WORKFLOW_NOT_SUPPORTED`。处置：`WorkflowCoreAsync` 增无副作用分支（纯状态翻转+审计），前端补 `HasStatelessApprove`；缺列走 `LIFECYCLE_COLUMN_MISSING`。A 类约 70 模块同修。 |
| 109 | 快照发布：内容未变不递增版本（对拍证据保值） | ✅ **已定并落地代码（2026-09-17 用户拍板）；**判据已升级为语义等价**（2026-09-25，ADR-023） | 问题：快照版本恒取 `MAX(VERSION)+1`，任何重发布都把已对拍证据降为 C 类。处置：发布时逐字比较 `DEFINITION_JSON`，相同则复用当前版本（`published=false/passed=true`）。ADR-023 升级为规范化语义等价，`PublishOneAsync` 与 `DetectStalenessAsync` 共用同一比较器。 |
| 110 | ADR-012 第②项：解批与失败分支三态对拍收口（含 2 个引擎缺陷 + 2 条已拍板口径差） | ✅ **已定并落地（2026-09-17）** | 缺陷①「空库位行」：`InventoryMoveHandler.BuildRowSet` 补守卫。缺陷②「反向流水撞主键」：解批流水改记 `SYSDATETIME()` 避开 `INV_DEPOT_LOG` 主键。验收按 ADR §7-6 只记行数不逐行对拍；对拍报告新增 `failure` 标记，账本只取失败分支证据。 |
| 111 | ADR-012 第④项：批核侧旧过程清零 + 1606 已拍板口径差登记 | ✅ **已定并落地（2026-09-17）** | 6 模块 5 过程退役（1405/170102/170202/1606、1201/1311/180401）：迁移 105/106 清 `MODULES.UPDATE_SP`、置空快照 `WorkflowSproc`、DROP；1201 置 `EFFECT_ENGINE_TAG=1`。`WorkflowStates` 新增 `HasApproveCapability` 并纳入引擎接管，1606 口径差登记白名单。 |
| 112 | ADR-012 第⑤项：次口径 64.2% vs 期望 70% 的处置 + 元数据面 50 模块退役 | ✅ **已定并落地**：**用户已选 A（2026-09-17）**（下沉 18 条使次口径达标）；元数据面批 1（50 模块 `UPDATE_SP` 清零 + 40 过程下线）已执行；两个钩子列亦已于 2026-09-18 物理删除 | ①次口径 179/279=64.2%，方案 A 下沉 `link-stamp`(10)+`set-state`(5)+`field-copy`(3) → 197/279=70.6% 达标。②50 模块 `UPDATE_SP` 指向 40 过程，迁移 107 批退（`dbo` 374→334）。四批路线见 `ADR-012两级覆盖率报告.md` §八。 |
| 113 | ADR-012 方案 A：link-stamp 明细定位五例下沉的两个引擎口径 | ✅ **已定并落地（2026-09-18，**未改引擎语义**） | 五例 1404/1418/1606/1615/1616：定位键改用 `SOURCE_SCOPE=TABLE`+显式 `SOURCE_TABLE`，新增纯增量令牌 `SOURCE_AGG=PICK`（按定位键取原值、NULL 保持）。迁移 123 下沉 5 条（动作键 `field-accumulate`），ADR-012 §13.3 增补 `PICK`；次口径 68.8%→70.6%。 |
| 114 | ADR-012 批 4：C# 规则 `MODULES.ERROR_NO_SAVE` 门控随退役 | ✅ **已定并落地（2026-09-18，**加法式能力，开关语义保持**） | 给校验门控新增 `MODULE` 域（`switch.gates` 带 `scope:SYSSS\|MODULE`，未知域 fail-closed）。首个落地 `qc-analysis`(3303)：迁移 124 补齐后删 `CusDomainRules.QcAnalysisAfterSaveAsync`、登记 `CatalogAfterSaveMap`；2815/1515、2707、2912 同法。 |
| 115 | ADR-012 批 4：剩余族所需的目录能力清单（模板扩展 / 写型能力） | ✅ **已定并落地（用户拍板：合并）**：四个同形键（`cop-account-rollup` 170101 / `purchase-due-rollup` 170201 / `cop-prepay-rollup` 170103 / `pur-prepay-rollup` 170203）已合并为**一个通用键 `detail-rollup`**（迁移 172）——参数声明明细表、舍入位数与若干条"主表列 ← ROUND(SUM(明细列),位数)［＋主表列］"，单据键列改由单据计划提供；四个旧处理器类已删除。 | 缺口 ⒜–⒢：⒝⒞ 解于 `reference-exists.refCondition`（迁移 129-132），⒜ 变更单族解于 `qty-not-exceed`/`duplicate-check`（130/132），⒠/⒟ 解于 `line-require.assert`/`targetAgg`（134/137）。待拍板：1608/1612 三表 UNION、180206/180207 跨月聚合、⒢写型能力（建议抽通用键 `master-detail-rollup`）。 |
| 116 | ADR-012 批 3 剩余：过程的收尾路径 | ✅ **已收口（2026-09-18）**：⒜ 报表族 8 → **0**（迁移 145/146）、⒝ 按名调用点 4 → 收口 3 + 最后 1 个转 #117（**已定：暂时保留**）、⒞ 验收基准改冻结期望**已完成**（基建 + 默认切冻结 + 迁移 142 下线 31 个）；当前库内 `dbo` 过程 **1**。 | 唯一剩 `P_HRM_WAGE_CALC`（见 #117）。已清零：系统 `xp_*` 9→0（迁移 144）、HR 分析报表 7→0（145）、库存日报 1→0（146）；报表侧统一 `ReportAggregateRegistry`。⒜报表族 8/8、⒝按名调用点 3/4、⒞冻结期望完成（迁移 142 下线 31）。 |
| 117 | ADR-012 (d)：最后 1 个按名调用点 `P_HRM_WAGE_CALC` 的退役路径 | ✅ **已定（用户拍板）**：**暂时收口**——该过程保留在库内（作考古/参照），薪酬域尚未推进到这一步；运行期注入面已确认不可达，无需额外加固。 | 实测：元数据驱动动态 SQL 工资引擎（`@sql` 拼串 `EXEC`、`@emp_ids` 经 `REPLACE` 拼进 IN 列表＝注入面）。唯一调用点 `HumanResourceJobsService.RunWageCalcForMonthAsync` 恒传 `@emp_ids=''`/`@calc_mode='A'`，拼接分支运行期不可达，故暂保留并登记。 |
| 118 | ADR-012 (d)：报表过程 `P_RPT_INV_PRO_DEPOT_1`（库存日报）移植口径 | ✅ **已定（用户拍板）**：按**意图口径**实现 + 差异登记——明知旧实现有缺陷，**不把 bug 抄过来**；无需改回逐字复刻。 | 移植为受控聚合源 `ReportAggregateRegistry.InventoryDaily`（18 列）。两处旧公式缺陷按意图修正：期初移动平均分母错、每日发出成本游标首行未累计；并修掉 `char(255)` 空上界静默截断（改"空值即无界"）。迁移 146 下线过程（`dbo` 2→1），证据 `InventoryReportPortLiveTests`。 |
| 119 | ADR-012 批 4：1608/1612 与 180206/180207 的引擎形状缺口与扩展 | ✅ **三族全部落地（2026-09-18）**：1608/1612 用 `usageOnly` + 预聚合视图；180207/180206 用 `custom-validation`（`hr-worktime-check` / `hr-apply-check`）。**批 4 登记族 53 → 0** | 缺口一：新增 check 级 `usageOnly` + 预聚合视图 `V_PUR_CANCEL_ALLOC`（三表并集），迁移 147 播种、删 C# 与登记。缺口二：用 `custom-validation`（迁移 170/171），删 `HrDomainRules.cs`，保留"未登记即拒绝"兜底。收口：登记族/分派分支均 0，过渡桥整体拆除。 |
| 120 | ADR-012 (d)/批 4：`sysdg`(2305)"孤儿权限行清理"该不该保留、落在哪 | ✅ **已定（用户拍板）：不做**——新系统的数据校验与产生都较规范，制造"孤儿权限行"这种数据垃圾的概率很小，没有必要恢复这套保存时顺带清理的语义（该语义的实现早已删除，本次仅作正式结论登记）。 | 事实：2305 `/admin/groups` 不在运行白名单，`DomainRuleMap[2305]="sysdg"`（`SysdgAfterSaveAsync`）从未被调用。已处置：迁移 151 回退效果接管（`EFFECT_ENGINE_TAG` 1→0）、删死登记与 C#，撤 `orphan-cleanup`；本语义不恢复。 |
| 121 | ADR-012 §6 次口径：可视化配置覆盖率的分母口径 | ✅ **已定（用户拍板）**：A、B 都不选——**覆盖率比例本身不重要**，本 ADR 的目的是把存储过程下线（SQL 版与由它翻译来的 C# 版）；该比例只作**信息性指标**保留在报告里，**不再作为验收门槛**。 | 实测 `MODULE_BUSINESS_ACTION`=批核/解批 279 步+SAVE 26 步；按 279 基线 197/279=70.6% 达标，并入分母稀释为 64.6%。逐键盘点：⒜可下沉 `link-stamp`(5)、⒝需扩公式能力 7 条、⒞语义专用保留 C# 30 余条。方案 A 取批核/解批口径（建议），B 追全量。 |
| 122 | ADR-015 多标签工作区：落地口径与回退开关 | ✅ **已定并落地（2026-09-18 用户拍板并逐条确认）**：`EOS.Web` 段 1–5 全部完成（未提交），**待用户在浏览器实测** | 全站标签+真保持+上限 12+关闭确认。五条口径：URL 先匹配标签、带 `from` 附来源、权限回收就地 403、标签列表存 `localStorage`、工作区路由禁 `loader`/`action`/`lazy`。`react-router` 7.18.2 只采用最后注册拦截器 ⇒ 脏页汇聚外壳唯一 blocker；回退开关 `erp-workspace-tabs-enabled=off`。 |
| 123 | ADR-014：工作台缺少"用户主动触发的自定义动作"机制（登记待讨论，不阻塞） | ✅ **机制已定稿（ADR-018 v7，2026-09-23）：代码闭集 handler＋复用业务动作表＋按钮级授权 fail-closed 名单＋once转后结案锁死＋弃用存储过程；待按 WS-0…WS-6 接力实施** | 现状 `MODULES.FORM_BUTTONS` 只是固定按钮集选取，效果事件为封闭集（`SAVE`/`APPROVE_EFFECT`/`DEAPPROVE`/`ENDCASE`/`UNENDCASE`/`DELETE`），"打开单据点一下做一次动作"无落点。需求登记于 内部资料《自定义按钮需求清单.md》；选项 A 维持现状、B 加 `CUSTOM_ACTION` 事件、C 点对点。 |
| 124 | ADR-014：库位/批次新列要不要进其余 23 张明细表的默认列 | ✅ **已定（用户拍板）：选 A——全部设为默认显示列，并已落地**（2026-09-19） | 迁移 199 把 5 列（`LOCATION_NO`/`IN_LOCATION_NO`/`OUT_LOCATION_NO`/`BAD_LOCATION_NO`/`BATCH_NO`）×29 表置默认（37 处列位），断言 `LOCATION_PATH` 不可见。实测 `FIELDS` 运行期实时读取 ⇒ 字段元数据迁移不需重发布，刻意未重发布。单测 1481/1481 PASS。 |
| 125 | `110310 库存策略`上的死 `AUTO_APPROVE` 标记 | ✅ **已收口（2026-09-19 用户拍板执行）**——两条硬前提均已满足，见 ADR-014 §4.22 | `110310` 随迁移 `189` 带入 `AUTO_APPROVE=1`，主表 `DEPOT_STOCK_POLICY` 无 `CONFIRM_TAG`；实测批核侧可打进去返回 `400 LIFECYCLE_COLUMN_MISSING`。处置：`RunWorkflow`/`RunFinish` 补白名单闸门（`!HasAdd && !HasEdit ⇒ 404`）、迁移 `201` 置 0、`check-lifecycle-columns.ps1` 补断言。批核端点 400→404。 |
| 126 | ADR-019：2301 模块管理的组织方式与配置权是否独立成位 | ✅ **已定（2026-09-24 用户拍板，并已实施）**：⒜ 不拆模块号，只在 2301 内按触发者分页签（行为动作 / 校验规则 / 自定义按钮）；⒝ 给 2301 加配置权位 `MODULE_CONFIG_TAG`；⒞ 与 `SETUP_TAG` **各管一块**（SETUP=基础属性：菜单名/URL/图标/启用/排序/移动/删除/默认查询列；新位=配置面：效果动作/校验规则/自定义按钮/发布）；⒟ 现有 admin 与组 1 **全勾上**（回填 = 复制其 `SETUP_TAG`） | 背景：2301 承载菜单+动作/公式/规则+按钮+发布。原拆 2301/2312/2313 因隐藏写路径成本高降为备选，真分权由模块内能力位承接（复用 `FORM_DESIGN_TAG`）。落地：迁移 `228`（`SYSDD`/`SYSDH` 加 `MODULE_CONFIG_TAG`+回填 2301）、`MenuAdminController` 分段判权、前端权限矩阵。佐证 `ADR-019-模块管理拆分.md`。 |
| 127 | ADR-025：日志**脱敏范围**与关联键交付落点（P0 前置，同时阻塞 ADR-029） | ✅ **已定（2026-09-29 用户拍板，已实施）**：脱敏取**方案 A（模式清单）**、关联键口径统一、一次操作一个键；诊断信息落点为「日志管理」页 + 错误提示条复制编号 | **决策**：① 只挡"看起来就是凭据"的内容，不加 `FIELDS.IS_COST`/`IS_SECRECY` 字段级标记（业务数据进日志属另一类问题，不靠脱敏兜底；将来可平滑升级）；② 后端全出口改读 `RequestContext.GetCorrelationId`；③ 前端一次操作一个键 + 可复制完整报障编号；④ "脱敏已生效"纳入发布前必跑项。**已实施**：`GlobalExceptionHandler`/`ApiExceptionFilter`/`AssistantController` 改读 `RequestContext`；`JsonFileLoggerProvider` 写入前对 message/fields/exception 过 `LogRedactor`，并补 3 个单测（脱敏正反例、超期轮转清理、目录不可用降级）；`docs/guide/31` 与 `docs/guide/30` 的现状订正同步。**零迁移、可回退** \| `docs/decisions/ADR-025-关联键单一真源与日志脱敏接入.md` |
| 128 | ADR-026：版本真源、首个版本号与发布节奏 | ✅ **已定（2026-09-29 用户拍板）并已接线**：根 `version.json` = `0.1.0`、主干 + 标签即发布、CHANGELOG 中文、递增**按提交类型** | 决策：真源 `version.json`；首个 `0.1.0`；主干+`v<version>` 标签即发布，不引入 Git Flow；递增 `feat`→MINOR、`fix`/`perf`→PATCH，破坏性在 0.x 用 MINOR+CHANGELOG 标 BREAKING。已实施：`EOS.API.csproj`、`vite.config.ts` 注入 `__APP_VERSION__`、`CHANGELOG.md`、`release.ps1`。 |
| 129 | ADR-027：**是否引入 Serilog**、日志保留天数与路径根 | ✅ **已定（2026-09-29 用户拍板，已实施）**：**不引入 Serilog**、保留 **14 天**、目录固定为程序目录下 `logs`、不可写只记 Error 不进 `/health/ready` 红灯 | **决策六条**：① 不引入第三方日志库（现有自研 JSONL 管道已覆盖结构化/级别门/大小轮转/共享读，缺的只是时间保留与脱敏；复评条件＝出现多 sink/采样/OTel/集中平台需求）；② 时间+大小双门保留，默认 14 天；③ 默认目录 `AppContext.BaseDirectory/logs`（不再依赖宿主工作目录）；④ 目录不可写时降级为"只控制台"+ 记 Error，`/health/ready` 的 `log_file` 项报 **Degraded**（不把整个服务判红）；⑤ 不做超期归档；⑥ 随发布物提供生产示例配置。**已实施**：`appsettings.json` 增 `Logging:File` 四项；`JsonFileLoggerProvider` 增保留/Rotation/降级/脱敏；`LogFileHealthCheck` 新增并注册；启动日志打印生效路径与保留天数 \| `docs/decisions/ADR-027-结构化日志的保留与轮转策略.md` |
| 130 | ADR-028：异常出口加固口径与前端错误边界 | ✅ **已定（2026-09-29 用户拍板）并已实施**；后端部分同 #127 | **五条裁决**：① 生产不返回堆栈（固化现状）；② 错误提示条 = 提示 + 复制报障编号，完整诊断信息放「日志管理」页；③ 关联键显示**完整值 + 一键复制**（不用短码，避免搜到别人的请求）；④ 客户端错误**先不加**限流；⑤ 后端异常出口日志字段补齐（随 #127 完成）。**已实施**：根级 `AppErrorBoundary`（错误页 + 强制上报）、`ErrorDiagnostics` 页内"报障编号 + 复制"、`MutationCache` 统一给变更失败挂"复制报障编号"动作（Toast 新增 `action`）、`lib/diagnostics.ts` 诊断文本出口、上报补 `correlationId`/`appVersion`、传输层支持调用方覆盖关联键。**验证**：前端 lint 0 警 0 错、build 通过、vitest 899/899。**未验**：需登录态与真实 500 的端到端（待用户重启服务） \| `docs/decisions/ADR-028-全局异常捕获与错误追踪.md` |
| 131 | ADR-029：用户反馈入口与一键日志打包（硬前置 = #127） | ✅ **已定（2026-09-29 用户拍板）并已实施**（迁移 **284** 重编号待重启落库） | 裁决：做一键打包，落独立页「日志管理」；权限模块 `2313` `CanBrowse`/`CanSetup`；`.github/ISSUE_TEMPLATE` 先不做；上限 100MB/60s；复用 `AUDIT_EVENT`。已实施 `LogFileReader.cs`/`LogsController.cs`（`/api/v1/logs`、`/bundle`）、`LogAdminPage.tsx`、迁移 283/284、指南 31。 |
| 132 | 日志管理页的模块编号与权限锚点（挂在「基本参数 → 系统参数」下是否合理） | ✅ **已定（2026-09-29 用户拍板，已实施；迁移 284 待重启落库）**：重编号到 **23 段**、接口权限**改锚本页模块**、**旧 `11xxxx` 编号不保留** | 初版迁移 283 挂 11/1101 下；惯例定制页一律落 `/admin/*`（23 系统管理）。裁决重编号 `M_IDX=2313`、`M_P_IDX=2311`、`M_ROOT_IDX=23`、`SORT_IDX=40`，不保留 11 编号；另出 284 不改已落库的 283。授权取模块 11 与 110111 并集，接口锚点改 2313。 |
| 133 | ADR-016 **S0 前置核对**（诊断黄金集的地基：`MODULE_VALIDATION_RULE` 187 条 ↔ 运行时口径） | ✅ **已核对并登记：逐条一致**（2026-09-29） | **做了什么**：新增 `scripts/check-validation-rule-parity.ps1` + `EOS.API.Tests/ValidationRuleParityLiveTests.cs`，把**配置表**与 `ValidationRuleRegistry` 的**运行时口径**逐条比对。**实测结论**：187 条**全部落在代码闭集内**、**全部进入当前快照**（表 187 = 快照 187，双向 0 差异）、运行时（`EffectPlanLoader` + `ValidationRuleRegistry`）**0 拒绝**。**为什么登记**：这是诊断黄金集能否成立的前提——若配置与执行不同源，黄金集就建在纸面规则上。**连带订正**：ADR-016 初稿对 `MODULES.FORM_BUTTONS` 的归因有误（该列**未失效**，是内置工具条动作的存在性白名单，全空 = 364 个模块都走"未配置回退集"），**代码不动**、已在 ADR 内订正；按钮面规模连带订正 `349 → 21`（`MANUAL` 行）。 |
| 134 | ADR-016 **S2 诊断黄金集**的口径与**人工审阅要求** | ⏳ **口径已定、跑分已做；人工审阅待办**（30 条当前 `reviewed:false`） | **口径（不让用户凭空回忆）**：**机器枚举候选 + 人工审阅确认**——每一条 `MODULE_VALIDATION_RULE` 天然就是一个"应当能被诊断出来"的样本；从该表按 `VALIDATION_KEY` 去重抽样 **30 条**（以 `STAGE=SAVE` 为主，`APPROVE`/`DEAPPROVE`/`DELETE` 各取若干，并补 `fieldGuard` 与状态限制类），每条**预填"应当被诊断出的原因"**（直接取该规则的 `MESSAGE` + 模块名 + 触发条件，本身已是标准答案）。**产物**：`EOS.API.Tests/AssistantEval/diagnosis/golden.jsonl`。**当前跑分**（以未审阅候选作临时黄金集）：命中 30/30、拒答 0、**错误归因 0**，硬门槛（错误归因 = 0、拒答率 ≤ 10%）达标。**待谁、待什么**：**待用户逐条审阅**——确认"这条规则在该模块下是否真的会拦人"、标掉不适用的并把 `reviewed` 置为已审；审阅后按同一跑分口径复跑，**结论才算定稿**（现分数只作临时基线，不得对外宣称已达商用门槛）。 |
| 135 | ADR-016 **R29 顺延项**（主动摘要第三条来源 + 滞留取数口径修正） | ✅ **已交付**（ADR-016 S2，2026-09-29） | **① 摘要第三条来源「此刻办不下去」**：逐单复用诊断的**同一套判据**（当前校验不过 / 状态不允许该操作）实时算出，**零模型调用**；代价参数化（每模块最近 N 单 / 年龄上界 / 每模块判据条数上限，**任一 ≤0 即整条来源停机**），判不了一律不报并在 `caveats` 里声明覆盖面。**② 滞留条目取数口径修正**：原先取到 3120 天前、同类 43 条的遗留单，改为**年龄上界 + 建立日期倒序**取数。**背景**：S1 曾把"最近被拒（含校验不过）"写成第三条来源，实测 `AUDIT_EVENT.RESULT=0` 全库仅 156 条且几乎全是测试噪音，不足以作主来源，故经用户拍板**顺延至 S2** 落地。 |
| 136 | ADR-012 退役 SP 残留：静态 WorkflowSproc 指向已删过程 | ✅ **已定并落地（2026-09-17）** | 9 模块（1404/1406/1604/1607/1615/170101/170103/170201/170203）静态 `WorkflowSproc` 指向已退役 SP，引擎接管无回归但 `HasWorkflow` 失真。处置：`ModuleBusinessMap` 该 9 条与 1201/1405/1606/170102/170202 一并置 null，白名单清空，`BillNoGeneratorTests` 断言同步改 false。 |
| 137 | 口径「营业额」的定名与语义 | ✅ **业务口径已定名**（2026-10-04 用户拍板） | **业务口径**：营业额 = 1405 销售订单（明细 `COP_ORDER_D`）的**含税**金额合计，**只统计已批核（有效）单据**。它与既有 `sales_amount`（#48：`SUM(AMOUNT_TAX)` 且主单 `COP_ORDER_M.CONFIRM_TAG=1`）**语义完全一致**，故**不新增第二条口径**（同一语义并存两条，改一处忘一处必然漂移）：迁移 `311_report_metric_turnover.sql` 把该口径的对外名称定为「营业额」、说明写清来源 / 含税 / 仅已批核，俗称「销售额（含税）」留在说明里——两种叫法都能被 `enum_metrics` 的关键字检索命中；未税那条 `sales_amount_ex` 的说明同步写清，避免两条被混用。 |
| 138 | 查询中心壳模块 2501-2508 与父目录 25 的下线口径 | ✅ **已拍板并落地（2026-10-05 用户拍板，迁移 314）** | 八个「××查询中心」是旧系统"每域一页"的遗留菜单叶子，在新系统里是**零贡献的菜单壳**：`SEARCH_1`/`SEARCH_2` 全为 0（不贡献检索面——真正让查询中心可用的，是 36 个业务模块自己挂的 `SEARCH` 位）、五项无主表、八项同指 `/search-center`、`/search-center/{2501..2508}` 选不中任何模块；`FIELDS.BROWSE_M_IDX` 指向 2502/2503 的 9 行早被 `WorkbenchBrowseResolver` 判为不可达（要求目标 `M_URL=/workbench` 且有主表）。用户选**方案 A（全清）**，并**一并要求下线**挂靠的报表 `COP_ORDER_D`（"订单查询中心"；原本待定的归属迁往 1405 或 1406 随之作废）。同时删除父目录 25 并清 `SYSDD` 2 行。检索面维持"由业务模块的 `SEARCH` 位决定"，入口为工作台「查询」动作与深链 |
| 139 | 统一表单的打开方式与每行列数是否回到模块元数据 | ✅ **回到模块级可配（2026-10-05 用户拍板，迁移 319）** | 模块字段多少差异大，统一表单"一律整页 + 一行四列"导致疏密两级观感都不合适。用户定：① 打开方式（本页签 / 新页签 / 弹窗）与弹窗宽高落 `MODULES.FORM_OPEN_MODE` / `FORM_DIALOG_WIDTH` / `FORM_DIALOG_HEIGHT`；② 每行列数落 `MODULES.FORM_LAYOUT_COLUMNS`（1..4，未配置 = 4）。**这推翻了 ADR-022 订正 4 的"全局固定一行四列"**（该结论随之作废，见 ADR-022 订正 6）：新版把列数做成模块声明，运行态表单、表单设计器画板与保存期校验同取这一处。弹窗**不引入第二套渲染路径**——地址与路由不变，只是同一表单页换容器（保存/校验/脏位/权限同一条） |
| 140 | 呈现配置为何改了不生效（落在发布链上）+ 内置动作列去留 + 设计器还原度 + 标签为何不显示模块名 | ✅ **已定并落地（2026-10-06 用户拍板，迁移 320）** | **① 呈现配置搬进表单设计器**：319 把打开方式/弹窗宽高/列数落回模块元数据，但入口留在 2301「统一表单」页签 ⇒ 落在"元数据草稿 → 人工点发布"那条链上，用户实测"配了弹窗、保存后打开仍是本页签"。改为**设计器里配、随版式同一笔保存**（设计器本就同请求内重发布）⇒ **保存即生效**；2301 那条链不再写这四列（只留只读摘要 + 设计器入口；那份摘要页签随后也删了，见 #143）。**快照仍是运行期唯一契约**，只是"重发布"由设计器代劳。**② `FORM_BUTTONS`（内置动作受控注册码）退役并物理删列**：库内 0/363 非空、运行态一直走"能力 + 权限 + 单据状态"回退集，删前删后逐字节等价。**③ 设计器画板＝运行态容器尺寸**：弹窗方式下画板锁到模块声明的窗体宽（所见即所得；此前"设计按整页排、运行按 900 宽排"折行位置不同）。**④ 标签标题取模块名**：模块页标题唯一真源是 `MODULES.M_DESC`（经导航树叶子下发），前端静态标题表只兜底分区名与非模块页面——此前 `/admin/menus` 被硬编码成「菜单管理」，模块改名后标签不跟 |

| 141 | 页签级布局列数 / 调整即合规 / 弹窗内是否摆页签 / 呈现入口位置 | ✅ **已定并落地（2026-10-06 用户拍板，折进迁移 319）** | ① **列数下放到页签**（`MODULE_FORM_TAB.LAYOUT_COLUMNS`）：模块级单值表达不了"页签 1 两列、页签 2 一列"，`MODULES.FORM_LAYOUT_COLUMNS` 退为兜底与新页签初值；② **调整即合规**（推翻 ADR-022 订正 6 的"不静默改写跨度"）：改页签列数时当场把该页签内越界跨度夹到新列数，服务端同口径，迁移一并夹齐存量；③ **呈现入口搬到页签行最右端**（原本的空白处）点开弹窗，画布上方不再单占一行；④ **弹窗内摆页签**（推翻 2026-10-05 "弹窗不摆页签"）：整页与弹窗同一套结构，含工具栏与页签之间那 10px 间隙 |

| 142 | 四个呈现配置列该不该留在 `MODULES`（尤其 `FORM_LAYOUT_COLUMNS`） | ✅ **打开方式三列留、`FORM_LAYOUT_COLUMNS` 删（2026-10-06 用户拍板，折进迁移 319）** | 用户自省后定：`FORM_OPEN_MODE` / `FORM_DIALOG_WIDTH` / `FORM_DIALOG_HEIGHT` 是**模块自身事实**（1:1、与是否定制版式无关，且约 100 个模块没有任何页签行——挂到 `MODULE_FORM_TAB` 会无处安放，还会被"重置版式/保存版式"的删行重插带走），留在 `MODULES`；`MODULE_FORM_TAB.LAYOUT_COLUMNS` 成为一行几列的**唯一真源**，`MODULES.FORM_LAYOUT_COLUMNS` 不再存在（提交前把 319+321+322 折成一稿，模块级那列根本没随提交发布过）、兜底由页签列的 **NOT NULL DEFAULT 4** 承担。同步收口消费面（删 `ResolveColumns`/定义段的 `Columns`/设计态与草稿的模块列数/模板列数），门禁越界判据只按页签列数。顺带修掉 `FormLayoutRepository.RestoreBackupAsync` 回滚丢页签列数的真 bug |

| 143 | 2301「统一表单」页签还要不要 | ✅ **删掉（2026-10-06 用户拍板）** | 319/320 两轮收口后，该页签只剩\"只读摘要 + 打开设计器按钮\"：呈现配置归设计器（ADR-022 订正 7）、内置动作受控注册码列退役（#140②）、页签定义与每行对数更早退役 ⇒ 没有可配的东西，却多一处每天要与设计器对齐的口径（用户原话：\"它完全没有存在的必要了\"）。删页签本体与枝叶（`designerUrl` / `describeOpenMode` / `formatDialogSize` / `manualActionCount` / `WorkspaceNavContext` 取用），并让 `MenuAdminModule` 不再投影 `FORM_OPEN_MODE` / `FORM_DIALOG_WIDTH` / `FORM_DIALOG_HEIGHT`（`MenuAdminRepository` 两处 SELECT + 读取映射同步收口）。**设计器入口不由它承载**：单据表单页的字段设置菜单 ›【表单设计】即可（需 `FORM_DESIGN_TAG`） |

| 144 | MODULES 与附属表瘦身：路由四列只留一个、死表退役 | ✅ **第一刀 + 死表已落地（2026-10-06 用户拍板，迁移 321~323）** | **① 路由收敛**（用户提的第一刀）：四列只留 `M_URL`——`NEW_URL` 全库 0/363 有值（纯死列）、`MODI_URL` 266 有值里 262 个是默认编辑模板、4 个真自定义页与本模块 `M_URL` 同值、1 个空格脏值、`HELP_URL` 只 2 个模块填过且都是坏链 `~/Client/<客户代号>/ProductIn.aspx` ⇒ 三列物理删除，**零信息损失**（迁移里带三档前置自证）。`M_URL` 语义定为"承载页"：空 = 目录节点/未声明（**单据模块按主表判定，默认落统一工作台**——新判据 `IsWorkbenchModule(M_URL, MASTER_TABLE)` 统一了 7 处判据）、`/workbench` = 统一工作台、精确路径 = 自定义承载页；顺带退场的是整套"动作路由"概念（`ResolveActionUrl` / `IsValidActionUrl` / `IsUnifiedFormRoute` 与动作模板形态），`HasAdd`/`HasEdit` 折叠为"写名单 / 写名单∨只读名单"。**② 死表退役**：`SYSTEMP`（279 行旧权限副本，零引用）与 `TASK`（0 行，仅改编号级联残留）删表；**同批两张经取证排除**——`SYS_WORK_TASK` 是活模块 2308 的主表、`SYSQR_DA` 挂在待退役的 2205 上，随"A 档其它"另议；`SYSDF`（11134 行历史审计）留档。**③ 元数据跟随**（迁移 323）：`WF_APPROVE`/`WF_MYTASK` 的「查看明细URL」虚拟字段登记指向 `MODULES.MODI_URL`（会被表数据视图当 SELECT 表达式用）⇒ 删该登记；三处选择器判据 `MODULES.MODI_URL NE ''` ⇒ 翻译为 `MODULES.M_URL NE ''`（新口径"模块有承载页"） | 迁移 `321_url_consolidation.sql` / `322_retire_dead_tables.sql` / `323_retire_module_url_field_registry.sql`（三份干跑 PASS、事务整库回滚、探针前后一致）；后端 `ModuleRouteValidator` / `WorkbenchDefinitionBuilder` / `WorkbenchDefinitionValidator` / `WorkbenchAccessPolicy` / `WorkbenchModels` / `MenuAdminModels` / `MenuAdminRepository` / `DocumentWorkbenchRepository` / `FlowDefinitionService` / `WorkbenchDefinitionSnapshotService`；前端 `menu-admin/MenuAdminPage.tsx` / `document-workbench/{DocumentWorkbenchPage,FormEditorPage,formDefinition}`；门禁 `check-retired-db-objects`（清单 22→25 条 + 正反样本）；文档见 10/11/12/40/42/70 篇与 ADR-006/014/020 后记。**运行态需重启 `EOS.API` + 重发布一轮快照** |

| 144 | MODULES 上那批旧系统遗产列、两个死模块（2205 / 1399）、两张随行表（SYSQR_DA / SYSDF）怎么处理 | ✅ **八列删（REMARK 留并放出来可编辑）；2205 + SYSQR_DA 删；1399 删；SYSDF 导出 CSV 后删；2308 + SYS_WORK_TASK 暂留（用户 2026-10-06 逐组拍板）** | 用户按我给的"逐值验过的证据"分组拍板：**① 一个"零"字定案**——`CONFIRM_TAG` 363 行全 0、`OWNER`/`OWNER_G` 全空 ⇒ 删；**② 占位值，看着有数据其实无内容**——`CONFIRM_DATE` 266 行全是 1900-01-01 占位、`CONFIRM_PERSON` 全空白、`CI` 全 'DEFAULT'（单公司键）、`CREATE_PERSON` 只 2 行、`CREATE_DATE` 99 行是 2026-08~10 的**补录时间**（不是旧系统历史）⇒ 删；**③ `REMARK` 352 行有文本，用户要它"写这个模块是干什么的"⇒ 保留**并放进 2301「基础」页签（多行文本域，随保存提交）；**④ 2205 报表过滤条件设置**：模块与主表 `SYSQR_DA`（132 行）整体退役——管理页与端点早已下线、`SYSQR_DA` 全仓零代码引用；**⑤ 1399 仓库综合报表**：M_TAG=1 却无页面无子模块（点开落占位页）⇒ 删；**⑥ SYSDF** 11134 行旧操作流水 ⇒ **导出 CSV 备查**（`logs/archive/retire-324/`，含行数与 `CHECKSUM_AGG` 校验）**后删表**；**⑦ 2308 工作任务记录 + SYS_WORK_TASK 暂留**（活模块，表空只是没人录数据）。**另有两处按证据顶回原判**：`SYSQR_DEFAULT`（541 行）与 `SYSQR_USER`（1152 行）**不随 2205 删**——它们是报表运行时的活真源（`ReportRepository`/`PrintSettingsRepository` 直读），ADR-009 §11 的"条件定义下线"是实现换层而非整层消失；`AUDIT_EVENT` 里的 2205 历史行按项目惯例保留（删它等于抹掉痕迹）。落地：迁移 `324_drop_modules_legacy_columns.sql`（MODULES 52→44 列、FIELDS 49→41 行）+ `325_retire_dead_modules_and_tables.sql`（模块 + SYSDQR_DA/SYSDF 与 43+10 行元数据），bootstrap 三件套重导（新库零迁移即终态） |

| 145 | 2308 工作任务记录去留 / 归档放哪 / 悬空登记 / 报表权限用例 | ✅ **2308 + `SYS_WORK_TASK` 彻底删；归档留本地（不进开源仓库）；悬空登记一并清；用例顺手修**（用户 2026-10-06 拍板） | 逐条落地：**① 2308 工作任务记录**——用户改判"这是旧系统开发团队的工作模式，我们不采用"，**整模块 + 主表 `SYS_WORK_TASK`（表内 0 行）彻底退役**（迁移 327：连带 MODULE_FORM_LAYOUT 27 / 工作台快照 11 / 权限 3 / REPORT 1 条（`REPORT_ID='SYS_WORK_TASK'`「工作任务记录」）/ 幂等键 1 + 元数据登记 FIELDS 27 / 数据源 2 / 默认查询列 9；`AUDIT_EVENT` 14 行痕迹保留）。**② 归档位置**——SYSDF 的 CSV 从 `db/archive/retire-324/` 移到**本地 `logs/archive/retire-324/`**（`logs/` 在 .gitignore 里，数据不进开源仓库），仓库只留**脚本** `scripts/export-sysdf-archive.ps1` 与迁移里的守卫数字；同时撤掉为它加的 `.gitattributes` 二进制规则。**③ 悬空登记清理**（迁移 328，幂等清扫）：清掉 19 个"库内无同名对象"的 T_ID 及其挂件（FIELDS 66 / 数据源 7 / 默认查询列 85 / TABLES 14 行），典型是 `TASK`、`SYSTEMP`、`SYSQD`、`SYSQL`、`LISTREPORT*`、`PRO_LINE`、`REPORT_IMAGE/INFO`、`SYSDH_REPORT`（随迁移 277 退役）。**④ 顺带查出、留待定夺**：`REPORT_HEADER`/`REPORT_FOOTER`/`REPORT_TAIL` 这 3 条被 **2202 页头设置 / 2203 页尾设置 / 2204 表尾设置**当主表，而那三张表早随 ADR-009 §11 下线删除 ⇒ 是"模块指向不存在的表"的真故障（与 2205 同一性质），328 **刻意跳过并显著报告**，等用户定夺这三页去留。**⑤ `ReportRightsSingleLayerLiveTests` 顺手修**：两条用例的挑组合逻辑改为"必须挑到名下确有报表的模块"（此前取 `(USER_ID, M_IDX)` TOP1 会落在 `admin` 的目录节点 11 上，永远报"该模块下没有报表"） |

| 146 | 2202/2203/2204（页头/页尾/表尾设置）三个模块 | ✅ **整模块退役**（用户 2026-10-07 拍板，迁移 329） | 迁移 328 的悬空登记清扫把它们报了出来：`REPORT_HEADER ← 2202`、`REPORT_FOOTER ← 2203`、`REPORT_TAIL ← 2204`——三张承载表早随 ADR-009 §11 下线删除，模块行却还在（`M_TAG=0`、主表指向不存在的表），属"模块指向不存在的表"的真故障，与 2205 同一性质。用户拍板照 2205 的路子整模块退役：删模块 3 + 报表定义 3（`REPORT_ID` = `REPORT_HEADER`/`REPORT_FOOTER`/`REPORT_Tail`）+ 权限 9（`SYSDD` 6 + `SYSDH` 3）+ 脏标记 3 + 三条悬空登记及其挂件（`FIELDS` 45 / 数据源 6 / 默认查询列 12 / 查询字段 12 / `TABLES` 3）；代码侧删掉只服务这三页的三个零引用 DTO（`PrintHeaderDraft` / `PrintTailDraft` / `PrintFooterDraft`）。**收尾效果**：ADR-009 §11 那批管理页（2201–2205）的遗留至此清空；`TABLES` 里"描述不存在表"的登记**归零**（328 建的不变量由 329 补齐）。**未加退役门禁条目**：这三张表在现代 schema 里从未存在过（`VirtualExpressionParserTests` 里作为旧系统 JOIN 示例出现属合法用法），加进清单只会误报 |

| 147 | 主档编号（主表主键）能不能改 | ✅ **不允许修改：编辑态只读，新增/复制态可填**（用户 2026-10-07 拍板） | 起因是真故障：编辑既有主档时改编号再保存报 "Not Found"，报障编号在服务端日志里搜不到。三处各表一半态度——前端用**界面字段值**拼记录键（改了编号，键就变成新编号）、服务端按新键查不到行返回裸 `NotFound()`（空错误体 ⇒ 按 Information 记，Warning+ 文件日志里没有）、写入侧本来就跳过主键列（即便键对得上也静默丢弃）。用户拍板：编号是**记录对外的身份**（单据、库存余额 `INV_PRO_DEPOT`、流水 `INV_DEPOT_LOG`、批次账、报表条件都按它的值引用，改它等于把引用改断），**不允许修改**。落地：`FormFieldSelector.LockPrimaryKeysOnEdit`（edit 锁主表主键，新增/复制不锁——手填编号的主档靠新增建档；判定不落 `FIELDS.IS_READONLY`，那个位与模式无关，置 1 会连新增一起禁掉）、写路径第二道闸 `RecordPayloadValidator.CheckImmutableKeys`（提交值 ≠ 记录键 ⇒ 400 `PRIMARY_KEY_IMMUTABLE`；同值回传算"原样回传"，大小写/首尾空白按库内 CI 口径）、前端写请求键改取 URL 路径主键（`keyParam`）、写路径 404 带错误体（`RECORD_NOT_FOUND` 等——原先是控制器的裸 `NotFound()` 把服务端算好的码丢掉了）。**明细行不在列**：明细身份是「主表键 + 项次」（服务端按既有项次保留），明细主键里的属性列随整行重写，改它不会打到别的行上。老系统（`DX/DxDataAccess/AjaxSaveData.cs` 的 `SaveData`）允许改：按原键 DELETE 再插新行，但不级联任何引用——那是另一个取向，不沿用 |

> ## 附：提项与选项（问题卡）

> 下面是各条最初提出时的问题与候选方案，保留原始小节号（`§N` 被其它文档引用，例如 `docs/status.md` 里的「见决策清单 §12/§13」）。结论见上表 `#N`。

## 1. BANK 银行资料空表

- **现状**：`BANK` 表 0 行；110105 银行帐号资料/收付款单银行字段无可选数据，银行结存类检查失效。
- **影响**：收付款单的银行/帐号选择为空；若有银行结存对账需求则无法检查。
- **选项**：A. 业务确认是否在用银行结存（不用 → 登记低优，不阻塞）；B. 需要时由顾问/运维提供真实银行资料录入。
- **建议**：确认使用与否；若用，数据建设属运维/顾问。

## 2. PRE_RECEIVE_DATE 客户付款天数

- **现状**：`CLIENT.PAYMENT_DAY`（付款天数）存在；应收预计收款日期（PRE_RECEIVE_DATE）由付款天数推算。
- **影响**：现代表单是否按"账单/发货日期 + 付款天数"自动推算预计收款日期，需确认规则。
- **选项**：A. 确认推算基准（发货日/账单日 + 天数，含节假日？）后服务端实现；B. 暂不自动推算（手工填）。
- **建议**：确认推算基准后实现（服务端权威，前端只读预览）。

## 3. 1408 出货通知单默认单别

- **现状**：旧系统 Shipment.aspx 有默认单别逻辑，候选 CHPC/CHTZ（BILLKIND）。
- **影响**：新增出货通知单的默认单别（单据号前缀）。
- **选项**：A. 默认 CHPC；B. 默认 CHTZ；C. 无默认（手选）。
- **建议**：业务确认默认值后，按既有自动单号机制配置。

## 4. 170203 预付帐款单是否允许无采购单

- **现状**：`PUR_PREPAY_M` 明细可引用采购单或为纯预付（冲抵）。
- **影响**：业务约束——先预付后采购（允许无采购单）vs 必须已有采购单才能预付。
- **选项**：A. 允许无采购单（纯预付，冲抵时再关联）；B. 必须有采购单。
- **建议**：业务确认约束后，在领域规则/DomainRuleService 落校验。

## 5. 1515 返工完成量覆盖语义（2026-08-23 已确认）

- **现状**：`MOC_PRODUCT_OUT_M` 返工单完成量（FINISHED_QTY 等）与原始完成量的关系。
- **确认结果**：✅ **对齐旧系统固定扣减**——旧 SP `P_WF_MOC_PRODUCT_OUT` 批核时固定
  `FINISHED_QTY -= 出库 QTY`（解批反向），不做覆盖/累加参数化；运行态已由受控
  `P_WF_MOC_PRODUCT_OUT` 实现，与旧系统等价。
- **处置**：登记即完成；本项 ⚠️ 状态关闭。

## 6. 1610/1616 报表权限补配（按环境）

- **现状**：1610 收料核价/1616 备料单无 REPORT 定义与报表权限（admin 个人 SYSDD 已清，组权限生效）。
- **影响**：这两个模块的报表不可打印。
- **选项**：按环境由顾问用 `PreparePrintConfig.ps1` 模板补配 REPORT + 权限。
- **建议**：属实施配置，非代码决策。

## 7. ERROR_NO_SAVE（异常记录不可保存）

- **现状**：38 个模块标记 ERROR_NO_SAVE=1；旧系统**无任何消费代码**（仅 MenuBuilder 维护，
  标签"异常记录不可保存"），DX/ERP 均无校验逻辑——旧系统实际未实现该语义。
- **影响**：现代统一表单不实现；字段保留（菜单管理可维护）。
- **选项**：A. 登记"旧系统未实现字段"，保留数据不实现；B. 业务定义"异常记录"具体规则后受控实现。
- **建议**：默认 A（不臆造规则）；业务有明确"异常"判定（如状态/金额校验）时再按 B 立项。

## 8. 3302 抱怨退货处理单 PRODUCE_NO（送货单号）必填引用（放量阻塞，2026-08-19）

- **现状**：自动放量第 1 批中 3302 唯一阻塞——`PRODUCE_NO` 为必填（IS_VERIFY=1），
  选择器为依赖型（`COP_SEND_D` 按品号过滤已批核送货单 `COP_SEND_M.CONFIRM_TAG=1`），
  当前无可选引用数据（源表有 8119 行送货单，但依赖主表 PRO_NO 联动过滤）。
- **影响**：3302 未放量；其余 30 个单表模块已启用。
- **选项**：A. 对齐旧系统——该单基于送货单产生，登记为前置数据依赖，真实送货单数据建设后
  重跑 `scripts/auto-enable-batch.ps1` 自动放量；B. 若业务允许"无送货单的抱怨/退货"，
  改 `IS_VERIFY=0` + 领域校验（有则必须完整引用）。
- **确认结果（2026-08-23）**：✅ **退货必须关联送货单**（业务约束成立，保持 `IS_VERIFY=1`）；
  开发期无真实数据，按用户拍板生成**语义化测试数据**（已批核送货单 `COP_SEND_M/D`，品号联动可命中）
  后重跑自动放量。

## 9. 未解析选择器字段观察（非阻塞，2026-08-19）

- **现状**：第 1 批放量中 5 个字段选择器源为空（测试数据已清理）：
  2701 `TYPE_ID_SUPERIOR`（工序上一级类别，自引用）、3305/3306 `COMPLAIN_NO`/`EXCEPTION_NO`
  （跨单引用）、180104 `DIMISSION_ID`（离职类别，18010109 已启用）。
- **影响**：字段留空保存（非必填），不影响放量；真实数据建设后自动可解析。
- **建议**：登记观察，数据建设后由流水线重跑复核，不单独处置。

## 10. 1305 库存日志是否允许手动新增/编辑（2026-08-19 已确认）

- **现状**：旧系统 `ERP/Comm/View_Master.aspx.cs` 中，`NEW_URL` 与 `MODI_URL` 均为空时
  新增按钮隐藏（`btnAddnew.Visible=false`）、编辑弹窗 URL 为空（不可编辑）；1305 为白名单内
  **唯一**双 URL 为空的模块。现代实现此前因"空 URL 回退统一表单"误开放了新增/编辑
  （`HasAdd/HasEdit = formEnabled`，见 `DocumentWorkbenchController.cs:295`）。
- **确认结果**：✅ 库存日志为系统流水，只读——不允许手动新增/编辑（与旧系统一致）。
- **处置**：1305 移出 `UnifiedFormEditor.EnabledModuleIds`（白名单 174→173）；
  工作台列表/查询/导出保留（definition 200），统一表单新增/编辑/浏览返回 404（已验证）。

## 11. 疑似重复模块三对（2026-08-19 已确认）

- **1403/1415 客户询价单**：同表（COP_CHAFFER_M/D）、同名、同旧页（`~/COP/Chaffer.aspx`）、
  同菜单（销售管理）；ERP 源码无 1415 引用，仅差异 AUTO_APPROVE（1403=1/1415=0）与排序，
  数据 0 行——**真重复**。决策：✅ 保留 1403、下线 1415（M_TAG=0，EOS-18 幂等迁移）。
- **180308 保密薪资基本项目 / 1908081 人员薪资项目**：同表 HR_BASEPAY_M/D，FILTER 互补
  （IF_SECRECY=1/0），旧页不同（BASEPAY_MBM.aspx / BASEPAY_M.aspx）——**有意拆分，保留**。
- **180311 / 180501 工资项目默认设定**：HR_WAGESYS / HRM_WAGESYS，旧系统双根菜单
  （18/38）下的 HR_* 与 HRM_* 平行体系——**非重复，保留**。

## 12. AUTO_APPROVE=1 的 57 个模块（2026-08-19 已确认）

- **现状**：57 个候选模块 `MODULES.AUTO_APPROVE=1`（保存即批核，落 CONFIRM_TAG，SYSTEM
  自动确认 + 审计幂等守卫，机制已实现并测试），此前列入"需顾问确认"。
- **确认结果**：✅ **保存即确认逻辑正确，无需逐模块确认**（用户拍板）——57 个模块解除
  放量确认门，可直接进入自动放量流水线。
- **处置**：随下一批自动放量（36 主子表 / 57 AUTO_APPROVE / 4 FILTER）统一执行，
  阻塞项照旧列出供审核。

## 13. 带模块 FILTER 的 4 个候选（2026-08-19 已确认）

- **现状**：1303 料件库存资料（`QTY>0.1`）、14997 应收未收明细（`CONFIRM_TAG=1 AND
  FINISHED_TAG=0 AND SUM_AMOUNT-RECEIVE_AMOUNT>0`）、180308 保密薪资基本项目
  （`IF_SECRECY=1`）、1908081 人员薪资项目（`IF_SECRECY=0`）；FILTER 走
  `DataFilterParser` 白名单 + 参数化受控解析（M9/M93 已实现并单测/E2E 验证）。
- **确认结果**：✅ **系统逻辑校验通过即放行，无需逐条业务确认**（用户拍板）——
  4 个模块解除放量确认门，随下一批自动放量执行。

## 14. 170204 其它付款凭证 RECEIVE_ID/ACCOUNT_TYPE_ID 引用基础资料为空（放量阻塞，2026-08-19）

- **现状**：第 2a 批中 170204 唯一阻塞——`RECEIVE_ID`（结算方式）必填，选择器源表
  `RECEIVE` 与 `ACCOUNT_TYPE`（帐款类型）当前 0 行；170104 其它收款收据同字段为选填故已放量。
- **影响**：170204 未放量；其余 34 个主子表模块已启用。
- **选项**：A. 对齐旧系统——付款凭证必须有结算方式，登记为前置数据依赖，110109 结算方式设定 /
  110110 帐款类型设定数据建设后重跑自动放量；B. 若业务允许无结算方式付款，改 `IS_VERIFY=0` + 领域校验。
- **确认结果（2026-08-23）**：✅ 保持必填；按用户拍板生成 RECEIVE（结算方式）/ ACCOUNT_TYPE（帐款类型）
  语义化测试数据后重跑自动放量。

## 15. 第 2b 批放量阻塞 10 项（2026-08-19）

- **已解决（流水线修复后放量）**：110105/110106/110303/110306（BANK/TAX/UNIT/DEPOT）——
  主键字段选择器自引用自身表（如 BANK_ID→BANK），测试生成器误取首行引用值撞主键；
  已改为自引用主键生成唯一码，4 模块重跑通过。
- **数据前置（待基础资料建设后重跑自动放量）**：
  2002 原纸条码资料（PAP_TYPE/BRAND/GRAMME/SPECS 原纸基础链为空）；
  3001/3002/3004 海关进/出仓单（明细 DEPOT_ID 依赖 CUS_DEPOT 海关库别，表为空）。
- **元数据缺口（待 2302 治理后重跑）**：
  1304 料件每月统计单（主表 PRO_NO 缺 FIELDS 行，17 行元数据 vs 14 物理列，含幽灵行）；
  110308 材质等级（STUFF_GRADE 无任何 FIELDS 元数据）。
- **特殊管理页（非统一表单模块，从表单候选剔除）**：2201 报表排序汇总设置 / 2202 页头设置 /
  2203 页尾设置 / 2204 表尾设置——由 `/admin/report-setup` 等专用管理页承接，不做统一表单放量；
  已从 内部资料《统一表单白名单候选.csv》 移除。

## 16. update.sql 冻结 + EOS.ERP 唯一库（2026-08-21 拍板冻结；2026-08-22 修订确立唯一库 + DbUp 通道）

- **现状**：本机升级史 update.sql 已约 2 万行、27 个事务块，混装三类生命周期完全不同的内容——
  ① 一次性安全 cut-over（ADR-004 密码/URL）；② **硬编码本客户库**的权限/元数据/数据修正
  （UPDATE MODULES/FIELDS/TABLES/SYSDH，按本客户 M_IDX/表名/字段状态）；③ 结构/SP 迁移（P_WF_*、P_*_After_Save，
  相对可跨客户复用）。幂等守卫只保护"同一库重复执行"，保护不了"不同库"——第二个客户库上硬编码段落要么错误命中、
  要么错误跳过，20k 行单文件的审查/测试成本已超过收益。
- **确认结果（2026-08-21）**：✅ **冻结**——不再向 update.sql 追加新段落；保留作为本客户升级史 + 转换逻辑模式库，不删除。
- **2026-08-22 修订（EOS.ERP 唯一库约定确立）**：EOS 本质是全新系统，唯一业务库为 `EOS.ERP`；
  新库对象一律建在 `EOS.ERP` 内并经 **DbUp 版本化迁移**（`EOS.API/Data/Migrations/`，`ErpDatabaseInitializer`
  启动执行，journal `ERP_SCHEMA_JOURNAL`，对象名全大写）直接落地——该通道已随 ATTACHMENT 表启用。
  `EOS.IM`/`EOS.Mail` 为历史遗留独立库，暂不处理、不登记、不理会。
- **客户升级管道（暂缓，待客户库）**：还原（脱敏）→ 探测旧版本/结构 → 对照不变量差异分析 → 生成客户专属方案 →
  副本库 dry-run + 跑既有验收/回归 → 出具升级方案。目标状态用"不变量"而非"与本公司库 diff"定义。

## 17. 开发/测试用语义化测试数据（2026-08-23 用户拍板）

- **背景**：当前系统处于开发阶段，无实际 ERP 用户，无法产生真实业务数据；放量主线的
  3302/170204/2002/3001/3002/3004 及 8 个复跑模块均被"无引用数据"阻塞。
- **确认结果**：✅ **按上下文语意与真实业务场景生成测试数据**，用于开发与测试系统功能和流程；
  不因无数据而放宽业务约束（必填/引用关系保持旧系统语义）。
- **范围**：
  - 3302：已批核送货单 `COP_SEND_M/D`（品号联动可命中）；
  - 170204：`RECEIVE` 结算方式 + `ACCOUNT_TYPE` 帐款类型基础资料；
  - 2002：`PAP_*` 原纸基础链（类型/品牌/克重/规格）；
  - 3001/3002/3004：`CUS_DEPOT` 海关库别；
  - 8 个依赖真实业务数据的模块（1207/1404/1411/1412/1517/1519/2910/180206）复跑验收数据。
- **通道**：基础资料表以幂等种子脚本落地；单据链经 EOS.API 受控端点创建（对齐 E2E 既有模式）；
  测试数据带明确前缀，可被真实数据替换。
- **执行结果（2026-08-23）**：`scripts/seed-dev-data.ps1` 幂等落地；RECEIVE 4 / ACCOUNT_TYPE 3 /
  CUS_DEPOT 3 / PAP_TYPE 3 / PAP_BRAND 3 / PAP_GRAMME 5 / PAP_SPECS 4；
  3302 链 = 品号 EOSDEV-PK-001（CLIENT_ID=668ZS，已批核）→ 其它入库 100/CP（已批核）→
  送货单 SHD/SH26080152（已批核）。放量流水线重跑 **3302/170204/2002/3001/3002/3004 全部 crud-pass**
  （白名单 264），回归 4/4 全绿。P0 复跑验收完成：1411/1412/1517/1519/2910/180206 专项验收全过、
  1404 crud-pass；**1207 已下线**（历史重复模块，清单修正为 7 个）；
  3305/3306 引用链（已批核客诉单 RESULT1+RESULT3 + 异常单 RESULT1+RESULT2）落地，完整度 100%。
  P1 元数据治理（2026-08-23）：1304/110308 放量成功（白名单 266），回归 4/4；
  1304 阻塞根因 = 旧 SYSQL_DEFAULT 主表 PRO_NO 虚拟字段残留（DETAIL_NO_FIELDS 已清空）+
  MONTH_DATE 长度校验误拒（已修复）；110308 FIELDS 注册齐全。

## 18. 数据选择统一走统一选择器（2026-08-27 用户拍板）

- **背景**：2306 新增用户（开户）的员工选择曾用 loader 自建通道（自定义 `/admin/users/employees` 端点 +
  前端 loader），未走统一选择器 sourceKey 机制，且前端按小写键取值导致点行不选中、确认回填失败。
- **确认结果**：✅ **凡从既有业务数据中选择记录作为输入，必须使用 `UnifiedChooser`**；数据源仅允许
  `formField` 或 `sourceKey`，`loader` 仅存量过渡、新代码禁止新增；列与默认列必须服务端元数据驱动；
  新增数据源在 `ChooserRepository` 注册白名单 + 权限门 + 参数化查询。
- **处置**：规则写入 `AGENTS.md`「统一选择器规范（强制）」与 `docs/架构.md`「统一选择器」；员工选择器已
  重构为 `user-admin.employees` sourceKey（元数据列 + 默认列 + 未开户过滤 + 2306 权限门），提交 9712b22。

## 19. 字段数据来源（数据选取）模型重构（2026-08-28 用户拍板，ADR-008）

- **背景**：字段设置「数据来源 1-4」旧模型把数组塞进 FIELDS 四组固定列（`CHOOSE_T_ID1-4 / CHOOSE_FILTER1-4 /
  CHOOSE_RETURNVAL1-4 / CHOOSE_ACTIVE1-4 / CHOOSE_M_IDX1-4 / CHOOSE_T_DESC1-4`，共 24 列），上限 4 且
  过滤条件（`CHOOSE_FILTER`）是**运行时参与计算**的手写 SQL 串（`DataFilterParser` 解析进选择器 WHERE）；
  且现代统一表单 `choosers.find(active)` 只取第一个启用数据源，与旧系统「多来源各是各的入口」语义不符
  （送货单明细可选出货排程、也可选出货通知单，打开两个不同的选择器）。
- **确认结果（2026-08-28）**：✅ 彻底重构（开发期无客户影响）。
  1. **独立数据源表 `FIELD_DATASOURCE`**（T_ID+F_ID+SERIAL_NO，任意数量、有序、FK→FIELDS ON DELETE CASCADE），
     替换 FIELDS 四组 24 列；`CHOOSE_PAGE / CHOOSE_MULTI / ONLY_CHOOSE` 字段级行为列保留；
  2. **多数据源 = 各是各的入口**：1 个直接弹选择器、多个先弹来源菜单（`SOURCE_DESC`）再选；修正 `find()` 语义；
  3. **过滤条件纯结构化**（`FILTER_STRUCT` JSON + 保存即校验 + 运行时编译参数化 SQL），**不留原始 SQL 后门**
     （答复：CHOOSE_FILTER 是参与计算的，不是参考；双通道会造成"双份真相"漂移）；
  4. **回填映射有序 JSON**（target=column 对，`RETURN_ITEMS`，落库前规范化，从源头杜绝 EOS-22 死映射复发）；
  5. **FIELDS 四组 24 列物理删除**（有迹可查由 ADR-008 + DbUp 迁移历史 + git 承担）；
  6. 字段设置 UI：统一选择器选表（sourceKey，仿 `menu-admin.tables`）+ 两步式构建。
- **存量 `CHOOSE_FILTER` 规则化转换评估（2026-08-28 实测）**：1008 个非空单元格 / 291 去重——
  - 第一档 ~70% 纯比较链（`表.列 运算符 值` AND 链 + `{m.X}`/`{d.X}` 模板 + `ISNULL(列,0)=0` 简单宏）→ 规则化直转；
  - 第二档 ~28% 需扩展结构化算子（`ISNULL(列A-列B,0)>0` 差值表达式、`NOT IN/EXISTS` 排除子查询、嵌套 OR 组、
    `DATEDIFF` 当日宏）→ 随 P3 编译器扩展后转换；
  - 第三档 ~4%（约 11 个：`f_get_*` 函数、字符串拼接 CAST 排除、悬空 `AND/OR`、截断/乱码）→ 人工清单重建；
  - 迁移配验证护栏：转换 → `ChooserFilterValidator` 试编译 → 与原 `DataFilterParser` 抽样等价对拍，
    任一不过即降档人工清单，不静默落库。
- **处置**：ADR-008 已接受（`docs/decisions/ADR-008-字段数据来源模型重构.md`）；实施分 P1 存储迁移 →
  P2 字段设置 UI → P3 条件编译器/构建器（含第二档算子）→ P4 回填映射构建器，各期独立回归。
- **实施状态（2026-08-28）**：**P1 已落地（迁移 017 已执行）；P2/P3/P4 已编码完成（迁移 018 待重启执行）**——
  DbUp 迁移 017（建 `FIELD_DATASOURCE`/`CHOOSER_FILTER_MIGRATION_LOG`、存量搬移 + `RETURN_ITEMS` SQL 无损转换、
  `FILTER_STRUCT` 三档转换（档一 374 行离线 C# 生成 + 漂移守卫；档二/三 603 行入迁移清单 fail-closed）、
  DROP 24 列、`P_Change_M_IDX` 改写、受影响模块标脏）；`ChooserFilterCompiler/Validator/LegacyConverter`
  组件 + 58 单测；运行期定义加载/字段 CRUD/发布校验/表单选择/命令回显全切新表；前端最小适配
  （`RETURN_ITEMS` JSON 消费 + 可变数据源列表 + `serialNo`）。EOS.API 0 警 0 错、后端 735/737 绿
  （2 失败为环境前置）、前端 lint/build + 501 测试全绿。P2-P4 待做（字段设置 UI 构建器、扩展算子、
  回填映射构建器、二/三档存量回填）。运行时验证待用户重启 API 后执行迁移 + UI 冒烟。
- **P2/P3/P4 完成（2026-08-28）**：编译器/转换器扩展（表达式/嵌套组/子查询/受控表值函数 +
  QUERY_RELATION 跨表 JOIN 权威机制 + 裸列归属）；迁移 018（603 待重建 + 188 兼容失败行 →
  **全自动 289 行 / 人工清单 502 行**，漂移守卫 + 事务回滚实测通过）；字段设置 UI 构建器
  （来源排序/统一选择器选表/过滤构建器/回填配对清单/多来源菜单，sourceKey `field-admin.tables|columns|fields`
  权限门 2302）；后端 742/744 绿（2 环境前置）、前端 lint/build + 501 测试全绿。人工清单：
  本机证据：fields-chooser-migration/manual-list-p3.csv（423 行跨表引用不在源表 QUERY_RELATION 的死配置 +
  79 行脏数据/超复杂待人工决定）。**迁移 018 待用户重启 EOS.API 后落地**。
- **字段设置全页化（2026-08-28 用户拍板）**：全尺寸页面 `/admin/fields/:tableId/:fieldId`（左字段导航 +
  右选项卡），选项卡归类为基本信息/数据来源/权限与行为/表单布局/高级设置 + 变更历史（AUDIT 明细）；
  两个入口（2302 字段维护 + 工作台表头字段设置）统一跳全页；后端 749/751 绿、前端 504 测试全绿。

  已由 170101 批核受控 SP（P_WF_COP_ACCOUNT）实现，登记即完成 |
  form-definition defaultValues.SHIPMENT_TYPE=CHTZ 已验证 |
  （FINISHED_QTY -= 出库 QTY，解批反向），非覆盖/累加二选一；
  建议对齐旧系统固定扣减、不做参数（见讨论） |
  form-definition 恢复 200 |
  工作台保留只读列表，统一表单 404（2026-08-19 验证） |
  180308/1908081、180311/180501 确认非重复保留 |
  随下一批自动放量执行（阻塞照旧上报） |
  随下一批自动放量执行 |
| 148 | 行归属三列（`CI`/`OWNER`/`OWNER_G`）与"按执行范围看数据"能力 | ✅ **三列全库物理下线、执行范围过滤退役**（用户 2026-10-07 拍板，迁移 334/335） | 真库复核：`CI` 在 250 张业务表恒为 `'DEFAULT'`（单公司部署、`COMPANY` 只有哨兵行），不区分任何数据；`OWNER`/`OWNER_G` 的唯一消费端是 `EXEC_TAG` 的 `B`/`C`/`D`/`E` 数据范围过滤，全库只有 5 条组权限记录在用，其中 4 条落在没有主表的模块上、实际生效 1 条（组 `CN` 的 2 个账号 @ 模块 1204，而他们在这张表上本就一行都看不到）。用户同时拍板：**`DEPT.CI`/`SYSDN.CI` 不删**——它们是部门与员工的公司归属（公司主档的现成锚点，支撑登录 `company_id` 声明、用户管理页「公司」列与 `COMPANY_NAME` 虚拟字段），与业务表上那 250 个常量 `CI` 不是同一件事。落地：迁移 334（752 列 + 252 个默认约束物理删除、746 行 `FIELDS` 与 `FIELD_DATASOURCE`/`SYSQL_*`/`SYSQR_*` 登记级联清理、`SYSDH` 存量 `B`/`C`/`D`/`E` 收敛为 `Z`）；迁移 335（版式行 771 行、选择器回填映射、模块 1401 分组表达式三处配置面残留）。`EXEC_TAG` 语义收敛为「只有 `Z`/`A`/空 表示无附加范围，其余一律拒绝」；保留 `WF_APPROVE.OWNER`（同名异义：审批待办人）与 `COMPANY` 表整体 | `ADR-013` §8、`EOS.API/Data/Migrations/334_drop_row_ownership_columns.sql`、`EOS.API/Data/Migrations/335_drop_row_ownership_config_residue.sql`、`EOS.API/Data/WorkbenchScopeFilter.cs` |
| 151 | 2301 拆分为纯目录「系统功能」＋四个配置面（ADR-033） | ✅ **用户 2026-10-10 拍板**（两次推翻见正文） | 2301 由 CUSTOMPAGE 改判**纯目录「系统功能」**，原内容重编号为 **230101「模块管理」**（页签收敛为 基础/主表/子表，不留行为页签的只读视图）；2315「模块分组」重编号为 **230102**；「自定义按钮」独立成 **230103**；**「行为动作 + 校验规则」独立成 230104**（一个模块、页内两个页签）。**两处推翻**：① **校验规则不独立成 230105**——它与行为动作在同一条链上（同一事件先拦后做）、同一个事务、同一个发布门，97 个有校验的模块里 81 个同时有动作，而"只配校验的那批人"经实测不构成独立群体（那 16 个模块的分闸本就关着，而校验根本不受分闸约束）；且编号不回收——拆出去再合回来会永久多一个退役号（可逆性不对等），"先合、需要再切"只加一个新号。② **2304/2305/2306 不改挂**，留在 23 段：排列整齐不值得每天多一层点击，将来真要收只是一次纯 `M_P_IDX` + `SORT_IDX` 调整。其余口径：**权限随配置面走**（`MODULE_CONFIG_TAG` 从 2301 一个总开关改为每面一个）；**发布对用户隐式**（保存即自动发布，快照与发布门都不拆）；**新增模块编号分配改「父编号＋两位序号」**（现状 `MAX(M_IDX)+1` 今天会产出 18010120）；本次不做「待发布清单」；不复用 2308。落地分四批（结构 → 拆按钮 → 拆动作＋校验 → 编号分配器）。状态：**未实施** | `docs/decisions/ADR-033-系统功能目录与模块配置面拆分.md`（迁移编号待定，当前最大 343） |
| 152 | 效果引擎模块级开关（`MODULES.EFFECT_ENGINE_TAG`）的去留（ADR-034） | ⚠️ **方向拍板、三项待定**（2026-10-10） | 实测：**分闸 ≡「该模块有没有动作」，110:110 零例外**；开着却零配置 4 个（1201/1401/1601/300303）；行级 `ENABLED` 建了从没用过（动作 0/349）。而**校验侧早就不受这道闸约束**，且是刻意的——`EffectEngineInvoker.ValidateStageAsync` 注释原文："不要在那里加闸，加了会让一条已启用的规则静默什么都不做"。故本决策把动作侧追上：**分闸下线**，判据改推导「该事件有启用的动作行」（没有即 `Ran=false` 回基线路径，与今天关闸逐字一致），停用统一走行级 `ENABLED` + 配置面的"批量停用本模块动作"操作。**总闸保留**（它管"引擎自身"的系统性缺陷，不是单模块配置），但 ① 改称「引擎断路器」② 绑定从 `Program.cs:219` 的一次性单例改 `IOptionsMonitor`——否则改 `appsettings` 必须重启，"一行配置止血"名不副实。**快照 `effectEngine` 段保留恒 `{"enabled":true}`、不再消费**：262 个现行快照 = 114 个 `true`（=分闸开着）+ 148 个 `null`（=分闸关着），删段属内容比较器的真差异 ⇒ 重发布即版本 +1、对拍证据按 ADR-023 降级。**唯一行为变化**：4 个空跑模块的批核分支从"引擎接管（实则空转）"落到"无副作用批核"，与 #108 口径一致，属修正。迁移手法：先推导、后删列，中间用"开关值 vs 推导值"双算对拍守着；删列前断言差集**恰好是那 4 个具名模块**。**待定**：① 总闸是否改 Monitor ② 段清理时机 ③ 4 个模块分支变化是否接受。状态：**未实施** | `docs/decisions/ADR-034-效果引擎开关收敛.md`、本台账 #58（该开关的原始决策） |

