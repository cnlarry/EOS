-- ============================================================================
-- 037: Field data source governance B class — add QUERY_RELATION joins
-- ----------------------------------------------------------------------------
-- Background: CHOOSER_FILTER_MIGRATION_LOG rows flagged JOIN_CLOSURE_FAIL could not be
-- rebuilt because the cross-table referenced by the filter condition is not in the source
-- table's TABLES.QUERY_RELATION aliases, so ChooserJoinCatalog.BuildJoinClause cannot
-- reconstruct the JOIN → fail-closed empty options. This migration adds the standard
-- LEFT JOIN for each source table so the referenced table becomes reachable.
-- Impact: QUERY_RELATION changes on shared table MOC_PRODUCE_M affect all choosers/virtual
-- columns that use that table as their source.
-- After running: re-run ChooserBackfillRerunTool (EOS_TOOL_CHOOSER_RERUN=1) to generate
-- the backfill migration (038).
-- ============================================================================
SET NOCOUNT ON;

IF DB_NAME() <> N'EOS.ERP'
BEGIN
    THROW 50000, N'本迁移只能在 EOS.ERP 数据库内执行。', 1;
END;

-- ============================================================================
-- 1. MOC_PRODUCE_M：补 MOC_BOM_STRU_M / MOC_PRODUCE_PROCESS_M / SFC_PRODUCE_M
--    现有 JOIN：MOC_PRODUCE_D, PRODUCT_M, PRODUCT 等（10+ JOIN）
--    建议 ON 键：PRODUCE_TYPE, PRODUCE_NO（两侧均有，SSDT 快照已确认）
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = N'MOC_PRODUCE_M'
    AND QUERY_RELATION LIKE N'%MOC_BOM_STRU_M%')
BEGIN
    UPDATE dbo.TABLES
    SET QUERY_RELATION = LTRIM(RTRIM(ISNULL(QUERY_RELATION, N''))) + N'
LEFT JOIN MOC_BOM_STRU_M WITH (NOLOCK) ON MOC_PRODUCE_M.PRODUCE_TYPE = MOC_BOM_STRU_M.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO = MOC_BOM_STRU_M.PRODUCE_NO
LEFT JOIN MOC_PRODUCE_PROCESS_M WITH (NOLOCK) ON MOC_PRODUCE_M.PRODUCE_TYPE = MOC_PRODUCE_PROCESS_M.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO = MOC_PRODUCE_PROCESS_M.PRODUCE_NO
LEFT JOIN SFC_PRODUCE_M WITH (NOLOCK) ON MOC_PRODUCE_M.PRODUCE_TYPE = SFC_PRODUCE_M.PRODUCE_TYPE AND MOC_PRODUCE_M.PRODUCE_NO = SFC_PRODUCE_M.PRODUCE_NO'
    WHERE T_ID = N'MOC_PRODUCE_M';
    PRINT N'[037] MOC_PRODUCE_M QUERY_RELATION 已补（MOC_BOM_STRU_M / MOC_PRODUCE_PROCESS_M / SFC_PRODUCE_M）。';
END
ELSE
    PRINT N'[037] MOC_PRODUCE_M QUERY_RELATION 已存在（跳过）。';

-- ============================================================================
-- 2. MOU_SCRAP_D：补 MOU_BATCH_M / MOU_BATCHTOP_M
--    现有 JOIN：MOU_SCRAP_M, MOU_MOULD
--    建议 ON 键：SCRAP_TYPE, SCRAP_NO
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = N'MOU_SCRAP_D'
    AND QUERY_RELATION LIKE N'%MOU_BATCH_M%')
BEGIN
    UPDATE dbo.TABLES
    SET QUERY_RELATION = LTRIM(RTRIM(ISNULL(QUERY_RELATION, N''))) + N'
LEFT JOIN MOU_BATCH_M WITH (NOLOCK) ON MOU_SCRAP_D.SCRAP_TYPE = MOU_BATCH_M.SCRAP_TYPE AND MOU_SCRAP_D.SCRAP_NO = MOU_BATCH_M.SCRAP_NO
LEFT JOIN MOU_BATCHTOP_M WITH (NOLOCK) ON MOU_SCRAP_D.SCRAP_TYPE = MOU_BATCHTOP_M.SCRAP_TYPE AND MOU_SCRAP_D.SCRAP_NO = MOU_BATCHTOP_M.SCRAP_NO'
    WHERE T_ID = N'MOU_SCRAP_D';
    PRINT N'[037] MOU_SCRAP_D QUERY_RELATION 已补（MOU_BATCH_M / MOU_BATCHTOP_M）。';
