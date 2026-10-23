using System.Data;

using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workbench;

/// <summary>
/// 表单数值上下文：币别/单位的小数位与舍入方式、以及模块字段的数值语义（<c>FIELDS.NUMERIC_ROLE</c>）。
///
/// 前端用它决定"这一格显示几位、按什么舍入"，服务端用它对齐落库精度（ADR-037）。数据访问此前写在
/// <c>DocumentWorkbenchController</c> 里，控制器因此同时是契约层与数据访问层（ADR-036 批 1 第 5 项）。
///
/// 三张表都是小而稳定的主数据（币别、单位、字段语义），读数不需要强一致，故不加锁提示——
/// 它们的读取次数极低，而精度口径错一位会让金额显示错，宁可走默认隔离级别。
/// </summary>
public sealed class NumericContextReader(DbConnectionFactory connections)
{
    /// <summary>一个模块的数值上下文。字典键保留库内原值（币别号、单位号、字段号），不做大小写改写。</summary>
    public sealed record NumericContext(
        IReadOnlyDictionary<string, int> CurrencyDecimals,
        IReadOnlyDictionary<string, string> CurrencyRounding,
        IReadOnlyDictionary<string, int> UnitDecimals,
        IReadOnlyDictionary<string, string> FieldRoles);

    /// <summary>读取上下文。<paramref name="tables"/> 为模块主表与（可选的）明细表。</summary>
    public async Task<NumericContext> ReadAsync(IReadOnlyList<string> tables, CancellationToken token)
    {
        var currencyDecimals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var currencyRounding = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unitDecimals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fieldRoles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        await using var connection = connections.Create();
        await connection.OpenAsync(token);

        await using (var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(CURR_ID)),DECIMAL_PLACES,ROUNDING_METHOD FROM dbo.CURR;", connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var id = reader.GetString(0).Trim();
                currencyDecimals[id] = reader.IsDBNull(1) ? 2 : Convert.ToInt32(reader.GetValue(1));
                currencyRounding[id] = reader.IsDBNull(2) ? "AwayFromZero" : Convert.ToString(reader.GetValue(2)) ?? "AwayFromZero";
            }
        }

        await using (var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(UNIT_ID)),DECIMAL_PLACES FROM dbo.UNIT;", connection))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                unitDecimals[reader.GetString(0).Trim()] = reader.IsDBNull(1) ? 3 : Convert.ToInt32(reader.GetValue(1));
            }
        }

        if (tables.Count > 0)
        {
            var placeholders = string.Join(',', tables.Select((_, index) => $"@t{index}"));
            await using var command = new SqlCommand(
                $"SELECT LTRIM(RTRIM(F_ID)),LTRIM(RTRIM(ISNULL(NUMERIC_ROLE,''))) FROM dbo.FIELDS "
                + $"WHERE T_ID IN ({placeholders}) AND ISNULL(NUMERIC_ROLE,'')<>'';", connection);
            for (var index = 0; index < tables.Count; index++)
            {
                command.Parameters.AddWithValue($"@t{index}", tables[index]);
            }
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                fieldRoles[reader.GetString(0).Trim()] = reader.GetString(1).Trim();
            }
        }

        return new NumericContext(currencyDecimals, currencyRounding, unitDecimals, fieldRoles);
    }
}
