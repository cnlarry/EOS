using System.Globalization;
using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// list_reports：列出某个模块下**当前用户可见**的报表（只读，不取数）。
///
/// <para>
/// 可见性不另写一套判定：直接用打印设置仓储的同一份清单——它已按**归属模块的 REPORT_TAG**
/// 过滤（个人 <c>SYSDD</c> 优先，没有个人记录时取组 <c>SYSDH</c> 的或）。报表可见性只有这一处真源，
/// 助手侧复制一份就会漂移。
/// </para>
///
/// <para>
/// "哪个模块"优先用模型给的 <c>module_id</c>，没给就用**用户当前所在页面**（处境注入，不经模型转述）——
/// 用户问"这个模块有哪些报表"时不必先报模块号。
/// </para>
/// </summary>
public sealed class ListReportsTool(
    IPermissionService permissions,
    IReportGateway reports,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase, IPageContextTool
{
    public const string ToolName = "list_reports";

    private PageContext? _page;

    /// <inheritdoc />
    public void UsePageContext(PageContext page) => _page = page;

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "列出某个模块下当前用户可见的报表清单（报表编号 / 名称 / 是否默认）。"
        + "只列清单不取数；要报表数据请用 run_report 并带上这里的报表编号。"
        + "省略 module_id 时用用户当前所在页面；也可先用 list_modules 查模块 ID。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID；省略则用用户当前所在页面的模块" }
          }
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var moduleId = arguments.GetIntArg("module_id");
        if (moduleId <= 0) moduleId = _page?.ModuleId ?? 0;
        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("请给出模块 ID（或先进入该模块的页面）后再列出报表。");
        }

        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (!permission.CanBrowse) return this.DenyBrowse($"#{moduleId}");

        var visible = await reports.ListReportsAsync(moduleId, userId, token);
        if (visible.Count == 0)
        {
            return ToolExecutionResult.Success($"模块 #{moduleId} 下当前用户可见的报表为空（可能没有配置报表，也可能报表可见性未授予）。");
        }

        var limit = Math.Max(1, Limits.ReportListMax);
        var shown = visible.Take(limit).ToList();
        var output = new StringBuilder();
        output.Append("模块 #").Append(moduleId).Append(" 可见报表 ").Append(visible.Count).Append(" 个");
        if (visible.Count > shown.Count) output.Append("（只列出前 ").Append(shown.Count).Append(" 个）");
        output.AppendLine("：");
        foreach (var report in shown)
        {
            output.Append("- ").Append(report.ReportName)
                .Append("（编号 ").Append(report.ReportId).Append('）');
            if (report.IsDefault) output.Append(" [默认]");
            output.AppendLine();
        }

        output.Append("取数请用 run_report，传入上面的报表编号。");
        return ToolExecutionResult.Success(output.ToString());
    }
}

