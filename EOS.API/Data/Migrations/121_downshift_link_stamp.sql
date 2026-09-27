-- ============================================================================
-- EOS.ERP migration 121: link-stamp 主表定位五例下沉为公式配置（方案 A 第三步·上）
-- ----------------------------------------------------------------------------
-- 本脚本只下沉"取值来自主表"的五例（2903/2904/3307/300301/300304）——它们与公式行的
--   OP_CODE=ASSIGN + SOURCE_SCOPE=MASTER + MATCH_STRUCT(主表定位键) 逐一对应：
--   UPDATE T SET T.<f> = (SELECT M.<f> FROM <主表> M WHERE <主键>)
--     FROM <目标> T WHERE T.<定位列> = (SELECT M.<定位源列> …)
--   与旧 `LinkStampHandler.BuildUpdate` 的
--   UPDATE T SET T.<f> = M.<f> FROM <目标> T JOIN <主表> M ON T.<refTarget>=M.<refSource>
--     WHERE M.<主键过滤>
--   同单据范围（主表按主键单行 ⇒ 标量子查询 ≡ JOIN）、同目标列、同取值。
-- `finish:true`（300301/300304）额外三条完工戳公式行：FINISHED_TAG=1、FINISHED_PERSON='SYSTEM'、
--   FINISHED_DATE=SYSDATETIME（与处理器 StampAssignments 的 finish 分支一致）。
-- 反向保持原 kind：`clear-refs`（清引用、SERIAL 列归 0）与 `clear-refs-unfinish`
--   （清引用 + FINISHED_TAG=0、FINISHED_PERSON='SYSTEM'、FINISHED_DATE=now），
--   已由公式解释器 `EffectFormulaExecutor.ClearOp` 承载。
-- 未下沉（另行处置，见 `docs/plans/次口径下沉方案A清单.md`）：
--   1404/1418/1606/1615/1616 —— 取值来自**明细行**，公式路径的明细标量取值会把字符/日期列的
--   NULL 包成 `ISNULL(...,0)`（漂移），且 DETAIL 定位键存在性判断未绑定本单（越界风险），
--   需先决定引擎口径再下沉。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

DECLARE @Actions TABLE (MODULE_ID INT, SEQ INT, ACTION_ID BIGINT, REVERSE_KIND NVARCHAR(40));
INSERT INTO @Actions (MODULE_ID, SEQ, ACTION_ID, REVERSE_KIND)
SELECT A.MODULE_ID, A.SEQ, A.ACTION_ID, ISNULL(JSON_VALUE(A.REVERSE_STRUCT, N'$.kind'), N'')
FROM dbo.MODULE_BUSINESS_ACTION A
WHERE A.EFFECT_KEY = N'link-stamp'
  AND ((A.MODULE_ID IN (2903, 2904, 3307) AND A.SEQ = 1)
    OR (A.MODULE_ID IN (300301, 300304) AND A.SEQ = 1));

IF (SELECT COUNT(*) FROM @Actions) <> 5
    THROW 50001, N'待下沉的 link-stamp 动作不是预期的 5 条，迁移中止（先核对配置）。', 1;

/* 清掉服务型占位公式行 */
DELETE O FROM dbo.MODULE_BUSINESS_ACTION_OP O JOIN @Actions A ON A.ACTION_ID = O.ACTION_ID;

/* 定位键（主表列 → 目标列） */
DECLARE @MatchAssess NVARCHAR(MAX) =
    N'[{"target":"ASSESS_TYPE","source":{"scope":"MASTER","field":"ASSESS_TYPE"}},'
    + N'{"target":"ASSESS_NO","source":{"scope":"MASTER","field":"ASSESS_NO"}}]';
DECLARE @MatchApply NVARCHAR(MAX) =
    N'[{"target":"APPLY_TYPE","source":{"scope":"MASTER","field":"APPLY_TYPE"}},'
    + N'{"target":"APPLY_NO","source":{"scope":"MASTER","field":"APPLY_NO"}}]';
DECLARE @MatchProduct NVARCHAR(MAX) =
    N'[{"target":"PRO_NO","source":{"scope":"MASTER","field":"PRO_NO"}}]';
