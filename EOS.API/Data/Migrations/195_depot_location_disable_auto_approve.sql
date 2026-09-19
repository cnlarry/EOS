-- ============================================================================
-- EOS.ERP migration 196: 库位主档关闭"自动批核"
-- ----------------------------------------------------------------------------
-- 现象：通过工作台新建库位后**无法再编辑**——保存被 `CONFIRMED_EDIT_FORBIDDEN`
-- （"记录已批核，禁止编辑（请先解批）"）挡下，于是"新增可用、修改不可用"。
--
-- 根因：110309 的 AUTO_APPROVE 为 1。该值来自 189 号迁移"逐列从同域 110306 复制全部 bit 标志位"
-- 的批量修正——那次修正的目的是补齐无默认值的空标志位，顺带把"自动批核"也带了过来。
-- 但库位主档是**维护型主档**，不是单据：主档"保存即批核"的结果就是**保存后被锁死**，
-- 与 CRUD 的基本预期冲突。
--
-- 处置：只关掉 110309 的 AUTO_APPROVE。CONFIRM_TAG 列保留——效果引擎接管的模块按发布门
-- 要求主表必须有状态位，关掉自动批核不影响该要求。
--
-- 注：110306 仓库资料（DEPOT）存在同样的取值，但它是有既有行为的模块，
-- 是否调整属另一个决策，本迁移不代劳。
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
    THROW 52300, @GUARD_MESSAGE, 1;

DECLARE @LocationModule INT = 110309;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @LocationModule
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, ''))) = N'DEPOT_LOCATION')
    THROW 52301, N'模块 110309 形态不符（应为 DEPOT_LOCATION），迁移中止。', 1;

-- 主表必须已有状态位，否则关掉自动批核会让发布门的能力判定与列不匹配
IF COL_LENGTH('dbo.DEPOT_LOCATION', 'CONFIRM_TAG') IS NULL
    THROW 52302, N'DEPOT_LOCATION 缺少 CONFIRM_TAG，迁移中止。', 1;

UPDATE dbo.MODULES
   SET AUTO_APPROVE = 0, LAST_UPDATE_BY = N'DbUp', LAST_UPDATE_DATE = SYSDATETIME()
 WHERE M_IDX = @LocationModule AND ISNULL(AUTO_APPROVE, 0) = 1;

MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
USING (SELECT @LocationModule AS MODULE_ID) AS S ON T.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ---------- */
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @LocationModule AND ISNULL(AUTO_APPROVE, 0) <> 0)
    THROW 52303, N'模块 110309 仍处于自动批核，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID = @LocationModule AND DIRTY_TAG = 1)
    THROW 52304, N'模块 110309 未标记为待发布，迁移中止。', 1;

PRINT N'== 库位主档已关闭自动批核（保存后不再锁死），模块待发布 ==';
