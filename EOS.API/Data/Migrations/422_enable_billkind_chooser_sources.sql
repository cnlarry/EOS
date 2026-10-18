-- ============================================================================
-- 单别字段「只能选入」：启用三处已配置但未启用的 BILLKIND 选择来源
--
-- FIELDS.ONLY_CHOOSE=1 表示该字段不接受键入，值只能由选择器回填；运行期判据是
-- 「字段有一条启用中的选择来源」（FIELD_DATASOURCE.ACTIVE_TAG=1），否则不渲染选择
-- 按钮，输入框只能保持可手输——约束因此不生效。
--
-- 三个字段都已有 BILLKIND 来源行，只是 ACTIVE_TAG=0；且 BILLKIND 里都有本模块的档位：
--   PUR_CHAFFER_M.CHAFFER_TYPE → 1603 CSXJ 厂商询价单
--   PUR_QUOTE_M.QUOTE_TYPE     → 1604 CSBJ 厂商报价单
--   CAR_MISSION_M.MISSION_TYPE → 1903 CHC  出车通知单
-- 180402（计件工资单别）不在此列：BILLKIND 没有 180402 的档位，启用只会变成一个
-- 空选择器，把可填的字段变成填不上的字段。
--
-- 选择器来源随定义快照冻结，故同批标脏这三个模块（发布后生效）。幂等：已启用的行不重复更新。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @enabled INT;

UPDATE s
SET s.ACTIVE_TAG = 1
FROM dbo.FIELD_DATASOURCE s
WHERE COALESCE(s.ACTIVE_TAG, 0) = 0
  AND LTRIM(RTRIM(ISNULL(s.SOURCE_T_ID, N''))) = N'BILLKIND'
  AND (
        (LTRIM(RTRIM(s.T_ID)) = N'PUR_CHAFFER_M' AND LTRIM(RTRIM(s.F_ID)) = N'CHAFFER_TYPE')
     OR (LTRIM(RTRIM(s.T_ID)) = N'PUR_QUOTE_M'   AND LTRIM(RTRIM(s.F_ID)) = N'QUOTE_TYPE')
     OR (LTRIM(RTRIM(s.T_ID)) = N'CAR_MISSION_M' AND LTRIM(RTRIM(s.F_ID)) = N'MISSION_TYPE')
  );
SET @enabled = @@ROWCOUNT;

MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT M_IDX FROM (VALUES (1603), (1604), (1903)) v(M_IDX)) AS s
   ON t.M_IDX = s.M_IDX
WHEN MATCHED THEN
    UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'EOS-MIG', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (s.M_IDX, 1, N'EOS-MIG', SYSDATETIME());

PRINT N'[422] 启用 BILLKIND 选择来源 ' + CAST(@enabled AS NVARCHAR(10)) + N' 行；已标脏 1603/1604/1903。';

COMMIT;
