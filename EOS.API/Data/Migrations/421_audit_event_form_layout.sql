-- ============================================================
-- EOS.ERP migration 421: 操作审计（2316）补模块级表单版式
-- ------------------------------------------------------------
-- 354 把 2316 改成统一工作台只读承载，登记了主表、字段与默认列，却漏了版式行：
-- 没有 MODULE_FORM_TAB / MODULE_FORM_LAYOUT，表单退化成"按字段顺序平铺"的一页，
-- 页签与复合格都无从配置（check-form-layout 的"持快照必须有版式行"会一直报它）。
--
-- 本脚本按物理列顺序给出**两列语义、四子列栅格**的只读版式：
-- 事件身份与结果在第一行，操作者与来源在第二行，对象在中段，追踪信息在下段，
-- 摘要单独占满一行（长文本，两行高）。改版式请走 2302/版式设计器，不要改本脚本。
-- ============================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
GO

DECLARE @ModuleId INT = 2316;
DECLARE @Table NVARCHAR(100) = N'AUDIT_EVENT';
DECLARE @Mig NVARCHAR(100) = N'DbUp';

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @ModuleId)
    THROW 57901, N'模块 2316 不存在，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES m
               WHERE m.M_IDX = @ModuleId
                 AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = @Table
                 AND ISNULL(m.UNIFIED_FORM_ENABLED, 0) = 1)
    THROW 57902, N'模块 2316 形态不符（应为 AUDIT_EVENT 主表的表单模块），迁移中止。', 1;

-- 已有版式行说明有人配过了：不自作主张覆盖
IF EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX = @ModuleId)
    PRINT N'[421] 2316 已有版式行，跳过。';
ELSE
BEGIN
    SET XACT_ABORT ON;
    BEGIN TRANSACTION;

    INSERT INTO dbo.MODULE_FORM_TAB (M_IDX, TAB_NO, TAB_TITLE, LAYOUT_COLUMNS)
    SELECT @ModuleId, 1, N'', 4
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_TAB WHERE M_IDX = @ModuleId AND TAB_NO = 1);

    INSERT INTO dbo.MODULE_FORM_LAYOUT
        (M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE, SECTION_ID, CELL_GROUP, CELL_ROLE, IS_HIDDEN, UPDATED_BY, UPDATED_AT)
    VALUES
        (@ModuleId, @Table, N'EVENT_ID',            1,  1, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'OCCURRED_AT',         1,  2, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'RESULT',              1,  3, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'ACTION',              1,  4, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'ACTOR_USER_ID',       1,  5, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'ACTOR_DISPLAY_NAME',  1,  6, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'ACTOR_TYPE',          1,  7, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'CLIENT_TYPE',         1,  8, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'M_IDX',               1,  9, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'RESOURCE_TYPE',       1, 10, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'RESOURCE_KEY',        1, 11, 2, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'CREATED_DATE',        1, 12, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'TRACE_ID',            1, 13, 2, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'CLIENT_IP',           1, 14, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'CORRELATION_ID',      1, 15, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'DEFINITION_VERSION',  1, 16, 1, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'ERROR_CODE',          1, 17, 2, 1, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME()),
        (@ModuleId, @Table, N'SUMMARY',             1, 18, 4, 2, 0, NULL, NULL, 0, 0, @Mig, SYSDATETIME());

    COMMIT TRANSACTION;
    PRINT N'[421] 2316 版式行已写入。';
END

-- 版式改了要重新发布：标脏让"待发布"清单看得见（只对工作台模块生效）
IF EXISTS (SELECT 1 FROM dbo.V_MODULE_NODE n WHERE n.M_IDX = @ModuleId AND n.NODE_KIND = N'WORKBENCH')
BEGIN
    MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
    USING (SELECT @ModuleId AS M_IDX) AS s ON t.M_IDX = s.M_IDX
    WHEN MATCHED THEN
        UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = @Mig, LAST_MODIFIED_AT = SYSDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
        VALUES (s.M_IDX, 1, @Mig, SYSDATETIME());
END

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX = @ModuleId)
    THROW 57903, N'2316 版式行仍为空，迁移中止。', 1;
GO
