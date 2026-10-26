using System.Data;
using System.Globalization;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Bom;

/// <summary>
/// 产品 BOM（模块 1204）的结构查询：多层展开、毛需求、反查用途。
///
/// 三个出口共用同一套"选版"规则（ADR-039 6.3）：结构树与毛需求的下一层版次
/// 由父行明细的 <c>ELEMENT_EDITION</c> 决定，不在运行时回退；反查用途按每个父件
/// 选中的版次（<c>@rootEdition</c>，缺省取最大版次）向上走。
///
/// 环检测靠路径而不是深度：回到已出现过的料号即停止该分支。这样环会在第一次
/// 回到自身时被切断，而不是跑到 maxDepth 上限才失败，且停止原因就在 PATH 里。
///
/// 一切标识符都是常量（BOM_STRU_M / BOM_STRU_D / PRODUCT），没有动态拼接；
/// 唯一拼进 SQL 的是 MAXRECURSION 的整数，来自 1..100 的白名单夹取。
/// </summary>
public sealed class BomRepository(DbConnectionFactory connections)
{
    /// <summary>递归上限的硬边界：BOM 真实层数极少超过 15，设它是防数据错误把库拖死。</summary>
    internal const int MaxDepthLimit = 100;

    /// <summary>结构树默认深度。</summary>
    public const int DefaultTreeDepth = 10;

    /// <summary>反查默认深度（向上找父件，通常很浅）。</summary>
    public const int DefaultWhereUsedDepth = 5;

    /// <summary>
    /// 反查用途的选版：每个父件挑一条 BOM。与 <c>@rootEdition</c> 相同的版次优先，
    /// 否则取最大版次。两位数字版次（ADR-039 D4）保证 MAX 的字典序与数值序一致。
    /// </summary>
    private const string EditionPick = """
        EDITION_PICK AS (
            SELECT m.PRO_NO,
                   m.EDITION,
                   ROW_NUMBER() OVER (
                       PARTITION BY m.PRO_NO
                       ORDER BY CASE WHEN m.EDITION = @rootEdition THEN 0 ELSE 1 END,
                                m.EDITION DESC) AS RN
            FROM dbo.BOM_STRU_M m
        )
        """;

    /// <summary>
    /// 结构树：从根向下逐层展开。下一层用父行 <c>ELEMENT_EDITION</c> 指定的子件版次，
    /// 递归成员里不做运行时选版；没有指定版次（材料）时自然展开到该行为止。
    /// </summary>
    private static readonly string TreeSql = $"""
        WITH TREE AS (
            SELECT CAST(N'/' + LTRIM(RTRIM(d.ELEMENT_PRO_NO)) + N'/' AS nvarchar(4000)) AS PATH,
                   1 AS LVL,
                   LTRIM(RTRIM(d.ELEMENT_PRO_NO)) AS PRO_NO,
                   CAST(d.EDITION AS nchar(10)) AS USED_EDITION,
                   CAST(d.ELEMENT_QTY / NULLIF(d.BASE_QTY, 0) AS decimal(38,10)) AS QTY_PER,
                   CAST(d.ELEMENT_QTY / NULLIF(d.BASE_QTY, 0) AS decimal(38,10)) AS QTY_CUM,
                   CAST(ISNULL(d.LOST_RATE, 0) AS decimal(18,6)) AS LOST_RATE,
                   CAST(1 + ISNULL(d.LOST_RATE, 0) / 100.0 AS decimal(38,10)) AS LOST_FACTOR,
                   -- 明细行自身的属性：右栏要显示"这个元件在配方里的位置"，
                   -- 项次、底数、备注都在这一行上，不该让前端再查一次明细
                   d.SERIAL_NO,
                   CAST(ISNULL(d.BASE_QTY, 1) AS decimal(18,6)) AS BASE_QTY,
                   CAST(ISNULL(d.REMARK, N'') AS nvarchar(200)) AS REMARK,
                   -- 子件自己的 BOM 版次：**由明细行指定**（迁移 416），不再运行时选版。
                   -- 为 NULL 表示它是材料（没有下阶 BOM），展开到这一行为止。
                   CAST(d.ELEMENT_EDITION AS nchar(10)) AS OWN_EDITION
            FROM dbo.BOM_STRU_D d
            WHERE d.PRO_NO = @rootProNo
              AND d.EDITION = @rootEdition

            UNION ALL

            SELECT CAST(t.PATH + LTRIM(RTRIM(d.ELEMENT_PRO_NO)) + N'/' AS nvarchar(4000)),
                   t.LVL + 1,
                   LTRIM(RTRIM(d.ELEMENT_PRO_NO)),
                   CAST(d.EDITION AS nchar(10)),
                   CAST(d.ELEMENT_QTY / NULLIF(d.BASE_QTY, 0) AS decimal(38,10)),
                   CAST(t.QTY_CUM * d.ELEMENT_QTY / NULLIF(d.BASE_QTY, 0) AS decimal(38,10)),
                   CAST(ISNULL(d.LOST_RATE, 0) AS decimal(18,6)),
                   CAST(t.LOST_FACTOR * (1 + ISNULL(d.LOST_RATE, 0) / 100.0) AS decimal(38,10)),
                   d.SERIAL_NO,
                   CAST(ISNULL(d.BASE_QTY, 1) AS decimal(18,6)),
                   CAST(ISNULL(d.REMARK, N'') AS nvarchar(200)),
                   CAST(d.ELEMENT_EDITION AS nchar(10))
            FROM dbo.BOM_STRU_D d
            -- 下一层用**父行指定的子件版本**继续展开：d.EDITION = t.OWN_EDITION。
            -- 这正是"子件版本写进明细"的意义——展开路径由数据决定，不由运行时规则决定。
            JOIN TREE t ON d.PRO_NO = t.PRO_NO
                       AND CAST(d.EDITION AS nchar(10)) = t.OWN_EDITION
            WHERE t.LVL < @maxDepth
              AND CHARINDEX(N'/' + LTRIM(RTRIM(d.ELEMENT_PRO_NO)) + N'/', t.PATH) = 0
        )
        SELECT t.LVL, t.PRO_NO, LTRIM(RTRIM(t.USED_EDITION)) AS USED_EDITION,
               t.QTY_PER, t.QTY_CUM, t.LOST_RATE, t.LOST_FACTOR,
               t.SERIAL_NO, t.BASE_QTY, t.REMARK,
               -- 两个版次语义不同，不能混：
               --   USED_EDITION = 父件那条 BOM 的版次（"我在哪一版父件里"）
               --   OWN_EDITION  = 它自己的 BOM 版次（"我自己的 BOM 是哪一版"，明细指定）
               LTRIM(RTRIM(ISNULL(t.OWN_EDITION, N''))) AS OWN_EDITION, t.PATH,
               ISNULL(pd.PRO_NAME, N'') AS PRO_NAME,
               ISNULL(pd.PRO_SPEC, N'') AS PRO_SPEC,
               ISNULL(pd.UNIT_ID, N'') AS UNIT_ID,
               -- 子件档案里"决定这份 BOM 能不能照着做"的四项：性质（成品/半成品/材料）、
               -- 来源（采购/自制/外包）、现有库存、是否停用。BOM 明细本身只有 8 列，
               -- 缺的从来不是明细，而是这些挂在上面的判断依据。
               LTRIM(RTRIM(ISNULL(pd.PRO_TYPE, N''))) AS PRO_TYPE,
               LTRIM(RTRIM(ISNULL(pd.MAIN_SOURCE, N''))) AS MAIN_SOURCE,
               CAST(ISNULL(pd.QTY, 0) AS decimal(18,6)) AS STOCK_QTY,
               CASE WHEN ISNULL(pd.STOP_TAG, 0) = 1 THEN 1 ELSE 0 END AS STOP_TAG,
               -- 有没有下阶：明细指定了子件版本才有
               CASE WHEN NULLIF(LTRIM(RTRIM(t.OWN_EDITION)), N'') IS NOT NULL THEN 1 ELSE 0 END AS HAS_CHILDREN,
               -- 子件版本写进明细后不再有"回退"这回事；恒 0 只为保持响应形状
               0 AS IS_FALLBACK
        FROM TREE t
        LEFT JOIN dbo.PRODUCT pd ON pd.PRO_NO = t.PRO_NO
        ORDER BY t.PATH
        OPTION (MAXRECURSION {(int)MaxDepthLimit});
        """;

