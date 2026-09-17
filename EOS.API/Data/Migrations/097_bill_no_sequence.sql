-- ============================================================================
-- EOS.ERP migration 098: 单据自动编号独立发号器（BILL_NO_SEQUENCE）
-- ----------------------------------------------------------------------------
-- 背景：自动单号此前由业务表推导——SELECT MAX(单号列) FROM 主表 WHERE 单号列 LIKE 字头%。
--   该做法有两个硬伤：① 每次取号都要扫业务表，行数增长后代价持续上升；② 行被删除或
--   单号列不唯一时，MAX 回退会重新发出已用过的号。
-- 方案：引入独立序列表 BILL_NO_SEQUENCE，按（种类码 BILL_CODE, 日期段 PERIOD_KEY）
--   维护当前流水号；取号为单条 UPDATE ... OUTPUT 的原子自增，不再读业务表。
--   单号规则的唯一来源仍是 BILLKIND（模块 2304「单据性质设定」的主表）：
--     BILL_CODE    种类码（单别）
--     USED_BILL_NO 编码方式表达式：字头 + {日期令牌} + 尾随 0（0 的个数即流水宽度）
--     IS_AUTO      是否自动编号   IS_DEFAULT  是否默认单别
--   日期段来自表达式里的日期令牌，"按年/月/日重置"由 PERIOD_KEY 天然表达；
--   无日期令牌的规则 PERIOD_KEY 为空串，流水永不重置。起始值固定为 1（规则表无起始值字段）。
--
-- 计数器初始化：按现存单号回填——对每个 (BILL_CODE, 字头) 取业务表现存最大流水，
--   写入 CURRENT_NO。取号时若计数器落后于现存最大号就会撞号，故回填是切号前提。
--   尚未出现的日期段不建行，首次取号时按需创建并从 1 开始，与切换前行为一致。
--
-- 幂等：表按存在性创建；回填用 MERGE 收敛为 max(现有计数器, 现存最大号)，
--   重复执行不回退、不重复建行（脱离 journal 手工重跑同样安全）。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 单据自动编号独立发号器开始 ==';

-- 1) 序列表 ------------------------------------------------------------------
IF OBJECT_ID(N'dbo.BILL_NO_SEQUENCE', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BILL_NO_SEQUENCE (
        BILL_CODE      NVARCHAR(20)  NOT NULL,   -- 种类码（BILLKIND.BILL_CODE）
        PERIOD_KEY     NVARCHAR(16)  NOT NULL,   -- 日期段（日期令牌渲染值；无日期令牌为 ''）
        TITLE          NVARCHAR(64)  NOT NULL,   -- 当期字头（前缀 + 日期段 + 后缀），字头变化即重置流水
        CURRENT_NO     BIGINT        NOT NULL CONSTRAINT DF_BILL_NO_SEQUENCE_CURRENT_NO DEFAULT (0),
        LAST_ISSUED_AT DATETIME2(0)  NULL,
        CONSTRAINT PK_BILL_NO_SEQUENCE PRIMARY KEY CLUSTERED (BILL_CODE, PERIOD_KEY)
    );
    SELECT CAST(N'  创建表 BILL_NO_SEQUENCE' AS NVARCHAR(200)) AS SUMMARY;
END
ELSE
    SELECT CAST(N'  表 BILL_NO_SEQUENCE 已存在，跳过创建' AS NVARCHAR(200)) AS SUMMARY;

-- 2) 规则清单：BILLKIND 自动编号规则 + 表达式拆解 ------------------------------
DECLARE @Rules TABLE (
    SEQ          INT IDENTITY(1,1) PRIMARY KEY,
    BILL_CODE    NVARCHAR(20) NOT NULL,
    B_M_IDX      INT          NOT NULL,
    MASTER_TABLE SYSNAME      NOT NULL,
    WIDTH        INT          NOT NULL,   -- 流水宽度（尾随 0 的个数）
    LIT_PREFIX   NVARCHAR(64) NOT NULL,   -- 日期令牌之前的字面前缀
    LIT_SUFFIX   NVARCHAR(64) NOT NULL    -- 日期令牌之后、流水之前的字面后缀
);

