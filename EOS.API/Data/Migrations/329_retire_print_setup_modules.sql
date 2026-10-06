-- ============================================================================
-- EOS.ERP migration 329: 退役 2202 页头设置 / 2203 页尾设置 / 2204 表尾设置
-- ----------------------------------------------------------------------------
-- 用户 2026-10-06 拍板（决策清单 #146）：这三个管理页照 2205 的路子整模块退役。
--
-- 为什么它们还留着：ADR-009 §11 早已裁定 2201–2205 五个报表管理页"随 P4 一并下线"，实现
-- 也确实把三张承载表删掉了（REPORT_HEADER / REPORT_FOOTER / REPORT_TAIL），但**模块行与
-- 元数据登记行留了下来**——于是库里出现"模块指向不存在的表"这种真故障（同一批的 2205 已在
-- 迁移 325 退役、2201 早已不在）。迁移 328 的悬空登记清扫正是被这 3 条绊住：它们不是垃圾行，
-- 是故障线索，328 刻意跳过并把它报了出来（`REPORT_HEADER ← 2202`、`REPORT_FOOTER ← 2203`、
-- `REPORT_TAIL ← 2204`）。本迁移把那三条线索连根收掉。
--
-- 取证（本迁移落地前实测）：
--   · 三个模块：M_TAG=0（不上侧栏）、无子模块、主表分别指向那三张已不存在的表；
--   · 挂件（按 M_IDX 动态扫全库）：MODULES 3 / REPORT 3（三个模块各自带一条报表定义）/
--     SYSDD 6 / SYSDH 3 / WORKBENCH_MODULE_DIRTY 3；
--   · 三条悬空登记的挂件：TABLES 3 / FIELDS 45 / FIELD_DATASOURCE 6 / SYSQL_DEFAULT 12 /
--     SYSQL_FIELDS 12。
--
-- 连带清掉的**代码侧死件**（同一个"整模块退役"动作的一部分，不在本 SQL 里、由同批提交携带）：
--   `EOS.API/Models/ReportModels.cs` 的 `PrintHeaderDraft` / `PrintTailDraft` / `PrintFooterDraft`
--   三个 record——它们注释里就写着"（2202/2204/2203）"，全仓零引用（grep 只命中定义处）。
--
-- 命名全大写；**非幂等**：模块不在即报错（与 320 / 322 / 325 / 327 同口径）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：三个模块都在，且形态与预期一致（主表 / 承载页 / M_TAG / 无子模块）
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
               WHERE M_IDX = 2202 AND M_TAG = 0
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'REPORT_HEADER'
                 AND LTRIM(RTRIM(ISNULL(M_URL, N''))) = N'/admin/print-setup/headers')
   OR NOT EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
                  WHERE M_IDX = 2203 AND M_TAG = 0
                    AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'REPORT_FOOTER'
                    AND LTRIM(RTRIM(ISNULL(M_URL, N''))) = N'/admin/print-setup/footers')
   OR NOT EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
                  WHERE M_IDX = 2204 AND M_TAG = 0
                    AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'REPORT_TAIL'
                    AND LTRIM(RTRIM(ISNULL(M_URL, N''))) = N'/admin/print-setup/tails')
    THROW 53900, N'2202 / 2203 / 2204 的形态与预期不符（主表、承载页或菜单可见性已变）：先看清现状再退役。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK) WHERE M_P_IDX IN (2202, 2203, 2204))
    THROW 53901, N'这三个模块下还挂着子模块：先处理子模块。', 1;

-- ② 前置自证：三条承载表登记**本来就指向不存在的表**（这正是要收掉的故障）；
--    且除这三个模块外没人再引用它们（清完模块，引用面即为空）。
IF EXISTS (SELECT 1 FROM dbo.TABLES WITH (NOLOCK)
           WHERE T_ID IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL')
             AND OBJECT_ID(N'dbo.' + T_ID) IS NOT NULL)
    THROW 53902, N'REPORT_HEADER / REPORT_FOOTER / REPORT_TAIL 里至少有一张表又存在了：先弄清是谁建的，再决定退役口径。', 1;

