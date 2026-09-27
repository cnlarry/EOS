-- ============================================================================
-- EOS.ERP migration 200: 按模块写入「模块判别字段」（保存期常量）
-- ----------------------------------------------------------------------------
-- 这批模块的过滤条件依赖一个**判别字段**（托外 / 补料 / 模具性质 / 扣款 / 报关方式）：
--
--   MOC_GET_M.OUTSIDE_TAG / REWORK_TAG   → 1503 / 1514 / 2805 / 2806 共用一张表
--   MOC_PRODUCE_M.OUTSIDE_TAG / REWORK_TAG → 1502 / 1512 / 2803 / 2804
--   COP_RETURN_M.SEND_TAG / PUR_CANCEL_M.SEND_TAG → 1407 vs 1409、1608 vs 1612
--   MOU_BATCHTOP_M.MOU_SORT              → 2915 = '2' / 2916 = '3'
--   CUS_EXPORT_M / CUS_IMPORT_M.DECLARE_TYPE → 300301/300302 = '1'、300304/300305 = '2'
--
-- 这个字段在表单上是**只读**的（前端渲染 disabled ⇒ 不会提交），而效果目录里**没有任何
-- 动作写它** ⇒ 新建记录取列默认值（多为 0），随即被模块**自己的过滤器**拒掉
-- （RECORD_OUT_OF_MODULE_FILTER）—— **用户从工作台建不出这些单**。
-- 既有实现是靠页面代码直接写死的（里就有
-- `chk_OUTSIDE_TAG.Checked = true` / `chk_REWORK_TAG.Checked = true`），移植时漏了这一步。
--
-- 本迁移按模块在 **SAVE 期**用 `set-state` 写入该常量。**前提**：创建路径的
-- "记录是否仍在模块范围内"判定必须排在效果链**之后**（与修改路径同一落点）——否则效果
-- 再写也来不及。该顺序调整见 `WorkbenchCommandHandler.SaveRecordAsync`。
--
-- 为什么写常量而不是把字段改成可编辑：判别字段是**模块身份**，不是用户输入。
--
-- 幂等：按 (MODULE_ID, EVENT_CODE='SAVE', SEQ) 合并；重复执行为更新。
-- 回滚：DELETE 本迁移按 SOURCE_REF='module-discriminator' 写入的动作行。
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
    THROW 52500, @GUARD_MESSAGE, 1;

/* ---------- 清单：模块 / 序号 / 期望主表 / 常量 ----------
   序号用 2 的三条（1512/2803/2804）是因为它们的 SAVE 期 SEQ=1 已被 `link-stamp` 占用。 */
DECLARE @specs TABLE (
    MODULE_ID INT NOT NULL PRIMARY KEY,
    SEQ INT NOT NULL,
    MASTER_TABLE SYSNAME NOT NULL,
    STATE NVARCHAR(200) NOT NULL,
    NOTE NVARCHAR(200) NOT NULL);

INSERT INTO @specs (MODULE_ID, SEQ, MASTER_TABLE, STATE, NOTE) VALUES
    (1409, 1, N'COP_RETURN_M',   N'{"SEND_TAG":1}',                          N'扣款退货单：SEND_TAG=1'),
    (1512, 2, N'MOC_PRODUCE_M',  N'{"REWORK_TAG":1}',                        N'重工生产单：REWORK_TAG=1'),
    (1514, 1, N'MOC_GET_M',      N'{"REWORK_TAG":1}',                        N'生产补料单：REWORK_TAG=1'),
    (1612, 1, N'PUR_CANCEL_M',   N'{"SEND_TAG":1}',                          N'扣款退料单：SEND_TAG=1'),
    (2803, 2, N'MOC_PRODUCE_M',  N'{"OUTSIDE_TAG":1}',                       N'托外生产单：OUTSIDE_TAG=1'),
    (2804, 2, N'MOC_PRODUCE_M',  N'{"OUTSIDE_TAG":1,"REWORK_TAG":1}',        N'托外重工单：OUTSIDE_TAG=1 且 REWORK_TAG=1'),
    (2805, 1, N'MOC_GET_M',      N'{"OUTSIDE_TAG":1}',                       N'托外领料单：OUTSIDE_TAG=1'),
    (2806, 1, N'MOC_GET_M',      N'{"OUTSIDE_TAG":1,"REWORK_TAG":1}',        N'托外补料单：OUTSIDE_TAG=1 且 REWORK_TAG=1'),
    (2915, 1, N'MOU_BATCHTOP_M', N'{"MOU_SORT":"2"}',                        N'上模开模单：MOU_SORT=2'),
    (2916, 1, N'MOU_BATCHTOP_M', N'{"MOU_SORT":"3"}',                        N'刀模开模单：MOU_SORT=3'),
    (300301, 1, N'CUS_EXPORT_M', N'{"DECLARE_TYPE":"1"}',                    N'出口报关单(直接出口)：DECLARE_TYPE=1'),
    (300302, 1, N'CUS_IMPORT_M', N'{"DECLARE_TYPE":"1"}',                    N'进口报关单(直接进口)：DECLARE_TYPE=1'),
    (300304, 1, N'CUS_EXPORT_M', N'{"DECLARE_TYPE":"2"}',                    N'出口报关单(转厂)：DECLARE_TYPE=2'),
    (300305, 1, N'CUS_IMPORT_M', N'{"DECLARE_TYPE":"2"}',                    N'进口报关单(转厂)：DECLARE_TYPE=2');

