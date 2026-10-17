-- ============================================================================
-- EOS.ERP migration 356: 其它付款凭证（170204）的汇率补回录入面
-- ----------------------------------------------------------------------------
-- 症状：模块 170204 的 DETAIL_NO_FIELDS = 'CURR_ID;CURR_RATE;SUPPLIER_ID'，
-- 而「新增一行」会逐键检查这几个主表字段是否已填（FormEditorPage.addDetailRow）——
-- 其中 CURR_RATE 的 FIELDS 行被标为 IS_VISIBLE=0，表单上根本不渲染它，
-- 于是那道门禁永远过不去：用户看到的始终是"请先填写主表字段：CURR_RATE"，明细行加不出来。
--
-- 为什么是补字段、而不是从门禁里摘掉这个键：同族的 170203（预付帐款单）主表上就有汇率；
-- 且 CURR_ID 与 SUPPLIER_ID 两条数据源的 RETURN_ITEMS 都把 CURR_RATE 列为回填目标
-- （选币别或选厂商即带出汇率）。字段与回填通路都是齐的，缺的只是"不显示"这一位。
--
-- 三处改动：
--   1. FIELDS.IS_VISIBLE 0 → 1：把它从"不显示"放回录入面。
--      注意顺序——FormFieldSelector.Select 先剔除不可见字段，版式施加在它之后，
--      所以只补版式行是变不出来的，必须先让它可见（与迁移 254 的口径一致）。
--   2. MODULE_FORM_LAYOUT 补一行：该表已定制版式，运行态的字段集完全由版式决定，
--      不加行则字段集里不会有它。排在末尾（现最大 ORDER_NO=40 → 41），
--      不改动任何既有字段的位置。
--   3. 标脏：170204 已有当前快照（IS_CURRENT=1），而版式段随快照冻结（模块级事实），
--      不标脏会被 check-snapshot-staleness 判为**静默落后**；标脏后以"待发布积压"的形式可见。
--
-- 幂等：两项数据改动各带取值比较 / NOT EXISTS 守卫，重复执行不产生第二行、不改已对的位；
--       标脏用 MERGE，重复执行只刷新时间戳。
-- 回滚：IS_VISIBLE 置回 0、删掉新增的那行版式即可（本脚本可安全重跑）。
-- ============================================================================

SET NOCOUNT ON;

SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 52810, @GUARD_MESSAGE, 1;

DECLARE @M_IDX INT = 170204;
DECLARE @T_ID NVARCHAR(100) = N'PUR_PAY_OTHER';
DECLARE @F_ID NVARCHAR(100) = N'CURR_RATE';

/* ---------- 前置断言：目标字段与模块必须存在，避免在别的库形态上静默空转 ---------- */
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = @T_ID AND LTRIM(RTRIM(F_ID)) = @F_ID)
    THROW 52811, N'找不到字段 FIELDS(PUR_PAY_OTHER.CURR_RATE)，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULES WHERE M_IDX = @M_IDX)
    THROW 52812, N'找不到模块 170204，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_TAB WHERE M_IDX = @M_IDX AND TAB_NO = 1)
    THROW 52813, N'模块 170204 没有页签 1，版式行的 TAB_NO 无从确定，迁移中止。', 1;

-- 版式已定制才需要补行；未定制时字段集本就取全部可见字段，补行反而会把表变成"已定制"
DECLARE @masterLayoutRows INT = (
    SELECT COUNT(1) FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX = @M_IDX AND LTRIM(RTRIM(T_ID)) = @T_ID);
IF @masterLayoutRows = 0
    THROW 52814, N'模块 170204 的主表没有版式行（未定制），无需补行——请核对迁移前提。', 1;

-- 迁移 254 的收口口径：版式行不得引用 IS_VISIBLE=0 的字段。若先补行后置可见，
-- 中途就落入了那种状态；这里先改可见性，顺带把这个前提断死。
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = @T_ID AND LTRIM(RTRIM(F_ID)) = @F_ID
             AND COALESCE(IS_VISIBLE, 1) = 0)
