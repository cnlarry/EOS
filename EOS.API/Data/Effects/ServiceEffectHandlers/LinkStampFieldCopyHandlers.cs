using System.Text.Json;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// link-stamp: writes link reference numbers / states from the module master onto a
/// related document. Shape A {targetTable, field|fields, mode}: same-named master
/// columns copied onto the target rows, joined on same-named master key columns.
/// Shape B {targets:[{table, ref:[cols]}], fields, finish}: ref names the target-side
/// columns tying back to the master key; finish=true additionally sets FINISHED_TAG=1
/// on the target (standard lifecycle column; revisited if evidence contradicts).
/// </summary>
public sealed class LinkStampHandler : IEffectServiceHandler
{
    public string EffectKey => "link-stamp";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("link-stamp 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null)
            throw new EffectConfigException("link-stamp 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token);

        var affected = 0;
        if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            var fields = ReadFields(root, "fields");
            var finish = root.TryGetProperty("finish", out var f) && f.ValueKind == JsonValueKind.True;
            foreach (var target in targets.EnumerateArray())
            {
                var table = target.TryGetProperty("table", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString()!.Trim()
                    : throw new EffectConfigException("link-stamp targets 项缺少 table。");
                var refs = ReadFields(target, "ref");
                affected += await StampAsync(context, table, refs, fields, finish, columns, token);
            }
        }
        else
        {
            var table = Required(root, "targetTable");
            var fields = ReadFields(root, "field") is { Length: > 0 } single
                ? single
                : ReadFields(root, "fields");
            affected += await StampAsync(context, table, Array.Empty<string>(), fields, finish: false, columns, token);
        }
        return affected;
    }

    private static async Task<int> StampAsync(
        ServiceEffectContext context,
        string table,
        IReadOnlyList<string> refs,
        IReadOnlyList<string> fields,
        bool finish,
        ISet<string> columns,
        CancellationToken token)
    {
        var plan = context.Plan;
        if (!columns.Contains(table))
            throw new EffectConfigException($"link-stamp 目标表不存在：{table}。");
        var sets = new List<string>();
        foreach (var field in fields)
        {
            if (!columns.Contains(table + "." + field) || !columns.Contains(plan.MasterTable + "." + field))
                throw new EffectConfigException($"link-stamp 字段不存在（主表或目标表）：{field}。");
            sets.Add($"T.{ServiceEffectSql.Q(field)} = M.{ServiceEffectSql.Q(field)}");
        }
        if (finish)
        {
            if (!columns.Contains(table + ".FINISHED_TAG"))
                throw new EffectConfigException($"link-stamp finish=true 但目标表 {table} 无 FINISHED_TAG 列。");
            sets.Add("T.[FINISHED_TAG] = 1");
        }
        if (sets.Count == 0)
            throw new EffectConfigException("link-stamp 缺少可回写字段。");

        var joinCondition = refs.Count > 0
            ? string.Join(" AND ", refs.Zip(plan.MasterPkOrder)
                .Select(pair => $"T.{ServiceEffectSql.Q(pair.First)} = M.{ServiceEffectSql.Q(pair.Second)}"))
            : ServiceEffectSql.SameNameKeyJoin(plan, "T");
        if (refs.Count > 0 && refs.Count != plan.MasterPkOrder.Count)
            throw new EffectConfigException($"link-stamp ref 列数({refs.Count})与主表主键数({plan.MasterPkOrder.Count})不一致。");
        var sql = $"UPDATE T SET {string.Join(", ", sets)} FROM dbo.{ServiceEffectSql.Q(table)} T "
            + $"JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M ON {joinCondition}";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, Array.Empty<EffectSqlParameter>(), token);
    }

    private static string[] ReadFields(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return Array.Empty<string>();
        if (value.ValueKind == JsonValueKind.String)
            return new[] { value.GetString()!.Trim() };
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .ToArray();
        throw new EffectConfigException($"link-stamp {name} 必须是字段名或字段名数组。");
    }

    private static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"link-stamp 缺少字符串字段 '{name}'。");
}

/// <summary>
/// field-copy: copies master columns onto related tables. Shape A {targetTable, field,
/// sourceField}; shape B {targets:[tables], fields, headerFields} where headerFields are
/// only applied to master-shaped tables (name ending in _M). Rows are located by the
/// same-named master key columns.
/// </summary>
public sealed class FieldCopyHandler : IEffectServiceHandler
{
    public string EffectKey => "field-copy";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("field-copy 缺少参数。");
        var plan = context.Plan;
        if (plan.MasterTable is null)
            throw new EffectConfigException("field-copy 需要主表形态。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token);

        var affected = 0;
        if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            var fields = ServiceEffectFields.Read(root, "fields");
            var headerFields = ServiceEffectFields.Read(root, "headerFields");
            foreach (var target in targets.EnumerateArray())
            {
                var table = target.GetString()!.Trim();
                var isMasterShape = table.EndsWith("_M", StringComparison.OrdinalIgnoreCase);
                var pairs = fields.Select(field => (Target: field, Source: field))
                    .Concat(isMasterShape ? headerFields.Select(field => (Target: field, Source: field)) : Array.Empty<(string, string)>())
                    .ToList();
                affected += await CopyAsync(context, table, pairs, columns, token);
            }
        }
        else
        {
            var table = ServiceEffectFields.Required(root, "targetTable");
            var field = ServiceEffectFields.Required(root, "field");
            var sourceField = ServiceEffectFields.Required(root, "sourceField");
            affected += await CopyAsync(context, table,
                new[] { (Target: field, Source: sourceField) }, columns, token);
        }
        return affected;
    }

    private static async Task<int> CopyAsync(
        ServiceEffectContext context,
        string table,
        IReadOnlyList<(string Target, string Source)> pairs,
        ISet<string> columns,
        CancellationToken token)
    {
        var plan = context.Plan;
        if (!columns.Contains(table))
            throw new EffectConfigException($"field-copy 目标表不存在：{table}。");
        var sets = new List<string>();
        foreach (var (target, source) in pairs)
        {
            if (!columns.Contains(table + "." + target))
                throw new EffectConfigException($"field-copy 目标列不存在：{table}.{target}。");
            if (!columns.Contains(plan.MasterTable + "." + source))
                throw new EffectConfigException($"field-copy 主表来源列不存在：{plan.MasterTable}.{source}。");
            sets.Add($"T.{ServiceEffectSql.Q(target)} = M.{ServiceEffectSql.Q(source)}");
        }
        if (sets.Count == 0)
            throw new EffectConfigException("field-copy 缺少可复制字段。");
        var sql = $"UPDATE T SET {string.Join(", ", sets)} FROM dbo.{ServiceEffectSql.Q(table)} T "
            + $"JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M ON {ServiceEffectSql.SameNameKeyJoin(plan, "T")}";
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, Array.Empty<EffectSqlParameter>(), token);
    }
}

internal static class ServiceEffectFields
{
    public static string[] Read(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return Array.Empty<string>();
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim())
                .ToArray();
        throw new EffectConfigException($"{name} 必须是字段名数组。");
    }

    public static string Required(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"缺少字符串字段 '{name}'。");
}
