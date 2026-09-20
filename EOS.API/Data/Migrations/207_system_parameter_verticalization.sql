/*------------------------------------------------------------------
  系统参数纵向化：把三张"单行宽表"（SYSSS / HR_SETUP / HRM_SETUP）就地收敛为
  一张纵向参数表 dbo.SYSSS（表名不变），用 OWNER_MODULE 区分归属模块：
  110111 系统参数设置 / 180213 考勤数据设置 / 180662 考勤数据设置。

  一行一个参数：PARAM_KEY（= 原列名）+ PARAM_VALUE（文本存储）+ VALUE_TYPE（解释方式）
  + DEFAULT_VALUE（代码默认）+ 分组与说明（页面按分组选项卡呈现）。

  为什么主键是 (OWNER_MODULE, PARAM_KEY)：HR_SETUP 与 HRM_SETUP 的列名几乎完全同名
  （MACHINE_START…WAGE_ADD 共 44 个），单列 PARAM_KEY 收敛后必然冲突；键名仍严格等于原列名，
  所有读取一律按 OWNER_MODULE 限定。

  只迁业务参数：三张表的技术/生命周期列（建立人、建立日期、修改人、修改日期、批核人、
  批核日期、批核状态、公司、所有者、所有者组）在新表里就是行级元数据，一律不迁。
  不迁的列在本脚本内显式枚举（见 #tech），并由断言逐列核对，不吃"排除法"。

  批次划分：DROP/CREATE 与后续引用必须分批（同批内会按旧表结构绑定列名），
  临时表跨批存活、局部变量不跨批，故数据一律先落到 #param / #value。

  步骤：备份 → 参数定义 → 抽现值 → 重建表 → 逐键断言（双向 EXCEPT，不是取样）。
  备份表是本次改造的回滚依据，收口前不得删除。
------------------------------------------------------------------*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

/* 1. 取样备份（放在 bak schema：是回滚用的快照副本，不属于业务 schema） */
IF SCHEMA_ID(N'bak') IS NULL EXEC(N'CREATE SCHEMA bak');
IF OBJECT_ID(N'bak.SYSSS_BAK_20260920', N'U') IS NULL
    SELECT * INTO bak.SYSSS_BAK_20260920 FROM dbo.SYSSS;
IF OBJECT_ID(N'bak.HR_SETUP_BAK_20260920', N'U') IS NULL
    SELECT * INTO bak.HR_SETUP_BAK_20260920 FROM dbo.HR_SETUP;
IF OBJECT_ID(N'bak.HRM_SETUP_BAK_20260920', N'U') IS NULL
    SELECT * INTO bak.HRM_SETUP_BAK_20260920 FROM dbo.HRM_SETUP;

/* 2. 归属模块 → 源表 / 备份表（备份表名已带 schema） */
CREATE TABLE #owner (OWNER_MODULE int NOT NULL PRIMARY KEY, SRC_TABLE nvarchar(128) NOT NULL, BAK_TABLE nvarchar(128) NOT NULL);
INSERT INTO #owner (OWNER_MODULE, SRC_TABLE, BAK_TABLE) VALUES
    (110111, N'SYSSS',     N'bak.SYSSS_BAK_20260920'),
    (180213, N'HR_SETUP',  N'bak.HR_SETUP_BAK_20260920'),
    (180662, N'HRM_SETUP', N'bak.HRM_SETUP_BAK_20260920');

/* 3. 显式声明不迁的技术/生命周期列 */
CREATE TABLE #tech (OWNER_MODULE int NOT NULL, PARAM_KEY nvarchar(64) NOT NULL, PRIMARY KEY (OWNER_MODULE, PARAM_KEY));
INSERT INTO #tech (OWNER_MODULE, PARAM_KEY) VALUES
    (110111, N'CREATE_DATE'),
    (180213, N'CREATE_PERSON'), (180213, N'CREATE_DATE'), (180213, N'LAST_UPDATE_BY'), (180213, N'LAST_UPDATE_DATE'),
    (180213, N'CONFIRM_PERSON'), (180213, N'CONFIRM_DATE'), (180213, N'CONFIRM_TAG'),
    (180213, N'CI'), (180213, N'OWNER'), (180213, N'OWNER_G'),
    (180662, N'CREATE_PERSON'), (180662, N'CREATE_DATE'), (180662, N'LAST_UPDATE_BY'), (180662, N'LAST_UPDATE_DATE'),
    (180662, N'CONFIRM_PERSON'), (180662, N'CONFIRM_DATE'), (180662, N'CONFIRM_TAG'),
    (180662, N'CI'), (180662, N'OWNER'), (180662, N'OWNER_G');

