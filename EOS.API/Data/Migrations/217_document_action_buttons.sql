-- ============================================================================
-- EOS.ERP migration 218: 自定义按钮（单据操作）的配置列与授权表
-- ----------------------------------------------------------------------------
-- 1) MODULE_BUSINESS_ACTION 增两列：
--      LABEL       按钮标题（既有的 EFFECT_NAME 是"整类效果的命名"，不足以当按钮文案）
--      CONFIRM_TAG 点击后先返回"将会发生什么"、用户确认才执行
--    行的触发源由 EVENT_CODE='MANUAL' 表达：这类行不由任何单据事件顺带执行，
--    只在用户点击按钮时运行；其键由代码内的单据操作注册表把关，
--    发布校验项 document_action_keys_registered 未通过即拒发布。
--
-- 2) 两张授权表 SYSDD_BUTTON / SYSDH_BUTTON（个人 / 组双通道），结构对齐
--    SYSDD_REPORT / SYSDH_REPORT，但语义有一处**有意不同**：
--      · 报表：无 override 行时沿用模块 REPORT_TAG（全开）；
--      · 按钮：无 override 行时**拒绝**（fail-closed）——配好并发布后全场无人可点，
--        管理员显式授权给谁，谁才能点。授权无豁免，管理员同样按名单判定。
--    聚合口径与报表同款：个人行完全覆盖组行；多组布尔取 OR（经 SYSDG_USER 落到人）。
--
-- 幂等：列按 COL_LENGTH 判存在再 ADD；表按 OBJECT_ID 判存在再 CREATE。
-- 回滚：DROP TABLE dbo.SYSDH_BUTTON / dbo.SYSDD_BUTTON；
--       ALTER TABLE dbo.MODULE_BUSINESS_ACTION DROP COLUMN CONFIRM_TAG, LABEL;
--       （列上带默认约束 DF_MODULE_BUSINESS_ACTION_CONFIRM_TAG，删列前先 DROP CONSTRAINT）
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
    THROW 50000, @GUARD_MESSAGE, 1;

/* ---------- 1) 配置列 ---------- */
IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
BEGIN
    ALTER TABLE dbo.MODULE_BUSINESS_ACTION ADD LABEL NVARCHAR(200) NULL;
END

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
BEGIN
    ALTER TABLE dbo.MODULE_BUSINESS_ACTION
        ADD CONFIRM_TAG BIT NOT NULL
            CONSTRAINT DF_MODULE_BUSINESS_ACTION_CONFIRM_TAG DEFAULT (0);
END

/* ---------- 2) 授权表 ---------- */
IF OBJECT_ID(N'dbo.SYSDD_BUTTON', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SYSDD_BUTTON
    (
        USER_ID          NCHAR(10)     NOT NULL,
        M_IDX            INT           NOT NULL,
        BUTTON_KEY       NVARCHAR(50)  NOT NULL,
        ALLOW_TAG        BIT           NOT NULL CONSTRAINT DF_SYSDD_BUTTON_ALLOW_TAG DEFAULT (1),
        REMARK           NVARCHAR(200) NULL,
        CREATE_PERSON    NVARCHAR(50)  NULL,
        CREATE_DATE      DATETIME      NULL,
        LAST_UPDATE_BY   NVARCHAR(50)  NULL,
        LAST_UPDATE_DATE DATETIME      NULL,
        CONSTRAINT PK_SYSDD_BUTTON PRIMARY KEY CLUSTERED (USER_ID ASC, M_IDX ASC, BUTTON_KEY ASC)
    );
END

IF OBJECT_ID(N'dbo.SYSDH_BUTTON', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SYSDH_BUTTON
    (
        G_IDX            NCHAR(10)     NOT NULL,
        M_IDX            INT           NOT NULL,
        BUTTON_KEY       NVARCHAR(50)  NOT NULL,
        ALLOW_TAG        BIT           NOT NULL CONSTRAINT DF_SYSDH_BUTTON_ALLOW_TAG DEFAULT (1),
        REMARK           NVARCHAR(200) NULL,
        CREATE_PERSON    NVARCHAR(50)  NULL,
        CREATE_DATE      DATETIME      NULL,
        LAST_UPDATE_BY   NVARCHAR(50)  NULL,
        LAST_UPDATE_DATE DATETIME      NULL,
        CONSTRAINT PK_SYSDH_BUTTON PRIMARY KEY CLUSTERED (G_IDX ASC, M_IDX ASC, BUTTON_KEY ASC)
    );
END

/* ---------- 收口断言 ---------- */
IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'LABEL') IS NULL
    THROW 52180, N'MODULE_BUSINESS_ACTION.LABEL 未建立，迁移中止。', 1;

IF COL_LENGTH(N'dbo.MODULE_BUSINESS_ACTION', N'CONFIRM_TAG') IS NULL
    THROW 52181, N'MODULE_BUSINESS_ACTION.CONFIRM_TAG 未建立，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM sys.columns c
           WHERE c.object_id = OBJECT_ID(N'dbo.MODULE_BUSINESS_ACTION') AND c.name = N'CONFIRM_TAG'
             AND c.is_nullable = 1)
    THROW 52182, N'CONFIRM_TAG 必须为 NOT NULL DEFAULT 0（缺省即"无需二次确认"），迁移中止。', 1;

IF OBJECT_ID(N'dbo.SYSDD_BUTTON', N'U') IS NULL OR OBJECT_ID(N'dbo.SYSDH_BUTTON', N'U') IS NULL
    THROW 52183, N'按钮授权表未建立，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes i
               WHERE i.object_id = OBJECT_ID(N'dbo.SYSDD_BUTTON') AND i.is_primary_key = 1
                 AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id) = 3)
    THROW 52184, N'SYSDD_BUTTON 主键不是 (USER_ID, M_IDX, BUTTON_KEY) 三列，迁移中止。', 1;

PRINT N'== 已建立自定义按钮的配置列（LABEL / CONFIRM_TAG）与授权表（SYSDD_BUTTON / SYSDH_BUTTON）==';
GO