DECLARE @MatchAccount NVARCHAR(MAX) =
    N'[{"target":"ACCOUNT_TYPE","source":{"scope":"MASTER","field":"ACCOUNT_TYPE"}},'
    + N'{"target":"ACCOUNT_NO","source":{"scope":"MASTER","field":"ACCOUNT_NO"}}]';
DECLARE @MatchSeal NVARCHAR(MAX) =
    N'[{"target":"SEAL_TYPE","source":{"scope":"MASTER","field":"SEAL_TYPE"}},'
    + N'{"target":"SEAL_NO","source":{"scope":"MASTER","field":"SEAL_NO"}}]';

/* 2903 开模申请单：把申请单号回写到方案评审单（同名字段） */
DECLARE @A2903 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 2903);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, MATCH_STRUCT, REMARK)
VALUES
    (@A2903, 1, N'MOU_ASSESS_M', N'APPLY_NO', N'ASSIGN', N'MASTER', N'APPLY_NO', @MatchAssess, N'方案评审单：申请单号');

/* 2904 模具承认单：把承认状态与承认单号回写到申请单 */
DECLARE @A2904 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 2904);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, MATCH_STRUCT, REMARK)
VALUES
    (@A2904, 1, N'MOU_APPLY_M', N'ACCEPT_STATE', N'ASSIGN', N'MASTER', N'ACCEPT_STATE', @MatchApply, N'申请单：承认状态'),
    (@A2904, 2, N'MOU_APPLY_M', N'ACCEPT_TYPE',  N'ASSIGN', N'MASTER', N'ACCEPT_TYPE',  @MatchApply, N'申请单：承认单别'),
    (@A2904, 3, N'MOU_APPLY_M', N'ACCEPT_NO',    N'ASSIGN', N'MASTER', N'ACCEPT_NO',    @MatchApply, N'申请单：承认单号');

/* 3307 样品管理：样品盒号回写到产品档（按品号定位） */
DECLARE @A3307 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 3307);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, MATCH_STRUCT, REMARK)
VALUES
    (@A3307, 1, N'PRODUCT', N'SAMPLE_BOX_NO', N'ASSIGN', N'MASTER', N'SAMPLE_BOX_NO', @MatchProduct, N'产品档：样品盒号');

