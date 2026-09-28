-- 报表权限例外行的收尾：登记带语义的行、清掉真正无意义的行。

--
-- 背景（D16）：例外层已退场——运行时判定只剩"归属模块的 REPORT_TAG"一层，
-- `SYSDD_REPORT` 的 `PREVIEW_TAG/PRINT_TAG/EXPORT_TAG/DATA_FILTER` 四列不再参与任何判定，
-- 个人/组例外行的管理界面也已删除。这张表今后只承载报表中心的**用户状态**
-- （`FAVORITE_TAG` / `SORT_IDX` / `LAST_RUN_AT`）。
--
-- 本脚本做两件事：
--   ① **登记**带权限语义的行（四列不是"全开、无行过滤"）——其收紧随四列一起失效，交付时需与业务确认；
--   ② **清理**四列全开、无行过滤、**且不带任何状态**的行。
--
-- "零语义"必须叠加"无状态"：`FAVORITE_TAG=1` 或 `SORT_IDX<>0` 或 `LAST_RUN_AT` 非空的行，
-- 即使四列全开也是**用户数据**（收藏、收藏排序、最近使用），删掉就是毁用户数据。
-- 实测：带语义 27 行、零语义 229 行（其中 1 行带状态），故实删 228 行、保留 28 行。
--
-- 本脚本**不动表结构**（四列与 `SYSDH_REPORT` 的退役随管理面后端一起做），
-- 执行前后运行期行为完全一致——这是"先清数据、后删列"里安全的那一半。
--
-- 写法说明：不用动态 SQL。谓词直接写出来（两处），因为 `sp_executesql` 的拼接与
-- `THROW` 的消息拼接都只接受变量/字面量，拼错一次就是语法错误或"守卫报的数字看不出是什么"。

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';
IF DB_NAME() <> N'EOS.ERP'
    THROW 50000, @GUARD_MESSAGE, 1;

BEGIN TRANSACTION;

-- ---------------------------------------------------------------- ① 实测两类行的数量
DECLARE @MeatyCount INT = (
    SELECT COUNT(*) FROM dbo.SYSDD_REPORT WITH (NOLOCK)
    WHERE NOT (ISNULL(PREVIEW_TAG, 0) = 1 AND ISNULL(PRINT_TAG, 0) = 1 AND ISNULL(EXPORT_TAG, 0) = 1
               AND ISNULL(LTRIM(RTRIM(DATA_FILTER)), '') = ''));

DECLARE @DeletableCount INT = (
    SELECT COUNT(*) FROM dbo.SYSDD_REPORT WITH (NOLOCK)
    WHERE (ISNULL(PREVIEW_TAG, 0) = 1 AND ISNULL(PRINT_TAG, 0) = 1 AND ISNULL(EXPORT_TAG, 0) = 1
           AND ISNULL(LTRIM(RTRIM(DATA_FILTER)), '') = '')
      AND ISNULL(FAVORITE_TAG, 0) = 0 AND ISNULL(SORT_IDX, 0) = 0 AND LAST_RUN_AT IS NULL);

DECLARE @TotalCount INT = (SELECT COUNT(*) FROM dbo.SYSDD_REPORT WITH (NOLOCK));

-- 登记数一旦与实测不符就停下（数据变了要人看，不做"差不多就删"）
IF @MeatyCount <> 27 OR @DeletableCount <> 228 OR @TotalCount <> 256
BEGIN
    DECLARE @Mismatch NVARCHAR(400) = N'例外行登记数与实测不符（登记 总 256 / 带语义 27 / 可清理 228）：实测 总 '
        + CAST(@TotalCount AS NVARCHAR(10)) + N' / 带语义 ' + CAST(@MeatyCount AS NVARCHAR(10))
        + N' / 可清理 ' + CAST(@DeletableCount AS NVARCHAR(10)) + N'。数据已变化，先重新核对，中止。';
    THROW 56000, @Mismatch, 1;
END