/* 4. 参数定义与分组（分组是可执行数据：新增分组不必改代码） */
CREATE TABLE #param (
    OWNER_MODULE  int            NOT NULL,
    PARAM_KEY     nvarchar(64)   NOT NULL,
    VALUE_TYPE    nvarchar(16)   NULL,
    DEFAULT_VALUE nvarchar(4000) NULL,
    GROUP_CODE    nvarchar(32)   NOT NULL,
    GROUP_LABEL   nvarchar(50)   NOT NULL,
    SEQ_NO        int            NOT NULL,
    DESC_TEXT     nvarchar(300)  NULL,
    PRIMARY KEY (OWNER_MODULE, PARAM_KEY)
);

/* 110111 系统参数设置 */
INSERT INTO #param (OWNER_MODULE, PARAM_KEY, GROUP_CODE, GROUP_LABEL, SEQ_NO) VALUES
    (110111, N'CLIENT_DAYS',                  N'PARTNER',  N'往来与账期',   10),
    (110111, N'SUPPLIER_DAYS',                N'PARTNER',  N'往来与账期',   20),
    (110111, N'PRODUCT_DAYS',                 N'PARTNER',  N'往来与账期',   30),
    (110111, N'MRP_DAYS',                     N'MRP',      N'MRP 与可用量', 10),
    (110111, N'MRP_WEEKS',                    N'MRP',      N'MRP 与可用量', 20),
    (110111, N'MRP_MONTHS',                   N'MRP',      N'MRP 与可用量', 30),
    (110111, N'PRO_MRP',                      N'MRP',      N'MRP 与可用量', 40),
    (110111, N'PRO_EDITION_TAG',              N'PRODUCT',  N'料件与版次',   10),
    (110111, N'FITOUT_TAG',                   N'DOC_LINK', N'单据联动开关', 10),
    (110111, N'FITOUT_ORDER_TAG',             N'DOC_LINK', N'单据联动开关', 20),
    (110111, N'FITOUT_PRODUCE_TAG',           N'DOC_LINK', N'单据联动开关', 30),
    (110111, N'FITOUT_PRODUCE_TRANSFER_TAG',  N'DOC_LINK', N'单据联动开关', 40),
    (110111, N'SEND_TAG',                     N'DOC_LINK', N'单据联动开关', 50),
    (110111, N'SEND_ORDER_TAG',               N'DOC_LINK', N'单据联动开关', 60),
    (110111, N'SEND_ORDER_FITOUT_TAG',        N'DOC_LINK', N'单据联动开关', 70),
    (110111, N'SEND_PRODUCE_TAG',             N'DOC_LINK', N'单据联动开关', 80),
    (110111, N'SEND_PRODUCE_TRANSFER_TAG',    N'DOC_LINK', N'单据联动开关', 90),
    (110111, N'SEND_PRODUCE_FITOUT_TAG',      N'DOC_LINK', N'单据联动开关', 100),
    (110111, N'SEND_FITOUT_TAG',              N'DOC_LINK', N'单据联动开关', 110),
    (110111, N'RETURN_SEND_TAG',              N'DOC_LINK', N'单据联动开关', 120),
    (110111, N'RETURN_ORDER_SEND_TAG',        N'DOC_LINK', N'单据联动开关', 130),
    (110111, N'RETURN_PRODUCE_SEND_TAG',      N'DOC_LINK', N'单据联动开关', 140),
    (110111, N'COP_RETURN_DEPOT_TAG',         N'DOC_LINK', N'单据联动开关', 150),
    (110111, N'PUR_APPLY_TAG',                N'PURCHASE', N'采购流程开关', 10),
    (110111, N'PUR_APPLY_PLAN_PUR_TAG',       N'PURCHASE', N'采购流程开关', 20),
    (110111, N'PUR_PRODUCE_APPLY_TAG',        N'PURCHASE', N'采购流程开关', 30),
    (110111, N'PUR_CANCEL_DEPOT_TAG',         N'PURCHASE', N'采购流程开关', 40),
    (110111, N'PRODUCE_IN_ORDER_TAG',         N'PRODUCE',  N'生产流程开关', 10),
    (110111, N'PRODUCE_ORDER_TAG',            N'PRODUCE',  N'生产流程开关', 20),
    (110111, N'PRODUCE_PLAN_MOC_TAG',         N'PRODUCE',  N'生产流程开关', 30),
    (110111, N'QTY_LINE1',                    N'QTY_RULE', N'数量阈值',     10),
    (110111, N'QTY_LINE2',                    N'QTY_RULE', N'数量阈值',     20),
    (110111, N'QTY_LINE3',                    N'QTY_RULE', N'数量阈值',     30),
    (110111, N'QTY_PS_WIDTH',                 N'QTY_RULE', N'数量阈值',     40),
    (110111, N'LOGIN_F12',                    N'UI',       N'界面与查询',   10),
    (110111, N'QUICK_SEARCH_ALL',             N'UI',       N'界面与查询',   20),
    (110111, N'REMARK',                       N'MISC',     N'其它',         10);

