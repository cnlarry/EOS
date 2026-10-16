-- ============================================================================
-- EOS.ERP migration 354: 操作审计 2316 改判为统一工作台**只读**承载
-- ----------------------------------------------------------------------------
-- 背景：2316 在 353 里建成了自定义承载页 `/admin/audit`（页面自带一套取数、搜索与列配置）。
-- 但 `AUDIT_EVENT` 就是一张普通业务形态的表——自增主键 + 一张子表 `AUDIT_FIELD_CHANGE`——
-- 而系统对"一张表 + 一个只读列表"的原生承载方式是统一工作台：搜索、服务端排序、列选择与列宽、
-- 导出、打印版式、字段级/行级权限、字段级变更子表全部由元数据驱动，不必在页面里各写一遍。
-- 定制页只留给"不是表"的面（会话管理、流程设计器、模块分组之类）。
--
-- 本迁移把 2316 改成工作台形态：
--   ① 模块行：`M_URL` 由 `/admin/audit` 改为 `/workbench`（菜单渲染时追加 `/{moduleId}`），
--      登记主表 `AUDIT_EVENT`、子表 `AUDIT_FIELD_CHANGE`，默认排序"最新在前"
--      （`SORT_FIELDS` 走 `NormalizeSort` 白名单：列必须是已登记的非虚拟字段）；
--   ② 字段元数据：`AUDIT_EVENT` 22 列、`AUDIT_FIELD_CHANGE` 5 列。工作台列表、统一表单与
--      关键字搜索（可查询的文本列 + 时间列）都读它——**登记不全就等于界面上看不见**；
--   ③ 默认列：`FIELDS.IS_DEFAULT_FIELDS`（决定列表初始列）与 `SYSQL_DEFAULT`（列选择与
--      "默认查询列"配置面的读数来源）两侧同口径；两处不一致会让"选择列"弹窗与实际渲染对不上；
--   ④ 表目录：两张表登记 `TABLES`，使 2302 表维护能维护它们的字段标签与列宽。
--
-- 写路径仍然封死：**只读名单是代码侧的事**（`EOS.API/appsettings.json` 的
-- `UnifiedFormEditor.ReadOnlyModuleIds`），进名单后新增/修改/删除端点对 2316 一律 404，
-- 与库存余额 1302、批次账 1303 同一道闸——理由也一样：通用表单写入会绕过真正的写入者
-- （那里是库存移动引擎，这里是审计写入器）。
-- 只读名单仍会渲染自定义按钮，故**不得给 2316 配 `MODULE_BUSINESS_ACTION`**：
-- 配一条就等于给审计表开一个可执行的写入口（本脚本末尾以断言守住）。
--
-- 幂等：模块行按当前值判断，字段走 MERGE，默认列与表目录走存在性判断，可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 56210, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.MODULES', N'U') IS NULL
    THROW 56211, N'MODULES 表不存在，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2316)
    THROW 56212, N'模块 2316 不存在，迁移中止（本脚本只改判 353 已登记的操作审计模块）。', 1;

-- 形态前置：要么还没改判（/admin/audit），要么已经改判（/workbench），其它值一概不认——
-- 编号不回收、不复用，被别的东西占用时必须人工介入。
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2316 AND ISNULL(M_URL, N'') NOT IN (N'/admin/audit', N'/workbench'))
    THROW 56213, N'模块 2316 的承载页不是 /admin/audit 或 /workbench，迁移中止。', 1;

IF OBJECT_ID(N'dbo.AUDIT_EVENT', N'U') IS NULL OR OBJECT_ID(N'dbo.AUDIT_FIELD_CHANGE', N'U') IS NULL
    THROW 56214, N'审计表 AUDIT_EVENT / AUDIT_FIELD_CHANGE 不存在，迁移中止。', 1;

BEGIN TRANSACTION;

