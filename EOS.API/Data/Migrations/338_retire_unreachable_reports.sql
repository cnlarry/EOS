-- ============================================================================
-- EOS.ERP migration 338: 退役三张"打不开"的报表
--                         （SFC_Process / SFC_Daily / CUS_ProIn）
-- ----------------------------------------------------------------------------
-- 背景：ADR-024 收尾清单里的"6 张不可达报表"，这 3 张的归属模块
--       （28 托外管理 / 1804 计件工资 / 3003 关封及报关单）都是**没有承载页的目录模块**：
--       模块没有主表，报表本身也没有登记汇总数据源 ⇒ 按 ADR-024 的可达性判据
--       （归属模块有主表 **或** 已登记汇总数据源）必然解析失败，实测端点 HTTP 404。
--
-- 决策（用户 2026-10-08 拍板）：**不再按旧系统语义实现，直接退役**。
--       同批另三张的处置：`SYS_Modules_List` / `SYS_Talbles_List` 已按汇总数据源登记（迁移见
--       `ReportAggregateRegistry`），`CAR_SUMMARY` 保持专属页面（`EXEC dbo.RPT_CAR_SUMMARY`）不重复登记。
--
-- 做法：照迁移 329 的范式——一个事务、前后自证、按 `REPORT_ID` **显式点名**（不用 LIKE/M_IDX 推导，
--       避免误伤同模块的其它报表），级联清 5 张引用表后再删 `REPORT` 本体。
--       引用表清单与 `ReportAdminRepository.DeleteReportAsync` 的级联清单**保持一致**；
--       漏清任何一张都会撞 `ReportIdStandardLiveTests` 的棘轮断言
--       （「引用报表编号的表里不得新增不存在的编号」——它对这 5 张表逐个断言孤儿数不得超过登记值）。
--
-- 不做：不动归属模块（28 / 1804 / 3003 本身是有用的目录节点，只是没有承载页），也不动 db/bootstrap。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @Targets TABLE (REPORT_ID NCHAR(50) NOT NULL PRIMARY KEY);
INSERT INTO @Targets (REPORT_ID) VALUES (N'SFC_Process'), (N'SFC_Daily'), (N'CUS_ProIn');

-- ① 前置自证：三张报表都得在（重复执行即中止，避免"静默成功"掩盖误删）
IF (SELECT COUNT(*) FROM dbo.REPORT WITH (NOLOCK)
    WHERE LTRIM(RTRIM(REPORT_ID)) IN (SELECT LTRIM(RTRIM(REPORT_ID)) FROM @Targets)) <> 3
    THROW 53801, N'前置不成立：待退役的三张报表不是 3 行（库已变，先复核再跑）。', 1;

-- 前置：这三张报表的归属模块必须确实没有主表（口径依据，防止"删了其实能用的报表"）
IF EXISTS (
        SELECT 1
        FROM dbo.REPORT r WITH (NOLOCK)
        JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = r.M_IDX
        WHERE LTRIM(RTRIM(r.REPORT_ID)) IN (SELECT LTRIM(RTRIM(REPORT_ID)) FROM @Targets)
          AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) <> N'')
    THROW 53802, N'前置不成立：待退役报表的归属模块现在有主表了——它们可能已经可达，先复核再删。', 1;

DECLARE @ReportRowsBefore INT = (SELECT COUNT(*) FROM dbo.REPORT WITH (NOLOCK));
DECLARE @SortRows INT = (SELECT COUNT(*) FROM dbo.REPORT_SORT WITH (NOLOCK) WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets));
DECLARE @InboxRows INT = (SELECT COUNT(*) FROM dbo.REPORT_INBOX WITH (NOLOCK) WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets));
DECLARE @SubRows INT = (SELECT COUNT(*) FROM dbo.REPORT_SUBSCRIPTION WITH (NOLOCK) WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets));
DECLARE @StateRows INT = (SELECT COUNT(*) FROM dbo.REPORT_USER_STATE WITH (NOLOCK) WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets));
DECLARE @CondRows INT = (SELECT COUNT(*) FROM dbo.SYSQR WITH (NOLOCK) WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets));