BEGIN
    BEGIN TRANSACTION;

    UPDATE dbo.FIELDS
       SET IS_VISIBLE = 1,
           LAST_UPDATE_BY = N'migration-356',
           LAST_UPDATE_DATE = SYSDATETIME()
     WHERE LTRIM(RTRIM(T_ID)) = @T_ID AND LTRIM(RTRIM(F_ID)) = @F_ID;

    PRINT N'== 字段 ' + @T_ID + N'.' + @F_ID + N' 已置为显示（' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 行）==';

    IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT
                    WHERE M_IDX = @M_IDX AND LTRIM(RTRIM(T_ID)) = @T_ID AND LTRIM(RTRIM(F_ID)) = @F_ID)
    BEGIN
        DECLARE @nextOrder INT = (
            SELECT ISNULL(MAX(ORDER_NO), 0) + 1 FROM dbo.MODULE_FORM_LAYOUT
             WHERE M_IDX = @M_IDX AND LTRIM(RTRIM(T_ID)) = @T_ID AND TAB_NO = 1);

        INSERT INTO dbo.MODULE_FORM_LAYOUT
            (M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE, CELL_ROLE, IS_HIDDEN, UPDATED_BY)
        VALUES
            (@M_IDX, @T_ID, @F_ID, 1, @nextOrder, 1, 1, 0, 0, 0, N'migration-356');

        PRINT N'== 已补版式行：' + @T_ID + N'.' + @F_ID + N' 排在第 ' + CONVERT(NVARCHAR(10), @nextOrder) + N' 位 ==';
    END

    COMMIT;
END
ELSE
    PRINT N'== 字段 ' + @T_ID + N'.' + @F_ID + N' 已是显示状态，跳过数据改动 ==';

/* ---------- 标脏：已有当前快照的受影响模块（否则会被判为快照静默落后） ---------- */
MERGE dbo.WORKBENCH_MODULE_DIRTY AS D
USING (SELECT S.M_IDX
         FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT S
        WHERE S.M_IDX = @M_IDX AND S.IS_CURRENT = 1) AS S
   ON D.M_IDX = S.M_IDX
WHEN MATCHED THEN UPDATE SET D.DIRTY_TAG = 1, D.LAST_MODIFIED_BY = N'DbUp', D.LAST_MODIFIED_AT = SYSDATETIME()
WHEN NOT MATCHED THEN INSERT (M_IDX, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
    VALUES (S.M_IDX, 1, N'DbUp', SYSDATETIME());

PRINT N'== 已标记 ' + CONVERT(NVARCHAR(10), @@ROWCOUNT) + N' 个模块待重发布 ==';

/* ---------- 收口断言：终态必须成立，任一条不成立即中止整笔 ---------- */
IF EXISTS (SELECT 1 FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = @T_ID AND LTRIM(RTRIM(F_ID)) = @F_ID
             AND COALESCE(IS_VISIBLE, 1) = 0)
    THROW 52815, N'收口失败：字段仍为"不显示"，门禁依旧过不去，迁移中止。', 1;

IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT
                WHERE M_IDX = @M_IDX AND LTRIM(RTRIM(T_ID)) = @T_ID AND LTRIM(RTRIM(F_ID)) = @F_ID)
    THROW 52816, N'收口失败：版式里没有该字段，表单上仍不会渲染，迁移中止。', 1;

IF EXISTS (SELECT 1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE M_IDX = @M_IDX AND IS_CURRENT = 1)
   AND NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY WHERE M_IDX = @M_IDX AND DIRTY_TAG = 1)
    THROW 52817, N'收口失败：模块有当前快照却未标记待发布，快照会被判为静默落后，迁移中止。', 1;

PRINT N'== 收口：170204 的汇率已回到录入面，模块待重发布 ==';
