-- ============================================================================
-- EOS.ERP migration 325: 退役两个死模块（2205 / 1399）与两张随行表（SYSQR_DA / SYSDF）
-- ----------------------------------------------------------------------------
-- 用户 2026-10-06 拍板（决策清单 #144）：
--
--   2205「报表过滤条件设置」   M_TAG=0、M_URL 空、无子模块。它的管理页与端点早就下线了
--                             （前端只剩 routeElements.tsx 里一条注释，后端没有任何
--                             report-conditions 端点），主表 SYSQR_DA（132 行）是旧"条件定义"
--                             表——现代真源是 **SYSQR_DEFAULT.FILTER_TEMPLATE**（ReportRepository
--                             直读，本迁移**不碰**它）。整套退役。
--   1399「仓库综合报表」       M_TAG=1 但 M_URL 空、无主表、无子模块 ⇒ 侧栏看得见、点开落
--                             "未接线"占位页（/fallback/modules/1399）。实测全库唯一一个
--                             这种形态的叶子。整行退役。
--   SYSQR_DA                   132 行旧条件定义表 + 43 行元数据（FIELDS 13 / SYSQL_DEFAULT 11 /
--                              SYSQL_FIELDS 17 / FIELD_DATASOURCE 1 / TABLES 1）。
--   SYSDF                      11134 行旧操作流水（2007-11-14 ~ 2026-09-05）。AUDIT_EVENT v2
--                              上线后 WorkbenchAuditWriter 明文停写、AuditController 不再查它。
--                              **导出 CSV 备查后删**：logs/archive/retire-324/SYSDF-*.csv
--                              （11134 行、含控制性校验值，见同目录 README）。
--                              删的是表，不是历史——历史在归档文件里。
--                              归档落在**本地** logs/ 下（该目录在 .gitignore 里，不进开源仓库）；
--                              仓库里留的是脚本 scripts/export-sysdf-archive.ps1，谁都能照口径重导一份。
--
-- 同批**不动**的（各有理由，本脚本后置自证会盯着它们）：
--   SYSQR_DEFAULT 541 行 / SYSQR_USER 1152 行   报表条件的活真源，ReportRepository 直读；
--   SYS_WORK_TASK                               活模块 2308「工作任务记录」的主表。**本批暂留**，
--                                               但用户 2026-10-06 同日改判（"旧开发团队的工作模式，
--                                               我们不采用"）⇒ 迁移 327 连模块 2308 一起退役。
--                                               下面⑦的断言写的是**本脚本执行时点**的事实，仍成立。
--   AUDIT_EVENT                                 审计行按项目惯例保留（删它等于抹掉痕迹）。
--
-- 命名全大写；**非幂等**：模块或表不在即报错（与 320 / 322 同口径）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：两个模块必须在，且形态与预期一致（M_TAG / M_URL / 主副表 / 无子模块）
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
               WHERE M_IDX = 2205 AND M_TAG = 0 AND NULLIF(LTRIM(RTRIM(ISNULL(M_URL, N''))), N'') IS NULL
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'SYSQR_DA')
   OR NOT EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
                  WHERE M_IDX = 1399 AND M_TAG = 1 AND NULLIF(LTRIM(RTRIM(ISNULL(M_URL, N''))), N'') IS NULL
                    AND NULLIF(LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))), N'') IS NULL)
    THROW 53500, N'2205 / 1399 的形态与预期不符（页面、菜单可见性或主表已变）：先看清现状再退役。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK) WHERE M_P_IDX IN (2205, 1399))
    THROW 53501, N'2205 / 1399 下还挂着子模块：先处理子模块，再退役父模块。', 1;

-- ② 前置自证：两张表必须在，且行数与归档一致（归档与即将删的数据必须对得上）
IF OBJECT_ID(N'dbo.SYSQR_DA', N'U') IS NULL OR OBJECT_ID(N'dbo.SYSDF', N'U') IS NULL
    THROW 53502, N'SYSQR_DA / SYSDF 不齐：本迁移的前提不成立（库结构已变或已删过）。', 1;

