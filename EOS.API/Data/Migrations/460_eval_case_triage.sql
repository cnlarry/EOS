-- ============================================================================
-- EOS.ERP migration 460：评估用例的"分诊"三列（失败改哪里）
-- ----------------------------------------------------------------------------
--  目标：评估报告不只说"这条没过"，还要说"该去哪里改、需要什么权限"，
--  让客户运维在系统内把能改的改掉，不必把每条失败都转给开发（见 ADR-046）。
--
--  三列：
--   FIX_AT          失败时的修改落点（case/param/prompt/tool/metric/metadata/rule/kb/data/code）
--   FIX_SUGGESTION  一句话建议动作（给人看，不做机读判定）
--   FIX_PERMISSION  该落点需要的权限所属模块号（如 3105 / 2302 / 230104 / 3103 / 3106；code 留空）
--
--  DDL 与后续更新必须分批（GO）：同一批里引用刚加的列，SQL Server 在编译期就会报"列名无效"。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51950, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

IF OBJECT_ID(N'dbo.ASSISTANT_EVAL_CASE', N'U') IS NULL
    THROW 51951, N'ASSISTANT_EVAL_CASE 不存在（迁移 458 未执行），迁移终止。', 1;

/* ---------- 1. 加列（幂等） ---------- */
IF COL_LENGTH(N'dbo.ASSISTANT_EVAL_CASE', N'FIX_AT') IS NULL
    ALTER TABLE dbo.ASSISTANT_EVAL_CASE ADD FIX_AT nvarchar(20) NULL;
IF COL_LENGTH(N'dbo.ASSISTANT_EVAL_CASE', N'FIX_SUGGESTION') IS NULL
    ALTER TABLE dbo.ASSISTANT_EVAL_CASE ADD FIX_SUGGESTION nvarchar(300) NULL;
IF COL_LENGTH(N'dbo.ASSISTANT_EVAL_CASE', N'FIX_PERMISSION') IS NULL
    ALTER TABLE dbo.ASSISTANT_EVAL_CASE ADD FIX_PERMISSION nvarchar(20) NULL;
GO

/* ---------- 2. 按层回填默认落点（只填空值，不覆盖已填） ---------- */
UPDATE c
SET c.FIX_AT = v.FIX_AT,
    c.FIX_SUGGESTION = v.FIX_SUGGESTION,
    c.FIX_PERMISSION = v.FIX_PERMISSION
FROM dbo.ASSISTANT_EVAL_CASE c
JOIN (VALUES
    (N'authz',      N'code',     N'越权泄露属服务端判定缺陷：附 correlationId 提交缺陷单', NULL),
    (N'diagnosis',  N'rule',     N'先核 MODULE_VALIDATION_RULE 的规则/文案是否与引擎一致；确属归因错误再提缺陷', N'230104'),
    (N'config',     N'rule',     N'核效果链动作与反向配置（模块动作）；配置类别缺失时提缺陷', N'230104'),
    (N'functional', N'prompt',   N'先看用例期望是否写错；其次补提示词规则/示例，必要时补知识库文档', N'3105'),
    (N'business',   N'prompt',   N'同上：期望→规则/示例→知识库', N'3105'),
    (N'inference',  N'prompt',   N'同上：跨源推断失败多因缺规则或示例', N'3105'),
    (N'action',     N'metadata', N'核模块字段/必填/只读与按钮授权', N'2302'),
    (N'situation',  N'prompt',   N'处境摘要类别偏了：先核上报字段，再核提示词规则', N'3105')
) AS v(LAYER, FIX_AT, FIX_SUGGESTION, FIX_PERMISSION)
  ON v.LAYER = c.LAYER
WHERE c.FIX_AT IS NULL;
GO

