-- ============================================================================
-- EOS.ERP migration 351: 自定义按钮（230103）的备注改写为面向使用者的说明
-- ----------------------------------------------------------------------------
-- 背景：MANUAL 行的 REMARK 曾在两个方向上跑偏——
--   ① 写成面向开发者的锚点（ADR 编号、WS 编号、表名与列名），实施人员与用户在界面上读不懂；
--   ② 只把模块名与按钮名重复一遍（"用户在制令单上点击：…"），没有增量信息。
-- 备注是**配置面的一部分**（与效果键的说明、参数 Schema 同性质），读者是配按钮的人，
-- 所以本脚本按「模块 + 处理器键」把它改成一句话说明：做什么、影响什么、有什么边界。
--
-- 只改 REMARK，不动 EFFECT_KEY / ENABLED / 确认位 / 条件与参数结构——按钮行为一行不变。
-- 幂等：文案以表变量为准做等值比较，已更新的行不再写；可重复执行。
-- 文案的取值依据：处理器实现对每张单据的实际动作（含它的前置条件与返回提示），
-- 而非处理器名的字面意思。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51940, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

BEGIN TRANSACTION;

DECLARE @remarks TABLE (
    M_IDX      INT            NOT NULL,
    EFFECT_KEY NVARCHAR(100)  NOT NULL,
    REMARK     NVARCHAR(500)  NOT NULL,
    PRIMARY KEY (M_IDX, EFFECT_KEY));

INSERT INTO @remarks (M_IDX, EFFECT_KEY, REMARK) VALUES
    (1302,    N'master-field-write',      N'修改本批次的人工字段（有效日期 / 批号日期 / 复检日期）。只改人工维护的列，库存数量与累计数不受影响。'),
    (1303,    N'inventory-freeze',        N'冻结这条库存（料号 / 库别 / 库位 / 批次）的可用量：可用量减少、在库数量不变。要填冻结数量，允许超过当前可用量。'),
    (1303,    N'inventory-unfreeze',      N'按先建先解释放这条库存的冻结占用：可用量增加、在库数量不变。要填解冻数量，不能超过该库存当前冻结总量。'),
    (1303,    N'inventory-reserve',       N'为指定的来源单据预留这条库存：可用量减少、在库数量不变。要填数量与来源单据号，来源结案或取消时自动释放。'),
    (1303,    N'inventory-release',       N'手工释放这条库存的预留占用：可用量增加、在库数量不变。要填释放数量，不能超过该库存当前预留总量。'),
    (1304,    N'month-close-snapshot',    N'按本单期末日期反算各料号的期末结存，写入快照明细（会覆盖原有明细）。已批核的单要先反结账才能重做。'),
    (1502,    N'produce-calc-materials',  N'按工单 BOM 重算用料：补齐或更新用料明细，并同步订单待办与申购应购量。已结案的不能算。'),
    (1502,    N'produce-gen-sub',         N'为需要下阶生产的子件生成子制令（已有子制令的跳过），并补齐本单的用料明细。'),
    (1503,    N'material-issue-allocate', N'按各行库别的当前库存重算可发料数量，并把待办表的待领量分配到明细行。'),
    (1512,    N'produce-calc-materials',  N'按工单 BOM 重算用料：补齐或更新用料明细，并同步订单待办与申购应购量。已结案的不能算。'),
    (1514,    N'material-issue-allocate', N'按各行库别的当前库存重算可发料数量，并把待办表的待领量分配到明细行。'),
    (1517,    N'material-issue-allocate', N'按各行库别的当前库存重算可发料数量，并把待办表的待领量分配到明细行。'),
    (1522,    N'produce-calc-materials',  N'按工单 BOM 重算用料：补齐或更新用料明细，并同步订单待办与申购应购量。已结案的不能算。'),
    (1606,    N'purchase-reprice',        N'按厂商计价重新取价：更新明细单价并重算金额与税额，再汇总到表头。没有计价的行保留原价、只重算金额。'),
    (2803,    N'produce-calc-materials',  N'按工单 BOM 重算用料：补齐或更新用料明细，并同步订单待办与申购应购量。已结案的不能算。'),
    (2804,    N'produce-calc-materials',  N'按工单 BOM 重算用料：补齐或更新用料明细，并同步订单待办与申购应购量。已结案的不能算。'),
    (2805,    N'material-issue-allocate', N'按各行库别的当前库存重算可发料数量，并把待办表的待领量分配到明细行。'),
    (2806,    N'material-issue-allocate', N'按各行库别的当前库存重算可发料数量，并把待办表的待领量分配到明细行。'),
    (130101,  N'recalc-account',          N'按当前库存重算已有明细行的账面数量；不增加、不删除明细行，也不动盘点数。'),
    (130101,  N'generate-adjustment',     N'把有盈亏的明细行生成一张库存调整单并过账；成功后本盘点单结案，不能再修改。'),
    (130101,  N'restock-scope',           N'按指定库区重做盘点明细：先清空本单原有明细，再按新库区重新生成（项次从 1 重排）。');

UPDATE a
   SET a.REMARK = r.REMARK,
       a.LAST_UPDATE_BY = N'351',
       a.LAST_UPDATE_DATE = GETDATE()
  FROM dbo.MODULE_BUSINESS_ACTION a
  JOIN @remarks r
    ON r.M_IDX = a.M_IDX
   AND r.EFFECT_KEY = a.EFFECT_KEY
 WHERE a.EVENT_CODE = N'MANUAL'
   AND ISNULL(a.REMARK, N'') <> r.REMARK;
PRINT N'== 自定义按钮备注已更新 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行 ==';

/* 收口断言一：清单里每条都真的落到了库内的按钮行上（模块或处理器键对不上即中止）。 */
IF EXISTS (
    SELECT 1
      FROM @remarks r
     WHERE NOT EXISTS (
            SELECT 1
              FROM dbo.MODULE_BUSINESS_ACTION a
             WHERE a.M_IDX = r.M_IDX
               AND a.EVENT_CODE = N'MANUAL'
               AND a.EFFECT_KEY = r.EFFECT_KEY
               AND a.REMARK = r.REMARK))
    THROW 51941, N'有自定义按钮的备注与清单不一致，迁移中止。', 1;

/* 收口断言二：不留下空备注，也不留下面向开发者的内部编号（这两类正是本次要清掉的东西）。 */
IF EXISTS (
    SELECT 1
      FROM dbo.MODULE_BUSINESS_ACTION
     WHERE EVENT_CODE = N'MANUAL'
       AND (REMARK IS NULL OR REMARK = N'' OR REMARK LIKE N'%ADR-%' OR REMARK LIKE N'%WS-%'))
    THROW 51942, N'仍有自定义按钮的备注为空或含内部编号，迁移中止。', 1;

COMMIT TRANSACTION;

DECLARE @manualCount INT = (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE EVENT_CODE = N'MANUAL');
PRINT N'== 收口：库内自定义按钮共 ' + CONVERT(NVARCHAR(10), @manualCount) + N' 个，备注均为面向使用者的说明 ==';
