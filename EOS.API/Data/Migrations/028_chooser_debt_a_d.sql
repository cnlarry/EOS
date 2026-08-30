-- ============================================================================
-- 028: ADR-008 字段数据源治理（A 类豁免登记 + D 类脏数据清空）
-- 日期：2026-08-30
-- 决策：docs/plans/业务待定项决策清单.md「ADR-008 字段数据源治理（2026-08-30）」
--       用户拍板：A（与旧系统同败，豁免）→ D（脏数据，清空）先清干净；
--       B（补 QUERY_RELATION 回填）/ C（人工重建）后续按清单处置。
-- 分诊依据：logs/fields-chooser-rerun/triage.md + still-manual.csv（不入库）。
--
-- A 类（10 行）：过滤条件含 NOT IN/EXISTS 子查询引用其它单据表，该表不在
--   数据源来源表 QUERY_RELATION JOIN 白名单内——旧系统运行时同样绑定失败
--   （选择器空选项）。处置：保持 FILTER_STRUCT=NULL（fail-closed，不放大
--   数据范围），仅把 CHOOSER_FILTER_MIGRATION_LOG.STATUS 置 EXEMPTED，
--   解除「迁移清单内来源禁止静默清空」对字段设置保存的拦截。
-- D 类（3 行）：脏数据/恒等/表名截断条件。处置：FIELD_DATASOURCE.FILTER_STRUCT
--   清为显式空过滤（选择器恢复可用）；原业务语义若被丢弃，由顾问按需重建。
-- 受影响模块标脏（WORKBENCH_MODULE_DIRTY），触发发布校验重跑。
-- ============================================================================
SET NOCOUNT ON;

-- ---- A 类：豁免登记（仅命中 STATUS=MANUAL 才落，幂等） ----
UPDATE dbo.CHOOSER_FILTER_MIGRATION_LOG
SET STATUS = N'EXEMPTED',
    ERROR  = N'[028 ADR-008治理] A类豁免：子查询引用表不在来源表QUERY_RELATION，旧系统同败，保持fail-closed空选项'
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
    ERROR  = N'[028 ADR-008治理] D类脏数据清空为显式空过滤（原条件：恒等/乱码/表名截断），业务语义若需恢复由顾问重建'
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

PRINT N'[028] ADR-008 治理 A/D 类处理完成（A 豁免 10 行、D 清空 3 行 + 标脏）。';
GO
