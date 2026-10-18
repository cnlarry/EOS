-- ============================================================================
-- EOS.ERP migration 416: 产品BOM 明细引入**子件版本**（ELEMENT_EDITION）
-- ----------------------------------------------------------------------------
-- 目标：明细行显式记录"这个子件用它的哪一版 BOM"，取代逐层运行时选版
--       （`EDITION_PICK` + `@rootEdition` 锁版 + 回退最大）。
--
-- 语义变化（这是本轮的核心）：
--   之前：子件用哪一版由**运行时**按规则算——优先根节点版次，没有则取该子件最大版次，
--         并标 `IS_FALLBACK`。同一份数据换一个根版次，展开结果会变。
--   之后：由**明细行**写死。子件版本成为这份 BOM 的一部分，与用量、损耗率同级。
--
-- 为什么只在"有下阶 BOM 的子件"上必填：
--   材料没有 BOM，填任何版本都不成立。所以列可空，但校验规则
--   （`require-when-referenced`，迁移内配置）要求：目标表 BOM_STRU_M 里存在该料号时，
--   版本必须非空且组合必须命中。半成品必填、材料留空，两者都由这一条兜住。
--
-- 回填口径（保证上线后展开结果与现在一致）：
--   ① 子件有"与父件相同的版次" → 填该版次（父子同版是最自然的约定）；
--   ② 否则填子件的最大版次（等价于现在的"回退最大"）；
--   ③ 子件没有 BOM → 留 NULL（材料）。
--
-- 工单 BOM（MOC_BOM_STRU_D）同步加列：快照的意义是"当时是什么样"，
--   缺了子件版本，半年后说不清那一单用的是 A1 的哪一版。
--
-- **为什么到处包 sp_executesql**：`ALTER TABLE ADD COLUMN` 与"引用该列"的语句在同一
--   批次里会被**整批预编译**——编译那一刻新列还不存在，于是报"列名无效"。把后续语句
--   放进动态 SQL，让它在新列就位之后再编译。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Mig NVARCHAR(100) = N'DbUp';
DECLARE @ModuleId INT = 1204;

IF OBJECT_ID(N'dbo.BOM_STRU_D', N'U') IS NULL
    THROW 57601, N'BOM_STRU_D 不存在，迁移中止。', 1;
IF OBJECT_ID(N'dbo.BOM_STRU_M', N'U') IS NULL
    THROW 57602, N'BOM_STRU_M 不存在，迁移中止。', 1;

BEGIN TRANSACTION;

/* ---------------------------------------------------------------- ① 明细表加列 */
IF COL_LENGTH(N'dbo.BOM_STRU_D', N'ELEMENT_EDITION') IS NULL
BEGIN
    EXEC sp_executesql N'ALTER TABLE dbo.BOM_STRU_D ADD ELEMENT_EDITION nchar(2) NULL;';
    PRINT N'[416] BOM_STRU_D.ELEMENT_EDITION 已新增';
END
ELSE
    PRINT N'[416] BOM_STRU_D.ELEMENT_EDITION 已存在，跳过';

/* ---------------------------------------------------------------- ② 回填 */
EXEC sp_executesql N'
UPDATE d
   SET d.ELEMENT_EDITION = COALESCE(
       (SELECT TOP 1 LTRIM(RTRIM(m.EDITION)) FROM dbo.BOM_STRU_M m WITH (NOLOCK)
         WHERE LTRIM(RTRIM(m.PRO_NO)) = LTRIM(RTRIM(d.ELEMENT_PRO_NO))
           AND LTRIM(RTRIM(m.EDITION)) = LTRIM(RTRIM(d.EDITION))),
       (SELECT MAX(LTRIM(RTRIM(m.EDITION))) FROM dbo.BOM_STRU_M m WITH (NOLOCK)
         WHERE LTRIM(RTRIM(m.PRO_NO)) = LTRIM(RTRIM(d.ELEMENT_PRO_NO))))
  FROM dbo.BOM_STRU_D d
 WHERE d.ELEMENT_EDITION IS NULL;';
PRINT N'[416] BOM_STRU_D 子件版本回填：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ---------------------------------------------------------------- ③ 组合索引 */
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_BOM_STRU_D_ELEMENT_EDITION' AND object_id = OBJECT_ID(N'dbo.BOM_STRU_D'))
BEGIN
    EXEC sp_executesql N'
        CREATE NONCLUSTERED INDEX IX_BOM_STRU_D_ELEMENT_EDITION
            ON dbo.BOM_STRU_D (ELEMENT_PRO_NO ASC, ELEMENT_EDITION ASC);';
    PRINT N'[416] 索引 IX_BOM_STRU_D_ELEMENT_EDITION 已建立';
END

