-- ============================================================================
-- EOS.ERP migration 467: 清理 2201 报表排序汇总设置的两类历史遗留资产
-- ----------------------------------------------------------------------------
-- 2201 从"报表设置"工作台模块改成定制页（/admin/report-setup）之后，有两批资产再没有消费方：
--
--   ① REPORT 表上 4 条字段登记指向**已被迁移 023 删除的物理列**：
--      DEFAULT_PAPER / DEFAULT_PRINTER / IMAGE_ID / REPORT_PATH。
--      它们是非虚拟字段（IS_VIRTUAL = 0），任何按 FIELDS 拼列名的读取路径（统一表单定义、
--      字段审计、工作台查询）碰到就是"列名无效"。订制页自带列清单，不读这些行。
--      连带 SYS_DEFAULT_COLUMN / SYS_USER_COLUMN 各 1 条 REPORT_PATH 列配置。
--
--   ② 2201 名下 3 张旧报表定义：REPORT / REPORT_229801 / SYS_Report_List。
--      2201 现在的职责就是"列出并维护报表"，这三张报表会让报表中心多出一个"报表设置"
--      业务域（内容是一张报表清单），与页面职责重复；它们继承自旧系统的报表设置主子表。
--      先例：迁移 338 退役 3 张不可达报表、迁移 329 退役三个模块各自的报表定义，
--      用的都是 `ReportAdminRepository.DeleteReportAsync` 的同一套引用表清单。
--
-- 取证（本迁移落地前实测，2026-11-01）：
--   · REPORT 的物理列里没有那 4 列；
--   · 3 张报表的引用行：REPORT_SORT 1 / REPORT_USER_STATE 1 / SYS_USER_REPORT_PRINT 2 /
--     REPORT_INBOX 0 / REPORT_SUBSCRIPTION 0；REPORT_FORM_LAYOUT 以 LAYOUT_ID 为键、不按报表号引用；
--   · 2201 上其它运行态均为 0 行（脏标记 / 幂等键 / 表单页签与版式 / 效果动作 / 校验规则）；
--   · 全库无"归属模块已不存在"的报表（迁移 329 已清）。
--
-- 幂等：全部 DELETE ... WHERE，重复执行结果不变（行已不在即 0 行）。
-- 收口注意：db/bootstrap/20_metadata.sql 里仍有这批种子行，随下一次
-- `scripts/export-bootstrap-metadata.ps1` 重导出即可（三件套同一批）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：那 4 列确实仍不在 REPORT 里。若有人把列加回来了，字段登记就不是垃圾，
--    本迁移要停下来由人判断（删登记会让真实字段从界面上消失）。
IF EXISTS (SELECT 1 FROM sys.columns c
           WHERE c.object_id = OBJECT_ID(N'dbo.REPORT')
             AND c.name IN (N'DEFAULT_PAPER', N'DEFAULT_PRINTER', N'IMAGE_ID', N'REPORT_PATH'))
    THROW 54700, N'REPORT 上又出现了 DEFAULT_PAPER / DEFAULT_PRINTER / IMAGE_ID / REPORT_PATH 中的列：字段登记不再是悬空行，先弄清是谁加的。', 1;