IF EXISTS (SELECT 1 FROM dbo.MODULES m WITH (NOLOCK)
           WHERE (LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL')
                  OR LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL'))
             AND m.M_IDX NOT IN (2202, 2203, 2204))
    THROW 53903, N'除 2202/2203/2204 外还有模块引用那三张不存在的表：本迁移只该收掉这三个模块，别的先解释清楚。', 1;

-- ③ 前置自证：除白名单外，任何挂 M_IDX 的表里都不得有这三个模块的行（动态扫全库）
DECLARE @stray NVARCHAR(MAX) = N'';
DECLARE @probe NVARCHAR(MAX), @tbl SYSNAME, @n INT;
DECLARE strayCur CURSOR LOCAL FAST_FORWARD FOR
    SELECT t.name FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    JOIN sys.schemas s ON s.schema_id = t.schema_id AND s.name = N'dbo'
    WHERE c.name = N'M_IDX'
      AND t.name NOT IN (N'MODULES', N'SYSDD', N'SYSDH', N'REPORT', N'MODULE_FORM_TAB', N'MODULE_FORM_LAYOUT',
                         N'MODULE_BUSINESS_ACTION', N'MODULE_VALIDATION_RULE', N'REPORT_INBOX', N'REPORT_SUBSCRIPTION',
                         N'WORKBENCH_DEFINITION_SNAPSHOT', N'WORKBENCH_MODULE_DIRTY', N'WORKBENCH_IDEMPOTENCY',
                         N'AUDIT_EVENT');
OPEN strayCur;
FETCH NEXT FROM strayCur INTO @tbl;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @probe = N'SELECT @n = COUNT(*) FROM dbo.' + QUOTENAME(@tbl) + N' WHERE M_IDX IN (2202, 2203, 2204);';
    EXEC sp_executesql @probe, N'@n INT OUTPUT', @n OUTPUT;
    IF @n > 0 SET @stray = @stray + N'    dbo.' + @tbl + N'：' + CONVERT(nvarchar(20), @n) + N' 行' + CHAR(10);
    FETCH NEXT FROM strayCur INTO @tbl;
END
CLOSE strayCur; DEALLOCATE strayCur;

IF LEN(@stray) > 0
    THROW 53904, N'除白名单外还有表挂着 2202 / 2203 / 2204 的行——先逐张解释清楚：', 1;

-- ④ 留痕：退役的是什么（含它们各自带的报表定义）
DECLARE @modulesTrail NVARCHAR(MAX) = N'';
SELECT @modulesTrail = @modulesTrail + N'    ' + CONVERT(nvarchar(10), M_IDX) + N' ' + ISNULL(M_DESC, N'')
                    + N'（M_URL=' + LTRIM(RTRIM(ISNULL(M_URL, N''))) + N'、主表 ' + LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) + N'）' + CHAR(10)
FROM dbo.MODULES WHERE M_IDX IN (2202, 2203, 2204) ORDER BY M_IDX;

DECLARE @reportsTrail NVARCHAR(MAX) = N'';
SELECT @reportsTrail = @reportsTrail + N'    模块 ' + CONVERT(nvarchar(10), M_IDX) + N' → '
                    + LTRIM(RTRIM(ISNULL(REPORT_ID, N''))) + N' ' + ISNULL(REPORT_NAME, N'') + CHAR(10)
FROM dbo.REPORT WHERE M_IDX IN (2202, 2203, 2204) ORDER BY M_IDX;

PRINT N'== 退役 2202 / 2203 / 2204（决策清单 #146：ADR-009 §11 五页下线的最后遗留）=='
    + CHAR(10) + @modulesTrail
    + N'    随模块退役的报表定义：' + CHAR(10) + ISNULL(NULLIF(@reportsTrail, N''), N'    （无）')
    + N'    同时收掉三条"模块指向不存在的表"的元数据登记：REPORT_HEADER / REPORT_FOOTER / REPORT_TAIL';

-- ⑤ 连带行清理（逐表留痕）
DECLARE @permRows INT = 0, @dirtyRows INT = 0, @reportRows INT = 0;

DELETE FROM dbo.SYSDD WHERE M_IDX IN (2202, 2203, 2204);
SET @permRows = @permRows + @@ROWCOUNT;
DELETE FROM dbo.SYSDH WHERE M_IDX IN (2202, 2203, 2204);
SET @permRows = @permRows + @@ROWCOUNT;
DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX IN (2202, 2203, 2204);
SET @dirtyRows = @@ROWCOUNT;
DELETE FROM dbo.WORKBENCH_IDEMPOTENCY WHERE M_IDX IN (2202, 2203, 2204);
SET @dirtyRows = @dirtyRows + @@ROWCOUNT;
DELETE FROM dbo.MODULE_FORM_TAB WHERE M_IDX IN (2202, 2203, 2204);
SET @dirtyRows = @dirtyRows + @@ROWCOUNT;
DELETE FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX IN (2202, 2203, 2204);
SET @dirtyRows = @dirtyRows + @@ROWCOUNT;
DELETE FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX IN (2202, 2203, 2204);
SET @dirtyRows = @dirtyRows + @@ROWCOUNT;
DELETE FROM dbo.MODULE_VALIDATION_RULE WHERE M_IDX IN (2202, 2203, 2204);
SET @dirtyRows = @dirtyRows + @@ROWCOUNT;

DELETE FROM dbo.REPORT WHERE M_IDX IN (2202, 2203, 2204);
SET @reportRows = @@ROWCOUNT;

DELETE FROM dbo.MODULES WHERE M_IDX IN (2202, 2203, 2204);