/* ---------------------------------------------------------------- ④ 字段元数据 */
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'ELEMENT_EDITION')
BEGIN
    INSERT INTO dbo.FIELDS
        (T_ID, F_ID, F_DESC, F_TYPE, REGEX, DISPLAY_LENGTH, IS_VISIBLE, IS_READONLY,
         IS_QUERY, CAN_COPY, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES
        (N'BOM_STRU_D', N'ELEMENT_EDITION', N'子件版次', N'nvarchar', N'^\d{2}$', 80, 1, 0,
         1, 1, @Mig, SYSDATETIME());
    PRINT N'[416] FIELDS 新增 BOM_STRU_D.ELEMENT_EDITION';
END

/* ---------------------------------------------------------------- ⑤ 校验规则 */
DECLARE @EditionMessage NVARCHAR(300) =
    N'以下序号项的子件有下阶 BOM，必须指定子件版次 ' + CHAR(13) + CHAR(10) + N'{ROWS}';
/* PARAM_STRUCT 里的换行必须是转义的 \r\n（JSON 不允许裸控制字符） */
DECLARE @EditionParam NVARCHAR(MAX) =
    N'{"checks":[{"sourceField":"ELEMENT_PRO_NO","requiredField":"ELEMENT_EDITION",'
    + N'"targetTable":"BOM_STRU_M","targetKeyField":"PRO_NO","targetValueField":"EDITION",'
    + N'"message":"以下序号项的子件有下阶 BOM，必须指定子件版次 \r\n{ROWS}","lineField":"SERIAL_NO"}]}';

MERGE dbo.MODULE_VALIDATION_RULE AS T
USING (SELECT @ModuleId AS M_IDX) AS S
   ON T.M_IDX = S.M_IDX AND T.STAGE = N'SAVE'
  AND T.VALIDATION_KEY = N'require-when-referenced' AND T.SEQ = 6
WHEN MATCHED THEN
    UPDATE SET T.PARAM_STRUCT = @EditionParam, T.MESSAGE = @EditionMessage, T.ENABLED = 1,
               T.REMARK = N'子件有下阶 BOM 时必须指定其版次（材料无 BOM，留空）',
               T.LAST_UPDATE_BY = @Mig, T.LAST_UPDATE_DATE = SYSDATETIME()
WHEN NOT MATCHED THEN
    INSERT (M_IDX, STAGE, SEQ, VALIDATION_KEY, ENABLED, PARAM_STRUCT, MESSAGE, REMARK,
            CREATE_PERSON, CREATE_DATE, LAST_UPDATE_BY, LAST_UPDATE_DATE)
    VALUES (@ModuleId, N'SAVE', 6, N'require-when-referenced', 1, @EditionParam, @EditionMessage,
            N'子件有下阶 BOM 时必须指定其版次（材料无 BOM，留空）',
            @Mig, SYSDATETIME(), @Mig, SYSDATETIME());

/* ---------------------------------------------------------------- ⑥ 工单 BOM 同步 */
IF OBJECT_ID(N'dbo.MOC_BOM_STRU_D', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.MOC_BOM_STRU_D', N'ELEMENT_EDITION') IS NULL
    BEGIN
        EXEC sp_executesql N'ALTER TABLE dbo.MOC_BOM_STRU_D ADD ELEMENT_EDITION nchar(2) NULL;';
        PRINT N'[416] MOC_BOM_STRU_D.ELEMENT_EDITION 已新增';
    END

    EXEC sp_executesql N'
        UPDATE d
           SET d.ELEMENT_EDITION = (SELECT MAX(LTRIM(RTRIM(m.EDITION))) FROM dbo.BOM_STRU_M m WITH (NOLOCK)
                                     WHERE LTRIM(RTRIM(m.PRO_NO)) = LTRIM(RTRIM(d.ELEMENT_PRO_NO)))
          FROM dbo.MOC_BOM_STRU_D d
         WHERE d.ELEMENT_EDITION IS NULL;';
    PRINT N'[416] MOC_BOM_STRU_D 子件版本回填：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';
END
ELSE
    PRINT N'[416] MOC_BOM_STRU_D 不存在，跳过工单侧';

/* ---------------------------------------------------------------- ⑦ 断言 */
IF COL_LENGTH(N'dbo.BOM_STRU_D', N'ELEMENT_EDITION') IS NULL
    THROW 57603, N'BOM_STRU_D.ELEMENT_EDITION 未建立，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM sys.indexes
               WHERE name = N'IX_BOM_STRU_D_ELEMENT_EDITION' AND object_id = OBJECT_ID(N'dbo.BOM_STRU_D'))
    THROW 57604, N'组合索引未建立，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS
               WHERE T_ID = N'BOM_STRU_D' AND F_ID = N'ELEMENT_EDITION'
                 AND ISNULL(REGEX, N'') = N'^\d{2}$')
    THROW 57605, N'子件版次字段元数据未就位，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE
               WHERE M_IDX = @ModuleId AND STAGE = N'SAVE'
                 AND VALIDATION_KEY = N'require-when-referenced' AND ENABLED = 1)
    THROW 57606, N'子件版本必填校验未启用，迁移中止。', 1;

/* 有 BOM 的子件必须都已回填；材料必须仍为空。两条都在动态 SQL 里（引用新列）。 */
DECLARE @MissingEdition INT;
DECLARE @BadEdition INT;
EXEC sp_executesql N'
SELECT @Missing = COUNT(*) FROM dbo.BOM_STRU_D d
 WHERE d.ELEMENT_EDITION IS NULL
   AND EXISTS (SELECT 1 FROM dbo.BOM_STRU_M m WHERE LTRIM(RTRIM(m.PRO_NO)) = LTRIM(RTRIM(d.ELEMENT_PRO_NO)));',
    N'@Missing INT OUTPUT', @Missing = @MissingEdition OUTPUT;
IF @MissingEdition > 0
    THROW 57607, N'仍有"有下阶 BOM 却没回填子件版本"的明细，迁移中止。', 1;

EXEC sp_executesql N'
SELECT @Bad = COUNT(*) FROM dbo.BOM_STRU_D d
 WHERE d.ELEMENT_EDITION IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM dbo.BOM_STRU_M m
                   WHERE LTRIM(RTRIM(m.PRO_NO)) = LTRIM(RTRIM(d.ELEMENT_PRO_NO))
                     AND LTRIM(RTRIM(m.EDITION)) = LTRIM(RTRIM(d.ELEMENT_EDITION)));',
    N'@Bad INT OUTPUT', @Bad = @BadEdition OUTPUT;
IF @BadEdition > 0
    THROW 57608, N'存在回填出的子件版本在 BOM_STRU_M 中不存在，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 1204 子件版本就位：明细显式记录子件版次，半成品必填、材料留空；工单 BOM 已同步 ==';
