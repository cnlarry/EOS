-- ============================================================================
-- EOS.ERP migration 326: 摘掉模块 2301 的 AUTO_APPROVE（它的主表就是 MODULES）
-- ----------------------------------------------------------------------------
-- 迁移 324 把 MODULES 的批核位（CONFIRM_TAG / CONFIRM_PERSON / CONFIRM_DATE）删了，
-- 于是 `check-lifecycle-columns.ps1` 立刻报出一处**真冲突**：
--
--     AUTO_APPROVE | 2301 模块管理 -> dbo.MODULES 缺 CONFIRM_TAG
--
-- 因为 2301「模块管理」这张管理页的**主表就是 MODULES**（M_URL=/admin/menus），而它身上带着
-- `AUTO_APPROVE = 1`。门禁的判据是"模块声明了会触达 CONFIRM_TAG 的批核能力 ⇒ 它的主表必须有
-- 该列"，并把两种收口都算合法："removing the flag or adding the column are both valid; pick one
-- deliberately"（见该脚本第 141 行的原话）。
--
-- 用户已拍板删列（决策清单 #144），故这里选**摘标志位**。取证支持这个选择：
--   · 全库 92 个模块声明 AUTO_APPROVE=1，**只有 2301 这一个**主表缺批核位（它自己就是那张表）；
--   · 2301 挂的是自定义承载页 /admin/menus：0 份工作台快照、0 个页签、0 条业务动作、0 条校验规则
--     ⇒ 它**没有工作台定义**，而"自动批核"只在保存走统一表单工作台时触发（WorkbenchApprovalService），
--     所以这一位对 2301 从未生效过——属门禁注释里点名的"配置表被批量复制标志位带上 AUTO_APPROVE"
--     那一类残留，不是有意声明。
--
-- 只动 AUTO_APPROVE 一位：M_TAG / IF_COPY / ERROR_NO_SAVE / EFFECT_ENGINE_TAG 等模块自身标志位
-- 一律保持原样（2301 的 EFFECT_ENGINE_TAG 本来就是 0，下面后置自证会把三类来源一起盯住）。
--
-- 命名全大写；**非幂等**：2301 不在、或它已经不是 AUTO_APPROVE=1 即报错（与 320~325 同口径）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ① 前置自证：2301 在、它确实声明着 AUTO_APPROVE=1、主表就是 MODULES，且 MODULES 已无批核位
IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WITH (NOLOCK)
               WHERE M_IDX = 2301 AND ISNULL(AUTO_APPROVE, 0) = 1
                 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'MODULES')
    THROW 53600, N'2301 不在、或它不是"AUTO_APPROVE=1 且主表=MODULES"的形态：本迁移的前提不成立（已处理过或结构已变）。', 1;

IF COL_LENGTH(N'dbo.MODULES', N'CONFIRM_TAG') IS NOT NULL
    THROW 53601, N'MODULES.CONFIRM_TAG 还在：先落迁移 324（本迁移是它的连带收口，前提是批核位已删）。', 1;

-- ② 留痕：摘之前先说清它是什么、以及"从未生效"的证据
DECLARE @evidence NVARCHAR(400) = N'';
SELECT @evidence = N'M_IDX=' + CONVERT(nvarchar(20), M_IDX) + N' ' + ISNULL(M_DESC, N'')
                 + N'（M_URL=' + ISNULL(NULLIF(LTRIM(RTRIM(ISNULL(M_URL, N''))), N''), N'(空)') + N'）'
                 + N'；工作台快照 ' + CONVERT(nvarchar(10), (SELECT COUNT(*) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX = 2301))
                 + N' 份、页签 ' + CONVERT(nvarchar(10), (SELECT COUNT(*) FROM dbo.MODULE_FORM_TAB WHERE M_IDX = 2301))
                 + N' 个、业务动作 ' + CONVERT(nvarchar(10), (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX = 2301))
                 + N' 条'
FROM dbo.MODULES WHERE M_IDX = 2301;
PRINT N'== 摘掉 AUTO_APPROVE（自动批核对无工作台定义的模块从未生效）==' + CHAR(10) + N'    ' + ISNULL(@evidence, N'(取不到)');

-- ③ 收口：只把这一位清零，其它标志位不动
UPDATE dbo.MODULES SET AUTO_APPROVE = 0 WHERE M_IDX = 2301;

-- ④ 后置自证：2301 的这位必须归零，且该模块行整行还在（别删错行）
IF EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2301 AND ISNULL(AUTO_APPROVE, 0) <> 0)
    THROW 53610, N'2301 的 AUTO_APPROVE 未被清零。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 2301 AND LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))) = N'MODULES')
    THROW 53611, N'2301 的模块行被误删或主表被改动（这张行本身必须完好）。', 1;

-- ⑤ 后置自证：按 check-lifecycle-columns.ps1 的三条来源复核"批核能力 ⇒ 主表有批核位"。
--    这里刻意抄同一口径（AUTO_APPROVE / EFFECT_ENGINE_TAG / 启用中的批核·解批效果链），
--    免得"门禁绿了但库里还留着另一类同源冲突"。
IF EXISTS (
    SELECT 1 FROM dbo.MODULES m
    WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) <> N''
      AND OBJECT_ID(N'dbo.' + m.MASTER_TABLE, N'U') IS NOT NULL
      AND COL_LENGTH(N'dbo.' + m.MASTER_TABLE, N'CONFIRM_TAG') IS NULL
      AND (ISNULL(m.AUTO_APPROVE, 0) = 1
           OR ISNULL(m.EFFECT_ENGINE_TAG, 0) = 1
           OR EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                      WHERE a.M_IDX = m.M_IDX AND a.ENABLED = 1
                        AND a.EVENT_CODE IN (N'APPROVE_EFFECT', N'DEAPPROVE'))))
    THROW 53612, N'库里仍有"声明批核能力但主表缺 CONFIRM_TAG"的模块：本次收口只处理了 2301，还有别的来源要逐个定夺。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口完成：模块 2301 摘掉 AUTO_APPROVE（迁移 324 删批核位的连带处理，其余标志位原样）==';