END
ELSE
    PRINT N'[037] MOU_SCRAP_D QUERY_RELATION 已存在（跳过）。';

-- ============================================================================
-- 3. QC_COMPLAIN_M：补 QC_APPLY_M / QC_REWORK_M / QC_SCRAP_D
--    现有 JOIN：BILLKIND, CLIENT, PRODUCT
--    建议 ON 键：COMPLAIN_NO
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = N'QC_COMPLAIN_M'
    AND QUERY_RELATION LIKE N'%QC_APPLY_M%')
BEGIN
    UPDATE dbo.TABLES
    SET QUERY_RELATION = LTRIM(RTRIM(ISNULL(QUERY_RELATION, N''))) + N'
LEFT JOIN QC_APPLY_M WITH (NOLOCK) ON QC_COMPLAIN_M.COMPLAIN_NO = QC_APPLY_M.COMPLAIN_NO
LEFT JOIN QC_REWORK_M WITH (NOLOCK) ON QC_COMPLAIN_M.COMPLAIN_NO = QC_REWORK_M.COMPLAIN_NO
LEFT JOIN QC_SCRAP_D WITH (NOLOCK) ON QC_COMPLAIN_M.COMPLAIN_NO = QC_SCRAP_D.COMPLAIN_NO'
    WHERE T_ID = N'QC_COMPLAIN_M';
    PRINT N'[037] QC_COMPLAIN_M QUERY_RELATION 已补（QC_APPLY_M / QC_REWORK_M / QC_SCRAP_D）。';
END
ELSE
    PRINT N'[037] QC_COMPLAIN_M QUERY_RELATION 已存在（跳过）。';

-- ============================================================================
-- 4. QC_EXCEPTION_M：补 QC_APPLY_M / QC_REWORK_M / QC_SCRAP_D
--    现有 JOIN：BILLKIND, CLIENT, PRODUCT
--    建议 ON 键：EXCEPTION_NO
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = N'QC_EXCEPTION_M'
    AND QUERY_RELATION LIKE N'%QC_APPLY_M%')
BEGIN
    UPDATE dbo.TABLES
    SET QUERY_RELATION = LTRIM(RTRIM(ISNULL(QUERY_RELATION, N''))) + N'
LEFT JOIN QC_APPLY_M WITH (NOLOCK) ON QC_EXCEPTION_M.EXCEPTION_NO = QC_APPLY_M.EXCEPTION_NO
LEFT JOIN QC_REWORK_M WITH (NOLOCK) ON QC_EXCEPTION_M.EXCEPTION_NO = QC_REWORK_M.EXCEPTION_NO
LEFT JOIN QC_SCRAP_D WITH (NOLOCK) ON QC_EXCEPTION_M.EXCEPTION_NO = QC_SCRAP_D.EXCEPTION_NO'
    WHERE T_ID = N'QC_EXCEPTION_M';
    PRINT N'[037] QC_EXCEPTION_M QUERY_RELATION 已补（QC_APPLY_M / QC_REWORK_M / QC_SCRAP_D）。';
END
ELSE
    PRINT N'[037] QC_EXCEPTION_M QUERY_RELATION 已存在（跳过）。';

-- ============================================================================
-- 5. QC_ANALYSIS_D：补 QC_LOSS_D / QC_SCRAP_D
--    现有 JOIN：QC_ANALYSIS_M, CLIENT, PRODUCT
--    建议 ON 键：ANALYSIS_NO
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = N'QC_ANALYSIS_D'
    AND QUERY_RELATION LIKE N'%QC_LOSS_D%')
BEGIN
    UPDATE dbo.TABLES
    SET QUERY_RELATION = LTRIM(RTRIM(ISNULL(QUERY_RELATION, N''))) + N'
LEFT JOIN QC_LOSS_D WITH (NOLOCK) ON QC_ANALYSIS_D.ANALYSIS_NO = QC_LOSS_D.ANALYSIS_NO
LEFT JOIN QC_SCRAP_D WITH (NOLOCK) ON QC_ANALYSIS_D.ANALYSIS_NO = QC_SCRAP_D.ANALYSIS_NO'
    WHERE T_ID = N'QC_ANALYSIS_D';
    PRINT N'[037] QC_ANALYSIS_D QUERY_RELATION 已补（QC_LOSS_D / QC_SCRAP_D）。';