-- ② 前置自证：待退役的 3 张报表仍挂在 2201 名下且编号未变（编号是身份，被改过就说明有人动过）。
IF (SELECT COUNT(*) FROM dbo.REPORT WITH (NOLOCK)
    WHERE M_IDX = 2201
      AND RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List')) <> 3
    THROW 54701, N'2201 名下的旧报表定义与预期不符（应恰为 REPORT / REPORT_229801 / SYS_Report_List 三张）：先看清现状再退役。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT WITH (NOLOCK)
           WHERE M_IDX = 2201
             AND RTRIM(REPORT_ID) NOT IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List'))
    THROW 54702, N'2201 名下还有本迁移未登记的报表定义：新加的报表可能是在用的，先确认再决定是否一并退役。', 1;

-- ③ 留痕：删之前把要删的东西打印出来（DbUp 日志是唯一事后可查的现场）
DECLARE @fieldTrail NVARCHAR(MAX) = N'';
SELECT @fieldTrail = @fieldTrail + N'    REPORT.' + f.F_ID + N' ' + ISNULL(f.F_DESC, N'') + CHAR(10)
FROM dbo.FIELDS f
WHERE f.T_ID = N'REPORT'
  AND f.F_ID IN (N'DEFAULT_PAPER', N'DEFAULT_PRINTER', N'IMAGE_ID', N'REPORT_PATH')
ORDER BY f.F_ID;

DECLARE @reportTrail NVARCHAR(MAX) = N'';
SELECT @reportTrail = @reportTrail + N'    ' + RTRIM(REPORT_ID) + N' ' + ISNULL(REPORT_NAME, N'')
                    + N'（默认报表=' + CASE WHEN IS_DEFAULT = 1 THEN N'是' ELSE N'否' END + N'）' + CHAR(10)
FROM dbo.REPORT
WHERE M_IDX = 2201
ORDER BY REPORT_ID;

PRINT N'== 清理 2201 报表排序汇总设置的历史遗留资产 =='
    + CHAR(10) + N'    悬空字段登记（物理列已于迁移 023 删除）：' + CHAR(10) + ISNULL(NULLIF(@fieldTrail, N''), N'    （无）')
    + N'    退役的旧报表定义：' + CHAR(10) + ISNULL(NULLIF(@reportTrail, N''), N'    （无）');

-- ④ 字段登记与列配置（只清这 4 个别名，不动 REPORT 其余字段）
DELETE FROM dbo.FIELDS
WHERE T_ID = N'REPORT'
  AND IS_VIRTUAL = 0
  AND F_ID IN (N'DEFAULT_PAPER', N'DEFAULT_PRINTER', N'IMAGE_ID', N'REPORT_PATH');

DELETE FROM dbo.SYS_DEFAULT_COLUMN
WHERE T_ID = N'REPORT'
  AND F_ID IN (N'DEFAULT_PAPER', N'DEFAULT_PRINTER', N'IMAGE_ID', N'REPORT_PATH');

DELETE FROM dbo.SYS_USER_COLUMN
WHERE T_ID = N'REPORT'
  AND F_ID IN (N'DEFAULT_PAPER', N'DEFAULT_PRINTER', N'IMAGE_ID', N'REPORT_PATH');

-- ⑤ 报表定义：先按 DeleteReportAsync 的同一套引用表清单级联清引用行，再删本体。
--    这些表之间没有外键（纯逻辑引用），清漏了就是"永远取不到的孤儿行"：不报错也不生效。
DELETE FROM dbo.REPORT_SORT
WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List');

DELETE FROM dbo.REPORT_INBOX
WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List');

DELETE FROM dbo.REPORT_SUBSCRIPTION
WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List');

DELETE FROM dbo.REPORT_USER_STATE
WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List');

DELETE FROM dbo.SYS_USER_REPORT_PRINT
WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List');

DELETE FROM dbo.REPORT
WHERE M_IDX = 2201
  AND RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List');

-- ⑥ 报表条件模板：2201 名下条件（条件随模块，M_IDX 指向归属模块）已没有报表消费它，
--    留着会在日后新建报表时以"默认条件"悄悄生效。用户填值一并清。
DELETE FROM dbo.SYS_USER_REPORT_PARAMETER WHERE M_IDX = 2201;
DELETE FROM dbo.SYS_REPORT_PARAMETER WHERE M_IDX = 2201;

-- ⑦ 后置自证：四件事都必须成立，否则回滚（XACT_ABORT 之上再显式兜一层）
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'REPORT'
             AND F_ID IN (N'DEFAULT_PAPER', N'DEFAULT_PRINTER', N'IMAGE_ID', N'REPORT_PATH')
           UNION ALL
           SELECT 1 FROM dbo.SYS_DEFAULT_COLUMN WHERE T_ID = N'REPORT'
             AND F_ID IN (N'DEFAULT_PAPER', N'DEFAULT_PRINTER', N'IMAGE_ID', N'REPORT_PATH')
           UNION ALL
           SELECT 1 FROM dbo.SYS_USER_COLUMN WHERE T_ID = N'REPORT'
             AND F_ID IN (N'DEFAULT_PAPER', N'DEFAULT_PRINTER', N'IMAGE_ID', N'REPORT_PATH'))
    THROW 54703, N'字段登记 / 列配置未清干净：请检查是否还有别处引用这 4 个别名。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT WHERE M_IDX = 2201)
    THROW 54704, N'2201 名下仍有报表定义：报表退役不完整。', 1;

IF EXISTS (SELECT 1 FROM dbo.REPORT_SORT WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List')
           UNION ALL SELECT 1 FROM dbo.REPORT_USER_STATE WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List')
           UNION ALL SELECT 1 FROM dbo.SYS_USER_REPORT_PRINT WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List')
           UNION ALL SELECT 1 FROM dbo.REPORT_INBOX WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List')
           UNION ALL SELECT 1 FROM dbo.REPORT_SUBSCRIPTION WHERE RTRIM(REPORT_ID) IN (N'REPORT', N'REPORT_229801', N'SYS_Report_List'))
    THROW 54705, N'引用表里仍有这 3 张报表的行：级联清理不完整。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYS_REPORT_PARAMETER WHERE M_IDX = 2201)
    THROW 54706, N'2201 名下仍有报表条件模板：条件退役不完整。', 1;

COMMIT TRANSACTION;

PRINT N'== 完成：2201 只保留定制页（/admin/report-setup）与其 REPORT/REPORT_SORT 单据面登记 ==';
