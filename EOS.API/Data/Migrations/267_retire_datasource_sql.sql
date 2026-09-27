-- DATASOURCE_SQL 彻底退役（列 + 引用它的遗留函数）。
--
-- 该列是旧系统「字段下拉候选值 SQL」的载体：旧查询对话框拿它填 dro_<表>^<字段> 下拉，
-- 旧字段维护页自己把它整块隐藏（不让维护）。职责在新系统已由两条通道覆盖——
-- 字段固定选项走 FIELDS.FORM_OPTIONS，选入引用数据走 FIELD_DATASOURCE + 统一选择器；
-- 运行时对 DATASOURCE_SQL 零消费（定义校验器曾把它当发布阻塞项，同批删除）。
--
-- ① 依赖对象：dbo.f_get_fields（表值函数）的实现里引用本列 3 处。非绑定依赖不阻止 DROP COLUMN，
--    但会让该函数在运行时报「列名无效」；它与下游的 f_get_fields_desc / f_get_fields_info
--    在本库内无任何调用者、在新代码里也零引用（sql_modules 与 EOS.API 双侧实测 0 命中），
--    故一并删除，不留失效对象。
-- ② 数据：全库仅 TABLES.T_KIND 一行非空（UNION 常量清单，同一枚举已由 CONVERT_FUNCTION
--    f_get_table_kind_desc 表达）；删列即删数据，故先把原值写入该字段备注留痕。
--
-- 幂等：列/函数已不存在时不再重复操作；非空行数超出预期（说明期间有人配过）时中止而不是继续删。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置取证：非空行数只允许 0（重复执行）或 1（首次执行）
DECLARE @NonEmpty INT = 0;
IF COL_LENGTH(N'dbo.FIELDS', N'DATASOURCE_SQL') IS NOT NULL
    SELECT @NonEmpty = COUNT(*) FROM dbo.FIELDS WITH (NOLOCK)
    WHERE ISNULL(LTRIM(RTRIM(DATASOURCE_SQL)), N'') <> N'';

IF @NonEmpty > 1
    THROW 55000, N'DATASOURCE_SQL 非空行数超出预期（只应有 TABLES.T_KIND 一行），中止以免漏删。', 1;

-- ② 留痕：把将被删掉的那行内容写入字段备注（必须在删列之前读）
DECLARE @TraceWritten BIT = 0;
IF @NonEmpty = 1
BEGIN
    DECLARE @Record NVARCHAR(300) = NULL;
    DECLARE @Value NVARCHAR(4000) = NULL;
    SELECT @Record = LTRIM(RTRIM(T_ID)) + N'.' + LTRIM(RTRIM(F_ID)),
           @Value = LTRIM(RTRIM(DATASOURCE_SQL))
    FROM dbo.FIELDS WITH (NOLOCK)
    WHERE ISNULL(LTRIM(RTRIM(DATASOURCE_SQL)), N'') <> N'';

    IF @Record <> N'TABLES.T_KIND'
        THROW 55001, N'非空行不是 TABLES.T_KIND，与本迁移的预期不符，中止。', 1;

    UPDATE dbo.FIELDS
    SET F_REMARK = LEFT(ISNULL(NULLIF(LTRIM(RTRIM(F_REMARK)), N''), N'') + N' ' +
                        N'原 DATASOURCE_SQL 已删除：' + @Value, 1000),
        LAST_UPDATE_BY = N'migration-265',
        LAST_UPDATE_DATE = SYSDATETIME()
    WHERE T_ID = N'TABLES' AND F_ID = N'T_KIND';

    SET @TraceWritten = 1;
END

-- ③ 删除引用本列的遗留函数（下游先删）
IF OBJECT_ID(N'dbo.f_get_fields_desc') IS NOT NULL DROP FUNCTION dbo.f_get_fields_desc;
IF OBJECT_ID(N'dbo.f_get_fields_info') IS NOT NULL DROP FUNCTION dbo.f_get_fields_info;
IF OBJECT_ID(N'dbo.f_get_fields') IS NOT NULL DROP FUNCTION dbo.f_get_fields;

-- ④ 删列
IF COL_LENGTH(N'dbo.FIELDS', N'DATASOURCE_SQL') IS NOT NULL
    ALTER TABLE dbo.FIELDS DROP COLUMN DATASOURCE_SQL;

-- ⑤ 后置自证：列与函数都不再存在，留痕到位
IF COL_LENGTH(N'dbo.FIELDS', N'DATASOURCE_SQL') IS NOT NULL
    THROW 55002, N'DATASOURCE_SQL 列仍然存在，退役未生效。', 1;

IF OBJECT_ID(N'dbo.f_get_fields') IS NOT NULL
    OR OBJECT_ID(N'dbo.f_get_fields_desc') IS NOT NULL
    OR OBJECT_ID(N'dbo.f_get_fields_info') IS NOT NULL
    THROW 55003, N'仍有引用本列的遗留函数未删除。', 1;

IF @TraceWritten = 1
   AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS WITH (NOLOCK)
                   WHERE T_ID = N'TABLES' AND F_ID = N'T_KIND' AND ISNULL(F_REMARK, N'') LIKE N'%原 DATASOURCE_SQL 已删除%')
    THROW 55004, N'被删除的表达式未写入字段备注留痕。', 1;

COMMIT TRANSACTION;
