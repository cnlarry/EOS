-- ============================================================================
-- EOS.ERP migration 141: 下线运行期按名调用点 P_UPDATE_PRO_MRP_ALL（可用量/MRP 重算已移植）
-- ----------------------------------------------------------------------------
-- 背景：该过程由两处按名调用 —— `InventoryMoveHandler.UpdateMrpAsync`（每次库存移动后，
-- 受 `SYSSS.PRO_MRP` 门控）与 `JobsController.MrpRecalc`（230901 作业）。现已改为受控 SQL 批
-- `MrpRecalcService.RecalcSql`（语句逐条对照过程本体），两处调用点均改为进程内执行。
-- **复核澄清（2026-09-18 实测）**：该过程**不写** `INV_PRO_DEPOT.USEABLE_QTY`（该列全库无写入方），
-- 它写的是 `PRODUCT` 的 `QTY/NOT_SEND_QTY/NOT_IN_QTY/NOT_GET_QTY/IN_BUY_QTY/MRP_QTY`；
-- 对库存表只有一次只读 `SUM(QTY) ... WHERE DEPOT_ID IN (SELECT … DEPOT.MRP=1) GROUP BY PRO_NO`，
-- 该聚合计法在四键扩键后仍然正确（多行相加），故 ADR-014 的 P0-03 依此重述。
-- 非钩子对象下线四判据（用户拍板方案 B）：三方核查零引用（库内依赖 / MODULES / REPORT_SORT /
-- 当前快照 / 代码与脚本文面量，调用点已随移植移除）、SSDT 快照可还原、本迁移内置守卫、已登记。
-- 幂等：不存在则跳过；末尾断言 dbo 过程总数为 92。
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

DECLARE @Target NVARCHAR(128) = N'P_UPDATE_PRO_MRP_ALL';

IF EXISTS (
    SELECT 1 FROM sys.sql_expression_dependencies d
    WHERE d.referenced_class = 1 AND d.referenced_entity_name = @Target
)
    THROW 50001, N'P_UPDATE_PRO_MRP_ALL 仍被库内对象调用，迁移中止。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.MODULES m
    WHERE ISNULL(m.UPDATE_SP, N'') + ISNULL(m.AFTERSAVE_SP, N'') LIKE N'%' + @Target + N'%'
)
    THROW 50002, N'P_UPDATE_PRO_MRP_ALL 仍被 MODULES 元数据引用，迁移中止。', 1;
IF EXISTS (SELECT 1 FROM dbo.REPORT_SORT s WHERE ISNULL(s.SORT_FIELDS, N'') LIKE N'%' + @Target + N'%')
    THROW 50003, N'P_UPDATE_PRO_MRP_ALL 仍被报表排序字段引用，迁移中止。', 1;
IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
    WHERE IS_CURRENT = 1 AND DEFINITION_JSON LIKE N'%P_UPDATE_PRO[_]%'
)
    AND EXISTS (
        SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
        WHERE IS_CURRENT = 1 AND DEFINITION_JSON LIKE N'%' + @Target + N'%'
    )
    THROW 50004, N'P_UPDATE_PRO_MRP_ALL 仍被已发布快照引用，迁移中止。', 1;

/* 移植侧的等价性证据：连同本迁移一起提交的真库用例
   `EOS.API.Tests/MrpRecalcLiveTests.cs` 在同一批数据上用 SAVEPOINT 对拍原过程与移植实现，
   比较 PRODUCT 全表八字段校验和与六个夹具产品的明细，逐位一致。 */
IF OBJECT_ID(N'dbo.P_UPDATE_PRO_MRP_ALL') IS NOT NULL
    EXEC sp_executesql N'DROP PROCEDURE dbo.P_UPDATE_PRO_MRP_ALL;';

IF OBJECT_ID(N'dbo.P_UPDATE_PRO_MRP_ALL') IS NOT NULL
    THROW 50005, N'P_UPDATE_PRO_MRP_ALL 未能下线，迁移中止。', 1;

DECLARE @Total INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo');
IF @Total <> 92
    THROW 50006, N'下线后 dbo 过程总数不是预期的 92 个，迁移中止（请复核引用清单）。', 1;

PRINT N'== P_UPDATE_PRO_MRP_ALL 已下线（可用量/MRP 重算改为受控 SQL 批）；dbo 过程合计 '
    + CAST(@Total AS NVARCHAR(10)) + N' 个 ==';
