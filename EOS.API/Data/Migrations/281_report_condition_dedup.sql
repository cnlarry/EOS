-- 重复筛选条件去重（可安全去重的那一批）。
--
-- 背景：筛选条件从报表承载页归位到归属模块时，规则是"序号沿用原值、被占则接在目标当前最大序号之后"，
-- 只避免了主键撞车，**没有与目标模块已有的同义条件比对**。于是目标模块上出现了
-- 「同一字段、同一模板」的条件各两份。实测全库 7 个模块 23 组：
--   1405 / 1502 / 180102 / 180105 / 180214 / 1803091（另有 4 行空模板条件）。
--
-- 为什么只做一部分：**两份都带用户填值**的模块（1405 / 1502 / 180214），
-- 保留哪一份、另一份的填值算不算数，是业务语义问题（同一条件两份填了不同值时，
-- 两条谓词同时生效 = 结果比用户以为的更窄）；机械合并会把这种歧义固化下来。
-- 那一批**不在本迁移范围**，已具名登记，等业务定夺。
--
-- 本迁移处理的三组，判据是**被删的那一份在 SYSQR_USER 里零填值**——
-- 删掉它不会丢任何用户输入。判据在脚本里断言，不靠人工核对。
--   180102：删 8 / 9 / 10 / 12（与 4 / 5 / 1 / 2 同义），保留 1..7
--   180105：删 8 / 10（与 1 / 2 同义），保留 1..5
--   1803091：删 1 / 2 / 3（与 5 / 6 / 4 同义），保留 4 / 5 / 6 / 7
--           另删 8（F_ID 与 FILTER_TEMPLATE 皆空的退化行，零填值）
--
-- 另两行空模板条件（180504 的 5、180652 的 2）：零填值、零内容，一并清掉。
-- （99000001 的同类行属测试用模块，不属本批范围。）

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- 范围外模块的条件行数先记下来：本批**不许**碰它们，事后按数对比。
IF OBJECT_ID('tempdb..#BEFORE') IS NOT NULL DROP TABLE #BEFORE;
CREATE TABLE #BEFORE (M_IDX INT NOT NULL PRIMARY KEY, CONDITION_COUNT INT NOT NULL);
INSERT INTO #BEFORE (M_IDX, CONDITION_COUNT)
SELECT M_IDX, COUNT(*) FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
WHERE M_IDX IN (1405, 1502, 180214) GROUP BY M_IDX;

IF OBJECT_ID('tempdb..#DROP_CONDITION') IS NOT NULL DROP TABLE #DROP_CONDITION;
CREATE TABLE #DROP_CONDITION (M_IDX INT NOT NULL, SERIAL_NO INT NOT NULL,
                              REASON NVARCHAR(200) NOT NULL,
                              PRIMARY KEY (M_IDX, SERIAL_NO));

INSERT INTO #DROP_CONDITION (M_IDX, SERIAL_NO, REASON) VALUES
    (180102,  8, N'与 4 同义（HR_EMPLOYEE.BIRTHDAY_MONTH）'),
    (180102,  9, N'与 5 同义（HR_EMPLOYEE.CONTRACT_DATE）'),
    (180102, 10, N'与 1 同义（HR_EMPLOYEE.DEPT_ID）'),
    (180102, 12, N'与 2 同义（HR_EMPLOYEE.EMP_NO）'),
    (180105,  8, N'与 1 同义（HR_EMPLOYEE.DEPT_ID）'),
    (180105, 10, N'与 2 同义（HR_EMPLOYEE.EMP_NO）'),
    (1803091, 1, N'与 5 同义（HR_EMPLOYEE.DEPT_ID）'),
    (1803091, 2, N'与 6 同义（HR_EMPLOYEE.EMP_NO）'),
    (1803091, 3, N'与 4 同义（HR_WAGE_M.COUNT_MONTH）'),
    (1803091, 8, N'退化行：F_ID 与 FILTER_TEMPLATE 皆空'),
    (180504,  5, N'空模板条件行'),
    (180652,  2, N'空模板条件行');

-- ---------------------------------------------------------------- ① 前置
IF EXISTS (SELECT 1 FROM #DROP_CONDITION d
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSQR_DEFAULT s WITH (NOLOCK)
                             WHERE s.M_IDX = d.M_IDX AND s.SERIAL_NO = d.SERIAL_NO))
    THROW 55920, N'待删条件行在 SYSQR_DEFAULT 中不存在，中止。', 1;

-- 红线：被删的序号只要有一条用户填值，就不许删——那是在丢用户输入。
IF EXISTS (SELECT 1 FROM #DROP_CONDITION d
           WHERE EXISTS (SELECT 1 FROM dbo.SYSQR_USER u WITH (NOLOCK)
                         WHERE u.M_IDX = d.M_IDX AND u.SERIAL_NO = d.SERIAL_NO))
    THROW 55921, N'待删条件行存在用户填值（SYSQR_USER），中止；本批只删零填值的那一份。', 1;

-- ---------------------------------------------------------------- ② 删除
DELETE s
FROM dbo.SYSQR_DEFAULT s
JOIN #DROP_CONDITION d ON s.M_IDX = d.M_IDX AND s.SERIAL_NO = d.SERIAL_NO;

IF @@ROWCOUNT <> 12
    THROW 55922, N'未按预期删除 12 行条件，中止。', 1;

-- ---------------------------------------------------------------- ③ 后置自证
-- 三组重复应全部消失；其余模块（用户填值纠缠的那批）**必须原样保留**，
-- 免得这次"顺手"把它们动了却没人发现。
IF EXISTS (
    SELECT 1 FROM dbo.SYSQR_DEFAULT s WITH (NOLOCK)
    WHERE s.M_IDX IN (180102, 180105, 1803091) AND ISNULL(s.F_ID, N'') <> N''
    GROUP BY s.M_IDX, s.F_ID, s.FILTER_TEMPLATE
    HAVING COUNT(*) > 1)
    THROW 55923, N'目标模块仍存在重复条件，中止。', 1;

-- 范围外模块（用户填值纠缠的那批）必须**一行未动**——免得这次"顺手"把它们改了却没人发现。
IF EXISTS (
    SELECT 1 FROM #BEFORE b
    WHERE b.CONDITION_COUNT <> (SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT s WITH (NOLOCK) WHERE s.M_IDX = b.M_IDX))
    THROW 55924, N'范围外模块的条件行数发生变化，中止。', 1;

COMMIT TRANSACTION;
