-- ============================================================================
-- 产品制程（2703）：给「固定时间不能为 0」补上数值断言
--
-- 该规则的 PARAM_STRUCT 只有触发条件（`STANDARD_TIME_TAG = 1`），没有 `assert`，
-- 于是走 line-require 的默认形态「字段已填」——`STANDARD_TIME = 0` 不是空值，
-- 判据不成立，等于这条校验从未拦过任何东西（旧过程 P_SFC_PROCESS_After_Save 的判据是
-- `STANDARD_TIME_TAG = 1 AND STANDARD_TIME = 0`，NULL 放行）。
--
-- 补 `{"op":"GT","value":0,"nullSkips":true}` 复刻旧行为：
--   · 违规 = NOT(STANDARD_TIME > 0) ⇒ 0 与负数命中；
--   · `nullSkips` 让 NULL 参与比较时得到 UNKNOWN ⇒ 不命中（与旧过程的 `= 0` 同口径）。
--
-- 校验规则随定义快照下发，故同批标脏 2703（重新发布后生效）。
-- 幂等：已经有 assert 的规则不改写。末尾断言 op 落在库内。
-- ============================================================================
SET NOCOUNT ON;
SET XACT_ABORT ON;
BEGIN TRANSACTION;

DECLARE @params NVARCHAR(MAX) = (
    SELECT PARAM_STRUCT FROM dbo.MODULE_VALIDATION_RULE
     WHERE M_IDX = 2703 AND VALIDATION_KEY = N'line-require');
IF @params IS NULL
BEGIN
    RAISERROR(N'[429] 2703 的 line-require 规则不存在，拒绝继续', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

IF JSON_VALUE(@params, '$.checks[0].assert.op') IS NULL
BEGIN
    UPDATE dbo.MODULE_VALIDATION_RULE
       SET PARAM_STRUCT = JSON_MODIFY(@params, '$.checks[0].assert',
               JSON_QUERY(N'{"op":"GT","value":0,"nullSkips":true}')),
           LAST_UPDATE_BY = N'EOS-MIG',
           LAST_UPDATE_DATE = SYSDATETIME()
     WHERE M_IDX = 2703 AND VALIDATION_KEY = N'line-require';
END

-- 断言：断言条件必须真的落在规则里，否则这条迁移什么也没修好
IF ISNULL(JSON_VALUE((SELECT PARAM_STRUCT FROM dbo.MODULE_VALIDATION_RULE
                       WHERE M_IDX = 2703 AND VALIDATION_KEY = N'line-require'),
                     '$.checks[0].assert.op'), N'') <> N'GT'
BEGIN
    RAISERROR(N'[429] 补 assert 后仍读不到 op=GT，回滚', 16, 1);
    ROLLBACK TRANSACTION;
    RETURN;
END

MERGE dbo.WORKBENCH_MODULE_DIRTY AS t
USING (SELECT M_IDX FROM (VALUES (2703)) v(M_IDX)) AS s
   ON t.M_IDX = s.M_IDX
WHEN MATCHED THEN
    UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'EOS-MIG', LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED BY TARGET THEN
    INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (s.M_IDX, 1, N'EOS-MIG', SYSDATETIME());

PRINT N'[429] 2703 line-require 已补 assert（GT 0 / nullSkips）；已标脏 2703。';

COMMIT;
