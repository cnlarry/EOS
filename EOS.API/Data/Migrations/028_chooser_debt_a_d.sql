-- ============================================================================
-- 028: Field data source governance — A-class exemption log + D-class dirty-data clearing
-- ----------------------------------------------------------------------------
-- Background: triage of remaining MANUAL chooser filter rows (see triage.md).
-- Decision: handle A (exemption) and D (dirty data) first; B (backfill via QUERY_RELATION)
-- and C (manual rebuild) follow per the governance plan.
--
-- A class (10 rows): filter conditions contain NOT IN/EXISTS subqueries referencing other
-- document tables not in the source table's QUERY_RELATION join whitelist — the legacy
-- runtime also failed to bind them (empty chooser options). Action: keep FILTER_STRUCT=NULL
-- (fail-closed, no scope expansion), set CHOOSER_FILTER_MIGRATION_LOG.STATUS to EXEMPTED
-- to unblock field-settings saving for these sources.
-- D class (3 rows): dirty/identity/truncated-table-name conditions. Action: clear
-- FIELD_DATASOURCE.FILTER_STRUCT to an explicit empty filter (chooser usable again).
-- Affected modules are marked dirty (WORKBENCH_MODULE_DIRTY).
-- ============================================================================
SET NOCOUNT ON;

-- ---- A 类：豁免登记（仅命中 STATUS=MANUAL 才落，幂等） ----
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG
SET STATUS = N'EXEMPTED',
    ERROR  = N'[028 治理] A类豁免：子查询引用表不在来源表QUERY_RELATION，旧系统同败，保持fail-closed空选项'
WHERE STATUS = N'MANUAL'
  AND ((T_ID = N'SFC_PLAN_D'     AND F_ID = N'PRO_NO'      AND SERIAL_NO = 2)
    OR (T_ID = N'CUS_PURCHASE_D' AND F_ID = N'IMPORT_NO'   AND SERIAL_NO = 1)
    OR (T_ID = N'HR_WORKTIME_D'  AND F_ID = N'EMP_NO'      AND SERIAL_NO = 1)
    OR (T_ID = N'SYSDD'          AND F_ID = N'M_DESC'      AND SERIAL_NO = 1)
    OR (T_ID = N'SYSDH'          AND F_ID = N'M_DESC'      AND SERIAL_NO = 1)
    OR (T_ID = N'MOU_GET2_D'     AND F_ID = N'APPLY_NO'    AND SERIAL_NO IN (1, 2))
    OR (T_ID = N'MOU_BATCHIN_D'  AND F_ID = N'BATCH_NO'    AND SERIAL_NO IN (2, 3))
    OR (T_ID = N'MOU_ASSESS_M'   AND F_ID = N'PRO_NO'      AND SERIAL_NO = 1));

-- ---- D 类：清空脏数据条件（显式空过滤 = 选择器恢复可用） ----
UPDATE dbo.FIELD_DATASOURCE
SET FILTER_STRUCT = N'{"logic":"AND","items":[]}',
    LAST_UPDATE_BY = N'SYSTEM',
    LAST_UPDATE_DATE = GETDATE()
WHERE ACTIVE_TAG = 1
  AND ((T_ID = N'SAM_APPLY_M'   AND F_ID = N'CURR_ID'   AND SERIAL_NO = 1)  -- CURR_NAME=CURR_NAME 恒等条件 + 列缺失
    OR (T_ID = N'HR_ENACTMENT_D' AND F_ID = N'EMP_ID'    AND SERIAL_NO = 1) -- TIER3 乱码/截断
    OR (T_ID = N'COP_ACCOUNT_D'  AND F_ID = N'S_R_TYPE'  AND SERIAL_NO = 2));-- COP_RETURN_ 表名截断

UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG
SET STATUS = N'CLEANED',
    ERROR  = N'[028 治理] D类脏数据清空为显式空过滤（原条件：恒等/乱码/表名截断），业务语义若需恢复由顾问重建'
WHERE STATUS = N'MANUAL'
  AND ((T_ID = N'SAM_APPLY_M'   AND F_ID = N'CURR_ID'   AND SERIAL_NO = 1)
    OR (T_ID = N'HR_ENACTMENT_D' AND F_ID = N'EMP_ID'    AND SERIAL_NO = 1)
    OR (T_ID = N'COP_ACCOUNT_D'  AND F_ID = N'S_R_TYPE'  AND SERIAL_NO = 2));

-- ---- 标脏 D 类字段所属模块（触发发布校验重跑） ----
INSERT INTO dbo.WORKBENCH_MODULE_DIRTY (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
SELECT DISTINCT m.M_IDX, 1, N'EOS-MIG', GETDATE()
FROM dbo.MODULES m
WHERE (m.MASTER_TABLE IN (N'SAM_APPLY_M', N'HR_ENACTMENT_D', N'COP_ACCOUNT_D')
       OR ISNULL(m.DETAIL_TABLE, N'') IN (N'SAM_APPLY_M', N'HR_ENACTMENT_D', N'COP_ACCOUNT_D'))
  AND NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = m.M_IDX);

PRINT N'[028] 治理 A/D 类处理完成（A 豁免 10 行、D 清空 3 行 + 标脏）。';
GO
