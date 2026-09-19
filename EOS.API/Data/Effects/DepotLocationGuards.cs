using System.Data;
using System.Text.Json;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects;

/// <summary>
/// 库位哨兵行（`LOCATION_NO = '-'`）的形态契约。哨兵行是"该库别尚未指定位置"的兜底行：
/// 过账时未填位置一律归一到它，余额表的位置外键也指向它。它一旦被停用、改名或改父，
/// 该库别的库存就会落进一个找不到的位置——因此它的形态必须由服务端守住，不能靠界面自觉。
/// </summary>
internal static class DepotLocationSentinel
{
    /// <summary>哨兵行的固定形态（与建表时的预置口径一致）。</summary>
    public const string LocationNo = "-";
    public const string Path = "/-";
    public const string Type = "BIN";
    public const string Status = "A";
}

/// <summary>
/// `custom-validation` 的库位哨兵守卫（SAVE 阶段）：哨兵行只允许保持既有形态，
/// 任何"改父 / 改类型 / 改路径 / 停用"都会让该库别的库存失去落点，一律拒绝。
/// </summary>
internal static class DepotLocationGuard
{
    public const string HandlerKey = "depot-location-guard";

    public static async Task<string?> CheckAsync(
        CustomValidationContext context, JsonElement root, CancellationToken token)
    {
        var config = DepotLocationGuardConfig.Parse(root);
        var row = await DepotLocationGuardConfig.ReadSentinelAsync(context, config, token);
        if (row is null)
            return null;   // 不是哨兵行（或已被删除）：本守卫不管

        var problems = new List<string>();
        if (!string.Equals(row.ParentNo, string.Empty, StringComparison.Ordinal))
            problems.Add($"上级库位应为空，实为 {row.ParentNo}");
        if (!string.Equals(row.LocationType, DepotLocationSentinel.Type, StringComparison.Ordinal))
            problems.Add($"位置类型应为 {DepotLocationSentinel.Type}，实为 {row.LocationType}");
        if (!string.Equals(row.Path, DepotLocationSentinel.Path, StringComparison.Ordinal))
            problems.Add($"位置路径应为 {DepotLocationSentinel.Path}，实为 {row.Path}");
        if (!string.Equals(row.Status, DepotLocationSentinel.Status, StringComparison.Ordinal))
            problems.Add($"状态应为 {DepotLocationSentinel.Status}（启用），实为 {row.Status}");
        if (problems.Count == 0)
            return null;

        return config.Message + string.Join("；", problems);
    }
}

/// <summary>
/// `custom-validation` 的库位哨兵守卫（DELETE 阶段）：哨兵行不允许删除。
/// 删掉它，该库别之后的过账会因为"库位不存在"被拦，而存量余额仍指向一个已消失的位置。
/// </summary>
internal static class DepotLocationDeleteGuard
{
    public const string HandlerKey = "depot-location-delete-guard";

    public static async Task<string?> CheckAsync(
        CustomValidationContext context, JsonElement root, CancellationToken token)
    {
        var config = DepotLocationGuardConfig.Parse(root);
        var row = await DepotLocationGuardConfig.ReadSentinelAsync(context, config, token);
        return row is null ? null : config.DeleteMessage;
    }
}

/// <summary>
/// 两个哨兵守卫共用的参数与读取。（校验在删除**之前**执行，所以删除守卫仍能读到该行。）
/// </summary>
internal sealed record DepotLocationGuardConfig(string Table, string DepotField, string LocationField, string Message,
    string DeleteMessage)
{
    public static DepotLocationGuardConfig Parse(JsonElement root) =>
        new(Required(root, "table"), Required(root, "depotField"), Required(root, "locationField"),
            Required(root, "message"), Required(root, "deleteMessage"));

    /// <summary>按主键读该行；不是哨兵行（或不存在）返回 null。</summary>
    public static async Task<SentinelRow?> ReadSentinelAsync(
        CustomValidationContext context, DepotLocationGuardConfig config, CancellationToken token)
    {
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("库位哨兵守卫缺少主键值。");

        // 列名来自配置，必须落回物理白名单（fail-closed：配置写错在保存期就报，不留到 SQL 报错）。
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        foreach (var column in new[] { config.DepotField, config.LocationField })
            if (!columns.Contains(config.Table + "." + column))
                throw new EffectConfigException($"库位哨兵守卫列不存在：{config.Table}.{column}。");

        var depot = context.MasterKeyValues[0] ?? string.Empty;
        var locationNo = context.MasterKeyValues[1] ?? string.Empty;
        if (!string.Equals(locationNo.Trim(), DepotLocationSentinel.LocationNo, StringComparison.Ordinal))
            return null;

        await using var command = new SqlCommand(
            $"SELECT ISNULL(PARENT_NO, N''), LOCATION_TYPE, LOCATION_PATH, STATUS FROM dbo.{ServiceEffectSql.Q(config.Table)} "
            + $"WHERE {ServiceEffectSql.Q(config.DepotField)}=@Depot AND {ServiceEffectSql.Q(config.LocationField)}=@Location;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@Depot", SqlDbType.NVarChar, 10).Value = depot.Trim();
        command.Parameters.Add("@Location", SqlDbType.NVarChar, 30).Value = locationNo.Trim();
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            return null;
        return new SentinelRow(
            reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim(),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim(),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2).Trim(),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim());
    }

    private static string Required(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"库位哨兵守卫缺少字符串字段 {name}。");
}

internal sealed record SentinelRow(string ParentNo, string LocationType, string Path, string Status);
