-- ============================================================================
-- EOS.ERP migration 285: 开发手册知识通道的两个集合（规范与坑 / 设计动因）
-- ----------------------------------------------------------------------------
--  来源：ADR-016 决策 8（系统自描述与知识供给）——知识供给走三条通道：
--        机制事实走能力目录（不进库）、规范与坑 / 设计动因走知识库、行为规则走常驻提示词。
--
--  为什么是"两个集合"而不是复用 kb_general：
--  可见性挂在集合上（ALL / CONSULTANT / OPS），而开发手册的读者是开发与实施工程师，
--  与组织级共享知识（kb_general，ALL）不是同一档受众。分档后"谁检索得到"由集合决定，
--  不必逐文档配可见性，也不会把面向运维的内容混进人人可见的集合里。
--
--  投放：两集合由 scripts/sync-guide-to-kb.ps1 推送，入库走既有端点（同一套敏感拦截、
--  引用复核与分块口径）；机制篇目一律不进库（机制真值在元数据与代码注册表里）。
--
--  幂等：按 COLLECTION_ID 不存在才插；已存在时不改可见性也不改标题（人工调整不被覆盖）。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

DECLARE @GuardMessage NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 51800, @GuardMessage, 1;

IF OBJECT_ID(N'dbo.KB_COLLECTION', N'U') IS NULL
    THROW 51801, N'KB_COLLECTION 不存在（迁移 044 未执行），迁移中止。', 1;

BEGIN TRANSACTION;

IF NOT EXISTS (SELECT 1 FROM dbo.KB_COLLECTION WHERE COLLECTION_ID = N'kb_guide_conventions')
BEGIN
    INSERT INTO dbo.KB_COLLECTION (COLLECTION_ID, TITLE, EMBEDDING_MODEL, DIMENSION, DEFAULT_VISIBILITY)
    VALUES (N'kb_guide_conventions', N'开发手册 · 规范与硬约束', N'bge-m3@1', 1024, N'CONSULTANT');
    PRINT N'== 新增集合 kb_guide_conventions（规范与坑，可见性 CONSULTANT）==';
END
ELSE
    PRINT N'== 集合 kb_guide_conventions 已存在，跳过 ==';

IF NOT EXISTS (SELECT 1 FROM dbo.KB_COLLECTION WHERE COLLECTION_ID = N'kb_guide_rationale')
BEGIN
    INSERT INTO dbo.KB_COLLECTION (COLLECTION_ID, TITLE, EMBEDDING_MODEL, DIMENSION, DEFAULT_VISIBILITY)
    VALUES (N'kb_guide_rationale', N'设计动因（ADR 决策记录）', N'bge-m3@1', 1024, N'CONSULTANT');
    PRINT N'== 新增集合 kb_guide_rationale（设计动因，可见性 CONSULTANT）==';
END
ELSE
    PRINT N'== 集合 kb_guide_rationale 已存在，跳过 ==';

/* ---------- 收口断言：两集合就位，且可见性档是 CONSULTANT ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.KB_COLLECTION
                 WHERE COLLECTION_ID = N'kb_guide_conventions' AND DEFAULT_VISIBILITY = N'CONSULTANT')
    THROW 51802, N'集合 kb_guide_conventions 未就位或可见性不是 CONSULTANT，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.KB_COLLECTION
                 WHERE COLLECTION_ID = N'kb_guide_rationale' AND DEFAULT_VISIBILITY = N'CONSULTANT')
    THROW 51803, N'集合 kb_guide_rationale 未就位或可见性不是 CONSULTANT，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 收口：开发手册知识通道两集合就位（kb_guide_conventions / kb_guide_rationale）==';
