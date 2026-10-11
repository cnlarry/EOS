-- ============================================================================
-- EOS.ERP migration 346: 退役模块级效果引擎开关（MODULES.EFFECT_ENGINE_TAG）
-- ----------------------------------------------------------------------------
-- 背景（ADR-035 §2 第 4 条、§8 批 4）：引擎接管的判据已改为**推导**——"该定义里有启用且
-- 接在效果链上的动作"（WorkflowStates.HasEnabledEngineActions；MANUAL 自定义按钮不算，
-- 按钮通道不经过 EffectEngineInvoker.IsEnabledFor）。代码侧自批 3 起就不再读这一列，
-- 本迁移把它物理删除。
--
-- 为什么可以删（删列前的三条自证）：
--   ① 不存在"配了启用动作却分闸关着"的模块——否则删列会让它的效果链从"不跑"变成"跑"；
--   ② 4 个"分闸开着却零配置"的模块（1201/1401/1601/300303）的行为变化是有意的：
--      它们从"引擎接管（空转）"落到"无副作用批核"分支（ADR-035 §7）；
--   ③ 批核入口集合不依赖它——已由 APPROVE_ENABLED 承接（迁移 345 已置位 170 个）。
--
-- 快照侧：新发布不再写出 `effectEngine` 段，且内容比较器把它当"运行期不读的差异"归一掉
-- （ADR-034 第②项随本批落地）——故历史快照不必 UPDATE，也不会因此升版本。
--
-- 幂等：列/约束不存在即跳过；自证语句走动态 SQL（静态引用已删列会让整批编译失败，参见
-- guide/05 与迁移 321 的那条纪律）。DbUp 每脚本一个事务，断言失败即整支回滚。
-- ============================================================================
SET NOCOUNT ON;
SET QUOTED_IDENTIFIER ON;
GO

/* ---------- 1. 删列前自证 + 解依赖 + 删列 ---------- */
IF COL_LENGTH(N'dbo.MODULES', N'EFFECT_ENGINE_TAG') IS NULL
BEGIN
    PRINT N'== MODULES.EFFECT_ENGINE_TAG 不存在（重复执行），跳过 ==';
END
ELSE
BEGIN
    DECLARE @badSql NVARCHAR(MAX) = N'
        SELECT COUNT(*) FROM dbo.MODULES m
        WHERE ISNULL(m.EFFECT_ENGINE_TAG, 0) = 0
          AND EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                      WHERE a.M_IDX = m.M_IDX AND a.EVENT_CODE <> N''MANUAL'' AND ISNULL(a.ENABLED, 1) = 1);';
    DECLARE @bad INT;
    EXEC sp_executesql @badSql, N'@out INT OUTPUT', @out = @bad OUTPUT;
    IF @bad > 0
        THROW 54120, N'存在"配了启用动作却分闸关着"的模块：删列会让它们的效果链从"不跑"变成"跑"，请先查清（ADR-035 §8 批 4）。', 1;

    -- 本迁移依赖 345：批核入口集合由 APPROVE_ENABLED 承接。先显式探一次（而不是让下面
    -- 那条静态引用报 Msg 207），再走动态 SQL 取数——静态引用未落库的新列会让整批编译失败。
    IF COL_LENGTH(N'dbo.MODULES', N'APPROVE_ENABLED') IS NULL
        THROW 54121, N'本迁移依赖迁移 345（批核入口声明 APPROVE_ENABLED）：请先执行 345 再执行 346。', 1;

    DECLARE @entrySql NVARCHAR(MAX) = N'SELECT COUNT(*) FROM dbo.MODULES WHERE ISNULL(APPROVE_ENABLED, 0) = 1;';
    DECLARE @entry INT;
    EXEC sp_executesql @entrySql, N'@out INT OUTPUT', @out = @entry OUTPUT;
    PRINT N'== 删列前自证：有启用动作却分闸关着的模块 = 0；批核入口声明置位 = '
          + CONVERT(NVARCHAR(10), @entry) + N' 个（迁移 345 的入口集合） ==';

    BEGIN TRANSACTION;

    DECLARE @constraint SYSNAME = (
        SELECT dc.name
        FROM sys.default_constraints dc
        INNER JOIN sys.columns c
                ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
        WHERE dc.parent_object_id = OBJECT_ID(N'dbo.MODULES') AND c.name = N'EFFECT_ENGINE_TAG');
    IF @constraint IS NOT NULL
    BEGIN
        EXEC(N'ALTER TABLE dbo.MODULES DROP CONSTRAINT [' + @constraint + N']');
        PRINT N'== 已删除 DEFAULT 约束 ' + @constraint + N' ==';
    END

    ALTER TABLE dbo.MODULES DROP COLUMN EFFECT_ENGINE_TAG;
    PRINT N'== 已删除 MODULES.EFFECT_ENGINE_TAG ==';

    COMMIT TRANSACTION;
END
GO

/* ---------- 2. 字段元数据收口（该列从未登记进 FIELDS，这里只做一次核对） ---------- */
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'MODULES' AND F_ID = N'EFFECT_ENGINE_TAG')
BEGIN
    DELETE FROM dbo.FIELDS WHERE T_ID = N'MODULES' AND F_ID = N'EFFECT_ENGINE_TAG';
    PRINT N'== 已清掉 FIELDS 里对已删列的登记 ==';
END
ELSE
    PRINT N'== FIELDS 无该列登记（本列从未登记），无需处理 ==';
GO

PRINT N'== migration 346 收口：模块级分闸退役；接管判据 = 定义里有启用且接在效果链上的动作 ==';