-- ---------------------------------------------------------------- ② 登记带语义的行（逐条留痕）
DECLARE @MeatyDetail NVARCHAR(MAX) = STUFF((
    SELECT N'；' + LTRIM(RTRIM(USER_ID)) + N'@' + CAST(M_IDX AS NVARCHAR(20)) + N'/' + LTRIM(RTRIM(REPORT_ID))
         + N' pv=' + CAST(ISNULL(PREVIEW_TAG, 0) AS NVARCHAR(1))
         + N' pr=' + CAST(ISNULL(PRINT_TAG, 0) AS NVARCHAR(1))
         + N' ex=' + CAST(ISNULL(EXPORT_TAG, 0) AS NVARCHAR(1))
         + N' filter=' + CASE WHEN ISNULL(LTRIM(RTRIM(DATA_FILTER)), '') = '' THEN N'-' ELSE N'有' END
    FROM dbo.SYSDD_REPORT WITH (NOLOCK)
    WHERE NOT (ISNULL(PREVIEW_TAG, 0) = 1 AND ISNULL(PRINT_TAG, 0) = 1 AND ISNULL(EXPORT_TAG, 0) = 1
               AND ISNULL(LTRIM(RTRIM(DATA_FILTER)), '') = '')
    ORDER BY USER_ID, M_IDX, REPORT_ID FOR XML PATH('')), 1, 1, N'');
DECLARE @MeatyMessage NVARCHAR(MAX) = N'== 报表权限例外行登记（其收紧随四列退场，逐条如下）：' + @MeatyDetail + N' ==';
PRINT @MeatyMessage;

-- ---------------------------------------------------------------- ③ 清理无意义的行
DELETE FROM dbo.SYSDD_REPORT
WHERE (ISNULL(PREVIEW_TAG, 0) = 1 AND ISNULL(PRINT_TAG, 0) = 1 AND ISNULL(EXPORT_TAG, 0) = 1
       AND ISNULL(LTRIM(RTRIM(DATA_FILTER)), '') = '')
  AND ISNULL(FAVORITE_TAG, 0) = 0 AND ISNULL(SORT_IDX, 0) = 0 AND LAST_RUN_AT IS NULL;

DECLARE @Deleted INT = @@ROWCOUNT;
IF @Deleted <> @DeletableCount
BEGIN
    DECLARE @DeleteMismatch NVARCHAR(300) = N'实际清理行数 ' + CAST(@Deleted AS NVARCHAR(10))
        + N' 与复算值 ' + CAST(@DeletableCount AS NVARCHAR(10)) + N' 不符，中止。';
    THROW 56001, @DeleteMismatch, 1;
END

-- ---------------------------------------------------------------- ④ 不变量
DECLARE @Remaining INT = (SELECT COUNT(*) FROM dbo.SYSDD_REPORT WITH (NOLOCK));
IF @Remaining <> @MeatyCount + 1
BEGIN
    DECLARE @RemainMismatch NVARCHAR(300) = N'清理后剩余 ' + CAST(@Remaining AS NVARCHAR(10))
        + N' 行，不等于"带语义行 + 带状态行"（' + CAST(@MeatyCount + 1 AS NVARCHAR(10)) + N'），中止。';
    THROW 56002, @RemainMismatch, 1;
END

-- 状态行必须还在（收藏/最近使用是用户数据）
IF NOT EXISTS (SELECT 1 FROM dbo.SYSDD_REPORT WITH (NOLOCK)
               WHERE ISNULL(FAVORITE_TAG, 0) = 1 OR ISNULL(SORT_IDX, 0) <> 0 OR LAST_RUN_AT IS NOT NULL)
    THROW 56003, N'清理后没有任何带状态的行，疑似误删用户数据，中止。', 1;

DECLARE @DoneMessage NVARCHAR(300) = N'== 报表权限例外行收尾：登记 ' + CAST(@MeatyCount AS NVARCHAR(10))
    + N' 行，清理 ' + CAST(@Deleted AS NVARCHAR(10)) + N' 行，保留 ' + CAST(@Remaining AS NVARCHAR(10))
    + N' 行（含带收藏/最近使用状态的行）==';
PRINT @DoneMessage;

COMMIT TRANSACTION;
