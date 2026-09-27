-- ============================================================================
-- EOS.ERP migration 266: 结案钩子成对（ADR-020 §9.7 D7-⑥ / §10 WS-22）
-- ----------------------------------------------------------------------------
-- 迁移 246 给模块 `1502 制令单` 挂了 `ENDCASE` ⇒ `inventory-release-by-source`：
-- 结案释放该单名下的有效预留。**只挂了单向**——结案释放了，取消结案收不回来，
-- 于是占用行停在 `C` 而单据已回到未结案态，可用量与占用两边对不上。
--
-- 本迁移补三样：
--   ① 预留行上的**释放归因列** `INV_RESERVE.RELEASE_KIND`：
--      结案释放时写 `ENDCASE`，取消结案**只收回带该归因的行**并清空归因。
--      判据必须逐行归因，不能放宽成"复活该来源所有 STATUS='C' 的行"——
--      人工释放的预留同样停在 `C`，那是一次没有依据的扣减。
--   ② 模块 `1502` 的 `UNENDCASE` ⇒ `inventory-release-by-source` 配置行
--      （与 ENDCASE 行同键同 SEQ：唯一键是 (M_IDX, EVENT_CODE, SEQ)，两者互不冲突）。
--   ③ 出口断言：列与两向配置都必须就位，缺一即失败。
--
-- 幂等：列按 COL_LENGTH 判、配置行按 (M_IDX, EVENT_CODE, EFFECT_KEY) 判；
-- 重复执行只打印"已存在，跳过"，不改动任何既有行。
-- 回滚：DROP COLUMN RELEASE_KIND + 删 UNENDCASE 配置行（该列不回填历史释放行）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 55200, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

DECLARE @sourceModule INT = 1502;
DECLARE @effectKey NVARCHAR(100) = N'inventory-release-by-source';

/* ---------- ① 释放归因列 ---------- */
IF COL_LENGTH(N'dbo.INV_RESERVE', N'RELEASE_KIND') IS NULL
BEGIN
    /* 可空：既有行没有归因，表示它们的释放不归任何可逆动作管（不会被取消结案收回）。 */
    ALTER TABLE dbo.INV_RESERVE ADD RELEASE_KIND NVARCHAR(20) NULL;
    PRINT N'== 新增列：INV_RESERVE.RELEASE_KIND ==';
END
ELSE
    PRINT N'== 列 INV_RESERVE.RELEASE_KIND 已存在，跳过 ==';

/* ---------- ② 取消结案 ⇒ 收回预留（挂生命周期） ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION
                WHERE M_IDX = @sourceModule AND RTRIM(EVENT_CODE) = N'UNENDCASE'
                  AND RTRIM(EFFECT_KEY) = @effectKey)
BEGIN
    INSERT INTO dbo.MODULE_BUSINESS_ACTION
        (M_IDX, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE,
         CONDITION_STRUCT, PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF,
         CREATE_PERSON, CREATE_DATE, LABEL, CONFIRM_TAG)
        VALUES (@sourceModule, N'UNENDCASE', 7, @effectKey, N'取消结案收回预留', 1, N'BLOCK',
                NULL, NULL, NULL,
                N'ADR-020 §9.7 D7-⑥（WS-22）：制令单取消结案时，把当初由结案释放的那批预留收回并重算 USEABLE_QTY；无参数（身份来自框架）',
                N'ADR-020 §9.7', N'ADR020', GETDATE(), N'取消结案收回预留', 0);
    PRINT N'== 新增效果行：1502 UNENDCASE inventory-release-by-source ==';
END
ELSE
    PRINT N'== 效果行 1502/UNENDCASE 已存在，跳过 ==';

/* ---------- ③ 出口断言 ---------- */
/* 列必须存在且可空：NOT NULL 会让既有行（没有归因）插不进来，也会逼出"用空串冒充无归因"的写法。 */
IF COL_LENGTH(N'dbo.INV_RESERVE', N'RELEASE_KIND') IS NULL
    THROW 55201, N'INV_RESERVE.RELEASE_KIND 未建成：取消结案无法区分"由结案释放"与"人工释放"，收回判据不成立。', 1;

IF EXISTS (SELECT 1 FROM sys.columns c
            WHERE c.object_id = OBJECT_ID(N'dbo.INV_RESERVE') AND c.name = N'RELEASE_KIND' AND c.is_nullable = 0)
    THROW 55202, N'INV_RESERVE.RELEASE_KIND 被建成了 NOT NULL，迁移中止（无归因的行必须能存 NULL）。', 1;

/* 两个方向必须成对：只有 ENDCASE 会让"结案释放了、取消收不回"，只有 UNENDCASE 则是无源之水。 */
DECLARE @pair INT = (SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION
                      WHERE M_IDX = @sourceModule
                        AND RTRIM(EVENT_CODE) IN (N'ENDCASE', N'UNENDCASE')
                        AND RTRIM(EFFECT_KEY) = @effectKey AND ENABLED = 1);
IF @pair <> 2 THROW 55203, N'来源模块的结案/取消结案钩子未成对就位（需 ENDCASE 与 UNENDCASE 各一条且启用），迁移中止。', 1;

PRINT CONCAT(N'== 就位：释放归因列 1 列 / 结案钩子 ', @pair, N' 条（ENDCASE + UNENDCASE）==');

COMMIT TRANSACTION;
