-- ============================================================================
-- EOS.ERP migration 177: 按 DEPOT 全量补插库位哨兵行
-- ----------------------------------------------------------------------------
-- "未指定位置"必须是一条真实的位置行（LOCATION_NO = N'-'），而不是 NULL：
--   · 库存余额表的位置列要能保持外键完整；
--   · "待归位"因此成为一张可查询、可盘点、可逐步清理的清单；
--   · 存量库存全部落在哨兵行上，启用位置管理前数量层面与现状等价。
--
-- 哨兵行每个库别一条：LOCATION_PATH = N'/-'、LOCATION_TYPE = N'BIN'、STATUS = N'A'。
-- 它是系统预置行，不参与"库位父子规则"校验（该规则面向人工维护的位置）。
--
-- 位置号 N'-' 保留给哨兵：库位编码规则中禁止把单独一个 - 作为合法位置号。
--
-- 幂等：已存在即跳过，可重复执行。
-- 回滚：DELETE ... WHERE LOCATION_NO = N'-'（库存余额仍在哨兵行时不可回滚）。
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
    THROW 50600, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.DEPOT_LOCATION', N'U') IS NULL
    THROW 50601, N'dbo.DEPOT_LOCATION 不存在，请先执行建表迁移，迁移中止。', 1;

IF OBJECT_ID(N'dbo.DEPOT', N'U') IS NULL
    THROW 50602, N'dbo.DEPOT 不存在，迁移中止。', 1;

/* ---------- 补插：每个库别一条哨兵行 ---------- */
INSERT INTO dbo.DEPOT_LOCATION
    (DEPOT_ID, LOCATION_NO, PARENT_NO, LOCATION_PATH, LOCATION_TYPE, LOCATION_NAME, STORAGE_TYPE, SEQ_NO, STATUS)
SELECT d.DEPOT_ID, N'-', NULL, N'/-', N'BIN', N'未指定位置（待归位）', NULL, 0, N'A'
FROM dbo.DEPOT d
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.DEPOT_LOCATION l
    WHERE l.DEPOT_ID = d.DEPOT_ID AND l.LOCATION_NO = N'-');

PRINT N'== 哨兵行补插完成，共 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条 ==';

/* ---------- 收口断言 ---------- */
DECLARE @depots INT = (SELECT COUNT(*) FROM dbo.DEPOT);
DECLARE @sentinels INT = (SELECT COUNT(*) FROM dbo.DEPOT_LOCATION WHERE LOCATION_NO = N'-');

IF @sentinels <> @depots
    THROW 50603, N'哨兵行数与库别数不一致，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM dbo.DEPOT_LOCATION
    WHERE LOCATION_NO = N'-'
      AND (LOCATION_PATH <> N'/-' OR LOCATION_TYPE <> N'BIN' OR STATUS <> N'A'
           OR PARENT_NO IS NOT NULL OR LOCATION_NAME <> N'未指定位置（待归位）'))
    THROW 50604, N'哨兵行的路径 / 类型 / 状态 / 上级 / 名称不符合预置口径，迁移中止。', 1;

/* 每个库别恰好一条哨兵行 */
IF EXISTS (
    SELECT DEPOT_ID FROM dbo.DEPOT_LOCATION WHERE LOCATION_NO = N'-'
    GROUP BY DEPOT_ID HAVING COUNT(*) <> 1)
    THROW 50605, N'存在库别有多条哨兵行，迁移中止。', 1;

PRINT N'== 收口：哨兵行 ' + CONVERT(NVARCHAR(10), @sentinels) + N' 条 = 库别数 ' + CONVERT(NVARCHAR(10), @depots) + N' 条 ==';
