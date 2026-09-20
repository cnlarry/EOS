-- ============================================================================
-- EOS.ERP migration 205: 2705 / 2706 工序工单数量校验的诊断列补聚合
-- ----------------------------------------------------------------------------
-- 现象：2705 工序工单、2706 工序完工单在**真的超出上限**时，保存返回 **500**，而不是
-- "以下工序工单超出工单制程数量…"那句可读文案。
--
-- 根因：两条规则（RULE_ID 10337 / 10338）都是**分组形态**（`thisQty.agg=SUM`），诊断列却按
-- "口径一（每组一行）"渲染了一个**非分组键**的源列——`{"scope":"SOURCE","field":"PROCESS_QTY"}`
-- 生成的是 `S1.[PROCESS_QTY]`。分组子查询只投影 match 键与求和别名（`SUM(S.PROCESS_QTY)`），
-- 该原列在 `S1` 里根本不存在 ⇒ 诊断查询报"列名无效"。已实测复现：
--   `SELECT S1.[PROCESS_QTY] FROM (SELECT S.PRODUCE_TYPE, S.PRODUCE_NO, S.PRODUCE_SERIAL_NO,
--    SUM(S.PROCESS_QTY) AS [__TQ_0] FROM dbo.MOC_WORK_D S GROUP BY …) S1` ⇒ Msg 207 列名无效。
-- 只在"命中违规"时才走诊断查询，所以平时看不出来——**报错恰好发生在最该给出解释的那一刻**。
--
-- 处置：给这两条诊断列加上 `"agg":"SUM"`——AGG 声明会把该聚合**放进分组子查询**再投影，外层
-- 取到的是本组该列的合计值，正是文案里"单据数量"那一列（本单合计）。规则文案与列序不变。
--
-- 影响面：只有这两条规则。运行期读的是**已发布快照**里的校验规则，故本迁移同时把两个模块标脏；
-- 必须重发布 2705 / 2706 才生效。
--
-- 幂等：按 RULE_ID 更新；两条规则都已是聚合形态时**空转且不重新标脏**（否则重跑会把已经
-- 发布过的模块重新标成"待发布"，界面显示与事实不符）。
-- 回滚：把 `,"agg":"SUM"` 从对应诊断列中移除（并重新标脏）。
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
    THROW 52720, @GUARD_MESSAGE, 1;

DECLARE @targets TABLE (RULE_ID INT PRIMARY KEY, MODULE_ID INT NOT NULL, NEEDLE NVARCHAR(200) NOT NULL, PATCH NVARCHAR(220) NOT NULL);
INSERT INTO @targets (RULE_ID, MODULE_ID, NEEDLE, PATCH) VALUES
    (10337, 2705, N'{"scope":"SOURCE","field":"PROCESS_QTY"}', N'{"scope":"SOURCE","field":"PROCESS_QTY","agg":"SUM"}'),
    (10338, 2706, N'{"scope":"SOURCE","field":"QTY"}',         N'{"scope":"SOURCE","field":"QTY","agg":"SUM"}');

/* ---------- 前提守卫：两条规则都在，且形态与预期一致 ---------- */
IF EXISTS (SELECT 1 FROM @targets t WHERE NOT EXISTS
        (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE r WHERE r.RULE_ID = t.RULE_ID AND r.MODULE_ID = t.MODULE_ID))
    THROW 52721, N'规则 10337 / 10338 与模块 2705 / 2706 的对应关系与预期不符，迁移中止。', 1;

DECLARE @ruleId INT, @moduleId INT, @needle NVARCHAR(200), @patch NVARCHAR(220), @current NVARCHAR(MAX);

/* ---------- 幂等短路：两条都已是聚合形态则整体空转（不标脏、不重复断言） ---------- */
IF NOT EXISTS (SELECT 1 FROM @targets t WHERE CHARINDEX(t.PATCH,
        (SELECT PARAM_STRUCT FROM dbo.MODULE_VALIDATION_RULE WHERE RULE_ID = t.RULE_ID)) = 0)
BEGIN
    PRINT N'== 规则 10337 / 10338 已是源列聚合形态，迁移空转 ==';
    RETURN;
END

DECLARE cur CURSOR LOCAL FAST_FORWARD FOR SELECT RULE_ID, MODULE_ID, NEEDLE, PATCH FROM @targets ORDER BY RULE_ID;
DECLARE @patched TABLE (RULE_ID INT PRIMARY KEY, MODULE_ID INT NOT NULL);
OPEN cur;
FETCH NEXT FROM cur INTO @ruleId, @moduleId, @needle, @patch;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @current = (SELECT PARAM_STRUCT FROM dbo.MODULE_VALIDATION_RULE WHERE RULE_ID = @ruleId);

    -- 已修过则跳过该条（幂等）
    IF CHARINDEX(@patch, @current) = 0
    BEGIN
        -- 待改的诊断列必须恰好出现一次：多处出现说明形态变了，逐处确认前不做批量替换
        IF (LEN(@current) - LEN(REPLACE(@current, @needle, N''))) / LEN(@needle) <> 1
            THROW 52722, N'规则的诊断列形态与预期不符（未找到唯一的待改片段），迁移中止。', 1;

        UPDATE dbo.MODULE_VALIDATION_RULE
           SET PARAM_STRUCT = REPLACE(@current, @needle, @patch),
               LAST_UPDATE_BY = N'DbUp',
               LAST_UPDATE_DATE = SYSDATETIME()
         WHERE RULE_ID = @ruleId;

        /* ---------- 标记待发布：运行期读的是已发布快照 ---------- */
        MERGE dbo.WORKBENCH_MODULE_DIRTY AS T
        USING (SELECT @moduleId AS MODULE_ID) AS S ON T.MODULE_ID = S.MODULE_ID
        WHEN MATCHED THEN UPDATE SET DIRTY_TAG = 1, LAST_MODIFIED_BY = N'DbUp', LAST_MODIFIED_AT = SYSDATETIME()
        WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
            VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

        INSERT INTO @patched (RULE_ID, MODULE_ID) VALUES (@ruleId, @moduleId);
    END

    FETCH NEXT FROM cur INTO @ruleId, @moduleId, @needle, @patch;
END
CLOSE cur;
DEALLOCATE cur;

/* ---------- 收口断言（只对本轮真正改过的规则/模块判定） ---------- */
IF EXISTS (SELECT 1 FROM @patched p
           WHERE CHARINDEX((SELECT PATCH FROM @targets t WHERE t.RULE_ID = p.RULE_ID),
                   (SELECT PARAM_STRUCT FROM dbo.MODULE_VALIDATION_RULE WHERE RULE_ID = p.RULE_ID)) = 0)
    THROW 52723, N'规则的诊断列未写入 agg，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM @patched p
           WHERE NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = p.MODULE_ID AND d.DIRTY_TAG = 1))
    THROW 52724, N'模块未标记为待发布，迁移中止。', 1;

PRINT N'== 2705 / 2706 的分组诊断列已改为源列聚合（SUM），两个模块待重发布 ==';