/* ---------- 1. 模块行：改判为工作台形态 ---------- */
UPDATE dbo.MODULES
   SET M_URL = N'/workbench',
       MASTER_TABLE = N'AUDIT_EVENT',
       DETAIL_TABLE = N'AUDIT_FIELD_CHANGE',
       -- 默认排序：审计要先看最新的。写上主表限定名，与 14997 的既有写法一致
       SORT_FIELDS = N'AUDIT_EVENT.EVENT_ID DESC',
       M_TAG = 1,
       -- 标志列显式给值：NULL 会让读取侧的 GetBoolean 走出未定义分支（L27 同类根因）
       DETAIL_NO_SAVE = 0,     -- 审计事件多数没有字段变更明细，"无明细不可保存"不适用于它
       SEARCH_1 = 0,           -- 不进搜索中心：审计的入口是它自己的列表，多一个搜索面只是噪声
       SEARCH_2 = 0,
       IF_COPY = 0,            -- 审计记录没有"复制"这回事
       AUTO_APPROVE = 0,
       ERROR_NO_SAVE = 0,
       FILTER = NULL,          -- 不加模块级行过滤：审计要看得全
       DETAIL_NO_FIELDS = NULL,
       REMARK = N'操作审计记录（AUDIT_EVENT）的只读承载页：记录由系统写入，界面只浏览与检索，'
              + N'不提供新增/修改/删除入口；字段级变更见子表 AUDIT_FIELD_CHANGE。',
       LAST_UPDATE_BY = N'migration-354',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = 2316;
PRINT N'== 模块 2316 已改判为统一工作台承载（主表 AUDIT_EVENT / 子表 AUDIT_FIELD_CHANGE）==';

/* ---------- 2. 字段元数据 ----------
   列宽（DISPLAY_LENGTH）按内容给：账号/姓名/动作/报障编号这类定长标识 100~160，
   时间列 140 配 DISPLAY_FORMAT（默认会退化成只显示年月日），摘要是长文本给到 300（上限）。
   ITEM_ALIGN 只取 '' | left | center | right（与 2302 字段维护的取值域一致）。*/
DECLARE @fields TABLE (
    T_ID NVARCHAR(200), F_ID NVARCHAR(200), F_DESC NVARCHAR(200), F_TYPE NVARCHAR(200),
    IS_PK BIT, IS_AUTOINC BIT, IS_VISIBLE BIT, IS_QUERY BIT, IS_DEFAULT_FIELDS BIT,
    DISPLAY_LENGTH INT, DISPLAY_FORMAT NVARCHAR(100), ITEM_ALIGN VARCHAR(50),
    VERIFY_INDEX INT, F_REMARK NVARCHAR(1000));