/* ---------- 3. 逐条覆盖：2026-10-31 的三批候选 ---------- */
-- 负样本：期望"拒答"。失败意味着服务端错误归因/编造原因 —— 属代码缺陷。
UPDATE dbo.ASSISTANT_EVAL_CASE
SET FIX_AT = N'code', FIX_PERMISSION = NULL,
    FIX_SUGGESTION = N'服务端未按预期拒答：附用例 ID 与命中事实提交缺陷单'
WHERE CASE_ID LIKE N'N-0%';

-- 真实失败样本：期望是该守卫的真实文案。失败＝解释与守卫不一致，先核规则文案（230104）。
UPDATE dbo.ASSISTANT_EVAL_CASE
SET FIX_AT = N'rule', FIX_PERMISSION = N'230104',
    FIX_SUGGESTION = N'先核该动作/校验的登记文案；文案对则属解释缺陷，提缺陷单'
WHERE CASE_ID LIKE N'OBS-%';

-- 配置面：可检出的效果类问题落在效果链配置（230104）。
UPDATE dbo.ASSISTANT_EVAL_CASE
SET FIX_AT = N'rule', FIX_PERMISSION = N'230104',
    FIX_SUGGESTION = N'核该效果键的反向配置/使用次数'
WHERE CASE_ID IN (N'CFG-01', N'CFG-02', N'CFG-03', N'CFG-04');

UPDATE dbo.ASSISTANT_EVAL_CASE
SET FIX_AT = N'metadata', FIX_PERMISSION = N'2302',
    FIX_SUGGESTION = N'核字段虚拟表达式与可见性'
WHERE CASE_ID = N'CFG-05';

UPDATE dbo.ASSISTANT_EVAL_CASE
SET FIX_AT = N'code', FIX_PERMISSION = NULL,
    FIX_SUGGESTION = N'配置面归因类别尚未定义：先定类别再补判据'
WHERE CASE_ID = N'CFG-06';

-- 诊断黄金集里的代码级守卫（FG/BL）：失败＝守卫或解释错，属代码。
UPDATE dbo.ASSISTANT_EVAL_CASE
SET FIX_AT = N'code', FIX_PERMISSION = NULL,
    FIX_SUGGESTION = N'代码级守卫：附用例 ID 提交缺陷单'
WHERE CASE_ID LIKE N'FG-%' OR CASE_ID LIKE N'BL-%';
GO

/* ---------- 4. 字段元数据（让这三列在 3106 上可见可筛） ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'ASSISTANT_EVAL_CASE' AND F_ID = N'FIX_AT')
    INSERT INTO dbo.FIELDS
        (T_ID, F_ID, F_DESC, F_TYPE, DISPLAY_LENGTH, HEADER_ALIGN, ITEM_ALIGN,
         IS_VERIFY, VERIFY_INDEX, IS_PK, IS_READONLY, IS_VISIBLE, IS_VIRTUAL, IS_QUERY,
         IS_DEFAULT_FIELDS, CAN_COPY, MODIFIED_BY, MODIFIED_TIME)
    VALUES
        (N'ASSISTANT_EVAL_CASE', N'FIX_AT',         N'改哪里',   N'nvarchar', 90,  N'center', N'left', 0, 21, 0, 0, 1, 0, 1, 1, 1, N'460', GETDATE()),
        (N'ASSISTANT_EVAL_CASE', N'FIX_SUGGESTION', N'改进建议', N'nvarchar', 300, N'left',   N'left', 0, 22, 0, 0, 1, 0, 1, 0, 1, N'460', GETDATE()),
        (N'ASSISTANT_EVAL_CASE', N'FIX_PERMISSION', N'所需权限', N'nvarchar', 90,  N'center', N'left', 0, 23, 0, 0, 1, 0, 1, 0, 1, N'460', GETDATE());
GO

/* ---------- 5. 自检 ---------- */
IF EXISTS (SELECT 1 FROM dbo.ASSISTANT_EVAL_CASE WHERE FIX_AT IS NULL)
    THROW 51952, N'仍有用例没有分诊落点，迁移终止。', 1;

PRINT N'== 分诊列已就绪 ==';
