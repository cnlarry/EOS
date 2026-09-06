-- ============================================================================
-- EOS.ERP migration 060: MODULES.EFFECT_ENGINE_TAG — 模块级效果引擎灰度开关
-- ----------------------------------------------------------------------------
-- 模块开关存放于模块元数据（工作区可编辑），发布时写入 Definition JSON 的
-- effectEngine.enabled 段供运行时读取；总闸另由 appsettings EffectEngine:Enabled 控制。
-- 命名全大写；幂等：COL_LENGTH 守卫。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF COL_LENGTH(N'dbo.MODULES', N'EFFECT_ENGINE_TAG') IS NULL
BEGIN
    ALTER TABLE dbo.MODULES ADD EFFECT_ENGINE_TAG BIT NOT NULL
        CONSTRAINT DF_MODULES_EFFECT_ENGINE_TAG DEFAULT (0);
END

IF NOT EXISTS (
    SELECT 1 FROM sys.extended_properties ep
    WHERE ep.major_id = OBJECT_ID(N'dbo.MODULES')
      AND ep.minor_id = COLUMNPROPERTY(OBJECT_ID(N'dbo.MODULES'), N'EFFECT_ENGINE_TAG', N'ColumnId')
      AND ep.name = N'MS_Description'
)
BEGIN
    EXEC sys.sp_addextendedproperty @name=N'MS_Description',
        @value=N'模块级效果引擎开关（发布时写入 Definition JSON effectEngine.enabled）。',
        @level0type=N'SCHEMA', @level0name=N'dbo',
        @level1type=N'TABLE', @level1name=N'MODULES',
        @level2type=N'COLUMN', @level2name=N'EFFECT_ENGINE_TAG';
END

PRINT N'== MODULES.EFFECT_ENGINE_TAG 创建完成 ==';