/* ---------- 前置守卫：模块存在、主表与期望一致、判别列是物理列 ---------- */
DECLARE @bad NVARCHAR(400) = (
    SELECT STRING_AGG(CONVERT(NVARCHAR(400), s.MODULE_ID), N', ')
    FROM @specs s
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m
                      WHERE m.M_IDX = s.MODULE_ID
                        AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = s.MASTER_TABLE));

IF @bad IS NOT NULL
BEGIN
    DECLARE @badMessage NVARCHAR(400) = N'以下模块不存在或主表与清单不符，迁移中止：' + @bad + N'。';
    THROW 52501, @badMessage, 1;
END

DECLARE @noColumn NVARCHAR(400) = (
    SELECT STRING_AGG(CONVERT(NVARCHAR(400), s.MODULE_ID), N', ')
    FROM @specs s
    WHERE EXISTS (
        SELECT 1 FROM OPENJSON(s.STATE) j
        WHERE COL_LENGTH(N'dbo.' + s.MASTER_TABLE, j.[key]) IS NULL));

IF @noColumn IS NOT NULL
BEGIN
    DECLARE @noColumnMessage NVARCHAR(400) = N'以下模块的判别列不是该主表的物理列，迁移中止：' + @noColumn + N'。';
    THROW 52502, @noColumnMessage, 1;
END

/* ---------- 写入保存期动作 ---------- */
DECLARE @moduleId INT, @seq INT, @table SYSNAME, @state NVARCHAR(200), @note NVARCHAR(200), @params NVARCHAR(MAX);
DECLARE spec_cursor CURSOR LOCAL FAST_FORWARD FOR
    SELECT MODULE_ID, SEQ, MASTER_TABLE, STATE, NOTE FROM @specs ORDER BY MODULE_ID;
OPEN spec_cursor;
FETCH NEXT FROM spec_cursor INTO @moduleId, @seq, @table, @state, @note;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @params = N'{"targets":["' + @table + N'"],"state":' + @state + N'}';

    MERGE dbo.MODULE_BUSINESS_ACTION AS T
    USING (SELECT @moduleId AS MODULE_ID, @seq AS SEQ) AS S
       ON T.MODULE_ID = S.MODULE_ID AND T.EVENT_CODE = N'SAVE' AND T.SEQ = S.SEQ
    WHEN MATCHED THEN
        UPDATE SET T.EFFECT_KEY = N'set-state', T.EFFECT_NAME = N'模块判别字段（保存期）',
                   T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.CONDITION_STRUCT = NULL,
                   T.PARAM_STRUCT = @params, T.REVERSE_STRUCT = N'{"kind":"none"}',
                   T.REMARK = @note, T.SOURCE_REF = N'module-discriminator',
                   T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
                PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
        VALUES (S.MODULE_ID, N'SAVE', S.SEQ, N'set-state', N'模块判别字段（保存期）', 1, N'BLOCK', NULL,
                @params, N'{"kind":"none"}', @note, N'module-discriminator',
                N'DbUp', SYSDATETIME(), N'DbUp', SYSDATETIME());

    FETCH NEXT FROM spec_cursor INTO @moduleId, @seq, @table, @state, @note;
END
CLOSE spec_cursor;
DEALLOCATE spec_cursor;

/* ---------- 标记待发布：运行时读的是已发布快照 ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT MODULE_ID FROM @specs) AS S
   ON D.MODULE_ID = S.MODULE_ID
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.MODULE_ID, 1, N'DbUp', SYSDATETIME());

/* ---------- 收口断言 ----------
   用 CHARINDEX 而不是 LIKE：JSON 里的 `[` 在 LIKE 模式中是**字符类**起始符，
   `LIKE '%"targets":["表名"]%'` 会静默匹配不上（本迁移第一版正是这么写的）。 */
IF EXISTS (
    SELECT 1 FROM @specs s
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                      WHERE a.MODULE_ID = s.MODULE_ID AND a.EVENT_CODE = N'SAVE' AND a.SEQ = s.SEQ
                        AND a.EFFECT_KEY = N'set-state' AND a.ENABLED = 1 AND a.FAIL_MODE = N'BLOCK'
                        AND CHARINDEX(N'"targets":["' + s.MASTER_TABLE + N'"]', ISNULL(a.PARAM_STRUCT, N'')) > 0))
    THROW 52503, N'有模块未配上判别字段动作，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM @specs s
           WHERE NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d
                             WHERE d.MODULE_ID = s.MODULE_ID AND d.DIRTY_TAG = 1))
    THROW 52504, N'有模块未标记为待发布，迁移中止。', 1;

PRINT N'== 已为 14 个模块配上「模块判别字段」保存期动作，模块待重发布 ==';
