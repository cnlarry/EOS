-- ============================================================================
-- 「只能选入」字段的选择器没有回填本字段 —— 批量补齐与停用孤儿
--
-- 现象：字段标 `ONLY_CHOOSE=1`（不许键入，值只能由选择器带回），而它的启用数据源
-- `RETURN_ITEMS` 只映射了同组的伴生列（显示名/编码），不含本字段自己。选择器打得开、
-- 行也选得中、确认按钮可用，**确认后本字段仍然是空的**，界面上不报任何错。
-- 判据与处置口径见 LESSONS.md L108、scripts/check-chooser-self-mapping.ps1。
--
-- 本迁移做三件事：
--   ① 10 条补「本字段 ← 来源表主键列」的自回填（已存在的跳过，幂等）；
--   ② 停用 3 条不该存在的配置（见下方逐条理由）；
--   ③ 标脏受影响的 14 个模块（定义快照随发布更新）。
--
-- 来源主键列（以 sys.indexes 实测为准，均为 nchar(20)）：
--   BILLKIND.BILL_CODE / DEPOT.DEPOT_ID / HR_POLITY.POLITY_ID / UNIT.UNIT_ID / CURR.CURR_ID
--
-- 编号：本脚本原为 430_chooser_self_return_targets，与 430_retire_attendance_jobs_menu 撞号
-- （两个文件同号，执行顺序只能靠文件名后缀决定）。现按「编号唯一」收口为 445，**内容未改**：
-- 对已执行过 430 的库，这是一次幂等重放（① 按 NOT EXISTS 补缺，② 只把已停用的保持停用，
-- ③ 重新标脏 14 个模块——它们本就该随本次修复重发布）。
--
-- 末尾顺带删除台账里旧资源名的那一行：改名不改台账会留下「磁盘无、台账有」的幽灵名，
-- 迁移台账门禁 check-migration-journal.ps1 会判失败。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @fix TABLE (ID INT PRIMARY KEY, TargetColumn NVARCHAR(200), SourceColumn NVARCHAR(200));
INSERT INTO @fix (ID, TargetColumn, SourceColumn) VALUES
    (79,   N'MISSION_TYPE',          N'BILL_CODE'),   -- 1903 出车通知单：出车单别
    (419,  N'POLITY_ID',             N'POLITY_ID'),   -- 180102/180105/180110/180111：政治面貌
    (556,  N'DEPOT_ID',              N'DEPOT_ID'),    -- 130102 期初开帐单明细：入库别
    (678,  N'CHANGE_PRODUCE_TYPE',   N'BILL_CODE'),   -- 1509 生产单变更：订单别
    (741,  N'PRODUCT_OUT_TYPE',      N'BILL_CODE'),   -- 1515 返工单 / 2815 托外返工单：出库单别
    (1487, N'NOW_CURR_ID',           N'CURR_ID'),     -- 300301/300304 出口报关明细：实际币别
    (1566, N'UNIT_ID',               N'UNIT_ID'),     -- 1603 厂商询价单明细：单位
    (1567, N'CHAFFER_TYPE',          N'BILL_CODE'),   -- 1603 厂商询价单：询价单别
    (1654, N'CHANGE_PURCHASE_TYPE',  N'BILL_CODE'),   -- 1609 采购单变更：变更单别
    (1705, N'QUOTE_TYPE',            N'BILL_CODE');   -- 1604 厂商报价单：报价单别

-- 以下三条不补自回填，停用：
--   276 `CUS_PURCHASE_M.PURCHASE_TYPE`：该表没有任何工作台模块、库内 0 行，
--       数据源指向一张界面上不存在的表（孤儿配置）；
--   698 `MOC_PRODUCE_M.ORDER_TYPE` ← 厂商计价明细、700 同一字段 ← 产品基本资料：
--       两条的回填目标（LINE_ID/价格/产品列）与「制令来源」无关，是把别的字段的映射
--       复制到了这里；同一字段另有 697（客户订单明细，回填 ORDER_TYPE/ORDER_NO/序号）
--       与 699（采购申请，回填 APPLY_TYPE/APPLY_NO），业务通路已经完整。
--       `MOC_PRODUCE_M.ORDER_TYPE` 存量 20 行全为空，参考系统该字段由订单选择器带出。
UPDATE dbo.FIELD_DATASOURCE
   SET ACTIVE_TAG = 0,
       LAST_UPDATE_BY = N'EOS-MIG',
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE ID IN (276, 698, 700) AND ACTIVE_TAG = 1;

