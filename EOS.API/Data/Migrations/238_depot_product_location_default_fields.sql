-- ============================================================================
-- EOS.ERP migration 239: 物料主货位的字段要标成"默认字段"
-- ----------------------------------------------------------------------------
-- 订正迁移 238：那 5 行 `FIELDS` 只设了 `IS_VISIBLE = 1`，漏了 `IS_DEFAULT_FIELDS`，
-- 结果是**定义里一个字段都没有**（发布出来的 `MasterFields` 为空），表单自然开不出来。
--
-- 原因在字段读取口径（`WorkbenchDefinitionBuilder.ReadFields`）：
--
--     AND ((有个人字段配置 AND 该字段在配置里) OR (无个人字段配置 AND IS_DEFAULT_FIELDS = 1))
--
-- 也就是说：**当某个用户对这个表还没有个人字段配置时，只有标了"默认字段"的列才会出现**。
-- 老模块（如 110309 库位主档）之所以有 18 个字段，是因为它们的字段集来自**用户的个人配置行**
-- （`SYSQL_FIELDS`），而不是靠默认位；全新模块没有任何个人配置行，于是全被这条口径挡掉。
--
-- 这正是"新模块为什么要在建元数据时就把默认字段标出来"的原因：默认位是**新表唯一的入口**，
-- 不是可选装饰。本迁移只订正这 5 行，不动读取口径（口径本身是对的：默认字段 = 未个性化时看到的列）。
--
-- 幂等：按表名整体置位，可重复执行。
-- ============================================================================

UPDATE dbo.FIELDS
   SET IS_DEFAULT_FIELDS = 1,
       LAST_UPDATE_BY = N'ADR020',
       LAST_UPDATE_DATE = GETDATE()
 WHERE RTRIM(T_ID) = N'DEPOT_PRODUCT_LOCATION'
   AND ISNULL(IS_DEFAULT_FIELDS, 0) = 0;

DECLARE @fields INT = (SELECT COUNT(*) FROM dbo.FIELDS WHERE RTRIM(T_ID) = N'DEPOT_PRODUCT_LOCATION');
DECLARE @defaults INT = (SELECT COUNT(*) FROM dbo.FIELDS
                          WHERE RTRIM(T_ID) = N'DEPOT_PRODUCT_LOCATION'
                            AND ISNULL(IS_DEFAULT_FIELDS, 0) = 1
                            AND ISNULL(IS_VISIBLE, 1) = 1);
IF @fields <> @defaults
    THROW 52112, N'物料主货位仍有字段不是"默认且可见"，迁移中止。', 1;

PRINT N'== 就位：物料主货位 5 个字段均为默认且可见 ==';
