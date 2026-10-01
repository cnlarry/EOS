-- ============================================================================
-- EOS.ERP migration 299: 助手参数第四批——**记录动作族开关**（3 条）
-- ----------------------------------------------------------------------------
--  来源：ADR-030 §5.3.4、§10 批 D2。
--
--  在此之前，"助手能不能对记录做删除"只有两条路：改代码或改模块权限。前者不叫配置，
--  后者是**人的权限**——而管理员的诉求常常是"这个模块不许助手删除"，既不是改代码，
--  也不是剥夺某人的删除权（人自己动手删仍然可以）。这一批把这件事变成一条参数。
--
--  键名由动作名机械生成：ACTION_ + 动作名转大写（引用 AssistantRecordActionNames 的常量）。
--  只有新增 / 修改 / 删除三个：批核族与权限授予类**枚举里没有成员**，因此也不可能有开关。
--
--  **这是助手参数里唯一声明了模块层的三个键**（目录里 ScopePolicy=Tighten、Layers=Module）：
--    · 收紧 —— 只能关、不能开：全局关掉的动作，任何模块都不许单独打开；
--    · 模块层 —— 消费点 AssistantActionGate 本来就带着"哪个模块"，判定落在权限判定之前。
--  与之配套的作用域表是 dbo.ASSISTANT_PARAM_SCOPE（迁移 296），不在这里建行。
--
--  默认全开（DEFAULT_VALUE = '1'，PARAM_VALUE 留空）："没配过"就是能执行，
--  与工具开关同口径。
--
--  幂等：建行按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50600, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

/* 动作名、参数键与组内序号：序号接在 27 个工具开关（50–310）之后 */
DECLARE @actions TABLE (SEQ_NO INT, ACTION_NAME NVARCHAR(32), PARAM_KEY NVARCHAR(64));

INSERT INTO @actions (SEQ_NO, ACTION_NAME, PARAM_KEY) VALUES
    (320, N'insert', N'ACTION_INSERT'),
    (330, N'update', N'ACTION_UPDATE'),
    (340, N'delete', N'ACTION_DELETE');

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, a.PARAM_KEY, NULL, N'bit', N'1',
    N'CAPABILITY', N'能力面', 40, a.SEQ_NO,
    N'关掉之后助手不能对记录做 ' + a.ACTION_NAME
        + N' 操作（预演与执行都在门禁处被拒）；只能越关越少，全局关掉的任何模块都不许单独打开',
    N'immediate', NULL, N'mig-299', SYSDATETIME()
FROM @actions a
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = a.PARAM_KEY);

PRINT N'== 记录动作族开关建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
-- 四批合计 75 条（9 + 36 + 27 + 3）
IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 75)
    THROW 50601, N'助手参数条数不是 75（9 + 36 + 27 + 3），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM @actions a
           WHERE NOT EXISTS (SELECT 1 FROM dbo.SYSSS s
                             WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = a.PARAM_KEY))
    THROW 50602, N'有动作族开关未落库，迁移中止。', 1;

-- 一律 bit、默认开、立即生效：写成"默认关"会让升级后的助手突然不能新增记录
IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
             AND PARAM_KEY IN (N'ACTION_INSERT', N'ACTION_UPDATE', N'ACTION_DELETE')
             AND (VALUE_TYPE <> N'bit' OR DEFAULT_VALUE <> N'1' OR EFFECT_SCOPE <> N'immediate'))
    THROW 50603, N'动作族开关的类型 / 默认值 / 生效范围不符（应为 bit、默认 1、immediate）。', 1;

-- 批核族与权限授予类**没有动作枚举成员**，因此不该出现任何相关开关
IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
             AND (PARAM_KEY LIKE N'ACTION[_]%APPROVE%' OR PARAM_KEY LIKE N'ACTION[_]%END[_]CASE%'
               OR PARAM_KEY LIKE N'%RIGHTS[_]%'))
    THROW 50604, N'出现了批核族 / 权限授予类的动作开关——它们没有可代理动作面（ADR-030 §7.3）。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：记录动作族开关已并入 dbo.SYSSS（3105 合计 75 条）==';
