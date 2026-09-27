-- 清空两条挂在非虚拟字段上的虚拟表达式（字段本身是物理列，表达式从不进入运行时）。
--
-- 口径：读取侧一律按 FIELDS.IS_VIRTUAL = 1 才解析 VIRTUAL_EXP（列表、表单、选择器来源列同一判据）；
-- IS_VIRTUAL = 0 时表达式既不报错也不取值，属于"看着配了、实际不工作"的隐患，故清空表达式，
-- 原值与原因写入字段备注留痕。字段仍是普通物理字段，取值口径不变。
--
-- 两行的情形（实查）：
--   ① HR_EMPLOYEE.IN_MONTH = DATEDIFF(MM,IN_DATE,GETDATE())：按月计的算式，字段本身是物理列；
--   ② HR_REDEPLOY_D.DEPT_ID = HR_EMPLOYEE.DEPT_ID：跨表引用，字段本身是物理列。
--
-- 不标脏：虚拟表达式文本不参与工作台定义内容（定义 JSON 不含该文本），清空不会改变任何已发布
-- 快照的内容，也不会让快照静默落后。
--
-- 幂等：两行都已清空时 0 行受影响；目标行缺失、表达式与预期不符、或已被登记为虚拟字段时报错
-- （避免静默改错行，也避免抹掉后来补上的虚拟字段配置）。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @Targets TABLE (
    T_ID NVARCHAR(200) NOT NULL,
    F_ID NVARCHAR(200) NOT NULL,
    Expected NVARCHAR(1000) NOT NULL,
    Note NVARCHAR(1000) NOT NULL);

INSERT INTO @Targets (T_ID, F_ID, Expected, Note) VALUES
    (N'HR_EMPLOYEE', N'IN_MONTH', N'DATEDIFF(MM,IN_DATE,GETDATE())',
     N'原虚拟表达式 DATEDIFF(MM,IN_DATE,GETDATE()) 已清空：该字段是物理列（IS_VIRTUAL=0），表达式不进入运行时。'),
    (N'HR_REDEPLOY_D', N'DEPT_ID', N'HR_EMPLOYEE.DEPT_ID',
     N'原虚拟表达式 HR_EMPLOYEE.DEPT_ID 已清空：该字段是物理列（IS_VIRTUAL=0），表达式不进入运行时。');

-- ① 前置自证：两行都在
IF EXISTS (SELECT 1 FROM @Targets t
           WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = t.T_ID AND f.F_ID = t.F_ID))
    THROW 55000, N'目标字段行缺失，与本迁移的预期不符。', 1;

-- ② 前置自证：不得已被登记为虚拟字段（说明有人补了配置，宁可中止）
IF EXISTS (SELECT 1 FROM @Targets t
           JOIN dbo.FIELDS f ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
           WHERE COALESCE(f.IS_VIRTUAL, 0) <> 0)
    THROW 55001, N'目标字段已被登记为虚拟字段，本迁移的预期不成立。', 1;

-- ③ 前置自证：表达式要么正是预期值，要么已被清空（重复执行）；其它值说明有人改过，中止
IF EXISTS (SELECT 1 FROM @Targets t
           JOIN dbo.FIELDS f ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
           WHERE ISNULL(LTRIM(RTRIM(f.VIRTUAL_EXP)), N'') NOT IN (N'', t.Expected))
    THROW 55002, N'目标字段的虚拟表达式与预期不符（可能已被修改），中止以免改错行。', 1;

-- ④ 清空表达式并把原因追加到字段备注；字段保持普通物理字段
UPDATE f
SET VIRTUAL_EXP = NULL,
    F_REMARK = LEFT(ISNULL(NULLIF(LTRIM(RTRIM(f.F_REMARK)), N''), N'') + N' ' + t.Note, 1000),
    LAST_UPDATE_BY = N'migration-264',
    LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.FIELDS f
JOIN @Targets t ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
WHERE ISNULL(LTRIM(RTRIM(f.VIRTUAL_EXP)), N'') <> N'';

-- ⑤ 后置自证：表达式已清空、字段仍未登记为虚拟、留痕到位
IF EXISTS (SELECT 1 FROM @Targets t
           JOIN dbo.FIELDS f ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
           WHERE f.VIRTUAL_EXP IS NOT NULL OR COALESCE(f.IS_VIRTUAL, 0) <> 0)
    THROW 55003, N'目标字段的虚拟表达式未清空，或字段被改成了虚拟字段。', 1;

IF EXISTS (SELECT 1 FROM @Targets t
           JOIN dbo.FIELDS f ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
           WHERE ISNULL(f.F_REMARK, N'') NOT LIKE N'%已清空%')
    THROW 55004, N'目标字段的备注未写入留痕。', 1;

COMMIT TRANSACTION;