/* 300301 直接出口报关单 / 300304 转厂报关单：回写报关单号并盖完工戳（finish=true） */
DECLARE @A300301 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 300301);
DECLARE @A300304 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 300304);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_FIELD, SOURCE_CONSTANT, MATCH_STRUCT, REMARK)
VALUES
    (@A300301, 1, N'CUS_ACCOUNT_M', N'EXPORT_TYPE',    N'ASSIGN', N'MASTER', N'EXPORT_TYPE',    NULL,          @MatchAccount, N'银行结存：报关单别'),
    (@A300301, 2, N'CUS_ACCOUNT_M', N'EXPORT_NO',      N'ASSIGN', N'MASTER', N'EXPORT_NO',      NULL,          @MatchAccount, N'银行结存：报关单号'),
    (@A300301, 3, N'CUS_ACCOUNT_M', N'FINISHED_TAG',   N'ASSIGN', N'CONSTANT', NULL,           N'1',          @MatchAccount, N'银行结存：完工标记'),
    (@A300301, 4, N'CUS_ACCOUNT_M', N'FINISHED_PERSON',N'ASSIGN', N'CONSTANT', NULL,           N'SYSTEM',     @MatchAccount, N'银行结存：完工人'),
    (@A300301, 5, N'CUS_ACCOUNT_M', N'FINISHED_DATE',  N'ASSIGN', N'CONSTANT', NULL,           N'SYSDATETIME',@MatchAccount, N'银行结存：完工日期'),
    (@A300301, 6, N'CUS_SEAL_M',    N'EXPORT_TYPE',    N'ASSIGN', N'MASTER', N'EXPORT_TYPE',    NULL,          @MatchSeal,    N'封条：报关单别'),
    (@A300301, 7, N'CUS_SEAL_M',    N'EXPORT_NO',      N'ASSIGN', N'MASTER', N'EXPORT_NO',      NULL,          @MatchSeal,    N'封条：报关单号'),
    (@A300301, 8, N'CUS_SEAL_M',    N'FINISHED_TAG',   N'ASSIGN', N'CONSTANT', NULL,           N'1',          @MatchSeal,    N'封条：完工标记'),
    (@A300301, 9, N'CUS_SEAL_M',    N'FINISHED_PERSON',N'ASSIGN', N'CONSTANT', NULL,           N'SYSTEM',     @MatchSeal,    N'封条：完工人'),
    (@A300301, 10, N'CUS_SEAL_M',   N'FINISHED_DATE',  N'ASSIGN', N'CONSTANT', NULL,           N'SYSDATETIME',@MatchSeal,    N'封条：完工日期'),
    (@A300304, 1, N'CUS_ACCOUNT_M', N'EXPORT_TYPE',    N'ASSIGN', N'MASTER', N'EXPORT_TYPE',    NULL,          @MatchAccount, N'银行结存：报关单别'),
    (@A300304, 2, N'CUS_ACCOUNT_M', N'EXPORT_NO',      N'ASSIGN', N'MASTER', N'EXPORT_NO',      NULL,          @MatchAccount, N'银行结存：报关单号'),
    (@A300304, 3, N'CUS_ACCOUNT_M', N'FINISHED_TAG',   N'ASSIGN', N'CONSTANT', NULL,           N'1',          @MatchAccount, N'银行结存：完工标记'),
    (@A300304, 4, N'CUS_ACCOUNT_M', N'FINISHED_PERSON',N'ASSIGN', N'CONSTANT', NULL,           N'SYSTEM',     @MatchAccount, N'银行结存：完工人'),
    (@A300304, 5, N'CUS_ACCOUNT_M', N'FINISHED_DATE',  N'ASSIGN', N'CONSTANT', NULL,           N'SYSDATETIME',@MatchAccount, N'银行结存：完工日期'),
    (@A300304, 6, N'CUS_SEAL_M',    N'EXPORT_TYPE',    N'ASSIGN', N'MASTER', N'EXPORT_TYPE',    NULL,          @MatchSeal,    N'封条：报关单别'),
    (@A300304, 7, N'CUS_SEAL_M',    N'EXPORT_NO',      N'ASSIGN', N'MASTER', N'EXPORT_NO',      NULL,          @MatchSeal,    N'封条：报关单号'),
    (@A300304, 8, N'CUS_SEAL_M',    N'FINISHED_TAG',   N'ASSIGN', N'CONSTANT', NULL,           N'1',          @MatchSeal,    N'封条：完工标记'),
    (@A300304, 9, N'CUS_SEAL_M',    N'FINISHED_PERSON',N'ASSIGN', N'CONSTANT', NULL,           N'SYSTEM',     @MatchSeal,    N'封条：完工人'),
    (@A300304, 10, N'CUS_SEAL_M',   N'FINISHED_DATE',  N'ASSIGN', N'CONSTANT', NULL,           N'SYSDATETIME',@MatchSeal,    N'封条：完工日期');

/* 动作改为公式键（服务参数不再需要；反向结构保持原 kind） */
UPDATE A
   SET A.EFFECT_KEY = N'field-accumulate',
       A.PARAM_STRUCT = NULL,
       A.EFFECT_NAME = A.EFFECT_NAME + N'（公式）',
       A.LAST_UPDATE_BY = N'DbUp',
       A.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.MODULE_BUSINESS_ACTION A JOIN @Actions X ON X.ACTION_ID = A.ACTION_ID;

/* 守卫：五条动作必须都是公式键、参数已清空、各有真实公式行、反向 kind 未被改动 */
IF EXISTS (
    SELECT 1 FROM @Actions X
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
                      WHERE A.ACTION_ID = X.ACTION_ID AND A.EFFECT_KEY = N'field-accumulate'
                        AND A.PARAM_STRUCT IS NULL
                        AND ISNULL(JSON_VALUE(A.REVERSE_STRUCT, N'$.kind'), N'') = X.REVERSE_KIND)
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION_OP O
                      WHERE O.ACTION_ID = X.ACTION_ID AND LTRIM(RTRIM(ISNULL(O.OP_CODE,''))) <> '')
)
    THROW 50002, N'下沉后仍有动作不是公式键、参数未清空、反向 kind 变化或缺少真实公式行，迁移中止。', 1;

PRINT N'== link-stamp 主表定位五例下沉完成（2903/2904/3307 各 1/3/1 行；300301/300304 各 10 行）==';
