-- 清空三条无法解析的虚拟表达式：引用名不在所在表的 TABLES.QUERY_RELATION 里。
--
-- 口径：VIRTUAL_EXP 的引用名必须命中本表 QUERY_RELATION 的某一段——该段的别名（无别名的段其别名
-- 即表名，同表多段时只认别名）。命中不了时字段既不报错也不渲染，只是长期显示为空，属于"看着配了、
-- 实际不工作"的隐患，故清空表达式并降级为普通字段（IS_VIRTUAL = 0），原因写入字段备注留痕。
--
-- 三行的情形（实查）：
--   ① HR_EMPLOYEE_CADE.STATE、MOC_PRODUCE_IN_M.LINE_ID：所在表在 sys.tables 里不存在
--      （无物理表、无模块引用、字段本就不显示），表达式无从解析；
--   ② SYSDL.G_DESC（默认群组）：表存在，但 SYSDL 没有组键，用户与组由 SYSDG_USER 承担且是多对多
--      （实测 39 个用户里 30 个属多组）⇒ 不存在唯一的"默认群组"，补 JOIN 会让主表行按组重复，
--      故不补关系、只清表达式。
--
-- 不标脏：涉及的三张表所属模块（2306/230906/239806 等）都不在统一表单白名单内，没有已发布快照会
-- 因此落后；标脏只会留下永不消费的脏行。
--
-- 幂等：三行都已清空时 0 行受影响；目标行缺失或表达式与预期不符时报错（避免静默改错行）。

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
    (N'HR_EMPLOYEE_CADE', N'STATE', N'HR_EMPLOYEE.STATE',
     N'虚拟表达式 HR_EMPLOYEE.STATE 已清空：HR_EMPLOYEE_CADE 无物理表，该引用无从解析。'),
    (N'MOC_PRODUCE_IN_M', N'LINE_ID', N'MOC_PRODUCE_M.LINE_ID',
     N'虚拟表达式 MOC_PRODUCE_M.LINE_ID 已清空：MOC_PRODUCE_IN_M 无物理表，该引用无从解析。'),
    (N'SYSDL', N'G_DESC', N'SYSDG.G_DESC',
     N'虚拟表达式 SYSDG.G_DESC 已清空：SYSDL 无组键，用户与组（SYSDG_USER）是多对多，不存在唯一的默认群组。');

-- ① 前置自证：三行都在
IF EXISTS (SELECT 1 FROM @Targets t
           WHERE NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WHERE f.T_ID = t.T_ID AND f.F_ID = t.F_ID))
    THROW 55000, N'目标字段行缺失，与本迁移的预期不符。', 1;

-- ② 前置自证：表达式要么正是预期值，要么已被清空（重复执行）；其它值说明有人改过，中止
IF EXISTS (SELECT 1 FROM @Targets t
           JOIN dbo.FIELDS f ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
           WHERE ISNULL(LTRIM(RTRIM(f.VIRTUAL_EXP)), N'') NOT IN (N'', t.Expected))
    THROW 55001, N'目标字段的虚拟表达式与预期不符（可能已被修改），中止以免改错行。', 1;

-- ③ 清空表达式、降级为普通字段，并把原因追加到字段备注
UPDATE f
SET VIRTUAL_EXP = NULL,
    IS_VIRTUAL = 0,
    F_REMARK = LEFT(ISNULL(NULLIF(LTRIM(RTRIM(f.F_REMARK)), N''), N'') + N' ' + t.Note, 1000),
    LAST_UPDATE_BY = N'migration-256',
    LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.FIELDS f
JOIN @Targets t ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
WHERE ISNULL(LTRIM(RTRIM(f.VIRTUAL_EXP)), N'') <> N'';

-- ④ 后置自证：不再声明虚拟、表达式为空、留痕到位
IF EXISTS (SELECT 1 FROM @Targets t
           JOIN dbo.FIELDS f ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
           WHERE f.VIRTUAL_EXP IS NOT NULL OR ISNULL(f.IS_VIRTUAL, 0) <> 0)
    THROW 55002, N'仍有目标字段声明为虚拟或表达式未清空，清理未生效。', 1;

IF EXISTS (SELECT 1 FROM @Targets t
           JOIN dbo.FIELDS f ON f.T_ID = t.T_ID AND f.F_ID = t.F_ID
           WHERE ISNULL(f.F_REMARK, N'') NOT LIKE N'%已清空%')
    THROW 55003, N'目标字段的备注未写入留痕。', 1;

COMMIT TRANSACTION;
