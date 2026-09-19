-- ============================================================================
-- EOS.ERP migration 201: 库存策略关闭"自动批核"
-- ----------------------------------------------------------------------------
-- 现象：模块 110310（库存策略，主表 DEPOT_STOCK_POLICY）不在统一表单白名单内，
-- 但 `/document-workbench/110310/approve`、`/deapprove` 仍能打到服务端，返回
-- 400 `LIFECYCLE_COLUMN_MISSING`（"该模块启用自动批核，但主表 DEPOT_STOCK_POLICY
-- 缺少 CONFIRM_TAG 列"）；发布 dry-run 也因同一条能力判定被判 FAIL ⇒ 该模块发布不了。
--
-- 根因：`AUTO_APPROVE=1` 来自 189 号迁移"逐列从同域 110306 复制全部 bit 标志位"的批量修正。
-- 但 DEPOT_STOCK_POLICY 是**配置表**而非单据：它连生命周期列都没有（无 CONFIRM_TAG /
-- FINISHED_TAG），"自动批核"对它没有任何语义。
--
-- 处置：只关掉 110310 的 `AUTO_APPROVE`。**不补 CONFIRM_TAG 列**——补列等于把配置表
-- 伪装成单据，会让"保存即批核、批核后禁止编辑"那套语义落到策略行上，与
-- DepotStockPolicyService 的写入口径冲突。
--
-- 边界：本迁移只消除"能力声明与主表列不匹配"。模块的写路径另由代码闸门收口
-- （统一表单的批核/解批/结案与新增/修改/删除共用同一道闸门），策略行仍只能经
-- DepotStockPolicyService / `PUT /api/v1/admin/depot-stock-policy` 写入。
--
-- 幂等：按主键更新；重复执行不再命中 `ISNULL(AUTO_APPROVE,0)=1`。
-- 回滚：UPDATE dbo.MODULES SET AUTO_APPROVE = 1 WHERE M_IDX = 110310;（并重新标脏）
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
    THROW 52600, @GUARD_MESSAGE, 1;

DECLARE @PolicyModule INT = 110310;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @PolicyModule
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = N'DEPOT_STOCK_POLICY')
    THROW 52601, N'模块 110310 形态不符（应为 DEPOT_STOCK_POLICY），迁移中止。', 1;

-- 前提守卫：本迁移只在"配置表确实没有状态位"时成立。若将来有人给它补了 CONFIRM_TAG，
-- 说明该模块已被重新定位成单据，届时应当重新评估"自动批核"该不该关，而不是照跑本迁移。
IF COL_LENGTH('dbo.DEPOT_STOCK_POLICY', 'CONFIRM_TAG') IS NOT NULL
    THROW 52602, N'DEPOT_STOCK_POLICY 已具备 CONFIRM_TAG：模块定位可能已变更，请重新评估后再迁移。', 1;

UPDATE dbo.MODULES
   SET AUTO_APPROVE = 0, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @PolicyModule AND ISNULL(AUTO_APPROVE, 0) = 1;

MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @PolicyModule AS MODULE_ID) AS S ON T.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @PolicyModule AND ISNULL(AUTO_APPROVE, 0) <> 0)
    THROW 52603, N'模块 110310 仍处于自动批核，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = @PolicyModule AND DIRTY_TAG = 1)
    THROW 52604, N'模块 110310 未标记为待发布，迁移中止。', 1;

PRINT N'== 库存策略已关闭自动批核（配置表不再声明单据能力），模块待发布 ==';
