using System.Text;
using System.Text.Json;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// link-stamp: writes link reference numbers / states of the approved document onto a
/// related document. Three closed shapes:
/// Shape A {targetTable, field|fields, mode}: same-named master columns copied onto the
/// target rows, joined on same-named master key columns.
/// Shape B {targets:[{table, ref:[cols]}], fields, finish}: ref names the target-side
/// columns tying back to the master key; finish=true additionally sets FINISHED_TAG=1
/// on the target (standard lifecycle column; revisited if evidence contradicts).
/// Shape C {targets:[{table, refs:[target cols], fromDetail:true, sourceRefs:[detail cols]}],
/// fields}: a detail-to-detail link — the target rows are located by the reference
/// columns carried by the document detail, and the stamped values are read from that
/// detail row. A fields entry is either a column name (same name on both sides) or a
/// {target, source} object when the detail column is named differently.
/// Every statement is scoped to the current document by its master key values and every
/// identifier is checked against the physical column whitelist (fail-closed).
/// Deapprove honours reverse.kind: clear-refs empties the stamped columns (SERIAL-like
/// columns are reset to 0), no-reverse does nothing, anything else is rejected.
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
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var spec = LinkStampSpec.Parse(root, plan, columns);

        if (context.ExecutionEvent is EffectEvent.ApproveEffect or EffectEvent.Save)
            return await StampAsync(context, spec, token);

        var kind = RequireReverseKind(context.Action.Reverse);
        return kind is "no-reverse" or "none" ? 0 : await ClearAsync(context, spec, kind == "clear-refs-unfinish", token);
    }

    private static async Task<int> StampAsync(ServiceEffectContext context, LinkStampSpec spec, CancellationToken token)
    {
        var affected = 0;
        foreach (var target in spec.Targets)
        {
            var parameters = new List<EffectSqlParameter>();
            var sql = BuildUpdate(
                context.Plan, target, context.MasterKeyValues, StampAssignments(target), parameters);
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
        }
        return affected;
    }

    /// <summary>
    /// Clears the stamped columns on the very rows the approval stamped, located through
    /// the same reference keys — the link is removed, no other column is touched.
    /// </summary>
    private static async Task<int> ClearAsync(ServiceEffectContext context, LinkStampSpec spec, bool unfinish, CancellationToken token)
    {
        var affected = 0;
        foreach (var target in spec.Targets)
        {
            var parameters = new List<EffectSqlParameter>();
            var assignments = ClearAssignments(target).ToList();
            if (unfinish)
            {
                assignments.Add("T.[FINISHED_TAG] = 0");
                assignments.Add("T.[FINISHED_PERSON] = 'SYSTEM'");
                assignments.Add("T.[FINISHED_DATE] = SYSDATETIME()");
            }
            var sql = BuildUpdate(
                context.Plan, target, context.MasterKeyValues, assignments, parameters);
            affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
        }
        return affected;
    }

    /// <summary>T.&lt;column&gt; = &lt;source&gt;.&lt;column&gt;, plus FINISHED_TAG=1 when finish is set.</summary>
    internal static IReadOnlyList<string> StampAssignments(LinkStampTarget target)
    {
        var alias = target.FromDetail ? "D" : "M";
        var assignments = target.Fields
            .Select(field => $"T.{ServiceEffectSql.Q(field.Target)} = {alias}.{ServiceEffectSql.Q(field.Source)}")
            .ToList();
        if (target.Finish)
        {
            assignments.Add("T.[FINISHED_TAG] = 1");
            assignments.Add("T.[FINISHED_PERSON] = 'SYSTEM'");
            assignments.Add("T.[FINISHED_DATE] = SYSDATETIME()");
        }
        return assignments;
    }

    /// <summary>Reference columns are emptied; SERIAL-like columns are numeric and reset to 0.</summary>
    internal static IReadOnlyList<string> ClearAssignments(LinkStampTarget target) =>
        target.Fields
            .Select(field => $"T.{ServiceEffectSql.Q(field.Target)} = {(IsSerialColumn(field.Target) ? "0" : "''")}")
            .ToList();

    /// <summary>
    /// UPDATE T SET &lt;assignments&gt; FROM target T [JOIN detail D ON refs = sourceRefs]
    /// JOIN master M ON … WHERE the master key values. The document scope is mandatory:
    /// a link-stamp never touches rows belonging to another document.
    /// </summary>
    internal static string BuildUpdate(
        ModuleEffectPlan plan,
        LinkStampTarget target,
        IReadOnlyList<string> masterKeyValues,
        IReadOnlyList<string> assignments,
        List<EffectSqlParameter> parameters)
    {
        if (assignments.Count == 0)
            throw new EffectConfigException("link-stamp 缺少可回写字段。");
        var from = new StringBuilder("dbo.").Append(ServiceEffectSql.Q(target.Table)).Append(" T");
        if (target.FromDetail)
        {
            from.Append(" JOIN dbo.").Append(ServiceEffectSql.Q(plan.DetailTable!)).Append(" D ON ")
                .Append(string.Join(" AND ", target.Refs.Zip(target.SourceRefs,
                    (reference, source) => $"T.{ServiceEffectSql.Q(reference)} = D.{ServiceEffectSql.Q(source)}")))
                .Append(" JOIN dbo.").Append(ServiceEffectSql.Q(plan.MasterTable!)).Append(" M ON ")
                .Append(ServiceEffectSql.SameNameKeyJoin(plan, "D"));
        }
        else
        {
            from.Append(" JOIN dbo.").Append(ServiceEffectSql.Q(plan.MasterTable!)).Append(" M ON ")
                .Append(MasterJoin(plan, target));
        }
        return $"UPDATE T SET {string.Join(", ", assignments)} FROM {from} "
            + $"WHERE {ServiceEffectSql.MasterKeyFilter(plan, masterKeyValues, parameters)}";
    }

    private static string MasterJoin(ModuleEffectPlan plan, LinkStampTarget target) =>
        target.Refs.Count > 0
            ? string.Join(" AND ", target.Refs.Zip(plan.MasterPkOrder)
                .Select(pair => $"T.{ServiceEffectSql.Q(pair.First)} = M.{ServiceEffectSql.Q(pair.Second)}"))
            : ServiceEffectSql.SameNameKeyJoin(plan, "T");

    private static bool IsSerialColumn(string column) =>
        column.Contains("SERIAL", StringComparison.OrdinalIgnoreCase);

    internal static string RequireReverseKind(JsonElement? reverse)
    {
        var kind = reverse is { } element
            && element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("kind", out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        return kind switch
        {
            "clear-refs" or "clear-refs-unfinish" or "no-reverse" or "none" => kind,
            null => throw new EffectConfigException("link-stamp 解批缺少 reverse.kind，禁止无守卫执行。"),
            _ => throw new EffectConfigException($"link-stamp 解批 reverse.kind '{kind}' 不受支持。"),
        };
    }
}

