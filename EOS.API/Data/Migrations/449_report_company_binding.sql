-- ============================================================================
-- EOS.ERP migration 449: 报表绑定公司（110101 公司基本资料 / COMPANY）
-- ----------------------------------------------------------------------------
-- 一张报表涉及三张表：主表、明细表、公司表。主表与明细表随单据走，公司表不随单据走，
-- 它是**开单主体**的配置：同一套送货单/拣货单可能分属不同法人实体（大陆公司、香港公司、
-- 内地其它公司），各实体用自己的公司抬头接单。绑定粒度因此落在报表（REPORT）上：
--   · 同一模块的多张报表可以各用自己的公司（送货单归 A 公司、拣货单归 B 公司）；
--   · 版式（layout.json）只描述"画什么"，不承担"哪家公司"这类数据。
--
-- 取值口径（打印侧）：**只认报表上的显式值，不做任何"猜一个公司"的兜底**：
--   · 存量报表一律回填为公司主档的系统默认编号 `DEFAULT`（本库唯一公司记录；
--     单公司部署的真实编号就是它，见迁移 096）；
--   · 列收紧为 NOT NULL + DEFAULT('DEFAULT')，新建报表不写该列时也落显式值；
--   · 另加 CHECK 约束禁止空串——"没选公司"必须是数据错误，而不是被代码悄悄补一个。
--   · 引用的公司记录不存在时，打印侧公司字段渲染为空（fail-closed），不回退页头旧文本。
--
-- 为什么是 nvarchar(20)：与 COMPANY.COMPANY_ID 的列类型/长度一致（本库刻意不用外键
-- 表达引用，见 ADR-036；引用完整性由保存校验与 CHECK 承担）。
--
-- 幂等：第二次执行不改任何行；列已存在（旧版本手工建过）时走补齐分支。
-- 回滚：DROP CONSTRAINT CK_REPORT_COMPANY_ID / DF_REPORT_COMPANY_ID，再 DROP COLUMN
--      COMPANY_ID（会丢失报表的公司绑定）。
-- ============================================================================

SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;

IF COL_LENGTH(N'dbo.REPORT', N'COMPANY_ID') IS NULL
BEGIN
    ALTER TABLE dbo.REPORT ADD COMPANY_ID nvarchar(20) NOT NULL
        CONSTRAINT DF_REPORT_COMPANY_ID DEFAULT (N'DEFAULT');
END
ELSE
BEGIN
    -- 旧列（可空、无默认值）：先把存量钉到显式默认公司，再补约束、收紧 NOT NULL。
    UPDATE dbo.REPORT
    SET COMPANY_ID = N'DEFAULT'
    WHERE COMPANY_ID IS NULL OR LTRIM(RTRIM(COMPANY_ID)) = N'';

    IF NOT EXISTS (SELECT 1
                   FROM sys.default_constraints dc
                   JOIN sys.columns c ON c.object_id = dc.parent_object_id
                                      AND c.column_id = dc.parent_column_id
                   WHERE dc.parent_object_id = OBJECT_ID(N'dbo.REPORT')
                     AND c.name = N'COMPANY_ID')
        ALTER TABLE dbo.REPORT ADD CONSTRAINT DF_REPORT_COMPANY_ID DEFAULT (N'DEFAULT') FOR COMPANY_ID;

    IF EXISTS (SELECT 1 FROM sys.columns
               WHERE object_id = OBJECT_ID(N'dbo.REPORT') AND name = N'COMPANY_ID' AND is_nullable = 1)
        ALTER TABLE dbo.REPORT ALTER COLUMN COMPANY_ID nvarchar(20) NOT NULL;
END

-- 空串不是"未绑定"：它会让打印静默失去公司名，必须在库层拒绝。
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE parent_object_id = OBJECT_ID(N'dbo.REPORT') AND name = N'CK_REPORT_COMPANY_ID')
    ALTER TABLE dbo.REPORT WITH CHECK
        ADD CONSTRAINT CK_REPORT_COMPANY_ID CHECK (LTRIM(RTRIM(COMPANY_ID)) <> N'');

IF EXISTS (SELECT 1 FROM sys.extended_properties
           WHERE major_id = OBJECT_ID(N'dbo.REPORT')
             AND minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.REPORT'), N'COMPANY_ID', 'ColumnId')
             AND name = N'MS_Description')
    EXEC sp_updateextendedproperty
        @name = N'MS_Description',
        @value = N'开单公司（COMPANY.COMPANY_ID，110101 公司基本资料）；系统默认公司编号 DEFAULT，存量与新建报表均落显式值。',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE',  @level1name = N'REPORT',
        @level2type = N'COLUMN', @level2name = N'COMPANY_ID';
ELSE
    EXEC sp_addextendedproperty
        @name = N'MS_Description',
        @value = N'开单公司（COMPANY.COMPANY_ID，110101 公司基本资料）；系统默认公司编号 DEFAULT，存量与新建报表均落显式值。',
        @level0type = N'SCHEMA', @level0name = N'dbo',
        @level1type = N'TABLE',  @level1name = N'REPORT',
        @level2type = N'COLUMN', @level2name = N'COMPANY_ID';
