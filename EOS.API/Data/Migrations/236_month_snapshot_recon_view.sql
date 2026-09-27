-- ============================================================================
-- EOS.ERP migration 237: 快照对账视图（快照 + 其后流水 vs 实时余额）
-- ----------------------------------------------------------------------------
--  P2-2：月结快照一旦生成，就必须能被**独立对出来**——快照与实时账的差异不能靠
-- "相信没人绕过引擎改库"，要有一处能指着名字说出来。本视图把
--   **最近一期已批核的月结快照** + **该期期末之后的流水净额** 与 **实时余额** 逐键比对，
-- 并且**只输出差异行**：无差异 ⇒ 0 行；账被绕过引擎动过 ⇒ 差异行点名到 (库别, 料号)。
--
-- 口径（每一处都是踩过的坑）：
--   ① 快照取"最近一期**已批核**"（`CONFIRM_TAG = 1`）。草稿不是关账、不参与任何口径——
--      与库存日报期初、关账拦截三处同一条规矩。
--   ② 快照明细**按维度细分**（库位 / 批次 / 制程），同一 (料号, 库别) 对应多行 ⇒ 必须 `SUM`。
--      只取其中一行会**静默少算**（记的正是这一处）。
--   ③ 余额表 `INV_PRO_DEPOT` 今天的粒度是 **(料号, 库别)**：它的 `LOCATION_NO` / `BATCH_NO`
--      是**预留列**（当前分别恒为哨兵 `-` 与空白，行数 = 键数）。故本视图把快照汇总到同一粒度。
--      将来余额表真按维度细分，本视图的粒度必须**跟着改**——两边粒度不一致时逐键比对没有意义。
--   ④ "其后流水"按**日期**比较（`CONVERT(date, ...)`）而不是时间：批核写的流水日期是单据日期、
--      解批写的是当前时间；期末当天 12:00 的流水仍属于该期，用时间比较会把它误算进下一期。
--   ⑤ 流水数量为 `NULL` 的行在本视图里按 **0** 计：对账算式不是 NULL 安全的（`SUM` 会静默忽略
--      这类行）。它们由 `scripts/check-inventory-balance-identity.ps1` 单独把关，本视图不兼管。
--   ⑥ 没有任何已批核快照时本视图**返回 0 行**——这与"对完无差异"同样是 0 行，两者含义完全不同，
--      所以调用方（门禁 / 报表）必须先分清"没有可比的对象"与"比过且一致"，不能把前者的 0 行读成通过。
-- ============================================================================

IF OBJECT_ID(N'dbo.V_INV_MONTH_SNAPSHOT_RECON', N'V') IS NOT NULL
    DROP VIEW dbo.V_INV_MONTH_SNAPSHOT_RECON;
GO

CREATE VIEW dbo.V_INV_MONTH_SNAPSHOT_RECON
AS
WITH latest AS
(
    SELECT TOP 1 m.MONTH_TYPE, m.MONTH_NO, m.MONTH_DATE
      FROM dbo.INV_PRO_MONTH_M m
     WHERE m.CONFIRM_TAG = 1
     ORDER BY m.MONTH_DATE DESC, m.MONTH_NO DESC
),
snapshot AS
(
    SELECT d.DEPOT_ID, d.PRO_NO, SUM(d.QTY) AS SNAP_QTY
      FROM dbo.INV_PRO_MONTH_D d
      JOIN latest l
        ON l.MONTH_TYPE = d.MONTH_TYPE
       AND l.MONTH_NO = d.MONTH_NO
     GROUP BY d.DEPOT_ID, d.PRO_NO
),
after_ledger AS
(
    SELECT g.DEPOT_ID, g.PRO_NO,
           SUM(CASE WHEN g.IN_OUT = 'I' THEN ISNULL(g.QTY, 0) ELSE -ISNULL(g.QTY, 0) END) AS AFTER_QTY
      FROM dbo.INV_DEPOT_LOG g
      CROSS JOIN latest l
     WHERE CONVERT(date, g.MUTUALITY_DATE) > CONVERT(date, l.MONTH_DATE)
     GROUP BY g.DEPOT_ID, g.PRO_NO
),
live AS
(
    SELECT p.DEPOT_ID, p.PRO_NO, SUM(p.QTY) AS LIVE_QTY
      FROM dbo.INV_PRO_DEPOT p
     GROUP BY p.DEPOT_ID, p.PRO_NO
),
joined AS
(
    SELECT COALESCE(s.DEPOT_ID, a.DEPOT_ID, v.DEPOT_ID) AS DEPOT_ID,
           COALESCE(s.PRO_NO, a.PRO_NO, v.PRO_NO)       AS PRO_NO,
           ISNULL(s.SNAP_QTY, 0)  AS SNAP_QTY,
           ISNULL(a.AFTER_QTY, 0) AS AFTER_LEDGER_QTY,
           ISNULL(v.LIVE_QTY, 0)  AS LIVE_QTY
      FROM snapshot s
      FULL OUTER JOIN after_ledger a
        ON a.DEPOT_ID = s.DEPOT_ID AND a.PRO_NO = s.PRO_NO
      FULL OUTER JOIN live v
        ON v.DEPOT_ID = COALESCE(s.DEPOT_ID, a.DEPOT_ID)
       AND v.PRO_NO   = COALESCE(s.PRO_NO, a.PRO_NO)
)
SELECT l.MONTH_TYPE        AS MONTH_TYPE,   -- 对的是哪一期（报错要点名）
       l.MONTH_NO          AS MONTH_NO,
       l.MONTH_DATE        AS MONTH_DATE,
       j.DEPOT_ID          AS DEPOT_ID,
       j.PRO_NO            AS PRO_NO,
       j.SNAP_QTY          AS SNAP_QTY,
       j.AFTER_LEDGER_QTY  AS AFTER_LEDGER_QTY,
       j.LIVE_QTY          AS LIVE_QTY,
       j.SNAP_QTY + j.AFTER_LEDGER_QTY - j.LIVE_QTY AS DIFF_QTY
  FROM joined j
  CROSS JOIN latest l
 WHERE ABS(j.SNAP_QTY + j.AFTER_LEDGER_QTY - j.LIVE_QTY) > 0.0001;
GO

PRINT N'== 就位：快照对账视图 dbo.V_INV_MONTH_SNAPSHOT_RECON（只输出差异行）==';
GO
