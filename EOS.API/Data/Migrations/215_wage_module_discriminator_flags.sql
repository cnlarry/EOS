-- ============================================================================
-- EOS.ERP migration 216: 工资表四模块的判别字段（保存期常量）
-- ----------------------------------------------------------------------------
-- 180309/180310/1803091/1803101 共用主表 HR_WAGE_M，靠两列区分模块：
--
--   180309  保密每月工资表   IF_SECRECY=1 AND IF_DIMISSION=0
--   180310  离职工资表       IF_SECRECY=0 AND IF_DIMISSION=1
--   1803091 员工工资表       IF_SECRECY=0 AND IF_DIMISSION=0
--   1803101 保密离职工资表   IF_SECRECY=1 AND IF_DIMISSION=1
--
-- 这两列在库内**可空且无默认值**，表单上是**只读**（不提交），效果目录里也没有任何动作写它们
-- ⇒ 新建记录时两列为 NULL，`NULL=0` 不成立，模块自己的过滤器必然拒收
-- （实测 1803091 新建返回 400 RECORD_OUT_OF_MODULE_FILTER）——与迁移 200 处理的
-- 「判别字段只读且无人写 ⇒ 建不出单」同形，只是那批 14 个模块不含这四个。
--
-- 处置与 200 同口径：按模块在 SAVE 期用 `set-state` 写入本模块的判别常量。
-- 注意 SEQ 选择：180310 / 1803101 的 SAVE 期 SEQ=1 已被 `wage-month-doc-prune` 占用，
-- 因此这两条写 SEQ=2；180309 / 1803091 无既有 SAVE 动作，写 SEQ=1。
--
-- 幂等：按 (MODULE_ID, EVENT_CODE='SAVE', SEQ) 合并；重复执行为更新。
-- 回滚：DELETE 按 SOURCE_REF='wage-module-discriminator' 写入的动作行，并清脏标记。
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

DECLARE @specs TABLE (
    MODULE_ID INT NOT NULL PRIMARY KEY,
    SEQ INT NOT NULL,
    MASTER_TABLE SYSNAME NOT NULL,
    STATE NVARCHAR(200) NOT NULL,
    NOTE NVARCHAR(200) NOT NULL);

INSERT INTO @specs (MODULE_ID, SEQ, MASTER_TABLE, STATE, NOTE) VALUES
    (180309,  1, N'HR_WAGE_M', N'{"IF_SECRECY":1,"IF_DIMISSION":0}', N'保密每月工资表：IF_SECRECY=1 且 IF_DIMISSION=0'),
    (180310,  2, N'HR_WAGE_M', N'{"IF_SECRECY":0,"IF_DIMISSION":1}', N'离职工资表：IF_SECRECY=0 且 IF_DIMISSION=1'),
    (1803091, 1, N'HR_WAGE_M', N'{"IF_SECRECY":0,"IF_DIMISSION":0}', N'员工工资表：IF_SECRECY=0 且 IF_DIMISSION=0'),
    (1803101, 2, N'HR_WAGE_M', N'{"IF_SECRECY":1,"IF_DIMISSION":1}', N'保密离职工资表：IF_SECRECY=1 且 IF_DIMISSION=1');

/* ---------- 前置守卫：模块存在、主表一致、判别列是物理列 ---------- */
DECLARE @bad NVARCHAR(400) = (
    SELECT STRING_AGG(CONVERT(NVARCHAR(400), s.MODULE_ID), N', ')
    FROM @specs s
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULES m
                      WHERE m.M_IDX = s.MODULE_ID
                        AND LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = s.MASTER_TABLE));
IF @bad IS NOT NULL
BEGIN
    DECLARE @badMessage NVARCHAR(400) = N'以下模块不存在或主表与清单不符，迁移中止：' + @bad + N'。';
    THROW 52601, @badMessage, 1;
END

DECLARE @noColumn NVARCHAR(400) = (
    SELECT STRING_AGG(CONVERT(NVARCHAR(400), s.MODULE_ID), N', ')
    FROM @specs s
    WHERE EXISTS (SELECT 1 FROM OPENJSON(s.STATE) j
                  WHERE COL_LENGTH(N'dbo.' + s.MASTER_TABLE, j.[key]) IS NULL));
