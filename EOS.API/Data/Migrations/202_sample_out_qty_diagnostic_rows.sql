-- ============================================================================
-- EOS.ERP migration 203: 2404 打样出库单库存校验的诊断口径修正
-- ----------------------------------------------------------------------------
-- 现象：2404 建单（自动批核）时若触发"样品库存不足"这条校验，接口返回 **500**，日志里是
-- `SqlException: 列名 'SERIAL_NO' 无效`，堆栈落在
-- `EffectValidationExecutor.CheckQuantityNotExceedAsync`。
--
-- 根因：该规则（`RULE_ID=10334`）是**分组形态**（`thisQty.agg=SUM`），诊断列却按"口径一
-- （每组一行）"渲染——分组子查询只投影 match 键与求和别名，而 `diagnosticFields` 要的是
-- `SOURCE.SERIAL_NO` 这种**非分组键**的源明细列 ⇒ 生成 `S1.[SERIAL_NO]`，该列在分组投影里
-- 不存在 ⇒ 无效列名。
--
-- 处置：给该 check 加上 `"diagnosticRows":"SOURCE"`（口径二：列出违规分组下的**源明细行**，
-- 诊断列一律取原始来源别名 `S`）。这正是"以下序号项…"这类文案的设计口径——规则文案本身
-- 就是"以下序号项样品库存不足"，与非分组键的序号列一致。引擎侧已有该口径的实现与闭集校验
-- （`ParseSourceRowDiagnosticCells`：仅接受 SOURCE，且要求分组形态）。
--
-- 影响面：仅这一条规则。运行期读的是**已发布快照**里的校验规则，故本迁移同时把模块标脏；
-- 必须重发布 2404 才生效。
--
-- 幂等：按 RULE_ID 更新，已带 `diagnosticRows` 则空转。
-- 回滚：把 `"diagnosticRows":"SOURCE",` 从 PARAM_STRUCT 中移除（并重新标脏）。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 52700, @GUARD_MESSAGE, 1;

DECLARE @ruleId INT = 10334;
DECLARE @moduleId INT = 2404;
DECLARE @needle NVARCHAR(100) = N'"diagnosticFields"';
DECLARE @patch NVARCHAR(100) = N'"diagnosticRows":"SOURCE","diagnosticFields"';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE WHERE RULE_ID = @ruleId AND MODULE_ID = @moduleId)
    THROW 52701, N'规则 10334 不存在或不属于模块 2404，迁移中止。', 1;

DECLARE @current NVARCHAR(MAX) = (SELECT PARAM_STRUCT FROM dbo.MODULE_VALIDATION_RULE WHERE RULE_ID = @ruleId);

-- 已修过则空转（幂等）
IF CHARINDEX(N'"diagnosticRows"', @current) > 0
BEGIN
    PRINT N'== 规则 10334 已带 diagnosticRows，无需处理 ==';
    RETURN;
END

-- 前提守卫：必须恰好一处 diagnosticFields，避免 REPLACE 误伤多处 check
IF CHARINDEX(@needle, @current) = 0
    THROW 52702, N'规则 10334 的参数里找不到 diagnosticFields，形态与预期不符，迁移中止。', 1;
IF (LEN(@current) - LEN(REPLACE(@current, @needle, N''))) / LEN(@needle) <> 1
    THROW 52703, N'规则 10334 出现多处 diagnosticFields，逐处处理前不应批量替换，迁移中止。', 1;

UPDATE dbo.MODULE_VALIDATION_RULE
   SET PARAM_STRUCT = REPLACE(@current, @needle, @patch),
       LAST_UPDATE_BY = N'DbUp',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE RULE_ID = @ruleId;

/* ---------- 标记待发布：运行期读的是已发布快照 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @moduleId AS MODULE_ID) AS S ON T.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF CHARINDEX(N'"diagnosticRows":"SOURCE"', (SELECT PARAM_STRUCT FROM dbo.MODULE_VALIDATION_RULE WHERE RULE_ID = @ruleId)) = 0
    THROW 52704, N'规则 10334 未写入 diagnosticRows，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = @moduleId AND DIRTY_TAG = 1)
    THROW 52705, N'模块 2404 未标记为待发布，迁移中止。', 1;

PRINT N'== 2404 打样出库单的库存校验诊断口径已改为 SOURCE（源明细行），模块待重发布 ==';
