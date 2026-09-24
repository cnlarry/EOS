using System.Text.RegularExpressions;
using EOS.API.Errors;
using EOS.API.Telemetry;
using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// 工作台数据范围统一构建器：
/// 把模块 FILTER、用户 DATA_FILTER、分组表达式、EXEC_TAG 落地为受控 SQL 谓词。
/// 规则边界：
/// - 列表/导出/详情/记录读取/子表/打印：模块 FILTER + DATA_FILTER + 分组 + EXEC_TAG 全范围一致
/// ；
/// - 子表：通过主表关联键把主表范围推导到明细（缺关联键禁止读取，fail-closed）。
/// 全部动态标识符来自服务端 Definition 白名单，值参数化；解析失败或范围无法确定时
/// 抛 DataFilterUnsupportedException/GroupExpressionUnsupportedException（403），不降级为全量查询。
/// </summary>
public sealed class WorkbenchScopeFilter(ApiMetrics metrics)
{
    private static readonly Regex ParameterName = new("@df(\\d+)", RegexOptions.Compiled);

    /// <summary>应用模块级行过滤（MODULES.FILTER，如 1206 成品资料 PRODUCT.PRO_TYPE=1）。</summary>
    public void ApplyModuleFilter(
        WorkbenchDefinition definition,
        ICollection<string> predicates,
        SqlCommand command)
    {
        if (string.IsNullOrWhiteSpace(definition.ModuleFilter))
        {
            return;
        }

        var allowedFields = ResolveFilterFieldKeys(definition);
        if (!DataFilterParser.TryParse(definition.ModuleFilter, definition.MasterTable, allowedFields,
                out var predicate, out var parameters))
        {
            metrics.IncrementScopeRejected("module_filter");
            throw new DataFilterUnsupportedException("该模块的数据过滤条件尚不支持，已拒绝查询。");
        }
        predicates.Add(predicate);
        AddParameters(command, predicate, parameters);
    }

    /// <summary>
    /// 应用用户数据范围（SYSDD/SYSDH 权限 DATA_FILTER；个人覆盖组、组 OR 已由
    /// RightsAdminRepository 组合为生效值）。与模块 FILTER 同边界：白名单主表字段 +
    /// 受限解析 + 参数化；参数名重命名避免与模块 FILTER 的 @dfN 冲突。
    /// </summary>
    public void ApplyUserDataFilter(
        WorkbenchDefinition definition,
        string? dataFilter,
        ICollection<string> predicates,
        SqlCommand command)
    {
        if (string.IsNullOrWhiteSpace(dataFilter))
        {
            return;
        }

        var allowedFields = ResolveFilterFieldKeys(definition);
        if (!DataFilterParser.TryParse(dataFilter, definition.MasterTable, allowedFields,
                out var predicate, out var parameters))
        {
            metrics.IncrementScopeRejected("data_filter");
            throw new DataFilterUnsupportedException("当前用户的权限数据范围尚不支持，已拒绝查询。");
        }
        var prefix = $"@udf{predicates.Count}_";
        var renamed = ParameterName.Replace(predicate, match => prefix + int.Parse(match.Groups[1].Value));
        for (var i = 0; i < parameters.Count; i++)
        {
            command.Parameters.AddWithValue(prefix + i, parameters[i]);
        }
        predicates.Add(renamed);
    }

    /// <summary>应用菜单分组筛选（GROUP_EXP&lt;groupIndex&gt; = groupValue）。</summary>
    public void ApplyGroupFilter(
        WorkbenchDefinition definition,
        int? groupIndex,
        string? groupValue,
        ICollection<string> predicates,
        SqlCommand command)
    {
        if (groupIndex is null || string.IsNullOrWhiteSpace(groupValue))
        {
            return;
        }
        if (groupIndex is < 1 or > 5)
        {
            metrics.IncrementScopeRejected("group_index");
            throw new GroupExpressionUnsupportedException("分组序号无效，已拒绝查询。");
        }

        var expression = definition.GroupExpressions[groupIndex.Value - 1];
        if (string.IsNullOrWhiteSpace(expression))
        {
            metrics.IncrementScopeRejected("group_expression");
            throw new GroupExpressionUnsupportedException("该模块未启用此分组表达式，已拒绝查询。");
        }

        var allowedFields = ResolveFilterFieldKeys(definition);
        if (!GroupExpressionParser.TryCompile(expression, definition.MasterTable, allowedFields, out var compiled))
        {
            metrics.IncrementScopeRejected("group_expression");
            throw new GroupExpressionUnsupportedException("分组表达式超出受控子集，已拒绝查询。");
        }
        var parameterName = $"@gf{predicates.Count}";
        predicates.Add($"({compiled})={parameterName}");
        command.Parameters.AddWithValue(parameterName, groupValue);
    }

