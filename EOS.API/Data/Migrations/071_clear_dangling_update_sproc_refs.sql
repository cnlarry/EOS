-- ============================================================================
-- EOS.ERP migration 072: 清理 MODULES.UPDATE_SP 中指向已不存在存储过程的引用
-- ----------------------------------------------------------------------------
-- 背景：批核旧存储过程在 061-071 中按域下线后，MODULES.UPDATE_SP 仍保留了过程名。
-- 运行时在「效果链对该事件没有任何动作」时会回落到 UPDATE_SP 调用旧过程；一旦该过程
-- 已被下线，批核会因找不到存储过程而失败。典型场景是无业务动作模块的批核/自动批核。
-- 处理：把这些悬空引用置空，并同步修正已发布 Definition 快照里的 WorkflowSproc 字段，
-- 使 MiddlesDefinition 与 MODULES 一致（无需依赖后续重发布即可生效）。
-- 幂等：只处理 OBJECT_ID 判定为不存在的引用；重复执行第二次为空操作。
-- 未处理：正在被使用、过程体仍存在的批核与保存钩子（它们的迁移另行处理）。
-- ============================================================================

SET NOCOUNT ON;

/* WORKBENCH_DEFINITION_SNAPSHOT 上有筛选唯一索引（IS_CURRENT=1），更新必须带正确的 SET 选项。 */
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'tempdb..#DanglingUpdateSproc') IS NOT NULL DROP TABLE #DanglingUpdateSproc;

SELECT M.M_IDX AS MODULE_ID, LTRIM(RTRIM(M.UPDATE_SP)) AS SPROC
INTO #DanglingUpdateSproc
FROM dbo.MODULES M
WHERE LEN(LTRIM(RTRIM(ISNULL(M.UPDATE_SP, N' ')))) > 1
  AND OBJECT_ID(N'dbo.' + LTRIM(RTRIM(M.UPDATE_SP)), N'P') IS NULL;

DECLARE @AFFECTED INT = (SELECT COUNT(*) FROM #DanglingUpdateSproc);

IF @AFFECTED = 0
BEGIN
    PRINT N'== 无悬空 UPDATE_SP 引用，迁移空转 ==';
END
ELSE
BEGIN
    DECLARE @AUDIT NVARCHAR(MAX) = N'';
    SELECT @AUDIT = @AUDIT + CAST(D.MODULE_ID AS NVARCHAR(10)) + N'->' + D.SPROC + N'  '
    FROM #DanglingUpdateSproc D
    ORDER BY D.MODULE_ID;

    /* 已发布快照里的批核过程名与 MODULES 保持一致置空，避免运行时仍回落到旧过程。 */
    UPDATE S
       SET S.DEFINITION_JSON = REPLACE(S.DEFINITION_JSON,
                                       N'"WorkflowSproc":"' + D.SPROC + N'"',
                                       N'"WorkflowSproc":null')
    FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
    JOIN #DanglingUpdateSproc D ON D.MODULE_ID = S.MODULE_ID
    WHERE S.IS_CURRENT = 1
      AND S.DEFINITION_JSON LIKE N'%"WorkflowSproc":"' + D.SPROC + N'"%';

    UPDATE M
       SET M.UPDATE_SP = NULL,
           M.LAST_UPDATE_BY = N'DbUp',
           M.LAST_UPDATE_DATE = GETDATE()
    FROM dbo.MODULES M
    JOIN #DanglingUpdateSproc D ON D.MODULE_ID = M.M_IDX;

    PRINT N'== 悬空 UPDATE_SP 引用清理完成，共 ' + CAST(@AFFECTED AS NVARCHAR(10)) + N' 个模块 ==';
    PRINT N'   明细：' + @AUDIT;
END

IF OBJECT_ID(N'tempdb..#DanglingUpdateSproc') IS NOT NULL DROP TABLE #DanglingUpdateSproc;