INSERT INTO @fields (T_ID, F_ID, F_DESC, F_TYPE, IS_PK, IS_AUTOINC, IS_VISIBLE, IS_QUERY, IS_DEFAULT_FIELDS,
                     DISPLAY_LENGTH, DISPLAY_FORMAT, ITEM_ALIGN, VERIFY_INDEX, F_REMARK) VALUES
    -- AUDIT_EVENT（主表）：默认列 = 时间 / 操作人 / 模块 / 动作 / 对象 / 结果 / 错误码 / 报障编号 / 摘要
    (N'AUDIT_EVENT', N'EVENT_ID',           N'事件号',     N'bigint',    1, 1, 1, 1, 0,  80, NULL,                  N'right',  1, N'自增主键：记录的身份，按它定位与翻页'),
    (N'AUDIT_EVENT', N'OCCURRED_AT',        N'发生时间',   N'datetime2', 0, 0, 1, 1, 1, 140, N'yyyy-MM-dd HH:mm:ss', N'left',   2, N'服务端本地时间；关键字搜日期可命中这一天的记录'),
    (N'AUDIT_EVENT', N'ACTOR_DISPLAY_NAME', N'操作人姓名', N'nvarchar',   0, 0, 1, 1, 1, 100, NULL,                  N'left',   3, N'写入时的姓名快照；早期记录与系统/测试账号为空，此时看操作人账号'),
    (N'AUDIT_EVENT', N'ACTOR_USER_ID',      N'操作人账号', N'nvarchar',   0, 0, 1, 1, 1, 100, NULL,                  N'left',   4, N'登录账号，也是排查时能拿去连库检索的那个键'),
    (N'AUDIT_EVENT', N'M_IDX',              N'模块号',     N'int',       0, 0, 1, 1, 1, 100, NULL,                  N'right',  5, N'动作归属的模块；系统级事件可以为空'),
    (N'AUDIT_EVENT', N'ACTION',             N'动作',       N'nvarchar',   0, 0, 1, 1, 1, 160, NULL,                  N'left',   6, N'动作码（如 UPDATE / APPROVE / LOG_QUERY）'),
    (N'AUDIT_EVENT', N'RESOURCE_TYPE',      N'对象类型',   N'nvarchar',   0, 0, 1, 1, 0, 120, NULL,                  N'left',   7, NULL),
    (N'AUDIT_EVENT', N'RESOURCE_KEY',       N'对象',       N'nvarchar',   0, 0, 1, 1, 1, 200, NULL,                  N'left',   8, N'被操作对象的键（主键值或资源标识）'),
    (N'AUDIT_EVENT', N'RESULT',             N'结果',       N'tinyint',   0, 0, 1, 1, 1,  60, NULL,                  N'right',  9, N'1 = 成功，0 = 失败'),
    (N'AUDIT_EVENT', N'ERROR_CODE',         N'错误码',     N'nvarchar',   0, 0, 1, 1, 1, 140, NULL,                  N'left',  10, N'失败时的稳定错误码'),
    (N'AUDIT_EVENT', N'CORRELATION_ID',     N'报障编号',   N'nvarchar',   0, 0, 1, 1, 1, 160, NULL,                  N'left',  11, N'与运行日志、前端响应头同一个号，用它串前后端链路'),
    (N'AUDIT_EVENT', N'SUMMARY',            N'摘要',       N'nvarchar',   0, 0, 1, 1, 1, 300, NULL,                  N'left',  12, NULL),
    (N'AUDIT_EVENT', N'TRACE_ID',           N'追踪号',     N'nvarchar',   0, 0, 1, 1, 0, 140, NULL,                  N'left',  13, NULL),
    (N'AUDIT_EVENT', N'CLIENT_IP',          N'客户端 IP',  N'nvarchar',   0, 0, 1, 1, 0, 120, NULL,                  N'left',  14, NULL),
    (N'AUDIT_EVENT', N'ACTOR_TYPE',         N'操作者类型', N'tinyint',   0, 0, 1, 0, 0,  90, NULL,                  N'right', 15, N'区分人、系统任务与代办的助手'),
    (N'AUDIT_EVENT', N'CLIENT_TYPE',        N'客户端类型', N'tinyint',   0, 0, 1, 0, 0,  90, NULL,                  N'right', 16, NULL),
    (N'AUDIT_EVENT', N'DEFINITION_VERSION', N'定义版本',   N'nvarchar',   0, 0, 1, 0, 0, 120, NULL,                  N'left',  17, N'写入时所依据的模块定义版本'),
    (N'AUDIT_EVENT', N'CREATED_DATE',       N'落库时间',   N'datetime2', 0, 0, 1, 0, 0, 140, N'yyyy-MM-dd HH:mm:ss', N'left',  18, NULL),
    (N'AUDIT_EVENT', N'REQUEST_METHOD',     N'请求方法',   N'nvarchar',   0, 0, 0, 0, 0,  90, NULL,                  N'left',  19, N'默认不显示：请求面的事实，需要时在字段设置面打开'),
    (N'AUDIT_EVENT', N'REQUEST_PATH',       N'请求路径',   N'nvarchar',   0, 0, 0, 0, 0, 200, NULL,                  N'left',  20, N'默认不显示'),
    (N'AUDIT_EVENT', N'USER_AGENT',         N'客户端 UA',  N'nvarchar',   0, 0, 0, 0, 0, 200, NULL,                  N'left',  21, N'默认不显示'),
    (N'AUDIT_EVENT', N'DETAIL_JSON',        N'明细快照',   N'nvarchar',   0, 0, 0, 0, 0, 300, NULL,                  N'left',  22, N'写入期的内部载荷，不进界面（默认不显示）'),
    -- AUDIT_FIELD_CHANGE（子表）：默认列 = 字段 / 原值 / 新值
    (N'AUDIT_FIELD_CHANGE', N'FIELD_NAME', N'字段',   N'nvarchar', 0, 0, 1, 1, 1, 160, NULL, N'left',  1, N'被改动的字段标签'),
    (N'AUDIT_FIELD_CHANGE', N'OLD_VALUE',  N'原值',   N'nvarchar', 0, 0, 1, 1, 1, 300, NULL, N'left',  2, NULL),
    (N'AUDIT_FIELD_CHANGE', N'NEW_VALUE',  N'新值',   N'nvarchar', 0, 0, 1, 1, 1, 300, NULL, N'left',  3, NULL),
    (N'AUDIT_FIELD_CHANGE', N'EVENT_ID',   N'事件号', N'bigint',   0, 0, 1, 1, 0,  90, NULL, N'right', 4, N'关联主表 AUDIT_EVENT.EVENT_ID；子表按它取数'),
    (N'AUDIT_FIELD_CHANGE', N'VALUE_HASH', N'值摘要', N'binary',   0, 0, 0, 0, 0, 120, NULL, N'left',  5, N'内部比对用的哈希，不进界面（默认不显示）');

