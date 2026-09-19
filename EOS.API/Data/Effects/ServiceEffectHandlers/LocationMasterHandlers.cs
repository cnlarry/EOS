using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// `location-path-recalc`：库位物化路径（`LOCATION_PATH`）的重算**唯一收口点**。
///
/// 路径是"当前位置"的快照（历史位置记在库存流水上），而库位可以被改挂到别的上级，
/// 改了上级却不同步后代路径，整个子树就会指着一条不存在的路径——按区盘点、按区汇总都会跟着错。
/// 因此：保存一个库位后，按它的上级重算自身路径；路径确实变了，就把整棵子树的旧前缀换成新前缀
/// （更新行数 = 自身 + 子树节点数）。同时拒绝成环：不允许把节点挂到自己的后代下。
/// </summary>
public sealed class LocationPathRecalcHandler : IEffectServiceHandler
{
    public string EffectKey => "location-path-recalc";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        if (context.ExecutionEvent is not (EffectEvent.Save or EffectEvent.ApproveEffect)) return 0;
        if (context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("location-path-recalc 缺少主键值。");
        var root = context.Action.Params ?? throw new EffectConfigException("location-path-recalc 缺少参数。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = LocationPathConfig.Parse(root, columns);

        var depot = (context.MasterKeyValues[0] ?? string.Empty).Trim();
        var locationNo = (context.MasterKeyValues[1] ?? string.Empty).Trim();
        var q = ServiceEffectSql.Q;

        var (parentNo, oldPath) = await ReadNodeAsync(context, config, depot, locationNo, token);

        if (parentNo.Length > 0)
        {
            // 自己当自己的上级，或上级落在自己的后代里，都会把树拧成环——环一旦形成，
            // 按路径前缀的子树查询会无限展开。
            if (string.Equals(parentNo, locationNo, StringComparison.Ordinal)
                || await IsDescendantAsync(context, config, depot, locationNo, parentNo, token))
                throw new EffectValidationException($"库位 {locationNo} 不能挂在它自己或它的下级之下（会形成循环）。");
        }

        var parentPath = parentNo.Length == 0
            ? string.Empty
            : await ReadPathAsync(context, config, depot, parentNo, token);
        var newPath = parentPath + "/" + locationNo;
        if (string.Equals(newPath, oldPath, StringComparison.Ordinal))
            return 0;

        var affected = await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            $"UPDATE dbo.{q(config.Table)} SET {q(config.PathField)}=@newPath "
            + $"WHERE {q(config.DepotField)}=@depot AND {q(config.LocationField)}=@location;",
            [new EffectSqlParameter("@newPath", newPath), new EffectSqlParameter("@depot", depot),
             new EffectSqlParameter("@location", locationNo)], token);

        // 后代路径 = 新前缀 + 旧路径去掉旧前缀的剩余部分。
        // 用"截断比较"而不是 LIKE：库位号里可能含 % 或 _，LIKE 会把它们当通配符。
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            $"UPDATE d SET d.{q(config.PathField)} = @newPath + SUBSTRING(d.{q(config.PathField)}, LEN(@oldPath)+1, 3900) "
            + $"FROM dbo.{q(config.Table)} d WHERE d.{q(config.DepotField)}=@depot "
            + $"AND SUBSTRING(d.{q(config.PathField)}, 1, LEN(@oldPath)+1) = @oldPath + '/' "
            + $"AND d.{q(config.LocationField)}<>@location;",
            [new EffectSqlParameter("@newPath", newPath), new EffectSqlParameter("@oldPath", oldPath),
             new EffectSqlParameter("@depot", depot), new EffectSqlParameter("@location", locationNo)], token);
        return affected;
    }

    private static async Task<(string ParentNo, string Path)> ReadNodeAsync(
        ServiceEffectContext context, LocationPathConfig config, string depot, string locationNo, CancellationToken token)
    {
        await using var command = new SqlCommand(
            $"SELECT ISNULL({ServiceEffectSql.Q(config.ParentField)}, N''), {ServiceEffectSql.Q(config.PathField)} "
            + $"FROM dbo.{ServiceEffectSql.Q(config.Table)} "
            + $"WHERE {ServiceEffectSql.Q(config.DepotField)}=@depot AND {ServiceEffectSql.Q(config.LocationField)}=@location;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@depot", SqlDbType.NVarChar, 10).Value = depot;
        command.Parameters.Add("@location", SqlDbType.NVarChar, 30).Value = locationNo;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new EffectConfigException($"location-path-recalc 找不到库位 {depot}/{locationNo}。");
        return (reader.IsDBNull(0) ? string.Empty : reader.GetString(0).Trim(), reader.GetString(1).Trim());
    }

    private static async Task<string> ReadPathAsync(
        ServiceEffectContext context, LocationPathConfig config, string depot, string parentNo, CancellationToken token)
    {
        await using var command = new SqlCommand(
            $"SELECT {ServiceEffectSql.Q(config.PathField)} FROM dbo.{ServiceEffectSql.Q(config.Table)} "
            + $"WHERE {ServiceEffectSql.Q(config.DepotField)}=@depot AND {ServiceEffectSql.Q(config.LocationField)}=@parent;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@depot", SqlDbType.NVarChar, 10).Value = depot;
        command.Parameters.Add("@parent", SqlDbType.NVarChar, 30).Value = parentNo;
        var path = await command.ExecuteScalarAsync(token);
        if (path is null || path is DBNull)
            throw new EffectValidationException($"上级库位 {parentNo} 不存在。");
        return ((string)path).Trim();
    }

    /// <summary>沿上级链上溯，判断 <paramref name="candidate"/> 是否落在 <paramref name="node"/> 的子树里。</summary>
    private static async Task<bool> IsDescendantAsync(
        ServiceEffectContext context, LocationPathConfig config, string depot, string node, string candidate, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"""
             WITH UP AS (
                 SELECT {q(config.LocationField)} AS LOCATION_NO, {q(config.ParentField)} AS PARENT_NO
                   FROM dbo.{q(config.Table)} WHERE {q(config.DepotField)}=@depot AND {q(config.LocationField)}=@candidate
                 UNION ALL
                 SELECT l.{q(config.LocationField)}, l.{q(config.ParentField)}
                   FROM dbo.{q(config.Table)} l JOIN UP u ON l.{q(config.LocationField)}=u.PARENT_NO
                  WHERE l.{q(config.DepotField)}=@depot
             )
             SELECT TOP 1 1 FROM UP WHERE LTRIM(RTRIM(LOCATION_NO))=@node OPTION (MAXRECURSION 100);
             """, context.Connection, context.Transaction);
        command.Parameters.Add("@depot", SqlDbType.NVarChar, 10).Value = depot;
        command.Parameters.Add("@candidate", SqlDbType.NVarChar, 30).Value = candidate;
        command.Parameters.Add("@node", SqlDbType.NVarChar, 30).Value = node;
        return await command.ExecuteScalarAsync(token) is not null;
    }
}

