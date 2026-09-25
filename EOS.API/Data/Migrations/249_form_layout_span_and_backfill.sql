-- ============================================================================
-- 模块级表单版式的两处订正（承接 P4 删列，均为数据订正，不动结构）
-- ----------------------------------------------------------------------------
-- ① 主表普通字段的列跨度订正：统一表单是「一行四列」（旧版每行 4 个字段），
--    而物化版式时把字段级旧语义 FORM_SPAN=1 映射成了 2 子列，于是主表普通字段
--    在 4 列栅格里只占一半宽——每行从 4 个字段退化成 2 个，表单高度翻倍。
--    本迁移把主表普通字段的 SPAN 归一到 1 子列；整行独占（备注类/旧 FORM_SPAN=2）
--    保持 SPAN=4、ROW_SPAN=2 不变。
--
-- ② 为「持已发布快照但没有版式行」的模块回放历史版式：P4 关闭了"无版式行则按字段级
--    配置推导"的回落，而删列前未把所有模块物化完（当时的删列棘轮把它们列为例外放行），
--    这些模块的默认版式因此退化（字段全挤一行、无复合格、备注不再整行）。
--    版式是无从重建的（输入列已删），但删列前的完整版式留在快照历史里
--    （WORKBENCH_DEFINITION_SNAPSHOT 的旧版本 DEFINITION_JSON.FormLayout），
--    故按「最新一个含非空版式的历史版本」回放顺序/复合格/分节/隐藏；跨度按 ① 的口径重算。
--
-- 幂等：① 的判据是 SPAN=2 的主表行（跑完即无）；② 用 NOT EXISTS 逐行守卫。重复执行不报错。
-- 回滚：本迁移不改结构，回滚 = 重跑删列前的物理备份；无库内可逆点（数据订正类）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

IF OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT', N'U') IS NULL OR OBJECT_ID(N'dbo.MODULE_FORM_TAB', N'U') IS NULL
    THROW 54000, N'模块级版式表不存在：本迁移的前提不成立（应先应用 233_form_layout_tables）。', 1;

BEGIN TRANSACTION;

-- ① 主表普通字段：SPAN 由 2 子列归一到 1 子列（4 列栅格下即每行 4 个字段）
UPDATE l
SET SPAN = 1,
    UPDATED_BY = N'ADR-022-SPAN-FIX',
    UPDATED_AT = SYSUTCDATETIME()
FROM dbo.MODULE_FORM_LAYOUT l
JOIN dbo.MODULES m ON m.M_IDX = l.M_IDX
WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = LTRIM(RTRIM(l.T_ID))
  AND l.SPAN = 2;

-- ② 回放：持当前快照但没有版式行的模块，取「最新含非空 Master 的历史版本」重建版式行
DECLARE @pending TABLE (MODULE_ID INT PRIMARY KEY);

INSERT INTO @pending (MODULE_ID)
SELECT s.MODULE_ID
FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK)
WHERE s.IS_CURRENT = 1
  AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l WITH (NOLOCK) WHERE l.M_IDX = s.MODULE_ID)
  AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_TAB t WITH (NOLOCK) WHERE t.M_IDX = s.MODULE_ID);