/* 180213 考勤数据设置 */
INSERT INTO #param (OWNER_MODULE, PARAM_KEY, GROUP_CODE, GROUP_LABEL, SEQ_NO) VALUES
    (180213, N'MACHINE_START',     N'ATT_CARD',     N'考勤卡号解析', 10),
    (180213, N'MACHINE_LENGTH',    N'ATT_CARD',     N'考勤卡号解析', 20),
    (180213, N'TYPE_START',        N'ATT_CARD',     N'考勤卡号解析', 30),
    (180213, N'TYPE_LENGTH',       N'ATT_CARD',     N'考勤卡号解析', 40),
    (180213, N'CARD_START',        N'ATT_CARD',     N'考勤卡号解析', 50),
    (180213, N'CARD_LENGTH',       N'ATT_CARD',     N'考勤卡号解析', 60),
    (180213, N'YEAR_START',        N'ATT_CARD',     N'考勤卡号解析', 70),
    (180213, N'YEAR_LENGTH',       N'ATT_CARD',     N'考勤卡号解析', 80),
    (180213, N'MONTH_START',       N'ATT_CARD',     N'考勤卡号解析', 90),
    (180213, N'MONTH_LENGTH',      N'ATT_CARD',     N'考勤卡号解析', 100),
    (180213, N'DAY_START',         N'ATT_CARD',     N'考勤卡号解析', 110),
    (180213, N'DAY_LENGTH',        N'ATT_CARD',     N'考勤卡号解析', 120),
    (180213, N'HOUR_START',        N'ATT_CARD',     N'考勤卡号解析', 130),
    (180213, N'HOUR_LENGTH',       N'ATT_CARD',     N'考勤卡号解析', 140),
    (180213, N'MINUTE_START',      N'ATT_CARD',     N'考勤卡号解析', 150),
    (180213, N'MINUTE_LENGTH',     N'ATT_CARD',     N'考勤卡号解析', 160),
    (180213, N'SECOND_START',      N'ATT_CARD',     N'考勤卡号解析', 170),
    (180213, N'SECOND_LENGTH',     N'ATT_CARD',     N'考勤卡号解析', 180),
    (180213, N'SAT_REST_DAY',      N'ATT_CALENDAR', N'考勤日历',     10),
    (180213, N'SUN_REST_DAY',      N'ATT_CALENDAR', N'考勤日历',     20),
    (180213, N'SAT_ABSENT',        N'ATT_CALENDAR', N'考勤日历',     30),
    (180213, N'SUN_ABSENT',        N'ATT_CALENDAR', N'考勤日历',     40),
    (180213, N'HOLIDAY_ABSENT',    N'ATT_CALENDAR', N'考勤日历',     50),
    (180213, N'WAGE_ADD',          N'WAGE_MAP',     N'工资字段映射', 10),
    (180213, N'WAGE_WORK',         N'WAGE_MAP',     N'工资字段映射', 20),
    (180213, N'WAGE_OVER',         N'WAGE_MAP',     N'工资字段映射', 30),
    (180213, N'WAGE_REST',         N'WAGE_MAP',     N'工资字段映射', 40),
    (180213, N'WAGE_HOLIDAY',      N'WAGE_MAP',     N'工资字段映射', 50),
    (180213, N'WAGE_WORKTIME',     N'WAGE_MAP',     N'工资字段映射', 60),
    (180213, N'WAGE_OVERTIME',     N'WAGE_MAP',     N'工资字段映射', 70),
    (180213, N'WAGE_RESTTIME',     N'WAGE_MAP',     N'工资字段映射', 80),
    (180213, N'WAGE_HOLITIME',     N'WAGE_MAP',     N'工资字段映射', 90),
    (180213, N'REQUIRE_ENACTMENT', N'ATT_FLAG',     N'考勤其他',     10),
    (180213, N'DIMISSION_NO_WAGE', N'ATT_FLAG',     N'考勤其他',     20),
    (180213, N'REMARK',            N'MISC',         N'其它',         10);