    /// <summary>
    /// 毛需求：只取叶子（没有选中版次的下阶 BOM），按料号合并。
    /// 损耗沿路径累乘（<see cref="BomTreeRow.LostFactor"/>），不是只加最后一层——
    /// 三层各 10% 的实际放大是 1.331 而不是 1.3。
    /// </summary>
    private static readonly string GrossSql = $"""
        WITH TREE AS (
            SELECT CAST(N'/' + LTRIM(RTRIM(d.ELEMENT_PRO_NO)) + N'/' AS nvarchar(4000)) AS PATH,
                   1 AS LVL,
                   LTRIM(RTRIM(d.ELEMENT_PRO_NO)) AS PRO_NO,
                   CAST(d.EDITION AS nchar(10)) AS USED_EDITION,
                   CAST(d.ELEMENT_QTY / NULLIF(d.BASE_QTY, 0) AS decimal(38,10)) AS QTY_CUM,
                   CAST(1 + ISNULL(d.LOST_RATE, 0) / 100.0 AS decimal(38,10)) AS LOST_FACTOR,
                   CAST(d.ELEMENT_EDITION AS nchar(10)) AS OWN_EDITION
            FROM dbo.BOM_STRU_D d
            WHERE d.PRO_NO = @rootProNo
              AND d.EDITION = @rootEdition

            UNION ALL

            SELECT CAST(t.PATH + LTRIM(RTRIM(d.ELEMENT_PRO_NO)) + N'/' AS nvarchar(4000)),
                   t.LVL + 1,
                   LTRIM(RTRIM(d.ELEMENT_PRO_NO)),
                   CAST(d.EDITION AS nchar(10)),
                   CAST(t.QTY_CUM * d.ELEMENT_QTY / NULLIF(d.BASE_QTY, 0) AS decimal(38,10)),
                   CAST(t.LOST_FACTOR * (1 + ISNULL(d.LOST_RATE, 0) / 100.0) AS decimal(38,10)),
                   CAST(d.ELEMENT_EDITION AS nchar(10))
            FROM dbo.BOM_STRU_D d
            JOIN TREE t ON d.PRO_NO = t.PRO_NO
                       AND CAST(d.EDITION AS nchar(10)) = t.OWN_EDITION
            WHERE t.LVL < @maxDepth
              AND CHARINDEX(N'/' + LTRIM(RTRIM(d.ELEMENT_PRO_NO)) + N'/', t.PATH) = 0
        )
        SELECT t.PRO_NO,
               SUM(t.QTY_CUM) AS QTY_PER_UNIT,
               SUM(t.QTY_CUM * t.LOST_FACTOR) AS QTY_PER_UNIT_WITH_LOSS,
               SUM(t.QTY_CUM * t.LOST_FACTOR - t.QTY_CUM) AS LOST_QTY_PER_UNIT,
               MAX(ISNULL(pd.PRO_NAME, N'')) AS PRO_NAME,
               MAX(ISNULL(pd.PRO_SPEC, N'')) AS PRO_SPEC,
               MAX(ISNULL(pd.UNIT_ID, N'')) AS UNIT_ID
        FROM TREE t
        LEFT JOIN dbo.PRODUCT pd ON pd.PRO_NO = t.PRO_NO
        -- 叶子 = 明细没有指定子件版本（材料）。与展开同一判据，树与毛需求不会再各算各的
        WHERE NULLIF(LTRIM(RTRIM(t.OWN_EDITION)), N'') IS NULL
        GROUP BY t.PRO_NO
        ORDER BY t.PRO_NO
        OPTION (MAXRECURSION {(int)MaxDepthLimit});
        """;

