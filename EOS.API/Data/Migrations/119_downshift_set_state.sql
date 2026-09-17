-- ============================================================================
-- EOS.ERP migration 119: set-state 五例下沉为公式配置（方案 A 第二步）
-- ----------------------------------------------------------------------------
-- 语义对照（`SetStateHandler` targets/state 形态）：
--   UPDATE T SET T.<state 列> = <常量/当前时间> FROM dbo.<目标> T
--     JOIN dbo.<定位来源> D|M ON <refs> … WHERE <单据范围>
-- 即"按定位置状态位"——正对应公式行的
--   OP_CODE=ASSIGN + SOURCE_SCOPE=CONSTANT + SOURCE_CONSTANT + MATCH_STRUCT。
-- 定位键转换：
--   refs[].source      → MATCH 的 {target, source:{scope:"DETAIL", field}}（逐明细行定位）
--   refs[].masterSource→ MATCH 的 {target, source:{scope:"MASTER", field}}（按主表列定位）
-- 反向（解批）保持 `{"kind":"clear-finish"}`：公式解释器已按 set-state 的清理口径实现
--   （数值标记→0、SYSDATETIME 标记→NULL、文本标记→空串、复制值→NULL），故动作行不动。
-- 动作级 CONDITION_STRUCT 保留（2915/2916 的 BATCH_SORT='3' 开关仍在动作行上）。
-- 落地后动作键由 `set-state` 变为 `field-accumulate`（公式解释器），PARAM_STRUCT 置空，
-- 既有占位公式行清掉后写入真实 ASSIGN 行。幂等：按动作重写其公式行。
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

DECLARE @Actions TABLE (MODULE_ID INT, SEQ INT, ACTION_ID BIGINT);
INSERT INTO @Actions (MODULE_ID, SEQ, ACTION_ID)
SELECT A.MODULE_ID, A.SEQ, A.ACTION_ID FROM dbo.MODULE_BUSINESS_ACTION A
WHERE A.EFFECT_KEY = N'set-state'
  AND ((A.MODULE_ID = 1908 AND A.SEQ = 1)
    OR (A.MODULE_ID = 2913 AND A.SEQ = 2)
    OR (A.MODULE_ID IN (2915, 2916, 2917) AND A.SEQ = 1));

IF (SELECT COUNT(*) FROM @Actions) <> 5
    THROW 50001, N'待下沉的 set-state 动作不是预期的 5 条，迁移中止（先核对配置）。', 1;

/* 清掉服务型占位公式行 */
DELETE O FROM dbo.MODULE_BUSINESS_ACTION_OP O JOIN @Actions A ON A.ACTION_ID = O.ACTION_ID;

/* 定位键（各动作共用同一形态，逐动作声明） */
DECLARE @MatchMission NVARCHAR(MAX) =
    N'[{"target":"MISSION_TYPE","source":{"scope":"DETAIL","field":"MISSION_TYPE"}},'
    + N'{"target":"MISSION_NO","source":{"scope":"DETAIL","field":"MISSION_NO"}}]';
DECLARE @MatchApply NVARCHAR(MAX) =
    N'[{"target":"APPLY_TYPE","source":{"scope":"DETAIL","field":"APPLY_TYPE"}},'
    + N'{"target":"APPLY_NO","source":{"scope":"DETAIL","field":"APPLY_NO"}}]';
DECLARE @MatchBatch NVARCHAR(MAX) =
    N'[{"target":"BATCH_TYPE","source":{"scope":"DETAIL","field":"APPLY_TYPE"}},'
    + N'{"target":"BATCH_NO","source":{"scope":"DETAIL","field":"APPLY_NO"}}]';
DECLARE @MatchScrap NVARCHAR(MAX) =
    N'[{"target":"SCRAP_TYPE","source":{"scope":"MASTER","field":"SCRAP_TYPE"}},'
    + N'{"target":"SCRAP_NO","source":{"scope":"MASTER","field":"SCRAP_NO"}},'
    + N'{"target":"SERIAL_NO","source":{"scope":"MASTER","field":"SCRAP_SERIAL_NO"}}]';
DECLARE @MatchAccept NVARCHAR(MAX) =
    N'[{"target":"ACCEPT_TYPE","source":{"scope":"DETAIL","field":"ACCEPT_TYPE"}},'
    + N'{"target":"ACCEPT_NO","source":{"scope":"DETAIL","field":"ACCEPT_NO"}}]';

/* 1908 出车统计表：出车任务完工标记（FINISHED_TAG=true） */
DECLARE @A1908 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 1908 AND SEQ = 1);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_CONSTANT, MATCH_STRUCT, REMARK)
VALUES
    (@A1908, 1, N'CAR_MISSION_M', N'FINISHED_TAG', N'ASSIGN', N'CONSTANT', N'1', @MatchMission, N'出车任务完工标记');