MERGE dbo.FIELDS AS target
USING (SELECT * FROM @fields) AS source
   ON LTRIM(RTRIM(target.T_ID)) = source.T_ID AND LTRIM(RTRIM(target.F_ID)) = source.F_ID
WHEN MATCHED THEN UPDATE SET
    F_DESC = source.F_DESC, F_TYPE = source.F_TYPE, IS_PK = source.IS_PK, IS_AUTOINC = source.IS_AUTOINC,
    IS_VISIBLE = source.IS_VISIBLE, IS_QUERY = source.IS_QUERY, IS_DEFAULT_FIELDS = source.IS_DEFAULT_FIELDS,
    IS_READONLY = 1, IS_COST = 0, IS_SECRECY = 0, IS_VERIFY = 0,
    DISPLAY_LENGTH = source.DISPLAY_LENGTH, DISPLAY_FORMAT = source.DISPLAY_FORMAT,
    HEADER_ALIGN = N'center', ITEM_ALIGN = source.ITEM_ALIGN, VERIFY_INDEX = source.VERIFY_INDEX,
    F_REMARK = source.F_REMARK, LAST_UPDATE_BY = N'migration-354', LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT
    (T_ID, F_ID, F_DESC, F_TYPE, IS_PK, IS_AUTOINC, IS_VISIBLE, IS_QUERY, IS_DEFAULT_FIELDS, IS_READONLY,
     IS_COST, IS_SECRECY, IS_VERIFY, DISPLAY_LENGTH, DISPLAY_FORMAT, HEADER_ALIGN, ITEM_ALIGN, VERIFY_INDEX,
     F_REMARK, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (source.T_ID, source.F_ID, source.F_DESC, source.F_TYPE, source.IS_PK, source.IS_AUTOINC,
            source.IS_VISIBLE, source.IS_QUERY, source.IS_DEFAULT_FIELDS, 1,
            0, 0, 0, source.DISPLAY_LENGTH, source.DISPLAY_FORMAT, N'center', source.ITEM_ALIGN,
            source.VERIFY_INDEX, source.F_REMARK, N'migration-354', SYSDATETIME());

PRINT N'== 已写入审计字段元数据 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行（主表 22 + 子表 5）==';

/* ---------- 3. 默认列：SYSQL_DEFAULT 与 FIELDS.IS_DEFAULT_FIELDS 同口径 ----------
   T_ID = 模块主表，T_ID_R = 目标表；F_IDX 在主表与子表之间连续编号（既有模块就是这个形状）。*/
DECLARE @defaults TABLE (T_ID VARCHAR(50), T_ID_R VARCHAR(50), F_IDX INT, F_ID NVARCHAR(200));
INSERT INTO @defaults (T_ID, T_ID_R, F_IDX, F_ID) VALUES
    (N'AUDIT_EVENT', N'AUDIT_EVENT',        1, N'OCCURRED_AT'),
    (N'AUDIT_EVENT', N'AUDIT_EVENT',        2, N'ACTOR_DISPLAY_NAME'),
    (N'AUDIT_EVENT', N'AUDIT_EVENT',        3, N'ACTOR_USER_ID'),
    (N'AUDIT_EVENT', N'AUDIT_EVENT',        4, N'M_IDX'),
    (N'AUDIT_EVENT', N'AUDIT_EVENT',        5, N'ACTION'),
    (N'AUDIT_EVENT', N'AUDIT_EVENT',        6, N'RESOURCE_KEY'),
    (N'AUDIT_EVENT', N'AUDIT_EVENT',        7, N'RESULT'),
    (N'AUDIT_EVENT', N'AUDIT_EVENT',        8, N'ERROR_CODE'),
    (N'AUDIT_EVENT', N'AUDIT_EVENT',        9, N'CORRELATION_ID'),
    (N'AUDIT_EVENT', N'AUDIT_EVENT',       10, N'SUMMARY'),
    (N'AUDIT_EVENT', N'AUDIT_FIELD_CHANGE', 11, N'FIELD_NAME'),
    (N'AUDIT_EVENT', N'AUDIT_FIELD_CHANGE', 12, N'OLD_VALUE'),
    (N'AUDIT_EVENT', N'AUDIT_FIELD_CHANGE', 13, N'NEW_VALUE');

DELETE d FROM dbo.SYSQL_DEFAULT d
 WHERE d.T_ID = N'AUDIT_EVENT'
   AND NOT EXISTS (SELECT 1 FROM @defaults s WHERE s.T_ID_R = d.T_ID_R AND RTRIM(d.F_ID) = s.F_ID);
PRINT N'== 已清理审计默认列里不再需要的行 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

INSERT INTO dbo.SYSQL_DEFAULT (T_ID, T_ID_R, F_IDX, F_ID)
SELECT s.T_ID, s.T_ID_R, s.F_IDX, s.F_ID
FROM @defaults s
WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT d
                   WHERE d.T_ID = s.T_ID AND d.T_ID_R = s.T_ID_R AND RTRIM(d.F_ID) = s.F_ID);
