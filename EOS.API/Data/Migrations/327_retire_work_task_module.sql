-- ============================================================================
-- EOS.ERP migration 327: 退役模块 2308「工作任务记录」与它的主表 SYS_WORK_TASK
-- ----------------------------------------------------------------------------
-- 用户在 2026-10-06 同一轮里先暂留、随后改判（决策清单 #145）：
--
--   "2308 工作任务记录 + 表 SYS_WORK_TASK，这是旧系统的开发团队的工作模式，
--    我们不采用这种模式工作，因此，也彻底删。"
--
-- 现状取证（本迁移落地前实测）：
--   · 模块 2308：M_URL=/workbench、M_TAG=1、主表 SYS_WORK_TASK、无子模块；
--   · 表 SYS_WORK_TASK：**0 行**（表空，删表不丢数据；字段登记 27 行、数据源 2 行、
--     默认查询列 9 行，是"曾经配过、没人用"的形态）；
--   · 挂件（按 M_IDX 动态扫全库）：MODULE_FORM_LAYOUT 27 / WORKBENCH_DEFINITION_SNAPSHOT 11 /
--     SYSDD 2 / MODULE_FORM_TAB 1 / SYSDH 1 / WORKBENCH_IDEMPOTENCY 1 / REPORT 1；
--   · REPORT 那 1 行是模块自带的报表定义（REPORT_ID='SYS_WORK_TASK'，名为「工作任务记录」），
--     随模块一并退役（本脚本留痕打印它的名字）。
--
-- 保留的：AUDIT_EVENT 里 14 行历史痕迹（项目惯例：删审计行等于抹掉痕迹）。
--
-- 命名全大写；**非幂等**：模块或表不在即报错（与 320 / 322 / 325 同口径）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
-- WORKBENCH_DEFINITION_SNAPSHOT 上有筛选唯一索引（UX_..._CURRENT，IS_CURRENT=1）：删它的行要求
-- QUOTED_IDENTIFIER ON（否则 Msg 1934）。DbUp 走 ADO.NET 默认就是 ON，但 sqlcmd 默认 OFF——
-- 显式打开，好让本脚本在 scripts/test-migration-dryrun.ps1 下也能干跑。
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：模块在、形态与预期一致（主表 / 承载页 / 无子模块）
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
               WHERE M_IDX = 2308
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'SYS_WORK_TASK')
    THROW 53700, N'2308 不在、或它的主表已不是 SYS_WORK_TASK：本迁移的前提不成立（已退役或结构已变）。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK) WHERE M_P_IDX = 2308)
    THROW 53701, N'2308 下还挂着子模块：先处理子模块，再退役这个模块。', 1;

-- ② 前置自证：表必须为空。绝不静默丢数据——真有行就先导出一份归档、再回来删。
IF OBJECT_ID(N'dbo.SYS_WORK_TASK', N'U') IS NULL
    THROW 53702, N'SYS_WORK_TASK 不在：本迁移的前提不成立（已删过）。', 1;

DECLARE @taskRows INT = (SELECT COUNT(*) FROM dbo.SYS_WORK_TASK);
IF @taskRows <> 0
    THROW 53703, N'SYS_WORK_TASK 里有数据：先导出归档（照 scripts/export-sysdf-archive.ps1 的口径写一份导出脚本）再退役，别静默丢数据。', 1;

-- ③ 前置自证：不得被视图 / 过程 / 函数引用，也不得有外键
IF EXISTS (SELECT 1 FROM sys.sql_expression_dependencies
           WHERE referenced_id = OBJECT_ID(N'dbo.SYS_WORK_TASK'))
    THROW 53704, N'SYS_WORK_TASK 仍被视图或存储过程引用：先摘依赖，再删表。', 1;

IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE referenced_object_id = OBJECT_ID(N'dbo.SYS_WORK_TASK')
              OR parent_object_id = OBJECT_ID(N'dbo.SYS_WORK_TASK'))
    THROW 53705, N'SYS_WORK_TASK 上还有外键：先摘外键，再删表。', 1;