    /// <summary>
    /// 反查用途：给定料号，向上找所有用到它的父件。只认每个父件<b>选中版次</b>的明细——
    /// 否则会把已经停用版本的引用也算进来，用户改一个料会被吓一跳。
    /// </summary>
    private static readonly string WhereUsedSql = $"""
        WITH {EditionPick},
        UP AS (
            SELECT CAST(N'/' + LTRIM(RTRIM(d.PRO_NO)) + N'/' AS nvarchar(4000)) AS PATH,
                   0 AS LVL,
                   LTRIM(RTRIM(d.PRO_NO)) AS PARENT_NO,
                   d.EDITION AS PARENT_EDITION,
                   d.SERIAL_NO,
                   CAST(d.ELEMENT_QTY / NULLIF(d.BASE_QTY, 0) AS decimal(38,10)) AS QTY_PER
            FROM dbo.BOM_STRU_D d
            JOIN EDITION_PICK p ON p.PRO_NO = d.PRO_NO
                               AND p.EDITION = d.EDITION
                               AND p.RN = 1
            WHERE d.ELEMENT_PRO_NO = @rootProNo

            UNION ALL

            SELECT CAST(u.PATH + LTRIM(RTRIM(d.PRO_NO)) + N'/' AS nvarchar(4000)),
                   u.LVL + 1,
                   LTRIM(RTRIM(d.PRO_NO)),
                   d.EDITION,
                   d.SERIAL_NO,
                   CAST(d.ELEMENT_QTY / NULLIF(d.BASE_QTY, 0) AS decimal(38,10))
            FROM dbo.BOM_STRU_D d
            JOIN UP u ON d.ELEMENT_PRO_NO = u.PARENT_NO
            JOIN EDITION_PICK p ON p.PRO_NO = d.PRO_NO
                               AND p.EDITION = d.EDITION
                               AND p.RN = 1
            WHERE u.LVL < @maxDepth
              AND CHARINDEX(N'/' + LTRIM(RTRIM(d.PRO_NO)) + N'/', u.PATH) = 0
        )
        SELECT u.LVL, u.PARENT_NO, LTRIM(RTRIM(u.PARENT_EDITION)) AS PARENT_EDITION,
               u.SERIAL_NO, u.QTY_PER, u.PATH,
               ISNULL(pd.PRO_NAME, N'') AS PARENT_NAME,
               ISNULL(pd.PRO_SPEC, N'') AS PARENT_SPEC
        FROM UP u
        LEFT JOIN dbo.PRODUCT pd ON pd.PRO_NO = u.PARENT_NO
        ORDER BY u.PATH
        OPTION (MAXRECURSION {(int)MaxDepthLimit});
        """;