PRINT N'== 已写入审计默认列 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

UPDATE d
   SET d.F_IDX = s.F_IDX
FROM dbo.SYSQL_DEFAULT d
JOIN @defaults s ON s.T_ID_R = d.T_ID_R AND RTRIM(d.F_ID) = s.F_ID
WHERE d.T_ID = N'AUDIT_EVENT' AND d.F_IDX <> s.F_IDX;

/* ---------- 4. 表目录：让 2302 表维护能维护审计字段的标签与列宽 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE LTRIM(RTRIM(T_ID)) = N'AUDIT_EVENT')
    INSERT INTO dbo.TABLES (T_ID, T_DESC, T_TYPE, T_KIND, T_REMARK, CAN_IMPORT, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (N'AUDIT_EVENT', N'操作审计事件', N'TABLE', N'P',
            N'系统写入的操作审计记录（模块 2316 只读承载）：只读，界面不提供新增/修改/删除。',
            0, N'migration-354', SYSDATETIME());

IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE LTRIM(RTRIM(T_ID)) = N'AUDIT_FIELD_CHANGE')
    INSERT INTO dbo.TABLES (T_ID, T_DESC, T_TYPE, T_KIND, T_REMARK, CAN_IMPORT, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (N'AUDIT_FIELD_CHANGE', N'操作审计字段变更', N'TABLE', N'S',
            N'操作审计的字段级变更明细（主表 AUDIT_EVENT）：只读。',
            0, N'migration-354', SYSDATETIME());

PRINT N'== 已登记审计表目录 AUDIT_EVENT / AUDIT_FIELD_CHANGE ==';

/* ---------- 5. 收口断言 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2316
                 AND M_URL = N'/workbench' AND MASTER_TABLE = N'AUDIT_EVENT'
                 AND DETAIL_TABLE = N'AUDIT_FIELD_CHANGE' AND ISNULL(M_TAG, 1) = 1)
    THROW 56215, N'模块 2316 未就位：承载页 / 主表 / 子表 与工作台形态不符，迁移中止。', 1;

-- 形态判据只有视图一处（迁移 340）：改判之后它必须是 WORKBENCH
IF NOT EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE WHERE M_IDX = 2316 AND NODE_KIND = N'WORKBENCH')
    THROW 56216, N'模块 2316 的节点形态不是 WORKBENCH，迁移中止（工作台模块才装配定义与列表）。', 1;

-- 字段元数据必须齐：缺列等于界面上看不见（列表、表单、关键字搜索都读它）
IF (SELECT COUNT(*) FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = N'AUDIT_EVENT') <> 22
    THROW 56217, N'AUDIT_EVENT 字段元数据不是 22 行，迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = N'AUDIT_FIELD_CHANGE') <> 5
    THROW 56218, N'AUDIT_FIELD_CHANGE 字段元数据不是 5 行，迁移中止。', 1;

-- 默认列两处同口径：不一致时"选择列"弹窗与实际渲染会对不上
IF (SELECT COUNT(*) FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = N'AUDIT_EVENT' AND IS_DEFAULT_FIELDS = 1) <> 10
    THROW 56219, N'AUDIT_EVENT 的默认列不是 10 列（FIELDS.IS_DEFAULT_FIELDS），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'AUDIT_EVENT' AND T_ID_R = N'AUDIT_EVENT') <> 10
    THROW 56220, N'AUDIT_EVENT 的系统默认列不是 10 行（SYSQL_DEFAULT），迁移中止。', 1;

IF (SELECT COUNT(*) FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'AUDIT_EVENT' AND T_ID_R = N'AUDIT_FIELD_CHANGE') <> 3
    THROW 56221, N'AUDIT_FIELD_CHANGE 的系统默认列不是 3 行（SYSQL_DEFAULT），迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE LTRIM(RTRIM(T_ID)) = N'AUDIT_EVENT')
   OR NOT EXISTS (SELECT 1 FROM dbo.TABLES WHERE LTRIM(RTRIM(T_ID)) = N'AUDIT_FIELD_CHANGE')
    THROW 56222, N'审计表未登记进表目录（TABLES），迁移中止。', 1;

-- 审计表不得配任何业务动作（自定义按钮）：只读名单仍会渲染按钮，配一条就是开一个写入口
IF EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX = 2316)
    THROW 56223, N'模块 2316 存在业务动作配置：审计记录不可执行任何动作，请先清掉 MODULE_BUSINESS_ACTION 里的行。', 1;

COMMIT TRANSACTION;

DECLARE @AuditFieldRows INT = (SELECT COUNT(*) FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) IN (N'AUDIT_EVENT', N'AUDIT_FIELD_CHANGE'));
DECLARE @DefaultRows INT = (SELECT COUNT(*) FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'AUDIT_EVENT');
PRINT N'== 收口：模块 2316 已改判为工作台只读承载（/workbench），字段元数据 '
      + CONVERT(NVARCHAR(10), @AuditFieldRows) + N' 行、默认列 ' + CONVERT(NVARCHAR(10), @DefaultRows) + N' 行 ==';
PRINT N'== 提醒：2316 必须在 EOS.API/appsettings.json 的 UnifiedFormEditor.ReadOnlyModuleIds 里'
      + N'（只读名单才是写路径唯一的闸；名单是代码侧配置，请随本次改动一起提交）==';