IF @noColumn IS NOT NULL
BEGIN
    DECLARE @noColumnMessage NVARCHAR(400) = N'以下模块的判别列不是该主表的物理列，迁移中止：' + @noColumn + N'。';
    THROW 52602, @noColumnMessage, 1;
END

/* 目标 SEQ 不得被非本次写入的动作占用（防覆盖既有保存期动作） */
DECLARE @occupied NVARCHAR(400) = (
    SELECT STRING_AGG(CONVERT(NVARCHAR(400), s.MODULE_ID), N', ')
    FROM @specs s
    WHERE EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                  WHERE a.MODULE_ID = s.MODULE_ID AND a.EVENT_CODE = N'SAVE' AND a.SEQ = s.SEQ
                    AND ISNULL(a.SOURCE_REF, N'') <> N'wage-module-discriminator'));
IF @occupied IS NOT NULL
BEGIN
    DECLARE @occupiedMessage NVARCHAR(400) = N'以下模块的目标 SEQ 已被其它保存期动作占用，迁移中止：' + @occupied + N'。';
    THROW 52603, @occupiedMessage, 1;
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
        UPDATE SET T.EFFECT_KEY = N'set-state', T.EFFECT_NAME = N'工资表模块判别字段（保存期）',
                   T.ENABLED = 1, T.FAIL_MODE = N'BLOCK', T.CONDITION_STRUCT = NULL,
                   T.PARAM_STRUCT = @params, T.REVERSE_STRUCT = N'{"kind":"none"}',
                   T.REMARK = @note, T.SOURCE_REF = N'wage-module-discriminator',
                   T.LAST_UPDATE_BY = N'DbUp', T.LAST_UPDATE_DATE = SYSDATETIME()
    WHEN NOT MATCHED THEN
        INSERT (MODULE_ID, EVENT_CODE, SEQ, EFFECT_KEY, EFFECT_NAME, ENABLED, FAIL_MODE, CONDITION_STRUCT,
                PARAM_STRUCT, REVERSE_STRUCT, REMARK, SOURCE_REF, CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
        VALUES (S.MODULE_ID, N'SAVE', S.SEQ, N'set-state', N'工资表模块判别字段（保存期）', 1, N'BLOCK', NULL,
                @params, N'{"kind":"none"}', @note, N'wage-module-discriminator',
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
   用 CHARINDEX 而不是 LIKE：JSON 里的 `[` 在 LIKE 模式中是字符类起始符，会静默匹配不上。 */
IF EXISTS (
    SELECT 1 FROM @specs s
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                      WHERE a.MODULE_ID = s.MODULE_ID AND a.EVENT_CODE = N'SAVE' AND a.SEQ = s.SEQ
                        AND a.EFFECT_KEY = N'set-state' AND a.ENABLED = 1 AND a.FAIL_MODE = N'BLOCK'
                        AND CHARINDEX(N'"targets":["' + s.MASTER_TABLE + N'"]', ISNULL(a.PARAM_STRUCT, N'')) > 0))
    THROW 52604, N'有模块未配上判别字段动作，迁移中止。', 1;

IF EXISTS (
    SELECT 1 FROM @specs s
    WHERE NOT EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a
                      WHERE a.MODULE_ID = s.MODULE_ID AND a.EVENT_CODE = N'SAVE' AND a.SEQ = s.SEQ
                        AND CHARINDEX(REPLACE(s.STATE, N' ', N''), REPLACE(ISNULL(a.PARAM_STRUCT, N''), N' ', N'')) > 0))
    THROW 52605, N'有模块的判别常量与清单不符，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM @specs s
           WHERE NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d
                             WHERE d.MODULE_ID = s.MODULE_ID AND d.DIRTY_TAG = 1))
    THROW 52606, N'有模块未标记为待发布，迁移中止。', 1;

PRINT N'== 已为 4 个工资表模块配上「模块判别字段」保存期动作，模块待重发布（180309 / 180310 / 1803091 / 1803101）==';
GO