-- ① 追加自回填。`RETURN_ITEMS` 为 NULL/空时以空数组起步；`JSON_MODIFY ... append $`
--    要求目标是数组。已含本字段的行不动（幂等）。
UPDATE d
   SET RETURN_ITEMS = JSON_MODIFY(
           ISNULL(NULLIF(LTRIM(RTRIM(d.RETURN_ITEMS)), N''), N'[]'),
           'append $',
           JSON_QUERY((SELECT f.TargetColumn AS [target], f.SourceColumn AS [column]
                         FROM @fix f WHERE f.ID = d.ID
                        FOR JSON PATH, WITHOUT_ARRAY_WRAPPER))),
       LAST_UPDATE_BY = N'EOS-MIG',
       LAST_UPDATE_DATE = SYSDATETIME()
  FROM dbo.FIELD_DATASOURCE d
  JOIN @fix f ON f.ID = d.ID
 WHERE d.ACTIVE_TAG = 1
   AND NOT EXISTS (
        SELECT 1
          FROM OPENJSON(d.RETURN_ITEMS) WITH (t NVARCHAR(200) '$.target')
         WHERE UPPER(LTRIM(RTRIM(t))) = UPPER(f.TargetColumn));

-- 断言：10 条自回填必须真的落进 JSON（漏一条说明上面的 UPDATE 没覆盖到）
DECLARE @missing NVARCHAR(MAX) = (
    SELECT STRING_AGG(CONCAT(f.ID, '/', f.TargetColumn), N',')
      FROM @fix f
      JOIN dbo.FIELD_DATASOURCE d ON d.ID = f.ID
     WHERE NOT EXISTS (
            SELECT 1 FROM OPENJSON(d.RETURN_ITEMS) WITH (t NVARCHAR(200) '$.target')
             WHERE UPPER(LTRIM(RTRIM(t))) = UPPER(f.TargetColumn)));
IF @missing IS NOT NULL
BEGIN
    RAISERROR(N'[445] 以下数据源仍缺自回填，回滚：%s', 16, 1, @missing);
    ROLLBACK TRANSACTION;
    RETURN;
END

-- 断言：停用的三条不得再处于启用态
IF EXISTS (SELECT 1 FROM dbo.FIELD_DATASOURCE WHERE ID IN (276, 698, 700) AND ACTIVE_TAG = 1)
BEGIN
    RAISERROR(N'[445] 276/698/700 未全部停用，回滚', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

-- ③ 标脏：主表/明细表用到这些表的模块都要重发布
MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT M_IDX FROM (VALUES
        (1903), (180102), (180105), (180110), (180111), (130102), (1509),
        (1515), (2815), (300301), (300304), (1603), (1604), (1609)) v(M_IDX)) AS s
   ON t.M_IDX = s.M_IDX
WHEN MATCHED THEN
    UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'EOS-MIG', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (s.M_IDX, 1, N'EOS-MIG', SYSDATETIME());

PRINT N'[445] 已补 10 条自回填、停用 3 条无效数据源（276/698/700）、标脏 14 个模块。';

COMMIT;

-- 台账去幽灵名（幂等）：新库里没有这一行，DELETE 命中 0 行即结束。
DELETE FROM dbo.ERP_SCHEMA_JOURNAL
 WHERE [scriptname] = N'EOS.API.Data.Migrations.430_chooser_self_return_targets.sql';
PRINT N'[445] 旧台账行已清除：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行';
