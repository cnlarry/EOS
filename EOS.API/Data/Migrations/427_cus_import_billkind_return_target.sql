-- ============================================================================
-- 进口报关单：补单别选择器的自回填目标
--
-- CUS_IMPORT_M.IMPORT_TYPE 是 nchar(20) 主键首列（明细 CUS_IMPORT_D 主键也含它），
-- 元数据标 ONLY_CHOOSE=1（不接受键入），值只能由选择器回填；模块的明细门禁
-- DETAIL_NO_FIELDS 又要求它先有值才能加明细行。
--
-- 唯一启用的数据源（ID=1499）只把选中的行回填到 BILL_NAME，没有回填本字段本身，
-- 于是「打开选择器 → 选中单别 → 确认」之后 IMPORT_TYPE 仍是空：
-- 300302 / 300305 的明细行永远加不出来。其它 BILLKIND 来源的写法都是
-- {"target":"<本字段>","column":"BILL_CODE"}，本条按同一口径补齐。
--
-- 选择器来源随定义快照冻结，故同批标脏这两个模块（重新发布后生效）。
-- 幂等：已经含 IMPORT_TYPE 目标的行不再改写。末尾断言回填目标确实落在库内。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @before NVARCHAR(MAX) = (SELECT RETURN_ITEMS FROM dbo.FIELD_DATASOURCE WHERE ID = 1499);
IF @before IS NULL
BEGIN
    RAISERROR(N'[427] 数据源 1499 不存在，拒绝继续', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

DECLARE @has_target BIT = CASE WHEN EXISTS (
        SELECT 1 FROM OPENJSON(@before) WITH (t NVARCHAR(200) '$.target', z NVARCHAR(200) '$.Target')
        WHERE UPPER(ISNULL(t, z)) = N'IMPORT_TYPE') THEN 1 ELSE 0 END;

IF @has_target = 0
BEGIN
    UPDATE dbo.FIELD_DATASOURCE
    SET RETURN_ITEMS = JSON_MODIFY(@before, 'append $',
            JSON_QUERY(N'{"target":"IMPORT_TYPE","column":"BILL_CODE"}')),
        LAST_UPDATE_BY = N'EOS-MIG',
        LAST_UPDATE_DATE = SYSDATETIME()
    WHERE ID = 1499;
END

-- 断言：本字段必须真的进了回填目标，否则这条迁移什么也没修好
IF NOT EXISTS (
        SELECT 1 FROM dbo.FIELD_DATASOURCE
        WHERE ID = 1499 AND EXISTS (
            SELECT 1 FROM OPENJSON(RETURN_ITEMS) WITH (t NVARCHAR(200) '$.target', z NVARCHAR(200) '$.Target')
            WHERE UPPER(ISNULL(t, z)) = N'IMPORT_TYPE'))
BEGIN
    RAISERROR(N'[427] 回填目标补齐后仍读不到 IMPORT_TYPE，回滚', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT M_IDX FROM (VALUES (300302), (300305)) v(M_IDX)) AS s
   ON t.M_IDX = s.M_IDX
WHEN MATCHED THEN
    UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'EOS-MIG', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (s.M_IDX, 1, N'EOS-MIG', SYSDATETIME());

PRINT N'[427] 数据源 1499 回填目标已补齐（' + CASE WHEN @has_target = 1 THEN N'原本已有，未改动' ELSE N'本次新增' END + N'）；已标脏 300302/300305。';

COMMIT;
