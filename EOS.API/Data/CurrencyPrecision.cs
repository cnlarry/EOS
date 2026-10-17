using System.Data;
using System.Data.Common;

namespace EOS.API.Data;

/// <summary>
/// 金额精度按币别读取（ADR-037 批 1）。
///
/// 背景：金额的小数位跟货币而不是跟系统——ISO 4217 里 JPY/VND 是 0 位、KWD/BHD/OMR 是 3 位、
/// CLF 是 4 位，全局常数 2 位会在这些币种下产生无意义的"分"。
///
/// 取值来源是 <c>CURR.DECIMAL_PLACES</c> 与 <c>CURR.ROUNDING_METHOD</c>（迁移 399）。
/// 币别为空或查不到时回落到 2 位 + 远离零舍入，与改造前的行为一致。
/// </summary>
public static class CurrencyPrecision
{
    public const int DefaultDecimals = 2;

    /// <summary>读取指定币别的金额精度与舍入方式。</summary>
    public static async Task<(int Decimals, MidpointRounding Rounding)> ReadAsync(
        DbConnection connection,
        DbTransaction? transaction,
        string? currId,
        CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(currId))
        {
            return (DefaultDecimals, MidpointRounding.AwayFromZero);
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT DECIMAL_PLACES, ROUNDING_METHOD FROM dbo.CURR WHERE CURR_ID = @id";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.DbType = DbType.String;
        parameter.Size = 10;
        parameter.Value = currId.Trim();
        command.Parameters.Add(parameter);

        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return (DefaultDecimals, MidpointRounding.AwayFromZero);
        }

        var decimals = reader.IsDBNull(0) ? DefaultDecimals : Convert.ToInt32(reader.GetValue(0));
        var method = reader.IsDBNull(1) ? "AwayFromZero" : Convert.ToString(reader.GetValue(1)) ?? "AwayFromZero";

        var rounding = method.Trim().ToUpperInvariant() switch
        {
            "TOEVEN" => MidpointRounding.ToEven,
            "TOZERO" => MidpointRounding.ToZero,
            _ => MidpointRounding.AwayFromZero,
        };

        // 0~6：低于 0 无意义，高于 6 与 decimal(18,4)/数量族精度的设计相悖
        return (Math.Clamp(decimals, 0, 6), rounding);
    }
}
