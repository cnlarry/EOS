-- ============================================================================
-- EOS.ERP migration 094: 修正 130105/130106 的入库别引用断言
-- ----------------------------------------------------------------------------
-- 091 播种时把"入库别不存在"写成了 refKey 同名简写：
--   {"refTable":"DEPOT","refKey":{"scope":"DETAIL","field":"IN_DEPOT_ID"}}
-- refKey 展开为 R.<field> = D.<field>，而 DEPOT 主档的主键是 DEPOT_ID，并不存在
-- IN_DEPOT_ID 列 —— 于是这两个模块保存时抛 Msg 207「列名 'IN_DEPOT_ID' 无效」而整单 500，
-- 与保存链无关。091 文件已改为显式 join；本迁移负责修复已经跑过旧版 091 的库。
--
-- 幂等：按 模块+SAVE+顺序 覆盖参数与文案。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 修正 130105/130106 入库别引用断言 ==';

DECLARE @NL NVARCHAR(2) = NCHAR(13) + NCHAR(10);
DECLARE @NLJ NVARCHAR(4) = N'\r\n';

DECLARE @DEPOT NVARCHAR(MAX) =
    N'{"refTable":"DEPOT","refKey":{"scope":"DETAIL","field":"DEPOT_ID"},'
    + N'"lineField":"SERIAL_NO","message":"以下序号项库别编号不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @DEPOT_IN NVARCHAR(MAX) =
    N'{"refTable":"DEPOT","join":[{"target":"DEPOT_ID","source":{"scope":"DETAIL","field":"IN_DEPOT_ID"}}],'
    + N'"lineField":"SERIAL_NO","message":"以下序号项入库别编号不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @PRODUCT NVARCHAR(MAX) =
    N'{"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"},'
    + N'"lineField":"SERIAL_NO","message":"以下序号项产品编号不存在 ' + @NLJ + N'{ROWS}"}';
DECLARE @PARAM NVARCHAR(MAX) = N'{"checks":[' + @DEPOT_IN + N',' + @DEPOT + N',' + @PRODUCT + N']}';

UPDATE dbo.MODULE_VALIDATION_RULE
SET PARAM_STRUCT = @PARAM,
    MESSAGE = N'以下序号项入库别编号不存在 ' + @NL + N'{ROWS}',
    LAST_UPDATE_BY = N'migration-094',
    LAST_UPDATE_DATE = SYSDATETIME()
WHERE MODULE_ID IN (130105, 130106)
  AND STAGE = N'SAVE'
  AND VALIDATION_KEY = N'reference-exists';

PRINT N'-- 修正后 --';
SELECT CONCAT(N'  module=', MODULE_ID, N' checks=', (SELECT COUNT(*) FROM OPENJSON(PARAM_STRUCT, '$.checks')),
              N' 首条 refTable=', JSON_VALUE(PARAM_STRUCT, '$.checks[0].refTable'),
              N' join=', ISNULL(JSON_VALUE(PARAM_STRUCT, '$.checks[0].join[0].target'), N'<无>')) AS SUMMARY
FROM dbo.MODULE_VALIDATION_RULE
WHERE MODULE_ID IN (130105, 130106) AND VALIDATION_KEY = N'reference-exists';

PRINT N'== 修正完成 ==';
