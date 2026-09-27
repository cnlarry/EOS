-- ============================================================================
-- EOS.ERP migration 146: 下线最后一个报表过程 —— 库存日报 `P_RPT_INV_PRO_DEPOT_1`
-- ----------------------------------------------------------------------------
-- 背景：该过程同时服务 3 个报表变体（`INV_Pro_Depot_1` / `_1_H` / `_1_sum`——同参数、同输出列，
-- 差异只在旧打印模板），已移植为受控聚合数据源（`ReportAggregateRegistry` 的 `InventoryDaily`）。
-- 本迁移做两件事：
--   ① 把 `REPORT_SORT.SORT_FIELDS` 的 `{P_RPT_INV_PRO_DEPOT_1.PRO_NO}` 改写为**纯列名**；
--   ② DROP 该过程。
-- 移植口径见，**已登记的有意差异**：
--   · 期初加权平均——既有实现 `@price_sum=(@price_sum*@qty_sum+@price*@qty)/(@qty_sum+@qty)` 受
--     "同一 SELECT 内变量赋值立即生效"影响，实际分母为 `Q_old+2q`；移植按加权平均。
--   · 每日发出成本——既有实现的游标首行（排序第一对的期初行）未进入累计，且把当日发出再从累计扣一次；
--     移植按当日累计加权平均（与 `INV_PRO_DEPOT.COST_PRICE` 维护口径一致）。
--   · 范围条件空上界——旧的 `char(255)` 哨兵在 `Chinese_PRC_CI_AS` 下会静默截断上界；移植改为"空值即无界"。
--   保留的既有行为：`@jc1` 无效（既有实现整段注释）、料件类别硬编码 `PRO_TYPE='3'`。
-- 真库 A/B（`InventoryReportPortLiveTests`）：结构列逐字一致、单价列按上述登记差异显式断言。
-- 非钩子对象下线四判据：三方核查零引用 / SSDT 快照可还原 / 本迁移内置守卫且失败即 THROW / 登记于覆盖率报告 §八。
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

DECLARE @Target NVARCHAR(128) = N'P_RPT_INV_PRO_DEPOT_1';

/* ---------- 守卫（一）：待下线对象存在 ---------- */
IF OBJECT_ID(N'dbo.' + @Target, N'P') IS NULL
    THROW 50201, N'待下线的库存日报过程不存在，迁移中止（可能已下线或名称变更）。', 1;

/* ---------- 守卫（二）：集合外库内对象不得引用 ---------- */
IF EXISTS (
    SELECT 1 FROM sys.sql_expression_dependencies d
    WHERE d.referenced_entity_name = @Target
      AND d.referenced_class = 1
      AND ISNULL(d.referenced_schema_name, N'dbo') = N'dbo'
      AND d.referencing_id <> OBJECT_ID(N'dbo.' + @Target)
)
    THROW 50202, N'待下线报表过程仍被其他库内对象引用，迁移中止。', 1;

/* ---------- 守卫（三）：运行期元数据不得引用 ---------- */
IF EXISTS (SELECT 1 FROM dbo.MODULES m WHERE m.UPDATE_SP = @Target OR m.AFTERSAVE_SP = @Target)
    THROW 50203, N'待下线报表过程仍被 MODULES 钩子字段引用，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
    WHERE IS_CURRENT = 1
      AND (JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.AfterSaveSproc') IS NOT NULL
           OR JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.WorkflowSproc') IS NOT NULL)
)
    THROW 50204, N'仍有当前快照登记了旧保存后 / 批核过程，迁移中止。', 1;

/* ---------- 改写报表排序引用：`{过程.列}` → `列` ---------- */
UPDATE s
   SET s.SORT_FIELDS = LTRIM(RTRIM(REPLACE(REPLACE(s.SORT_FIELDS, N'{' + @Target + N'.', N''), N'}', N'')))
  FROM dbo.REPORT_SORT s
  WHERE ISNULL(s.SORT_FIELDS, N'') LIKE N'%' + @Target + N'%';

IF EXISTS (SELECT 1 FROM dbo.REPORT_SORT s WHERE ISNULL(s.SORT_FIELDS, N'') LIKE N'%' + @Target + N'%')
    THROW 50205, N'报表排序字段仍引用待下线过程（改写未生效），迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.REPORT_SORT
    WHERE REPORT_ID IN (N'INV_Pro_Depot_1', N'INV_Pro_Depot_1_H', N'INV_Pro_Depot_1_sum')
      AND (ISNULL(SORT_FIELDS, N'') LIKE N'%{%' OR ISNULL(SORT_FIELDS, N'') LIKE N'%}%')
)
    THROW 50206, N'库存日报各变体的排序字段仍有花括号引用（须为纯列名），迁移中止。', 1;

/* ---------- 下线 ---------- */
DECLARE @statement NVARCHAR(300) = N'DROP PROCEDURE dbo.' + QUOTENAME(@Target) + N';';
EXEC sp_executesql @statement;

/* ---------- 收口断言 ---------- */
IF OBJECT_ID(N'dbo.' + @Target, N'P') IS NOT NULL
    THROW 50207, N'库存日报过程仍存在于库内，迁移中止。', 1;

DECLARE @Total INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo');
DECLARE @Reports INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo' AND name LIKE N'P_RPT[_]%');
DECLARE @Wage INT = (SELECT COUNT(*) FROM sys.objects WHERE type = 'P' AND SCHEMA_NAME(schema_id) = 'dbo' AND name = N'P_HRM_WAGE_CALC');

IF @Total <> 1 OR @Reports <> 0 OR @Wage <> 1
    THROW 50208, N'下线后过程构成不是预期的（总数 1 / 报表族 0 / 工资引擎 1），迁移中止（请复核引用清单）。', 1;

PRINT N'== 库存日报过程已下线 1 个；dbo 过程合计 ' + CAST(@Total AS NVARCHAR(10))
    + N' 个（报表族 ' + CAST(@Reports AS NVARCHAR(10))
    + N' / 工资引擎 ' + CAST(@Wage AS NVARCHAR(10)) + N'）==';