/* 180662 考勤数据设置：与 180213 同一套参数，少一个"离职当月不保存工资" */
INSERT INTO #param (OWNER_MODULE, PARAM_KEY, GROUP_CODE, GROUP_LABEL, SEQ_NO)
SELECT 180662, PARAM_KEY, GROUP_CODE, GROUP_LABEL, SEQ_NO
FROM #param
WHERE OWNER_MODULE = 180213 AND PARAM_KEY <> N'DIMISSION_NO_WAGE';

/* 5. 从元数据补齐类型、默认值与说明（说明取 FIELDS.F_DESC） */
UPDATE p SET
    VALUE_TYPE = CASE
                     WHEN m.TY = N'bit' THEN N'bit'
                     WHEN m.TY IN (N'int', N'smallint', N'tinyint', N'bigint') THEN N'int'
                     WHEN m.TY IN (N'decimal', N'numeric', N'float', N'real', N'money', N'smallmoney') THEN N'decimal'
                     ELSE N'string'
                 END,
    DESC_TEXT = f.F_DESC,
    DEFAULT_VALUE = CASE
                        WHEN m.DEF IS NULL OR m.DEF LIKE N'%getdate%' THEN NULL
                        ELSE REPLACE(REPLACE(REPLACE(REPLACE(m.DEF, N'(', N''), N')', N''), N'N''', N''), N'''', N'')
                    END
FROM #param p
JOIN #owner ow ON ow.OWNER_MODULE = p.OWNER_MODULE
JOIN (
    SELECT ob.name AS SRC_TABLE, c.name AS COL_NAME, TYPE_NAME(c.user_type_id) AS TY, dc.definition AS DEF
    FROM sys.columns c
    JOIN sys.objects ob ON ob.object_id = c.object_id AND ob.type = N'U'
    JOIN sys.schemas s ON s.schema_id = ob.schema_id AND s.name = N'dbo'
    LEFT JOIN sys.default_constraints dc ON dc.parent_object_id = c.object_id AND dc.parent_column_id = c.column_id
    WHERE ob.name IN (N'SYSSS', N'HR_SETUP', N'HRM_SETUP')
) m ON m.SRC_TABLE COLLATE DATABASE_DEFAULT = ow.SRC_TABLE
   AND m.COL_NAME  COLLATE DATABASE_DEFAULT = p.PARAM_KEY
LEFT JOIN dbo.FIELDS f ON f.T_ID = ow.SRC_TABLE AND f.F_ID = p.PARAM_KEY;

/* 6. 覆盖率断言：备份表每一列必须恰好落在"已迁参数"或"显式不迁的技术列"之一 */
IF EXISTS (
    SELECT 1
    FROM #owner ow
    JOIN sys.columns c ON c.object_id = OBJECT_ID(ow.BAK_TABLE)
    WHERE NOT EXISTS (SELECT 1 FROM #param p WHERE p.OWNER_MODULE = ow.OWNER_MODULE AND p.PARAM_KEY = c.name COLLATE DATABASE_DEFAULT)
      AND NOT EXISTS (SELECT 1 FROM #tech  t WHERE t.OWNER_MODULE = ow.OWNER_MODULE AND t.PARAM_KEY  = c.name COLLATE DATABASE_DEFAULT)
)
    THROW 51000, N'系统参数纵向化：存在既未迁移也未登记为技术列的列。', 1;

IF EXISTS (
    SELECT 1
    FROM #owner ow
    WHERE (SELECT COUNT(*) FROM sys.columns c WHERE c.object_id = OBJECT_ID(ow.BAK_TABLE))
        <> (SELECT COUNT(*) FROM #param p WHERE p.OWNER_MODULE = ow.OWNER_MODULE)
         + (SELECT COUNT(*) FROM #tech  t WHERE t.OWNER_MODULE = ow.OWNER_MODULE)
)
    THROW 51000, N'系统参数纵向化：参数数 + 技术列数 <> 源表列数。', 1;

IF EXISTS (SELECT 1 FROM #param WHERE VALUE_TYPE IS NULL OR DESC_TEXT IS NULL)
    THROW 51000, N'系统参数纵向化：参数缺少类型或说明（FIELDS.F_DESC 不齐）。', 1;

IF EXISTS (SELECT 1 FROM #param WHERE OWNER_MODULE = 110111 HAVING COUNT(*) <> 37)
    THROW 51000, N'系统参数纵向化：110111 参数定义不是 37 条。', 1;
IF EXISTS (SELECT 1 FROM #param WHERE OWNER_MODULE = 180213 HAVING COUNT(*) <> 35)
    THROW 51000, N'系统参数纵向化：180213 参数定义不是 35 条。', 1;
IF EXISTS (SELECT 1 FROM #param WHERE OWNER_MODULE = 180662 HAVING COUNT(*) <> 34)
    THROW 51000, N'系统参数纵向化：180662 参数定义不是 34 条。', 1;

/* 7. 抽取现值：按 JSON 逐列取值，bit 归一为 1/0，字符串原样（不 TRIM） */
CREATE TABLE #value (OWNER_MODULE int NOT NULL, PARAM_KEY nvarchar(64) NOT NULL, PARAM_VALUE nvarchar(4000) NULL, PRIMARY KEY (OWNER_MODULE, PARAM_KEY));
INSERT INTO #value (OWNER_MODULE, PARAM_KEY, PARAM_VALUE)
SELECT OWNER_MODULE, PARAM_KEY, NULL FROM #param;

DECLARE @om int, @bak nvarchar(128), @sql nvarchar(max);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT OWNER_MODULE, BAK_TABLE FROM #owner ORDER BY OWNER_MODULE;
OPEN cur;
FETCH NEXT FROM cur INTO @om, @bak;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'IF EXISTS (SELECT 1 FROM ' + @bak + N') '
             + N'UPDATE v SET v.PARAM_VALUE = CASE WHEN p.VALUE_TYPE = N''bit'' '
             + N'        THEN CASE j.[value] WHEN N''true'' THEN N''1'' WHEN N''false'' THEN N''0'' ELSE j.[value] END '
             + N'        ELSE j.[value] END '
             + N'FROM #value v '
             + N'JOIN #param p ON p.OWNER_MODULE = v.OWNER_MODULE AND p.PARAM_KEY = v.PARAM_KEY '
             + N'JOIN OPENJSON((SELECT * FROM ' + @bak + N' FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER)) j '
             + N'  ON j.[key] COLLATE DATABASE_DEFAULT = v.PARAM_KEY '
             + N'WHERE v.OWNER_MODULE = ' + CAST(@om AS nvarchar(10)) + N';';
    EXEC sp_executesql @sql;
    FETCH NEXT FROM cur INTO @om, @bak;
END
CLOSE cur;
DEALLOCATE cur;
GO

/* 8. 重建表：主键 (OWNER_MODULE, PARAM_KEY)，生命周期列按 NOT NULL DEFAULT / 可空口径 */
DROP TABLE dbo.SYSSS;

CREATE TABLE dbo.SYSSS (
    OWNER_MODULE     INT            NOT NULL,
    PARAM_KEY        NVARCHAR(64)   NOT NULL,
    PARAM_VALUE      NVARCHAR(4000) NULL,
    VALUE_TYPE       NVARCHAR(16)   NOT NULL,
    DEFAULT_VALUE    NVARCHAR(4000) NULL,
    GROUP_CODE       NVARCHAR(32)   NOT NULL,
    GROUP_LABEL      NVARCHAR(50)   NOT NULL,
    DESC_TEXT        NVARCHAR(300)  NOT NULL,
    EFFECT_SCOPE     NVARCHAR(16)   NOT NULL,
    SEQ_NO           INT            NOT NULL,
    OPTIONS          NVARCHAR(500)  NULL,
    REMARK           NVARCHAR(1000) NULL,
    CREATE_PERSON    NCHAR(10)      NOT NULL CONSTRAINT DF_SYSSS_CREATE_PERSON DEFAULT (N''),
    CREATE_DATE      DATETIME       NOT NULL CONSTRAINT DF_SYSSS_CREATE_DATE   DEFAULT (GETDATE()),
    LAST_UPDATE_BY   NCHAR(10)      NULL,
    LAST_UPDATE_DATE DATETIME       NULL,
    CONSTRAINT PK_SYSSS PRIMARY KEY CLUSTERED (OWNER_MODULE, PARAM_KEY),
    CONSTRAINT CK_SYSSS_VALUE_TYPE   CHECK (VALUE_TYPE IN (N'bit', N'int', N'decimal', N'string')),
    CONSTRAINT CK_SYSSS_EFFECT_SCOPE CHECK (EFFECT_SCOPE IN (N'immediate', N'restart'))
);

CREATE INDEX IX_SYSSS_GROUP ON dbo.SYSSS (OWNER_MODULE, GROUP_CODE, SEQ_NO);
GO

/* 9. 写入参数行并断言 */
INSERT INTO dbo.SYSSS
    (OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
     GROUP_CODE, GROUP_LABEL, DESC_TEXT, EFFECT_SCOPE, SEQ_NO, OPTIONS, REMARK, CREATE_PERSON, CREATE_DATE)
SELECT p.OWNER_MODULE, p.PARAM_KEY, v.PARAM_VALUE, p.VALUE_TYPE, p.DEFAULT_VALUE,
       p.GROUP_CODE, p.GROUP_LABEL, p.DESC_TEXT, N'immediate', p.SEQ_NO, NULL, NULL, N'', GETDATE()
FROM #param p
JOIN #value v ON v.OWNER_MODULE = p.OWNER_MODULE AND v.PARAM_KEY = p.PARAM_KEY;

IF (SELECT COUNT(*) FROM dbo.SYSSS) <> 106
    THROW 51000, N'系统参数纵向化：新表条数不是 106。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 110111 HAVING COUNT(*) <> 37)
    THROW 51000, N'系统参数纵向化：110111 参数条数不是 37。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 180213 HAVING COUNT(*) <> 35)
    THROW 51000, N'系统参数纵向化：180213 参数条数不是 35。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 180662 HAVING COUNT(*) <> 34)
    THROW 51000, N'系统参数纵向化：180662 参数条数不是 34。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 180662 AND PARAM_VALUE IS NOT NULL)
    THROW 51000, N'系统参数纵向化：零行源表不应有取值。', 1;

IF EXISTS (SELECT PARAM_KEY FROM dbo.SYSSS WHERE PARAM_KEY COLLATE Latin1_General_BIN LIKE N'%[^A-Z0-9_]%')
    THROW 51000, N'系统参数纵向化：参数键含非标识符字符。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS WHERE GROUP_CODE = N'' OR GROUP_LABEL = N'' OR DESC_TEXT = N'' OR EFFECT_SCOPE NOT IN (N'immediate', N'restart'))
    THROW 51000, N'系统参数纵向化：分组或生效范围登记不全。', 1;
GO

/* 10. 逐键对拍：把备份表的每个键按同一口径取回，与新表双向 EXCEPT（不是取样） */
DECLARE @cmp nvarchar(max) = N'';
SELECT @cmp = @cmp + CASE WHEN @cmp = N'' THEN N'' ELSE N' UNION ALL ' END
           + N'SELECT ' + CAST(p.OWNER_MODULE AS nvarchar(10)) + N' AS OWNER_MODULE, N''' + p.PARAM_KEY
           + N''' AS PARAM_KEY, CONVERT(nvarchar(4000), [' + p.PARAM_KEY + N']) AS PARAM_VALUE FROM ' + ow.BAK_TABLE
FROM #param p
JOIN #owner ow ON ow.OWNER_MODULE = p.OWNER_MODULE
WHERE p.OWNER_MODULE <> 180662;

DECLARE @check nvarchar(max) =
      N'IF EXISTS ((' + @cmp + N') EXCEPT (SELECT OWNER_MODULE, PARAM_KEY, PARAM_VALUE FROM dbo.SYSSS WHERE OWNER_MODULE <> 180662))'
    + N' THROW 51000, N''系统参数纵向化：迁移后取值与备份不一致（备份有、新表缺或不同）。'', 1;'
    + N'IF EXISTS ((SELECT OWNER_MODULE, PARAM_KEY, PARAM_VALUE FROM dbo.SYSSS WHERE OWNER_MODULE <> 180662) EXCEPT (' + @cmp + N'))'
    + N' THROW 51000, N''系统参数纵向化：迁移后取值与备份不一致（新表有、备份缺或不同）。'', 1;';
EXEC sp_executesql @check;

PRINT N'系统参数纵向化完成：SYSSS 106 行（110111=37 / 180213=35 / 180662=34），逐键取值与备份一致。';
GO
