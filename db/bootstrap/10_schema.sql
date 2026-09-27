/*
 * EOS 数据库结构
 *
 * 包含表、标量/表值函数、视图，以及应用运行时调用的存储过程。
 * 本脚本只建立结构，不含任何业务数据；元数据种子见 20_metadata.sql。
 *
 * 执行前请先建库并切换到目标库，见 00_create_database.sql。
 */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
GO


------------------------------------------------------------------------------
-- ACC_ACCOUNT
------------------------------------------------------------------------------
CREATE TABLE dbo.[ACC_ACCOUNT] (
    [ACCOUNT_ID] nchar(10) NOT NULL,
    [ACCOUNT_NAME] nvarchar(50) NULL,
    [TYPE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1253579504] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_ACC_ACCOUNT] PRIMARY KEY CLUSTERED ([ACCOUNT_ID])
);
GO

------------------------------------------------------------------------------
-- ACC_ACCOUNT_TYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[ACC_ACCOUNT_TYPE] (
    [TYPE_ID] nchar(10) NOT NULL,
    [TYPE_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1269579561] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_ACC_ACCOUNT_TYPE] PRIMARY KEY CLUSTERED ([TYPE_ID])
);
GO

------------------------------------------------------------------------------
-- ACC_DAILY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[ACC_DAILY_D] (
    [DAILY_TYPE] nchar(10) NOT NULL,
    [DAILY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [CONTENT] nvarchar(100) NULL,
    [ACCOUNT_ID] nchar(10) NOT NULL,
    [QTY] float NULL,
    [AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_ACC_DAILY_D] PRIMARY KEY CLUSTERED ([DAILY_TYPE], [DAILY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- ACC_DAILY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[ACC_DAILY_M] (
    [DAILY_TYPE] nchar(10) NOT NULL,
    [DAILY_NO] nchar(20) NOT NULL,
    [DAILY_DATE] datetime NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1737109279] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1737109279] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_ACC_DAILY_M] PRIMARY KEY CLUSTERED ([DAILY_TYPE], [DAILY_NO])
);
GO

------------------------------------------------------------------------------
-- ACCOUNT_TYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[ACCOUNT_TYPE] (
    [ACCOUNT_TYPE_ID] nchar(10) NOT NULL,
    [ACCOUNT_TYPE_NAME] nvarchar(50) NULL,
    [ACCOUNT_ID] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_ACCOUNT_TYPE] PRIMARY KEY CLUSTERED ([ACCOUNT_TYPE_ID])
);
GO

------------------------------------------------------------------------------
-- ASSISTANT_MESSAGE
------------------------------------------------------------------------------
CREATE TABLE dbo.[ASSISTANT_MESSAGE] (
    [ID] bigint IDENTITY(1,1) NOT NULL,
    [SESSION_ID] bigint NOT NULL,
    [ROLE] tinyint NOT NULL,
    [CONTENT] nvarchar(max) NOT NULL,
    [MODEL_NAME] nvarchar(100) NULL,
    [PROMPT_TOKENS] int NULL,
    [COMPLETION_TOKENS] int NULL,
    [ELAPSED_MS] int NULL,
    [CORRELATION_ID] nvarchar(128) NULL,
    [CREATED_AT] datetime2(3) NOT NULL CONSTRAINT [DF_CREATED_AT_2053634409] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_ASSISTANT_MESSAGE] PRIMARY KEY CLUSTERED ([ID]),
    CONSTRAINT [CK_ASSISTANT_MESSAGE_ROLE] CHECK ([ROLE]=(3) OR [ROLE]=(2) OR [ROLE]=(1))
);
GO
CREATE NONCLUSTERED INDEX [IX_ASSISTANT_MESSAGE_SESSION] ON dbo.[ASSISTANT_MESSAGE] ([SESSION_ID], [ID]);
GO

------------------------------------------------------------------------------
-- ASSISTANT_SESSION
------------------------------------------------------------------------------
CREATE TABLE dbo.[ASSISTANT_SESSION] (
    [ID] bigint IDENTITY(1,1) NOT NULL,
    [USER_ID] nvarchar(50) NOT NULL,
    [TITLE] nvarchar(200) NOT NULL CONSTRAINT [DF_TITLE_1973634124] DEFAULT (N'新对话'),
    [CREATED_AT] datetime2(3) NOT NULL CONSTRAINT [DF_CREATED_AT_1973634124] DEFAULT (sysutcdatetime()),
    [LAST_ACTIVE_AT] datetime2(3) NOT NULL CONSTRAINT [DF_LAST_ACTIVE_AT_1973634124] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_ASSISTANT_SESSION] PRIMARY KEY CLUSTERED ([ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_ASSISTANT_SESSION_USER_ACTIVE] ON dbo.[ASSISTANT_SESSION] ([USER_ID], [LAST_ACTIVE_AT] DESC);
GO

------------------------------------------------------------------------------
-- ATTACHMENT
------------------------------------------------------------------------------
CREATE TABLE dbo.[ATTACHMENT] (
    [ID] bigint IDENTITY(1,1) NOT NULL,
    [MODULE_ID] int NOT NULL,
    [MASTER_TABLE] nvarchar(128) NOT NULL,
    [KEY_VALUES] nvarchar(2000) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [FILE_NAME] nvarchar(255) NOT NULL,
    [CLIENT_FILE_NAME] nvarchar(255) NOT NULL,
    [CONTENT_TYPE] nvarchar(128) NOT NULL,
    [SIZE_BYTES] bigint NOT NULL,
    [SHA256] char(64) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [UPLOADED_BY] nvarchar(64) NOT NULL,
    [UPLOADED_BY_DISPLAY] nvarchar(100) NULL,
    [UPLOADED_AT] datetime2(3) NOT NULL CONSTRAINT [DF_UPLOADED_AT_1365631958] DEFAULT (sysutcdatetime()),
    CONSTRAINT [PK_ATTACHMENT] PRIMARY KEY CLUSTERED ([ID]),
    CONSTRAINT [UQ_ATTACHMENT_SCOPE_SERIAL] UNIQUE ([MODULE_ID], [MASTER_TABLE], [KEY_VALUES], [SERIAL_NO]),
    CONSTRAINT [CK_ATTACHMENT_SIZE] CHECK ([SIZE_BYTES]>(0) AND [SIZE_BYTES]<=(52428800))
);
GO
CREATE NONCLUSTERED INDEX [IX_ATTACHMENT_SCOPE] ON dbo.[ATTACHMENT] ([MODULE_ID], [MASTER_TABLE], [KEY_VALUES]);
GO

------------------------------------------------------------------------------
-- AUDIT_EVENT
------------------------------------------------------------------------------
CREATE TABLE dbo.[AUDIT_EVENT] (
    [EVENT_ID] bigint IDENTITY(1,1) NOT NULL,
    [OCCURRED_AT] datetime2(3) NOT NULL,
    [CORRELATION_ID] nvarchar(64) NOT NULL,
    [ACTOR_USER_ID] nvarchar(20) NOT NULL,
    [ACTOR_TYPE] tinyint NOT NULL,
    [CLIENT_TYPE] tinyint NOT NULL,
    [MODULE_ID] int NULL,
    [RESOURCE_TYPE] nvarchar(50) NOT NULL,
    [RESOURCE_KEY] nvarchar(200) NOT NULL,
    [ACTION] nvarchar(50) NOT NULL,
    [RESULT] tinyint NOT NULL,
    [DEFINITION_VERSION] nvarchar(64) NULL,
    [SUMMARY] nvarchar(1000) NULL,
    [DETAIL_JSON] nvarchar(max) NULL,
    [CREATED_DATE] datetime2(3) NOT NULL,
    CONSTRAINT [PK_AUDIT_EVENT] PRIMARY KEY CLUSTERED ([EVENT_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_AUDIT_EVENT_CORRELATION_ID] ON dbo.[AUDIT_EVENT] ([CORRELATION_ID], [OCCURRED_AT]);
GO
CREATE NONCLUSTERED INDEX [IX_AUDIT_EVENT_MODULE_ACTION] ON dbo.[AUDIT_EVENT] ([MODULE_ID], [ACTION], [OCCURRED_AT]);
GO
CREATE NONCLUSTERED INDEX [IX_AUDIT_EVENT_OCCURRED_AT] ON dbo.[AUDIT_EVENT] ([OCCURRED_AT]);
GO

------------------------------------------------------------------------------
-- AUDIT_FIELD_CHANGE
------------------------------------------------------------------------------
CREATE TABLE dbo.[AUDIT_FIELD_CHANGE] (
    [EVENT_ID] bigint NOT NULL,
    [FIELD_NAME] nvarchar(100) NOT NULL,
    [OLD_VALUE] nvarchar(max) NULL,
    [NEW_VALUE] nvarchar(max) NULL,
    [VALUE_HASH] binary(32) NULL,
    CONSTRAINT [PK_AUDIT_FIELD_CHANGE] PRIMARY KEY CLUSTERED ([EVENT_ID], [FIELD_NAME])
);
GO
CREATE NONCLUSTERED INDEX [IX_AUDIT_FIELD_CHANGE_EVENT] ON dbo.[AUDIT_FIELD_CHANGE] ([EVENT_ID]);
GO

------------------------------------------------------------------------------
-- BANK
------------------------------------------------------------------------------
CREATE TABLE dbo.[BANK] (
    [BANK_ID] nchar(50) NOT NULL,
    [BANK_NAME_CN] nvarchar(100) NULL,
    [BANK_NAME_EN] nvarchar(100) NULL,
    [BANK_ADDR_CN] nvarchar(100) NULL,
    [BANK_ADDR_EN] nvarchar(100) NULL,
    [CURR_ID] nchar(10) NULL,
    [LINK_PERSON] nvarchar(100) NULL,
    [TEL1] nvarchar(100) NULL,
    [TEL2] nvarchar(100) NULL,
    [TEL3] nvarchar(100) NULL,
    [FAX] nvarchar(100) NULL,
    [MOBILE] nvarchar(100) NULL,
    [EMAIL] nvarchar(100) NULL,
    [WWW_ADDR] nvarchar(100) NULL,
    [AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [INIT_AMOUNT] float NULL,
    CONSTRAINT [PK_BANK] PRIMARY KEY CLUSTERED ([BANK_ID])
);
GO

------------------------------------------------------------------------------
-- BILLKIND
------------------------------------------------------------------------------
CREATE TABLE dbo.[BILLKIND] (
    [BILL_CODE] nchar(10) NOT NULL,
    [BILL_NAME] nvarchar(50) NOT NULL,
    [B_M_IDX] int NULL,
    [IS_AUTO] bit NULL,
    [IS_DEFAULT] bit NULL CONSTRAINT [DF_IS_DEFAULT_1301579675] DEFAULT ((0)),
    [USED_BILL_NO] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_BILLKIND] PRIMARY KEY CLUSTERED ([BILL_CODE])
);
GO
CREATE NONCLUSTERED INDEX [IX_BILLKIND] ON dbo.[BILLKIND] ([B_M_IDX]);
GO

------------------------------------------------------------------------------
-- BOM_COST_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[BOM_COST_D] (
    [PRO_NO] nchar(30) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [ELEMENT_PRO_NAME] nvarchar(100) NULL,
    [ELEMENT_PRO_SPEC] nvarchar(100) NULL,
    [ELEMENT_COLOR] nvarchar(50) NULL,
    [ELEMENT_QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [BASE_QTY] int NULL CONSTRAINT [DF_BASE_QTY_1317579732] DEFAULT ((1)),
    [LOST_RATE] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [REMARK] nvarchar(300) NULL,
    [EDITION] nchar(10) NULL,
    CONSTRAINT [PK_BOM_COST_D] PRIMARY KEY CLUSTERED ([PRO_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_BOM_PRICE_D] ON dbo.[BOM_COST_D] ([ELEMENT_PRO_NO]);
GO

------------------------------------------------------------------------------
-- BOM_COST_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[BOM_COST_M] (
    [PRO_NO] nchar(30) NOT NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [COLOR_NAME] nchar(10) NULL,
    [BATCH_QTY] int NULL,
    [UNIT_ID] nchar(10) NULL,
    [EDITION] nchar(10) NULL,
    [ATTENTION] nvarchar(1000) NULL,
    [PLAN_PERSON] nchar(10) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(50) NULL,
    [MATERIAL_SUM] float NULL,
    [SALES_SUM] float NULL,
    [WAGE_SUM] float NULL,
    [PRODUCE_SUM] float NULL,
    [PACK_SUM] float NULL,
    [TRANSIT_SUM] float NULL,
    [SEA_SUM] float NULL,
    [OTHER1_SUM] float NULL,
    [OTHER2_SUM] float NULL,
    [LOST_SUM] float NULL,
    [PRICE_SUM] float NULL,
    [PRICE] float NULL,
    [GAIN_SUM] float NULL,
    [BOX_QTY] float NULL,
    [BOX_SPEC] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1333579789] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_BOM_COST_M] PRIMARY KEY CLUSTERED ([PRO_NO])
);
GO

------------------------------------------------------------------------------
-- BOM_INSTRUCT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[BOM_INSTRUCT_D] (
    [PRO_NO] nchar(30) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [REMARK_1] nvarchar(500) NULL,
    [REMARK_2] nvarchar(500) NULL,
    [REMARK_3] nvarchar(500) NULL,
    [REMARK_4] nvarchar(500) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1349579846] DEFAULT ((1)),
    CONSTRAINT [PK_BOM_INSTRUCT_D] PRIMARY KEY CLUSTERED ([PRO_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- BOM_INSTRUCT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[BOM_INSTRUCT_M] (
    [PRO_NO] nchar(30) NOT NULL,
    [BATCH_QTY] int NULL,
    [EDITION] nchar(10) NULL,
    [ATTENTION] nvarchar(1000) NULL,
    [PLAN_PERSON] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1365579903] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_BOM_INSTRUCT_M] PRIMARY KEY CLUSTERED ([PRO_NO])
);
GO

------------------------------------------------------------------------------
-- BOM_REDEPLOY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[BOM_REDEPLOY_D] (
    [REDEPLOY_TYPE] nchar(10) NOT NULL,
    [REDEPLOY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [PRO_ID] nchar(50) NULL,
    [PRO_ID_OLD] nchar(50) NULL,
    [REMARK] nvarchar(50) NULL,
    CONSTRAINT [PK_BOM_REDEPLOY_D] PRIMARY KEY CLUSTERED ([REDEPLOY_TYPE], [REDEPLOY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- BOM_REDEPLOY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[BOM_REDEPLOY_M] (
    [REDEPLOY_TYPE] nchar(10) NOT NULL,
    [REDEPLOY_NO] nchar(20) NOT NULL,
    [REDEPLOY_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [PRO_ID] nchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1397580017] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_BOM_REDEPLOY_M] PRIMARY KEY CLUSTERED ([REDEPLOY_TYPE], [REDEPLOY_NO])
);
GO

------------------------------------------------------------------------------
-- BOM_STRU_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[BOM_STRU_D] (
    [PRO_NO] nchar(30) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [ELEMENT_QTY] float NULL,
    [BASE_QTY] int NULL CONSTRAINT [DF_BASE_QTY_1413580074] DEFAULT ((1)),
    [LOST_RATE] float NULL,
    [REMARK] nvarchar(300) NULL,
    [EDITION] nchar(10) NULL,
    CONSTRAINT [PK_BOM_STRU_D] PRIMARY KEY CLUSTERED ([PRO_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_BOM_STRU_D] ON dbo.[BOM_STRU_D] ([PRO_NO], [ELEMENT_PRO_NO]);
GO

------------------------------------------------------------------------------
-- BOM_STRU_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[BOM_STRU_M] (
    [PRO_NO] nchar(30) NOT NULL,
    [BATCH_QTY] int NULL,
    [EDITION] nchar(10) NULL,
    [ATTENTION] nvarchar(1000) NULL,
    [PLAN_PERSON] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1429580131] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [P_LENGTH_OLD] float NULL,
    [P_WIDTH_OLD] float NULL,
    CONSTRAINT [PK_BOM_STRU_M] PRIMARY KEY CLUSTERED ([PRO_NO])
);
GO

------------------------------------------------------------------------------
-- CAR
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR] (
    [CAR_ID] nchar(20) NOT NULL,
    [CAR_NAME] nvarchar(50) NULL,
    [ENGINE_NO] nvarchar(50) NULL,
    [CARRIAGE_NO] nvarchar(50) NULL,
    [CERTIFICATE_DATE] datetime NULL,
    [CHARGE_PERSON] nchar(10) NULL,
    [START_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [EXAMINE_YEAR] nvarchar(50) NULL,
    [EXAMINE_QUARTER] nvarchar(50) NULL,
    [ROAD_FEE] nvarchar(50) NULL,
    [MAINTAIN] nvarchar(50) NULL,
    [INSURANCE_NO_1] nvarchar(50) NULL,
    [INSURANCE_CORP_1] nvarchar(100) NULL,
    [INSURANCE_SALEMAN_1] nvarchar(50) NULL,
    [INSURANCE_START_DATE_1] datetime NULL,
    [INSURANCE_END_DATE_1] datetime NULL,
    [INSURANCE_TEL_1] nvarchar(50) NULL,
    [INSURANCE_FAX_1] nvarchar(50) NULL,
    [INSURANCE_NO_2] nvarchar(50) NULL,
    [INSURANCE_CORP_2] nvarchar(100) NULL,
    [INSURANCE_SALEMAN_2] nvarchar(50) NULL,
    [INSURANCE_START_DATE_2] datetime NULL,
    [INSURANCE_END_DATE_2] datetime NULL,
    [INSURANCE_TEL_2] nvarchar(50) NULL,
    [INSURANCE_FAX_2] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1445580188] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [OIL_MAINTAIN] float NULL CONSTRAINT [DF_OIL_MAINTAIN_1445580188] DEFAULT ((0)),
    [LAST_MILEAGE] float NULL CONSTRAINT [DF_LAST_MILEAGE_1445580188] DEFAULT ((0)),
    [OIL_CARD] nchar(30) NULL,
    [LAST_OIL_MILEAGE] float NULL CONSTRAINT [DF_LAST_OIL_MILEAGE_1445580188] DEFAULT ((0)),
    CONSTRAINT [PK_CAR] PRIMARY KEY CLUSTERED ([CAR_ID])
);
GO

------------------------------------------------------------------------------
-- CAR_ADDUP_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_ADDUP_D] (
    [ADDUP_TYPE] nchar(10) NOT NULL,
    [ADDUP_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MISSION_TYPE] nchar(10) NULL,
    [MISSION_NO] nchar(20) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [LEAVE_TIME] nvarchar(50) NULL,
    [MISSION_ODOMETER] float NULL CONSTRAINT [DF_MISSION_ODOMETER_1461580245] DEFAULT ((0)),
    [BACK_TIME] nvarchar(50) NULL,
    [BACK_ODOMETER] float NULL CONSTRAINT [DF_BACK_ODOMETER_1461580245] DEFAULT ((0)),
    [RUN_METER] float NULL CONSTRAINT [DF_RUN_METER_1461580245] DEFAULT ((0)),
    [EAT_FEE] float NULL CONSTRAINT [DF_EAT_FEE_1461580245] DEFAULT ((0)),
    [LOAD_FEE] float NULL CONSTRAINT [DF_LOAD_FEE_1461580245] DEFAULT ((0)),
    [OUT_FACTORY_TIME] nvarchar(50) NULL,
    [IN_FACTORY_TIME] nvarchar(50) NULL,
    [REMARK] nvarchar(50) NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1461580245] DEFAULT ((0)),
    CONSTRAINT [PK_CAR_ADDUP_D] PRIMARY KEY CLUSTERED ([ADDUP_TYPE], [ADDUP_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CAR_ADDUP_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_ADDUP_M] (
    [ADDUP_TYPE] nchar(10) NOT NULL,
    [ADDUP_NO] nchar(20) NOT NULL,
    [ADDUP_DATE] datetime NULL,
    [CAR_ID] nchar(20) NULL,
    [MOTORMAN] nchar(10) NULL,
    [MOTORMAN_NAME] nchar(10) NULL,
    [FOLLOW1] nchar(10) NULL,
    [FOLLOW1_NAME] nchar(10) NULL,
    [FOLLOW2] nchar(10) NULL,
    [FOLLOW2_NAME] nchar(10) NULL,
    [LEAVE_TIME] nvarchar(50) NULL,
    [MISSION_ODOMETER] float NULL CONSTRAINT [DF_MISSION_ODOMETER_1477580302] DEFAULT ((0)),
    [BACK_TIME] nvarchar(50) NULL,
    [BACK_ODOMETER] float NULL CONSTRAINT [DF_BACK_ODOMETER_1477580302] DEFAULT ((0)),
    [RUN_METER] float NULL CONSTRAINT [DF_RUN_METER_1477580302] DEFAULT ((0)),
    [SPEND_MONEY] float NULL CONSTRAINT [DF_SPEND_MONEY_1477580302] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1477580302] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1477580302] DEFAULT ((0)),
    CONSTRAINT [PK_CAR_ADDUP_M] PRIMARY KEY CLUSTERED ([ADDUP_TYPE], [ADDUP_NO])
);
GO

------------------------------------------------------------------------------
-- CAR_FEE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_FEE_D] (
    [FEE_TYPE] nchar(10) NOT NULL,
    [FEE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [OILCARD_ID] nchar(30) NULL,
    [CAR_ID] nchar(20) NULL,
    [LAST_AMOUNT] float NULL,
    [AMOUNT] float NULL,
    [TOTAL_AMOUNT] float NULL,
    [REMARK] nvarchar(50) NULL,
    CONSTRAINT [PK_CAR_FEE_D] PRIMARY KEY CLUSTERED ([FEE_TYPE], [FEE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CAR_FEE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_FEE_M] (
    [FEE_TYPE] nchar(10) NOT NULL,
    [FEE_NO] nchar(20) NOT NULL,
    [FEE_DATE] datetime NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1509580416] DEFAULT ((0)),
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1509580416] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CAR_FEE_M] PRIMARY KEY CLUSTERED ([FEE_TYPE], [FEE_NO])
);
GO

------------------------------------------------------------------------------
-- CAR_FILLOIL_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_FILLOIL_M] (
    [FILLOIL_TYPE] nchar(10) NOT NULL,
    [FILLOIL_NO] nchar(20) NOT NULL,
    [FILLOIL_DATE] datetime NULL,
    [CAR_ID] nchar(20) NULL,
    [MILEAGE] float NULL,
    [FILL_QTY] float NULL,
    [PRICE] float NULL,
    [FILL_AMOUNT] float NULL,
    [FILLMAN] nchar(20) NULL,
    [MOTORMAN] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1525580473] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FILLMAN_NAME] nchar(10) NULL,
    [MOTORMAN_NAME] nchar(10) NULL,
    [OIL_MAINTAIN] float NULL CONSTRAINT [DF_OIL_MAINTAIN_1525580473] DEFAULT ((0)),
    [OIL_MILEAGE] float NULL CONSTRAINT [DF_OIL_MILEAGE_1525580473] DEFAULT ((0)),
    [LAST_MILEAGE] float NULL CONSTRAINT [DF_LAST_MILEAGE_1525580473] DEFAULT ((0)),
    [NOW_LAST_MILEAGE] float NULL CONSTRAINT [DF_NOW_LAST_MILEAGE_1525580473] DEFAULT ((0)),
    [LAST_OIL_MILEAGE] float NULL CONSTRAINT [DF_LAST_OIL_MILEAGE_1525580473] DEFAULT ((0)),
    [NOW_OIL_MILEAGE] float NULL CONSTRAINT [DF_NOW_OIL_MILEAGE_1525580473] DEFAULT ((0)),
    [NOW_LAST_OIL_MILEAGE] float NULL CONSTRAINT [DF_NOW_LAST_OIL_MILEAGE_1525580473] DEFAULT ((0)),
    [LAST_OIL] float NULL CONSTRAINT [DF_LAST_OIL_1525580473] DEFAULT ((0)),
    [NOW_OIL] float NULL CONSTRAINT [DF_NOW_OIL_1525580473] DEFAULT ((0)),
    [NOW_LAST_OIL] float NULL CONSTRAINT [DF_NOW_LAST_OIL_1525580473] DEFAULT ((0)),
    [OIL_CARD] nchar(30) NULL,
    [LAST_OIL_AMOUNT] float NULL CONSTRAINT [DF_LAST_OIL_AMOUNT_1525580473] DEFAULT ((0)),
    [NOW_OIL_AMOUNT] float NULL CONSTRAINT [DF_NOW_OIL_AMOUNT_1525580473] DEFAULT ((0)),
    [NOW_LAST_OIL_AMOUNT] float NULL CONSTRAINT [DF_NOW_LAST_OIL_AMOUNT_1525580473] DEFAULT ((0)),
    CONSTRAINT [PK_CAR_FILLOIL_M] PRIMARY KEY CLUSTERED ([FILLOIL_TYPE], [FILLOIL_NO])
);
GO

------------------------------------------------------------------------------
-- CAR_MISSION_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_MISSION_D] (
    [MISSION_TYPE] nchar(10) NOT NULL,
    [MISSION_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [SEND_TYPE] nchar(10) NULL,
    [SEND_NO] nchar(20) NULL,
    [CUST_ID] nchar(10) NULL,
    [PIECE] float NULL,
    [SIGN_IN] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_CAR_MISSION_D] PRIMARY KEY CLUSTERED ([MISSION_TYPE], [MISSION_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IDX_CAR_MISSION_DETAIL_1] ON dbo.[CAR_MISSION_D] ([MISSION_TYPE], [MISSION_NO]);
GO

------------------------------------------------------------------------------
-- CAR_MISSION_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_MISSION_M] (
    [MISSION_TYPE] nchar(10) NOT NULL,
    [MISSION_NO] nchar(20) NOT NULL,
    [MISSION_DATE] datetime NULL,
    [CAR_ID] nchar(20) NULL,
    [MOTORMAN] nchar(10) NULL,
    [FOLLOW1] nchar(10) NULL,
    [FOLLOW2] nchar(10) NULL,
    [LEAVE_TIME] nvarchar(50) NULL,
    [MISSION_ODOMETER] float NULL CONSTRAINT [DF_MISSION_ODOMETER_1557580587] DEFAULT ((0)),
    [BACK_TIME] nvarchar(50) NULL,
    [BACK_ODOMETER] float NULL CONSTRAINT [DF_BACK_ODOMETER_1557580587] DEFAULT ((0)),
    [RUN_METER] float NULL CONSTRAINT [DF_RUN_METER_1557580587] DEFAULT ((0)),
    [SPEND_MONEY] float NULL CONSTRAINT [DF_SPEND_MONEY_1557580587] DEFAULT ((0)),
    [OUT_FACTORY_TIME] nvarchar(50) NULL,
    [IN_FACTORY_TIME] nvarchar(50) NULL,
    [GUARD] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1557580587] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [MOTORMAN_NAME] nchar(10) NULL,
    [FOLLOW1_NAME] nchar(10) NULL,
    [FOLLOW2_NAME] nchar(10) NULL,
    [GUARD_NAME] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1557580587] DEFAULT ((0)),
    CONSTRAINT [PK_CAR_MISSION_M] PRIMARY KEY CLUSTERED ([MISSION_TYPE], [MISSION_NO])
);
GO

------------------------------------------------------------------------------
-- CAR_OILCARD
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_OILCARD] (
    [OILCARD_ID] nchar(30) NOT NULL,
    [OILCARD_NAME] nvarchar(50) NULL,
    [OILCARD_COMPANY] nvarchar(50) NULL,
    [CAR_ID] nchar(20) NULL,
    [LAST_AMOUNT] float NULL,
    [LAST_FEE_DATE] datetime NULL,
    [LAST_OIL_DATE] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1573580644] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CAR_OILCARD] PRIMARY KEY CLUSTERED ([OILCARD_ID])
);
GO

------------------------------------------------------------------------------
-- CAR_REPAIR_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_REPAIR_D] (
    [REPAIR_TYPE] nchar(10) NOT NULL,
    [REPAIR_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ITEM] nvarchar(200) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_CAR_REPAIR_D] PRIMARY KEY CLUSTERED ([REPAIR_TYPE], [REPAIR_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CAR_REPAIR_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_REPAIR_M] (
    [REPAIR_TYPE] nchar(10) NOT NULL,
    [REPAIR_NO] nchar(20) NOT NULL,
    [REPAIR_DATE] datetime NULL,
    [CAR_ID] nchar(20) NULL,
    [MOTORMAN] nchar(10) NULL,
    [MILEAGE] float NULL CONSTRAINT [DF_MILEAGE_1605580758] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1605580758] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1605580758] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [REPAIR_EMP_ID] nchar(10) NULL,
    [REPAIR_EMP_NAME] nvarchar(50) NULL,
    CONSTRAINT [PK_CAR_REPAIR_M] PRIMARY KEY CLUSTERED ([REPAIR_TYPE], [REPAIR_NO])
);
GO

------------------------------------------------------------------------------
-- CAR_TAKEOUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_TAKEOUT_D] (
    [TAKEOUT_TYPE] nchar(10) NOT NULL,
    [TAKEOUT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NAME] nvarchar(50) NULL,
    [QTY] float NULL,
    [BOX_NUM] float NULL,
    [REMARK] nvarchar(50) NULL,
    [UNIT_ID] nchar(10) NULL,
    CONSTRAINT [PK_CAR_TAKEOUT_D] PRIMARY KEY CLUSTERED ([TAKEOUT_TYPE], [TAKEOUT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CAR_TAKEOUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CAR_TAKEOUT_M] (
    [TAKEOUT_TYPE] nchar(10) NOT NULL,
    [TAKEOUT_NO] nchar(20) NOT NULL,
    [TAKEOUT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [ADDRESS] nvarchar(50) NULL,
    [CAR_ID] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1637580872] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1637580872] DEFAULT ((0)),
    CONSTRAINT [PK_CAR_TAKEOUT_M] PRIMARY KEY CLUSTERED ([TAKEOUT_TYPE], [TAKEOUT_NO])
);
GO

------------------------------------------------------------------------------
-- CHOOSER_FILTER_MIGRATION_LOG
------------------------------------------------------------------------------
CREATE TABLE dbo.[CHOOSER_FILTER_MIGRATION_LOG] (
    [ID] int IDENTITY(1,1) NOT NULL,
    [T_ID] nvarchar(100) NOT NULL,
    [F_ID] nvarchar(100) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [LEGACY_FILTER] nvarchar(1000) NULL,
    [TIER] nvarchar(20) NOT NULL,
    [STATUS] nvarchar(20) NOT NULL,
    [ERROR] nvarchar(500) NULL,
    [CREATED_AT] datetime NOT NULL CONSTRAINT [DF_CREATED_AT_354152357] DEFAULT (getdate()),
    CONSTRAINT [PK_CHOOSER_FILTER_MIGRATION_LOG] PRIMARY KEY CLUSTERED ([ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_CHOOSER_FILTER_LOG_FIELD] ON dbo.[CHOOSER_FILTER_MIGRATION_LOG] ([T_ID], [F_ID], [SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- CLIENT
------------------------------------------------------------------------------
CREATE TABLE dbo.[CLIENT] (
    [CLIENT_ID] nchar(10) NOT NULL,
    [CLIENT_NAME] nvarchar(50) NULL,
    [FULL_NAME_CN] nvarchar(100) NULL,
    [FULL_NAME_EN] nvarchar(100) NULL,
    [REG_ADDR_CN] nvarchar(200) NULL,
    [REG_ADDR_EN] nvarchar(200) NULL,
    [DELI_ADDR_CN] nvarchar(200) NULL,
    [DELI_ADDR_EN] nvarchar(200) NULL,
    [INV_ADDR_CN] nvarchar(200) NULL,
    [INV_ADDR_EN] varchar(200) NULL,
    [BANK_ID] nvarchar(50) NULL,
    [BANK_NAME_CN] nvarchar(100) NULL,
    [BANK_NAME_EN] nvarchar(100) NULL,
    [BANK_ADDR_CN] nvarchar(200) NULL,
    [BANK_ADDR_EN] nvarchar(200) NULL,
    [BANK_TEL] nvarchar(100) NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_NO] nvarchar(50) NULL,
    [FIRST_TRADE_DATE] datetime NULL,
    [LAST_TRADE_DATE] datetime NULL,
    [CAPITAL_SUM] float NULL,
    [LICENSE] nvarchar(50) NULL,
    [TURNOVER_SUM] float NULL,
    [EMPOYEE_SUM] int NULL,
    [SELL_GRADE_TAG] char(1) NULL,
    [CREDIT_GRADE_TAG] char(1) NULL CONSTRAINT [DF_CREDIT_GRADE_TAG_1653580929] DEFAULT ('A'),
    [ACCOUNT_ID] nvarchar(50) NULL,
    [CURR_ID] nchar(10) NULL,
    [PRICE_CONDITION] nvarchar(100) NULL,
    [PAY_CONDITION] nvarchar(100) NULL,
    [COMPANT_SELL_TYPE] nvarchar(100) NULL,
    [ACCOUNT_VERIFY_DAY] nchar(10) NULL,
    [DAY_NO26] nchar(10) NULL CONSTRAINT [DF_DAY_NO26_1653580929] DEFAULT ('01'),
    [DAY_NO25] nchar(10) NULL CONSTRAINT [DF_DAY_NO25_1653580929] DEFAULT ('31'),
    [GATHERING_DAY] nchar(10) NULL,
    [PAYMENT_DAY] int NULL CONSTRAINT [DF_PAYMENT_DAY_1653580929] DEFAULT ((1000000)),
    [CREDIT_LIMIT_QTY] float NULL CONSTRAINT [DF_CREDIT_LIMIT_QTY_1653580929] DEFAULT ((10000000)),
    [CREDIT_LIMIT_NUM] float NULL CONSTRAINT [DF_CREDIT_LIMIT_NUM_1653580929] DEFAULT ((10000000)),
    [SALES_ID] nchar(10) NULL,
    [BUSINESS_TAG] bit NULL CONSTRAINT [DF_BUSINESS_TAG_1653580929] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1653580929] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] nchar(1) NULL CONSTRAINT [DF_TAX_TYPE_1653580929] DEFAULT ('O'),
    [HEADER_ID] nchar(10) NULL,
    [LINKMAN] nvarchar(100) NULL,
    [TEL] nvarchar(100) NULL,
    [FAX] nvarchar(100) NULL,
    [MIN_ORDER_AMOUNT] int NULL CONSTRAINT [DF_MIN_ORDER_AMOUNT_1653580929] DEFAULT ((0)),
    [TYPE] nchar(1) NULL CONSTRAINT [DF_TYPE_1653580929] DEFAULT ('1'),
    [PRINT_PRICE] bit NULL CONSTRAINT [DF_PRINT_PRICE_1653580929] DEFAULT ((0)),
    [PREPAY_SUM] float NULL,
    [COMPANY_NO_CN] nvarchar(100) NULL,
    [DECLARE_TYPE] nchar(1) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [PRO_NO] nvarchar(50) NULL,
    [REBATE_PRICE] float NULL,
    [REBATE_RATE] float NULL,
    CONSTRAINT [PK_CLIENT] PRIMARY KEY CLUSTERED ([CLIENT_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_CLIENT] ON dbo.[CLIENT] ([HEADER_ID]);
GO

------------------------------------------------------------------------------
-- CLIENT_LINKMAN
------------------------------------------------------------------------------
CREATE TABLE dbo.[CLIENT_LINKMAN] (
    [CLIENT_ID] nchar(10) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [LINKMAN] nvarchar(100) NULL,
    [DEPT_ID] nvarchar(100) NULL,
    [TEL] nvarchar(100) NULL,
    [TEL_CDMA] nvarchar(100) NULL,
    [FAX] nvarchar(100) NULL,
    [TEL_MOBILE] nvarchar(100) NULL,
    [WEBSITE] nvarchar(100) NULL,
    [EMAIL] nvarchar(100) NULL,
    [QQ_MSN] nvarchar(100) NULL,
    [REMARK] nvarchar(100) NULL,
    [REMARK1] nvarchar(100) NULL,
    [REMARK2] nvarchar(100) NULL,
    [REMARK3] nvarchar(100) NULL,
    CONSTRAINT [PK_CLIENT_LINKMAN] PRIMARY KEY CLUSTERED ([CLIENT_ID], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IDX_COPMC_1] ON dbo.[CLIENT_LINKMAN] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- CLIENT_PRICE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CLIENT_PRICE_D] (
    [CLIENT_ID] nchar(10) NOT NULL,
    [PRO_NO] nchar(30) NOT NULL,
    [UNIT_ID] nchar(10) NOT NULL,
    [TAX_ID] nchar(10) NOT NULL,
    [TAX_TYPE] char(1) NOT NULL CONSTRAINT [DF_TAX_TYPE_1685581043] DEFAULT ('1'),
    [CURR_ID] nchar(10) NOT NULL,
    [CURR_RATE] float NULL,
    [PRICE] float NULL,
    [TAX_RATE] float NULL,
    [PROCESS_PRICE] float NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [REBATE] float NOT NULL,
    [VERIFY_PRICE_DATE] datetime NULL,
    [OLD_PRICE] float NULL,
    [OLD_PRICE_DATE] datetime NULL,
    [QUOTE_TYPE] nchar(10) NULL,
    [QUOTE_NO] nchar(20) NULL,
    [QUOTE_SERIAL_NO] smallint NULL,
    [REMARK] nvarchar(100) NULL,
    [IN_EFFECT_DATE] datetime NULL CONSTRAINT [DF_IN_EFFECT_DATE_1685581043] DEFAULT ('9999.12.31'),
    CONSTRAINT [PK_CLIENT_PRICE_D] PRIMARY KEY CLUSTERED ([CLIENT_ID], [PRO_NO], [UNIT_ID], [TAX_ID], [TAX_TYPE], [CURR_ID], [REBATE])
);
GO
CREATE NONCLUSTERED INDEX [IX_CLIENT_PRICE_D] ON dbo.[CLIENT_PRICE_D] ([CLIENT_ID], [PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IDX_COPME_1] ON dbo.[CLIENT_PRICE_D] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- CLIENT_PRICE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CLIENT_PRICE_M] (
    [CLIENT_ID] nchar(10) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1701581100] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CLIENT_PRICE_M] PRIMARY KEY CLUSTERED ([CLIENT_ID])
);
GO

------------------------------------------------------------------------------
-- COLOR
------------------------------------------------------------------------------
CREATE TABLE dbo.[COLOR] (
    [COLOR_ID] nchar(10) NOT NULL,
    [COLOR_NAME_CN] nvarchar(50) NULL,
    [COLOR_NAME_EN] nvarchar(50) NULL,
    [COLOR_IDNO] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1717581157] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [COLOR_NAME] nvarchar(50) NULL,
    CONSTRAINT [PK_COLOR] PRIMARY KEY CLUSTERED ([COLOR_ID])
);
GO

------------------------------------------------------------------------------
-- COMPANY
------------------------------------------------------------------------------
CREATE TABLE dbo.[COMPANY] (
    [COMPANY_ID] nchar(10) NOT NULL,
    [NAME_CN] nvarchar(150) NULL,
    [NAME_EN] nvarchar(150) NULL,
    [INV_ADDR_CN] nvarchar(150) NULL,
    [INV_ADDR_EN] varchar(150) NULL,
    [DEL_ADDR_CN] nvarchar(150) NULL,
    [DEL_ADDR_EN] nvarchar(100) NULL,
    [TEL] nvarchar(100) NULL,
    [FAX] nvarchar(100) NULL,
    [SHORT_NAME_CN] nvarchar(100) NULL,
    [SHORT_NAME_EN] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1733581214] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_COMPANY] PRIMARY KEY CLUSTERED ([COMPANY_ID])
);
GO

------------------------------------------------------------------------------
-- COP_ACCOUNT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ACCOUNT_D] (
    [ACCOUNT_TYPE] nchar(10) NOT NULL,
    [ACCOUNT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [S_R_TYPE] nchar(10) NULL,
    [S_R_NO] nchar(20) NULL,
    [S_R_SERIAL_NO] smallint NULL,
    [S_R_DATE] datetime NULL,
    [REBATE] float NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [SEND_TYPE] nchar(10) NULL,
    [SEND_NO] nchar(20) NULL,
    [SEND_SERIAL_NO] smallint NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1749581271] DEFAULT ('1'),
    [CLIENT_ACCEPT_NO] nvarchar(50) NULL CONSTRAINT [DF_CLIENT_ACCEPT_NO_1749581271] DEFAULT (''),
    [CLIENT_OTHER_NO] nvarchar(50) NULL,
    CONSTRAINT [PK_COP_ACCOUNT_D] PRIMARY KEY CLUSTERED ([ACCOUNT_TYPE], [ACCOUNT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTI_4] ON dbo.[COP_ACCOUNT_D] ([SEND_TYPE], [SEND_NO], [SEND_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTI_3] ON dbo.[COP_ACCOUNT_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTI_2] ON dbo.[COP_ACCOUNT_D] ([S_R_TYPE], [S_R_NO], [S_R_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTI_1] ON dbo.[COP_ACCOUNT_D] ([ACCOUNT_TYPE], [ACCOUNT_NO]);
GO

------------------------------------------------------------------------------
-- COP_ACCOUNT_DD_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ACCOUNT_DD_D] (
    [ACCOUNT_TYPE] nchar(10) NOT NULL,
    [ACCOUNT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [PERIOR_QTY] float NULL,
    [RECEIVE_QTY] float NULL,
    [SEND_QTY] float NULL,
    [RETURN_QTY] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [BACK_MATERIAL] float NULL,
    [BACK_BAD] float NULL,
    [LOST_RATE] float NULL,
    [CAN_LOST_QTY] float NULL,
    [OUT_LOST_QTY] float NULL,
    [LOST_PRICE] float NULL,
    [LOST_AMOUNT] float NULL,
    [LESS_QTY] float NULL,
    [LESS_PRICE] float NULL,
    [LESS_AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_COP_ACCOUNT_DD_D] PRIMARY KEY CLUSTERED ([ACCOUNT_TYPE], [ACCOUNT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- COP_ACCOUNT_DD_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ACCOUNT_DD_M] (
    [ACCOUNT_TYPE] nchar(10) NOT NULL,
    [ACCOUNT_NO] nchar(20) NOT NULL,
    [ACCOUNT_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [SALES_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1781581385] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL CONSTRAINT [DF_TAX_ID_1781581385] DEFAULT (''),
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_1781581385] DEFAULT ((0)),
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [INVOICE_NO] nvarchar(50) NULL,
    [INVOICE_DATE] datetime NULL,
    [INVOICE_SUM] float NULL,
    [PRE_RECEIVE_DATE] datetime NULL,
    [OTHER_PRICE] float NULL,
    [SUM_AMOUNT] float NULL,
    [RECEIVE_AMOUNT] float NULL,
    [FACT_RECEIVE_DATE] datetime NULL,
    [BEGIN_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1781581385] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1781581385] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_COP_ACCOUNT_DD_M] PRIMARY KEY CLUSTERED ([ACCOUNT_TYPE], [ACCOUNT_NO])
);
GO

------------------------------------------------------------------------------
-- COP_ACCOUNT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ACCOUNT_M] (
    [ACCOUNT_TYPE] nchar(10) NOT NULL,
    [ACCOUNT_NO] nchar(20) NOT NULL,
    [ACCOUNT_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [INVOICE_NO] nvarchar(50) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL CONSTRAINT [DF_TAX_ID_1797581442] DEFAULT (''),
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_1797581442] DEFAULT ((0)),
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [INVOICE_DATE] datetime NULL,
    [PRE_RECEIVE_DATE] datetime NULL,
    [OTHER_PRICE] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1797581442] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1797581442] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [SUM_AMOUNT] float NULL,
    [RECEIVE_AMOUNT] float NULL,
    [FACT_RECEIVE_DATE] datetime NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1797581442] DEFAULT ('O'),
    [INVOICE_SUM] float NULL,
    [BEGIN_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [ACCOUNT_MONTH] nchar(6) NULL,
    [QTY_TOTAL] float NULL,
    [REMARK11] nvarchar(20) NULL,
    [REMARK10] nvarchar(20) NULL,
    [REMARK9] nvarchar(20) NULL,
    [REMARK8] nvarchar(20) NULL,
    [REMARK7] nvarchar(20) NULL,
    [REMARK6] nvarchar(20) NULL,
    [REMARK5] nvarchar(20) NULL,
    [REMARK4] nvarchar(20) NULL,
    [REMARK3] nvarchar(20) NULL,
    [REMARK2] nvarchar(20) NULL,
    [REMARK1] nvarchar(20) NULL,
    CONSTRAINT [PK_COP_ACCOUNT_M] PRIMARY KEY CLUSTERED ([ACCOUNT_TYPE], [ACCOUNT_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_ACCOUNT_M] ON dbo.[COP_ACCOUNT_M] ([CLIENT_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_ACCOUNT_M_CONFIRM_FINISHED] ON dbo.[COP_ACCOUNT_M] ([CONFIRM_TAG], [FINISHED_TAG]) INCLUDE ([CLIENT_ID], [SALES_ID], [CURR_ID], [AMOUNT], [RECEIVE_AMOUNT]);
GO

------------------------------------------------------------------------------
-- COP_BACK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_BACK_D] (
    [BACK_TYPE] nchar(10) NOT NULL,
    [BACK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [SPARE_QTY] float NULL,
    [DEPOT_ID] nchar(10) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [BATCH_NO] nchar(30) NULL,
    [FINISHED_QTY] float NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1813581499] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_COP_BACK_D] PRIMARY KEY CLUSTERED ([BACK_TYPE], [BACK_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- COP_BACK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_BACK_M] (
    [BACK_TYPE] nchar(10) NOT NULL,
    [BACK_NO] nchar(20) NOT NULL,
    [BACK_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [BACK_CODE] nchar(1) NULL,
    [BACK_ADDRESS] nvarchar(100) NULL,
    [CAR_ID] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1829581556] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1829581556] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_COP_BACK_M] PRIMARY KEY CLUSTERED ([BACK_TYPE], [BACK_NO])
);
GO

------------------------------------------------------------------------------
-- COP_CALLBACK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_CALLBACK_D] (
    [CALLBACK_TYPE] nchar(10) NOT NULL,
    [CALLBACK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [S_R_TYPE] nchar(10) NULL,
    [S_R_NO] nchar(20) NULL,
    [S_R_SERIAL_NO] smallint NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL,
    [SPARE_QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1845581613] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [CLIENT_ACCEPT_NO] nvarchar(50) NULL CONSTRAINT [DF_CLIENT_ACCEPT_NO_1845581613] DEFAULT (''),
    [REMARK] nvarchar(500) NULL,
    [SEND_TYPE] nchar(10) NULL,
    [SEND_NO] nchar(20) NULL,
    [SEND_SERIAL_NO] smallint NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_1845581613] DEFAULT ((100)),
    [S_R_DATE] datetime NULL,
    CONSTRAINT [PK_COP_CALLBACK_D] PRIMARY KEY CLUSTERED ([CALLBACK_TYPE], [CALLBACK_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CALLBACK_D_1] ON dbo.[COP_CALLBACK_D] ([SEND_TYPE], [SEND_NO], [SEND_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CALLBACK_D_2] ON dbo.[COP_CALLBACK_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CALLBACK_D] ON dbo.[COP_CALLBACK_D] ([S_R_TYPE], [S_R_NO], [S_R_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CALLBACK_D_3] ON dbo.[COP_CALLBACK_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CALLBACK_D_4] ON dbo.[COP_CALLBACK_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO

------------------------------------------------------------------------------
-- COP_CALLBACK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_CALLBACK_M] (
    [CALLBACK_TYPE] nchar(10) NOT NULL,
    [CALLBACK_NO] nchar(20) NOT NULL,
    [CALLBACK_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [MOTORMAN] nchar(10) NULL,
    [FOLLOW] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1861581670] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1861581670] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    CONSTRAINT [PK_COP_CALLBACK_M] PRIMARY KEY CLUSTERED ([CALLBACK_TYPE], [CALLBACK_NO])
);
GO

------------------------------------------------------------------------------
-- COP_CHAFFER_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_CHAFFER_D] (
    [CHAFFER_TYPE] nchar(10) NOT NULL,
    [CHAFFER_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [PRO_STUFF] nvarchar(100) NULL,
    [PRO_SIZE] nvarchar(100) NULL,
    [PRO_COLOR] nvarchar(100) NULL,
    [PRO_LENGTH] float NULL,
    [PRO_WIDTH] float NULL,
    [PRO_HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_1877581727] DEFAULT ((100)),
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [OLD_PRICE] float NULL,
    [REMARK] nvarchar(100) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1877581727] DEFAULT ('O'),
    [P_LENGTH] float NULL,
    [P_WIDTH] float NULL,
    [MIN_PRICE] float NULL,
    [PARAMETER_PRICE] float NULL,
    [LOST_RATE] float NULL,
    [MOULD_SUM] float NULL,
    [PRINTING_SUM] float NULL,
    [PARAMETER_QTY] float NULL,
    [PROCESS_PRICE] float NULL,
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [CPG] nvarchar(20) NULL,
    [KPG] nvarchar(20) NULL,
    [QUOTE_TYPE] nchar(10) NULL,
    [QUOTE_NO] nchar(20) NULL,
    [QUOTE_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_COP_CHAFFER_D] PRIMARY KEY CLUSTERED ([CHAFFER_TYPE], [CHAFFER_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CHAFFER_D] ON dbo.[COP_CHAFFER_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- COP_CHAFFER_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_CHAFFER_M] (
    [CHAFFER_TYPE] nchar(10) NOT NULL,
    [CHAFFER_NO] nchar(20) NOT NULL,
    [CHAFFER_DATE] datetime NOT NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [PRICE_CONDITION] nvarchar(50) NULL,
    [PAYMENT_CONDITION] nvarchar(50) NULL,
    [DELIVERY_DATE] datetime NULL,
    [IN_EFFECT_DATE] datetime NULL,
    [CLIENT_CONFIRM] nvarchar(100) NULL CONSTRAINT [DF_CLIENT_CONFIRM_1893581784] DEFAULT ('N'),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1893581784] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1893581784] DEFAULT ('O'),
    [CLIENT_NAME] nvarchar(100) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_1893581784] DEFAULT ((100)),
    CONSTRAINT [PK_COP_CHAFFER_M] PRIMARY KEY CLUSTERED ([CHAFFER_TYPE], [CHAFFER_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CHAFFER_M] ON dbo.[COP_CHAFFER_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_FITIN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_FITIN_D] (
    [FITIN_TYPE] nchar(10) NOT NULL,
    [FITIN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [FITOUT_TYPE] nchar(10) NULL,
    [FITOUT_NO] nchar(20) NULL,
    [FITOUT_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [BATCH_NO] nchar(30) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_1909581841] DEFAULT ((100)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1909581841] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [SPARE_QTY] float NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_COP_FITIN_D] PRIMARY KEY CLUSTERED ([FITIN_TYPE], [FITIN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_FITIN_D_1] ON dbo.[COP_FITIN_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_FITIN_D_2] ON dbo.[COP_FITIN_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_FITIN_D] ON dbo.[COP_FITIN_D] ([FITOUT_TYPE], [FITOUT_NO], [FITOUT_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_FITIN_D_3] ON dbo.[COP_FITIN_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO

------------------------------------------------------------------------------
-- COP_FITIN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_FITIN_M] (
    [FITIN_TYPE] nchar(10) NOT NULL,
    [FITIN_NO] nchar(20) NOT NULL,
    [FITIN_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(60) NULL,
    [SALES_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1925581898] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1925581898] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_COP_FITIN_M] PRIMARY KEY CLUSTERED ([FITIN_TYPE], [FITIN_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_FITIN_M] ON dbo.[COP_FITIN_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_FITOUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_FITOUT_D] (
    [FITOUT_TYPE] nchar(10) NOT NULL,
    [FITOUT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1941581955] DEFAULT ((0)),
    [PRICE] float NULL,
    [SHIPMENT_TYPE] nchar(10) NULL,
    [SHIPMENT_NO] nchar(20) NULL,
    [SHIPMENT_SERIAL_NO] smallint NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [BATCH_NO] nchar(30) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_1941581955] DEFAULT ((100)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1941581955] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [SPARE_QTY] float NULL CONSTRAINT [DF_SPARE_QTY_1941581955] DEFAULT ((0)),
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1941581955] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1941581955] DEFAULT ((0)),
    [FINISHED_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_SPARE_QTY_1941581955] DEFAULT ((0)),
    [RETURN_QTY] float NULL CONSTRAINT [DF_RETURN_QTY_1941581955] DEFAULT ((0)),
    [RETURN_SPARE_QTY] float NULL CONSTRAINT [DF_RETURN_SPARE_QTY_1941581955] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [PCS_COUNT] nvarchar(50) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [FINISHED_TRANSFER_QTY] float NULL CONSTRAINT [DF_FINISHED_TRANSFER_QTY_1941581955] DEFAULT ((0)),
    [FINISHED_TRANSFER_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_TRANSFER_SPARE_QTY_1941581955] DEFAULT ((0)),
    [ITEM_NO] nvarchar(50) NULL,
    CONSTRAINT [PK_COP_FITOUT_D] PRIMARY KEY CLUSTERED ([FITOUT_TYPE], [FITOUT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_FITOUT_D_2] ON dbo.[COP_FITOUT_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_FITOUT_D_1] ON dbo.[COP_FITOUT_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_FITOUT_D] ON dbo.[COP_FITOUT_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- COP_FITOUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_FITOUT_M] (
    [FITOUT_TYPE] nchar(10) NOT NULL,
    [FITOUT_NO] nchar(20) NOT NULL,
    [FITOUT_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(60) NULL,
    [SALES_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1957582012] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1957582012] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1957582012] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [BOARD_QTY] nvarchar(50) NULL,
    [SEND_DATE] smalldatetime NULL,
    [DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_COP_FITOUT_M] PRIMARY KEY CLUSTERED ([FITOUT_TYPE], [FITOUT_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_FITOUT_M] ON dbo.[COP_FITOUT_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_MONTH_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_MONTH_D] (
    [MONTH_TYPE] nchar(10) NOT NULL,
    [MONTH_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [PERIOD_AMOUNT] float NULL CONSTRAINT [DF_PERIOD_AMOUNT_1973582069] DEFAULT ((0)),
    [CURRENT_PREPAY] float NULL CONSTRAINT [DF_CURRENT_PREPAY_1973582069] DEFAULT ((0)),
    [CURRENT_ACCOUNT] float NULL CONSTRAINT [DF_CURRENT_ACCOUNT_1973582069] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CURRENT_BALANCE] float NULL CONSTRAINT [DF_CURRENT_BALANCE_1973582069] DEFAULT ((0)),
    [CURRENT_REBATE] float NULL CONSTRAINT [DF_CURRENT_REBATE_1973582069] DEFAULT ((0)),
    [CURRENT_RECEIPT] float NULL CONSTRAINT [DF_CURRENT_RECEIPT_1973582069] DEFAULT ((0)),
    CONSTRAINT [PK_COP_MONTH_D] PRIMARY KEY CLUSTERED ([MONTH_TYPE], [MONTH_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- COP_MONTH_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_MONTH_M] (
    [MONTH_TYPE] nchar(10) NOT NULL,
    [MONTH_NO] nchar(20) NOT NULL,
    [MONTH_DATE] datetime NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1989582126] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_COP_MONTH_M] PRIMARY KEY CLUSTERED ([MONTH_TYPE], [MONTH_NO])
);
GO

------------------------------------------------------------------------------
-- COP_ORDER_CALC_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ORDER_CALC_D] (
    [CALC_ORDER_TYPE] nchar(10) NOT NULL,
    [CALC_ORDER_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL,
    [SPARE_QTY] float NULL,
    [PRE_DELIVERY_DATE] datetime NULL,
    [PLAN_QTY] float NULL,
    [PLAN_SPARE_QTY] float NULL,
    [APPLY_QTY] float NULL,
    [APPLY_SPARE_QTY] float NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [APPLY_TYPE] nchar(10) NULL,
    [APPLY_NO] nchar(20) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [LINE_ID] nchar(10) NULL,
    CONSTRAINT [PK_COP_ORDER_CALC_D] PRIMARY KEY CLUSTERED ([CALC_ORDER_TYPE], [CALC_ORDER_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- COP_ORDER_CALC_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ORDER_CALC_M] (
    [CALC_ORDER_TYPE] char(4) NOT NULL,
    [CALC_ORDER_NO] varchar(20) NOT NULL,
    [CALC_ORDER_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_COP_ORDER_CALC_M] PRIMARY KEY CLUSTERED ([CALC_ORDER_TYPE], [CALC_ORDER_NO])
);
GO

------------------------------------------------------------------------------
-- COP_ORDER_CHANGE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ORDER_CHANGE_D] (
    [CHANGE_ORDER_TYPE] nchar(10) NOT NULL,
    [CHANGE_ORDER_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL,
    [SPARE_QTY] float NULL,
    [PRICE] float NULL,
    [PRE_DELIVERY_DATE] datetime NULL,
    [PLAN_QTY] float NULL,
    [PLAN_SPARE_QTY] float NULL,
    [OLD_QTY] float NULL,
    [OLD_SPARE_QTY] float NULL,
    [OLD_PRICE] float NULL,
    [OLD_PRE_DELIVERY_DATE] datetime NULL,
    [OLD_PLAN_QTY] float NULL,
    [OLD_PLAN_SPARE_QTY] float NULL,
    [FINISHED_PLAN_QTY] float NULL CONSTRAINT [DF_FINISHED_PLAN_QTY_2037582297] DEFAULT ((0)),
    [FINISHED_PLAN_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_PLAN_SPARE_QTY_2037582297] DEFAULT ((0)),
    [FINISHED_SEND_QTY] float NULL CONSTRAINT [DF_FINISHED_SEND_QTY_2037582297] DEFAULT ((0)),
    [FINISHED_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_SPARE_QTY_2037582297] DEFAULT ((0)),
    [FINISHED_PRODUCE_QTY] float NULL CONSTRAINT [DF_FINISHED_PRODUCE_QTY_2037582297] DEFAULT ((0)),
    [FINISHED_PRODUCE_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_PRODUCE_SPARE_QTY_2037582297] DEFAULT ((0)),
    [FINISHED_FITOUT_QTY] float NULL CONSTRAINT [DF_FINISHED_FITOUT_QTY_2037582297] DEFAULT ((0)),
    [FINISHED_FITOUT_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_FITOUT_SPARE_QTY_2037582297] DEFAULT ((0)),
    [OLD_FINISHED_PLAN_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_PLAN_QTY_2037582297] DEFAULT ((0)),
    [OLD_FINISHED_PLAN_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_PLAN_SPARE_QTY_2037582297] DEFAULT ((0)),
    [OLD_FINISHED_SEND_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_SEND_QTY_2037582297] DEFAULT ((0)),
    [OLD_FINISHED_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_SPARE_QTY_2037582297] DEFAULT ((0)),
    [OLD_FINISHED_PRODUCE_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_PRODUCE_QTY_2037582297] DEFAULT ((0)),
    [OLD_FINISHED_PRODUCE_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_PRODUCE_SPARE_QTY_2037582297] DEFAULT ((0)),
    [OLD_FINISHED_FITOUT_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_FITOUT_QTY_2037582297] DEFAULT ((0)),
    [OLD_FINISHED_FITOUT_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_FITOUT_SPARE_QTY_2037582297] DEFAULT ((0)),
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [OLD_CLIENT_ORDER_NO] nvarchar(50) NULL,
    [OLD_BATCH_NO] nchar(30) NULL,
    [OLD_ITEM_NO] nvarchar(50) NULL,
    [OLD_MO_NO] nvarchar(50) NULL,
    [OLD_CLIENT_OTHER_NO] nvarchar(50) NULL,
    [BATCH_NO] nchar(30) NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [MO_NO] nvarchar(50) NULL,
    [CLIENT_OTHER_NO] nvarchar(50) NULL,
    [CLIENT_ORDER_NO1] nvarchar(50) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    CONSTRAINT [PK_COP_ORDER_CHANGE_D] PRIMARY KEY CLUSTERED ([CHANGE_ORDER_TYPE], [CHANGE_ORDER_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTM_1] ON dbo.[COP_ORDER_CHANGE_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CHANGE_ORDER_D] ON dbo.[COP_ORDER_CHANGE_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- COP_ORDER_CHANGE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ORDER_CHANGE_M] (
    [CHANGE_ORDER_TYPE] char(4) NOT NULL,
    [CHANGE_ORDER_NO] varchar(20) NOT NULL,
    [CHANGE_ORDER_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [SEND_ADDRESS] nvarchar(100) NULL,
    [PRICE_CONDITION] nvarchar(100) NULL,
    [PAYMENT_CONDITION] nvarchar(100) NULL,
    [OLD_CLIENT_ORDER_NO] nvarchar(50) NULL,
    [OLD_SEND_ADDRESS] nvarchar(100) NULL,
    [OLD_PRICE_CONDITION] nvarchar(100) NULL,
    [OLD_PAYMENT_CONDITION] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_COP_ORDER_CHANGE_M] PRIMARY KEY CLUSTERED ([CHANGE_ORDER_TYPE], [CHANGE_ORDER_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CHANGE_ORDER_M_1] ON dbo.[COP_ORDER_CHANGE_M] ([CLIENT_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_CHANGE_ORDER_M] ON dbo.[COP_ORDER_CHANGE_M] ([ORDER_TYPE], [ORDER_NO]);
GO

------------------------------------------------------------------------------
-- COP_ORDER_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ORDER_D] (
    [ORDER_TYPE] nchar(10) NOT NULL,
    [ORDER_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [FINISHED_SEND_QTY] float NULL CONSTRAINT [DF_FINISHED_SEND_QTY_2069582411] DEFAULT ((0)),
    [PRICE] float NULL,
    [PRE_SEND_DATE] datetime NULL,
    [FACT_SEND_DATE] datetime NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_2069582411] DEFAULT ((100)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [QUOTE_TYPE] nchar(10) NULL,
    [QUOTE_NO] nchar(20) NULL,
    [QUOTE_SERIAL_NO] smallint NULL,
    [FINISHED_PLAN_QTY] float NULL CONSTRAINT [DF_FINISHED_PLAN_QTY_2069582411] DEFAULT ((0)),
    [FINISHED_PRODUCE_QTY] float NULL CONSTRAINT [DF_FINISHED_PRODUCE_QTY_2069582411] DEFAULT ((0)),
    [FINISHED_RECEIPT_AMOUNT] float NULL CONSTRAINT [DF_FINISHED_RECEIPT_AMOUNT_2069582411] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_2069582411] DEFAULT ((0)),
    [PLAN_QTY] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_2069582411] DEFAULT ('O'),
    [SPARE_QTY] float NULL,
    [FINISHED_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_SPARE_QTY_2069582411] DEFAULT ((0)),
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [PLAN_SPARE_QTY] float NULL,
    [FINISHED_PLAN_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_PLAN_SPARE_QTY_2069582411] DEFAULT ((0)),
    [FINISHED_PRODUCE_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_PRODUCE_SPARE_QTY_2069582411] DEFAULT ((0)),
    [FINISHED_FITOUT_QTY] float NULL CONSTRAINT [DF_FINISHED_FITOUT_QTY_2069582411] DEFAULT ((0)),
    [FINISHED_FITOUT_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_FITOUT_SPARE_QTY_2069582411] DEFAULT ((0)),
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [EDITION] char(10) NULL,
    [DEPOT_QTY] float NULL CONSTRAINT [DF_DEPOT_QTY_2069582411] DEFAULT ((0)),
    [ITEM_NO] nvarchar(50) NULL,
    [MO_NO] nvarchar(50) NULL,
    [CLIENT_OTHER_NO] nvarchar(50) NULL,
    [BACK_MATERIAL] float NULL CONSTRAINT [DF_BACK_MATERIAL_2069582411] DEFAULT ((0)),
    [BACK_BAD] float NULL CONSTRAINT [DF_BACK_BAD_2069582411] DEFAULT ((0)),
    [BATCH_NO] nchar(30) NULL,
    [DO_PLAN_QTY] float NULL,
    [DO_PLAN_SPARE_QTY] float NULL,
    [APPLY_TYPE] nchar(10) NULL,
    [APPLY_NO] nchar(20) NULL,
    [APPLY_SERIAL_NO] smallint NULL,
    [FINISHED_AMOUNT] float NULL CONSTRAINT [DF_FINISHED_AMOUNT_2069582411] DEFAULT ((0)),
    [PRE_LASTPLAN_DATE] datetime NULL,
    CONSTRAINT [PK_COP_ORDER_D] PRIMARY KEY CLUSTERED ([ORDER_TYPE], [ORDER_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_ORDER_D] ON dbo.[COP_ORDER_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- COP_ORDER_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ORDER_M] (
    [ORDER_TYPE] nchar(10) NOT NULL,
    [ORDER_NO] nchar(20) NOT NULL,
    [ORDER_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [PRICE_CONDITION] nvarchar(100) NULL,
    [PAYMENT_CONDITION] nvarchar(100) NULL,
    [SUBSCRIPTION] float NULL,
    [SEND_ADDRESS] nvarchar(100) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2085582468] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_2085582468] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_2085582468] DEFAULT ('O'),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_2085582468] DEFAULT ((100)),
    CONSTRAINT [PK_COP_ORDER_M] PRIMARY KEY CLUSTERED ([ORDER_TYPE], [ORDER_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_ORDER_M] ON dbo.[COP_ORDER_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_ORDER_MORE
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_ORDER_MORE] (
    [ORDER_TYPE] nchar(10) NOT NULL,
    [ORDER_NO] nchar(20) NOT NULL,
    [PRO_NO] nchar(30) NOT NULL,
    [NEED_QTY] float NULL CONSTRAINT [DF_NEED_QTY_2101582525] DEFAULT ((0)),
    [APPLY_QTY] float NULL CONSTRAINT [DF_APPLY_QTY_2101582525] DEFAULT ((0)),
    [USED_QTY] float NULL CONSTRAINT [DF_USED_QTY_2101582525] DEFAULT ((0)),
    [PURCHASE_QTY] float NULL CONSTRAINT [DF_PURCHASE_QTY_2101582525] DEFAULT ((0)),
    [RECEIVE_QTY] float NULL CONSTRAINT [DF_RECEIVE_QTY_2101582525] DEFAULT ((0)),
    [LOST_QTY] float NULL,
    CONSTRAINT [PK_COP_ORDER_MORE] PRIMARY KEY CLUSTERED ([ORDER_TYPE], [ORDER_NO], [PRO_NO])
);
GO

------------------------------------------------------------------------------
-- COP_PACK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_PACK_D] (
    [PACK_TYPE] nchar(10) NOT NULL,
    [PACK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [SEND_TYPE] nchar(10) NULL,
    [SEND_NO] nchar(20) NULL,
    [SEND_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_2117582582] DEFAULT ((100)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_2117582582] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [PACKING_NO] nvarchar(50) NULL,
    [MARKS] nvarchar(200) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [BOX_PCS] nvarchar(20) NULL,
    [BOX_QTY] int NULL,
    [NET_WEIGHT_PCS] nvarchar(20) NULL,
    [NET_WEIGHT] float NULL,
    [GROSS_WEIGHT_PCS] nvarchar(20) NULL,
    [GROSS_WEIGHT] float NULL,
    [BOX_SPEC] nvarchar(100) NULL,
    [BOX_CUBAGE_PCS] nvarchar(20) NULL,
    [BOX_CUBAGE] float NULL,
    [DESCRIPTION] nvarchar(200) NULL,
    CONSTRAINT [PK_COP_PACK_D] PRIMARY KEY CLUSTERED ([PACK_TYPE], [PACK_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_PACK_D] ON dbo.[COP_PACK_D] ([SEND_TYPE], [SEND_NO], [SEND_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- COP_PACK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_PACK_M] (
    [PACK_TYPE] nchar(10) NOT NULL,
    [PACK_NO] nchar(20) NOT NULL,
    [PACK_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SEND_TYPE] nchar(10) NULL,
    [SEND_NO] nchar(20) NULL,
    [ADDRESS] nvarchar(100) NULL,
    [REPORT_HEADER] nvarchar(50) NULL,
    [INVOICE_NO] nvarchar(50) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [ACCOUNT_ADDR] nvarchar(200) NULL,
    [TEL] nvarchar(50) NULL,
    [FAX] nvarchar(50) NULL,
    [MARKS] nvarchar(200) NULL,
    [PORT] nvarchar(50) NULL,
    [SHIPPING_DATE] datetime NULL,
    [SAILING_ON] datetime NULL,
    [SAILING_FROM] nvarchar(50) NULL,
    [COUNTRY] nvarchar(50) NULL,
    [SHIPPING_BY] nvarchar(50) NULL,
    [SHIPPING_PER] nvarchar(50) NULL,
    [SHIPPING_PAYMENT] nvarchar(50) NULL,
    [CONTRACT_NO] nvarchar(50) NULL,
    [PRICE_CONDITION] nvarchar(50) NULL,
    [COMPANY_NAME] nvarchar(50) NULL,
    [COMPANY_ADDR] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2133582639] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [LC_NO] nvarchar(50) NULL CONSTRAINT [DF_LC_NO_2133582639] DEFAULT (''),
    CONSTRAINT [PK_COP_PACK_M] PRIMARY KEY CLUSTERED ([PACK_TYPE], [PACK_NO])
);
GO

------------------------------------------------------------------------------
-- COP_PREPAY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_PREPAY_D] (
    [PREPAY_TYPE] nchar(10) NOT NULL,
    [PREPAY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [RECEIVE_ACCOUNT_ID] nchar(20) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [BILL_NO] nvarchar(50) NULL,
    [AT_TERM_DATE] datetime NULL,
    [REMARK] nvarchar(50) NULL,
    [ACCOUNT_TYPE_ID] nchar(10) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    CONSTRAINT [PK_COP_PREPAY_D] PRIMARY KEY CLUSTERED ([PREPAY_TYPE], [PREPAY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- COP_PREPAY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_PREPAY_M] (
    [PREPAY_TYPE] nchar(10) NOT NULL,
    [PREPAY_NO] nchar(20) NOT NULL,
    [PREPAY_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [RECEIVE_ID] nchar(10) NULL,
    [BANK_ID] nchar(30) NULL,
    [ACCOUNT_ID] nchar(20) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [PREPAY_AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_18099105] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [ACCOUNT_TYPE_ID] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_18099105] DEFAULT ((0)),
    CONSTRAINT [PK_COP_PREPAY_M] PRIMARY KEY CLUSTERED ([PREPAY_TYPE], [PREPAY_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_PREPAY_M] ON dbo.[COP_PREPAY_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_QUOTE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_QUOTE_D] (
    [QUOTE_TYPE] nchar(10) NOT NULL,
    [QUOTE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [PRO_STUFF] nvarchar(100) NULL,
    [PRO_SIZE] nvarchar(100) NULL,
    [PRO_COLOR] nvarchar(100) NULL,
    [PRO_LENGTH] float NULL,
    [PRO_WIDTH] float NULL,
    [PRO_HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_34099162] DEFAULT ((100)),
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [OLD_PRICE] float NULL,
    [REMARK] nvarchar(100) NULL,
    [CHAFFER_TYPE] nchar(10) NULL,
    [CHAFFER_NO] nchar(20) NULL,
    [CHAFFER_SERIAL_NO] smallint NULL,
    [PROCESS_PRICE] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_34099162] DEFAULT ('O'),
    [P_LENGTH] float NULL,
    [P_WIDTH] float NULL,
    [MIN_PRICE] float NULL,
    [PARAMETER_PRICE] float NULL,
    [LOST_RATE] float NULL,
    [MOULD_SUM] float NULL,
    [PRINTING_SUM] float NULL,
    [PARAMETER_QTY] float NULL,
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [UP_PERCENT] float NULL CONSTRAINT [DF_UP_PERCENT_34099162] DEFAULT ((0)),
    CONSTRAINT [PK_COP_QUOTE_D] PRIMARY KEY CLUSTERED ([QUOTE_TYPE], [QUOTE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_QUOTE_D] ON dbo.[COP_QUOTE_D] ([CHAFFER_TYPE], [CHAFFER_NO], [CHAFFER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_QUOTE_D_1] ON dbo.[COP_QUOTE_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- COP_QUOTE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_QUOTE_M] (
    [QUOTE_TYPE] nchar(10) NOT NULL,
    [QUOTE_NO] nchar(20) NOT NULL,
    [QUOTE_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [PRICE_CONDITION] nvarchar(50) NULL,
    [PAYMENT_CONDITION] nvarchar(50) NULL,
    [DELIVERY_DATE] datetime NULL,
    [IN_EFFECT_DATE] datetime NULL,
    [CLIENT_CONFIRM] nvarchar(100) NULL CONSTRAINT [DF_CLIENT_CONFIRM_50099219] DEFAULT ('N'),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_50099219] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_50099219] DEFAULT ('1'),
    [CLIENT_NAME] nvarchar(100) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_50099219] DEFAULT ((100)),
    CONSTRAINT [PK_COP_QUOTE_M] PRIMARY KEY CLUSTERED ([QUOTE_TYPE], [QUOTE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IDX_COPMJ_1] ON dbo.[COP_QUOTE_M] ([CLIENT_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_QUOTE_M] ON dbo.[COP_QUOTE_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_QUOTE_PARAMETER
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_QUOTE_PARAMETER] (
    [STUFF_ID] nchar(30) NOT NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [TAX_PCT] float NULL,
    [MATERIAL_SUM] float NULL,
    [MATERIAL_PCT] float NULL,
    [ASSISTANT_SUM] float NULL,
    [ASSISTANT_PCT] float NULL,
    [SALES_SUM] float NULL,
    [SALES_PCT] float NULL,
    [LOST_SUM] float NULL,
    [LOST_PCT] float NULL,
    [PRODUCE_SUM] float NULL,
    [PRODUCE_PCT] float NULL,
    [PACK_SUM] float NULL,
    [PACK_PCT] float NULL,
    [TRANSIT_SUM] float NULL,
    [TRANSIT_PCT] float NULL,
    [ELETRICITY_SUM] float NULL,
    [ELETRICITY_PCT] float NULL,
    [WAGE_SUM] float NULL,
    [WAGE_PCT] float NULL,
    [OTHER1_SUM] float NULL,
    [OTHER1_PCT] float NULL,
    [OTHER2_SUM] float NULL,
    [OTHER2_PCT] float NULL,
    [OTHER3_SUM] float NULL,
    [OTHER3_PCT] float NULL,
    [OTHER4_SUM] float NULL,
    [OTHER4_PCT] float NULL,
    [PRICE] float NULL,
    [GAIN_SUM] float NULL,
    [GAIN_PCT] float NULL,
    [OLD_PRICE] float NULL,
    [OLD_GAIN_SUM] float NULL,
    [OLD_GAIN_PCT] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FIX_PRICE] char(1) NULL CONSTRAINT [DF_FIX_PRICE_66099276] DEFAULT ((2)),
    [FIX_PCT] char(1) NULL CONSTRAINT [DF_FIX_PCT_66099276] DEFAULT ((1)),
    CONSTRAINT [PK_COP_QUOTE_PARAMETER] PRIMARY KEY CLUSTERED ([STUFF_ID])
);
GO

------------------------------------------------------------------------------
-- COP_RECEIPT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_RECEIPT_D] (
    [RECEIPT_TYPE] nchar(10) NOT NULL,
    [RECEIPT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ACCOUNT_TYPE] nchar(10) NULL,
    [ACCOUNT_NO] nchar(20) NULL,
    [AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    [RECEIVE_AMOUNT] float NULL CONSTRAINT [DF_RECEIVE_AMOUNT_82099333] DEFAULT ((0)),
    CONSTRAINT [PK_COP_RECEIPT_D] PRIMARY KEY CLUSTERED ([RECEIPT_TYPE], [RECEIPT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTK_2] ON dbo.[COP_RECEIPT_D] ([ACCOUNT_TYPE], [ACCOUNT_NO]);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTK_1] ON dbo.[COP_RECEIPT_D] ([RECEIPT_TYPE], [RECEIPT_NO]);
GO

------------------------------------------------------------------------------
-- COP_RECEIPT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_RECEIPT_M] (
    [RECEIPT_TYPE] nchar(10) NOT NULL,
    [RECEIPT_NO] nchar(20) NOT NULL,
    [RECEIPT_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [RECEIVE_ID] nchar(10) NULL,
    [BANK_ID] nchar(30) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_98099390] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [RECEIVE_ACCOUNT_ID] nchar(20) NULL,
    [REBATE_ACCOUNT_ID] nchar(20) NULL,
    [RECEIVE_SUM] float NULL,
    [PREPAY_SUM] float NULL,
    [REBATE_SUM] float NULL,
    [ACCOUNT_TYPE_ID] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_98099390] DEFAULT ('O'),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_98099390] DEFAULT ((0)),
    CONSTRAINT [PK_COP_RECEIPT_M] PRIMARY KEY CLUSTERED ([RECEIPT_TYPE], [RECEIPT_NO])
);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTJ_1] ON dbo.[COP_RECEIPT_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_RECEIPT_OTHER
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_RECEIPT_OTHER] (
    [RECEIPT_TYPE] nchar(10) NOT NULL,
    [RECEIPT_NO] nchar(20) NOT NULL,
    [RECEIPT_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(100) NULL,
    [SALES_ID] nchar(10) NULL,
    [RECEIPT_DESC] nvarchar(100) NULL,
    [RECEIVE_ID] nchar(10) NULL,
    [BANK_ID] nchar(30) NULL,
    [RECEIVE_ACCOUNT_ID] nchar(20) NULL,
    [REBATE_ACCOUNT_ID] nchar(20) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [AMOUNT_WORD] nvarchar(200) NULL,
    [RECEIVE_SUM] float NULL,
    [PREPAY_SUM] float NULL,
    [REBATE_SUM] float NULL,
    [PREPAY_TYPE] nchar(10) NULL,
    [PREPAY_NO] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_114099447] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [ACCOUNT_TYPE_ID] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_114099447] DEFAULT ('O'),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_114099447] DEFAULT ((0)),
    CONSTRAINT [PK_COP_RECEIPT_OTHER] PRIMARY KEY CLUSTERED ([RECEIPT_TYPE], [RECEIPT_NO])
);
GO

------------------------------------------------------------------------------
-- COP_RECEIPT_OTHER_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_RECEIPT_OTHER_D] (
    [RECEIPT_TYPE] nchar(10) NOT NULL,
    [RECEIPT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [SUMMARY] nvarchar(100) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_COP_RECEIPT_OTHER_D] PRIMARY KEY CLUSTERED ([RECEIPT_TYPE], [RECEIPT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- COP_RECEIPT_PREPAY
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_RECEIPT_PREPAY] (
    [RECEIPT_TYPE] nchar(10) NOT NULL,
    [RECEIPT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PREPAY_TYPE] nchar(10) NULL,
    [PREPAY_NO] nchar(20) NULL,
    [AMOUNT] float NULL,
    [PREPAY_AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_COP_RECEIPT_PREPAY] PRIMARY KEY CLUSTERED ([RECEIPT_TYPE], [RECEIPT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- COP_RETURN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_RETURN_D] (
    [RETURN_TYPE] nchar(10) NOT NULL,
    [RETURN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [SEND_TYPE] nchar(10) NULL,
    [SEND_NO] nchar(20) NULL,
    [SEND_SERIAL_NO] smallint NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [BATCH_NO] nchar(30) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_162099618] DEFAULT ((100)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [RETURNED_AMOUNT] float NULL CONSTRAINT [DF_RETURNED_AMOUNT_162099618] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_162099618] DEFAULT ((0)),
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_162099618] DEFAULT ('O'),
    [SPARE_QTY] float NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [CALLBACK_TYPE] nchar(10) NULL,
    [CALLBACK_NO] nchar(20) NULL,
    [CALLBACK_SERIAL_NO] smallint NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [FINISHED_QTY] float NULL,
    [CLIENT_OTHER_NO] nvarchar(50) NULL,
    [CLIENT_ACCEPT_NO] nvarchar(50) NULL CONSTRAINT [DF_CLIENT_ACCEPT_NO_162099618] DEFAULT (''),
    [BAD_DEPOT_ID] nchar(10) NULL,
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_162099618] DEFAULT ((0)),
    [BAD_SPARE_QTY] float NULL CONSTRAINT [DF_BAD_SPARE_QTY_162099618] DEFAULT ((0)),
    [GOOD_QTY] float NULL CONSTRAINT [DF_GOOD_QTY_162099618] DEFAULT ((0)),
    [GOOD_SPARE_QTY] float NULL CONSTRAINT [DF_GOOD_SPARE_QTY_162099618] DEFAULT ((0)),
    [REQUIR_PRODUCE] bit NULL,
    [RE_PRODUCE_TYPE] nchar(10) NULL,
    [RE_PRODUCE_NO] nchar(20) NULL,
    CONSTRAINT [PK_COP_RETURN_D] PRIMARY KEY CLUSTERED ([RETURN_TYPE], [RETURN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_RETURN_D_3] ON dbo.[COP_RETURN_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_RETURN_D_2] ON dbo.[COP_RETURN_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTG_2] ON dbo.[COP_RETURN_D] ([SEND_TYPE], [SEND_NO], [SEND_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_RETURN_D] ON dbo.[COP_RETURN_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_RETURN_D_1] ON dbo.[COP_RETURN_D] ([SEND_TYPE], [SEND_NO], [SEND_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTG_3] ON dbo.[COP_RETURN_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IDX_COPTG_1] ON dbo.[COP_RETURN_D] ([RETURN_TYPE], [RETURN_NO]);
GO

------------------------------------------------------------------------------
-- COP_RETURN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_RETURN_M] (
    [RETURN_TYPE] nchar(10) NOT NULL,
    [RETURN_NO] nchar(20) NOT NULL,
    [RETURN_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_178099675] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_178099675] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [INVOICE_NO] nvarchar(50) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_178099675] DEFAULT ('O'),
    [CLIENT_NAME] nvarchar(60) NULL,
    [SEND_TAG] bit NULL CONSTRAINT [DF_SEND_TAG_178099675] DEFAULT ((0)),
    CONSTRAINT [PK_COP_RETURN_M] PRIMARY KEY CLUSTERED ([RETURN_TYPE], [RETURN_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_RETURN_M] ON dbo.[COP_RETURN_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_SEND_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_SEND_D] (
    [SEND_TYPE] nchar(10) NOT NULL,
    [SEND_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [BATCH_NO] nchar(30) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_194099732] DEFAULT ((100)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [RECEIVED_AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_194099732] DEFAULT ((0)),
    [RETURN_QTY] float NULL CONSTRAINT [DF_RETURN_QTY_194099732] DEFAULT ((0)),
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_194099732] DEFAULT ('O'),
    [SPARE_QTY] float NULL,
    [RETURN_SPARE_QTY] float NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [FITOUT_TYPE] nchar(10) NULL,
    [FITOUT_NO] nchar(20) NULL,
    [FITOUT_SERIAL_NO] smallint NULL,
    [CALLBACK_TYPE] nchar(10) NULL,
    [CALLBACK_NO] nchar(20) NULL,
    [CALLBACK_SERIAL_NO] smallint NULL,
    [CLIENT_OTHER_NO] nvarchar(50) NULL,
    [PCS_COUNT] nvarchar(50) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_194099732] DEFAULT ((0)),
    [CLIENT_ACCEPT_NO] nvarchar(50) NULL CONSTRAINT [DF_CLIENT_ACCEPT_NO_194099732] DEFAULT (''),
    [ITEM_NO] nvarchar(50) NULL,
    [MO_NO] nvarchar(50) NULL,
    [BOX_PCS] int NULL,
    [PRINT_NO] nvarchar(50) NULL,
    [SHIPMENT_TYPE] nchar(10) NULL,
    [SHIPMENT_NO] nchar(20) NULL,
    [SHIPMENT_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_COP_SEND_D] PRIMARY KEY CLUSTERED ([SEND_TYPE], [SEND_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_SEND_D_1] ON dbo.[COP_SEND_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_SEND_D] ON dbo.[COP_SEND_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_SEND_D_2] ON dbo.[COP_SEND_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO

------------------------------------------------------------------------------
-- COP_SEND_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_SEND_M] (
    [SEND_TYPE] nchar(10) NOT NULL,
    [SEND_NO] nchar(20) NOT NULL,
    [SEND_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [SEND_ADDRESS] nvarchar(200) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_210099789] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_210099789] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [INVOICE_NO] nvarchar(50) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_210099789] DEFAULT ('O'),
    [CLIENT_NAME] nvarchar(60) NULL,
    [CAR_ID] nchar(20) NULL,
    CONSTRAINT [PK_COP_SEND_M] PRIMARY KEY CLUSTERED ([SEND_TYPE], [SEND_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_SEND_M] ON dbo.[COP_SEND_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_SHIPMENT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_SHIPMENT_D] (
    [SHIPMENT_TYPE] nchar(10) NOT NULL,
    [SHIPMENT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [CLIENT_ID] nchar(10) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [BOX_QTY] float NULL,
    [QTY] float NULL,
    [START_BOX_NO] nvarchar(50) NULL,
    [END_BOX_NO] nvarchar(50) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [PRO_NO] nchar(30) NULL,
    [DEPOT_QTY] float NULL,
    [PRODUCE_QTY] float NULL,
    [PACK_SPEC] nvarchar(50) NULL,
    [MOULD_ID] nvarchar(200) NULL,
    [SEND_DATE] nvarchar(50) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [CUBAGE_ALL] float NULL,
    [SUTTLE_ALL] float NULL,
    [REMARK] nvarchar(500) NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_226099846] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_226099846] DEFAULT ((0)),
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [FACT_SEND_DATE] datetime NULL,
    [SEND_TYPE] nchar(10) NULL,
    [SEND_NO] nchar(20) NULL,
    [SEND_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_COP_SHIPMENT_D] PRIMARY KEY CLUSTERED ([SHIPMENT_TYPE], [SHIPMENT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_COP_SHIPMENT_D_1] ON dbo.[COP_SHIPMENT_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_COP_SHIPMENT_D] ON dbo.[COP_SHIPMENT_D] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- COP_SHIPMENT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_SHIPMENT_M] (
    [SHIPMENT_TYPE] nchar(10) NOT NULL,
    [SHIPMENT_NO] nchar(20) NOT NULL,
    [SHIPMENT_DATE] datetime NULL,
    [SHIP_NAME] nvarchar(50) NULL,
    [SHIP_NO] nvarchar(50) NULL,
    [TANK_NO] nvarchar(50) NULL,
    [PRE_SEND_DATE] datetime NULL,
    [SHIPPING_DATE] datetime NULL,
    [ENTRY_DATE] datetime NULL,
    [MAKE_ORDER_DATE] datetime NULL,
    [INVOICE_NO] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_242099903] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_242099903] DEFAULT ((0)),
    CONSTRAINT [PK_COP_SHIPMENT_M] PRIMARY KEY CLUSTERED ([SHIPMENT_TYPE], [SHIPMENT_NO])
);
GO

------------------------------------------------------------------------------
-- COP_TRANSBACK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_TRANSBACK_D] (
    [TRANSBACK_TYPE] nchar(10) NOT NULL,
    [TRANSBACK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [TRANSFER_TYPE] nchar(10) NULL,
    [TRANSFER_NO] nchar(20) NULL,
    [TRANSFER_SERIAL_NO] smallint NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [BATCH_NO] nchar(30) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_258099960] DEFAULT ((100)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_258099960] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [SPARE_QTY] float NULL,
    [PCS_COUNT] nvarchar(50) NULL,
    [BOX_PCS] int NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_258099960] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_258099960] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [IN_DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_COP_TRANSBACK_D] PRIMARY KEY CLUSTERED ([TRANSBACK_TYPE], [TRANSBACK_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- COP_TRANSBACK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_TRANSBACK_M] (
    [TRANSBACK_TYPE] nchar(10) NOT NULL,
    [TRANSBACK_NO] nchar(20) NOT NULL,
    [TRANSBACK_DATE] datetime NULL CONSTRAINT [DF_TRANSBACK_DATE_274100017] DEFAULT (getdate()),
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(60) NULL,
    [SALES_ID] nchar(10) NULL,
    [SEND_ADDRESS] nvarchar(100) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_274100017] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_274100017] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_274100017] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_COP_TRANSBACK_M] PRIMARY KEY CLUSTERED ([TRANSBACK_TYPE], [TRANSBACK_NO])
);
GO

------------------------------------------------------------------------------
-- COP_TRANSFER_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_TRANSFER_D] (
    [TRANSFER_TYPE] nchar(10) NOT NULL,
    [TRANSFER_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [FITOUT_TYPE] nchar(10) NULL,
    [FITOUT_NO] nchar(20) NULL,
    [FITOUT_SERIAL_NO] smallint NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [BATCH_NO] nchar(30) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_290100074] DEFAULT ((100)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_290100074] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [SPARE_QTY] float NULL,
    [PCS_COUNT] nvarchar(50) NULL,
    [BOX_PCS] int NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_290100074] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_290100074] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [IN_DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_COP_TRANSFER_D] PRIMARY KEY CLUSTERED ([TRANSFER_TYPE], [TRANSFER_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- COP_TRANSFER_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[COP_TRANSFER_M] (
    [TRANSFER_TYPE] nchar(10) NOT NULL,
    [TRANSFER_NO] nchar(20) NOT NULL,
    [TRANSFER_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(60) NULL,
    [SALES_ID] nchar(10) NULL,
    [SEND_ADDRESS] nvarchar(100) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_306100131] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_306100131] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_306100131] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_COP_TRANSFER_M] PRIMARY KEY CLUSTERED ([TRANSFER_TYPE], [TRANSFER_NO])
);
GO

------------------------------------------------------------------------------
-- CURR
------------------------------------------------------------------------------
CREATE TABLE dbo.[CURR] (
    [CURR_ID] nchar(10) NOT NULL,
    [CURR_NAME] nchar(30) NULL,
    [CURR_RATE] float NULL,
    [IS_BASE] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CURR] PRIMARY KEY CLUSTERED ([CURR_ID])
);
GO

------------------------------------------------------------------------------
-- CUS_ACCOUNT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_ACCOUNT_D] (
    [ACCOUNT_TYPE] nchar(10) NOT NULL,
    [ACCOUNT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [S_R_TYPE] nchar(10) NULL,
    [S_R_NO] nchar(20) NULL,
    [S_R_SERIAL_NO] smallint NULL,
    [S_R_DATE] datetime NULL,
    [REBATE] float NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [SEND_TYPE] nchar(10) NULL,
    [SEND_NO] nchar(20) NULL,
    [SEND_SERIAL_NO] smallint NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_338100245] DEFAULT ('1'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [CLIENT_ACCEPT_NO] nvarchar(50) NULL CONSTRAINT [DF_CLIENT_ACCEPT_NO_338100245] DEFAULT (''),
    [CLIENT_OTHER_NO] nvarchar(50) NULL,
    [SUTTLE] float NULL,
    [CUS_QTY] float NULL,
    [ACCOUNT_QTY] float NULL,
    [REMARK] nvarchar(200) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [GROSS_WEIGHT] float NULL,
    [CUS_GROSS_QTY] float NULL,
    CONSTRAINT [PK_CUS_ACCOUNT_D] PRIMARY KEY CLUSTERED ([ACCOUNT_TYPE], [ACCOUNT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_ACCOUNT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_ACCOUNT_M] (
    [ACCOUNT_TYPE] nchar(10) NOT NULL,
    [ACCOUNT_NO] nchar(20) NOT NULL,
    [ACCOUNT_DATE] datetime NULL,
    [ACCOUNT_MONTH] nchar(6) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [QTY_TOTAL] float NULL,
    [SUM_WEIGHT] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_354100302] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL CONSTRAINT [DF_TAX_ID_354100302] DEFAULT (''),
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_354100302] DEFAULT ((0)),
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [INVOICE_NO] nvarchar(50) NULL,
    [INVOICE_DATE] datetime NULL,
    [INVOICE_SUM] float NULL,
    [PRE_RECEIVE_DATE] datetime NULL,
    [PROCESS_PRICE] float NULL,
    [PROCESS_AMOUNT] float NULL,
    [RECEIVE_AMOUNT] float NULL,
    [FACT_RECEIVE_DATE] datetime NULL,
    [SUM_AMOUNT] float NULL,
    [BEGIN_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [OTHER_PRICE] float NULL,
    [OTHER_WEIGHT] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_354100302] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_354100302] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CUS_QTY] float NULL,
    [CUS_GROSS_QTY] float NULL,
    [EXPORT_TYPE] nchar(10) NULL,
    [EXPORT_NO] nchar(20) NULL,
    [CURR_ID_NOW] nchar(10) NULL,
    [CURR_RATE_NOW] float NULL,
    [PRICE_NOW] float NULL,
    [AMOUNT_NOW] float NULL,
    [OTHER_QTY] float NULL,
    [PRO_SERIAL_NO] int NULL,
    CONSTRAINT [PK_CUS_ACCOUNT_M] PRIMARY KEY CLUSTERED ([ACCOUNT_TYPE], [ACCOUNT_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_CANCEL_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_CANCEL_M] (
    [CANCEL_TYPE] nchar(10) NOT NULL,
    [CANCEL_NO] nchar(20) NOT NULL,
    [CANCEL_DATE] datetime NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [CORP_NAME] nvarchar(50) NULL,
    [END_DATE] datetime NULL,
    [YHTCKZL] float NULL,
    [SJCKZL] float NULL,
    [ZCLZ] float NULL,
    [SJCKJE] float NULL,
    [YHTJKZL] float NULL,
    [SJJKZL] float NULL,
    [SJJKJE] float NULL,
    [YFHTJGF] float NULL,
    [YFJGF] float NULL,
    [WFJGF] float NULL,
    [BHTYIKFP] float NULL,
    [BHTYINGKFP] float NULL,
    [BHTWKFP] float NULL,
    [HTCE] float NULL,
    [HTYLZL] float NULL,
    [BLS] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_370100359] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_370100359] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PRO_SERIAL_NO] int NULL
);
GO

------------------------------------------------------------------------------
-- CUS_COUNTRY
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_COUNTRY] (
    [COUNTRY_ID] nchar(10) NOT NULL,
    [COUNTRY_NAME] nvarchar(50) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_386100416] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CUS_COUNTRY] PRIMARY KEY CLUSTERED ([COUNTRY_ID])
);
GO

------------------------------------------------------------------------------
-- CUS_CUSTOMS
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_CUSTOMS] (
    [CUSTOMS_ID] nchar(10) NOT NULL,
    [CUSTOMS_NAME] nvarchar(50) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_402100473] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CUS_CUSTOMS] PRIMARY KEY CLUSTERED ([CUSTOMS_ID])
);
GO

------------------------------------------------------------------------------
-- CUS_DEPOT
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_DEPOT] (
    [DEPOT_ID] nchar(10) NOT NULL,
    [DEPOT_NAME] nvarchar(50) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_418100530] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [DEPOT_TYPE] char(1) NULL,
    CONSTRAINT [PK_CUS_DEPOT] PRIMARY KEY CLUSTERED ([DEPOT_ID])
);
GO

------------------------------------------------------------------------------
-- CUS_DRAWBACK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_DRAWBACK_D] (
    [DRAWBACK_TYPE] nchar(10) NOT NULL,
    [DRAWBACK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EXPORT_TYPE] nchar(10) NULL,
    [EXPORT_NO] nchar(20) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [CUS_QTY] float NULL,
    [PROCESS_PRICE] float NULL CONSTRAINT [DF_PROCESS_PRICE_434100587] DEFAULT ((0)),
    [PROCESS_AMOUNT] float NULL CONSTRAINT [DF_PROCESS_AMOUNT_434100587] DEFAULT ((0)),
    [TAX_RATE] float NULL,
    [AMOUNT_TAX] float NULL,
    [AMOUNT] float NULL,
    [SUB_AMOUNT] float NULL,
    [SUB_RATE] float NULL,
    [REMARK] nvarchar(300) NULL,
    CONSTRAINT [PK_CUS_DRAWBACK_D] PRIMARY KEY CLUSTERED ([DRAWBACK_TYPE], [DRAWBACK_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_DRAWBACK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_DRAWBACK_M] (
    [DRAWBACK_TYPE] nchar(10) NOT NULL,
    [DRAWBACK_NO] nchar(20) NOT NULL,
    [DRAWBACK_DATE] datetime NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CORP_NAME] nvarchar(50) NULL,
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_450100644] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_450100644] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_450100644] DEFAULT ((0)),
    CONSTRAINT [PK_CUS_DRAWBACK_M] PRIMARY KEY CLUSTERED ([DRAWBACK_TYPE], [DRAWBACK_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_EXPORT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_EXPORT_D] (
    [EXPORT_TYPE] nchar(10) NOT NULL,
    [EXPORT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [PRO_SERIAL_NO] int NULL,
    [PRO_ID] nvarchar(50) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [CUS_QTY] float NULL,
    [COUNTRY_ID] nchar(10) NULL,
    [IMPOSE_ID] nchar(10) NULL,
    [NON_INSURE_RATE] float NULL,
    [REMARK] nvarchar(300) NULL,
    [PO_NO] nvarchar(50) NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [BATCH_NO] nvarchar(50) NULL,
    [IN_QTY] float NULL,
    [OUT_QTY] float NULL,
    [PRO_DESC] nvarchar(200) NULL,
    [CURR_ID] nchar(10) NULL,
    [PROCESS_PRICE] float NULL CONSTRAINT [DF_PROCESS_PRICE_466100701] DEFAULT ((0)),
    [PROCESS_AMOUNT] float NULL CONSTRAINT [DF_PROCESS_AMOUNT_466100701] DEFAULT ((0)),
    [NOW_PRICE] float NULL CONSTRAINT [DF_NOW_PRICE_466100701] DEFAULT ((0)),
    [NOW_AMOUNT] float NULL CONSTRAINT [DF_NOW_AMOUNT_466100701] DEFAULT ((0)),
    [NOW_CURR_ID] nchar(10) NULL,
    [NOW_CURR_RATE] float NULL CONSTRAINT [DF_NOW_CURR_RATE_466100701] DEFAULT ((1)),
    [ACCOUNT_TYPE] nchar(10) NULL,
    [ACCOUNT_NO] nchar(20) NULL,
    CONSTRAINT [PK_CUS_EXPORT_D] PRIMARY KEY CLUSTERED ([EXPORT_TYPE], [EXPORT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_EXPORT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_EXPORT_M] (
    [EXPORT_TYPE] nchar(10) NOT NULL,
    [EXPORT_NO] nchar(20) NOT NULL,
    [EXPORT_DATE] smalldatetime NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [EXPORT_WAY] nchar(2) NULL,
    [DECLARE_DATE] smalldatetime NULL,
    [DECLARE_TYPE] nchar(1) NULL,
    [PORT_NAME] nvarchar(50) NULL,
    [PREKEY_NO] nvarchar(50) NULL,
    [CUSTOMS_ID] nchar(10) NULL,
    [CTN_QTY] float NULL,
    [GROSS_WEIGHT] float NULL,
    [NET_WEIGHT] float NULL,
    [PACK_TYPE] nvarchar(100) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [INVOICE_NO] nvarchar(100) NULL,
    [VERIFY_NO] nvarchar(100) NULL,
    [SEND_CORP] nvarchar(100) NULL,
    [RECEIVE_CORP] nvarchar(100) NULL,
    [TRAFFIC_TYPE] nvarchar(100) NULL,
    [TRAFFIC_TOOL] nvarchar(100) NULL,
    [DELIVERY_NO] nvarchar(100) NULL,
    [TRADE_TYPE] nvarchar(50) NULL,
    [IMPOSE_ID] nchar(10) NULL,
    [SETTLE_TYPE] nvarchar(50) NULL,
    [LICENSE_NO] nvarchar(50) NULL,
    [COUNTRY_ID] nchar(10) NULL,
    [EXPORT_PORT] nvarchar(50) NULL,
    [SOURCE_ADDR] nvarchar(100) NULL,
    [APPROVE_NO] nvarchar(50) NULL,
    [DEAL_TYPE] nvarchar(50) NULL,
    [FREIGHT] float NULL,
    [PREMIUM] float NULL,
    [FEES] float NULL,
    [AGREE_NO] nvarchar(50) NULL,
    [CONTAINER] nvarchar(50) NULL,
    [ADDTION_BILLS] nvarchar(50) NULL,
    [USES] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_482100758] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [SEAL_TYPE] nchar(10) NULL,
    [SEAL_NO] nchar(20) NULL,
    [DRAWBACK_AMOUNT_TAX] float NULL CONSTRAINT [DF_DRAWBACK_AMOUNT_TAX_482100758] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_482100758] DEFAULT ((0)),
    [ACCOUNT_TYPE] nchar(10) NULL,
    [ACCOUNT_NO] nchar(20) NULL,
    CONSTRAINT [PK_CUS_EXPORT_M] PRIMARY KEY CLUSTERED ([EXPORT_TYPE], [EXPORT_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_EXPORT_MAT
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_EXPORT_MAT] (
    [EXPORT_TYPE] nchar(10) NOT NULL,
    [EXPORT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_498100815] DEFAULT ((0)),
    [IN_QTY] float NULL CONSTRAINT [DF_IN_QTY_498100815] DEFAULT ((0)),
    [OUT_QTY] float NULL CONSTRAINT [DF_OUT_QTY_498100815] DEFAULT ((0)),
    [PUR_QTY] float NULL CONSTRAINT [DF_PUR_QTY_498100815] DEFAULT ((0)),
    [PO_NO] nvarchar(50) NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [BATCH_NO] nvarchar(50) NULL,
    [MAT_SERIAL_NO] int NOT NULL,
    CONSTRAINT [PK_CUS_EXPORT_MAT] PRIMARY KEY CLUSTERED ([EXPORT_TYPE], [EXPORT_NO], [MAT_SERIAL_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_IMPORT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_IMPORT_D] (
    [IMPORT_TYPE] nchar(10) NOT NULL,
    [IMPORT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [MAT_SERIAL_NO] int NULL,
    [PRO_ID] nvarchar(50) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [CUS_QTY] float NULL,
    [COUNTRY_ID] nchar(10) NULL,
    [IMPOSE_ID] nchar(10) NULL,
    [NON_INSURE_RATE] float NULL,
    [REMARK] nvarchar(300) NULL,
    [PUR_QTY] float NULL,
    [PRO_DESC] nvarchar(200) NULL,
    [CURR_ID] nchar(10) NULL,
    [NOW_PRICE] float NULL CONSTRAINT [DF_NOW_PRICE_514100872] DEFAULT ((0)),
    [NOW_AMOUNT] float NULL CONSTRAINT [DF_NOW_AMOUNT_514100872] DEFAULT ((0)),
    [NOW_CURR_ID] nchar(10) NULL,
    [NOW_CURR_RATE] float NULL CONSTRAINT [DF_NOW_CURR_RATE_514100872] DEFAULT ((1)),
    CONSTRAINT [PK_CUS_IMPORT_D] PRIMARY KEY CLUSTERED ([IMPORT_TYPE], [IMPORT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_IMPORT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_IMPORT_M] (
    [IMPORT_TYPE] nchar(10) NOT NULL,
    [IMPORT_NO] nchar(20) NOT NULL,
    [IMPORT_DATE] smalldatetime NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [EXPORT_WAY] nchar(2) NULL,
    [DECLARE_DATE] smalldatetime NULL,
    [DECLARE_TYPE] nchar(1) NULL,
    [PORT_NAME] nvarchar(50) NULL,
    [PREKEY_NO] nvarchar(50) NULL,
    [CUSTOMS_ID] nchar(10) NULL,
    [CTN_QTY] float NULL,
    [GROSS_WEIGHT] float NULL,
    [NET_WEIGHT] float NULL,
    [PACK_TYPE] nvarchar(100) NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [INVOICE_NO] nvarchar(100) NULL,
    [VERIFY_NO] nvarchar(100) NULL,
    [SEND_CORP] nvarchar(100) NULL,
    [RECEIVE_CORP] nvarchar(100) NULL,
    [TRAFFIC_TYPE] nvarchar(100) NULL,
    [TRAFFIC_TOOL] nvarchar(100) NULL,
    [DELIVERY_NO] nvarchar(100) NULL,
    [TRADE_TYPE] nvarchar(50) NULL,
    [IMPOSE_ID] nchar(10) NULL,
    [SETTLE_TYPE] nvarchar(50) NULL,
    [LICENSE_NO] nvarchar(50) NULL,
    [COUNTRY_ID] nchar(10) NULL,
    [EXPORT_PORT] nvarchar(50) NULL,
    [SOURCE_ADDR] nvarchar(100) NULL,
    [APPROVE_NO] nvarchar(50) NULL,
    [DEAL_TYPE] nvarchar(50) NULL,
    [FREIGHT] float NULL,
    [PREMIUM] float NULL,
    [FEES] float NULL,
    [AGREE_NO] nvarchar(50) NULL,
    [CONTAINER] nvarchar(50) NULL,
    [ADDTION_BILLS] nvarchar(50) NULL,
    [USES] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_530100929] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_530100929] DEFAULT ((0)),
    CONSTRAINT [PK_CUS_IMPORT_M] PRIMARY KEY CLUSTERED ([IMPORT_TYPE], [IMPORT_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_IMPOSE
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_IMPOSE] (
    [IMPOSE_ID] nchar(10) NOT NULL,
    [IMPOSE_NAME] nvarchar(50) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_546100986] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CUS_IMPOSE] PRIMARY KEY CLUSTERED ([IMPOSE_ID])
);
GO

------------------------------------------------------------------------------
-- CUS_MANUAL_BOM
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_MANUAL_BOM] (
    [MANUAL_NO] nvarchar(50) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [BOM_SERIAL_NO] int NOT NULL,
    [PRO_ID] nvarchar(50) NULL,
    [USE_QTY] float NULL,
    [LOST_RATE] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_CUS_MANUAL_BOM] PRIMARY KEY CLUSTERED ([MANUAL_NO], [SERIAL_NO], [BOM_SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_MANUAL_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_MANUAL_M] (
    [MANUAL_NO] nvarchar(50) NOT NULL,
    [IMPORT_NO] nvarchar(50) NULL,
    [EXPORT_NO] nvarchar(50) NULL,
    [MANUAL_STATE] nchar(1) NULL,
    [CUSTOMS_ID] nchar(10) NULL,
    [COUNTRY_ID] nchar(10) NULL,
    [TRADE_TYPE] nvarchar(50) NULL,
    [IMPOSE_ID] nchar(10) NULL,
    [AFFIRM_NO] nvarchar(50) NULL,
    [BEGIN_DATE] smalldatetime NULL,
    [END_DATE] smalldatetime NULL,
    [LATE_DATE] smalldatetime NULL,
    [OFF_DATE] smalldatetime NULL,
    [CURR_ID] nchar(10) NULL,
    [PORT_NAME] nvarchar(50) NULL,
    [CORP_NAME] nvarchar(50) NULL,
    [IS_CONTAIN_LOST] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_578101100] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_578101100] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PROCESS_PRICE] float NULL CONSTRAINT [DF_PROCESS_PRICE_578101100] DEFAULT ((0)),
    [PROCESS_AMOUNT] float NULL CONSTRAINT [DF_PROCESS_AMOUNT_578101100] DEFAULT ((0)),
    [CUS_QTY] float NULL CONSTRAINT [DF_CUS_QTY_578101100] DEFAULT ((0)),
    [FINISHED_AMOUNT_TAX] float NULL CONSTRAINT [DF_FINISHED_AMOUNT_TAX_578101100] DEFAULT ((0)),
    [LOST_RATE] float NULL,
    CONSTRAINT [PK_CUS_MANUAL_M] PRIMARY KEY CLUSTERED ([MANUAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_MANUAL_MAT
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_MANUAL_MAT] (
    [MANUAL_NO] nvarchar(50) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [PRO_ID] nvarchar(50) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_594101157] DEFAULT ((0)),
    [PRICE] float NULL CONSTRAINT [DF_PRICE_594101157] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_594101157] DEFAULT ((0)),
    [CUS_QTY] float NULL CONSTRAINT [DF_CUS_QTY_594101157] DEFAULT ((0)),
    [COUNTRY_ID] nchar(10) NULL,
    [IMPOSE_ID] nchar(10) NULL,
    [PROCESS_PRICE] float NULL CONSTRAINT [DF_PROCESS_PRICE_594101157] DEFAULT ((0)),
    [PROCESS_AMOUNT] float NULL CONSTRAINT [DF_PROCESS_AMOUNT_594101157] DEFAULT ((0)),
    [IMP_QTY] float NULL CONSTRAINT [DF_IMP_QTY_594101157] DEFAULT ((0)),
    [OUT_QTY] float NULL CONSTRAINT [DF_OUT_QTY_594101157] DEFAULT ((0)),
    [TRAN_QTY] float NULL CONSTRAINT [DF_TRAN_QTY_594101157] DEFAULT ((0)),
    [IN_QTY] float NULL CONSTRAINT [DF_IN_QTY_594101157] DEFAULT ((0)),
    [NOT_BOND_RATE] float NULL CONSTRAINT [DF_NOT_BOND_RATE_594101157] DEFAULT ((0)),
    [OFF_QTY] float NULL CONSTRAINT [DF_OFF_QTY_594101157] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [ZR_MANUAL_NO] nvarchar(50) NULL,
    [ZR_SERIAL_NO] int NULL,
    [ZR_QTY] float NULL CONSTRAINT [DF_ZR_QTY_594101157] DEFAULT ((0)),
    [ZC_MANUAL_NO] nvarchar(50) NULL,
    [ZC_SERIAL_NO] int NULL,
    [ZC_QTY] float NULL CONSTRAINT [DF_ZC_QTY_594101157] DEFAULT ((0)),
    [BF_QTY] float NULL CONSTRAINT [DF_BF_QTY_594101157] DEFAULT ((0)),
    [PUR_QTY] float NULL CONSTRAINT [DF_PUR_QTY_594101157] DEFAULT ((0)),
    CONSTRAINT [PK_CUS_MANUAL_MAT] PRIMARY KEY CLUSTERED ([MANUAL_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_MANUAL_PRO
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_MANUAL_PRO] (
    [MANUAL_NO] nvarchar(50) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [PRO_ID] nvarchar(50) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_610101214] DEFAULT ((0)),
    [PRICE] float NULL CONSTRAINT [DF_PRICE_610101214] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_610101214] DEFAULT ((0)),
    [CUS_QTY] float NULL CONSTRAINT [DF_CUS_QTY_610101214] DEFAULT ((0)),
    [COUNTRY_ID] nchar(10) NULL,
    [IMPOSE_ID] nchar(10) NULL,
    [USE_STATE] bit NULL,
    [PROCESS_PRICE] float NULL CONSTRAINT [DF_PROCESS_PRICE_610101214] DEFAULT ((0)),
    [PROCESS_AMOUNT] float NULL CONSTRAINT [DF_PROCESS_AMOUNT_610101214] DEFAULT ((0)),
    [EXP_QTY] float NULL CONSTRAINT [DF_EXP_QTY_610101214] DEFAULT ((0)),
    [OUT_QTY] float NULL CONSTRAINT [DF_OUT_QTY_610101214] DEFAULT ((0)),
    [TRAN_QTY] float NULL CONSTRAINT [DF_TRAN_QTY_610101214] DEFAULT ((0)),
    [IN_QTY] float NULL CONSTRAINT [DF_IN_QTY_610101214] DEFAULT ((0)),
    [OFF_QTY] float NULL CONSTRAINT [DF_OFF_QTY_610101214] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [ZR_MANUAL_NO] nvarchar(50) NULL,
    [ZR_SERIAL_NO] int NULL,
    [ZR_QTY] float NULL,
    [ZC_MANUAL_NO] nvarchar(50) NULL,
    [ZC_SERIAL_NO] int NULL,
    [ZC_QTY] float NULL,
    CONSTRAINT [PK_CUS_MANUAL_PRO] PRIMARY KEY CLUSTERED ([MANUAL_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_MATERIN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_MATERIN_D] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PO_NO] nvarchar(50) NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [PRO_DESC] nvarchar(200) NULL,
    [QTY] float NULL,
    [REMARK] nvarchar(300) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [MAT_SERIAL_NO] int NULL,
    [DEPOT_ID] nchar(10) NULL,
    [EXPORT_TYPE] nchar(10) NULL,
    [EXPORT_NO] nchar(20) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [CUS_QTY] float NULL,
    [PURCHASE_TYPE] nchar(10) NULL,
    [PURCHASE_NO] nchar(20) NULL,
    [PURCHASE_SERIAL_NO] smallint NULL,
    [BATCH_NO] nvarchar(50) NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [EXPORT_SERIAL_NO] int NULL,
    CONSTRAINT [PK_CUS_MATERIN_D] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_MATERIN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_MATERIN_M] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [BILL_DATE] smalldatetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_642101328] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [DECLARE_TYPE] nchar(1) NULL,
    [DECLARATION_NO] nvarchar(50) NULL,
    [SEND_NO] nvarchar(50) NULL,
    CONSTRAINT [PK_CUS_MATERIN_M] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_MATEROUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_MATEROUT_D] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PO_NO] nvarchar(50) NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [PRO_DESC] nvarchar(200) NULL,
    [QTY] float NULL,
    [REMARK] nvarchar(300) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [MAT_SERIAL_NO] int NULL,
    [DEPOT_ID] nchar(10) NULL,
    [EXPORT_TYPE] nchar(10) NULL,
    [EXPORT_NO] nchar(20) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [CUS_QTY] float NULL,
    [BATCH_NO] nvarchar(50) NULL,
    [EXPORT_SERIAL_NO] int NULL,
    CONSTRAINT [PK_CUS_MATEROUT_D] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_MATEROUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_MATEROUT_M] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [BILL_DATE] smalldatetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_674101442] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [DECLARE_TYPE] nchar(1) NULL,
    CONSTRAINT [PK_CUS_MATEROUT_M] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PACK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PACK_D] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [JZX_NO] nvarchar(50) NULL,
    [MARKS] nvarchar(200) NULL,
    [PRO_DESC] nvarchar(200) NULL,
    [CTNS] float NULL,
    [QTY] float NULL,
    [G_W] float NULL,
    [N_W] float NULL,
    [REMARK] nvarchar(300) NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [BATCH_NO] nvarchar(50) NULL,
    [BOX_QTY] int NULL,
    [EXPORT_TYPE] nchar(10) NULL,
    [EXPORT_NO] nchar(20) NULL,
    CONSTRAINT [PK_CUS_PACK_D] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PACK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PACK_M] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [BILL_DATE] smalldatetime NULL,
    [CLIENT_NAME] nvarchar(100) NULL,
    [INVOICE_NO] nvarchar(50) NULL,
    [HT_NO] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_706101556] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CUS_PACK_M] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PRO_DEPOT
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PRO_DEPOT] (
    [PRO_ID] nchar(30) NOT NULL,
    [DEPOT_ID] nchar(10) NOT NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_866102126] DEFAULT ((0)),
    [CUS_QTY] float NULL,
    CONSTRAINT [PK_CUS_PRO_DEPOT] PRIMARY KEY CLUSTERED ([PRO_ID], [DEPOT_ID])
);
GO

------------------------------------------------------------------------------
-- CUS_PRO_DEPOT_LOG
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PRO_DEPOT_LOG] (
    [PRO_ID] nchar(30) NOT NULL,
    [IN_OUT] char(1) NOT NULL,
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [BILL_SERIAL_NO] smallint NOT NULL,
    [BILL_DATE] datetime NOT NULL,
    [DEPOT_ID] nchar(10) NOT NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_882102183] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CUS_QTY] float NULL CONSTRAINT [DF_CUS_QTY_882102183] DEFAULT ((0)),
    CONSTRAINT [PK_CUS_PRO_DEPOT_LOG] PRIMARY KEY CLUSTERED ([PRO_ID], [IN_OUT], [BILL_TYPE], [BILL_NO], [BILL_SERIAL_NO], [BILL_DATE], [DEPOT_ID])
);
GO

------------------------------------------------------------------------------
-- CUS_PROCESS_INVOICE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PROCESS_INVOICE_D] (
    [PROCESS_INVOICE_TYPE] nchar(10) NOT NULL,
    [PROCESS_INVOICE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [PROCESS_AMOUNT] float NULL CONSTRAINT [DF_PROCESS_AMOUNT_722101613] DEFAULT ((0)),
    [FINISHED_PROCESS_AMOUNT] float NULL CONSTRAINT [DF_FINISHED_PROCESS_AMOUNT_722101613] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_722101613] DEFAULT ((0)),
    [REMARK] nvarchar(300) NULL,
    CONSTRAINT [PK_CUS_PROCESS_INVOICE_D] PRIMARY KEY CLUSTERED ([PROCESS_INVOICE_TYPE], [PROCESS_INVOICE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PROCESS_INVOICE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PROCESS_INVOICE_M] (
    [PROCESS_INVOICE_TYPE] nchar(10) NOT NULL,
    [PROCESS_INVOICE_NO] nchar(20) NOT NULL,
    [PROCESS_INVOICE_DATE] datetime NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [CORP_NAME] nvarchar(50) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_738101670] DEFAULT ((17)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_738101670] DEFAULT ((0)),
    [INVOICE_TYPE] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_738101670] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_738101670] DEFAULT ((0)),
    CONSTRAINT [PK_CUS_PROCESS_INVOICE_M] PRIMARY KEY CLUSTERED ([PROCESS_INVOICE_TYPE], [PROCESS_INVOICE_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PROCESS_PAY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PROCESS_PAY_D] (
    [PROCESS_PAY_TYPE] nchar(10) NOT NULL,
    [PROCESS_PAY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [PROCESS_PRICE] float NULL CONSTRAINT [DF_PROCESS_PRICE_754101727] DEFAULT ((0)),
    [PROCESS_AMOUNT] float NULL CONSTRAINT [DF_PROCESS_AMOUNT_754101727] DEFAULT ((0)),
    [CUS_QTY] float NULL CONSTRAINT [DF_CUS_QTY_754101727] DEFAULT ((0)),
    [SUB_RATE] float NULL CONSTRAINT [DF_SUB_RATE_754101727] DEFAULT ((0)),
    [SUB_AMOUNT] float NULL CONSTRAINT [DF_SUB_AMOUNT_754101727] DEFAULT ((0)),
    [CURR_RATE] float NULL CONSTRAINT [DF_CURR_RATE_754101727] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_754101727] DEFAULT ((0)),
    [REMARK] nvarchar(300) NULL,
    CONSTRAINT [PK_CUS_PROCESS_PAY_D] PRIMARY KEY CLUSTERED ([PROCESS_PAY_TYPE], [PROCESS_PAY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PROCESS_PAY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PROCESS_PAY_M] (
    [PROCESS_PAY_TYPE] nchar(10) NOT NULL,
    [PROCESS_PAY_NO] nchar(20) NOT NULL,
    [PROCESS_PAY_DATE] datetime NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [CORP_NAME] nvarchar(50) NULL,
    [INVOICE_TYPE] nvarchar(50) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_770101784] DEFAULT ((17)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_770101784] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_770101784] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_770101784] DEFAULT ((0)),
    CONSTRAINT [PK_CUS_PROCESS_PAY_M] PRIMARY KEY CLUSTERED ([PROCESS_PAY_TYPE], [PROCESS_PAY_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PRODUCT
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PRODUCT] (
    [PRO_ID] nvarchar(50) NOT NULL,
    [PRO_NO] nvarchar(50) NOT NULL,
    [PRO_NAME] nvarchar(50) NOT NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [PRO_SORT] nchar(1) NULL,
    [REPORT_UNIT_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [UNIT_RATE] float NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [IMPOSE_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_786101841] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PRO_TYPE] nchar(1) NULL,
    [WEIGHT] float NULL,
    [INSIDE_NAME] nvarchar(200) NULL,
    [INSIDE_SPEC] nvarchar(50) NULL,
    CONSTRAINT [PK_CUS_PRODUCT] PRIMARY KEY CLUSTERED ([PRO_ID])
);
GO

------------------------------------------------------------------------------
-- CUS_PROIN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PROIN_D] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PO_NO] nvarchar(50) NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [PRO_DESC] nvarchar(200) NULL,
    [QTY] float NULL,
    [REMARK] nvarchar(300) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [PRO_SERIAL_NO] int NULL,
    [DEPOT_ID] nchar(10) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [CUS_QTY] float NULL,
    [BATCH_NO] nvarchar(50) NULL,
    [EXPORT_TYPE] nchar(10) NULL,
    [EXPORT_NO] nchar(20) NULL,
    [EXPORT_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_CUS_PROIN_D] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PROIN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PROIN_M] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [BILL_DATE] smalldatetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_818101955] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [DECLARE_TYPE] nchar(1) NULL,
    CONSTRAINT [PK_CUS_PROIN_M] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PROOUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PROOUT_D] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PO_NO] nvarchar(50) NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [PRO_DESC] nvarchar(200) NULL,
    [QTY] float NULL,
    [BGD_NO] nvarchar(50) NULL,
    [HT_NO] nvarchar(50) NULL,
    [SEND_NO] nvarchar(50) NULL,
    [REMARK] nvarchar(300) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [PRO_SERIAL_NO] int NULL,
    [DEPOT_ID] nchar(10) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [CUS_QTY] float NULL,
    [BATCH_NO] nvarchar(50) NULL,
    [EXPORT_TYPE] nchar(10) NULL,
    [EXPORT_NO] nchar(20) NULL,
    [EXPORT_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_CUS_PROOUT_D] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PROOUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PROOUT_M] (
    [BILL_TYPE] nchar(10) NOT NULL,
    [BILL_NO] nchar(20) NOT NULL,
    [BILL_DATE] smalldatetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_850102069] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [DECLARE_TYPE] nchar(1) NULL,
    [DECLARATION_NO] nvarchar(50) NULL,
    [PACK_NO] nchar(20) NULL,
    CONSTRAINT [PK_CUS_PROOUT_M] PRIMARY KEY CLUSTERED ([BILL_TYPE], [BILL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PURCHASE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PURCHASE_D] (
    [PURCHASE_TYPE] nchar(10) NOT NULL,
    [PURCHASE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [MAT_SERIAL_NO] int NULL,
    [EXPORT_TYPE] nchar(10) NULL,
    [EXPORT_NO] nchar(20) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [PO_NO] nvarchar(50) NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [PRO_DESC] nvarchar(200) NULL,
    [BATCH_NO] nvarchar(50) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [CUS_QTY] float NULL,
    [FINISHED_QTY] float NULL,
    [PLAN_DELIVERY_DATE] datetime NULL,
    [REMARK] nvarchar(300) NULL,
    [EXPORT_SERIAL_NO] int NULL,
    [IMPORT_TYPE] nchar(10) NULL,
    [IMPORT_NO] nchar(20) NULL,
    CONSTRAINT [PK_CUS_PURCHASE_D] PRIMARY KEY CLUSTERED ([PURCHASE_TYPE], [PURCHASE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_PURCHASE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_PURCHASE_M] (
    [PURCHASE_TYPE] nchar(10) NOT NULL,
    [PURCHASE_NO] nchar(20) NOT NULL,
    [PURCHASE_DATE] smalldatetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [DECLARE_TYPE] nchar(1) NULL,
    [CURR_ID] nchar(10) NULL,
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_914102297] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_914102297] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CUS_PURCHASE_M] PRIMARY KEY CLUSTERED ([PURCHASE_TYPE], [PURCHASE_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_SEAL_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_SEAL_D] (
    [SEAL_TYPE] nchar(10) NOT NULL,
    [SEAL_NO] nchar(20) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [CLIENT_MANUAL_NO] nvarchar(50) NULL,
    [PRO_ID] nvarchar(50) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_930102354] DEFAULT ((0)),
    [CUS_QTY] float NULL CONSTRAINT [DF_CUS_QTY_930102354] DEFAULT ((0)),
    [PRICE] float NULL CONSTRAINT [DF_PRICE_930102354] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_930102354] DEFAULT ((0)),
    [PROCESS_PRICE] float NULL CONSTRAINT [DF_PROCESS_PRICE_930102354] DEFAULT ((0)),
    [PROCESS_AMOUNT] float NULL CONSTRAINT [DF_PROCESS_AMOUNT_930102354] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_CUS_SEAL_D] PRIMARY KEY CLUSTERED ([SEAL_TYPE], [SEAL_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_SEAL_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_SEAL_M] (
    [SEAL_TYPE] nchar(10) NOT NULL,
    [SEAL_NO] nchar(20) NOT NULL,
    [SEAL_DATE] datetime NULL,
    [MANUAL_NO] nvarchar(50) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [IMPORT_NO] nvarchar(50) NULL,
    [MANUAL_EXPORT_NO] nvarchar(50) NULL,
    [EXPORT_TYPE] nchar(10) NULL,
    [EXPORT_NO] nchar(20) NULL,
    [CUSTOMS_ID] nchar(10) NULL,
    [COUNTRY_ID] nchar(10) NULL,
    [TRADE_TYPE] nvarchar(50) NULL,
    [IMPOSE_ID] nchar(10) NULL,
    [BEGIN_DATE] smalldatetime NULL,
    [END_DATE] smalldatetime NULL,
    [LATE_DATE] smalldatetime NULL,
    [CURR_ID] nchar(10) NULL,
    [PORT_NAME] nvarchar(50) NULL,
    [CORP_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_946102411] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_946102411] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CUS_SEAL_M] PRIMARY KEY CLUSTERED ([SEAL_TYPE], [SEAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_TAX
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_TAX] (
    [TAX_ID] nchar(10) NOT NULL,
    [TAX_NAME] nvarchar(50) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_962102468] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CUS_TAX] PRIMARY KEY CLUSTERED ([TAX_ID])
);
GO

------------------------------------------------------------------------------
-- CUS_TRANSFER_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_TRANSFER_D] (
    [TRANSFER_TYPE] nchar(10) NOT NULL,
    [TRANSFER_NO] nchar(20) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [PRO_ID] nvarchar(50) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_978102525] DEFAULT ((0)),
    [ZR_MANUAL_NO] nvarchar(50) NULL,
    [ZR_SERIAL_NO] int NULL,
    [ZC_MANUAL_NO] nvarchar(50) NULL,
    [ZC_SERIAL_NO] int NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_CUS_TRANSFER_D] PRIMARY KEY CLUSTERED ([TRANSFER_TYPE], [TRANSFER_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- CUS_TRANSFER_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[CUS_TRANSFER_M] (
    [TRANSFER_TYPE] nchar(10) NOT NULL,
    [TRANSFER_NO] nchar(20) NOT NULL,
    [TRANSFER_DATE] datetime NULL,
    [ZC_MANUAL_NO] nvarchar(50) NULL,
    [ZR_MANUAL_NO] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_994102582] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_994102582] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_CUS_TRANSFER_M] PRIMARY KEY CLUSTERED ([TRANSFER_TYPE], [TRANSFER_NO])
);
GO

------------------------------------------------------------------------------
-- DEPOT
------------------------------------------------------------------------------
CREATE TABLE dbo.[DEPOT] (
    [DEPOT_ID] nchar(10) NOT NULL,
    [DEPOT_NAME] nvarchar(50) NOT NULL,
    [TEL] nvarchar(50) NULL,
    [ADDRESS] nvarchar(60) NULL,
    [PRINCIPAL] nvarchar(50) NULL,
    [MRP] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_DEPOT] PRIMARY KEY CLUSTERED ([DEPOT_ID])
);
GO

------------------------------------------------------------------------------
-- DEPT
------------------------------------------------------------------------------
CREATE TABLE dbo.[DEPT] (
    [DEPT_ID] nchar(10) NOT NULL,
    [DEPT_NAME] nvarchar(50) NULL,
    [TEL] nvarchar(50) NULL,
    [EMP_ID_LEADER] nvarchar(50) NULL,
    [DEPT_ID_SUPERIOR] nchar(10) NULL,
    [COMPANY_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_DEPT] PRIMARY KEY CLUSTERED ([DEPT_ID])
);
GO

------------------------------------------------------------------------------
-- ERP_SCHEMA_JOURNAL
------------------------------------------------------------------------------
CREATE TABLE dbo.[ERP_SCHEMA_JOURNAL] (
    [Id] int IDENTITY(1,1) NOT NULL,
    [ScriptName] nvarchar(255) NOT NULL,
    [Applied] datetime NOT NULL,
    CONSTRAINT [PK_ERP_SCHEMA_JOURNAL] PRIMARY KEY CLUSTERED ([Id])
);
GO

------------------------------------------------------------------------------
-- EXPRESSION_WHITELIST_VERSION
------------------------------------------------------------------------------
CREATE TABLE dbo.[EXPRESSION_WHITELIST_VERSION] (
    [VERSION] int NOT NULL,
    [REMARK] nvarchar(500) NOT NULL,
    [EFFECTIVE_DATE] datetime NOT NULL CONSTRAINT [DF_EFFECTIVE_DATE_1013630704] DEFAULT (getdate()),
    CONSTRAINT [PK_EXPRESSION_WHITELIST_VERSION] PRIMARY KEY CLUSTERED ([VERSION])
);
GO

------------------------------------------------------------------------------
-- FIELD_DATASOURCE
------------------------------------------------------------------------------
CREATE TABLE dbo.[FIELD_DATASOURCE] (
    [ID] int IDENTITY(1,1) NOT NULL,
    [T_ID] nvarchar(100) NOT NULL,
    [F_ID] nvarchar(100) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [ACTIVE_TAG] bit NOT NULL CONSTRAINT [DF_ACTIVE_TAG_274152072] DEFAULT ((0)),
    [SOURCE_T_ID] nvarchar(300) NOT NULL,
    [SOURCE_DESC] nvarchar(50) NULL,
    [SOURCE_M_IDX] int NULL,
    [FILTER_STRUCT] nvarchar(max) NULL,
    [RETURN_ITEMS] nvarchar(max) NULL,
    [CREATE_BY] nvarchar(50) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nvarchar(50) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    CONSTRAINT [PK_FIELD_DATASOURCE] PRIMARY KEY CLUSTERED ([ID]),
    CONSTRAINT [UQ_FIELD_DATASOURCE] UNIQUE ([T_ID], [F_ID], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_FIELD_DATASOURCE_SOURCE_M_IDX] ON dbo.[FIELD_DATASOURCE] ([SOURCE_M_IDX]);
GO

------------------------------------------------------------------------------
-- FIELDS
------------------------------------------------------------------------------
CREATE TABLE dbo.[FIELDS] (
    [T_ID] nvarchar(100) NOT NULL,
    [F_ID] nvarchar(100) NOT NULL,
    [F_DESC] nvarchar(300) NULL,
    [F_TYPE] nvarchar(100) NULL,
    [VIRTUAL_EXP] nvarchar(500) NULL,
    [BROWSE_URL] varchar(1000) NULL,
    [BROWSE_M_IDX] int NULL,
    [ONLY_CHOOSE] bit NULL,
    [CHOOSE_PAGE] nvarchar(500) NULL,
    [CHOOSE_MULTI] bit NULL CONSTRAINT [DF_CHOOSE_MULTI_1042102753] DEFAULT ((0)),
    [REGEX] nvarchar(300) NULL,
    [DISPLAY_LENGTH] int NULL CONSTRAINT [DF_DISPLAY_LENGTH_1042102753] DEFAULT ((100)),
    [DISPLAY_FORMAT] nvarchar(50) NULL,
    [HEADER_ALIGN] nvarchar(50) NULL CONSTRAINT [DF_HEADER_ALIGN_1042102753] DEFAULT ('center'),
    [ITEM_ALIGN] varchar(50) NULL,
    [IS_VERIFY] bit NULL CONSTRAINT [DF_IS_VERIFY_1042102753] DEFAULT ((0)),
    [VERIFY_INDEX] int NULL,
    [IS_PK] bit NULL CONSTRAINT [DF_IS_PK_1042102753] DEFAULT ((0)),
    [IS_READONLY] bit NULL CONSTRAINT [DF_IS_READONLY_1042102753] DEFAULT ((0)),
    [IS_VISIBLE] bit NULL,
    [IS_VIRTUAL] bit NULL CONSTRAINT [DF_IS_VIRTUAL_1042102753] DEFAULT ((0)),
    [IS_AUTOINC] bit NULL CONSTRAINT [DF_IS_AUTOINC_1042102753] DEFAULT ((0)),
    [IS_QUERY] bit NULL CONSTRAINT [DF_IS_QUERY_1042102753] DEFAULT ((1)),
    [IS_COST] bit NULL,
    [IS_SECRECY] bit NULL,
    [DFT_VALUE] nvarchar(200) NULL,
    [CAN_COPY] bit NULL,
    [IS_DEFAULT_FIELDS] bit NULL,
    [CONVERT_FUNCTION] nvarchar(100) NULL,
    [DATASOURCE_SQL] nvarchar(500) NULL,
    [F_REMARK] nvarchar(500) NULL,
    [LAST_UPDATE_BY] nvarchar(50) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [FORM_TAB_NO] int NULL,
    [FORM_ORDER] int NULL,
    [FORM_SPAN] tinyint NULL,
    [FORM_NEW_LINE] bit NULL,
    [FORM_CELL_GROUP] nvarchar(50) NULL,
    [FORM_CELL_ROLE] tinyint NULL,
    [FORM_OPTIONS] nvarchar(500) NULL,
    CONSTRAINT [PK_FIELDS] PRIMARY KEY CLUSTERED ([T_ID], [F_ID])
);
GO

------------------------------------------------------------------------------
-- HALF_IN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HALF_IN_D] (
    [IN_TYPE] nchar(10) NOT NULL,
    [IN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [DEPOT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_HALF_IN_D] PRIMARY KEY CLUSTERED ([IN_TYPE], [IN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HALF_IN_D] ON dbo.[HALF_IN_D] ([PRO_NO], [PROCEDURE_TYPE_ID]);
GO

------------------------------------------------------------------------------
-- HALF_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HALF_IN_M] (
    [IN_TYPE] nchar(10) NOT NULL,
    [IN_NO] nchar(20) NOT NULL,
    [IN_DATE] datetime NULL,
    [LINE_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1074102867] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HALF_IN_M] PRIMARY KEY CLUSTERED ([IN_TYPE], [IN_NO])
);
GO

------------------------------------------------------------------------------
-- HALF_OUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HALF_OUT_D] (
    [OUT_TYPE] nchar(10) NOT NULL,
    [OUT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [DEPOT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_HALF_OUT_D] PRIMARY KEY CLUSTERED ([OUT_TYPE], [OUT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HALF_OUT_D] ON dbo.[HALF_OUT_D] ([PRO_NO], [PROCEDURE_TYPE_ID]);
GO

------------------------------------------------------------------------------
-- HALF_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HALF_OUT_M] (
    [OUT_TYPE] nchar(10) NOT NULL,
    [OUT_NO] nchar(20) NOT NULL,
    [OUT_DATE] datetime NULL,
    [LINE_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1106102981] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HALF_OUT_M] PRIMARY KEY CLUSTERED ([OUT_TYPE], [OUT_NO])
);
GO

------------------------------------------------------------------------------
-- HALF_PRO
------------------------------------------------------------------------------
CREATE TABLE dbo.[HALF_PRO] (
    [PRO_NO] nchar(30) NOT NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NOT NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [COLOR_ID] nchar(10) NULL,
    [STUFF_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [LENGTH] float NULL,
    [WIDTH] float NULL,
    [HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1122103038] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1122103038] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HALF_PRO] PRIMARY KEY CLUSTERED ([PRO_NO], [PROCEDURE_TYPE_ID])
);
GO

------------------------------------------------------------------------------
-- HALF_PRO_DEPOT
------------------------------------------------------------------------------
CREATE TABLE dbo.[HALF_PRO_DEPOT] (
    [PRO_NO] nchar(30) NOT NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NOT NULL,
    [DEPOT_ID] nchar(10) NOT NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1138103095] DEFAULT ((0)),
    [INIT_QTY] float NULL CONSTRAINT [DF_INIT_QTY_1138103095] DEFAULT ((0)),
    [LAST_CHECK_DATE] datetime NULL,
    [USEABLE_QTY] float NULL,
    [COST_PRICE] float NULL,
    [COST_AMOUNT] float NULL CONSTRAINT [DF_COST_AMOUNT_1138103095] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1138103095] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HALF_PRO_DEPOT] PRIMARY KEY CLUSTERED ([PRO_NO], [PROCEDURE_TYPE_ID], [DEPOT_ID])
);
GO

------------------------------------------------------------------------------
-- HR_ABSENT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_ABSENT_D] (
    [ABSENT_TYPE] nchar(10) NOT NULL,
    [ABSENT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [START_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [ABSENT_DAYS] float NULL,
    [COUNT_DAYS] float NULL,
    [REMARK] nvarchar(100) NULL,
    [START_TIME] char(5) NULL,
    [END_TIME] char(5) NULL,
    CONSTRAINT [PK_HR_ABSENT_D] PRIMARY KEY CLUSTERED ([ABSENT_TYPE], [ABSENT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_ABSENT_D] ON dbo.[HR_ABSENT_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_ABSENT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_ABSENT_M] (
    [ABSENT_TYPE] nchar(10) NOT NULL,
    [ABSENT_NO] nchar(20) NOT NULL,
    [ABSENT_DATE] datetime NULL,
    [COUNT_MULTIPLE] float NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1554104577] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_ABSENT_M] PRIMARY KEY CLUSTERED ([ABSENT_TYPE], [ABSENT_NO])
);
GO

------------------------------------------------------------------------------
-- HR_ADD_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_ADD_D] (
    [ADD_TYPE] nchar(10) NOT NULL,
    [ADD_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [ADD_SUM01] float NULL,
    [ADD_SUM02] float NULL,
    [ADD_SUM03] float NULL,
    [ADD_SUM04] float NULL,
    [ADD_SUM05] float NULL,
    [ADD_SUM06] float NULL,
    [ADD_SUM07] float NULL,
    [ADD_SUM08] float NULL,
    [ADD_SUM09] float NULL,
    [ADD_SUM10] float NULL,
    [ADD_SUM11] float NULL,
    [ADD_SUM12] float NULL,
    [ADD_SUM13] float NULL,
    [ADD_SUM14] float NULL,
    [ADD_SUM15] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_ADD_D] PRIMARY KEY CLUSTERED ([ADD_TYPE], [ADD_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_ADD_D] ON dbo.[HR_ADD_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_ADD_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_ADD_M] (
    [ADD_TYPE] nchar(10) NOT NULL,
    [ADD_NO] nchar(20) NOT NULL,
    [ADD_DATE] datetime NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1586104691] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_ADD_M] PRIMARY KEY CLUSTERED ([ADD_TYPE], [ADD_NO])
);
GO

------------------------------------------------------------------------------
-- HR_ADJUST_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_ADJUST_D] (
    [ADJUST_TYPE] nchar(10) NOT NULL,
    [ADJUST_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [COUNT_DATE] smalldatetime NULL,
    [EMP_ID] nchar(10) NULL,
    [TIMETYPE_ID] char(10) NULL,
    [IN_TIME1] char(5) NULL,
    [OUT_TIME1] char(5) NULL,
    [WORK_HOURS1] float NULL,
    [ADD_HOURS1] float NULL,
    [IN_TIME2] char(5) NULL,
    [OUT_TIME2] char(5) NULL,
    [WORK_HOURS2] float NULL,
    [ADD_HOURS2] float NULL,
    [IN_TIME3] char(5) NULL,
    [OUT_TIME3] char(5) NULL,
    [WORK_HOURS3] float NULL,
    [ADD_HOURS3] float NULL,
    [IN_TIME4] char(5) NULL,
    [OUT_TIME4] char(5) NULL,
    [WORK_HOURS4] float NULL,
    [ADD_HOURS4] float NULL,
    [REMARK] nvarchar(50) NULL,
    CONSTRAINT [PK_HR_ADJUST_D] PRIMARY KEY CLUSTERED ([ADJUST_TYPE], [ADJUST_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_ADJUST_D] ON dbo.[HR_ADJUST_D] ([EMP_ID], [TIMETYPE_ID]);
GO

------------------------------------------------------------------------------
-- HR_ADJUST_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_ADJUST_M] (
    [ADJUST_TYPE] nchar(10) NOT NULL,
    [ADJUST_NO] nchar(20) NOT NULL,
    [ADJUST_DATE] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1618104805] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_ADJUST_M] PRIMARY KEY CLUSTERED ([ADJUST_TYPE], [ADJUST_NO])
);
GO

------------------------------------------------------------------------------
-- HR_AMERCE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_AMERCE] (
    [AMERCE_ID] nchar(10) NOT NULL,
    [AMERCE_NAME] nvarchar(50) NULL,
    [AMERCE_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_AMERCE] PRIMARY KEY CLUSTERED ([AMERCE_ID])
);
GO

------------------------------------------------------------------------------
-- HR_AMERCE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_AMERCE_D] (
    [AMERCE_TYPE] nchar(10) NOT NULL,
    [AMERCE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [AMERCE_ID] nchar(10) NULL,
    [AMERCE_SUM] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_AMERCE_D] PRIMARY KEY CLUSTERED ([AMERCE_TYPE], [AMERCE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_AMERCE_D] ON dbo.[HR_AMERCE_D] ([EMP_ID], [AMERCE_ID]);
GO

------------------------------------------------------------------------------
-- HR_AMERCE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_AMERCE_M] (
    [AMERCE_TYPE] nchar(10) NOT NULL,
    [AMERCE_NO] nchar(20) NOT NULL,
    [AMERCE_DATE] datetime NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1666104976] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_AMERCE_M] PRIMARY KEY CLUSTERED ([AMERCE_TYPE], [AMERCE_NO])
);
GO

------------------------------------------------------------------------------
-- HR_APPLY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_APPLY_D] (
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ON3] char(5) NULL,
    [OUT3] char(5) NULL,
    [ON4] char(5) NULL,
    [OUT4] char(5) NULL,
    [WORKTIME] float NULL,
    [OVERTIME] float NULL,
    [REST_OVERTIME] float NULL,
    [HOLIDAY_OVERTIME] float NULL,
    [REMARK] nvarchar(100) NULL,
    [USED_WORKTIME] float NULL,
    [USED_OVERTIME] float NULL,
    [USED_REST_OVERTIME] float NULL,
    [USED_HOLIDAY_OVERTIME] float NULL,
    CONSTRAINT [PK_HR_APPLY_D] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_APPLY_D] ON dbo.[HR_APPLY_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_APPLY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_APPLY_M] (
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    [APPLY_DATE] datetime NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1698105090] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_APPLY_M] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO])
);
GO

------------------------------------------------------------------------------
-- HR_AWARD
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_AWARD] (
    [AWARD_ID] nchar(10) NOT NULL,
    [AWARD_NAME] nvarchar(50) NULL,
    [AWARD_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_AWARD] PRIMARY KEY CLUSTERED ([AWARD_ID])
);
GO

------------------------------------------------------------------------------
-- HR_AWARD_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_AWARD_D] (
    [AWARD_TYPE] nchar(10) NOT NULL,
    [AWARD_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [AWARD_ID] nchar(10) NULL,
    [AWARD_SUM] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_AWARD_D] PRIMARY KEY CLUSTERED ([AWARD_TYPE], [AWARD_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_AWARD_D] ON dbo.[HR_AWARD_D] ([EMP_ID], [AWARD_ID]);
GO

------------------------------------------------------------------------------
-- HR_AWARD_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_AWARD_M] (
    [AWARD_TYPE] nchar(10) NOT NULL,
    [AWARD_NO] nchar(20) NOT NULL,
    [AWARD_DATE] datetime NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1746105261] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_AWARD_M] PRIMARY KEY CLUSTERED ([AWARD_TYPE], [AWARD_NO])
);
GO

------------------------------------------------------------------------------
-- HR_BASEPAY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_BASEPAY_D] (
    [BASEPAY_TYPE] nchar(10) NOT NULL,
    [BASEPAY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [BASEPAY_ITEM01] float NULL,
    [BASEPAY_ITEM02] float NULL,
    [BASEPAY_ITEM03] float NULL,
    [BASEPAY_ITEM04] float NULL,
    [BASEPAY_ITEM05] float NULL,
    [BASEPAY_ITEM06] float NULL,
    [BASEPAY_ITEM07] float NULL,
    [BASEPAY_ITEM08] float NULL,
    [BASEPAY_ITEM09] float NULL,
    [BASEPAY_ITEM10] float NULL,
    [BASEPAY_ITEM11] float NULL,
    [BASEPAY_ITEM12] float NULL,
    [BASEPAY_ITEM13] float NULL,
    [BASEPAY_ITEM14] float NULL,
    [BASEPAY_ITEM15] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_BASEPAY_D] PRIMARY KEY CLUSTERED ([BASEPAY_TYPE], [BASEPAY_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_BASEPAY_D] ON dbo.[HR_BASEPAY_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_BASEPAY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_BASEPAY_M] (
    [BASEPAY_TYPE] nchar(10) NOT NULL,
    [BASEPAY_NO] nchar(20) NOT NULL,
    [BASEPAY_DATE] datetime NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [DEPT_ID] nchar(10) NULL,
    [IF_SECRECY] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1778105375] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_BASEPAY_M] PRIMARY KEY CLUSTERED ([BASEPAY_TYPE], [BASEPAY_NO])
);
GO

------------------------------------------------------------------------------
-- HR_BED
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_BED] (
    [BED_ID] nchar(10) NOT NULL,
    [BED_NAME] nvarchar(50) NULL,
    [BED_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_BED] PRIMARY KEY CLUSTERED ([BED_ID])
);
GO

------------------------------------------------------------------------------
-- HR_BLOOD
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_BLOOD] (
    [BLOOD_ID] nchar(10) NOT NULL,
    [BLOOD_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_BLOOD] PRIMARY KEY CLUSTERED ([BLOOD_ID])
);
GO

------------------------------------------------------------------------------
-- HR_CERTIFY
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_CERTIFY] (
    [CERTIFY_ID] nchar(10) NOT NULL,
    [CERTIFY_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_CERTIFY] PRIMARY KEY CLUSTERED ([CERTIFY_ID])
);
GO

------------------------------------------------------------------------------
-- HR_CERTIFY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_CERTIFY_D] (
    [CERTIFY_TYPE] nchar(10) NOT NULL,
    [CERTIFY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [CERTIFY_ID] nchar(10) NULL,
    [CERTIFY_NUMBER] nvarchar(50) NOT NULL,
    [BEGIN_DATE] smalldatetime NULL,
    [END_DATE] smalldatetime NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_HR_CERTIFY_D] PRIMARY KEY CLUSTERED ([CERTIFY_TYPE], [CERTIFY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HR_CERTIFY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_CERTIFY_M] (
    [CERTIFY_TYPE] nchar(10) NOT NULL,
    [CERTIFY_NO] nchar(20) NOT NULL,
    [CERTIFY_DATE] smalldatetime NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1858105660] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_CERTIFY_M] PRIMARY KEY CLUSTERED ([CERTIFY_TYPE], [CERTIFY_NO])
);
GO

------------------------------------------------------------------------------
-- HR_CONTRACT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_CONTRACT_D] (
    [CONT_TYPE] nchar(10) NOT NULL,
    [CONT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [CONTRACT_NO] nvarchar(50) NOT NULL,
    [BEGIN_DATE] smalldatetime NULL,
    [END_DATE] smalldatetime NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_HR_CONTRACT_D] PRIMARY KEY CLUSTERED ([CONT_TYPE], [CONT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HR_CONTRACT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_CONTRACT_M] (
    [CONT_TYPE] nchar(10) NOT NULL,
    [CONT_NO] nchar(20) NOT NULL,
    [CONT_DATE] smalldatetime NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1890105774] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_HR_CONTRACT_M] PRIMARY KEY CLUSTERED ([CONT_TYPE], [CONT_NO])
);
GO

------------------------------------------------------------------------------
-- HR_DIARY
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_DIARY] (
    [COUNT_DATE] smalldatetime NOT NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [TIMETYPE_ID] nchar(10) NULL,
    [ON1] char(5) NULL,
    [OUT1] char(5) NULL,
    [ON2] char(5) NULL,
    [OUT2] char(5) NULL,
    [ON3] char(5) NULL,
    [OUT3] char(5) NULL,
    [ON4] char(5) NULL,
    [OUT4] char(5) NULL,
    [WORKTIME] float NULL,
    [OVERTIME] float NULL,
    [REST_OVERTIME] float NULL,
    [HOLIDAY_OVERTIME] float NULL,
    [ABSENT_TIME] float NULL,
    [BE_LATE_FOR] float NULL,
    [LATE_TIMES] float NULL,
    [LEAVE_EARLY] float NULL,
    [LEAVE_EARLY_TIMES] float NULL,
    [BE_LATE_FOR1] float NULL,
    [BE_LATE_FOR2] float NULL,
    [BE_LATE_FOR3] float NULL,
    [BE_LATE_FOR4] float NULL,
    [LEAVE_EARLY1] float NULL,
    [LEAVE_EARLY2] float NULL,
    [LEAVE_EARLY3] float NULL,
    [LEAVE_EARLY4] float NULL,
    [SIGN_IN] nvarchar(200) NULL,
    [ERR_TIME] nvarchar(500) NULL,
    [EXCHANGE_DATE] smalldatetime NULL,
    [ON_DUTY_TIME] nvarchar(200) NULL,
    [REMARK] nvarchar(300) NULL,
    CONSTRAINT [PK_HR_DIARY] PRIMARY KEY CLUSTERED ([COUNT_DATE], [EMP_ID])
);
GO

------------------------------------------------------------------------------
-- HR_DIARY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_DIARY_D] (
    [CARD_ID] nchar(20) NOT NULL,
    [COUNT_DATE] smalldatetime NOT NULL,
    CONSTRAINT [PK_HR_DIARY_D] PRIMARY KEY CLUSTERED ([CARD_ID], [COUNT_DATE])
);
GO

------------------------------------------------------------------------------
-- HR_DIARY_T
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_DIARY_T] (
    [CARD_ID] nchar(20) NOT NULL,
    [COUNT_DATE] smalldatetime NOT NULL
);
GO

------------------------------------------------------------------------------
-- HR_DIMISSION
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_DIMISSION] (
    [DIMISSION_ID] nchar(10) NOT NULL,
    [DIMISSION_NAME] nvarchar(50) NULL,
    [DIMISSION_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_DIMISSION] PRIMARY KEY CLUSTERED ([DIMISSION_ID])
);
GO

------------------------------------------------------------------------------
-- HR_DIMISSION_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_DIMISSION_M] (
    [DIMISSION_TYPE] nchar(10) NOT NULL,
    [DIMISSION_NO] nchar(20) NOT NULL,
    [DIMISSION_DATE] datetime NULL,
    [INURE_DATE] datetime NULL,
    [EMP_ID] nchar(10) NULL,
    [DIMISSION_ID] nchar(10) NULL,
    [DIMISSION_SUM] float NULL,
    [REASON] nvarchar(200) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1970106059] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_DIMISSION_M] PRIMARY KEY CLUSTERED ([DIMISSION_TYPE], [DIMISSION_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_DIMISSION_M] ON dbo.[HR_DIMISSION_M] ([EMP_ID], [DIMISSION_ID]);
GO

------------------------------------------------------------------------------
-- HR_DIPLOMA
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_DIPLOMA] (
    [DIPLOMA_ID] nchar(10) NOT NULL,
    [DIPLOMA_NAME] nvarchar(50) NULL,
    [DIPLOMA_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_DIPLOMA] PRIMARY KEY CLUSTERED ([DIPLOMA_ID])
);
GO

------------------------------------------------------------------------------
-- HR_DORM
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_DORM] (
    [DORM_ID] nchar(10) NOT NULL,
    [DORM_NAME] nvarchar(50) NULL,
    [SEX] smallint NULL CONSTRAINT [DF_SEX_2002106173] DEFAULT ((0)),
    [LEADER_EMP_ID] nchar(10) NULL,
    [TEL] nvarchar(50) NULL,
    [PERSON_SUM] smallint NULL,
    [BED_SUM] smallint NULL,
    [USE_TAG] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_DORM] PRIMARY KEY CLUSTERED ([DORM_ID])
);
GO

------------------------------------------------------------------------------
-- HR_DUTY
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_DUTY] (
    [DUTY_ID] nchar(10) NOT NULL,
    [DUTY_NAME] nvarchar(50) NULL,
    [DUTY_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_DUTY] PRIMARY KEY CLUSTERED ([DUTY_ID])
);
GO

------------------------------------------------------------------------------
-- HR_EMPLOYEE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_EMPLOYEE] (
    [EMP_ID] nchar(10) NOT NULL,
    [EMP_NO] nchar(20) NULL,
    [EMP_NAME] nvarchar(50) NULL,
    [EMP_OLD_NAME] nvarchar(50) NULL,
    [SEX] bit NULL CONSTRAINT [DF_SEX_2034106287] DEFAULT ((0)),
    [WEDLOCK] bit NULL CONSTRAINT [DF_WEDLOCK_2034106287] DEFAULT ((0)),
    [BLOOD_ID] nchar(10) NULL,
    [LANGUAGE_ID] nchar(10) NULL,
    [NATION_ID] nchar(10) NULL,
    [POLITY_ID] nchar(10) NULL,
    [PROVINCE_ID] nchar(10) NULL,
    [TITLE_ID] nchar(10) NULL,
    [DIPLOMA_ID] nchar(10) NULL,
    [BIRTHDAY] datetime NULL,
    [PHOTO] nvarchar(200) NULL,
    [ID_CARD] nvarchar(50) NULL,
    [ID_ADDRESS] nvarchar(500) NULL,
    [ID_DEPARTMENT] nvarchar(50) NULL,
    [PERMANENT_ADDRESS] nvarchar(200) NULL,
    [MOBILE] nvarchar(50) NULL,
    [EMAIL] nvarchar(50) NULL,
    [TEL] nvarchar(50) NULL,
    [LINKMAN] nvarchar(500) NULL,
    [STUDY_STORY] nvarchar(500) NULL,
    [WORK_STORY] nvarchar(500) NULL,
    [DEPT_ID] nchar(10) NULL,
    [DORM_ID] nchar(10) NULL,
    [BED_ID] nchar(10) NULL,
    [DUTY_ID] nchar(10) NULL,
    [GRADE_ID] nchar(10) NULL,
    [POST_ID] nchar(10) NULL,
    [WORKTYPE_ID] nchar(10) NULL,
    [IN_DATE] datetime NULL,
    [STATE] smallint NULL,
    [KIND] smallint NULL,
    [ON_DUTY_DATE] datetime NULL,
    [DIMISSION_ID] nchar(10) NULL,
    [DIMISSION_DATE] datetime NULL,
    [DIMISSION_WHYS] nvarchar(100) NULL,
    [BANK_ID] nchar(50) NULL,
    [ACCOUNTS] nchar(50) NULL,
    [IF_CARD] bit NULL,
    [IF_SECRECY] bit NULL,
    [IF_DORM] bit NULL CONSTRAINT [DF_IF_DORM_2034106287] DEFAULT ((1)),
    [IF_EAT] bit NULL CONSTRAINT [DF_IF_EAT_2034106287] DEFAULT ((1)),
    [IF_COUNT] bit NULL CONSTRAINT [DF_IF_COUNT_2034106287] DEFAULT ((1)),
    [IF_SHOW] bit NULL CONSTRAINT [DF_IF_SHOW_2034106287] DEFAULT ((1)),
    [IF_OVERTIMEPAY] bit NULL CONSTRAINT [DF_IF_OVERTIMEPAY_2034106287] DEFAULT ((1)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [INTRODUCER] nchar(10) NULL,
    [INTRODUCER_NAME] nvarchar(50) NULL,
    [BIRTHDAY_MONTH] AS (datepart(month,[BIRTHDAY])),
    [IN_MONTH] AS (datediff(month,[IN_DATE],getdate())),
    [IF_AWARD] bit NULL,
    [CONTRACT_DATE] smalldatetime NULL,
    [BASEPAY_1] float NULL,
    [BASEPAY_2] float NULL,
    [BASEPAY_3] float NULL,
    [BASEPAY_4] float NULL,
    [BASEPAY_5] float NULL,
    [BASEPAY_6] float NULL,
    [BASEPAY_7] float NULL,
    [BASEPAY_8] float NULL,
    [BASEPAY_9] float NULL,
    [BASEPAY_A] float NULL,
    [BASEPAY_B] float NULL,
    [BASEPAY_C] float NULL,
    [CONTRACT_NO] nvarchar(50) NULL,
    [CONTRACT_BEGIN_DATE] smalldatetime NULL,
    CONSTRAINT [PK_HR_EMPLOYEE] PRIMARY KEY CLUSTERED ([EMP_ID])
);
GO

------------------------------------------------------------------------------
-- HR_EMPLOYEE_CARD
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_EMPLOYEE_CARD] (
    [EMP_ID] nchar(10) NOT NULL,
    [CARD_ID] nchar(20) NOT NULL,
    [BEGIN_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_EMPLOYEE_CARD] PRIMARY KEY CLUSTERED ([EMP_ID], [CARD_ID])
);
GO

------------------------------------------------------------------------------
-- HR_EMPLOYEE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_EMPLOYEE_D] (
    [EMP_ID] nchar(10) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PAPER_NAME] nvarchar(50) NULL,
    [PAPER_NO] nvarchar(50) NULL,
    [START_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [IF_CUE] bit NULL CONSTRAINT [DF_IF_CUE_2066106401] DEFAULT ((0)),
    [CUE_DATE] nchar(10) NULL,
    [PAPER_DEPARTMENT] nvarchar(50) NULL,
    [LINKMAN] nvarchar(100) NULL,
    [TEL] nvarchar(100) NULL,
    [MOBILE] nvarchar(100) NULL,
    [WEBSITE] nvarchar(100) NULL,
    [EMAIL] nvarchar(100) NULL,
    [QQ_MSN] nvarchar(100) NULL,
    [REMARK] nvarchar(100) NULL,
    [REMARK1] nvarchar(100) NULL,
    [REMARK2] nvarchar(100) NULL,
    [REMARK3] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_EMPLOYEE_D] PRIMARY KEY CLUSTERED ([EMP_ID], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HR_ENACTMENT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_ENACTMENT_D] (
    [ENACTMENT_TYPE] nchar(10) NOT NULL,
    [ENACTMENT_NO] nchar(20) NOT NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [WORKTIME] float NULL,
    [OVERTIME] float NULL,
    [REST_OVERTIME] float NULL,
    [HOLIDAY_OVERTIME] float NULL,
    [USED_WORKTIME] float NULL,
    [USED_OVERTIME] float NULL,
    [USED_REST_OVERTIME] float NULL,
    [USED_HOLIDAY_OVERTIME] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_ENACTMENT_D] PRIMARY KEY CLUSTERED ([ENACTMENT_TYPE], [ENACTMENT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_ENACTMENT_D] ON dbo.[HR_ENACTMENT_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_ENACTMENT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_ENACTMENT_M] (
    [ENACTMENT_TYPE] nchar(10) NOT NULL,
    [ENACTMENT_NO] nchar(20) NOT NULL,
    [ENACTMENT_DATE] datetime NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2098106515] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_ENACTMENT_M] PRIMARY KEY CLUSTERED ([ENACTMENT_TYPE], [ENACTMENT_NO])
);
GO

------------------------------------------------------------------------------
-- HR_EVECTION
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_EVECTION] (
    [EVECTION_ID] nchar(10) NOT NULL,
    [EVECTION_NAME] nvarchar(50) NULL,
    [EVECTION_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_EVECTION] PRIMARY KEY CLUSTERED ([EVECTION_ID])
);
GO

------------------------------------------------------------------------------
-- HR_EVECTION_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_EVECTION_D] (
    [EVECTION_TYPE] nchar(10) NOT NULL,
    [EVECTION_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [EVECTION_ID] nchar(10) NULL,
    [START_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [EVECTION_DAYS] float NULL,
    [COUNT_DAYS] float NULL,
    [EVECTION_SUM] float NULL,
    [REMARK] nvarchar(100) NULL,
    [START_TIME] char(5) NULL,
    [END_TIME] char(5) NULL,
    CONSTRAINT [PK_HR_EVECTION_D] PRIMARY KEY CLUSTERED ([EVECTION_TYPE], [EVECTION_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_EVECTION_D] ON dbo.[HR_EVECTION_D] ([EMP_ID], [EVECTION_ID]);
GO

------------------------------------------------------------------------------
-- HR_EVECTION_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_EVECTION_M] (
    [EVECTION_TYPE] nchar(10) NOT NULL,
    [EVECTION_NO] nchar(20) NOT NULL,
    [EVECTION_DATE] datetime NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2146106686] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_EVECTION_M] PRIMARY KEY CLUSTERED ([EVECTION_TYPE], [EVECTION_NO])
);
GO

------------------------------------------------------------------------------
-- HR_EXCHANGE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_EXCHANGE_D] (
    [EXCHANGE_TYPE] nchar(10) NOT NULL,
    [EXCHANGE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [FIRST_DAY] datetime NULL,
    [END_DAY] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_HR_EXCHANGE_D] PRIMARY KEY CLUSTERED ([EXCHANGE_TYPE], [EXCHANGE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_EXCHANGE_D] ON dbo.[HR_EXCHANGE_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_EXCHANGE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_EXCHANGE_M] (
    [EXCHANGE_TYPE] nchar(10) NOT NULL,
    [EXCHANGE_NO] nchar(20) NOT NULL,
    [EXCHANGE_DATE] datetime NULL,
    [TYPE] bit NULL CONSTRAINT [DF_TYPE_30623152] DEFAULT ((1)),
    [FIRST_DAY] datetime NULL,
    [END_DAY] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_30623152] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_EXCHANGE_M] PRIMARY KEY CLUSTERED ([EXCHANGE_TYPE], [EXCHANGE_NO])
);
GO

------------------------------------------------------------------------------
-- HR_FOREGIFT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_FOREGIFT_D] (
    [FOREGIFT_TYPE] nchar(10) NOT NULL,
    [FOREGIFT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [FOREGIFT_GOODS] nvarchar(50) NULL,
    [FOREGIFT_SUM] float NULL,
    [RETURN_DATE] datetime NULL,
    [RETURN_GOODS] nvarchar(50) NULL,
    [RETURN_SUM] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_FOREGIFT_D] PRIMARY KEY CLUSTERED ([FOREGIFT_TYPE], [FOREGIFT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_FOREGIFT_D] ON dbo.[HR_FOREGIFT_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_FOREGIFT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_FOREGIFT_M] (
    [FOREGIFT_TYPE] nchar(10) NOT NULL,
    [FOREGIFT_NO] nchar(20) NOT NULL,
    [FOREGIFT_DATE] datetime NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_62623266] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_FOREGIFT_M] PRIMARY KEY CLUSTERED ([FOREGIFT_TYPE], [FOREGIFT_NO])
);
GO

------------------------------------------------------------------------------
-- HR_GRADE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_GRADE] (
    [GRADE_ID] nchar(10) NOT NULL,
    [GRADE_NAME] nvarchar(50) NULL,
    [GRADE_SUM0] float NULL,
    [GRADE_SUM1] float NULL,
    [GRADE_SUM2] float NULL,
    [GRADE_SUM3] float NULL,
    [GRADE_SUM4] float NULL,
    [GRADE_SUM5] float NULL,
    [GRADE_SUM6] float NULL,
    [GRADE_SUM7] float NULL,
    [GRADE_SUM8] float NULL,
    [GRADE_SUM9] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_GRADE] PRIMARY KEY CLUSTERED ([GRADE_ID])
);
GO

------------------------------------------------------------------------------
-- HR_HOLIDAY
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_HOLIDAY] (
    [HOLIDAY_ID] nchar(10) NOT NULL,
    [HOLIDAY_NAME] nvarchar(50) NULL,
    [START_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [COUNT_MULTIPLE] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [HOLIDAY_DAYS] smallint NULL,
    CONSTRAINT [PK_HR_HOLIDAY] PRIMARY KEY CLUSTERED ([HOLIDAY_ID])
);
GO

------------------------------------------------------------------------------
-- HR_LANGUAGE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_LANGUAGE] (
    [LANGUAGE_ID] nchar(10) NOT NULL,
    [LANGUAGE_NAME] nvarchar(50) NULL,
    [LANGUAGE_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_LANGUAGE] PRIMARY KEY CLUSTERED ([LANGUAGE_ID])
);
GO

------------------------------------------------------------------------------
-- HR_LEAVE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_LEAVE] (
    [LEAVE_ID] nchar(10) NOT NULL,
    [LEAVE_NAME] nvarchar(50) NULL,
    [LEAVE_MULTIPLE] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_LEAVE] PRIMARY KEY CLUSTERED ([LEAVE_ID])
);
GO

------------------------------------------------------------------------------
-- HR_LEAVE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_LEAVE_D] (
    [LEAVE_TYPE] nchar(10) NOT NULL,
    [LEAVE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [LEAVE_ID] nchar(10) NULL,
    [START_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [LEAVE_DAYS] float NULL,
    [COUNT_DAYS] float NULL,
    [REMARK] nvarchar(100) NULL,
    [START_TIME] char(5) NULL,
    [END_TIME] char(5) NULL,
    CONSTRAINT [PK_HR_LEAVE_D] PRIMARY KEY CLUSTERED ([LEAVE_TYPE], [LEAVE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_LEAVE_D] ON dbo.[HR_LEAVE_D] ([EMP_ID], [LEAVE_ID]);
GO

------------------------------------------------------------------------------
-- HR_LEAVE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_LEAVE_M] (
    [LEAVE_TYPE] nchar(10) NOT NULL,
    [LEAVE_NO] nchar(20) NOT NULL,
    [LEAVE_DATE] datetime NULL,
    [COUNT_MULTIPLE] float NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_158623608] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_LEAVE_M] PRIMARY KEY CLUSTERED ([LEAVE_TYPE], [LEAVE_NO])
);
GO

------------------------------------------------------------------------------
-- HR_LOAN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_LOAN_D] (
    [LOAN_TYPE] nchar(10) NOT NULL,
    [LOAN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [LOAN_SUM] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_LOAN_D] PRIMARY KEY CLUSTERED ([LOAN_TYPE], [LOAN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_LOAN_D] ON dbo.[HR_LOAN_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_LOAN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_LOAN_M] (
    [LOAN_TYPE] nchar(10) NOT NULL,
    [LOAN_NO] nchar(20) NOT NULL,
    [LOAN_DATE] datetime NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_190623722] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_LOAN_M] PRIMARY KEY CLUSTERED ([LOAN_TYPE], [LOAN_NO])
);
GO

------------------------------------------------------------------------------
-- HR_NATION
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_NATION] (
    [NATION_ID] nchar(10) NOT NULL,
    [NATION_NAME] nvarchar(50) NULL,
    [NATION_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_NATION] PRIMARY KEY CLUSTERED ([NATION_ID])
);
GO

------------------------------------------------------------------------------
-- HR_PLAN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_PLAN_D] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [DAY_01] nchar(10) NULL,
    [DAY_02] nchar(10) NULL,
    [DAY_03] nchar(10) NULL,
    [DAY_04] nchar(10) NULL,
    [DAY_05] nchar(10) NULL,
    [DAY_06] nchar(10) NULL,
    [DAY_07] nchar(10) NULL,
    [DAY_08] nchar(10) NULL,
    [DAY_09] nchar(10) NULL,
    [DAY_10] nchar(10) NULL,
    [DAY_11] nchar(10) NULL,
    [DAY_12] nchar(10) NULL,
    [DAY_13] nchar(10) NULL,
    [DAY_14] nchar(10) NULL,
    [DAY_15] nchar(10) NULL,
    [DAY_16] nchar(10) NULL,
    [DAY_17] nchar(10) NULL,
    [DAY_18] nchar(10) NULL,
    [DAY_19] nchar(10) NULL,
    [DAY_20] nchar(10) NULL,
    [DAY_21] nchar(10) NULL,
    [DAY_22] nchar(10) NULL,
    [DAY_23] nchar(10) NULL,
    [DAY_24] nchar(10) NULL,
    [DAY_25] nchar(10) NULL,
    [DAY_26] nchar(10) NULL,
    [DAY_27] nchar(10) NULL,
    [DAY_28] nchar(10) NULL,
    [DAY_29] nchar(10) NULL,
    [DAY_30] nchar(10) NULL,
    [DAY_31] nchar(10) NULL,
    CONSTRAINT [PK_HR_PLAN_D] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_PLAN_D] ON dbo.[HR_PLAN_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_PLAN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_PLAN_M] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [PLAN_DATE] datetime NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_238623893] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_PLAN_M] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO])
);
GO

------------------------------------------------------------------------------
-- HR_POLITY
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_POLITY] (
    [POLITY_ID] nchar(10) NOT NULL,
    [POLITY_NAME] nvarchar(50) NULL,
    [POLITY_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_POLITY] PRIMARY KEY CLUSTERED ([POLITY_ID])
);
GO

------------------------------------------------------------------------------
-- HR_POST
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_POST] (
    [POST_ID] nchar(10) NOT NULL,
    [POST_NAME] nvarchar(50) NULL,
    [POST_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_POST] PRIMARY KEY CLUSTERED ([POST_ID])
);
GO

------------------------------------------------------------------------------
-- HR_PROVINCE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_PROVINCE] (
    [PROVINCE_ID] nchar(10) NOT NULL,
    [PROVINCE_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_PROVINCE] PRIMARY KEY CLUSTERED ([PROVINCE_ID])
);
GO

------------------------------------------------------------------------------
-- HR_RECESS_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_RECESS_D] (
    [RECESS_TYPE] nchar(10) NOT NULL,
    [RECESS_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [START_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [COUNT_DAYS] float NULL,
    [REMARK] nvarchar(100) NULL,
    [START_TIME] char(5) NULL,
    [END_TIME] char(5) NULL,
    [RECESS_DAYS] float NULL,
    CONSTRAINT [PK_HR_RECESS_D] PRIMARY KEY CLUSTERED ([RECESS_TYPE], [RECESS_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_RECESS_D] ON dbo.[HR_RECESS_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_RECESS_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_RECESS_M] (
    [RECESS_TYPE] nchar(10) NOT NULL,
    [RECESS_NO] nchar(20) NOT NULL,
    [RECESS_DATE] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_318624178] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TYPE] bit NULL CONSTRAINT [DF_TYPE_318624178] DEFAULT ((1)),
    [START_DATE] datetime NULL,
    [START_TIME] char(5) NULL,
    [END_DATE] datetime NULL,
    [END_TIME] char(5) NULL,
    [RECESS_DAYS] float NULL,
    [COUNT_DAYS] float NULL CONSTRAINT [DF_COUNT_DAYS_318624178] DEFAULT ((0)),
    CONSTRAINT [PK_HR_RECESS_M] PRIMARY KEY CLUSTERED ([RECESS_TYPE], [RECESS_NO])
);
GO

------------------------------------------------------------------------------
-- HR_REDEPLOY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_REDEPLOY_D] (
    [REDEPLOY_TYPE] nchar(10) NOT NULL,
    [REDEPLOY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [DEPT_ID] nchar(10) NULL,
    [OLD_DEPT_ID] nchar(10) NULL,
    [DORM_ID] nchar(10) NULL,
    [OLD_DORM_ID] nchar(10) NULL,
    [BED_ID] nchar(10) NULL,
    [OLD_BED_ID] nchar(10) NULL,
    [DUTY_ID] nchar(10) NULL,
    [OLD_DUTY_ID] nchar(10) NULL,
    [GRADE_ID] nchar(10) NULL,
    [OLD_GRADE_ID] nchar(10) NULL,
    [POST_ID] nchar(10) NULL,
    [OLD_POST_ID] nchar(10) NULL,
    [WORKTYPE_ID] nchar(10) NULL,
    [OLD_WORKTYPE_ID] nchar(10) NULL,
    [STATE] smallint NULL,
    [OLD_STATE] smallint NULL,
    [ON_DUTY_DATE] datetime NULL,
    [IF_CARD] bit NULL,
    [OLD_IF_CARD] bit NULL,
    [IF_SECRECY] bit NULL,
    [OLD_IF_SECRECY] bit NULL,
    [IF_INSURANCE] bit NULL,
    [OLD_IF_INSURANCE] bit NULL,
    [IF_DORM] bit NULL CONSTRAINT [DF_IF_DORM_334624235] DEFAULT ((1)),
    [OLD_IF_DORM] bit NULL CONSTRAINT [DF_OLD_IF_DORM_334624235] DEFAULT ((1)),
    [IF_EAT] bit NULL CONSTRAINT [DF_IF_EAT_334624235] DEFAULT ((1)),
    [OLD_IF_EAT] bit NULL CONSTRAINT [DF_OLD_IF_EAT_334624235] DEFAULT ((1)),
    [IF_COUNT] bit NULL CONSTRAINT [DF_IF_COUNT_334624235] DEFAULT ((1)),
    [OLD_IF_COUNT] bit NULL CONSTRAINT [DF_OLD_IF_COUNT_334624235] DEFAULT ((1)),
    [IF_SHOW] bit NULL CONSTRAINT [DF_IF_SHOW_334624235] DEFAULT ((1)),
    [OLD_IF_SHOW] bit NULL CONSTRAINT [DF_OLD_IF_SHOW_334624235] DEFAULT ((1)),
    [OLD_ON_DUTY_DATE] datetime NULL,
    [ACCOUNTS] nchar(50) NULL,
    [OLD_ACCOUNTS] nchar(50) NULL,
    [BASEPAY_1] float NULL,
    [OLD_BASEPAY_1] float NULL,
    [BASEPAY_2] float NULL,
    [OLD_BASEPAY_2] float NULL,
    [BASEPAY_3] float NULL,
    [OLD_BASEPAY_3] float NULL,
    [BASEPAY_4] float NULL,
    [OLD_BASEPAY_4] float NULL,
    [BASEPAY_5] float NULL,
    [OLD_BASEPAY_5] float NULL,
    [BASEPAY_6] float NULL,
    [OLD_BASEPAY_6] float NULL,
    [BASEPAY_7] float NULL,
    [OLD_BASEPAY_7] float NULL,
    [BASEPAY_8] float NULL,
    [OLD_BASEPAY_8] float NULL,
    [BASEPAY_9] float NULL,
    [OLD_BASEPAY_9] float NULL,
    [BASEPAY_A] float NULL,
    [OLD_BASEPAY_A] float NULL,
    [BASEPAY_B] float NULL,
    [OLD_BASEPAY_B] float NULL,
    [BASEPAY_C] float NULL,
    [OLD_BASEPAY_C] float NULL,
    CONSTRAINT [PK_HR_REDEPLOY_D] PRIMARY KEY CLUSTERED ([REDEPLOY_TYPE], [REDEPLOY_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_REDEPLOY_D] ON dbo.[HR_REDEPLOY_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_REDEPLOY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_REDEPLOY_M] (
    [REDEPLOY_TYPE] nchar(10) NOT NULL,
    [REDEPLOY_NO] nchar(20) NOT NULL,
    [REDEPLOY_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_350624292] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_REDEPLOY_M] PRIMARY KEY CLUSTERED ([REDEPLOY_TYPE], [REDEPLOY_NO])
);
GO

------------------------------------------------------------------------------
-- HR_SAFE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_SAFE] (
    [SAFE_ID] nchar(10) NOT NULL,
    [SAFE_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_SAFE] PRIMARY KEY CLUSTERED ([SAFE_ID])
);
GO

------------------------------------------------------------------------------
-- HR_SAFE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_SAFE_D] (
    [SAFE_TYPE] nchar(10) NOT NULL,
    [SAFE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [SAFE_ID] nchar(10) NULL,
    [SAFE_NUMBER] nvarchar(50) NOT NULL,
    [BEGIN_DATE] smalldatetime NULL,
    [END_DATE] smalldatetime NULL,
    [AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_HR_SAFE_D] PRIMARY KEY CLUSTERED ([SAFE_TYPE], [SAFE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HR_SAFE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_SAFE_M] (
    [SAFE_TYPE] nchar(10) NOT NULL,
    [SAFE_NO] nchar(20) NOT NULL,
    [SAFE_DATE] smalldatetime NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_398624463] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_SAFE_M] PRIMARY KEY CLUSTERED ([SAFE_TYPE], [SAFE_NO])
);
GO

------------------------------------------------------------------------------
-- HR_SETUP
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_SETUP] (
    [MACHINE_START] smallint NOT NULL,
    [MACHINE_LENGTH] smallint NULL,
    [TYPE_START] smallint NULL,
    [TYPE_LENGTH] smallint NULL,
    [CARD_START] smallint NULL,
    [CARD_LENGTH] smallint NULL,
    [YEAR_START] smallint NULL,
    [YEAR_LENGTH] smallint NULL,
    [MONTH_START] smallint NULL,
    [MONTH_LENGTH] smallint NULL,
    [DAY_START] smallint NULL,
    [DAY_LENGTH] smallint NULL,
    [HOUR_START] smallint NULL,
    [HOUR_LENGTH] smallint NULL,
    [MINUTE_START] smallint NULL,
    [MINUTE_LENGTH] smallint NULL,
    [SECOND_START] smallint NULL,
    [SECOND_LENGTH] smallint NULL,
    [SAT_REST_DAY] bit NULL CONSTRAINT [DF_SAT_REST_DAY_414624520] DEFAULT ((1)),
    [SUN_REST_DAY] bit NULL CONSTRAINT [DF_SUN_REST_DAY_414624520] DEFAULT ((1)),
    [SAT_ABSENT] bit NULL CONSTRAINT [DF_SAT_ABSENT_414624520] DEFAULT ((0)),
    [SUN_ABSENT] bit NULL CONSTRAINT [DF_SUN_ABSENT_414624520] DEFAULT ((0)),
    [HOLIDAY_ABSENT] bit NULL CONSTRAINT [DF_HOLIDAY_ABSENT_414624520] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [REQUIRE_ENACTMENT] bit NULL,
    [WAGE_ADD] nvarchar(100) NULL,
    [WAGE_WORK] nvarchar(100) NULL,
    [WAGE_OVER] nvarchar(100) NULL,
    [WAGE_REST] nvarchar(100) NULL,
    [WAGE_HOLIDAY] nvarchar(100) NULL,
    [WAGE_WORKTIME] nvarchar(100) NULL,
    [WAGE_OVERTIME] nvarchar(100) NULL,
    [WAGE_RESTTIME] nvarchar(100) NULL,
    [WAGE_HOLITIME] nvarchar(100) NULL,
    [DIMISSION_NO_WAGE] bit NULL
);
GO

------------------------------------------------------------------------------
-- HR_SIGN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_SIGN_D] (
    [SIGN_TYPE] nchar(10) NOT NULL,
    [SIGN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [COUNT_DATE] smalldatetime NULL,
    [ON1] char(5) NULL,
    [OUT1] char(5) NULL,
    [ON2] char(5) NULL,
    [OUT2] char(5) NULL,
    [ON3] char(5) NULL,
    [OUT3] char(5) NULL,
    [ON4] char(5) NULL,
    [OUT4] char(5) NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_SIGN_D] PRIMARY KEY CLUSTERED ([SIGN_TYPE], [SIGN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_SIGN_D] ON dbo.[HR_SIGN_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_SIGN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_SIGN_M] (
    [SIGN_TYPE] nchar(10) NOT NULL,
    [SIGN_NO] nchar(20) NOT NULL,
    [SIGN_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_446624634] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_SIGN_M] PRIMARY KEY CLUSTERED ([SIGN_TYPE], [SIGN_NO])
);
GO

------------------------------------------------------------------------------
-- HR_SUBTRACT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_SUBTRACT_D] (
    [SUBTRACT_TYPE] nchar(10) NOT NULL,
    [SUBTRACT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [SUBTRACT_SUM01] float NULL,
    [SUBTRACT_SUM02] float NULL,
    [SUBTRACT_SUM03] float NULL,
    [SUBTRACT_SUM04] float NULL,
    [SUBTRACT_SUM05] float NULL,
    [SUBTRACT_SUM06] float NULL,
    [SUBTRACT_SUM07] float NULL,
    [SUBTRACT_SUM08] float NULL,
    [SUBTRACT_SUM09] float NULL,
    [SUBTRACT_SUM10] float NULL,
    [SUBTRACT_SUM11] float NULL,
    [SUBTRACT_SUM12] float NULL,
    [SUBTRACT_SUM13] float NULL,
    [SUBTRACT_SUM14] float NULL,
    [SUBTRACT_SUM15] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_SUBTRACT_D] PRIMARY KEY CLUSTERED ([SUBTRACT_TYPE], [SUBTRACT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_SUBTRACT_D] ON dbo.[HR_SUBTRACT_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_SUBTRACT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_SUBTRACT_M] (
    [SUBTRACT_TYPE] nchar(10) NOT NULL,
    [SUBTRACT_NO] nchar(20) NOT NULL,
    [SUBTRACT_DATE] datetime NOT NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_478624748] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_SUBTRACT_M] PRIMARY KEY CLUSTERED ([SUBTRACT_TYPE], [SUBTRACT_NO])
);
GO

------------------------------------------------------------------------------
-- HR_TIMETYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_TIMETYPE] (
    [TIMETYPE_ID] char(10) NOT NULL,
    [TIMETYPE_NAME] nvarchar(50) NULL,
    [TYPE] nvarchar(50) NULL,
    [IN_TIME1] char(5) NULL,
    [IN_MOREDAY1] bit NULL,
    [IN_CHECK1] bit NULL,
    [IN_FORE_MINUTE1] int NULL,
    [IN_BACK_MINUTE1] int NULL,
    [IN_LATE_MINUTE1] int NULL,
    [OUT_TIME1] char(5) NULL,
    [OUT_MOREDAY1] bit NULL,
    [OUT_CHECK1] bit NULL,
    [OUT_FORE_MINUTE1] int NULL,
    [OUT_BACK_MINUTE1] int NULL,
    [OUT_LATE_MINUTE1] int NULL,
    [WORK_HOURS1] float NULL,
    [ADD_HOURS1] float NULL,
    [IF_OVERTIME1] bit NULL,
    [ADD_HOUR12] float NULL,
    [IN_TIME2] char(5) NULL,
    [IN_MOREDAY2] bit NULL,
    [IN_CHECK2] bit NULL,
    [IN_FORE_MINUTE2] int NULL,
    [IN_BACK_MINUTE2] int NULL,
    [IN_LATE_MINUTE2] int NULL,
    [OUT_TIME2] char(5) NULL,
    [OUT_MOREDAY2] bit NULL,
    [OUT_CHECK2] bit NULL,
    [OUT_FORE_MINUTE2] int NULL,
    [OUT_BACK_MINUTE2] int NULL,
    [OUT_LATE_MINUTE2] int NULL,
    [WORK_HOURS2] float NULL,
    [ADD_HOURS2] float NULL,
    [IF_OVERTIME2] bit NULL,
    [ADD_HOUR23] float NULL,
    [IN_TIME3] char(5) NULL,
    [IN_MOREDAY3] bit NULL,
    [IN_CHECK3] bit NULL,
    [IN_FORE_MINUTE3] int NULL,
    [IN_BACK_MINUTE3] int NULL,
    [IN_LATE_MINUTE3] int NULL,
    [OUT_TIME3] char(5) NULL,
    [OUT_MOREDAY3] bit NULL,
    [OUT_CHECK3] bit NULL,
    [OUT_FORE_MINUTE3] int NULL,
    [OUT_BACK_MINUTE3] int NULL,
    [OUT_LATE_MINUTE3] int NULL,
    [WORK_HOURS3] float NULL,
    [ADD_HOURS3] float NULL,
    [IF_OVERTIME3] bit NULL,
    [ADD_HOUR34] float NULL,
    [IN_TIME4] char(5) NULL,
    [IN_MOREDAY4] bit NULL,
    [IN_CHECK4] bit NULL,
    [IN_FORE_MINUTE4] int NULL,
    [IN_BACK_MINUTE4] int NULL,
    [IN_LATE_MINUTE4] int NULL,
    [IN_ADD_HOUR4] float NULL,
    [OUT_TIME4] char(5) NULL,
    [OUT_MOREDAY4] bit NULL,
    [OUT_CHECK4] bit NULL,
    [OUT_FORE_MINUTE4] int NULL,
    [OUT_BACK_MINUTE4] int NULL,
    [OUT_LATE_MINUTE4] int NULL,
    [OUT_ADD_HOUR4] float NULL,
    [WORK_HOURS4] float NULL,
    [ADD_HOURS4] float NULL,
    [IF_OVERTIME4] bit NULL,
    [IF_CONFIRM] bit NULL,
    [IF_ABSENT] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [IS_OUT_FLEX1] bit NULL,
    [IS_OUT_FLEX2] bit NULL,
    [IS_OUT_FLEX3] bit NULL,
    [IS_OUT_FLEX4] bit NULL,
    CONSTRAINT [PK_HR_TIMETYPE] PRIMARY KEY CLUSTERED ([TIMETYPE_ID])
);
GO

------------------------------------------------------------------------------
-- HR_TITLE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_TITLE] (
    [TITLE_ID] nchar(10) NOT NULL,
    [TITLE_NAME] nvarchar(50) NULL,
    [TITLE_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_TITLE] PRIMARY KEY CLUSTERED ([TITLE_ID])
);
GO

------------------------------------------------------------------------------
-- HR_TXT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_TXT_D] (
    [SERIAL_NO] smallint NOT NULL,
    [FILE_NAME_CLIENT] nvarchar(200) NULL,
    [FILE_NAME_SERVER] nvarchar(200) NULL,
    [TXT_TYPE] nchar(10) NOT NULL,
    [TXT_NO] nchar(20) NOT NULL,
    CONSTRAINT [PK_HR_TXT_D] PRIMARY KEY CLUSTERED ([TXT_TYPE], [TXT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HR_TXT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_TXT_M] (
    [TXT_DATE] datetime NOT NULL,
    [IS_PROCESSED] bit NULL,
    [PROCESS_DATE] datetime NULL,
    [PROCESS_MAN] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_542624976] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TXT_TYPE] nchar(10) NOT NULL,
    [TXT_NO] nchar(20) NOT NULL,
    CONSTRAINT [PK_HR_TXT_M] PRIMARY KEY CLUSTERED ([TXT_TYPE], [TXT_NO])
);
GO

------------------------------------------------------------------------------
-- HR_WAGE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_WAGE] (
    [WAGE_ID] nchar(20) NOT NULL,
    [WAGE_NAME] nvarchar(50) NULL,
    [SOURCE_FIELD] nvarchar(50) NULL,
    [SOURCE_SQL] varchar(4000) NULL,
    [SOURCE_EXP] varchar(2000) NULL,
    [DISPLAY_FORMAT] nvarchar(50) NULL,
    [SQL_REMARK] nvarchar(2000) NULL,
    [WAGE_FIELD] nvarchar(50) NOT NULL,
    [CALC_ORDER] smallint NULL,
    [IS_USED] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_WAGE] PRIMARY KEY CLUSTERED ([WAGE_ID]),
    CONSTRAINT [IX_HR_WAGE] UNIQUE ([WAGE_FIELD])
);
GO

------------------------------------------------------------------------------
-- HR_WAGE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_WAGE_D] (
    [WAGE_TYPE] nchar(10) NOT NULL,
    [WAGE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [WAGE_ITEM01] float NULL CONSTRAINT [DF_WAGE_ITEM01_590625147] DEFAULT ((0)),
    [WAGE_ITEM02] float NULL CONSTRAINT [DF_WAGE_ITEM02_590625147] DEFAULT ((0)),
    [WAGE_ITEM03] float NULL CONSTRAINT [DF_WAGE_ITEM03_590625147] DEFAULT ((0)),
    [WAGE_ITEM04] float NULL CONSTRAINT [DF_WAGE_ITEM04_590625147] DEFAULT ((0)),
    [WAGE_ITEM05] float NULL CONSTRAINT [DF_WAGE_ITEM05_590625147] DEFAULT ((0)),
    [WAGE_ITEM06] float NULL CONSTRAINT [DF_WAGE_ITEM06_590625147] DEFAULT ((0)),
    [WAGE_ITEM07] float NULL CONSTRAINT [DF_WAGE_ITEM07_590625147] DEFAULT ((0)),
    [WAGE_ITEM08] float NULL CONSTRAINT [DF_WAGE_ITEM08_590625147] DEFAULT ((0)),
    [WAGE_ITEM09] float NULL CONSTRAINT [DF_WAGE_ITEM09_590625147] DEFAULT ((0)),
    [WAGE_ITEM10] float NULL CONSTRAINT [DF_WAGE_ITEM10_590625147] DEFAULT ((0)),
    [WAGE_ITEM11] float NULL CONSTRAINT [DF_WAGE_ITEM11_590625147] DEFAULT ((0)),
    [WAGE_ITEM12] float NULL CONSTRAINT [DF_WAGE_ITEM12_590625147] DEFAULT ((0)),
    [WAGE_ITEM13] float NULL CONSTRAINT [DF_WAGE_ITEM13_590625147] DEFAULT ((0)),
    [WAGE_ITEM14] float NULL CONSTRAINT [DF_WAGE_ITEM14_590625147] DEFAULT ((0)),
    [WAGE_ITEM15] float NULL CONSTRAINT [DF_WAGE_ITEM15_590625147] DEFAULT ((0)),
    [WAGE_ITEM16] float NULL CONSTRAINT [DF_WAGE_ITEM16_590625147] DEFAULT ((0)),
    [WAGE_ITEM17] float NULL CONSTRAINT [DF_WAGE_ITEM17_590625147] DEFAULT ((0)),
    [WAGE_ITEM18] float NULL CONSTRAINT [DF_WAGE_ITEM18_590625147] DEFAULT ((0)),
    [WAGE_ITEM19] float NULL CONSTRAINT [DF_WAGE_ITEM19_590625147] DEFAULT ((0)),
    [WAGE_ITEM20] float NULL CONSTRAINT [DF_WAGE_ITEM20_590625147] DEFAULT ((0)),
    [WAGE_ITEM21] float NULL CONSTRAINT [DF_WAGE_ITEM21_590625147] DEFAULT ((0)),
    [WAGE_ITEM22] float NULL CONSTRAINT [DF_WAGE_ITEM22_590625147] DEFAULT ((0)),
    [WAGE_ITEM23] float NULL CONSTRAINT [DF_WAGE_ITEM23_590625147] DEFAULT ((0)),
    [WAGE_ITEM24] float NULL CONSTRAINT [DF_WAGE_ITEM24_590625147] DEFAULT ((0)),
    [WAGE_ITEM25] float NULL CONSTRAINT [DF_WAGE_ITEM25_590625147] DEFAULT ((0)),
    [WAGE_ITEM26] float NULL CONSTRAINT [DF_WAGE_ITEM26_590625147] DEFAULT ((0)),
    [WAGE_ITEM27] float NULL CONSTRAINT [DF_WAGE_ITEM27_590625147] DEFAULT ((0)),
    [WAGE_ITEM28] float NULL CONSTRAINT [DF_WAGE_ITEM28_590625147] DEFAULT ((0)),
    [WAGE_ITEM29] float NULL CONSTRAINT [DF_WAGE_ITEM29_590625147] DEFAULT ((0)),
    [WAGE_ITEM30] float NULL CONSTRAINT [DF_WAGE_ITEM30_590625147] DEFAULT ((0)),
    [WAGE_ITEM31] float NULL CONSTRAINT [DF_WAGE_ITEM31_590625147] DEFAULT ((0)),
    [WAGE_ITEM32] float NULL CONSTRAINT [DF_WAGE_ITEM32_590625147] DEFAULT ((0)),
    [WAGE_ITEM33] float NULL CONSTRAINT [DF_WAGE_ITEM33_590625147] DEFAULT ((0)),
    [WAGE_ITEM34] float NULL CONSTRAINT [DF_WAGE_ITEM34_590625147] DEFAULT ((0)),
    [WAGE_ITEM35] float NULL CONSTRAINT [DF_WAGE_ITEM35_590625147] DEFAULT ((0)),
    [WAGE_ITEM36] float NULL CONSTRAINT [DF_WAGE_ITEM36_590625147] DEFAULT ((0)),
    [WAGE_ITEM37] float NULL CONSTRAINT [DF_WAGE_ITEM37_590625147] DEFAULT ((0)),
    [WAGE_ITEM38] float NULL CONSTRAINT [DF_WAGE_ITEM38_590625147] DEFAULT ((0)),
    [WAGE_ITEM39] float NULL CONSTRAINT [DF_WAGE_ITEM39_590625147] DEFAULT ((0)),
    [WAGE_ITEM40] float NULL CONSTRAINT [DF_WAGE_ITEM40_590625147] DEFAULT ((0)),
    [WAGE_ITEM41] float NULL CONSTRAINT [DF_WAGE_ITEM41_590625147] DEFAULT ((0)),
    [WAGE_ITEM42] float NULL CONSTRAINT [DF_WAGE_ITEM42_590625147] DEFAULT ((0)),
    [WAGE_ITEM43] float NULL CONSTRAINT [DF_WAGE_ITEM43_590625147] DEFAULT ((0)),
    [WAGE_ITEM44] float NULL CONSTRAINT [DF_WAGE_ITEM44_590625147] DEFAULT ((0)),
    [WAGE_ITEM45] float NULL CONSTRAINT [DF_WAGE_ITEM45_590625147] DEFAULT ((0)),
    [WAGE_ITEM46] float NULL CONSTRAINT [DF_WAGE_ITEM46_590625147] DEFAULT ((0)),
    [WAGE_ITEM47] float NULL CONSTRAINT [DF_WAGE_ITEM47_590625147] DEFAULT ((0)),
    [WAGE_ITEM48] float NULL CONSTRAINT [DF_WAGE_ITEM48_590625147] DEFAULT ((0)),
    [WAGE_ITEM49] float NULL CONSTRAINT [DF_WAGE_ITEM49_590625147] DEFAULT ((0)),
    [WAGE_ITEM50] float NULL CONSTRAINT [DF_WAGE_ITEM50_590625147] DEFAULT ((0)),
    [WAGE_ITEM51] float NULL CONSTRAINT [DF_WAGE_ITEM51_590625147] DEFAULT ((0)),
    [WAGE_ITEM52] float NULL CONSTRAINT [DF_WAGE_ITEM52_590625147] DEFAULT ((0)),
    [WAGE_ITEM53] float NULL CONSTRAINT [DF_WAGE_ITEM53_590625147] DEFAULT ((0)),
    [WAGE_ITEM54] float NULL CONSTRAINT [DF_WAGE_ITEM54_590625147] DEFAULT ((0)),
    [WAGE_ITEM55] float NULL CONSTRAINT [DF_WAGE_ITEM55_590625147] DEFAULT ((0)),
    [WAGE_ITEM56] float NULL CONSTRAINT [DF_WAGE_ITEM56_590625147] DEFAULT ((0)),
    [WAGE_ITEM57] float NULL CONSTRAINT [DF_WAGE_ITEM57_590625147] DEFAULT ((0)),
    [WAGE_ITEM58] float NULL CONSTRAINT [DF_WAGE_ITEM58_590625147] DEFAULT ((0)),
    [WAGE_ITEM59] float NULL CONSTRAINT [DF_WAGE_ITEM59_590625147] DEFAULT ((0)),
    [WAGE_ITEM60] float NULL CONSTRAINT [DF_WAGE_ITEM60_590625147] DEFAULT ((0)),
    [WAGE_ITEM61] float NULL CONSTRAINT [DF_WAGE_ITEM61_590625147] DEFAULT ((0)),
    [WAGE_ITEM62] float NULL CONSTRAINT [DF_WAGE_ITEM62_590625147] DEFAULT ((0)),
    [WAGE_ITEM63] float NULL CONSTRAINT [DF_WAGE_ITEM63_590625147] DEFAULT ((0)),
    [WAGE_ITEM64] float NULL CONSTRAINT [DF_WAGE_ITEM64_590625147] DEFAULT ((0)),
    [WAGE_ITEM65] float NULL CONSTRAINT [DF_WAGE_ITEM65_590625147] DEFAULT ((0)),
    [WAGE_ITEM66] float NULL CONSTRAINT [DF_WAGE_ITEM66_590625147] DEFAULT ((0)),
    [WAGE_ITEM67] float NULL CONSTRAINT [DF_WAGE_ITEM67_590625147] DEFAULT ((0)),
    [WAGE_ITEM68] float NULL CONSTRAINT [DF_WAGE_ITEM68_590625147] DEFAULT ((0)),
    [WAGE_ITEM69] float NULL CONSTRAINT [DF_WAGE_ITEM69_590625147] DEFAULT ((0)),
    [WAGE_ITEM70] float NULL CONSTRAINT [DF_WAGE_ITEM70_590625147] DEFAULT ((0)),
    [WAGE_ITEM71] float NULL CONSTRAINT [DF_WAGE_ITEM71_590625147] DEFAULT ((0)),
    [WAGE_ITEM72] float NULL CONSTRAINT [DF_WAGE_ITEM72_590625147] DEFAULT ((0)),
    [WAGE_ITEM73] float NULL CONSTRAINT [DF_WAGE_ITEM73_590625147] DEFAULT ((0)),
    [WAGE_ITEM74] float NULL CONSTRAINT [DF_WAGE_ITEM74_590625147] DEFAULT ((0)),
    [WAGE_ITEM75] float NULL CONSTRAINT [DF_WAGE_ITEM75_590625147] DEFAULT ((0)),
    [WAGE_ITEM76] float NULL CONSTRAINT [DF_WAGE_ITEM76_590625147] DEFAULT ((0)),
    [WAGE_ITEM77] float NULL CONSTRAINT [DF_WAGE_ITEM77_590625147] DEFAULT ((0)),
    [WAGE_ITEM78] float NULL CONSTRAINT [DF_WAGE_ITEM78_590625147] DEFAULT ((0)),
    [WAGE_ITEM79] float NULL CONSTRAINT [DF_WAGE_ITEM79_590625147] DEFAULT ((0)),
    [WAGE_ITEM80] float NULL CONSTRAINT [DF_WAGE_ITEM80_590625147] DEFAULT ((0)),
    [WAGE_ITEM81] float NULL CONSTRAINT [DF_WAGE_ITEM81_590625147] DEFAULT ((0)),
    [WAGE_ITEM82] float NULL CONSTRAINT [DF_WAGE_ITEM82_590625147] DEFAULT ((0)),
    [WAGE_ITEM83] float NULL CONSTRAINT [DF_WAGE_ITEM83_590625147] DEFAULT ((0)),
    [WAGE_ITEM84] float NULL CONSTRAINT [DF_WAGE_ITEM84_590625147] DEFAULT ((0)),
    [WAGE_ITEM85] float NULL CONSTRAINT [DF_WAGE_ITEM85_590625147] DEFAULT ((0)),
    [WAGE_ITEM86] float NULL CONSTRAINT [DF_WAGE_ITEM86_590625147] DEFAULT ((0)),
    [WAGE_ITEM87] float NULL CONSTRAINT [DF_WAGE_ITEM87_590625147] DEFAULT ((0)),
    [WAGE_ITEM88] float NULL CONSTRAINT [DF_WAGE_ITEM88_590625147] DEFAULT ((0)),
    [WAGE_ITEM89] float NULL CONSTRAINT [DF_WAGE_ITEM89_590625147] DEFAULT ((0)),
    [WAGE_ITEM90] float NULL CONSTRAINT [DF_WAGE_ITEM90_590625147] DEFAULT ((0)),
    [WAGE_ITEM91] float NULL CONSTRAINT [DF_WAGE_ITEM91_590625147] DEFAULT ((0)),
    [WAGE_ITEM92] float NULL CONSTRAINT [DF_WAGE_ITEM92_590625147] DEFAULT ((0)),
    [WAGE_ITEM93] float NULL CONSTRAINT [DF_WAGE_ITEM93_590625147] DEFAULT ((0)),
    [WAGE_ITEM94] float NULL CONSTRAINT [DF_WAGE_ITEM94_590625147] DEFAULT ((0)),
    [WAGE_ITEM95] float NULL CONSTRAINT [DF_WAGE_ITEM95_590625147] DEFAULT ((0)),
    [WAGE_ITEM96] float NULL CONSTRAINT [DF_WAGE_ITEM96_590625147] DEFAULT ((0)),
    [WAGE_ITEM97] float NULL CONSTRAINT [DF_WAGE_ITEM97_590625147] DEFAULT ((0)),
    [WAGE_ITEM98] float NULL CONSTRAINT [DF_WAGE_ITEM98_590625147] DEFAULT ((0)),
    [WAGE_ITEM99] float NULL CONSTRAINT [DF_WAGE_ITEM99_590625147] DEFAULT ((0)),
    [WAGE_ITEMA0] float NULL CONSTRAINT [DF_WAGE_ITEMA0_590625147] DEFAULT ((0)),
    [WAGE_ITEMA1] float NULL CONSTRAINT [DF_WAGE_ITEMA1_590625147] DEFAULT ((0)),
    [WAGE_ITEMA2] float NULL CONSTRAINT [DF_WAGE_ITEMA2_590625147] DEFAULT ((0)),
    [WAGE_ITEMA3] float NULL CONSTRAINT [DF_WAGE_ITEMA3_590625147] DEFAULT ((0)),
    [WAGE_ITEMA4] float NULL CONSTRAINT [DF_WAGE_ITEMA4_590625147] DEFAULT ((0)),
    [WAGE_ITEMA5] float NULL CONSTRAINT [DF_WAGE_ITEMA5_590625147] DEFAULT ((0)),
    [WAGE_ITEMA6] float NULL CONSTRAINT [DF_WAGE_ITEMA6_590625147] DEFAULT ((0)),
    [WAGE_ITEMA7] float NULL CONSTRAINT [DF_WAGE_ITEMA7_590625147] DEFAULT ((0)),
    [WAGE_ITEMA8] float NULL CONSTRAINT [DF_WAGE_ITEMA8_590625147] DEFAULT ((0)),
    [WAGE_ITEMA9] float NULL CONSTRAINT [DF_WAGE_ITEMA9_590625147] DEFAULT ((0)),
    [DIMISSION_TYPE] nchar(10) NULL,
    [DIMISSION_NO] nchar(20) NULL,
    [REMARK] nvarchar(100) NULL,
    [ADD_REMARK] nvarchar(50) NULL,
    [SUB_REMARK] nvarchar(50) NULL,
    CONSTRAINT [PK_HR_WAGE_D] PRIMARY KEY CLUSTERED ([WAGE_TYPE], [WAGE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_WAGE_D] ON dbo.[HR_WAGE_D] ([EMP_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_HR_WAGE_D_1] ON dbo.[HR_WAGE_D] ([DIMISSION_TYPE], [DIMISSION_NO]);
GO

------------------------------------------------------------------------------
-- HR_WAGE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_WAGE_M] (
    [WAGE_TYPE] nchar(10) NOT NULL,
    [WAGE_NO] nchar(20) NOT NULL,
    [WAGE_DATE] datetime NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [DEPT_ID] nchar(10) NULL,
    [IF_SECRECY] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_606625204] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [IF_DIMISSION] bit NULL,
    CONSTRAINT [PK_HR_WAGE_M] PRIMARY KEY CLUSTERED ([WAGE_TYPE], [WAGE_NO])
);
GO

------------------------------------------------------------------------------
-- HR_WAGESYS
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_WAGESYS] (
    [WAGE_ID] nchar(20) NOT NULL,
    [WAGE_NAME] nvarchar(50) NULL,
    [SOURCE_FIELD] nvarchar(50) NULL,
    [SOURCE_SQL] varchar(4000) NULL,
    [SOURCE_EXP] varchar(2000) NULL,
    [DISPLAY_FORMAT] nvarchar(50) NULL,
    [SQL_REMARK] nvarchar(2000) NULL,
    [WAGE_FIELD] nvarchar(50) NULL,
    [CALC_ORDER] smallint NULL,
    [IS_USED] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_WAGESYS] PRIMARY KEY CLUSTERED ([WAGE_ID])
);
GO

------------------------------------------------------------------------------
-- HR_WORKTIME_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_WORKTIME_D] (
    [WORKTIME_TYPE] nchar(10) NOT NULL,
    [WORKTIME_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [APPLY_TYPE] nchar(10) NULL,
    [APPLY_NO] nchar(20) NULL,
    [EMP_ID] nchar(10) NULL,
    [ON1] char(5) NULL,
    [OUT1] char(5) NULL,
    [ON2] char(5) NULL,
    [OUT2] char(5) NULL,
    [ON3] char(5) NULL,
    [OUT3] char(5) NULL,
    [ON4] char(5) NULL,
    [OUT4] char(5) NULL,
    [WORKTIME] float NULL,
    [OVERTIME] float NULL,
    [REST_OVERTIME] float NULL,
    [HOLIDAY_OVERTIME] float NULL,
    [BE_LATE_FOR] float NULL,
    [LATE_TIMES] float NULL,
    [LEAVE_EARLY] float NULL,
    [LEAVE_EARLY_TIMES] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HR_WORKTIME_D] PRIMARY KEY CLUSTERED ([WORKTIME_TYPE], [WORKTIME_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_HR_WORKTIME_D_1] ON dbo.[HR_WORKTIME_D] ([APPLY_TYPE], [APPLY_NO], [EMP_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_HR_WORKTIME_D] ON dbo.[HR_WORKTIME_D] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- HR_WORKTIME_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_WORKTIME_M] (
    [WORKTIME_TYPE] nchar(10) NOT NULL,
    [WORKTIME_NO] nchar(20) NOT NULL,
    [WORKTIME_DATE] datetime NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [COUNT_MONTH] nchar(6) NULL,
    CONSTRAINT [PK_HR_WORKTIME_M] PRIMARY KEY CLUSTERED ([WORKTIME_TYPE], [WORKTIME_NO])
);
GO

------------------------------------------------------------------------------
-- HR_WORKTYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HR_WORKTYPE] (
    [WORKTYPE_ID] nchar(10) NOT NULL,
    [WORKTYPE_NAME] nvarchar(50) NULL,
    [WORKTYPE_SUM] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HR_WORKTYPE] PRIMARY KEY CLUSTERED ([WORKTYPE_ID])
);
GO

------------------------------------------------------------------------------
-- HRM_ADJUST_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_ADJUST_D] (
    [ADJUST_TYPE] nchar(10) NOT NULL,
    [ADJUST_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [COUNT_DATE] smalldatetime NULL,
    [EMP_ID] nchar(10) NULL,
    [TIMETYPE_ID] char(10) NULL,
    [IN_TIME1] char(5) NULL,
    [OUT_TIME1] char(5) NULL,
    [WORK_HOURS1] float NULL,
    [ADD_HOURS1] float NULL,
    [IN_TIME2] char(5) NULL,
    [OUT_TIME2] char(5) NULL,
    [WORK_HOURS2] float NULL,
    [ADD_HOURS2] float NULL,
    [IN_TIME3] char(5) NULL,
    [OUT_TIME3] char(5) NULL,
    [WORK_HOURS3] float NULL,
    [ADD_HOURS3] float NULL,
    [IN_TIME4] char(5) NULL,
    [OUT_TIME4] char(5) NULL,
    [WORK_HOURS4] float NULL,
    [ADD_HOURS4] float NULL,
    [REMARK] nvarchar(50) NULL,
    CONSTRAINT [PK_HRM_ADJUST_D] PRIMARY KEY CLUSTERED ([ADJUST_TYPE], [ADJUST_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_ADJUST_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_ADJUST_M] (
    [ADJUST_TYPE] nchar(10) NOT NULL,
    [ADJUST_NO] nchar(20) NOT NULL,
    [ADJUST_DATE] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1170103209] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_ADJUST_M] PRIMARY KEY CLUSTERED ([ADJUST_TYPE], [ADJUST_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_BASEPAY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_BASEPAY_D] (
    [BASEPAY_TYPE] nchar(10) NOT NULL,
    [BASEPAY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [BASEPAY_ITEM01] float NULL,
    [BASEPAY_ITEM02] float NULL,
    [BASEPAY_ITEM03] float NULL,
    [BASEPAY_ITEM04] float NULL,
    [BASEPAY_ITEM05] float NULL,
    [BASEPAY_ITEM06] float NULL,
    [BASEPAY_ITEM07] float NULL,
    [BASEPAY_ITEM08] float NULL,
    [BASEPAY_ITEM09] float NULL,
    [BASEPAY_ITEM10] float NULL,
    [BASEPAY_ITEM11] float NULL,
    [BASEPAY_ITEM12] float NULL,
    [BASEPAY_ITEM13] float NULL,
    [BASEPAY_ITEM14] float NULL,
    [BASEPAY_ITEM15] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HRM_BASEPAY_D] PRIMARY KEY CLUSTERED ([BASEPAY_TYPE], [BASEPAY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_BASEPAY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_BASEPAY_M] (
    [BASEPAY_TYPE] nchar(10) NOT NULL,
    [BASEPAY_NO] nchar(20) NOT NULL,
    [BASEPAY_DATE] datetime NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [DEPT_ID] nchar(10) NULL,
    [IF_SECRECY] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1202103323] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_BASEPAY_M] PRIMARY KEY CLUSTERED ([BASEPAY_TYPE], [BASEPAY_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_DIARY
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_DIARY] (
    [COUNT_DATE] smalldatetime NOT NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [TIMETYPE_ID] nchar(10) NULL,
    [ON1] char(5) NULL,
    [OUT1] char(5) NULL,
    [ON2] char(5) NULL,
    [OUT2] char(5) NULL,
    [ON3] char(5) NULL,
    [OUT3] char(5) NULL,
    [ON4] char(5) NULL,
    [OUT4] char(5) NULL,
    [WORKTIME] float NULL,
    [OVERTIME] float NULL,
    [REST_OVERTIME] float NULL,
    [HOLIDAY_OVERTIME] float NULL,
    [ABSENT_TIME] float NULL,
    [BE_LATE_FOR] float NULL,
    [LATE_TIMES] float NULL,
    [LEAVE_EARLY] float NULL,
    [LEAVE_EARLY_TIMES] float NULL,
    [BE_LATE_FOR1] float NULL,
    [BE_LATE_FOR2] float NULL,
    [BE_LATE_FOR3] float NULL,
    [BE_LATE_FOR4] float NULL,
    [LEAVE_EARLY1] float NULL,
    [LEAVE_EARLY2] float NULL,
    [LEAVE_EARLY3] float NULL,
    [LEAVE_EARLY4] float NULL,
    [SIGN_IN] nvarchar(100) NULL,
    [ERR_TIME] nvarchar(200) NULL,
    [EXCHANGE_DATE] datetime NULL,
    [ON_DUTY_TIME] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_HRM_DIARY] PRIMARY KEY CLUSTERED ([COUNT_DATE], [EMP_ID])
);
GO

------------------------------------------------------------------------------
-- HRM_EVECTION_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_EVECTION_D] (
    [EVECTION_TYPE] nchar(10) NOT NULL,
    [EVECTION_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [EVECTION_ID] nchar(10) NULL,
    [START_DATE] datetime NULL,
    [START_TIME] char(5) NULL,
    [END_DATE] datetime NULL,
    [END_TIME] char(5) NULL,
    [EVECTION_DAYS] float NULL,
    [COUNT_DAYS] float NULL,
    [EVECTION_SUM] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HRM_EVECTION_D] PRIMARY KEY CLUSTERED ([EVECTION_TYPE], [EVECTION_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_EVECTION_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_EVECTION_M] (
    [EVECTION_TYPE] nchar(10) NOT NULL,
    [EVECTION_NO] nchar(20) NOT NULL,
    [EVECTION_DATE] datetime NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1250103494] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_EVECTION_M] PRIMARY KEY CLUSTERED ([EVECTION_TYPE], [EVECTION_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_EXCHANGE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_EXCHANGE_D] (
    [EXCHANGE_TYPE] nchar(10) NOT NULL,
    [EXCHANGE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [FIRST_DAY] datetime NULL,
    [END_DAY] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_HRM_EXCHANGE_D] PRIMARY KEY CLUSTERED ([EXCHANGE_TYPE], [EXCHANGE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_EXCHANGE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_EXCHANGE_M] (
    [EXCHANGE_TYPE] nchar(10) NOT NULL,
    [EXCHANGE_NO] nchar(20) NOT NULL,
    [EXCHANGE_DATE] datetime NULL,
    [TYPE] bit NULL CONSTRAINT [DF_TYPE_1282103608] DEFAULT ((1)),
    [FIRST_DAY] datetime NULL,
    [END_DAY] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1282103608] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_EXCHANGE_M] PRIMARY KEY CLUSTERED ([EXCHANGE_TYPE], [EXCHANGE_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_HOLIDAY
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_HOLIDAY] (
    [HOLIDAY_ID] nchar(10) NOT NULL,
    [HOLIDAY_NAME] nvarchar(50) NULL,
    [START_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [HOLIDAY_DAYS] smallint NULL,
    [COUNT_MULTIPLE] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_HOLIDAY] PRIMARY KEY CLUSTERED ([HOLIDAY_ID])
);
GO

------------------------------------------------------------------------------
-- HRM_LEAVE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_LEAVE_D] (
    [LEAVE_TYPE] nchar(10) NOT NULL,
    [LEAVE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [LEAVE_ID] nchar(10) NULL,
    [START_DATE] datetime NULL,
    [START_TIME] char(5) NULL,
    [END_DATE] datetime NULL,
    [END_TIME] char(5) NULL,
    [LEAVE_DAYS] float NULL,
    [COUNT_DAYS] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HRM_LEAVE_D] PRIMARY KEY CLUSTERED ([LEAVE_TYPE], [LEAVE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_LEAVE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_LEAVE_M] (
    [LEAVE_TYPE] nchar(10) NOT NULL,
    [LEAVE_NO] nchar(20) NOT NULL,
    [LEAVE_DATE] datetime NULL,
    [COUNT_MULTIPLE] float NULL,
    [COUNT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1330103779] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_LEAVE_M] PRIMARY KEY CLUSTERED ([LEAVE_TYPE], [LEAVE_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_PLAN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_PLAN_D] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [DAY_01] nchar(10) NULL,
    [DAY_02] nchar(10) NULL,
    [DAY_03] nchar(10) NULL,
    [DAY_04] nchar(10) NULL,
    [DAY_05] nchar(10) NULL,
    [DAY_06] nchar(10) NULL,
    [DAY_07] nchar(10) NULL,
    [DAY_08] nchar(10) NULL,
    [DAY_09] nchar(10) NULL,
    [DAY_10] nchar(10) NULL,
    [DAY_11] nchar(10) NULL,
    [DAY_12] nchar(10) NULL,
    [DAY_13] nchar(10) NULL,
    [DAY_14] nchar(10) NULL,
    [DAY_15] nchar(10) NULL,
    [DAY_16] nchar(10) NULL,
    [DAY_17] nchar(10) NULL,
    [DAY_18] nchar(10) NULL,
    [DAY_19] nchar(10) NULL,
    [DAY_20] nchar(10) NULL,
    [DAY_21] nchar(10) NULL,
    [DAY_22] nchar(10) NULL,
    [DAY_23] nchar(10) NULL,
    [DAY_24] nchar(10) NULL,
    [DAY_25] nchar(10) NULL,
    [DAY_26] nchar(10) NULL,
    [DAY_27] nchar(10) NULL,
    [DAY_28] nchar(10) NULL,
    [DAY_29] nchar(10) NULL,
    [DAY_30] nchar(10) NULL,
    [DAY_31] nchar(10) NULL,
    CONSTRAINT [PK_HRM_PLAN_D] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_PLAN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_PLAN_M] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [PLAN_DATE] datetime NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1362103893] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_PLAN_M] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_RECESS_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_RECESS_D] (
    [RECESS_TYPE] nchar(10) NOT NULL,
    [RECESS_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [START_DATE] datetime NULL,
    [START_TIME] char(5) NULL,
    [END_DATE] datetime NULL,
    [END_TIME] char(5) NULL,
    [RECESS_DAYS] float NULL,
    [COUNT_DAYS] float NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HRM_RECESS_D] PRIMARY KEY CLUSTERED ([RECESS_TYPE], [RECESS_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_RECESS_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_RECESS_M] (
    [RECESS_TYPE] nchar(10) NOT NULL,
    [RECESS_NO] nchar(20) NOT NULL,
    [RECESS_DATE] datetime NULL,
    [TYPE] bit NULL CONSTRAINT [DF_TYPE_1394104007] DEFAULT ((1)),
    [START_DATE] datetime NULL,
    [START_TIME] char(5) NULL,
    [END_DATE] datetime NULL,
    [END_TIME] char(5) NULL,
    [RECESS_DAYS] float NULL,
    [COUNT_DAYS] float NULL CONSTRAINT [DF_COUNT_DAYS_1394104007] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1394104007] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_RECESS_M] PRIMARY KEY CLUSTERED ([RECESS_TYPE], [RECESS_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_SETUP
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_SETUP] (
    [MACHINE_START] smallint NOT NULL,
    [MACHINE_LENGTH] smallint NULL,
    [TYPE_START] smallint NULL,
    [TYPE_LENGTH] smallint NULL,
    [CARD_START] smallint NULL,
    [CARD_LENGTH] smallint NULL,
    [YEAR_START] smallint NULL,
    [YEAR_LENGTH] smallint NULL,
    [MONTH_START] smallint NULL,
    [MONTH_LENGTH] smallint NULL,
    [DAY_START] smallint NULL,
    [DAY_LENGTH] smallint NULL,
    [HOUR_START] smallint NULL,
    [HOUR_LENGTH] smallint NULL,
    [MINUTE_START] smallint NULL,
    [MINUTE_LENGTH] smallint NULL,
    [SECOND_START] smallint NULL,
    [SECOND_LENGTH] smallint NULL,
    [SAT_REST_DAY] bit NULL CONSTRAINT [DF_SAT_REST_DAY_1410104064] DEFAULT ((1)),
    [SUN_REST_DAY] bit NULL CONSTRAINT [DF_SUN_REST_DAY_1410104064] DEFAULT ((1)),
    [SAT_ABSENT] bit NULL CONSTRAINT [DF_SAT_ABSENT_1410104064] DEFAULT ((0)),
    [SUN_ABSENT] bit NULL CONSTRAINT [DF_SUN_ABSENT_1410104064] DEFAULT ((0)),
    [HOLIDAY_ABSENT] bit NULL CONSTRAINT [DF_HOLIDAY_ABSENT_1410104064] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [REQUIRE_ENACTMENT] bit NULL,
    [WAGE_HOLITIME] nvarchar(100) NULL,
    [WAGE_RESTTIME] nvarchar(100) NULL,
    [WAGE_OVERTIME] nvarchar(100) NULL,
    [WAGE_WORKTIME] nvarchar(100) NULL,
    [WAGE_HOLIDAY] nvarchar(100) NULL,
    [WAGE_REST] nvarchar(100) NULL,
    [WAGE_OVER] nvarchar(100) NULL,
    [WAGE_WORK] nvarchar(100) NULL,
    [WAGE_ADD] nvarchar(100) NULL
);
GO

------------------------------------------------------------------------------
-- HRM_SIGN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_SIGN_D] (
    [SIGN_TYPE] nchar(10) NOT NULL,
    [SIGN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [COUNT_DATE] smalldatetime NULL,
    [ON1] char(5) NULL,
    [OUT1] char(5) NULL,
    [ON2] char(5) NULL,
    [OUT2] char(5) NULL,
    [ON3] char(5) NULL,
    [OUT3] char(5) NULL,
    [ON4] char(5) NULL,
    [OUT4] char(5) NULL,
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_HRM_SIGN_D] PRIMARY KEY CLUSTERED ([SIGN_TYPE], [SIGN_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_SIGN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_SIGN_M] (
    [SIGN_TYPE] nchar(10) NOT NULL,
    [SIGN_NO] nchar(20) NOT NULL,
    [SIGN_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1442104178] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_SIGN_M] PRIMARY KEY CLUSTERED ([SIGN_TYPE], [SIGN_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_TIMETYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_TIMETYPE] (
    [TIMETYPE_ID] char(10) NOT NULL,
    [TIMETYPE_NAME] nvarchar(50) NULL,
    [TYPE] nvarchar(50) NULL,
    [IN_TIME1] char(5) NULL,
    [IN_MOREDAY1] bit NULL,
    [IN_CHECK1] bit NULL,
    [IN_FORE_MINUTE1] int NULL,
    [IN_BACK_MINUTE1] int NULL,
    [IN_LATE_MINUTE1] int NULL,
    [OUT_TIME1] char(5) NULL,
    [OUT_MOREDAY1] bit NULL,
    [OUT_CHECK1] bit NULL,
    [OUT_FORE_MINUTE1] int NULL,
    [OUT_BACK_MINUTE1] int NULL,
    [OUT_LATE_MINUTE1] int NULL,
    [WORK_HOURS1] float NULL,
    [ADD_HOURS1] float NULL,
    [IF_OVERTIME1] bit NULL,
    [ADD_HOUR12] float NULL,
    [IN_TIME2] char(5) NULL,
    [IN_MOREDAY2] bit NULL,
    [IN_CHECK2] bit NULL,
    [IN_FORE_MINUTE2] int NULL,
    [IN_BACK_MINUTE2] int NULL,
    [IN_LATE_MINUTE2] int NULL,
    [OUT_TIME2] char(5) NULL,
    [OUT_MOREDAY2] bit NULL,
    [OUT_CHECK2] bit NULL,
    [OUT_FORE_MINUTE2] int NULL,
    [OUT_BACK_MINUTE2] int NULL,
    [OUT_LATE_MINUTE2] int NULL,
    [WORK_HOURS2] float NULL,
    [ADD_HOURS2] float NULL,
    [IF_OVERTIME2] bit NULL,
    [ADD_HOUR23] float NULL,
    [IN_TIME3] char(5) NULL,
    [IN_MOREDAY3] bit NULL,
    [IN_CHECK3] bit NULL,
    [IN_FORE_MINUTE3] int NULL,
    [IN_BACK_MINUTE3] int NULL,
    [IN_LATE_MINUTE3] int NULL,
    [OUT_TIME3] char(5) NULL,
    [OUT_MOREDAY3] bit NULL,
    [OUT_CHECK3] bit NULL,
    [OUT_FORE_MINUTE3] int NULL,
    [OUT_BACK_MINUTE3] int NULL,
    [OUT_LATE_MINUTE3] int NULL,
    [WORK_HOURS3] float NULL,
    [ADD_HOURS3] float NULL,
    [IF_OVERTIME3] bit NULL,
    [ADD_HOUR34] float NULL,
    [IN_TIME4] char(5) NULL,
    [IN_MOREDAY4] bit NULL,
    [IN_CHECK4] bit NULL,
    [IN_FORE_MINUTE4] int NULL,
    [IN_BACK_MINUTE4] int NULL,
    [IN_LATE_MINUTE4] int NULL,
    [IN_ADD_HOUR4] float NULL,
    [OUT_TIME4] char(5) NULL,
    [OUT_MOREDAY4] bit NULL,
    [OUT_CHECK4] bit NULL,
    [OUT_FORE_MINUTE4] int NULL,
    [OUT_BACK_MINUTE4] int NULL,
    [OUT_LATE_MINUTE4] int NULL,
    [OUT_ADD_HOUR4] float NULL,
    [WORK_HOURS4] float NULL,
    [ADD_HOURS4] float NULL,
    [IF_OVERTIME4] bit NULL,
    [IF_CONFIRM] bit NULL,
    [IF_ABSENT] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [IS_OUT_FLEX1] bit NULL,
    [IS_OUT_FLEX2] bit NULL,
    [IS_OUT_FLEX3] bit NULL,
    [IS_OUT_FLEX4] bit NULL,
    CONSTRAINT [PK_HRM_TIMETYPE] PRIMARY KEY CLUSTERED ([TIMETYPE_ID])
);
GO

------------------------------------------------------------------------------
-- HRM_WAGE
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_WAGE] (
    [WAGE_ID] nchar(20) NOT NULL,
    [WAGE_NAME] nvarchar(50) NULL,
    [SOURCE_FIELD] nvarchar(50) NULL,
    [SOURCE_SQL] varchar(4000) NULL,
    [SOURCE_EXP] varchar(2000) NULL,
    [DISPLAY_FORMAT] nvarchar(50) NULL,
    [SQL_REMARK] nvarchar(2000) NULL,
    [WAGE_FIELD] nvarchar(50) NOT NULL,
    [CALC_ORDER] smallint NULL,
    [IS_USED] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_WAGE] PRIMARY KEY CLUSTERED ([WAGE_ID])
);
GO

------------------------------------------------------------------------------
-- HRM_WAGE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_WAGE_D] (
    [WAGE_TYPE] nchar(10) NOT NULL,
    [WAGE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [DIMISSION_TYPE] nchar(10) NULL,
    [DIMISSION_NO] nchar(20) NULL,
    [WAGE_ITEM01] float NULL CONSTRAINT [DF_WAGE_ITEM01_1506104406] DEFAULT ((0)),
    [WAGE_ITEM02] float NULL CONSTRAINT [DF_WAGE_ITEM02_1506104406] DEFAULT ((0)),
    [WAGE_ITEM03] float NULL CONSTRAINT [DF_WAGE_ITEM03_1506104406] DEFAULT ((0)),
    [WAGE_ITEM04] float NULL CONSTRAINT [DF_WAGE_ITEM04_1506104406] DEFAULT ((0)),
    [WAGE_ITEM05] float NULL CONSTRAINT [DF_WAGE_ITEM05_1506104406] DEFAULT ((0)),
    [WAGE_ITEM06] float NULL CONSTRAINT [DF_WAGE_ITEM06_1506104406] DEFAULT ((0)),
    [WAGE_ITEM07] float NULL CONSTRAINT [DF_WAGE_ITEM07_1506104406] DEFAULT ((0)),
    [WAGE_ITEM08] float NULL CONSTRAINT [DF_WAGE_ITEM08_1506104406] DEFAULT ((0)),
    [WAGE_ITEM09] float NULL CONSTRAINT [DF_WAGE_ITEM09_1506104406] DEFAULT ((0)),
    [WAGE_ITEM10] float NULL CONSTRAINT [DF_WAGE_ITEM10_1506104406] DEFAULT ((0)),
    [WAGE_ITEM11] float NULL CONSTRAINT [DF_WAGE_ITEM11_1506104406] DEFAULT ((0)),
    [WAGE_ITEM12] float NULL CONSTRAINT [DF_WAGE_ITEM12_1506104406] DEFAULT ((0)),
    [WAGE_ITEM13] float NULL CONSTRAINT [DF_WAGE_ITEM13_1506104406] DEFAULT ((0)),
    [WAGE_ITEM14] float NULL CONSTRAINT [DF_WAGE_ITEM14_1506104406] DEFAULT ((0)),
    [WAGE_ITEM15] float NULL CONSTRAINT [DF_WAGE_ITEM15_1506104406] DEFAULT ((0)),
    [WAGE_ITEM16] float NULL CONSTRAINT [DF_WAGE_ITEM16_1506104406] DEFAULT ((0)),
    [WAGE_ITEM17] float NULL CONSTRAINT [DF_WAGE_ITEM17_1506104406] DEFAULT ((0)),
    [WAGE_ITEM18] float NULL CONSTRAINT [DF_WAGE_ITEM18_1506104406] DEFAULT ((0)),
    [WAGE_ITEM19] float NULL CONSTRAINT [DF_WAGE_ITEM19_1506104406] DEFAULT ((0)),
    [WAGE_ITEM20] float NULL CONSTRAINT [DF_WAGE_ITEM20_1506104406] DEFAULT ((0)),
    [WAGE_ITEM21] float NULL CONSTRAINT [DF_WAGE_ITEM21_1506104406] DEFAULT ((0)),
    [WAGE_ITEM22] float NULL CONSTRAINT [DF_WAGE_ITEM22_1506104406] DEFAULT ((0)),
    [WAGE_ITEM23] float NULL CONSTRAINT [DF_WAGE_ITEM23_1506104406] DEFAULT ((0)),
    [WAGE_ITEM24] float NULL CONSTRAINT [DF_WAGE_ITEM24_1506104406] DEFAULT ((0)),
    [WAGE_ITEM25] float NULL CONSTRAINT [DF_WAGE_ITEM25_1506104406] DEFAULT ((0)),
    [WAGE_ITEM26] float NULL CONSTRAINT [DF_WAGE_ITEM26_1506104406] DEFAULT ((0)),
    [WAGE_ITEM27] float NULL CONSTRAINT [DF_WAGE_ITEM27_1506104406] DEFAULT ((0)),
    [WAGE_ITEM28] float NULL CONSTRAINT [DF_WAGE_ITEM28_1506104406] DEFAULT ((0)),
    [WAGE_ITEM29] float NULL CONSTRAINT [DF_WAGE_ITEM29_1506104406] DEFAULT ((0)),
    [WAGE_ITEM30] float NULL CONSTRAINT [DF_WAGE_ITEM30_1506104406] DEFAULT ((0)),
    [WAGE_ITEM31] float NULL CONSTRAINT [DF_WAGE_ITEM31_1506104406] DEFAULT ((0)),
    [WAGE_ITEM32] float NULL CONSTRAINT [DF_WAGE_ITEM32_1506104406] DEFAULT ((0)),
    [WAGE_ITEM33] float NULL CONSTRAINT [DF_WAGE_ITEM33_1506104406] DEFAULT ((0)),
    [WAGE_ITEM34] float NULL CONSTRAINT [DF_WAGE_ITEM34_1506104406] DEFAULT ((0)),
    [WAGE_ITEM35] float NULL CONSTRAINT [DF_WAGE_ITEM35_1506104406] DEFAULT ((0)),
    [WAGE_ITEM36] float NULL CONSTRAINT [DF_WAGE_ITEM36_1506104406] DEFAULT ((0)),
    [WAGE_ITEM37] float NULL CONSTRAINT [DF_WAGE_ITEM37_1506104406] DEFAULT ((0)),
    [WAGE_ITEM38] float NULL CONSTRAINT [DF_WAGE_ITEM38_1506104406] DEFAULT ((0)),
    [WAGE_ITEM39] float NULL CONSTRAINT [DF_WAGE_ITEM39_1506104406] DEFAULT ((0)),
    [WAGE_ITEM40] float NULL CONSTRAINT [DF_WAGE_ITEM40_1506104406] DEFAULT ((0)),
    [WAGE_ITEM41] float NULL CONSTRAINT [DF_WAGE_ITEM41_1506104406] DEFAULT ((0)),
    [WAGE_ITEM42] float NULL CONSTRAINT [DF_WAGE_ITEM42_1506104406] DEFAULT ((0)),
    [WAGE_ITEM43] float NULL CONSTRAINT [DF_WAGE_ITEM43_1506104406] DEFAULT ((0)),
    [WAGE_ITEM44] float NULL CONSTRAINT [DF_WAGE_ITEM44_1506104406] DEFAULT ((0)),
    [WAGE_ITEM45] float NULL CONSTRAINT [DF_WAGE_ITEM45_1506104406] DEFAULT ((0)),
    [WAGE_ITEM46] float NULL CONSTRAINT [DF_WAGE_ITEM46_1506104406] DEFAULT ((0)),
    [WAGE_ITEM47] float NULL CONSTRAINT [DF_WAGE_ITEM47_1506104406] DEFAULT ((0)),
    [WAGE_ITEM48] float NULL CONSTRAINT [DF_WAGE_ITEM48_1506104406] DEFAULT ((0)),
    [WAGE_ITEM49] float NULL CONSTRAINT [DF_WAGE_ITEM49_1506104406] DEFAULT ((0)),
    [WAGE_ITEM50] float NULL CONSTRAINT [DF_WAGE_ITEM50_1506104406] DEFAULT ((0)),
    [WAGE_ITEM51] float NULL CONSTRAINT [DF_WAGE_ITEM51_1506104406] DEFAULT ((0)),
    [WAGE_ITEM52] float NULL CONSTRAINT [DF_WAGE_ITEM52_1506104406] DEFAULT ((0)),
    [WAGE_ITEM53] float NULL CONSTRAINT [DF_WAGE_ITEM53_1506104406] DEFAULT ((0)),
    [WAGE_ITEM54] float NULL CONSTRAINT [DF_WAGE_ITEM54_1506104406] DEFAULT ((0)),
    [WAGE_ITEM55] float NULL CONSTRAINT [DF_WAGE_ITEM55_1506104406] DEFAULT ((0)),
    [WAGE_ITEM56] float NULL CONSTRAINT [DF_WAGE_ITEM56_1506104406] DEFAULT ((0)),
    [WAGE_ITEM57] float NULL CONSTRAINT [DF_WAGE_ITEM57_1506104406] DEFAULT ((0)),
    [WAGE_ITEM58] float NULL CONSTRAINT [DF_WAGE_ITEM58_1506104406] DEFAULT ((0)),
    [WAGE_ITEM59] float NULL CONSTRAINT [DF_WAGE_ITEM59_1506104406] DEFAULT ((0)),
    [WAGE_ITEM60] float NULL CONSTRAINT [DF_WAGE_ITEM60_1506104406] DEFAULT ((0)),
    [WAGE_ITEM61] float NULL CONSTRAINT [DF_WAGE_ITEM61_1506104406] DEFAULT ((0)),
    [WAGE_ITEM62] float NULL CONSTRAINT [DF_WAGE_ITEM62_1506104406] DEFAULT ((0)),
    [WAGE_ITEM63] float NULL CONSTRAINT [DF_WAGE_ITEM63_1506104406] DEFAULT ((0)),
    [WAGE_ITEM64] float NULL CONSTRAINT [DF_WAGE_ITEM64_1506104406] DEFAULT ((0)),
    [WAGE_ITEM65] float NULL CONSTRAINT [DF_WAGE_ITEM65_1506104406] DEFAULT ((0)),
    [WAGE_ITEM66] float NULL CONSTRAINT [DF_WAGE_ITEM66_1506104406] DEFAULT ((0)),
    [WAGE_ITEM67] float NULL CONSTRAINT [DF_WAGE_ITEM67_1506104406] DEFAULT ((0)),
    [WAGE_ITEM68] float NULL CONSTRAINT [DF_WAGE_ITEM68_1506104406] DEFAULT ((0)),
    [WAGE_ITEM69] float NULL CONSTRAINT [DF_WAGE_ITEM69_1506104406] DEFAULT ((0)),
    [WAGE_ITEM70] float NULL CONSTRAINT [DF_WAGE_ITEM70_1506104406] DEFAULT ((0)),
    [WAGE_ITEM71] float NULL CONSTRAINT [DF_WAGE_ITEM71_1506104406] DEFAULT ((0)),
    [WAGE_ITEM72] float NULL CONSTRAINT [DF_WAGE_ITEM72_1506104406] DEFAULT ((0)),
    [WAGE_ITEM73] float NULL CONSTRAINT [DF_WAGE_ITEM73_1506104406] DEFAULT ((0)),
    [WAGE_ITEM74] float NULL CONSTRAINT [DF_WAGE_ITEM74_1506104406] DEFAULT ((0)),
    [WAGE_ITEM75] float NULL CONSTRAINT [DF_WAGE_ITEM75_1506104406] DEFAULT ((0)),
    [WAGE_ITEM76] float NULL CONSTRAINT [DF_WAGE_ITEM76_1506104406] DEFAULT ((0)),
    [WAGE_ITEM77] float NULL CONSTRAINT [DF_WAGE_ITEM77_1506104406] DEFAULT ((0)),
    [WAGE_ITEM78] float NULL CONSTRAINT [DF_WAGE_ITEM78_1506104406] DEFAULT ((0)),
    [WAGE_ITEM79] float NULL CONSTRAINT [DF_WAGE_ITEM79_1506104406] DEFAULT ((0)),
    [WAGE_ITEM80] float NULL CONSTRAINT [DF_WAGE_ITEM80_1506104406] DEFAULT ((0)),
    [WAGE_ITEM81] float NULL CONSTRAINT [DF_WAGE_ITEM81_1506104406] DEFAULT ((0)),
    [WAGE_ITEM82] float NULL CONSTRAINT [DF_WAGE_ITEM82_1506104406] DEFAULT ((0)),
    [WAGE_ITEM83] float NULL CONSTRAINT [DF_WAGE_ITEM83_1506104406] DEFAULT ((0)),
    [WAGE_ITEM84] float NULL CONSTRAINT [DF_WAGE_ITEM84_1506104406] DEFAULT ((0)),
    [WAGE_ITEM85] float NULL CONSTRAINT [DF_WAGE_ITEM85_1506104406] DEFAULT ((0)),
    [WAGE_ITEM86] float NULL CONSTRAINT [DF_WAGE_ITEM86_1506104406] DEFAULT ((0)),
    [WAGE_ITEM87] float NULL CONSTRAINT [DF_WAGE_ITEM87_1506104406] DEFAULT ((0)),
    [WAGE_ITEM88] float NULL CONSTRAINT [DF_WAGE_ITEM88_1506104406] DEFAULT ((0)),
    [WAGE_ITEM89] float NULL CONSTRAINT [DF_WAGE_ITEM89_1506104406] DEFAULT ((0)),
    [WAGE_ITEM90] float NULL CONSTRAINT [DF_WAGE_ITEM90_1506104406] DEFAULT ((0)),
    [WAGE_ITEM91] float NULL CONSTRAINT [DF_WAGE_ITEM91_1506104406] DEFAULT ((0)),
    [WAGE_ITEM92] float NULL CONSTRAINT [DF_WAGE_ITEM92_1506104406] DEFAULT ((0)),
    [WAGE_ITEM93] float NULL CONSTRAINT [DF_WAGE_ITEM93_1506104406] DEFAULT ((0)),
    [WAGE_ITEM94] float NULL CONSTRAINT [DF_WAGE_ITEM94_1506104406] DEFAULT ((0)),
    [WAGE_ITEM95] float NULL CONSTRAINT [DF_WAGE_ITEM95_1506104406] DEFAULT ((0)),
    [WAGE_ITEM96] float NULL CONSTRAINT [DF_WAGE_ITEM96_1506104406] DEFAULT ((0)),
    [WAGE_ITEM97] float NULL CONSTRAINT [DF_WAGE_ITEM97_1506104406] DEFAULT ((0)),
    [WAGE_ITEM98] float NULL CONSTRAINT [DF_WAGE_ITEM98_1506104406] DEFAULT ((0)),
    [WAGE_ITEM99] float NULL CONSTRAINT [DF_WAGE_ITEM99_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA0] float NULL CONSTRAINT [DF_WAGE_ITEMA0_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA1] float NULL CONSTRAINT [DF_WAGE_ITEMA1_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA2] float NULL CONSTRAINT [DF_WAGE_ITEMA2_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA3] float NULL CONSTRAINT [DF_WAGE_ITEMA3_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA4] float NULL CONSTRAINT [DF_WAGE_ITEMA4_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA5] float NULL CONSTRAINT [DF_WAGE_ITEMA5_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA6] float NULL CONSTRAINT [DF_WAGE_ITEMA6_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA7] float NULL CONSTRAINT [DF_WAGE_ITEMA7_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA8] float NULL CONSTRAINT [DF_WAGE_ITEMA8_1506104406] DEFAULT ((0)),
    [WAGE_ITEMA9] float NULL CONSTRAINT [DF_WAGE_ITEMA9_1506104406] DEFAULT ((0)),
    CONSTRAINT [PK_HRM_WAGE_D] PRIMARY KEY CLUSTERED ([WAGE_TYPE], [WAGE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_WAGE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_WAGE_M] (
    [WAGE_TYPE] nchar(10) NOT NULL,
    [WAGE_NO] nchar(20) NOT NULL,
    [WAGE_DATE] datetime NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [DEPT_ID] nchar(10) NULL,
    [IF_SECRECY] bit NULL,
    [IF_DIMISSION] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1522104463] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_WAGE_M] PRIMARY KEY CLUSTERED ([WAGE_TYPE], [WAGE_NO])
);
GO

------------------------------------------------------------------------------
-- HRM_WAGESYS
------------------------------------------------------------------------------
CREATE TABLE dbo.[HRM_WAGESYS] (
    [WAGE_ID] nchar(20) NOT NULL,
    [WAGE_NAME] nvarchar(50) NULL,
    [SOURCE_FIELD] nvarchar(50) NULL,
    [SOURCE_SQL] varchar(4000) NULL,
    [SOURCE_EXP] varchar(2000) NULL,
    [DISPLAY_FORMAT] nvarchar(50) NULL,
    [SQL_REMARK] nvarchar(2000) NULL,
    [WAGE_FIELD] nvarchar(50) NULL,
    [CALC_ORDER] smallint NULL,
    [IS_USED] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_HRM_WAGESYS] PRIMARY KEY CLUSTERED ([WAGE_ID])
);
GO

------------------------------------------------------------------------------
-- INV_BATCH_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_BATCH_D] (
    [BATCH_NO] nchar(30) NOT NULL,
    [PRO_NO] nchar(30) NOT NULL,
    [BATCH_DATE] datetime NOT NULL,
    [BATCH_ORDER_TYPE] char(4) NOT NULL,
    [BATCH_ORDER_NO] varchar(20) NOT NULL,
    [BATCH_SERIAL_NO] smallint NOT NULL,
    [DEPOT_ID] nchar(10) NOT NULL,
    [EFFECT_DEPOT] char(1) NOT NULL,
    [QTY] float NULL,
    [MUTUALITY_QTY] float NULL,
    [MUTUALITY_UNIT_ID] nchar(10) NULL,
    [MUTUALITY_PRICE] float NULL,
    [MUTUALITY_CURR_ID] nchar(10) NULL,
    [MUTUALITY_CURR_RATE] float NULL,
    [MUTUALITY_AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_INV_BATCH_D] PRIMARY KEY CLUSTERED ([BATCH_NO], [PRO_NO], [BATCH_DATE], [BATCH_ORDER_TYPE], [BATCH_ORDER_NO], [BATCH_SERIAL_NO], [DEPOT_ID], [EFFECT_DEPOT])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_BATCH_D] ON dbo.[INV_BATCH_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_BATCH_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_BATCH_M] (
    [BATCH_NO] nchar(30) NOT NULL,
    [PRO_NO] nchar(30) NOT NULL,
    [BATCH_DATE] datetime NULL,
    [EFFECT_DATE] datetime NULL,
    [IN_SUM] float NULL CONSTRAINT [DF_IN_SUM_750625717] DEFAULT ((0)),
    [OUT_SUM] float NULL CONSTRAINT [DF_OUT_SUM_750625717] DEFAULT ((0)),
    [LATELY_IN_DATE] datetime NULL,
    [LATELY_OUT_DATE] datetime NULL,
    [LATELY_CHECK_DATE] datetime NULL,
    [AGAIN_CHECK_DATE] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_750625717] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_750625717] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_BATCH_M] PRIMARY KEY CLUSTERED ([BATCH_NO], [PRO_NO])
);
GO
CREATE NONCLUSTERED INDEX [IDX_INVMC_1] ON dbo.[INV_BATCH_M] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_CHECK_STOCK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_CHECK_STOCK_D] (
    [CHECK_STOCK_TYPE] nchar(10) NOT NULL,
    [CHECK_STOCK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] varchar(30) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [ACCOUNT_QTY] float NULL,
    [CHECK_QTY] float NULL,
    CONSTRAINT [PK_INV_CHECK_STOCK_D] PRIMARY KEY CLUSTERED ([CHECK_STOCK_TYPE], [CHECK_STOCK_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_CHECK_STOCK_D] ON dbo.[INV_CHECK_STOCK_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_CHECK_STOCK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_CHECK_STOCK_M] (
    [CHECK_STOCK_TYPE] nchar(10) NOT NULL,
    [CHECK_STOCK_NO] nchar(20) NOT NULL,
    [CHECK_DATE] datetime NULL,
    [SORT_ID] nchar(10) NULL CONSTRAINT [DF_SORT_ID_782625831] DEFAULT (''),
    [DEPOT_ID] nchar(10) NULL CONSTRAINT [DF_DEPOT_ID_782625831] DEFAULT (''),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_782625831] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_782625831] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [COUNT_DATE] datetime NULL,
    [ADJUST_TYPE] nchar(10) NULL,
    [ADJUST_NO] char(20) NULL,
    CONSTRAINT [PK_INV_CHECK_STOCK_M] PRIMARY KEY CLUSTERED ([CHECK_STOCK_TYPE], [CHECK_STOCK_NO])
);
GO

------------------------------------------------------------------------------
-- INV_DEPOT_LOG
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_DEPOT_LOG] (
    [PRO_NO] nchar(30) NOT NULL,
    [MUTUALITY_DATE] datetime NOT NULL,
    [IN_OUT] char(1) NOT NULL,
    [MUTUALITY_TYPE] nchar(10) NOT NULL,
    [MUTUALITY_NO] nchar(20) NOT NULL,
    [MUTUALITY_SERIAL_NO] smallint NOT NULL,
    [DEPOT_ID] nchar(10) NOT NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_798625888] DEFAULT ((0)),
    [PRICE] float NULL CONSTRAINT [DF_PRICE_798625888] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_798625888] DEFAULT ((0)),
    [BATCH_NO] nchar(10) NULL,
    [MUTUALITY_QTY] float NULL CONSTRAINT [DF_MUTUALITY_QTY_798625888] DEFAULT ((0)),
    [MUTUALITY_UNIT_ID] nchar(10) NULL CONSTRAINT [DF_MUTUALITY_UNIT_ID_798625888] DEFAULT (''),
    [MUTUALITY_PRICE] float NULL CONSTRAINT [DF_MUTUALITY_PRICE_798625888] DEFAULT ((0)),
    [MUTUALITY_CURR_ID] nchar(10) NULL CONSTRAINT [DF_MUTUALITY_CURR_ID_798625888] DEFAULT (''),
    [MUTUALITY_CURR_RATE] float NULL CONSTRAINT [DF_MUTUALITY_CURR_RATE_798625888] DEFAULT ((0)),
    [MUTUALITY_AMOUNT] float NULL CONSTRAINT [DF_MUTUALITY_AMOUNT_798625888] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_INV_DEPOT_LOG] PRIMARY KEY CLUSTERED ([PRO_NO], [MUTUALITY_DATE], [IN_OUT], [MUTUALITY_TYPE], [MUTUALITY_NO], [MUTUALITY_SERIAL_NO], [DEPOT_ID])
);
GO
CREATE NONCLUSTERED INDEX [IDX_INVSD_1] ON dbo.[INV_DEPOT_LOG] ([PRO_NO], [BATCH_NO]);
GO

------------------------------------------------------------------------------
-- INV_LOAN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_LOAN_D] (
    [LOAN_TYPE] nchar(10) NOT NULL,
    [LOAN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [IN_DEPOT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [RETURN_QTY] float NULL,
    [RETURN_TYPE] nchar(10) NULL,
    [RETURN_NO] nchar(20) NULL,
    [RETURN_SERIAL_NO] smallint NULL,
    [REMARK] nvarchar(500) NULL,
    [DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_INV_LOAN_D] PRIMARY KEY CLUSTERED ([LOAN_TYPE], [LOAN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_LOAN_D_1] ON dbo.[INV_LOAN_D] ([RETURN_TYPE], [RETURN_NO], [RETURN_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_INV_LOAN_D] ON dbo.[INV_LOAN_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_LOAN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_LOAN_M] (
    [LOAN_TYPE] nchar(10) NOT NULL,
    [LOAN_NO] nchar(20) NOT NULL,
    [LOAN_DATE] datetime NOT NULL,
    [DEPT_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_830626002] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_LOAN_M] PRIMARY KEY CLUSTERED ([LOAN_TYPE], [LOAN_NO])
);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_ADJUST_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_ADJUST_D] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(500) NULL,
    [DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_ADJUST_D] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_OCCUR_ADJUST_D] ON dbo.[INV_OCCUR_ADJUST_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_ADJUST_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_ADJUST_M] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [OCCUR_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL CONSTRAINT [DF_DEPT_ID_862626116] DEFAULT (''),
    [EMP_ID] nchar(10) NULL CONSTRAINT [DF_EMP_ID_862626116] DEFAULT (''),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_862626116] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_ADJUST_M] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_OCCUR_ADJUST_M] ON dbo.[INV_OCCUR_ADJUST_M] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_IN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_IN_D] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_910626287] DEFAULT ((0)),
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(500) NULL,
    [DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_IN_D] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_OCCUR_IN_D] ON dbo.[INV_OCCUR_IN_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_IN_M] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [OCCUR_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL CONSTRAINT [DF_DEPT_ID_926626344] DEFAULT (''),
    [EMP_ID] nchar(10) NULL CONSTRAINT [DF_EMP_ID_926626344] DEFAULT (''),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NOT NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_926626344] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_IN_M] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_OCCUR_IN_M] ON dbo.[INV_OCCUR_IN_M] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_INIT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_INIT_D] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(500) NULL,
    [DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_INIT_D] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_OCCUR_INIT_D] ON dbo.[INV_OCCUR_INIT_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_INIT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_INIT_M] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [OCCUR_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL CONSTRAINT [DF_DEPT_ID_894626230] DEFAULT (''),
    [EMP_ID] nchar(10) NULL CONSTRAINT [DF_EMP_ID_894626230] DEFAULT (''),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_894626230] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_INIT_M] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO])
);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_OUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_OUT_D] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(500) NULL,
    [DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_OUT_D] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_OCCUR_OUT_D] ON dbo.[INV_OCCUR_OUT_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_OUT_M] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [OCCUR_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL CONSTRAINT [DF_DEPT_ID_958626458] DEFAULT (''),
    [EMP_ID] nchar(10) NULL CONSTRAINT [DF_EMP_ID_958626458] DEFAULT (''),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_958626458] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_OUT_M] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO])
);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_SCRAP_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_SCRAP_D] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [IN_DEPOT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(500) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [SPARE_QTY] float NULL,
    [DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_SCRAP_D] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_OCCUR_SCRAP_D] ON dbo.[INV_OCCUR_SCRAP_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_SCRAP_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_SCRAP_M] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [OCCUR_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL CONSTRAINT [DF_DEPT_ID_990626572] DEFAULT (''),
    [EMP_ID] nchar(10) NULL CONSTRAINT [DF_EMP_ID_990626572] DEFAULT (''),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_990626572] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_SCRAP_M] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO])
);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_TRANSFER_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_TRANSFER_D] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [IN_DEPOT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(500) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    CONSTRAINT [PK_INV_OCCUR_TRANSFER_D] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_OCCUR_TRANSFER_D] ON dbo.[INV_OCCUR_TRANSFER_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- INV_OCCUR_TRANSFER_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_OCCUR_TRANSFER_M] (
    [OCCUR_TYPE] nchar(10) NOT NULL,
    [OCCUR_NO] nchar(20) NOT NULL,
    [OCCUR_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1022626686] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_OCCUR_TRANSFER_M] PRIMARY KEY CLUSTERED ([OCCUR_TYPE], [OCCUR_NO])
);
GO

------------------------------------------------------------------------------
-- INV_PRO_DEPOT
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_PRO_DEPOT] (
    [PRO_NO] nchar(30) NOT NULL,
    [DEPOT_ID] nchar(10) NOT NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1038626743] DEFAULT ((0)),
    [INIT_QTY] float NULL CONSTRAINT [DF_INIT_QTY_1038626743] DEFAULT ((0)),
    [LAST_CHECK_DATE] datetime NULL,
    [USEABLE_QTY] float NULL CONSTRAINT [DF_USEABLE_QTY_1038626743] DEFAULT ((0)),
    [COST_PRICE] float NULL CONSTRAINT [DF_COST_PRICE_1038626743] DEFAULT ((0)),
    [COST_AMOUNT] float NULL CONSTRAINT [DF_COST_AMOUNT_1038626743] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1038626743] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_PRO_DEPOT] PRIMARY KEY CLUSTERED ([PRO_NO], [DEPOT_ID])
);
GO

------------------------------------------------------------------------------
-- INV_PRO_MONTH_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_PRO_MONTH_D] (
    [PRO_NO] nchar(30) NOT NULL,
    [DEPOT_ID] nchar(10) NOT NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1054626800] DEFAULT ((0)),
    [MONTH_TYPE] nchar(10) NOT NULL,
    [MONTH_NO] nchar(20) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_1054626800] DEFAULT ((0)),
    CONSTRAINT [PK_INV_PRO_MONTH_D] PRIMARY KEY CLUSTERED ([MONTH_TYPE], [MONTH_NO], [SERIAL_NO]),
    CONSTRAINT [IX_INV_PRO_MONTH_D] UNIQUE ([MONTH_TYPE], [MONTH_NO], [DEPOT_ID], [PRO_NO])
);
GO

------------------------------------------------------------------------------
-- INV_PRO_MONTH_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_PRO_MONTH_M] (
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1070626857] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [MONTH_TYPE] nchar(10) NOT NULL,
    [MONTH_NO] nchar(20) NOT NULL,
    [MONTH_DATE] datetime NULL,
    CONSTRAINT [PK_INV_PRO_MONTH_M] PRIMARY KEY CLUSTERED ([MONTH_TYPE], [MONTH_NO])
);
GO

------------------------------------------------------------------------------
-- INV_RETURN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_RETURN_D] (
    [RETURN_TYPE] nchar(10) NOT NULL,
    [RETURN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [OUT_DEPOT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [LOAN_TYPE] nchar(10) NULL,
    [LOAN_NO] nchar(20) NULL,
    [LOAN_SERIAL_NO] smallint NULL,
    [REMARK] nvarchar(500) NULL,
    [DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_INV_RETURN_D] PRIMARY KEY CLUSTERED ([RETURN_TYPE], [RETURN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INV_RETURN_D] ON dbo.[INV_RETURN_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_INV_RETURN_D_1] ON dbo.[INV_RETURN_D] ([LOAN_TYPE], [LOAN_NO], [LOAN_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- INV_RETURN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INV_RETURN_M] (
    [RETURN_TYPE] nchar(10) NOT NULL,
    [RETURN_NO] nchar(20) NOT NULL,
    [RETURN_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1102626971] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_INV_RETURN_M] PRIMARY KEY CLUSTERED ([RETURN_TYPE], [RETURN_NO])
);
GO

------------------------------------------------------------------------------
-- INVOICE_IN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INVOICE_IN_D] (
    [INVOICE_IN_TYPE] nchar(10) NOT NULL,
    [INVOICE_IN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [DUE_TYPE] nchar(10) NULL,
    [DUE_NO] nchar(20) NULL,
    [INVOICE_NO] char(10) NULL,
    [INVOICE_SUM] char(10) NULL CONSTRAINT [DF_INVOICE_SUM_670625432] DEFAULT ((0)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_INVOICE_IN_D] PRIMARY KEY CLUSTERED ([INVOICE_IN_TYPE], [INVOICE_IN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INVOICE_IN_D] ON dbo.[INVOICE_IN_D] ([DUE_TYPE], [DUE_NO]);
GO

------------------------------------------------------------------------------
-- INVOICE_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INVOICE_IN_M] (
    [INVOICE_IN_TYPE] nchar(10) NOT NULL,
    [INVOICE_IN_NO] nchar(20) NOT NULL,
    [INVOICE_IN_DATE] datetime NOT NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [SUPPLIER_NAME] nvarchar(50) NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_686625489] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_686625489] DEFAULT ('O'),
    CONSTRAINT [PK_INVOICE_IN_M] PRIMARY KEY CLUSTERED ([INVOICE_IN_TYPE], [INVOICE_IN_NO], [INVOICE_IN_DATE])
);
GO

------------------------------------------------------------------------------
-- INVOICE_OUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[INVOICE_OUT_D] (
    [INVOICE_OUT_TYPE] nchar(10) NOT NULL,
    [INVOICE_OUT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ACCOUNT_TYPE] nchar(10) NULL,
    [ACCOUNT_NO] nchar(20) NULL,
    [INVOICE_NO] char(10) NULL,
    [INVOICE_SUM] char(10) NULL CONSTRAINT [DF_INVOICE_SUM_702625546] DEFAULT ((0)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_INVOICE_OUT_D] PRIMARY KEY CLUSTERED ([INVOICE_OUT_TYPE], [INVOICE_OUT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_INVOICE_OUT_D] ON dbo.[INVOICE_OUT_D] ([ACCOUNT_TYPE], [ACCOUNT_NO]);
GO

------------------------------------------------------------------------------
-- INVOICE_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[INVOICE_OUT_M] (
    [INVOICE_OUT_TYPE] nchar(10) NOT NULL,
    [INVOICE_OUT_NO] nchar(20) NOT NULL,
    [INVOICE_OUT_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(50) NULL,
    [SALES_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_718625603] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_718625603] DEFAULT ('O'),
    CONSTRAINT [PK_INVOICE_OUT_M] PRIMARY KEY CLUSTERED ([INVOICE_OUT_TYPE], [INVOICE_OUT_NO])
);
GO

------------------------------------------------------------------------------
-- LINE
------------------------------------------------------------------------------
CREATE TABLE dbo.[LINE] (
    [LINE_ID] nchar(10) NOT NULL,
    [LINE_NAME] nchar(50) NULL,
    [OUTPUT_PERSON] float NULL,
    [OUTPUT_MACHINE] float NULL,
    [EFF_PERSON] float NULL,
    [EFF_MECHINE] float NULL,
    [FEE_ASSIGN_NO] bit NULL,
    [COST_PERSON] float NULL,
    [COST_PRODUCE] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_LINE] PRIMARY KEY CLUSTERED ([LINE_ID])
);
GO

------------------------------------------------------------------------------
-- MOC_BACK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_BACK_D] (
    [BACK_TYPE] nchar(10) NOT NULL,
    [BACK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [GET_TYPE] nchar(10) NULL,
    [GET_NO] nchar(20) NULL,
    [GET_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1166627199] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1166627199] DEFAULT ((0)),
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(200) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [LOST_QTY] float NULL CONSTRAINT [DF_LOST_QTY_1166627199] DEFAULT ((0)),
    [RETURN_QTY] float NULL CONSTRAINT [DF_RETURN_QTY_1166627199] DEFAULT ((0)),
    [EMPLOYEE_ID] nvarchar(50) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    CONSTRAINT [PK_MOC_BACK_D] PRIMARY KEY CLUSTERED ([BACK_TYPE], [BACK_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_BACK_D] ON dbo.[MOC_BACK_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_BACK_D_1] ON dbo.[MOC_BACK_D] ([GET_TYPE], [GET_NO], [GET_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_BACK_D_3] ON dbo.[MOC_BACK_D] ([GET_TYPE], [GET_NO], [GET_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_BACK_D_2] ON dbo.[MOC_BACK_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- MOC_BACK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_BACK_M] (
    [BACK_TYPE] nchar(10) NOT NULL,
    [BACK_NO] nchar(20) NOT NULL,
    [BACK_DATE] datetime NULL,
    [LINE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1182627256] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CUS_STATE] char(1) NULL CONSTRAINT [DF_CUS_STATE_1182627256] DEFAULT ('1'),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1182627256] DEFAULT ((0)),
    [EMP_NAME] nchar(20) NULL,
    [BACK_MONTH] nchar(10) NULL,
    CONSTRAINT [PK_MOC_BACK_M] PRIMARY KEY CLUSTERED ([BACK_TYPE], [BACK_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_BOM_STRU_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_BOM_STRU_D] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [PRO_NO] nchar(30) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [ELEMENT_QTY] float NULL,
    [BASE_QTY] int NULL,
    [LOST_RATE] float NULL,
    [REMARK] nvarchar(300) NULL,
    CONSTRAINT [PK_MOC_BOM_STRU_D] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO], [PRO_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_BOM_STRU_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_BOM_STRU_M] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [PRO_NO] nchar(30) NOT NULL,
    [BATCH_QTY] int NULL,
    [EDITION] nchar(10) NULL,
    [ATTENTION] nvarchar(1000) NULL,
    [PLAN_PERSON] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1214627370] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOC_BOM_STRU_M] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO], [PRO_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_GET_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_GET_D] (
    [GET_TYPE] nchar(10) NOT NULL,
    [GET_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1230627427] DEFAULT ((0)),
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(200) NULL,
    [SEND_QTY] float NULL CONSTRAINT [DF_SEND_QTY_1230627427] DEFAULT ((0)),
    [RETURN_QTY] float NULL CONSTRAINT [DF_RETURN_QTY_1230627427] DEFAULT ((0)),
    [RETURNED_QTY] float NULL CONSTRAINT [DF_RETURNED_QTY_1230627427] DEFAULT ((0)),
    [LOST_QTY] float NULL CONSTRAINT [DF_LOST_QTY_1230627427] DEFAULT ((0)),
    [LOST_FACT_QTY] float NULL CONSTRAINT [DF_LOST_FACT_QTY_1230627427] DEFAULT ((0)),
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_MOC_GET_D] PRIMARY KEY CLUSTERED ([GET_TYPE], [GET_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_GET_D] ON dbo.[MOC_GET_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- MOC_GET_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_GET_M] (
    [GET_TYPE] nchar(10) NOT NULL,
    [GET_NO] nchar(20) NOT NULL,
    [GET_DATE] datetime NOT NULL,
    [LINE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1246627484] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [REWORK_TAG] bit NULL CONSTRAINT [DF_REWORK_TAG_1246627484] DEFAULT ((0)),
    [OUTSIDE_TAG] bit NULL CONSTRAINT [DF_OUTSIDE_TAG_1246627484] DEFAULT ((0)),
    [ORDER_NO] nvarchar(300) NULL,
    [PRODUCE_NO] nvarchar(300) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1246627484] DEFAULT ((0)),
    [CUS_STATE] char(1) NULL CONSTRAINT [DF_CUS_STATE_1246627484] DEFAULT ('1'),
    CONSTRAINT [PK_MOC_GET_M] PRIMARY KEY CLUSTERED ([GET_TYPE], [GET_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_GET_MORE
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_GET_MORE] (
    [GET_TYPE] nchar(10) NOT NULL,
    [GET_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1262627541] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(200) NULL,
    [REQUIRE_QTY] float NULL CONSTRAINT [DF_REQUIRE_QTY_1262627541] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_GET_MORE] PRIMARY KEY CLUSTERED ([GET_TYPE], [GET_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_GET_MORE] ON dbo.[MOC_GET_MORE] ([PRODUCE_TYPE], [PRODUCE_NO], [PRODUCE_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_GET_MORE_1] ON dbo.[MOC_GET_MORE] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- MOC_MRP_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_MRP_D] (
    [PRO_NO] nchar(30) NOT NULL,
    [BEGIN_DATE] smalldatetime NOT NULL,
    [END_DATE] smalldatetime NULL,
    [DEPOT_QTY] float NULL,
    [DEPOT_SAFE_QTY] float NULL,
    [NOT_SEND_QTY] float NULL,
    [NOT_GET_QTY] float NULL,
    [NOT_PRODUCE_IN_QTY] float NULL,
    [NOT_RECEIVE_QTY] float NULL,
    CONSTRAINT [PK_MOC_MRP_D] PRIMARY KEY CLUSTERED ([PRO_NO], [BEGIN_DATE])
);
GO

------------------------------------------------------------------------------
-- MOC_MRP_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_MRP_M] (
    [PRO_NO] nchar(30) NOT NULL,
    [LAST_DATE] smalldatetime NULL,
    [DEPOT_QTY] float NULL,
    [DEPOT_SAFE_QTY] float NULL,
    [NOT_SEND_QTY] float NULL,
    [NOT_GET_QTY] float NULL,
    [NOT_PRODUCE_IN_QTY] float NULL,
    [NOT_RECEIVE_QTY] float NULL,
    CONSTRAINT [PK_MOC_MRP_M] PRIMARY KEY CLUSTERED ([PRO_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_OUT_PRODUCT_IN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_OUT_PRODUCT_IN_D] (
    [OUT_PRODUCT_IN_TYPE] nchar(10) NOT NULL,
    [OUT_PRODUCT_IN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [PRICE_QTY] float NULL CONSTRAINT [DF_PRICE_QTY_1310627712] DEFAULT ((0)),
    [PRICE_UNIT] nchar(10) NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_1310627712] DEFAULT ((0)),
    [AMOUNT] float NULL,
    [ACCEPT_QTY] float NULL CONSTRAINT [DF_ACCEPT_QTY_1310627712] DEFAULT ((0)),
    [REJECT_QTY] float NULL CONSTRAINT [DF_REJECT_QTY_1310627712] DEFAULT ((0)),
    [CHECK_DATE] datetime NULL,
    [RECOUP_AMOUNT] float NULL CONSTRAINT [DF_RECOUP_AMOUNT_1310627712] DEFAULT ((0)),
    [RECOUP_REMARK] nvarchar(300) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1310627712] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_OUT_PRODUCT_IN_D] PRIMARY KEY CLUSTERED ([OUT_PRODUCT_IN_TYPE], [OUT_PRODUCT_IN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_OUT_PRODUCT_IN_D] ON dbo.[MOC_OUT_PRODUCT_IN_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_OUT_PRODUCT_IN_D_1] ON dbo.[MOC_OUT_PRODUCT_IN_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO

------------------------------------------------------------------------------
-- MOC_OUT_PRODUCT_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_OUT_PRODUCT_IN_M] (
    [OUT_PRODUCT_IN_TYPE] nchar(10) NOT NULL,
    [OUT_PRODUCT_IN_NO] nchar(20) NOT NULL,
    [OUT_PRODUCT_IN_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1326627769] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1326627769] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOC_OUT_PRODUCT_IN_M] PRIMARY KEY CLUSTERED ([OUT_PRODUCT_IN_TYPE], [OUT_PRODUCT_IN_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_OUT_PRODUCT_OUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_OUT_PRODUCT_OUT_D] (
    [OUT_PRODUCT_OUT_TYPE] nchar(10) NOT NULL,
    [OUT_PRODUCT_OUT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [OUT_PRODUCT_IN_TYPE] nchar(10) NULL,
    [OUT_PRODUCT_IN_NO] nchar(20) NULL,
    [OUT_PROCUCT_IN_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1342627826] DEFAULT ((0)),
    [PRICE_QTY] float NULL CONSTRAINT [DF_PRICE_QTY_1342627826] DEFAULT ((0)),
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [DEPOT_ID] nchar(10) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(50) NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1342627826] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_OUT_PRODUCT_OUT_D] PRIMARY KEY CLUSTERED ([OUT_PRODUCT_OUT_TYPE], [OUT_PRODUCT_OUT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_OUT_PRODUCT_OUT_D_1] ON dbo.[MOC_OUT_PRODUCT_OUT_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_OUT_PRODUCT_OUT_D] ON dbo.[MOC_OUT_PRODUCT_OUT_D] ([OUT_PRODUCT_IN_TYPE], [OUT_PRODUCT_IN_NO], [OUT_PROCUCT_IN_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- MOC_OUT_PRODUCT_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_OUT_PRODUCT_OUT_M] (
    [OUT_PRODUCT_OUT_TYPE] nchar(10) NOT NULL,
    [OUT_PRODUCT_OUT_NO] nchar(20) NOT NULL,
    [OUT_PRODUCT_OUT_DATE] datetime NOT NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1358627883] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1358627883] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOC_OUT_PRODUCT_OUT_M] PRIMARY KEY CLUSTERED ([OUT_PRODUCT_OUT_TYPE], [OUT_PRODUCT_OUT_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_PLAN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PLAN_D] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRE_SEND_DATE] datetime NULL,
    [NOT_PLAN_QTY] float NULL CONSTRAINT [DF_NOT_PLAN_QTY_1374627940] DEFAULT ((0)),
    [NOT_PLAN_SPARE_QTY] float NULL CONSTRAINT [DF_NOT_PLAN_SPARE_QTY_1374627940] DEFAULT ((0)),
    [QTY] float NULL,
    [SPARE_QTY] float NULL,
    [TRAN_PLAN_QTY] float NULL,
    [TRAN_PLAN_SPARE_QTY] float NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1374627940] DEFAULT ((0)),
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    [ORDER_QTY] float NULL,
    [ORDER_SPARE_QTY] float NULL,
    CONSTRAINT [PK_MOC_PLAN_D] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_PLAN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PLAN_M] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [PLAN_DATE] smalldatetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1390627997] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1390627997] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [PRO_NO] nchar(30) NULL,
    CONSTRAINT [PK_MOC_PLAN_M] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_PLAN_MOC
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PLAN_MOC] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [BEGIN_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1406628054] DEFAULT ((0)),
    [MRP_QTY] float NULL CONSTRAINT [DF_MRP_QTY_1406628054] DEFAULT ((0)),
    [DEPOT_QTY] float NULL CONSTRAINT [DF_DEPOT_QTY_1406628054] DEFAULT ((0)),
    [SAFETY_QTY] float NULL CONSTRAINT [DF_SAFETY_QTY_1406628054] DEFAULT ((0)),
    [IN_BUY_QTY] float NULL CONSTRAINT [DF_IN_BUY_QTY_1406628054] DEFAULT ((0)),
    [NOT_IN_QTY] float NULL CONSTRAINT [DF_NOT_IN_QTY_1406628054] DEFAULT ((0)),
    [NOT_SEND_QTY] float NULL CONSTRAINT [DF_NOT_SEND_QTY_1406628054] DEFAULT ((0)),
    [NOT_GET_QTY] float NULL CONSTRAINT [DF_NOT_GET_QTY_1406628054] DEFAULT ((0)),
    [NET_QTY] float NULL,
    [REQUIRE_QTY] float NULL CONSTRAINT [DF_REQUIRE_QTY_1406628054] DEFAULT ((0)),
    [PRODUCE_QTY] float NULL CONSTRAINT [DF_PRODUCE_QTY_1406628054] DEFAULT ((0)),
    [APPLY_QTY] float NULL CONSTRAINT [DF_APPLY_QTY_1406628054] DEFAULT ((0)),
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    [PRE_SEND_DATE] datetime NULL,
    CONSTRAINT [PK_MOC_PLAN_MOC] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_PLAN_PUR
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PLAN_PUR] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [BEGIN_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1422628111] DEFAULT ((0)),
    [MRP_QTY] float NULL CONSTRAINT [DF_MRP_QTY_1422628111] DEFAULT ((0)),
    [DEPOT_QTY] float NULL CONSTRAINT [DF_DEPOT_QTY_1422628111] DEFAULT ((0)),
    [SAFETY_QTY] float NULL CONSTRAINT [DF_SAFETY_QTY_1422628111] DEFAULT ((0)),
    [IN_BUY_QTY] float NULL CONSTRAINT [DF_IN_BUY_QTY_1422628111] DEFAULT ((0)),
    [NOT_IN_QTY] float NULL CONSTRAINT [DF_NOT_IN_QTY_1422628111] DEFAULT ((0)),
    [NOT_SEND_QTY] float NULL CONSTRAINT [DF_NOT_SEND_QTY_1422628111] DEFAULT ((0)),
    [NOT_GET_QTY] float NULL CONSTRAINT [DF_NOT_GET_QTY_1422628111] DEFAULT ((0)),
    [NET_QTY] float NULL,
    [REQUIRE_QTY] float NULL CONSTRAINT [DF_REQUIRE_QTY_1422628111] DEFAULT ((0)),
    [PRODUCE_QTY] float NULL CONSTRAINT [DF_PRODUCE_QTY_1422628111] DEFAULT ((0)),
    [APPLY_QTY] float NULL CONSTRAINT [DF_APPLY_QTY_1422628111] DEFAULT ((0)),
    [APPLY_TYPE] nchar(10) NULL,
    [APPLY_NO] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_MOC_PLAN_PUR] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCE_CHANGE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCE_CHANGE_D] (
    [CHANGE_PRODUCE_TYPE] nchar(10) NOT NULL,
    [CHANGE_PRODUCE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [NEED_QTY] float NULL CONSTRAINT [DF_NEED_QTY_1438628168] DEFAULT ((0)),
    [OLD_NEED_QTY] float NULL CONSTRAINT [DF_OLD_NEED_QTY_1438628168] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [USED_QTY] float NULL CONSTRAINT [DF_USED_QTY_1438628168] DEFAULT ((0)),
    [APPLY_QTY] float NULL CONSTRAINT [DF_APPLY_QTY_1438628168] DEFAULT ((0)),
    [OLD_USED_QTY] float NULL CONSTRAINT [DF_OLD_USED_QTY_1438628168] DEFAULT ((0)),
    [OLD_APPLY_QTY] float NULL CONSTRAINT [DF_OLD_APPLY_QTY_1438628168] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_PRODUCE_CHANGE_D] PRIMARY KEY CLUSTERED ([CHANGE_PRODUCE_TYPE], [CHANGE_PRODUCE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_CHANGE_PRODUCE_D] ON dbo.[MOC_PRODUCE_CHANGE_D] ([PRODUCE_TYPE], [PRODUCE_NO], [PRODUCE_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCE_CHANGE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCE_CHANGE_M] (
    [CHANGE_PRODUCE_TYPE] nchar(10) NOT NULL,
    [CHANGE_PRODUCE_NO] nchar(20) NOT NULL,
    [CHANGE_PRODUCE_DATE] datetime NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1454628225] DEFAULT ((0)),
    [SPARE_QTY] float NULL CONSTRAINT [DF_SPARE_QTY_1454628225] DEFAULT ((0)),
    [PRE_SEND_DATE] datetime NULL,
    [OLD_QTY] float NULL CONSTRAINT [DF_OLD_QTY_1454628225] DEFAULT ((0)),
    [OLD_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_SPARE_QTY_1454628225] DEFAULT ((0)),
    [OLD_PRE_SEND_DATE] datetime NULL,
    [PLAN_START] datetime NULL,
    [PLAN_END] datetime NULL,
    [START_TAG] bit NULL CONSTRAINT [DF_START_TAG_1454628225] DEFAULT ((0)),
    [END_TAG] bit NULL CONSTRAINT [DF_END_TAG_1454628225] DEFAULT ((0)),
    [ATTENTION] nvarchar(1000) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1454628225] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1454628225] DEFAULT ((0)),
    [FINISHED_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_SPARE_QTY_1454628225] DEFAULT ((0)),
    [FINISHED_SEND_QTY] float NULL CONSTRAINT [DF_FINISHED_SEND_QTY_1454628225] DEFAULT ((0)),
    [FINISHED_SEND_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_SEND_SPARE_QTY_1454628225] DEFAULT ((0)),
    [FINISHED_FITOUT_QTY] float NULL CONSTRAINT [DF_FINISHED_FITOUT_QTY_1454628225] DEFAULT ((0)),
    [FINISHED_FITOUT_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_FITOUT_SPARE_QTY_1454628225] DEFAULT ((0)),
    [OLD_FINISHED_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_QTY_1454628225] DEFAULT ((0)),
    [OLD_FINISHED_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_SPARE_QTY_1454628225] DEFAULT ((0)),
    [OLD_FINISHED_SEND_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_SEND_QTY_1454628225] DEFAULT ((0)),
    [OLD_FINISHED_SEND_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_SEND_SPARE_QTY_1454628225] DEFAULT ((0)),
    [OLD_FINISHED_FITOUT_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_FITOUT_QTY_1454628225] DEFAULT ((0)),
    [OLD_FINISHED_FITOUT_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_FINISHED_FITOUT_SPARE_QTY_1454628225] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_PRODUCE_CHANGE_M] PRIMARY KEY CLUSTERED ([CHANGE_PRODUCE_TYPE], [CHANGE_PRODUCE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_CHANGE_PRODUCE_M] ON dbo.[MOC_PRODUCE_CHANGE_M] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCE_D] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [PRO_NO] nchar(30) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [NEED_QTY] float NULL CONSTRAINT [DF_NEED_QTY_1470628282] DEFAULT ((0)),
    [USED_QTY] float NULL CONSTRAINT [DF_USED_QTY_1470628282] DEFAULT ((0)),
    [COMPONENT_QTY] float NULL CONSTRAINT [DF_COMPONENT_QTY_1470628282] DEFAULT ((0)),
    [LOST_RATE] float NULL CONSTRAINT [DF_LOST_RATE_1470628282] DEFAULT ((0)),
    [DEPOT_ID] nchar(10) NULL,
    [PURCHASE_QTY] float NULL CONSTRAINT [DF_PURCHASE_QTY_1470628282] DEFAULT ((0)),
    [DEPOT_QTY] float NULL CONSTRAINT [DF_DEPOT_QTY_1470628282] DEFAULT ((0)),
    [RECEIVE_QTY] float NULL CONSTRAINT [DF_RECEIVE_QTY_1470628282] DEFAULT ((0)),
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1470628282] DEFAULT ((0)),
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PRO_QTY] float NULL CONSTRAINT [DF_PRO_QTY_1470628282] DEFAULT ((0)),
    [APPLY_QTY] float NULL CONSTRAINT [DF_APPLY_QTY_1470628282] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_PRODUCE_D] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO], [PRO_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCE_D] ON dbo.[MOC_PRODUCE_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCE_D_1] ON dbo.[MOC_PRODUCE_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCE_INSTRUCT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCE_INSTRUCT_D] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ITEM_NO] nvarchar(50) NULL,
    [REMARK_1] nvarchar(100) NULL,
    [REMARK_2] nvarchar(100) NULL,
    [REMARK_3] nvarchar(100) NULL,
    [REMARK_4] nvarchar(100) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1486628339] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_PRODUCE_INSTRUCT_D] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOCDC] ON dbo.[MOC_PRODUCE_INSTRUCT_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCE_INSTRUCT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCE_INSTRUCT_M] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [PRODUCE_DATE] datetime NULL,
    [PRO_NO] nchar(30) NOT NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [SERIAL_NO] smallint NULL,
    [EDITION] nchar(10) NULL,
    [ATTENTION] nvarchar(1000) NULL,
    [PLAN_PERSON] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1502628396] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOC_PRODUCE_INSTRUCT_M] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCE_INSTRUCT_M_1] ON dbo.[MOC_PRODUCE_INSTRUCT_M] ([ORDER_TYPE], [ORDER_NO], [SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCE_INSTRUCT_M] ON dbo.[MOC_PRODUCE_INSTRUCT_M] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCE_M] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [PRODUCE_DATE] datetime NULL,
    [PRO_NO] nchar(30) NULL,
    [USED_QTY] float NULL CONSTRAINT [DF_USED_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1518628453] DEFAULT ((0)),
    [PLAN_START] datetime NULL,
    [PLAN_END] datetime NULL,
    [PURCHASE_QTY] float NULL CONSTRAINT [DF_PURCHASE_QTY_1518628453] DEFAULT ((0)),
    [DEPOT_ID] nchar(10) NULL,
    [LINE_ID] nchar(10) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [UNIT_ID] nchar(10) NULL,
    [START_TAG] bit NULL CONSTRAINT [DF_START_TAG_1518628453] DEFAULT ((0)),
    [END_TAG] bit NULL CONSTRAINT [DF_END_TAG_1518628453] DEFAULT ((0)),
    [INFACT_START] datetime NULL,
    [INFACT_END] datetime NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1518628453] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1518628453] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [SPARE_QTY] float NULL CONSTRAINT [DF_SPARE_QTY_1518628453] DEFAULT ((0)),
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_SPARE_QTY_1518628453] DEFAULT ((0)),
    [PRE_SEND_DATE] datetime NULL,
    [ATTENTION] nvarchar(1000) NULL,
    [EDITION] nchar(10) NULL,
    [PARENT_TYPE] nchar(10) NULL CONSTRAINT [DF_PARENT_TYPE_1518628453] DEFAULT (''),
    [PARENT_NO] nchar(20) NULL CONSTRAINT [DF_PARENT_NO_1518628453] DEFAULT (''),
    [FINISHED_SEND_QTY] float NULL CONSTRAINT [DF_FINISHED_SEND_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_SEND_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_SEND_SPARE_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_FITOUT_QTY] float NULL CONSTRAINT [DF_FINISHED_FITOUT_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_FITOUT_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_FITOUT_SPARE_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_WORK_QTY] float NULL CONSTRAINT [DF_FINISHED_WORK_QTY_1518628453] DEFAULT ((0)),
    [PROCESS_PRICE] float NULL CONSTRAINT [DF_PROCESS_PRICE_1518628453] DEFAULT ((0)),
    [FINISHED_WORK_IN_QTY] float NULL CONSTRAINT [DF_FINISHED_WORK_IN_QTY_1518628453] DEFAULT ((0)),
    [REWORK_TAG] bit NULL CONSTRAINT [DF_REWORK_TAG_1518628453] DEFAULT ((0)),
    [OUTSIDE_TAG] bit NULL CONSTRAINT [DF_OUTSIDE_TAG_1518628453] DEFAULT ((0)),
    [SCRAP_QTY] float NULL CONSTRAINT [DF_SCRAP_QTY_1518628453] DEFAULT ((0)),
    [SCRAP_SPARE_QTY] float NULL CONSTRAINT [DF_SCRAP_SPARE_QTY_1518628453] DEFAULT ((0)),
    [SCRAP_IN_QTY] float NULL CONSTRAINT [DF_SCRAP_IN_QTY_1518628453] DEFAULT ((0)),
    [SCRAP_IN_SPARE_QTY] float NULL CONSTRAINT [DF_SCRAP_IN_SPARE_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_WORK_DATE] smalldatetime NULL,
    [FINISHED_WORK_IN_DATE] smalldatetime NULL CONSTRAINT [DF_FINISHED_WORK_IN_DATE_1518628453] DEFAULT ((0)),
    [PLAN_TYPE] nchar(10) NULL,
    [PLAN_NO] nchar(20) NULL,
    [PLAN_SERIAL_NO] smallint NULL,
    [WORK_QTY] float NULL CONSTRAINT [DF_WORK_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_IN_DATE] smalldatetime NULL,
    [FINISHED_FITOUT_DATE] smalldatetime NULL,
    [FINISHED_TRANSFER_QTY] float NULL CONSTRAINT [DF_FINISHED_TRANSFER_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_TRANSFER_SPARE_QTY] float NULL CONSTRAINT [DF_FINISHED_TRANSFER_SPARE_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_TRANSFER_DATE] smalldatetime NULL,
    [FINISHED_SEND_DATE] smalldatetime NULL,
    [FINISHED_SCRAP_IN_DATE] smalldatetime NULL,
    [FINISHED_WORK_OUT_QTY] float NULL CONSTRAINT [DF_FINISHED_WORK_OUT_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_WORK_OUT_DATE] smalldatetime NULL,
    [HUANCUN_DEPOT_ID] nchar(10) NULL CONSTRAINT [DF_HUANCUN_DEPOT_ID_1518628453] DEFAULT (N'HUANCUN'),
    [FINISHED_ANALYSIS_QTY] float NULL CONSTRAINT [DF_FINISHED_ANALYSIS_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_ANALYSIS_DATE] smalldatetime NULL,
    [DEPOT_PLACE] nvarchar(200) NULL,
    [FIRST_SEND_DATE] smalldatetime NULL,
    [FINISHED_PLAN_PROCESS_QTY] float NULL CONSTRAINT [DF_FINISHED_PLAN_PROCESS_QTY_1518628453] DEFAULT ((0)),
    [FINISHED_PLAN_PROCESS_DATE] smalldatetime NULL,
    CONSTRAINT [PK_MOC_PRODUCE_M] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCE_M_1] ON dbo.[MOC_PRODUCE_M] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCE_M] ON dbo.[MOC_PRODUCE_M] ([CLIENT_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCE_M_2] ON dbo.[MOC_PRODUCE_M] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCE_PROCESS_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCE_PROCESS_D] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [PROCESS_QTY] float NULL CONSTRAINT [DF_PROCESS_QTY_1534628510] DEFAULT ((0)),
    [PROCESS_DESC] nvarchar(500) NULL,
    [LINE_ID] nchar(10) NULL,
    [PLAN_START] datetime NULL,
    [PLAN_END] datetime NULL,
    [INFACT_START] datetime NULL,
    [INFACT_END] datetime NULL,
    [STANDARD_PERSON_TIME] float NULL CONSTRAINT [DF_STANDARD_PERSON_TIME_1534628510] DEFAULT ((0)),
    [STANDARD_MACHINE_TIME] float NULL CONSTRAINT [DF_STANDARD_MACHINE_TIME_1534628510] DEFAULT ((0)),
    [INFACT_PERSON_TIME] float NULL CONSTRAINT [DF_INFACT_PERSON_TIME_1534628510] DEFAULT ((0)),
    [INFACT_MACHINE_TIME] float NULL CONSTRAINT [DF_INFACT_MACHINE_TIME_1534628510] DEFAULT ((0)),
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_1534628510] DEFAULT ((0)),
    [RECOUP_AMOUNT] float NULL CONSTRAINT [DF_RECOUP_AMOUNT_1534628510] DEFAULT ((0)),
    [PROCEDURE_ID] nchar(10) NULL,
    [PROCESS_OVER_QTY] float NULL CONSTRAINT [DF_PROCESS_OVER_QTY_1534628510] DEFAULT ((0)),
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1534628510] DEFAULT ((0)),
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL CONSTRAINT [DF_PROCEDURE_TYPE_ID_1534628510] DEFAULT (''),
    [PRO_NAME] nvarchar(100) NULL CONSTRAINT [DF_PRO_NAME_1534628510] DEFAULT (''),
    [PRO_SPEC] nvarchar(100) NULL CONSTRAINT [DF_PRO_SPEC_1534628510] DEFAULT (''),
    [COLOR_ID] nchar(10) NULL CONSTRAINT [DF_COLOR_ID_1534628510] DEFAULT (''),
    [STUFF_ID] nchar(10) NULL CONSTRAINT [DF_STUFF_ID_1534628510] DEFAULT (''),
    [UNIT_ID] nchar(10) NULL CONSTRAINT [DF_UNIT_ID_1534628510] DEFAULT (''),
    [LENGTH] float NULL,
    [WIDTH] float NULL,
    [HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [LINE_NAME] nvarchar(50) NULL,
    [FINISHED_PLAN_QTY] float NULL CONSTRAINT [DF_FINISHED_PLAN_QTY_1534628510] DEFAULT ((0)),
    [FINISHED_IN_QTY] float NULL CONSTRAINT [DF_FINISHED_IN_QTY_1534628510] DEFAULT ((0)),
    [FINISHED_OUT_QTY] float NULL CONSTRAINT [DF_FINISHED_OUT_QTY_1534628510] DEFAULT ((0)),
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PRICE] float NULL,
    [HARD_RATE] float NULL,
    [PS_SPEC] nchar(10) NULL CONSTRAINT [DF_PS_SPEC_1534628510] DEFAULT (''),
    [PS_LENGTH] float NULL CONSTRAINT [DF_PS_LENGTH_1534628510] DEFAULT ((0)),
    [PS_WIDTH] float NULL CONSTRAINT [DF_PS_WIDTH_1534628510] DEFAULT ((0)),
    [OM_LENGTH] float NULL CONSTRAINT [DF_OM_LENGTH_1534628510] DEFAULT ((0)),
    [OM_WIDTH] float NULL CONSTRAINT [DF_OM_WIDTH_1534628510] DEFAULT ((0)),
    [BJ_LENGTH] smallint NULL CONSTRAINT [DF_BJ_LENGTH_1534628510] DEFAULT ((0)),
    [BJ_WIDTH] smallint NULL CONSTRAINT [DF_BJ_WIDTH_1534628510] DEFAULT ((0)),
    [SET_QTY] smallint NULL CONSTRAINT [DF_SET_QTY_1534628510] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_PRODUCE_PROCESS_D] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCE_PROCESS_D] ON dbo.[MOC_PRODUCE_PROCESS_D] ([PRO_NO], [PROCEDURE_TYPE_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCE_PROCESS_D_1] ON dbo.[MOC_PRODUCE_PROCESS_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCE_PROCESS_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCE_PROCESS_M] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1550628567] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1550628567] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_PRODUCE_PROCESS_M] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCT_IN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCT_IN_D] (
    [PRODUCT_IN_TYPE] nchar(10) NOT NULL,
    [PRODUCT_IN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1566628624] DEFAULT ((0)),
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [BATCH_NO] nchar(30) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_1566628624] DEFAULT ((0)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [SPARE_QTY] float NULL CONSTRAINT [DF_SPARE_QTY_1566628624] DEFAULT ((0)),
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ID] nchar(10) NULL,
    [DEPOT_PLACE] nvarchar(50) NULL,
    [OTHER_QTY] float NULL CONSTRAINT [DF_OTHER_QTY_1566628624] DEFAULT ((0)),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1566628624] DEFAULT ('O'),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_1566628624] DEFAULT ((100)),
    CONSTRAINT [PK_MOC_PRODUCT_IN_D] PRIMARY KEY CLUSTERED ([PRODUCT_IN_TYPE], [PRODUCT_IN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCT_IN_D] ON dbo.[MOC_PRODUCT_IN_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_PRODUCT_IN_D_1] ON dbo.[MOC_PRODUCT_IN_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCT_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCT_IN_M] (
    [PRODUCT_IN_TYPE] nchar(10) NOT NULL,
    [PRODUCT_IN_NO] nchar(20) NOT NULL,
    [PRODUCT_IN_DATE] datetime NULL,
    [LINE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1582628681] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CUS_STATE] char(1) NULL CONSTRAINT [DF_CUS_STATE_1582628681] DEFAULT ('1'),
    CONSTRAINT [PK_MOC_PRODUCT_IN_M] PRIMARY KEY CLUSTERED ([PRODUCT_IN_TYPE], [PRODUCT_IN_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCT_OUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCT_OUT_D] (
    [PRODUCT_OUT_TYPE] nchar(10) NOT NULL,
    [PRODUCT_OUT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_1598628738] DEFAULT ((0)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1598628738] DEFAULT ((0)),
    [SPARE_QTY] float NULL CONSTRAINT [DF_SPARE_QTY_1598628738] DEFAULT ((0)),
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [BATCH_NO] nchar(30) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_1598628738] DEFAULT ('O'),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_1598628738] DEFAULT ((100)),
    CONSTRAINT [PK_MOC_PRODUCT_OUT_D] PRIMARY KEY CLUSTERED ([PRODUCT_OUT_TYPE], [PRODUCT_OUT_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_PRODUCT_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_PRODUCT_OUT_M] (
    [PRODUCT_OUT_TYPE] nchar(10) NOT NULL,
    [PRODUCT_OUT_NO] nchar(20) NOT NULL,
    [PRODUCT_OUT_DATE] datetime NULL,
    [LINE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1614628795] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CUS_STATE] char(1) NULL CONSTRAINT [DF_CUS_STATE_1614628795] DEFAULT ('1'),
    CONSTRAINT [PK_MOC_PRODUCT_OUT_M] PRIMARY KEY CLUSTERED ([PRODUCT_OUT_TYPE], [PRODUCT_OUT_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_WORK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_WORK_D] (
    [WORK_TYPE] nchar(10) NOT NULL,
    [WORK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [PROCESS_QTY] float NULL CONSTRAINT [DF_PROCESS_QTY_1630628852] DEFAULT ((0)),
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [COLOR_ID] nchar(10) NULL,
    [STUFF_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [LENGTH] float NULL,
    [WIDTH] float NULL,
    [HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [PLAN_START] datetime NULL,
    [PLAN_END] datetime NULL,
    [INFACT_START] datetime NULL,
    [INFACT_END] datetime NULL,
    [FINISHED_IN_QTY] float NULL CONSTRAINT [DF_FINISHED_IN_QTY_1630628852] DEFAULT ((0)),
    [FINISHED_OUT_QTY] float NULL CONSTRAINT [DF_FINISHED_OUT_QTY_1630628852] DEFAULT ((0)),
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_1630628852] DEFAULT ((0)),
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1630628852] DEFAULT ((0)),
    [DEPOT_ID] nchar(10) NULL,
    [ULLAGE_QTY] float NULL CONSTRAINT [DF_ULLAGE_QTY_1630628852] DEFAULT ((0)),
    [LINE_ID] nchar(10) NULL,
    [LINE_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PS_SPEC] nchar(10) NULL CONSTRAINT [DF_PS_SPEC_1630628852] DEFAULT (''),
    [PS_LENGTH] float NULL CONSTRAINT [DF_PS_LENGTH_1630628852] DEFAULT ((0)),
    [PS_WIDTH] float NULL CONSTRAINT [DF_PS_WIDTH_1630628852] DEFAULT ((0)),
    [OM_LENGTH] float NULL CONSTRAINT [DF_OM_LENGTH_1630628852] DEFAULT ((0)),
    [OM_WIDTH] float NULL CONSTRAINT [DF_OM_WIDTH_1630628852] DEFAULT ((0)),
    [BJ_LENGTH] smallint NULL CONSTRAINT [DF_BJ_LENGTH_1630628852] DEFAULT ((0)),
    [BJ_WIDTH] smallint NULL CONSTRAINT [DF_BJ_WIDTH_1630628852] DEFAULT ((0)),
    [SET_QTY] smallint NULL CONSTRAINT [DF_SET_QTY_1630628852] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_WORK_D] PRIMARY KEY CLUSTERED ([WORK_TYPE], [WORK_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_D_2] ON dbo.[MOC_WORK_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_D_1] ON dbo.[MOC_WORK_D] ([PRO_NO], [PROCEDURE_TYPE_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_D] ON dbo.[MOC_WORK_D] ([PRODUCE_TYPE], [PRODUCE_NO], [PRODUCE_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- MOC_WORK_IN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_WORK_IN_D] (
    [WORK_IN_TYPE] nchar(10) NOT NULL,
    [WORK_IN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [CLIENT_ID] nchar(10) NULL,
    [WORK_TYPE] nchar(10) NULL,
    [WORK_NO] nchar(20) NULL,
    [WORK_SERIAL_NO] smallint NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_1646628909] DEFAULT ((0)),
    [QTY] float NULL CONSTRAINT [DF_QTY_1646628909] DEFAULT ((0)),
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [COLOR_ID] nchar(10) NULL,
    [STUFF_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_MOC_WORK_IN_D] PRIMARY KEY CLUSTERED ([WORK_IN_TYPE], [WORK_IN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_IN_D_1] ON dbo.[MOC_WORK_IN_D] ([PRODUCE_TYPE], [PRODUCE_NO], [PRODUCE_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_IN_D_3] ON dbo.[MOC_WORK_IN_D] ([PRO_NO], [PROCEDURE_TYPE_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_IN_D_4] ON dbo.[MOC_WORK_IN_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_IN_D_2] ON dbo.[MOC_WORK_IN_D] ([WORK_TYPE], [WORK_NO], [WORK_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_IN_D] ON dbo.[MOC_WORK_IN_D] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- MOC_WORK_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_WORK_IN_M] (
    [WORK_IN_TYPE] nchar(10) NOT NULL,
    [WORK_IN_NO] nchar(20) NOT NULL,
    [WORK_IN_DATE] datetime NULL,
    [LINE_ID] nchar(10) NULL,
    [LINE_NAME] nvarchar(50) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1662628966] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOC_WORK_IN_M] PRIMARY KEY CLUSTERED ([WORK_IN_TYPE], [WORK_IN_NO])
);
GO

------------------------------------------------------------------------------
-- MOC_WORK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_WORK_M] (
    [WORK_TYPE] nchar(10) NOT NULL,
    [WORK_NO] nchar(20) NOT NULL,
    [WORK_DATE] datetime NULL,
    [LINE_ID] nchar(10) NULL,
    [LINE_NAME] nvarchar(50) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1678629023] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1678629023] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1678629023] DEFAULT ((0)),
    CONSTRAINT [PK_MOC_WORK_M] PRIMARY KEY CLUSTERED ([WORK_TYPE], [WORK_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_M] ON dbo.[MOC_WORK_M] ([PROCEDURE_TYPE_ID]);
GO

------------------------------------------------------------------------------
-- MOC_WORK_OUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_WORK_OUT_D] (
    [WORK_OUT_TYPE] nchar(10) NOT NULL,
    [WORK_OUT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [WORK_TYPE] nchar(10) NULL,
    [WORK_NO] nchar(20) NULL,
    [WORK_SERIAL_NO] smallint NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1694629080] DEFAULT ((0)),
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [COLOR_ID] nchar(10) NULL,
    [STUFF_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [OUT_DEPOT_ID] nchar(10) NULL,
    [IN_DEPOT_ID] nchar(10) NULL,
    [NEXT_PROCEDURE_TYPE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_MOC_WORK_OUT_D] PRIMARY KEY CLUSTERED ([WORK_OUT_TYPE], [WORK_OUT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_OUT_D_2] ON dbo.[MOC_WORK_OUT_D] ([PRO_NO], [PROCEDURE_TYPE_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_OUT_D_3] ON dbo.[MOC_WORK_OUT_D] ([ORDER_TYPE], [ORDER_NO], [ORDER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_OUT_D_1] ON dbo.[MOC_WORK_OUT_D] ([PRODUCE_TYPE], [PRODUCE_NO], [PRODUCE_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOC_WORK_OUT_D] ON dbo.[MOC_WORK_OUT_D] ([WORK_TYPE], [WORK_NO], [WORK_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- MOC_WORK_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOC_WORK_OUT_M] (
    [WORK_OUT_TYPE] nchar(10) NOT NULL,
    [WORK_OUT_NO] nchar(20) NOT NULL,
    [WORK_OUT_DATE] datetime NULL,
    [LINE_ID] nchar(10) NULL,
    [LINE_NAME] nvarchar(50) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1710629137] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOC_WORK_OUT_M] PRIMARY KEY CLUSTERED ([WORK_OUT_TYPE], [WORK_OUT_NO])
);
GO

------------------------------------------------------------------------------
-- MODULES
------------------------------------------------------------------------------
CREATE TABLE dbo.[MODULES] (
    [M_IDX] int NOT NULL,
    [M_DESC] nvarchar(500) NULL,
    [M_URL] nvarchar(500) NULL,
    [MODI_URL] nvarchar(300) NULL,
    [M_P_IDX] int NULL,
    [M_ROOT_IDX] int NULL,
    [SORT_IDX] int NULL,
    [M_TAG] bit NULL,
    [MASTER_TABLE] nvarchar(50) NULL,
    [FILTER] nvarchar(500) NULL,
    [DETAIL_TABLE] nvarchar(50) NULL,
    [UPDATE_SP] nvarchar(100) NULL,
    [AFTERSAVE_SP] nvarchar(100) NULL,
    [GROUP1] bit NULL,
    [GROUP_EXP1] nvarchar(500) NULL,
    [GROUP_DESC1] varchar(50) NULL,
    [GROUP2] bit NULL,
    [GROUP_EXP2] nvarchar(500) NULL,
    [GROUP_DESC2] nvarchar(50) NULL,
    [GROUP3] bit NULL,
    [GROUP_EXP3] nvarchar(500) NULL,
    [GROUP_DESC3] nvarchar(50) NULL,
    [GROUP4] bit NULL,
    [GROUP_EXP4] nvarchar(500) NULL,
    [GROUP_DESC4] nvarchar(50) NULL,
    [GROUP5] bit NULL,
    [GROUP_EXP5] nvarchar(500) NULL,
    [GROUP_DESC5] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [M_ALIAS] nvarchar(50) NULL,
    [DETAIL_NO_FIELDS] nvarchar(500) NULL,
    [DETAIL_NO_SAVE] bit NULL,
    [NOT_BACK_FIELDS] nvarchar(500) NULL,
    [NOT_BACK_FIELDS_M] nvarchar(500) NULL,
    [AUTO_APPROVE] bit NULL CONSTRAINT [DF_AUTO_APPROVE_1726629194] DEFAULT ((0)),
    [ERROR_NO_SAVE] bit NULL CONSTRAINT [DF_ERROR_NO_SAVE_1726629194] DEFAULT ((0)),
    [SEARCH_1] bit NULL,
    [SEARCH_2] bit NULL,
    [SORT_FIELDS] nvarchar(200) NULL,
    [HELP_URL] nvarchar(300) NULL,
    [IF_COPY] bit NULL,
    [NEW_URL] nvarchar(300) NULL,
    [FORM_TABS] nvarchar(500) NULL,
    [FORM_COLUMNS] tinyint NULL,
    [FORM_BUTTONS] nvarchar(300) NULL,
    [M_ICON] nvarchar(50) NULL,
    CONSTRAINT [PK_MODULES] PRIMARY KEY CLUSTERED ([M_IDX])
);
GO
CREATE NONCLUSTERED INDEX [IX_MODULES] ON dbo.[MODULES] ([M_ROOT_IDX], [M_P_IDX]);
GO

------------------------------------------------------------------------------
-- MOU_ACCEPT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_ACCEPT_M] (
    [ACCEPT_TYPE] nchar(10) NOT NULL,
    [ACCEPT_NO] nchar(20) NOT NULL,
    [ACCEPT_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [APPLY_TYPE] nchar(10) NULL,
    [APPLY_NO] nchar(20) NULL,
    [MOU_TYPE] nchar(10) NULL,
    [MOULD_ID] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1774629365] DEFAULT ((0)),
    [AMOUNT] float NULL,
    [ACCEPT_STATE] nchar(2) NULL,
    [BATCH_STATE] char(1) NULL,
    [FINISHED_QTY] int NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1774629365] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [IF_NEW] bit NULL,
    [IF_CHANGE] bit NULL,
    [EDITION] nvarchar(20) NULL,
    [PRO_NO] nchar(30) NULL,
    [YLKD] nvarchar(50) NULL,
    [CPG] nvarchar(20) NULL,
    [KPG] nvarchar(20) NULL,
    [LIAOCHANG] nvarchar(50) NULL,
    [P_LENGTH] nvarchar(20) NULL,
    [P_WIDTH] nvarchar(20) NULL,
    [ELEMENTS] nvarchar(50) NULL,
    [PACK_PE] bit NULL,
    [PACK_BOX] bit NULL,
    [CLIENT_REQUIRE] nvarchar(200) NULL,
    [AFFORD_XS] bit NULL,
    [AFFORD_SW] bit NULL,
    [AFFORD_ZH] bit NULL,
    [AFFORD_QT] bit NULL,
    [AFFORD_DOC] nvarchar(50) NULL,
    [SAMPLE_PLACE] nvarchar(50) NULL,
    [PAINT_REQ1] bit NULL,
    [PAINT_REQ2] bit NULL,
    [PAINT_REQ3] bit NULL,
    [PAINT_REQ4] bit NULL,
    [FACE_ZK1] bit NULL,
    [FACE_ZK2] bit NULL,
    [FACE_ZK3] bit NULL,
    [FACE_ZK4] bit NULL,
    [FACE_ZK5] bit NULL,
    [QC_CHECK1] nvarchar(200) NULL,
    [QC_CHECK2] nvarchar(200) NULL,
    [QC_CHECK3] nvarchar(200) NULL,
    [REQUIRE1] nvarchar(200) NULL,
    [REQUIRE2] nvarchar(200) NULL,
    [REQUIRE3] nvarchar(200) NULL,
    [REQUIRE4] nvarchar(200) NULL,
    [REQUIRE5] nvarchar(200) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1774629365] DEFAULT ((0)),
    [SUTTLE] float NULL,
    [PRO_SPACE] float NULL,
    [SHANGMO] nchar(10) NULL,
    [PACK_QTY] float NULL CONSTRAINT [DF_PACK_QTY_1774629365] DEFAULT ((0)),
    [PACK_JS] float NULL CONSTRAINT [DF_PACK_JS_1774629365] DEFAULT ((0)),
    [PACK_SUM] float NULL CONSTRAINT [DF_PACK_SUM_1774629365] DEFAULT ((0)),
    [SHANGMO_WK] nchar(10) NULL,
    [ELEMENT_PRO_NO1] nchar(30) NULL CONSTRAINT [DF_ELEMENT_PRO_NO1_1774629365] DEFAULT (''),
    [ELEMENT_PRO_NO2] nchar(30) NULL CONSTRAINT [DF_ELEMENT_PRO_NO2_1774629365] DEFAULT (''),
    [ELEMENT_PRO_NO3] nchar(30) NULL CONSTRAINT [DF_ELEMENT_PRO_NO3_1774629365] DEFAULT (''),
    CONSTRAINT [PK_MOU_ACCEPT_M] PRIMARY KEY CLUSTERED ([ACCEPT_TYPE], [ACCEPT_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_ACCEPT_M] ON dbo.[MOU_ACCEPT_M] ([APPLY_TYPE]);
GO

------------------------------------------------------------------------------
-- MOU_ACCEPTDELE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_ACCEPTDELE_D] (
    [ACCEPTDELE_TYPE] nchar(10) NOT NULL,
    [ACCEPTDELE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [ACCEPT_TYPE] nchar(10) NOT NULL,
    [ACCEPT_NO] nchar(20) NOT NULL,
    CONSTRAINT [PK_MOU_ACCEPTDELE_D] PRIMARY KEY CLUSTERED ([ACCEPTDELE_TYPE], [ACCEPTDELE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- MOU_ACCEPTDELE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_ACCEPTDELE_M] (
    [ACCEPTDELE_TYPE] nchar(10) NOT NULL,
    [ACCEPTDELE_NO] nchar(20) NOT NULL,
    [ACCEPTDELE_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1758629308] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOU_ACCEPTDELE_M] PRIMARY KEY CLUSTERED ([ACCEPTDELE_TYPE], [ACCEPTDELE_NO])
);
GO

------------------------------------------------------------------------------
-- MOU_APPLY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_APPLY_D] (
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [LENGTH] float NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1790629422] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [SAM_QTY] float NULL CONSTRAINT [DF_SAM_QTY_1790629422] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_MOU_APPLY_D] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_APPLY_D] ON dbo.[MOU_APPLY_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- MOU_APPLY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_APPLY_M] (
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    [APPLY_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(100) NULL,
    [SALES_ID] nchar(10) NULL,
    [SAM_TYPE] char(1) NULL,
    [APPLY_TYPE_OLD] nchar(10) NULL,
    [APPLY_NO_OLD] nchar(20) NULL,
    [MOU_TYPE] nchar(10) NULL,
    [MOULD_ID] nchar(30) NULL,
    [IF_PAPER] bit NULL CONSTRAINT [DF_IF_PAPER_1806629479] DEFAULT ((0)),
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [PRO_NO] nchar(30) NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [QTY] float NULL,
    [MOU_QTY] float NULL CONSTRAINT [DF_MOU_QTY_1806629479] DEFAULT ((0)),
    [SEND_DATE] datetime NULL,
    [AMOUNT] float NULL,
    [REQUEST1] nvarchar(200) NULL,
    [REQUEST2] nvarchar(200) NULL,
    [SAM_COUNT] float NULL CONSTRAINT [DF_SAM_COUNT_1806629479] DEFAULT ((0)),
    [ACCEPT_TYPE] nchar(10) NULL,
    [ACCEPT_NO] nchar(20) NULL,
    [SUTTLE] float NULL,
    [ACCEPT_STATE] nchar(2) NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [SUPPLIER_ORDER_NO] nvarchar(50) NULL,
    [SUPPLIER_DATE] datetime NULL,
    [SUPPLIER_AMOUNT] float NULL,
    [SUPPLIER_REMARK] nvarchar(50) NULL,
    [FIRST_LEVEL] nvarchar(50) NULL,
    [PURCHASE_TYPE] nchar(1) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1806629479] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [MOULD_CUT] nchar(30) NULL,
    [ASSESS_TYPE] nchar(10) NULL,
    [ASSESS_NO] nchar(20) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1806629479] DEFAULT ((0)),
    [PRODUCE_DATE] nvarchar(50) NULL,
    CONSTRAINT [PK_MOU_APPLY_M] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_APPLY_M_1] ON dbo.[MOU_APPLY_M] ([APPLY_TYPE_OLD], [APPLY_NO_OLD]);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_APPLY_M] ON dbo.[MOU_APPLY_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- MOU_ASSESS_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_ASSESS_M] (
    [ASSESS_TYPE] nchar(10) NOT NULL,
    [ASSESS_NO] nchar(20) NOT NULL,
    [ASSESS_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [PRO_NO] nchar(30) NULL,
    [ELEMENTS] nvarchar(50) NULL,
    [PRO_SIZE] nvarchar(100) NULL,
    [PINGGU] bit NULL,
    [KAIMO] bit NULL,
    [YLKD] nvarchar(50) NULL,
    [MJSL] nvarchar(50) NULL,
    [LIAOCHANG] nvarchar(50) NULL,
    [MOULD_ID] nvarchar(50) NULL,
    [CPLX1] bit NULL,
    [CPLX2] bit NULL,
    [CPLX3] bit NULL,
    [CPLX4] bit NULL,
    [CPLX5] bit NULL,
    [CPLX6] bit NULL,
    [CPLX7] bit NULL,
    [CPLX8] bit NULL,
    [CPLX9] bit NULL,
    [CPLX10] bit NULL,
    [XSYT1] bit NULL,
    [XSYT2] bit NULL,
    [LSXZY1] bit NULL,
    [LSXZY2] bit NULL,
    [TGZL1] bit NULL,
    [TGZL2] bit NULL,
    [TGZL3] bit NULL,
    [TGZL4] bit NULL,
    [TGZL5] bit NULL,
    [TGZL6] bit NULL,
    [TGZL7] bit NULL,
    [TGZL8] nvarchar(100) NULL,
    [KMJZ1] bit NULL,
    [KMJZ2] bit NULL,
    [KMJZ3] nvarchar(100) NULL,
    [KMLX1] bit NULL,
    [KMLX2] bit NULL,
    [KMLX3] bit NULL,
    [KMLX4] bit NULL,
    [KMLX5] nvarchar(100) NULL,
    [KMLX6] nvarchar(100) NULL,
    [KMLX8] bit NULL,
    [KMLX9] bit NULL,
    [PHCC1] bit NULL,
    [PHCC2] bit NULL,
    [PHCC3] bit NULL,
    [PHCC4] nvarchar(50) NULL,
    [PHCC5] bit NULL,
    [PHCC6] bit NULL,
    [PHCC7] bit NULL,
    [XSLB1] bit NULL,
    [XSLB2] nvarchar(50) NULL,
    [XSLB3] bit NULL,
    [XSLB4] bit NULL,
    [XSLB5] bit NULL,
    [DYFS1] bit NULL,
    [DYFS2] bit NULL,
    [DYFS3] bit NULL,
    [DYFS4] bit NULL,
    [DYFS5] bit NULL,
    [DYFS6] bit NULL,
    [DYFS7] bit NULL,
    [DYFS8] bit NULL,
    [DYFS9] bit NULL,
    [DGQR1] bit NULL,
    [DGQR2] bit NULL,
    [DGQR3] bit NULL,
    [DGQR4] bit NULL,
    [BMCL1] bit NULL,
    [BMCL2] bit NULL,
    [BMCL4] nvarchar(50) NULL,
    [BMCL5] bit NULL,
    [BMCL6] nvarchar(50) NULL,
    [BMDK1] bit NULL,
    [BMDK2] bit NULL,
    [BMDK3] bit NULL,
    [BMDK4] bit NULL,
    [BMDK5] bit NULL,
    [BMDK6] bit NULL,
    [BMDK7] bit NULL,
    [BMDK8] bit NULL,
    [BMDK9] bit NULL,
    [BMDK10] nvarchar(100) NULL,
    [KHSY1] bit NULL,
    [KHSY2] bit NULL,
    [KHSY3] bit NULL,
    [KHSY4] bit NULL,
    [KHSY5] bit NULL,
    [KHSY6] bit NULL,
    [KHSY7] bit NULL,
    [KHSY8] bit NULL,
    [KHSY9] nvarchar(100) NULL,
    [CS1] bit NULL,
    [CS2] bit NULL,
    [CS3] nvarchar(100) NULL,
    [ZDGK] nvarchar(500) NULL,
    [CNCBCSJ] nvarchar(50) NULL,
    [SJJGSJ] nvarchar(50) NULL,
    [SMSJ] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1822629536] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [APPLY_NO] nchar(20) NULL,
    [KMLX7] bit NULL,
    [APPLY_TYPE] nchar(10) NULL,
    [CPG] nvarchar(20) NULL,
    [KPG] nvarchar(20) NULL,
    [PBKD] nvarchar(50) NULL,
    [PBCD] nvarchar(50) NULL,
    [KMLX10] bit NULL,
    CONSTRAINT [PK_MOU_ASSESS_M] PRIMARY KEY CLUSTERED ([ASSESS_TYPE], [ASSESS_NO])
);
GO

------------------------------------------------------------------------------
-- MOU_BATCH_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_BATCH_D] (
    [BATCH_TYPE] nchar(10) NOT NULL,
    [BATCH_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [LENGTH] float NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1902629821] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1902629821] DEFAULT ((0)),
    [SAM_QTY] float NULL CONSTRAINT [DF_SAM_QTY_1902629821] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_MOU_BATCH_D] PRIMARY KEY CLUSTERED ([BATCH_TYPE], [BATCH_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_BATCH_D] ON dbo.[MOU_BATCH_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- MOU_BATCH_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_BATCH_M] (
    [BATCH_TYPE] nchar(10) NOT NULL,
    [BATCH_NO] nchar(20) NOT NULL,
    [BATCH_DATE] datetime NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [MOU_TYPE] nchar(10) NULL,
    [MOULD_ID] nchar(30) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [PRO_NO] nchar(30) NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1918629878] DEFAULT ((0)),
    [SEND_DATE] datetime NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1918629878] DEFAULT ((0)),
    [REQUEST1] nvarchar(50) NULL,
    [REQUEST2] nvarchar(50) NULL,
    [REQUEST3] nvarchar(50) NULL,
    [REQUEST4] nvarchar(50) NULL,
    [REQUEST5] nvarchar(50) NULL,
    [REQUEST6] nvarchar(50) NULL,
    [REQUEST7] nvarchar(500) NULL,
    [REQUEST8] nvarchar(500) NULL,
    [ACCEPT_TYPE] nchar(10) NULL,
    [ACCEPT_NO] nchar(20) NULL,
    [SUTTLE] float NULL CONSTRAINT [DF_SUTTLE_1918629878] DEFAULT ((0)),
    [SUPPLIER_ID] nchar(15) NULL,
    [SUPPLIER_ORDER_NO] nvarchar(50) NULL,
    [SUPPLIER_DATE] datetime NULL,
    [SUPPLIER_AMOUNT] float NULL,
    [SUPPLIER_REMARK] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1918629878] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [BATCH_SORT] char(1) NULL,
    [SCRAP_TYPE] nchar(10) NULL,
    [SCRAP_NO] nchar(20) NULL,
    [SCRAP_SERIAL_NO] smallint NULL,
    [PRICE] float NULL,
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1918629878] DEFAULT ((0)),
    [ASSESS_TYPE] nchar(10) NULL,
    [ASSESS_NO] nchar(20) NULL,
    [LIAOCHANG] nvarchar(50) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1918629878] DEFAULT ((0)),
    CONSTRAINT [PK_MOU_BATCH_M] PRIMARY KEY CLUSTERED ([BATCH_TYPE], [BATCH_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_BATCH_M] ON dbo.[MOU_BATCH_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- MOU_BATCHIN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_BATCHIN_D] (
    [BATCHIN_TYPE] nchar(10) NOT NULL,
    [BATCHIN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [BATCH_TYPE] nchar(10) NULL,
    [BATCH_NO] nchar(20) NULL,
    [MOULD_ID] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1838629593] DEFAULT ((0)),
    [PRICE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1838629593] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1838629593] DEFAULT ((0)),
    CONSTRAINT [PK_MOU_BATCHIN_D] PRIMARY KEY CLUSTERED ([BATCHIN_TYPE], [BATCHIN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_BATCHIN_D] ON dbo.[MOU_BATCHIN_D] ([BATCH_TYPE], [BATCH_NO]);
GO

------------------------------------------------------------------------------
-- MOU_BATCHIN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_BATCHIN_M] (
    [BATCHIN_TYPE] nchar(10) NOT NULL,
    [BATCHIN_NO] nchar(20) NOT NULL,
    [BATCHIN_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [SUPPLIER_ORDER_NO] nvarchar(50) NULL,
    [SUPPLIER_DATE] datetime NULL,
    [SUPPLIER_AMOUNT] float NULL,
    [SUPPLIER_REMARK] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1854629650] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [MAIN_SOURCE] char(1) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1854629650] DEFAULT ((0)),
    CONSTRAINT [PK_MOU_BATCHIN_M] PRIMARY KEY CLUSTERED ([BATCHIN_TYPE], [BATCHIN_NO])
);
GO

------------------------------------------------------------------------------
-- MOU_BATCHTOP_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_BATCHTOP_D] (
    [BATCH_TYPE] nchar(10) NOT NULL,
    [BATCH_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [LENGTH] float NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1870629707] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1870629707] DEFAULT ((0)),
    [SAM_QTY] float NULL CONSTRAINT [DF_SAM_QTY_1870629707] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_MOU_BATCHTOP_D] PRIMARY KEY CLUSTERED ([BATCH_TYPE], [BATCH_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- MOU_BATCHTOP_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_BATCHTOP_M] (
    [BATCH_TYPE] nchar(10) NOT NULL,
    [BATCH_NO] nchar(20) NOT NULL,
    [BATCH_DATE] datetime NULL,
    [BATCH_SORT] char(1) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [SALES_ID] nchar(10) NULL,
    [SCRAP_TYPE] nchar(10) NULL,
    [SCRAP_NO] nchar(20) NULL,
    [SCRAP_SERIAL_NO] smallint NULL,
    [MOU_TYPE] nchar(10) NULL,
    [MOU_SORT] nchar(1) NULL,
    [MOU_SHAPE] nvarchar(50) NULL,
    [MAIN_SOURCE] char(1) NULL,
    [MOU_SPEC] nvarchar(100) NULL,
    [MOU_GIRTH] float NULL,
    [GIRTH_PRICE] float NULL,
    [GIRTH_AMOUNT] float NULL,
    [ADD_LAYERS] int NULL,
    [LAYER_PRICE] float NULL,
    [LAYER_AMOUNT] float NULL,
    [TOTAL_AMOUNT] float NULL,
    [MOULD_ID] nchar(30) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [PRO_NO] nchar(30) NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [SEND_DATE] datetime NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1886629764] DEFAULT ((0)),
    [PRICE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1886629764] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1886629764] DEFAULT ((0)),
    [REQUEST1] nvarchar(50) NULL,
    [REQUEST2] nvarchar(50) NULL,
    [REQUEST3] nvarchar(50) NULL,
    [REQUEST4] nvarchar(50) NULL,
    [REQUEST5] nvarchar(50) NULL,
    [REQUEST6] nvarchar(50) NULL,
    [REQUEST7] nvarchar(500) NULL,
    [REQUEST8] nvarchar(500) NULL,
    [ACCEPT_TYPE] nchar(10) NULL,
    [ACCEPT_NO] nchar(20) NULL,
    [SUTTLE] float NULL CONSTRAINT [DF_SUTTLE_1886629764] DEFAULT ((0)),
    [SUPPLIER_ID] nchar(15) NULL,
    [SUPPLIER_ORDER_NO] nvarchar(50) NULL,
    [SUPPLIER_DATE] datetime NULL,
    [SUPPLIER_AMOUNT] float NULL,
    [SUPPLIER_REMARK] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1886629764] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [BATCH_YP] nchar(1) NULL,
    CONSTRAINT [PK_MOU_BATCHTOP_M] PRIMARY KEY CLUSTERED ([BATCH_TYPE], [BATCH_NO])
);
GO

------------------------------------------------------------------------------
-- MOU_GET_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_GET_D] (
    [GET_TYPE] nchar(10) NOT NULL,
    [GET_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1966630049] DEFAULT ((0)),
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1966630049] DEFAULT ((0)),
    [DEPOT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(500) NULL,
    [IN_DEPOT_ID] nchar(10) NULL,
    CONSTRAINT [PK_MOU_GET_D] PRIMARY KEY CLUSTERED ([GET_TYPE], [GET_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_GET_D] ON dbo.[MOU_GET_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- MOU_GET_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_GET_M] (
    [GET_TYPE] nchar(10) NOT NULL,
    [GET_NO] nchar(20) NOT NULL,
    [GET_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1982630106] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOU_GET_M] PRIMARY KEY CLUSTERED ([GET_TYPE], [GET_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_GET_M] ON dbo.[MOU_GET_M] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- MOU_GET2_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_GET2_D] (
    [GET_TYPE] nchar(10) NOT NULL,
    [GET_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1934629935] DEFAULT ((0)),
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1934629935] DEFAULT ((0)),
    [DEPOT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [REMARK] nvarchar(500) NULL,
    [APPLY_TYPE] nchar(10) NULL CONSTRAINT [DF_APPLY_TYPE_1934629935] DEFAULT (''),
    [APPLY_NO] nchar(20) NULL CONSTRAINT [DF_APPLY_NO_1934629935] DEFAULT (''),
    CONSTRAINT [PK_MOU_GET2_D] PRIMARY KEY CLUSTERED ([GET_TYPE], [GET_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- MOU_GET2_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_GET2_M] (
    [GET_TYPE] nchar(10) NOT NULL,
    [GET_NO] nchar(20) NOT NULL,
    [GET_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1950629992] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOU_GET2_M] PRIMARY KEY CLUSTERED ([GET_TYPE], [GET_NO])
);
GO

------------------------------------------------------------------------------
-- MOU_IN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_IN_D] (
    [IN_TYPE] nchar(10) NOT NULL,
    [IN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [OUT_TYPE] nchar(10) NULL,
    [OUT_NO] nchar(20) NULL,
    [OUT_SERIAL_NO] smallint NULL,
    [MOULD_ID] nchar(30) NULL CONSTRAINT [DF_MOULD_ID_1998630163] DEFAULT (''),
    [QTY] float NULL CONSTRAINT [DF_QTY_1998630163] DEFAULT ((0)),
    [USE_COUNT] float NULL CONSTRAINT [DF_USE_COUNT_1998630163] DEFAULT ((0)),
    [REMARK] nvarchar(200) NULL,
    CONSTRAINT [PK_MOU_IN_D] PRIMARY KEY CLUSTERED ([IN_TYPE], [IN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_IN_D_1] ON dbo.[MOU_IN_D] ([MOULD_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_IN_D] ON dbo.[MOU_IN_D] ([OUT_TYPE], [OUT_NO], [OUT_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- MOU_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_IN_M] (
    [IN_TYPE] nchar(10) NOT NULL,
    [IN_NO] nchar(20) NOT NULL,
    [IN_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL CONSTRAINT [DF_DEPT_ID_2014630220] DEFAULT (''),
    [EMP_ID] nchar(10) NULL CONSTRAINT [DF_EMP_ID_2014630220] DEFAULT (''),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2014630220] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOU_IN_M] PRIMARY KEY CLUSTERED ([IN_TYPE], [IN_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_IN_M] ON dbo.[MOU_IN_M] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- MOU_MOULD
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_MOULD] (
    [MOULD_ID] nchar(30) NOT NULL,
    [MOULD_NAME] nvarchar(50) NULL,
    [MOULD_SPEC] nvarchar(50) NULL,
    [MOULD_SPEC1] nvarchar(50) NULL,
    [MOULD_SPEC2] nvarchar(50) NULL,
    [MOULD_SPEC3] nvarchar(50) NULL,
    [LENGTH] float NULL,
    [WIDTH] float NULL,
    [HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [BATCH_TYPE] nchar(10) NULL,
    [BATCH_NO] nchar(20) NULL,
    [ACCEPT_TYPE] nchar(10) NULL,
    [ACCEPT_NO] nchar(20) NULL,
    [APPLY_TYPE] nchar(10) NULL,
    [APPLY_NO] nchar(20) NULL,
    [MOU_TYPE] nchar(10) NULL,
    [OWNER_ID] nchar(10) NULL,
    [IS_BACK] bit NULL CONSTRAINT [DF_IS_BACK_2030630277] DEFAULT ((0)),
    [USE_TAG] bit NULL CONSTRAINT [DF_USE_TAG_2030630277] DEFAULT ((0)),
    [FIRST_IN_DATE] datetime NULL,
    [LAST_IN_DATE] datetime NULL,
    [LAST_OUT_DATE] datetime NULL,
    [LAST_CHECK_DATE] datetime NULL,
    [MOU_QTY] float NULL CONSTRAINT [DF_MOU_QTY_2030630277] DEFAULT ((0)),
    [QTY] float NULL CONSTRAINT [DF_QTY_2030630277] DEFAULT ((0)),
    [SCRAP_QTY] float NULL CONSTRAINT [DF_SCRAP_QTY_2030630277] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_2030630277] DEFAULT ((0)),
    [CLIENT_MOULD_NO] nvarchar(50) NULL,
    [CAN_COUNT] float NULL,
    [FINISHED_COUNT] float NULL,
    [PLACE1] nvarchar(50) NULL,
    [PLACE2] nvarchar(50) NULL,
    [PLACE3] nvarchar(50) NULL,
    [PLACE4] nvarchar(50) NULL,
    [ATTENTION] nvarchar(200) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2030630277] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [MOU_SORT] nchar(1) NULL,
    [LINE_ID] nvarchar(50) NULL,
    CONSTRAINT [PK_MOU_MOULD] PRIMARY KEY CLUSTERED ([MOULD_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_MOULD_2] ON dbo.[MOU_MOULD] ([BATCH_TYPE], [BATCH_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_MOULD_3] ON dbo.[MOU_MOULD] ([APPLY_TYPE], [APPLY_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_MOULD_1] ON dbo.[MOU_MOULD] ([ACCEPT_TYPE], [ACCEPT_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_MOULD] ON dbo.[MOU_MOULD] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- MOU_OUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_OUT_D] (
    [OUT_TYPE] nchar(10) NOT NULL,
    [OUT_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MOULD_ID] nchar(30) NULL CONSTRAINT [DF_MOULD_ID_2046630334] DEFAULT (''),
    [QTY] float NULL CONSTRAINT [DF_QTY_2046630334] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_2046630334] DEFAULT ((0)),
    [REMARK] nvarchar(200) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    CONSTRAINT [PK_MOU_OUT_D] PRIMARY KEY CLUSTERED ([OUT_TYPE], [OUT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_OUT_D] ON dbo.[MOU_OUT_D] ([MOULD_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_OUT_D_1] ON dbo.[MOU_OUT_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO

------------------------------------------------------------------------------
-- MOU_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_OUT_M] (
    [OUT_TYPE] nchar(10) NOT NULL,
    [OUT_NO] nchar(20) NOT NULL,
    [OUT_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2062630391] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOU_OUT_M] PRIMARY KEY CLUSTERED ([OUT_TYPE], [OUT_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_OUT_M] ON dbo.[MOU_OUT_M] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- MOU_OWNER
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_OWNER] (
    [OWNER_ID] nchar(10) NOT NULL,
    [OWNER_NAME] nchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2078630448] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOU_OWNER] PRIMARY KEY CLUSTERED ([OWNER_ID])
);
GO

------------------------------------------------------------------------------
-- MOU_PRO_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_PRO_D] (
    [PRO_NO] nchar(30) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MOULD_ID] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_2094630505] DEFAULT ((0)),
    [REMARK] nvarchar(300) NULL,
    [EDITION] nchar(10) NULL,
    CONSTRAINT [PK_MOU_PRO_D] PRIMARY KEY CLUSTERED ([PRO_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_PRO_D] ON dbo.[MOU_PRO_D] ([MOULD_ID], [PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_PRO_D_1] ON dbo.[MOU_PRO_D] ([PRO_NO], [MOULD_ID]);
GO

------------------------------------------------------------------------------
-- MOU_PRO_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_PRO_M] (
    [PRO_NO] nchar(30) NOT NULL,
    [EDITION] nchar(10) NULL,
    [ATTENTION] nvarchar(200) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2110630562] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [MOULD_IDS] nvarchar(500) NULL,
    CONSTRAINT [PK_MOU_PRO_M] PRIMARY KEY CLUSTERED ([PRO_NO])
);
GO

------------------------------------------------------------------------------
-- MOU_SCRAP_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_SCRAP_D] (
    [SCRAP_TYPE] nchar(10) NOT NULL,
    [SCRAP_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [OUT_TYPE] nchar(10) NULL,
    [OUT_NO] nchar(20) NULL,
    [OUT_SERIAL_NO] smallint NULL,
    [MOULD_ID] nchar(30) NULL CONSTRAINT [DF_MOULD_ID_2126630619] DEFAULT (''),
    [QTY] float NULL CONSTRAINT [DF_QTY_2126630619] DEFAULT ((0)),
    [REMARK] nvarchar(200) NULL,
    [BATCH_STATE] bit NULL CONSTRAINT [DF_BATCH_STATE_2126630619] DEFAULT ((0)),
    [ADD_QTY] float NULL CONSTRAINT [DF_ADD_QTY_2126630619] DEFAULT ((0)),
    [CLIENT_ID] nchar(10) NULL,
    CONSTRAINT [PK_MOU_SCRAP_D] PRIMARY KEY CLUSTERED ([SCRAP_TYPE], [SCRAP_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_SCRAP_D_1] ON dbo.[MOU_SCRAP_D] ([OUT_TYPE], [OUT_NO], [OUT_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_SCRAP_D] ON dbo.[MOU_SCRAP_D] ([MOULD_ID]);
GO

------------------------------------------------------------------------------
-- MOU_SCRAP_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_SCRAP_M] (
    [SCRAP_TYPE] nchar(10) NOT NULL,
    [SCRAP_NO] nchar(20) NOT NULL,
    [SCRAP_DATE] datetime NULL,
    [SCRAP_ID] nchar(1) NULL,
    [DEPT_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2142630676] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOU_SCRAP_M] PRIMARY KEY CLUSTERED ([SCRAP_TYPE], [SCRAP_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_MOU_SCRAP_M] ON dbo.[MOU_SCRAP_M] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- MOU_TYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[MOU_TYPE] (
    [TYPE_ID] nchar(10) NOT NULL,
    [TYPE_NAME] nchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_MOU_TYPE] PRIMARY KEY CLUSTERED ([TYPE_ID])
);
GO

------------------------------------------------------------------------------
-- PAP_BARCODE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_BARCODE_M] (
    [BARCODE_TYPE] nchar(10) NOT NULL,
    [BARCODE_NO] nchar(20) NOT NULL,
    [BARCODE_DATE] datetime NOT NULL,
    [TYPE_ID] nchar(10) NULL,
    [BRAND_ID] nchar(10) NULL,
    [GRAMME_ID] nchar(10) NULL,
    [SPECS_ID] nchar(10) NULL,
    [WEIGHT] float NULL CONSTRAINT [DF_WEIGHT_27147142] DEFAULT ((0)),
    [IN_NUM] float NULL CONSTRAINT [DF_IN_NUM_27147142] DEFAULT ((0)),
    [OUT_NUM] float NULL CONSTRAINT [DF_OUT_NUM_27147142] DEFAULT ((0)),
    [CHECK_NUM] float NULL CONSTRAINT [DF_CHECK_NUM_27147142] DEFAULT ((0)),
    [PRINT_NUM] float NULL CONSTRAINT [DF_PRINT_NUM_27147142] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_27147142] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_27147142] DEFAULT ((0)),
    [CURR_ID] nchar(10) NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_27147142] DEFAULT ((0)),
    [SEND_TYPE] nchar(10) NULL,
    [SEND_NO] nchar(20) NULL,
    [SERIAL_NO] smallint NULL,
    [IN_DATE] datetime NULL,
    [OUT_DATE] datetime NULL,
    [CHECK_DATE] datetime NULL,
    [DEPOT_ID] nchar(10) NULL,
    [GUNHAO] nvarchar(50) NULL,
    [CHECK_OUT_NUM] float NULL CONSTRAINT [DF_CHECK_OUT_NUM_27147142] DEFAULT ((0)),
    [CHECK_OUT_DATE] datetime NULL,
    CONSTRAINT [PK_PAP_BARCODE_M] PRIMARY KEY CLUSTERED ([BARCODE_TYPE], [BARCODE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PAP_BARCODE_M] ON dbo.[PAP_BARCODE_M] ([TYPE_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_PAP_BARCODE_M_2] ON dbo.[PAP_BARCODE_M] ([GRAMME_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_PAP_BARCODE_M_3] ON dbo.[PAP_BARCODE_M] ([SPECS_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_PAP_BARCODE_M_1] ON dbo.[PAP_BARCODE_M] ([BRAND_ID]);
GO

------------------------------------------------------------------------------
-- PAP_BRAND
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_BRAND] (
    [BRAND_ID] nchar(10) NOT NULL,
    [BRAND_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_43147199] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PAP_BRAND] PRIMARY KEY CLUSTERED ([BRAND_ID])
);
GO

------------------------------------------------------------------------------
-- PAP_CHECK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_CHECK_M] (
    [BARCODE_NO] nchar(20) NOT NULL,
    [CHECK_DATE] datetime NULL,
    CONSTRAINT [PK_PAP_CHECK_M] PRIMARY KEY CLUSTERED ([BARCODE_NO])
);
GO

------------------------------------------------------------------------------
-- PAP_CHECK_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_CHECK_OUT_M] (
    [BARCODE_NO] nchar(20) NOT NULL,
    [CHECK_OUT_DATE] datetime NULL,
    CONSTRAINT [PK_PAP_CHECK_OUT_M] PRIMARY KEY CLUSTERED ([BARCODE_NO])
);
GO

------------------------------------------------------------------------------
-- PAP_DEPOT
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_DEPOT] (
    [DEPOT_ID] nchar(10) NOT NULL,
    [DEPOT_NAME] nvarchar(50) NOT NULL,
    [TEL] nvarchar(50) NULL,
    [ADDRESS] nvarchar(60) NULL,
    [PRINCIPAL] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PAP_DEPOT] PRIMARY KEY CLUSTERED ([DEPOT_ID])
);
GO

------------------------------------------------------------------------------
-- PAP_GRAMME
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_GRAMME] (
    [GRAMME_ID] nchar(10) NOT NULL,
    [GRAMME_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_107147427] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PAP_GRAMME] PRIMARY KEY CLUSTERED ([GRAMME_ID])
);
GO

------------------------------------------------------------------------------
-- PAP_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_IN_M] (
    [BARCODE_NO] nchar(20) NOT NULL,
    [IN_DATE] datetime NULL,
    CONSTRAINT [PK_PAP_IN_M] PRIMARY KEY CLUSTERED ([BARCODE_NO])
);
GO

------------------------------------------------------------------------------
-- PAP_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_OUT_M] (
    [BARCODE_NO] nchar(20) NOT NULL,
    [OUT_DATE] datetime NULL,
    CONSTRAINT [PK_PAP_OUT_M] PRIMARY KEY CLUSTERED ([BARCODE_NO])
);
GO

------------------------------------------------------------------------------
-- PAP_SPECS
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_SPECS] (
    [SPECS_ID] nchar(10) NOT NULL,
    [SPECS_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_155147598] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PAP_SPECS] PRIMARY KEY CLUSTERED ([SPECS_ID])
);
GO

------------------------------------------------------------------------------
-- PAP_TYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAP_TYPE] (
    [TYPE_ID] nchar(10) NOT NULL,
    [TYPE_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TYPE_SORT] nchar(10) NULL,
    CONSTRAINT [PK_PAP_TYPE] PRIMARY KEY CLUSTERED ([TYPE_ID])
);
GO

------------------------------------------------------------------------------
-- PAYMENT
------------------------------------------------------------------------------
CREATE TABLE dbo.[PAYMENT] (
    [PAYMENT_ID] nchar(10) NOT NULL,
    [PAYMENT_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_187147712] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PAYMENT] PRIMARY KEY CLUSTERED ([PAYMENT_ID])
);
GO

------------------------------------------------------------------------------
-- PRICE
------------------------------------------------------------------------------
CREATE TABLE dbo.[PRICE] (
    [PRICE_ID] nchar(10) NOT NULL,
    [PRICE_NAME] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_203147769] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PRICE] PRIMARY KEY CLUSTERED ([PRICE_ID])
);
GO

------------------------------------------------------------------------------
-- PRO_HOLE
------------------------------------------------------------------------------
CREATE TABLE dbo.[PRO_HOLE] (
    [HOLE_SHAPE] nvarchar(20) NOT NULL,
    CONSTRAINT [PK_PRO_HOLE] PRIMARY KEY CLUSTERED ([HOLE_SHAPE])
);
GO

------------------------------------------------------------------------------
-- PRO_PROCESS
------------------------------------------------------------------------------
CREATE TABLE dbo.[PRO_PROCESS] (
    [PROCESS_ID] nchar(10) NOT NULL,
    [PROCESS_SORT] nchar(10) NULL,
    [PROCESS_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_267147997] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PRO_PROCESS] PRIMARY KEY CLUSTERED ([PROCESS_ID])
);
GO

------------------------------------------------------------------------------
-- PRO_PROCESS_SORT
------------------------------------------------------------------------------
CREATE TABLE dbo.[PRO_PROCESS_SORT] (
    [PROCESS_SORT] nvarchar(50) NOT NULL,
    CONSTRAINT [PK_PRO_PROCESS_SORT] PRIMARY KEY CLUSTERED ([PROCESS_SORT])
);
GO

------------------------------------------------------------------------------
-- PRO_WAVE
------------------------------------------------------------------------------
CREATE TABLE dbo.[PRO_WAVE] (
    [WAVE_ID] nchar(10) NOT NULL,
    [WAVE_NAME] nvarchar(50) NULL,
    [PAPER_THICK] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_299148111] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PRO_WAVE] PRIMARY KEY CLUSTERED ([WAVE_ID])
);
GO

------------------------------------------------------------------------------
-- PRODUCT
------------------------------------------------------------------------------
CREATE TABLE dbo.[PRODUCT] (
    [PRO_NO] nchar(30) NOT NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [SORT_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [UNIT_ID_1] nchar(10) NULL,
    [UNIT_RATE_1] float NULL,
    [UNIT_FORMULA_1] nvarchar(50) NULL,
    [UNIT_NARRATE_1] nvarchar(50) NULL,
    [UNIT_ID_2] nchar(10) NULL,
    [UNIT_RATE_2] float NULL,
    [UNIT_FORMULA_2] nvarchar(50) NULL,
    [UNIT_NARRATE_2] nvarchar(50) NULL,
    [UNIT_ID_3] nchar(10) NULL,
    [UNIT_RATE_3] float NULL,
    [UNIT_FORMULA_3] nvarchar(50) NULL,
    [UNIT_NARRATE_3] nvarchar(50) NULL,
    [UNIT_ID_4] nchar(10) NULL,
    [UNIT_RATE_4] float NULL,
    [UNIT_FORMULA_4] nvarchar(50) NULL,
    [UNIT_NARRATE_4] nvarchar(50) NULL,
    [MAIN_SOURCE] char(1) NULL,
    [PRO_TYPE] char(1) NULL,
    [WEIGHT_UNIT_ID] nchar(10) NULL,
    [SUTTLE] float NULL,
    [MANAGE_BATCH] bit NULL,
    [DEPOT_ID] nchar(10) NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [COLOR_ID] nchar(10) NULL,
    [STUFF_ID] nchar(10) NULL,
    [TYPE_ID] nchar(10) NULL,
    [PRE_DAYS] smallint NULL,
    [SAFETY_QTY] float NULL CONSTRAINT [DF_SAFETY_QTY_219147826] DEFAULT ((0)),
    [ADDING_QTY] float NULL CONSTRAINT [DF_ADDING_QTY_219147826] DEFAULT ((0)),
    [MAX_PURCHASE_PRICE] float NULL CONSTRAINT [DF_MAX_PURCHASE_PRICE_219147826] DEFAULT ((0)),
    [LAST_PURCHASE_PRICE] float NULL CONSTRAINT [DF_LAST_PURCHASE_PRICE_219147826] DEFAULT ((0)),
    [STA_PURCHASE_PRICE] float NULL CONSTRAINT [DF_STA_PURCHASE_PRICE_219147826] DEFAULT ((0)),
    [STA_SALE_PRICE] float NULL CONSTRAINT [DF_STA_SALE_PRICE_219147826] DEFAULT ((0)),
    [STUFF_COST] float NULL CONSTRAINT [DF_STUFF_COST_219147826] DEFAULT ((0)),
    [LABOUR_COST] float NULL CONSTRAINT [DF_LABOUR_COST_219147826] DEFAULT ((0)),
    [MAKE_COST] float NULL CONSTRAINT [DF_MAKE_COST_219147826] DEFAULT ((0)),
    [STA_LABOUR_FEE] float NULL CONSTRAINT [DF_STA_LABOUR_FEE_219147826] DEFAULT ((0)),
    [STA_MAKE_FEE] float NULL CONSTRAINT [DF_STA_MAKE_FEE_219147826] DEFAULT ((0)),
    [MAKE_FEE] float NULL CONSTRAINT [DF_MAKE_FEE_219147826] DEFAULT ((0)),
    [STA_FEE] float NULL CONSTRAINT [DF_STA_FEE_219147826] DEFAULT ((0)),
    [LENGTH] float NULL,
    [WIDTH] float NULL,
    [HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [CUBAGE] float NULL,
    [AREA] float NULL,
    [GROSS_WEIGHT] float NULL,
    [LOAD20_QTY] float NULL,
    [CUBAGE_UNIT_ID] nchar(10) NULL,
    [SINGLE_LENGTH] float NULL,
    [SINGLE_WIDTH] float NULL,
    [SINGLE_HEIGHT] float NULL,
    [BOX_SUTTLE] float NULL,
    [BOX_GROSS_WEIGHT] float NULL,
    [UNIT_PCS] float NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [CLIENT_PRO_NAME] nvarchar(100) NULL,
    [CLIENT_PRO_SPEC] nvarchar(100) NULL,
    [P_WIDTH] float NULL,
    [P_LENGTH] float NULL,
    [PS_LENGTH] float NULL CONSTRAINT [DF_PS_LENGTH_219147826] DEFAULT ((0)),
    [PS_WIDTH] float NULL CONSTRAINT [DF_PS_WIDTH_219147826] DEFAULT ((0)),
    [OM_LENGTH] float NULL CONSTRAINT [DF_OM_LENGTH_219147826] DEFAULT ((0)),
    [OM_WIDTH] float NULL,
    [BJ_LENGTH] smallint NULL,
    [BJ_WIDTH] smallint NULL,
    [SET_QTY] smallint NULL,
    [PS_LENGTH1] float NULL,
    [PS_WIDTH1] float NULL,
    [OM_LENGTH1] float NULL,
    [OM_WIDTH1] float NULL,
    [BJ_LENGTH1] smallint NULL,
    [BJ_WIDTH1] smallint NULL,
    [SET_QTY1] smallint NULL,
    [PS_LENGTH2] float NULL,
    [PS_WIDTH2] float NULL,
    [OM_LENGTH2] float NULL,
    [OM_WIDTH2] float NULL,
    [BJ_LENGTH2] smallint NULL,
    [BJ_WIDTH2] smallint NULL,
    [SET_QTY2] smallint NULL,
    [PS_LENGTH3] float NULL,
    [PS_WIDTH3] float NULL,
    [OM_LENGTH3] float NULL,
    [OM_WIDTH3] float NULL,
    [BJ_LENGTH3] smallint NULL,
    [BJ_WIDTH3] smallint NULL,
    [SET_QTY3] smallint NULL,
    [PS_LENGTH4] float NULL,
    [PS_WIDTH4] float NULL,
    [OM_LENGTH4] float NULL,
    [OM_WIDTH4] float NULL,
    [BJ_LENGTH4] smallint NULL,
    [BJ_WIDTH4] smallint NULL,
    [SET_QTY4] smallint NULL,
    [PS_LENGTH5] float NULL,
    [PS_WIDTH5] float NULL,
    [OM_LENGTH5] float NULL,
    [OM_WIDTH5] float NULL,
    [BJ_LENGTH5] smallint NULL,
    [BJ_WIDTH5] smallint NULL,
    [SET_QTY5] smallint NULL,
    [LIMB_HEIGHT1] float NULL,
    [BOX_HEIGHT] float NULL,
    [LIMB_HEIGHT2] float NULL,
    [PRODUCE_REMARK] nvarchar(300) NULL,
    [LAST_IN_DATE] datetime NULL,
    [LAST_OUT_DATE] datetime NULL,
    [LAST_CHECK_DATE] datetime NULL,
    [BUSINESS_TAG] bit NULL CONSTRAINT [DF_BUSINESS_TAG_219147826] DEFAULT ((0)),
    [PRODUCE_STUFF_ID] nchar(10) NULL,
    [KL_TYPE] nvarchar(50) NULL,
    [PRODUCE_ULLAGE] float NULL,
    [PRODUCE_SPEC] nvarchar(80) NULL,
    [PRODUCE_PICTURE] nvarchar(100) NULL,
    [SAMPLE_BOX_NO] nvarchar(50) NULL,
    [BIEMO_NO] nvarchar(50) NULL,
    [FIXED_BOARD_NO] nvarchar(50) NULL,
    [STICKINESS] bit NULL CONSTRAINT [DF_STICKINESS_219147826] DEFAULT ((0)),
    [SINGLE_NAIL] bit NULL CONSTRAINT [DF_SINGLE_NAIL_219147826] DEFAULT ((0)),
    [DOUBLE_NAIL] bit NULL CONSTRAINT [DF_DOUBLE_NAIL_219147826] DEFAULT ((0)),
    [NAIL_ORA] nvarchar(50) NULL CONSTRAINT [DF_NAIL_ORA_219147826] DEFAULT ((0)),
    [NP_INTENSITY] nvarchar(50) NULL,
    [NY_INTENSITY] nvarchar(50) NULL,
    [BY_INTENSITY] nvarchar(50) NULL,
    [JQ_INTENSITY] nvarchar(50) NULL,
    [ZH_INTENSITY] nvarchar(50) NULL,
    [WATER_RATE] nvarchar(50) NULL,
    [OTHER] nvarchar(100) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [EDITION] char(10) NULL,
    [COLOR1] nvarchar(50) NULL,
    [COLOR2] nvarchar(50) NULL,
    [COLOR3] nvarchar(50) NULL,
    [COLOR4] nvarchar(50) NULL,
    [LINE_ID] nchar(10) NULL,
    [WORK_1] nchar(2) NULL CONSTRAINT [DF_WORK_1_219147826] DEFAULT ((0)),
    [WORK_2] nchar(2) NULL CONSTRAINT [DF_WORK_2_219147826] DEFAULT ((0)),
    [WORK_3] nchar(2) NULL CONSTRAINT [DF_WORK_3_219147826] DEFAULT ((0)),
    [WORK_4] nchar(2) NULL CONSTRAINT [DF_WORK_4_219147826] DEFAULT ((0)),
    [WORK_5] nchar(2) NULL CONSTRAINT [DF_WORK_5_219147826] DEFAULT ((0)),
    [WORK_6] nchar(2) NULL CONSTRAINT [DF_WORK_6_219147826] DEFAULT ((0)),
    [WORK_7] nchar(2) NULL CONSTRAINT [DF_WORK_7_219147826] DEFAULT ((0)),
    [WORK_8] nchar(2) NULL CONSTRAINT [DF_WORK_8_219147826] DEFAULT ((0)),
    [WORK_9] nchar(2) NULL CONSTRAINT [DF_WORK_9_219147826] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_219147826] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PS_SPEC] nchar(10) NULL,
    [PS_SPEC1] nchar(10) NULL,
    [PS_SPEC2] nchar(10) NULL,
    [PS_SPEC3] nchar(10) NULL,
    [PS_SPEC4] nchar(10) NULL,
    [PS_SPEC5] nchar(10) NULL,
    [ELEMENT_PRO_NO] nchar(30) NULL CONSTRAINT [DF_ELEMENT_PRO_NO_219147826] DEFAULT (''),
    [LAST_TRADE_DATE] datetime NULL,
    [MRP_QTY] float NULL,
    [CLIENT_PRO_COLOR] nvarchar(50) NULL,
    [WORK_ALL] AS (((((((((((((((([WORK_1]+';')+[WORK_2])+';')+[WORK_3])+';')+[WORK_4])+';')+[WORK_5])+';')+[WORK_6])+';')+[WORK_7])+';')+[WORK_8])+';')+[WORK_9]),
    [NOT_SEND_QTY] float NULL,
    [NOT_IN_QTY] float NULL,
    [NOT_GET_QTY] float NULL,
    [IN_BUY_QTY] float NULL,
    [LINE_ID3] nchar(10) NULL,
    [LINE_ID2] nchar(10) NULL,
    [LINE_ID1] nchar(10) NULL,
    [PS_WIDTH_NEW] float NULL CONSTRAINT [DF_PS_WIDTH_NEW_219147826] DEFAULT ((0)),
    [PS_WIDTH_NEW1] float NULL CONSTRAINT [DF_PS_WIDTH_NEW1_219147826] DEFAULT ((0)),
    [PS_WIDTH_NEW2] float NULL CONSTRAINT [DF_PS_WIDTH_NEW2_219147826] DEFAULT ((0)),
    [PS_WIDTH_NEW3] float NULL CONSTRAINT [DF_PS_WIDTH_NEW3_219147826] DEFAULT ((0)),
    [PS_WIDTH_NEW4] float NULL CONSTRAINT [DF_PS_WIDTH_NEW4_219147826] DEFAULT ((0)),
    [PS_WIDTH_NEW5] float NULL CONSTRAINT [DF_PS_WIDTH_NEW5_219147826] DEFAULT ((0)),
    [MIN_PRODUCE_QTY] float NULL CONSTRAINT [DF_MIN_PRODUCE_QTY_219147826] DEFAULT ((0)),
    [ELEMENT_PRO_NO1] nchar(30) NULL CONSTRAINT [DF_ELEMENT_PRO_NO1_219147826] DEFAULT (''),
    [ELEMENT_PRO_NO2] nchar(30) NULL CONSTRAINT [DF_ELEMENT_PRO_NO2_219147826] DEFAULT (''),
    [ELEMENT_PRO_NO3] nchar(30) NULL CONSTRAINT [DF_ELEMENT_PRO_NO3_219147826] DEFAULT (''),
    [LAST_PURCHASE_UNIT_ID] nchar(10) NULL CONSTRAINT [DF_LAST_PURCHASE_UNIT_ID_219147826] DEFAULT (''),
    [LAST_PURCHASE_CURR_ID] nchar(10) NULL CONSTRAINT [DF_LAST_PURCHASE_CURR_ID_219147826] DEFAULT (''),
    [STOP_TAG] bit NULL CONSTRAINT [DF_STOP_TAG_219147826] DEFAULT ((0)),
    [PRO_ID] nvarchar(50) NULL,
    CONSTRAINT [PK_PRODUCT] PRIMARY KEY CLUSTERED ([PRO_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PRODUCT] ON dbo.[PRODUCT] ([CLIENT_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_PRODUCT_TYPE_NO_SORT] ON dbo.[PRODUCT] ([PRO_TYPE], [PRO_NO], [SORT_ID]);
GO

------------------------------------------------------------------------------
-- PRODUCT_EDITION
------------------------------------------------------------------------------
CREATE TABLE dbo.[PRODUCT_EDITION] (
    [PRO_NO] nchar(30) NOT NULL,
    [EDITION] char(10) NOT NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [SORT_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [UNIT_ID_1] nchar(10) NULL,
    [UNIT_RATE_1] float NULL,
    [UNIT_FORMULA_1] nvarchar(50) NULL,
    [UNIT_NARRATE_1] nvarchar(50) NULL,
    [UNIT_ID_2] nchar(10) NULL,
    [UNIT_RATE_2] float NULL,
    [UNIT_FORMULA_2] nvarchar(50) NULL,
    [UNIT_NARRATE_2] nvarchar(50) NULL,
    [UNIT_ID_3] nchar(10) NULL,
    [UNIT_RATE_3] float NULL,
    [UNIT_FORMULA_3] nvarchar(50) NULL,
    [UNIT_NARRATE_3] nvarchar(50) NULL,
    [UNIT_ID_4] nchar(10) NULL,
    [UNIT_RATE_4] float NULL,
    [UNIT_FORMULA_4] nvarchar(50) NULL,
    [UNIT_NARRATE_4] nvarchar(50) NULL,
    [MAIN_SOURCE] char(1) NULL,
    [PRO_TYPE] char(1) NULL,
    [WEIGHT_UNIT_ID] nchar(10) NULL,
    [SUTTLE] float NULL,
    [MANAGE_BATCH] bit NULL,
    [DEPOT_ID] nchar(10) NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [COLOR_ID] nchar(10) NULL,
    [STUFF_ID] nchar(10) NULL,
    [TYPE_ID] nchar(10) NULL,
    [PRE_DAYS] smallint NULL CONSTRAINT [DF_PRE_DAYS_235147883] DEFAULT ((0)),
    [SAFETY_QTY] float NULL CONSTRAINT [DF_SAFETY_QTY_235147883] DEFAULT ((0)),
    [ADDING_QTY] float NULL CONSTRAINT [DF_ADDING_QTY_235147883] DEFAULT ((0)),
    [MAX_PURCHASE_PRICE] float NULL,
    [LAST_PURCHASE_PRICE] float NULL,
    [STA_PURCHASE_PRICE] float NULL,
    [STA_SALE_PRICE] float NULL,
    [STUFF_COST] float NULL,
    [LABOUR_COST] float NULL,
    [MAKE_COST] float NULL,
    [STA_LABOUR_FEE] float NULL,
    [STA_MAKE_FEE] float NULL,
    [MAKE_FEE] float NULL,
    [STA_FEE] float NULL,
    [LENGTH] float NULL,
    [WIDTH] float NULL,
    [HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [CUBAGE] float NULL,
    [AREA] float NULL,
    [GROSS_WEIGHT] float NULL,
    [LOAD20_QTY] float NULL,
    [CUBAGE_UNIT_ID] nchar(10) NULL,
    [SINGLE_LENGTH] float NULL,
    [SINGLE_WIDTH] float NULL,
    [SINGLE_HEIGHT] float NULL,
    [BOX_SUTTLE] float NULL,
    [BOX_GROSS_WEIGHT] float NULL,
    [UNIT_PCS] float NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [CLIENT_PRO_NAME] nvarchar(100) NULL,
    [CLIENT_PRO_SPEC] nvarchar(100) NULL,
    [P_WIDTH] float NULL,
    [P_LENGTH] float NULL,
    [PS_LENGTH] float NULL,
    [PS_WIDTH] float NULL,
    [OM_LENGTH] float NULL,
    [OM_WIDTH] float NULL,
    [BJ_LENGTH] smallint NULL,
    [BJ_WIDTH] smallint NULL,
    [SET_QTY] smallint NULL,
    [PS_LENGTH1] float NULL,
    [PS_WIDTH1] float NULL,
    [OM_LENGTH1] float NULL,
    [OM_WIDTH1] float NULL,
    [BJ_LENGTH1] smallint NULL,
    [BJ_WIDTH1] smallint NULL,
    [SET_QTY1] smallint NULL,
    [PS_LENGTH2] float NULL,
    [PS_WIDTH2] float NULL,
    [OM_LENGTH2] float NULL,
    [OM_WIDTH2] float NULL,
    [BJ_LENGTH2] smallint NULL,
    [BJ_WIDTH2] smallint NULL,
    [SET_QTY2] smallint NULL,
    [PS_LENGTH3] float NULL,
    [PS_WIDTH3] float NULL,
    [OM_LENGTH3] float NULL,
    [OM_WIDTH3] float NULL,
    [BJ_LENGTH3] smallint NULL,
    [BJ_WIDTH3] smallint NULL,
    [SET_QTY3] smallint NULL,
    [PS_LENGTH4] float NULL,
    [PS_WIDTH4] float NULL,
    [OM_LENGTH4] float NULL,
    [OM_WIDTH4] float NULL,
    [BJ_LENGTH4] smallint NULL,
    [BJ_WIDTH4] smallint NULL,
    [SET_QTY4] smallint NULL,
    [PS_LENGTH5] float NULL,
    [PS_WIDTH5] float NULL,
    [OM_LENGTH5] float NULL,
    [OM_WIDTH5] float NULL,
    [BJ_LENGTH5] smallint NULL,
    [BJ_WIDTH5] smallint NULL,
    [SET_QTY5] smallint NULL,
    [LIMB_HEIGHT1] float NULL,
    [BOX_HEIGHT] float NULL,
    [LIMB_HEIGHT2] float NULL,
    [PRODUCE_REMARK] nvarchar(300) NULL,
    [LAST_IN_DATE] datetime NULL,
    [LAST_OUT_DATE] datetime NULL,
    [LAST_CHECK_DATE] datetime NULL,
    [BUSINESS_TAG] bit NULL,
    [PRODUCE_STUFF_ID] nchar(10) NULL,
    [KL_TYPE] nvarchar(50) NULL,
    [PRODUCE_ULLAGE] float NULL,
    [PRODUCE_SPEC] nvarchar(80) NULL,
    [PRODUCE_PICTURE] nvarchar(100) NULL,
    [SAMPLE_BOX_NO] nvarchar(50) NULL,
    [BIEMO_NO] nvarchar(50) NULL,
    [FIXED_BOARD_NO] nvarchar(50) NULL,
    [STICKINESS] bit NULL,
    [SINGLE_NAIL] bit NULL,
    [DOUBLE_NAIL] bit NULL,
    [NAIL_ORA] nvarchar(50) NULL,
    [APPLY_MATERIEL_NO] nvarchar(100) NULL,
    [NP_INTENSITY] nvarchar(50) NULL,
    [NY_INTENSITY] nvarchar(50) NULL,
    [BY_INTENSITY] nvarchar(50) NULL,
    [JQ_INTENSITY] nvarchar(50) NULL,
    [ZH_INTENSITY] nvarchar(50) NULL,
    [WATER_RATE] nvarchar(50) NULL,
    [OTHER] nvarchar(100) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [COLOR1] nvarchar(50) NULL,
    [COLOR2] nvarchar(50) NULL,
    [COLOR3] nvarchar(50) NULL,
    [COLOR4] nvarchar(50) NULL,
    [LINE_ID] nchar(10) NULL,
    [WORK_1] nchar(2) NULL CONSTRAINT [DF_WORK_1_235147883] DEFAULT ((0)),
    [WORK_2] nchar(2) NULL CONSTRAINT [DF_WORK_2_235147883] DEFAULT ((0)),
    [WORK_3] nchar(2) NULL CONSTRAINT [DF_WORK_3_235147883] DEFAULT ((0)),
    [WORK_4] nchar(2) NULL CONSTRAINT [DF_WORK_4_235147883] DEFAULT ((0)),
    [WORK_5] nchar(2) NULL CONSTRAINT [DF_WORK_5_235147883] DEFAULT ((0)),
    [WORK_6] nchar(2) NULL CONSTRAINT [DF_WORK_6_235147883] DEFAULT ((0)),
    [WORK_7] nchar(2) NULL CONSTRAINT [DF_WORK_7_235147883] DEFAULT ((0)),
    [WORK_8] nchar(2) NULL CONSTRAINT [DF_WORK_8_235147883] DEFAULT ((0)),
    [WORK_9] nchar(2) NULL CONSTRAINT [DF_WORK_9_235147883] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_235147883] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PS_SPEC] nchar(10) NULL,
    [PS_SPEC1] nchar(10) NULL,
    [PS_SPEC2] nchar(10) NULL,
    [PS_SPEC3] nchar(10) NULL,
    [PS_SPEC4] nchar(10) NULL,
    [PS_SPEC5] nchar(10) NULL,
    [LINE_ID3] nchar(10) NULL,
    [LINE_ID2] nchar(10) NULL,
    [LINE_ID1] nchar(10) NULL,
    [PS_WIDTH_NEW] float NULL CONSTRAINT [DF_PS_WIDTH_NEW_235147883] DEFAULT ((0)),
    [PS_WIDTH_NEW1] float NULL CONSTRAINT [DF_PS_WIDTH_NEW1_235147883] DEFAULT ((0)),
    [PS_WIDTH_NEW2] float NULL CONSTRAINT [DF_PS_WIDTH_NEW2_235147883] DEFAULT ((0)),
    [PS_WIDTH_NEW3] float NULL CONSTRAINT [DF_PS_WIDTH_NEW3_235147883] DEFAULT ((0)),
    [PS_WIDTH_NEW4] float NULL CONSTRAINT [DF_PS_WIDTH_NEW4_235147883] DEFAULT ((0)),
    [PS_WIDTH_NEW5] float NULL CONSTRAINT [DF_PS_WIDTH_NEW5_235147883] DEFAULT ((0)),
    [MIN_PRODUCE_QTY] float NULL CONSTRAINT [DF_MIN_PRODUCE_QTY_235147883] DEFAULT ((0)),
    CONSTRAINT [PK_PRODUCT_EDITION] PRIMARY KEY CLUSTERED ([PRO_NO], [EDITION])
);
GO
CREATE NONCLUSTERED INDEX [IX_PRODUCT_EDITION] ON dbo.[PRODUCT_EDITION] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- PUR_APPLY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_APPLY_D] (
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [USED_DATE] datetime NULL,
    [DEPOT_ID] nchar(10) NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [PURCHASE_QTY] float NULL CONSTRAINT [DF_PURCHASE_QTY_315148168] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_315148168] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_315148168] DEFAULT ((0)),
    [REMARK] nvarchar(300) NULL,
    [PLAN_DELIVERY_DATE] datetime NULL,
    [PURCHASE_TYPE] nchar(10) NULL,
    [PURCHASE_SERIAL_NO] smallint NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_315148168] DEFAULT ((0)),
    [CTN_QTY] float NULL CONSTRAINT [DF_CTN_QTY_315148168] DEFAULT ((0)),
    [QTY] float NULL CONSTRAINT [DF_QTY_315148168] DEFAULT ((0)),
    [PURCHASE_NO] nchar(20) NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [REQUIRE_QTY] float NULL,
    [PLAN_TYPE] nchar(10) NULL,
    [PLAN_NO] nchar(20) NULL,
    [PLAN_SERIAL_NO] int NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [SUPPLIER_PRO_NO] nvarchar(50) NULL,
    [ADVISEVT_QTY] float NULL,
    [LOST_QTY] float NULL CONSTRAINT [DF_LOST_QTY_315148168] DEFAULT ((0)),
    [DUO_REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_PUR_APPLY_D] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_APPLY_D_1] ON dbo.[PUR_APPLY_D] ([PURCHASE_TYPE], [PURCHASE_NO], [PURCHASE_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_APPLY_D_2] ON dbo.[PUR_APPLY_D] ([SUPPLIER_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_APPLY_D] ON dbo.[PUR_APPLY_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- PUR_APPLY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_APPLY_M] (
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    [APPLY_DATE] datetime NULL,
    [DEPT_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_331148225] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_331148225] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [ORDER_NO] nvarchar(300) NULL,
    [PRODUCE_NO] nvarchar(300) NULL,
    CONSTRAINT [PK_PUR_APPLY_M] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_APPLY_M] ON dbo.[PUR_APPLY_M] ([EMP_ID]);
GO

------------------------------------------------------------------------------
-- PUR_APPLY_MORE
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_APPLY_MORE] (
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [REQUIRE_QTY] float NULL CONSTRAINT [DF_REQUIRE_QTY_347148282] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [REMARK] nvarchar(200) NULL,
    [LOST_QTY] float NULL,
    CONSTRAINT [PK_PUR_APPLY_MORE] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_CALLBACK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_CALLBACK_D] (
    [CALLBACK_TYPE] nchar(10) NOT NULL,
    [CALLBACK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [S_R_TYPE] nchar(10) NULL,
    [S_R_NO] nchar(20) NULL,
    [S_R_SERIAL_NO] smallint NULL,
    [RECEIVE_TYPE] nchar(10) NULL,
    [RECEIVE_NO] nchar(20) NULL,
    [RECEIVE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL,
    [SPARE_QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [PURCHASE_TYPE] nchar(10) NULL,
    [PURCHASE_NO] nchar(20) NULL,
    [PURCHASE_SERIAL_NO] smallint NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_363148339] DEFAULT ((100)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_363148339] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [RECOUP_MONEY] float NULL CONSTRAINT [DF_RECOUP_MONEY_363148339] DEFAULT ((0)),
    [RECOUP_NOTE] nvarchar(50) NULL,
    CONSTRAINT [PK_PUR_CALLBACK_D] PRIMARY KEY CLUSTERED ([CALLBACK_TYPE], [CALLBACK_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_CALLBACK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_CALLBACK_M] (
    [CALLBACK_TYPE] nchar(10) NOT NULL,
    [CALLBACK_NO] nchar(20) NOT NULL,
    [CALLBACK_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_379148396] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_379148396] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PUR_CALLBACK_M] PRIMARY KEY CLUSTERED ([CALLBACK_TYPE], [CALLBACK_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_CANCEL_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_CANCEL_D] (
    [CANCEL_TYPE] nchar(10) NOT NULL,
    [CANCEL_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_395148453] DEFAULT ((0)),
    [QTY] float NULL CONSTRAINT [DF_QTY_395148453] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [RECEIVE_TYPE] nchar(10) NULL,
    [RECEIVE_NO] nchar(20) NULL,
    [RECEIVE_SERIAL_NO] smallint NULL,
    [PURCHASE_TYPE] nchar(10) NULL,
    [PURCHASE_NO] nchar(20) NULL,
    [PURCHASE_SERIAL_NO] smallint NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_395148453] DEFAULT ((0)),
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_395148453] DEFAULT ('O'),
    [SPARE_QTY] float NULL CONSTRAINT [DF_SPARE_QTY_395148453] DEFAULT ((0)),
    [FINISHED_AMOUNT] float NULL CONSTRAINT [DF_FINISHED_AMOUNT_395148453] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_395148453] DEFAULT ((0)),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_395148453] DEFAULT ((100)),
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [SUPPLIER_PRO_NO] nvarchar(50) NULL,
    CONSTRAINT [PK_PUR_CANCEL_D] PRIMARY KEY CLUSTERED ([CANCEL_TYPE], [CANCEL_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_CANCEL_D] ON dbo.[PUR_CANCEL_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- PUR_CANCEL_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_CANCEL_M] (
    [CANCEL_TYPE] nchar(10) NOT NULL,
    [CANCEL_NO] nchar(20) NOT NULL,
    [CANCEL_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_411148510] DEFAULT ((0)),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_411148510] DEFAULT ((0)),
    [TAX_SUM] float NULL CONSTRAINT [DF_TAX_SUM_411148510] DEFAULT ((0)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_411148510] DEFAULT ((0)),
    [DEPOT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_411148510] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_411148510] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_411148510] DEFAULT ('O'),
    [SEND_TAG] bit NULL CONSTRAINT [DF_SEND_TAG_411148510] DEFAULT ((0)),
    CONSTRAINT [PK_PUR_CANCEL_M] PRIMARY KEY CLUSTERED ([CANCEL_TYPE], [CANCEL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_CANCEL_M] ON dbo.[PUR_CANCEL_M] ([SUPPLIER_ID]);
GO

------------------------------------------------------------------------------
-- PUR_CHAFFER_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_CHAFFER_D] (
    [CHAFFER_TYPE] nchar(10) NOT NULL,
    [CHAFFER_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [PRO_STUFF] nvarchar(100) NULL,
    [PRO_SIZE] nvarchar(100) NULL,
    [PRO_COLOR] nvarchar(100) NULL,
    [PRO_LENGTH] float NULL,
    [PRO_WIDTH] float NULL,
    [PRO_HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL CONSTRAINT [DF_CURR_RATE_427148567] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_427148567] DEFAULT ((0)),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL CONSTRAINT [DF_TAX_SUM_427148567] DEFAULT ((0)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_427148567] DEFAULT ((0)),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_427148567] DEFAULT ((100)),
    [SUPPLIER_PRO_NO] nvarchar(50) NULL,
    [OLD_PRICE] float NULL CONSTRAINT [DF_OLD_PRICE_427148567] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_427148567] DEFAULT ('1'),
    [P_LENGTH] float NULL,
    [P_WIDTH] float NULL,
    [MIN_PRICE] float NULL,
    [PARAMETER_PRICE] float NULL,
    [LOST_RATE] float NULL,
    [MOULD_SUM] float NULL,
    [PRINTING_SUM] float NULL,
    [PARAMETER_QTY] float NULL,
    [PROCESS_PRICE] float NULL,
    CONSTRAINT [PK_PUR_CHAFFER_D] PRIMARY KEY CLUSTERED ([CHAFFER_TYPE], [CHAFFER_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_CHAFFER_D] ON dbo.[PUR_CHAFFER_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- PUR_CHAFFER_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_CHAFFER_M] (
    [CHAFFER_TYPE] nchar(10) NOT NULL,
    [CHAFFER_NO] nchar(20) NOT NULL,
    [CHAFFER_DATE] datetime NOT NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [SUPPLIER_NAME_CN] nvarchar(100) NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_443148624] DEFAULT ((0)),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_443148624] DEFAULT ((0)),
    [TAX_SUM] float NULL CONSTRAINT [DF_TAX_SUM_443148624] DEFAULT ((0)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_443148624] DEFAULT ((0)),
    [PRICE_CONDITION] nvarchar(50) NULL,
    [PAYMENT_CONDITION] nvarchar(50) NULL,
    [DELIVERY_DATE] datetime NULL,
    [IN_EFFECT_DATE] datetime NULL,
    [SUPPLIER_CONFIRM] nvarchar(100) NULL CONSTRAINT [DF_SUPPLIER_CONFIRM_443148624] DEFAULT ('N'),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_443148624] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_443148624] DEFAULT ('O'),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_443148624] DEFAULT ((100)),
    CONSTRAINT [PK_PUR_CHAFFER_M] PRIMARY KEY CLUSTERED ([CHAFFER_TYPE], [CHAFFER_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_CHAFFER_M] ON dbo.[PUR_CHAFFER_M] ([SUPPLIER_ID]);
GO

------------------------------------------------------------------------------
-- PUR_DUE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_DUE_D] (
    [DUE_TYPE] nchar(10) NOT NULL,
    [DUE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [R_C_TYPE] nchar(10) NULL,
    [R_C_NO] nchar(20) NULL,
    [R_C_SERIAL_NO] smallint NULL,
    [REMARK] nvarchar(100) NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_459148681] DEFAULT ((0)),
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL CONSTRAINT [DF_CURR_RATE_459148681] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_459148681] DEFAULT ((0)),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [PURCHASE_TYPE] nchar(10) NULL,
    [PURCHASE_NO] nchar(20) NULL,
    [PURCHASE_SERIAL_NO] smallint NULL,
    [RECEIVE_TYPE] nchar(10) NULL,
    [RECEIVE_NO] nchar(20) NULL,
    [RECEIVE_SERIAL_NO] smallint NULL,
    [R_C_DATE] datetime NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_459148681] DEFAULT ((100)),
    [SUPPLIER_PRO_NO] nvarchar(50) NULL,
    [UNIT_ID] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_459148681] DEFAULT ('O'),
    CONSTRAINT [PK_PUR_DUE_D] PRIMARY KEY CLUSTERED ([DUE_TYPE], [DUE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_DUE_D_1] ON dbo.[PUR_DUE_D] ([PURCHASE_TYPE], [PURCHASE_NO], [PURCHASE_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_DUE_D] ON dbo.[PUR_DUE_D] ([R_C_TYPE], [R_C_NO], [R_C_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_DUE_D_2] ON dbo.[PUR_DUE_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- PUR_DUE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_DUE_M] (
    [DUE_TYPE] nchar(10) NOT NULL,
    [DUE_NO] nchar(20) NOT NULL,
    [DUE_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_475148738] DEFAULT ((0)),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL CONSTRAINT [DF_TAX_SUM_475148738] DEFAULT ((0)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_475148738] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_475148738] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_475148738] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [INVOICE_NO] nvarchar(50) NULL,
    [INVOICE_DATE] datetime NULL,
    [PRE_PAYOUT_DATE] datetime NULL,
    [OTHER_PRICE] float NULL,
    [SUM_AMOUNT] float NULL CONSTRAINT [DF_SUM_AMOUNT_475148738] DEFAULT ((0)),
    [PAYOUT_AMOUNT] float NULL CONSTRAINT [DF_PAYOUT_AMOUNT_475148738] DEFAULT ((0)),
    [FACT_PAYOUT_DATE] datetime NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_475148738] DEFAULT ('O'),
    [INVOICE_SUM] float NULL,
    [DUE_MONTH] nchar(6) NULL,
    [QTY_TOTAL] float NULL,
    [BEGIN_DATE] datetime NULL,
    [END_DATE] datetime NULL,
    CONSTRAINT [PK_PUR_DUE_M] PRIMARY KEY CLUSTERED ([DUE_TYPE], [DUE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_DUE_M] ON dbo.[PUR_DUE_M] ([SUPPLIER_ID]);
GO

------------------------------------------------------------------------------
-- PUR_MONTH_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_MONTH_D] (
    [MONTH_TYPE] nchar(10) NOT NULL,
    [MONTH_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [PERIOD_AMOUNT] float NULL,
    [CURRENT_PREPAY] float NULL,
    [CURRENT_ACCOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CURRENT_BALANCE] float NULL CONSTRAINT [DF_CURRENT_BALANCE_491148795] DEFAULT ((0)),
    [CURRENT_REBATE] float NULL CONSTRAINT [DF_CURRENT_REBATE_491148795] DEFAULT ((0)),
    [CURRENT_PAY] float NULL,
    CONSTRAINT [PK_PUR_MONTH_D] PRIMARY KEY CLUSTERED ([MONTH_TYPE], [MONTH_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_MONTH_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_MONTH_M] (
    [MONTH_TYPE] nchar(10) NOT NULL,
    [MONTH_NO] nchar(20) NOT NULL,
    [MONTH_DATE] datetime NULL,
    [COUNT_MONTH] nchar(6) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_507148852] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PUR_MONTH_M] PRIMARY KEY CLUSTERED ([MONTH_TYPE], [MONTH_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_PACK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PACK_M] (
    [PACK_TYPE] nchar(10) NOT NULL,
    [PACK_NO] nchar(20) NOT NULL,
    [PACK_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [PRO_NO] nchar(30) NULL,
    [PRE_SEND_DATE] datetime NULL,
    [BOX_STUFF] nchar(10) NULL,
    [BOX_SPEC] nchar(50) NULL,
    [BOX_QTY] float NULL,
    [PK_STUFF] nchar(10) NULL,
    [PK_SPEC] nchar(50) NULL,
    [PK_QTY] float NULL,
    [FRONT] nvarchar(500) NULL,
    [SIDE] nvarchar(500) NULL,
    [LINK] nvarchar(500) NULL,
    [PRINTING] nvarchar(200) NULL,
    [OUT_BOX1] bit NULL,
    [OUT_BOX2] bit NULL,
    [OUT_BOX3] bit NULL,
    [OUT_BOX4] bit NULL,
    [OUT_BOX5] bit NULL,
    [OUT_BOX6] bit NULL,
    [OUT_BOX7] bit NULL,
    [OUT_BOX8] bit NULL,
    [OUT_BOX9] bit NULL,
    [OUT_BOX10] bit NULL,
    [OUT_BOX11] bit NULL,
    [OUT_BOX12] bit NULL,
    [OUT_BOX13] bit NULL,
    [OUT_BOX14] bit NULL,
    [OUT_BOX15] bit NULL,
    [OUT_REMARK] nchar(30) NULL,
    [IN_BOX1] bit NULL,
    [IN_BOX2] bit NULL,
    [IN_BOX3] bit NULL,
    [IN_BOX4] bit NULL,
    [IN_BOX5] bit NULL,
    [IN_BOX6] bit NULL,
    [IN_BOX7] bit NULL,
    [IN_BOX8] bit NULL,
    [IN_BOX9] bit NULL,
    [IN_BOX10] bit NULL,
    [IN_BOX11] bit NULL,
    [IN_BOX12] bit NULL,
    [IN_BOX13] bit NULL,
    [IN_BOX14] bit NULL,
    [IN_BOX15] bit NULL,
    [IN_REMARK] nchar(30) NULL,
    [S_BOX1] bit NULL,
    [S_BOX2] bit NULL,
    [S_BOX3] bit NULL,
    [S_BOX4] bit NULL,
    [S_BOX5] bit NULL,
    [S_BOX6] bit NULL,
    [S_BOX7] bit NULL,
    [S_BOX8] bit NULL,
    [S_BOX9] bit NULL,
    [S_BOX10] bit NULL,
    [S_BOX11] bit NULL,
    [S_BOX12] bit NULL,
    [S_BOX13] bit NULL,
    [S_BOX14] bit NULL,
    [S_BOX15] bit NULL,
    [S_REMARK] nchar(30) NULL,
    [L_BOX1] bit NULL,
    [L_BOX2] bit NULL,
    [L_BOX3] bit NULL,
    [L_BOX4] bit NULL,
    [L_BOX5] bit NULL,
    [L_BOX6] bit NULL,
    [L_BOX7] bit NULL,
    [L_BOX8] bit NULL,
    [L_BOX9] bit NULL,
    [L_BOX10] bit NULL,
    [L_BOX11] bit NULL,
    [L_BOX12] bit NULL,
    [L_BOX13] bit NULL,
    [L_BOX14] bit NULL,
    [L_BOX15] bit NULL,
    [L_REMARK] nchar(30) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_523148909] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL CONSTRAINT [DF_CURR_RATE_523148909] DEFAULT ((0)),
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_523148909] DEFAULT ('O'),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_523148909] DEFAULT ((0)),
    [BOX_PRICE] float NULL CONSTRAINT [DF_BOX_PRICE_523148909] DEFAULT ((0)),
    [BOX_AMOUNT] float NULL CONSTRAINT [DF_BOX_AMOUNT_523148909] DEFAULT ((0)),
    [PK_PRICE] float NULL,
    [PK_AMOUNT] float NULL CONSTRAINT [DF_PK_AMOUNT_523148909] DEFAULT ((0)),
    [BOX_PRO_NO] nchar(30) NULL,
    [FINISHED_BOX_QTY] float NULL,
    [PK_PRO_NO] nchar(30) NULL,
    [FINISHED_PK_QTY] float NULL,
    [RECEIVE_TYPE] nchar(10) NULL,
    [RECEIVE_NO] nchar(20) NULL,
    [RECEIVE_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_PUR_PACK_M] PRIMARY KEY CLUSTERED ([PACK_TYPE], [PACK_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_PAY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PAY_D] (
    [PAY_TYPE] nchar(10) NOT NULL,
    [PAY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [DUE_TYPE] nchar(10) NULL,
    [DUE_NO] nchar(20) NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_539148966] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [PAYOUT_AMOUNT] float NULL CONSTRAINT [DF_PAYOUT_AMOUNT_539148966] DEFAULT ((0)),
    CONSTRAINT [PK_PUR_PAY_D] PRIMARY KEY CLUSTERED ([PAY_TYPE], [PAY_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_PAY_D] ON dbo.[PUR_PAY_D] ([DUE_TYPE], [DUE_NO]);
GO

------------------------------------------------------------------------------
-- PUR_PAY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PAY_M] (
    [PAY_TYPE] nchar(10) NOT NULL,
    [PAY_NO] nchar(20) NOT NULL,
    [PAY_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_555149023] DEFAULT ((0)),
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_555149023] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_555149023] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [RECEIVE_ID] nchar(10) NULL,
    [BANK_ID] nchar(30) NULL,
    [PAYOUT_ACCOUNT_ID] nchar(20) NULL,
    [REBATE_ACCOUNT_ID] nchar(20) NULL,
    [PAYOUT_SUM] float NULL CONSTRAINT [DF_PAYOUT_SUM_555149023] DEFAULT ((0)),
    [PREPAY_SUM] float NULL CONSTRAINT [DF_PREPAY_SUM_555149023] DEFAULT ((0)),
    [REBATE_SUM] float NULL CONSTRAINT [DF_REBATE_SUM_555149023] DEFAULT ((0)),
    [ACCOUNT_TYPE_ID] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_555149023] DEFAULT ('O'),
    CONSTRAINT [PK_PUR_PAY_M] PRIMARY KEY CLUSTERED ([PAY_TYPE], [PAY_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_PAY_M] ON dbo.[PUR_PAY_M] ([SUPPLIER_ID]);
GO

------------------------------------------------------------------------------
-- PUR_PAY_OTHER
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PAY_OTHER] (
    [PAY_TYPE] nchar(10) NOT NULL,
    [PAY_NO] nchar(20) NOT NULL,
    [PAY_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [SUPPLIER_NAME] nvarchar(100) NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [PAY_DESC] nvarchar(200) NULL,
    [RECEIVE_ID] nchar(10) NULL,
    [BANK_ID] nchar(30) NULL,
    [PAYOUT_ACCOUNT_ID] nchar(20) NULL,
    [REBATE_ACCOUNT_ID] nchar(20) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_571149080] DEFAULT ((0)),
    [AMOUNT_WORD] nvarchar(200) NULL,
    [PAYOUT_SUM] float NULL CONSTRAINT [DF_PAYOUT_SUM_571149080] DEFAULT ((0)),
    [PREPAY_SUM] float NULL CONSTRAINT [DF_PREPAY_SUM_571149080] DEFAULT ((0)),
    [REBATE_SUM] float NULL CONSTRAINT [DF_REBATE_SUM_571149080] DEFAULT ((0)),
    [PREPAY_TYPE] nchar(10) NULL,
    [PREPAY_NO] nchar(20) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_571149080] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_571149080] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [ACCOUNT_TYPE_ID] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_571149080] DEFAULT ('O'),
    CONSTRAINT [PK_PUR_PAY_OTHER] PRIMARY KEY CLUSTERED ([PAY_TYPE], [PAY_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_PAY_OTHER] ON dbo.[PUR_PAY_OTHER] ([SUPPLIER_ID]);
GO

------------------------------------------------------------------------------
-- PUR_PAY_OTHER_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PAY_OTHER_D] (
    [PAY_TYPE] nchar(10) NOT NULL,
    [PAY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [SUMMARY] nvarchar(100) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_PUR_PAY_OTHER_D] PRIMARY KEY CLUSTERED ([PAY_TYPE], [PAY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_PAY_PREPAY
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PAY_PREPAY] (
    [PAY_TYPE] nchar(10) NOT NULL,
    [PAY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PREPAY_TYPE] nchar(10) NULL,
    [PREPAY_NO] nchar(20) NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_603149194] DEFAULT ((0)),
    [PREPAY_AMOUNT] float NULL CONSTRAINT [DF_PREPAY_AMOUNT_603149194] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_PUR_PAY_PREPAY] PRIMARY KEY CLUSTERED ([PAY_TYPE], [PAY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_PREPAY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PREPAY_D] (
    [PREPAY_TYPE] nchar(10) NOT NULL,
    [PREPAY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [RECEIVE_ACCOUNT_ID] nchar(20) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_619149251] DEFAULT ((0)),
    [BILL_NO] nvarchar(50) NULL,
    [AT_TERM_DATE] datetime NULL,
    [REMARK] nvarchar(50) NULL,
    [ACCOUNT_TYPE_ID] nchar(10) NULL,
    [PURCHASE_TYPE] nchar(10) NULL,
    [PURCHASE_NO] nchar(20) NULL,
    [PURCHASE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_619149251] DEFAULT ((0)),
    [PRICE] float NULL CONSTRAINT [DF_PRICE_619149251] DEFAULT ((0)),
    CONSTRAINT [PK_PUR_PREPAY_D] PRIMARY KEY CLUSTERED ([PREPAY_TYPE], [PREPAY_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_PREPAY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PREPAY_M] (
    [PREPAY_TYPE] nchar(10) NOT NULL,
    [PREPAY_NO] nchar(20) NOT NULL,
    [PREPAY_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [RECEIVE_ID] nchar(10) NULL,
    [BANK_ID] nchar(30) NULL,
    [RECEIVE_ACCOUNT_ID] nchar(20) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_635149308] DEFAULT ((0)),
    [PREPAY_AMOUNT] float NULL CONSTRAINT [DF_PREPAY_AMOUNT_635149308] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_635149308] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [ACCOUNT_TYPE_ID] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_635149308] DEFAULT ((0)),
    CONSTRAINT [PK_PUR_PREPAY_M] PRIMARY KEY CLUSTERED ([PREPAY_TYPE], [PREPAY_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_PURCHASE_CHANGE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PURCHASE_CHANGE_D] (
    [CHANGE_PURCHASE_TYPE] nchar(10) NOT NULL,
    [CHANGE_PURCHASE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PURCHASE_TYPE] nchar(10) NULL,
    [PURCHASE_NO] nchar(20) NULL,
    [PURCHASE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_651149365] DEFAULT ((0)),
    [PRICE] float NULL CONSTRAINT [DF_PRICE_651149365] DEFAULT ((0)),
    [PLAN_DELIVERY_DATE] datetime NULL,
    [OLD_QTY] float NULL CONSTRAINT [DF_OLD_QTY_651149365] DEFAULT ((0)),
    [OLD_PRICE] float NULL CONSTRAINT [DF_OLD_PRICE_651149365] DEFAULT ((0)),
    [OLD_PLAN_DELIVERY_DATE] datetime NULL,
    [REMARK] nvarchar(100) NULL,
    [SPARE_QTY] float NULL CONSTRAINT [DF_SPARE_QTY_651149365] DEFAULT ((0)),
    [RECEIVE_QTY] float NULL,
    [RECEIVE_SPARE_QTY] float NULL CONSTRAINT [DF_RECEIVE_SPARE_QTY_651149365] DEFAULT ((0)),
    [OLD_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_SPARE_QTY_651149365] DEFAULT ((0)),
    [OLD_RECEIVE_QTY] float NULL,
    [OLD_RECEIVE_SPARE_QTY] float NULL CONSTRAINT [DF_OLD_RECEIVE_SPARE_QTY_651149365] DEFAULT ((0)),
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    CONSTRAINT [PK_PUR_PURCHASE_CHANGE_D] PRIMARY KEY CLUSTERED ([CHANGE_PURCHASE_TYPE], [CHANGE_PURCHASE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_CHANGE_PURCHASE_D] ON dbo.[PUR_PURCHASE_CHANGE_D] ([PURCHASE_TYPE], [PURCHASE_NO], [PURCHASE_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- PUR_PURCHASE_CHANGE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PURCHASE_CHANGE_M] (
    [CHANGE_PURCHASE_TYPE] nchar(10) NOT NULL,
    [CHANGE_PURCHASE_NO] nchar(20) NOT NULL,
    [CHANGE_PURCHASE_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [PURCHASE_TYPE] nchar(10) NULL,
    [PURCHASE_NO] nchar(20) NULL,
    [PRICE_CONDITION] nchar(10) NULL,
    [PAY_CONDITION] nchar(10) NULL,
    [DELIVERY_ADDRESS] nvarchar(80) NULL,
    [RECKONING_ADDRESS] nvarchar(80) NULL,
    [BILL_ADDRESS] nvarchar(80) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_667149422] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_PUR_PURCHASE_CHANGE_M] PRIMARY KEY CLUSTERED ([CHANGE_PURCHASE_TYPE], [CHANGE_PURCHASE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_CHANGE_PURCHASE_M] ON dbo.[PUR_PURCHASE_CHANGE_M] ([PURCHASE_TYPE], [PURCHASE_NO]);
GO

------------------------------------------------------------------------------
-- PUR_PURCHASE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PURCHASE_D] (
    [PURCHASE_TYPE] nchar(10) NOT NULL,
    [PURCHASE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_683149479] DEFAULT ((0)),
    [CTN_QTY] float NULL CONSTRAINT [DF_CTN_QTY_683149479] DEFAULT ((0)),
    [RECEIVE_QTY] float NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_683149479] DEFAULT ((0)),
    [PLAN_DELIVERY_DATE] datetime NULL,
    [DEPOT_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_683149479] DEFAULT ((0)),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_683149479] DEFAULT ((0)),
    [REAL_DELIVERY_DATE] datetime NULL,
    [REMARK] nvarchar(50) NULL,
    [APPLY_TYPE] nchar(10) NULL,
    [APPLY_NO] nchar(20) NULL,
    [APPLY_SERIAL_NO] smallint NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_683149479] DEFAULT ((0)),
    [SPARE_QTY] float NULL CONSTRAINT [DF_SPARE_QTY_683149479] DEFAULT ((0)),
    [RECEIVE_SPARE_QTY] float NULL CONSTRAINT [DF_RECEIVE_SPARE_QTY_683149479] DEFAULT ((0)),
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_683149479] DEFAULT ('O'),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_683149479] DEFAULT ((100)),
    [QUOTE_TYPE] nchar(10) NULL,
    [QUOTE_NO] nchar(20) NULL,
    [QUOTE_SERIAL_NO] smallint NULL,
    [REQUIRE_QTY] float NULL CONSTRAINT [DF_REQUIRE_QTY_683149479] DEFAULT ((0)),
    [FINISHED_AMOUNT] float NULL CONSTRAINT [DF_FINISHED_AMOUNT_683149479] DEFAULT ((0)),
    [PRE_LASTPLAN_DATE] datetime NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    [SUPPLIER_PRO_NO] nvarchar(50) NULL,
    [DUO_REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_PUR_PURCHASE_D] PRIMARY KEY CLUSTERED ([PURCHASE_TYPE], [PURCHASE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_PURCHASE_D_2] ON dbo.[PUR_PURCHASE_D] ([QUOTE_TYPE], [QUOTE_NO], [QUOTE_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_PURCHASE_D] ON dbo.[PUR_PURCHASE_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_PURCHASE_D_FINISHED_PLAN] ON dbo.[PUR_PURCHASE_D] ([FINISHED_TAG], [PLAN_DELIVERY_DATE]) INCLUDE ([PRO_NO], [ORDER_SERIAL_NO], [ORDER_NO], [ORDER_TYPE], [PRE_LASTPLAN_DATE], [FINISHED_AMOUNT], [QUOTE_SERIAL_NO], [QUOTE_NO], [APPLY_SERIAL_NO], [APPLY_NO], [APPLY_TYPE], [AMOUNT_TAX], [AMOUNT], [CURR_RATE], [CURR_ID], [DEPOT_ID], [PRICE], [RECEIVE_QTY], [CTN_QTY], [QTY], [CLIENT_ORDER_NO], [DUO_REMARK]);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_PURCHASE_D_1] ON dbo.[PUR_PURCHASE_D] ([APPLY_TYPE], [APPLY_NO], [APPLY_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- PUR_PURCHASE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PURCHASE_M] (
    [PURCHASE_TYPE] nchar(10) NOT NULL,
    [PURCHASE_NO] nchar(20) NOT NULL,
    [PURCHASE_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_699149536] DEFAULT ((0)),
    [TAX_SUM] float NULL CONSTRAINT [DF_TAX_SUM_699149536] DEFAULT ((0)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_699149536] DEFAULT ((0)),
    [PRICE_CONDITION] nchar(30) NULL,
    [PAY_CONDITION] nchar(30) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [DELIVERY_ADDRESS] nvarchar(80) NULL,
    [RECKONING_ADDRESS] nvarchar(80) NULL,
    [BILL_ADDRESS] nvarchar(80) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_699149536] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_699149536] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_699149536] DEFAULT ('O'),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_699149536] DEFAULT ((100)),
    [ORDER_NO] nvarchar(300) NULL,
    [PRODUCE_NO] nvarchar(300) NULL,
    CONSTRAINT [PK_PUR_PURCHASE_M] PRIMARY KEY CLUSTERED ([PURCHASE_TYPE], [PURCHASE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_PURCHASE_M] ON dbo.[PUR_PURCHASE_M] ([SUPPLIER_ID]);
GO

------------------------------------------------------------------------------
-- PUR_PURCHASE_MORE
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_PURCHASE_MORE] (
    [PURCHASE_TYPE] nchar(10) NOT NULL,
    [PURCHASE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRO_NO] nchar(30) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [DEPOT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [REQUIRE_QTY] float NULL CONSTRAINT [DF_REQUIRE_QTY_715149593] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [REMARK] nvarchar(200) NULL,
    CONSTRAINT [PK_PUR_PURCHASE_MORE] PRIMARY KEY CLUSTERED ([PURCHASE_TYPE], [PURCHASE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- PUR_QUOTE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_QUOTE_D] (
    [QUOTE_TYPE] nchar(10) NOT NULL,
    [QUOTE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [CHAFFER_TYPE] nchar(10) NULL,
    [CHAFFER_NO] nchar(20) NULL,
    [CHAFFER_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [PRO_STUFF] nvarchar(100) NULL,
    [PRO_SIZE] nvarchar(100) NULL,
    [PRO_COLOR] nvarchar(100) NULL,
    [PRO_LENGTH] float NULL,
    [PRO_WIDTH] float NULL,
    [PRO_HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [QTY] float NULL,
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [PROCESS_PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_731149650] DEFAULT ((0)),
    [TAX_SUM] float NULL CONSTRAINT [DF_TAX_SUM_731149650] DEFAULT ((0)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_731149650] DEFAULT ((0)),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_731149650] DEFAULT ((100)),
    [SUPPLIER_PRO_NO] nvarchar(50) NULL,
    [OLD_PRICE] float NULL CONSTRAINT [DF_OLD_PRICE_731149650] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_731149650] DEFAULT ('O'),
    [P_LENGTH] float NULL,
    [P_WIDTH] float NULL,
    [MIN_PRICE] float NULL,
    [PARAMETER_PRICE] float NULL,
    [LOST_RATE] float NULL,
    [MOULD_SUM] float NULL,
    [PRINTING_SUM] float NULL,
    [PARAMETER_QTY] float NULL,
    [UP_PERCENT] float NULL CONSTRAINT [DF_UP_PERCENT_731149650] DEFAULT ((0)),
    CONSTRAINT [PK_PUR_QUOTE_D] PRIMARY KEY CLUSTERED ([QUOTE_TYPE], [QUOTE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_QUOTE_D_1] ON dbo.[PUR_QUOTE_D] ([CHAFFER_TYPE], [CHAFFER_NO], [CHAFFER_SERIAL_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_QUOTE_D] ON dbo.[PUR_QUOTE_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- PUR_QUOTE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_QUOTE_M] (
    [QUOTE_TYPE] nchar(10) NOT NULL,
    [QUOTE_NO] nchar(20) NOT NULL,
    [QUOTE_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [PURCHASE_ID] nchar(15) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL,
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_747149707] DEFAULT ((0)),
    [PRICE_CONDITION] nvarchar(50) NULL,
    [PAYMENT_CONDITION] nvarchar(50) NULL,
    [DELIVERY_DATE] datetime NULL,
    [IN_EFFECT_DATE] datetime NULL,
    [SUPPLIER_CONFIRM] nvarchar(100) NULL CONSTRAINT [DF_SUPPLIER_CONFIRM_747149707] DEFAULT ('N'),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_747149707] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_747149707] DEFAULT ('O'),
    [SUPPLIER_NAME] nvarchar(100) NULL,
    [REBATE] float NULL CONSTRAINT [DF_REBATE_747149707] DEFAULT ((100)),
    CONSTRAINT [PK_PUR_QUOTE_M] PRIMARY KEY CLUSTERED ([QUOTE_TYPE], [QUOTE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_QUOTE_M] ON dbo.[PUR_QUOTE_M] ([SUPPLIER_ID]);
GO

------------------------------------------------------------------------------
-- PUR_RECEIVE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_RECEIVE_D] (
    [RECEIVE_TYPE] nchar(10) NOT NULL,
    [RECEIVE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PURCHASE_TYPE] nchar(10) NULL,
    [PURCHASE_NO] nchar(20) NULL,
    [PURCHASE_SERIAL_NO] smallint NULL,
    [PRO_NO] nchar(30) NULL,
    [UNIT_ID] nchar(10) NULL,
    [BATCH_NO] nchar(30) NULL,
    [DEPOT_ID] nchar(10) NULL,
    [CHECK_RECEIVE_QTY] float NULL CONSTRAINT [DF_CHECK_RECEIVE_QTY_763149764] DEFAULT ((0)),
    [PRICE] float NULL CONSTRAINT [DF_PRICE_763149764] DEFAULT ((0)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL CONSTRAINT [DF_CURR_RATE_763149764] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_763149764] DEFAULT ((0)),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_763149764] DEFAULT ((0)),
    [TAX_SUM] float NULL CONSTRAINT [DF_TAX_SUM_763149764] DEFAULT ((0)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_763149764] DEFAULT ((0)),
    [BUY_FEE] float NULL CONSTRAINT [DF_BUY_FEE_763149764] DEFAULT ((0)),
    [UNPAY_TAG] bit NULL CONSTRAINT [DF_UNPAY_TAG_763149764] DEFAULT ((0)),
    [RECOUP_MONEY] float NULL CONSTRAINT [DF_RECOUP_MONEY_763149764] DEFAULT ((0)),
    [RECOUP_NOTE] nvarchar(50) NULL,
    [PAY_AMOUNT] float NULL CONSTRAINT [DF_PAY_AMOUNT_763149764] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_763149764] DEFAULT ((0)),
    [CANCEL_QTY] float NULL CONSTRAINT [DF_CANCEL_QTY_763149764] DEFAULT ((0)),
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_763149764] DEFAULT ('O'),
    [SPARE_QTY] float NULL CONSTRAINT [DF_SPARE_QTY_763149764] DEFAULT ((0)),
    [CANCEL_SPARE_QTY] float NULL CONSTRAINT [DF_CANCEL_SPARE_QTY_763149764] DEFAULT ((0)),
    [QTY] float NULL CONSTRAINT [DF_QTY_763149764] DEFAULT ((0)),
    [CTN_QTY] float NULL CONSTRAINT [DF_CTN_QTY_763149764] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_763149764] DEFAULT ((0)),
    [REBATE] float NULL CONSTRAINT [DF_REBATE_763149764] DEFAULT ((100)),
    [FINISHED_AMOUNT] float NULL CONSTRAINT [DF_FINISHED_AMOUNT_763149764] DEFAULT ((0)),
    [WEIGHT] float NULL,
    [SUPPLIER_PRO_NO] nvarchar(50) NULL,
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    [CLIENT_ORDER_NO] nvarchar(50) NULL,
    CONSTRAINT [PK_PUR_RECEIVE_D] PRIMARY KEY CLUSTERED ([RECEIVE_TYPE], [RECEIVE_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_RECEIVE_D] ON dbo.[PUR_RECEIVE_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- PUR_RECEIVE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[PUR_RECEIVE_M] (
    [RECEIVE_TYPE] nchar(10) NOT NULL,
    [RECEIVE_NO] nchar(20) NOT NULL,
    [RECEIVE_DATE] datetime NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_779149821] DEFAULT ((0)),
    [TAX_ID] nchar(10) NULL,
    [TAX_RATE] float NULL,
    [TAX_SUM] float NULL CONSTRAINT [DF_TAX_SUM_779149821] DEFAULT ((0)),
    [AMOUNT_TAX] float NULL CONSTRAINT [DF_AMOUNT_TAX_779149821] DEFAULT ((0)),
    [DEPOT_ID] nchar(10) NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [DELIVERY_ADDRESS] nvarchar(100) NULL,
    [SUPPLIER_ORDER_NO] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_779149821] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_779149821] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] char(1) NULL CONSTRAINT [DF_TAX_TYPE_779149821] DEFAULT ('O'),
    CONSTRAINT [PK_PUR_RECEIVE_M] PRIMARY KEY CLUSTERED ([RECEIVE_TYPE], [RECEIVE_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_PUR_RECEIVE_M] ON dbo.[PUR_RECEIVE_M] ([SUPPLIER_ID]);
GO

------------------------------------------------------------------------------
-- QC_ANALYSIS_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_ANALYSIS_D] (
    [ANALYSIS_TYPE] nchar(10) NOT NULL,
    [ANALYSIS_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_QTY] float NULL CONSTRAINT [DF_PRODUCE_QTY_795149878] DEFAULT ((0)),
    [CLIENT_ID] nchar(10) NULL,
    [PRO_NO] nchar(30) NULL CONSTRAINT [DF_PRO_NO_795149878] DEFAULT (''),
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [GOOD_QTY] float NULL CONSTRAINT [DF_GOOD_QTY_795149878] DEFAULT ((0)),
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_795149878] DEFAULT ((0)),
    [BAD_RATE] float NULL CONSTRAINT [DF_BAD_RATE_795149878] DEFAULT ((0)),
    [RESULT1] float NULL,
    [RESULT2] float NULL,
    [RESULT3] float NULL,
    [RESULT4] float NULL,
    [RESULT5] float NULL,
    [RESULT6] float NULL,
    [RESULT7] float NULL,
    [RESULT8] float NULL,
    [RESULT9] float NULL,
    [RESULT10] nvarchar(50) NULL,
    [REMARK] nvarchar(100) NULL,
    [SCRAP_TYPE] nchar(10) NULL,
    [SCRAP_NO] nchar(20) NULL,
    [EXCEPTION_TYPE] nchar(10) NULL,
    [EXCEPTION_NO] nchar(20) NULL,
    [RESULT_TYPE] nchar(10) NULL,
    [SCRAP_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_QC_ANALYSIS_D] PRIMARY KEY CLUSTERED ([ANALYSIS_TYPE], [ANALYSIS_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- QC_ANALYSIS_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_ANALYSIS_M] (
    [ANALYSIS_TYPE] nchar(10) NOT NULL,
    [ANALYSIS_NO] nchar(20) NOT NULL,
    [ANALYSIS_DATE] datetime NULL CONSTRAINT [DF_ANALYSIS_DATE_811149935] DEFAULT (getdate()),
    [ANALYSIS_REMARK] nvarchar(300) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_811149935] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_811149935] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_QC_ANALYSIS_M] PRIMARY KEY CLUSTERED ([ANALYSIS_TYPE], [ANALYSIS_NO])
);
GO

------------------------------------------------------------------------------
-- QC_APPLY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_APPLY_M] (
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    [APPLY_DATE] datetime NULL CONSTRAINT [DF_APPLY_DATE_827149992] DEFAULT (getdate()),
    [RESULT1] bit NULL CONSTRAINT [DF_RESULT1_827149992] DEFAULT ((0)),
    [RESULT2] bit NULL CONSTRAINT [DF_RESULT2_827149992] DEFAULT ((0)),
    [RESULT3] bit NULL CONSTRAINT [DF_RESULT3_827149992] DEFAULT ((0)),
    [EXCEPTION_TYPE] nchar(10) NULL,
    [EXCEPTION_NO] nchar(20) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_DATE] datetime NULL,
    [PRO_NO] nchar(30) NULL CONSTRAINT [DF_PRO_NO_827149992] DEFAULT (''),
    [CLIENT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_827149992] DEFAULT ((0)),
    [BAD_REMARK] nvarchar(100) NULL,
    [APPLY_REMARK] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_827149992] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_827149992] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [COMPLAIN_TYPE] nchar(10) NULL,
    [COMPLAIN_NO] nchar(20) NULL,
    CONSTRAINT [PK_QC_APPLY_M] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO])
);
GO

------------------------------------------------------------------------------
-- QC_COMPLAIN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_COMPLAIN_M] (
    [COMPLAIN_TYPE] nchar(10) NOT NULL,
    [COMPLAIN_NO] nchar(20) NOT NULL,
    [COMPLAIN_DATE] datetime NULL CONSTRAINT [DF_COMPLAIN_DATE_843150049] DEFAULT (getdate()),
    [CLIENT_ID] nchar(10) NULL,
    [IF_COMPLAIN] bit NULL CONSTRAINT [DF_IF_COMPLAIN_843150049] DEFAULT ((0)),
    [IF_RETURN] bit NULL CONSTRAINT [DF_IF_RETURN_843150049] DEFAULT ((0)),
    [PRO_NO] nchar(30) NULL CONSTRAINT [DF_PRO_NO_843150049] DEFAULT (''),
    [SEND_DATE] datetime NULL,
    [PRODUCE_DATE] datetime NULL,
    [SEND_QTY] float NULL CONSTRAINT [DF_SEND_QTY_843150049] DEFAULT ((0)),
    [PRODUCE_QTY] float NULL CONSTRAINT [DF_PRODUCE_QTY_843150049] DEFAULT ((0)),
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [RETURN_QTY] float NULL CONSTRAINT [DF_RETURN_QTY_843150049] DEFAULT ((0)),
    [PRODUCE_EMP_ID] nchar(10) NULL,
    [COMPLAIN_REMARK] nvarchar(200) NULL,
    [BAD_CONDITION1] nvarchar(50) NULL,
    [BAD_QTY1] float NULL CONSTRAINT [DF_BAD_QTY1_843150049] DEFAULT ((0)),
    [BAD_RATE1] float NULL CONSTRAINT [DF_BAD_RATE1_843150049] DEFAULT ((0)),
    [BAD_CONDITION2] nvarchar(50) NULL,
    [BAD_QTY2] float NULL CONSTRAINT [DF_BAD_QTY2_843150049] DEFAULT ((0)),
    [BAD_RATE2] float NULL CONSTRAINT [DF_BAD_RATE2_843150049] DEFAULT ((0)),
    [CLIENT_REMARK] nvarchar(200) NULL,
    [HELP_REMARK] nvarchar(100) NULL,
    [RESULT1] bit NULL CONSTRAINT [DF_RESULT1_843150049] DEFAULT ((0)),
    [RESULT2] bit NULL CONSTRAINT [DF_RESULT2_843150049] DEFAULT ((0)),
    [RESULT3] bit NULL CONSTRAINT [DF_RESULT3_843150049] DEFAULT ((0)),
    [RESULT4] bit NULL CONSTRAINT [DF_RESULT4_843150049] DEFAULT ((0)),
    [SCRAP_QTY] float NULL CONSTRAINT [DF_SCRAP_QTY_843150049] DEFAULT ((0)),
    [ADD_QTY] float NULL CONSTRAINT [DF_ADD_QTY_843150049] DEFAULT ((0)),
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [AMERCE1] float NULL CONSTRAINT [DF_AMERCE1_843150049] DEFAULT ((0)),
    [AMERCE2] float NULL CONSTRAINT [DF_AMERCE2_843150049] DEFAULT ((0)),
    [AMERCE3] float NULL CONSTRAINT [DF_AMERCE3_843150049] DEFAULT ((0)),
    [AMERCE4] float NULL CONSTRAINT [DF_AMERCE4_843150049] DEFAULT ((0)),
    [AMERCE5] float NULL CONSTRAINT [DF_AMERCE5_843150049] DEFAULT ((0)),
    [CHARGE_EMP_ID] nchar(10) NULL,
    [OQC_REMARK] nvarchar(100) NULL,
    [IPQC_REMARK] nvarchar(100) NULL,
    [IPQC_METHOD] nvarchar(100) NULL,
    [BAD_REMARK] nvarchar(100) NULL,
    [BAD_METHOD] nvarchar(100) NULL,
    [SAME_METHOD] nvarchar(100) NULL,
    [RESULT_REMARK] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_843150049] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_843150049] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [DEPT_ID2] nchar(10) NULL,
    [EMP_NAME] nvarchar(50) NULL,
    [BL_REMARK] nvarchar(200) NULL,
    [GS_REMARK] nvarchar(200) NULL,
    [CL_REMARK] nvarchar(200) NULL,
    CONSTRAINT [PK_QC_COMPLAIN_M] PRIMARY KEY CLUSTERED ([COMPLAIN_TYPE], [COMPLAIN_NO])
);
GO

------------------------------------------------------------------------------
-- QC_EXCEPTION_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_EXCEPTION_M] (
    [EXCEPTION_TYPE] nchar(10) NOT NULL,
    [EXCEPTION_NO] nchar(20) NOT NULL,
    [EXCEPTION_DATE] datetime NULL CONSTRAINT [DF_EXCEPTION_DATE_859150106] DEFAULT (getdate()),
    [DEPT_ID] nchar(10) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_QTY] float NULL CONSTRAINT [DF_PRODUCE_QTY_859150106] DEFAULT ((0)),
    [CLIENT_ID] nchar(10) NULL,
    [PRO_NO] nchar(30) NULL CONSTRAINT [DF_PRO_NO_859150106] DEFAULT (''),
    [SUPPLIER_ID] nchar(15) NULL,
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [BAD_CONDITION1] nvarchar(50) NULL,
    [BAD_QTY1] float NULL CONSTRAINT [DF_BAD_QTY1_859150106] DEFAULT ((0)),
    [BAD_RATE1] float NULL CONSTRAINT [DF_BAD_RATE1_859150106] DEFAULT ((0)),
    [BAD_CONDITION2] nvarchar(50) NULL,
    [BAD_QTY2] float NULL CONSTRAINT [DF_BAD_QTY2_859150106] DEFAULT ((0)),
    [BAD_RATE2] float NULL CONSTRAINT [DF_BAD_RATE2_859150106] DEFAULT ((0)),
    [BAD_CONDITION3] nvarchar(50) NULL,
    [BAD_QTY3] float NULL CONSTRAINT [DF_BAD_QTY3_859150106] DEFAULT ((0)),
    [BAD_RATE] float NULL CONSTRAINT [DF_BAD_RATE_859150106] DEFAULT ((0)),
    [BAD_CONDITION4] nvarchar(50) NULL,
    [BAD_QTY4] float NULL CONSTRAINT [DF_BAD_QTY4_859150106] DEFAULT ((0)),
    [BAD_RATE4] float NULL CONSTRAINT [DF_BAD_RATE4_859150106] DEFAULT ((0)),
    [BAD_CONDITION5] nvarchar(50) NULL,
    [BAD_QTY5] float NULL CONSTRAINT [DF_BAD_QTY5_859150106] DEFAULT ((0)),
    [BAD_RATE5] float NULL CONSTRAINT [DF_BAD_RATE5_859150106] DEFAULT ((0)),
    [RESULT1] bit NULL CONSTRAINT [DF_RESULT1_859150106] DEFAULT ((0)),
    [RESULT2] bit NULL CONSTRAINT [DF_RESULT2_859150106] DEFAULT ((0)),
    [RESULT3] bit NULL CONSTRAINT [DF_RESULT3_859150106] DEFAULT ((0)),
    [RESULT4] bit NULL CONSTRAINT [DF_RESULT4_859150106] DEFAULT ((0)),
    [RESULT5] bit NULL CONSTRAINT [DF_RESULT5_859150106] DEFAULT ((0)),
    [AMERCE1] float NULL CONSTRAINT [DF_AMERCE1_859150106] DEFAULT ((0)),
    [AMERCE2] float NULL CONSTRAINT [DF_AMERCE2_859150106] DEFAULT ((0)),
    [AMERCE3] float NULL CONSTRAINT [DF_AMERCE3_859150106] DEFAULT ((0)),
    [AMERCE4] float NULL CONSTRAINT [DF_AMERCE4_859150106] DEFAULT ((0)),
    [AMERCE5] float NULL CONSTRAINT [DF_AMERCE5_859150106] DEFAULT ((0)),
    [CHARGE_EMP_ID] nchar(10) NULL,
    [ADD_QTY] float NULL CONSTRAINT [DF_ADD_QTY_859150106] DEFAULT ((0)),
    [ELEMENT_QTY] float NULL,
    [BAD_REMARK] nvarchar(100) NULL,
    [REMARK] nvarchar(200) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_859150106] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_859150106] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [DEPT_ID1] nchar(10) NULL,
    [SCRAP_TYPE] nchar(10) NULL,
    [SCRAP_NO] nchar(20) NULL,
    [SCRAP_SERIAL_NO] smallint NULL,
    [DEPT_ID2] nchar(10) NULL,
    [EMP_NAME] nvarchar(50) NULL,
    [BL_REMARK] nvarchar(200) NULL,
    [GS_REMARK] nvarchar(200) NULL,
    [CL_REMARK] nvarchar(200) NULL,
    CONSTRAINT [PK_QC_EXCEPTION_M] PRIMARY KEY CLUSTERED ([EXCEPTION_TYPE], [EXCEPTION_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_QC_EXCEPTION_M] ON dbo.[QC_EXCEPTION_M] ([SCRAP_TYPE], [SCRAP_NO], [SCRAP_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- QC_LOSS_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_LOSS_D] (
    [LOSS_TYPE] nchar(10) NOT NULL,
    [LOSS_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [ANALYSIS_TYPE] nchar(10) NOT NULL,
    [ANALYSIS_NO] nchar(20) NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_QTY] float NULL CONSTRAINT [DF_PRODUCE_QTY_875150163] DEFAULT ((0)),
    [SUPPLIER_ID] nchar(15) NULL,
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [GOOD_QTY] float NULL CONSTRAINT [DF_GOOD_QTY_875150163] DEFAULT ((0)),
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_875150163] DEFAULT ((0)),
    [BAD_RATE] float NULL CONSTRAINT [DF_BAD_RATE_875150163] DEFAULT ((0)),
    [WEIGHT] float NULL CONSTRAINT [DF_WEIGHT_875150163] DEFAULT ((0)),
    [CLIENT_ID] nchar(10) NULL,
    [PRO_NO] nchar(30) NULL CONSTRAINT [DF_PRO_NO_875150163] DEFAULT (''),
    [REMARK] nvarchar(100) NULL,
    CONSTRAINT [PK_QC_LOSS_D] PRIMARY KEY CLUSTERED ([LOSS_TYPE], [LOSS_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_QC_LOSS_D_1] ON dbo.[QC_LOSS_D] ([ANALYSIS_TYPE], [ANALYSIS_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_QC_LOSS_D] ON dbo.[QC_LOSS_D] ([ANALYSIS_TYPE], [ANALYSIS_NO]);
GO

------------------------------------------------------------------------------
-- QC_LOSS_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_LOSS_M] (
    [LOSS_TYPE] nchar(10) NOT NULL,
    [LOSS_NO] nchar(20) NOT NULL,
    [LOSS_DATE] datetime NULL CONSTRAINT [DF_LOSS_DATE_891150220] DEFAULT (getdate()),
    [LOSS_REMARK] nvarchar(300) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_891150220] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_891150220] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_QC_LOSS_M] PRIMARY KEY CLUSTERED ([LOSS_TYPE], [LOSS_NO])
);
GO

------------------------------------------------------------------------------
-- QC_REWORK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_REWORK_M] (
    [REWORK_TYPE] nchar(10) NOT NULL,
    [REWORK_NO] nchar(20) NOT NULL,
    [REWORK_DATE] datetime NULL CONSTRAINT [DF_REWORK_DATE_907150277] DEFAULT (getdate()),
    [SEND_DEPT_ID] nchar(10) NULL,
    [RECEIVE_DEPT_ID] nchar(10) NULL,
    [EXCEPTION_TYPE] nchar(10) NULL,
    [EXCEPTION_NO] nchar(20) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_DATE] datetime NULL,
    [PRO_NO] nchar(30) NULL CONSTRAINT [DF_PRO_NO_907150277] DEFAULT (''),
    [CLIENT_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_907150277] DEFAULT ((0)),
    [BAD_REMARK] nvarchar(100) NULL,
    [REWORK_REMARK] nvarchar(100) NULL,
    [AMERCE1] float NULL CONSTRAINT [DF_AMERCE1_907150277] DEFAULT ((0)),
    [AMERCE2] float NULL CONSTRAINT [DF_AMERCE2_907150277] DEFAULT ((0)),
    [AMERCE3] float NULL CONSTRAINT [DF_AMERCE3_907150277] DEFAULT ((0)),
    [AMERCE4] float NULL CONSTRAINT [DF_AMERCE4_907150277] DEFAULT ((0)),
    [AMERCE5] float NULL CONSTRAINT [DF_AMERCE5_907150277] DEFAULT ((0)),
    [ADD_QTY] float NULL CONSTRAINT [DF_ADD_QTY_907150277] DEFAULT ((0)),
    [CHARGE_EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_907150277] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_907150277] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [SCRAP_TYPE] nchar(10) NULL,
    [SCRAP_NO] nchar(20) NULL,
    [SCRAP_SERIAL_NO] smallint NULL,
    [COMPLAIN_TYPE] nchar(10) NULL,
    [COMPLAIN_NO] nchar(20) NULL,
    CONSTRAINT [PK_QC_REWORK_M] PRIMARY KEY CLUSTERED ([REWORK_TYPE], [REWORK_NO])
);
GO

------------------------------------------------------------------------------
-- QC_SAMPLE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_SAMPLE_M] (
    [PRO_NO] nchar(30) NOT NULL,
    [SAMPLE_BOX_NO] nvarchar(50) NULL,
    [SAMPLE_SG] float NULL CONSTRAINT [DF_SAMPLE_SG_923150334] DEFAULT ((0)),
    [SAMPLE_LC] float NULL CONSTRAINT [DF_SAMPLE_LC_923150334] DEFAULT ((0)),
    [SAMPLE_XD] float NULL CONSTRAINT [DF_SAMPLE_XD_923150334] DEFAULT ((0)),
    [IF_QC] bit NULL CONSTRAINT [DF_IF_QC_923150334] DEFAULT ((0)),
    [IF_KHQC] bit NULL CONSTRAINT [DF_IF_KHQC_923150334] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_923150334] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_QC_SAMPLE_M] PRIMARY KEY CLUSTERED ([PRO_NO])
);
GO

------------------------------------------------------------------------------
-- QC_SCRAP_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_SCRAP_D] (
    [SCRAP_TYPE] nchar(10) NOT NULL,
    [SCRAP_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_QTY] float NULL CONSTRAINT [DF_PRODUCE_QTY_939150391] DEFAULT ((0)),
    [CLIENT_ID] nchar(10) NULL,
    [PRO_NO] nchar(30) NULL CONSTRAINT [DF_PRO_NO_939150391] DEFAULT (''),
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_939150391] DEFAULT ((0)),
    [BAD_RATE] float NULL CONSTRAINT [DF_BAD_RATE_939150391] DEFAULT ((0)),
    [ADD_QTY] float NULL CONSTRAINT [DF_ADD_QTY_939150391] DEFAULT ((0)),
    [BAD_REMARK] nvarchar(100) NULL,
    [ANALYSIS_TYPE] nchar(10) NULL,
    [ANALYSIS_NO] nchar(20) NULL,
    [COMPLAIN_TYPE] nchar(10) NULL,
    [COMPLAIN_NO] nchar(20) NULL,
    [REWORK_TYPE] nchar(10) NULL,
    [REWORK_NO] nchar(20) NULL,
    [EXCEPTION_TYPE] nchar(10) NULL,
    [EXCEPTION_NO] nchar(20) NULL,
    CONSTRAINT [PK_QC_SCRAP_D] PRIMARY KEY CLUSTERED ([SCRAP_TYPE], [SCRAP_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- QC_SCRAP_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[QC_SCRAP_M] (
    [SCRAP_TYPE] nchar(10) NOT NULL,
    [SCRAP_NO] nchar(20) NOT NULL,
    [SCRAP_DATE] datetime NULL CONSTRAINT [DF_SCRAP_DATE_955150448] DEFAULT (getdate()),
    [SCRAP_REMARK] nvarchar(300) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_955150448] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_QC_SCRAP_M] PRIMARY KEY CLUSTERED ([SCRAP_TYPE], [SCRAP_NO])
);
GO

------------------------------------------------------------------------------
-- RECEIVE
------------------------------------------------------------------------------
CREATE TABLE dbo.[RECEIVE] (
    [RECEIVE_ID] nchar(10) NOT NULL,
    [RECEIVE_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_971150505] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_RECEIVE] PRIMARY KEY CLUSTERED ([RECEIVE_ID])
);
GO

------------------------------------------------------------------------------
-- REMARK
------------------------------------------------------------------------------
CREATE TABLE dbo.[REMARK] (
    [REMARK_TYPE] nchar(10) NOT NULL,
    [REMARK_NO] nchar(20) NOT NULL,
    [REMARK_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_987150562] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_REMARK] PRIMARY KEY CLUSTERED ([REMARK_TYPE], [REMARK_NO])
);
GO

------------------------------------------------------------------------------
-- REPORT
------------------------------------------------------------------------------
CREATE TABLE dbo.[REPORT] (
    [REPORT_ID] nchar(50) NOT NULL,
    [REPORT_NAME] nvarchar(100) NULL,
    [R_M_IDX] int NULL CONSTRAINT [DF_R_M_IDX_1003150619] DEFAULT ((0)),
    [Q_M_IDX] int NULL CONSTRAINT [DF_Q_M_IDX_1003150619] DEFAULT ((0)),
    [ISO_NO] nvarchar(50) NULL,
    [HEADER_ID] nchar(10) NULL,
    [FOOTER_TEXT] nvarchar(2000) NULL,
    [TAIL_ID] nchar(10) NULL,
    [IS_DEFAULT] bit NULL CONSTRAINT [DF_IS_DEFAULT_1003150619] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1003150619] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [REPORT_FILTER] nvarchar(500) NULL,
    CONSTRAINT [PK_REPORT] PRIMARY KEY CLUSTERED ([REPORT_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT_TAIL_ID] ON dbo.[REPORT] ([TAIL_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT] ON dbo.[REPORT] ([R_M_IDX]);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT_1] ON dbo.[REPORT] ([Q_M_IDX]);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT_HEADER_ID] ON dbo.[REPORT] ([HEADER_ID]);
GO

------------------------------------------------------------------------------
-- REPORT_FORM_BINDING
------------------------------------------------------------------------------
CREATE TABLE dbo.[REPORT_FORM_BINDING] (
    [FORM_TYPE] nvarchar(20) NOT NULL,
    [CLIENT_ID] nvarchar(50) NOT NULL CONSTRAINT [DF_CLIENT_ID_946154466] DEFAULT (N''),
    [LAYOUT_ID] int NULL,
    [HEADER_ID] nvarchar(50) NULL,
    [TAIL_ID] nvarchar(50) NULL,
    [PRINT_PRICE] bit NULL,
    [CREATE_PERSON] nchar(40) NULL,
    [CREATE_DATE] datetime2(7) NOT NULL CONSTRAINT [DF_CREATE_DATE_946154466] DEFAULT (sysdatetime()),
    [LAST_UPDATE_BY] nchar(40) NULL,
    [LAST_UPDATE_DATE] datetime2(7) NULL,
    CONSTRAINT [PK_REPORT_FORM_BINDING] PRIMARY KEY CLUSTERED ([FORM_TYPE], [CLIENT_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT_FORM_BINDING_LAYOUT] ON dbo.[REPORT_FORM_BINDING] ([LAYOUT_ID]);
GO

------------------------------------------------------------------------------
-- REPORT_FORM_LAYOUT
------------------------------------------------------------------------------
CREATE TABLE dbo.[REPORT_FORM_LAYOUT] (
    [LAYOUT_ID] int IDENTITY(1,1) NOT NULL,
    [BASE_FORMAT_ID] nvarchar(50) NOT NULL,
    [OWNER_ID] nvarchar(50) NOT NULL CONSTRAINT [DF_OWNER_ID_850154124] DEFAULT (N''),
    [LAYOUT_JSON] nvarchar(max) NOT NULL,
    [LAYOUT_VERSION] int NOT NULL CONSTRAINT [DF_LAYOUT_VERSION_850154124] DEFAULT ((1)),
    [KIND] nvarchar(20) NOT NULL CONSTRAINT [DF_KIND_850154124] DEFAULT (N'document'),
    [CREATE_PERSON] nchar(40) NULL,
    [CREATE_DATE] datetime2(7) NOT NULL CONSTRAINT [DF_CREATE_DATE_850154124] DEFAULT (sysdatetime()),
    [LAST_UPDATE_BY] nchar(40) NULL,
    [LAST_UPDATE_DATE] datetime2(7) NULL,
    CONSTRAINT [PK_REPORT_FORM_LAYOUT] PRIMARY KEY CLUSTERED ([LAYOUT_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT_FORM_LAYOUT_BASE_OWNER] ON dbo.[REPORT_FORM_LAYOUT] ([BASE_FORMAT_ID], [OWNER_ID]);
GO

------------------------------------------------------------------------------
-- REPORT_FORM_LAYOUT_VERSION
------------------------------------------------------------------------------
CREATE TABLE dbo.[REPORT_FORM_LAYOUT_VERSION] (
    [LAYOUT_ID] int NOT NULL,
    [VERSION] int NOT NULL,
    [LAYOUT_JSON] nvarchar(max) NOT NULL,
    [CREATE_PERSON] nchar(40) NULL,
    [CREATE_DATE] datetime2(7) NOT NULL CONSTRAINT [DF_CREATE_DATE_1090154979] DEFAULT (sysdatetime()),
    CONSTRAINT [PK_REPORT_FORM_LAYOUT_VERSION] PRIMARY KEY CLUSTERED ([LAYOUT_ID], [VERSION])
);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT_FORM_LAYOUT_VERSION_DATE] ON dbo.[REPORT_FORM_LAYOUT_VERSION] ([LAYOUT_ID], [CREATE_DATE] DESC);
GO

------------------------------------------------------------------------------
-- REPORT_INBOX
------------------------------------------------------------------------------
CREATE TABLE dbo.[REPORT_INBOX] (
    [ID] int IDENTITY(1,1) NOT NULL,
    [USER_ID] nchar(10) NOT NULL,
    [MODULE_ID] int NOT NULL,
    [REPORT_ID] nchar(50) NOT NULL,
    [TITLE] nvarchar(200) NOT NULL,
    [PDF_PATH] nvarchar(500) NOT NULL,
    [GENERATED_AT] datetime2(7) NOT NULL CONSTRAINT [DF_GENERATED_AT_546153041] DEFAULT (sysdatetime()),
    [READ_TAG] bit NOT NULL CONSTRAINT [DF_READ_TAG_546153041] DEFAULT ((0)),
    CONSTRAINT [PK_REPORT_INBOX] PRIMARY KEY CLUSTERED ([ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT_INBOX_USER] ON dbo.[REPORT_INBOX] ([USER_ID], [GENERATED_AT]);
GO

------------------------------------------------------------------------------
-- REPORT_LAYOUT
------------------------------------------------------------------------------
CREATE TABLE dbo.[REPORT_LAYOUT] (
    [LAYOUT_ID] nvarchar(50) NOT NULL,
    [KIND] nvarchar(20) NOT NULL,
    [LAYOUT_DESC] nvarchar(100) NULL,
    [CONTENT] nvarchar(max) NULL,
    [IMAGE_PATH] nvarchar(500) NULL,
    [IS_DEFAULT] bit NOT NULL CONSTRAINT [DF_IS_DEFAULT_786153896] DEFAULT ((0)),
    [CREATE_PERSON] nchar(40) NULL,
    [CREATE_DATE] datetime2(7) NOT NULL CONSTRAINT [DF_CREATE_DATE_786153896] DEFAULT (sysdatetime()),
    [LAST_UPDATE_BY] nchar(40) NULL,
    [LAST_UPDATE_DATE] datetime2(7) NULL,
    CONSTRAINT [PK_REPORT_LAYOUT] PRIMARY KEY CLUSTERED ([LAYOUT_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT_LAYOUT_KIND] ON dbo.[REPORT_LAYOUT] ([KIND]);
GO

------------------------------------------------------------------------------
-- REPORT_METRIC
------------------------------------------------------------------------------
CREATE TABLE dbo.[REPORT_METRIC] (
    [METRIC_ID] nvarchar(50) NOT NULL,
    [METRIC_NAME] nvarchar(100) NOT NULL,
    [DEFINITION] nvarchar(max) NOT NULL,
    [SOURCE_TABLE] nvarchar(200) NULL,
    [DIMENSION_KEYS] nvarchar(500) NULL,
    [DOMAIN] nvarchar(50) NULL,
    [VERSION] int NOT NULL CONSTRAINT [DF_VERSION_610153269] DEFAULT ((1)),
    [DESCRIPTION] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(40) NULL,
    [CREATE_DATE] datetime2(7) NOT NULL CONSTRAINT [DF_CREATE_DATE_610153269] DEFAULT (sysdatetime()),
    [LAST_UPDATE_BY] nchar(40) NULL,
    [LAST_UPDATE_DATE] datetime2(7) NULL,
    CONSTRAINT [PK_REPORT_METRIC] PRIMARY KEY CLUSTERED ([METRIC_ID])
);
GO

------------------------------------------------------------------------------
-- REPORT_SORT
------------------------------------------------------------------------------
CREATE TABLE dbo.[REPORT_SORT] (
    [REPORT_ID] nchar(50) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [SORT_NAME] nvarchar(50) NULL,
    [SORT_FIELDS] nvarchar(500) NULL,
    [SORT_DESC] nvarchar(1000) NULL,
    [GROUP_NAME] nvarchar(50) NULL,
    [GROUP_FIELDS] nvarchar(500) NULL,
    [GROUP_DESC] nvarchar(1000) NULL,
    CONSTRAINT [PK_REPORT_SORT] PRIMARY KEY CLUSTERED ([REPORT_ID], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- REPORT_SUBSCRIPTION
------------------------------------------------------------------------------
CREATE TABLE dbo.[REPORT_SUBSCRIPTION] (
    [ID] int IDENTITY(1,1) NOT NULL,
    [USER_ID] nchar(10) NOT NULL,
    [MODULE_ID] int NOT NULL,
    [REPORT_ID] nchar(50) NOT NULL,
    [SCHEDULE_TYPE] nchar(10) NOT NULL CONSTRAINT [DF_SCHEDULE_TYPE_434152642] DEFAULT (N'DAILY'),
    [RUN_HOUR] int NOT NULL CONSTRAINT [DF_RUN_HOUR_434152642] DEFAULT ((8)),
    [RUN_MINUTE] int NOT NULL CONSTRAINT [DF_RUN_MINUTE_434152642] DEFAULT ((0)),
    [WEEKDAY] int NULL,
    [MONTH_DAY] int NULL,
    [ENABLED_TAG] bit NOT NULL CONSTRAINT [DF_ENABLED_TAG_434152642] DEFAULT ((1)),
    [LAST_RUN_AT] datetime2(7) NULL,
    [CREATE_PERSON] nchar(40) NULL,
    [CREATE_DATE] datetime2(7) NOT NULL CONSTRAINT [DF_CREATE_DATE_434152642] DEFAULT (sysdatetime()),
    [LAST_UPDATE_BY] nchar(40) NULL,
    [LAST_UPDATE_DATE] datetime2(7) NULL,
    CONSTRAINT [PK_REPORT_SUBSCRIPTION] PRIMARY KEY CLUSTERED ([ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_REPORT_SUBSCRIPTION_USER] ON dbo.[REPORT_SUBSCRIPTION] ([USER_ID], [ENABLED_TAG]);
GO

------------------------------------------------------------------------------
-- SAM_APPLY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SAM_APPLY_D] (
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1115151018] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1115151018] DEFAULT ((0)),
    [SAM_QTY] float NULL CONSTRAINT [DF_SAM_QTY_1115151018] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    CONSTRAINT [PK_SAM_APPLY_D] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_SAM_APPLY_D] ON dbo.[SAM_APPLY_D] ([PRO_NO]);
GO

------------------------------------------------------------------------------
-- SAM_APPLY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SAM_APPLY_M] (
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(100) NULL,
    [SALES_ID] nchar(10) NULL,
    [ADDRESS] nvarchar(250) NULL,
    [SAM_TYPE] char(1) NULL,
    [IF_PAPER] bit NULL CONSTRAINT [DF_IF_PAPER_1131151075] DEFAULT ((0)),
    [SEND_DATE] datetime NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [PRO_NO] nchar(30) NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [PRO_STUFF] nvarchar(100) NULL,
    [PRO_SIZE] nvarchar(100) NULL,
    [COLOR_ID] nchar(10) NULL,
    [COLOR_NAME] nvarchar(50) NULL,
    [LENGTH] float NULL,
    [WIDTH] float NULL,
    [HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [PRODUCE_STUFF_ID] nchar(10) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1131151075] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_1131151075] DEFAULT ((0)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL CONSTRAINT [DF_CURR_RATE_1131151075] DEFAULT ((0)),
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1131151075] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1131151075] DEFAULT ((0)),
    [PRODUCE_REMARK] nvarchar(500) NULL,
    [CLIENT_CONFIRM] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1131151075] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [APPLY_TYPE] nchar(10) NOT NULL,
    [APPLY_NO] nchar(20) NOT NULL,
    [APPLY_DATE] datetime NULL,
    [MODIFY_QTY] smallint NULL CONSTRAINT [DF_MODIFY_QTY_1131151075] DEFAULT ((0)),
    [REMARK12] nvarchar(50) NULL,
    [REMARK11] nvarchar(50) NULL,
    [REMARK10] nvarchar(50) NULL,
    [REMARK9] nvarchar(50) NULL,
    [REMARK8] nvarchar(50) NULL,
    [REMARK7] nvarchar(50) NULL,
    [REMARK6] nvarchar(50) NULL,
    [REMARK5] nvarchar(50) NULL,
    [REMARK4] nvarchar(50) NULL,
    [REMARK3] nvarchar(50) NULL,
    [REMARK2] nvarchar(50) NULL,
    [REMARK1] nvarchar(50) NULL,
    CONSTRAINT [PK_SAM_APPLY_M] PRIMARY KEY CLUSTERED ([APPLY_TYPE], [APPLY_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_SAM_APPLY_M] ON dbo.[SAM_APPLY_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- SAM_IN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SAM_IN_D] (
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1147151132] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_1147151132] DEFAULT ((0)),
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1147151132] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1147151132] DEFAULT ((0)),
    [CLIENT_CONFIRM] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [IN_TYPE] nchar(10) NOT NULL,
    [IN_NO] nchar(20) NOT NULL,
    [APPLY_TYPE] nchar(10) NULL,
    [APPLY_NO] nchar(20) NULL,
    CONSTRAINT [PK_SAM_IN_D] PRIMARY KEY CLUSTERED ([IN_TYPE], [IN_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_SAM_IN_D_1] ON dbo.[SAM_IN_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_SAM_IN_D] ON dbo.[SAM_IN_D] ([APPLY_TYPE], [APPLY_NO]);
GO

------------------------------------------------------------------------------
-- SAM_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SAM_IN_M] (
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(100) NULL,
    [SALES_ID] nchar(10) NULL,
    [CLIENT_CONFIRM] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1163151189] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [IN_TYPE] nchar(10) NOT NULL,
    [IN_NO] nchar(20) NOT NULL,
    [IN_DATE] datetime NULL,
    CONSTRAINT [PK_SAM_IN_M] PRIMARY KEY CLUSTERED ([IN_TYPE], [IN_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_SAM_IN_M] ON dbo.[SAM_IN_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- SAM_OUT_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SAM_OUT_D] (
    [SERIAL_NO] smallint NOT NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1179151246] DEFAULT ((0)),
    [UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [CURR_ID] nchar(10) NULL,
    [CURR_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1179151246] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [OUT_TYPE] nchar(10) NOT NULL,
    [OUT_NO] nchar(20) NOT NULL,
    [IN_TYPE] nchar(10) NULL,
    [IN_NO] nchar(20) NULL,
    [IN_SERIAL_NO] int NULL,
    CONSTRAINT [PK_SAM_OUT_D] PRIMARY KEY CLUSTERED ([OUT_TYPE], [OUT_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_SAM_OUT_D_1] ON dbo.[SAM_OUT_D] ([PRO_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_SAM_OUT_D] ON dbo.[SAM_OUT_D] ([IN_TYPE], [IN_NO], [IN_SERIAL_NO]);
GO

------------------------------------------------------------------------------
-- SAM_OUT_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SAM_OUT_M] (
    [CLIENT_ID] nchar(10) NULL,
    [CLIENT_NAME] nvarchar(100) NULL,
    [SALES_ID] nchar(10) NULL,
    [CLIENT_CONFIRM] nvarchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1195151303] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [OUT_TYPE] nchar(10) NOT NULL,
    [OUT_NO] nchar(20) NOT NULL,
    [OUT_DATE] datetime NULL,
    CONSTRAINT [PK_SAM_OUT_M] PRIMARY KEY CLUSTERED ([OUT_TYPE], [OUT_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_SAM_OUT_M] ON dbo.[SAM_OUT_M] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- SAMPLE_PRO
------------------------------------------------------------------------------
CREATE TABLE dbo.[SAMPLE_PRO] (
    [PRO_NO] nchar(30) NOT NULL,
    [PRO_NAME] nvarchar(100) NULL,
    [PRO_SPEC] nvarchar(100) NULL,
    [SORT_ID] nchar(10) NULL,
    [UNIT_ID] nchar(10) NULL,
    [UNIT_ID_1] nchar(10) NULL,
    [UNIT_RATE_1] float NULL,
    [UNIT_FORMULA_1] nvarchar(50) NULL,
    [UNIT_NARRATE_1] nvarchar(50) NULL,
    [UNIT_ID_2] nchar(10) NULL,
    [UNIT_RATE_2] float NULL,
    [UNIT_FORMULA_2] nvarchar(50) NULL,
    [UNIT_NARRATE_2] nvarchar(50) NULL,
    [UNIT_ID_3] nchar(10) NULL,
    [UNIT_RATE_3] float NULL,
    [UNIT_FORMULA_3] nvarchar(50) NULL,
    [UNIT_NARRATE_3] nvarchar(50) NULL,
    [UNIT_ID_4] nchar(10) NULL,
    [UNIT_RATE_4] float NULL,
    [UNIT_FORMULA_4] nvarchar(50) NULL,
    [UNIT_NARRATE_4] nvarchar(50) NULL,
    [MAIN_SOURCE] char(1) NULL,
    [PRO_TYPE] char(1) NULL,
    [WEIGHT_UNIT_ID] nchar(10) NULL,
    [SUTTLE] float NULL,
    [MANAGE_BATCH] bit NULL,
    [DEPOT_ID] nchar(10) NULL,
    [SUPPLIER_ID] nchar(15) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [COLOR_ID] nchar(10) NULL,
    [STUFF_ID] nchar(10) NULL,
    [TYPE_ID] nchar(10) NULL,
    [PRE_DAYS] smallint NULL,
    [SAFETY_QTY] float NULL,
    [ADDING_QTY] float NULL,
    [LAST_PURCHASE_PRICE] float NULL,
    [STA_PURCHASE_PRICE] float NULL,
    [STA_SALE_PRICE] float NULL,
    [STUFF_COST] float NULL,
    [LABOUR_COST] float NULL,
    [MAKE_COST] float NULL,
    [STA_LABOUR_FEE] float NULL,
    [STA_MAKE_FEE] float NULL,
    [MAKE_FEE] float NULL,
    [STA_FEE] float NULL,
    [LENGTH] float NULL,
    [WIDTH] float NULL,
    [HEIGHT] float NULL,
    [SIZE_UNIT_ID] nchar(10) NULL,
    [CUBAGE] float NULL,
    [AREA] float NULL,
    [GROSS_WEIGHT] float NULL,
    [LOAD20_QTY] float NULL,
    [CUBAGE_UNIT_ID] nchar(10) NULL,
    [SINGLE_LENGTH] float NULL,
    [SINGLE_WIDTH] float NULL,
    [SINGLE_HEIGHT] float NULL,
    [BOX_SUTTLE] float NULL,
    [BOX_GROSS_WEIGHT] float NULL,
    [UNIT_PCS] float NULL,
    [CLIENT_PRO_NO] nvarchar(50) NULL,
    [CLIENT_PRO_NAME] nvarchar(100) NULL,
    [CLIENT_PRO_SPEC] nvarchar(100) NULL,
    [P_WIDTH] float NULL,
    [P_LENGTH] float NULL,
    [PS_LENGTH] float NULL,
    [PS_WIDTH] float NULL,
    [OM_LENGTH] float NULL,
    [OM_WIDTH] float NULL,
    [PS_LENGTH1] float NULL,
    [PS_WIDTH1] float NULL,
    [OM_LENGTH1] float NULL,
    [OM_WIDTH1] float NULL,
    [PS_LENGTH2] float NULL,
    [PS_WIDTH2] float NULL,
    [OM_LENGTH2] float NULL,
    [OM_WIDTH2] float NULL,
    [LIMB_HEIGHT1] float NULL,
    [BOX_HEIGHT] float NULL,
    [LIMB_HEIGHT2] float NULL,
    [PRODUCE_REMARK] nvarchar(500) NULL,
    [LAST_IN_DATE] datetime NULL,
    [LAST_OUT_DATE] datetime NULL,
    [LAST_CHECK_DATE] datetime NULL,
    [BUSINESS_TAG] bit NULL,
    [PRODUCE_STUFF_ID] nchar(10) NULL,
    [KL_TYPE] nvarchar(50) NULL,
    [PRODUCE_ULLAGE] float NULL,
    [PRODUCE_SPEC] nvarchar(80) NULL,
    [PRODUCE_PICTURE] nvarchar(300) NULL,
    [SAMPLE_BOX_NO] nvarchar(50) NULL,
    [BIEMO_NO] nvarchar(50) NULL,
    [FIXED_BOARD_NO] nvarchar(50) NULL,
    [STICKINESS] bit NULL,
    [SINGLE_NAIL] bit NULL,
    [DOUBLE_NAIL] bit NULL,
    [NAIL_ORA] nvarchar(50) NULL,
    [APPLY_MATERIEL_NO] nvarchar(100) NULL,
    [NP_INTENSITY] nvarchar(50) NULL,
    [NY_INTENSITY] nvarchar(50) NULL,
    [BY_INTENSITY] nvarchar(50) NULL,
    [JQ_INTENSITY] nvarchar(50) NULL,
    [ZH_INTENSITY] nvarchar(50) NULL,
    [WATER_RATE] nvarchar(50) NULL,
    [OTHER] nvarchar(100) NULL,
    [QTY] float NULL,
    [PRICE] float NULL,
    [AMOUNT] float NULL,
    [EDITION] char(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1099150961] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [COLOR1] nvarchar(50) NULL,
    [COLOR2] nvarchar(50) NULL,
    [COLOR3] nvarchar(50) NULL,
    [COLOR4] nvarchar(50) NULL,
    [MAX_PURCHASE_PRICE] float NULL,
    [BJ_LENGTH] smallint NULL,
    [BJ_WIDTH] smallint NULL,
    [SET_QTY] smallint NULL,
    [BJ_LENGTH1] smallint NULL,
    [BJ_WIDTH1] smallint NULL,
    [SET_QTY1] smallint NULL,
    [BJ_LENGTH2] smallint NULL,
    [BJ_WIDTH2] smallint NULL,
    [SET_QTY2] smallint NULL,
    [PS_LENGTH3] float NULL,
    [PS_WIDTH3] float NULL,
    [OM_LENGTH3] float NULL,
    [OM_WIDTH3] float NULL,
    [BJ_LENGTH3] smallint NULL,
    [BJ_WIDTH3] smallint NULL,
    [SET_QTY3] smallint NULL,
    [PS_LENGTH4] float NULL,
    [PS_WIDTH4] float NULL,
    [OM_LENGTH4] float NULL,
    [OM_WIDTH4] float NULL,
    [BJ_LENGTH4] smallint NULL,
    [BJ_WIDTH4] smallint NULL,
    [SET_QTY4] smallint NULL,
    [PS_LENGTH5] float NULL,
    [PS_WIDTH5] float NULL,
    [OM_LENGTH5] float NULL,
    [OM_WIDTH5] float NULL,
    [BJ_LENGTH5] smallint NULL,
    [BJ_WIDTH5] smallint NULL,
    [SET_QTY5] smallint NULL,
    [LINE_ID] nchar(10) NULL,
    [WORK_1] nchar(2) NULL CONSTRAINT [DF_WORK_1_1099150961] DEFAULT ((0)),
    [WORK_2] nchar(2) NULL CONSTRAINT [DF_WORK_2_1099150961] DEFAULT ((0)),
    [WORK_3] nchar(2) NULL CONSTRAINT [DF_WORK_3_1099150961] DEFAULT ((0)),
    [WORK_4] nchar(2) NULL CONSTRAINT [DF_WORK_4_1099150961] DEFAULT ((0)),
    [WORK_5] nchar(2) NULL CONSTRAINT [DF_WORK_5_1099150961] DEFAULT ((0)),
    [WORK_6] nchar(2) NULL CONSTRAINT [DF_WORK_6_1099150961] DEFAULT ((0)),
    [WORK_7] nchar(2) NULL CONSTRAINT [DF_WORK_7_1099150961] DEFAULT ((0)),
    [WORK_8] nchar(2) NULL CONSTRAINT [DF_WORK_8_1099150961] DEFAULT ((0)),
    [WORK_9] nchar(2) NULL CONSTRAINT [DF_WORK_9_1099150961] DEFAULT ((0)),
    CONSTRAINT [PK_SAMPLE_PRO] PRIMARY KEY CLUSTERED ([PRO_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_SAMPLE_PRO] ON dbo.[SAMPLE_PRO] ([CLIENT_ID]);
GO

------------------------------------------------------------------------------
-- SFC_AMERCE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_AMERCE_D] (
    [AMERCE_TYPE] nchar(10) NOT NULL,
    [AMERCE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PROCEDURE_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1211151360] DEFAULT ((0)),
    [PRICE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1211151360] DEFAULT ((0)),
    [HARD_RATE] float NULL,
    [EMP_RATE] float NULL,
    [BADSORT_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_SFC_AMERCE_D] PRIMARY KEY CLUSTERED ([AMERCE_TYPE], [AMERCE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_AMERCE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_AMERCE_M] (
    [AMERCE_TYPE] nchar(10) NOT NULL,
    [AMERCE_NO] nchar(20) NOT NULL,
    [AMERCE_DATE] datetime NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PROCEDURE_ID] nchar(10) NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1227151417] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SFC_AMERCE_M] PRIMARY KEY CLUSTERED ([AMERCE_TYPE], [AMERCE_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_BADSORT
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_BADSORT] (
    [BADSORT_ID] nchar(10) NOT NULL,
    [BADSORT_NAME] nvarchar(50) NULL,
    [IF_FEE] bit NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1243151474] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SFC_BADSORT] PRIMARY KEY CLUSTERED ([BADSORT_ID])
);
GO

------------------------------------------------------------------------------
-- SFC_DAILY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_DAILY_D] (
    [DAILY_TYPE] nchar(10) NOT NULL,
    [DAILY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1259151531] DEFAULT ((0)),
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_1259151531] DEFAULT ((0)),
    [PROCESS_QTY] float NULL CONSTRAINT [DF_PROCESS_QTY_1259151531] DEFAULT ((0)),
    [TIME_PERSON] float NULL CONSTRAINT [DF_TIME_PERSON_1259151531] DEFAULT ((0)),
    [INFACT_START] datetime NULL,
    [INFACT_END] datetime NULL,
    [REMARK] nvarchar(100) NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1259151531] DEFAULT ((0)),
    [PROCEDURE_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [TIME_MACHINE] float NULL CONSTRAINT [DF_TIME_MACHINE_1259151531] DEFAULT ((0)),
    [USED_HOURS] float NULL CONSTRAINT [DF_USED_HOURS_1259151531] DEFAULT ((0)),
    CONSTRAINT [PK_SFC_DAILY_D] PRIMARY KEY CLUSTERED ([DAILY_TYPE], [DAILY_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_SFC_DAILY_D] ON dbo.[SFC_DAILY_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_SFC_DAILY_D_1] ON dbo.[SFC_DAILY_D] ([EMP_ID], [PROCEDURE_ID]);
GO

------------------------------------------------------------------------------
-- SFC_DAILY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_DAILY_M] (
    [DAILY_TYPE] nchar(10) NOT NULL,
    [DAILY_NO] nchar(20) NOT NULL,
    [DAILY_DATE] datetime NULL,
    [LINE_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1275151588] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1275151588] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SFC_DAILY_M] PRIMARY KEY CLUSTERED ([DAILY_TYPE], [DAILY_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PAY_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PAY_D] (
    [PAY_TYPE] nchar(10) NOT NULL,
    [PAY_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PROCESS_DESC] nvarchar(60) NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1291151645] DEFAULT ((0)),
    [PRICE] float NULL CONSTRAINT [DF_PRICE_1291151645] DEFAULT ((0)),
    [RANGE] float NULL,
    [UNIT_ID] nchar(12) NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1291151645] DEFAULT ((0)),
    [REMARK] nvarchar(100) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PROCEDURE_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [USED_HOURS] float NULL CONSTRAINT [DF_USED_HOURS_1291151645] DEFAULT ((0)),
    [WORK_HOURS] float NULL CONSTRAINT [DF_WORK_HOURS_1291151645] DEFAULT ((0)),
    CONSTRAINT [PK_SFC_PAY_D] PRIMARY KEY CLUSTERED ([PAY_TYPE], [PAY_NO], [SERIAL_NO])
);
GO
CREATE NONCLUSTERED INDEX [IX_SFC_PAY_D] ON dbo.[SFC_PAY_D] ([PRODUCE_TYPE], [PRODUCE_NO]);
GO
CREATE NONCLUSTERED INDEX [IX_SFC_PAY_D_1] ON dbo.[SFC_PAY_D] ([EMP_ID], [PROCEDURE_ID]);
GO

------------------------------------------------------------------------------
-- SFC_PAY_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PAY_M] (
    [PAY_TYPE] nchar(10) NOT NULL,
    [PAY_NO] nchar(20) NOT NULL,
    [PAY_DATE] datetime NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1307151702] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1307151702] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SFC_PAY_M] PRIMARY KEY CLUSTERED ([PAY_TYPE], [PAY_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PLAN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PLAN_D] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MACHINE_ID] nchar(10) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL,
    [PRODUCE_QTY] float NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [PROCEDURE_ID] nchar(10) NULL,
    [HOURS] float NULL,
    [PLAN_TIME] nvarchar(50) NULL,
    [MOULD_ID] nvarchar(1000) NULL,
    [PACK_SPEC] nvarchar(50) NULL,
    [PRODUCE_REMARK] nvarchar(500) NULL,
    [SHIPMENT_TYPE] nchar(10) NULL CONSTRAINT [DF_SHIPMENT_TYPE_1323151759] DEFAULT (''),
    [SHIPMENT_NO] nchar(20) NULL CONSTRAINT [DF_SHIPMENT_NO_1323151759] DEFAULT (''),
    [SHIPMENT_SERIAL_NO] smallint NULL CONSTRAINT [DF_SHIPMENT_SERIAL_NO_1323151759] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1323151759] DEFAULT ((0)),
    [PERSON_UNIT_HOUR] float NULL CONSTRAINT [DF_PERSON_UNIT_HOUR_1323151759] DEFAULT ((0)),
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1323151759] DEFAULT ((0)),
    [CONGCHAI] float NULL CONSTRAINT [DF_CONGCHAI_1323151759] DEFAULT ((0)),
    CONSTRAINT [PK_SFC_PLAN_D] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PLAN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PLAN_M] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [PLAN_DATE] datetime NULL,
    [SORT_IDX] char(1) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1339151816] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1339151816] DEFAULT ((0)),
    CONSTRAINT [PK_SFC_PLAN_M] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PLAN_MORE
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PLAN_MORE] (
    [PLAN_TYPE] nchar(10) NOT NULL,
    [PLAN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [MACHINE_ID] nchar(10) NULL,
    [CLIENT_ID] nchar(10) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL,
    [PRODUCE_QTY] float NULL,
    [PLAN_TIME] nvarchar(50) NULL,
    [MOULD_ID] nvarchar(1000) NULL,
    [PACK_SPEC] nvarchar(50) NULL,
    [PRODUCE_REMARK] nvarchar(500) NULL,
    [SHIPMENT_TYPE] nchar(10) NULL CONSTRAINT [DF_SHIPMENT_TYPE_1355151873] DEFAULT (''),
    [SHIPMENT_NO] nchar(20) NULL CONSTRAINT [DF_SHIPMENT_NO_1355151873] DEFAULT (''),
    [SHIPMENT_SERIAL_NO] smallint NULL CONSTRAINT [DF_SHIPMENT_SERIAL_NO_1355151873] DEFAULT ((0)),
    [ORDER_TYPE] nchar(10) NULL,
    [ORDER_NO] nchar(20) NULL,
    [ORDER_SERIAL_NO] smallint NULL,
    CONSTRAINT [PK_SFC_PLAN_MORE] PRIMARY KEY CLUSTERED ([PLAN_TYPE], [PLAN_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PLAN_PROCESS_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PLAN_PROCESS_D] (
    [PLAN_PROCESS_TYPE] nchar(10) NOT NULL,
    [PLAN_PROCESS_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [CLIENT_ID] nchar(10) NULL,
    [PRO_NO] nchar(30) NULL,
    [QTY] float NULL,
    [PRODUCE_QTY] float NULL,
    [PROCEDURE_ID] nchar(10) NULL,
    [HOURS] float NULL,
    [PLAN_TIME] nvarchar(50) NULL,
    [PERSON_UNIT_HOUR] float NULL CONSTRAINT [DF_PERSON_UNIT_HOUR_1371151930] DEFAULT ((0)),
    [MACHINE_ID] nchar(10) NULL,
    [REMARK] nchar(100) NULL,
    [CONGCHAI] float NULL CONSTRAINT [DF_CONGCHAI_1371151930] DEFAULT ((0)),
    [PLAN_TYPE] nchar(10) NULL,
    [PLAN_NO] nchar(20) NULL,
    [PLAN_SERIAL_NO] smallint NULL,
    [EMPLOYEE_ID] nvarchar(50) NULL,
    [ALL_PRODUCE_NO] nvarchar(100) NULL,
    CONSTRAINT [PK_SFC_PLAN_PROCESS_D] PRIMARY KEY CLUSTERED ([PLAN_PROCESS_TYPE], [PLAN_PROCESS_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PLAN_PROCESS_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PLAN_PROCESS_M] (
    [PLAN_PROCESS_TYPE] nchar(10) NOT NULL,
    [PLAN_PROCESS_NO] nchar(20) NOT NULL,
    [PLAN_PROCESS_DATE] datetime NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [TIMETYPE_ID] nchar(10) NULL,
    [SORT_IDX] char(1) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1387151987] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1387151987] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SFC_PLAN_PROCESS_M] PRIMARY KEY CLUSTERED ([PLAN_PROCESS_TYPE], [PLAN_PROCESS_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PROCEDURE
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PROCEDURE] (
    [PROCEDURE_ID] nchar(10) NOT NULL,
    [PROCEDURE_NAME] nvarchar(50) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL,
    [PERSON_HOUR_UNIT] float NULL CONSTRAINT [DF_PERSON_HOUR_UNIT_1403152044] DEFAULT ((0)),
    [PERSON_UNIT_HOUR] float NULL CONSTRAINT [DF_PERSON_UNIT_HOUR_1403152044] DEFAULT ((0)),
    [MACHINE_HOUR_UNIT] float NULL CONSTRAINT [DF_MACHINE_HOUR_UNIT_1403152044] DEFAULT ((0)),
    [MACHINE_UNIT_HOUR] float NULL CONSTRAINT [DF_MACHINE_UNIT_HOUR_1403152044] DEFAULT ((0)),
    [PERSON_COST_UNIT] float NULL CONSTRAINT [DF_PERSON_COST_UNIT_1403152044] DEFAULT ((0)),
    [PRODUCE_OVER_RATE] float NULL CONSTRAINT [DF_PRODUCE_OVER_RATE_1403152044] DEFAULT ((0)),
    [PRODUCE_OVER_QTY] float NULL CONSTRAINT [DF_PRODUCE_OVER_QTY_1403152044] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1403152044] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [USE_STAND_TIME] bit NULL,
    [PRICE] float NULL,
    [PRICE_HOUR] float NULL,
    [EMP_RATE] float NULL,
    [EMP_DAYS] int NULL,
    CONSTRAINT [PK_SFC_PROCEDURE] PRIMARY KEY CLUSTERED ([PROCEDURE_ID])
);
GO

------------------------------------------------------------------------------
-- SFC_PROCEDURE_TYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PROCEDURE_TYPE] (
    [PROCEDURE_TYPE_ID] nchar(10) NOT NULL,
    [PROCEDURE_TYPE_NAME] nvarchar(50) NULL,
    [TYPE_ID_SUPERIOR] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1419152101] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [CONTROL] bit NULL CONSTRAINT [DF_CONTROL_1419152101] DEFAULT ((0)),
    [SORT_IDX] smallint NULL CONSTRAINT [DF_SORT_IDX_1419152101] DEFAULT ((0)),
    CONSTRAINT [PK_SFC_PROCEDURE_TYPE] PRIMARY KEY CLUSTERED ([PROCEDURE_TYPE_ID])
);
GO

------------------------------------------------------------------------------
-- SFC_PROCESS_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PROCESS_D] (
    [PRO_NO] nchar(30) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [PROCEDURE_ID] nchar(10) NULL CONSTRAINT [DF_PROCEDURE_ID_1435152158] DEFAULT (''),
    [PROCESS_DESC] nvarchar(500) NULL,
    [LINE_ID] nchar(10) NULL,
    [PERSON_HOUR_UNIT] float NULL CONSTRAINT [DF_PERSON_HOUR_UNIT_1435152158] DEFAULT ((0)),
    [PERSON_UNIT_HOUR] float NULL CONSTRAINT [DF_PERSON_UNIT_HOUR_1435152158] DEFAULT ((0)),
    [MACHINE_HOUR_UNIT] float NULL CONSTRAINT [DF_MACHINE_HOUR_UNIT_1435152158] DEFAULT ((0)),
    [MACHINE_UNIT_HOUR] float NULL CONSTRAINT [DF_MACHINE_UNIT_HOUR_1435152158] DEFAULT ((0)),
    [PERSON_COST_UNIT] float NULL CONSTRAINT [DF_PERSON_COST_UNIT_1435152158] DEFAULT ((0)),
    [MACHINE_COST_UNIT] float NULL CONSTRAINT [DF_MACHINE_COST_UNIT_1435152158] DEFAULT ((0)),
    [STANDARD_TIME] float NULL CONSTRAINT [DF_STANDARD_TIME_1435152158] DEFAULT ((0)),
    [STANDARD_TIME_TAG] bit NULL CONSTRAINT [DF_STANDARD_TIME_TAG_1435152158] DEFAULT ((0)),
    [CONTROL] bit NULL CONSTRAINT [DF_CONTROL_1435152158] DEFAULT ((1)),
    [BAD_RATE] float NULL CONSTRAINT [DF_BAD_RATE_1435152158] DEFAULT ((0)),
    [RECOUP_UNIT] float NULL CONSTRAINT [DF_RECOUP_UNIT_1435152158] DEFAULT ((0)),
    [PROCESS_QTY] float NULL CONSTRAINT [DF_PROCESS_QTY_1435152158] DEFAULT ((1)),
    [PRODUCE_OVER_RATE] float NULL CONSTRAINT [DF_PRODUCE_OVER_RATE_1435152158] DEFAULT ((0)),
    [PRODUCE_OVER_QTY] float NULL CONSTRAINT [DF_PRODUCE_OVER_QTY_1435152158] DEFAULT ((0)),
    [ELEMENT_PRO_NO] nchar(30) NULL,
    [PROCEDURE_TYPE_ID] nchar(10) NULL CONSTRAINT [DF_PROCEDURE_TYPE_ID_1435152158] DEFAULT (''),
    [ELEMENT_PRO_NAME] nvarchar(100) NULL CONSTRAINT [DF_ELEMENT_PRO_NAME_1435152158] DEFAULT (''),
    [ELEMENT_PRO_SPEC] nvarchar(100) NULL CONSTRAINT [DF_ELEMENT_PRO_SPEC_1435152158] DEFAULT (''),
    [ELEMENT_COLOR_ID] nchar(10) NULL CONSTRAINT [DF_ELEMENT_COLOR_ID_1435152158] DEFAULT (''),
    [ELEMENT_STUFF_ID] nchar(10) NULL CONSTRAINT [DF_ELEMENT_STUFF_ID_1435152158] DEFAULT (''),
    [ELEMENT_UNIT_ID] nchar(10) NULL CONSTRAINT [DF_ELEMENT_UNIT_ID_1435152158] DEFAULT (''),
    [ELEMENT_LENGTH] float NULL,
    [ELEMENT_WIDTH] float NULL,
    [ELEMENT_HEIGHT] float NULL,
    [ELEMENT_SIZE_UNIT_ID] nchar(10) NULL,
    [PRICE] float NULL,
    [HARD_RATE] float NULL,
    [PERSON_QTY] float NULL CONSTRAINT [DF_PERSON_QTY_1435152158] DEFAULT ((0)),
    [CONGCHAI] float NULL CONSTRAINT [DF_CONGCHAI_1435152158] DEFAULT ((0)),
    CONSTRAINT [PK_SFC_PROCESS_D] PRIMARY KEY CLUSTERED ([PRO_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PROCESS_IN_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PROCESS_IN_D] (
    [PROCESS_IN_TYPE] nchar(10) NOT NULL,
    [PROCESS_IN_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PROCEDURE_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1451152215] DEFAULT ((0)),
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_1451152215] DEFAULT ((0)),
    [PRICE] float NULL,
    [REMARK] nvarchar(500) NULL,
    [HARD_RATE] float NULL,
    [EMP_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1451152215] DEFAULT ((0)),
    CONSTRAINT [PK_SFC_PROCESS_IN_D] PRIMARY KEY CLUSTERED ([PROCESS_IN_TYPE], [PROCESS_IN_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PROCESS_IN_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PROCESS_IN_M] (
    [PROCESS_IN_TYPE] nchar(10) NOT NULL,
    [PROCESS_IN_NO] nchar(20) NOT NULL,
    [PROCESS_IN_DATE] datetime NULL,
    [PROCEDURE_ID] nchar(10) NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1467152272] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    CONSTRAINT [PK_SFC_PROCESS_IN_M] PRIMARY KEY CLUSTERED ([PROCESS_IN_TYPE], [PROCESS_IN_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PROCESS_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PROCESS_M] (
    [PRO_NO] nchar(30) NOT NULL,
    [BATCH_QTY] float NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PRICE_SUM] float NULL,
    [PRICE_MAX] float NULL,
    CONSTRAINT [PK_SFC_PROCESS_M] PRIMARY KEY CLUSTERED ([PRO_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PRODUCE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PRODUCE_D] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [PROCESS_QTY] float NULL CONSTRAINT [DF_PROCESS_QTY_1499152386] DEFAULT ((0)),
    [PRICE] float NULL,
    [HARD_RATE] float NULL,
    [PROCEDURE_ID] nchar(10) NULL,
    [PLAN_START] datetime NULL,
    [PLAN_END] datetime NULL,
    [INFACT_START] datetime NULL,
    [INFACT_END] datetime NULL,
    [STANDARD_PERSON_TIME] float NULL CONSTRAINT [DF_STANDARD_PERSON_TIME_1499152386] DEFAULT ((0)),
    [STANDARD_MACHINE_TIME] float NULL CONSTRAINT [DF_STANDARD_MACHINE_TIME_1499152386] DEFAULT ((0)),
    [INFACT_PERSON_TIME] float NULL CONSTRAINT [DF_INFACT_PERSON_TIME_1499152386] DEFAULT ((0)),
    [INFACT_MACHINE_TIME] float NULL CONSTRAINT [DF_INFACT_MACHINE_TIME_1499152386] DEFAULT ((0)),
    [FINISHED_QTY] float NULL CONSTRAINT [DF_FINISHED_QTY_1499152386] DEFAULT ((0)),
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_1499152386] DEFAULT ((0)),
    [RECOUP_AMOUNT] float NULL CONSTRAINT [DF_RECOUP_AMOUNT_1499152386] DEFAULT ((0)),
    [PROCESS_OVER_QTY] float NULL CONSTRAINT [DF_PROCESS_OVER_QTY_1499152386] DEFAULT ((0)),
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1499152386] DEFAULT ((0)),
    CONSTRAINT [PK_SFC_PRODUCE_D] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_PRODUCE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_PRODUCE_M] (
    [PRODUCE_TYPE] nchar(10) NOT NULL,
    [PRODUCE_NO] nchar(20) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1515152443] DEFAULT ((0)),
    [FINISHED_PERSON] nchar(20) NULL,
    [FINISHED_DATE] datetime NULL,
    [FINISHED_TAG] bit NULL CONSTRAINT [DF_FINISHED_TAG_1515152443] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SFC_PRODUCE_M] PRIMARY KEY CLUSTERED ([PRODUCE_TYPE], [PRODUCE_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_REWORK_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_REWORK_D] (
    [REWORK_TYPE] nchar(10) NOT NULL,
    [REWORK_NO] nchar(20) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [PROCEDURE_ID] nchar(10) NULL,
    [EMP_ID] nchar(10) NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PRODUCE_SERIAL_NO] smallint NULL,
    [QTY] float NULL CONSTRAINT [DF_QTY_1531152500] DEFAULT ((0)),
    [BAD_QTY] float NULL CONSTRAINT [DF_BAD_QTY_1531152500] DEFAULT ((0)),
    [PRICE] float NULL,
    [HARD_RATE] float NULL,
    [EMP_RATE] float NULL,
    [AMOUNT] float NULL CONSTRAINT [DF_AMOUNT_1531152500] DEFAULT ((0)),
    [BADSORT_ID] nchar(10) NULL,
    [IF_FEE] bit NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_SFC_REWORK_D] PRIMARY KEY CLUSTERED ([REWORK_TYPE], [REWORK_NO], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SFC_REWORK_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SFC_REWORK_M] (
    [REWORK_TYPE] nchar(10) NOT NULL,
    [REWORK_NO] nchar(20) NOT NULL,
    [REWORK_DATE] datetime NULL,
    [PRODUCE_TYPE] nchar(10) NULL,
    [PRODUCE_NO] nchar(20) NULL,
    [PROCEDURE_ID] nchar(10) NOT NULL,
    [EMP_ID] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1547152557] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SFC_REWORK_M] PRIMARY KEY CLUSTERED ([REWORK_TYPE], [REWORK_NO])
);
GO

------------------------------------------------------------------------------
-- SORT
------------------------------------------------------------------------------
CREATE TABLE dbo.[SORT] (
    [SORT_ID] nchar(10) NOT NULL,
    [SORT_NAME] nvarchar(50) NOT NULL,
    [ACCOUNT_ID] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1563152614] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SORT] PRIMARY KEY CLUSTERED ([SORT_ID])
);
GO

------------------------------------------------------------------------------
-- STUFF
------------------------------------------------------------------------------
CREATE TABLE dbo.[STUFF] (
    [STUFF_ID] nchar(10) NOT NULL,
    [STUFF_NAME] nvarchar(50) NULL,
    [HOLE_SHAPE] nchar(10) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1579152671] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [PRICE] float NULL CONSTRAINT [DF_PRICE_1579152671] DEFAULT ((0)),
    [WEIGHT] float NULL CONSTRAINT [DF_WEIGHT_1579152671] DEFAULT ((0)),
    [SPECIFIC_GRAVITY] float NULL CONSTRAINT [DF_SPECIFIC_GRAVITY_1579152671] DEFAULT ((0)),
    CONSTRAINT [PK_STUFF] PRIMARY KEY CLUSTERED ([STUFF_ID])
);
GO

------------------------------------------------------------------------------
-- STUFF_GRADE
------------------------------------------------------------------------------
CREATE TABLE dbo.[STUFF_GRADE] (
    [GRADE_ID] nchar(10) NOT NULL,
    [GRADE_NAME] nvarchar(100) NULL,
    [REMARK] nvarchar(1000) NULL,
    [CREATE_PERSON] nchar(40) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(40) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(40) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL,
    [CI] nchar(20) NULL,
    [OWNER] nchar(20) NULL,
    [OWNER_G] nchar(20) NULL,
    CONSTRAINT [PK_STUFF_GRADE] PRIMARY KEY CLUSTERED ([GRADE_ID])
);
GO

------------------------------------------------------------------------------
-- SUPPLIER
------------------------------------------------------------------------------
CREATE TABLE dbo.[SUPPLIER] (
    [SUPPLIER_ID] nchar(15) NOT NULL,
    [SUPPLIER_NAME] nvarchar(150) NULL,
    [FULL_NAME_CN] nvarchar(100) NULL,
    [FULL_NAME_EN] nvarchar(100) NULL,
    [REG_ADDR_CN] nvarchar(100) NULL,
    [REG_ADDR_EN] nvarchar(100) NULL,
    [DELI_ADDR_CN] nvarchar(100) NULL,
    [DELI_ADDR_EN] nvarchar(100) NULL,
    [INV_ADDR_CN] nvarchar(100) NULL,
    [INV_ADDR_EN] varchar(100) NULL,
    [BANK_ID] nvarchar(50) NULL,
    [BANK_NAME_CN] nvarchar(100) NULL,
    [BANK_NAME_EN] nvarchar(100) NULL,
    [BANK_ADDR_CN] nvarchar(100) NULL,
    [BANK_ADDR_EN] nvarchar(100) NULL,
    [BANK_TEL] nvarchar(100) NULL,
    [CONPANY_NO_CN] nvarchar(100) NULL,
    [TAX_ID] nchar(10) NULL,
    [TAX_NO] nvarchar(50) NULL,
    [FIRST_TRADE_DATE] datetime NULL,
    [LAST_TRADE_DATE] datetime NULL,
    [CAPITAL_SUM] float NULL,
    [LICENSE] nvarchar(50) NULL,
    [TURNOVER_SUM] float NULL,
    [EMPOYEE_SUM] int NULL CONSTRAINT [DF_EMPOYEE_SUM_1595152728] DEFAULT ((0)),
    [SELL_GRADE_TAG] char(1) NULL,
    [CREDIT_GRADE_TAG] char(1) NULL,
    [ACCOUNT_ID] nvarchar(50) NULL,
    [CURR_ID] nchar(10) NULL,
    [PRICE_CONDITION] nvarchar(100) NULL,
    [PAY_CONDITION] nvarchar(100) NULL,
    [COMPANT_SELL_TYPE] nvarchar(100) NULL,
    [ACCOUNT_VERIFY_DAY] nchar(10) NULL,
    [DAY_NO26] nchar(10) NULL,
    [DAY_NO25] nchar(10) NULL,
    [GATHERING_DAY] nchar(10) NULL,
    [PAYMENT_DAY] int NULL,
    [CREDIT_LIMIT_QTY] float NULL,
    [CREDIT_LIMIT_NUM] float NULL,
    [PURCHASE_ID] nchar(10) NULL,
    [OUTER_TAG] bit NULL CONSTRAINT [DF_OUTER_TAG_1595152728] DEFAULT ((0)),
    [BUSINESS_TAG] bit NULL CONSTRAINT [DF_BUSINESS_TAG_1595152728] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1595152728] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [TAX_TYPE] nchar(1) NULL CONSTRAINT [DF_TAX_TYPE_1595152728] DEFAULT ('O'),
    [HEADER_ID] nchar(10) NULL,
    [LINKMAN] nvarchar(100) NULL,
    [TEL] nvarchar(100) NULL,
    [FAX] nvarchar(100) NULL,
    [MIN_BUY_AMOUNT] int NULL CONSTRAINT [DF_MIN_BUY_AMOUNT_1595152728] DEFAULT ((0)),
    [PREPAY_SUM] float NULL,
    [TYPE] nchar(1) NULL CONSTRAINT [DF_TYPE_1595152728] DEFAULT ('1'),
    CONSTRAINT [PK_SUPPLIER] PRIMARY KEY CLUSTERED ([SUPPLIER_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_SUPPLIER] ON dbo.[SUPPLIER] ([HEADER_ID]);
GO

------------------------------------------------------------------------------
-- SUPPLIER_LINKMAN
------------------------------------------------------------------------------
CREATE TABLE dbo.[SUPPLIER_LINKMAN] (
    [SUPPLIER_ID] nchar(15) NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [LINKMAN] nvarchar(100) NULL,
    [DEPT_ID] nvarchar(100) NULL,
    [TEL] nvarchar(100) NULL,
    [TEL_CDMA] nvarchar(100) NULL,
    [FAX] nvarchar(100) NULL,
    [TEL_MOBILE] nvarchar(100) NULL,
    [WEBSITE] nvarchar(100) NULL,
    [EMAIL] nvarchar(100) NULL,
    [QQ_MSN] nvarchar(100) NULL,
    [REMARK] nvarchar(100) NULL,
    [REMARK1] nvarchar(100) NULL,
    [REMARK2] nvarchar(100) NULL,
    [REMARK3] nvarchar(100) NULL,
    CONSTRAINT [PK_SUPPLIER_LINKMAN] PRIMARY KEY CLUSTERED ([SUPPLIER_ID], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SUPPLIER_PRICE_D
------------------------------------------------------------------------------
CREATE TABLE dbo.[SUPPLIER_PRICE_D] (
    [SUPPLIER_ID] nchar(15) NOT NULL,
    [PRO_NO] nchar(30) NOT NULL,
    [UNIT_ID] nchar(10) NOT NULL,
    [CURR_ID] nchar(10) NOT NULL,
    [TAX_ID] nchar(10) NOT NULL,
    [TAX_TYPE] char(1) NOT NULL CONSTRAINT [DF_TAX_TYPE_1627152842] DEFAULT ('O'),
    [CURR_RATE] float NULL CONSTRAINT [DF_CURR_RATE_1627152842] DEFAULT ((0)),
    [PRICE] float NULL CONSTRAINT [DF_PRICE_1627152842] DEFAULT ((0)),
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_1627152842] DEFAULT ((0)),
    [PROCESS_PRICE] float NULL CONSTRAINT [DF_PROCESS_PRICE_1627152842] DEFAULT ((0)),
    [SUPPLIER_PRO_NO] nvarchar(50) NULL,
    [REBATE] float NOT NULL,
    [OLD_PRICE] float NULL,
    [OLD_PRICE_DATE] datetime NULL,
    [QUOTE_TYPE] nchar(10) NULL,
    [QUOTE_NO] nchar(20) NULL,
    [QUOTE_SERIAL_NO] smallint NULL,
    [VERIFY_PRICE_DATE] datetime NULL,
    [REMARK] nvarchar(100) NULL,
    [IN_EFFECT_DATE] datetime NULL CONSTRAINT [DF_IN_EFFECT_DATE_1627152842] DEFAULT ('2049.12.31'),
    CONSTRAINT [PK_SUPPLIER_PRICE_D] PRIMARY KEY CLUSTERED ([SUPPLIER_ID], [PRO_NO], [UNIT_ID], [CURR_ID], [TAX_ID], [TAX_TYPE], [REBATE])
);
GO
CREATE NONCLUSTERED INDEX [IX_SUPPLIER_PRICE_D] ON dbo.[SUPPLIER_PRICE_D] ([PRO_NO], [SUPPLIER_ID]);
GO

------------------------------------------------------------------------------
-- SUPPLIER_PRICE_M
------------------------------------------------------------------------------
CREATE TABLE dbo.[SUPPLIER_PRICE_M] (
    [SUPPLIER_ID] nchar(15) NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1643152899] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SUPPLIER_PRICE_M] PRIMARY KEY CLUSTERED ([SUPPLIER_ID])
);
GO

------------------------------------------------------------------------------
-- SYS_FILE
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYS_FILE] (
    [T_ID] nvarchar(100) NOT NULL,
    [KEY_VALUE] nvarchar(200) NOT NULL,
    [SERIAL_NO] int NOT NULL,
    [FILE_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [UPLOAD_DATE] datetime NULL,
    CONSTRAINT [PK_SYS_FILE] PRIMARY KEY CLUSTERED ([T_ID], [KEY_VALUE], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SYS_WORK_TASK
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYS_WORK_TASK] (
    [WORK_TYPE] nchar(10) NOT NULL,
    [WORK_NO] nchar(20) NOT NULL,
    [WORK_DATE] smalldatetime NULL,
    [PLAN_BEGIN_TIME] smalldatetime NULL,
    [PLAN_FINISH_TIME] smalldatetime NULL,
    [W_M_IDX] int NULL,
    [W_M_DESC] nvarchar(500) NULL,
    [TASK_TYPE] char(1) NULL,
    [WORK_DESC] nvarchar(2000) NULL,
    [TEST_STEP] nvarchar(2000) NULL,
    [TEST_QUESTION] varchar(2000) NULL,
    [QUESTION_REASON] varchar(2000) NULL,
    [QUESTION_ANSWER] varchar(2000) NULL,
    [BEGIN_TIME] smalldatetime NULL,
    [FINISH_TIME] smalldatetime NULL,
    [FINISH_TAG] bit NULL CONSTRAINT [DF_FINISH_TAG_2075154438] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2075154438] DEFAULT ((0)),
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SYS_WORK_TASK] PRIMARY KEY CLUSTERED ([WORK_TYPE], [WORK_NO])
);
GO

------------------------------------------------------------------------------
-- SYSDD
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDD] (
    [USER_ID] nchar(10) NOT NULL,
    [M_IDX] int NOT NULL,
    [EXEC_TAG] char(1) NULL CONSTRAINT [DF_EXEC_TAG_1659152956] DEFAULT ('A'),
    [ADDNEW_TAG] bit NULL CONSTRAINT [DF_ADDNEW_TAG_1659152956] DEFAULT ((0)),
    [DELETE_TAG] bit NULL CONSTRAINT [DF_DELETE_TAG_1659152956] DEFAULT ((0)),
    [EDIT_TAG] bit NULL CONSTRAINT [DF_EDIT_TAG_1659152956] DEFAULT ((0)),
    [REPORT_TAG] bit NULL CONSTRAINT [DF_REPORT_TAG_1659152956] DEFAULT ((0)),
    [COST_TAG] bit NULL CONSTRAINT [DF_COST_TAG_1659152956] DEFAULT ((0)),
    [SETUP_TAG] bit NULL CONSTRAINT [DF_SETUP_TAG_1659152956] DEFAULT ((0)),
    [SECRECY_TAG] bit NULL CONSTRAINT [DF_SECRECY_TAG_1659152956] DEFAULT ((0)),
    [ENDCASE_TAG] bit NULL CONSTRAINT [DF_ENDCASE_TAG_1659152956] DEFAULT ((0)),
    [UNENDCASE_TAG] bit NULL CONSTRAINT [DF_UNENDCASE_TAG_1659152956] DEFAULT ((0)),
    [OTHER1_TAG] bit NULL CONSTRAINT [DF_OTHER1_TAG_1659152956] DEFAULT ((0)),
    [OTHER2_TAG] bit NULL CONSTRAINT [DF_OTHER2_TAG_1659152956] DEFAULT ((0)),
    [OTHER3_TAG] bit NULL CONSTRAINT [DF_OTHER3_TAG_1659152956] DEFAULT ((0)),
    [OTHER4_TAG] bit NULL CONSTRAINT [DF_OTHER4_TAG_1659152956] DEFAULT ((0)),
    [DENY_VIEW_FIELD_MASTER] varchar(3000) NULL,
    [DENY_VIEW_FIELD_DETAIL] varchar(3000) NULL,
    [DENY_NEW_FIELD_MASTER] varchar(3000) NULL,
    [DENY_NEW_FIELD_DETAIL] varchar(3000) NULL,
    [DENY_MODI_FIELD_MASTER] varchar(3000) NULL,
    [DENY_MODI_FIELD_DETAIL] varchar(3000) NULL,
    [DATA_FILTER] varchar(3000) NULL,
    [CI] nchar(10) NULL CONSTRAINT [DF_CI_1659152956] DEFAULT ('_ccorp'),
    [OPERFLAG] bit NULL CONSTRAINT [DF_OPERFLAG_1659152956] DEFAULT ((0)),
    [APPROVE_TAG] bit NULL CONSTRAINT [DF_APPROVE_TAG_1659152956] DEFAULT ((0)),
    [DEAPPROVE_TAG] bit NULL CONSTRAINT [DF_DEAPPROVE_TAG_1659152956] DEFAULT ((0)),
    [FILE_VIEW_TAG] bit NULL CONSTRAINT [DF_FILE_VIEW_TAG_1659152956] DEFAULT ((0)),
    [FILE_UPDA_TAG] bit NULL CONSTRAINT [DF_FILE_UPDA_TAG_1659152956] DEFAULT ((0)),
    [FILE_EDIT_TAG] bit NULL CONSTRAINT [DF_FILE_EDIT_TAG_1659152956] DEFAULT ((0)),
    [FILE_DELE_TAG] bit NULL CONSTRAINT [DF_FILE_DELE_TAG_1659152956] DEFAULT ((0)),
    [FORM_DESIGN_TAG] bit NOT NULL CONSTRAINT [DF_FORM_DESIGN_TAG_1659152956] DEFAULT ((0)),
    [FORM_ADJUST_TAG] bit NOT NULL CONSTRAINT [DF_FORM_ADJUST_TAG_1659152956] DEFAULT ((0)),
    CONSTRAINT [PK_SYSDD] PRIMARY KEY CLUSTERED ([USER_ID], [M_IDX])
);
GO

------------------------------------------------------------------------------
-- SYSDD_EXEC_TYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDD_EXEC_TYPE] (
    [EXEC_ID] char(1) NOT NULL,
    [EXEC_DESC] nvarchar(50) NULL,
    CONSTRAINT [PK_SYSDD_EXEC_TYPE] PRIMARY KEY CLUSTERED ([EXEC_ID])
);
GO

------------------------------------------------------------------------------
-- SYSDD_REPORT
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDD_REPORT] (
    [USER_ID] nchar(10) NOT NULL,
    [M_IDX] int NOT NULL,
    [REPORT_ID] nchar(50) NOT NULL,
    [PREVIEW_TAG] bit NULL CONSTRAINT [DF_PREVIEW_TAG_1691153070] DEFAULT ((0)),
    [PRINT_TAG] bit NULL CONSTRAINT [DF_PRINT_TAG_1691153070] DEFAULT ((0)),
    [EXPORT_TAG] bit NULL CONSTRAINT [DF_EXPORT_TAG_1691153070] DEFAULT ((0)),
    [DATA_FILTER] nvarchar(2000) NULL,
    [FAVORITE_TAG] bit NOT NULL CONSTRAINT [DF_FAVORITE_TAG_1691153070] DEFAULT ((0)),
    [SORT_IDX] int NULL,
    [LAST_RUN_AT] datetime2(7) NULL,
    CONSTRAINT [PK_SYSDD_REPORT] PRIMARY KEY CLUSTERED ([USER_ID], [M_IDX], [REPORT_ID])
);
GO

------------------------------------------------------------------------------
-- SYSDF
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDF] (
    [LOG_IDX] bigint IDENTITY(1,1) NOT NULL,
    [M_IDX] int NULL CONSTRAINT [DF_M_IDX_1707153127] DEFAULT ((0)),
    [RECORD_IDX] nvarchar(100) NULL,
    [CONTENT] nvarchar(1000) NULL,
    [TYPE] nvarchar(50) NULL,
    [EXEC_BY] nvarchar(50) NULL,
    [EXEC_DATE] datetime NULL CONSTRAINT [DF_EXEC_DATE_1707153127] DEFAULT (getdate()),
    [CI] nchar(10) NULL CONSTRAINT [DF_CI_1707153127] DEFAULT ('_ccorp'),
    [OPERFLAG] bit NULL CONSTRAINT [DF_OPERFLAG_1707153127] DEFAULT ((1)),
    CONSTRAINT [PK_SYSDF] PRIMARY KEY CLUSTERED ([LOG_IDX])
);
GO

------------------------------------------------------------------------------
-- SYSDG
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDG] (
    [G_IDX] nchar(10) NOT NULL,
    [G_DESC] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1723153184] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SYSDG] PRIMARY KEY CLUSTERED ([G_IDX])
);
GO

------------------------------------------------------------------------------
-- SYSDG_USER
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDG_USER] (
    [G_IDX] nchar(10) NOT NULL,
    [USER_ID] nchar(10) NOT NULL,
    CONSTRAINT [PK_SYSDG_USER] PRIMARY KEY CLUSTERED ([G_IDX], [USER_ID])
);
GO

------------------------------------------------------------------------------
-- SYSDH
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDH] (
    [G_IDX] nchar(10) NOT NULL,
    [M_IDX] int NOT NULL,
    [EXEC_TAG] char(1) NULL CONSTRAINT [DF_EXEC_TAG_1755153298] DEFAULT ('A'),
    [ADDNEW_TAG] bit NULL CONSTRAINT [DF_ADDNEW_TAG_1755153298] DEFAULT ((0)),
    [DELETE_TAG] bit NULL CONSTRAINT [DF_DELETE_TAG_1755153298] DEFAULT ((0)),
    [EDIT_TAG] bit NULL CONSTRAINT [DF_EDIT_TAG_1755153298] DEFAULT ((0)),
    [REPORT_TAG] bit NULL CONSTRAINT [DF_REPORT_TAG_1755153298] DEFAULT ((0)),
    [COST_TAG] bit NULL CONSTRAINT [DF_COST_TAG_1755153298] DEFAULT ((0)),
    [SETUP_TAG] bit NULL CONSTRAINT [DF_SETUP_TAG_1755153298] DEFAULT ((0)),
    [SECRECY_TAG] bit NULL CONSTRAINT [DF_SECRECY_TAG_1755153298] DEFAULT ((0)),
    [ENDCASE_TAG] bit NULL CONSTRAINT [DF_ENDCASE_TAG_1755153298] DEFAULT ((0)),
    [UNENDCASE_TAG] bit NULL CONSTRAINT [DF_UNENDCASE_TAG_1755153298] DEFAULT ((0)),
    [OTHER1_TAG] bit NULL CONSTRAINT [DF_OTHER1_TAG_1755153298] DEFAULT ((0)),
    [OTHER2_TAG] bit NULL CONSTRAINT [DF_OTHER2_TAG_1755153298] DEFAULT ((0)),
    [OTHER3_TAG] bit NULL CONSTRAINT [DF_OTHER3_TAG_1755153298] DEFAULT ((0)),
    [OTHER4_TAG] bit NULL CONSTRAINT [DF_OTHER4_TAG_1755153298] DEFAULT ((0)),
    [DENY_VIEW_FIELD_MASTER] varchar(3000) NULL,
    [DENY_VIEW_FIELD_DETAIL] varchar(3000) NULL,
    [DENY_NEW_FIELD_MASTER] varchar(3000) NULL,
    [DENY_NEW_FIELD_DETAIL] varchar(3000) NULL,
    [DENY_MODI_FIELD_MASTER] varchar(3000) NULL,
    [DENY_MODI_FIELD_DETAIL] varchar(3000) NULL,
    [DATA_FILTER] varchar(3000) NULL,
    [CI] nchar(10) NULL CONSTRAINT [DF_CI_1755153298] DEFAULT ('_ccorp'),
    [OPERFLAG] bit NULL CONSTRAINT [DF_OPERFLAG_1755153298] DEFAULT ((0)),
    [APPROVE_TAG] bit NULL CONSTRAINT [DF_APPROVE_TAG_1755153298] DEFAULT ((0)),
    [DEAPPROVE_TAG] bit NULL CONSTRAINT [DF_DEAPPROVE_TAG_1755153298] DEFAULT ((0)),
    [FILE_VIEW_TAG] bit NULL CONSTRAINT [DF_FILE_VIEW_TAG_1755153298] DEFAULT ((0)),
    [FILE_UPDA_TAG] bit NULL CONSTRAINT [DF_FILE_UPDA_TAG_1755153298] DEFAULT ((0)),
    [FILE_EDIT_TAG] bit NULL CONSTRAINT [DF_FILE_EDIT_TAG_1755153298] DEFAULT ((0)),
    [FILE_DELE_TAG] bit NULL CONSTRAINT [DF_FILE_DELE_TAG_1755153298] DEFAULT ((0)),
    [FORM_DESIGN_TAG] bit NOT NULL CONSTRAINT [DF_FORM_DESIGN_TAG_1755153298] DEFAULT ((0)),
    [FORM_ADJUST_TAG] bit NOT NULL CONSTRAINT [DF_FORM_ADJUST_TAG_1755153298] DEFAULT ((0)),
    CONSTRAINT [PK_SYSDH] PRIMARY KEY CLUSTERED ([G_IDX], [M_IDX])
);
GO

------------------------------------------------------------------------------
-- SYSDH_REPORT
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDH_REPORT] (
    [G_IDX] nchar(10) NOT NULL,
    [M_IDX] int NOT NULL,
    [REPORT_ID] nchar(50) NOT NULL,
    [PREVIEW_TAG] bit NULL CONSTRAINT [DF_PREVIEW_TAG_1771153355] DEFAULT ((0)),
    [PRINT_TAG] bit NULL CONSTRAINT [DF_PRINT_TAG_1771153355] DEFAULT ((0)),
    [EXPORT_TAG] bit NULL CONSTRAINT [DF_EXPORT_TAG_1771153355] DEFAULT ((0)),
    [DATA_FILTER] nvarchar(2000) NULL,
    [FAVORITE_TAG] bit NOT NULL CONSTRAINT [DF_FAVORITE_TAG_1771153355] DEFAULT ((0)),
    [SORT_IDX] int NULL,
    [LAST_RUN_AT] datetime2(7) NULL,
    CONSTRAINT [PK_SYSDH_REPORT] PRIMARY KEY CLUSTERED ([G_IDX], [M_IDX], [REPORT_ID])
);
GO

------------------------------------------------------------------------------
-- SYSDL
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDL] (
    [USER_ID] nchar(10) NOT NULL,
    [USER_PWD] nvarchar(50) NULL,
    [G_IDX] nchar(10) NULL,
    [EMP_ID] nchar(10) NOT NULL,
    [SIR_ID] nchar(10) NULL,
    [ACTIVE_TAG] bit NULL CONSTRAINT [DF_ACTIVE_TAG_1787153412] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1787153412] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SYSDL] PRIMARY KEY CLUSTERED ([USER_ID])
);
GO

------------------------------------------------------------------------------
-- SYSDN
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSDN] (
    [EMP_ID] nchar(10) NOT NULL,
    [EMP_NAME] nvarchar(50) NULL,
    [DUTY_ID] nchar(10) NULL,
    [DEPT_ID] nchar(10) NULL,
    [COMPANY_ID] nchar(10) NULL,
    [PIC] nvarchar(150) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1803153469] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SYSDN] PRIMARY KEY CLUSTERED ([EMP_ID])
);
GO

------------------------------------------------------------------------------
-- SYSQD_CONDITION
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQD_CONDITION] (
    [STEP_ID] bigint IDENTITY(1,1) NOT NULL,
    [USER_ID] nchar(10) NULL,
    [T_ID] varchar(100) NULL,
    [T_ID_R] varchar(100) NULL,
    [F_ID] varchar(250) NULL,
    [F_DESC] nvarchar(250) NULL,
    [LEFT_KH] varchar(20) NULL,
    [OPERATOR] nvarchar(50) NULL,
    [CENTENT_KEY_1] nvarchar(500) NULL,
    [CENTENT_DESC_1] nvarchar(50) NULL,
    [RIGHT_KH] varchar(20) NULL,
    [LOGIC] varchar(20) NULL,
    [IS_USE] bit NULL CONSTRAINT [DF_IS_USE_1835153583] DEFAULT ((0)),
    CONSTRAINT [PK_SYSQD_CONDITION] PRIMARY KEY CLUSTERED ([STEP_ID])
);
GO

------------------------------------------------------------------------------
-- SYSQL_COND_DFT
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQL_COND_DFT] (
    [STEP_ID] bigint IDENTITY(1,1) NOT NULL,
    [USER_ID] nchar(10) NULL,
    [T_ID] varchar(100) NULL,
    [T_ID_R] varchar(100) NULL,
    [F_ID] varchar(250) NULL,
    [F_DESC] nvarchar(250) NULL,
    [LEFT_KH] varchar(20) NULL,
    [OPERATOR] nvarchar(50) NULL,
    [CENTENT_KEY_1] nvarchar(500) NULL,
    [CENTENT_DESC_1] nvarchar(50) NULL,
    [RIGHT_KH] varchar(20) NULL,
    [LOGIC] varchar(20) NULL,
    [IS_USE] bit NULL CONSTRAINT [DF_IS_USE_1883153754] DEFAULT ((0)),
    CONSTRAINT [PK_SYSQL_COND_DFT] PRIMARY KEY CLUSTERED ([STEP_ID])
);
GO

------------------------------------------------------------------------------
-- SYSQL_CONDITION
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQL_CONDITION] (
    [STEP_ID] bigint IDENTITY(1,1) NOT NULL,
    [USER_ID] nchar(10) NULL,
    [T_ID] varchar(100) NULL,
    [T_ID_R] varchar(100) NULL,
    [F_ID] varchar(250) NULL,
    [F_DESC] nvarchar(250) NULL,
    [LEFT_KH] varchar(20) NULL,
    [OPERATOR] nvarchar(50) NULL,
    [CENTENT_KEY_1] nvarchar(500) NULL,
    [CENTENT_DESC_1] nvarchar(50) NULL,
    [RIGHT_KH] varchar(20) NULL,
    [LOGIC] varchar(20) NULL,
    [IS_USE] bit NULL CONSTRAINT [DF_IS_USE_1867153697] DEFAULT ((0)),
    CONSTRAINT [PK_SYSQL_CONDITION] PRIMARY KEY CLUSTERED ([STEP_ID])
);
GO

------------------------------------------------------------------------------
-- SYSQL_DEFAULT
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQL_DEFAULT] (
    [T_ID] varchar(50) NOT NULL,
    [T_ID_R] varchar(50) NOT NULL,
    [F_IDX] int NOT NULL,
    [F_ID] nvarchar(100) NULL,
    CONSTRAINT [PK_SYSQL_DEFAULT] PRIMARY KEY CLUSTERED ([T_ID], [T_ID_R], [F_IDX])
);
GO

------------------------------------------------------------------------------
-- SYSQL_FIELDS
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQL_FIELDS] (
    [STEP_ID] bigint IDENTITY(1,1) NOT NULL,
    [USER_ID] nchar(10) NULL,
    [T_ID] varchar(100) NULL,
    [T_ID_R] varchar(100) NULL,
    [F_ID] nvarchar(100) NULL,
    [F_IDX] int NULL CONSTRAINT [DF_F_IDX_1915153868] DEFAULT ((0)),
    CONSTRAINT [PK_SYSQL_FIELDS] PRIMARY KEY CLUSTERED ([STEP_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_SYSQL_FIELDS_USER_TARGET] ON dbo.[SYSQL_FIELDS] ([USER_ID], [T_ID], [T_ID_R]) INCLUDE ([F_ID], [F_IDX]);
GO

------------------------------------------------------------------------------
-- SYSQQ
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQQ] (
    [USER_ID] nchar(10) NOT NULL,
    [T_ID] nvarchar(100) NOT NULL,
    [F_ID] nvarchar(100) NULL,
    [F_VALUE] nvarchar(100) NULL,
    CONSTRAINT [PK_SYSQQ] PRIMARY KEY CLUSTERED ([USER_ID], [T_ID])
);
GO

------------------------------------------------------------------------------
-- SYSQR
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQR] (
    [USER_ID] nchar(10) NOT NULL,
    [R_M_IDX] int NOT NULL,
    [REPORT_ID] nchar(50) NOT NULL,
    [PAPER_SIZE] nvarchar(50) NULL,
    [SORT_ASC] bit NULL CONSTRAINT [DF_SORT_ASC_1947153982] DEFAULT ((0)),
    [SHOW_GROUP] bit NULL CONSTRAINT [DF_SHOW_GROUP_1947153982] DEFAULT ((0)),
    [SHOW_DETAIL] bit NULL CONSTRAINT [DF_SHOW_DETAIL_1947153982] DEFAULT ((0)),
    [HEADER_ID] nchar(10) NULL,
    [TAIL_ID] nchar(10) NULL,
    [IS_LAST] bit NULL CONSTRAINT [DF_IS_LAST_1947153982] DEFAULT ((0)),
    [PRINTER_NAME] nvarchar(50) NULL,
    [LAST_SORT] nvarchar(50) NULL,
    CONSTRAINT [PK_SYSQR] PRIMARY KEY CLUSTERED ([USER_ID], [R_M_IDX], [REPORT_ID])
);
GO

------------------------------------------------------------------------------
-- SYSQR_DA
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQR_DA] (
    [M_IDX] int NOT NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_1963154039] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_SYSQR_DA] PRIMARY KEY CLUSTERED ([M_IDX])
);
GO

------------------------------------------------------------------------------
-- SYSQR_DEFAULT
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQR_DEFAULT] (
    [M_IDX] int NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [F_ID] nvarchar(50) NULL,
    [F_TYPE] nchar(1) NULL,
    [F_EXPR] nvarchar(2000) NULL,
    [F_DESC] nvarchar(50) NULL,
    [F_VALUE] nvarchar(500) NULL,
    [REMARK] nvarchar(500) NULL,
    [PARA_NAME] nvarchar(50) NULL,
    [FILTER_TEMPLATE] nvarchar(max) NULL,
    CONSTRAINT [PK_SYSQR_DEFAULT] PRIMARY KEY CLUSTERED ([M_IDX], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SYSQR_TYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQR_TYPE] (
    [F_TYPE] nchar(1) NOT NULL,
    [F_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_SYSQR_TYPE] PRIMARY KEY CLUSTERED ([F_TYPE])
);
GO

------------------------------------------------------------------------------
-- SYSQR_USER
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSQR_USER] (
    [USER_ID] nchar(10) NOT NULL,
    [M_IDX] int NOT NULL,
    [SERIAL_NO] smallint NOT NULL,
    [F_TAG] bit NULL,
    [F_VALUE] nvarchar(500) NULL,
    CONSTRAINT [PK_SYSQR_USER] PRIMARY KEY CLUSTERED ([USER_ID], [M_IDX], [SERIAL_NO])
);
GO

------------------------------------------------------------------------------
-- SYSSS
------------------------------------------------------------------------------
CREATE TABLE dbo.[SYSSS] (
    [SUPPLIER_DAYS] int NOT NULL CONSTRAINT [DF_SUPPLIER_DAYS_2027154267] DEFAULT ((1000000)),
    [CLIENT_DAYS] int NULL CONSTRAINT [DF_CLIENT_DAYS_2027154267] DEFAULT ((1000000)),
    [PRODUCT_DAYS] int NULL CONSTRAINT [DF_PRODUCT_DAYS_2027154267] DEFAULT ((1000000)),
    [REMARK] nvarchar(500) NULL CONSTRAINT [DF_REMARK_2027154267] DEFAULT (''),
    [MRP_DAYS] int NULL,
    [MRP_WEEKS] int NULL,
    [MRP_MONTHS] int NULL,
    [PRO_MRP] bit NULL CONSTRAINT [DF_PRO_MRP_2027154267] DEFAULT ((0)),
    [FITOUT_TAG] bit NULL CONSTRAINT [DF_FITOUT_TAG_2027154267] DEFAULT ((1)),
    [PRO_EDITION_TAG] bit NULL,
    [PUR_APPLY_TAG] bit NULL CONSTRAINT [DF_PUR_APPLY_TAG_2027154267] DEFAULT ((1)),
    [SEND_PRODUCE_TAG] bit NULL CONSTRAINT [DF_SEND_PRODUCE_TAG_2027154267] DEFAULT ((0)),
    [LOGIN_F12] bit NULL,
    [QUICK_SEARCH_ALL] bit NULL,
    [QTY_LINE1] int NULL CONSTRAINT [DF_QTY_LINE1_2027154267] DEFAULT ((100)),
    [QTY_LINE2] int NULL CONSTRAINT [DF_QTY_LINE2_2027154267] DEFAULT ((1000)),
    [QTY_LINE3] int NULL CONSTRAINT [DF_QTY_LINE3_2027154267] DEFAULT ((2000)),
    [QTY_PS_WIDTH] int NULL CONSTRAINT [DF_QTY_PS_WIDTH_2027154267] DEFAULT ((2000)),
    [SEND_TAG] bit NULL,
    [FITOUT_PRODUCE_TRANSFER_TAG] bit NULL,
    [FITOUT_ORDER_TAG] bit NULL,
    [FITOUT_PRODUCE_TAG] bit NULL,
    [SEND_ORDER_FITOUT_TAG] bit NULL CONSTRAINT [DF_SEND_ORDER_FITOUT_TAG_2027154267] DEFAULT ((1)),
    [SEND_ORDER_TAG] bit NULL CONSTRAINT [DF_SEND_ORDER_TAG_2027154267] DEFAULT ((1)),
    [SEND_PRODUCE_FITOUT_TAG] bit NULL CONSTRAINT [DF_SEND_PRODUCE_FITOUT_TAG_2027154267] DEFAULT ((0)),
    [SEND_FITOUT_TAG] bit NULL CONSTRAINT [DF_SEND_FITOUT_TAG_2027154267] DEFAULT ((0)),
    [SEND_PRODUCE_TRANSFER_TAG] bit NULL CONSTRAINT [DF_SEND_PRODUCE_TRANSFER_TAG_2027154267] DEFAULT ((0)),
    [RETURN_SEND_TAG] bit NULL CONSTRAINT [DF_RETURN_SEND_TAG_2027154267] DEFAULT ((1)),
    [RETURN_ORDER_SEND_TAG] bit NULL CONSTRAINT [DF_RETURN_ORDER_SEND_TAG_2027154267] DEFAULT ((1)),
    [RETURN_PRODUCE_SEND_TAG] bit NULL CONSTRAINT [DF_RETURN_PRODUCE_SEND_TAG_2027154267] DEFAULT ((1)),
    [CREATE_DATE] datetime NULL,
    [PRODUCE_PLAN_MOC_TAG] bit NULL CONSTRAINT [DF_PRODUCE_PLAN_MOC_TAG_2027154267] DEFAULT ((1)),
    [PRODUCE_ORDER_TAG] bit NULL CONSTRAINT [DF_PRODUCE_ORDER_TAG_2027154267] DEFAULT ((1)),
    [PRODUCE_IN_ORDER_TAG] bit NULL CONSTRAINT [DF_PRODUCE_IN_ORDER_TAG_2027154267] DEFAULT ((1)),
    [COP_RETURN_DEPOT_TAG] bit NULL CONSTRAINT [DF_COP_RETURN_DEPOT_TAG_2027154267] DEFAULT ((1)),
    [PUR_APPLY_PLAN_PUR_TAG] bit NULL CONSTRAINT [DF_PUR_APPLY_PLAN_PUR_TAG_2027154267] DEFAULT ((0)),
    [PUR_PRODUCE_APPLY_TAG] bit NULL CONSTRAINT [DF_PUR_PRODUCE_APPLY_TAG_2027154267] DEFAULT ((0)),
    [PUR_CANCEL_DEPOT_TAG] bit NULL CONSTRAINT [DF_PUR_CANCEL_DEPOT_TAG_2027154267] DEFAULT ((0)),
    CONSTRAINT [PK_SYSSS] PRIMARY KEY CLUSTERED ([SUPPLIER_DAYS])
);
GO

------------------------------------------------------------------------------
-- TABLES
------------------------------------------------------------------------------
CREATE TABLE dbo.[TABLES] (
    [T_ID] nvarchar(100) NOT NULL,
    [T_DESC] nvarchar(200) NOT NULL,
    [T_TYPE] nvarchar(50) NULL,
    [T_KIND] nvarchar(50) NULL,
    [T_REMARK] nvarchar(500) NULL,
    [FK_T_ID_1] nvarchar(100) NULL,
    [FK_T_ID_2] nvarchar(100) NULL,
    [DF_CONDITION] nvarchar(500) NULL,
    [DF_VERIFY] nvarchar(500) NULL,
    [QUERY_RELATION] nvarchar(2000) NULL,
    [LAST_UPDATE_BY] nvarchar(50) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CAN_IMPORT] bit NULL,
    [FK_T_ID_3] nvarchar(100) NULL,
    [FK_T_ID_4] nvarchar(100) NULL,
    [FK_T_ID_5] nvarchar(100) NULL,
    CONSTRAINT [PK_TABLES] PRIMARY KEY CLUSTERED ([T_ID])
);
GO

------------------------------------------------------------------------------
-- TASK
------------------------------------------------------------------------------
CREATE TABLE dbo.[TASK] (
    [TASK_ID] nchar(10) NOT NULL,
    [TASK_NAME] nvarchar(500) NULL,
    [TASK_DATE] datetime NULL,
    [PLAN_START_TIME] char(30) NULL,
    [PLAN_END_TIME] char(30) NULL,
    [M_IDX] int NULL CONSTRAINT [DF_M_IDX_2107154552] DEFAULT ((0)),
    [TASK_TYPE] smallint NULL,
    [TEST_STEP] nvarchar(500) NULL,
    [TEST_QUESTION] nvarchar(500) NULL,
    [WHYS] nvarchar(500) NULL,
    [METHOD] nvarchar(500) NULL,
    [FACT_START_TIME] char(30) NULL,
    [FACT_END_TIME] char(30) NULL,
    [SUCCESS_TAG] bit NULL CONSTRAINT [DF_SUCCESS_TAG_2107154552] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2107154552] DEFAULT ((0)),
    [CI] nchar(8) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_TASK] PRIMARY KEY CLUSTERED ([TASK_ID])
);
GO

------------------------------------------------------------------------------
-- TAX
------------------------------------------------------------------------------
CREATE TABLE dbo.[TAX] (
    [TAX_ID] nchar(10) NOT NULL,
    [TAX_NAME] nchar(30) NULL,
    [TAX_RATE] float NULL CONSTRAINT [DF_TAX_RATE_2123154609] DEFAULT ((0)),
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_2123154609] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    CONSTRAINT [PK_TAX] PRIMARY KEY CLUSTERED ([TAX_ID])
);
GO

------------------------------------------------------------------------------
-- TAX_TYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[TAX_TYPE] (
    [TAX_TYPE] nchar(1) NOT NULL,
    [TAX_TYPE_NAME] nchar(30) NULL,
    CONSTRAINT [PK_TAX_TYPE] PRIMARY KEY CLUSTERED ([TAX_TYPE])
);
GO

------------------------------------------------------------------------------
-- TYPE
------------------------------------------------------------------------------
CREATE TABLE dbo.[TYPE] (
    [TYPE_ID] nchar(10) NOT NULL,
    [TYPE_NAME] nvarchar(50) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_7671075] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [AREA_MULTI] float NULL CONSTRAINT [DF_AREA_MULTI_7671075] DEFAULT ((0)),
    CONSTRAINT [PK_TYPE] PRIMARY KEY CLUSTERED ([TYPE_ID])
);
GO

------------------------------------------------------------------------------
-- TYPE_FORMULA
------------------------------------------------------------------------------
CREATE TABLE dbo.[TYPE_FORMULA] (
    [TYPE_ID] nchar(10) NOT NULL,
    [HOLE_SHAPE] nchar(10) NOT NULL,
    [UNIT_PRICE_EXP] nvarchar(90) NULL,
    [WIDTH_EXP] nvarchar(500) NULL,
    [LENGTH_EXP] nvarchar(500) NULL,
    [LIMB_HEIGHT_EXP1] nvarchar(500) NULL,
    [LIMB_HEIGHT_EXP2] nvarchar(500) NULL,
    [BOX_HEIGHT_EXP] nvarchar(500) NULL,
    [REMARK] nvarchar(500) NULL,
    CONSTRAINT [PK_TYPE_FORMULA] PRIMARY KEY CLUSTERED ([TYPE_ID], [HOLE_SHAPE])
);
GO

------------------------------------------------------------------------------
-- UNIT
------------------------------------------------------------------------------
CREATE TABLE dbo.[UNIT] (
    [UNIT_ID] nchar(10) NOT NULL,
    [UNIT_NAME] nvarchar(50) NULL,
    [UNIT_TYPE] char(1) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] nchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_PERSON] nchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_39671189] DEFAULT ((0)),
    [CI] nchar(10) NULL,
    [OWNER] nchar(10) NULL,
    [OWNER_G] nchar(10) NULL,
    [BASE_RATE] float NULL CONSTRAINT [DF_BASE_RATE_39671189] DEFAULT ((0)),
    CONSTRAINT [PK_UNIT] PRIMARY KEY CLUSTERED ([UNIT_ID])
);
GO

------------------------------------------------------------------------------
-- WF_APPROVE
------------------------------------------------------------------------------
CREATE TABLE dbo.[WF_APPROVE] (
    [M_IDX] int NOT NULL CONSTRAINT [DF_M_IDX_87671360] DEFAULT ((0)),
    [KEY_VALUE] varchar(200) NOT NULL,
    [KEY_VALUE_DESC] varchar(300) NULL,
    [OWNER] nchar(10) NULL,
    [LAST_UPDATE_BY] nchar(20) NULL,
    CONSTRAINT [PK_WF_APPROVE] PRIMARY KEY CLUSTERED ([M_IDX], [KEY_VALUE])
);
GO

------------------------------------------------------------------------------
-- WF_FUNCTION_INFO
------------------------------------------------------------------------------
CREATE TABLE dbo.[WF_FUNCTION_INFO] (
    [serialno] float NULL,
    [function_key] nvarchar(255) NULL,
    [function_expression] nvarchar(255) NULL,
    [remark] nvarchar(255) NULL
);
GO

------------------------------------------------------------------------------
-- WF_MONITOR
------------------------------------------------------------------------------
CREATE TABLE dbo.[WF_MONITOR] (
    [WF_ID] bigint IDENTITY(1,1) NOT NULL,
    [WF_M_IDX] int NOT NULL CONSTRAINT [DF_WF_M_IDX_119671474] DEFAULT ((0)),
    [KEY_VALUE] varchar(200) NOT NULL,
    [KEY_VALUE_DESC] varchar(300) NULL,
    [WF_STATE] char(1) NULL,
    [UPDATE_SUBFLOW] char(3) NULL,
    [START_USER] nvarchar(50) NULL,
    [START_DATE] datetime NULL,
    CONSTRAINT [PK_WF_MONITOR] PRIMARY KEY CLUSTERED ([WF_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_WF_MONITOR] ON dbo.[WF_MONITOR] ([WF_M_IDX], [KEY_VALUE]);
GO

------------------------------------------------------------------------------
-- WF_MYTASK
------------------------------------------------------------------------------
CREATE TABLE dbo.[WF_MYTASK] (
    [MYTASK_ID] bigint IDENTITY(1,1) NOT NULL,
    [WF_ID] bigint NULL,
    [SUBFLOW_NO] char(3) NULL,
    [APPROVER] varchar(20) NULL,
    [APP_EMP_ID] varchar(20) NULL,
    [APPROVE_TAG] bit NULL CONSTRAINT [DF_APPROVE_TAG_135671531] DEFAULT ((0)),
    [APPROVE_MSG] varchar(3000) NULL CONSTRAINT [DF_APPROVE_MSG_135671531] DEFAULT (''),
    [APPROVE_DATE] datetime NULL,
    [APPROVE_STATE] char(1) NULL CONSTRAINT [DF_APPROVE_STATE_135671531] DEFAULT (''),
    [IS_AUTO_EXEC] bit NULL CONSTRAINT [DF_IS_AUTO_EXEC_135671531] DEFAULT ((0)),
    [IS_SIGN] bit NULL CONSTRAINT [DF_IS_SIGN_135671531] DEFAULT ((0)),
    [IS_MUST_SIGN] bit NULL CONSTRAINT [DF_IS_MUST_SIGN_135671531] DEFAULT ((0)),
    [PASS_PERCENT] int NULL CONSTRAINT [DF_PASS_PERCENT_135671531] DEFAULT ((0)),
    [IS_EFFECT] bit NULL CONSTRAINT [DF_IS_EFFECT_135671531] DEFAULT ((0)),
    [PRE_MUST_UNDER] bit NULL CONSTRAINT [DF_PRE_MUST_UNDER_135671531] DEFAULT ((0)),
    [CAN_SIR_AGENCY] bit NULL CONSTRAINT [DF_CAN_SIR_AGENCY_135671531] DEFAULT ((0)),
    [SUBFLOW_DESC] varchar(50) NULL,
    [APPROVE_POWER] bit NULL CONSTRAINT [DF_APPROVE_POWER_135671531] DEFAULT ((0)),
    [FORWARD_POWER] bit NULL CONSTRAINT [DF_FORWARD_POWER_135671531] DEFAULT ((0)),
    [IS_CURRENT] bit NULL CONSTRAINT [DF_IS_CURRENT_135671531] DEFAULT ((0)),
    CONSTRAINT [PK_WF_MYTASK] PRIMARY KEY CLUSTERED ([MYTASK_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_WF_MYTASK_1] ON dbo.[WF_MYTASK] ([APPROVER]);
GO
CREATE NONCLUSTERED INDEX [IX_WF_MYTASK] ON dbo.[WF_MYTASK] ([WF_ID], [SUBFLOW_NO]);
GO

------------------------------------------------------------------------------
-- WF_MYTASK_LOG
------------------------------------------------------------------------------
CREATE TABLE dbo.[WF_MYTASK_LOG] (
    [MYTASK_ID] bigint NULL CONSTRAINT [DF_MYTASK_ID_151671588] DEFAULT ((0)),
    [WF_ID] bigint NULL CONSTRAINT [DF_WF_ID_151671588] DEFAULT ((0)),
    [SUBFLOW_NO] char(3) NULL,
    [APP_EMP_ID] varchar(20) NULL,
    [APPROVE_MSG] varchar(3000) NULL,
    [APPROVE_DATE] datetime NULL,
    [APPROVE_STATE] char(1) NULL,
    [MYTASK_LOG_ID] bigint IDENTITY(1,1) NOT NULL,
    [SUBFLOW_DESC] varchar(50) NULL,
    CONSTRAINT [PK_WF_MYTASK_LOG] PRIMARY KEY CLUSTERED ([MYTASK_LOG_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_WF_MYTASK_LOG_1] ON dbo.[WF_MYTASK_LOG] ([WF_ID]);
GO
CREATE NONCLUSTERED INDEX [IX_WF_MYTASK_LOG] ON dbo.[WF_MYTASK_LOG] ([MYTASK_ID]);
GO

------------------------------------------------------------------------------
-- WFFORM
------------------------------------------------------------------------------
CREATE TABLE dbo.[WFFORM] (
    [WF_M_IDX] int NOT NULL,
    [FLOW_NAME] varchar(100) NULL,
    [REMARK] nvarchar(500) NULL,
    [CREATE_PERSON] varchar(20) NULL,
    [CREATE_DATE] datetime NULL,
    [LAST_UPDATE_BY] varchar(20) NULL,
    [LAST_UPDATE_DATE] datetime NULL,
    [CONFIRM_TAG] bit NULL CONSTRAINT [DF_CONFIRM_TAG_55671246] DEFAULT ((0)),
    [CONFIRM_PERSON] varchar(20) NULL,
    [CONFIRM_DATE] datetime NULL,
    CONSTRAINT [PK_WFFORM] PRIMARY KEY CLUSTERED ([WF_M_IDX])
);
GO

------------------------------------------------------------------------------
-- WFFORM_FLOW
------------------------------------------------------------------------------
CREATE TABLE dbo.[WFFORM_FLOW] (
    [WF_M_IDX] int NOT NULL,
    [SORT_NO] char(3) NOT NULL,
    [SUBFLOW_DESC] varchar(50) NULL CONSTRAINT [DF_SUBFLOW_DESC_71671303] DEFAULT (''),
    [EXEC_PERSON] varchar(1000) NULL CONSTRAINT [DF_EXEC_PERSON_71671303] DEFAULT (''),
    [MUST_SIGNER] varchar(1000) NULL CONSTRAINT [DF_MUST_SIGNER_71671303] DEFAULT (''),
    [PERSON_CONDITION] varchar(4000) NULL CONSTRAINT [DF_PERSON_CONDITION_71671303] DEFAULT (''),
    [EXEC_CONDITION] varchar(4000) NULL CONSTRAINT [DF_EXEC_CONDITION_71671303] DEFAULT (''),
    [IS_AUTO_EXEC] bit NULL CONSTRAINT [DF_IS_AUTO_EXEC_71671303] DEFAULT ((0)),
    [AUTO_EXEC_CONDITION] varchar(4000) NULL CONSTRAINT [DF_AUTO_EXEC_CONDITION_71671303] DEFAULT (''),
    [PASS_PERCENT] int NULL CONSTRAINT [DF_PASS_PERCENT_71671303] DEFAULT ((0)),
    [IS_SIGN] bit NULL CONSTRAINT [DF_IS_SIGN_71671303] DEFAULT ((0)),
    [IS_EFFECT] bit NULL CONSTRAINT [DF_IS_EFFECT_71671303] DEFAULT ((0)),
    [PRE_MUST_UNDER] bit NULL CONSTRAINT [DF_PRE_MUST_UNDER_71671303] DEFAULT ((0)),
    [CAN_SIR_AGENCY] bit NULL CONSTRAINT [DF_CAN_SIR_AGENCY_71671303] DEFAULT ((0)),
    [REMARK] varchar(6000) NULL,
    [PERSON_APP_POWER] varchar(1000) NULL,
    [PERSON_FORWARD_POWER] varchar(1000) NULL CONSTRAINT [DF_PERSON_FORWARD_POWER_71671303] DEFAULT (''),
    CONSTRAINT [PK_WFFORM_FLOW] PRIMARY KEY CLUSTERED ([WF_M_IDX], [SORT_NO])
);
GO

------------------------------------------------------------------------------
-- WORKBENCH_DEFINITION_SNAPSHOT
------------------------------------------------------------------------------
CREATE TABLE dbo.[WORKBENCH_DEFINITION_SNAPSHOT] (
    [SNAPSHOT_ID] bigint IDENTITY(1,1) NOT NULL,
    [MODULE_ID] int NOT NULL,
    [VERSION] int NOT NULL,
    [DEFINITION_JSON] nvarchar(max) NOT NULL,
    [SOURCE_METADATA_VERSION] nvarchar(100) NULL,
    [VALIDATION_STATUS] nvarchar(20) NOT NULL,
    [VALIDATION_REPORT_JSON] nvarchar(max) NULL,
    [PUBLISHED_BY] nvarchar(100) NULL,
    [PUBLISHED_AT] datetime2(3) NOT NULL CONSTRAINT [DF_PUBLISHED_AT_1717633212] DEFAULT (sysdatetime()),
    [IS_CURRENT] bit NOT NULL CONSTRAINT [DF_IS_CURRENT_1717633212] DEFAULT ((0)),
    CONSTRAINT [PK_WORKBENCH_DEFINITION_SNAPSHOT] PRIMARY KEY CLUSTERED ([SNAPSHOT_ID])
);
GO
CREATE NONCLUSTERED INDEX [IX_WORKBENCH_DEFINITION_SNAPSHOT_MODULE] ON dbo.[WORKBENCH_DEFINITION_SNAPSHOT] ([MODULE_ID], [VERSION]);
GO

------------------------------------------------------------------------------
-- WORKBENCH_IDEMPOTENCY
------------------------------------------------------------------------------
CREATE TABLE dbo.[WORKBENCH_IDEMPOTENCY] (
    [IDEMPOTENCY_KEY] nvarchar(128) NOT NULL,
    [MODULE_ID] int NOT NULL,
    [ACTION] nvarchar(20) NOT NULL,
    [RESULT_KEY] nvarchar(1000) NULL,
    [FLOW_STARTED] bit NOT NULL CONSTRAINT [DF_FLOW_STARTED_1781633440] DEFAULT ((0)),
    [CREATED_AT] datetime2(3) NOT NULL CONSTRAINT [DF_CREATED_AT_1781633440] DEFAULT (sysdatetime()),
    CONSTRAINT [PK_WORKBENCH_IDEMPOTENCY] PRIMARY KEY CLUSTERED ([IDEMPOTENCY_KEY])
);
GO
CREATE NONCLUSTERED INDEX [IX_WORKBENCH_IDEMPOTENCY_SCOPE] ON dbo.[WORKBENCH_IDEMPOTENCY] ([MODULE_ID], [ACTION], [CREATED_AT]);
GO

------------------------------------------------------------------------------
-- WORKBENCH_MODULE_DIRTY
------------------------------------------------------------------------------
CREATE TABLE dbo.[WORKBENCH_MODULE_DIRTY] (
    [MODULE_ID] int NOT NULL,
    [DIRTY_TAG] bit NOT NULL CONSTRAINT [DF_DIRTY_TAG_1653632984] DEFAULT ((1)),
    [LAST_MODIFIED_BY] nvarchar(100) NULL,
    [LAST_MODIFIED_AT] datetime2(3) NOT NULL CONSTRAINT [DF_LAST_MODIFIED_AT_1653632984] DEFAULT (sysdatetime()),
    CONSTRAINT [PK_WORKBENCH_MODULE_DIRTY] PRIMARY KEY CLUSTERED ([MODULE_ID])
);
GO

------------------------------------------------------------------------------
-- 外键（5）
------------------------------------------------------------------------------

ALTER TABLE dbo.[REPORT_FORM_LAYOUT_VERSION] WITH CHECK ADD CONSTRAINT [FK_REPORT_FORM_LAYOUT_VERSION_LAYOUT] FOREIGN KEY ([LAYOUT_ID]) REFERENCES dbo.[REPORT_FORM_LAYOUT] ([LAYOUT_ID]) ON DELETE CASCADE;
ALTER TABLE dbo.[AUDIT_FIELD_CHANGE] WITH CHECK ADD CONSTRAINT [FK_AUDIT_FIELD_CHANGE_EVENT] FOREIGN KEY ([EVENT_ID]) REFERENCES dbo.[AUDIT_EVENT] ([EVENT_ID]);
ALTER TABLE dbo.[ASSISTANT_MESSAGE] WITH CHECK ADD CONSTRAINT [FK_ASSISTANT_MESSAGE_SESSION] FOREIGN KEY ([SESSION_ID]) REFERENCES dbo.[ASSISTANT_SESSION] ([ID]);
ALTER TABLE dbo.[FIELD_DATASOURCE] WITH CHECK ADD CONSTRAINT [FK_FIELD_DATASOURCE_FIELDS] FOREIGN KEY ([T_ID], [F_ID]) REFERENCES dbo.[FIELDS] ([T_ID], [F_ID]) ON DELETE CASCADE;
ALTER TABLE dbo.[REPORT_FORM_BINDING] WITH CHECK ADD CONSTRAINT [FK_REPORT_FORM_BINDING_LAYOUT] FOREIGN KEY ([LAYOUT_ID]) REFERENCES dbo.[REPORT_FORM_LAYOUT] ([LAYOUT_ID]);
GO

------------------------------------------------------------------------------
-- 标量/表值函数
------------------------------------------------------------------------------

-- f_char_compare
CREATE  FUNCTION dbo.f_char_compare (@char varchar(1000),@sign char(1))
RETURNS varchar(1000) AS  
BEGIN 
	declare @char_now varchar(1000),
		@char_split varchar(1000),
		@intpos int,
		@int_startpos int,
		@int_nowpos int,
		@int_leftpos int
	declare @char_compare varchar(1000)

	set @char_now=@char+@sign
	set @int_nowpos=0
	set @int_leftpos=0
	set @int_startpos=0
	set @char_compare=''

	set @intpos=charindex(@sign,@char_now)
	while (@intpos>=0)
	begin
		if @intPos>0 
			set @char_split=left(@char_now,@intPos-1)
		else
			set @char_split=@char_now
			
		if @char_split!=@char_now
		begin
			set @int_leftpos=charindex(@char_split,@char_now,len(@char_split)+1)
			while (@int_leftpos>0)
			begin
				if substring(@char_now,@int_leftpos-1,1)=@sign and substring(@char_now,@int_leftpos+len(@char_split),1)=@sign
				begin
					set @char_now=left(@char_now,@int_leftpos-1)+right(@char_now,len(@char_now)-@int_leftpos-len(@char_split))
					set @int_leftpos=charindex(@char_split,@char_now,@int_leftpos)
				end
				else
				begin
					set @int_nowpos=@int_leftpos+len(@char_split)
					set @int_leftpos=charindex(@char_split,@char_now,@int_nowpos+1)
				end
			end
		end
		set @char_now=right(@char_now,len(@char_now)-@intPos)
		if @intPos>0
			set @intPos=charindex(@sign,@char_now)
		else
			set @intPos=-1
		set @char_compare=@char_compare+@char_split+@sign
	end
	--returns.
	return left(@char_compare,len(@char_compare)-2)
END
GO

-- f_char_compare_array
CREATE FUNCTION dbo.f_char_compare_array (@source_char varchar(4000),@target_char varchar(4000),@sign varchar(1))
RETURNS bit  AS 
BEGIN 
	declare @issame bit
	
	declare @t_1 table(value varchar(1000))
	declare @t_2 table(value varchar(1000))
	
	insert into @t_1
		select value from dbo.f_char_split_to_table(@source_char,@sign)
	insert into @t_2
		select value from dbo.f_char_split_to_table(@target_char,@sign)
		
	if (exists(select * from @t_1 A left join @t_2 B on A.value=B.value where isnull(A.value,'')!='' and isnull(B.value,'')!=''))
		set @issame=1
	else
		set @issame=0

return @issame
END
GO

-- f_char_compare_resemble
CREATE  FUNCTION dbo.f_char_compare_resemble (@char varchar(1000),@sign char(1))
RETURNS varchar(1000) AS  
BEGIN 
	declare @char_now varchar(1000),
		@char_split varchar(1000),
		@intpos int,
		@int_startpos int,
		@int_nowpos int,
		@int_leftpos int,
		 @next_pos int
		

	declare @char_compare varchar(1000)


	set @char_now=@char+@sign
	set @int_nowpos=0
	set @int_leftpos=0
	set @int_startpos=0
	set @next_pos=0
	set @char_compare=''


	set @intpos=charindex(@sign,@char_now)
	while (@intpos>=0)
	begin
		if @intPos>0 --if not is last character then.
			set @char_split=left(@char_now,@intPos-1)
		else--Otherwise, if is last character then.
			set @char_split=@char_now
			
		if @char_split!=@char_now
		begin
			set @int_leftpos=charindex(@char_split,@char_now,len(@char_split)+1)
			while (@int_leftpos>0)
			begin
				if substring(@char_now,@int_leftpos-1,1)=@sign
				begin
					set @next_pos=charindex(@sign,@char_now,@int_leftpos)
					if @next_pos>0
					begin	
						set @char_now=left(@char_now,@int_leftpos-1)+right(@char_now,len(@char_now)-@next_pos)
						set @int_leftpos=charindex(@char_split,@char_now,@int_leftpos)
					end
					else
					begin
						set @char_now=left(@char_now,@int_leftpos-1)
					end
				end
				else
				begin
					set @int_leftpos=charindex(@char_split,@char_now,@int_leftpos+1)
				end
			end
		end
		set @char_now=right(@char_now,len(@char_now)-@intPos)
		if @intPos>0--setup next character.
			set @intPos=charindex(@sign,@char_now)
		else
			set @intPos=-1--exit while repetition.
		set @char_compare=@char_compare+@char_split+@sign
	end
	set @char_compare= left(@char_compare,len(@char_compare)-2)
	set @char_now=reverse(@char_compare)+@sign
	set @int_nowpos=0
	set @int_leftpos=0
	set @int_startpos=0
	set @char_compare=''

	set @intpos=charindex(@sign,@char_now)
	while (@intpos>=0)
	begin
		if @intPos>0 
			set @char_split=left(@char_now,@intPos-1)
		else
			set @char_split=@char_now
		set @char_split=reverse(@char_split)
		set @char_now=left(@char_now,len(@char_now)-1)
		set @char_now=reverse(@char_now)+@sign
		if @char_split!=@char_now
		begin

			set @int_leftpos=charindex(@char_split,@char_now,1)
			while (@int_leftpos>0)
			begin
				if @int_leftpos=1 or substring(@char_now,@int_leftpos-1,1)=@sign
				begin
					set @next_pos=charindex(@sign,@char_now,@int_leftpos+1)
					if @next_pos>0
					begin
						set @char_now=left(@char_now,@int_leftpos-1)+right(@char_now,len(@char_now)-@next_pos)
						set @int_leftpos=charindex(@char_split,@char_now,@int_leftpos)
					end
				end
				else
				begin
					set @int_leftpos=charindex(@char_split,@char_now,@int_leftpos+2)
				end
			end
		end
		if len(@char_now)>0
		begin
			set @char_now=left(@char_now,len(@char_now)-1)
			set @char_now=reverse(@char_now)+@sign
		end
		if @intPos>0 and len(@char_now)>0
			set @intPos=charindex(@sign,@char_now)
		else
			set @intPos=-1
		set @char_compare=@char_compare+@char_split+@sign
	end

	--returns.
	return left(@char_compare,len(@char_compare)-1)
END
GO

-- f_char_split_to_table
CREATE FUNCTION dbo.f_char_split_to_table
(@char_List varchar(8000),@sign char(1))
RETURNS @t_1 table(id int,value varchar(300))
AS  
BEGIN 
	declare @intPos int,
			@sp_char varchar(300)
	declare @index int

	set @index=1
	
	if len(isnull(@char_List,''))=0
		return
	else if len(isnull(@sign,''))=0
		insert into @t_1 select @index,@char_List
	else
	begin	
		set @intpos=charindex(@sign,@char_List)
		while (@intpos>=0)
		begin
			if @intPos>0 
				set @sp_char=left(@char_List,@intPos-1)
			else
				set @sp_char=@char_List
				
			insert into @t_1 select @index,@sp_char
				
			set @char_List=right(@char_List,len(@char_List)-@intPos)
			if @intPos>0
				set @intPos=charindex(@sign,@char_List)
			else
				set @intPos=-1
			
			set @index=@index+1
		end		
	end
	
return
END
GO

-- f_date_compare
CREATE FUNCTION dbo.f_date_compare(
	@source_date1 datetime,
	@source_date2 datetime,
	@target_date1 datetime,
	@target_date2 datetime,
	@datepart varchar(10))
RETURNS int  
BEGIN 
	declare @compare1 int,
		@compare2 int,
		@compare3 int,
		@compare4 int
	declare @return int
	
	set @return=0
	
	if(@source_date1 is null or @source_date2 is null or @target_date1 is null or @target_date2 is null)
		goto quit
	
	if(@datepart='year')
	begin
		set @compare1=datediff(year,@source_date1,@target_date1)
		set @compare2=datediff(year,@source_date2,@target_date2)
		set @compare3=datediff(year,@source_date1,@target_date2)
		set @compare4=datediff(year,@source_date2,@target_date1)
	
		if(@compare1<0 and @compare2<=0 and @compare3>0)
		begin
			set @return=1
			goto quit
		end
		else if(@compare1>=0 and @compare2<=0)
		begin
			set @return=2
			goto quit
		end
		else if(@compare1>=0 and @compare2>0 and @compare4<0)
		begin
			set @return=3
			goto quit
		end	
		else if(@compare1<0 and @compare2>0)
		begin
			set @return=4
			goto quit
		end		
	end
	else if(@datepart='month')
	begin
		set @compare1=datediff(month,@source_date1,@target_date1)
		set @compare2=datediff(month,@source_date2,@target_date2)
		set @compare3=datediff(month,@source_date1,@target_date2)
		set @compare4=datediff(month,@source_date2,@target_date1)
	
		if(@compare1<0 and @compare2<=0 and @compare3>0)
		begin
			set @return=1
			goto quit
		end
		else if(@compare1>=0 and @compare2<=0)
		begin
			set @return=2
			goto quit
		end
		else if(@compare1>=0 and @compare2>0 and @compare4<0)
		begin
			set @return=3
			goto quit
		end	
		else if(@compare1<0 and @compare2>0)
		begin
			set @return=4
			goto quit
		end		
	end
	else if(@datepart='day')
	begin
		set @compare1=datediff(day,@source_date1,@target_date1)
		set @compare2=datediff(day,@source_date2,@target_date2)
		set @compare3=datediff(day,@source_date1,@target_date2)
		set @compare4=datediff(day,@source_date2,@target_date1)
	
		if(@compare1<0 and @compare2<=0 and @compare3>0)
		begin
			set @return=1
			goto quit
		end
		else if(@compare1>=0 and @compare2<=0)
		begin
			set @return=2
			goto quit
		end
		else if(@compare1>=0 and @compare2>0 and @compare4<0)
		begin
			set @return=3
			goto quit
		end	
		else if(@compare1<0 and @compare2>0)
		begin
			set @return=4
			goto quit
		end		
	end	
	else if(@datepart='hour')
	begin
		set @compare1=datediff(hour,@source_date1,@target_date1)
		set @compare2=datediff(hour,@source_date2,@target_date2)
		set @compare3=datediff(hour,@source_date1,@target_date2)
		set @compare4=datediff(hour,@source_date2,@target_date1)
	
		if(@compare1<0 and @compare2<=0 and @compare3>0)
		begin
			set @return=1
			goto quit
		end
		else if(@compare1>=0 and @compare2<=0)
		begin
			set @return=2
			goto quit
		end
		else if(@compare1>=0 and @compare2>0 and @compare4<0)
		begin
			set @return=3
			goto quit
		end	
		else if(@compare1<0 and @compare2>0)
		begin
			set @return=4
			goto quit
		end		
	end
	else if(@datepart='minute')
	begin
		set @compare1=datediff(minute,@source_date1,@target_date1)
		set @compare2=datediff(minute,@source_date2,@target_date2)
		set @compare3=datediff(minute,@source_date1,@target_date2)
		set @compare4=datediff(minute,@source_date2,@target_date1)
	
		if(@compare1<0 and @compare2<=0 and @compare3>0)
		begin
			set @return=1
			goto quit
		end
		else if(@compare1>=0 and @compare2<=0)
		begin
			set @return=2
			goto quit
		end
		else if(@compare1>=0 and @compare2>0 and @compare4<0)
		begin
			set @return=3
			goto quit
		end	
		else if(@compare1<0 and @compare2>0)
		begin
			set @return=4
			goto quit
		end		
	end
	else if(@datepart='second')
	begin
		set @compare1=datediff(second,@source_date1,@target_date1)
		set @compare2=datediff(second,@source_date2,@target_date2)
		set @compare3=datediff(second,@source_date1,@target_date2)
		set @compare4=datediff(second,@source_date2,@target_date1)
	
		if(@compare1<0 and @compare2<=0 and @compare3>0)
		begin
			set @return=1
			goto quit
		end
		else if(@compare1>=0 and @compare2<=0)
		begin
			set @return=2
			goto quit
		end
		else if(@compare1>=0 and @compare2>0 and @compare4<0)
		begin
			set @return=3
			goto quit
		end	
		else if(@compare1<0 and @compare2>0)
		begin
			set @return=4
			goto quit
		end		
	end
	else if(@datepart='millisecond')
	begin
		set @compare1=datediff(millisecond,@source_date1,@target_date1)
		set @compare2=datediff(millisecond,@source_date2,@target_date2)
		set @compare3=datediff(millisecond,@source_date1,@target_date2)
		set @compare4=datediff(millisecond,@source_date2,@target_date1)
	
		if(@compare1<0 and @compare2<=0 and @compare3>0)
		begin
			set @return=1
			goto quit
		end
		else if(@compare1>=0 and @compare2<=0)
		begin
			set @return=2
			goto quit
		end
		else if(@compare1>=0 and @compare2>0 and @compare4<0)
		begin
			set @return=3
			goto quit
		end	
		else if(@compare1<0 and @compare2>0)
		begin
			set @return=4
			goto quit
		end		
	end	
	
quit:
return @return
END
GO

-- f_date_split
CREATE FUNCTION dbo.f_date_split (@date DATETIME)  
RETURNS nvarchar(50) AS  
BEGIN 
	DECLARE @datepart nvarchar(50)
	DECLARE @today DATETIME
	SELECT @today = TODAY FROM V_TODAY
	IF DATEDIFF(day, @date, @today) = 0
		SELECT @datepart = '1.今天'
	ELSE IF DATEDIFF(day, @date, @today) = 1
		SELECT @datepart = '2.昨天'
	ELSE IF YEAR(@date)=YEAR(@today) AND MONTH(@date)=MONTH(@today)
		SELECT @datepart = '3.本月'
	ELSE IF YEAR(@date) =YEAR(@today) AND MONTH(@date)=MONTH(@today)-1
		SELECT @datepart = '4.上月'
	ELSE
		SELECT @datepart = '5.更早'
	return @datepart
END
GO

-- f_get_acc_type_desc
CREATE FUNCTION dbo.f_get_acc_type_desc (@acc_type_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @acc_type_desc nvarchar(50)
	
	if (@acc_type_id = '0')
		set @acc_type_desc = '现金帐户'
	else
		set @acc_type_desc = '银行帐户'

return @acc_type_desc
END
GO

-- f_get_account_name
CREATE FUNCTION dbo.f_get_account_name(@acc_no varchar(4))
RETURNS nvarchar(50) AS  
BEGIN 
	declare @acc_name nvarchar(50)
	SELECT TOP 1 @acc_name = ACC_NAME FROM COP_ACCOUNT_INFO WHERE ACC_NO = @acc_no
return @acc_name
END
GO

-- f_get_approve_state_desc
CREATE FUNCTION dbo.f_get_approve_state_desc (@state_id char(1))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @state_desc nvarchar(50)
	
	select @state_id = isnull(@state_id,'')
	if (@state_id = 'Y')
		set @state_desc = '同意'
	else if(@state_id = 'N')
		set @state_desc = '不同意'
	else if(@state_id = 'U')
		set @state_desc = '解批'
	else if(@state_id = 'R')
		set @state_desc = '被否'
	else
		set @state_desc = '待批'
return @state_desc

END
GO

-- f_get_basic_curr
CREATE FUNCTION dbo.f_get_basic_curr()
RETURNS nvarchar(50) AS  
BEGIN 
	declare @basic_curr nvarchar(50)
	SELECT TOP 1 @basic_curr = CURR_IDX FROM SYSDK WHERE IS_BASIC_CURR = 1
return @basic_curr
END
GO

-- f_get_basic_curr_value
CREATE FUNCTION dbo.f_get_basic_curr_value(@curr_idx char(3), @rate numeric(18,5), @base int,@value numeric(18,5), @scale int)
RETURNS numeric(18,4) AS  
BEGIN 
	--先取本位币
	declare @basic_curr nvarchar(50),
		@return_value float
	select @basic_curr = dbo.f_get_basic_curr()
	if (@curr_idx != @basic_curr)
	begin
		select @return_value = round((@value * @rate / @base), @scale)
	end
	else
	begin
		select @return_value = @value
	end
return @return_value
END
GO

-- f_get_basic_unit_value
CREATE FUNCTION dbo.f_get_basic_unit_value(@pro_no varchar(50), @unit_id varchar(20), @qty numeric(18,5))
RETURNS numeric(18,4) AS  
BEGIN 
	--先取库存单位
	declare @s_unit_id varchar(50),
		@return_value float

	SELECT @s_unit_id=UNIT_ID FROM INVMA WHERE PRO_NO=@pro_no

	if (@unit_id != @s_unit_id)
	begin
		SELECT @return_value = round((CASE WHEN @UNIT_ID=CON_UNIT_ID1 THEN @QTY/CON_RATE1
		WHEN @UNIT_ID=CON_UNIT_ID2 THEN @QTY/CON_RATE2
		WHEN @UNIT_ID=CON_UNIT_ID3 THEN @QTY/CON_RATE3
		else  @QTY END),4)
		FROM INVMA WHERE PRO_NO=@PRO_NO
	end
	else
	begin
		select @return_value = @qty
	end
return isnull(@return_value,0)
END
GO

-- f_get_car_attri_desc
CREATE FUNCTION dbo.f_get_car_attri_desc (@car_attri_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @car_attri_desc nvarchar(50)
	
	if (@car_attri_id = '1')
		set @car_attri_desc = '出车单'
	else if (@car_attri_id = '2')
		set @car_attri_desc = '维修单'
return @car_attri_desc
END
GO

-- f_get_cess_desc
CREATE FUNCTION dbo.f_get_cess_desc (@cess_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @cess_desc nvarchar(50)
	SELECT @cess_desc=REV_DESC FROM SYSDM WHERE (REV_IDX =@cess_id)
return @cess_desc
END
GO

-- f_get_char_byindex
CREATE  FUNCTION dbo.f_get_char_byindex(@char_List varchar(8000),@sign char(1),@index int)
RETURNS varchar(1000) AS  
BEGIN 
	declare @char_now varchar(8000),
		@char_split varchar(8000),
		@intpos int,
		@this_index int
	declare @return_char varchar(1000)

	set @char_now=@char_List
	set @this_index=1

	set @intpos=charindex(@sign,@char_now)
	while (@intpos>=0)
	begin
		if @intPos>0 
			set @char_split=left(@char_now,@intPos-1)
		else
			set @char_split=@char_now
			
		if @this_index=@index
		begin
			set @return_char=@char_split
			goto this
		end
		else
		begin
			set @this_index=@this_index+1
		end
		set @char_now=right(@char_now,len(@char_now)-@intPos)
		if @intPos>0
			set @intPos=charindex(@sign,@char_now)
		else
			set @intPos=-1
	end

this:
	--returns.
	return @return_char
END
GO

-- f_get_color_desc
CREATE FUNCTION dbo.f_get_color_desc (@color_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @color_desc nvarchar(50)
	SELECT @color_desc=COLOR_NAME_CN FROM INVSF WHERE (COLOR_ID =@color_id)
return @color_desc
END
GO

-- f_get_currency_desc
CREATE FUNCTION dbo.f_get_currency_desc (@curr_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @curr_desc nvarchar(50)
	SELECT @curr_desc=CURR_DESC FROM SYSDK WHERE (CURR_IDX =@curr_id)
return @curr_desc
END
GO

-- f_get_currency_rate
CREATE FUNCTION dbo.f_get_currency_rate (@currency_id varchar(8))  
RETURNS numeric(18,5) AS  
BEGIN 
declare @rate numeric(18,5)
SELECT @rate=RATE FROM SYSDK where CURR_IDX=@currency_id
return @rate
END
GO

-- f_get_customer_desc
CREATE FUNCTION dbo.f_get_customer_desc (@cust_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @cust_desc nvarchar(50)
	SELECT @cust_desc=CUST_NAME FROM COPMD WHERE (CUST_ID =@cust_id)
return @cust_desc
END
GO

-- f_get_dept_desc
CREATE FUNCTION dbo.f_get_dept_desc (@dept_id varchar(6))  
RETURNS nvarchar(300) AS  
BEGIN 
declare @dpet_desc nvarchar(100)
SELECT @dpet_desc=DEPT_NAME FROM DEPT WHERE (DEPT_ID =@dept_id)
return @dpet_desc
END
GO

-- f_get_effect_store_desc
CREATE FUNCTION dbo.f_get_effect_store_desc (@effect_store_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @effect_store_desc nvarchar(50)
	
	if (@effect_store_id = 'I')
		set @effect_store_desc = '加'
	else if (@effect_store_id = 'O')
		set @effect_store_desc = '减'
	
return @effect_store_desc
END
GO

-- f_get_emp_name_by_id
CREATE FUNCTION dbo.f_get_emp_name_by_id (@emp_id varchar(50))  
RETURNS nvarchar(50) AS  
BEGIN 
    DECLARE @emp_name varchar(50)
    SELECT @emp_name=emp_name FROM sysdN WHERE emp_id=@emp_id
    RETURN @emp_name
END
GO

-- f_get_empname_by_userid
CREATE FUNCTION dbo.f_get_empname_by_userid (@user_id varchar(50))  
RETURNS nvarchar(50) AS  
BEGIN 
    DECLARE @emp_name varchar(50)
    SELECT @emp_name=emp_name FROM sysdl,sysdn WHERE sysdl.emp_id=sysdn.emp_id AND sysdl.user_id=@user_id
    RETURN @emp_name
END
GO

-- f_get_field_colid
CREATE FUNCTION dbo.f_get_field_colid (@t_id varchar(100),@f_id varchar(100))  
RETURNS int AS  
BEGIN 
declare @colid int
SELECT @colid=b.colid
FROM sysobjects a INNER JOIN
      syscolumns b ON a.id = b.id
WHERE (a.name = @t_id) AND (b.name = @f_id)
return @colid
END
GO

-- f_get_field_desc
CREATE FUNCTION dbo.f_get_field_desc (@t_id varchar(100),@f_id varchar(100))  
RETURNS varchar(300) AS  
BEGIN 
declare @f_desc varchar(300)
SELECT @f_desc= f_desc FROM fields WHERE (t_id = @t_id) AND (f_id = @f_id)
return @f_desc
END
GO

-- f_get_field_type
CREATE FUNCTION dbo.f_get_field_type (@t_id varchar(100),@f_id varchar(100))  
RETURNS varchar(300) AS  
BEGIN 
declare @f_field_type varchar(300)
SELECT @f_field_type=a.name+';'+c.name+';'+CONVERT(varchar(20),a.length) FROM  syscolumns a INNER JOIN sysobjects b ON a.id = b.id INNER JOIN  systypes c ON a.xusertype = c.xusertype WHERE
(b.name =@t_id) and a.name=@f_id
return @f_field_type
END
GO

-- f_get_fields
CREATE FUNCTION dbo.f_get_fields 
	(
		@tableName varchar(100)
	)
RETURNS  @table_variable TABLE (column_desc nvarchar(100),column_info nvarchar(4000)) 
AS
	BEGIN
		declare @fk_f_id_1 varchar(100)
		declare @fk_f_id_2 varchar(100)
		--获取副表
		SELECT @fk_f_id_1=FK_T_ID_1, @fk_f_id_2=FK_T_ID_2
		FROM TABLES	WHERE (T_ID = @tableName)
		
		--插入主表字段
		INSERT INTO @table_variable (column_desc,column_info)
		SELECT b.F_DESC AS column_desc, dbo.f_get_field_type(b.T_ID, b.F_ID) 
		+ ';' + ISNULL(b.CONVERT_FUNCTION, '') + ';' + ISNULL(b.DATASOURCE_SQL, '') 
		+ ';' + b.T_ID AS column_info
		FROM sysobjects c INNER JOIN
		syscolumns a ON c.id = a.id INNER JOIN
		FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
		WHERE (b.IS_QUERY = 1) AND (b.T_ID = @tableName)
		ORDER BY a.colid

	--插入副表一
		INSERT INTO @table_variable (column_desc,column_info)
		SELECT b.F_DESC AS column_desc, dbo.f_get_field_type(b.T_ID, b.F_ID) 
		+ ';' + ISNULL(b.CONVERT_FUNCTION, '') + ';' + ISNULL(b.DATASOURCE_SQL, '') 
		+ ';' + b.T_ID AS column_info
		FROM sysobjects c INNER JOIN
		syscolumns a ON c.id = a.id INNER JOIN
		FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
		WHERE (b.IS_QUERY = 1) AND (b.T_ID = @fk_f_id_1)
		ORDER BY a.colid
	--插入副表二
		INSERT INTO @table_variable (column_desc,column_info)
		SELECT b.F_DESC AS column_desc, dbo.f_get_field_type(b.T_ID, b.F_ID) 
		+ ';' + ISNULL(b.CONVERT_FUNCTION, '') + ';' + ISNULL(b.DATASOURCE_SQL, '') 
		+ ';' + b.T_ID AS column_info
		FROM sysobjects c INNER JOIN
		syscolumns a ON c.id = a.id INNER JOIN
		FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
		WHERE (b.IS_QUERY = 1) AND (b.T_ID = @fk_f_id_2)
		ORDER BY a.colid

	RETURN
	END
GO

-- f_get_fields_desc
CREATE FUNCTION dbo.f_get_fields_desc (@t_id varchar(100),@f_ids varchar(500))  
RETURNS @retTable TABLE (F_DESC varchar(300))  AS  
BEGIN 
    declare @a varchar(3000)
    select @a = 'select f_desc into #temp from fields where F_id in ('+@f_ids+') and T_ID='+@t_id+'select * from #temp'
    RETURN
END
GO

-- f_get_fields_info
CREATE FUNCTION dbo.f_get_fields_info
(
	@tableName varchar(100),--主表名
	@table_type varchar(10),--输出主表字段信息或副表字信息
	@pkeys varchar(300)--主键信息 
)
RETURNS  @table_variable TABLE (column_desc nvarchar(100),column_info nvarchar(4000)) 
AS
	BEGIN
		declare @fk_f_id_1 varchar(100),
			@fk_f_id_2 varchar(100)

		--获取副表名---------------------------------------------------------------------------------------------------------------------------------------------------
		SELECT @fk_f_id_1=FK_T_ID_1, @fk_f_id_2=FK_T_ID_2
		FROM TABLES	WHERE (T_ID = @tableName)

		--获取主表字段信息----------------------------------------------------------------------------------------------------------------------------------------------
		if (@table_type = 'primary')
		begin
			--插入主表字段---------------------------------------------------------------------------------------------------------------------------------------------------
			INSERT INTO @table_variable (column_desc,column_info)
			SELECT b.F_DESC AS column_desc, (b.T_ID + '^'+b.F_ID) as column_info
			FROM sysobjects c INNER JOIN
			syscolumns a ON c.id = a.id INNER JOIN
			FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
			WHERE  (ISNULL(b.IS_VIRTUAL , 0) = 0 AND b.IS_VISIBLE=1 AND b.T_ID = @tableName)
			ORDER BY a.colid

			INSERT INTO @table_variable (column_desc,column_info)
			SELECT b.F_DESC AS column_desc, (b.T_ID + '^'+b.F_ID) as column_info
			FROM FIELDS b
			WHERE b.IS_VIRTUAL=1 AND b.IS_VISIBLE = 1 AND b.T_ID = @tableName
			ORDER BY b.F_ID
		end
		else if (@table_type = 'second')
		begin
			--插入副表一---------------------------------------------------------------------------------------------------------------------------------------------------
			INSERT INTO @table_variable (column_desc,column_info)
			SELECT  b.F_DESC AS column_desc, (b.T_ID + '^'+b.F_ID) as column_info
			FROM sysobjects c INNER JOIN
			syscolumns a ON c.id = a.id INNER JOIN
			FIELDS b ON (a.name = b.F_ID) AND (c.name = b.T_ID)
			WHERE (ISNULL(b.IS_VIRTUAL , 0) = 0 AND b.IS_VISIBLE=1 AND b.T_ID = @fk_f_id_1) -- (b.IS_QUERY = 1) AND 
			and B.F_ID not in(select value from dbo.f_char_split_to_table(@pkeys, ';'))
			ORDER BY a.colid

			INSERT INTO @table_variable (column_desc,column_info)
			SELECT  b.F_DESC AS column_desc, (b.T_ID + '^'+b.F_ID) as column_info
			FROM FIELDS b 
			WHERE (b.IS_VIRTUAL = 1) AND b.IS_VISIBLE=1 AND (b.T_ID = @fk_f_id_1) -- (b.IS_QUERY = 1) AND
			and B.F_ID not in(select value from dbo.f_char_split_to_table(@pkeys, ';'))
			ORDER BY b.F_ID

			--插入副表二---------------------------------------------------------------------------------------------------------------------------------------------------
			INSERT INTO @table_variable (column_desc,column_info)
			SELECT b.F_DESC AS column_desc,  (b.T_ID + '^'+b.F_ID) as column_info
			FROM sysobjects c INNER JOIN
			syscolumns a ON c.id = a.id INNER JOIN
			FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
			WHERE (b.IS_QUERY = 1) AND (b.T_ID = @fk_f_id_2)
			and B.F_ID not in(select value from dbo.f_char_split_to_table(@pkeys,';'))
			ORDER BY a.colid
		end
		else
		begin
			--插入主表字段---------------------------------------------------------------------------------------------------------------------------------------------------
			INSERT INTO @table_variable (column_desc,column_info)
			SELECT '[主表]' + b.F_DESC AS column_desc, (b.T_ID + '^'+b.F_ID) as column_info
			FROM sysobjects c INNER JOIN
			syscolumns a ON c.id = a.id INNER JOIN
			FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
			WHERE ISNULL(b.IS_VIRTUAL , 0) = 0 AND (b.T_ID = @tableName) AND (b.IS_VISIBLE=1) AND (b.IS_QUERY = 1)
			ORDER BY a.colid

			INSERT INTO @table_variable (column_desc,column_info)
			SELECT '[主表]' + b.F_DESC AS column_desc, '^' + b.VIRTUAL_EXP as column_info
			FROM FIELDS b 
			WHERE (b.IS_VIRTUAL = 1) AND (b.T_ID = @tableName) AND (b.IS_VISIBLE=1) AND (b.IS_QUERY = 1) 
			ORDER BY b.F_ID


			--插入副表一---------------------------------------------------------------------------------------------------------------------------------------------------
			INSERT INTO @table_variable (column_desc,column_info)
			SELECT '[副表]' +  b.F_DESC AS column_desc, (b.T_ID + '^'+b.F_ID) as column_info
			FROM sysobjects c INNER JOIN
			syscolumns a ON c.id = a.id INNER JOIN
			FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
			WHERE (b.T_ID = @fk_f_id_1) AND (b.IS_VISIBLE=1) AND (ISNULL(B.IS_VIRTUAL,0)=0) AND (b.IS_QUERY = 1)
			and B.F_ID not in(select value from dbo.f_char_split_to_table(@pkeys, ';'))
			ORDER BY a.colid

			INSERT INTO @table_variable (column_desc,column_info)
			SELECT '[副表]' +  b.F_DESC AS column_desc, '^' + b.VIRTUAL_EXP as column_info
			FROM FIELDS b 
			WHERE (b.T_ID = @fk_f_id_1) AND (b.IS_VISIBLE=1) AND (B.IS_VIRTUAL=1) AND (b.IS_QUERY = 1)
			and B.F_ID not in(select value from dbo.f_char_split_to_table(@pkeys, ';'))
			ORDER BY b.F_ID

			--插入副表二---------------------------------------------------------------------------------------------------------------------------------------------------
			INSERT INTO @table_variable (column_desc,column_info)
			SELECT '[副表]' + b.F_DESC AS column_desc,  (b.T_ID + '^'+b.F_ID) as column_info
			FROM sysobjects c INNER JOIN
			syscolumns a ON c.id = a.id INNER JOIN
			FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
			WHERE (b.T_ID = @fk_f_id_2) AND (b.IS_VISIBLE=1) AND (b.IS_QUERY = 1) 
			and B.F_ID not in(select value from dbo.f_char_split_to_table(@pkeys,';'))
			ORDER BY a.colid
		end

	RETURN
	END
GO

-- f_get_flow_state
CREATE FUNCTION dbo.f_get_flow_state (@state char(1), @subflow_desc varchar(50))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @return_desc varchar(50)

	if (@state = 'R')
		set @return_desc = '待' + @subflow_desc
	else if (@state = 'Y')
		set @return_desc = '审批通过'
	else if (@state = 'N')
		set @return_desc = '审批未通过'
	
return @return_desc
END
GO

-- f_get_holiday_days
CREATE FUNCTION f_get_holiday_days 
	(@emp_id nchar(10), @year int, @month int)
RETURNS int AS  
BEGIN 
	declare @didate datetime, @indate datetime
	declare @stdate datetime, @endate datetime
	declare @date1 datetime, @date9 datetime
	declare @i int

	select @indate=isnull(IN_DATE, '1900-1-1'), @didate=isnull(DIMISSION_DATE, '2049-12-31') from HR_EMPLOYEE where EMP_ID=@emp_id
	select @date1=dateadd(mm, datediff(mm,0,cast(cast(@year as varchar) + '-' + cast(@month as varchar) + '-01' as datetime)), 0) 
	select @date9=dateadd(ms,-3,DATEADD(mm, DATEDIFF(m,0,@date1)+1, 0))
	select @i=0
	
	if @indate<@date1
		select @indate=@date1
	if @didate>@date9
		select @didate=@date9
	declare cur_tmp cursor for select START_DATE,END_DATE from HR_HOLIDAY where (START_DATE>=@indate and START_DATE<=@didate) or (START_DATE<=@indate and END_DATE>=@indate)
	open cur_tmp
	fetch next from cur_tmp into @stdate, @endate
	while @@FETCH_STATUS=0 begin
		if @stdate < @date1
			select @stdate = @date1
		if @endate > @date9
			select @endate = @date9
		if @indate<=@stdate begin
			if @didate<@endate
				select @i = @i + datediff(day, @stdate,@didate) + 1
			else
				select @i = @i + datediff(day, @stdate,@endate) + 1
		end
		else begin
			if @didate<@endate
				select @i = @i + datediff(day, @indate,@didate) + 1
			else
				select @i = @i + datediff(day, @indate,@endate) + 1
		end
		fetch next from cur_tmp into @stdate, @endate
	end
	close cur_tmp
	deallocate cur_tmp
	return @i
END
GO

-- f_get_horizontal_align_desc
CREATE FUNCTION dbo.f_get_horizontal_align_desc (@align_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @align_desc nvarchar(50)
	
	if (@align_id = 'left')
		set @align_desc = '左对齐'
	else if (@align_id = 'right')
		set @align_desc = '右对齐'
	else if (@align_id = 'justify')
		set @align_desc = '默认对齐'
	else if (@align_id = 'center')
		set @align_desc = '居中对齐'
	
return @align_desc
END
GO

-- f_get_key_value_desc
CREATE FUNCTION dbo.f_get_key_value_desc (@t_id varchar(100),@key_value varchar(500))  
RETURNS nvarchar(300)  AS  
BEGIN 
	declare @f_id nvarchar(100), @f_desc nvarchar(300), @desc nvarchar(300)
	declare cur_tmp cursor for 
		select A.COLUMN_NAME,F.F_DESC
		from INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE A join sysobjects B on B.xtype=N'PK' and object_id(A.CONSTRAINT_NAME)=B.id 
			join FIELDS F on A.COLUMN_NAME=F.F_ID AND F.T_ID=@t_id
		where A.table_name=@t_id 
	open cur_tmp
	fetch next from cur_tmp into @f_id, @f_desc
	while @@FETCH_STATUS = 0 begin
		select @key_value=replace(@key_value, @f_id, @f_desc)
		fetch next from cur_tmp into @f_id, @f_desc
	end
	close cur_tmp
	deallocate cur_tmp
	select @key_value=upper(replace(@key_value, ' AND ', ' 且 '))
    RETURN @key_value
END
GO

-- f_get_main_source_desc
CREATE FUNCTION dbo.f_get_main_source_desc (@main_source_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @main_source_desc nvarchar(50)
	
	if (@main_source_id = '1')
		set @main_source_desc = '采购'
	else if (@main_source_id = '2')
		set @main_source_desc = '自制'
	else if (@main_source_id = '3')
		set @main_source_desc = '外包'
	
return @main_source_desc
END
GO

-- f_get_module_desc
CREATE FUNCTION dbo.f_get_module_desc (@m_idx int)  
RETURNS nvarchar(100) AS  
BEGIN 
declare @module_desc nvarchar(100)
SELECT @module_desc=case when isnull(B.m_desc,'') != '' then 
	 A.M_DESC + '[' + B.m_desc + ']' else A.m_desc end   FROM MODULES a LEFT  JOIN MODULES b on a.m_p_idx = b.m_idx WHERE (a.M_IDX = @m_idx)
return @module_desc
END
GO

-- f_get_module_pid
CREATE FUNCTION dbo.f_get_module_pid (@m_idx int)  
RETURNS int AS  
BEGIN 
declare @pidx int
SELECT @pidx=M_P_IDX FROM MODULES WHERE (M_IDX = @m_idx)
return @pidx
END
GO

-- f_get_module_url
CREATE FUNCTION dbo.f_get_module_url (@m_idx int)  
RETURNS varchar(300) AS  
BEGIN 
declare @m_url varchar(300)
SELECT @m_url=M_URL FROM MODULES WHERE (M_IDX = @m_idx)
return @m_url
END
GO

-- f_get_month_days
CREATE FUNCTION dbo.f_get_month_days (@date datetime)  
RETURNS int AS  
BEGIN 
	return datepart(dd,dateadd(dd,-1,dateadd(mm,1,cast(cast(year(@date) as varchar)+'-'+cast(month(@date) as varchar)+'-01' as datetime))))
END
GO

-- f_get_object_type
CREATE FUNCTION dbo.f_get_object_type 
	(
		@object_name varchar(100)
	)
RETURNS  varchar(20)
AS
	
	BEGIN
	
	declare @object_type varchar(20)
	
	SELECT @object_type=rtrim(xtype) FROM sysobjects WHERE (name =@object_name)
	
	--视图
	if @object_type='P '
	set @object_type='PROCEDURE'
	
	--存储过程
	else if @object_type='V'
	set @object_type='VIEW'
	
	--用户表
	else if @object_type='U'
	set @object_type='TABLE'
	
	--系统表
	else if @object_type='P'
	set @object_type='SYSTEM TABLE'
	
	--用户函数
	else if @object_type='FN'
	set @object_type='FUNCTION'
	
	--主键
	else if @object_type='PK'
	set @object_type='PRIMARY KEY'
	else 
	set @object_type='UNKNOW'
	
	RETURN @object_type
	END
GO

-- f_get_order_attri_desc
CREATE FUNCTION dbo.f_get_order_attri_desc (@order_attri_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @order_attri_desc nvarchar(50)
	
	if (@order_attri_id = '1')
		set @order_attri_desc = '客户订单'
	else if (@order_attri_id = '2')
		set @order_attri_desc = '销货单'
	else if (@order_attri_id = '3')
		set @order_attri_desc = '退货单'
	else if (@order_attri_id = '4')
		set @order_attri_desc = '结帐单'
	else if (@order_attri_id = '5')
		set @order_attri_desc = '收款单'
	else if (@order_attri_id = '6')
		set @order_attri_desc = '出货通知单'
	else if (@order_attri_id = '7')
		set @order_attri_desc = '报价单'
	
return @order_attri_desc
END
GO

-- f_get_order_desc
CREATE FUNCTION dbo.f_get_order_desc (@order_code varchar(6),@order_type varchar(20))  
RETURNS nvarchar(300) AS  
BEGIN 
	declare @order_desc nvarchar(100)
	if (@order_type = 'storehouse')
		SELECT @order_desc=ORDER_NAME FROM INVSC WHERE (ORDER_CODE =@order_code)
	else if (@order_type = 'deptlog')
		select @order_desc=M.ORDER_NAME from (SELECT ORDER_CODE,ORDER_NAME FROM INVSC UNION ALL SELECT ORDER_CODE,ORDER_NAME FROM COPMH)
		as M WHERE (M.ORDER_CODE =@order_code)
	else  if (@order_type = 'business')
		SELECT @order_desc=ORDER_NAME FROM COPMH WHERE (ORDER_CODE =@order_code)
	else  if (@order_type = 'cars')
		SELECT @order_desc=ORDER_NAME FROM CAR_PROPERTY WHERE (ORDER_CODE =@order_code)
	return @order_desc
END
GO

-- f_get_ordertype_bystore
CREATE FUNCTION dbo.f_get_ordertype_bystore (@order_type_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @order_type_desc nvarchar(50)
	
	if (@order_type_id = '1')
		set @order_type_desc = '入库'
	else if (@order_type_id = '2')
		set @order_type_desc = '送货'
	else if (@order_type_id = '3')
		set @order_type_desc = '领用'
	else if (@order_type_id = '4')
		set @order_type_desc = '转拔'
	else if (@order_type_id = '5')
		set @order_type_desc = '调整'
	
return @order_type_desc
END
GO

-- f_get_part_desc
CREATE FUNCTION dbo.f_get_part_desc (@part_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @part_desc nvarchar(50)
	SELECT @part_desc=PART_NAME FROM INVSA WHERE (PART_ID =@part_id)
return @part_desc
END
GO

-- f_get_payment_condi_desc
CREATE FUNCTION dbo.f_get_payment_condi_desc (@payment_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @payment_desc nvarchar(50)
	SELECT @payment_desc=PAYMENT_NAME FROM INVSP WHERE (PAYMENT_ID =@payment_id)
return @payment_desc
END
GO

-- f_get_price_condi_desc
CREATE FUNCTION dbo.f_get_price_condi_desc (@price_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @price_desc nvarchar(50)
	SELECT @price_desc=PRICE_NAME FROM INVSQ WHERE (PRICE_ID =@price_id)
return @price_desc
END
GO

-- f_get_pro_moulds
CREATE FUNCTION dbo.f_get_pro_moulds (@pro_no nchar(30))  
	RETURNS nvarchar(2000) AS  
BEGIN 
	declare @moulds nvarchar(2000), @mou_sort nchar(1), @place2 nvarchar(30), @mou_type nvarchar(30) 
	declare @mou1 nvarchar(1000), @mou2 nvarchar(1000), @mou3 nvarchar(1000), @mou4 nvarchar(1000) , @line_id nvarchar(50) 
	declare @m nvarchar(30)
	declare cursor_count cursor for select rtrim(d.MOULD_ID), m.MOU_SORT,rtrim(m.PLACE2),rtrim(m.LINE_ID),rtrim(m.MOU_TYPE) from MOU_PRO_D d left join MOU_MOULD m on d.MOULD_ID=m.MOULD_ID where d.PRO_NO=@pro_no order by m.MOU_SORT
	open cursor_count
	fetch next from cursor_count into @m,@mou_sort,@place2,@line_id,@mou_type
	select @moulds='', @mou1='吸塑模:', @mou2='上模:', @mou3='刀模:', @mou4=''
	while @@FETCH_STATUS = 0 begin
		if @mou_sort='1'
			select @mou1=@mou1+@m +'在' + @place2 +@mou_type+',' +'机台'+@line_id+';'
		else if @mou_sort='2'
			select @mou2=@mou2+@m+'在' + @place2+@mou_type +',' 
		else if @mou_sort='3'
			select @mou3=@mou3+@m+'在' + @place2 +@mou_type+',' 
		else
			select @mou4=@mou4+@m+'在' + @place2 +@mou_type+',' 
		fetch next from cursor_count into @m, @mou_sort,@place2,@line_id,@mou_type
	end
	select @moulds=@mou4 + @mou1 +'  ' + @mou2 +'  ' + @mou3
	close cursor_count
	deallocate cursor_count
	return substring(@moulds,2,len(@moulds))
END
GO

-- f_get_pro_name
CREATE FUNCTION dbo.f_get_pro_name (@pro_no varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @pro_name nvarchar(50)
	SELECT @pro_name=PRO_NAME FROM INVMA WHERE (PRO_NO =@pro_no)
return @pro_name
END
GO

-- f_get_pro_unit
CREATE FUNCTION dbo.f_get_pro_unit (@pro_no varchar(20))  
RETURNS @t_1 table(unit_id char(4), unit_name varchar(20)) AS  
BEGIN 
	declare @unit_id char(4),
		@con_unit_id1 char(4),
		@con_unit_id2 char(4),
		@con_unit_id3 char(4)
	select @unit_id=unit_id,@con_unit_id1=con_unit_id1,@con_unit_id2=con_unit_id2,@con_unit_id3=con_unit_id3 
	from invma where pro_no=@pro_no
	--基本单位
	insert into @t_1(unit_id, unit_name) select @unit_id, unit_name from invsi where unit_id=@unit_id
	--转换单位1
	if (isnull(@con_unit_id1,'') != '')
		insert into @t_1(unit_id, unit_name) select @con_unit_id1, unit_name from invsi where unit_id=@con_unit_id1
	--转换单位2
	if (isnull(@con_unit_id2,'') != '')
		insert into @t_1(unit_id, unit_name) select @con_unit_id2, unit_name from invsi where unit_id=@con_unit_id2
	--转换单位3
	if (isnull(@con_unit_id3,'') != '')
		insert into @t_1(unit_id, unit_name) select @con_unit_id3, unit_name from invsi where unit_id=@con_unit_id3	
return
END
GO

-- f_get_pro_units
CREATE  FUNCTION dbo.f_get_pro_units (@pro_no nchar(30))
RETURNS @T_RETURN table(UNIT_ID nchar(10))
AS  
BEGIN 
	insert into  @T_RETURN 
		select UNIT_ID from PRODUCT where PRO_NO=@pro_no
		union select UNIT_ID_1 from PRODUCT where PRO_NO=@pro_no
		union select UNIT_ID_2 from PRODUCT where PRO_NO=@pro_no
		union select UNIT_ID_3 from PRODUCT where PRO_NO=@pro_no
		union select UNIT_ID_4 from PRODUCT where PRO_NO=@pro_no
	return
END
GO

-- f_get_produce_under
CREATE FUNCTION dbo.f_get_produce_under (@root_type nchar(10), @root_no nchar(20))
RETURNS @T_RETURN table(STEP int, PRODUCE_TYPE nchar(10), PRODUCE_NO nchar(20))
AS  
BEGIN 
	declare @i int
	select @i = 0
	insert into  @T_RETURN select @i, PRODUCE_TYPE, PRODUCE_NO from MOC_PRODUCE_M where PARENT_TYPE=@root_type and PARENT_NO=@root_no
	while  @@ROWCOUNT > 0 begin	
		select @i = @i + 1
		insert into @T_RETURN select @i, PRODUCE_TYPE, PRODUCE_NO from MOC_PRODUCE_M 
			where exists(select * from @T_RETURN where STEP=@i-1 and PRODUCE_TYPE=MOC_PRODUCE_M.PARENT_TYPE and PRODUCE_NO=MOC_PRODUCE_M.PARENT_NO)
	end
	return
END
GO

-- f_get_product_attri_desc
CREATE FUNCTION dbo.f_get_product_attri_desc (@product_attri_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @product_attri_desc nvarchar(50)
	
	if (@product_attri_id = '1')
		set @product_attri_desc = '成品'
	else if (@product_attri_id = '2')
		set @product_attri_desc = '半成品'
	else if (@product_attri_id = '3')
		set @product_attri_desc = '材料'
	else if (@product_attri_id = '4')
		set @product_attri_desc = '辅料'
	
return @product_attri_desc
END
GO

-- f_get_rest_days
CREATE FUNCTION f_get_rest_days 
	(@emp_id nchar(10), @year int, @month int)
RETURNS int AS  
BEGIN 
	declare @indate datetime, @didate datetime
	declare @date1 datetime, @date9 datetime
	declare @i int, @w int
	select @indate=isnull(IN_DATE, '1900-1-1'), @didate=isnull(DIMISSION_DATE, '2049-12-31') from HR_EMPLOYEE where EMP_ID=@emp_id
	select @date1=dateadd(mm, datediff(mm,0,cast(cast(@year as varchar) + '-' + cast(@month as varchar) + '-01' as datetime)), 0) 
	select @date9=dateadd(ms,-3,DATEADD(mm, DATEDIFF(m,0,@date1)+1, 0))
	select @i=0
	
	if @indate>@date1
		select @date1=@indate
	if @didate<@date9
		select @date9=@didate
	while(@date9>=@date1) begin
		select @w=datepart(dw,@date1)-1
		if(@w=6 or @w=0) begin
			if(not exists(select * from HR_HOLIDAY where @date1 between START_DATE and END_DATE) and ((@w=6 and exists(select * from HR_SETUP where SAT_REST_DAY=1)) or  (@w=0 and exists(select * from HR_SETUP where SUN_REST_DAY=1))))
				select @i=@i+1
		end
		select @date1=dateadd(dd,1,@date1)
	end
	return @i
END
GO

-- f_get_root_m_idx
CREATE FUNCTION dbo.f_get_root_m_idx(@m_idx int)
RETURNS int AS  
BEGIN 
	declare @root_idx int
	select @root_idx=ISNULL(m_p_idx,-1) from MODULES where m_idx=@m_idx
	if @root_idx = -1 or @root_idx = 0  or @root_idx = @m_idx
		select @root_idx = @m_idx
	else
		select @root_idx = dbo.f_get_root_m_idx(@root_idx)
	return @root_idx
END
GO

-- f_get_root_produce
CREATE FUNCTION dbo.f_get_root_produce(@produce_type nchar(10), @produce_no nchar(20))
RETURNS @T_RETURN table(PRODUCE_TYPE nchar(10), PRODUCE_NO nchar(20)) AS  
BEGIN 
	declare @root_type nchar(10), @root_no nchar(20),@tmp_type nchar(10), @tmp_no nchar(20)
	select @root_type=@produce_type, @root_no=@produce_no
	while 1=1 begin
		select @tmp_type=PARENT_TYPE, @tmp_no=PARENT_NO from MOC_PRODUCE_M where PRODUCE_TYPE=@root_type and PRODUCE_NO=@root_no
		if isnull(@tmp_no,'')=''
			break;
		else
			select @root_type=@tmp_type, @root_no=@tmp_no
	end
	insert into @T_RETURN select @root_type, @root_no
	return
END
GO

-- f_get_same_fieldlist
CREATE FUNCTION dbo.f_get_same_fieldlist (@table1 nvarchar(50), @table2 nvarchar(50))  
RETURNS nvarchar(4000) AS  
BEGIN 
	declare @return_str nvarchar(4000), @field_str nvarchar(50)
	declare cur_temp cursor  for
		select a.name from (select name from syscolumns where id = object_id(@table1) and iscomputed=0) a, (select name from syscolumns where id = object_id(@table2) and iscomputed=0) b where a.name=b.name
	open cur_temp
	select @return_str = ''
	fetch next from cur_temp into @field_str
	while @@FETCH_STATUS = 0 begin
		select @return_str = @return_str + @field_str + ','
		fetch next from cur_temp into @field_str
	end
	close cur_temp
	deallocate cur_temp
	if len(@return_str) > 0
		select @return_str = left(@return_str,len(@return_str)-1)
	return @return_str
END
GO

-- f_get_sirs
CREATE FUNCTION dbo.f_get_sirs (@USER_ID varchar(6))
RETURNS @T_RETURN table(user_id varchar(6))
AS  
BEGIN 
	insert into  @T_RETURN select SIR_ID from SYSDL where USER_ID=@USER_ID
	while  @@ROWCOUNT > 0
	begin	
		insert into @T_RETURN select SIR_ID from SYSDL where USER_ID  in (select USER_ID from @T_RETURN)  and SIR_ID not in (select USER_ID from @T_RETURN)
	end
	
return
END
GO

-- f_get_store_desc
CREATE FUNCTION dbo.f_get_store_desc (@store_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @store_desc nvarchar(50)
	SELECT @store_desc=STORE_NAME FROM INVSB WHERE (STORE_ID =@store_id)
return @store_desc
END
GO

-- f_get_stuff_desc
CREATE FUNCTION dbo.f_get_stuff_desc (@stuff_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @stuff_desc nvarchar(50)
	SELECT @stuff_desc=STUFF_NAME FROM INVSK WHERE (STUFF_ID =@stuff_id)
return @stuff_desc
END
GO

-- f_get_subflow_desc
CREATE FUNCTION dbo.f_get_subflow_desc (@wf_id int,@subflow_no char(3))  
RETURNS varchar(300) AS  
BEGIN 
declare @subflow_desc varchar(300),@wf_m_idx int
SELECT @wf_m_idx=wf_m_idx FROM wf_monitor WHERE wf_id=@wf_id
SELECT @subflow_desc = m.flow_name+'-->'+d.subflow_desc FROM wfform m,wfform_flow d WHERE m.wf_m_idx=d.wf_m_idx AND m.wf_m_idx = @wf_m_idx AND d.sort_no=@subflow_no
return @subflow_desc
END
GO

-- f_get_table_desc
CREATE FUNCTION dbo.f_get_table_desc (@t_id varchar(100))  
RETURNS nvarchar(300) AS  
BEGIN 
declare @t_desc nvarchar(300)
SELECT @t_desc=T_DESC FROM TABLES WHERE (T_ID = @t_id)
return @t_desc
END
GO

-- f_get_table_kind_desc
CREATE FUNCTION dbo.f_get_table_kind_desc (@table_kind_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @table_kind_desc nvarchar(50)
	
	if (@table_kind_id = 'P')
		set @table_kind_desc = '主表'
	else if (@table_kind_id = 'S')
		set @table_kind_desc = '副表'
	else if (@table_kind_id = 'O')
		set @table_kind_desc = '其它'
	else
		set @table_kind_desc = '视图'
return @table_kind_desc
END
GO

-- f_get_type_desc
CREATE FUNCTION dbo.f_get_type_desc (@type_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @type_desc nvarchar(50)
	SELECT @type_desc=TYPE_NAME FROM INVSJ WHERE (TYPE_ID =@type_id)
return @type_desc
END
GO

-- f_get_under_depts
CREATE FUNCTION dbo.f_get_under_depts (@DEPT_ID varchar(6))
RETURNS @T_RETURN table(DEPT_ID varchar(6))
AS  
BEGIN 
	insert into  @T_RETURN select DEPT_ID from DEPT where DEPT_ID=@DEPT_ID or DEPT_ID_SUPERIOR=@DEPT_ID
	while  @@ROWCOUNT > 0
	begin	
		insert into @T_RETURN select DEPT_ID from DEPT where DEPT_ID_SUPERIOR  in (select DEPT_ID from @T_RETURN)  and DEPT_ID not in (select DEPT_ID from @T_RETURN)
	end
return
END
GO

-- f_get_under_m_idx
CREATE FUNCTION dbo.f_get_under_m_idx (@M_IDX INT)
RETURNS @T_RETURN table(M_IDX INT)
AS  
BEGIN 
	insert into  @T_RETURN select M_IDX from MODULES where M_IDX=@M_IDX or M_P_IDX=@M_IDX
	while  @@ROWCOUNT > 0
	begin	
		insert into @T_RETURN select M_IDX from MODULES where M_P_IDX  in (select M_IDX from @T_RETURN)  and M_IDX not in (select M_IDX from @T_RETURN)
	end
return
END
GO

-- f_get_under_me
CREATE FUNCTION dbo.f_get_under_me (@SIR_ID varchar(10))
RETURNS @T_RETURN table(user_id varchar(10))
AS  
BEGIN 
	insert into  @T_RETURN select USER_ID from SYSDL where SIR_ID=@SIR_ID
	while exists(select  * from SYSDL where SIR_ID in (select USER_ID from @T_RETURN)  and USER_ID not in (select USER_ID from @T_RETURN))
	begin	
		insert into  @T_RETURN select USER_ID from SYSDL where SIR_ID  in (select USER_ID from @T_RETURN)  and USER_ID not in (select USER_ID from @T_RETURN)
	end
	insert into  @T_RETURN values(@SIR_ID)
return
END
GO

-- f_get_underling
CREATE FUNCTION dbo.f_get_underling (@SIR_ID varchar(10))
RETURNS @T_RETURN table(user_id varchar(10))
AS  
BEGIN 
	insert into  @T_RETURN select USER_ID from SYSDL where SIR_ID=@SIR_ID
	while exists(select  * from SYSDL where SIR_ID in (select USER_ID from @T_RETURN)  and USER_ID not in (select USER_ID from @T_RETURN))
	begin	
		insert into  @T_RETURN select USER_ID from SYSDL where SIR_ID  in (select USER_ID from @T_RETURN)  and USER_ID not in (select USER_ID from @T_RETURN)
	end
	
return
END
GO

-- f_get_unit_desc
CREATE FUNCTION dbo.f_get_unit_desc (@unit_id varchar(6))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @unit_desc nvarchar(50)
	SELECT @unit_desc=UNIT_NAME FROM INVSI WHERE (UNIT_ID =@unit_id)
return @unit_desc
END
GO

-- f_get_unit_type_desc
CREATE FUNCTION dbo.f_get_unit_type_desc (@unit_type_id varchar(20))  
RETURNS nvarchar(50) AS  
BEGIN 
	declare @order_type_desc nvarchar(50)
	
	if (@unit_type_id = '1')
		set @order_type_desc = '数量'
	else if (@unit_type_id = '2')
		set @order_type_desc = '重量'
	else if (@unit_type_id = '3')
		set @order_type_desc = '长度'
	else if (@unit_type_id = '4')
		set @order_type_desc = '面积'
	else if (@unit_type_id = '5')
		set @order_type_desc = '体积'
	
return @order_type_desc
END
GO

-- f_get_user_active_desc
CREATE FUNCTION dbo.f_get_user_active_desc (@ACTIVE_TAG bit)  
RETURNS nvarchar(10) AS  
BEGIN 
declare @active_desc nvarchar(10)
if @ACTIVE_TAG=1
set @active_desc='正常'
else
set @active_desc='暂停'
return @active_desc
END
GO

-- f_get_user_approve
CREATE FUNCTION dbo.f_get_user_approve (@USER_ID nchar(10))
RETURNS @T_RETURN table(M_IDX int)
AS  
BEGIN 
	insert into  @T_RETURN select M_IDX from SYSDH where G_IDX in (select G_IDX FROM SYSDG_USER WHERE USER_ID = @USER_ID) and M_IDX not in(select M_IDX from SYSDD where USER_ID=@USER_ID)  and APPROVE_TAG=1
	insert into  @T_RETURN select M_IDX from SYSDD where USER_ID=@USER_ID and APPROVE_TAG=1
return
END
GO

-- f_get_user_fields_info
CREATE FUNCTION dbo.f_get_user_fields_info
(
	@tableName varchar(100),--主表名
	@userId varchar(50),--用户号
	@table_type varchar(10),--输出主表字段信息或副表字信息
	@pkeys varchar(300)--主键信息 
)
RETURNS  @table_variable TABLE (column_desc nvarchar(100),column_info nvarchar(4000)) 
AS
	BEGIN
		declare @fk_f_id_1 varchar(100),
			@fk_f_id_2 varchar(100)

		--获取副表名---------------------------------------------------------------------------------------------------------------------------------------------------
		SELECT @fk_f_id_1=FK_T_ID_1, @fk_f_id_2=FK_T_ID_2
		FROM TABLES	WHERE (T_ID = @tableName)
		--插入主表字段---------------------------------------------------------------------------------------------------------------------------------------------------
			INSERT INTO @table_variable (column_desc,column_info)
			SELECT '[主表]' + b.F_DESC AS column_desc, (b.T_ID + '^'+b.F_ID) as column_info
			FROM sysobjects c INNER JOIN
			syscolumns a ON c.id = a.id INNER JOIN
			FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
			WHERE ISNULL(b.IS_VIRTUAL , 0) = 0 AND (b.T_ID = @tableName) AND (b.IS_VISIBLE=1) AND (b.IS_QUERY = 1)
			ORDER BY a.colid

			INSERT INTO @table_variable (column_desc,column_info)
			SELECT '[主表]' + b.F_DESC AS column_desc, '^' + b.VIRTUAL_EXP as column_info
			FROM FIELDS b 
			WHERE (b.IS_VIRTUAL = 1) AND (b.T_ID = @tableName) AND (b.IS_VISIBLE=1) AND (b.IS_QUERY = 1) 
			ORDER BY b.F_ID


			--插入副表一---------------------------------------------------------------------------------------------------------------------------------------------------
			INSERT INTO @table_variable (column_desc,column_info)
			SELECT '[副表]' +  b.F_DESC AS column_desc, (b.T_ID + '^'+b.F_ID) as column_info
			FROM sysobjects c INNER JOIN
			syscolumns a ON c.id = a.id INNER JOIN
			FIELDS b ON a.name = b.F_ID AND c.name = b.T_ID
			WHERE (b.T_ID = @fk_f_id_1) AND (b.IS_VISIBLE=1) AND (ISNULL(B.IS_VIRTUAL,0)=0) AND (b.IS_QUERY = 1)
			and B.F_ID not in(select value from dbo.f_char_split_to_table(@pkeys, ';'))
			ORDER BY a.colid

			INSERT INTO @table_variable (column_desc,column_info)
			SELECT '[副表]' +  b.F_DESC AS column_desc, '^' + b.VIRTUAL_EXP as column_info
			FROM FIELDS b 
			WHERE (b.T_ID = @fk_f_id_1) AND (b.IS_VISIBLE=1) AND (B.IS_VIRTUAL=1) AND (b.IS_QUERY = 1)
			and B.F_ID not in(select value from dbo.f_char_split_to_table(@pkeys, ';'))
			ORDER BY b.F_ID
	RETURN
	END
GO

-- f_get_user_gidx
CREATE FUNCTION dbo.f_get_user_gidx (@USER_ID varchar (6))  
RETURNS int AS  
BEGIN 
declare @gidx int
SELECT @gidx=G_IDX FROM SYSDL WHERE (USER_ID = @USER_ID)
return @gidx
END
GO

-- f_get_user_group_desc
CREATE FUNCTION dbo.f_get_user_group_desc (@G_IDX int)  
RETURNS nvarchar(100) AS  
BEGIN 
declare @group_desc nvarchar(100)
SELECT  @group_desc=G_DESC FROM SYSDG WHERE G_IDX=@G_IDX
return @group_desc
END
GO

-- f_get_user_listdesc
CREATE FUNCTION dbo.f_get_user_listdesc (@user_list varchar(1000), @sign char(1))  
RETURNS nvarchar(1000) AS  
BEGIN 
	declare @user_listdesc varchar(1000),
		@pos int,
		@user_temp varchar(50)
	
	set @user_listdesc = ''
	set @pos = charindex(';', @user_list)
	while @pos >= 0
	begin
		if @pos = 0
		begin
			set @user_temp = @user_list
			set @user_list = ''
		end
		else
		begin
			set @user_temp = substring(@user_list, 0, @pos)
			set @user_list = right(@user_list, len(@user_list) - @pos)
		end
	
		set @user_listdesc = @user_listdesc + dbo.f_get_user_desc(@user_temp) + @sign
			
		if len(@user_list) > 0
			set @pos = charindex(';', @user_list)
		else
			set @pos = -1
	end

	if @user_listdesc != ''
		set @user_listdesc = left(@user_listdesc, len(@user_listdesc) - 1)

	return @user_listdesc
END
GO

-- f_get_work_days
CREATE FUNCTION f_get_work_days
	(@year int, @month int)
RETURNS int AS 
BEGIN 
	declare @date1 datetime, @date9 datetime
	declare @i int, @w int
	select @date1=dateadd(mm, datediff(mm,0,cast(cast(@year as varchar) + '-' + cast(@month as varchar) + '-01' as datetime)), 0) 
	select @date9=dateadd(ms,-3,DATEADD(mm, DATEDIFF(m,0,@date1)+1, 0))
	select @i=datediff(day,@date1,@date9)+1
	
	while(@date9>=@date1) begin
		select @w=datepart(dw,@date1)-1
		--默认@@datefirst=7 @w=6表示星期六，@w=0表示星期天	
		--如果是假期直接减，否则看是不是星期六、天休息，是则减，再判断休息日是否调休，加一天。
		if(exists(select * from HR_HOLIDAY where @date1 between START_DATE and END_DATE))
		begin
			select @i=@i-1
		end
		else begin
			if(@w=6 or @w=0) and ((@w=6 and exists(select * from HR_SETUP where SAT_REST_DAY=1)) or  (@w=0 and exists(select * from HR_SETUP where SUN_REST_DAY=1)))
				select @i=@i-1
		end
		select @date1=dateadd(dd,1,@date1)
	end
	return @i
END
GO

-- f_split_emp_ids2names
CREATE FUNCTION dbo.f_split_emp_ids2names (@emp_ids varchar(3000))  
RETURNS nvarchar(3000) AS  
BEGIN 
    DECLARE @emp_names varchar(3000), @temp_name varchar(50)
   
    SET @emp_names = ''
    DECLARE cur_emp CURSOR FOR  SELECT emp_name FROM f_split_string2table(@emp_ids,';') a, sysdn b WHERE a.value=b.emp_id
    OPEN cur_emp
    FETCH NEXT FROM cur_emp INTO @temp_name
    WHILE @@FETCH_STATUS = 0 BEGIN
        SET @emp_names=@emp_names+@temp_name+' '
        FETCH NEXT FROM cur_emp INTO @temp_name
    END
    CLOSE cur_emp
    DEALLOCATE cur_emp

    RETURN @emp_names

END
GO

-- f_split_string2table
CREATE FUNCTION dbo.f_split_string2table
(@strList varchar(8000),@sign char(1))
RETURNS @T_RETURN table(id int,value varchar(300))
AS  
BEGIN 
	declare @intPos int, @strItem varchar(300), @index int

	set @index=1
	
	if len(isnull(@strList,''))=0 begin
		insert into @T_RETURN select @index,@strList
	end
	else if len(isnull(@sign,''))=0
		insert into @T_RETURN select @index,@strList
	else
	begin	
		set @intPos=charindex(@sign,@strList)
		while (@intPos>=0)
		begin
			if @intPos>0 
				set @strItem=left(@strList,@intPos-1)
			else
				set @strItem=@strList
				
			insert into @T_RETURN select @index,@strItem
				
			set @strList=right(@strList,len(@strList)-@intPos)
			if @intPos>0
				set @intPos=charindex(@sign,@strList)
			else
				set @intPos=-1
			
			set @index=@index+1
		end		
	end
	
return
END
GO


------------------------------------------------------------------------------
-- 视图
------------------------------------------------------------------------------

-- MOC_GET_SHOWSUM
CREATE VIEW dbo.MOC_GET_SHOWSUM
AS
SELECT GET_TYPE, GET_NO, PRO_NO, SUM(SEND_QTY) AS SEND_QTY, SUM(QTY) 
      AS QTY, SUM(RETURN_QTY) AS RETURN_QTY, SUM(RETURNED_QTY) 
      AS RETURNED_QTY
FROM dbo.MOC_GET_D
GROUP BY GET_TYPE, GET_NO, PRO_NO
GO

-- V_COP_ACCOUNT_M
CREATE VIEW V_COP_ACCOUNT_M
AS
SELECT     CLIENT_ID, SALES_ID, CURR_ID, SUM(AMOUNT) AS AMOUNT, SUM(RECEIVE_AMOUNT) AS RECEIVE_AMOUNT, SUM(AMOUNT - RECEIVE_AMOUNT) 
                      AS NOT_RECEIVE_AMOUNT
FROM         dbo.COP_ACCOUNT_M WHERE FINISHED_TAG=0 AND CONFIRM_TAG=1
GROUP BY CLIENT_ID, SALES_ID, CURR_ID
GO

-- V_HR_EMPLOYEE
CREATE VIEW dbo.V_HR_EMPLOYEE
AS
SELECT EMP_ID, EMP_NO, EMP_NAME, STATE
FROM dbo.HR_EMPLOYEE
GO

-- V_PRO_MAIN_SOURCE
CREATE VIEW dbo.V_PRO_MAIN_SOURCE
AS
SELECT 1 AS CODE, '采购' AS NAME
UNION
SELECT 2 AS CODE, '自制' AS NAME
GO

-- V_PUR_DUE_M
CREATE VIEW V_PUR_DUE_M
AS
SELECT     SUPPLIER_ID, CURR_ID, SUM(AMOUNT) AS AMOUNT, SUM(PAYOUT_AMOUNT) AS PAYOUT_AMOUNT, SUM(AMOUNT - PAYOUT_AMOUNT) 
                      AS NOT_PAYOUT_AMOUNT
FROM         dbo.PUR_DUE_M WHERE FINISHED_TAG=0 AND CONFIRM_TAG=1
GROUP BY SUPPLIER_ID, CURR_ID
GO

-- V_SYSDL_SYSDN
CREATE VIEW dbo.V_SYSDL_SYSDN
AS
SELECT dbo.SYSDL.USER_ID, dbo.SYSDN.EMP_ID, dbo.SYSDN.EMP_NAME
FROM dbo.SYSDL INNER JOIN
      dbo.SYSDN ON dbo.SYSDL.EMP_ID = dbo.SYSDN.EMP_ID
GO

-- V_TODAY
CREATE VIEW dbo.V_TODAY
AS
SELECT DATEADD(dd, DATEDIFF(dd, 0, GETDATE()), 0) AS TODAY
GO

-- V_WF_FORM_FLOW
CREATE VIEW dbo.V_WF_FORM_FLOW
AS
SELECT dbo.WFFORM.WF_M_IDX, dbo.WFFORM.FLOW_NAME, 
      dbo.WFFORM_FLOW.SORT_NO, dbo.WFFORM_FLOW.SUBFLOW_DESC
FROM dbo.WFFORM INNER JOIN
      dbo.WFFORM_FLOW ON 
      dbo.WFFORM.WF_M_IDX = dbo.WFFORM_FLOW.WF_M_IDX
GO

-- vw_Client
CREATE VIEW vw_Client
AS
SELECT 
	A.CLIENT_ID,
	A.CLIENT_NAME,
	A.FULL_NAME_CN,
	A.DELI_ADDR_CN,
	A.CURR_ID,
	A.PRICE_CONDITION,
	A.PAY_CONDITION,
	A.SALES_ID,
	A.REMARK,
	A.CREATE_PERSON,
	A.CREATE_DATE,
	A.LINKMAN,
	A.TEL,
	A.FAX,
	A.TAX_ID,
	(SELECT COUNT(*) FROM COP_ORDER_M WHERE CLIENT_ID=A.CLIENT_ID) OrderCount
FROM CLIENT AS A
GO

-- vw_ORDER_LIST
CREATE VIEW vw_ORDER_LIST
AS
SELECT
	B.CLIENT_ID AS 客户编号,
	A.ORDER_NO AS 订单号,
	B.ORDER_DATE AS 订单日期,
	A.PRE_SEND_DATE AS 预交日期,
	A.FACT_SEND_DATE AS 实交日期,
	DATEDIFF(DAY,B.ORDER_DATE,A.FACT_SEND_DATE) AS 实际交货周期
FROM COP_ORDER_D AS A 
	JOIN COP_ORDER_M AS B ON B.ORDER_NO=A.ORDER_NO
WHERE B.CLIENT_ID='TTI769' 
	AND B.ORDER_DATE BETWEEN '2013-12-31' AND '2015-01-01' 
	AND A.FACT_SEND_DATE IS NOT NULL
GO

-- VW_ORDER_SHEET
CREATE VIEW VW_ORDER_SHEET
AS
SELECT TOP 1000
	YEAR(ORDER_DATE) AS [Year],
	MONTH(ORDER_DATE) AS [Month],
	SUM(AMOUNT_TAX * CURR_RATE) AS Total
FROM COP_ORDER_M 
GROUP BY 
	YEAR(ORDER_DATE),
	MONTH(ORDER_DATE) 
ORDER BY 
	YEAR(ORDER_DATE) DESC,
	MONTH(ORDER_DATE) DESC;
GO

-- vw_TodaySend
Create VIEW vw_TodaySend
AS
SELECT
	C.CLIENT_NAME,
	C.SALES_ID,
	A.ORDER_NO,
	B.CLIENT_ORDER_NO,
	B.ORDER_DATE,
	A.PRO_NO,
	A.CLIENT_PRO_NO,
	A.QTY,
	A.FINISHED_SEND_QTY,
	A.QTY-A.FINISHED_SEND_QTY AS NOT_SEND_QTY,
	A.PRE_SEND_DATE
FROM COP_ORDER_D AS A
	JOIN COP_ORDER_M AS B ON B.ORDER_NO=A.ORDER_NO
	JOIN CLIENT AS C ON C.CLIENT_ID=B.CLIENT_ID
WHERE DATEDIFF(DAY,A.PRE_SEND_DATE,GETDATE())=0 AND A.FINISHED_TAG=0
GO

-- vw_UnusualOrder
CREATE VIEW vw_UnusualOrder
AS
SELECT
	C.CLIENT_NAME,
	C.SALES_ID,
	A.ORDER_NO,
	B.CLIENT_ORDER_NO,
	B.ORDER_DATE,
	A.PRO_NO,
	A.CLIENT_PRO_NO,
	A.QTY,
	A.FINISHED_SEND_QTY,
	A.QTY-A.FINISHED_SEND_QTY AS NOT_SEND_QTY,
	A.PRE_SEND_DATE
FROM COP_ORDER_D AS A
	JOIN COP_ORDER_M AS B ON B.ORDER_NO=A.ORDER_NO
	JOIN CLIENT AS C ON C.CLIENT_ID=B.CLIENT_ID
WHERE A.PRE_SEND_DATE < GETDATE() AND A.FINISHED_TAG=0
GO

-- vw_users
CREATE view vw_users
as
SELECT
	B.EMP_ID AS ID,
	B.EMP_NAME AS Name, 
	A.USER_ID AS Passport, 
	A.USER_PWD AS Password,
	C.DEPT_NAME
FROM
	dbo.SYSDL AS A INNER JOIN
    dbo.SYSDN AS B ON A.EMP_ID = B.EMP_ID INNER JOIN
    dbo.DEPT AS C ON C.DEPT_ID =B.DEPT_ID
GO


------------------------------------------------------------------------------
-- 存储过程
------------------------------------------------------------------------------

-- P_BOM_CHECK
CREATE PROCEDURE P_BOM_CHECK 
	@ProNo NVARCHAR(50), @ok INT OUTPUT, @errCode NVARCHAR(50) OUTPUT
AS
DECLARE @step INT
SELECT @ok = 1, @step = 1
--往下检查是否循环引用
SELECT @step StepNo, PRO_NO, ELEMENT_PRO_NO INTO #temp FROM BOM_STRU_D WHERE PRO_NO=@ProNo
WHILE 1=1 BEGIN
	SELECT TOP 1 @errCode=PRO_NO FROM #temp WHERE ELEMENT_PRO_NO=@ProNo AND StepNo=@step
	IF ISNULL(@errCode, '') != '' BEGIN
		SELECT @ok = 0
			BREAK 
	END
	ELSE BEGIN
		SELECT @step = @step + 1
		INSERT INTO #temp SELECT @step, b.PRO_NO, b.ELEMENT_PRO_NO FROM BOM_STRU_D b, #temp t WHERE b.PRO_NO = t.ELEMENT_PRO_NO AND t.StepNo = @step - 1
		IF @@ROWCOUNT<=0
			BREAK
	END
END

DROP TABLE #temp
GO

-- P_Change_M_IDX
CREATE PROCEDURE dbo.P_Change_M_IDX
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

END
GO

-- P_HRM_WAGE_CALC
CREATE PROCEDURE P_HRM_WAGE_CALC
	(@wage_type NCHAR(10),@wage_no NCHAR(20), @emp_ids VARCHAR(1000), @calc_mode char(1), @if_secrecy bit=0)
AS
	DECLARE @year CHAR(4),@month CHAR(2),@dept_id NCHAR(10)
	DECLARE @sql VARCHAR(8000)
	DECLARE @round INT
	SELECT @year=SUBSTRING(COUNT_MONTH,1,4), @month=SUBSTRING(COUNT_MONTH,5,6), @dept_id=LTRIM(RTRIM(ISNULL(DEPT_ID,''))) FROM HRM_WAGE_M WHERE WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no

	DECLARE @ymsql VARCHAR(1000), @filter_sql VARCHAR(1000), @serial_no int
	DECLARE @wage_field VARCHAR(50), @source_sql VARCHAR(4000), @source_exp VARCHAR(2000)

	SELECT @ymsql='', @filter_sql='', @serial_no=1, @emp_ids=ISNULL(@emp_ids,''), @calc_mode=ISNULL(@calc_mode,'A')

	SELECT @ymsql =  'declare @wage_type NCHAR(10),@wage_no NCHAR(20),@dept_id varchar(10),@emp_ids varchar(1000),@year int,@month int, @if_secrecy bit, @serial_no int  
		 select @wage_type='''+@wage_type+''', @wage_no='''+@wage_no+''', @year='+@year+', @month='+@month+',@dept_id='''+@dept_id+''',@emp_ids='''+@emp_ids+''',@if_secrecy='+cast(@if_secrecy as char(1))+', @serial_no=0 '
	BEGIN TRAN
	IF @calc_mode='A' BEGIN --计算整单
		--部门条件语句
		IF ISNULL(@dept_id,'') != ''
			SELECT @filter_sql = ' AND source.EMP_ID in(select EMP_ID from HR_EMPLOYEE where DEPT_ID in (select DEPT_ID from dbo.f_get_under_depts(@dept_id))) '

		PRINT '删除单据中有而不需要计算薪资的员工'
		SELECT @sql = ' DELETE HRM_WAGE_D where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no 
			and EMP_ID in (select EMP_ID from HR_EMPLOYEE where isnull(IF_SECRECY,0)<>@if_secrecy or isnull(IF_COUNT,0)=0 or (year(IN_DATE)*100+month(IN_DATE)>@year*100+@month) or (year(DIMISSION_DATE)*100+month(DIMISSION_DATE)<@year*100+@month) '
		IF ISNULL(@dept_id,'') != ''
			SELECT @sql = @sql + ' or DEPT_ID not in(select DEPT_ID from  dbo.f_get_under_depts(@dept_id))) '
		ELSE
			SELECT @sql = @sql +')'
		PRINT(@ymsql + @sql)
		EXEC(@ymsql+@sql)
		IF @@ERROR<>0
			GOTO ErrorHandler

		PRINT '插入单据中没有而需要计算薪资的新员工'
		SELECT @sql = ' SELECT @wage_type AS WAGE_TYPE, @wage_no as  WAGE_NO, @serial_no as SERIAL_NO, EMP_ID into #tmp FROM HR_EMPLOYEE source 
			WHERE IF_COUNT=1' + @filter_sql + ' and isnull(IF_SECRECY,0)=@if_secrecy and (year(IN_DATE)*100+month(IN_DATE)<=@year*100+@month) and (DIMISSION_DATE is null or year(DIMISSION_DATE)*100+month(DIMISSION_DATE)>=@year*100+@month) and EMP_ID not in (select EMP_ID from HRM_WAGE_M m, HRM_WAGE_D d where m.WAGE_TYPE=d.WAGE_TYPE and m.WAGE_NO=d.WAGE_NO and m.COUNT_MONTH=''' + @year + @month + ''') ORDER BY DEPT_ID,EMP_ID
			select @serial_no=max(SERIAL_NO) from HRM_WAGE_D where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no
			select @serial_no = isnull(@serial_no, 0)
			update #tmp set @serial_no=@serial_no+1, SERIAL_NO=@serial_no  
			INSERT INTO HRM_WAGE_D(WAGE_TYPE, WAGE_NO,SERIAL_NO,EMP_ID)
			SELECT WAGE_TYPE, WAGE_NO,SERIAL_NO,EMP_ID FROM #tmp drop table #tmp ' 

		PRINT(@ymsql + @sql)
		EXEC(@ymsql+@sql)
		IF @@ERROR<>0
			GOTO ErrorHandler

		--PRINT '删除原记录'
		--SELECT @sql = 'DELETE HRM_WAGE_D  WHERE WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no'
		--PRINT(@ymsql + @sql)
		--EXEC(@ymsql + @sql)
		--IF @@ERROR<>0
		--	GOTO ErrorHandler
		--PRINT '插入需计算薪资的员工'
		--SELECT @sql = 'SELECT @wage_type AS WAGE_TYPE, @wage_no as  WAGE_NO, @serial_no as SERIAL_NO, EMP_ID into #tmp FROM HR_EMPLOYEE source 
		--		WHERE IF_COUNT=1' + @filter_sql + ' update #tmp set @serial_no=@serial_no+1, SERIAL_NO=@serial_no  
		--	INSERT INTO HRM_WAGE_D(WAGE_TYPE, WAGE_NO,SERIAL_NO,EMP_ID)
		--   SELECT WAGE_TYPE, WAGE_NO,SERIAL_NO,EMP_ID FROM #tmp drop table #tmp ' 
		--PRINT(@ymsql + @sql)
		--EXEC(@ymsql+@sql)
		--IF @@ERROR<>0
		--	GOTO ErrorHandler
	END
	ELSE BEGIN --计算指定人员
		--人员条件
		SELECT @filter_sql = ' AND source.EMP_ID in('''+REPLACE(@emp_ids,',',''',''')+''') '
		PRINT '删除单据中有而不需要计算薪资的员工'
		SELECT @sql = 'DELETE HRM_WAGE_D where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no  
			and EMP_ID in('''+REPLACE(@emp_ids,',',''',''')+''')  and EMP_ID in (select EMP_ID from HR_EMPLOYEE where isnull(IF_SECRECY,0)<>@if_secrecy or isnull(IF_COUNT,0)=0 or (year(IN_DATE)*100+month(IN_DATE)>@year*100+@month) or (year(DIMISSION_DATE)*100+month(DIMISSION_DATE)<@year*100+@month)) '
		PRINT(@ymsql + @sql)
		EXEC(@ymsql+@sql)
		IF @@ERROR<>0
			GOTO ErrorHandler
		--SELECT @sql = 'DELETE HRM_WAGE_D  WHERE WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no' + ' AND EMP_ID in('''+REPLACE(@emp_ids,',',''',''')+''') '
		--PRINT(@ymsql + @sql)
		--EXEC(@ymsql + @sql)
		--IF @@ERROR<>0
		--	GOTO ErrorHandler
	END

	IF @@ERROR<>0
		GOTO ErrorHandler

	--删除员工基本资料中没有的员工
	delete from HRM_WAGE_D where WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no and EMP_ID not in (select EMP_ID from HR_EMPLOYEE)

	--删除当月离职员工
	if exists(select * from HR_SETUP where DIMISSION_NO_WAGE=1)
		delete from HRM_WAGE_D where WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no and EMP_ID in (select EMP_ID from HR_EMPLOYEE where year(DIMISSION_DATE)*100+month(DIMISSION_DATE)>=@year*100+@month)

	PRINT '计算薪资项目'
	DECLARE cur_wage CURSOR FOR SELECT ISNULL(SOURCE_SQL, ''), LTRIM(RTRIM(ISNULL(SOURCE_EXP, ''))), ISNULL(WAGE_FIELD,'') FROM HRM_WAGE WHERE ISNULL(IS_USED, 0)=1 ORDER BY CALC_ORDER
	OPEN cur_wage
	FETCH NEXT FROM cur_wage INTO @source_sql, @source_exp, @wage_field
	WHILE @@FETCH_STATUS = 0 BEGIN
		SELECT @round = (CASE WHEN LEN(display_format)>0 THEN PATINDEX('%.%',reverse(display_format)) ELSE 3 END) FROM fields WHERE T_ID='HRM_WAGE_D' and F_ID=@wage_field
		IF @round>0  BEGIN
			SELECT @round=@round-1
		END
		IF @source_exp !='' BEGIN
			SELECT @sql ='update HRM_WAGE_D set '+@wage_field +'=0  where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no ' + REPLACE(@filter_sql, 'source.', '') 
				+' update HRM_WAGE_D set '+@wage_field+'=round(' + @source_exp + ',' + STR(@round) + ') where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no ' + REPLACE(@filter_sql, 'source.', '')
		END
		ELSE  IF @source_sql !='' BEGIN
			SELECT @sql ='update HRM_WAGE_D set '+@wage_field +'=0 where WAGE_TYPE=@wage_type and WAGE_NO=@wage_no '  +  REPLACE(@filter_sql, 'source.', '') 
				+' update HRM_WAGE_D set '+@wage_field+'=round(source.VALUE,' + STR(@round) + ')  from (' + @source_sql + ') source
				where HRM_WAGE_D.EMP_ID=source.EMP_ID and WAGE_TYPE=@wage_type and WAGE_NO=@wage_no '
			   + @filter_sql
		END
		PRINT(@ymsql + @sql)
		EXEC(@ymsql +@sql)
		IF @@ERROR<>0 BEGIN
			CLOSE cur_wage
			DEALLOCATE cur_wage
			GOTO ErrorHandler
		END
		FETCH NEXT FROM cur_wage INTO @source_sql, @source_exp, @wage_field
	END
	CLOSE cur_wage
	DEALLOCATE cur_wage
	UPDATE HRM_WAGE_M SET LAST_UPDATE_DATE=cast(getdate() as smalldatetime) WHERE WAGE_TYPE=@wage_type AND WAGE_NO=@wage_no
--结束
IF @@ERROR<>0   AND @@TRANCOUNT <> 0
	ROLLBACK TRAN
ELSE
	COMMIT TRAN
ErrorHandler:
	IF @@TRANCOUNT <> 0
		ROLLBACK TRAN
GO

-- P_Run_After_Save
CREATE PROCEDURE P_Run_After_Save 
	@module int,
	@pri_idx nvarchar(1000)
AS
	declare @sp nvarchar(100), @df_verify nvarchar(500), @t_id nvarchar(50), @sql nvarchar(2000)
	declare @is_repeat bit

	select @sp = ltrim(rtrim(aftersave_sp)), @t_id=DETAIL_TABLE from modules where m_idx=@module
	--重复校验
	if ISNULL(@t_id, '') <> '' begin
		select @df_verify=DF_VERIFY from TABLES where T_ID=@t_id
		if ISNULL(@df_verify, '') <> '' begin
			select @df_verify = REPLACE(@df_verify, ';', ',')
			select @sql = 'if exists(select ' + @df_verify + ' from ' + @t_id + ' where ' + @pri_idx + ' group by ' + @df_verify + ' having count(*)>1)  select @is_repeat=1 else select @is_repeat=0'
			exec sp_executesql @sql, N'@is_repeat bit output', @is_repeat output
			if @is_repeat = 1 begin
				RAISERROR('明细表资料重复',16, 1)
				goto finally
			end
		end
	end

	if ISNULL(@sp,'')<>''
		exec @sp @pri_idx, @module

	finally:
GO

-- P_WF_COP_ACCOUNT
CREATE PROCEDURE P_WF_COP_ACCOUNT
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
	declare @account_type nchar(10), @account_no nchar(20)
	select @success = 1

	select @sql = N'select @account_type = ACCOUNT_TYPE, @account_no =  ACCOUNT_NO from COP_ACCOUNT_M where ' + @key_value
	exec sp_executesql @sql,N'@account_type nchar(10) output, @account_no nchar(20) output', @account_type output, @account_no output

	if @approve_tag = 1 begin --审核
		--检查
		exec P_COP_ACCOUNT_CHECK @account_type, @account_no, @success output, @msg output
		if @@ERROR<>0 or @success = 0 begin
			select @success=0
			goto endhandle
		end

		if @success = 1 begin
			--更新送、退货单对帐数量、金额
			update COP_SEND_D set COP_SEND_D.FINISHED_QTY = COP_SEND_D.FINISHED_QTY + sd.QTY,  COP_SEND_D.RECEIVED_AMOUNT = COP_SEND_D.RECEIVED_AMOUNT + sd.AMOUNT_TAX
				from (select S_R_TYPE,S_R_NO,S_R_SERIAL_NO,sum(QTY) QTY,sum(AMOUNT_TAX) AMOUNT_TAX from COP_ACCOUNT_D where ACCOUNT_TYPE=@account_type and ACCOUNT_NO=@account_no group by S_R_TYPE,S_R_NO,S_R_SERIAL_NO) sd 
				where COP_SEND_D.SEND_TYPE=sd.S_R_TYPE and COP_SEND_D.SEND_NO=sd.S_R_NO and COP_SEND_D.SERIAL_NO=sd.S_R_SERIAL_NO
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
			--退货为负数，故用减法COP_RETURN_D.FINISHED_QTY - sd.QTY
			update COP_RETURN_D set COP_RETURN_D.FINISHED_QTY = COP_RETURN_D.FINISHED_QTY - sd.QTY,  COP_RETURN_D.RETURNED_AMOUNT = COP_RETURN_D.RETURNED_AMOUNT - sd.AMOUNT_TAX
				from (select S_R_TYPE,S_R_NO,S_R_SERIAL_NO,sum(QTY) QTY,sum(AMOUNT_TAX) AMOUNT_TAX from COP_ACCOUNT_D where ACCOUNT_TYPE=@account_type and ACCOUNT_NO=@account_no group by S_R_TYPE,S_R_NO,S_R_SERIAL_NO) sd 
				where COP_RETURN_D.RETURN_TYPE=sd.S_R_TYPE and COP_RETURN_D.RETURN_NO=sd.S_R_NO and COP_RETURN_D.SERIAL_NO=sd.S_R_SERIAL_NO
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
			--依据对帐单上所属月份（如为空从对帐日期取年-月-）的下个月第一天加上客户资料中月结天数（为空则表示现金）
			declare @cmonth char(8),@nadd int
			select @cmonth = case when isnull(m.account_month,'')='' then convert(char(8),m.account_date,20) else left(m.account_month,4)+'-'+right(m.account_month,2)+'-' end,@nadd=isnull(d.PAYMENT_DAY,0) from COP_ACCOUNT_M m,client d where m.ACCOUNT_TYPE=@account_type and  m.ACCOUNT_NO=@account_no and m.CLIENT_ID=d.CLIENT_ID
			update COP_ACCOUNT_M set PRE_RECEIVE_DATE= dateadd(day,@nadd,dateadd(mm,1,cast(@cmonth+'01' as datetime))) where COP_ACCOUNT_M.ACCOUNT_TYPE=@account_type and COP_ACCOUNT_M.ACCOUNT_NO=@account_no
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
		end
	end
	else begin	--解批
		if @success = 1 begin
			--更新送、退货单对帐数量、金额
			update COP_SEND_D set COP_SEND_D.FINISHED_QTY = COP_SEND_D.FINISHED_QTY - sd.QTY,  COP_SEND_D.RECEIVED_AMOUNT = COP_SEND_D.RECEIVED_AMOUNT - sd.AMOUNT_TAX
				from (select S_R_TYPE,S_R_NO,S_R_SERIAL_NO,sum(QTY) QTY,sum(AMOUNT_TAX) AMOUNT_TAX from COP_ACCOUNT_D where ACCOUNT_TYPE=@account_type and ACCOUNT_NO=@account_no group by S_R_TYPE,S_R_NO,S_R_SERIAL_NO) sd 
				where COP_SEND_D.SEND_TYPE=sd.S_R_TYPE and COP_SEND_D.SEND_NO=sd.S_R_NO and COP_SEND_D.SERIAL_NO=sd.S_R_SERIAL_NO
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
			update COP_RETURN_D set COP_RETURN_D.FINISHED_QTY = COP_RETURN_D.FINISHED_QTY + sd.QTY,  COP_RETURN_D.RETURNED_AMOUNT = COP_RETURN_D.RETURNED_AMOUNT + sd.AMOUNT_TAX
				from (select S_R_TYPE,S_R_NO,S_R_SERIAL_NO,sum(QTY) QTY,sum(AMOUNT_TAX) AMOUNT_TAX from COP_ACCOUNT_D where ACCOUNT_TYPE=@account_type and ACCOUNT_NO=@account_no group by S_R_TYPE,S_R_NO,S_R_SERIAL_NO) sd 
				where COP_RETURN_D.RETURN_TYPE=sd.S_R_TYPE and COP_RETURN_D.RETURN_NO=sd.S_R_NO and COP_RETURN_D.SERIAL_NO=sd.S_R_SERIAL_NO
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
		end
	end

	if @success = 1 begin
		--更新送货单结案标志
		select @sql =  'select distinct S_R_TYPE, S_R_NO from COP_ACCOUNT_D where ' + @key_value 
		exec P_COP_SEND_FINISHED  @sql, @approve_tag, 1, @success output
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end

	if @success = 1 begin
		--更新退货单结案标志
		select @sql =  'select distinct S_R_TYPE, S_R_NO from COP_ACCOUNT_D where ' + @key_value 
		exec P_COP_RETURN_FINISHED  @sql, @approve_tag, 1, @success output
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end

	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end
endhandle:
	return  @success
GO

-- P_WF_COP_ORDER
CREATE PROCEDURE P_WF_COP_ORDER
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000), @mrp_qty float, @pro_mrp nchar(30)
	declare @serial_no int, @qty float, @finished_qty float, @my_qty float, @spare_qty float, @finished_spare_qty float , @my_spare_qty float, @pro_no nchar(30) 
	declare @order_type nchar(10), @order_no nchar(20), @order_date datetime, @client_id nchar(10), @client_curr_id nchar(10), @client_curr_rate float, @last_trade_date datetime, @client_days int, @product_days int, @amount_tax float, @curr_id nchar(10), @curr_rate float, @credit_limit_num float
	declare @credit_days int, @min_order_amount float
	select @success = 1

	select @sql = N'select @order_type = ORDER_TYPE, @order_no = ORDER_NO, @order_date=ORDER_DATE, @client_id=CLIENT_ID, @amount_tax=AMOUNT_TAX, @curr_id=CURR_ID, @curr_rate=CURR_RATE from COP_ORDER_M where ' + @key_value
	exec sp_executesql @sql,N'@order_type nchar(10) output, @order_no nchar(20) output, @order_date datetime output, @client_id nchar(10) output, @amount_tax float output, @curr_id nchar(10) output, @curr_rate float output', @order_type output, @order_no output, @order_date output, @client_id output, @amount_tax output, @curr_id output, @curr_rate output
	
	select @last_trade_date=c.LAST_TRADE_DATE, @credit_limit_num=c.CREDIT_LIMIT_NUM, @credit_days=c.PAYMENT_DAY, @min_order_amount=c.MIN_ORDER_AMOUNT, @client_curr_id=c.CURR_ID, @client_curr_rate=r.CURR_RATE
		from CLIENT c left join CURR r on c.CURR_ID=r.CURR_ID
		where CLIENT_ID=@client_id
	if isnull(@client_curr_rate, 0)=0 begin
		select @client_curr_rate = 1
	end

	if @approve_tag = 1 begin --审核
		--检查
		exec P_COP_ORDER_CHECK @order_type, @order_no, @success output, @msg output
		if @@ERROR<>0 or @success = 0 begin
			select @success=0
			goto endhandle
		end
	end
	
	--更新客户信用余额
	update CLIENT set CREDIT_LIMIT_NUM=round((CREDIT_LIMIT_NUM*@client_curr_rate + @amount_tax * @curr_rate * @approve_tag)/@client_curr_rate, 2)
		where CLIENT_ID=@client_id
	if @@ERROR<>0 begin
		select @success=0, @msg = '更新客户信用余额失败'
		goto endhandle
	end

	if exists(select * from SYSSS where PRO_MRP=1) and @success = 1 begin
		if @approve_tag = 1 begin
			--计算计划生产数量  需考虑同一张订单两个相同品号
			declare cur_tmp cursor for
				select SERIAL_NO, PRO_NO, QTY, SPARE_QTY from COP_ORDER_D where ORDER_TYPE=@order_type and ORDER_NO=@order_no order by PRO_NO
			open cur_tmp
			fetch next from cur_tmp into @serial_no, @pro_mrp, @qty, @spare_qty
			while @@FETCH_STATUS = 0  begin
				if @pro_no<>@pro_mrp
					select @pro_no=@pro_mrp, @mrp_qty = MRP_QTY from PRODUCT where PRO_NO=@pro_mrp
				--如果有MRP库存，先扣MRP库存，减少计划生产
				update COP_ORDER_D set PLAN_QTY=case when @mrp_qty>0 then case when @mrp_qty>=@qty then 0 else @qty-@mrp_qty end else @qty end,
					PLAN_SPARE_QTY=case when @mrp_qty-@qty>0 then case when @mrp_qty-@qty>=@spare_qty then 0 else @spare_qty-(@mrp_qty-@qty) end else @spare_qty end,
					DEPOT_QTY=case when @mrp_qty>0 then case when @mrp_qty>=@qty+@spare_qty then @qty+@spare_qty else @mrp_qty end else 0 end where ORDER_TYPE=@order_type and ORDER_NO=@order_no and SERIAL_NO=@serial_no
				fetch next from cur_tmp into @serial_no, @pro_mrp, @qty, @spare_qty
			end
			close cur_tmp
			deallocate cur_tmp
			if @@ERROR<>0 begin
				select @success=0, @msg = '计算计划生产数量失败'
				goto endhandle
			end
		end
		--更新可用库存
		--select @sql =  'select d.PRO_NO, '''', d.QTY + d.SPARE_QTY, d.UNIT_ID from (select * from COP_ORDER_M where ' + @key_value + ') m, COP_ORDER_D d  where m.ORDER_TYPE=d.ORDER_TYPE and m.ORDER_NO=d.ORDER_NO'
		--exec @success = P_UPDATE_PRO_MRP_QTY @sql, -1, @approve_tag, @msg output

		--更新产品预计送货
		update PRODUCT set NOT_SEND_QTY=isnull(PRODUCT.NOT_SEND_QTY,0)+d.QTY*@approve_tag, MRP_QTY=isnull(PRODUCT.MRP_QTY,0)-d.QTY*@approve_tag 
			from (select PRO_NO, sum(QTY+SPARE_QTY-FINISHED_SEND_QTY-FINISHED_SPARE_QTY) QTY from COP_ORDER_D where ORDER_TYPE=@order_type and ORDER_NO=@order_no and QTY+SPARE_QTY>FINISHED_SEND_QTY+FINISHED_SPARE_QTY group by PRO_NO) d
			where PRODUCT.PRO_NO=d.PRO_NO
		if @@ERROR <> 0 begin
			select @success = 0, @msg='更新产品预计送货失败'
			goto endhandle
		end
	end

endhandle:
	return  @success
GO

-- P_WF_COP_PREPAY
CREATE PROCEDURE P_WF_COP_PREPAY
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
    	declare @prepay_type nchar(10), @prepay_no nchar(20), @client_id nchar(10), @amount_tax float, @curr_rate float, @client_curr_id nchar(10), @client_curr_rate float, @bank_amount float, @bank_rate float
	select @success=1

	select @sql = 'select @prepay_type=PREPAY_TYPE, @prepay_no=PREPAY_NO, @client_id=CLIENT_ID, @amount_tax=AMOUNT, @curr_rate=CURR_RATE from COP_PREPAY_M  where ' + @key_value
	exec sp_executesql @sql, N'@prepay_type nchar(10) output, @prepay_no nchar(20) output, @client_id nchar(10) output, @amount_tax float output, @curr_rate float output', @prepay_type output, @prepay_no output, @client_id output, @amount_tax output, @curr_rate output
	if @@ERROR <> 0 begin
		select @success = 0
		goto errhandle
	end

	select @client_curr_id=c.CURR_ID, @client_curr_rate=r.CURR_RATE
		from CLIENT c left join CURR r on c.CURR_ID=r.CURR_ID
		where CLIENT_ID=@client_id
	if isnull(@client_curr_rate, 0)=0 begin
		select @client_curr_rate = 1
	end

	select @bank_amount=b.AMOUNT, @bank_rate=c.CURR_RATE
		from BANK b left join CURR c on b.CURR_ID=c.CURR_ID 
		where b.BANK_ID in(select BANK_ID from COP_PREPAY_M where PREPAY_TYPE=@prepay_type and PREPAY_NO=@prepay_no)
	if isnull(@bank_rate, 0) = 0
		select @bank_rate = 1

	--检查银行结存
	if @bank_amount*@bank_rate + @amount_tax * @curr_rate * @approve_tag < 0 begin
		select @success = 0, @msg = '银行结存不足'
		goto errhandle
	end
	--更新银行金额
	update BANK set AMOUNT=round((BANK.AMOUNT * @bank_rate + @amount_tax * @curr_rate * @approve_tag)/@bank_rate, 2)
		from COP_PREPAY_M
		where COP_PREPAY_M.PREPAY_TYPE=@prepay_type and COP_PREPAY_M.PREPAY_NO=@prepay_no and BANK.BANK_ID=COP_PREPAY_M.BANK_ID
	if @@ERROR<>0 begin
		select @success = 0, @msg='更新银行结存错误'
		goto errhandle
	end

	--更新客户信用余额
	update CLIENT set CREDIT_LIMIT_NUM=round((CREDIT_LIMIT_NUM*@client_curr_rate + @amount_tax*@curr_rate * @approve_tag) / @client_curr_rate, 2),PREPAY_SUM=round((PREPAY_SUM*@client_curr_rate + @amount_tax*@curr_rate * @approve_tag) / @client_curr_rate, 2)
		where CLIENT_ID=@client_id
	if @@ERROR <> 0 begin
		select @success = 0
		goto errhandle
	end
	--更新订单预收金额
	update COP_ORDER_D set FINISHED_AMOUNT = FINISHED_AMOUNT + sd.AMOUNT* @approve_tag 
		from (select ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, sum(AMOUNT) AMOUNT  from COP_PREPAY_D where PREPAY_TYPE=@prepay_type and PREPAY_NO=@prepay_no  group by ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO) sd 
		where COP_ORDER_D.ORDER_TYPE=sd.ORDER_TYPE and COP_ORDER_D.ORDER_NO=sd.ORDER_NO and COP_ORDER_D.SERIAL_NO=sd.ORDER_SERIAL_NO
	if @@ERROR <> 0 begin
		select @success = 0
		goto errhandle
	end

errhandle:
	return  @success
GO

-- P_WF_COP_QUOTE
CREATE PROCEDURE P_WF_COP_QUOTE
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
	declare @quote_type nchar(10), @quote_no nchar(20), @client_id nchar(10), @in_effect_date datetime

	select @sql = 'select @quote_type=QUOTE_TYPE , @quote_no=QUOTE_NO, @client_id=CLIENT_ID, @in_effect_date=IN_EFFECT_DATE from COP_QUOTE_M  where ' + @key_value
	exec sp_executesql @sql, N'@quote_type  nchar(10) output, @quote_no nchar(20) output, @client_id nchar(10) output, @in_effect_date datetime output', @quote_type output, @quote_no output, @client_id output, @in_effect_date output
	if @approve_tag = 1 begin
		--更新客户计价数据
		insert into CLIENT_PRICE_M(CLIENT_ID, CREATE_PERSON, CREATE_DATE)
			select CLIENT_ID, 'SYSTEM', QUOTE_DATE
			from COP_QUOTE_M
			where QUOTE_TYPE=@quote_type and QUOTE_NO=@quote_no and not exists (select * from CLIENT_PRICE_M where CLIENT_ID=COP_QUOTE_M.CLIENT_ID)
		if @@ERROR<>0 begin
			select @success = 0
			goto errhand
		end

		update CLIENT_PRICE_D set CLIENT_PRICE_D.UNIT_ID=d.UNIT_ID, CLIENT_PRICE_D.CURR_ID=d.CURR_ID, CLIENT_PRICE_D.CURR_RATE=d.CURR_RATE, CLIENT_PRICE_D.TAX_ID=d.TAX_ID, CLIENT_PRICE_D.TAX_RATE=d.TAX_RATE, CLIENT_PRICE_D.TAX_TYPE=d.TAX_TYPE, CLIENT_PRICE_D.PRICE=d.PRICE, CLIENT_PRICE_D.PROCESS_PRICE=d.PROCESS_PRICE, CLIENT_PRICE_D.CLIENT_PRO_NO=d.CLIENT_PRO_NO,
				CLIENT_PRICE_D.REBATE=d.REBATE, CLIENT_PRICE_D.VERIFY_PRICE_DATE=m.QUOTE_DATE, CLIENT_PRICE_D.OLD_PRICE=CLIENT_PRICE_D.PRICE, CLIENT_PRICE_D.OLD_PRICE_DATE=CLIENT_PRICE_D.VERIFY_PRICE_DATE, 
				CLIENT_PRICE_D.QUOTE_TYPE=m.QUOTE_TYPE, CLIENT_PRICE_D.QUOTE_NO=m.QUOTE_NO, CLIENT_PRICE_D.QUOTE_SERIAL_NO=d.SERIAL_NO, CLIENT_PRICE_D.IN_EFFECT_DATE=m.IN_EFFECT_DATE, CLIENT_PRICE_D.REMARK=d.REMARK
			from COP_QUOTE_M m, COP_QUOTE_D d 
			where m.QUOTE_TYPE=d.QUOTE_TYPE and m.QUOTE_NO=d.QUOTE_NO and m.QUOTE_TYPE=@quote_type and m.QUOTE_NO=@quote_no --and m.QUOTE_DATE>=CLIENT_PRICE_D.VERIFY_PRICE_DATE
				 and m.CLIENT_ID=CLIENT_PRICE_D.CLIENT_ID and d.PRO_NO=CLIENT_PRICE_D.PRO_NO and d.CURR_ID=CLIENT_PRICE_D.CURR_ID and d.TAX_ID=CLIENT_PRICE_D.TAX_ID and d.TAX_TYPE=CLIENT_PRICE_D.TAX_TYPE and d.UNIT_ID=CLIENT_PRICE_D.UNIT_ID and d.REBATE=CLIENT_PRICE_D.REBATE
		if @@ERROR<>0 begin
			select @success = 0
			goto errhand
		end

		insert into CLIENT_PRICE_D(CLIENT_ID, PRO_NO, UNIT_ID, CURR_ID, CURR_RATE, TAX_ID, TAX_RATE, TAX_TYPE, PRICE, PROCESS_PRICE, CLIENT_PRO_NO, REBATE, VERIFY_PRICE_DATE, OLD_PRICE, OLD_PRICE_DATE, QUOTE_TYPE, QUOTE_NO, QUOTE_SERIAL_NO, IN_EFFECT_DATE, REMARK)
			select m.CLIENT_ID, d.PRO_NO, d.UNIT_ID, d.CURR_ID, d.CURR_RATE, d.TAX_ID, d.TAX_RATE, d.TAX_TYPE, d.PRICE, d.PROCESS_PRICE, d.CLIENT_PRO_NO, d.REBATE, m.QUOTE_DATE, null, null, d.QUOTE_TYPE, d.QUOTE_NO, d.SERIAL_NO, m.IN_EFFECT_DATE, d.REMARK
			from COP_QUOTE_M m, COP_QUOTE_D d 
			where m.QUOTE_TYPE=d.QUOTE_TYPE and m.QUOTE_NO=d.QUOTE_NO and m.QUOTE_TYPE=@quote_type and m.QUOTE_NO=@quote_no 
				and not exists(select * from CLIENT_PRICE_D where CLIENT_ID=m.CLIENT_ID and PRO_NO=d.PRO_NO and CURR_ID=d.CURR_ID and TAX_ID=d.TAX_ID and TAX_TYPE=d.TAX_TYPE and UNIT_ID=d.UNIT_ID and REBATE=d.REBATE)
		if @@ERROR<>0 begin
			select @success = 0
			goto errhand
		end

		--更新料件的最近单价
		update PRODUCT set LAST_PURCHASE_PRICE = d.PRICE,LAST_PURCHASE_UNIT_ID = d.UNIT_ID,LAST_PURCHASE_CURR_ID=d.CURR_ID from COP_QUOTE_M m, COP_QUOTE_D d where m.QUOTE_TYPE=d.QUOTE_TYPE and m.QUOTE_NO=d.QUOTE_NO and m.QUOTE_TYPE=@quote_type and m.QUOTE_NO=@quote_no and d.PRO_NO = PRODUCT.PRO_NO and m.QUOTE_DATE>=isnull(PRODUCT.LAST_TRADE_DATE,'1900-01-01') 
		
		--更新询价单 的报价单号
		update COP_CHAFFER_D set QUOTE_TYPE=d.QUOTE_TYPE, QUOTE_NO=d.QUOTE_NO, QUOTE_SERIAL_NO=d.SERIAL_NO
			from COP_QUOTE_D d where d.QUOTE_TYPE=@quote_type and d.QUOTE_NO=@quote_no  and COP_CHAFFER_D.CHAFFER_TYPE=d.CHAFFER_TYPE and COP_CHAFFER_D.CHAFFER_NO=d.CHAFFER_NO and COP_CHAFFER_D.SERIAL_NO=d.CHAFFER_SERIAL_NO

	end
	else begin
		--判断计价表里是否有新单价
		if exists(select * from CLIENT_PRICE_D where CLIENT_ID=@client_id and IN_EFFECT_DATE>@in_effect_date and PRO_NO in(select PRO_NO from COP_QUOTE_D where QUOTE_TYPE=@quote_type and QUOTE_NO=@quote_no)) begin
			select @success = 0, @msg = '此报价单中部份产品已有最新报价'
			goto errhand
		end
		if @@ERROR<>0 begin
			select @success = 0
			goto errhand
		end

		--还原旧单价
		update CLIENT_PRICE_D set PRICE=OLD_PRICE, QUOTE_TYPE='', QUOTE_NO='', QUOTE_SERIAL_NO=0
			where QUOTE_TYPE=@quote_type and QUOTE_NO=@quote_no
		if @@ERROR <> 0 begin
			select @success = 0, @msg='还原旧单价失败'
			goto errhand
		end

	end
	select @success=1
errhand:
	return  @success
GO

-- P_WF_COP_RECEIPT
CREATE PROCEDURE P_WF_COP_RECEIPT
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
	declare @receipt_type nchar(10), @receipt_no nchar(20), @client_id nchar(10), @receive_sum float, @curr_rate float, @client_curr_id nchar(10), @client_curr_rate float, @bank_amount float, @bank_rate float, @prepay_sum float
	select @success=1

	select @sql = 'select @receipt_type=RECEIPT_TYPE, @receipt_no=RECEIPT_NO, @client_id=CLIENT_ID,  @receive_sum=RECEIVE_SUM, @curr_rate=CURR_RATE, @prepay_sum=PREPAY_SUM from COP_RECEIPT_M  where ' + @key_value
	exec sp_executesql @sql, N'@receipt_type nchar(10) output, @receipt_no nchar(20) output, @client_id nchar(10) output, @receive_sum float output, @curr_rate float output, @prepay_sum float output ', @receipt_type output, @receipt_no output, @client_id output, @receive_sum output, @curr_rate output, @prepay_sum output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	if @approve_tag = 1 begin --审核
		if @success = 1 begin
			--检查
			exec P_COP_RECEIPT_CHECK @receipt_type, @receipt_no, @success output, @msg output
			if @@ERROR<>0 or @success = 0 begin
				select @success=0
				goto endhandle
			end
		end
	end

	select @client_curr_id=c.CURR_ID, @client_curr_rate=r.CURR_RATE
		from CLIENT c left join CURR r on c.CURR_ID=r.CURR_ID
		where CLIENT_ID=@client_id
	if isnull(@client_curr_rate, 0)=0 begin
		select @client_curr_rate = 1
	end

	select @bank_amount=b.AMOUNT, @bank_rate=c.CURR_RATE
		from BANK b left join CURR c on b.CURR_ID=c.CURR_ID 
		where b.BANK_ID in(select BANK_ID from COP_RECEIPT_M where RECEIPT_TYPE=@receipt_type and RECEIPT_NO=@receipt_no)
	if isnull(@bank_rate, 0) = 0
		select @bank_rate = 1

	--检查银行结存
	if @bank_amount*@bank_rate + @receive_sum * @curr_rate * @approve_tag < 0 begin
		select @success = 0, @msg = '银行结存不足'
		goto endhandle
	end

	--更新银行结存
	update BANK set AMOUNT=round((BANK.AMOUNT * @bank_rate + @receive_sum * @curr_rate * @approve_tag)/@bank_rate, 2)
		from COP_RECEIPT_M
		where COP_RECEIPT_M.RECEIPT_TYPE=@receipt_type and COP_RECEIPT_M.RECEIPT_NO=@receipt_no and BANK.BANK_ID=COP_RECEIPT_M.BANK_ID
	if @@ERROR<>0 begin
		select @success = 0
		goto endhandle
	end

	--更新应收帐款
	update COP_ACCOUNT_M set COP_ACCOUNT_M.RECEIVE_AMOUNT=COP_ACCOUNT_M.RECEIVE_AMOUNT+COP_RECEIPT_D.RECEIVE_AMOUNT*@approve_tag
		from COP_RECEIPT_D
		where COP_RECEIPT_D.RECEIPT_TYPE=@receipt_type and COP_RECEIPT_D.RECEIPT_NO=@receipt_no and COP_RECEIPT_D.ACCOUNT_TYPE=COP_ACCOUNT_M.ACCOUNT_TYPE and COP_RECEIPT_D.ACCOUNT_NO=COP_ACCOUNT_M.ACCOUNT_NO
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	--预收冲帐
	update COP_PREPAY_M set COP_PREPAY_M.PREPAY_AMOUNT=COP_PREPAY_M.PREPAY_AMOUNT+d.PREPAY_AMOUNT*@approve_tag
		from COP_RECEIPT_M m, COP_RECEIPT_PREPAY d
		where m.RECEIPT_TYPE=@receipt_type and m.RECEIPT_NO=@receipt_no and m.RECEIPT_TYPE=d.RECEIPT_TYPE and m.RECEIPT_NO=d.RECEIPT_NO
			and COP_PREPAY_M.PREPAY_TYPE=d.PREPAY_TYPE and COP_PREPAY_M.PREPAY_NO=d.PREPAY_NO
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	--更新客户信用余额，预收款抵扣
	update CLIENT set CREDIT_LIMIT_NUM=round((CREDIT_LIMIT_NUM*@client_curr_rate + @receive_sum*@curr_rate * @approve_tag) / @client_curr_rate, 2),PREPAY_SUM=round((PREPAY_SUM*@client_curr_rate - @prepay_sum*@curr_rate * @approve_tag) / @client_curr_rate, 2)
		where CLIENT_ID=@client_id
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	--更新对帐单结案标志
	select @sql =  'select distinct ACCOUNT_TYPE, ACCOUNT_NO from COP_RECEIPT_D where ' + @key_value 
	exec P_COP_ACCOUNT_FINISHED  @sql, @approve_tag, 1, @success output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end


	--更新预收帐款结案标志
	select @sql =  'select distinct PREPAY_TYPE, PREPAY_NO from COP_RECEIPT_PREPAY where ' + @key_value 
	exec P_COP_PREPAY_FINISHED  @sql, @approve_tag, 1, @success output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	select @success=1

endhandle:
	return  @success
GO

-- P_WF_COP_SEND
CREATE PROCEDURE P_WF_COP_SEND
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
	declare @order_type nchar(10), @order_no nchar(20), @fitout_type nchar(10), @fitout_no nchar(20), @qty float, @spare_qty float, @finished_send_qty float, @finished_fitout_qty float, @fitout_return_qty float, @fitout_return_spare_qty float, @my_qty float, @finished_spare_qty float, @my_spare_qty float 
	declare @produce_type nchar(10), @produce_no nchar(20)
	declare @send_type nchar(10), @send_no nchar(20), @send_date datetime
	select @success = 1

	select @sql = N'select @send_type = SEND_TYPE, @send_no =  SEND_NO, @send_date=SEND_DATE from COP_SEND_M where ' + @key_value
	exec sp_executesql @sql,N'@send_type nchar(10) output, @send_no nchar(20) output, @send_date datetime output', @send_type output, @send_no output, @send_date output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end
	if @approve_tag = 1 begin --审核
		--检查
		exec P_COP_SEND_CHECK @send_type, @send_no, @success output, @msg output
		if @@ERROR<>0 or @success = 0 begin
			select @success=0
			goto endhandle
		end
		--更新订单送货数量、实交日期
		update COP_ORDER_D set FINISHED_SEND_QTY = isnull(FINISHED_SEND_QTY,0) + isnull(sd.QTY,0), FINISHED_SPARE_QTY = isnull(FINISHED_SPARE_QTY,0) + isnull(sd.SPARE_QTY,0), FACT_SEND_DATE= case when sd.SEND_DATE>isnull(FACT_SEND_DATE,'1900/1/1') then SEND_DATE else FACT_SEND_DATE end  
			from (select m.SEND_DATE, d.SEND_TYPE, d.SEND_NO, d.ORDER_TYPE, d.ORDER_NO, d.ORDER_SERIAL_NO, sum(d.QTY) QTY, sum(d.SPARE_QTY) SPARE_QTY from COP_SEND_D d,(select SEND_TYPE, SEND_NO, SEND_DATE from COP_SEND_M where SEND_TYPE=@send_type and SEND_NO=@send_no) m where m.SEND_TYPE=d.SEND_TYPE and m.SEND_NO=d.SEND_NO group by m.SEND_DATE, d.SEND_TYPE, d.SEND_NO, d.ORDER_TYPE, d.ORDER_NO, d.ORDER_SERIAL_NO) sd 
			where COP_ORDER_D.ORDER_TYPE=sd.ORDER_TYPE and COP_ORDER_D.ORDER_NO=sd.ORDER_NO and COP_ORDER_D.SERIAL_NO=sd.ORDER_SERIAL_NO
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
		--更新送货排程送货数量、实交日期
		update COP_SHIPMENT_D set FINISHED_QTY = isnull(FINISHED_QTY,0) + isnull(sd.QTY,0), FACT_SEND_DATE= case when sd.SEND_DATE>isnull(FACT_SEND_DATE,'1900/1/1') then sd.SEND_DATE else FACT_SEND_DATE end,SEND_TYPE=sd.SEND_TYPE,SEND_NO=sd.SEND_NO 
			from (select m.SEND_DATE, d.SEND_TYPE, d.SEND_NO, d.SHIPMENT_TYPE, d.SHIPMENT_NO, d.SHIPMENT_SERIAL_NO, sum(d.QTY) QTY from COP_SEND_D d,(select SEND_TYPE, SEND_NO, SEND_DATE from COP_SEND_M where SEND_TYPE=@send_type and SEND_NO=@send_no) m where m.SEND_TYPE=d.SEND_TYPE and m.SEND_NO=d.SEND_NO group by m.SEND_DATE, d.SEND_TYPE, d.SEND_NO, d.SHIPMENT_TYPE, d.SHIPMENT_NO, d.SHIPMENT_SERIAL_NO) sd 
			where COP_SHIPMENT_D.SHIPMENT_TYPE=sd.SHIPMENT_TYPE and COP_SHIPMENT_D.SHIPMENT_NO=sd.SHIPMENT_NO and COP_SHIPMENT_D.SERIAL_NO=sd.SHIPMENT_SERIAL_NO
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end

		--更新客户最近交易日期
		update CLIENT set CLIENT.LAST_TRADE_DATE = case when isnull(CLIENT.LAST_TRADE_DATE, '1900/1/1')>COP_SEND_M.SEND_DATE then CLIENT.LAST_TRADE_DATE else COP_SEND_M.SEND_DATE end
			from COP_SEND_M
			where COP_SEND_M.SEND_TYPE=@send_type and COP_SEND_M.SEND_NO=@send_no and COP_SEND_M.CLIENT_ID=CLIENT.CLIENT_ID
		if @@ERROR <> 0 begin
			select @success=0
			goto endhandle
		end
		--更新料件最近交易日期
		update PRODUCT set PRODUCT.LAST_TRADE_DATE = case when isnull(PRODUCT.LAST_TRADE_DATE, '1900/1/1')>@send_date then PRODUCT.LAST_TRADE_DATE else @send_date end
			from COP_SEND_D
			where COP_SEND_D.SEND_TYPE=@send_type and COP_SEND_D.SEND_NO=@send_no and COP_SEND_D.PRO_NO=PRODUCT.PRO_NO
		if @@ERROR <> 0 begin
			select @success=0
			goto endhandle
		end
	end
	else begin	--解批
		if exists(select * from SYSSS where SEND_ORDER_FITOUT_TAG=1) begin
			--检查订单是否出完
			declare cur_tmp cursor for
				select od.ORDER_TYPE, od.ORDER_NO, od.FINISHED_FITOUT_QTY, od.FINISHED_FITOUT_SPARE_QTY, od.FINISHED_SEND_QTY, od.FINISHED_SPARE_QTY, sd.QTY, sd.SPARE_QTY 
					from COP_ORDER_D od, (select ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, sum(QTY) QTY,  sum(SPARE_QTY) SPARE_QTY from COP_SEND_D where SEND_TYPE=@send_type and SEND_NO=@send_no  group by ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO) sd 
					where od.ORDER_TYPE=sd.ORDER_TYPE and od.ORDER_NO=sd.ORDER_NO and od.SERIAL_NO=sd.ORDER_SERIAL_NO and (od.FINISHED_SEND_QTY < sd.QTY or od.FINISHED_SPARE_QTY < sd.SPARE_QTY)
			open cur_tmp
			fetch next from cur_tmp into @order_type, @order_no, @qty, @spare_qty, @finished_send_qty, @finished_spare_qty, @my_qty, @my_spare_qty
			select @msg = ''
			while @@FETCH_STATUS = 0  begin
				select @success = 0, @msg = @msg + rtrim(@order_no) + '    ' + cast(@qty as varchar) + '    ' + cast(@finished_send_qty as varchar) + '    '  + cast(@my_qty as varchar) + '    '  + cast(@spare_qty as varchar) +  '    '  + cast(@finished_spare_qty as varchar) + '    '  + cast(@my_spare_qty as varchar) + '    '  + char(13)
				fetch next from cur_tmp into @order_type, @order_no, @qty, @spare_qty, @finished_send_qty, @finished_spare_qty, @my_qty, @my_spare_qty
			end
			close cur_tmp
			deallocate cur_tmp
			if @success = 0 begin
				select @msg = '以下解批数量超出订单送货数量' + char(13) + '订单单号   已备数量  已送数量  单据数量  已备备品  已送备品  单据备品' + char(13) + @msg
				goto endhandle
			end
		end
		if exists(select * from SYSSS where SEND_PRODUCE_FITOUT_TAG=1) begin
			--检查工单是否出完
			declare cur_tmp cursor for
				select od.PRODUCE_TYPE, od.PRODUCE_NO, od.FINISHED_FITOUT_QTY, od.FINISHED_FITOUT_SPARE_QTY, od.FINISHED_SEND_QTY, od.FINISHED_SEND_SPARE_QTY, sd.QTY, sd.SPARE_QTY 
					from MOC_PRODUCE_M od, (select PRODUCE_TYPE, PRODUCE_NO, sum(QTY) QTY,  sum(SPARE_QTY) SPARE_QTY from COP_SEND_D where SEND_TYPE=@send_type and SEND_NO=@send_no  group by PRODUCE_TYPE, PRODUCE_NO) sd 
					where od.PRODUCE_TYPE=sd.PRODUCE_TYPE and od.PRODUCE_NO=sd.PRODUCE_NO and (od.FINISHED_SEND_QTY < sd.QTY  or od.FINISHED_SEND_SPARE_QTY < sd.SPARE_QTY)
			open cur_tmp
			fetch next from cur_tmp into @produce_type, @produce_no, @qty, @spare_qty, @finished_send_qty, @finished_spare_qty, @my_qty, @my_spare_qty
			select @msg = ''
			while @@FETCH_STATUS = 0  begin
				select @success = 0, @msg = @msg + rtrim(@produce_no) + '    ' + cast(@qty as varchar) + '    ' + cast(@finished_send_qty as varchar) + '    '  + cast(@my_qty as varchar) + '    '  + cast(@spare_qty as varchar) +  '    '  + cast(@finished_spare_qty as varchar) + '    '  + cast(@my_spare_qty as varchar) + '    '  + char(13)
				fetch next from cur_tmp into @produce_type, @produce_no, @qty, @spare_qty, @finished_send_qty, @finished_spare_qty, @my_qty, @my_spare_qty
			end
			close cur_tmp
			deallocate cur_tmp
			if @success = 0 begin
				select @msg = '以下解批数量超出工单送货数量' + char(13) + '工单单号   已备数量  已送数量  单据数量  已备备品  已送货备品  单据备品' + char(13) + @msg
				goto endhandle
			end
		end
		if exists(select * from SYSSS where SEND_FITOUT_TAG=1) begin
			--检查备货单是否出完
			declare cur_tmp cursor for
				select od.FITOUT_TYPE, od.FITOUT_NO, od.QTY, od.SPARE_QTY, od.FINISHED_QTY, od.FINISHED_SPARE_QTY, od.RETURN_QTY, od.RETURN_SPARE_QTY, sd.QTY, sd.SPARE_QTY 
					from COP_FITOUT_D od, (select FITOUT_TYPE, FITOUT_NO, FITOUT_SERIAL_NO, sum(QTY) QTY,  sum(SPARE_QTY) SPARE_QTY from COP_SEND_D where SEND_TYPE=@send_type and SEND_NO=@send_no  group by FITOUT_TYPE, FITOUT_NO, FITOUT_SERIAL_NO) sd
					where od.FITOUT_TYPE=sd.FITOUT_TYPE and od.FITOUT_NO=sd.FITOUT_NO and od.SERIAL_NO=sd.FITOUT_SERIAL_NO and (od.FINISHED_QTY + od.RETURN_QTY < sd.QTY or od.FINISHED_SPARE_QTY + od.RETURN_SPARE_QTY < sd.SPARE_QTY )
			open cur_tmp
			fetch next from cur_tmp into @fitout_type, @fitout_no, @qty, @spare_qty, @finished_send_qty, @finished_spare_qty, @fitout_return_qty, @fitout_return_spare_qty, @my_qty, @my_spare_qty
			select @msg = ''
			while @@FETCH_STATUS = 0  begin
				select @success = 0, @msg = @msg + rtrim(@fitout_no) + '    ' + cast(@qty as varchar) + '    ' + cast(@fitout_return_qty as varchar) + '    ' + cast(@finished_send_qty as varchar) + '    '  + cast(@my_qty as varchar) + '    '  + cast(@spare_qty as varchar) +  '    '  + cast(@fitout_return_spare_qty as varchar) + '    ' + cast(@finished_spare_qty as varchar) + '    '  + cast(@my_spare_qty as varchar) + '    '  + char(13)
				fetch next from cur_tmp into @fitout_type, @fitout_no, @qty, @spare_qty, @finished_send_qty, @finished_spare_qty, @fitout_return_qty, @fitout_return_spare_qty, @my_qty, @my_spare_qty
			end
			close cur_tmp
			deallocate cur_tmp
			if @success = 0 begin
				select @msg = '以下解批数量超出备货单已送数量' + char(13) + '备货单号   数量  返仓数量  已送货数量  单据数量  备品  返仓备品  已送货备品  单据备品' + char(13) + @msg
				goto endhandle
			end
		end
		--更新订单送货数量
		update COP_ORDER_D set FINISHED_SEND_QTY = FINISHED_SEND_QTY - sd.QTY, FINISHED_SPARE_QTY = FINISHED_SPARE_QTY - sd.SPARE_QTY
			from (select ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO, sum(QTY) QTY,  sum(SPARE_QTY) SPARE_QTY from COP_SEND_D where SEND_TYPE=@send_type and SEND_NO=@send_no  group by ORDER_TYPE, ORDER_NO, ORDER_SERIAL_NO) sd 
			where COP_ORDER_D.ORDER_TYPE=sd.ORDER_TYPE and COP_ORDER_D.ORDER_NO=sd.ORDER_NO and COP_ORDER_D.SERIAL_NO=sd.ORDER_SERIAL_NO
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
		--更新送货排程送货数量、实交日期
		update COP_SHIPMENT_D set FINISHED_QTY = isnull(FINISHED_QTY,0) - isnull(sd.QTY,0) 
			from (select m.SEND_DATE, d.SEND_TYPE, d.SEND_NO, d.SHIPMENT_TYPE, d.SHIPMENT_NO, d.SHIPMENT_SERIAL_NO, sum(d.QTY) QTY from COP_SEND_D d,(select SEND_TYPE, SEND_NO, SEND_DATE from COP_SEND_M where SEND_TYPE=@send_type and SEND_NO=@send_no) m where m.SEND_TYPE=d.SEND_TYPE and m.SEND_NO=d.SEND_NO group by m.SEND_DATE, d.SEND_TYPE, d.SEND_NO, d.SHIPMENT_TYPE, d.SHIPMENT_NO, d.SHIPMENT_SERIAL_NO) sd 
			where COP_SHIPMENT_D.SHIPMENT_TYPE=sd.SHIPMENT_TYPE and COP_SHIPMENT_D.SHIPMENT_NO=sd.SHIPMENT_NO and COP_SHIPMENT_D.SERIAL_NO=sd.SHIPMENT_SERIAL_NO
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
		
	end
	if exists(select * from SYSSS where SEND_PRODUCE_TAG=1) begin
		--更新工单送货数量
		update MOC_PRODUCE_M set FINISHED_SEND_QTY = FINISHED_SEND_QTY + sd.QTY * @approve_tag, FINISHED_SEND_SPARE_QTY = FINISHED_SEND_SPARE_QTY + sd.SPARE_QTY * @approve_tag, FINISHED_SEND_DATE =sd.FINISHED_SEND_DATE ,FIRST_SEND_DATE = case when isnull(MOC_PRODUCE_M.FIRST_SEND_DATE, '2040-01-01')>sd.FIRST_SEND_DATE then sd.FIRST_SEND_DATE else MOC_PRODUCE_M.FIRST_SEND_DATE end 
			from (select PRODUCE_TYPE, PRODUCE_NO, sum(QTY) QTY,  sum(SPARE_QTY) SPARE_QTY, max(m.CREATE_DATE) FINISHED_SEND_DATE, min(m.CREATE_DATE) FIRST_SEND_DATE from COP_SEND_M m,COP_SEND_D d where m.SEND_TYPE=d.SEND_TYPE and m.SEND_NO=d.SEND_NO and m.SEND_TYPE=@send_type and m.SEND_NO=@send_no  group by PRODUCE_TYPE, PRODUCE_NO) sd 
			where MOC_PRODUCE_M.PRODUCE_TYPE=sd.PRODUCE_TYPE and MOC_PRODUCE_M.PRODUCE_NO=sd.PRODUCE_NO 
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end
	if exists(select * from SYSSS where SEND_FITOUT_TAG=1) begin
		--更新备货单
		update COP_FITOUT_D set COP_FITOUT_D.FINISHED_QTY = COP_FITOUT_D.FINISHED_QTY + sd.QTY * @approve_tag, COP_FITOUT_D.FINISHED_SPARE_QTY = COP_FITOUT_D.FINISHED_SPARE_QTY + sd.SPARE_QTY * @approve_tag
			from (select FITOUT_TYPE, FITOUT_NO, FITOUT_SERIAL_NO, sum(QTY) QTY,  sum(SPARE_QTY) SPARE_QTY from COP_SEND_D where SEND_TYPE=@send_type and SEND_NO=@send_no  group by FITOUT_TYPE, FITOUT_NO, FITOUT_SERIAL_NO) sd
			where sd.FITOUT_TYPE = COP_FITOUT_D.FITOUT_TYPE and sd.FITOUT_NO = COP_FITOUT_D.FITOUT_NO and sd.FITOUT_SERIAL_NO = COP_FITOUT_D.SERIAL_NO
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end
	--更新订单结案标志
	select @sql =  'select distinct ORDER_TYPE, ORDER_NO from COP_SEND_D where ' + @key_value 
	exec P_COP_ORDER_FINISHED  @sql, @approve_tag, 1, @success output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end
	--更新排程结案标志
	select @sql =  'select distinct SHIPMENT_TYPE, SHIPMENT_NO from COP_SEND_D where ' + @key_value 
	exec P_COP_SHIPMENT_FINISHED  @sql, @approve_tag, 1, @success output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	--更新备货结案标志
	select @sql =  'select distinct FITOUT_TYPE, FITOUT_NO from COP_SEND_D where ' + @key_value 
	exec P_COP_FITOUT_FINISHED  @sql, @approve_tag, 1, @success output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	if exists(select * from SYSSS where SEND_TAG=1) begin
		if @success = 1 begin
			--更新库存、批号、日志
			select @sql =  'select m.SEND_TYPE, m.SEND_NO, m.SEND_DATE, d.SERIAL_NO, d.PRO_NO, d.DEPOT_ID, ISNULL(d.QTY,0) + ISNULL(d.SPARE_QTY,0), d.UNIT_ID, ISNULL(d.PRICE,0), d.CURR_ID, d.CURR_RATE, d.AMOUNT, d.BATCH_NO from (select * from COP_SEND_M where ' + @key_value + ') m, COP_SEND_D d  where m.SEND_TYPE=d.SEND_TYPE and m.SEND_NO=d.SEND_NO'
			exec @success = P_UPDATE_PRO_DEPOT @sql, -1, @approve_tag, 0, @msg output
		end
	end

	if exists(select * from SYSSS where PRO_MRP=1) and @success = 1 begin
		--更新产品预计送货
		update PRODUCT set NOT_SEND_QTY=isnull(PRODUCT.NOT_SEND_QTY,0)-d.QTY*@approve_tag 
			from (select PRO_NO, sum(QTY+SPARE_QTY) QTY from COP_SEND_D where SEND_TYPE=@send_type and SEND_NO=@send_no and ORDER_NO>'' group by PRO_NO) d
			where PRODUCT.PRO_NO=d.PRO_NO
		if @@ERROR <> 0 begin
			select @success = 0, @msg='更新产品预计送货失败'
			goto endhandle
		end
	end

endhandle:
	return  @success
GO

-- P_WF_PRODUCT
CREATE PROCEDURE [dbo].[P_WF_PRODUCT]
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000), @pro_m_idx int
	declare @pro_no nvarchar(30), @new_no nvarchar(30), @new_edition nvarchar(10), @old_edition nvarchar(10)
	select @success = 1
	declare @pro_key_value varchar(200), @pro_key_value_desc varchar(300)
	select top 1 @pro_m_idx = M_IDX from MODULES where M_ALIAS='Product'

	select @sql = N'select @pro_no=PRO_NO, @old_edition=ltrim(rtrim(isnull(EDITION,''''))) from PRODUCT where ' + @key_value
	exec sp_executesql @sql,N'@pro_no nchar(30) output, @old_edition nchar(10) output',@pro_no output, @old_edition output

	select @pro_no = ltrim(rtrim(@pro_no))
	--批核
	if @approve_tag = 1 begin 
		--产生新版次号
		if exists(select * from SYSSS where PRO_EDITION_TAG=1) begin
			if @old_edition = ''
				select @new_edition='01'
			else begin
				select @new_edition = cast(cast(@old_edition as int) + 1 as varchar)
				if len(@new_edition)=1
					select @new_edition = '0' + @new_edition
			end
			update PRODUCT set EDITION=@new_edition where PRO_NO=@pro_no
			--更新样品版次基本资料
			--update SAMPLE_PRO set EDITION=@new_edition where PRO_NO=@pro_no
			--插入分版次保存的表
			select @sql = dbo.f_get_same_fieldlist('PRODUCT','PRODUCT_EDITION')
			exec('insert into PRODUCT_EDITION(' + @sql + ') select ' + @sql + ' from PRODUCT where ' + @key_value)
		end
	end

	return  @success
GO

-- P_WF_PUR_APPLY
CREATE PROCEDURE [dbo].[P_WF_PUR_APPLY]
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000), @produce_no nvarchar(50)
	declare @apply_type nchar(10), @apply_no nchar(20), @serial_no int, @qty float, @my_qty float, @finished_qty float,  @spare_qty float, @my_spare_qty float, @finished_spare_qty float
	select @success = 1

	select @sql = N'select @apply_type = APPLY_TYPE, @apply_no = APPLY_NO from PUR_APPLY_M where ' + @key_value
	exec sp_executesql @sql,N'@apply_type nchar(10) output, @apply_no nchar(20) output', @apply_type output, @apply_no output

	if @approve_tag = 1 begin --审核
		--检查数量<制令需求数量
		declare cur_tmp cursor for
			select i.PRODUCE_NO, i.QTY, o.NEED_QTY, o.APPLY_QTY 
				from (select PRODUCE_TYPE, PRODUCE_NO, PRODUCE_SERIAL_NO,sum(QTY) QTY from PUR_APPLY_MORE where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no group by PRODUCE_TYPE, PRODUCE_NO, PRODUCE_SERIAL_NO) i, MOC_PRODUCE_D o
				where  i.PRODUCE_TYPE=o.PRODUCE_TYPE and i.PRODUCE_NO=o.PRODUCE_NO and i.PRODUCE_SERIAL_NO=o.SERIAL_NO and i.QTY>o.NEED_QTY-o.APPLY_QTY
		open cur_tmp
		fetch next from cur_tmp into @produce_no, @my_qty, @qty, @finished_qty
		select @msg = ''
		while @@FETCH_STATUS = 0  begin
			select @success = 0, @msg = @msg + @produce_no + '    ' + cast(@qty as varchar) + '    ' + cast(@finished_qty as varchar) + '    ' + cast(@my_qty as varchar) + char(13)
			fetch next from cur_tmp into @produce_no, @my_qty, @qty, @finished_qty
		end
		close cur_tmp
		deallocate cur_tmp
		if @success = 0 begin
			select @msg = '以下明细项申购数量超出生产单数量' + char(13) + '序号  需求数量  已申请数量  单据数量' + char(13) + @msg
			goto endhandle
		end
	end

	--更新制令已请购数量
	update MOC_PRODUCE_D set MOC_PRODUCE_D.APPLY_QTY = MOC_PRODUCE_D.APPLY_QTY + d.QTY * @approve_tag
		from (select PRODUCE_TYPE, PRODUCE_NO, PRODUCE_SERIAL_NO,sum(QTY) QTY from PUR_APPLY_MORE where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no group by PRODUCE_TYPE, PRODUCE_NO, PRODUCE_SERIAL_NO) d
		where MOC_PRODUCE_D.PRODUCE_TYPE=d.PRODUCE_TYPE and MOC_PRODUCE_D.PRODUCE_NO=d.PRODUCE_NO and MOC_PRODUCE_D.SERIAL_NO=d.PRODUCE_SERIAL_NO
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end
	--更新生产计划已请购数量
	update MOC_PLAN_PUR set APPLY_QTY = MOC_PLAN_PUR.APPLY_QTY + d.QTY * @approve_tag, APPLY_TYPE=case when @approve_tag=1 then d.APPLY_TYPE else MOC_PLAN_PUR.APPLY_TYPE end, APPLY_NO=case when @approve_tag=1 then d.APPLY_NO else MOC_PLAN_PUR.APPLY_NO end
		from (select APPLY_TYPE, APPLY_NO, PLAN_TYPE, PLAN_NO, PLAN_SERIAL_NO,sum(QTY) QTY from PUR_APPLY_D where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no group by APPLY_TYPE, APPLY_NO, PLAN_TYPE, PLAN_NO, PLAN_SERIAL_NO) d
		where MOC_PLAN_PUR.PLAN_TYPE=d.PLAN_TYPE and MOC_PLAN_PUR.PLAN_NO=d.PLAN_NO and MOC_PLAN_PUR.SERIAL_NO=d.PLAN_SERIAL_NO
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end
/*-
	if exists(select * from SYSSS where PRO_MRP=1) begin
		--更新产品在途数量
		update PRODUCT set IN_BUY_QTY=isnull(PRODUCT.IN_BUY_QTY,0)+d.QTY*@approve_tag, MRP_QTY=isnull(PRODUCT.MRP_QTY,0)+d.QTY*@approve_tag
			from (select PRO_NO, sum(QTY) QTY from PUR_APPLY_D where APPLY_TYPE=@apply_type and APPLY_NO=@apply_no group by PRO_NO) d
			where PRODUCT.PRO_NO=d.PRO_NO
		if @@ERROR <> 0 begin
			select @success = 0, @msg='更新产品在途数量失败'
			goto endhandle
		end
	end
*/
	--更新订单是否已下请购单
	if @approve_tag = 1 begin --审核
		update COP_ORDER_D set COP_ORDER_D.APPLY_TYPE= d.APPLY_TYPE,COP_ORDER_D.APPLY_NO= d.APPLY_NO,COP_ORDER_D.APPLY_SERIAL_NO= d.SERIAL_NO from PUR_APPLY_D d
			where COP_ORDER_D.ORDER_TYPE=d.ORDER_TYPE and COP_ORDER_D.ORDER_NO=d.ORDER_NO and COP_ORDER_D.SERIAL_NO=d.ORDER_SERIAL_NO and d.APPLY_TYPE=@apply_type and d.APPLY_NO=@apply_no
	end
	else begin
		update COP_ORDER_D set COP_ORDER_D.APPLY_TYPE= '',COP_ORDER_D.APPLY_NO= '',COP_ORDER_D.APPLY_SERIAL_NO= 0 from PUR_APPLY_D d
			where COP_ORDER_D.ORDER_TYPE=d.ORDER_TYPE and COP_ORDER_D.ORDER_NO=d.ORDER_NO and COP_ORDER_D.SERIAL_NO=d.ORDER_SERIAL_NO and d.APPLY_TYPE=@apply_type and d.APPLY_NO=@apply_no
	end

	update COP_ORDER_MORE set APPLY_QTY = isnull(APPLY_QTY,0) + isnull(sd.QTY,0) * @approve_tag 
		from (select d.ORDER_TYPE, d.ORDER_NO,d.PRO_NO,sum(d.QTY) QTY from PUR_APPLY_D d  where d.APPLY_TYPE=@apply_type and d.APPLY_NO=@apply_no  GROUP BY d.ORDER_TYPE, d.ORDER_NO,d.PRO_NO) sd 
		where COP_ORDER_MORE.ORDER_TYPE=sd.ORDER_TYPE and COP_ORDER_MORE.ORDER_NO=sd.ORDER_NO and COP_ORDER_MORE.PRO_NO=sd.PRO_NO 

	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

endhandle:
	return  @success
GO

-- P_WF_PUR_DUE
CREATE PROCEDURE P_WF_PUR_DUE
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
	declare @due_type nchar(10), @due_no nchar(20)
	select @success = 1

	select @sql = N'select @due_type = DUE_TYPE, @due_no =  DUE_NO from PUR_DUE_M where ' + @key_value
	exec sp_executesql @sql,N'@due_type nchar(10) output, @due_no nchar(20) output', @due_type output, @due_no output

	if @approve_tag = 1 begin --审核
		--检查
		exec P_PUR_DUE_CHECK @due_type, @due_no, @success output, @msg output
		if @@ERROR<>0 or @success = 0 begin
			select @success=0
			goto endhandle
		end

		if @success = 1 begin
			--更新送、退货单对帐数量、金额
			update PUR_RECEIVE_D set PUR_RECEIVE_D.FINISHED_QTY = PUR_RECEIVE_D.FINISHED_QTY + PUR_DUE_D.QTY,  PUR_RECEIVE_D.FINISHED_AMOUNT = PUR_RECEIVE_D.FINISHED_AMOUNT + PUR_DUE_D.AMOUNT_TAX
				from PUR_DUE_D
				where PUR_DUE_D.DUE_TYPE=@due_type and PUR_DUE_D.DUE_NO=@due_no and PUR_RECEIVE_D.RECEIVE_TYPE=PUR_DUE_D.R_C_TYPE and PUR_RECEIVE_D.RECEIVE_NO=PUR_DUE_D.R_C_NO and PUR_RECEIVE_D.SERIAL_NO=PUR_DUE_D.R_C_SERIAL_NO
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
			--退货为负数，故用减法PUR_CANCEL_D.FINISHED_QTY - PUR_DUE_D.QTY,
			update PUR_CANCEL_D set PUR_CANCEL_D.FINISHED_QTY = PUR_CANCEL_D.FINISHED_QTY - PUR_DUE_D.QTY,  PUR_CANCEL_D.FINISHED_AMOUNT = PUR_CANCEL_D.FINISHED_AMOUNT - PUR_DUE_D.AMOUNT_TAX
				from PUR_DUE_D
				where PUR_DUE_D.DUE_TYPE=@due_type and PUR_DUE_D.DUE_NO=@due_no and PUR_CANCEL_D.CANCEL_TYPE=PUR_DUE_D.R_C_TYPE and PUR_CANCEL_D.CANCEL_NO=PUR_DUE_D.R_C_NO and PUR_CANCEL_D.SERIAL_NO=PUR_DUE_D.R_C_SERIAL_NO
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
			--依据对帐单上所属月份（如为空从对帐日期取年-月-）的下个月第一天加上厂商资料中月结天数（为空则表示现金）
			declare @cmonth char(8),@nadd int
			select @cmonth = case when isnull(m.due_month,'')='' then convert(char(8),m.due_date,20) else left(m.due_month,4)+'-'+right(m.due_month,2)+'-' end,@nadd=isnull(d.PAYMENT_DAY,0) from PUR_DUE_M m,supplier d where m.DUE_TYPE=@due_type and  m.DUE_NO=@due_no and m.SUPPLIER_ID=d.SUPPLIER_ID
			update PUR_DUE_M set PRE_PAYOUT_DATE= dateadd(day,@nadd,dateadd(mm,1,cast(@cmonth+'01' as datetime))) where PUR_DUE_M.DUE_TYPE=@due_type and PUR_DUE_M.DUE_NO=@due_no
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
		end
	end
	else begin	--解批
		if @success = 1 begin
			--更新送、退货单对帐数量、金额
			update PUR_RECEIVE_D set PUR_RECEIVE_D.FINISHED_QTY = PUR_RECEIVE_D.FINISHED_QTY - PUR_DUE_D.QTY,  PUR_RECEIVE_D.FINISHED_AMOUNT = PUR_RECEIVE_D.FINISHED_AMOUNT - PUR_DUE_D.AMOUNT_TAX
				from PUR_DUE_D
				where PUR_DUE_D.DUE_TYPE=@due_type and PUR_DUE_D.DUE_NO=@due_no and PUR_RECEIVE_D.RECEIVE_TYPE=PUR_DUE_D.R_C_TYPE and PUR_RECEIVE_D.RECEIVE_NO=PUR_DUE_D.R_C_NO and PUR_RECEIVE_D.SERIAL_NO=PUR_DUE_D.R_C_SERIAL_NO
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
			update PUR_CANCEL_D set PUR_CANCEL_D.FINISHED_QTY = PUR_CANCEL_D.FINISHED_QTY + PUR_DUE_D.QTY,  PUR_CANCEL_D.FINISHED_AMOUNT = PUR_CANCEL_D.FINISHED_AMOUNT + PUR_DUE_D.AMOUNT_TAX
				from PUR_DUE_D
				where PUR_DUE_D.DUE_TYPE=@due_type and PUR_DUE_D.DUE_NO=@due_no and PUR_CANCEL_D.CANCEL_TYPE=PUR_DUE_D.R_C_TYPE and PUR_CANCEL_D.CANCEL_NO=PUR_DUE_D.R_C_NO and PUR_CANCEL_D.SERIAL_NO=PUR_DUE_D.R_C_SERIAL_NO
			if @@ERROR <> 0 begin
				select @success = 0
				goto endhandle
			end
		end
	end

	if @success = 1 begin
		--更新送货单结案标志
		select @sql =  'select distinct R_C_TYPE, R_C_NO from PUR_DUE_D where ' + @key_value 
		exec P_PUR_RECEIVE_FINISHED  @sql, @approve_tag, 1, @success output
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end

	if @success = 1 begin
		--更新退货单结案标志
		select @sql =  'select distinct R_C_TYPE, R_C_NO from PUR_DUE_D where ' + @key_value 
		exec P_PUR_CANCEL_FINISHED  @sql, @approve_tag, 1, @success output
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end

	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

endhandle:
	return  @success
GO

-- P_WF_PUR_PAY
CREATE PROCEDURE P_WF_PUR_PAY
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
	declare @pay_type nchar(10), @pay_no nchar(20), @supplier_id nchar(10), @payout_sum float, @curr_rate float,@prepay_sum float
	declare @supp_curr_rate float, @bank_amount float, @bank_rate float
	select @success=1

	select @sql = 'select @pay_type=PAY_TYPE, @pay_no=PAY_NO, @supplier_id = SUPPLIER_ID, @payout_sum=PAYOUT_SUM, @curr_rate=CURR_RATE, @prepay_sum=PREPAY_SUM from PUR_PAY_M where ' + @key_value
	exec sp_executesql @sql, N'@pay_type nchar(10) output, @pay_no nchar(20) output, @supplier_id nchar(10) output, @payout_sum float output, @curr_rate float output, @prepay_sum float output', @pay_type output, @pay_no output, @supplier_id output, @payout_sum output, @curr_rate output, @prepay_sum output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	if @approve_tag = 1 begin --审核
		if @success = 1 begin
			--检查
			exec P_PUR_PAY_CHECK @pay_type, @pay_no, @success output, @msg output
			if @@ERROR<>0 or @success = 0 begin
				select @success=0
				goto endhandle
			end
		end
	end

	select @supp_curr_rate=c.CURR_RATE from SUPPLIER s, CURR c where s.CURR_ID=c.CURR_ID and s.SUPPLIER_ID=@supplier_id
	if isnull(@supp_curr_rate, 0) = 0
		select @supp_curr_rate = 1

	select @bank_amount=b.AMOUNT, @bank_rate=c.CURR_RATE
		from BANK b left join CURR c on b.CURR_ID=c.CURR_ID 
		where b.BANK_ID in(select BANK_ID from PUR_PAY_M where PAY_TYPE=@pay_type and PAY_NO=@pay_no)
	if isnull(@bank_rate, 0) = 0
		select @bank_rate = 1

	--银行金额是否够扣
	if @bank_amount * @bank_rate - @payout_sum * @curr_rate * @approve_tag<0 begin
		select @success = 0, @msg='银行结存不够'
		goto endhandle
	end


	--更新应付帐款
	update PUR_DUE_M set PUR_DUE_M.PAYOUT_AMOUNT=PUR_DUE_M.PAYOUT_AMOUNT+PUR_PAY_D.PAYOUT_AMOUNT*@approve_tag
		from PUR_PAY_D
		where PUR_PAY_D.PAY_TYPE=@pay_type and PUR_PAY_D.PAY_NO=@pay_no and PUR_PAY_D.DUE_TYPE=PUR_DUE_M.DUE_TYPE and PUR_PAY_D.DUE_NO=PUR_DUE_M.DUE_NO
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	--预付冲帐
	update PUR_PREPAY_M set PUR_PREPAY_M.PREPAY_AMOUNT=PUR_PREPAY_M.PREPAY_AMOUNT+d.PREPAY_AMOUNT*@approve_tag
		from PUR_PAY_M m, PUR_PAY_PREPAY d
		where m.PAY_TYPE=@pay_type and m.PAY_NO=@pay_no and m.PAY_TYPE=d.PAY_TYPE and m.PAY_NO=d.PAY_NO
			and PUR_PREPAY_M.PREPAY_TYPE=d.PREPAY_TYPE and PUR_PREPAY_M.PREPAY_NO=d.PREPAY_NO
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	--更新厂商信用余额
	update SUPPLIER set CREDIT_LIMIT_NUM=round((CREDIT_LIMIT_NUM*@supp_curr_rate + @payout_sum*@curr_rate * @approve_tag)/@supp_curr_rate, 2),PREPAY_SUM=round((PREPAY_SUM*@supp_curr_rate - @prepay_sum*@curr_rate*@approve_tag)/@supp_curr_rate, 2)
		where SUPPLIER_ID=@supplier_id
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	--更新银行结存
	update BANK set AMOUNT=round((BANK.AMOUNT * @bank_rate - @payout_sum * @curr_rate * @approve_tag)/@bank_rate, 2)
		from PUR_PAY_M
		where PUR_PAY_M.PAY_TYPE=@pay_type and PUR_PAY_M.PAY_NO=@pay_no and BANK.BANK_ID=PUR_PAY_M.BANK_ID
	if @@ERROR<>0 begin
		select @success = 0
		goto endhandle
	end


	--更新对帐单结案标志
	select @sql =  'select distinct DUE_TYPE, DUE_NO from PUR_PAY_D where ' + @key_value 
	exec P_PUR_DUE_FINISHED  @sql, @approve_tag, 1, @success output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	--更新预付帐款结案标志
	select @sql =  'select distinct PREPAY_TYPE, PREPAY_NO from PUR_PAY_PREPAY where ' + @key_value 
	exec P_PUR_PREPAY_FINISHED  @sql, @approve_tag, 1, @success output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end
	select @success=1

endhandle:
	return  @success
GO

-- P_WF_PUR_PREPAY
CREATE PROCEDURE P_WF_PUR_PREPAY
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
    	declare @prepay_type nchar(10), @prepay_no nchar(20)
	declare @supp_curr_rate float, @supplier_id nchar(10), @amount_tax float, @curr_rate float, @bank_amount float, @bank_rate float

	select @success=1
	select @sql = 'select @prepay_type=PREPAY_TYPE, @prepay_no=PREPAY_NO, @supplier_id=SUPPLIER_ID, @curr_rate=CURR_RATE, @amount_tax=AMOUNT from PUR_PREPAY_M where ' + @key_value
    	exec sp_executesql @sql, N'@prepay_type nchar(10) output, @prepay_no nchar(20) output, @supplier_id nchar(10) output, @amount_tax float output, @curr_rate float output', @prepay_type output, @prepay_no output, @supplier_id output, @amount_tax output, @curr_rate output

	if @approve_tag = 1 begin --审核
		if @success = 1 begin
			--检查
			exec P_PUR_PREPAY_CHECK @prepay_type, @prepay_no, @success output, @msg output
			if @@ERROR<>0 or @success = 0 begin
				select @success=0
				goto errhandle
			end
		end
	end

	select @supp_curr_rate=c.CURR_RATE from SUPPLIER s, CURR c where s.CURR_ID=c.CURR_ID and s.SUPPLIER_ID=@supplier_id
	if isnull(@supp_curr_rate, 0) = 0
		select @supp_curr_rate = 1

	select @bank_amount=b.AMOUNT, @bank_rate=c.CURR_RATE
		from BANK b left join CURR c on b.CURR_ID=c.CURR_ID 
		where b.BANK_ID in(select BANK_ID from PUR_PREPAY_M where PUR_PREPAY_M.PREPAY_TYPE=@prepay_type and PUR_PREPAY_M.PREPAY_NO=@prepay_no)
	if isnull(@bank_rate, 0) = 0
		select @bank_rate = 1


	--银行金额是否够扣
	if @bank_amount * @bank_rate - @amount_tax * @curr_rate * @approve_tag<0 begin
		select @success = 0, @msg='银行结存不够'
		goto errhandle
	end


	--更新厂商信用余额，厂商预付款增加
	update SUPPLIER set CREDIT_LIMIT_NUM=round((CREDIT_LIMIT_NUM*@supp_curr_rate + @amount_tax*@curr_rate*@approve_tag)/@supp_curr_rate, 2),PREPAY_SUM=round((PREPAY_SUM*@supp_curr_rate + @amount_tax*@curr_rate*@approve_tag)/@supp_curr_rate, 2)
		where SUPPLIER_ID=@supplier_id
	if @@ERROR <> 0 begin
		select @success = 0
		goto errhandle
	end

	--更新银行结存
	update BANK set AMOUNT=round((BANK.AMOUNT * @bank_rate - @amount_tax * @curr_rate * @approve_tag)/@bank_rate, 2)
		from PUR_PREPAY_M
		where PUR_PREPAY_M.PREPAY_TYPE=@prepay_type and PUR_PREPAY_M.PREPAY_NO=@prepay_no and BANK.BANK_ID=PUR_PREPAY_M.BANK_ID
	if @@ERROR<>0 begin
		select @success = 0
		goto errhandle
	end
	--更新采购单预付金额
	update PUR_PURCHASE_D set FINISHED_AMOUNT = FINISHED_AMOUNT + sd.AMOUNT* @approve_tag 
		from (select PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO, sum(AMOUNT) AMOUNT  from PUR_PREPAY_D where PREPAY_TYPE=@prepay_type and PREPAY_NO=@prepay_no  group by PURCHASE_TYPE, PURCHASE_NO, PURCHASE_SERIAL_NO) sd 
		where PUR_PURCHASE_D.PURCHASE_TYPE=sd.PURCHASE_TYPE and PUR_PURCHASE_D.PURCHASE_NO=sd.PURCHASE_NO and PUR_PURCHASE_D.SERIAL_NO=sd.PURCHASE_SERIAL_NO
	if @@ERROR <> 0 begin
		select @success = 0
		goto errhandle
	end

errhandle:
	return  @success
GO

-- P_WF_PUR_PURCHASE
CREATE PROCEDURE P_WF_PUR_PURCHASE
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
	declare @serial_no int, @qty float, @finished_qty float, @my_qty float, @spare_qty float, @finished_spare_qty float , @my_spare_qty float, @pro_no nchar(30) 
	declare @purchase_type nchar(10), @purchase_no nchar(20), @purchase_date datetime, @supplier_id nchar(10), @supp_curr_id nchar(10), @supp_curr_rate float, @last_trade_date datetime, @supplier_days int, @amount_tax float, @curr_id nchar(10), @curr_rate float, @credit_limit_num float
	declare @credit_days int, @min_buy_amount float
	select @success = 1

	select @sql = N'select @purchase_type = PURCHASE_TYPE, @purchase_no = PURCHASE_NO, @purchase_date=PURCHASE_DATE, @supplier_id=SUPPLIER_ID, @amount_tax=AMOUNT_TAX, @curr_id=CURR_ID, @curr_rate=CURR_RATE from PUR_PURCHASE_M where ' + @key_value
	exec sp_executesql @sql,N'@purchase_type nchar(10) output, @purchase_no nchar(20) output, @purchase_date datetime output, @supplier_id nchar(10) output, @amount_tax float output, @curr_id nchar(10) output, @curr_rate float output', @purchase_type output, @purchase_no output, @purchase_date output, @supplier_id output, @amount_tax output, @curr_id output, @curr_rate output

	select @last_trade_date=s.LAST_TRADE_DATE, @credit_limit_num=s.CREDIT_LIMIT_NUM,  @credit_days=s.PAYMENT_DAY, @min_buy_amount=s.MIN_BUY_AMOUNT, @supp_curr_id=s.CURR_ID, @supp_curr_rate=c.CURR_RATE
		from SUPPLIER s left join CURR c on s.CURR_ID=c.CURR_ID
		where SUPPLIER_ID=@supplier_id
	if isnull(@supp_curr_rate,0)=0
		select @supp_curr_rate = 1

	if @approve_tag = 1 begin --审核
		--是否超出厂商交易天数
		select top 1 @supplier_days=SUPPLIER_DAYS from SYSSS
		if @supplier_days is not null and @last_trade_date is not null begin
			if @supplier_days < datediff(day, @last_trade_date, @purchase_date)
				select @success = 0, @msg = '已超过厂商交易天数'
			if @success = 0
				goto endhandle
		end

		--最低采购金额
		if isnull(@min_buy_amount, 0) > 0 begin
			if @min_buy_amount * @supp_curr_rate > @amount_tax*@curr_rate
				select @success = 0, @msg = '总金额小于厂商最低采购额:' + cast(@min_buy_amount as varchar)
			if @success = 0
				goto endhandle
		end

		--判断厂商信用余额是否够
		if @credit_limit_num*@supp_curr_rate < @amount_tax*@curr_rate
			select @success = 0, @msg = '厂商信用余额不足：' + str(round((@credit_limit_num*@supp_curr_rate - @amount_tax*@curr_rate)/@supp_curr_rate, 2)) + @supp_curr_id
		if @success = 0
			goto endhandle

		--是否超出请购单数量
		if exists(select * from SYSSS where PUR_APPLY_TAG=0) begin
			declare cur_tmp cursor for
				select a.SERIAL_NO, a.QTY, a.PURCHASE_QTY, p.QTY
					from PUR_APPLY_D a, PUR_PURCHASE_D p
					where p.PURCHASE_TYPE=@purchase_type and p.PURCHASE_NO=@purchase_no 
						and p.APPLY_TYPE=a.APPLY_TYPE and p.APPLY_NO=a.APPLY_NO and p.APPLY_SERIAL_NO=a.SERIAL_NO and (a.QTY < a.PURCHASE_QTY + p.QTY)
			open cur_tmp
			fetch next from cur_tmp into @serial_no, @qty, @finished_qty, @my_qty
			select @msg = ''
			while @@FETCH_STATUS = 0  begin
				select @success = 0, @msg = @msg + cast(@serial_no as varchar) + '    ' + cast(@qty as varchar) + '    ' + cast(@finished_qty as varchar) + '    '  + cast(@my_qty as varchar) + char(13)
				fetch next from cur_tmp into @serial_no, @qty, @finished_qty, @my_qty
			end
			close cur_tmp
			deallocate cur_tmp
			if @success = 0 begin
				select @msg = '以下采购单超出申购数量' + char(13) + '序号  申购数量   已采购数量   单据数量' + char(13) + @msg
				goto endhandle
			end
		end
		--产品计价期限------------------------
		declare cur_tmp cursor for
			select a.PRO_NO
				from PUR_PURCHASE_D a, SUPPLIER_PRICE_D p
				where a.PURCHASE_TYPE=@purchase_type and a.PURCHASE_NO=@purchase_no and p.SUPPLIER_ID=@supplier_id
					and a.PRO_NO=p.PRO_NO  and a.CURR_ID=p.CURR_ID  and a.TAX_ID=p.TAX_ID  and a.TAX_TYPE=p.TAX_TYPE  and p.IN_EFFECT_DATE<@purchase_date
		open cur_tmp
		fetch next from cur_tmp into @pro_no
		select @msg = ''
		while @@FETCH_STATUS = 0  begin
			select @success = 0, @msg = @msg + rtrim(@pro_no) + '    '
			fetch next from cur_tmp into @pro_no
		end
		close cur_tmp
		deallocate cur_tmp
		if @success = 0 begin
			select @success=0, @msg = '以下产品计价已过有效期' + char(13) + @msg
			goto endhandle
		end
		--更新申购单
		update PUR_APPLY_D set PUR_APPLY_D.PURCHASE_TYPE=@purchase_type, PUR_APPLY_D.PURCHASE_NO=@purchase_no, PUR_APPLY_D.PURCHASE_SERIAL_NO=p.SERIAL_NO,
			PUR_APPLY_D.PURCHASE_QTY=PUR_APPLY_D.PURCHASE_QTY+p.QTY, PUR_APPLY_D.FINISHED_TAG=(case when PUR_APPLY_D.QTY<=PUR_APPLY_D.PURCHASE_QTY+p.QTY then 1 else 0 end)
			from PUR_PURCHASE_D p 
			where p.PURCHASE_TYPE=@purchase_type and p.PURCHASE_NO=@purchase_no 
				and p.APPLY_TYPE=PUR_APPLY_D.APPLY_TYPE and p.APPLY_NO=PUR_APPLY_D.APPLY_NO and p.APPLY_SERIAL_NO=PUR_APPLY_D.SERIAL_NO
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
		--更新客户订单的从表备注，将采购预交日期、出现状况原因汇总。
		update COP_ORDER_D set PRE_LASTPLAN_DATE=d.PLAN_DELIVERY_DATE,REMARK = RTRIM(COP_ORDER_D.REMARK)+d.REMARK 
			from PUR_PURCHASE_D d 
			where COP_ORDER_D.ORDER_TYPE=d.ORDER_TYPE and COP_ORDER_D.ORDER_NO=d.ORDER_NO and COP_ORDER_D.SERIAL_NO=d.ORDER_SERIAL_NO and d.PURCHASE_TYPE=@purchase_type and d.PURCHASE_NO=@purchase_no
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end

	end
	else begin	--解批
		--更新申购单
		update PUR_APPLY_D set PUR_APPLY_D.PURCHASE_TYPE='', PUR_APPLY_D.PURCHASE_NO='', PUR_APPLY_D.PURCHASE_SERIAL_NO=0,
			PUR_APPLY_D.PURCHASE_QTY=PUR_APPLY_D.PURCHASE_QTY-p.QTY, PUR_APPLY_D.FINISHED_TAG=0
			from PUR_PURCHASE_D p 
			where p.PURCHASE_TYPE=@purchase_type and p.PURCHASE_NO=@purchase_no 
				and p.APPLY_TYPE=PUR_APPLY_D.APPLY_TYPE and p.APPLY_NO=PUR_APPLY_D.APPLY_NO and p.APPLY_SERIAL_NO=PUR_APPLY_D.SERIAL_NO
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end

	--更新信用余额
	update SUPPLIER set CREDIT_LIMIT_NUM=case when @supp_curr_id=@curr_id then round(CREDIT_LIMIT_NUM - @amount_tax * @approve_tag, 2) else round((CREDIT_LIMIT_NUM*@supp_curr_rate - @amount_tax * @curr_rate * @approve_tag)/@supp_curr_rate, 2) end
		where SUPPLIER_ID=@supplier_id
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	--更新制令已采购
	update MOC_PRODUCE_D set MOC_PRODUCE_D.PURCHASE_QTY = MOC_PRODUCE_D.PURCHASE_QTY + d.QTY * @approve_tag
		from PUR_PURCHASE_MORE d
		where MOC_PRODUCE_D.PRODUCE_TYPE=d.PRODUCE_TYPE and MOC_PRODUCE_D.PRODUCE_NO=d.PRODUCE_NO and MOC_PRODUCE_D.SERIAL_NO=d.PRODUCE_SERIAL_NO
			and d.PURCHASE_TYPE=@purchase_type and d.PURCHASE_NO=@purchase_no
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	update COP_ORDER_MORE set PURCHASE_QTY = isnull(PURCHASE_QTY,0) + isnull(sd.QTY,0) * @approve_tag 
		from (select d.ORDER_TYPE, d.ORDER_NO,d.PRO_NO,sum(d.QTY) QTY from PUR_PURCHASE_D d where d.PURCHASE_TYPE=@purchase_type and d.PURCHASE_NO=@purchase_no GROUP BY d.ORDER_TYPE, d.ORDER_NO,d.PRO_NO) sd 
		where COP_ORDER_MORE.ORDER_TYPE=sd.ORDER_TYPE and COP_ORDER_MORE.ORDER_NO=sd.ORDER_NO and COP_ORDER_MORE.PRO_NO=sd.PRO_NO 

	--更新可用库存
	--if exists(select * from SYSSS where PRO_MRP=1) and @success = 1 begin
	--	select @sql =  'select PRO_NO, '''', QTY + SPARE_QTY, UNIT_ID from PUR_PURCHASE_D where ' + @key_value 
	--	exec @success = P_UPDATE_PRO_MRP_QTY @sql, 1, @approve_tag, @msg output
	--	if @success = 0 begin
	--		select @success=0, @msg = '更新可用库存错误'
	--		goto endhandle
	--	end
	--end

	--更新结案标志
	select @sql =  'select distinct APPLY_TYPE, APPLY_NO from PUR_PURCHASE_D where ' + @key_value 
	exec P_PUR_APPLY_FINISHED  @sql, @approve_tag, 1, @success output
	if @@ERROR <> 0 begin
		select @success = 0
		goto endhandle
	end

	if exists(select * from SYSSS where PRO_MRP=1) begin
		--更新产品在途数量
		update PRODUCT set IN_BUY_QTY=isnull(PRODUCT.IN_BUY_QTY,0)+d.QTY*@approve_tag, MRP_QTY=isnull(PRODUCT.MRP_QTY,0)+d.QTY*@approve_tag
			from (select PRO_NO, sum(QTY) QTY from PUR_PURCHASE_D where PURCHASE_TYPE=@purchase_type and PURCHASE_NO=@purchase_no group by PRO_NO) d
			where PRODUCT.PRO_NO=d.PRO_NO
		if @@ERROR <> 0 begin
			select @success = 0, @msg='更新产品在途数量失败'
			goto endhandle
		end
	end

	
endhandle:
	return  @success
GO

-- P_WF_PUR_QUOTE
CREATE PROCEDURE P_WF_PUR_QUOTE
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
	declare @quote_type nchar(10), @quote_no nchar(20)

	select @sql = 'select @quote_type=QUOTE_TYPE, @quote_no=QUOTE_NO from PUR_QUOTE_M  where ' + @key_value
	exec sp_executesql @sql, N'@quote_type nchar(10) output, @quote_no nchar(20) output', @quote_type output, @quote_no output
	if @approve_tag = 1 begin
		--更新厂商计价数据
		insert into SUPPLIER_PRICE_M(SUPPLIER_ID, CREATE_PERSON, CREATE_DATE)
			select SUPPLIER_ID, 'SYSTEM', QUOTE_DATE
			from PUR_QUOTE_M
			where QUOTE_TYPE=@quote_type and QUOTE_NO=@quote_no and not exists (select * from SUPPLIER_PRICE_M where SUPPLIER_ID=PUR_QUOTE_M.SUPPLIER_ID)
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end

		update SUPPLIER_PRICE_D set SUPPLIER_PRICE_D.UNIT_ID=d.UNIT_ID, SUPPLIER_PRICE_D.CURR_ID=d.CURR_ID, SUPPLIER_PRICE_D.CURR_RATE=d.CURR_RATE, SUPPLIER_PRICE_D.TAX_ID=d.TAX_ID, SUPPLIER_PRICE_D.TAX_RATE=d.TAX_RATE, SUPPLIER_PRICE_D.TAX_TYPE=d.TAX_TYPE, SUPPLIER_PRICE_D.PRICE=d.PRICE, SUPPLIER_PRICE_D.PROCESS_PRICE=d.PROCESS_PRICE, SUPPLIER_PRICE_D.SUPPLIER_PRO_NO=d.SUPPLIER_PRO_NO,
				SUPPLIER_PRICE_D.REBATE=d.REBATE, SUPPLIER_PRICE_D.VERIFY_PRICE_DATE=m.QUOTE_DATE, SUPPLIER_PRICE_D.OLD_PRICE=SUPPLIER_PRICE_D.PRICE, SUPPLIER_PRICE_D.OLD_PRICE_DATE=SUPPLIER_PRICE_D.VERIFY_PRICE_DATE, 
				SUPPLIER_PRICE_D.QUOTE_TYPE=m.QUOTE_TYPE, SUPPLIER_PRICE_D.QUOTE_NO=m.QUOTE_NO, SUPPLIER_PRICE_D.QUOTE_SERIAL_NO=d.SERIAL_NO, SUPPLIER_PRICE_D.IN_EFFECT_DATE=m.IN_EFFECT_DATE, SUPPLIER_PRICE_D.REMARK=d.REMARK
			from PUR_QUOTE_M m, PUR_QUOTE_D d 
			where m.QUOTE_TYPE=d.QUOTE_TYPE and m.QUOTE_NO=d.QUOTE_NO and m.QUOTE_TYPE=@quote_type and m.QUOTE_NO=@quote_no and m.QUOTE_DATE>=SUPPLIER_PRICE_D.VERIFY_PRICE_DATE
				 and m.SUPPLIER_ID=SUPPLIER_PRICE_D.SUPPLIER_ID and d.PRO_NO=SUPPLIER_PRICE_D.PRO_NO and d.CURR_ID=SUPPLIER_PRICE_D.CURR_ID and d.TAX_ID=SUPPLIER_PRICE_D.TAX_ID and d.TAX_TYPE=SUPPLIER_PRICE_D.TAX_TYPE and d.UNIT_ID=SUPPLIER_PRICE_D.UNIT_ID and d.REBATE=SUPPLIER_PRICE_D.REBATE
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
		insert into SUPPLIER_PRICE_D(SUPPLIER_ID, PRO_NO, UNIT_ID, CURR_ID, CURR_RATE, TAX_ID, TAX_RATE, TAX_TYPE, PRICE, PROCESS_PRICE, SUPPLIER_PRO_NO, REBATE, VERIFY_PRICE_DATE, OLD_PRICE, OLD_PRICE_DATE, QUOTE_TYPE, QUOTE_NO, QUOTE_SERIAL_NO, IN_EFFECT_DATE, REMARK)
			select m.SUPPLIER_ID, d.PRO_NO, d.UNIT_ID, d.CURR_ID, d.CURR_RATE, d.TAX_ID, d.TAX_RATE, d.TAX_TYPE, d.PRICE, d.PROCESS_PRICE, d.SUPPLIER_PRO_NO, d.REBATE, m.QUOTE_DATE, null, null, d.QUOTE_TYPE, d.QUOTE_NO, d.SERIAL_NO, m.IN_EFFECT_DATE, d.REMARK
			from PUR_QUOTE_M m, PUR_QUOTE_D d 
			where m.QUOTE_TYPE=d.QUOTE_TYPE and m.QUOTE_NO=d.QUOTE_NO and m.QUOTE_TYPE=@quote_type and m.QUOTE_NO=@quote_no
				and not exists(select * from SUPPLIER_PRICE_D where SUPPLIER_ID=m.SUPPLIER_ID and PRO_NO=d.PRO_NO and CURR_ID=d.CURR_ID and TAX_ID=d.TAX_ID and TAX_TYPE=d.TAX_TYPE and UNIT_ID=d.UNIT_ID and REBATE=d.REBATE)
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end

		--更新料件的最近单价,如果是以材质报价则更新所有相同STUFF_ID的单价
		update PRODUCT set LAST_PURCHASE_PRICE = d.PRICE,LAST_PURCHASE_UNIT_ID = d.UNIT_ID,LAST_PURCHASE_CURR_ID=d.CURR_ID from PUR_QUOTE_M m, PUR_QUOTE_D d where m.QUOTE_TYPE=d.QUOTE_TYPE and m.QUOTE_NO=d.QUOTE_NO and m.QUOTE_TYPE=@quote_type and m.QUOTE_NO=@quote_no and (d.PRO_NO = PRODUCT.PRO_NO or d.PRO_NO = PRODUCT.STUFF_ID) 

		--更新报价参数
		update COP_QUOTE_PARAMETER
			set COP_QUOTE_PARAMETER.MATERIAL_SUM = round(q.QUOTE_PRICE, 3), 
				COP_QUOTE_PARAMETER.PRICE=case when COP_QUOTE_PARAMETER.MATERIAL_PCT!=0 then round(q.QUOTE_PRICE/COP_QUOTE_PARAMETER.MATERIAL_PCT*100,3) else 0 end
			from (select d.PRO_NO, max(d.PRICE*d.CURR_RATE*(case d.TAX_TYPE when 'I' then 1/(1+d.TAX_RATE/100) else 1 end)) as QUOTE_PRICE from PUR_QUOTE_M m, PUR_QUOTE_D d where m.QUOTE_TYPE=d.QUOTE_TYPE and m.QUOTE_NO=d.QUOTE_NO and m.CONFIRM_TAG=1 and m.IN_EFFECT_DATE>=DATEADD(dd, DATEDIFF(dd,0,getdate()), 0) and d.PRO_NO in (select PRO_NO from PUR_QUOTE_D where QUOTE_TYPE=@quote_type and QUOTE_NO=@quote_no) group by d.PRO_NO) q
			where COP_QUOTE_PARAMETER.STUFF_ID=q.PRO_NO 
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end

		insert into COP_QUOTE_PARAMETER(STUFF_ID, MATERIAL_SUM, MATERIAL_PCT, PRICE)
			select q.PRO_NO, round(q.PRICE*q.CURR_RATE*(case q.TAX_TYPE when 'I' then 1/(1+q.TAX_RATE/100) else 1 end),3), 100, 
				round(q.PRICE*q.CURR_RATE*(case q.TAX_TYPE when 'I' then 1/(1+q.TAX_RATE/100) else 1 end),3)
			from PUR_QUOTE_D q, STUFF s
			where q.QUOTE_TYPE=@quote_type and q.QUOTE_NO=@quote_no and q.PRO_NO=s.STUFF_ID and not exists(select * from COP_QUOTE_PARAMETER where STUFF_ID=q.PRO_NO)
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end

		update COP_QUOTE_PARAMETER
			set TAX_SUM=round(PRICE*TAX_PCT/100, 3), ASSISTANT_SUM=round(PRICE*ASSISTANT_PCT/100, 3),
			SALES_SUM=round(PRICE*SALES_PCT/100, 3), LOST_SUM=round(PRICE*LOST_PCT/100, 3),
			PRODUCE_SUM=round(PRICE*PRODUCE_PCT/100, 3), PACK_SUM=round(PRICE*PACK_PCT/100, 3),
			TRANSIT_SUM=round(PRICE*TRANSIT_PCT/100, 3), ELETRICITY_SUM=round(PRICE*ELETRICITY_PCT/100, 3),
			WAGE_SUM=round(PRICE*WAGE_PCT/100, 3), OTHER1_SUM=round(PRICE*OTHER1_PCT/100, 3),
			OTHER2_SUM=round(PRICE*OTHER2_PCT/100, 3), OTHER3_SUM=round(PRICE*OTHER3_PCT/100, 3),
			OTHER4_SUM=round(PRICE*OTHER4_PCT/100, 3), GAIN_SUM=round(PRICE*GAIN_PCT/100, 3)
			where exists(select * from PUR_QUOTE_D where QUOTE_TYPE=@quote_type and QUOTE_NO=@quote_no and PRO_NO=COP_QUOTE_PARAMETER.STUFF_ID)
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end

	end
	else begin
		--更新报价参数
		update COP_QUOTE_PARAMETER
			set COP_QUOTE_PARAMETER.MATERIAL_SUM = round(q.QUOTE_PRICE, 3), 
				COP_QUOTE_PARAMETER.PRICE=case when COP_QUOTE_PARAMETER.MATERIAL_PCT!=0 then round(q.QUOTE_PRICE/COP_QUOTE_PARAMETER.MATERIAL_PCT*100,3) else 0 end
			from (select d.PRO_NO, max(d.PRICE*d.CURR_RATE*(case d.TAX_TYPE when 'I' then 1/(1+d.TAX_RATE/100) else 1 end)) as QUOTE_PRICE from PUR_QUOTE_M m, PUR_QUOTE_D d where m.QUOTE_TYPE=d.QUOTE_TYPE and m.QUOTE_NO=d.QUOTE_NO and m.CONFIRM_TAG=1 and m.IN_EFFECT_DATE>=DATEADD(dd, DATEDIFF(dd,0,getdate()), 0) and d.PRO_NO in (select PRO_NO from PUR_QUOTE_D where QUOTE_TYPE=@quote_type and QUOTE_NO=@quote_no) group by d.PRO_NO) q
			where COP_QUOTE_PARAMETER.STUFF_ID=q.PRO_NO 
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end

		update COP_QUOTE_PARAMETER
			set TAX_SUM=round(PRICE*TAX_PCT/100, 3), ASSISTANT_SUM=round(PRICE*ASSISTANT_PCT/100, 3),
			SALES_SUM=round(PRICE*SALES_PCT/100, 3), LOST_SUM=round(PRICE*LOST_PCT/100, 3),
			PRODUCE_SUM=round(PRICE*PRODUCE_PCT/100, 3), PACK_SUM=round(PRICE*PACK_PCT/100, 3),
			TRANSIT_SUM=round(PRICE*TRANSIT_PCT/100, 3), ELETRICITY_SUM=round(PRICE*ELETRICITY_PCT/100, 3),
			WAGE_SUM=round(PRICE*WAGE_PCT/100, 3), OTHER1_SUM=round(PRICE*OTHER1_PCT/100, 3),
			OTHER2_SUM=round(PRICE*OTHER2_PCT/100, 3), OTHER3_SUM=round(PRICE*OTHER3_PCT/100, 3),
			OTHER4_SUM=round(PRICE*OTHER4_PCT/100, 3), GAIN_SUM=round(PRICE*GAIN_PCT/100, 3)
			where exists(select * from PUR_QUOTE_D where QUOTE_TYPE=@quote_type and QUOTE_NO=@quote_no and PRO_NO=COP_QUOTE_PARAMETER.STUFF_ID)
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
		--还原旧单价
		update SUPPLIER_PRICE_D set PRICE=OLD_PRICE, QUOTE_TYPE='', QUOTE_NO='', QUOTE_SERIAL_NO=0
			where QUOTE_TYPE=@quote_type and QUOTE_NO=@quote_no
		if @@ERROR <> 0 begin
			select @success = 0, @msg='还原旧单价失败'

			goto endhandle
		end
	end
	select @success=1

endhandle:
	return  @success
GO

-- P_WF_PUR_RECEIVE
CREATE PROCEDURE P_WF_PUR_RECEIVE
	@key_value varchar(200), @approve_tag int, @msg varchar(8000) output
AS
	declare @success int
	declare @sql nvarchar(4000)
	declare @receive_type nchar(10), @receive_no nchar(20), @receive_date datetime, @serial_no int, @qty float, @my_qty float, @finished_qty float
	select @success = 1

	select @sql = N'select @receive_type = RECEIVE_TYPE, @receive_no = RECEIVE_NO, @receive_date=RECEIVE_DATE from PUR_RECEIVE_M where ' + @key_value
	exec sp_executesql @sql,N'@receive_type nchar(10) output, @receive_no nchar(20) output, @receive_date datetime output', @receive_type output, @receive_no output, @receive_date output

	if @approve_tag = 1 begin --审核
		--检查数量<采购数量
		exec P_PUR_RECEIVE_CHECK @receive_type, @receive_no, @success output, @msg output
		if @@ERROR<>0 or @success = 0 begin
			select @success=0
			goto endhandle
		end
	end

	if @success = 1 begin
		--更新采购单
		update PUR_PURCHASE_D set PUR_PURCHASE_D.RECEIVE_QTY = ISNULL(PUR_PURCHASE_D.RECEIVE_QTY,0) + sd.QTY * @approve_tag, PUR_PURCHASE_D.REAL_DELIVERY_DATE=sd.RECEIVE_DATE, 
			PUR_PURCHASE_D.RECEIVE_SPARE_QTY = ISNULL(PUR_PURCHASE_D.RECEIVE_SPARE_QTY,0) + sd.SPARE_QTY * @approve_tag
			from (select d.PURCHASE_TYPE, d.PURCHASE_NO,d.PURCHASE_SERIAL_NO,max(m.RECEIVE_DATE) RECEIVE_DATE, sum(d.QTY) QTY,sum(SPARE_QTY) SPARE_QTY from PUR_RECEIVE_M m, PUR_RECEIVE_D d where m.RECEIVE_TYPE=d.RECEIVE_TYPE and m.RECEIVE_NO=d.RECEIVE_NO and m.RECEIVE_TYPE=@receive_type and m.RECEIVE_NO=@receive_no group by d.PURCHASE_TYPE, d.PURCHASE_NO,d.PURCHASE_SERIAL_NO) sd
			where PUR_PURCHASE_D.PURCHASE_TYPE=sd.PURCHASE_TYPE and PUR_PURCHASE_D.PURCHASE_NO=sd.PURCHASE_NO and PUR_PURCHASE_D.SERIAL_NO=sd.PURCHASE_SERIAL_NO
				
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
		--更新厂商最近交易日期
		update SUPPLIER set SUPPLIER.LAST_TRADE_DATE = case when isnull(SUPPLIER.LAST_TRADE_DATE, '1900/1/1')>PUR_RECEIVE_M.RECEIVE_DATE then SUPPLIER.LAST_TRADE_DATE else PUR_RECEIVE_M.RECEIVE_DATE end
			from PUR_RECEIVE_M
			where PUR_RECEIVE_M.RECEIVE_TYPE=@receive_type and PUR_RECEIVE_M.RECEIVE_NO=@receive_no and PUR_RECEIVE_M.SUPPLIER_ID=SUPPLIER.SUPPLIER_ID
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
		--更新产品最近交易日期
		update PRODUCT set PRODUCT.LAST_TRADE_DATE = case when isnull(PRODUCT.LAST_TRADE_DATE, '1900/1/1')>@receive_date then PRODUCT.LAST_TRADE_DATE else @receive_date  end
			from PUR_RECEIVE_D
			where PUR_RECEIVE_D.RECEIVE_TYPE=@receive_type and PUR_RECEIVE_D.RECEIVE_NO=@receive_no and PUR_RECEIVE_D.PRO_NO=PRODUCT.PRO_NO
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end

	update COP_ORDER_MORE set RECEIVE_QTY = isnull(RECEIVE_QTY,0) + isnull(sd.QTY,0) * @approve_tag 
		from (select d.ORDER_TYPE, d.ORDER_NO,d.PRO_NO,sum(d.QTY) QTY from PUR_RECEIVE_D d where d.RECEIVE_TYPE=@receive_type and d.RECEIVE_NO=@receive_no GROUP BY d.ORDER_TYPE, d.ORDER_NO,d.PRO_NO) sd 
		where COP_ORDER_MORE.ORDER_TYPE=sd.ORDER_TYPE and COP_ORDER_MORE.ORDER_NO=sd.ORDER_NO and COP_ORDER_MORE.PRO_NO=sd.PRO_NO 
	
	if @success = 1 begin
		--更新结案标志
		select @sql =  'select distinct PURCHASE_TYPE, PURCHASE_NO from PUR_RECEIVE_D where ' + @key_value 
		exec P_PUR_PURCHASE_FINISHED  @sql, @approve_tag, 1, @success output
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end
	if @success = 1 begin
		--更新库存、批号、日志
		select @sql =  'select m.RECEIVE_TYPE, m.RECEIVE_NO, m.RECEIVE_DATE, d.SERIAL_NO, d.PRO_NO, d.DEPOT_ID, ISNULL(d.QTY,0) + ISNULL(d.SPARE_QTY,0), d.UNIT_ID, ISNULL(d.PRICE,0), d.CURR_ID, d.CURR_RATE, d.AMOUNT, d.BATCH_NO from (select * from PUR_RECEIVE_M where ' + @key_value + ') m, PUR_RECEIVE_D d  where m.RECEIVE_TYPE=d.RECEIVE_TYPE and m.RECEIVE_NO=d.RECEIVE_NO'
		exec @success = P_UPDATE_PRO_DEPOT @sql, 1, @approve_tag, 0, @msg output
		if @@ERROR <> 0 begin
			select @success = 0
			goto endhandle
		end
	end
	if @success = 1 and exists(select * from SYSSS where PRO_MRP=1) begin
		--更新产品在途数量
		update PRODUCT set IN_BUY_QTY=isnull(PRODUCT.IN_BUY_QTY,0)-d.QTY*@approve_tag
			from (select PRO_NO, sum(QTY+SPARE_QTY) QTY from PUR_RECEIVE_D where RECEIVE_TYPE=@receive_type and RECEIVE_NO=@receive_no group by PRO_NO) d
			where PRODUCT.PRO_NO=d.PRO_NO
		if @@ERROR <> 0 begin
			select @success = 0, @msg='更新产品在途数量失败'
			goto endhandle
		end
	end

endhandle:
return  @success
GO