/// <summary>One stamped column: the target column and the source column feeding it.</summary>
internal sealed record LinkStampField(string Target, string Source);

/// <summary>
/// One resolved stamp target: target table, its locating columns, whether the values and
/// the locating values come from the document detail, and the resolved field pairs.
/// </summary>
internal sealed record LinkStampTarget(
    string Table,
    IReadOnlyList<string> Refs,
    bool FromDetail,
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<LinkStampField> Fields,
    bool Finish);

/// <summary>Parsed link-stamp parameters (all shapes) validated against physical columns.</summary>
internal sealed record LinkStampSpec(IReadOnlyList<LinkStampTarget> Targets)
{
    public static LinkStampSpec Parse(JsonElement root, ModuleEffectPlan plan, ISet<string> columns)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new EffectConfigException("link-stamp 参数必须是 JSON 对象。");
        var finish = root.TryGetProperty("finish", out var flag) && flag.ValueKind == JsonValueKind.True;
        var declared = ReadFields(root, "field") is { Length: > 0 } single
            ? single
            : ReadFields(root, "fields");
        if (declared.Length == 0)
            throw new EffectConfigException("link-stamp 缺少可回写字段。");

        var targets = new List<LinkStampTarget>();
        if (root.TryGetProperty("targets", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    throw new EffectConfigException("link-stamp targets 项必须是对象。");
                var refs = ReadNames(item, "refs") is { Length: > 0 } plural
                    ? plural
                    : ReadNames(item, "ref");
                targets.Add(Resolve(
                    Required(item, "table"), refs,
                    item.TryGetProperty("fromDetail", out var detail) && detail.ValueKind == JsonValueKind.True,
                    ReadNames(item, "sourceRefs"),
                    declared, plan, columns, finish));
            }
        }
        else
        {
            targets.Add(Resolve(
                Required(root, "targetTable"), Array.Empty<string>(), false, Array.Empty<string>(),
                declared, plan, columns, finish));
        }
        if (targets.Count == 0)
            throw new EffectConfigException("link-stamp 未配置任何目标表。");
        return new LinkStampSpec(targets);
    }

    private static LinkStampTarget Resolve(
        string table,
        IReadOnlyList<string> refs,
        bool fromDetail,
        IReadOnlyList<string> sourceRefs,
        IReadOnlyList<LinkStampField> declared,
        ModuleEffectPlan plan,
        ISet<string> columns,
        bool finish)
    {
        var sourceTable = fromDetail
            ? plan.DetailTable ?? throw new EffectConfigException("link-stamp fromDetail=true 需要模块明细表（模块形态不足）。")
            : plan.MasterTable!;
        if (!HasColumns(columns, table))
            throw new EffectConfigException($"link-stamp 目标表不存在：{table}。");
        if (!HasColumns(columns, sourceTable))
            throw new EffectConfigException($"link-stamp 来源表不存在：{sourceTable}。");

        if (fromDetail)
        {
            if (refs.Count == 0 || sourceRefs.Count == 0)
                throw new EffectConfigException($"link-stamp 目标 {table} fromDetail=true 需要 refs 与 sourceRefs。");
            if (refs.Count != sourceRefs.Count)
                throw new EffectConfigException(
                    $"link-stamp 目标 {table} refs 列数({refs.Count})与 sourceRefs 列数({sourceRefs.Count})不一致。");
            foreach (var key in plan.MasterPkOrder)
                if (!columns.Contains(sourceTable + "." + key))
                    throw new EffectConfigException(
                        $"link-stamp 明细表 {sourceTable} 缺少主表主键列 {key}，无法限定单据范围。");
        }
        else if (refs.Count > 0 && refs.Count != plan.MasterPkOrder.Count)
        {
            throw new EffectConfigException($"link-stamp ref 列数({refs.Count})与主表主键数({plan.MasterPkOrder.Count})不一致。");
        }
        foreach (var reference in refs)
            if (!columns.Contains(table + "." + reference))
                throw new EffectConfigException($"link-stamp 目标定位列不存在：{table}.{reference}。");
        foreach (var source in sourceRefs)
            if (!columns.Contains(sourceTable + "." + source))
                throw new EffectConfigException($"link-stamp 来源定位列不存在：{sourceTable}.{source}。");

        var fields = new List<LinkStampField>();
        foreach (var field in declared)
        {
            if (!columns.Contains(table + "." + field.Target))
                throw new EffectConfigException($"link-stamp 目标列不存在：{table}.{field.Target}。");
            if (!columns.Contains(sourceTable + "." + field.Source))
                throw new EffectConfigException($"link-stamp 来源列不存在：{sourceTable}.{field.Source}。");
            fields.Add(field);
        }
        if (finish && !columns.Contains(table + ".FINISHED_TAG"))
            throw new EffectConfigException($"link-stamp finish=true 但目标表 {table} 无 FINISHED_TAG 列。");
        return new LinkStampTarget(table, refs, fromDetail, sourceRefs, fields, finish);
    }

    private static LinkStampField[] ReadFields(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return Array.Empty<LinkStampField>();
        if (value.ValueKind == JsonValueKind.String)
            return new[] { SameName(value.GetString()!.Trim()) };
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Select(item => item.ValueKind switch
                {
                    JsonValueKind.String => SameName(item.GetString()!.Trim()),
                    JsonValueKind.Object => new LinkStampField(ObjectName(item, "target"), ObjectName(item, "source")),
                    _ => throw new EffectConfigException($"link-stamp {name} 项必须是字段名或 {{target,source}} 对象。"),
                })
                .ToArray();
        throw new EffectConfigException($"link-stamp {name} 必须是字段名或字段名数组。");
    }

    private static string[] ReadNames(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return Array.Empty<string>();
        if (value.ValueKind == JsonValueKind.String)
            return new[] { value.GetString()!.Trim() };
        if (value.ValueKind == JsonValueKind.Array)
            return value.EnumerateArray()
                .Select(item => item.ValueKind == JsonValueKind.String
                    ? item.GetString()!.Trim()
                    : throw new EffectConfigException($"link-stamp {name} 必须是字段名数组。"))
                .ToArray();
        throw new EffectConfigException($"link-stamp {name} 必须是字段名或字段名数组。");
    }

    /// <summary>
    /// The physical column whitelist carries TABLE.COLUMN entries only, so a table is
    /// present when at least one of its columns is; a table with no column entry can
    /// never be stamped and is rejected.
    /// </summary>
    private static bool HasColumns(ISet<string> columns, string table) =>
        columns.Any(entry => entry.StartsWith(table + ".", StringComparison.OrdinalIgnoreCase));

    private static LinkStampField SameName(string column) => new(column, column);

    private static string ObjectName(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : throw new EffectConfigException($"link-stamp 字段项缺少字符串 '{name}'。");

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
        // The copy is approve-direction only. The reverse structure states the
        // deapprove semantics explicitly: none/no-reverse means a no-op; anything
        // else is refused as an unguarded reverse write.
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save))
        {
            var kind = ReverseKind(context);
            if (kind is "none" or "no-reverse")
            {
                return 0;
            }
            throw new EffectConfigException("field-copy 解批缺少 reverse.kind（none/no-reverse），禁止无守卫执行。");
        }
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);

        var affected = 0;
        if (root.TryGetProperty("targets", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            var fields = ServiceEffectFields.Read(root, "fields");
            var headerFields = ServiceEffectFields.Read(root, "headerFields");
            foreach (var target in targets.EnumerateArray())
            {
                // Shape B/C: a plain table name keeps the legacy same-name key join;
                // an object adds explicit refs [{target, source}] locating the target
                // row from the master columns (e.g. the referenced original document
                // keys on a change master) and may narrow the copied fields per target.
                var (table, refs, perTargetFields) = target.ValueKind == JsonValueKind.String
                    ? (target.GetString()!.Trim(), Array.Empty<(string Target, string Source)>(), fields)
                    : ParseTargetObject(target);
                var isMasterShape = table.EndsWith("_M", StringComparison.OrdinalIgnoreCase);
                var pairs = perTargetFields.Select(field => (Target: field, Source: field))
                    .Concat(isMasterShape ? headerFields.Select(field => (Target: field, Source: field)) : Array.Empty<(string, string)>())
                    .ToList();
                affected += await CopyAsync(context, table, pairs, refs, columns, token);
            }
        }
        else
        {
            var table = ServiceEffectFields.Required(root, "targetTable");
            var field = ServiceEffectFields.Required(root, "field");
            var sourceField = ServiceEffectFields.Required(root, "sourceField");
            affected += await CopyAsync(context, table,
                new[] { (Target: field, Source: sourceField) }, Array.Empty<(string, string)>(), columns, token);
        }
        return affected;
    }

    private static (string Table, IReadOnlyList<(string Target, string Source)> Refs, string[] Fields) ParseTargetObject(JsonElement target)
    {
        var table = ServiceEffectFields.Required(target, "table");
        var fields = target.TryGetProperty("fields", out var fieldsValue) && fieldsValue.ValueKind == JsonValueKind.Array
            ? fieldsValue.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!.Trim()).ToArray()
            : Array.Empty<string>();
        if (fields.Length == 0)
            throw new EffectConfigException("field-copy targets 对象缺少非空 fields。");
        var refs = new List<(string Target, string Source)>();
        if (target.TryGetProperty("refs", out var refsValue) && refsValue.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in refsValue.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("target", out var t) || t.ValueKind != JsonValueKind.String
                    || !item.TryGetProperty("source", out var s) || s.ValueKind != JsonValueKind.String)
                    throw new EffectConfigException("field-copy refs 项必须是 {target, source}。");
                refs.Add((t.GetString()!.Trim(), s.GetString()!.Trim()));
            }
        }
        if (refs.Count == 0)
            throw new EffectConfigException("field-copy targets 对象缺少非空 refs。");
        return (table, refs, fields);
    }

    private static async Task<int> CopyAsync(
        ServiceEffectContext context,
        string table,
        IReadOnlyList<(string Target, string Source)> pairs,
        IReadOnlyList<(string Target, string Source)> refs,
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
        var join = refs.Count > 0
            ? string.Join(" AND ", refs.Select(reference =>
                $"T.{ServiceEffectSql.Q(reference.Target)} = M.{ServiceEffectSql.Q(reference.Source)}"))
            : ServiceEffectSql.SameNameKeyJoin(plan, "T");
        foreach (var reference in refs)
        {
            if (!columns.Contains(table + "." + reference.Target))
                throw new EffectConfigException($"field-copy 定位列不存在：{table}.{reference.Target}。");
            if (!columns.Contains(plan.MasterTable + "." + reference.Source))
                throw new EffectConfigException($"field-copy 主表定位列不存在：{plan.MasterTable}.{reference.Source}。");
        }
        var parameters = new List<EffectSqlParameter>();
        var sql = BuildCopyUpdate(plan, table, pairs, refs, context.MasterKeyValues, parameters);
        return await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sql, parameters, token);
    }

    private static string? ReverseKind(ServiceEffectContext context)
    {
        if (context.Action.Reverse is not { } reverse || reverse.ValueKind != JsonValueKind.Object
            || !reverse.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
            return null;
        return kind.GetString();
    }

    internal static string BuildCopyUpdate(
        ModuleEffectPlan plan,
        string table,
        IReadOnlyList<(string Target, string Source)> pairs,
        IReadOnlyList<(string Target, string Source)> refs,
        IReadOnlyList<string> masterKeyValues,
        List<EffectSqlParameter> parameters)
    {
        var join = refs.Count > 0
            ? string.Join(" AND ", refs.Select(reference =>
                $"T.{ServiceEffectSql.Q(reference.Target)} = M.{ServiceEffectSql.Q(reference.Source)}"))
            : ServiceEffectSql.SameNameKeyJoin(plan, "T");
        var sets = string.Join(", ", pairs.Select(pair =>
            $"T.{ServiceEffectSql.Q(pair.Target)} = M.{ServiceEffectSql.Q(pair.Source)}"));
        var where = ServiceEffectSql.MasterKeyFilter(plan, masterKeyValues, parameters);
        return $"UPDATE T SET {sets} FROM dbo.{ServiceEffectSql.Q(table)} T "
            + $"JOIN dbo.{ServiceEffectSql.Q(plan.MasterTable!)} M ON {join} WHERE {where}";
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