INSERT INTO @Rules (BILL_CODE, B_M_IDX, MASTER_TABLE, WIDTH, LIT_PREFIX, LIT_SUFFIX)
SELECT B.BILL_CODE, B.B_M_IDX, M.MASTER_TABLE, D.WIDTH, D.LIT_PREFIX, D.LIT_SUFFIX
FROM dbo.BILLKIND AS B
JOIN dbo.MODULES AS M ON M.M_IDX = B.B_M_IDX
CROSS APPLY (SELECT EXPR = LTRIM(RTRIM(ISNULL(B.USED_BILL_NO, N'')))) AS E
CROSS APPLY (
    -- 尾随 0 的个数 = 流水宽度；整串都是 0 时宽度为表达式全长
    SELECT WIDTH = CASE
        WHEN PATINDEX(N'%[^0]%', REVERSE(E.EXPR)) > 0
            THEN PATINDEX(N'%[^0]%', REVERSE(E.EXPR)) - 1
        ELSE LEN(E.EXPR) END
) AS Z
CROSS APPLY (SELECT TOKEN_START = CHARINDEX(N'{', E.EXPR), TOKEN_END = CHARINDEX(N'}', E.EXPR)) AS T
CROSS APPLY (
    SELECT LIT_PREFIX = CASE
        WHEN T.TOKEN_START > 0 THEN LEFT(E.EXPR, T.TOKEN_START - 1)
        ELSE LEFT(E.EXPR, LEN(E.EXPR) - Z.WIDTH) END
) AS P
CROSS APPLY (
    SELECT AFTER_TOKEN = CASE
        WHEN T.TOKEN_END > T.TOKEN_START THEN SUBSTRING(E.EXPR, T.TOKEN_END + 1, LEN(E.EXPR))
        ELSE N'' END
) AS A
CROSS APPLY (
    SELECT LIT_SUFFIX = CASE
        WHEN LEN(A.AFTER_TOKEN) > Z.WIDTH THEN LEFT(A.AFTER_TOKEN, LEN(A.AFTER_TOKEN) - Z.WIDTH)
        ELSE N'' END
) AS S
CROSS APPLY (
    SELECT WIDTH = Z.WIDTH, LIT_PREFIX = P.LIT_PREFIX, LIT_SUFFIX = S.LIT_SUFFIX
) AS D
WHERE B.IS_AUTO = 1
  AND Z.WIDTH > 0
  AND M.MASTER_TABLE IS NOT NULL
  AND LEN(M.MASTER_TABLE) > 0
ORDER BY B.B_M_IDX, B.BILL_CODE;

DECLARE @RuleTotal INT = (SELECT COUNT(*) FROM @Rules);
SELECT CONCAT(N'  待回填规则数：', @RuleTotal) AS SUMMARY;

-- 3) 逐规则扫描业务表，按字头归集现存最大流水 ----------------------------------
DECLARE @Scan TABLE (PREFIX NVARCHAR(200) NOT NULL, MAX_SERIAL BIGINT NOT NULL);
DECLARE @Found TABLE (SEQ INT NOT NULL, PREFIX NVARCHAR(200) NOT NULL, MAX_SERIAL BIGINT NOT NULL);
DECLARE @Pk TABLE (ORD INT NOT NULL, COL SYSNAME NOT NULL);

DECLARE @Seq INT = 1;
DECLARE @Seeded INT = 0;
DECLARE @Skipped INT = 0;
DECLARE @R_CODE NVARCHAR(20), @R_TABLE SYSNAME, @R_PRE NVARCHAR(64), @R_SUF NVARCHAR(64);
DECLARE @R_MODULE INT, @R_WIDTH INT;

WHILE @Seq <= @RuleTotal
BEGIN
    SELECT @R_CODE = BILL_CODE, @R_MODULE = B_M_IDX, @R_TABLE = MASTER_TABLE,
           @R_WIDTH = WIDTH, @R_PRE = LIT_PREFIX, @R_SUF = LIT_SUFFIX
    FROM @Rules WHERE SEQ = @Seq;

    -- 主表不存在（表或视图均可）则跳过
    IF NOT EXISTS (SELECT 1 FROM sys.objects AS o
                   JOIN sys.schemas AS s ON s.schema_id = o.schema_id
                   WHERE s.name = N'dbo' AND o.name = @R_TABLE AND o.type IN (N'U', N'V'))
    BEGIN
        SET @Skipped += 1;
        SET @Seq += 1;
        CONTINUE;
    END;

    -- 单号列 / 单别列：主表主键中按键序取"名称含 NO"的列，其余列中的第一个作单别列
    DELETE FROM @Pk;
    INSERT INTO @Pk (ORD, COL)
    SELECT ic.key_ordinal, c.name
    FROM sys.indexes AS i
    JOIN sys.index_columns AS ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
    JOIN sys.columns AS c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
    WHERE i.object_id = OBJECT_ID(N'dbo.' + @R_TABLE) AND i.is_primary_key = 1
    ORDER BY ic.key_ordinal;

    DECLARE @NoCol SYSNAME, @TypeCol SYSNAME;
    SET @NoCol = NULL;
    SET @TypeCol = NULL;
    SELECT TOP 1 @NoCol = COL FROM @Pk WHERE UPPER(COL) LIKE N'%NO%' ORDER BY ORD;
    SELECT TOP 1 @TypeCol = COL FROM @Pk WHERE COL <> @NoCol ORDER BY ORD;

    IF @NoCol IS NULL OR @TypeCol IS NULL
    BEGIN
        SET @Skipped += 1;
        SET @Seq += 1;
        CONTINUE;
    END;

    DELETE FROM @Scan;
    DECLARE @ScanSql NVARCHAR(MAX) = N'