/// <summary>
/// `depot-sentinel-location`：新建/修改库别后，保证该库别的哨兵位置行存在。
///
/// 哨兵行是余额表位置外键的落点，也是"未填位置"的归一目标：缺了它，该库别的任何过账都会
/// 卡在"库位不存在"上。放在库别保存路径上，是因为库别的新增入口就是它；直接改库导入的库别
/// 由后续保存补齐。
/// </summary>
public sealed class DepotSentinelLocationHandler : IEffectServiceHandler
{
    public string EffectKey => "depot-sentinel-location";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        if (context.ExecutionEvent is not (EffectEvent.Save or EffectEvent.ApproveEffect)) return 0;
        if (context.MasterKeyValues.Count < 1)
            throw new EffectConfigException("depot-sentinel-location 缺少库别主键值。");
        var root = context.Action.Params ?? throw new EffectConfigException("depot-sentinel-location 缺少参数。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = SentinelLocationConfig.Parse(root, columns);

        var depot = (context.MasterKeyValues[0] ?? string.Empty).Trim();
        if (depot.Length == 0)
            throw new EffectConfigException("depot-sentinel-location 库别代号为空。");

        var q = ServiceEffectSql.Q;
        // 只补不覆盖：已存在就原样保留（它可能已被归位作业改成别的形态，或已挂了库存）。
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction,
            $"INSERT INTO dbo.{q(config.Table)} "
            + $"({q(config.DepotField)}, {q(config.LocationField)}, {q(config.ParentField)}, {q(config.PathField)}, "
            + $"{q(config.TypeField)}, {q(config.NameField)}, {q(config.SeqField)}) "
            + "SELECT @depot, @location, NULL, @path, @type, @name, 0 "
            + $"WHERE NOT EXISTS (SELECT 1 FROM dbo.{q(config.Table)} "
            + $"WHERE {q(config.DepotField)}=@depot AND {q(config.LocationField)}=@location);",
            [new EffectSqlParameter("@depot", depot),
             new EffectSqlParameter("@location", DepotLocationSentinel.LocationNo),
             new EffectSqlParameter("@path", DepotLocationSentinel.Path),
             new EffectSqlParameter("@type", DepotLocationSentinel.Type),
             new EffectSqlParameter("@name", config.SentinelName)], token);
    }
}

internal sealed record LocationPathConfig(string Table, string DepotField, string LocationField, string ParentField,
    string PathField)
{
    public static LocationPathConfig Parse(JsonElement root, ISet<string> columns)
    {
        const string label = "location-path-recalc";
        var table = Required(root, "table", label);
        var config = new LocationPathConfig(table, Required(root, "depotField", label),
            Required(root, "locationField", label), Required(root, "parentField", label),
            Required(root, "pathField", label));
        foreach (var column in new[] { config.DepotField, config.LocationField, config.ParentField, config.PathField })
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"{label} 列不存在：{table}.{column}。");
        return config;
    }

    private static string Required(JsonElement root, string name, string label)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"{label} 缺少字符串字段 {name}。");
}

internal sealed record SentinelLocationConfig(string Table, string DepotField, string LocationField, string ParentField,
    string PathField, string TypeField, string NameField, string SeqField, string SentinelName)
{
    public static SentinelLocationConfig Parse(JsonElement root, ISet<string> columns)
    {
        const string label = "depot-sentinel-location";
        var table = Required(root, "table", label);
        var config = new SentinelLocationConfig(table, Required(root, "depotField", label),
            Required(root, "locationField", label), Required(root, "parentField", label),
            Required(root, "pathField", label), Required(root, "typeField", label),
            Required(root, "nameField", label), Required(root, "seqField", label),
            Required(root, "sentinelName", label));
        foreach (var column in new[]
                 {
                     config.DepotField, config.LocationField, config.ParentField, config.PathField,
                     config.TypeField, config.NameField, config.SeqField,
                 })
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"{label} 列不存在：{table}.{column}。");
        return config;
    }

    private static string Required(JsonElement root, string name, string label)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"{label} 缺少字符串字段 {name}。");
}
