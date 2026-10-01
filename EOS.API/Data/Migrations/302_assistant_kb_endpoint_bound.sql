-- ============================================================================
-- EOS.ERP migration 302: 助手参数第七批——**检索端点的条数上界**（1 条）
-- ----------------------------------------------------------------------------
--  来源：ADR-030 §5.3.9 补记。
--
--  缺口：`POST /api/v1/assistant/kb/search` 的 `TopK` 由调用方传入，**没有上界**——
--  一句请求就能让检索返回任意条数。而助手工具那条路一直有 KB_SEARCH_MAX_HITS 兜着，
--  于是"检索最多返回几条"这件事在两条入口上是两套口径，其中一套还是"没有上限"。
--
--  为什么另立一个键而不是复用 KB_SEARCH_MAX_HITS：
--    · KB_SEARCH_MAX_HITS 管的是"进模型上下文几条"（检索结果要喂给模型，是 token 预算）；
--    · 本键管的是"界面能翻出几条"（检索页/调用方看）。
--  合成一个的话，管理员为省 token 把工具上限调到 3，检索页会一起缩水到 3，而界面上看不出这层关联；
--  反过来为了检索页能翻，就得给模型多喂几倍片段。两者各有各的主张，因此各占一个键。
--
--  默认 20：高于端点原有的默认 TopK（5）与工具上限（5），所以**不改变既有调用方的行为**，
--  只把"无上限"这条堵上。超限时端点回显实际生效条数，调用方看得见被夹住。
--
--  幂等：建行按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50900, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, N'KB_ENDPOINT_MAX_HITS', NULL, N'int', N'20',
    N'KB', N'知识库', 90, 50,
    N'REST 检索端点一次最多返回几条：调用方传的 TopK 会被夹在 1 与本值之间，响应里回显实际条数；'
    + N'与「一次检索的命中条数上限」分开——那个管进模型上下文几条，这个管界面能翻出几条',
    N'immediate', NULL, N'mig-302', SYSDATETIME()
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = N'KB_ENDPOINT_MAX_HITS');

PRINT N'== 检索端点条数上界建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* ---------- 断言 ---------- */
-- 七批合计 107 条（9 + 36 + 27 + 3 + 21 + 10 + 1）
IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 107)
    THROW 50901, N'助手参数条数不是 107，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS
               WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'KB_ENDPOINT_MAX_HITS'
                 AND VALUE_TYPE = N'int' AND EFFECT_SCOPE = N'immediate' AND PARAM_VALUE IS NULL)
    THROW 50902, N'KB_ENDPOINT_MAX_HITS 的类型 / 生效范围 / 取值形态不符。', 1;

-- 上界必须**大于**工具上限：否则"检索页能翻的条数"比"喂给模型的条数"还少，
-- 这个键就失去了它存在的理由（那还不如直接复用工具上限）
IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS e
               JOIN dbo.SYSSS t ON t.OWNER_MODULE = 3105 AND t.PARAM_KEY = N'KB_SEARCH_MAX_HITS'
               WHERE e.OWNER_MODULE = 3105 AND e.PARAM_KEY = N'KB_ENDPOINT_MAX_HITS'
                 AND TRY_CAST(e.DEFAULT_VALUE AS INT) > TRY_CAST(t.DEFAULT_VALUE AS INT))
    THROW 50903, N'KB_ENDPOINT_MAX_HITS 必须大于 KB_SEARCH_MAX_HITS，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：检索端点条数上界已并入 dbo.SYSSS（3105 合计 107 条）==';