SELECT P.PREFIX, MAX(P.SERIAL) AS MAX_SERIAL
FROM (
    -- 尾号长度固定为流水宽度（字头 = 单号去掉尾部 width 位），与旧系统"按字头长度截取"一致
    SELECT
        PREFIX = LEFT(N.NO_VALUE, LEN(N.NO_VALUE) - @Width),
        SERIAL = TRY_CAST(RIGHT(N.NO_VALUE, @Width) AS BIGINT)
    FROM dbo.' + QUOTENAME(@R_TABLE) + N' AS T
    CROSS APPLY (SELECT LTRIM(RTRIM(CAST(T.' + QUOTENAME(@NoCol) + N' AS NVARCHAR(200)))) AS NO_VALUE) AS N
    WHERE T.' + QUOTENAME(@TypeCol) + N' = @BillCode
      AND LEN(N.NO_VALUE) >= @Width
      AND RIGHT(N.NO_VALUE, @Width) NOT LIKE N''%[^0-9]%''
) AS P
WHERE P.SERIAL IS NOT NULL
GROUP BY P.PREFIX;';

    BEGIN TRY
        INSERT INTO @Scan (PREFIX, MAX_SERIAL)
        EXEC sp_executesql @ScanSql,
            N'@BillCode NVARCHAR(20), @Width INT',
            @BillCode = @R_CODE, @Width = @R_WIDTH;
    END TRY
    BEGIN CATCH
        SELECT CONCAT(N'  WARN 规则扫描失败已跳过 module=', @R_MODULE, N' code=', @R_CODE,
                      N' err=', ERROR_NUMBER()) AS SUMMARY;
        SET @Skipped += 1;
        SET @Seq += 1;
        CONTINUE;
    END CATCH;

    DELETE FROM @Found;
    INSERT INTO @Found (SEQ, PREFIX, MAX_SERIAL)
    SELECT ROW_NUMBER() OVER (ORDER BY PREFIX), PREFIX, MAX_SERIAL FROM @Scan;

    -- 4) 写入计数器：字头 → (种类码, 日期段) ---------------------------------
    DECLARE @FSeq INT = 1;
    DECLARE @FTotal INT = (SELECT COUNT(*) FROM @Found);
    DECLARE @Prefix NVARCHAR(200), @Period NVARCHAR(16), @Serial BIGINT;

    WHILE @FSeq <= @FTotal
    BEGIN
        SELECT @Prefix = PREFIX, @Serial = MAX_SERIAL FROM @Found WHERE SEQ = @FSeq;

        -- 字头必须形如 <字面前缀><日期段><字面后缀>，否则不是本规则产出的号
        IF LEN(@Prefix) >= LEN(@R_PRE) + LEN(@R_SUF)
           AND LEFT(@Prefix, LEN(@R_PRE)) = @R_PRE
           AND RIGHT(@Prefix, LEN(@R_SUF)) = @R_SUF
        BEGIN
            SET @Period = SUBSTRING(@Prefix, LEN(@R_PRE) + 1, LEN(@Prefix) - LEN(@R_PRE) - LEN(@R_SUF));

            MERGE dbo.BILL_NO_SEQUENCE AS T
            USING (SELECT @R_CODE AS BILL_CODE,
                          @Period AS PERIOD_KEY,
                          CAST(LEFT(@Prefix, 64) AS NVARCHAR(64)) AS TITLE,
                          @Serial AS CURRENT_NO) AS S
            ON T.BILL_CODE = S.BILL_CODE AND T.PERIOD_KEY = S.PERIOD_KEY
            WHEN MATCHED AND T.CURRENT_NO < S.CURRENT_NO THEN
                UPDATE SET CURRENT_NO = S.CURRENT_NO, TITLE = S.TITLE
            WHEN NOT MATCHED BY TARGET THEN
                INSERT (BILL_CODE, PERIOD_KEY, TITLE, CURRENT_NO)
                VALUES (S.BILL_CODE, S.PERIOD_KEY, S.TITLE, S.CURRENT_NO);

            SET @Seeded += 1;
        END;

        SET @FSeq += 1;
    END;

    SET @Seq += 1;
END;

SELECT CONCAT(N'  计数器回填：', @Seeded, N' 个 (种类码, 日期段)；跳过规则 ', @Skipped, N' / ', @RuleTotal) AS SUMMARY;
SELECT CONCAT(N'  序列表现状：', (SELECT COUNT(*) FROM dbo.BILL_NO_SEQUENCE), N' 个计数器') AS SUMMARY;

PRINT N'== 单据自动编号独立发号器完成 ==';
