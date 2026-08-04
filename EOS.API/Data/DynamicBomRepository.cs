using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed partial class DynamicBomRepository(
    IConfiguration configuration,
    FieldConfigurationRepository fields)
{
    private const int MaximumLimit = 200;

    public async Task<DynamicGridResult> SearchMasterAsync(
        string userId,
        string searchField,
        string keyword,
        int limit,
        bool allowCost,
        bool allowSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken cancellationToken)
    {
        var columns = await fields.GetSelectedAsync(
            userId, "master", allowCost, allowSecrecy, deniedFields, cancellationToken);
        var hasVisibleKey = columns.Any(column =>
            column.SourceTable == "BOM_STRU_M" && column.SourceField == "PRO_NO");
        var select = columns.Select(BuildMasterExpression).ToList();
        if (!hasVisibleKey)
            select.Add("BOM_STRU_M.PRO_NO AS [__rowKey]");
        var filter = GetFilter(searchField, keyword.Trim());
        var sql = $"""
            SELECT TOP (@Limit) {string.Join(", ", select)}
            FROM dbo.BOM_STRU_M AS BOM_STRU_M
            LEFT JOIN dbo.PRODUCT AS PRODUCT_M ON PRODUCT_M.PRO_NO = BOM_STRU_M.PRO_NO
            WHERE 1 = 1 {filter}
            ORDER BY BOM_STRU_M.PRO_NO ASC;
            """;

        return await ExecuteAsync(sql, columns, command =>
        {
            command.Parameters.Add("@Limit", SqlDbType.Int).Value = Math.Clamp(limit, 1, MaximumLimit);
            if (keyword.Trim().Length > 0)
                command.Parameters.Add("@Keyword", SqlDbType.NVarChar, 202).Value = $"%{keyword.Trim()}%";
        }, cancellationToken, hasVisibleKey ? null : "__rowKey");
    }

    public async Task<DynamicGridResult> GetDetailsAsync(
        string userId,
        string proNo,
        bool allowCost,
        bool allowSecrecy,
        IReadOnlySet<string> deniedFields,
        CancellationToken cancellationToken)
    {
        var columns = await fields.GetSelectedAsync(
            userId, "detail", allowCost, allowSecrecy, deniedFields, cancellationToken);
        var select = columns.Select(BuildDetailExpression).ToArray();
        if (select.Length == 0)
            return new DynamicGridResult(columns, [], 0);

        var joins = BuildDetailJoins(columns);
        var sql = $"""
            SELECT {string.Join(", ", select)}
            FROM dbo.BOM_STRU_D AS BOM_STRU_D
            LEFT JOIN dbo.PRODUCT AS PRODUCT ON PRODUCT.PRO_NO = BOM_STRU_D.ELEMENT_PRO_NO
            {joins}
            WHERE BOM_STRU_D.PRO_NO = @ProNo
            ORDER BY BOM_STRU_D.SERIAL_NO ASC;
            """;
        return await ExecuteAsync(sql, columns, command =>
        {
            command.Parameters.Add("@ProNo", SqlDbType.NChar, 30).Value = proNo.Trim();
        }, cancellationToken);
    }

    private async Task<DynamicGridResult> ExecuteAsync(
        string sql,
        IReadOnlyList<FieldDefinition> columns,
        Action<SqlCommand> parameters,
        CancellationToken cancellationToken,
        string? internalKeyColumn = null)
    {
        await using var connection = CreateConnection();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        parameters(command);
        await connection.OpenAsync(cancellationToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in columns)
            {
                var value = reader[column.Id];
                row[column.Id] = value is DBNull ? null : value;
            }
            if (internalKeyColumn is not null)
            {
                var key = reader[internalKeyColumn];
                row["__rowKey"] = key is DBNull ? null : key;
            }
            rows.Add(row);
        }
        return new DynamicGridResult(columns, rows, rows.Count);
    }

    private static string BuildMasterExpression(FieldDefinition field) =>
        BuildExpression(field, "BOM_STRU_M", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BOM_STRU_M", "PRODUCT_M" });

    private static string BuildDetailExpression(FieldDefinition field) =>
        BuildExpression(field, "BOM_STRU_D", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BOM_STRU_D", "PRODUCT", "PRODUCT_J", "STUFF", "COLOR" });

    private static string BuildExpression(
        FieldDefinition field,
        string physicalTable,
        IReadOnlySet<string> allowedAliases)
    {
        var expression = field.IsVirtual
            ? ValidateVirtualExpression(field.VirtualExpression, allowedAliases)
            : $"{physicalTable}.{QuoteIdentifier(field.SourceField)}";
        return $"{expression} AS {QuoteIdentifier(field.Id)}";
    }

    private static string ValidateVirtualExpression(
        string? expression,
        IReadOnlySet<string> allowedAliases)
    {
        if (string.IsNullOrWhiteSpace(expression) || DangerousSql().IsMatch(expression))
            throw new InvalidOperationException("虚拟字段表达式无效或包含不允许的 SQL 结构。");

        var aliases = AliasReference().Matches(expression)
            .Select(match => match.Groups[1].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase);
        if (aliases.Any(alias => !allowedAliases.Contains(alias)))
            throw new InvalidOperationException("虚拟字段引用了未授权的数据表。" );
        return expression;
    }

    private static string BuildDetailJoins(IReadOnlyList<FieldDefinition> columns)
    {
        var expressions = columns.Where(field => field.IsVirtual)
            .Select(field => field.VirtualExpression ?? string.Empty).ToArray();
        var joins = new List<string>();
        if (expressions.Any(expression => expression.Contains("PRODUCT_J.", StringComparison.OrdinalIgnoreCase)))
            joins.Add("LEFT JOIN dbo.PRODUCT AS PRODUCT_J ON PRODUCT_J.PRO_NO = BOM_STRU_D.PRO_NO");
        if (expressions.Any(expression => expression.Contains("STUFF.", StringComparison.OrdinalIgnoreCase)))
            joins.Add("LEFT JOIN dbo.STUFF AS STUFF ON STUFF.STUFF_ID = PRODUCT.STUFF_ID");
        if (expressions.Any(expression => expression.Contains("COLOR.", StringComparison.OrdinalIgnoreCase)))
            joins.Add("LEFT JOIN dbo.COLOR AS COLOR ON COLOR.COLOR_ID = PRODUCT.COLOR_ID");
        return string.Join(Environment.NewLine, joins);
    }

    private static string GetFilter(string searchField, string keyword)
    {
        if (keyword.Length == 0) return string.Empty;
        return searchField switch
        {
            "proName" => "AND PRODUCT_M.PRO_NAME LIKE @Keyword",
            "proSpec" => "AND PRODUCT_M.PRO_SPEC LIKE @Keyword",
            "elementProNo" => "AND EXISTS (SELECT 1 FROM dbo.BOM_STRU_D AS dx WHERE dx.PRO_NO = BOM_STRU_M.PRO_NO AND dx.ELEMENT_PRO_NO LIKE @Keyword)",
            _ => "AND BOM_STRU_M.PRO_NO LIKE @Keyword"
        };
    }

    private static string QuoteIdentifier(string value) => $"[{value.Replace("]", "]]", StringComparison.Ordinal)}]";

    private SqlConnection CreateConnection()
    {
        var connectionString = configuration.GetConnectionString("ErpDatabase");
        return !string.IsNullOrWhiteSpace(connectionString)
            ? new SqlConnection(connectionString)
            : throw new InvalidOperationException("ConnectionStrings:ErpDatabase 未配置。");
    }

    [GeneratedRegex(@"(;|--|/\*|\*/|\b(insert|update|delete|drop|alter|create|exec(?:ute)?|merge|truncate|grant|revoke)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousSql();

    [GeneratedRegex(@"\b([A-Za-z_][A-Za-z0-9_]*)\s*\.")]
    private static partial Regex AliasReference();
}