IF EXISTS (SELECT 1 FROM @pending)
BEGIN
    -- 每个待回放模块的源版本：VERSION 最大且 FormLayout.Master 非空
    DECLARE @source TABLE (MODULE_ID INT PRIMARY KEY, DEFINITION_JSON NVARCHAR(MAX));

    INSERT INTO @source (MODULE_ID, DEFINITION_JSON)
    SELECT ranked.MODULE_ID, ranked.DEFINITION_JSON
    FROM (
        SELECT s.MODULE_ID, s.DEFINITION_JSON,
               ROW_NUMBER() OVER (PARTITION BY s.MODULE_ID ORDER BY s.VERSION DESC) AS RN
        FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK)
        JOIN @pending p ON p.MODULE_ID = s.MODULE_ID
        WHERE JSON_QUERY(s.DEFINITION_JSON, N'$.FormLayout.Master') IS NOT NULL
          AND JSON_QUERY(s.DEFINITION_JSON, N'$.FormLayout.Master') <> N'[]'
    ) ranked
    WHERE ranked.RN = 1;

    -- ②-a 主表行：顺序/复合格/分节/隐藏取自历史；跨度按 ① 的口径重算
    INSERT INTO dbo.MODULE_FORM_LAYOUT
        (M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE,
         SECTION_ID, CELL_GROUP, CELL_ROLE, IS_HIDDEN, UPDATED_BY, UPDATED_AT)
    SELECT src.MODULE_ID, m.MASTER_TABLE, r.[Key], r.[TabNo], r.[OrderNo],
           CASE WHEN LTRIM(RTRIM(f.F_TYPE)) IN (N'text', N'ntext') OR r.[Key] LIKE N'%REMARK' THEN 4 ELSE 1 END,
           CASE WHEN LTRIM(RTRIM(f.F_TYPE)) IN (N'text', N'ntext') OR r.[Key] LIKE N'%REMARK' THEN 2 ELSE 1 END,
           r.[NewLine], r.[SectionId], r.[CellGroup], r.[CellRole], r.[Hidden],
           N'ADR-022-BACKFILL', SYSUTCDATETIME()
    FROM @source src
    JOIN dbo.MODULES m ON m.M_IDX = src.MODULE_ID
    CROSS APPLY OPENJSON(src.DEFINITION_JSON, N'$.FormLayout.Master')
        WITH ([Key] NVARCHAR(100) N'$.Key', [TabNo] INT N'$.TabNo', [OrderNo] INT N'$.OrderNo',
              [NewLine] BIT N'$.NewLine', [SectionId] NVARCHAR(50) N'$.SectionId',
              [CellGroup] NVARCHAR(50) N'$.CellGroup', [CellRole] INT N'$.CellRole',
              [Hidden] BIT N'$.Hidden') r
    JOIN dbo.FIELDS f ON f.T_ID = m.MASTER_TABLE AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(r.[Key]))
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l
        WHERE l.M_IDX = src.MODULE_ID AND l.T_ID = m.MASTER_TABLE
          AND LTRIM(RTRIM(l.F_ID)) = LTRIM(RTRIM(r.[Key])));

    -- ②-b 明细行：明细只消费列顺序与显隐，跨度固定 1
    INSERT INTO dbo.MODULE_FORM_LAYOUT
        (M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE,
         SECTION_ID, CELL_GROUP, CELL_ROLE, IS_HIDDEN, UPDATED_BY, UPDATED_AT)
    SELECT src.MODULE_ID, m.DETAIL_TABLE, r.[Key], 1, r.[OrderNo], 1, 1, 0,
           NULL, NULL, 0, r.[Hidden], N'ADR-022-BACKFILL', SYSUTCDATETIME()
    FROM @source src
    JOIN dbo.MODULES m ON m.M_IDX = src.MODULE_ID
    CROSS APPLY OPENJSON(src.DEFINITION_JSON, N'$.FormLayout.Detail')
        WITH ([Key] NVARCHAR(100) N'$.Key', [OrderNo] INT N'$.OrderNo', [Hidden] BIT N'$.Hidden') r
    JOIN dbo.FIELDS f ON f.T_ID = m.DETAIL_TABLE AND LTRIM(RTRIM(f.F_ID)) = LTRIM(RTRIM(r.[Key]))
    WHERE NULLIF(LTRIM(RTRIM(ISNULL(m.DETAIL_TABLE, N''))), N'') IS NOT NULL
      AND NOT EXISTS (
        SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l
        WHERE l.M_IDX = src.MODULE_ID AND l.T_ID = m.DETAIL_TABLE
          AND LTRIM(RTRIM(l.F_ID)) = LTRIM(RTRIM(r.[Key])));

    -- ②-c 页签：标题与序号取自历史
    INSERT INTO dbo.MODULE_FORM_TAB (M_IDX, TAB_NO, TAB_TITLE)
    SELECT src.MODULE_ID, t.[No], ISNULL(t.[Title], N'')
    FROM @source src
    CROSS APPLY OPENJSON(src.DEFINITION_JSON, N'$.FormLayout.Tabs')
        WITH ([No] INT N'$.No', [Title] NVARCHAR(50) N'$.Title') t
    WHERE NOT EXISTS (
        SELECT 1 FROM dbo.MODULE_FORM_TAB x
        WHERE x.M_IDX = src.MODULE_ID AND x.TAB_NO = t.[No]);
END

-- ③ 后置自证（① ）：主表行不应再有 2 子列
IF EXISTS (
        SELECT 1
        FROM dbo.MODULE_FORM_LAYOUT l
        JOIN dbo.MODULES m ON m.M_IDX = l.M_IDX
        WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = LTRIM(RTRIM(l.T_ID))
          AND l.SPAN = 2)
    THROW 54001, N'主表仍有 2 子列的版式行：列跨度订正未跑完。', 1;

-- ④ 后置自证（②）：持当前快照的模块都必须已有版式行
IF EXISTS (
        SELECT 1
        FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK)
        WHERE s.IS_CURRENT = 1
          AND NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT l WHERE l.M_IDX = s.MODULE_ID))
    THROW 54002, N'仍有持快照却无版式行的模块：历史版式回放未覆盖。', 1;

COMMIT TRANSACTION;

SELECT N'主表 2 子列残留' AS CHECK_NAME, COUNT(*) AS CNT
FROM dbo.MODULE_FORM_LAYOUT l
JOIN dbo.MODULES m ON m.M_IDX = l.M_IDX
WHERE LTRIM(RTRIM(ISNULL(m.MASTER_TABLE, N''))) = LTRIM(RTRIM(l.T_ID)) AND l.SPAN = 2;
