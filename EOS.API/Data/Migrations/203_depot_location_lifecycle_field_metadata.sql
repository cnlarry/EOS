-- ============================================================================
-- EOS.ERP migration 204: 库位主档补生命周期列的字段元数据
-- ----------------------------------------------------------------------------
-- DEPOT_LOCATION 的建表（175）与字段元数据登记（186）都发生在生命周期列补入（194）
-- 之前，于是表上已有 CONFIRM_TAG / CONFIRM_PERSON / CONFIRM_DATE，FIELDS 里却一直没有
-- 对应行。表单与列表按 FIELDS 渲染，缺行即等于这些列不存在：库位主档的列表选不出
-- 批核状态，表单上也没有批核信息。这是全库唯一一处"有批核列却无字段元数据"的表。
--
-- 展示口径不另立一套：三行逐列复制同域兄弟表 DEPOT（模块 110306 的主表）的既有取值，
-- 使库位主档与仓库资料在批核列的文案、可见性、宽度上完全一致；F_TYPE 例外，取本表
-- 实际列型（同为批核列，两表的 CONFIRM_PERSON 一处 nchar 一处 nvarchar，元数据必须
-- 描述真实列型）。
--
-- 幂等：按 (T_ID, F_ID) 合并；已存在时只刷新展示口径，不覆盖 FORM_ORDER——那是版式
-- 口径，可能已被手工调整。
-- 回滚：DELETE 本迁移写入的 3 行 FIELDS 行（T_ID = 'DEPOT_LOCATION' AND F_ID IN ...）。
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

IF OBJECT_ID(N'dbo.DEPOT_LOCATION', N'U') IS NULL
    THROW 52701, N'表 dbo.DEPOT_LOCATION 不存在，迁移中止。', 1;

/* 三列由生命周期迁移补入；缺列说明库内状态与预期不符，先查明再迁移 */
IF COL_LENGTH(N'dbo.DEPOT_LOCATION', N'CONFIRM_TAG') IS NULL
   OR COL_LENGTH(N'dbo.DEPOT_LOCATION', N'CONFIRM_PERSON') IS NULL
   OR COL_LENGTH(N'dbo.DEPOT_LOCATION', N'CONFIRM_DATE') IS NULL
    THROW 52702, N'DEPOT_LOCATION 缺少批核列（CONFIRM_TAG / CONFIRM_PERSON / CONFIRM_DATE），迁移中止。', 1;

/* 展示口径的来源表：同域兄弟模块的主表 */
IF OBJECT_ID(N'dbo.DEPOT', N'U') IS NULL
    THROW 52703, N'参考表 dbo.DEPOT 不存在，迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.FIELDS
     WHERE T_ID = N'DEPOT' AND F_ID IN (N'CONFIRM_TAG', N'CONFIRM_PERSON', N'CONFIRM_DATE')) <> 3
    THROW 52704, N'参考表 DEPOT 的批核列字段元数据不完整，无法复制口径，迁移中止。', 1;

/* ---------- 三行字段元数据 ---------- */
DECLARE @baseOrder INT = (SELECT ISNULL(MAX(FORM_ORDER), 0) FROM dbo.FIELDS WHERE T_ID = N'DEPOT_LOCATION');

MERGE dbo.FIELDS AS target
USING (
    SELECT src.F_ID,
           src.F_DESC,
           TYPE_NAME(col.user_type_id) AS F_TYPE,
           src.IS_PK, src.IS_VISIBLE, src.IS_QUERY, src.IS_READONLY, src.DISPLAY_LENGTH,
           src.IS_DEFAULT_FIELDS, src.CAN_COPY, src.F_REMARK,
           CASE src.F_ID WHEN N'CONFIRM_TAG' THEN 1 WHEN N'CONFIRM_PERSON' THEN 2 ELSE 3 END AS ORD
    FROM dbo.FIELDS src
    JOIN sys.columns col
      ON col.object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION') AND col.name = src.F_ID
    WHERE src.T_ID = N'DEPOT' AND src.F_ID IN (N'CONFIRM_TAG', N'CONFIRM_PERSON', N'CONFIRM_DATE')
) AS source
   ON target.T_ID = N'DEPOT_LOCATION' AND target.F_ID = source.F_ID
