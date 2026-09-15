-- ============================================================================
-- EOS.ERP migration 085: 公式行空串归一（空聚合/空来源域 → NULL）
-- ----------------------------------------------------------------------------
-- 背景：工作区里 409 行公式行的 SOURCE_AGG、100 行的 SOURCE_SCOPE 存的是**空串**而非 NULL。
-- 定义装配会把空串原样写进快照 JSON（`"sourceAgg":""`），而效果计划加载器对这两个字段做
-- **闭式集合校验**（AGG / 来源域），空串不在集合内 → 加载即抛配置异常。
--
-- 影响面：44 个模块存在"有算子/目标列但缺聚合或来源域"的公式行（共 300+ 行）；这些模块
-- 只要保存走到效果计划加载，就会以 500 失败（历史上是"引擎未接管即不加载"掩盖了这个隐患）。
--
-- 处置：把这两列的空串统一归一为 NULL —— 空串在语义上就是"未设置"，加载器按缺省处理。
-- （占位行不受影响：其算子/目标列本就为空，加载时被跳过。）
-- 幂等：只更新仍是空串的行。
-- ============================================================================

SET NOCOUNT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

PRINT N'== 归一同名公式行的空串 ==';

UPDATE dbo.MODULE_BUSINESS_ACTION_OP
SET SOURCE_AGG = NULL
WHERE SOURCE_AGG IS NOT NULL AND LTRIM(RTRIM(SOURCE_AGG)) = '';

PRINT CONCAT(N'  SOURCE_AGG 归一为 NULL 的行数=', @@ROWCOUNT);

-- SOURCE_SCOPE 是非空列，不能置 NULL；按加载器的缺省语义归一为 MASTER（来源域缺省即主表）。
UPDATE dbo.MODULE_BUSINESS_ACTION_OP
SET SOURCE_SCOPE = N'MASTER'
WHERE LTRIM(RTRIM(SOURCE_SCOPE)) = '';

PRINT CONCAT(N'  SOURCE_SCOPE 归一为 MASTER 的行数=', @@ROWCOUNT);

-- 复核：仍是空串的行应为 0（NULL 是"未设置"的正常取值，不算问题）
SELECT CONCAT(N'  剩余空串行=', COUNT(*)) AS SUMMARY
FROM dbo.MODULE_BUSINESS_ACTION_OP
WHERE SOURCE_AGG = N'' OR SOURCE_SCOPE = N'';

PRINT N'== 公式行空串归一完成 ==';