-- 行数口径：**0（新建库，里面本来就没数据，删表不丢东西）或与归档逐数字相同**。
-- 这条守卫的用意是"绝不静默丢数据"：只要库里真有行，就必须恰好是被导出的那一批。
DECLARE @sysqrRows INT = (SELECT COUNT(*) FROM dbo.SYSQR_DA);
IF @sysqrRows NOT IN (0, 132)
    THROW 53503, N'SYSQR_DA 的行数既不是 0（新建库）也不是归档时的 132：先弄清多出来/少了的是什么，再退役。', 1;

DECLARE @sysdfRows INT = (SELECT COUNT(*) FROM dbo.SYSDF);
DECLARE @sysdfMaxDate VARCHAR(19) = CONVERT(varchar(19), (SELECT MAX(EXEC_DATE) FROM dbo.SYSDF), 120);
IF @sysdfRows <> 0 AND (@sysdfRows <> 11134 OR @sysdfMaxDate <> '2026-09-05 15:49:44')
    THROW 53504, N'SYSDF 有数据、但与归档（logs/archive/retire-324/SYSDF-*.csv：11134 行、最新 2026-09-05 15:49:44）不一致。先重跑 logs/archive/retire-324/export-sysdf.ps1 重新归档，再按新数字更新本脚本的期望值。', 1;

-- ③ 前置自证：不得被视图 / 过程 / 函数引用，也不得有外键
IF EXISTS (SELECT 1 FROM sys.sql_expression_dependencies
           WHERE referenced_id IN (OBJECT_ID(N'dbo.SYSQR_DA'), OBJECT_ID(N'dbo.SYSDF')))
    THROW 53505, N'SYSQR_DA / SYSDF 仍被视图或存储过程引用：先摘依赖，再删表。', 1;

IF EXISTS (SELECT 1 FROM sys.foreign_keys
           WHERE referenced_object_id IN (OBJECT_ID(N'dbo.SYSQR_DA'), OBJECT_ID(N'dbo.SYSDF'))
              OR parent_object_id IN (OBJECT_ID(N'dbo.SYSQR_DA'), OBJECT_ID(N'dbo.SYSDF')))
    THROW 53506, N'SYSQR_DA / SYSDF 上还有外键：先摘外键，再删表。', 1;

-- ④ 前置自证：除白名单外，任何挂着 M_IDX 的表里都不得有这两个模块的行。
--    动态扫全库（而不是写死几张表）：将来新增一张按模块存数据的表时，这条守卫自动覆盖到它。
--    白名单：MODULES（本次要删的行）、SYSDD/SYSDH（模块权限，随模块删）、
--            WORKBENCH_MODULE_DIRTY / WORKBENCH_IDEMPOTENCY（运行态中间表，随模块删）、
--            SYSDF（整表退役）、AUDIT_EVENT（审计痕迹，按惯例保留）。
DECLARE @stray NVARCHAR(MAX) = N'';
DECLARE @probe NVARCHAR(MAX), @tbl SYSNAME, @n INT;
DECLARE strayCur CURSOR LOCAL FAST_FORWARD FOR
    SELECT t.name FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id AND s.name = N'dbo'
    WHERE c.name = N'M_IDX'
      AND t.name NOT IN (N'MODULES', N'SYSDD', N'SYSDH', N'WORKBENCH_MODULE_DIRTY',
                         N'WORKBENCH_IDEMPOTENCY', N'SYSDF', N'AUDIT_EVENT');
OPEN strayCur;
FETCH NEXT FROM strayCur INTO @tbl;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @probe = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@tbl) + N' WHERE M_IDX IN (2205, 1399);';
    EXEC sp_executesql @probe, N'@n INT OUTPUT', @n OUTPUT;
    IF @n > 0 SET @stray = @stray + N'    dbo.' + @tbl + N'：' + CONVERT(nvarchar(20), @n) + N' 行' + CHAR(10);
    FETCH NEXT FROM strayCur INTO @tbl;
END
CLOSE strayCur; DEALLOCATE strayCur;

IF LEN(@stray) > 0
    THROW 53507, N'除白名单外还有表挂着 2205 / 1399 的行——先逐张解释清楚（该随模块删的加进白名单，该留的说明理由）：', 1;