    /// <summary>
    /// 执行范围（EXEC_TAG）行级过滤：谓词口径与拒绝条件统一由 <see cref="TryResolveExecTag"/> 决定。
    /// 取值不受支持或表缺 OWNER/OWNER_G 列时抛 403（不降级为全量查询）；值全部参数化。
    /// </summary>
    public void ApplyExecTagScope(
        WorkbenchDefinition definition,
        ICollection<string> predicates,
        SqlCommand command)
    {
        const string ownerParameter = "@execOwner";
        if (!TryResolveExecTag(definition.ExecTag, definition.HasOwnerColumn, definition.HasOwnerGroupColumn,
                out var template, out var rejectMetric, out var rejectMessage))
        {
            metrics.IncrementScopeRejected(rejectMetric);
            throw new DataFilterUnsupportedException(rejectMessage);
        }
        if (template.Length == 0)
        {
            return;
        }
        predicates.Add(RenderExecTagPredicate(template, ownerParameter, null));
        command.Parameters.AddWithValue(ownerParameter, definition.UserId);
    }

    /// <summary>列表/导出统一入口：模块 FILTER + DATA_FILTER + 分组 + EXEC_TAG。</summary>
    public void ApplyScope(
        WorkbenchDefinition definition,
        string? dataFilter,
        int? groupIndex,
        string? groupValue,
        ICollection<string> predicates,
        SqlCommand command)
    {
        ApplyModuleFilter(definition, predicates, command);
        ApplyUserDataFilter(definition, dataFilter, predicates, command);
        ApplyGroupFilter(definition, groupIndex, groupValue, predicates, command);
        ApplyExecTagScope(definition, predicates, command);
    }

    /// <summary>
    /// 子表范围：通过主表关联键把主表范围（DATA_FILTER + EXEC_TAG）推导到明细。
    /// 明细查询必须已携带主表关联键（predicates 非空），否则调用方应已拒绝；
    /// 范围无法解析时抛 403，不降级为全量查询。
    /// </summary>
    public void ApplyDetailScope(
        WorkbenchDefinition definition,
        string? dataFilter,
        string detailTable,
        IReadOnlyList<string> keyColumns,
        ICollection<string> predicates,
        SqlCommand command)
    {
        if (keyColumns.Count == 0)
        {
            metrics.IncrementScopeRejected("detail_master_key_missing");
            throw new DataFilterUnsupportedException("子表查询缺少有效主表关联键，已拒绝查询。");
        }

        var scopePredicates = new List<string>();
        ApplyModuleFilter(definition, scopePredicates, command);
        ApplyUserDataFilter(definition, dataFilter, scopePredicates, command);
        ApplyExecTagScope(definition, scopePredicates, command);
        if (scopePredicates.Count == 0)
        {
            return;
        }

        var keyJoin = string.Join(" AND ", keyColumns.Select(pk => $"[{pk}]=dbo.[{detailTable}].[{pk}]"));
        predicates.Add(
            $"EXISTS (SELECT 1 FROM dbo.[{definition.MasterTable}] WITH (NOLOCK) " +
            $"WHERE {keyJoin} AND ({string.Join(" AND ", scopePredicates)}))");
    }

    /// <summary>
    /// 记录/打印范围谓词（DATA_FILTER + EXEC_TAG）：参数统一按 @dfN 命名，
    /// 供 RecordInScopeAsync 及打印主表读取复用；解析失败返回 false（调用方必须拒绝）。
    /// </summary>
    public bool TryBuildRecordScopePredicate(
        WorkbenchDefinition definition,
        string? dataFilter,
        out string predicate,
        out IReadOnlyList<object> parameters)
    {
        var values = new List<object>();
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(definition.ModuleFilter))
        {
            var allowedFields = ResolveFilterFieldKeys(definition);
            if (!DataFilterParser.TryParse(definition.ModuleFilter, definition.MasterTable, allowedFields,
                    out var parsed, out var parsedParameters))
            {
                metrics.IncrementScopeRejected("module_filter");
                predicate = string.Empty;
                parameters = [];
                return false;
            }
            parts.Add(Renumber(parsed, values, parsedParameters));
        }