PRINT N'== 连带行清理（留痕）：权限 ' + CONVERT(nvarchar(10), @permRows)
    + N' 行；报表定义 ' + CONVERT(nvarchar(10), @reportRows)
    + N' 行；其它运行态/版式挂件 ' + CONVERT(nvarchar(10), @dirtyRows) + N' 行 ==';

-- ⑥ 收掉三条悬空登记及其全部挂件（模块已删，引用面为空）
DECLARE @regTrail NVARCHAR(MAX) = N'';
SELECT @regTrail = @regTrail + N'    ' + t.T_ID
                + N'：FIELDS ' + CONVERT(nvarchar(10), (SELECT COUNT(*) FROM dbo.FIELDS f WHERE LTRIM(RTRIM(f.T_ID)) = t.T_ID))
                + N' / 数据源 ' + CONVERT(nvarchar(10), (SELECT COUNT(*) FROM dbo.FIELD_DATASOURCE x WHERE LTRIM(RTRIM(x.T_ID)) = t.T_ID))
                + N' / 默认查询列 ' + CONVERT(nvarchar(10), (SELECT COUNT(*) FROM dbo.SYSQL_DEFAULT q WHERE LTRIM(RTRIM(q.T_ID)) = t.T_ID))
                + N' / 查询字段 ' + CONVERT(nvarchar(10), (SELECT COUNT(*) FROM dbo.SYSQL_FIELDS s WHERE LTRIM(RTRIM(s.T_ID)) = t.T_ID))
                + CHAR(10)
FROM dbo.TABLES t WHERE t.T_ID IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL');

PRINT N'== 随模块收掉的悬空登记（描述不存在的表，迁移 328 刻意留给人定夺）==' + CHAR(10) + ISNULL(NULLIF(@regTrail, N''), N'    （无）');

DELETE FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL');
DELETE FROM dbo.FIELD_DATASOURCE WHERE LTRIM(RTRIM(T_ID)) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL');
DELETE FROM dbo.SYSQL_DEFAULT WHERE LTRIM(RTRIM(T_ID)) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL');
DELETE FROM dbo.SYSQL_FIELDS WHERE LTRIM(RTRIM(T_ID)) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL');
DELETE FROM dbo.TABLES WHERE T_ID IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL');

-- ⑦ 后置自证：模块、报表定义、登记行都必须消失
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX IN (2202, 2203, 2204))
    THROW 53910, N'2202 / 2203 / 2204 未被删除。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT WHERE M_IDX IN (2202, 2203, 2204))
    THROW 53911, N'三个模块的报表定义未被清理。', 1;

IF EXISTS (SELECT 1 FROM dbo.TABLES WHERE T_ID IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL'))
    OR EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL'))
    OR EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE WHERE LTRIM(RTRIM(T_ID)) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL'))
    OR EXISTS (SELECT 1 FROM dbo.SYSQL_DEFAULT WHERE LTRIM(RTRIM(T_ID)) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL'))
    OR EXISTS (SELECT 1 FROM dbo.SYSQL_FIELDS WHERE LTRIM(RTRIM(T_ID)) IN (N'REPORT_HEADER', N'REPORT_FOOTER', N'REPORT_TAIL'))
    THROW 53912, N'三条悬空登记还有残留。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSDD WHERE M_IDX IN (2202, 2203, 2204))
    OR EXISTS (SELECT 1 FROM dbo.SYSDH WHERE M_IDX IN (2202, 2203, 2204))
    OR EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX IN (2202, 2203, 2204))
    THROW 53913, N'三个模块还有权限行或脏标记没清干净。', 1;

-- ⑧ 后置自证：库里不该再有"指向不存在对象"的元数据登记（迁移 328 建的不变量，本迁移把它补齐）
IF EXISTS (SELECT 1 FROM dbo.TABLES t WHERE OBJECT_ID(N'dbo.' + t.T_ID) IS NULL)
    THROW 53914, N'仍有描述不存在表的 TABLES 登记行：本迁移之后应当归零。', 1;

-- ⑨ 后置自证：不该被这次退役顺手动到的东西必须原样
IF (SELECT COUNT(*) FROM dbo.MODULES) <> 357
    THROW 53915, N'MODULES 行数不是 357（360 − 2202/2203/2204 三行）：说明本次退役多删或少删了模块。', 1;

IF (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT) <> 541
    THROW 53916, N'SYSQR_DEFAULT 行数变了（应为 541）：报表条件真源不该被本次退役碰到。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2301
               AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'MODULES')
    OR (SELECT COUNT(*) FROM dbo.MODULES WHERE LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'MODULES') <> 1
    THROW 53917, N'把 MODULES 当主表的模块（实测只有 2301 模块管理）变了：退役时误伤了别的模块。', 1;

COMMIT TRANSACTION;

PRINT N'== 退役完成：2202 页头设置 / 2203 页尾设置 / 2204 表尾设置 已整模块退役，三条悬空登记一并收掉 ==';
