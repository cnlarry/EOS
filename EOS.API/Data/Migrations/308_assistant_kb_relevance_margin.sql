-- ============================================================================
-- EOS.ERP migration 308: 助手参数第八批——**知识库检索的相关性截断幅度**（1 条）
-- ----------------------------------------------------------------------------
--  背景（ADR-031 §4.1）：检索此前是"VECTOR_DISTANCE('cosine') + TOP(N)"，**没有任何相关性下限**。
--  "凑得上的都注入"会把不相关的片段一起塞进模型上下文，而知识库里满是参数键、错误码、模块号
--  这类专有标识符——纯向量对它们最弱（这正是"检索回来的东西看起来像答案、其实不是"的来源）。
--
--  口径：**相对截断**，不是绝对相似度下限。只注入与**最佳命中**的相似度相差不超过「幅度」
--  个百分点的片段。选相对口径的理由：cosine 的分值分布逐模型逐厂商不同，写死一个绝对下限
--  会在换嵌入模型那天**静默失效**（表现成"某天开始答得变差"或"什么都检索不到"），不报错；
--  相对口径还有一条性质——**永远保留最佳命中**，所以阈值配错也不会让知识通道整体失声
--  （代价是"整批都不相关"时仍带一条，由工具输出开头那句"未必都与问题相关"交给模型判断）。
--
--  本批交付的是四件套：
--    · 目录条目 —— KB 域一条（KbDefaults.RelevanceMarginPct，键 KB_SEARCH_RELEVANCE_MARGIN_PCT）；
--    · 库行     —— 本脚本；
--    · 消费改造 —— KbRelevance.Cut 在 kb_search 路径上截断；被丢掉的条数写进工具输出
--                   （"另有 N 条相关性明显更低未列出"），让模型知道尾巴被切过；
--    · 门禁     —— 目录 ↔ 库（连库单测逐字段比对 SEQ_NO 等）、截断边界（离线单测）。
--
--  序号：新参数紧挨另外两条**检索侧**参数，因此本组序号重排一次——
--    10 命中条数 / 20 片段字符 / 30 相关性截断 / 40 块长 / 50 重叠 / 60 端点条数上界。
--  重排用的是**按参数键写死目标值**的 UPDATE（不是"全部 +10"）：迁移允许重复执行，
--  递增式重排第二次执行会把序号再推一遍。
--
--  幂等：按 (OWNER_MODULE, PARAM_KEY) 判存在；可重复执行。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, N'本脚本只能在 EOS.ERP 数据库内执行。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = 3105)
    THROW 50650, N'模块 3105 助手设置不存在，迁移中止（先跑迁移 293）。', 1;

BEGIN TRANSACTION;

INSERT INTO dbo.SYSSS (
    OWNER_MODULE, PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE,
    GROUP_CODE, GROUP_LABEL, GROUP_SEQ, SEQ_NO, DESC_TEXT, EFFECT_SCOPE, REMARK,
    CREATE_PERSON, CREATE_DATE)
SELECT
    3105, N'KB_SEARCH_RELEVANCE_MARGIN_PCT', NULL, N'int', N'15',
    N'KB', N'知识库', 90, 30,
    N'只注入与最佳命中的相似度相差不超过这个幅度（百分点）的片段；0 = 只留最佳命中，100 = 基本不截断。'
    + N'最佳命中永远保留，所以配错也不会让知识通道整体失声。',
    N'immediate', NULL, N'mig-308', SYSDATETIME()
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.SYSSS x
    WHERE x.OWNER_MODULE = 3105 AND x.PARAM_KEY = N'KB_SEARCH_RELEVANCE_MARGIN_PCT');

PRINT N'== 相关性截断幅度建行：' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 条（表内已有则跳过）==';

/* 组内序号重排：写死目标值，可重复执行（只在真的不同时才写，避免无谓的更新时间戳） */
UPDATE dbo.SYSSS SET SEQ_NO = 40
 WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'KB_CHUNK_MAX_CHARS' AND SEQ_NO <> 40;

UPDATE dbo.SYSSS SET SEQ_NO = 50
 WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'KB_CHUNK_OVERLAP_CHARS' AND SEQ_NO <> 50;

UPDATE dbo.SYSSS SET SEQ_NO = 60
 WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'KB_ENDPOINT_MAX_HITS' AND SEQ_NO <> 60;

/* ---------- 断言 ---------- */
IF EXISTS (
    SELECT 1 FROM (VALUES
        (N'KB_SEARCH_MAX_HITS', 10), (N'KB_SEARCH_MAX_CONTENT_LENGTH', 20),
        (N'KB_SEARCH_RELEVANCE_MARGIN_PCT', 30), (N'KB_CHUNK_MAX_CHARS', 40),
        (N'KB_CHUNK_OVERLAP_CHARS', 50), (N'KB_ENDPOINT_MAX_HITS', 60)) expected(PARAM_KEY, SEQ_NO)
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.SYSSS s
        WHERE s.OWNER_MODULE = 3105 AND s.PARAM_KEY = expected.PARAM_KEY
          AND s.SEQ_NO = expected.SEQ_NO AND s.GROUP_CODE = N'KB'))
    THROW 50651, N'KB 域的组内序号与目录推导不一致（应为 10/20/30/40/50/60），迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105 AND PARAM_KEY = N'KB_SEARCH_RELEVANCE_MARGIN_PCT'
             AND (VALUE_TYPE <> N'int'
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) IS NULL
                  OR TRY_CONVERT(INT, DEFAULT_VALUE) NOT BETWEEN 0 AND 100))
    THROW 50652, N'相关性截断幅度的类型 / 默认值不符（应为 0–100 的整数），迁移中止。', 1;

IF EXISTS (SELECT GROUP_CODE, SEQ_NO FROM dbo.SYSSS
           WHERE OWNER_MODULE = 3105
           GROUP BY GROUP_CODE, SEQ_NO HAVING COUNT(*) > 1)
    THROW 50653, N'助手参数的组内序号重复，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.SYSSS WHERE OWNER_MODULE = 3105 HAVING COUNT(*) = 119)
    THROW 50654, N'助手参数条数不是 119（118 + 1），迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：知识库检索的相关性截断幅度已并入 dbo.SYSSS（3105 合计 119 条）==';
