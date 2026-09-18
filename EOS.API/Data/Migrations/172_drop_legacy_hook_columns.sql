-- EOS.ERP migration 173: 物理删除 MODULES.UPDATE_SP / MODULES.AFTERSAVE_SP 两个遗留钩子列
-- ----------------------------------------------------------------------------
-- 背景：这两列是旧系统的钩子字段（`UPDATE_SP`＝批核生效过程，`AFTERSAVE_SP`＝保存后过程）。
-- 新系统的保存/批核行为已全部由**校验目录**（MODULE_VALIDATION_RULE）与**效果目录**
-- （MODULE_BUSINESS_ACTION）承载：批核侧与保存侧的遗留过程引用均已清零（各 0 个模块），
-- 定义里的 `BusinessRule.DomainRule` 与领域规则过渡桥也已拆除。用户指示：把这两列**物理删除**。
--
-- 先证后删（任一不成立即 THROW，绝不在有引用的环境上盲删）：
--   ⒜ 非空引用必须为 0（`UPDATE_SP`/`AFTERSAVE_SP` 任一非空的模块数 = 0）；
--   ⒝ 当前快照不得残留 `BusinessRule.AfterSaveSproc` / `WorkflowSproc`（按 JSON 取值判定，不用 LIKE 粗扫）；
--   ⒞ 库内不得有对象（视图/过程/函数）定义里引用这两列名；
--   ⒟ 两列上不得有默认值约束或索引（若有须先人工处置，脚本不代删约束）。
--
-- 同批清理字段元数据（否则 2301 菜单管理界面仍会渲染这两个已不存在的字段）：
--   `FIELDS`（T_ID='MODULES' 的两行）与 `SYSQL_DEFAULT`（列表默认列里的 UPDATE_SP 行）。
--   注意：`MODULES_Backup_ADR004` 是历史备份表，**保持原样**（它的列与元数据不动）。
--
-- 幂等：两列任一已不存在时只做元数据清理与断言，不重复 DROP。
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

DECLARE @HasUpdate BIT = COL_LENGTH('dbo.MODULES', 'UPDATE_SP');
DECLARE @HasAfterSave BIT = COL_LENGTH('dbo.MODULES', 'AFTERSAVE_SP');

IF @HasUpdate IS NULL AND @HasAfterSave IS NULL
BEGIN
    PRINT N'== MODULES.UPDATE_SP / AFTERSAVE_SP 已不存在（幂等跳过 DROP）==';
END
ELSE
BEGIN
    /* ⒜ 非空引用必须为 0 */
    IF EXISTS (SELECT 1 FROM dbo.MODULES
               WHERE LTRIM(RTRIM(ISNULL(UPDATE_SP, N''))) <> N''
                  OR LTRIM(RTRIM(ISNULL(AFTERSAVE_SP, N''))) <> N'')
        THROW 50001, N'仍有模块挂着 UPDATE_SP/AFTERSAVE_SP 引用，不能在引用清零前删列（迁移中止）。', 1;

    /* ⒝ 当前快照不得残留过程字段 */
    IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
               WHERE IS_CURRENT = 1
                 AND (JSON_VALUE(DEFINITION_JSON, N'$.BusinessRule.AfterSaveSproc') IS NOT NULL
                   OR JSON_VALUE(DEFINITION_JSON, N'$.BusinessRule.WorkflowSproc') IS NOT NULL))
        THROW 50002, N'当前快照仍残留 AfterSaveSproc/WorkflowSproc（重发布后再删列），迁移中止。', 1;

    /* ⒞ 库内对象不得引用这两列名（保守文本判定：定义里出现列名即拦下人工复核） */
    IF EXISTS (SELECT 1 FROM sys.sql_modules sm
               WHERE sm.definition LIKE N'%UPDATE_SP%' OR sm.definition LIKE N'%AFTERSAVE_SP%')
        THROW 50003, N'库内仍有对象定义引用 UPDATE_SP/AFTERSAVE_SP，迁移中止（先人工复核）。', 1;

    /* ⒟ 两列上不得有默认值约束或索引 */
    IF EXISTS (SELECT 1 FROM sys.columns c
               WHERE c.object_id = OBJECT_ID('dbo.MODULES') AND c.name IN (N'UPDATE_SP', N'AFTERSAVE_SP')
                 AND c.default_object_id <> 0)
        THROW 50004, N'UPDATE_SP/AFTERSAVE_SP 上存在默认值约束，迁移中止（先人工处置）。', 1;

    IF EXISTS (SELECT 1 FROM sys.index_columns ic JOIN sys.columns c
                 ON c.object_id = ic.object_id AND c.column_id = ic.column_id
               WHERE ic.object_id = OBJECT_ID('dbo.MODULES') AND c.name IN (N'UPDATE_SP', N'AFTERSAVE_SP'))
        THROW 50005, N'UPDATE_SP/AFTERSAVE_SP 上存在索引，迁移中止（先人工处置）。', 1;

    /* 字段元数据：菜单管理的列定义与列表默认列 */
    DELETE FROM dbo.FIELDS WHERE T_ID = N'MODULES' AND F_ID IN (N'UPDATE_SP', N'AFTERSAVE_SP');
    DELETE FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'MODULES' AND F_ID IN (N'UPDATE_SP', N'AFTERSAVE_SP');

    /* 真正删列（无默认值/索引，直接 DROP 即可；列级扩展属性随列一并消失） */
    IF @HasUpdate IS NOT NULL
        ALTER TABLE dbo.MODULES DROP COLUMN UPDATE_SP;
    IF @HasAfterSave IS NOT NULL
        ALTER TABLE dbo.MODULES DROP COLUMN AFTERSAVE_SP;

    PRINT N'== MODULES.UPDATE_SP / AFTERSAVE_SP 已物理删除（含 FIELDS/SYSQL_DEFAULT 元数据清理）==';
END

/* 收口断言：列不存在、元数据无残留 */
IF COL_LENGTH('dbo.MODULES', 'UPDATE_SP') IS NOT NULL
    THROW 50006, N'UPDATE_SP 仍存在，迁移中止。', 1;

IF COL_LENGTH('dbo.MODULES', 'AFTERSAVE_SP') IS NOT NULL
    THROW 50007, N'AFTERSAVE_SP 仍存在，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'MODULES' AND F_ID IN (N'UPDATE_SP', N'AFTERSAVE_SP'))
    THROW 50008, N'FIELDS 里仍有 MODULES 的两个钩子字段元数据，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'MODULES' AND F_ID IN (N'UPDATE_SP', N'AFTERSAVE_SP'))
    THROW 50009, N'SYSQL_DEFAULT 里仍有 MODULES 的钩子字段，迁移中止。', 1;