BEGIN TRANSACTION;

-- ② 级联：先清引用表（顺序与 ReportAdminRepository.DeleteReportAsync 一致）
DELETE FROM dbo.REPORT_SORT         WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets);
DELETE FROM dbo.REPORT_INBOX        WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets);
DELETE FROM dbo.REPORT_SUBSCRIPTION WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets);
DELETE FROM dbo.REPORT_USER_STATE   WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets);
DELETE FROM dbo.SYSQR               WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets);

-- ③ 再删本体
DELETE FROM dbo.REPORT WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets);

COMMIT TRANSACTION;

-- ④ 后置自证
IF EXISTS (SELECT 1 FROM dbo.REPORT WITH (NOLOCK)
           WHERE LTRIM(RTRIM(REPORT_ID)) IN (SELECT LTRIM(RTRIM(REPORT_ID)) FROM @Targets))
    THROW 53810, N'后置失败：REPORT 里仍有待退役的报表编号。', 1;
IF (SELECT COUNT(*) FROM dbo.REPORT WITH (NOLOCK)) <> @ReportRowsBefore - 3
    THROW 53811, N'后置失败：REPORT 减少的行数不是 3。', 1;
IF EXISTS (SELECT 1 FROM dbo.REPORT_SORT WITH (NOLOCK)         WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets))
    THROW 53812, N'后置失败：REPORT_SORT 里还留着孤儿行。', 1;
IF EXISTS (SELECT 1 FROM dbo.REPORT_INBOX WITH (NOLOCK)        WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets))
    THROW 53813, N'后置失败：REPORT_INBOX 里还留着孤儿行。', 1;
IF EXISTS (SELECT 1 FROM dbo.REPORT_SUBSCRIPTION WITH (NOLOCK) WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets))
    THROW 53814, N'后置失败：REPORT_SUBSCRIPTION 里还留着孤儿行。', 1;
IF EXISTS (SELECT 1 FROM dbo.REPORT_USER_STATE WITH (NOLOCK)   WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets))
    THROW 53815, N'后置失败：REPORT_USER_STATE 里还留着孤儿行。', 1;
IF EXISTS (SELECT 1 FROM dbo.SYSQR WITH (NOLOCK)               WHERE REPORT_ID IN (SELECT REPORT_ID FROM @Targets))
    THROW 53816, N'后置失败：SYSQR 里还留着孤儿行。', 1;

-- PRINT 里不能嵌子查询（只允许标量表达式）——迁移 337 刚踩过同一个坑，这里同样先落变量再拼串。
DECLARE @ReportRowsAfter INT = (SELECT COUNT(*) FROM dbo.REPORT WITH (NOLOCK));

PRINT N'[] 报表退役完成：SFC_Process / SFC_Daily / CUS_ProIn 三张'
    + N'（归属模块 28 / 1804 / 3003，均为无承载页的目录模块，端点长期 404）。'
    + N' REPORT 行数 ' + CONVERT(NVARCHAR(10), @ReportRowsBefore) + N' → '
    + CONVERT(NVARCHAR(10), @ReportRowsAfter) + N'；'
    + N'级联清理 REPORT_SORT=' + CONVERT(NVARCHAR(10), @SortRows)
    + N' / REPORT_INBOX=' + CONVERT(NVARCHAR(10), @InboxRows)
    + N' / REPORT_SUBSCRIPTION=' + CONVERT(NVARCHAR(10), @SubRows)
    + N' / REPORT_USER_STATE=' + CONVERT(NVARCHAR(10), @StateRows)
    + N' / SYSQR=' + CONVERT(NVARCHAR(10), @CondRows) + N' 行。';