-- ④ 前置自证：除白名单外，任何挂着 M_IDX 的表里都不得有 2308 的行。
--    动态扫全库（不写死表名）：将来新增一张按模块存数据的表，这条守卫自动覆盖到它。
--    白名单＝本脚本显式要清的几张表 + AUDIT_EVENT（审计痕迹，按惯例保留）。
DECLARE @stray NVARCHAR(MAX) = N'';
DECLARE @probe NVARCHAR(MAX), @tbl SYSNAME, @n INT;
DECLARE strayCur CURSOR LOCAL FAST_FORWARD FOR
    SELECT t.name FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id AND s.name = N'dbo'
    WHERE c.name = N'M_IDX'
      AND t.name NOT IN (N'MODULES', N'SYSDD', N'SYSDH', N'MODULE_FORM_TAB', N'MODULE_FORM_LAYOUT',
                         N'MODULE_BUSINESS_ACTION', N'MODULE_VALIDATION_RULE', N'REPORT',
                         N'REPORT_INBOX', N'REPORT_SUBSCRIPTION',
                         N'WORKBENCH_DEFINITION_SNAPSHOT', N'WORKBENCH_MODULE_DIRTY',
                         N'WORKBENCH_IDEMPOTENCY', N'AUDIT_EVENT');
OPEN strayCur;
FETCH NEXT FROM strayCur INTO @tbl;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @probe = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@tbl) + N' WHERE M_IDX = 2308;';
    EXEC sp_executesql @probe, N'@n INT OUTPUT', @n OUTPUT;
    IF @n > 0 SET @stray = @stray + N'    dbo.' + @tbl + N'：' + CONVERT(nvarchar(20), @n) + N' 行' + CHAR(10);
    FETCH NEXT FROM strayCur INTO @tbl;
END
CLOSE strayCur; DEALLOCATE strayCur;

IF LEN(@stray) > 0
    THROW 53706, N'除白名单外还有表挂着 2308 的行——先逐张解释清楚（该随模块删的加进白名单，该留的说明理由）：', 1;

-- ⑤ 留痕：删掉的是什么（含模块自带的报表定义名字）
DECLARE @taskDesc NVARCHAR(200) = (SELECT ISNULL(M_DESC, N'') FROM dbo.MODULES WHERE M_IDX = 2308);
DECLARE @reportId VARCHAR(60) = (SELECT TOP 1 LTRIM(RTRIM(REPORT_ID)) FROM dbo.REPORT WHERE M_IDX = 2308);
DECLARE @reportName NVARCHAR(200) = (SELECT TOP 1 ISNULL(REPORT_NAME, N'') FROM dbo.REPORT WHERE M_IDX = 2308);

PRINT N'== 退役模块 2308（决策清单 #145：旧开发团队的工作模式，不采用）=='
    + CHAR(10) + N'    2308 ' + ISNULL(@taskDesc, N'') + N'（M_URL=/workbench、主表 SYS_WORK_TASK）'
    + CHAR(10) + N'    表 SYS_WORK_TASK 行数 ' + CONVERT(nvarchar(20), @taskRows) + N'（空表，删表不丢数据）'
    + CHAR(10) + N'    随模块退役的报表定义：' + ISNULL(@reportId, N'(无)') + N' / ' + ISNULL(@reportName, N'')
    + CHAR(10) + N'    AUDIT_EVENT 里 14 行历史痕迹按惯例保留';

-- ⑥ 连带行清理（逐表留痕）
DECLARE @permRows INT = 0, @formRows INT = 0, @snapRows INT = 0, @miscRows INT = 0;

DELETE FROM dbo.SYSDD WHERE M_IDX = 2308;
SET @permRows = @permRows + @@ROWCOUNT;
DELETE FROM dbo.SYSDH WHERE M_IDX = 2308;
SET @permRows = @permRows + @@ROWCOUNT;

DELETE FROM dbo.MODULE_FORM_TAB WHERE M_IDX = 2308;
SET @formRows = @formRows + @@ROWCOUNT;
DELETE FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX = 2308;
SET @formRows = @formRows + @@ROWCOUNT;

DELETE FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX = 2308;
SET @snapRows = @@ROWCOUNT;

DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = 2308;
SET @miscRows = @miscRows + @@ROWCOUNT;
DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX = 2308;
SET @miscRows = @miscRows + @@ROWCOUNT;
DELETE FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX = 2308;
SET @miscRows = @miscRows + @@ROWCOUNT;
DELETE FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX = 2308;
SET @miscRows = @miscRows + @@ROWCOUNT;
DELETE FROM dbo.REPORT_INBOX WHERE M_IDX = 2308;
SET @miscRows = @miscRows + @@ROWCOUNT;
DELETE FROM dbo.REPORT_SUBSCRIPTION WHERE M_IDX = 2308;
SET @miscRows = @miscRows + @@ROWCOUNT;

-- 报表家族里按 REPORT_ID 挂的行（本库 0 行；照删，免得留下孤儿行）
DELETE FROM dbo.REPORT_SORT WHERE LTRIM(RTRIM(REPORT_ID)) = N'SYS_WORK_TASK';
SET @miscRows = @miscRows + @@ROWCOUNT;

DELETE FROM dbo.REPORT WHERE M_IDX = 2308;

