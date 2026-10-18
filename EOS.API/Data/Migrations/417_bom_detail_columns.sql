-- ============================================================================
-- EOS.ERP migration 417: 把「子件版次」与「底数」排进 1204 的明细录入面
-- ----------------------------------------------------------------------------
-- 两件事合并成一个迁移，因为它们是同一个动作：**字段在 `FIELDS` 里有了，
-- 但 `MODULE_FORM_LAYOUT` 里没有 → 录入界面看不见**。
--
--   · `ELEMENT_EDITION`（子件版次，416 新增字段）：紧跟「元件料号」
--   · `BASE_QTY`（底数，字段一直在但 `IS_VISIBLE=0`）：紧跟「组成用量」
--
-- 为什么 `detailFields` 是版式出的：`form-definition?mode=edit` 的字段序列与
-- `MODULE_FORM_LAYOUT.ORDER_NO` 逐项一致（实测量过），所以只加 `FIELDS` 不够。
--
-- **幂等**：按「目标字段是否已在版式里」逐项判断，已存在就整段跳过。
-- 不能用固定 `ORDER_NO >= N` 的写法——那在重跑时会把后面的行再推一位，
-- 一条迁移跑两次数值就散了。插入位点也一律用**参照字段的实际 ORDER_NO** 算，
-- 不硬编码数字（参照字段自己也可能被前一条改过位置）。
-- ============================================================================

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Mig NVARCHAR(100) = N'DbUp';
DECLARE @ModuleId INT = 1204;
DECLARE @Table NVARCHAR(100) = N'BOM_STRU_D';

IF OBJECT_ID(N'dbo.MODULE_FORM_LAYOUT', N'U') IS NULL
    THROW 57701, N'MODULE_FORM_LAYOUT 不存在，迁移中止。', 1;

BEGIN TRANSACTION;

/* ------------------------------------------------- ① 子件版次：紧跟元件料号 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT
               WHERE M_IDX = @ModuleId AND LTRIM(RTRIM(T_ID)) = @Table
                 AND LTRIM(RTRIM(F_ID)) = N'ELEMENT_EDITION')
BEGIN
    DECLARE @anchor INT = (
        SELECT TOP 1 ORDER_NO FROM dbo.MODULE_FORM_LAYOUT
         WHERE M_IDX = @ModuleId AND LTRIM(RTRIM(T_ID)) = @Table
           AND LTRIM(RTRIM(F_ID)) = N'ELEMENT_PRO_NO');

    IF @anchor IS NULL
        THROW 57702, N'版式里找不到元件料号，无法定位子件版次的位置。', 1;

    UPDATE dbo.MODULE_FORM_LAYOUT
       SET ORDER_NO = ORDER_NO + 1, UPDATED_BY = @Mig, UPDATED_AT = SYSDATETIME()
     WHERE M_IDX = @ModuleId AND LTRIM(RTRIM(T_ID)) = @Table AND ORDER_NO > @anchor;

    INSERT INTO dbo.MODULE_FORM_LAYOUT
        (M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE, SECTION_ID, IS_HIDDEN,
         UPDATED_BY, UPDATED_AT)
    VALUES
        (@ModuleId, @Table, N'ELEMENT_EDITION', 1, @anchor + 1, 1, 1, 0, -1, 0,
         @Mig, SYSDATETIME());
    PRINT N'[417] 版式新增子件版次（ORDER_NO=' + CAST(@anchor + 1 AS NVARCHAR(10)) + N'）';
END
ELSE
    PRINT N'[417] 版式已有子件版次，跳过';

/* ------------------------------------------------- ② 底数：紧跟组成用量 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT
               WHERE M_IDX = @ModuleId AND LTRIM(RTRIM(T_ID)) = @Table
                 AND LTRIM(RTRIM(F_ID)) = N'BASE_QTY')
BEGIN
    DECLARE @qtyAnchor INT = (
        SELECT TOP 1 ORDER_NO FROM dbo.MODULE_FORM_LAYOUT
         WHERE M_IDX = @ModuleId AND LTRIM(RTRIM(T_ID)) = @Table
           AND LTRIM(RTRIM(F_ID)) = N'ELEMENT_QTY');

    IF @qtyAnchor IS NULL
        THROW 57703, N'版式里找不到组成用量，无法定位底数的位置。', 1;

    UPDATE dbo.MODULE_FORM_LAYOUT
       SET ORDER_NO = ORDER_NO + 1, UPDATED_BY = @Mig, UPDATED_AT = SYSDATETIME()
     WHERE M_IDX = @ModuleId AND LTRIM(RTRIM(T_ID)) = @Table AND ORDER_NO > @qtyAnchor;

    INSERT INTO dbo.MODULE_FORM_LAYOUT
        (M_IDX, T_ID, F_ID, TAB_NO, ORDER_NO, SPAN, ROW_SPAN, NEW_LINE, SECTION_ID, IS_HIDDEN,
         UPDATED_BY, UPDATED_AT)
    VALUES
        (@ModuleId, @Table, N'BASE_QTY', 1, @qtyAnchor + 1, 1, 1, 0, -1, 0,
         @Mig, SYSDATETIME());
    PRINT N'[417] 版式新增底数（ORDER_NO=' + CAST(@qtyAnchor + 1 AS NVARCHAR(10)) + N'）';
END
ELSE
    PRINT N'[417] 版式已有底数，跳过';

/* ------------------------------------------------- ③ 字段可见性 */
UPDATE dbo.FIELDS
   SET IS_VISIBLE = 1, LAST_UPDATE_BY = @Mig, LAST_UPDATE_DATE = SYSDATETIME()
 WHERE LTRIM(RTRIM(T_ID)) = @Table AND LTRIM(RTRIM(F_ID)) = N'BASE_QTY'
   AND ISNULL(IS_VISIBLE, 0) = 0;