-- ⑤ 留痕：删掉的是什么，进执行日志（表数据不在日志里，在归档文件里）。
--    PRINT 里不能嵌子查询（Msg 1046：只允许标量表达式），先落到变量再拼串。
DECLARE @sysdfMinDate VARCHAR(19) = CONVERT(varchar(19), (SELECT MIN(EXEC_DATE) FROM dbo.SYSDF), 120);
DECLARE @sysdfContentBytes BIGINT = ISNULL((SELECT SUM(DATALENGTH(CONTENT)) FROM dbo.SYSDF), 0);

PRINT N'== 退役死模块（决策清单 #144）=='
    + CHAR(10) + N'    2205 报表过滤条件设置（M_TAG=0，页面与端点早已下线）→ 主表 SYSQR_DA ' + CONVERT(nvarchar(20), @sysqrRows) + N' 行随之删表'
    + CHAR(10) + N'    1399 仓库综合报表（M_TAG=1 但无页面 ⇒ 点开落占位页）→ 无主表、无子模块'
    + CHAR(10) + N'== SYSDF 表数据留档 =='
    + CHAR(10) + N'    ' + CONVERT(nvarchar(20), @sysdfRows) + N' 行 / '
    + ISNULL(@sysdfMinDate, N'(空)') + N' ~ ' + ISNULL(@sysdfMaxDate, N'(空)')
    + N' / CONTENT 合计 ' + CONVERT(nvarchar(20), @sysdfContentBytes) + N' 字节'
    + CHAR(10) + N'    归档文件：logs/archive/retire-324/SYSDF-2007-11-14_to_2026-09-05.csv（见同目录 README 的校验值）';

-- ⑥ 删两个模块行与它们的连带行。审计行（AUDIT_EVENT）按惯例保留：删它等于抹掉痕迹。
DECLARE @permRows INT = 0, @dirtyRows INT = 0, @idemRows INT = 0;

DELETE FROM dbo.SYSDD WHERE M_IDX IN (2205, 1399);
SET @permRows = @permRows + @@ROWCOUNT;
DELETE FROM dbo.SYSDH WHERE M_IDX IN (2205, 1399);
SET @permRows = @permRows + @@ROWCOUNT;
DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX IN (2205, 1399);
SET @dirtyRows = @@ROWCOUNT;
DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX IN (2205, 1399);
SET @idemRows = @@ROWCOUNT;
DELETE FROM dbo.MODULES WHERE M_IDX IN (2205, 1399);

PRINT N'== 连带行清理（行数留痕）：模块权限 ' + CONVERT(nvarchar(10), @permRows)
    + N' 行；脏标记 ' + CONVERT(nvarchar(10), @dirtyRows)
    + N' 行；幂等键 ' + CONVERT(nvarchar(10), @idemRows) + N' 行 ==';

-- ⑦ 删 SYSQR_DA 的元数据挂件（描述一张即将不存在的表的行）。先留痕再删。
DECLARE @sysqrTrail NVARCHAR(MAX) = N'';
SELECT @sysqrTrail = @sysqrTrail + N'    FIELDS ' + CONVERT(nvarchar(10), COUNT(*)) + N' 行' + CHAR(10)
FROM dbo.FIELDS WHERE T_ID = N'SYSQR_DA';

DECLARE @qryDefaultRows INT = (SELECT COUNT(*) FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'SYSQR_DA');
DECLARE @qryFieldRows INT = (SELECT COUNT(*) FROM dbo.SYSQL_FIELDS WHERE T_ID = N'SYSQR_DA');
DECLARE @dsRows INT = (SELECT COUNT(*) FROM dbo.FIELD_DATASOURCE WHERE T_ID = N'SYSQR_DA');

PRINT N'== 随 SYSQR_DA 删除的元数据登记 =='
    + CHAR(10) + @sysqrTrail
    + N'    SYSQL_DEFAULT ' + CONVERT(nvarchar(10), @qryDefaultRows) + N' 行'
    + CHAR(10) + N'    SYSQL_FIELDS ' + CONVERT(nvarchar(10), @qryFieldRows) + N' 行'
    + CHAR(10) + N'    FIELD_DATASOURCE ' + CONVERT(nvarchar(10), @dsRows) + N' 行';