        if (!string.IsNullOrWhiteSpace(dataFilter))
        {
            var allowedFields = ResolveFilterFieldKeys(definition);
            if (!DataFilterParser.TryParse(dataFilter, definition.MasterTable, allowedFields,
                    out var parsed, out var parsedParameters))
            {
                metrics.IncrementScopeRejected("data_filter");
                predicate = string.Empty;
                parameters = [];
                return false;
            }
            parts.Add(Renumber(parsed, values, parsedParameters));
        }

        if (!TryAppendExecTag(definition, parts, values, out predicate))
        {
            predicate = string.Empty;
            parameters = [];
            return false;
        }

        predicate = parts.Count == 0 ? string.Empty : string.Join(" AND ", parts);
        parameters = values;
        return true;
    }

    private bool TryAppendExecTag(
        WorkbenchDefinition definition,
        ICollection<string> parts,
        List<object> values,
        out string error)
        => TryAppendExecTagCore(definition.ExecTag, definition.UserId, definition.HasOwnerColumn,
            definition.HasOwnerGroupColumn, parts, values, out error);

    /// <summary>
    /// 把执行范围谓词追加到参数列表形态（@dfN 槽位，与 DataFilterParser 输出同族）。
    /// 取值不受支持或表缺所需列时返回 false：调用方必须据此拒绝，不得降级为全量查询。
    /// </summary>
    private bool TryAppendExecTagCore(
        string? execTag,
        string userId,
        bool hasOwnerColumn,
        bool hasOwnerGroupColumn,
        ICollection<string> parts,
        List<object> values,
        out string error,
        // 选择器可能带跨表 JOIN：两表都有 OWNER/OWNER_G 时不限定表名即"列名不明确"报 500。
        // 列表/详情是单表查询，传 null 保持既有 SQL 形状不变。
        string? columnQualifier = null)
    {
        error = string.Empty;
        if (!TryResolveExecTag(execTag, hasOwnerColumn, hasOwnerGroupColumn,
                out var template, out var rejectMetric, out var rejectMessage))
        {
            metrics.IncrementScopeRejected(rejectMetric);
            error = rejectMessage;
            return false;
        }
        if (template.Length == 0)
        {
            return true;
        }
        var parameterName = $"@df{values.Count}";
        parts.Add(RenderExecTagPredicate(template, parameterName, columnQualifier));
        values.Add(userId);
        return true;
    }

    /// <summary>
    /// 表单选择器数据范围：模块 FILTER（仅源表=模块主表时）+
    /// DATA_FILTER + EXEC_TAG，与列表/详情/打印同一口径；解析失败返回 false（调用方返回空选项）。
    /// </summary>
    public bool TryBuildChooserScopePredicate(
        string sourceTable,
        string? moduleFilter,
        string? moduleMasterTable,
        string? dataFilter,
        string? execTag,
        string userId,
        bool hasOwnerColumn,
        bool hasOwnerGroupColumn,
        IReadOnlySet<string> allowedFields,
        out string predicate,
        out IReadOnlyList<object> parameters)
    {
        var values = new List<object>();
        var parts = new List<string>();

        // 选择器的 FROM 可能带跨表 JOIN（filterStruct 引用了白名单外键表），此时**源表列必须带
        // 表名前缀**，否则两表同名列（OWNER / IS_SHOW 之类）会以"列名不明确"报 500。
        // 解析器按 `ForeignTables is null ? 裸列 : 限定列` 区分两种形态：这里传一个**空但非 null**
        // 的白名单（等于"不引入任何外键表"），即可拿到限定形态，且外键引用照旧被拒 —— 与列表/详情
        // 的单表查询（仍走裸列）语义一致，只是加了前缀。
        IReadOnlyDictionary<string, string> noForeignTables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(moduleFilter)
            && string.Equals(moduleMasterTable, sourceTable, StringComparison.OrdinalIgnoreCase))
        {
            if (!DataFilterParser.TryParseWithJoins(moduleFilter, sourceTable, allowedFields, noForeignTables, null,
                    out var parsed, out var parsedParameters, out _, out _))
            {
                metrics.IncrementScopeRejected("chooser_module_filter");
                predicate = string.Empty;
                parameters = [];
                return false;
            }
            parts.Add(Renumber(parsed, values, parsedParameters));
        }

        if (!string.IsNullOrWhiteSpace(dataFilter))
        {
            if (!DataFilterParser.TryParseWithJoins(dataFilter, sourceTable, allowedFields, noForeignTables, null,
                    out var parsed, out var parsedParameters, out _, out _))
            {
                metrics.IncrementScopeRejected("chooser_data_filter");
                predicate = string.Empty;
                parameters = [];
                return false;
            }
            parts.Add(Renumber(parsed, values, parsedParameters));
        }

        if (!TryAppendExecTagCore(execTag, userId, hasOwnerColumn, hasOwnerGroupColumn, parts, values, out _,
                columnQualifier: sourceTable))
        {
            predicate = string.Empty;
            parameters = [];
            return false;
        }

        predicate = parts.Count == 0 ? string.Empty : string.Join(" AND ", parts);
        parameters = values;
        return true;
    }

    /// <summary>
    /// 执行范围（EXEC_TAG）的唯一策略源：B 仅本人、C 本人及下级（含 f_get_underling）、
    /// D 本人所属组、E 本人及下级所属组；Z/A/空 无附加范围。
    /// 谓词模板以 {owner}/{group}/{p} 占位，由调用方按各自形态渲染（命令参数名或 @dfN 槽位），
    /// 使列表/详情/打印/选择器多条查询路径共用同一口径。
    /// 取值不受支持或缺少所需列时返回 false 并给出拒绝指标与提示；未知取值一律拒绝，
    /// 不得静默放行，否则同一模块在不同入口的可见性会不一致。
    /// </summary>
    private static bool TryResolveExecTag(
        string? execTag,
        bool hasOwnerColumn,
        bool hasOwnerGroupColumn,
        out string template,
        out string rejectMetric,
        out string rejectMessage)
    {
        template = string.Empty;
        rejectMetric = string.Empty;
        rejectMessage = string.Empty;
        var tag = (execTag ?? "Z").Trim().ToUpperInvariant();
        switch (tag)
        {
            case "Z" or "A" or "":
                return true;
            case "B":
            case "C":
                if (!hasOwnerColumn)
                {
                    rejectMetric = "exec_tag_owner_missing";
                    rejectMessage = "该模块不支持按执行范围过滤。";
                    return false;
                }
                template = tag == "B"
                    ? "{owner}={p}"
                    : "({owner}={p} OR {owner} IN (SELECT USER_ID FROM dbo.f_get_underling({p})))";
                return true;
            case "D":
            case "E":
                if (!hasOwnerGroupColumn)
                {
                    rejectMetric = "exec_tag_owner_group_missing";
                    rejectMessage = "该模块不支持按执行范围过滤。";
                    return false;
                }
                template = tag == "D"
                    ? "{group} IN (SELECT G_IDX FROM dbo.SYSDG_USER WITH (NOLOCK) WHERE USER_ID={p})"
                    : "{group} IN (SELECT G_IDX FROM dbo.SYSDG_USER WITH (NOLOCK) WHERE USER_ID IN (SELECT {p} UNION ALL SELECT USER_ID FROM dbo.f_get_underling({p})))";
                return true;
            default:
                rejectMetric = "exec_tag_unknown";
                rejectMessage = "不支持的执行范围。";
                return false;
        }
    }

    /// <summary>渲染执行范围谓词：{owner}/{group} 按是否限定表名展开（跨表 JOIN 需限定），{p} 替换为参数名。</summary>
    private static string RenderExecTagPredicate(string template, string parameterName, string? columnQualifier)
    {
        var owner = columnQualifier is null ? "[OWNER]" : $"[{columnQualifier}].[OWNER]";
        var group = columnQualifier is null ? "[OWNER_G]" : $"[{columnQualifier}].[OWNER_G]";
        return template
            .Replace("{owner}", owner)
            .Replace("{group}", group)
            .Replace("{p}", parameterName);
    }

    /// <summary>把解析器输出的 @dfN 重编号为累计参数槽位，保证与 RecordInScopeAsync 的 @dfN 注入一致。</summary>
    private static string Renumber(string predicate, List<object> values, IReadOnlyList<object> incoming)
    {
        var offset = values.Count;
        foreach (var value in incoming)
        {
            values.Add(value);
        }
        return ParameterName.Replace(predicate, match => $"@df{offset + int.Parse(match.Groups[1].Value)}");
    }

    private static void AddParameters(SqlCommand command, string predicate, IReadOnlyList<object> parameters)
    {
        var names = ParameterName.Matches(predicate)
            .Select(match => match.Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => int.Parse(name[3..]))
            .ToList();
        for (var i = 0; i < names.Count; i++)
        {
            command.Parameters.AddWithValue(names[i], parameters[i]);
        }
    }

    private static IReadOnlySet<string> ResolveFilterFieldKeys(WorkbenchDefinition definition)
    {
        if (definition.FilterFieldKeys is { Count: > 0 })
        {
            return definition.FilterFieldKeys;
        }
        return definition.MasterFields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