PRINT N'[417] BASE_QTY 改为可见：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ------------------------------------------------- ④ 字段元数据整行对齐 */
/* 416 建 `ELEMENT_EDITION` 这一行时只写了部分列（F_TYPE / REGEX / DISPLAY_LENGTH…），
   其余列取默认值。**其中某一列会让装配阶段把字段筛掉**——实测：`FIELDS` 行在、
   版式行在、物理列也在，`form-definition.detailFields` 里就是没有它，而紧邻的
   `BASE_QTY`（416 之前就存在的老字段）正常出现。
   逐列猜差异猜不出来（对比过 F_TYPE / IS_QUERY / IS_VIRTUAL / IS_DEFAULT_FIELDS /
   CELL_ROLE / SECTION_ID，全都一致或非致命）。稳妥的做法是**整行照抄同表同类型的
   「元件料号」**：属性本就该一致，抄完即一致，不必知道是哪一列在作祟。
   判断条件用 `VERIFY_INDEX IS NULL`（416 没写这一列）——已对齐过的行不会被反复重建。 */
IF EXISTS (SELECT 1 FROM dbo.FIELDS
           WHERE LTRIM(RTRIM(T_ID)) = @Table AND LTRIM(RTRIM(F_ID)) = N'ELEMENT_EDITION'
             AND VERIFY_INDEX IS NULL)
BEGIN
    DECLARE @cols NVARCHAR(MAX), @rebuild NVARCHAR(MAX);
    SELECT @cols = STRING_AGG(QUOTENAME(name), ',') FROM sys.columns
     WHERE object_id = OBJECT_ID(N'dbo.FIELDS') AND name NOT IN (N'F_ID', N'F_DESC', N'REGEX');
    SET @rebuild =
        N'DELETE FROM dbo.FIELDS WHERE LTRIM(RTRIM(T_ID)) = N''BOM_STRU_D'''
        + N' AND LTRIM(RTRIM(F_ID)) = N''ELEMENT_EDITION''; '
        + N'INSERT INTO dbo.FIELDS (' + @cols + N', F_ID, F_DESC, REGEX) SELECT ' + @cols
        + N', N''ELEMENT_EDITION'', N''子件版次'', N''^\d{2}$'' FROM dbo.FIELDS'
        + N' WHERE LTRIM(RTRIM(T_ID)) = N''BOM_STRU_D'' AND LTRIM(RTRIM(F_ID)) = N''ELEMENT_PRO_NO'';';
    EXEC sp_executesql @rebuild;
    PRINT N'[417] 子件版次字段元数据已整行对齐「元件料号」';
END
ELSE
    PRINT N'[417] 子件版次字段元数据已对齐，跳过';

/* ------------------------------------------------- ⑤ 照抄后的三处纠正 */
/* 整行照抄是对的（它能消除"某一列导致字段被筛掉"这种查不出来的差异），但
   `ELEMENT_PRO_NO` 有三个属性对"子件版次"是错的，抄过来必须改回去：

   ① `IS_VERIFY=1` → **必填**。前端据此把空值拦下，而材料行的版次**必须留空**
      （实测：6 个材料行全红"该字段不能为空"，整个 BOM 存不了）。
      半成品必填由服务端 `require-when-referenced` 判，前端不能替它拦。
   ② `BROWSE_M_IDX=1201` / `BROWSE_URL=...{ELEMENT_PRO_NO}` → 版次会变成
      "点开跳料件资料"的链接，且占位符指向别的列。
   ③ `ONLY_CHOOSE=1` → 只能选不能输；版次是两位数字，手输才是主路径。 */
UPDATE dbo.FIELDS
   SET IS_VERIFY = 0,
       BROWSE_M_IDX = NULL,
       BROWSE_URL = NULL,
       ONLY_CHOOSE = 0,
       LAST_UPDATE_BY = @Mig,
       LAST_UPDATE_DATE = SYSDATETIME()
 WHERE LTRIM(RTRIM(T_ID)) = @Table AND LTRIM(RTRIM(F_ID)) = N'ELEMENT_EDITION'
   AND (ISNULL(IS_VERIFY, 0) = 1 OR BROWSE_M_IDX IS NOT NULL OR ISNULL(ONLY_CHOOSE, 0) = 1);
PRINT N'[417] 子件版次纠正为"非必填/无浏览链接/可手输"：' + CAST(@@ROWCOUNT AS NVARCHAR(10)) + N' 行';

/* ------------------------------------------------- ⑤ 断言 */
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT
               WHERE M_IDX = @ModuleId AND LTRIM(RTRIM(T_ID)) = @Table
                 AND LTRIM(RTRIM(F_ID)) = N'ELEMENT_EDITION')
    THROW 57704, N'子件版次未排进明细版式，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.MODULE_FORM_LAYOUT
               WHERE M_IDX = @ModuleId AND LTRIM(RTRIM(T_ID)) = @Table
                 AND LTRIM(RTRIM(F_ID)) = N'BASE_QTY')
    THROW 57705, N'底数未排进明细版式，迁移中止。', 1;
IF NOT EXISTS (SELECT 1 FROM dbo.FIELDS
               WHERE LTRIM(RTRIM(T_ID)) = @Table AND LTRIM(RTRIM(F_ID)) = N'BASE_QTY'
                 AND ISNULL(IS_VISIBLE, 0) = 1)
    THROW 57706, N'底数字段仍不可见，迁移中止。', 1;

COMMIT TRANSACTION;

PRINT N'== 1204 明细版式：元件料号 → 子件版次 → 用量 → 底数 → 损耗率 ==';
