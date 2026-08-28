-- ADR-008：字段数据来源（数据选取）模型重构 P1 存储与迁移（2026-08-28）
--
-- 决策来源：docs/decisions/ADR-008-字段数据来源模型重构.md（用户拍板，已接受）
-- 结构：建 FIELDS_CHOOSER（独立数据源表，任意数量/有序/审计）→ 存量搬移（UNION ALL 展开四组列）
--      → RETURN_ITEMS JSON（SQL 无损转换）→ FILTER_STRUCT 三档规则化转换（本文件为离线生成静态转换，
--        带漂移守卫：仅当对应 CHOOSE_FILTERn 原文与生成时一致才落库，否则保持 NULL（fail-closed）并记 DRIFT）
--      → DROP FIELDS 四组 24 列 → P_Change_M_IDX 改写 → 受影响模块标脏。
-- 验证护栏：档一转换逐条经 LegacyChooserFilterConverter + ChooserFilterValidator 试编译；
--         手工/离线对拍报告见 logs/fields-chooser-migration/（不入库）。
-- 注意：CHOOSER_FILTER_MIGRATION_LOG 是迁移审计/待重建队列（P3 编译器上线后按清单回填），
--       不是运行时通道；FILTER_STRUCT=NULL 的启用来源运行期 fail-closed（空选项）。

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.FIELDS_CHOOSER', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.FIELDS_CHOOSER (
        ID               INT IDENTITY(1,1) NOT NULL,
        T_ID             NVARCHAR(100)  NOT NULL,
        F_ID             NVARCHAR(100)  NOT NULL,
        SERIAL_NO        INT            NOT NULL,
        ACTIVE_TAG       BIT            NOT NULL CONSTRAINT DF_FIELDS_CHOOSER_ACTIVE_TAG DEFAULT (0),
        SOURCE_T_ID      NVARCHAR(300)  NOT NULL,
        SOURCE_DESC      NVARCHAR(50)   NULL,
        SOURCE_M_IDX     INT            NULL,
        FILTER_STRUCT    NVARCHAR(MAX)  NULL,
        RETURN_ITEMS     NVARCHAR(MAX)  NULL,
        CREATE_BY        NVARCHAR(50)   NULL,
        CREATE_DATE      DATETIME       NULL,
        LAST_UPDATE_BY   NVARCHAR(50)   NULL,
        LAST_UPDATE_DATE DATETIME       NULL,
        CONSTRAINT PK_FIELDS_CHOOSER PRIMARY KEY CLUSTERED (ID ASC),
        CONSTRAINT UQ_FIELDS_CHOOSER UNIQUE (T_ID, F_ID, SERIAL_NO),
        CONSTRAINT FK_FIELDS_CHOOSER_FIELDS FOREIGN KEY (T_ID, F_ID)
            REFERENCES dbo.FIELDS (T_ID, F_ID) ON DELETE CASCADE
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FIELDS_CHOOSER_SOURCE_M_IDX' AND object_id = OBJECT_ID(N'dbo.FIELDS_CHOOSER'))
    CREATE NONCLUSTERED INDEX IX_FIELDS_CHOOSER_SOURCE_M_IDX ON dbo.FIELDS_CHOOSER (SOURCE_M_IDX ASC);

