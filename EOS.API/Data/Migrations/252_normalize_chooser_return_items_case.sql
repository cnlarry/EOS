-- ============================================================================
-- 选择器回填映射（FIELD_DATASOURCE.RETURN_ITEMS）键名大小写归一
-- ----------------------------------------------------------------------------
-- 写入侧曾按 C# 属性名序列化，落库成 PascalCase 的 `Target`/`Column`；而契约与绝大多数
-- 存量行用的是 camelCase 的 `target`/`column`（与 FILTER_STRUCT 同一口径）。前端按小写键
-- 读取，读到 PascalCase 会静默得到空映射——选择器照常弹出、选完什么都不回填、界面不报错。
-- 写入侧已固定为 camelCase；本迁移把存量里的非 camelCase 行一次性抹平。
--
-- 键名替换带引号，只命中 JSON 键位置，不会波及值里的同名文本。
-- 幂等：判据只在含 PascalCase 键的行上成立，跑完即无；重复执行不报错。
-- 回滚：不改结构，回滚 = 重跑本迁移前的物理备份（数据订正类，无库内可逆点）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.FIELD_DATASOURCE', N'U') IS NULL
    THROW 54000, N'FIELD_DATASOURCE 不存在：本迁移的前提不成立。', 1;

BEGIN TRANSACTION;

UPDATE dbo.FIELD_DATASOURCE
SET RETURN_ITEMS = REPLACE(REPLACE(RETURN_ITEMS, N'"Target"', N'"target"'), N'"Column"', N'"column"'),
    LAST_UPDATE_BY = N'CHOOSER-RETURN-CASE',
    LAST_UPDATE_DATE = SYSUTCDATETIME()
WHERE RETURN_ITEMS COLLATE Latin1_General_BIN LIKE N'%"Target"%'
   OR RETURN_ITEMS COLLATE Latin1_General_BIN LIKE N'%"Column"%';

-- 后置自证：不应再有 PascalCase 键的回填映射行
IF EXISTS (
        SELECT 1
        FROM dbo.FIELD_DATASOURCE
        WHERE RETURN_ITEMS COLLATE Latin1_General_BIN LIKE N'%"Target"%'
           OR RETURN_ITEMS COLLATE Latin1_General_BIN LIKE N'%"Column"%')
    THROW 54001, N'仍有 PascalCase 键的回填映射行：大小写归一未跑完。', 1;

COMMIT TRANSACTION;

SELECT N'非小写键回填映射残留' AS CHECK_NAME, COUNT(*) AS CNT
FROM dbo.FIELD_DATASOURCE
WHERE RETURN_ITEMS COLLATE Latin1_General_BIN LIKE N'%"Target"%'
   OR RETURN_ITEMS COLLATE Latin1_General_BIN LIKE N'%"Column"%';
