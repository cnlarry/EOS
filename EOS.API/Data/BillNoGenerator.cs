using Microsoft.Data.SqlClient;
using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// Bill number generator. Resolves the bill type template (e.g. BJK{YYMM}0000)
/// into a prefix + sequential number: BJK2608 + 0001.
/// Table/column names come from server-side metadata whitelist; all values are parameterized.
/// </summary>
public static class BillNoGenerator
{
    private static readonly Regex TrailingZeros = new(@"0+$", RegexOptions.Compiled);
    private static readonly Regex DateToken = new(@"\{[^\{\}]*\}", RegexOptions.Compiled);

    /// <summary>
    /// 解析单号表达式，返回（字头前缀，流水宽度）。
    /// 例：BJK{YYMM}0000 → ("BJK2608", 4)；{YYYY}000 → ("2026", 3)。
    /// </summary>
    public static (string Title, int Width) BuildCode(string expression, DateTime now)
    {
        var trailing = TrailingZeros.Match(expression).Value;
        var width = trailing.Length;
        var prefix = width > 0 ? expression[..(expression.Length - trailing.Length)] : expression;
        prefix = DateToken.Replace(prefix, match =>
        {
            var token = match.Value[1..^1];
            // Date token format: {YYMM} → yyMM, {YYYYMM} → yyyyMM, {YYMMDD} → yyMMdd, {YYYY} → yyyy
            token = token.Replace('Y', 'y').Replace('m', 'M').Replace('D', 'd');
            return now.ToString(token, CultureInfo.InvariantCulture);
        });
        return (prefix, width);
    }

    /// <summary>
    /// 为模块生成新单号（事务内）。返回 null 表示该模块未配置自动单号。
    /// billNoField/billTypeField 必须是服务端登记的白名单字段（调用方传配置值）。
    /// </summary>
    public static async Task<string?> GenerateAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        string masterTable,
        string billNoField,
        string billTypeField,
        CancellationToken token)
    {
        const string billKindSql = """
            SELECT TOP 1 LTRIM(RTRIM(BILL_CODE)) AS BILL_CODE, LTRIM(RTRIM(USED_BILL_NO)) AS USED_BILL_NO
            FROM dbo.BILLKIND WITH (NOLOCK)
            WHERE B_M_IDX=@ModuleId AND IS_DEFAULT=1 AND IS_AUTO=1
            ORDER BY BILL_CODE;
            """;
        await using var kindCommand = new SqlCommand(billKindSql, connection, transaction);
        kindCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await kindCommand.ExecuteReaderAsync(token);
        string? billCode = null;
        string? expression = null;
        if (await reader.ReadAsync(token))
        {
            billCode = reader.GetString(0).Trim();
            expression = reader.GetString(1).Trim();
        }
        await reader.DisposeAsync();
        if (billCode is null || string.IsNullOrEmpty(expression)) return null;

        var (title, width) = BuildCode(expression, DateTime.Now);
        if (width == 0) return title;

        // 取该单别 + 字头前缀下的最大单号（等价旧 GetMaxID）。
        // UPDLOCK+HOLDLOCK：事务内对匹配前缀加更新/范围锁，串行化并发取号，
        // 消除"取最大号+1"的并发撞号；调用方必须传入保存事务（CreateRecordAsync 保证）。
        var maxSql = $"SELECT MAX([{billNoField}]) FROM dbo.[{masterTable}] WITH (UPDLOCK, HOLDLOCK) WHERE [{billNoField}] LIKE @Prefix + '%' AND [{billTypeField}]=@BillCode";
        await using var maxCommand = new SqlCommand(maxSql, connection, transaction);
        maxCommand.Parameters.Add("@Prefix", SqlDbType.NVarChar, 100).Value = title;
        maxCommand.Parameters.Add("@BillCode", SqlDbType.NVarChar, 50).Value = billCode;
        var maxNo = await maxCommand.ExecuteScalarAsync(token) as string;

        var serial = 1L;
        if (!string.IsNullOrWhiteSpace(maxNo) && maxNo!.Trim().Length > title.Length)
        {
            var tail = maxNo.Trim()[title.Length..];
            if (long.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                serial = parsed + 1;
        }
        return title + serial.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
    }

    /// <summary>
    /// 取模块默认单别（BILLKIND.IS_DEFAULT=1 的 BILL_CODE），无默认配置返回 null。
    /// </summary>
    public static async Task<string?> GetDefaultBillCodeAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 LTRIM(RTRIM(BILL_CODE)) AS BILL_CODE
            FROM dbo.BILLKIND WITH (NOLOCK)
            WHERE B_M_IDX=@ModuleId AND IS_DEFAULT=1
            ORDER BY BILL_CODE;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        var value = await command.ExecuteScalarAsync(token);
        return value as string;
    }

    /// <summary>
    /// 模块是否配置自动单号（BILLKIND 存在 IS_DEFAULT=1 且 IS_AUTO=1 的单别）。
    /// </summary>
    public static async Task<bool> HasAutoBillNoAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        int moduleId,
        CancellationToken token)
    {
        const string sql = """
            SELECT TOP 1 1 FROM dbo.BILLKIND WITH (NOLOCK)
            WHERE B_M_IDX=@ModuleId AND IS_DEFAULT=1 AND IS_AUTO=1;
            """;
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        return await command.ExecuteScalarAsync(token) is not null;
    }
}