DELETE FROM dbo.FIELDS WHERE T_ID = N'SYSQR_DA';
DELETE FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'SYSQR_DA';
DELETE FROM dbo.SYSQL_FIELDS WHERE T_ID = N'SYSQR_DA';
DELETE FROM dbo.FIELD_DATASOURCE WHERE T_ID = N'SYSQR_DA';
DELETE FROM dbo.TABLES WHERE T_ID = N'SYSQR_DA';

DROP TABLE dbo.SYSQR_DA;

-- ⑧ 删 SYSDF 的元数据挂件与表本身（数据在 logs/archive/retire-324/ 的 CSV 里）
DELETE FROM dbo.FIELDS WHERE T_ID = N'SYSDF';
DELETE FROM dbo.TABLES WHERE T_ID = N'SYSDF';

DROP TABLE dbo.SYSDF;

-- ⑨ 后置自证：模块与表必须消失
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX IN (2205, 1399))
    THROW 53510, N'2205 / 1399 未被删除。', 1;

IF OBJECT_ID(N'dbo.SYSQR_DA', N'U') IS NOT NULL OR OBJECT_ID(N'dbo.SYSDF', N'U') IS NOT NULL
    THROW 53511, N'SYSQR_DA / SYSDF 未被删除。', 1;

IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID IN (N'SYSQR_DA', N'SYSDF'))
    OR EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT WHERE T_ID = N'SYSQR_DA')
    OR EXISTS (SELECT 1 FROM dbo.SYSQL_FIELDS WHERE T_ID = N'SYSQR_DA')
    OR EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE WHERE T_ID = N'SYSQR_DA')
    OR EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID IN (N'SYSQR_DA', N'SYSDF'))
    THROW 53512, N'仍留着 SYSQR_DA / SYSDF 的元数据登记（描述不存在的表会在字段维护页上变成坏条目）。', 1;

-- ⑩ 后置自证：不该被顺手删掉的东西必须还在，且报表条件真源一行不少。
--    注意 SYS_WORK_TASK 这一项：它断言的是"**本迁移**没顺手删它"。用户随后（同日）拍板要退
--    整个模块 2308，那是**迁移 327** 的事——两份脚本各管自己那一刻的库状态，先后有序，
--    不会因为 327 跑了就让 325 变成"断言失败"（325 早已落账、不会重跑）。
IF OBJECT_ID(N'dbo.SYSQR_DEFAULT', N'U') IS NULL
    OR OBJECT_ID(N'dbo.SYSQR_USER', N'U') IS NULL
    OR OBJECT_ID(N'dbo.SYS_WORK_TASK', N'U') IS NULL
    OR OBJECT_ID(N'dbo.SYSDH', N'U') IS NULL
    OR OBJECT_ID(N'dbo.SYSDD', N'U') IS NULL
    THROW 53513, N'不该删的表被删了：SYSQR_DEFAULT / SYSQR_USER 是报表条件的活真源，SYS_WORK_TASK 是活模块 2308 的主表。', 1;

IF (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT) <> 541
    THROW 53514, N'SYSQR_DEFAULT 的行数变了（应为 541）：报表条件真源不该被本次退役碰到。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT WHERE M_IDX IN (2205, 1399))
    OR EXISTS (SELECT 1 FROM dbo.SYSQR_USER WHERE M_IDX IN (2205, 1399))
    THROW 53515, N'报表条件/用户填值行仍指向已退役模块（悬空行）：先摘掉它们，再退役模块。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSDD WHERE M_IDX IN (2205, 1399))
    OR EXISTS (SELECT 1 FROM dbo.SYSDH WHERE M_IDX IN (2205, 1399))
    THROW 53516, N'模块权限行未被清理。', 1;

COMMIT TRANSACTION;

PRINT N'== 死模块与随行表退役完成：2205 / 1399 已从 MODULES 删除，SYSQR_DA / SYSDF 已删表（SYSDF 数据留档在 logs/archive/retire-324/）==';