DELETE FROM dbo.MODULES WHERE M_IDX = 2308;

PRINT N'== 连带行清理（留痕）：权限 ' + CONVERT(nvarchar(10), @permRows)
    + N' 行；表单版式 ' + CONVERT(nvarchar(10), @formRows)
    + N' 行；工作台快照 ' + CONVERT(nvarchar(10), @snapRows)
    + N' 行；其它 ' + CONVERT(nvarchar(10), @miscRows) + N' 行 ==';

-- ⑦ 删 SYS_WORK_TASK 的元数据挂件（描述一张即将不存在的表的行），再删表
DECLARE @fieldsRows INT = (SELECT COUNT(*) FROM dbo.FIELDS WHERE T_ID = N'SYS_WORK_TASK');
DECLARE @dsRows INT = (SELECT COUNT(*) FROM dbo.FIELD_DATASOURCE WHERE T_ID = N'SYS_WORK_TASK');
DECLARE @qryRows INT = (SELECT COUNT(*) FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'SYS_WORK_TASK');
DECLARE @qryFieldRows INT = (SELECT COUNT(*) FROM dbo.SYSQL_FIELDS WHERE T_ID = N'SYS_WORK_TASK');

DELETE FROM dbo.FIELDS WHERE T_ID = N'SYS_WORK_TASK';
DELETE FROM dbo.FIELD_DATASOURCE WHERE T_ID = N'SYS_WORK_TASK';
DELETE FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'SYS_WORK_TASK';
DELETE FROM dbo.SYSQL_FIELDS WHERE T_ID = N'SYS_WORK_TASK';
DELETE FROM dbo.TABLES WHERE T_ID = N'SYS_WORK_TASK';

PRINT N'== 随 SYS_WORK_TASK 删除的元数据登记 =='
    + CHAR(10) + N'    FIELDS ' + CONVERT(nvarchar(10), @fieldsRows) + N' 行'
    + CHAR(10) + N'    FIELD_DATASOURCE ' + CONVERT(nvarchar(10), @dsRows) + N' 行'
    + CHAR(10) + N'    SYSQL_DEFAULT ' + CONVERT(nvarchar(10), @qryRows) + N' 行'
    + CHAR(10) + N'    SYSQL_FIELDS ' + CONVERT(nvarchar(10), @qryFieldRows) + N' 行';

DROP TABLE dbo.SYS_WORK_TASK;

-- ⑧ 后置自证：模块与表必须消失，元数据登记必须清干净
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2308)
    THROW 53710, N'2308 未被删除。', 1;

IF OBJECT_ID(N'dbo.SYS_WORK_TASK', N'U') IS NOT NULL
    THROW 53711, N'SYS_WORK_TASK 未被删除。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'SYS_WORK_TASK')
    OR EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE WHERE T_ID = N'SYS_WORK_TASK')
    OR EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'SYS_WORK_TASK')
    OR EXISTS (SELECT 1 FROM dbo.SYSQL_FIELDS WHERE T_ID = N'SYS_WORK_TASK')
    OR EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID = N'SYS_WORK_TASK')
    THROW 53712, N'仍留着 SYS_WORK_TASK 的元数据登记（描述不存在的表会在字段维护页上变成坏条目）。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT WHERE M_IDX = 2308)
    OR EXISTS (SELECT 1 FROM dbo.MODULE_FORM_TAB WHERE M_IDX = 2308)
    OR EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX = 2308)
    OR EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX = 2308)
    OR EXISTS (SELECT 1 FROM dbo.SYSDD WHERE M_IDX = 2308)
    OR EXISTS (SELECT 1 FROM dbo.SYSDH WHERE M_IDX = 2308)
    THROW 53713, N'2308 还有挂件没清干净（报表 / 版式 / 快照 / 权限）。', 1;

-- ⑨ 后置自证：不该被这次退役顺手动到的东西必须原样
IF (SELECT COUNT(*) FROM dbo.MODULES) <> 360
    THROW 53714, N'MODULES 行数不是 360（363 − 2205/1399 − 2308 ×1 = 360）：说明本次退役多删或少删了模块。', 1;

IF (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT) <> 541
    THROW 53715, N'SYSQR_DEFAULT 行数变了（应为 541）：报表条件真源不该被本次退役碰到。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.AUDIT_EVENT WITH (NOLOCK) WHERE M_IDX = 2308)
    THROW 53716, N'2308 的审计痕迹被删了：审计行按项目惯例保留（删它等于抹掉痕迹）。', 1;

COMMIT TRANSACTION;

PRINT N'== 退役完成：模块 2308「工作任务记录」与主表 SYS_WORK_TASK 已从库中删除（审计痕迹保留）==';
