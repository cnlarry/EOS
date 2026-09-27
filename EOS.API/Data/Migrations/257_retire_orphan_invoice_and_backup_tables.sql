/* =============================================================================
 * 退役 5 个孤立库对象（用户 2026-09-26 拍板：能力缺口 5 项全部退役）。
 *
 *   第 1 组 4 张"发票"表（与海关域 301102/301103 加工费发票无关，那是另表且在跑）：
 *     INVOICE_IN_M / INVOICE_IN_D / INVOICE_OUT_M / INVOICE_OUT_D —— 0 行数据，
 *     全库无模块以它们为主表/明细表，仅剩 FIELDS/TABLES/FIELD_DATASOURCE 登记。
 *   第 2 组 1 张库存留底表：
 *     INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP —— 68 行，全部 DEPOT_ID='YL'（清理
 *     "孤立库别"时的留底，均为 0 数量的历史行）；无 FIELDS/数据源/依赖登记。
 *
 * 退役前已做三方引用核查（证据在 logs/c5-retire/）：
 *   ① 库内依赖：sys.sql_expression_dependencies / sys.foreign_keys / 视图过程函数触发器按名扫描 —— 全 0；
 *   ② 运行时配置：配置类表全文本列扫描（含 WORKBENCH_DEFINITION_SNAPSHOT 当前与历史版本、
 *      REPORT_*、SYSQR_*、MODULE_*、BILL_NO_SEQUENCE）—— 仅命中本迁移要清的登记行本身，无外部引用；
 *   ③ 代码与脚本：EOS.API / EOS.API.Tests / scripts 按名扫描 —— 仅 scripts/check-capability-ledger.ps1
 *      的台帐登记（本批同步移除），无运行期引用。
 *   68 行已留档到 logs/c5-retire/orphan-backup-rows.json（行数断言 68，不符即中止）。
 *
 * 自证：前置校验对象存在与行数/登记数符合预期，后置校验对象与登记行都已消失。
 * 任一不符即 THROW，整个迁移回滚。
 * ============================================================================= */

SET NOCOUNT ON;

DECLARE @missing nvarchar(400) = N'';
SELECT @missing = @missing + t.name + N';'
  FROM (VALUES (N'INVOICE_IN_M'), (N'INVOICE_IN_D'), (N'INVOICE_OUT_M'), (N'INVOICE_OUT_D'), (N'INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP')) v(name)
  LEFT JOIN sys.tables t ON t.name = v.name
 WHERE t.object_id IS NULL;
IF @missing <> N''
    THROW 50001, N'前置校验失败：待退役对象不存在（可能是上一轮已退役）——请核对后调整迁移。', 1;

DECLARE @badRows nvarchar(400) = N'';
SELECT @badRows = @badRows + RTRIM(e.name) + N'=' + CONVERT(nvarchar(20), x.rows) + N';'
  FROM (VALUES (N'INVOICE_IN_M', 0), (N'INVOICE_IN_D', 0), (N'INVOICE_OUT_M', 0), (N'INVOICE_OUT_D', 0), (N'INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP', 68)) e(name, rows)
  JOIN sys.partitions p ON p.object_id = OBJECT_ID(N'dbo.' + e.name) AND p.index_id IN (0, 1)
  CROSS APPLY (SELECT CONVERT(int, p.rows) AS rows) x
 WHERE x.rows <> e.rows;
IF @badRows <> N''
    THROW 50002, N'前置校验失败：对象行数与预期不符（INVOICE_* 应为 0，留底表应为 68）——请先查清数据来源，不要直接删。', 1;

DECLARE @fields int = (SELECT COUNT(*) FROM dbo.FIELDS WHERE T_ID IN (N'INVOICE_IN_M', N'INVOICE_IN_D', N'INVOICE_OUT_M', N'INVOICE_OUT_D'));
DECLARE @sources int = (SELECT COUNT(*) FROM dbo.FIELD_DATASOURCE WHERE T_ID LIKE N'INVOICE%');
DECLARE @tables int = (SELECT COUNT(*) FROM dbo.TABLES WHERE T_ID IN (N'INVOICE_IN_M', N'INVOICE_IN_D', N'INVOICE_OUT_M', N'INVOICE_OUT_D'));
IF @fields <> 70 OR @sources <> 5 OR @tables <> 4
    THROW 50003, N'前置校验失败：登记行数与核查时的实测不符（FIELDS 70 / FIELD_DATASOURCE 5 / TABLES 4）——可能有别的会话动过元数据，请重新核查。', 1;

-- 元数据先清（只清这 4 张表的登记，不按名字前缀误伤）
DELETE FROM dbo.FIELD_DATASOURCE WHERE T_ID IN (N'INVOICE_IN_M', N'INVOICE_IN_D', N'INVOICE_OUT_M', N'INVOICE_OUT_D');
DELETE FROM dbo.FIELDS           WHERE T_ID IN (N'INVOICE_IN_M', N'INVOICE_IN_D', N'INVOICE_OUT_M', N'INVOICE_OUT_D');
DELETE FROM dbo.TABLES           WHERE T_ID IN (N'INVOICE_IN_M', N'INVOICE_IN_D', N'INVOICE_OUT_M', N'INVOICE_OUT_D');

-- 再删表（无外键、无依赖，明细先于主表）
DROP TABLE dbo.INVOICE_IN_D;
DROP TABLE dbo.INVOICE_IN_M;
DROP TABLE dbo.INVOICE_OUT_D;
DROP TABLE dbo.INVOICE_OUT_M;
DROP TABLE dbo.INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP;

-- 后置自证：对象与登记行都必须消失
DECLARE @left int = (SELECT COUNT(*) FROM sys.tables WHERE name IN (N'INVOICE_IN_M', N'INVOICE_IN_D', N'INVOICE_OUT_M', N'INVOICE_OUT_D', N'INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP'));
DECLARE @leftMeta int = (SELECT COUNT(*) FROM dbo.FIELDS WHERE T_ID IN (N'INVOICE_IN_M', N'INVOICE_IN_D', N'INVOICE_OUT_M', N'INVOICE_OUT_D'))
                      + (SELECT COUNT(*) FROM dbo.TABLES WHERE T_ID IN (N'INVOICE_IN_M', N'INVOICE_IN_D', N'INVOICE_OUT_M', N'INVOICE_OUT_D'))
                      + (SELECT COUNT(*) FROM dbo.FIELD_DATASOURCE WHERE T_ID LIKE N'INVOICE%');
IF @left <> 0 OR @leftMeta <> 0
    THROW 50004, N'后置校验失败：对象或登记行仍存在。', 1;

SELECT N'RETIRED|INVOICE_IN_M,INVOICE_IN_D,INVOICE_OUT_M,INVOICE_OUT_D,INV_PRO_DEPOT_ORPHAN_DEPOT_BACKUP' AS RESULT;