    /// <summary>料号是否存在（用于区分"没有这张 BOM"与"查询为空"）。</summary>
    public async Task<bool> ProductExistsAsync(string proNo, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.PRODUCT p WHERE p.PRO_NO = @proNo) THEN 1 ELSE 0 END;",
            connection);
        command.Parameters.Add("@proNo", SqlDbType.NVarChar, 60).Value = proNo;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) == 1;
    }

    /// <summary>
    /// 取根节点的版次：调用方给了就用，没给就取该料号的最大版次。
    /// 返回 null 表示该料号没有 BOM——调用方据此给"找不到"而不是空结果。
    /// </summary>
    public async Task<string?> ResolveEditionAsync(string proNo, string? edition, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 LTRIM(RTRIM(m.EDITION))
            FROM dbo.BOM_STRU_M m
            WHERE m.PRO_NO = @proNo
              AND (@edition IS NULL OR m.EDITION = @edition)
            ORDER BY m.EDITION DESC;
            """,
            connection);
        command.Parameters.Add("@proNo", SqlDbType.NVarChar, 60).Value = proNo;
        command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value =
            string.IsNullOrWhiteSpace(edition) ? DBNull.Value : edition.Trim();
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : (string)value;
    }

    /// <summary>
    /// 产品品名（没有档案时返回空串）。
    ///
    /// 单独一个方法而不是并进 <see cref="ExpandAsync"/>：树的每一行都自带品名（来自 PRODUCT 的
    /// 联表），但**根节点自己不是任何明细行**，它的品名取不到——树又要把根画成同一形状，
    /// 所以调用方需要单独补一次。
    /// </summary>
    public async Task<string> ProductNameAsync(string proNo, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(ISNULL(PRO_NAME, N''))) FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) = @proNo;",
            connection);
        command.Parameters.Add("@proNo", SqlDbType.NVarChar, 60).Value = proNo;
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? string.Empty : (string)value;
    }

    /// <summary>
    /// 该产品有哪些版次（升序）。
    ///
    /// 结构树页要能就地切版次：版次是**同一条 BOM 的切面**，不是"另一个产品"，
    /// 为看 02 版退回列表再进来是多余的往返。产品之间不提供切换——那才是换一棵树。
    /// </summary>
    public async Task<IReadOnlyList<string>> EditionsAsync(string proNo, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(EDITION)) FROM dbo.BOM_STRU_M WHERE PRO_NO = @pro ORDER BY EDITION;",
            connection);
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = proNo;
        var editions = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token)) editions.Add(reader.GetString(0));
        return editions;
    }

    /// <summary>多层展开。根节点没有 BOM 时返回空集，由调用方判定为"找不到"。</summary>
    public async Task<IReadOnlyList<BomTreeRow>> ExpandAsync(
        string proNo, string edition, int maxDepth, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(TreeSql, connection);
        AddCommonParameters(command, proNo, edition, maxDepth);
        var rows = new List<BomTreeRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new BomTreeRow(
                Level: reader.GetInt32(0),
                ProNo: reader.GetString(1).Trim(),
                Edition: reader.GetString(2),
                QtyPer: reader.GetDecimal(3),
                QtyCum: reader.GetDecimal(4),
                LostRate: reader.GetDecimal(5),
                LostFactor: reader.GetDecimal(6),
                SerialNo: reader.GetInt16(7),
                BaseQty: reader.GetDecimal(8),
                Remark: reader.GetString(9),
                OwnEdition: reader.GetString(10),
                Path: reader.GetString(11),
                ProName: reader.GetString(12),
                ProSpec: reader.GetString(13),
                UnitId: reader.GetString(14).Trim(),
                ProType: reader.GetString(15),
                MainSource: reader.GetString(16),
                StockQty: reader.GetDecimal(17),
                StopTag: reader.GetInt32(18) == 1,
                HasChildren: reader.GetInt32(19) == 1,
                IsFallback: reader.GetInt32(20) == 1));
        }
        return rows;
    }

    /// <summary>毛需求：叶子按料号合并，含逐层累乘后的损耗量。</summary>
    public async Task<IReadOnlyList<BomGrossRow>> GrossRequirementAsync(
        string proNo, string edition, int maxDepth, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(GrossSql, connection);
        AddCommonParameters(command, proNo, edition, maxDepth);
        var rows = new List<BomGrossRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new BomGrossRow(
                ProNo: reader.GetString(0).Trim(),
                QtyPerUnit: reader.GetDecimal(1),
                QtyPerUnitWithLoss: reader.GetDecimal(2),
                LostQtyPerUnit: reader.GetDecimal(3),
                ProName: reader.GetString(4),
                ProSpec: reader.GetString(5),
                UnitId: reader.GetString(6).Trim()));
        }
        return rows;
    }

    /// <summary>反查用途：给定料号，向上找父件。</summary>
    public async Task<IReadOnlyList<BomWhereUsedRow>> WhereUsedAsync(
        string proNo, string edition, int maxDepth, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(WhereUsedSql, connection);
        AddCommonParameters(command, proNo, edition, maxDepth);
        var rows = new List<BomWhereUsedRow>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(new BomWhereUsedRow(
                Level: reader.GetInt32(0),
                ParentNo: reader.GetString(1).Trim(),
                ParentEdition: reader.GetString(2),
                SerialNo: reader.GetInt16(3),
                QtyPer: reader.GetDecimal(4),
                Path: reader.GetString(5),
                ParentName: reader.GetString(6),
                ParentSpec: reader.GetString(7)));
        }
        return rows;
    }

    private static void AddCommonParameters(SqlCommand command, string proNo, string edition, int maxDepth)
    {
        command.Parameters.Add("@rootProNo", SqlDbType.NVarChar, 60).Value = proNo;
        command.Parameters.Add("@rootEdition", SqlDbType.NVarChar, 20).Value = edition;
        command.Parameters.Add("@maxDepth", SqlDbType.Int).Value = ClampDepth(maxDepth);
    }

    /// <summary>
    /// 新建版本：把 <paramref name="edition"/> 的整棵结构复制到下一个版次，返回新版次。
    ///
    /// 版次由服务端算（`MAX+1`，两位数字，上限 99）——版次是主键，让客户端传等于把主键
    /// 交给它。只复制结构、不复制状态：批量/注意事项/计划员/备注/历史长宽照抄，
    /// 制单与确认重置；明细项次原样保留，两版才能逐行对比。
    ///
    /// 两条 INSERT 在同一事务里：主表插入了、明细没插入是最坏的中间态。
    /// </summary>
    public async Task<string> CreateNewVersionAsync(
        string productNo, string edition, string executor, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        // 表结构漂移的早失败：缺列就报出来，而不是拼出一句语法正确、语义错误的 SQL。
        var columns = await new EffectPhysicalColumns().LoadAsync(connection, token, transaction);
        EnsureVersionColumns(columns);

        var next = await NextEditionAsync(connection, transaction, productNo, token);
        await CopyVersionAsync(connection, transaction, productNo, edition, next, executor, token);
        await transaction.CommitAsync(token);
        return next;
    }

    private static void EnsureVersionColumns(ISet<string> columns)
    {
        foreach (var (table, column) in new[]
                 {
                     ("BOM_STRU_M", "PRO_NO"), ("BOM_STRU_M", "EDITION"),
                     ("BOM_STRU_M", "BATCH_QTY"), ("BOM_STRU_M", "ATTENTION"),
                     ("BOM_STRU_M", "PLAN_PERSON"), ("BOM_STRU_M", "REMARK"),
                     ("BOM_STRU_M", "P_LENGTH_OLD"), ("BOM_STRU_M", "P_WIDTH_OLD"),
                     ("BOM_STRU_M", "CREATED_BY"), ("BOM_STRU_M", "CREATED_TIME"),
                     ("BOM_STRU_M", "MODIFIED_BY"), ("BOM_STRU_M", "MODIFIED_TIME"),
                     ("BOM_STRU_M", "APPROVED"),
                     ("BOM_STRU_D", "PRO_NO"), ("BOM_STRU_D", "EDITION"), ("BOM_STRU_D", "SERIAL_NO"),
                     ("BOM_STRU_D", "ELEMENT_PRO_NO"), ("BOM_STRU_D", "ELEMENT_EDITION"),
                     ("BOM_STRU_D", "ELEMENT_QTY"),
                     ("BOM_STRU_D", "BASE_QTY"), ("BOM_STRU_D", "LOST_RATE"), ("BOM_STRU_D", "REMARK"),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new InvalidOperationException($"产品 BOM 表结构已变：缺少列 {table}.{column}，新建版本中止。");
        }
    }

    /// <summary>取下一个版次：当前最大版次 + 1，两位补零；到 99 就拒绝，不偷偷进位成三位。</summary>
    private static async Task<string> NextEditionAsync(
        SqlConnection connection, SqlTransaction transaction, string productNo, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT MAX(EDITION) FROM dbo.BOM_STRU_M WHERE PRO_NO=@pro;", connection, transaction);
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
        var value = await command.ExecuteScalarAsync(token);
        if (value is null or DBNull)
            throw new InvalidOperationException($"料号 {productNo} 没有可复制的 BOM 版本。");

        var current = ((string)value).Trim();
        if (!int.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            throw new InvalidOperationException($"料号 {productNo} 的当前版次「{current}」不是两位数字，先修正后再新建版本。");
        if (number >= MaxEdition)
            throw new InvalidOperationException($"料号 {productNo} 已到版次 {MaxEdition}，不能再新建（版次是两位数字）。");

        return (number + 1).ToString("00", CultureInfo.InvariantCulture);
    }

    private static async Task CopyVersionAsync(
        SqlConnection connection, SqlTransaction transaction,
        string productNo, string edition, string nextEdition, string executor, CancellationToken token)
    {
        var insertMaster = """
            INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION, BATCH_QTY, ATTENTION, PLAN_PERSON, REMARK,
                                        P_LENGTH_OLD, P_WIDTH_OLD, CREATED_BY, CREATED_TIME,
                                        MODIFIED_BY, MODIFIED_TIME, APPROVED)
            SELECT PRO_NO, @newEdition, BATCH_QTY, ATTENTION, PLAN_PERSON, REMARK,
                   P_LENGTH_OLD, P_WIDTH_OLD, @executor, SYSDATETIME(),
                   @executor, SYSDATETIME(), 0
            FROM dbo.BOM_STRU_M WHERE PRO_NO=@pro AND EDITION=@edition;
            """;
        await using (var command = new SqlCommand(insertMaster, connection, transaction))
        {
            command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
            command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = edition;
            command.Parameters.Add("@newEdition", SqlDbType.NVarChar, 20).Value = nextEdition;
            command.Parameters.Add("@executor", SqlDbType.NVarChar, 100).Value = executor;
            if (await command.ExecuteNonQueryAsync(token) == 0)
                throw new InvalidOperationException($"料号 {productNo} 的版次 {edition} 不存在，无法复制。");
        }

        var insertDetail = """
            INSERT INTO dbo.BOM_STRU_D
                (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_EDITION, ELEMENT_QTY, BASE_QTY, LOST_RATE, REMARK)
            SELECT PRO_NO, @newEdition, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_EDITION, ELEMENT_QTY, BASE_QTY, LOST_RATE, REMARK
            FROM dbo.BOM_STRU_D WHERE PRO_NO=@pro AND EDITION=@edition;
            """;
        await using (var command = new SqlCommand(insertDetail, connection, transaction))
        {
            command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
            command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = edition;
            command.Parameters.Add("@newEdition", SqlDbType.NVarChar, 20).Value = nextEdition;
            await command.ExecuteNonQueryAsync(token);
        }
    }

    /// <summary>
    /// 版本对比：按子件对齐两个版次，给出 added / removed / changed / same。
    ///
    /// 比的是**原始列**（`ELEMENT_QTY` / `BASE_QTY` / `LOST_RATE` / `REMARK`），不是折算后的
    /// 单件用量：用户改的就是这几列，拿折算值比会把"改了底数"显示成"改了用量"。
    ///
    /// 同一版次下同一子件理论上一行（保存时按子件查重），但历史数据可能有重复，
    /// 所以按子件取第一行而不是 `ToDictionary`——重复数据不该让整个对比失败。
    /// </summary>
    public async Task<BomComparison> CompareAsync(
        string productNo, string editionA, string editionB, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var left = await LoadVersionLinesAsync(connection, productNo, editionA, token);
        var right = await LoadVersionLinesAsync(connection, productNo, editionB, token);

        var rows = new List<BomComparisonRow>();
        foreach (var proNo in left.Keys.Union(right.Keys).OrderBy(key => key, StringComparer.Ordinal))
        {
            left.TryGetValue(proNo, out var a);
            right.TryGetValue(proNo, out var b);
            // 子件版本也参与"是否相同"的判定：只改了子件版本（A1 从 01 换到 02）
            // 对下游是实质变化，不能报成 same
            var status = a is null ? "added"
                : b is null ? "removed"
                : a.Qty == b.Qty && a.BaseQty == b.BaseQty && a.LostRate == b.LostRate
                    && a.Remark == b.Remark && a.ElementEdition == b.ElementEdition
                    ? "same"
                    : "changed";
            rows.Add(new BomComparisonRow(
                proNo,
                a?.SerialNo, b?.SerialNo,
                a?.ElementEdition, b?.ElementEdition,
                a?.Qty, b?.Qty,
                a?.BaseQty, b?.BaseQty,
                a?.LostRate, b?.LostRate,
                a?.Remark, b?.Remark,
                status));
        }
        return new BomComparison(productNo, editionA, editionB, rows);
    }

    /// <summary>
    /// 取试算表：把外部算好的明细行导入当前版次（追加或整体替换）。
    ///
    /// 三条 fail-closed：
    ///   ① 主表该版次必须存在——否则插进去的是孤儿明细（`no-cycle` 与展开都看不到它）；
    ///   ② 子件必须存在于产品档案——导入最常见的失败是"试算表里有个料还没建档"，
    ///      必须指名是哪一行，而不是让整批静默失败；
    ///   ③ 底数必须 > 0、损耗率必须 0..100——与保存期 `line-require` 同口径，
    ///      导入绕过它等于从侧门把非法数据放进来。
    ///
    /// 项次由服务端接续分配（`MAX + 1` 逐行递增），不接受外部给号：项次是明细行的身份，
    /// 下游单据按"单号 + 项次"引用它，外部给号撞号时会覆盖别人的引用。
    /// </summary>
    public async Task<int> ImportLinesAsync(
        string productNo, string edition, bool replace,
        IReadOnlyList<BomImportLine> lines, string executor, CancellationToken token)
    {
        if (lines.Count == 0) return 0;
        if (lines.Count > MaxImportLines)
            throw new InvalidOperationException($"一次最多导入 {MaxImportLines} 行，当前 {lines.Count} 行。");

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        var versionExists = await ExistsAsync(connection, transaction,
            "SELECT TOP 1 1 FROM dbo.BOM_STRU_M WHERE PRO_NO=@pro AND EDITION=@edition;",
            productNo, edition, token);
        if (!versionExists)
            throw new InvalidOperationException($"料号 {productNo} 的版次 {edition} 不存在，无法导入明细。");

        // 子件存在性一次查完：逐行查库在 200 行时就是 200 次往返
        var elements = lines.Select(line => line.ElementProNo).Distinct(StringComparer.Ordinal).ToArray();
        var known = await LoadKnownProductsAsync(connection, transaction, elements, token);
        var missing = elements.Where(element => !known.Contains(element)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException($"以下子件在料件档案中不存在：{string.Join("、", missing.Take(10))}"
                + (missing.Length > 10 ? $" 等 {missing.Length} 个" : string.Empty));

        // 子件版本判据与保存期 `require-when-referenced` 同一套：
        //   有下阶 BOM → 必填且必须命中；没有 BOM（材料）→ 必须留空。
        var (withBom, bomVersions) = await LoadBomVersionsAsync(connection, transaction, elements, token);

        if (replace)
        {
            await using var delete = new SqlCommand(
                "DELETE FROM dbo.BOM_STRU_D WHERE PRO_NO=@pro AND EDITION=@edition;", connection, transaction);
            delete.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
            delete.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = edition;
            await delete.ExecuteNonQueryAsync(token);
        }

        foreach (var line in lines)
        {
            if (line.BaseQty <= 0)
                throw new InvalidOperationException($"子件 {line.ElementProNo} 的底数必须大于 0。");
            if (line.LostRate < 0 || line.LostRate > 100)
                throw new InvalidOperationException($"子件 {line.ElementProNo} 的损耗率必须在 0..100 之间。");
            if (line.Qty < 0)
                throw new InvalidOperationException($"子件 {line.ElementProNo} 的用量不能为负。");

            var elementEdition = (line.ElementEdition ?? string.Empty).Trim();
            if (withBom.Contains(line.ElementProNo))
            {
                if (elementEdition.Length == 0)
                    throw new InvalidOperationException($"子件 {line.ElementProNo} 有下阶 BOM，必须指定子件版次。");
                if (elementEdition.Length != 2 || !elementEdition.All(char.IsAsciiDigit))
                    throw new InvalidOperationException($"子件 {line.ElementProNo} 的版次「{elementEdition}」不是两位数字。");
                if (!bomVersions.Contains(line.ElementProNo + "|" + elementEdition))
                    throw new InvalidOperationException(
                        $"子件 {line.ElementProNo} 在版次 {elementEdition} 下没有 BOM，版次不存在。");
            }
            else if (elementEdition.Length > 0)
            {
                // 材料填了版本也不成立：它没有 BOM，填什么都是错的
                throw new InvalidOperationException($"子件 {line.ElementProNo} 没有下阶 BOM，子件版次必须留空。");
            }

            await using var insert = new SqlCommand("""
                INSERT INTO dbo.BOM_STRU_D
                    (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_EDITION, ELEMENT_QTY, BASE_QTY, LOST_RATE, REMARK)
                SELECT @pro, @edition,
                       ISNULL((SELECT MAX(SERIAL_NO) FROM dbo.BOM_STRU_D WHERE PRO_NO=@pro AND EDITION=@edition), 0) + 1,
                       @element, @elementEdition, @qty, @baseQty, @lostRate, @remark;
                """, connection, transaction);
            insert.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
            insert.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = edition;
            insert.Parameters.Add("@element", SqlDbType.NVarChar, 60).Value = line.ElementProNo;
            insert.Parameters.Add("@elementEdition", SqlDbType.NVarChar, 2).Value =
                elementEdition.Length == 0 ? DBNull.Value : elementEdition;
            insert.Parameters.Add("@qty", SqlDbType.Decimal).Value = line.Qty;
            insert.Parameters.Add("@baseQty", SqlDbType.Decimal).Value = line.BaseQty;
            insert.Parameters.Add("@lostRate", SqlDbType.Decimal).Value = line.LostRate;
            insert.Parameters.Add("@remark", SqlDbType.NVarChar, 200).Value = (object?)line.Remark ?? DBNull.Value;
            await insert.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
        return lines.Count;
    }

    /// <summary>一次导入的行数上限：与试算表规模匹配，同时挡住"把整张表当 BOM 导进来"。</summary>
    public const int MaxImportLines = 500;

    private static async Task<bool> ExistsAsync(
        SqlConnection connection, SqlTransaction transaction, string sql,
        string productNo, string edition, CancellationToken token)
    {
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
        command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = edition;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>
    /// 一次查出这些料号里"谁有 BOM、有哪些版次"，组合键用 `PRO_NO|EDITION` 拼接。
    ///
    /// 为什么要两个集合：判断"该不该填版本"看的是**这个料号有没有 BOM**；
    /// 判断"填的版本对不对"看的是**组合是否存在**。材料两者都不满足，半成品都要满足。
    /// </summary>
    private static async Task<(HashSet<string> WithBom, HashSet<string> Versions)> LoadBomVersionsAsync(
        SqlConnection connection, SqlTransaction transaction, string[] productNos, CancellationToken token)
    {
        var withBom = new HashSet<string>(StringComparer.Ordinal);
        var versions = new HashSet<string>(StringComparer.Ordinal);
        if (productNos.Length == 0) return (withBom, versions);

        // 参数上限 2100，已按 MaxImportLines 限过；批量 200 一档避免语句过长
        foreach (var chunk in productNos.Chunk(200))
        {
            var names = chunk.Select((_, index) => "@b" + index).ToArray();
            await using var command = new SqlCommand(
                $"SELECT LTRIM(RTRIM(PRO_NO)), LTRIM(RTRIM(EDITION)) FROM dbo.BOM_STRU_M"
                + $" WHERE LTRIM(RTRIM(PRO_NO)) IN ({string.Join(",", names)});",
                connection, transaction);
            for (var index = 0; index < chunk.Length; index++)
                command.Parameters.Add(names[index], SqlDbType.NVarChar, 60).Value = chunk[index];
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                var proNo = reader.GetString(0);
                withBom.Add(proNo);
                versions.Add(proNo + "|" + reader.GetString(1));
            }
        }
        return (withBom, versions);
    }

    private static async Task<HashSet<string>> LoadKnownProductsAsync(
        SqlConnection connection, SqlTransaction transaction, string[] productNos, CancellationToken token)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        if (productNos.Length == 0) return known;
        // 参数上限 2100，这里已经按 MaxImportLines 限过；批量 200 一档避免语句过长
        foreach (var chunk in productNos.Chunk(200))
        {
            var names = chunk.Select((_, index) => "@p" + index).ToArray();
            await using var command = new SqlCommand(
                $"SELECT LTRIM(RTRIM(PRO_NO)) FROM dbo.PRODUCT WHERE LTRIM(RTRIM(PRO_NO)) IN ({string.Join(",", names)});",
                connection, transaction);
            for (var index = 0; index < chunk.Length; index++)
                command.Parameters.Add(names[index], SqlDbType.NVarChar, 60).Value = chunk[index];
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
                known.Add(reader.GetString(0));
        }
        return known;
    }

    private static async Task<Dictionary<string, BomVersionLine>> LoadVersionLinesAsync(
        SqlConnection connection, string productNo, string edition, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ELEMENT_PRO_NO)), LTRIM(RTRIM(ISNULL(ELEMENT_EDITION, N''))), SERIAL_NO,
                   ISNULL(ELEMENT_QTY, 0), ISNULL(BASE_QTY, 1), ISNULL(LOST_RATE, 0),
                   LTRIM(RTRIM(ISNULL(REMARK, N'')))
            FROM dbo.BOM_STRU_D
            WHERE PRO_NO = @pro AND EDITION = @edition
            ORDER BY SERIAL_NO;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = productNo;
        command.Parameters.Add("@edition", SqlDbType.NVarChar, 20).Value = edition;
        var map = new Dictionary<string, BomVersionLine>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var element = reader.GetString(0);
            // 同子件多行时保留项次最小的那一行（ORDER BY SERIAL_NO 已保证先到先得）
            map.TryAdd(element, new BomVersionLine(
                element,
                reader.GetString(1),
                reader.GetInt16(2),
                reader.GetDecimal(3),
                // BASE_QTY 是 int（可为 NULL，SQL 侧已 ISNULL 兜成 1）；
                // 直接 GetDecimal 会抛 InvalidCastException，版本对比因此整个 500。
                Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture),
                reader.GetDecimal(5),
                reader.GetString(6)));
        }
        return map;
    }

    /// <summary>深度夹取：MAXRECURSION 是编译期常量，只能拼数字，所以必须是夹过的整数。</summary>
    internal static int ClampDepth(int maxDepth) => maxDepth switch
    {
        <= 0 => 1,
        > MaxDepthLimit => MaxDepthLimit,
        _ => maxDepth,
    };

    /// <summary>两位数字版次的格式上限（与 FIELDS.REGEX `^\d{2}$` 同口径）。</summary>
    public const int MaxEdition = 99;
}

/// <summary>一个版次里的一行明细快照（对比用原值，不折算）。</summary>
internal sealed record BomVersionLine(
    string ProNo, string ElementEdition, short SerialNo,
    decimal Qty, decimal BaseQty, decimal LostRate, string Remark);

/// <summary>
/// 导入的一行明细；项次由服务端分配，不接受外部给号。
///
/// <c>ElementEdition</c> 是**子件自己的** BOM 版次：半成品必填（它有自己的 BOM），
/// 材料必须留空（填任何版本都不成立）。这条判据与保存期的
/// `require-when-referenced` 同口径，导入不能从侧门放宽。
/// </summary>
public sealed record BomImportLine(
    string ElementProNo, string? ElementEdition,
    decimal Qty, decimal BaseQty, decimal LostRate, string? Remark);

/// <summary>版本对比的一行：按子件对齐两个版次；某侧为 null 表示该版次没有这个子件。</summary>
public sealed record BomComparisonRow(
    string ProNo,
    short? SerialNoA, short? SerialNoB,
    string? ElementEditionA, string? ElementEditionB,
    decimal? QtyA, decimal? QtyB,
    decimal? BaseQtyA, decimal? BaseQtyB,
    decimal? LostRateA, decimal? LostRateB,
    string? RemarkA, string? RemarkB,
    string Status);

/// <summary>版本对比结果；<c>Status</c> ∈ added / removed / changed / same。</summary>
public sealed record BomComparison(
    string ProNo, string EditionA, string EditionB, IReadOnlyList<BomComparisonRow> Rows);

/// <summary>
/// 结构树的一行。每行都带"这一层用了哪一版、是不是回退来的"，以及它在父件 BOM 明细里的
/// 位置（项次 / 底数 / 备注）——右栏要把它当成一条记录来显示，缺这三项就只剩用量与损耗。
/// </summary>
public sealed record BomTreeRow(
    int Level,
    string ProNo,
    string Edition,
    decimal QtyPer,
    decimal QtyCum,
    decimal LostRate,
    decimal LostFactor,
    short SerialNo,
    decimal BaseQty,
    string Remark,
    /// <summary>
    /// 该料号**自己的** BOM 版次（它作为父件时选中的版次）；没有下阶 BOM 时为空串。
    /// 与 <c>Edition</c>（父件那条 BOM 的版次）不是一回事，查本件记录必须用这个。
    /// </summary>
    string OwnEdition,
    string Path,
    string ProName,
    string ProSpec,
    string UnitId,
    /// <summary>产品性质：1 成品 / 2 半成品 / 3 材料 / 4 辅料（`FIELDS.FORM_OPTIONS` 同口径）。</summary>
    string ProType,
    /// <summary>主要来源：1 采购 / 2 自制 / 3 外包。</summary>
    string MainSource,
    /// <summary>现有库存数量。</summary>
    decimal StockQty,
    /// <summary>是否已停止使用。</summary>
    bool StopTag,
    bool HasChildren,
    bool IsFallback);

/// <summary>毛需求的一行（按料号合并后的叶子）。</summary>
public sealed record BomGrossRow(
    string ProNo,
    decimal QtyPerUnit,
    decimal QtyPerUnitWithLoss,
    decimal LostQtyPerUnit,
    string ProName,
    string ProSpec,
    string UnitId);

/// <summary>反查用途的一行（一个父件及其用量）。</summary>
public sealed record BomWhereUsedRow(
    int Level,
    string ParentNo,
    string ParentEdition,
    short SerialNo,
    decimal QtyPer,
    string Path,
    string ParentName,
    string ParentSpec);