END
ELSE
    PRINT N'[037] QC_ANALYSIS_D QUERY_RELATION 已存在（跳过）。';

-- ============================================================================
-- 6. QC_REWORK_M：补 QC_SCRAP_D
--    现有 JOIN：BILLKIND, CLIENT, PRODUCT
--    建议 ON 键：REWORK_NO
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = N'QC_REWORK_M'
    AND QUERY_RELATION LIKE N'%QC_SCRAP_D%')
BEGIN
    UPDATE dbo.TABLES
    SET QUERY_RELATION = LTRIM(RTRIM(ISNULL(QUERY_RELATION, N''))) + N'
LEFT JOIN QC_SCRAP_D WITH (NOLOCK) ON QC_REWORK_M.REWORK_NO = QC_SCRAP_D.REWORK_NO'
    WHERE T_ID = N'QC_REWORK_M';
    PRINT N'[037] QC_REWORK_M QUERY_RELATION 已补（QC_SCRAP_D）。';
END
ELSE
    PRINT N'[037] QC_REWORK_M QUERY_RELATION 已存在（跳过）。';

-- ============================================================================
-- 7. SYSDL：补 SYSDG_USER
--    现有 JOIN：SYSDD, MODULES, V_SYSDL_SYSDN, SYSDN, DEPT
--    建议 ON 键：USER_ID
-- ============================================================================
IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = N'SYSDL'
    AND QUERY_RELATION LIKE N'%SYSDG_USER%')
BEGIN
    UPDATE dbo.TABLES
    SET QUERY_RELATION = LTRIM(RTRIM(ISNULL(QUERY_RELATION, N''))) + N'
LEFT JOIN SYSDG_USER WITH (NOLOCK) ON SYSDL.USER_ID = SYSDG_USER.USER_ID'
    WHERE T_ID = N'SYSDL';
    PRINT N'[037] SYSDL QUERY_RELATION 已补（SYSDG_USER）。';
END
ELSE
    PRINT N'[037] SYSDL QUERY_RELATION 已存在（跳过）。';

-- ============================================================================
-- 8. SYSQR_DEFAULT.F_ID（源表 FIELDS）：治理文档 §4 判定"不适合补 JOIN，建议转 C"
--    不补 QUERY_RELATION，保持 MANUAL 状态，由顾问人工重建。
-- ============================================================================
PRINT N'[037] SYSQR_DEFAULT.F_ID（FIELDS）转 C 处理，不入 B。';

-- ============================================================================
-- 9. COP_CHAFFER_D（源表 COP_CHAFFER_D，引 COP_QUOTE_D）：
--    治理文档 §4 建议补。但 ACTIVE_TAG=false（来源未启用），且 triage 建议
--    EXTEND_QUERY_RELATION。暂不补（来源未启用，重跑工具后仍 fail-closed）；
--    顾问启用来源后按需补。
-- ============================================================================
PRINT N'[037] COP_CHAFFER_D（ACTIVE_TAG=false，跳过）。';

-- ============================================================================
-- 标脏：补了 QUERY_RELATION 的表对应的受影响模块标记脏，触发发布校验。
-- ============================================================================
INSERT INTO dbo.WORKBENCH_MODULE_DIRTY (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
SELECT DISTINCT m.M_IDX, 1, N'EOS-MIG', GETDATE()
FROM dbo.MODULES m
WHERE (m.MASTER_TABLE IN (N'MOC_PRODUCE_M', N'MOU_SCRAP_D', N'QC_COMPLAIN_M', N'QC_EXCEPTION_M', N'QC_ANALYSIS_D', N'QC_REWORK_M', N'SYSDL')
       OR ISNULL(m.DETAIL_TABLE, N'') IN (N'MOC_PRODUCE_M', N'MOU_SCRAP_D', N'QC_COMPLAIN_M', N'QC_EXCEPTION_M', N'QC_ANALYSIS_D', N'QC_REWORK_M', N'SYSDL'))
  AND NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = m.M_IDX);

PRINT N'[037] ADR-008 B 类 QUERY_RELATION 补丁完成。';
GO