/// <summary>
/// run_report：按报表编号取报表数据（只读）。
///
/// <para>
/// **归属模块只能由服务端解析**（<see cref="ReportRepository.FindIdentityAsync"/>）：报表编号全库唯一，
/// 若让调用方指定模块，任何人都能把一个报表编号挂到别的模块上用那个模块的权限打开它。
/// 解析不到一律按防探测口径回答"不存在"。
/// </para>
///
/// <para>
/// 取数走与报表页面完全同一条链路：<c>GetDefinitionAsync</c>（列与字段级权限：成本位 / 保密位 /
/// 禁止字段）→ <c>QueryAsync</c>（模块 FILTER + 用户 <c>DATA_FILTER</c>，条件参数化）。
/// **不下发版式**：只回列定义与行数据，打印/导出是另一条链路。
/// </para>
/// </summary>
public sealed class RunReportTool(
    IPermissionService permissions,
    IReportGateway reports,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase
{
    public const string ToolName = "run_report";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "按报表编号取报表数据（列与行，不含打印版式）。报表编号来自 list_reports。"
        + "不带条件即按默认条件运行；带条件时条件序号见输出里的「可填条件」。"
        + "金额与数量以本工具返回的数值为准，不要自行估算。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "report_id": { "type": "string", "description": "报表编号（来自 list_reports），全库唯一" },
            "conditions": {
              "type": "object",
              "description": "可选：条件序号 → 值。序号见输出里的「可填条件」；单值条件给字符串/数字，范围条件给 {\"from\":\"起\",\"to\":\"止\"}"
            },
            "page": { "type": "integer", "description": "页码，从 1 开始，默认 1" },
            "page_size": { "type": "integer", "description": "每页行数，默认取系统配置（服务端还会夹到 10..200）" }
          },
          "required": ["report_id"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var reportId = arguments.GetStringArg("report_id").Trim();
        if (reportId.Length == 0) return ToolExecutionResult.Deny("缺少报表编号（report_id）。");

        var identity = await reports.FindIdentityAsync(reportId, token);
        if (identity is null) return this.DenyNotFound();

        var permission = await permissions.GetAsync(userId, identity.ModuleId, token);
        if (!permission.CanBrowse) return this.DenyBrowse($"#{identity.ModuleId}");

        var definition = await reports.GetDefinitionAsync(
            identity.ModuleId, userId, permission.Rights, reportId, token);
        if (definition is null) return ToolExecutionResult.Deny("无法在当前用户权限范围内构建该报表，拒绝取数。");

        var parse = ParseConditions(arguments, definition);
        if (parse.Error is not null) return ToolExecutionResult.Deny(parse.Error);

        var page = Math.Max(1, arguments.GetIntArg("page"));
        var pageSize = arguments.GetIntArg("page_size");
        if (pageSize <= 0) pageSize = Limits.ReportMaxRows;

        var result = await reports.QueryAsync(
            definition, new ReportQueryRequest(parse.Values, parse.ValuesTo),
            page, pageSize, permission.Rights.DataFilter, token);

        return ToolExecutionResult.Success(Render(definition, identity, parse, result));
    }

    /// <summary>
    /// 条件值解析：序号必须是该报表**声明过的**序号——不校验的话，模型写错序号只会得到一份
    /// "看起来用了条件、其实是全量"的数据，那比报错危险得多。
    /// </summary>
    private static (Dictionary<int, string?> Values, Dictionary<int, string?> ValuesTo, string? Error)
        ParseConditions(JsonElement arguments, ReportDefinition definition)
    {
        var values = new Dictionary<int, string?>();
        var valuesTo = new Dictionary<int, string?>();
        if (!arguments.TryGetProperty("conditions", out var element) || element.ValueKind == JsonValueKind.Undefined)
        {
            return (values, valuesTo, null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return (values, valuesTo, "conditions 必须是对象（条件序号 → 值）。");
        }

        var declared = definition.Conditions.Select(condition => condition.SerialNo).ToHashSet();
        foreach (var property in element.EnumerateObject())
        {
            var raw = property.Name.Trim().TrimStart('#');
            if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var serial) || serial <= 0)
            {
                return (values, valuesTo, $"条件序号不合法：{property.Name}（应为数字序号，如 \"1\"）。");
            }

            if (!declared.Contains(serial))
            {
                return (values, valuesTo, $"条件序号 {serial} 不在该报表的条件里；可填条件见下方清单。");
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.String:
                    values[serial] = property.Value.GetString();
                    break;
                case JsonValueKind.Number:
                    values[serial] = property.Value.GetRawText();
                    break;
                case JsonValueKind.Object:
                {
                    if (!property.Value.TryGetProperty("from", out var from)
                        || !property.Value.TryGetProperty("to", out var to)
                        || from.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)
                        || to.ValueKind is not (JsonValueKind.Number or JsonValueKind.String))
                    {
                        return (values, valuesTo, $"条件 {serial} 是范围条件，必须同时给 from 与 to。");
                    }

                    values[serial] = from.ValueKind == JsonValueKind.Number ? from.GetRawText() : from.GetString();
                    valuesTo[serial] = to.ValueKind == JsonValueKind.Number ? to.GetRawText() : to.GetString();
                    break;
                }
                default:
                    return (values, valuesTo, $"条件 {serial} 的值必须是字符串、数字或 from/to 区间。");
            }
        }

        return (values, valuesTo, null);
    }

    private string Render(
        ReportDefinition definition, ReportIdentity identity,
        (Dictionary<int, string?> Values, Dictionary<int, string?> ValuesTo, string? Error) conditions,
        ReportQueryResult result)
    {
        var maxRows = Math.Max(1, Limits.ReportMaxRows);
        var maxColumns = Math.Max(1, Limits.ReportMaxColumns);
        var maxValue = Math.Max(1, Limits.ReportMaxValueLength);

        var output = new StringBuilder();
        output.Append("报表=").Append(identity.ReportName)
            .Append("（编号 ").Append(identity.ReportId)
            .Append("，归属模块 #").Append(identity.ModuleId)
            .Append(' ').Append(identity.ModuleName).Append('）')
            .Append(" 命中=").Append(result.Total)
            .Append(" 本页=").Append(result.Page).Append('/').Append(result.PageSize)
            .AppendLine();

        var columns = definition.Columns.Take(maxColumns).ToList();
        output.Append("列：").AppendLine(string.Join(" | ", columns.Select(column => column.Label)));
        if (definition.Columns.Count > columns.Count)
        {
            output.Append("（另有 ").Append(definition.Columns.Count - columns.Count).Append(" 列未列出）").AppendLine();
        }

        if (definition.Conditions.Count > 0)
        {
            output.Append("可填条件：").AppendLine(string.Join("；", definition.Conditions.Take(maxColumns).Select(condition =>
                $"#{condition.SerialNo} {condition.Desc}（{ConditionTypeLabel(condition.Type)}）")));
            if (conditions.Values.Count > 0)
            {
                output.Append("本次已用条件：").AppendLine(string.Join("；", conditions.Values.Select(pair =>
                    conditions.ValuesTo.TryGetValue(pair.Key, out var to)
                        ? $"#{pair.Key}={pair.Value}..{to}"
                        : $"#{pair.Key}={pair.Value}")));
            }
        }

        foreach (var row in result.Rows.Take(maxRows))
        {
            output.Append("- ");
            output.AppendLine(string.Join(" ", columns.Select(column =>
                $"{column.Label}={Truncate(FormatValue(column.Key, row), maxValue)}")));
        }

        if (result.Rows.Count > maxRows)
        {
            output.Append("（本页还有 ").Append(result.Rows.Count - maxRows).Append(" 行未列出）").AppendLine();
        }

        // 聚合报表的行级范围由报表自身的数据源决定：报表取数链路不接受模块 DATA_FILTER 的注入，
        // 这一点必须说出来——否则"看起来是没有数据范围过滤"会被当成越权或当成全量。
        if (definition.Aggregate is not null)
        {
            output.AppendLine("注：这是跨表汇总报表，行范围由报表自身的数据源决定，不叠加模块级数据范围。");
        }

        return output.ToString();
    }

    private static string ConditionTypeLabel(int type) => type switch
    {
        1 => "范围",
        2 => "单选",
        4 => "多选",
        _ => "条件",
    };

    private static string FormatValue(string key, IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue(key, out var value) || value is null || value is DBNull) return string.Empty;
        return value switch
        {
            bool flag => flag ? "1" : "0",
            decimal number => number.ToString("0.####", CultureInfo.InvariantCulture),
            double number => number.ToString("0.####", CultureInfo.InvariantCulture),
            DateTime date => date.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            DateTimeOffset offset => offset.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