/* 2913 模具耗料单：申请单与批次单结案盖章（完工标记 + 完工人 + 完工日期） */
DECLARE @A2913 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 2913 AND SEQ = 2);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_CONSTANT, MATCH_STRUCT, REMARK)
VALUES
    (@A2913, 1, N'MOU_APPLY_M', N'FINISHED_TAG',    N'ASSIGN', N'CONSTANT', N'1',          @MatchApply, N'申请单：结案标记'),
    (@A2913, 2, N'MOU_APPLY_M', N'FINISHED_PERSON', N'ASSIGN', N'CONSTANT', N'SYSTEM',     @MatchApply, N'申请单：完工人'),
    (@A2913, 3, N'MOU_APPLY_M', N'FINISHED_DATE',   N'ASSIGN', N'CONSTANT', N'SYSDATETIME',@MatchApply, N'申请单：完工日期'),
    (@A2913, 4, N'MOU_BATCH_M', N'FINISHED_TAG',    N'ASSIGN', N'CONSTANT', N'1',          @MatchBatch, N'批次单：结案标记'),
    (@A2913, 5, N'MOU_BATCH_M', N'FINISHED_PERSON', N'ASSIGN', N'CONSTANT', N'SYSTEM',     @MatchBatch, N'批次单：完工人'),
    (@A2913, 6, N'MOU_BATCH_M', N'FINISHED_DATE',   N'ASSIGN', N'CONSTANT', N'SYSDATETIME',@MatchBatch, N'批次单：完工日期');

/* 2915 上模开模单 / 2916 刀模开模单：报废明细开模状态（BATCH_STATE=true，按主表列定位） */
DECLARE @A2915 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 2915 AND SEQ = 1);
DECLARE @A2916 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 2916 AND SEQ = 1);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_CONSTANT, MATCH_STRUCT, REMARK)
VALUES
    (@A2915, 1, N'MOU_SCRAP_D', N'BATCH_STATE', N'ASSIGN', N'CONSTANT', N'1', @MatchScrap, N'报废明细：开模状态'),
    (@A2916, 1, N'MOU_SCRAP_D', N'BATCH_STATE', N'ASSIGN', N'CONSTANT', N'1', @MatchScrap, N'报废明细：开模状态');

/* 2917 承认单模具报废：承认单结案盖章 */
DECLARE @A2917 BIGINT = (SELECT ACTION_ID FROM @Actions WHERE MODULE_ID = 2917 AND SEQ = 1);
INSERT INTO dbo.MODULE_BUSINESS_ACTION_OP
    (ACTION_ID, OP_SEQ, TARGET_TABLE, TARGET_FIELD, OP_CODE, SOURCE_SCOPE, SOURCE_CONSTANT, MATCH_STRUCT, REMARK)
VALUES
    (@A2917, 1, N'MOU_ACCEPT_M', N'FINISHED_TAG',    N'ASSIGN', N'CONSTANT', N'1',          @MatchAccept, N'承认单：结案标记'),
    (@A2917, 2, N'MOU_ACCEPT_M', N'FINISHED_DATE',   N'ASSIGN', N'CONSTANT', N'SYSDATETIME',@MatchAccept, N'承认单：完工日期'),
    (@A2917, 3, N'MOU_ACCEPT_M', N'FINISHED_PERSON', N'ASSIGN', N'CONSTANT', N'SYSTEM',     @MatchAccept, N'承认单：完工人');

/* 动作改为公式键（服务参数不再需要；反向 clear-finish 保留） */
UPDATE A
   SET A.EFFECT_KEY = N'field-accumulate',
       A.PARAM_STRUCT = NULL,
       A.EFFECT_NAME = A.EFFECT_NAME + N'（公式）',
       A.LAST_UPDATE_BY = N'DbUp',
       A.LAST_UPDATE_DATE = SYSDATETIME()
FROM dbo.MODULE_BUSINESS_ACTION A JOIN @Actions X ON X.ACTION_ID = A.ACTION_ID;

/* 守卫：五条动作必须都是公式键、参数已清空、且各有真实公式行（无空算子行） */
IF EXISTS (
    SELECT 1 FROM @Actions X
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION A
                      WHERE A.ACTION_ID = X.ACTION_ID AND A.EFFECT_KEY = N'field-accumulate'
                        AND A.PARAM_STRUCT IS NULL)
       OR NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION_OP O
                      WHERE O.ACTION_ID = X.ACTION_ID AND LTRIM(RTRIM(ISNULL(O.OP_CODE,''))) <> '')
)
    THROW 50002, N'下沉后仍有动作不是公式键、参数未清空或缺少真实公式行，迁移中止。', 1;

/* 守卫：反向结构必须仍是 clear-finish（解批清理口径依赖它） */
IF EXISTS (
    SELECT 1 FROM @Actions X JOIN dbo.MODULE_BUSINESS_ACTION A ON A.ACTION_ID = X.ACTION_ID
    WHERE ISNULL(JSON_VALUE(A.REVERSE_STRUCT, N'$.kind'), N'') <> N'clear-finish'
)
    THROW 50003, N'下沉后反向结构不再是 clear-finish，迁移中止（解批清理会丢）。', 1;

PRINT N'== set-state 五例下沉完成（1908/SEQ1 → 1 行；2913/SEQ2 → 6 行；2915/2916/SEQ1 → 各 1 行；2917/SEQ1 → 3 行）==';
