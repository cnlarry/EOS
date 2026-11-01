-- ============================================================================
-- EOS.ERP migration 466：模型用途增加第三种 —— 判官（JUDGE）
-- ----------------------------------------------------------------------------
--  背景（ADR-044 §8 第 1 项）：判官的模型必须**能单独指定并钉住**。
--  原先 `ASSISTANT_MODEL.KIND` 只有 CHAT / EMBEDDING 两种用途，检查约束也只允许这两个值；
--  判官若复用 CHAT 的当前模型，"换对话模型 = 换判官"，历史判定不可比（judgeModel 写进
--  verdict 也只能事后解释，不算"固定"）。
--
--  这次只改检查约束：`KIND` 是 nvarchar(16)，每用途一条当前模型的保证由既有过滤唯一索引
--  `UX_ASSISTANT_MODEL_ACTIVE_KIND`（KIND where IS_ACTIVE = 1）承担，加一个取值不需要新索引。
--  "每用途只有一个当前模型"因此对新用途同样成立，不需要第二条规则。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;

IF DB_NAME() <> N'EOS.ERP'
    THROW 51970, N'本脚本只允许在 EOS.ERP 数据库上执行。', 1;

IF OBJECT_ID(N'dbo.ASSISTANT_MODEL', N'U') IS NULL
    THROW 51971, N'ASSISTANT_MODEL 不存在，迁移终止。', 1;

/* ---------- 重建 KIND 检查约束：允许 JUDGE（幂等） ---------- */
DECLARE @definition nvarchar(max) =
    (SELECT cc.definition FROM sys.check_constraints cc
     WHERE cc.parent_object_id = OBJECT_ID(N'dbo.ASSISTANT_MODEL')
       AND cc.name = N'CK_ASSISTANT_MODEL_KIND');

IF @definition IS NULL
BEGIN
    ALTER TABLE dbo.ASSISTANT_MODEL WITH CHECK
        ADD CONSTRAINT CK_ASSISTANT_MODEL_KIND
        CHECK ([KIND] = N'CHAT' OR [KIND] = N'EMBEDDING' OR [KIND] = N'JUDGE');
    PRINT N'== CK_ASSISTANT_MODEL_KIND 已创建（含 JUDGE） ==';
END
ELSE IF @definition LIKE N'%JUDGE%'
BEGIN
    PRINT N'== CK_ASSISTANT_MODEL_KIND 已允许 JUDGE，跳过 ==';
END
ELSE
BEGIN
    ALTER TABLE dbo.ASSISTANT_MODEL DROP CONSTRAINT CK_ASSISTANT_MODEL_KIND;
    ALTER TABLE dbo.ASSISTANT_MODEL WITH CHECK
        ADD CONSTRAINT CK_ASSISTANT_MODEL_KIND
        CHECK ([KIND] = N'CHAT' OR [KIND] = N'EMBEDDING' OR [KIND] = N'JUDGE');
    PRINT N'== CK_ASSISTANT_MODEL_KIND 已重建（新增 JUDGE） ==';
END