IF OBJECT_ID(N'dbo.CHOOSER_FILTER_MIGRATION_LOG', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CHOOSER_FILTER_MIGRATION_LOG (
        ID            INT IDENTITY(1,1) NOT NULL,
        T_ID          NVARCHAR(100)  NOT NULL,
        F_ID          NVARCHAR(100)  NOT NULL,
        SERIAL_NO     INT            NOT NULL,
        LEGACY_FILTER NVARCHAR(1000) NULL,
        TIER          NVARCHAR(20)   NOT NULL,
        STATUS        NVARCHAR(20)   NOT NULL,
        ERROR         NVARCHAR(500)  NULL,
        CREATED_AT    DATETIME       NOT NULL CONSTRAINT DF_CHOOSER_FILTER_LOG_CREATED_AT DEFAULT (GETDATE()),
        CONSTRAINT PK_CHOOSER_FILTER_MIGRATION_LOG PRIMARY KEY CLUSTERED (ID ASC)
    );
END

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_CHOOSER_FILTER_LOG_FIELD' AND object_id = OBJECT_ID(N'dbo.CHOOSER_FILTER_MIGRATION_LOG'))
    CREATE NONCLUSTERED INDEX IX_CHOOSER_FILTER_LOG_FIELD ON dbo.CHOOSER_FILTER_MIGRATION_LOG (T_ID, F_ID, SERIAL_NO);

INSERT INTO dbo.FIELDS_CHOOSER
    (T_ID, F_ID, SERIAL_NO, ACTIVE_TAG, SOURCE_T_ID, SOURCE_DESC, SOURCE_M_IDX, FILTER_STRUCT, RETURN_ITEMS,
     CREATE_BY, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
SELECT f.T_ID, LTRIM(RTRIM(f.F_ID)), v.SERIAL_NO, CAST(ISNULL(v.ACTIVE_TAG,0) AS bit),
       LTRIM(RTRIM(v.SOURCE_T_ID)), NULLIF(LTRIM(RTRIM(v.SOURCE_DESC)), ''), v.SOURCE_M_IDX,
       CASE WHEN LTRIM(RTRIM(ISNULL(v.FILTERVAL,''))) = '' THEN N'{"logic":"AND","items":[]}' ELSE NULL END,
       CASE WHEN LTRIM(RTRIM(ISNULL(v.RETURNVAL,''))) = '' THEN NULL ELSE (
           SELECT [target] = LTRIM(RTRIM(CASE
                       WHEN LOWER(LEFT(LTRIM(s.value), 4)) IN (N'txt_', N'cho_', N'dro_', N'chk_', N'lab_') THEN SUBSTRING(LTRIM(s.value), 5, 4000)
                       WHEN LOWER(LEFT(LTRIM(s.value), 5)) = N'hidd_' THEN SUBSTRING(LTRIM(s.value), 6, 4000)
                       ELSE LEFT(LTRIM(s.value), CHARINDEX(N'=', s.value) - 1) END)),
                  [column] = LTRIM(RTRIM(SUBSTRING(s.value, CHARINDEX(N'=', s.value) + 1, 4000)))
           FROM STRING_SPLIT(REPLACE(v.RETURNVAL, N';', N','), N',') s
           WHERE CHARINDEX(N'=', s.value) > 1
           FOR JSON PATH) END AS RETURN_ITEMS,
       N'EOS-MIG', GETDATE(), N'EOS-MIG', GETDATE()
FROM dbo.FIELDS f
CROSS APPLY (VALUES
    (1, f.CHOOSE_ACTIVE1, f.CHOOSE_T_ID1, f.CHOOSE_T_DESC1, f.CHOOSE_M_IDX1, f.CHOOSE_RETURNVAL1, f.CHOOSE_FILTER1),
    (2, f.CHOOSE_ACTIVE2, f.CHOOSE_T_ID2, f.CHOOSE_T_DESC2, f.CHOOSE_M_IDX2, f.CHOOSE_RETURNVAL2, f.CHOOSE_FILTER2),
    (3, f.CHOOSE_ACTIVE3, f.CHOOSE_T_ID3, f.CHOOSE_T_DESC3, f.CHOOSE_M_IDX3, f.CHOOSE_RETURNVAL3, f.CHOOSE_FILTER3),
    (4, f.CHOOSE_ACTIVE4, f.CHOOSE_T_ID4, f.CHOOSE_T_DESC4, f.CHOOSE_M_IDX4, f.CHOOSE_RETURNVAL4, f.CHOOSE_FILTER4)
) v(SERIAL_NO, ACTIVE_TAG, SOURCE_T_ID, SOURCE_DESC, SOURCE_M_IDX, RETURNVAL, FILTERVAL)
WHERE LTRIM(RTRIM(ISNULL(v.SOURCE_T_ID,''))) <> '';

-- 档一转换（离线 C# 生成，带漂移守卫：原文不一致不落库，保持 NULL fail-closed）
UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{m.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"PRODUCT.PRO_ID","operator":"NE","value":"{m.PRO_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRODUCT.PRO_ID<>''{m.PRO_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"COP_SEND_M.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"COP_SEND_M.FINISHED_TAG","operator":"EQ","value":"0","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'COP_SEND_M.CONFIRM_TAG=1 AND COP_SEND_M.FINISHED_TAG=0';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER3, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.OUTER_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'SUPPLIER.OUTER_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 4
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER4, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT_PRICE_M.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER3, N''))) = N'CLIENT_PRICE_M.CLIENT_ID=''{M.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 4
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER4, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'CLIENT.BUSINESS_TAG=0';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"T_ID","operator":"EQ","value":"HR_WAGE","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'T_ID = ''HR_WAGE''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''1''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER3, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"GE","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER3, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"GE","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER3, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''1''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER3, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"GE","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER3, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"GE","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null},{"field":"PRODUCT.QTY","operator":"GT","value":"0","nullSafe":null},{"field":"PRODUCT.DEPOT_ID","operator":"EQ","value":"YL","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3'' AND PRODUCT.QTY>0 AND PRODUCT.DEPOT_ID = ''YL''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null},{"field":"PRODUCT.QTY","operator":"GT","value":"0","nullSafe":null},{"field":"PRODUCT.DEPOT_ID","operator":"EQ","value":"YL","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER3, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4'' AND PRODUCT.QTY>0 AND PRODUCT.DEPOT_ID = ''YL''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.OUTER_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'SUPPLIER.OUTER_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.OUTER_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'SUPPLIER.OUTER_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.OUTER_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'SUPPLIER.OUTER_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.OUTER_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'SUPPLIER.OUTER_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"UNIT_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'UNIT_TYPE=3';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.OUTER_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'SUPPLIER.CONFIRM_TAG=1 AND SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.OUTER_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.OUTER_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'SUPPLIER.CONFIRM_TAG=1 AND SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.OUTER_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.OUTER_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'SUPPLIER.CONFIRM_TAG=1 AND SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.OUTER_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=3';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"GE","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE>=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''1''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"GE","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE>=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''1''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"GE","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE>=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.OUTER_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'SUPPLIER.OUTER_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.STOP_TAG","operator":"EQ","value":"0","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 3
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER3, N''))) = N'PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.BUSINESS_TAG=0 AND PRODUCT.STOP_TAG=0';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"GT","value":"2","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE>''2''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"4","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''4''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.SUPPLIER_ID=''{M.SUPPLIER_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.SUPPLIER_ID=''{M.SUPPLIER_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.SUPPLIER_ID","operator":"EQ","value":"{m.SUPPLIER_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.SUPPLIER_ID=''{M.SUPPLIER_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.CONFIRM_TAG=1 AND PRODUCT.CLIENT_ID=''{m.CLIENT_ID}''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":null},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.CLIENT_ID","operator":"EQ","value":"{m.CLIENT_ID}","nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"NE","value":"1","nullSafe":null},{"field":"PRODUCT.BUSINESS_TAG","operator":"EQ","value":"0","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRODUCT.CONFIRM_TAG<>1 AND PRODUCT.BUSINESS_TAG=0';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"PRODUCT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"PRODUCT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"},{"field":"PRODUCT.PRO_TYPE","operator":"EQ","value":"3","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 2
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER2, N''))) = N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"B_M_IDX","operator":"EQ","value":"{module}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'B_M_IDX={module}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"R_M_IDX","operator":"EQ","value":"{d.M_IDX}","nullSafe":null}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'R_M_IDX={d.M_IDX}';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"CLIENT.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"CLIENT.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1';

UPDATE c SET FILTER_STRUCT = N'{"logic":"AND","items":[{"field":"SUPPLIER.BUSINESS_TAG","operator":"ISNULL_ZERO","value":null,"nullSafe":null},{"field":"SUPPLIER.CONFIRM_TAG","operator":"EQ","value":"1","nullSafe":"ZERO"}]}'
FROM dbo.FIELDS_CHOOSER c
JOIN dbo.FIELDS f ON f.T_ID = c.T_ID AND LTRIM(RTRIM(f.F_ID)) = c.F_ID
WHERE c.SERIAL_NO = 1
  AND c.FILTER_STRUCT IS NULL
  AND LTRIM(RTRIM(ISNULL(f.CHOOSE_FILTER1, N''))) = N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1';

-- 迁移审计/待重建清单（含原文，供 P3 编译器与顾问重建；DRIFT 由末尾翻转步骤标记）
INSERT INTO dbo.CHOOSER_FILTER_MIGRATION_LOG (T_ID, F_ID, SERIAL_NO, LEGACY_FILTER, TIER, STATUS, ERROR) VALUES
    (N'ACCOUNT_TYPE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'BANK', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'BILLKIND', N'B_M_IDX', 1, N'ISNULL(MODULES.MODI_URL,'''')<>'''' AND m_tag=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MODULES 不在 JOIN 白名单内。'),
    (N'BILLKIND', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'BOM_COST_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'BOM_COST_M', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRO_TYPE=''1'' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM BOM_COST_M)', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM BOM_COST_M)（不匹配比较链语法）'),
    (N'BOM_INSTRUCT_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'BOM_INSTRUCT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'BOM_INSTRUCT_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND ISNULL(PRODUCT.PRO_TYPE,'''') <''3'' AND PRODUCT.PRO_NO NOT IN (SELECT BOM_INSTRUCT_M.PRO_NO FROM BOM_INSTRUCT_M)', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT BOM_INSTRUCT_M.PRO_NO FROM BOM_INSTRUCT_M)（不匹配比较链语法）'),
    (N'BOM_REDEPLOY_D', N'PRO_ID', 1, N'CUS_PRODUCT.PRO_TYPE =''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'BOM_REDEPLOY_D', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'BOM_REDEPLOY_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRODUCT.PRO_ID<>''{m.PRO_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'BOM_REDEPLOY_D', N'PRO_NO', 4, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRO_NO NOT IN (SELECT PRO_NO FROM CLIENT_PRICE_D)', N'TIER2', N'PENDING_P3', N'需扩展算子：PRO_NO NOT IN (SELECT PRO_NO FROM CLIENT_PRICE_D)（不匹配比较链语法）'),
    (N'BOM_REDEPLOY_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'BOM_REDEPLOY_M', N'PRO_ID', 1, N'CUS_PRODUCT.PRO_TYPE =''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'BOM_REDEPLOY_M', N'REDEPLOY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'BOM_STRU_D', N'IIF_ELEMENT_PRO_NO', 1, N'PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'BOM_STRU_D', N'PRO_ELEMENT_PRO_NO', 1, N'PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'BOM_STRU_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'BOM_STRU_D', N'STUFF_ID', 1, N'TYPE_FORMULA.TYPE_ID=''{m.TYPE_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 TYPE_FORMULA 不在 JOIN 白名单内。'),
    (N'BOM_STRU_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'BOM_STRU_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND ISNULL(PRODUCT.PRO_TYPE,'''') <''3'' AND PRODUCT.PRO_NO NOT IN (SELECT BOM_STRU_M.PRO_NO FROM BOM_STRU_M)', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT BOM_STRU_M.PRO_NO FROM BOM_STRU_M)（不匹配比较链语法）'),
    (N'CAR', N'CHARGE_PERSON', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'CAR_ADDUP_D', N'MISSION_NO', 1, N'CAR_MISSION_M.CONFIRM_TAG=1 AND CAR_MISSION_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CAR_MISSION_M 不在 JOIN 白名单内。；跨表引用 CAR_MISSION_M 不在 JOIN 白名单内。'),
    (N'CAR_ADDUP_M', N'ADDUP_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CAR_ADDUP_M', N'FOLLOW1', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_ADDUP_M', N'FOLLOW2', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_ADDUP_M', N'MOTORMAN', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_FEE_M', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_FEE_M', N'FEE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CAR_FILLOIL_M', N'FILLMAN', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_FILLOIL_M', N'FILLOIL_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CAR_FILLOIL_M', N'MOTORMAN', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_FILLOIL_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'CAR_MISSION_D', N'SEND_NO', 1, N'COP_SEND_M.CONFIRM_TAG=1 AND COP_SEND_M.FINISHED_TAG=0', N'TIER1', N'CONVERTED', NULL),
    (N'CAR_MISSION_M', N'FOLLOW1', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_MISSION_M', N'FOLLOW2', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_MISSION_M', N'GUARD', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_MISSION_M', N'MISSION_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CAR_MISSION_M', N'MOTORMAN', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_MISSION_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'CAR_REPAIR_M', N'MOTORMAN', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_REPAIR_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'CAR_REPAIR_M', N'REPAIR_EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CAR_REPAIR_M', N'REPAIR_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CAR_TAKEOUT_M', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'CLIENT', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CLIENT', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'CLIENT', N'PRO_ID', 1, N'CUS_PRODUCT.PRO_TYPE =''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CLIENT', N'PRO_NO', 1, N'CUS_PRODUCT.PRO_TYPE =''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CLIENT_LINKMAN', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CLIENT_PRICE_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CLIENT_PRICE_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM CLIENT_PRICE_D WHERE CLIENT_PRICE_D.CLIENT_ID=''{M.CLIENT_ID}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM CLIENT_PRICE_D WHERE CLIENT_PRICE_D.CLIENT_ID=''{M.CLIENT_ID}'')（不匹配比较链语法）'),
    (N'CLIENT_PRICE_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'CLIENT_PRICE_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CLIENT_PRICE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COLOR', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COMPANY', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_ACCOUNT_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ACCOUNT_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ACCOUNT_D', N'S_R_NO', 1, N'ISNULL(COP_SEND_M.CONFIRM_TAG,0)=1 AND COP_SEND_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_SEND_D.FINISHED_TAG,0)=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_SEND_D 不在 JOIN 白名单内。'),
    (N'COP_ACCOUNT_D', N'S_R_NO', 2, N'COP_RETURN_M.CONFIRM_TAG=1 AND COP_RETURN_M.CLIENT_ID=''{M.CLIENT_ID}'' AND COP_RETURN_D.FINISHED_TAG=0', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_RETURN_M.CONFIRM_TAG EQ=1；COP_RETURN_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_RETURN_D.FINISHED_TAG EQ=0'),
    (N'COP_ACCOUNT_D', N'S_R_TYPE', 1, N'ISNULL(COP_SEND_M.CONFIRM_TAG,0)=1 AND COP_SEND_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_SEND_D.FINISHED_TAG,0)=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_SEND_D 不在 JOIN 白名单内。'),
    (N'COP_ACCOUNT_D', N'S_R_TYPE', 2, N'COP_RETURN_M.CONFIRM_TAG=1 AND COP_RETURN_M.CLIENT_ID=''{M.CLIENT_ID}'' AND COP_RETURN_.FINISHED_TAG=0', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_RETURN_M.CONFIRM_TAG EQ=1；COP_RETURN_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_RETURN_.FINISHED_TAG EQ=0'),
    (N'COP_ACCOUNT_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'COP_ACCOUNT_DD_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ACCOUNT_DD_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ACCOUNT_DD_M', N'ACCOUNT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ACCOUNT_DD_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ACCOUNT_DD_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_ACCOUNT_M', N'ACCOUNT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ACCOUNT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ACCOUNT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_BACK_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_BACK_D', N'PRO_NO', 1, N'COP_ORDER_M.CONFIRM_TAG=1 AND COP_ORDER_M.CLIENT_ID=''{M.CLIENT_ID}'' AND COP_ORDER_D.FINISHED_TAG=0 AND COP_ORDER_D.QTY>COP_ORDER_D.FINISHED_SEND_QTY+COP_ORDER_D.BACK_MATERIAL+COP_ORDER_D.BACK_BAD', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_ORDER_M.CONFIRM_TAG EQ=1；COP_ORDER_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_ORDER_D.FINISHED_TAG EQ=0；COP_ORDER_D.QTY GT=COP_ORDER_D.FINISHED_SEND_QTY+COP_ORDER_D.BACK_MATERIAL+COP_ORDER_D.BACK_BAD'),
    (N'COP_BACK_D', N'PRO_NO', 3, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.CLIENT_ID=''{M.CLIENT_ID}'' AND (ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_QTY-MOC_PRODUCE_M.FINISHED_SEND_QTY,0)>0 OR ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_SPARE_QTY,0)>ISNULL(MOC_PRODUCE_M.FINISHED_SEND_SPARE_QTY,0))', N'TIER2', N'PENDING_P3', N'需扩展算子：(ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_QTY-MOC_PRODUCE_M.FINISHED_SEND_QTY,0)>0 OR ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_SPARE_QTY,0)>ISNULL(MOC_PRODUCE_M.FINISHED_SEND_SPARE_QTY,0))（不匹配比较链语法）'),
    (N'COP_BACK_M', N'BACK_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_BACK_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_CALLBACK_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_CALLBACK_D', N'S_R_NO', 1, N'COP_SEND_M.CLIENT_ID=''{m.CLIENT_ID}'' AND COP_SEND_M.CONFIRM_TAG=1 AND ISNULL(COP_SEND_M.FINISHED_TAG,0)=0 AND ISNULL(COP_SEND_D.CALLBACK_NO,'''')=''''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_SEND_D 不在 JOIN 白名单内。'),
    (N'COP_CALLBACK_D', N'S_R_NO', 2, N'COP_RETURN_M.CLIENT_ID=''{m.CLIENT_ID}'' AND COP_RETURN_M.CONFIRM_TAG=1 AND COP_RETURN_M.FINISHED_TAG=0 AND ISNULL(COP_RETURN_D.CALLBACK_NO,'''')=''''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_RETURN_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_RETURN_M.CONFIRM_TAG EQ=1；COP_RETURN_M.FINISHED_TAG EQ=0；COP_RETURN_D.CALLBACK_NO EQ[EMPTY]='),
    (N'COP_CALLBACK_M', N'CALLBACK_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_CALLBACK_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_CHAFFER_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_CHAFFER_D', N'CLIENT_PRO_NO', 1, N'CLIENT_PRICE_M.CLIENT_ID=''{m.CLIENT_ID}'' AND CLIENT_PRICE_D.CURR_ID=''{m.CURR_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CLIENT_PRICE_D 不在 JOIN 白名单内。'),
    (N'COP_CHAFFER_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_CHAFFER_D', N'PRO_NO', 2, N'CLIENT_PRICE_M.CLIENT_ID=''{M.CLIENT_ID}'' AND CLIENT_PRICE_D.CURR_ID=''{m.CURR_ID}'' AND CLIENT_PRICE_D.TAX_ID=''{m.TAX_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CLIENT_PRICE_D 不在 JOIN 白名单内。；跨表引用 CLIENT_PRICE_D 不在 JOIN 白名单内。'),
    (N'COP_CHAFFER_D', N'PRO_WIDTH', 1, N'MOU_ASSESS_M.CONFIRM_TAG=1 and MOU_ASSESS_M.PRO_NO=''{d.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。；跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。'),
    (N'COP_CHAFFER_D', N'PRO_WIDTH', 2, N'MOU_ASSESS_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。'),
    (N'COP_CHAFFER_D', N'SIZE_UNIT_ID', 1, N'UNIT.UNIT_TYPE = ''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'COP_CHAFFER_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'COP_CHAFFER_M', N'CHAFFER_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_CHAFFER_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_CHAFFER_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_CHAFFER_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_FITIN_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_FITIN_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_FITIN_D', N'PRO_NO', 1, N'COP_FITOUT_M.CONFIRM_TAG=1 AND COP_FITOUT_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_FITOUT_D.FINISHED_TAG,0)=0 AND ISNULL(COP_FITOUT_D.QTY-COP_FITOUT_D.FINISHED_QTY,0)>ISNULL(COP_FITOUT_D.RETURN_QTY,0)', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(COP_FITOUT_D.QTY-COP_FITOUT_D.FINISHED_QTY,0)>ISNULL(COP_FITOUT_D.RETURN_QTY,0)（不匹配比较链语法）'),
    (N'COP_FITIN_D', N'PRO_NO', 2, N'COP_ORDER_M.CONFIRM_TAG=1 AND COP_ORDER_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_ORDER_D.FINISHED_TAG,0)=0 AND ISNULL(COP_ORDER_D.FINISHED_FITOUT_QTY-COP_ORDER_D.FINISHED_SEND_QTY,0)>0', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(COP_ORDER_D.FINISHED_FITOUT_QTY-COP_ORDER_D.FINISHED_SEND_QTY,0)>0（不匹配比较链语法）'),
    (N'COP_FITIN_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_FITIN_M', N'FITIN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_FITOUT_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_FITOUT_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_FITOUT_D', N'ORDER_NO', 1, N'COP_ORDER_M.CONFIRM_TAG=1 AND COP_ORDER_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_ORDER_D.FINISHED_TAG,0)=0 AND ISNULL(COP_ORDER_D.QTY+COP_ORDER_D.SPARE_QTY-COP_ORDER_D.FINISHED_FITOUT_QTY,0)>0', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(COP_ORDER_D.QTY+COP_ORDER_D.SPARE_QTY-COP_ORDER_D.FINISHED_FITOUT_QTY,0)>0（不匹配比较链语法）'),
    (N'COP_FITOUT_D', N'PRO_NO', 1, N'COP_ORDER_M.CONFIRM_TAG=1 AND COP_ORDER_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_ORDER_D.FINISHED_TAG,0)=0 AND (ISNULL(COP_ORDER_D.QTY-COP_ORDER_D.FINISHED_FITOUT_QTY,0)>0 OR ISNULL(COP_ORDER_D.SPARE_QTY,0)>ISNULL(COP_ORDER_D.FINISHED_FITOUT_SPARE_QTY,0))', N'TIER2', N'PENDING_P3', N'需扩展算子：(ISNULL(COP_ORDER_D.QTY-COP_ORDER_D.FINISHED_FITOUT_QTY,0)>0 OR ISNULL(COP_ORDER_D.SPARE_QTY,0)>ISNULL(COP_ORDER_D.FINISHED_FITOUT_SPARE_QTY,0))（不匹配比较链语法）'),
    (N'COP_FITOUT_D', N'PRO_NO', 2, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.CLIENT_ID=''{M.CLIENT_ID}'' AND (ISNULL(MOC_PRODUCE_M.FINISHED_QTY-MOC_PRODUCE_M.FINISHED_FITOUT_QTY,0)>0 OR ISNULL(MOC_PRODUCE_M.FINISHED_SPARE_QTY,0)>ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_SPARE_QTY,0))', N'TIER2', N'PENDING_P3', N'需扩展算子：(ISNULL(MOC_PRODUCE_M.FINISHED_QTY-MOC_PRODUCE_M.FINISHED_FITOUT_QTY,0)>0 OR ISNULL(MOC_PRODUCE_M.FINISHED_SPARE_QTY,0)>ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_SPARE_QTY,0))（不匹配比较链语法）'),
    (N'COP_FITOUT_D', N'PRO_NO', 3, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'COP_FITOUT_D', N'PRO_NO', 4, N'COP_SHIPMENT_M.CONFIRM_TAG=1 AND COP_SHIPMENT_D.CLIENT_ID=''{M.CLIENT_ID}'' AND (ISNULL(COP_ORDER_D.QTY,0) >ISNULL(COP_ORDER_D.FINISHED_FITOUT_QTY,0) OR ISNULL(COP_ORDER_D.SPARE_QTY,0)>ISNULL(COP_ORDER_D.FINISHED_FITOUT_SPARE_QTY,0))', N'TIER2', N'PENDING_P3', N'需扩展算子：(ISNULL(COP_ORDER_D.QTY,0) >ISNULL(COP_ORDER_D.FINISHED_FITOUT_QTY,0) OR ISNULL(COP_ORDER_D.SPARE_QTY,0)>ISNULL(COP_ORDER_D.FINISHED_FITOUT_SPARE_QTY,0))（不匹配比较链语法）'),
    (N'COP_FITOUT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_FITOUT_M', N'FITOUT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_MONTH_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_MONTH_D', N'CLIENT_NAME', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_CALC_D', N'APPLY_NO', 1, N'PUR_APPLY_M.CONFIRM_TAG=1 AND ISNULL(PUR_APPLY_D.QTY-PUR_APPLY_D.PURCHASE_QTY,0)>0', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(PUR_APPLY_D.QTY-PUR_APPLY_D.PURCHASE_QTY,0)>0（不匹配比较链语法）'),
    (N'COP_ORDER_CALC_D', N'LINE_ID', 2, N'SUPPLIER.OUTER_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_CALC_D', N'PRO_NO', 1, N'COP_ORDER_D.PLAN_QTY-COP_ORDER_D.FINISHED_PLAN_QTY>0 AND COP_ORDER_M.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER2', N'PENDING_P3', N'需扩展算子：COP_ORDER_D.PLAN_QTY-COP_ORDER_D.FINISHED_PLAN_QTY>0（不匹配比较链语法）'),
    (N'COP_ORDER_CALC_D', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND MOC_PRODUCE_M.END_TAG=0  AND ISNULL(MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY,0)>0', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY,0)>0（不匹配比较链语法）'),
    (N'COP_ORDER_CALC_M', N'CALC_ORDER_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_CALC_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_CHANGE_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_CHANGE_D', N'ORDER_SERIAL_NO', 1, N'COP_ORDER_D.ORDER_TYPE=''{m.ORDER_TYPE}'' and COP_ORDER_D.ORDER_NO=''{m.ORDER_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_ORDER_D 不在 JOIN 白名单内。；跨表引用 COP_ORDER_D 不在 JOIN 白名单内。'),
    (N'COP_ORDER_CHANGE_D', N'PRO_NO', 1, N'COP_ORDER_D.ORDER_TYPE=''{m.ORDER_TYPE}'' and COP_ORDER_D.ORDER_NO=''{m.ORDER_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_ORDER_D 不在 JOIN 白名单内。；跨表引用 COP_ORDER_D 不在 JOIN 白名单内。'),
    (N'COP_ORDER_CHANGE_M', N'CHANGE_ORDER_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_CHANGE_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_CHANGE_M', N'ORDER_TYPE', 1, N'COP_ORDER_M.CONFIRM_TAG=1 AND COP_ORDER_M.CLIENT_ID=''{m.CLIENT_ID}'' AND ISNULL(COP_ORDER_M.FINISHED_TAG,0)=0', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_ORDER_M.CONFIRM_TAG EQ=1；COP_ORDER_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_ORDER_M.FINISHED_TAG ISNULL_ZERO='),
    (N'COP_ORDER_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_D', N'PRO_NO', 1, N'CLIENT_PRICE_M.CLIENT_ID=''{m.CLIENT_ID}'' AND CLIENT_PRICE_D.CURR_ID=''{m.CURR_ID}'' AND PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CLIENT_PRICE_D 不在 JOIN 白名单内。'),
    (N'COP_ORDER_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_D', N'PRO_NO', 3, N'COP_QUOTE_M.CLIENT_ID=''{m.CLIENT_ID}'' AND COP_QUOTE_M.CONFIRM_TAG=1', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_QUOTE_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_QUOTE_M.CONFIRM_TAG EQ=1'),
    (N'COP_ORDER_D', N'QUOTE_NO', 1, N'COP_QUOTE_M.CLIENT_ID=''{m.CLIENT_ID}'' AND COP_QUOTE_M.CONFIRM_TAG=1', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_QUOTE_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_QUOTE_M.CONFIRM_TAG EQ=1'),
    (N'COP_ORDER_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'COP_ORDER_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_ORDER_M', N'ORDER_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_ORDER_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_ORDER_MORE', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_PACK_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_PACK_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_PACK_M', N'SEND_TYPE', 1, N'COP_SEND_M.CONFIRM_TAG=1 AND COP_SEND_M.CLIENT_ID=''{M.CLIENT_ID}'' AND COP_SEND_D.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_SEND_D 不在 JOIN 白名单内。'),
    (N'COP_PREPAY_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_PREPAY_D', N'PRO_NO', 1, N'COP_ORDER_M.CONFIRM_TAG=1 AND COP_ORDER_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_ORDER_D.FINISHED_TAG,0)=0 AND ISNULL(COP_ORDER_D.AMOUNT-COP_ORDER_D.FINISHED_AMOUNT,0)>0', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(COP_ORDER_D.AMOUNT-COP_ORDER_D.FINISHED_AMOUNT,0)>0（不匹配比较链语法）'),
    (N'COP_PREPAY_D', N'PRO_NO', 2, N'COP_FITOUT_M.CONFIRM_TAG=1 AND COP_FITOUT_M.CLIENT_ID=''{M.CLIENT_ID}'' AND COP_FITOUT_D.FINISHED_TAG=0 AND ISNULL(COP_FITOUT_D.QTY-COP_FITOUT_D.FINISHED_QTY-COP_FITOUT_D.RETURN_QTY,0)>0', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(COP_FITOUT_D.QTY-COP_FITOUT_D.FINISHED_QTY-COP_FITOUT_D.RETURN_QTY,0)>0（不匹配比较链语法）'),
    (N'COP_PREPAY_D', N'PRO_NO', 3, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.CLIENT_ID=''{M.CLIENT_ID}'' AND (ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_QTY-MOC_PRODUCE_M.FINISHED_SEND_QTY,0)>0 OR ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_SPARE_QTY,0)>ISNULL(MOC_PRODUCE_M.FINISHED_SEND_SPARE_QTY,0))', N'TIER2', N'PENDING_P3', N'需扩展算子：(ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_QTY-MOC_PRODUCE_M.FINISHED_SEND_QTY,0)>0 OR ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_SPARE_QTY,0)>ISNULL(MOC_PRODUCE_M.FINISHED_SEND_SPARE_QTY,0))（不匹配比较链语法）'),
    (N'COP_PREPAY_D', N'PRO_NO', 4, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'COP_PREPAY_M', N'BANK_ID', 1, N'BANK.CURR_ID=''{m.CURR_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 BANK 不在 JOIN 白名单内。'),
    (N'COP_PREPAY_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_PREPAY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_PREPAY_M', N'PREPAY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_QUOTE_D', N'CHAFFER_NO', 1, N'COP_CHAFFER_M.CLIENT_ID=''{m.CLIENT_ID}'' AND COP_CHAFFER_M.CONFIRM_TAG=1 AND COP_CHAFFER_D.CURR_ID=''{m.CURR_ID}'' AND COP_CHAFFER_D.CHAFFER_TYPE+COP_CHAFFER_D.CHAFFER_NO+CAST(COP_CHAFFER_D.SERIAL_NO AS CHAR) NOT IN (SELECT COP_QUOTE_D.CHAFFER_TYPE+COP_QUOTE_D.CHAFFER_NO+CAST(COP_QUOTE_D.CHAFFER_SERIAL_NO AS CHAR) FROM COP_QUOTE_D)', N'TIER2', N'PENDING_P3', N'需扩展算子：COP_CHAFFER_D.CHAFFER_TYPE+COP_CHAFFER_D.CHAFFER_NO+CAST(COP_CHAFFER_D.SERIAL_NO AS CHAR) NOT IN (SELECT COP_QUOTE_D.CHAFFER_TYPE+COP_QUOTE_D.CHAFFER_NO+CAST(COP_QUOTE_D.CHAFFER_SERIAL_NO AS CHAR) FROM COP_QUOTE_D)（不匹配比较链语法）'),
    (N'COP_QUOTE_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_QUOTE_D', N'PRO_NO', 1, N'COP_CHAFFER_M.CLIENT_ID=''{m.CLIENT_ID}'' AND COP_CHAFFER_M.CONFIRM_TAG=1 AND COP_CHAFFER_D.CURR_ID=''{m.CURR_ID}'' AND COP_CHAFFER_D.TAX_ID=''{m.TAX_ID}'' AND ISNULL(COP_CHAFFER_D.QUOTE_NO,'''')=''''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_CHAFFER_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_CHAFFER_M.CONFIRM_TAG EQ=1；COP_CHAFFER_D.CURR_ID EQ={m.CURR_ID}；COP_CHAFFER_D.TAX_ID EQ={m.TAX_ID}；COP_CHAFFER_D.QUOTE_NO EQ[EMPTY]='),
    (N'COP_QUOTE_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_QUOTE_D', N'PRO_NO', 3, N'CLIENT_PRICE_M.CLIENT_ID=''{M.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'COP_QUOTE_D', N'PRO_NO', 4, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_QUOTE_D', N'SIZE_UNIT_ID', 1, N'UNIT.UNIT_TYPE=''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'COP_QUOTE_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'COP_QUOTE_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_QUOTE_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_QUOTE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_QUOTE_M', N'QUOTE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_RECEIPT_D', N'ACCOUNT_NO', 1, N'COP_ACCOUNT_M.CONFIRM_TAG=1 AND COP_ACCOUNT_M.FINISHED_TAG=0 AND COP_ACCOUNT_M.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_ACCOUNT_M 不在 JOIN 白名单内。；跨表引用 COP_ACCOUNT_M 不在 JOIN 白名单内。；跨表引用 COP_ACCOUNT_M 不在 JOIN 白名单内。'),
    (N'COP_RECEIPT_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_RECEIPT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_RECEIPT_OTHER', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_RECEIPT_OTHER', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_RECEIPT_OTHER', N'PREPAY_TYPE', 1, N'COP_PREPAY_M.CONFIRM_TAG=1 AND COP_PREPAY_M.AMOUNT>COP_PREPAY_M.PREPAY_AMOUNT', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_PREPAY_M 不在 JOIN 白名单内。；跨表引用 COP_PREPAY_M 不在 JOIN 白名单内。'),
    (N'COP_RETURN_D', N'BAD_DEPOT_ID', 1, N'DEPOT.MRP=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_RETURN_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_RETURN_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_RETURN_D', N'PRO_NO', 1, N'COP_SEND_M.CONFIRM_TAG=1 AND COP_SEND_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_SEND_D.QTY-COP_SEND_D.RETURN_QTY,0)>0', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(COP_SEND_D.QTY-COP_SEND_D.RETURN_QTY,0)>0（不匹配比较链语法）'),
    (N'COP_RETURN_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'COP_RETURN_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'COP_RETURN_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_RETURN_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_RETURN_M', N'RETURN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_SEND_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_SEND_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1 AND DEPOT_ID<>''TW''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_SEND_D', N'ORDER_TYPE', 1, N'COP_ORDER_M.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_ORDER_M.CLIENT_ID EQ={m.CLIENT_ID}'),
    (N'COP_SEND_D', N'PRO_NO', 1, N'COP_ORDER_M.CONFIRM_TAG=1 AND COP_ORDER_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_ORDER_D.FINISHED_TAG,0)=0 AND ISNULL(COP_ORDER_D.QTY-COP_ORDER_D.FINISHED_SEND_QTY,0)>0', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(COP_ORDER_D.QTY-COP_ORDER_D.FINISHED_SEND_QTY,0)>0（不匹配比较链语法）'),
    (N'COP_SEND_D', N'PRO_NO', 2, N'COP_SHIPMENT_M.CONFIRM_TAG=1 AND COP_SHIPMENT_D.CLIENT_ID=''{M.CLIENT_ID}'' AND COP_SHIPMENT_D.QTY>COP_SHIPMENT_D.FINISHED_QTY AND Datediff(day,COP_SHIPMENT_M.SHIPMENT_DATE,GETDATE())=0', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_SHIPMENT_M.CONFIRM_TAG EQ=1；COP_SHIPMENT_D.CLIENT_ID EQ={m.CLIENT_ID}；COP_SHIPMENT_D.QTY GT=COP_SHIPMENT_D.FINISHED_QTY；COP_SHIPMENT_M.SHIPMENT_DATE DAYS_FROM_TODAY=0'),
    (N'COP_SEND_D', N'PRO_NO', 3, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.CLIENT_ID=''{M.CLIENT_ID}'' AND (ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_QTY-MOC_PRODUCE_M.FINISHED_SEND_QTY,0)>0 OR ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_SPARE_QTY,0)>ISNULL(MOC_PRODUCE_M.FINISHED_SEND_SPARE_QTY,0))', N'TIER2', N'PENDING_P3', N'需扩展算子：(ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_QTY-MOC_PRODUCE_M.FINISHED_SEND_QTY,0)>0 OR ISNULL(MOC_PRODUCE_M.FINISHED_FITOUT_SPARE_QTY,0)>ISNULL(MOC_PRODUCE_M.FINISHED_SEND_SPARE_QTY,0))（不匹配比较链语法）'),
    (N'COP_SEND_D', N'PRO_NO', 4, N'COP_ORDER_M.CONFIRM_TAG=1 AND COP_ORDER_M.CLIENT_ID=''{M.CLIENT_ID}'' AND ISNULL(COP_ORDER_D.FINISHED_TAG,0)=0', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_ORDER_M.CONFIRM_TAG EQ=1；COP_ORDER_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_ORDER_D.FINISHED_TAG ISNULL_ZERO='),
    (N'COP_SEND_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'COP_SEND_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_SEND_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_SEND_M', N'SEND_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'COP_SHIPMENT_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'COP_SHIPMENT_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'COP_SHIPMENT_D', N'ORDER_NO', 1, N'COP_ORDER_M.CLIENT_ID=''{d.CLIENT_ID}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_ORDER_M.CLIENT_ID EQ={d.CLIENT_ID}'),
    (N'COP_SHIPMENT_D', N'PRO_NO', 1, N'COP_ORDER_D.FINISHED_SEND_QTY<COP_ORDER_D.QTY and COP_ORDER_D.FINISHED_TAG=0 AND COP_ORDER_M.CLIENT_ID=''{d.CLIENT_ID}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_ORDER_D.FINISHED_SEND_QTY LT=COP_ORDER_D.QTY；COP_ORDER_D.FINISHED_TAG EQ=0；COP_ORDER_M.CLIENT_ID EQ={d.CLIENT_ID}'),
    (N'COP_SHIPMENT_D', N'PRO_NO', 2, N'MOC_PRODUCE_M.CLIENT_ID=''{d.CLIENT_ID}'' AND MOC_PRODUCE_M.FINISHED_QTY<MOC_PRODUCE_M.QTY', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'COP_SHIPMENT_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'COP_SHIPMENT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'COP_SHIPMENT_M', N'SHIPMENT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CURR', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'CUS_ACCOUNT_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_ACCOUNT_D', N'S_R_NO', 1, N'ISNULL(COP_SEND_M.CONFIRM_TAG,0)=1 AND COP_SEND_M.CLIENT_ID=''{M.CLIENT_ID}'' AND COP_SEND_D.CURR_ID=''{d.CURR_ID}'' AND ISNULL(COP_SEND_D.FINISHED_TAG,0)=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_SEND_D 不在 JOIN 白名单内。；跨表引用 COP_SEND_D 不在 JOIN 白名单内。'),
    (N'CUS_ACCOUNT_D', N'S_R_NO', 2, N'COP_RETURN_M.CONFIRM_TAG=1 AND COP_RETURN_M.CLIENT_ID=''{M.CLIENT_ID}'' AND COP_RETURN_D.CURR_ID=''{d.CURR_ID}'' AND COP_RETURN_D.FINISHED_TAG=0', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_RETURN_M.CONFIRM_TAG EQ=1；COP_RETURN_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_RETURN_D.CURR_ID EQ={d.CURR_ID}；COP_RETURN_D.FINISHED_TAG EQ=0'),
    (N'CUS_ACCOUNT_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'CUS_ACCOUNT_M', N'ACCOUNT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_ACCOUNT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_ACCOUNT_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_M.CONFIRM_TAG=1', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：CUS_MANUAL_M.MANUAL_STATE NE=3；CUS_MANUAL_M.CONFIRM_TAG EQ=1'),
    (N'CUS_ACCOUNT_M', N'PRO_ID', 1, N'CUS_PRODUCT.PRO_TYPE =''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CUS_DRAWBACK_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_DRAWBACK_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。；跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'CUS_EXPORT_D', N'PRO_ID', 1, N'CUS_MANUAL_PRO.MANUAL_NO=''{m.MANUAL_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_PRO 不在 JOIN 白名单内。'),
    (N'CUS_EXPORT_D', N'PRO_NO', 1, N'CUS_PRODUCT.PRO_SORT<>''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CUS_EXPORT_M', N'ACCOUNT_NO', 1, N'CUS_ACCOUNT_M.CONFIRM_TAG=1 AND CUS_ACCOUNT_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_ACCOUNT_M 不在 JOIN 白名单内。；跨表引用 CUS_ACCOUNT_M 不在 JOIN 白名单内。'),
    (N'CUS_EXPORT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_EXPORT_M', N'EXPORT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_EXPORT_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。；跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'CUS_EXPORT_M', N'SEAL_NO', 1, N'CUS_SEAL_M.CONFIRM_TAG=1 AND CUS_SEAL_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_SEAL_M 不在 JOIN 白名单内。；跨表引用 CUS_SEAL_M 不在 JOIN 白名单内。'),
    (N'CUS_IMPORT_D', N'PRO_ID', 1, N'CUS_MANUAL_MAT.MANUAL_NO=''{m.MANUAL_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_MAT 不在 JOIN 白名单内。'),
    (N'CUS_IMPORT_D', N'PRO_NO', 1, N'CUS_PRODUCT.PRO_SORT<>''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CUS_IMPORT_M', N'IMPORT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_IMPORT_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'CUS_IMPORT_M', N'SUPPLIER_ID', 1, N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_MANUAL_BOM', N'PRO_ID', 1, N'CUS_PRODUCT.PRO_TYPE=''2'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CUS_MANUAL_BOM', N'PRO_NO', 1, N'CUS_PRODUCT.PRO_TYPE=''2'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CUS_MANUAL_MAT', N'PRO_ID', 1, N'CUS_PRODUCT.PRO_SORT<>''2'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CUS_MANUAL_MAT', N'PRO_NO', 1, N'CUS_PRODUCT.PRO_SORT<>''2'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CUS_MANUAL_PRO', N'PRO_ID', 1, N'CUS_PRODUCT.PRO_TYPE=''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CUS_MANUAL_PRO', N'PRO_NO', 1, N'CUS_PRODUCT.PRO_TYPE=''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'CUS_MATERIN_D', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_MAT.CUS_QTY>CUS_MANUAL_MAT.IN_QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：CUS_MANUAL_M.MANUAL_STATE NE=3；CUS_MANUAL_MAT.CUS_QTY GT=CUS_MANUAL_MAT.IN_QTY'),
    (N'CUS_MATERIN_D', N'PURCHASE_NO', 1, N'CUS_PURCHASE_M.CONFIRM_TAG=1 and CUS_PURCHASE_D.FINISHED_QTY<CUS_PURCHASE_D.QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：CUS_PURCHASE_M.CONFIRM_TAG EQ=1；CUS_PURCHASE_D.FINISHED_QTY LT=CUS_PURCHASE_D.QTY'),
    (N'CUS_MATERIN_M', N'BILL_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_MATERIN_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'CUS_MATERIN_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_MATEROUT_D', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_MAT.IN_QTY>CUS_MANUAL_MAT.OUT_QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：CUS_MANUAL_M.MANUAL_STATE NE=3；CUS_MANUAL_MAT.IN_QTY GT=CUS_MANUAL_MAT.OUT_QTY'),
    (N'CUS_MATEROUT_M', N'BILL_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_MATEROUT_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'CUS_MATEROUT_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_PACK_M', N'BILL_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_PACK_M', N'CLIENT_NAME', 1, N'CLIENT.BUSINESS_TAG=0', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_PROCESS_INVOICE_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。；跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'CUS_PROCESS_PAY_D', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。；跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'CUS_PROCESS_PAY_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。；跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'CUS_PROIN_D', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_PRO.CUS_QTY>CUS_MANUAL_PRO.OUT_QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：CUS_MANUAL_M.MANUAL_STATE NE=3；CUS_MANUAL_PRO.CUS_QTY GT=CUS_MANUAL_PRO.OUT_QTY'),
    (N'CUS_PROIN_M', N'BILL_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_PROOUT_D', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_PRO.IN_QTY>CUS_MANUAL_PRO.OUT_QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：CUS_MANUAL_M.MANUAL_STATE NE=3；CUS_MANUAL_PRO.IN_QTY GT=CUS_MANUAL_PRO.OUT_QTY'),
    (N'CUS_PROOUT_M', N'BILL_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_PURCHASE_D', N'EXPORT_NO', 1, N'CUS_EXPORT_MAT.MANUAL_NO=''{m.MANUAL_NO}'' AND CUS_EXPORT_MAT.QTY>CUS_EXPORT_MAT.PUR_QTY', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_EXPORT_MAT 不在 JOIN 白名单内。；跨表引用 CUS_EXPORT_MAT 不在 JOIN 白名单内。'),
    (N'CUS_PURCHASE_D', N'IMPORT_NO', 1, N'CUS_IMPORT_M.MANUAL_NO=''{m.MANUAL_NO}'' and CUS_IMPORT_M.CONFIRM_TAG=1 and CUS_IMPORT_D.PRO_ID=''{d.PRO_ID}'' and CUS_IMPORT_D.PUR_QTY<CUS_IMPORT_D.QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：CUS_IMPORT_M.MANUAL_NO EQ={m.MANUAL_NO}；CUS_IMPORT_M.CONFIRM_TAG EQ=1；CUS_IMPORT_D.PRO_ID EQ={d.PRO_ID}；CUS_IMPORT_D.PUR_QTY LT=CUS_IMPORT_D.QTY'),
    (N'CUS_PURCHASE_D', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_MAT.QTY>CUS_MANUAL_MAT.IN_QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：CUS_MANUAL_M.MANUAL_STATE NE=3；CUS_MANUAL_MAT.QTY GT=CUS_MANUAL_MAT.IN_QTY'),
    (N'CUS_PURCHASE_D', N'MANUAL_NO', 2, N'CUS_EXPORT_MAT.MANUAL_NO=''{m.MANUAL_NO}'' AND CUS_EXPORT_MAT.QTY>CUS_EXPORT_MAT.PUR_QTY', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_EXPORT_MAT 不在 JOIN 白名单内。；跨表引用 CUS_EXPORT_MAT 不在 JOIN 白名单内。'),
    (N'CUS_PURCHASE_D', N'PRICE', 1, N'SUPPLIER_PRICE_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' AND SUPPLIER_PRICE_D.CURR_ID=''{d.CURR_ID}'' AND SUPPLIER_PRICE_D.TAX_ID=''{d.TAX_ID}'' AND SUPPLIER_PRICE_D.TAX_TYPE=''{d.TAX_TYPE}'' AND SUPPLIER_PRICE_D.PRO_NO=''{d.PRO_NO}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：SUPPLIER_PRICE_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；SUPPLIER_PRICE_D.CURR_ID EQ={d.CURR_ID}；SUPPLIER_PRICE_D.TAX_ID EQ={d.TAX_ID}；SUPPLIER_PRICE_D.TAX_TYPE EQ={d.TAX_TYPE}；SUPPLIER_PRICE_D.PRO_NO EQ={d.PRO_NO}'),
    (N'CUS_PURCHASE_D', N'PRO_ID', 1, N'CUS_EXPORT_MAT.MANUAL_NO=''{m.MANUAL_NO}'' AND CUS_EXPORT_MAT.QTY>CUS_EXPORT_MAT.PUR_QTY', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_EXPORT_MAT 不在 JOIN 白名单内。；跨表引用 CUS_EXPORT_MAT 不在 JOIN 白名单内。'),
    (N'CUS_PURCHASE_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE=''2''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'CUS_PURCHASE_M', N'PURCHASE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_PURCHASE_M', N'SUPPLIER_ID', 1, N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_SEAL_D', N'PRO_ID', 1, N'CUS_MANUAL_PRO.MANUAL_NO=''{m.MANUAL_NO}'' AND CUS_MANUAL_PRO.QTY>CUS_MANUAL_PRO.EXP_QTY', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_PRO 不在 JOIN 白名单内。；跨表引用 CUS_MANUAL_PRO 不在 JOIN 白名单内。'),
    (N'CUS_SEAL_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'CUS_SEAL_M', N'MANUAL_NO', 1, N'CUS_MANUAL_M.MANUAL_STATE<>''3'' AND CUS_MANUAL_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。；跨表引用 CUS_MANUAL_M 不在 JOIN 白名单内。'),
    (N'DEPOT', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'DEPOT', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'DEPT', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'FIELDS', N'CHOOSE_M_IDX1', 1, N'MODULES.MASTER_TABLE=''{m.CHOOSE_T_ID1}'' or MODULES.DETAIL_TABLE=''{m.CHOOSE_T_ID1}''', N'TIER2', N'PENDING_P3', N'需扩展算子：MODULES.MASTER_TABLE=''{m.CHOOSE_T_ID1}'' or MODULES.DETAIL_TABLE=''{m.CHOOSE_T_ID1}''（不匹配比较链语法）'),
    (N'FIELDS', N'CHOOSE_M_IDX2', 1, N'MODULES.MASTER_TABLE=''{m.CHOOSE_T_ID2}'' or MODULES.DETAIL_TABLE=''{m.CHOOSE_T_ID2}''', N'TIER2', N'PENDING_P3', N'需扩展算子：MODULES.MASTER_TABLE=''{m.CHOOSE_T_ID2}'' or MODULES.DETAIL_TABLE=''{m.CHOOSE_T_ID2}''（不匹配比较链语法）'),
    (N'FIELDS', N'CHOOSE_M_IDX3', 1, N'MODULES.MASTER_TABLE=''{m.CHOOSE_T_ID3}'' or MODULES.DETAIL_TABLE=''{m.CHOOSE_T_ID3}''', N'TIER2', N'PENDING_P3', N'需扩展算子：MODULES.MASTER_TABLE=''{m.CHOOSE_T_ID3}'' or MODULES.DETAIL_TABLE=''{m.CHOOSE_T_ID3}''（不匹配比较链语法）'),
    (N'FIELDS', N'CHOOSE_M_IDX4', 1, N'MODULES.MASTER_TABLE=''{m.CHOOSE_T_ID4}'' or MODULES.DETAIL_TABLE=''{m.CHOOSE_T_ID4}''', N'TIER2', N'PENDING_P3', N'需扩展算子：MODULES.MASTER_TABLE=''{m.CHOOSE_T_ID4}'' or MODULES.DETAIL_TABLE=''{m.CHOOSE_T_ID4}''（不匹配比较链语法）'),
    (N'HALF_IN_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'HALF_IN_M', N'IN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HALF_OUT_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'HALF_OUT_M', N'OUT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HALF_PRO', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'HALF_PRO', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'HALF_PRO', N'SIZE_UNIT_ID', 1, N'UNIT.UNIT_TYPE = ''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'HALF_PRO_DEPOT', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'HR_ABSENT_D', N'ABSENT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_ABSENT_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_ABSENT_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_ABSENT_M', N'ABSENT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_ABSENT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_ADD_D', N'ADD_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_ADD_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_ADD_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_ADD_M', N'ADD_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_ADD_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_ADJUST_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_ADJUST_M', N'ADJUST_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_ADJUST_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_AMERCE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_AMERCE_D', N'AMERCE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_AMERCE_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_AMERCE_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_AMERCE_M', N'AMERCE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_AMERCE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_APPLY_D', N'APPLY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_APPLY_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_APPLY_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_APPLY_D WHERE HR_APPLY_D.APPLY_TYPE=''{m.APPLY_TYPE}'' AND HR_APPLY_D.APPLY_NO=''{m.APPLY_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_APPLY_D WHERE HR_APPLY_D.APPLY_TYPE=''{m.APPLY_TYPE}''（不匹配比较链语法）'),
    (N'HR_APPLY_M', N'APPLY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_APPLY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_AWARD', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_AWARD_D', N'AWARD_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_AWARD_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_AWARD_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_AWARD_M', N'AWARD_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_AWARD_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_BASEPAY_D', N'BASEPAY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_BASEPAY_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_BASEPAY_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_BASEPAY_M m,HR_BASEPAY_D d WHERE m.BASEPAY_TYPE=d.BASEPAY_TYPE and m.BASEPAY_NO=d.BASEPAY_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_BASEPAY_M m,HR_BASEPAY_D d WHERE m.BASEPAY_TYPE=d.BASEPAY_TYPE（不匹配比较链语法）'),
    (N'HR_BASEPAY_M', N'BASEPAY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_BASEPAY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_BED', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_BLOOD', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_CERTIFY_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_CERTIFY_M', N'CERTIFY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_CONTRACT_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_CONTRACT_M', N'CONT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_DIARY', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY}', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。；跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_DIMISSION', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_DIMISSION_M', N'DIMISSION_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_DIMISSION_M', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_DIMISSION_M', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_DIMISSION_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_DIPLOMA', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_DORM', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_DUTY', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_EMPLOYEE', N'INTRODUCER', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_EMPLOYEE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_EMPLOYEE_CARD', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_EMPLOYEE_CARD)', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_EMPLOYEE_CARD)（不匹配比较链语法）'),
    (N'HR_EMPLOYEE_CARD', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_CARD = 1 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_EMPLOYEE_CARD)', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_EMPLOYEE_CARD)（不匹配比较链语法）'),
    (N'HR_EMPLOYEE_CARD', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_EMPLOYEE_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_ENACTMENT_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_ENACTMENT_D WHERE ENACTMENT_NO=''{m.ENACTMENT_NO}'' AND ENACTMENT_TYPE=''{m.ENACTMENT_TYPE}'')

'')', N'TIER3', N'MANUAL', N'脏数据（乱码/截断/未闭合引号/悬空运算符）'),
    (N'HR_ENACTMENT_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_ENACTMENT_D WHERE ENACTMENT_NO=''{m.ENACTMENT_NO}'' AND ENACTMENT_TYPE=''{m.ENACTMENT_TYPE}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN (SELECT EMP_ID FROM HR_ENACTMENT_D WHERE ENACTMENT_NO=''{m.ENACTMENT_NO}''（不匹配比较链语法）'),
    (N'HR_ENACTMENT_M', N'ENACTMENT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_ENACTMENT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_EVECTION', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_EVECTION_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_EVECTION_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_EVECTION_D', N'EVECTION_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_EVECTION_M', N'EVECTION_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_EVECTION_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_EXCHANGE_M', N'EXCHANGE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_EXCHANGE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_FOREGIFT_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_FOREGIFT_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_FOREGIFT_D', N'FOREGIFT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_FOREGIFT_M', N'FOREGIFT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_FOREGIFT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_GRADE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_HOLIDAY', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_LANGUAGE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_LEAVE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_LEAVE_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_LEAVE_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_LEAVE_D', N'LEAVE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_LEAVE_M', N'LEAVE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_LEAVE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_LOAN_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_LOAN_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_LOAN_D', N'LOAN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_LOAN_M', N'LOAN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_LOAN_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_NATION', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_PLAN_D', N'EMP_NO', 1, N'ISNULL(HR_EMPLOYEE.STATE,0) < 4 AND HR_EMPLOYEE.EMP_ID NOT IN (SELECT HR_PLAN_D.EMP_ID FROM HR_PLAN_M,HR_PLAN_D WHERE HR_PLAN_M.PLAN_TYPE=HR_PLAN_D.PLAN_TYPE AND HR_PLAN_M.PLAN_NO=HR_PLAN_D.PLAN_NO AND HR_PLAN_M.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN (SELECT HR_PLAN_D.EMP_ID FROM HR_PLAN_M,HR_PLAN_D WHERE HR_PLAN_M.PLAN_TYPE=HR_PLAN_D.PLAN_TYPE（不匹配比较链语法）'),
    (N'HR_PLAN_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_PLAN_M', N'PLAN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_POLITY', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_POST', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_PROVINCE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_RECESS_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_RECESS_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_RECESS_M', N'RECESS_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_REDEPLOY_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_REDEPLOY_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_REDEPLOY_D', N'REDEPLOY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_REDEPLOY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_REDEPLOY_M', N'REDEPLOY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_SAFE_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_SAFE_D', N'IN_DATE', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE（不匹配比较链语法）'),
    (N'HR_SAFE_M', N'SAFE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_SETUP', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_SETUP', N'WAGE_ADD', 1, N'T_ID = ''HR_WAGE''', N'TIER1', N'CONVERTED', NULL),
    (N'HR_SIGN_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_SIGN_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_SIGN_D', N'EMP_NO', 2, N'HR_EMPLOYEE.IF_CARD=1 AND HR_DIARY.COUNT_DATE={m.SIGN_DATE} AND (HR_DIARY.ERR_TIME>'''' OR HR_DIARY.ABSENT_TIME>0 OR HR_DIARY.LEAVE_EARLY_TIMES>0 OR HR_DIARY.LATE_TIMES>0)', N'TIER2', N'PENDING_P3', N'需扩展算子：(HR_DIARY.ERR_TIME>'''' OR HR_DIARY.ABSENT_TIME>0 OR HR_DIARY.LEAVE_EARLY_TIMES>0 OR HR_DIARY.LATE_TIMES>0)（不匹配比较链语法）'),
    (N'HR_SIGN_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_SIGN_M', N'SIGN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_SUBTRACT_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_SUBTRACT_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_SUBTRACT_D', N'IN_DATE', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE（不匹配比较链语法）'),
    (N'HR_SUBTRACT_D', N'SUBTRACT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_SUBTRACT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_SUBTRACT_M', N'SUBTRACT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_TIMETYPE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_TITLE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_TXT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_TXT_M', N'TXT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_WAGE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_WAGE', N'WAGE_FIELD', 1, N'FIELDS.[T_ID]=''HR_WAGE_D'' AND FIELDS.F_ID NOT IN (SELECT WAGE_FIELD FROM HR_WAGE) AND F_ID LIKE ''%_ITEM%''', N'TIER2', N'PENDING_P3', N'需扩展算子：FIELDS.[T_ID]=''HR_WAGE_D''（不匹配比较链语法）'),
    (N'HR_WAGE', N'WAGE_FIELD_DESC', 1, N'[FIELDS.T_ID]=''HR_WAGE_D''', N'TIER2', N'PENDING_P3', N'需扩展算子：[FIELDS.T_ID]=''HR_WAGE_D''（不匹配比较链语法）'),
    (N'HR_WAGE_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_WAGE_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE（不匹配比较链语法）'),
    (N'HR_WAGE_D', N'EMP_NO', 2, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE（不匹配比较链语法）'),
    (N'HR_WAGE_D', N'IN_DATE', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE（不匹配比较链语法）'),
    (N'HR_WAGE_D', N'WAGE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_WAGE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_WAGE_M', N'WAGE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_WAGESYS', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_WORKTIME_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HR_WORKTIME_D', N'EMP_NO', 1, N'HR_EMPLOYEE.IN_DATE<=''{m.COUNT_DATE}'' AND (HR_EMPLOYEE.DIMISSION_DATE IS NULL OR HR_EMPLOYEE.DIMISSION_DATE>''{m.COUNT_DATE}'') AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WORKTIME_D WHERE EXISTS(SELECT * FROM HR_WORKTIME_M WHERE COUNT_DATE=''{m.COUNT_DATE}'' AND WORKTIME_TYPE=HR_WORKTIME_D.WORKTIME_TYPE AND WORKTIME_NO=HR_WORKTIME_D.WORKTIME_NO)) AND HR_EMPLOYEE.STATE < 4', N'TIER2', N'PENDING_P3', N'需扩展算子：(HR_EMPLOYEE.DIMISSION_DATE IS NULL OR HR_EMPLOYEE.DIMISSION_DATE>''{m.COUNT_DATE}'')（不匹配比较链语法）'),
    (N'HR_WORKTIME_D', N'WORKTIME_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_WORKTIME_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HR_WORKTIME_M', N'WORKTIME_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HR_WORKTYPE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_ADJUST_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_ADJUST_M', N'ADJUST_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_ADJUST_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_BASEPAY_D', N'BASEPAY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_BASEPAY_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_BASEPAY_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_BASEPAY_M m,HR_BASEPAY_D d WHERE m.BASEPAY_TYPE=d.BASEPAY_TYPE and m.BASEPAY_NO=d.BASEPAY_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_BASEPAY_M m,HR_BASEPAY_D d WHERE m.BASEPAY_TYPE=d.BASEPAY_TYPE（不匹配比较链语法）'),
    (N'HRM_BASEPAY_M', N'BASEPAY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_BASEPAY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_DIARY', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY}', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。；跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_EVECTION_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_EVECTION_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_EVECTION_D', N'EVECTION_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_EVECTION_M', N'EVECTION_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_EVECTION_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_EXCHANGE_M', N'EXCHANGE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_EXCHANGE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_HOLIDAY', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_LEAVE_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_LEAVE_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_LEAVE_D', N'LEAVE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_LEAVE_M', N'LEAVE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_LEAVE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_PLAN_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 and HR_EMPLOYEE.EMP_ID not in(select d.EMP_ID from HR_PLAN_M m,HR_PLAN_D d where m.PLAN_TYPE=d.PLAN_TYPE and m.PLAN_NO=d.PLAN_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID not in(select d.EMP_ID from HR_PLAN_M m,HR_PLAN_D d where m.PLAN_TYPE=d.PLAN_TYPE（不匹配比较链语法）'),
    (N'HRM_PLAN_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_PLAN_M', N'PLAN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_RECESS_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_RECESS_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_RECESS_M', N'RECESS_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_SETUP', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_SIGN_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_SIGN_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_SIGN_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_SIGN_M', N'SIGN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_TIMETYPE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_WAGE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_WAGE', N'WAGE_FIELD', 1, N'FIELDS.[T_ID]=''HR_WAGE_D'' AND FIELDS.F_ID NOT IN (SELECT WAGE_FIELD FROM HR_WAGE) AND F_ID LIKE ''%_ITEM%''', N'TIER2', N'PENDING_P3', N'需扩展算子：FIELDS.[T_ID]=''HR_WAGE_D''（不匹配比较链语法）'),
    (N'HRM_WAGE', N'WAGE_FIELD_DESC', 1, N'[FIELDS.T_ID]=''HR_WAGE_D''', N'TIER2', N'PENDING_P3', N'需扩展算子：[FIELDS.T_ID]=''HR_WAGE_D''（不匹配比较链语法）'),
    (N'HRM_WAGE_D', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'HRM_WAGE_D', N'EMP_NO', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE（不匹配比较链语法）'),
    (N'HRM_WAGE_D', N'EMP_NO', 2, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE（不匹配比较链语法）'),
    (N'HRM_WAGE_D', N'IN_DATE', 1, N'HR_EMPLOYEE.STATE < 4 AND HR_EMPLOYEE.IF_SECRECY={m.IF_SECRECY} AND HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''{m.COUNT_MONTH}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_EMPLOYEE.EMP_ID NOT IN(SELECT EMP_ID FROM HR_WAGE_M m,HR_WAGE_D d WHERE m.WAGE_TYPE=d.WAGE_TYPE（不匹配比较链语法）'),
    (N'HRM_WAGE_D', N'WAGE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_WAGE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'HRM_WAGE_M', N'WAGE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'HRM_WAGESYS', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_BATCH_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_BATCH_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'INV_BATCH_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_BATCH_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'INV_CHECK_STOCK_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1  AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM INV_CHECK_STOCK_D WHERE CHECK_STOCK_TYPE=''{m.CHECK_STOCK_TYPE}'' AND CHECK_STOCK_NO=''{m.CHECK_STOCK_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM INV_CHECK_STOCK_D WHERE CHECK_STOCK_TYPE=''{m.CHECK_STOCK_TYPE}''（不匹配比较链语法）'),
    (N'INV_CHECK_STOCK_M', N'CHECK_STOCK_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INV_CHECK_STOCK_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_CHECK_STOCK_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_DEPOT_LOG', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_DEPOT_LOG', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'INV_LOAN_D', N'DEPOT_ID', 1, N'DEPOT.DEPOT_ID<>''{d.IN_DEPOT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_LOAN_D', N'IN_DEPOT_ID', 1, N'DEPOT.DEPOT_ID<>''{d.OUT_DEPOT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_LOAN_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''1''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_LOAN_D', N'PRO_NO', 2, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_LOAN_D', N'PRO_NO', 3, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_LOAN_M', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'INV_LOAN_M', N'LOAN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_ADJUST_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_ADJUST_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_ADJUST_D', N'PRO_NO', 2, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_ADJUST_D', N'PRO_NO', 3, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_ADJUST_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'INV_OCCUR_ADJUST_M', N'OCCUR_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_ADJUST_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_IN_D', N'CPDEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_IN_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_IN_D', N'PRO_NO', 2, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_IN_D', N'PRO_NO', 3, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_IN_D', N'PRO_NO', 4, N'ISNULL(PUR_PURCHASE_D.FINISHED_TAG,0) = 0 AND PUR_PURCHASE_M.CONFIRM_TAG = 1 AND PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0 AND ''{m.OCCUR_TYPE}''=''TWQR''', N'TIER2', N'PENDING_P3', N'需扩展算子：PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0（不匹配比较链语法）'),
    (N'INV_OCCUR_IN_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'INV_OCCUR_IN_M', N'OCCUR_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_IN_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_INIT_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''1''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_INIT_D', N'PRO_NO', 2, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_INIT_D', N'PRO_NO', 3, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_INIT_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'INV_OCCUR_INIT_M', N'OCCUR_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_INIT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_OUT_D', N'BATCH_NO', 1, N'INV_BATCH_M.IN_SUM > INV_BATCH_M.OUT_SUM', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 INV_BATCH_M 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_OUT_D', N'CPDEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_OUT_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_OUT_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1'' AND ''{m.OCCUR_TYPE}''=''QTC''', N'TIER2', N'PENDING_P3', N'需扩展算子：''{m.OCCUR_TYPE}''=''QTC''（不匹配比较链语法）'),
    (N'INV_OCCUR_OUT_D', N'PRO_NO', 2, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_OUT_D', N'PRO_NO', 3, N'INV_PRO_DEPOT.QTY>0.1 AND INV_PRO_DEPOT.DEPOT_ID=''TW'' AND (''{m.OCCUR_TYPE}''=''TWQC'' OR ''{m.OCCUR_TYPE}''=''PJBL'')', N'TIER2', N'PENDING_P3', N'需扩展算子：(''{m.OCCUR_TYPE}''=''TWQC'' OR ''{m.OCCUR_TYPE}''=''PJBL'')（不匹配比较链语法）'),
    (N'INV_OCCUR_OUT_D', N'PRO_NO', 4, N'PRODUCT.QTY>0.1 AND ''{m.OCCUR_TYPE}''=''QTC''', N'TIER2', N'PENDING_P3', N'需扩展算子：''{m.OCCUR_TYPE}''=''QTC''（不匹配比较链语法）'),
    (N'INV_OCCUR_OUT_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'INV_OCCUR_OUT_M', N'OCCUR_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_OUT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。');

INSERT INTO dbo.CHOOSER_FILTER_MIGRATION_LOG (T_ID, F_ID, SERIAL_NO, LEGACY_FILTER, TIER, STATUS, ERROR) VALUES
    (N'INV_OCCUR_SCRAP_D', N'DEPOT_ID', 1, N'DEPOT.DEPOT_ID <> ''{d.IN_DEPOT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_SCRAP_D', N'IN_DEPOT_ID', 1, N'DEPOT.DEPOT_ID <> ''{d.DEPOT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_SCRAP_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_SCRAP_D', N'PRO_NO', 2, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_SCRAP_D', N'PRO_NO', 3, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_SCRAP_D', N'PRO_NO', 4, N'INV_PRO_DEPOT.QTY>0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 INV_PRO_DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_SCRAP_D', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_SCRAP_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'INV_OCCUR_SCRAP_M', N'OCCUR_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_SCRAP_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_TRANSFER_D', N'DEPOT_ID', 1, N'INV_PRO_DEPOT.DEPOT_ID <> ''{d.IN_DEPOT_ID}'' AND INV_PRO_DEPOT.QTY>0 AND INV_PRO_DEPOT.PRO_NO = ''{d.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 INV_PRO_DEPOT 不在 JOIN 白名单内。；跨表引用 INV_PRO_DEPOT 不在 JOIN 白名单内。；跨表引用 INV_PRO_DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_TRANSFER_D', N'IN_DEPOT_ID', 1, N'DEPOT.DEPOT_ID <> ''{d.OUT_DEPOT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_TRANSFER_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE>=''1''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_TRANSFER_D', N'PRO_NO', 2, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3'' AND PRODUCT.QTY>0 AND PRODUCT.DEPOT_ID = ''YL''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_TRANSFER_D', N'PRO_NO', 3, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''4'' AND PRODUCT.QTY>0 AND PRODUCT.DEPOT_ID = ''YL''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_TRANSFER_D', N'PRO_NO', 4, N'INV_PRO_DEPOT.QTY>0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 INV_PRO_DEPOT 不在 JOIN 白名单内。'),
    (N'INV_OCCUR_TRANSFER_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'INV_OCCUR_TRANSFER_M', N'OCCUR_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INV_OCCUR_TRANSFER_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_PRO_DEPOT', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'INV_PRO_DEPOT', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_PRO_DEPOT', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_PRO_DEPOT', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'INV_PRO_MONTH_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_PRO_MONTH_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'INV_PRO_MONTH_M', N'MONTH_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INV_PRO_MONTH_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'INV_RETURN_D', N'DEPOT_ID', 1, N'DEPOT.DEPOT_ID<>''{d.OUT_DEPOT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_RETURN_D', N'OUT_DEPOT_ID', 1, N'DEPOT.DEPOT_ID<>''{d.IN_DEPOT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'INV_RETURN_D', N'PRO_NO', 1, N'INV_LOAN_M.CONFIRM_TAG=1 AND INV_LOAN_D.QTY-INV_LOAN_D.RETURN_QTY>0', N'TIER2', N'PENDING_P3', N'需扩展算子：INV_LOAN_D.QTY-INV_LOAN_D.RETURN_QTY>0（不匹配比较链语法）'),
    (N'INV_RETURN_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'INV_RETURN_M', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'INV_RETURN_M', N'RETURN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'INVOICE_OUT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'LINE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOC_BACK_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_BACK_D', N'GET_NO', 1, N'MOC_GET_M.CONFIRM_TAG=1 and MOC_GET_D.RETURN_QTY-MOC_GET_D.RETURNED_QTY-MOC_GET_D.LOST_QTY>0.01', N'TIER2', N'PENDING_P3', N'需扩展算子：MOC_GET_D.RETURN_QTY-MOC_GET_D.RETURNED_QTY-MOC_GET_D.LOST_QTY>0.01（不匹配比较链语法）'),
    (N'MOC_BACK_M', N'BACK_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_BACK_M', N'EMP_NAME', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'MOC_BOM_STRU_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_BOM_STRU_M', N'PRODUCE_TYPE', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND NOT EXISTS(SELECT * FROM MOC_BOM_STRU_M WHERE PRODUCE_TYPE=MOC_PRODUCE_M.PRODUCE_TYPE AND PRODUCE_NO=MOC_PRODUCE_M.PRODUCE_NO AND PRO_NO=MOC_PRODUCE_M.PRO_NO)', N'TIER2', N'PENDING_P3', N'需扩展算子：NOT EXISTS(SELECT * FROM MOC_BOM_STRU_M WHERE PRODUCE_TYPE=MOC_PRODUCE_M.PRODUCE_TYPE（不匹配比较链语法）'),
    (N'MOC_GET_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_GET_D', N'DEPOT_ID', 2, N'INV_PRO_DEPOT.PRO_NO=''{d.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 INV_PRO_DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_GET_D', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_GET_D', N'PRO_NO', 2, N'MOC_PRODUCE_M.CONFIRM_TAG=1 and MOC_PRODUCE_M.OUTSIDE_TAG=''{m.OUTSIDE_TAG}'' and MOC_PRODUCE_D.NEED_QTY-MOC_PRODUCE_D.USED_QTY>0 and PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG=1', N'TIER2', N'PENDING_P3', N'需扩展算子：MOC_PRODUCE_D.NEED_QTY-MOC_PRODUCE_D.USED_QTY>0（不匹配比较链语法）'),
    (N'MOC_GET_D', N'PRO_NO_CP', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_GET_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'MOC_GET_M', N'GET_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_GET_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOC_GET_MORE', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_GET_MORE', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND ISNULL(MOC_PRODUCE_D.NEED_QTY-MOC_PRODUCE_D.USED_QTY,0)>0 AND MOC_PRODUCE_M.REWORK_TAG={m.REWORK_TAG} AND MOC_PRODUCE_M.OUTSIDE_TAG={m.OUTSIDE_TAG}', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(MOC_PRODUCE_D.NEED_QTY-MOC_PRODUCE_D.USED_QTY,0)>0（不匹配比较链语法）'),
    (N'MOC_OUT_PRODUCT_IN_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_OUT_PRODUCT_IN_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_OUT_PRODUCT_IN_D', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND ISNULL(MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY,0)>0 AND MOC_PRODUCE_M.CLIENT_ID=''{m.client_id}''', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY,0)>0（不匹配比较链语法）'),
    (N'MOC_OUT_PRODUCT_IN_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'MOC_OUT_PRODUCT_IN_M', N'OUT_PRODUCT_IN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_OUT_PRODUCT_IN_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOC_OUT_PRODUCT_IN_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_OUT_PRODUCT_OUT_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_OUT_PRODUCT_OUT_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_OUT_PRODUCT_OUT_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'MOC_OUT_PRODUCT_OUT_M', N'OUT_PRODUCT_OUT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_OUT_PRODUCT_OUT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOC_OUT_PRODUCT_OUT_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PLAN_D', N'ORDER_NO', 1, N'COP_ORDER_M.CLIENT_ID=''{m.CLIENT_ID}'' and COP_ORDER_M.CONFIRM_TAG=1 and COP_ORDER_D.FINISHED_TAG=0 and COP_ORDER_D.qty>isnull(COP_ORDER_D.do_plan_qty,0) and NOT (PRODUCT.BUSINESS_TAG=1 OR PRODUCT.STOP_TAG=1)', N'TIER2', N'PENDING_P3', N'需扩展算子：NOT (PRODUCT.BUSINESS_TAG=1 OR PRODUCT.STOP_TAG=1)（不匹配比较链语法）'),
    (N'MOC_PLAN_D', N'ORDER_NO', 2, N'COP_ORDER_M.CLIENT_ID=''{m.CLIENT_ID}'' and COP_ORDER_D.PRO_NO=''{m.PRO_NO}'' and COP_ORDER_M.CONFIRM_TAG=1 and COP_ORDER_D.FINISHED_TAG=0 and COP_ORDER_D.qty>isnull(COP_ORDER_D.do_plan_qty,0)', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_ORDER_M.CLIENT_ID EQ={m.CLIENT_ID}；COP_ORDER_D.PRO_NO EQ={m.PRO_NO}；COP_ORDER_M.CONFIRM_TAG EQ=1；COP_ORDER_D.FINISHED_TAG EQ=0；COP_ORDER_D.qty GT=isnull(COP_ORDER_D.do_plan_qty,0)'),
    (N'MOC_PLAN_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PLAN_M', N'PLAN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PLAN_M', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_CHANGE_D', N'PRO_NO', 1, N'MOC_PRODUCE_M.PRODUCE_TYPE=''{m.PRODUCE_TYPE}'' and MOC_PRODUCE_M.PRODUCE_NO=''{m.PRODUCE_NO}'' and MOC_PRODUCE_D.FINISHED_TAG=0', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：MOC_PRODUCE_M.PRODUCE_TYPE EQ={m.PRODUCE_TYPE}；MOC_PRODUCE_M.PRODUCE_NO EQ={m.PRODUCE_NO}；MOC_PRODUCE_D.FINISHED_TAG EQ=0'),
    (N'MOC_PRODUCE_CHANGE_M', N'CHANGE_PRODUCE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_CHANGE_M', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_CHANGE_M', N'PRODUCE_TYPE', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_D', N'CP_PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_D', N'PRO_NO', 2, N'BOM_STRU_D.PRO_NO=''{m.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 BOM_STRU_D 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_IN_M', N'LINE_ID', 2, N'SUPPLIER.OUTER_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_INSTRUCT_M', N'ORDER_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_INSTRUCT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_INSTRUCT_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_INSTRUCT_M', N'PRODUCE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_M', N'LINE_ID', 1, N'LINE.LINE_ID=''SWT-HJI-94'' AND ''{m.PRODUCE_TYPE}''=''ZCML''', N'TIER2', N'PENDING_P3', N'需扩展算子：''{m.PRODUCE_TYPE}''=''ZCML''（不匹配比较链语法）'),
    (N'MOC_PRODUCE_M', N'LINE_ID', 2, N'SUPPLIER.OUTER_TAG=1 and SUPPLIER_PRICE_D.PRO_NO=''{m.PRO_NO}''

LINE.LINE_ID=''SWT-HJI-94'' AND ''{m.PRODUCE_TYPE}''=''ZCML''', N'TIER2', N'PENDING_P3', N'需扩展算子：SUPPLIER_PRICE_D.PRO_NO=''{m.PRO_NO}''

LINE.LINE_ID=''SWT-HJI-94''（不匹配比较链语法）'),
    (N'MOC_PRODUCE_M', N'LINE_ID', 3, N'LINE.LINE_ID<>''SWT-HJI-94'' AND ''{m.PRODUCE_TYPE}''<>''ZCML''', N'TIER2', N'PENDING_P3', N'需扩展算子：''{m.PRODUCE_TYPE}''<>''ZCML''（不匹配比较链语法）'),
    (N'MOC_PRODUCE_M', N'ORDER_TYPE', 1, N'COP_ORDER_M.CONFIRM_TAG=1 and COP_ORDER_M.FINISHED_TAG=0 and COP_ORDER_D.FINISHED_TAG=0 AND COP_ORDER_D.PLAN_QTY>COP_ORDER_D.FINISHED_PLAN_QTY AND COP_ORDER_D.PRO_NO IN (SELECT PRO_NO FROM BOM_STRU_M) AND PRODUCT.MAIN_SOURCE=''2'' AND COP_ORDER_M.ORDER_DATE>=''2014-10-01''', N'TIER2', N'PENDING_P3', N'需扩展算子：COP_ORDER_D.PRO_NO IN (SELECT PRO_NO FROM BOM_STRU_M)（不匹配比较链语法）'),
    (N'MOC_PRODUCE_M', N'ORDER_TYPE', 2, N'SUPPLIER.OUTER_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_M', N'ORDER_TYPE', 3, N'PUR_APPLY_M.CONFIRM_TAG=1 AND PUR_APPLY_D.APPLY_TYPE=''CLQG'' AND PUR_APPLY_D.QTY - PUR_APPLY_D.PURCHASE_QTY>0 AND PRODUCT.MAIN_SOURCE=''2'' AND PRODUCT.PRO_NO IN (SELECT PRO_NO FROM BOM_STRU_M)', N'TIER2', N'PENDING_P3', N'需扩展算子：PUR_APPLY_D.QTY - PUR_APPLY_D.PURCHASE_QTY>0（不匹配比较链语法）'),
    (N'MOC_PRODUCE_M', N'ORDER_TYPE', 4, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.MAIN_SOURCE=''2'' AND PRODUCT.PRO_NO IN (SELECT PRO_NO FROM BOM_STRU_M)', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO IN (SELECT PRO_NO FROM BOM_STRU_M)（不匹配比较链语法）'),
    (N'MOC_PRODUCE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_M', N'PRODUCE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_M', N'TWDEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_M', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'MOC_PRODUCE_PROCESS_D', N'PRO_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND MOC_PRODUCE_M.END_TAG=0 AND MOC_PRODUCE_M.PRODUCE_TYPE=''{m.PRODUCE_TYPE}'' AND MOC_PRODUCE_M.PRODUCE_NO=''{m.PRODUCE_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_PROCESS_D', N'PROCEDURE_ID', 2, N'SFC_PROCESS_D.PRO_NO=''{m.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SFC_PROCESS_D 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_PROCESS_D', N'PROCEDURE_TYPE_ID', 1, N'SFC_PROCEDURE_TYPE.CONTROL=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SFC_PROCEDURE_TYPE 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_PROCESS_D', N'PROCEDURE_TYPE_NAME', 1, N'SFC_PROCEDURE_TYPE.CONTROL=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SFC_PROCEDURE_TYPE 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_PROCESS_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCE_PROCESS_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCE_PROCESS_M', N'PRODUCE_TYPE', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND MOC_PRODUCE_M.END_TAG=0 AND not exists(select * from MOC_PRODUCE_PROCESS_M where PRODUCE_TYPE=MOC_PRODUCE_M.PRODUCE_TYPE and PRODUCE_NO=MOC_PRODUCE_M.PRODUCE_NO)', N'TIER2', N'PENDING_P3', N'需扩展算子：not exists(select * from MOC_PRODUCE_PROCESS_M where PRODUCE_TYPE=MOC_PRODUCE_M.PRODUCE_TYPE（不匹配比较链语法）'),
    (N'MOC_PRODUCT_IN_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCT_IN_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCT_IN_D', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY>0 AND MOC_PRODUCE_M.OUTSIDE_TAG=0 AND ''{m.PRODUCT_IN_TYPE}''=''RK''', N'TIER2', N'PENDING_P3', N'需扩展算子：MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY>0（不匹配比较链语法）'),
    (N'MOC_PRODUCT_IN_D', N'PRODUCE_NO', 2, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY>0 AND MOC_PRODUCE_M.OUTSIDE_TAG=1 AND ''{m.PRODUCT_IN_TYPE}''=''TWRK''', N'TIER2', N'PENDING_P3', N'需扩展算子：MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY>0（不匹配比较链语法）'),
    (N'MOC_PRODUCT_IN_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'MOC_PRODUCT_IN_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCT_IN_M', N'LINE_ID', 2, N'SUPPLIER.OUTER_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCT_IN_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCT_IN_M', N'PRODUCT_IN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCT_OUT_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCT_OUT_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCT_OUT_D', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_QTY>0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCT_OUT_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'MOC_PRODUCT_OUT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCT_OUT_M', N'LINE_ID', 2, N'SUPPLIER.OUTER_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_PRODUCT_OUT_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOC_PRODUCT_OUT_M', N'PRODUCT_OUT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_WORK_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_WORK_D', N'PRODUCE_NO', 1, N'MOC_PRODUCE_PROCESS_M.CONFIRM_TAG=1 AND ISNULL(MOC_PRODUCE_PROCESS_D.PROCESS_QTY-MOC_PRODUCE_PROCESS_D.FINISHED_PLAN_QTY,0)>0 AND MOC_PRODUCE_PROCESS_D.PROCEDURE_TYPE_ID=''{m.PROCEDURE_TYPE_ID}''', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(MOC_PRODUCE_PROCESS_D.PROCESS_QTY-MOC_PRODUCE_PROCESS_D.FINISHED_PLAN_QTY,0)>0（不匹配比较链语法）'),
    (N'MOC_WORK_D', N'SIZE_UNIT_ID', 1, N'UNIT_TYPE=3', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_WORK_IN_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_WORK_IN_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_WORK_IN_D', N'PRODUCE_NO', 1, N'MOC_PRODUCE_PROCESS_M.CONFIRM_TAG=1 AND ISNULL(MOC_PRODUCE_PROCESS_D.PROCESS_QTY-MOC_PRODUCE_PROCESS_D.FINISHED_IN_QTY,0)>0 AND MOC_PRODUCE_PROCESS_D.PROCEDURE_TYPE_ID=''{m.PROCEDURE_TYPE_ID}''', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(MOC_PRODUCE_PROCESS_D.PROCESS_QTY-MOC_PRODUCE_PROCESS_D.FINISHED_IN_QTY,0)>0（不匹配比较链语法）'),
    (N'MOC_WORK_IN_D', N'WORK_NO', 1, N'MOC_WORK_M.CONFIRM_TAG=1 AND ISNULL(MOC_WORK_M.FINISHED_TAG,0)=0 AND ISNULL(MOC_WORK_D.PROCESS_QTY-MOC_WORK_D.FINISHED_IN_QTY,0)>0 AND MOC_WORK_D.PROCEDURE_TYPE_ID=''{m.PROCEDURE_TYPE_ID}''', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(MOC_WORK_D.PROCESS_QTY-MOC_WORK_D.FINISHED_IN_QTY,0)>0（不匹配比较链语法）'),
    (N'MOC_WORK_IN_M', N'LINE_ID', 2, N'SUPPLIER.CONFIRM_TAG=1 AND SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.OUTER_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_WORK_IN_M', N'WORK_IN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_WORK_M', N'LINE_ID', 2, N'SUPPLIER.CONFIRM_TAG=1 AND SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.OUTER_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_WORK_M', N'WORK_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_WORK_OUT_D', N'IN_DEPOT_ID', 1, N'DEPOT.DEPOT_ID <>''{d.OUT_DEPOT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_WORK_OUT_D', N'NEXT_PROCEDURE_TYPE_ID', 1, N'SFC_PROCEDURE_TYPE.PROCEDURE_TYPE_ID<>''{d.PROCEDURE_TYPE_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SFC_PROCEDURE_TYPE 不在 JOIN 白名单内。'),
    (N'MOC_WORK_OUT_D', N'OUT_DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOC_WORK_OUT_D', N'WORK_NO', 1, N'MOC_WORK_M.CONFIRM_TAG=1 AND ISNULL(MOC_WORK_M.FINISHED_TAG,0)=0 AND ISNULL(MOC_WORK_D.FINISHED_IN_QTY-MOC_WORK_D.FINISHED_OUT_QTY,0)>0 AND MOC_WORK_D.PROCEDURE_TYPE_ID=''{m.PROCEDURE_TYPE_ID}''', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(MOC_WORK_D.FINISHED_IN_QTY-MOC_WORK_D.FINISHED_OUT_QTY,0)>0（不匹配比较链语法）'),
    (N'MOC_WORK_OUT_M', N'LINE_ID', 2, N'SUPPLIER.CONFIRM_TAG=1 AND SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.OUTER_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOC_WORK_OUT_M', N'WORK_OUT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MODULES', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'MOU_ACCEPT_M', N'ACCEPT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ACCEPT_M', N'APPLY_NO', 1, N'MOU_APPLY_M.CONFIRM_TAG=1 AND MOU_APPLY_M.CLIENT_ID=''{m.CLIENT_ID}'' AND ISNULL(MOU_APPLY_M.ACCEPT_STATE,'''')=''''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_APPLY_M 不在 JOIN 白名单内。；跨表引用 MOU_APPLY_M 不在 JOIN 白名单内。；跨表引用 MOU_APPLY_M 不在 JOIN 白名单内。'),
    (N'MOU_ACCEPT_M', N'APPLY_TYPE', 1, N'MOU_APPLY_M.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_APPLY_M.CONFIRM_TAG=1 AND MOU_APPLY_M.SAM_TYPE=''1'' AND MOU_APPLY_M.ACCEPT_STATE='''' AND MOU_APPLY_M.APPLY_NO NOT IN (SELECT MOU_ACCEPT_M.APPLY_NO FROM MOU_ACCEPT_M)', N'TIER2', N'PENDING_P3', N'需扩展算子：MOU_APPLY_M.APPLY_NO NOT IN (SELECT MOU_ACCEPT_M.APPLY_NO FROM MOU_ACCEPT_M)（不匹配比较链语法）'),
    (N'MOU_ACCEPT_M', N'APPLY_TYPE', 2, N'MOU_APPLY_M.CONFIRM_TAG=1 AND MOU_APPLY_M.SAM_TYPE=''1'' AND MOU_APPLY_M.ACCEPT_STATE='''' AND MOU_APPLY_M.APPLY_NO NOT IN (SELECT MOU_ACCEPT_M.APPLY_NO FROM MOU_ACCEPT_M)', N'TIER2', N'PENDING_P3', N'需扩展算子：MOU_APPLY_M.APPLY_NO NOT IN (SELECT MOU_ACCEPT_M.APPLY_NO FROM MOU_ACCEPT_M)（不匹配比较链语法）'),
    (N'MOU_ACCEPT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ACCEPT_M', N'ELEMENT_PRO_NO1', 1, N'PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ACCEPT_M', N'ELEMENT_PRO_NO2', 1, N'PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ACCEPT_M', N'ELEMENT_PRO_NO3', 1, N'PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ACCEPT_M', N'MOULD_CUT', 1, N'MOU_MOULD.MOU_SORT=3', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_ACCEPT_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ACCEPTDELE_D', N'ACCEPT_NO', 1, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.CLIENT_ID=''{m.CLIENT_ID}''  AND MOU_ACCEPT_M.ACCEPT_STATE=''报废'' AND MOU_ACCEPT_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'MOU_ACCEPTDELE_D', N'ACCEPT_NO', 2, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.ACCEPT_STATE=''报废'' AND MOU_ACCEPT_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'MOU_ACCEPTDELE_D', N'ACCEPT_NO', 3, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_ACCEPT_M.BATCH_STATE<''{m.BATCH_SORT}'' AND MOU_ACCEPT_M.ACCEPT_STATE=''承认''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'MOU_ACCEPTDELE_D', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ACCEPTDELE_M', N'ACCEPTDELE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ACCEPTDELE_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_APPLY_D', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_APPLY_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_APPLY_M', N'APPLY_NO_OLD', 1, N'MOU_APPLY_M.CONFIRM_TAG=1 AND MOU_APPLY_M.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_APPLY_M.ACCEPT_STATE<>''报废''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_APPLY_M 不在 JOIN 白名单内。；跨表引用 MOU_APPLY_M 不在 JOIN 白名单内。；跨表引用 MOU_APPLY_M 不在 JOIN 白名单内。'),
    (N'MOU_APPLY_M', N'APPLY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_APPLY_M', N'ASSESS_TYPE', 1, N'MOU_ASSESS_M.CLIENT_ID=''{M.CLIENT_ID}'' AND MOU_ASSESS_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。；跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。'),
    (N'MOU_APPLY_M', N'ASSESS_TYPE', 2, N'MOU_ASSESS_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。'),
    (N'MOU_APPLY_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_APPLY_M', N'MOULD_CUT', 1, N'MOU_MOULD.MOU_SORT=3', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_APPLY_M', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_APPLY_M', N'SUPPLIER_ID', 1, N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ASSESS_M', N'ASSESS_TYPE', 1, N'MOU_ASSESS_M.CONFIRM_TAG=1 and MOU_ASSESS_M.PRO_NO=''{m.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。；跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。'),
    (N'MOU_ASSESS_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ASSESS_M', N'CLIENT_NAME', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ASSESS_M', N'MOULD_ID', 1, N'PRODUCT.PRO_TYPE=3', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_ASSESS_M', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.CLIENT_ID=''{M.CLIENT_ID}'' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM MOU_ASSESS_M WHERE MOU_ASSESS_M.ASSESS_NO<>''{m.ASSESS_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM MOU_ASSESS_M WHERE MOU_ASSESS_M.ASSESS_NO<>''{m.ASSESS_NO}'')（不匹配比较链语法）'),
    (N'MOU_BATCH_D', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE>=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCH_M', N'ACCEPT_NO', 1, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.CLIENT_ID=''{m.CLIENT_ID}''  AND MOU_ACCEPT_M.ACCEPT_STATE=''承认'' AND MOU_ACCEPT_M.FINISHED_QTY<MOU_ACCEPT_M.QTY', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCH_M', N'ACCEPT_NO', 2, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.ACCEPT_STATE=''承认'' AND MOU_ACCEPT_M.FINISHED_QTY<MOU_ACCEPT_M.QTY', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCH_M', N'ACCEPT_NO', 3, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_ACCEPT_M.BATCH_STATE<''{m.BATCH_SORT}'' AND MOU_ACCEPT_M.ACCEPT_STATE=''承认''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCH_M', N'ASSESS_TYPE', 1, N'MOU_ASSESS_M.CONFIRM_TAG=1 and MOU_ASSESS_M.PRO_NO=''{m.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。；跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCH_M', N'ASSESS_TYPE', 2, N'MOU_ASSESS_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCH_M', N'BATCH_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCH_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCH_M', N'MOULD_ID', 1, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCH_M', N'MOULD_ID', 2, N'MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_BATCH_M', N'PRO_NO', 1, N'PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''1''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCH_M', N'REQUEST2', 1, N'MOU_ASSESS_M.CONFIRM_TAG=1 and MOU_ASSESS_M.PRO_NO=''{m.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。；跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCH_M', N'REQUEST2', 2, N'MOU_ASSESS_M.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ASSESS_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCH_M', N'SCRAP_NO', 1, N'MOU_SCRAP_M.CONFIRM_TAG=1 AND MOU_SCRAP_D.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_SCRAP_M.SCRAP_NO NOT IN (SELECT SCRAP_NO FROM MOU_BATCH_M WHERE MOU_BATCH_M.BATCH_NO<>''{m.BATCH_NO}'') AND MOU_MOULD.MOU_SORT=1 AND MOU_SCRAP_D.ADD_QTY>0 AND MOU_SCRAP_D.BATCH_STATE=0', N'TIER2', N'PENDING_P3', N'需扩展算子：MOU_SCRAP_M.SCRAP_NO NOT IN (SELECT SCRAP_NO FROM MOU_BATCH_M WHERE MOU_BATCH_M.BATCH_NO<>''{m.BATCH_NO}'')（不匹配比较链语法）'),
    (N'MOU_BATCH_M', N'SUPPLIER_ID', 1, N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCHIN_D', N'BATCH_NO', 1, N'MOU_BATCH_M.CONFIRM_TAG=1 AND MOU_BATCH_M.CLIENT_ID=''{d.CLIENT_ID}'' AND MOU_BATCH_M.QTY>MOU_BATCH_M.FINISHED_QTY', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_BATCH_M 不在 JOIN 白名单内。；跨表引用 MOU_BATCH_M 不在 JOIN 白名单内。；跨表引用 MOU_BATCH_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCHIN_D', N'BATCH_NO', 2, N'MOU_BATCHTOP_M.CONFIRM_TAG=1 AND MOU_BATCHTOP_M.MOU_SORT=''3'' AND MOU_BATCHTOP_M.CLIENT_ID=''{d.CLIENT_ID}'' AND MOU_BATCHTOP_M.BATCH_NO NOT IN (SELECT BATCH_NO FROM MOU_BATCHIN_D WHERE MOU_BATCHIN_D.BATCHIN_NO<>''{m.BATCHIN_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：MOU_BATCHTOP_M.BATCH_NO NOT IN (SELECT BATCH_NO FROM MOU_BATCHIN_D WHERE MOU_BATCHIN_D.BATCHIN_NO<>''{m.BATCHIN_NO}'')（不匹配比较链语法）'),
    (N'MOU_BATCHIN_D', N'BATCH_NO', 3, N'MOU_BATCHTOP_M.CONFIRM_TAG=1 AND MOU_BATCHTOP_M.MOU_SORT=''2'' AND MOU_BATCHTOP_M.CLIENT_ID=''{d.CLIENT_ID}'' AND MOU_BATCHTOP_M.BATCH_NO NOT IN (SELECT BATCH_NO FROM MOU_BATCHIN_D WHERE MOU_BATCHIN_D.BATCHIN_NO<>''{m.BATCHIN_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：MOU_BATCHTOP_M.BATCH_NO NOT IN (SELECT BATCH_NO FROM MOU_BATCHIN_D WHERE MOU_BATCHIN_D.BATCHIN_NO<>''{m.BATCHIN_NO}'')（不匹配比较链语法）'),
    (N'MOU_BATCHIN_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCHIN_D', N'MOULD_ID', 1, N'MOU_MOULD.MOU_SORT=''1'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{d.CLIENT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_BATCHIN_D', N'MOULD_ID', 2, N'MOU_MOULD.MOU_SORT=''2'' AND MOU_MOULD.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_BATCHIN_D', N'MOULD_ID', 3, N'MOU_MOULD.MOU_SORT=''3'' AND MOU_MOULD.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_BATCHIN_M', N'BATCHIN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCHIN_M', N'SUPPLIER_ID', 1, N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCHTOP_D', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE>=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCHTOP_M', N'ACCEPT_NO', 1, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_ACCEPT_M.BATCH_STATE<''{m.BATCH_SORT}'' AND MOU_ACCEPT_M.ACCEPT_STATE=''承认''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCHTOP_M', N'BATCH_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCHTOP_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCHTOP_M', N'MOULD_ID', 1, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'MOU_BATCHTOP_M', N'MOULD_ID', 2, N'MOU_MOULD.CONFIRM_TAG=1 AND MOU_SORT=''{m.MOU_SORT}'' AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_BATCHTOP_M', N'PRO_NO', 1, N'PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''1''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_BATCHTOP_M', N'SCRAP_NO', 1, N'MOU_SCRAP_M.CONFIRM_TAG=1 AND MOU_SCRAP_D.BATCH_STATE=0 AND MOU_SCRAP_D.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_SCRAP_D.SCRAP_NO NOT IN (SELECT SCRAP_NO FROM MOU_BATCHTOP_M WHERE MOU_BATCHTOP_M.BATCH_NO<>''{m.BATCH_NO}'') AND MOU_MOULD.MOU_SORT=''{m.MOU_SORT}'' AND MOU_SCRAP_D.ADD_QTY>0 AND MOU_SCRAP_D.BATCH_STATE=0', N'TIER2', N'PENDING_P3', N'需扩展算子：MOU_SCRAP_D.SCRAP_NO NOT IN (SELECT SCRAP_NO FROM MOU_BATCHTOP_M WHERE MOU_BATCHTOP_M.BATCH_NO<>''{m.BATCH_NO}'')（不匹配比较链语法）'),
    (N'MOU_BATCHTOP_M', N'SUPPLIER_ID', 1, N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG = 1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_GET_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOU_GET_D', N'IN_DEPOT_ID', 1, N'DEPOT.MRP=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOU_GET_D', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE>=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_GET_M', N'GET_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_GET2_D', N'APPLY_NO', 1, N'MOU_APPLY_M.CONFIRM_TAG=1 AND MOU_APPLY_M.APPLY_NO NOT IN (SELECT APPLY_NO FROM MOU_GET2_D WHERE MOU_GET2_D.GET_NO<>''m.GET_NO'')', N'TIER2', N'PENDING_P3', N'需扩展算子：MOU_APPLY_M.APPLY_NO NOT IN (SELECT APPLY_NO FROM MOU_GET2_D WHERE MOU_GET2_D.GET_NO<>''m.GET_NO'')（不匹配比较链语法）'),
    (N'MOU_GET2_D', N'APPLY_NO', 2, N'MOU_BATCH_M.CONFIRM_TAG=1 AND MOU_BATCH_M.BATCH_NO NOT IN (SELECT APPLY_NO FROM MOU_GET2_D WHERE MOU_GET2_D.GET_NO<>''m.GET_NO'')', N'TIER2', N'PENDING_P3', N'需扩展算子：MOU_BATCH_M.BATCH_NO NOT IN (SELECT APPLY_NO FROM MOU_GET2_D WHERE MOU_GET2_D.GET_NO<>''m.GET_NO'')（不匹配比较链语法）'),
    (N'MOU_GET2_D', N'DEPOT_ID', 1, N'DEPOT.MRP=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'MOU_GET2_D', N'PRO_NO', 1, N'INV_PRO_DEPOT.QTY>0.1 AND INV_PRO_DEPOT.DEPOT_ID=''模房用料''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 INV_PRO_DEPOT 不在 JOIN 白名单内。；跨表引用 INV_PRO_DEPOT 不在 JOIN 白名单内。'),
    (N'MOU_GET2_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'MOU_GET2_M', N'GET_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_IN_D', N'MOULD_ID', 1, N'MOU_OUT_M.CONFIRM_TAG=1 AND MOU_OUT_D.QTY-MOU_OUT_D.FINISHED_QTY>0', N'TIER2', N'PENDING_P3', N'需扩展算子：MOU_OUT_D.QTY-MOU_OUT_D.FINISHED_QTY>0（不匹配比较链语法）'),
    (N'MOU_IN_M', N'IN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_MOULD', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_MOULD', N'LINE_ID', 2, N'SUPPLIER.OUTER_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_MOULD', N'PLACE1', 1, N'MOU_APPLY_M.ACCEPT_STATE=''承认'' and MOU_APPLY_M.PRO_NO=''{m.CLIENT_PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_APPLY_M 不在 JOIN 白名单内。；跨表引用 MOU_APPLY_M 不在 JOIN 白名单内。'),
    (N'MOU_MOULD', N'SIZE_UNIT_ID', 1, N'UNIT.UNIT_TYPE = ''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'MOU_OUT_D', N'MOULD_ID', 1, N'MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.USE_TAG=1 AND MOU_MOULD.QTY>0 AND MOU_MOULD.MOU_SORT=''1''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_OUT_D', N'MOULD_ID', 2, N'MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.USE_TAG=1 AND MOU_MOULD.QTY>0 AND MOU_MOULD.MOU_SORT=''2''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_OUT_D', N'MOULD_ID', 3, N'MOU_MOULD.CONFIRM_TAG=1  AND MOU_MOULD.USE_TAG=1 AND MOU_MOULD.QTY>0 AND MOU_MOULD.MOU_SORT=''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_OUT_M', N'OUT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_PRO_D', N'MOULD_ID', 1, N'MOU_MOULD.MOU_SORT=''1'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}'' AND MOU_MOULD.CLIENT_PRO_NO=''{m.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_PRO_D', N'MOULD_ID', 2, N'MOU_MOULD.MOU_SORT=''2'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_PRO_D', N'MOULD_ID', 3, N'MOU_MOULD.MOU_SORT=''3'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_PRO_D', N'MOULD_ID', 4, N'MOU_MOULD.MOU_SORT<>''1'' AND MOU_MOULD.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_MOULD 不在 JOIN 白名单内。；跨表引用 MOU_MOULD 不在 JOIN 白名单内。'),
    (N'MOU_PRO_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_PRO_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''1'' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM MOU_PRO_M WHERE MOU_PRO_M.PRO_NO<>''{m.PRO_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM MOU_PRO_M WHERE MOU_PRO_M.PRO_NO<>''{m.PRO_NO}'')（不匹配比较链语法）'),
    (N'MOU_SCRAP_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'MOU_SCRAP_D', N'MOULD_ID', 1, N'MOU_MOULD.MOU_SORT=''1'' AND MOU_MOULD.CONFIRM_TAG=1 AND MOU_MOULD.USE_TAG=1 AND (MOU_MOULD.QTY>0) AND MOU_MOULD.CLIENT_ID=''{d.CLIENT_ID}''', N'TIER2', N'PENDING_P3', N'需扩展算子：(MOU_MOULD.QTY>0)（不匹配比较链语法）'),
    (N'MOU_SCRAP_D', N'MOULD_ID', 2, N'MOU_MOULD.MOU_SORT=''2'' AND MOU_MOULD.CONFIRM_TAG=1 AND (MOU_MOULD.QTY>0 OR MOU_MOULD.MOU_QTY>0) AND MOU_MOULD.CLIENT_ID=''{d.CLIENT_ID}''', N'TIER2', N'PENDING_P3', N'需扩展算子：(MOU_MOULD.QTY>0 OR MOU_MOULD.MOU_QTY>0)（不匹配比较链语法）'),
    (N'MOU_SCRAP_D', N'MOULD_ID', 3, N'MOU_MOULD.MOU_SORT=''3'' AND MOU_MOULD.CONFIRM_TAG=1  AND (MOU_MOULD.QTY>0) AND MOU_MOULD.CLIENT_ID=''{d.CLIENT_ID}''', N'TIER2', N'PENDING_P3', N'需扩展算子：(MOU_MOULD.QTY>0)（不匹配比较链语法）'),
    (N'MOU_SCRAP_M', N'SCRAP_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PAP_BARCODE_M', N'BARCODE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PAP_BARCODE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PAP_BARCODE_M', N'SEND_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PAP_BRAND', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PAP_GRAMME', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PAP_SPECS', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PAP_TYPE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PAYMENT', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PRICE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PRODUCT', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PRODUCT', N'CUBAGE_UNIT_ID', 1, N'UNIT.UNIT_TYPE = ''5''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'PRODUCT', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PRODUCT', N'ELEMENT_PRO_NO', 1, N'PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'PRODUCT', N'ELEMENT_PRO_NO', 2, N'MOU_ACCEPT_M.CONFIRM_TAG=1 AND MOU_ACCEPT_M.ACCEPT_STATE=''承认'' AND MOU_ACCEPT_M.PRO_NO=''{m.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。；跨表引用 MOU_ACCEPT_M 不在 JOIN 白名单内。'),
    (N'PRODUCT', N'ELEMENT_PRO_NO1', 1, N'PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'PRODUCT', N'ELEMENT_PRO_NO2', 1, N'PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'PRODUCT', N'ELEMENT_PRO_NO3', 1, N'PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'PRODUCT', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PRODUCT', N'PRO_ID', 1, N'CUS_PRODUCT.PRO_TYPE =''1'' AND CUS_PRODUCT.CONFIRM_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。；跨表引用 CUS_PRODUCT 不在 JOIN 白名单内。'),
    (N'PRODUCT', N'SIZE_UNIT_ID', 1, N'UNIT.UNIT_TYPE = ''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'PRODUCT', N'STUFF_ID', 1, N'TYPE_FORMULA.TYPE_ID=''{m.TYPE_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 TYPE_FORMULA 不在 JOIN 白名单内。'),
    (N'PRODUCT', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PRODUCT', N'WEIGHT_UNIT_ID', 1, N'UNIT.UNIT_TYPE=''2''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'PRODUCT_EDITION', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PRODUCT_EDITION', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PRODUCT_EDITION', N'WEIGHT_UNIT_ID', 1, N'UNIT.UNIT_TYPE=''2''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'PUR_APPLY_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_APPLY_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_APPLY_D', N'PRO_NO', 1, N'COP_ORDER_MORE.NEED_QTY>COP_ORDER_MORE.APPLY_QTY AND COP_ORDER_M.CONFIRM_TAG=1 AND COP_ORDER_M.FINISHED_TAG=0 AND ''CLQG''=''{m.APPLY_TYPE}''', N'TIER2', N'PENDING_P3', N'需扩展算子：''CLQG''=''{m.APPLY_TYPE}''（不匹配比较链语法）'),
    (N'PUR_APPLY_D', N'PRO_NO', 2, N'COP_ORDER_M.CONFIRM_TAG=1 and COP_ORDER_M.FINISHED_TAG=0 and COP_ORDER_D.FINISHED_TAG=0 and COP_ORDER_D.PLAN_QTY>COP_ORDER_D.FINISHED_PLAN_QTY AND COP_ORDER_D.PRO_NO NOT IN (SELECT PRO_NO FROM BOM_STRU_M) and ''QG''=''{m.APPLY_TYPE}''', N'TIER2', N'PENDING_P3', N'需扩展算子：COP_ORDER_D.PRO_NO NOT IN (SELECT PRO_NO FROM BOM_STRU_M)（不匹配比较链语法）'),
    (N'PUR_APPLY_D', N'PRO_NO', 3, N'PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.BUSINESS_TAG=0 AND PRODUCT.STOP_TAG=0', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_APPLY_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_APPLY_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'PUR_APPLY_M', N'APPLY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_APPLY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_APPLY_MORE', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_D.APPLY_QTY<MOC_PRODUCE_D.NEED_QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：MOC_PRODUCE_M.CONFIRM_TAG EQ=1；MOC_PRODUCE_D.APPLY_QTY LT=MOC_PRODUCE_D.NEED_QTY'),
    (N'PUR_CALLBACK_D', N'S_R_NO', 1, N'PUR_RECEIVE_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' and PUR_RECEIVE_M.CONFIRM_TAG=1', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：PUR_RECEIVE_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；PUR_RECEIVE_M.CONFIRM_TAG EQ=1'),
    (N'PUR_CALLBACK_D', N'S_R_NO', 2, N'PUR_CANCEL_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' and PUR_CANCEL_M.CONFIRM_TAG=1', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：PUR_CANCEL_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；PUR_CANCEL_M.CONFIRM_TAG EQ=1'),
    (N'PUR_CALLBACK_D', N'SUPPLIER_ID', 1, N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CALLBACK_D', N'UNIT_ID', 2, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'PUR_CALLBACK_M', N'CALLBACK_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CALLBACK_M', N'SUPPLIER_ID', 1, N'SUPPLIER.BUSINESS_TAG=0 AND SUPPLIER.CONFIRM_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CANCEL_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_CANCEL_D', N'PRO_NO', 1, N'PUR_RECEIVE_M.CONFIRM_TAG = 1 AND PUR_RECEIVE_M.SUPPLIER_ID=''{M.SUPPLIER_ID}'' AND ISNULL(PUR_RECEIVE_D.CHECK_RECEIVE_QTY,0)>ISNULL(PUR_RECEIVE_D.CANCEL_QTY,0)', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：PUR_RECEIVE_M.CONFIRM_TAG EQ=1；PUR_RECEIVE_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；PUR_RECEIVE_D.CHECK_RECEIVE_QTY GT[ZERO]=ISNULL(PUR_RECEIVE_D.CANCEL_QTY,0)'),
    (N'PUR_CANCEL_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE>''2''', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CANCEL_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CANCEL_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'PUR_CANCEL_M', N'CANCEL_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CANCEL_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_CANCEL_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_CANCEL_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CHAFFER_D', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CHAFFER_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''4''', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CHAFFER_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CHAFFER_D', N'UNIT_ID', 1, N'UNIT.UNIT_TYPE!=2', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'PUR_CHAFFER_D', N'UNIT_ID', 2, N'UNIT.UNIT_TYPE=''2''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'PUR_CHAFFER_M', N'CHAFFER_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_CHAFFER_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_CHAFFER_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_CHAFFER_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_DUE_D', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.SUPPLIER_ID=''{M.SUPPLIER_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_DUE_D', N'R_C_NO', 1, N'PUR_RECEIVE_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' and PUR_RECEIVE_M.CONFIRM_TAG=1 and PUR_RECEIVE_D.FINISHED_TAG=0', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：PUR_RECEIVE_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；PUR_RECEIVE_M.CONFIRM_TAG EQ=1；PUR_RECEIVE_D.FINISHED_TAG EQ=0'),
    (N'PUR_DUE_D', N'R_C_NO', 2, N'PUR_CANCEL_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' and PUR_CANCEL_M.CONFIRM_TAG=1 and PUR_CANCEL_D.FINISHED_TAG=0', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：PUR_CANCEL_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；PUR_CANCEL_M.CONFIRM_TAG EQ=1；PUR_CANCEL_D.FINISHED_TAG EQ=0'),
    (N'PUR_DUE_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_DUE_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'PUR_DUE_M', N'DUE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_DUE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_DUE_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_MONTH_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PACK_M', N'ORDER_TYPE', 1, N'COP_ORDER_M.CONFIRM_TAG=1 and COP_ORDER_M.FINISHED_TAG=0 and COP_ORDER_D.FINISHED_TAG=0 and COP_ORDER_D.PLAN_QTY>COP_ORDER_D.FINISHED_PLAN_QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：COP_ORDER_M.CONFIRM_TAG EQ=1；COP_ORDER_M.FINISHED_TAG EQ=0；COP_ORDER_D.FINISHED_TAG EQ=0；COP_ORDER_D.PLAN_QTY GT=COP_ORDER_D.FINISHED_PLAN_QTY'),
    (N'PUR_PACK_M', N'PACK_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PACK_M', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PACK_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PAY_D', N'DUE_NO', 1, N'PUR_DUE_M.CONFIRM_TAG=1 and PUR_DUE_M.FINISHED_TAG=0 AND PUR_DUE_M.SUPPLIER_ID=''{m.supplier_id}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 PUR_DUE_M 不在 JOIN 白名单内。；跨表引用 PUR_DUE_M 不在 JOIN 白名单内。；跨表引用 PUR_DUE_M 不在 JOIN 白名单内。'),
    (N'PUR_PAY_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PAY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_PAY_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PAY_OTHER', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_PAY_OTHER', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PAY_PREPAY', N'PREPAY_NO', 1, N'PUR_PREPAY_M.CONFIRM_TAG=1 AND PUR_PREPAY_M.PREPAY_AMOUNT < PUR_PREPAY_M.AMOUNT AND PUR_PREPAY_M.SUPPLIER_ID=''{m.supplier_id}'' AND PUR_PREPAY_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 PUR_PREPAY_M 不在 JOIN 白名单内。；跨表引用 PUR_PREPAY_M 不在 JOIN 白名单内。；跨表引用 PUR_PREPAY_M 不在 JOIN 白名单内。'),
    (N'PUR_PREPAY_D', N'PRO_NO', 1, N'PUR_PURCHASE_M.CONFIRM_TAG = 1 AND PUR_PURCHASE_M.SUPPLIER_ID=''{M.SUPPLIER_ID}'' AND PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0', N'TIER2', N'PENDING_P3', N'需扩展算子：PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0（不匹配比较链语法）'),
    (N'PUR_PREPAY_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PREPAY_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PREPAY_D', N'SUPPLIER_NAME', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PREPAY_M', N'BANK_ID', 1, N'BANK.CURR_ID=''{m.CURR_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 BANK 不在 JOIN 白名单内。'),
    (N'PUR_PREPAY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_PREPAY_M', N'PREPAY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PREPAY_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PREPAY_M', N'SUPPLIER_NAME', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PURCHASE_CHANGE_D', N'PRO_NO', 1, N'PUR_PURCHASE_D.PURCHASE_TYPE=''{m.PURCHASE_TYPE}'' AND PUR_PURCHASE_D.PURCHASE_NO=''{m.PURCHASE_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 PUR_PURCHASE_D 不在 JOIN 白名单内。；跨表引用 PUR_PURCHASE_D 不在 JOIN 白名单内。'),
    (N'PUR_PURCHASE_CHANGE_D', N'PURCHASE_SERIAL_NO', 1, N'PUR_PURCHASE_D.FINISHED_TAG=0 AND PUR_PURCHASE_D.PURCHASE_TYPE=''{m.PURCHASE_TYPE}'' AND PUR_PURCHASE_D.PURCHASE_NO=''{m.PURCHASE_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 PUR_PURCHASE_D 不在 JOIN 白名单内。；跨表引用 PUR_PURCHASE_D 不在 JOIN 白名单内。；跨表引用 PUR_PURCHASE_D 不在 JOIN 白名单内。'),
    (N'PUR_PURCHASE_CHANGE_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PURCHASE_CHANGE_M', N'CHANGE_PURCHASE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PURCHASE_CHANGE_M', N'PURCHASE_TYPE', 1, N'PUR_PURCHASE_M.CONFIRM_TAG = 1 AND PUR_PURCHASE_M.SUPPLIER_ID=''{M.SUPPLIER_ID}'' AND PUR_PURCHASE_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 PUR_PURCHASE_M 不在 JOIN 白名单内。；跨表引用 PUR_PURCHASE_M 不在 JOIN 白名单内。；跨表引用 PUR_PURCHASE_M 不在 JOIN 白名单内。'),
    (N'PUR_PURCHASE_CHANGE_M', N'PURCHASE_TYPE', 2, N'PUR_PURCHASE_M.CONFIRM_TAG = 1 AND PUR_PURCHASE_M.SUPPLIER_ID=''{M.SUPPLIER_ID}'' AND PUR_PURCHASE_M.FINISHED_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 PUR_PURCHASE_M 不在 JOIN 白名单内。；跨表引用 PUR_PURCHASE_M 不在 JOIN 白名单内。；跨表引用 PUR_PURCHASE_M 不在 JOIN 白名单内。'),
    (N'PUR_PURCHASE_CHANGE_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PURCHASE_D', N'APPLY_NO', 1, N'PUR_APPLY_M.CONFIRM_TAG=1 AND ISNULL(PUR_APPLY_D.QTY-PUR_APPLY_D.PURCHASE_QTY,0)>0', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(PUR_APPLY_D.QTY-PUR_APPLY_D.PURCHASE_QTY,0)>0（不匹配比较链语法）'),
    (N'PUR_PURCHASE_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_PURCHASE_D', N'PRICE', 1, N'SUPPLIER_PRICE_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' AND SUPPLIER_PRICE_D.CURR_ID=''{d.CURR_ID}'' AND SUPPLIER_PRICE_D.TAX_ID=''{d.TAX_ID}'' AND SUPPLIER_PRICE_D.TAX_TYPE=''{d.TAX_TYPE}'' AND SUPPLIER_PRICE_D.PRO_NO=''{d.PRO_NO}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：SUPPLIER_PRICE_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；SUPPLIER_PRICE_D.CURR_ID EQ={d.CURR_ID}；SUPPLIER_PRICE_D.TAX_ID EQ={d.TAX_ID}；SUPPLIER_PRICE_D.TAX_TYPE EQ={d.TAX_TYPE}；SUPPLIER_PRICE_D.PRO_NO EQ={d.PRO_NO}'),
    (N'PUR_PURCHASE_D', N'PRO_NO', 1, N'PUR_QUOTE_M.CONFIRM_TAG=1 AND PUR_QUOTE_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' AND PUR_QUOTE_D.CURR_ID=''{d.CURR_ID}'' AND PUR_QUOTE_D.TAX_ID=''{d.TAX_ID}'' AND PUR_QUOTE_D.TAX_TYPE=''{d.TAX_TYPE}'' and PUR_QUOTE_M.IN_EFFECT_DATE>=''{m.PURCHASE_DATE}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：PUR_QUOTE_M.CONFIRM_TAG EQ=1；PUR_QUOTE_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；PUR_QUOTE_D.CURR_ID EQ={d.CURR_ID}；PUR_QUOTE_D.TAX_ID EQ={d.TAX_ID}；PUR_QUOTE_D.TAX_TYPE EQ={d.TAX_TYPE}；PUR_QUOTE_M.IN_EFFECT_DATE GE={m.PURCHASE_DATE}'),
    (N'PUR_PURCHASE_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PURCHASE_D', N'PRO_NO', 3, N'SUPPLIER_PRICE_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' AND SUPPLIER_PRICE_M.CONFIRM_TAG=1 AND SUPPLIER_PRICE_D.CURR_ID=''{d.CURR_ID}'' AND SUPPLIER_PRICE_D.TAX_ID=''{d.TAX_ID}'' AND SUPPLIER_PRICE_D.TAX_TYPE=''{d.TAX_TYPE}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：SUPPLIER_PRICE_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；SUPPLIER_PRICE_M.CONFIRM_TAG EQ=1；SUPPLIER_PRICE_D.CURR_ID EQ={d.CURR_ID}；SUPPLIER_PRICE_D.TAX_ID EQ={d.TAX_ID}；SUPPLIER_PRICE_D.TAX_TYPE EQ={d.TAX_TYPE}'),
    (N'PUR_PURCHASE_D', N'PRO_NO', 4, N'PUR_APPLY_M.CONFIRM_TAG=1 AND PUR_APPLY_D.QTY>PUR_APPLY_D.PURCHASE_QTY AND PUR_APPLY_D.FINISHED_TAG=0 AND PUR_APPLY_D.APPLY_NO NOT IN (SELECT ORDER_NO AS APPLY_NO FROM MOC_PRODUCE_M)', N'TIER2', N'PENDING_P3', N'需扩展算子：PUR_APPLY_D.APPLY_NO NOT IN (SELECT ORDER_NO AS APPLY_NO FROM MOC_PRODUCE_M)（不匹配比较链语法）'),
    (N'PUR_PURCHASE_D', N'QUOTE_NO', 1, N'PUR_QUOTE_M.CONFIRM_TAG=1 AND PUR_QUOTE_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' AND PUR_QUOTE_D.CURR_ID=''{d.CURR_ID}'' AND PUR_QUOTE_D.TAX_ID=''{d.TAX_ID}'' AND PUR_QUOTE_D.TAX_TYPE=''{d.TAX_TYPE}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：PUR_QUOTE_M.CONFIRM_TAG EQ=1；PUR_QUOTE_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；PUR_QUOTE_D.CURR_ID EQ={d.CURR_ID}；PUR_QUOTE_D.TAX_ID EQ={d.TAX_ID}；PUR_QUOTE_D.TAX_TYPE EQ={d.TAX_TYPE}'),
    (N'PUR_PURCHASE_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PURCHASE_D', N'SUPPLIER_NAME', 1, N'SUPPLIER_PRICE_D.PRO_NO =''{d.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SUPPLIER_PRICE_D 不在 JOIN 白名单内。'),
    (N'PUR_PURCHASE_D', N'TWDEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_PURCHASE_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'PUR_PURCHASE_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_PURCHASE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_PURCHASE_M', N'PURCHASE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PURCHASE_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PURCHASE_MORE', N'PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.SUPPLIER_ID=''{M.SUPPLIER_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_PURCHASE_MORE', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_D.PURCHASE_QTY<MOC_PRODUCE_D.NEED_QTY', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：MOC_PRODUCE_M.CONFIRM_TAG EQ=1；MOC_PRODUCE_D.PURCHASE_QTY LT=MOC_PRODUCE_D.NEED_QTY'),
    (N'PUR_QUOTE_D', N'PRO_NO', 1, N'PUR_CHAFFER_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' AND PUR_CHAFFER_M.CONFIRM_TAG=1 AND PUR_CHAFFER_D.CURR_ID=''{d.CURR_ID}'' AND PUR_CHAFFER_D.TAX_ID=''{d.TAX_ID}'' AND PUR_CHAFFER_D.TAX_TYPE=''{d.TAX_TYPE}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：PUR_CHAFFER_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；PUR_CHAFFER_M.CONFIRM_TAG EQ=1；PUR_CHAFFER_D.CURR_ID EQ={d.CURR_ID}；PUR_CHAFFER_D.TAX_ID EQ={d.TAX_ID}；PUR_CHAFFER_D.TAX_TYPE EQ={d.TAX_TYPE}'),
    (N'PUR_QUOTE_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_QUOTE_D', N'PRO_NO', 3, N'SUPPLIER_PRICE_M.SUPPLIER_ID=''{m.SUPPLIER_ID}'' AND SUPPLIER_PRICE_D.CURR_ID=''{d.CURR_ID}'' AND SUPPLIER_PRICE_D.TAX_ID=''{d.TAX_ID}'' AND SUPPLIER_PRICE_D.TAX_TYPE=''{d.TAX_TYPE}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：SUPPLIER_PRICE_M.SUPPLIER_ID EQ={m.SUPPLIER_ID}；SUPPLIER_PRICE_D.CURR_ID EQ={d.CURR_ID}；SUPPLIER_PRICE_D.TAX_ID EQ={d.TAX_ID}；SUPPLIER_PRICE_D.TAX_TYPE EQ={d.TAX_TYPE}'),
    (N'PUR_QUOTE_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_QUOTE_D', N'UNIT_ID', 2, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'PUR_QUOTE_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_QUOTE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_QUOTE_M', N'QUOTE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_QUOTE_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_RECEIVE_D', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_RECEIVE_D', N'PRO_NO', 1, N'ISNULL(PUR_PURCHASE_D.FINISHED_TAG,0) = 0 AND PUR_PURCHASE_M.CONFIRM_TAG = 1 AND PUR_PURCHASE_M.SUPPLIER_ID=''{M.SUPPLIER_ID}'' AND PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0', N'TIER2', N'PENDING_P3', N'需扩展算子：PUR_PURCHASE_D.QTY-PUR_PURCHASE_D.RECEIVE_QTY>0（不匹配比较链语法）'),
    (N'PUR_RECEIVE_D', N'PRO_NO', 2, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.SUPPLIER_ID=''{M.SUPPLIER_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_RECEIVE_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_RECEIVE_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'PUR_RECEIVE_M', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'PUR_RECEIVE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'PUR_RECEIVE_M', N'RECEIVE_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'PUR_RECEIVE_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'QC_ANALYSIS_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'QC_ANALYSIS_D', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.QTY>MOC_PRODUCE_M.FINISHED_ANALYSIS_QTY', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'QC_ANALYSIS_M', N'ANALYSIS_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'QC_APPLY_M', N'APPLY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'QC_APPLY_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'QC_APPLY_M', N'COMPLAIN_NO', 1, N'QC_COMPLAIN_M.CONFIRM_TAG=1 AND QC_COMPLAIN_M.RESULT3=1 AND QC_COMPLAIN_M.COMPLAIN_NO NOT IN (SELECT COMPLAIN_NO FROM QC_APPLY_M WHERE APPLY_NO<>''{d.APPLY_NO}'') AND QC_COMPLAIN_M.CLIENT_ID=''{m.client_id}''', N'TIER2', N'PENDING_P3', N'需扩展算子：QC_COMPLAIN_M.COMPLAIN_NO NOT IN (SELECT COMPLAIN_NO FROM QC_APPLY_M WHERE APPLY_NO<>''{d.APPLY_NO}'')（不匹配比较链语法）'),
    (N'QC_APPLY_M', N'EXCEPTION_NO', 1, N'QC_EXCEPTION_M.CONFIRM_TAG=1 AND QC_EXCEPTION_M.RESULT1=1 AND QC_EXCEPTION_M.EXCEPTION_NO NOT IN (SELECT EXCEPTION_NO FROM QC_APPLY_M WHERE APPLY_NO<>''{d.APPLY_NO}'') AND QC_EXCEPTION_M.CLIENT_ID=''{m.client_id}''', N'TIER2', N'PENDING_P3', N'需扩展算子：QC_EXCEPTION_M.EXCEPTION_NO NOT IN (SELECT EXCEPTION_NO FROM QC_APPLY_M WHERE APPLY_NO<>''{d.APPLY_NO}'')（不匹配比较链语法）'),
    (N'QC_APPLY_M', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND ISNULL(MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY,0)>0 AND MOC_PRODUCE_M.CLIENT_ID=''{m.client_id}''', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY,0)>0（不匹配比较链语法）'),
    (N'QC_COMPLAIN_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'QC_COMPLAIN_M', N'COMPLAIN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'QC_COMPLAIN_M', N'EMP_NAME', 1, N'ISNULL(HR_EMPLOYEE.STATE,0) < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'QC_COMPLAIN_M', N'PRO_NO', 1, N'PRODUCT.CONFIRM_TAG=1 AND PRODUCT.CLIENT_ID=''{m.CLIENT_ID}''', N'TIER1', N'CONVERTED', NULL),
    (N'QC_COMPLAIN_M', N'PRODUCE_NO', 1, N'COP_SEND_M.CONFIRM_TAG=1 AND COP_SEND_D.PRO_NO=''{m.PRO_NO}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 COP_SEND_D 不在 JOIN 白名单内。'),
    (N'QC_EXCEPTION_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'QC_EXCEPTION_M', N'EMP_NAME', 1, N'ISNULL(HR_EMPLOYEE.STATE,0) < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'QC_EXCEPTION_M', N'EXCEPTION_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'QC_EXCEPTION_M', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.CLIENT_ID=''{m.client_id}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'QC_EXCEPTION_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'QC_LOSS_D', N'ANALYSIS_NO', 1, N'QC_ANALYSIS_M.CONFIRM_TAG=1 AND QC_ANALYSIS_D.RESULT_TYPE=''B'' AND QC_ANALYSIS_D.ANALYSIS_NO NOT IN (SELECT ANALYSIS_NO FROM QC_LOSS_D WHERE LOSS_NO<>''{d.LOSS_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：QC_ANALYSIS_D.ANALYSIS_NO NOT IN (SELECT ANALYSIS_NO FROM QC_LOSS_D WHERE LOSS_NO<>''{d.LOSS_NO}'')（不匹配比较链语法）'),
    (N'QC_LOSS_D', N'ELEMENT_PRO_NO', 1, N'PRODUCT.BUSINESS_TAG=0 AND PRODUCT.CONFIRM_TAG = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'QC_LOSS_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'QC_REWORK_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'QC_REWORK_M', N'COMPLAIN_NO', 1, N'QC_COMPLAIN_M.CONFIRM_TAG=1 AND QC_COMPLAIN_M.RESULT1=1 AND QC_COMPLAIN_M.COMPLAIN_NO NOT IN (SELECT COMPLAIN_NO FROM QC_REWORK_M WHERE REWORK_NO<>''{d.REWORK_NO}'') AND QC_COMPLAIN_M.CLIENT_ID=''{m.client_id}''', N'TIER2', N'PENDING_P3', N'需扩展算子：QC_COMPLAIN_M.COMPLAIN_NO NOT IN (SELECT COMPLAIN_NO FROM QC_REWORK_M WHERE REWORK_NO<>''{d.REWORK_NO}'')（不匹配比较链语法）'),
    (N'QC_REWORK_M', N'EXCEPTION_NO', 1, N'QC_EXCEPTION_M.CONFIRM_TAG=1 AND QC_EXCEPTION_M.RESULT2=1 AND QC_EXCEPTION_M.EXCEPTION_NO NOT IN (SELECT EXCEPTION_NO FROM QC_REWORK_M WHERE REWORK_NO<>''{d.REWORK_NO}'') AND QC_EXCEPTION_M.CLIENT_ID=''{m.client_id}''', N'TIER2', N'PENDING_P3', N'需扩展算子：QC_EXCEPTION_M.EXCEPTION_NO NOT IN (SELECT EXCEPTION_NO FROM QC_REWORK_M WHERE REWORK_NO<>''{d.REWORK_NO}'')（不匹配比较链语法）'),
    (N'QC_REWORK_M', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND MOC_PRODUCE_M.CLIENT_ID=''{m.client_id}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'QC_REWORK_M', N'REWORK_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'QC_SAMPLE_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND ISNULL(PRODUCT.PRO_TYPE,'''') <''3'' AND PRODUCT.PRO_NO NOT IN (SELECT QC_SAMPLE_M.PRO_NO FROM QC_SAMPLE_M)', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT QC_SAMPLE_M.PRO_NO FROM QC_SAMPLE_M)（不匹配比较链语法）'),
    (N'QC_SCRAP_D', N'ANALYSIS_NO', 1, N'QC_ANALYSIS_M.CONFIRM_TAG=1 AND QC_ANALYSIS_D.RESULT_TYPE=''A'' AND QC_ANALYSIS_D.ANALYSIS_NO NOT IN (SELECT ANALYSIS_NO FROM QC_SCRAP_D WHERE SCRAP_NO<>''{d.SCRAP_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：QC_ANALYSIS_D.ANALYSIS_NO NOT IN (SELECT ANALYSIS_NO FROM QC_SCRAP_D WHERE SCRAP_NO<>''{d.SCRAP_NO}'')（不匹配比较链语法）'),
    (N'QC_SCRAP_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'QC_SCRAP_D', N'COMPLAIN_NO', 1, N'QC_COMPLAIN_M.CONFIRM_TAG=1 AND QC_COMPLAIN_M.SCRAP_QTY>0 AND QC_COMPLAIN_M.COMPLAIN_NO NOT IN (SELECT COMPLAIN_NO FROM QC_SCRAP_D WHERE SCRAP_NO<>''{d.SCRAP_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：QC_COMPLAIN_M.COMPLAIN_NO NOT IN (SELECT COMPLAIN_NO FROM QC_SCRAP_D WHERE SCRAP_NO<>''{d.SCRAP_NO}'')（不匹配比较链语法）'),
    (N'QC_SCRAP_D', N'EXCEPTION_NO', 1, N'QC_EXCEPTION_M.CONFIRM_TAG=1 AND QC_EXCEPTION_M.RESULT5=1 AND QC_EXCEPTION_M.EXCEPTION_NO NOT IN (SELECT EXCEPTION_NO FROM QC_SCRAP_D WHERE SCRAP_NO<>''{d.SCRAP_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：QC_EXCEPTION_M.EXCEPTION_NO NOT IN (SELECT EXCEPTION_NO FROM QC_SCRAP_D WHERE SCRAP_NO<>''{d.SCRAP_NO}'')（不匹配比较链语法）'),
    (N'QC_SCRAP_D', N'PRODUCE_NO', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND ISNULL(MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY,0)>0 AND MOC_PRODUCE_M.CLIENT_ID=''{d.client_id}''', N'TIER2', N'PENDING_P3', N'需扩展算子：ISNULL(MOC_PRODUCE_M.QTY-MOC_PRODUCE_M.FINISHED_QTY,0)>0（不匹配比较链语法）'),
    (N'QC_SCRAP_D', N'REWORK_NO', 1, N'QC_REWORK_M.CONFIRM_TAG=1 AND QC_REWORK_M.AMERCE4>0 AND QC_REWORK_M.REWORK_NO NOT IN (SELECT REWORK_NO FROM QC_SCRAP_D WHERE SCRAP_NO<>''{d.SCRAP_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：QC_REWORK_M.REWORK_NO NOT IN (SELECT REWORK_NO FROM QC_SCRAP_D WHERE SCRAP_NO<>''{d.SCRAP_NO}'')（不匹配比较链语法）'),
    (N'QC_SCRAP_M', N'SCRAP_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'RECEIVE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'REPORT', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'REPORT', N'Q_M_IDX', 1, N'MODULES.M_IDX NOT IN (SELECT M_P_IDX FROM MODULES WHERE M_P_IDX IS NOT NULL)', N'TIER2', N'PENDING_P3', N'需扩展算子：MODULES.M_IDX NOT IN (SELECT M_P_IDX FROM MODULES WHERE M_P_IDX IS NOT NULL)（不匹配比较链语法）'),
    (N'REPORT', N'R_M_IDX', 1, N'MODULES.M_IDX NOT IN (SELECT M_P_IDX FROM MODULES WHERE M_P_IDX IS NOT NULL)', N'TIER2', N'PENDING_P3', N'需扩展算子：MODULES.M_IDX NOT IN (SELECT M_P_IDX FROM MODULES WHERE M_P_IDX IS NOT NULL)（不匹配比较链语法）'),
    (N'REPORT_FOOTER', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'REPORT_HEADER', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'REPORT_IMAGE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'REPORT_TAIL', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SAM_APPLY_M', N'APPLY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'SAM_APPLY_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'SAM_APPLY_M', N'CURR_ID', 1, N'CURR_NAME=CURR_NAME', N'TIER2', N'PENDING_P3', N'常量/列间条件：CURR_NAME=CURR_NAME（右侧为裸列引用：CURR_NAME）'),
    (N'SAM_APPLY_M', N'PRO_NO', 1, N'PRODUCT.CLIENT_ID=''{m.CLIENT_ID}'' AND PRODUCT.CONFIRM_TAG<>1 AND PRODUCT.BUSINESS_TAG=0', N'TIER1', N'CONVERTED', NULL),
    (N'SAM_IN_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'SAM_IN_M', N'IN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'SAM_OUT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'SAM_OUT_M', N'OUT_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'SAMPLE_PRO', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'SAMPLE_PRO', N'CUBAGE_UNIT_ID', 1, N'UNIT.UNIT_TYPE = ''5''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'SAMPLE_PRO', N'DEPOT_ID', 1, N'DEPOT.MRP=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 DEPOT 不在 JOIN 白名单内。'),
    (N'SAMPLE_PRO', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SAMPLE_PRO', N'SIZE_UNIT_ID', 1, N'UNIT.UNIT_TYPE = ''3''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'SAMPLE_PRO', N'STUFF_ID', 1, N'TYPE_FORMULA.TYPE_ID=''{m.TYPE_ID}''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 TYPE_FORMULA 不在 JOIN 白名单内。'),
    (N'SAMPLE_PRO', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'SAMPLE_PRO', N'WEIGHT_UNIT_ID', 1, N'UNIT.UNIT_TYPE=''2''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'SFC_AMERCE_D', N'EMP_NAME', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'SFC_DAILY_M', N'DAILY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'SFC_DAILY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SFC_PAY_D', N'UNIT_ID', 1, N'UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))', N'TIER2', N'PENDING_P3', N'需扩展算子：UNIT.UNIT_ID in (select UNIT_ID from dbo.f_get_pro_units(''{d.PRO_NO}''))（不匹配比较链语法）'),
    (N'SFC_PAY_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SFC_PAY_M', N'PAY_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'SFC_PLAN_D', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'SFC_PLAN_D', N'PRO_NO', 1, N'MOC_PRODUCE_M.CLIENT_ID=''{d.CLIENT_ID}'' AND MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。；跨表引用 MOC_PRODUCE_M 不在 JOIN 白名单内。'),
    (N'SFC_PLAN_D', N'PRO_NO', 2, N'COP_SHIPMENT_D.CLIENT_ID=''{d.CLIENT_ID}'' AND COP_SHIPMENT_D.FINISHED_TAG=0 AND COP_SHIPMENT_D.SHIPMENT_NO+CAST(COP_SHIPMENT_D.SERIAL_NO AS CHAR(6)) NOT IN (SELECT SHIPMENT_NO+CAST(SHIPMENT_SERIAL_NO AS CHAR(6)) FROM SFC_PLAN_M,SFC_PLAN_D WHERE SFC_PLAN_M.PLAN_TYPE=SFC_PLAN_D.PLAN_TYPE AND SFC_PLAN_M.PLAN_NO=SFC_PLAN_D.PLAN_NO AND SFC_PLAN_D.PLAN_NO<>''m.PLAN_NO'' AND SFC_PLAN_M.SORT_IDX=''{m.SORT_IDX}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：COP_SHIPMENT_D.SHIPMENT_NO+CAST(COP_SHIPMENT_D.SERIAL_NO AS CHAR(6)) NOT IN (SELECT SHIPMENT_NO+CAST(SHIPMENT_SERIAL_NO AS CHAR(6)) FROM SFC_PLAN_M,SFC_PLAN_D WHERE SFC_PLAN_M.PLAN_TYPE=SFC_PLAN_D.PLAN_TYPE（不匹配比较链语法）'),
    (N'SFC_PLAN_M', N'PLAN_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'SFC_PLAN_PROCESS_D', N'EMPLOYEE_ID', 1, N'ISNULL(HR_EMPLOYEE.STATE,0) < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'SFC_PLAN_PROCESS_D', N'PRO_NO', 1, N'SFC_PLAN_M.CONFIRM_TAG = 1 AND SFC_PLAN_D.FINISHED_TAG=0 AND SFC_PLAN_D.PROCEDURE_TYPE_ID =''{m.SORT_IDX}''', N'TIER2', N'PENDING_P3', N'结构合法但无法编译：SFC_PLAN_M.CONFIRM_TAG EQ=1；SFC_PLAN_D.FINISHED_TAG EQ=0；SFC_PLAN_D.PROCEDURE_TYPE_ID EQ={m.SORT_IDX}'),
    (N'SFC_PLAN_PROCESS_M', N'PLAN_PROCESS_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'SFC_PLAN_PROCESS_M', N'TIMETYPE_ID', 1, N'HR_TIMETYPE.TIMETYPE_ID IN (''白班'',''晚班'')', N'TIER2', N'PENDING_P3', N'需扩展算子：HR_TIMETYPE.TIMETYPE_ID IN (''白班'',''晚班'')（不匹配比较链语法）'),
    (N'SFC_PROCEDURE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SFC_PROCEDURE_TYPE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SFC_PROCESS_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'SFC_PROCESS_IN_D', N'EMP_NAME', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'SFC_PROCESS_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SFC_PROCESS_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''1'' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM SFC_PROCESS_M WHERE SFC_PROCESS_M.PRO_NO <> ''{m.PRO_NO}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM SFC_PROCESS_M WHERE SFC_PROCESS_M.PRO_NO <> ''{m.PRO_NO}'')（不匹配比较链语法）'),
    (N'SFC_PRODUCE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SFC_PRODUCE_M', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1', N'TIER1', N'CONVERTED', NULL),
    (N'SFC_PRODUCE_M', N'PRODUCE_TYPE', 1, N'MOC_PRODUCE_M.CONFIRM_TAG=1 AND MOC_PRODUCE_M.FINISHED_TAG=0 AND MOC_PRODUCE_M.END_TAG=0 AND not exists(select * from MOC_PRODUCE_PROCESS_M where PRODUCE_TYPE=MOC_PRODUCE_M.PRODUCE_TYPE and PRODUCE_NO=MOC_PRODUCE_M.PRODUCE_NO)', N'TIER2', N'PENDING_P3', N'需扩展算子：not exists(select * from MOC_PRODUCE_PROCESS_M where PRODUCE_TYPE=MOC_PRODUCE_M.PRODUCE_TYPE（不匹配比较链语法）'),
    (N'SFC_REWORK_D', N'EMP_NAME', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'SORT', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'STUFF', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SUPPLIER', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SUPPLIER_LINKMAN', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'SUPPLIER_PRICE_D', N'PRO_NO', 1, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3'' AND PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM SUPPLIER_PRICE_D WHERE SUPPLIER_PRICE_D.SUPPLIER_ID=''{M.SUPPLIER_ID}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：PRODUCT.PRO_NO NOT IN (SELECT PRO_NO FROM SUPPLIER_PRICE_D WHERE SUPPLIER_PRICE_D.SUPPLIER_ID=''{M.SUPPLIER_ID}'')（不匹配比较链语法）'),
    (N'SUPPLIER_PRICE_D', N'PRO_NO', 2, N'ISNULL(PRODUCT.BUSINESS_TAG,0)=0 AND ISNULL(PRODUCT.CONFIRM_TAG,0) = 1 AND PRODUCT.PRO_TYPE=''3''', N'TIER1', N'CONVERTED', NULL),
    (N'SUPPLIER_PRICE_D', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'SUPPLIER_PRICE_D', N'UNIT_ID', 1, N'UNIT.UNIT_TYPE!=2', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'SUPPLIER_PRICE_D', N'UNIT_ID', 2, N'UNIT.UNIT_TYPE=''2''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 UNIT 不在 JOIN 白名单内。'),
    (N'SUPPLIER_PRICE_M', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SUPPLIER_PRICE_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'SYS_WORK_TASK', N'W_M_IDX', 1, N'ISNULL(MODULES.MODI_URL,'''')<>'''' AND M_TAG=1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MODULES 不在 JOIN 白名单内。'),
    (N'SYS_WORK_TASK', N'WORK_TYPE', 1, N'B_M_IDX={module}', N'TIER1', N'CONVERTED', NULL),
    (N'SYSDD', N'M_DESC', 1, N'MODULES.M_TAG=1 AND MODULES.M_IDX in (SELECT M_IDX FROM dbo.f_get_under_m_idx(''{d.M_IDX}'')) AND MODULES.M_IDX NOT IN(SELECT M_IDX FROM SYSDD WHERE USER_ID=''{m.USER_ID}'') AND MODULES.M_IDX NOT IN(SELECT DISTINCT M_P_IDX FROM MODULES WHERE M_P_IDX IS NOT NULL)', N'TIER2', N'PENDING_P3', N'需扩展算子：MODULES.M_IDX in (SELECT M_IDX FROM dbo.f_get_under_m_idx(''{d.M_IDX}''))（不匹配比较链语法）'),
    (N'SYSDD_REPORT', N'REPORT_ID', 1, N'R_M_IDX={d.M_IDX}', N'TIER1', N'CONVERTED', NULL),
    (N'SYSDG', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SYSDG_USER', N'G_IDX', 1, N'SYSDG.G_IDX NOT IN (SELECT G_IDX FROM SYSDG_USER WHERE USER_ID=''{M.USER_ID}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：SYSDG.G_IDX NOT IN (SELECT G_IDX FROM SYSDG_USER WHERE USER_ID=''{M.USER_ID}'')（不匹配比较链语法）'),
    (N'SYSDG_USER', N'USER_ID', 1, N'SYSDL.USER_ID NOT IN (SELECT USER_ID FROM SYSDG_USER WHERE G_IDX=''{M.G_IDX}'')', N'TIER2', N'PENDING_P3', N'需扩展算子：SYSDL.USER_ID NOT IN (SELECT USER_ID FROM SYSDG_USER WHERE G_IDX=''{M.G_IDX}'')（不匹配比较链语法）'),
    (N'SYSDH', N'M_DESC', 1, N'MODULES.M_TAG=1 AND MODULES.M_IDX in (SELECT M_IDX FROM dbo.f_get_under_m_idx(''{d.M_IDX}'')) AND MODULES.M_IDX NOT IN(SELECT M_IDX FROM SYSDH WHERE G_IDX=''{m.G_IDX}'') AND MODULES.M_IDX NOT IN(SELECT DISTINCT M_P_IDX FROM MODULES WHERE M_P_IDX IS NOT NULL)', N'TIER2', N'PENDING_P3', N'需扩展算子：MODULES.M_IDX in (SELECT M_IDX FROM dbo.f_get_under_m_idx(''{d.M_IDX}''))（不匹配比较链语法）'),
    (N'SYSDL', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SYSDN', N'EMP_ID', 1, N'HR_EMPLOYEE.STATE < 4', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 HR_EMPLOYEE 不在 JOIN 白名单内。'),
    (N'SYSDN', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'SYSQR_DEFAULT', N'F_ID', 1, N'FIELDS.IS_VIRTUAL=0 AND T_ID IN (SELECT MASTER_TABLE FROM MODULES WHERE M_IDX={m.M_IDX} UNION ALL SELECT DETAIL_TABLE FROM MODULES WHERE M_IDX={m.M_IDX})', N'TIER2', N'PENDING_P3', N'需扩展算子：T_ID IN (SELECT MASTER_TABLE FROM MODULES WHERE M_IDX={m.M_IDX} UNION ALL SELECT DETAIL_TABLE FROM MODULES WHERE M_IDX={m.M_IDX})（不匹配比较链语法）'),
    (N'TASK', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'TAX', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'TYPE', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'UNIT', N'OWNER', 1, N'ISNULL(SYSDL.ACTIVE_TAG,0) = 1', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 SYSDL 不在 JOIN 白名单内。'),
    (N'V_COP_ACCOUNT_M', N'CLIENT_ID', 1, N'ISNULL(CLIENT.BUSINESS_TAG,0)=0 AND ISNULL(CLIENT.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'V_PUR_DUE_M', N'SUPPLIER_ID', 1, N'ISNULL(SUPPLIER.BUSINESS_TAG,0)=0 AND ISNULL(SUPPLIER.CONFIRM_TAG,0)=1', N'TIER1', N'CONVERTED', NULL),
    (N'WFFORM', N'WF_M_IDX', 1, N'ISNULL(MODULES.MODI_URL,'''')<>'''' AND ISNULL(MODULES.MASTER_TABLE,'''')<>'''' AND ISNULL(MODULES.DETAIL_TABLE,'''')<>''''', N'TIER3', N'MANUAL', N'试编译失败：跨表引用 MODULES 不在 JOIN 白名单内。；跨表引用 MODULES 不在 JOIN 白名单内。；跨表引用 MODULES 不在 JOIN 白名单内。');

UPDATE l SET STATUS = N'DRIFT'
FROM dbo.CHOOSER_FILTER_MIGRATION_LOG l
WHERE l.TIER = N'TIER1' AND l.STATUS = N'CONVERTED'
  AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS_CHOOSER c
                   WHERE c.T_ID = l.T_ID AND c.F_ID = l.F_ID AND c.SERIAL_NO = l.SERIAL_NO
                     AND c.FILTER_STRUCT IS NOT NULL);

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FIELDS' AND object_id = OBJECT_ID(N'dbo.FIELDS'))
    DROP INDEX IX_FIELDS ON dbo.FIELDS;
ALTER TABLE dbo.FIELDS DROP COLUMN
    CHOOSE_ACTIVE1, CHOOSE_T_ID1, CHOOSE_T_DESC1, CHOOSE_M_IDX1, CHOOSE_FILTER1, CHOOSE_RETURNVAL1,
    CHOOSE_ACTIVE2, CHOOSE_T_ID2, CHOOSE_T_DESC2, CHOOSE_M_IDX2, CHOOSE_FILTER2, CHOOSE_RETURNVAL2,
    CHOOSE_ACTIVE3, CHOOSE_T_ID3, CHOOSE_T_DESC3, CHOOSE_M_IDX3, CHOOSE_FILTER3, CHOOSE_RETURNVAL3,
    CHOOSE_ACTIVE4, CHOOSE_T_ID4, CHOOSE_T_DESC4, CHOOSE_M_IDX4, CHOOSE_FILTER4, CHOOSE_RETURNVAL4;

EXEC(N'ALTER PROCEDURE dbo.P_Change_M_IDX
	(	
		@OLD_IDX int,
		@NEW_IDX int
		)
		
AS
BEGIN

	--更新本节点
	update MODULES  set  M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX
	--更新子节点
	update MODULES  set  M_P_IDX=@NEW_IDX WHERE M_P_IDX=@OLD_IDX
	--更新根节点
	update MODULES  set  M_ROOT_IDX=@NEW_IDX WHERE M_ROOT_IDX=@OLD_IDX
	--更新权限
	update SYSDD  set  M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX
	update SYSDD_REPORT  set  M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX
	update SYSDH  set  M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX
	update SYSDH_REPORT  set  M_IDX=@NEW_IDX WHERE M_IDX=@OLD_IDX
	--更新报表
	update REPORT  set  R_M_IDX=@NEW_IDX WHERE R_M_IDX=@OLD_IDX
	update REPORT  set  Q_M_IDX=@NEW_IDX WHERE Q_M_IDX=@OLD_IDX
	update SYSQR  set  R_M_IDX=@NEW_IDX WHERE R_M_IDX=@OLD_IDX
	--更新字段
	update FIELDS set BROWSE_M_IDX=@NEW_IDX WHERE BROWSE_M_IDX=@OLD_IDX
	--更新字段数据源（ADR-008：FIELDS_CHOOSER.SOURCE_M_IDX 取代 FIELDS.CHOOSE_M_IDX1-4）
	update FIELDS_CHOOSER set SOURCE_M_IDX=@NEW_IDX WHERE SOURCE_M_IDX=@OLD_IDX
	--更新流程表单资料
	update WFFORM set WF_M_IDX=@NEW_IDX WHERE WF_M_IDX=@OLD_IDX
	update WFFORM_FLOW set WF_M_IDX=@NEW_IDX WHERE WF_M_IDX=@OLD_IDX
	--更新流程监视资料
	update WF_MONITOR set WF_M_IDX=@NEW_IDX WHERE WF_M_IDX=@OLD_IDX
	--更新单据性质
	update BILLKIND set B_M_IDX=@NEW_IDX where B_M_IDX=@OLD_IDX
	--更新任务记录
	update TASK set M_IDX=@NEW_IDX where M_IDX=@OLD_IDX

END');

INSERT INTO dbo.WORKBENCH_MODULE_DIRTY (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
SELECT DISTINCT m.M_IDX, 1, N'EOS-MIG', GETDATE()
FROM dbo.MODULES m
WHERE (m.MASTER_TABLE IN (SELECT DISTINCT T_ID FROM dbo.FIELDS_CHOOSER)
       OR ISNULL(m.DETAIL_TABLE,'') IN (SELECT DISTINCT T_ID FROM dbo.FIELDS_CHOOSER))
  AND NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = m.M_IDX);

-- PRINT 表达式不允许子查询（SQL Server 1046），先取标量再输出
DECLARE @CHOOSER_MIGRATED_COUNT INT = (SELECT COUNT(*) FROM dbo.FIELDS_CHOOSER);
PRINT N'[ADR-008] FIELDS_CHOOSER 迁移完成：' + CAST(@CHOOSER_MIGRATED_COUNT AS NVARCHAR(10)) + N' 行数据源。';