WHEN MATCHED THEN UPDATE SET
    F_DESC = source.F_DESC, F_TYPE = source.F_TYPE, IS_PK = source.IS_PK,
    IS_VISIBLE = source.IS_VISIBLE, IS_QUERY = source.IS_QUERY, IS_READONLY = source.IS_READONLY,
    DISPLAY_LENGTH = source.DISPLAY_LENGTH, IS_DEFAULT_FIELDS = source.IS_DEFAULT_FIELDS,
    CAN_COPY = source.CAN_COPY, F_REMARK = source.F_REMARK,
    LAST_UPDATE_BY = N'migration', LAST_UPDATE_DATE = GETDATE()
WHEN NOT MATCHED THEN INSERT
    (T_ID, F_ID, F_DESC, F_TYPE, IS_PK, IS_VISIBLE, IS_QUERY, IS_READONLY, DISPLAY_LENGTH,
     IS_DEFAULT_FIELDS, CAN_COPY, F_REMARK, FORM_ORDER, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (N'DEPOT_LOCATION', source.F_ID, source.F_DESC, source.F_TYPE, source.IS_PK,
            source.IS_VISIBLE, source.IS_QUERY, source.IS_READONLY, source.DISPLAY_LENGTH,
            source.IS_DEFAULT_FIELDS, source.CAN_COPY, source.F_REMARK, @baseOrder + source.ORD,
            N'migration', GETDATE());

PRINT N'== 已补入库位主档批核列的字段元数据 ==';

/* ---------- 标记待发布：运行时读已发布快照 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT 110309 AS MODULE_ID) AS S
   ON T.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'migration', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'migration', SYSDATETIME());

/* ---------- 收口断言 ---------- */
/* ① 三行齐备，且按"可填字段"之外的口径登记：只读、可见、不参与关键字查询 */
IF (SELECT COUNT(*) FROM dbo.FIELDS WHERE T_ID = N'DEPOT_LOCATION'
     AND F_ID IN (N'CONFIRM_TAG', N'CONFIRM_PERSON', N'CONFIRM_DATE')
     AND IS_VISIBLE = 1 AND IS_QUERY = 0 AND IS_READONLY = 1) <> 3
    THROW 52705, N'批核列的字段元数据未按预期登记（应为可见 / 只读 / 不可查询），迁移中止。', 1;

/* ② 表单顺序必须落在既有字段之后，避免新列插进版式中间 */
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'DEPOT_LOCATION'
            AND F_ID IN (N'CONFIRM_TAG', N'CONFIRM_PERSON', N'CONFIRM_DATE')
            AND (FORM_ORDER IS NULL OR FORM_ORDER <= @baseOrder))
    THROW 52706, N'批核列的表单顺序未排在既有字段之后，迁移中止。', 1;

/* ③ 元数据描述的类型必须与物理列一致 */
IF EXISTS (
    SELECT 1 FROM dbo.FIELDS f
    JOIN sys.columns c ON c.object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION') AND c.name = f.F_ID
    WHERE f.T_ID = N'DEPOT_LOCATION' AND LTRIM(RTRIM(f.F_TYPE)) <> TYPE_NAME(c.user_type_id))
    THROW 52707, N'字段元数据类型与物理列不一致，迁移中止。', 1;

/* ④ 该表每一物理列都必须有字段元数据（本次收口的正是这类缺口） */
DECLARE @missing NVARCHAR(400) = (
    SELECT TOP 1 N'dbo.DEPOT_LOCATION.' + c.name
    FROM sys.columns c
    WHERE c.object_id = OBJECT_ID(N'dbo.DEPOT_LOCATION') AND c.is_computed = 0
      AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f
                      WHERE f.T_ID = N'DEPOT_LOCATION' AND LTRIM(RTRIM(f.F_ID)) = c.name));

IF @missing IS NOT NULL
    THROW 52708, N'仍有物理列没有字段元数据，迁移中止。', 1;

/* ⑤ 模块 110309 必须待发布 */
IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = 110309 AND DIRTY_TAG = 1)
    THROW 52709, N'模块 110309 未标记为待发布，迁移中止。', 1;

DECLARE @rows NVARCHAR(10) = CONVERT(NVARCHAR(10),
    (SELECT COUNT(*) FROM dbo.FIELDS WHERE T_ID = N'DEPOT_LOCATION'));
PRINT N'== 收口：DEPOT_LOCATION 字段元数据 ' + @rows + N' 行（物理列全覆盖），模块 110309 待发布 ==';
