using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;

using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// get_record_detail：按主键值数组精确取一行完整详情。
/// 走「导出所选」同一路径（GetExportRowsByKeysAsync），列集合沿用列表口径
/// （个人列选择 / 默认列 + 成本/保密/禁止字段过滤），**但顺序按物理列序**：
/// 明细行每行只显示前 N 列，按个人列序排会把料号、数量挤出展示面。
/// DATA_FILTER/EXEC_TAG 范围外的行不可见（查不到即返回未找到）。
/// </summary>
public sealed class GetRecordDetailTool(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions,
    IAssistantRuntimeConfig? runtime = null) : IAssistantTool
{
    public const string ToolName = "get_record_detail";

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public string Name => ToolName;

    public AssistantToolRisk Risk => AssistantToolRisk.Read;

    public string Description =>
        "按主键取单条记录的完整字段，含明细行（主表字段 + 明细前若干行）。"
        + "module_id 与 _keys 必须来自 search_records 的返回结果，不要臆造。";

    public string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "search_records 返回的 module 编号" },
            "_keys": { "type": "array", "items": { "type": "string" }, "description": "search_records 每行返回的主键值数组" }
          },
          "required": ["module_id", "_keys"]
        }
        """;

    public async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        int moduleId = arguments.GetIntArg("module_id");
        IReadOnlyList<string> keys = [];
        if (arguments.TryGetProperty("_keys", out var keysEl) && keysEl.ValueKind == JsonValueKind.Array)
        {
            keys = keysEl.EnumerateArray()
                .Where(k => k.ValueKind == JsonValueKind.String)
                .Select(k => k.GetString() ?? string.Empty)
                .ToArray();
        }

        if (moduleId <= 0 || keys.Count == 0 || keys.Any(string.IsNullOrEmpty))
        {
            return ToolExecutionResult.Deny("参数不完整：需要 module_id 与非空的 _keys 主键值数组。");
        }

        var permission = await permissions.GetAsync(userId, moduleId, token);
        if (permission is null || !permission.CanBrowse)
        {
            return this.DenyBrowse($"#{moduleId}");
        }

        var (execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail) = permission.Scope();
        // 详情按物理列序读：明细每行只渲染前 N 列，按个人列序（常是字母序）排会把
        // 料号/数量挤出展示面（实测采购单明细只回金额/件数/库别）。字段集仍是列表口径，
        // 成本/保密/禁看过滤照旧由权限参数传入。
        var definition = await gateway.GetAssistantDefinitionAsync(
            moduleId, userId, execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail, token);
        if (definition is null)
        {
            return ToolExecutionResult.Deny($"模块 #{moduleId} 不是可查询的通用工作台模块。");
        }

        if (keys.Count != definition.MasterPkOrder.Count)
        {
            return ToolExecutionResult.Deny(
                $"主键长度不符：该模块主键为 {definition.MasterPkOrder.Count} 段（{string.Join("/", definition.MasterPkOrder)}）。");
        }

        var rows = await gateway.GetExportRowsByKeysAsync(
            definition, [keys], token, dataFilter: permission.Rights.DataFilter);
        var row = rows.FirstOrDefault();
        if (row is null)
        {
            // DATA_FILTER/EXEC_TAG 范围外的记录在此表现为“不存在”——不泄露其存在性。
            return this.DenyNotFound();
        }

        // 明细行与主表同一条读路径：主键映射按模块主键顺序，数据范围仍由 DATA_FILTER 强制。
        WorkbenchData? details = null;
        if (!string.IsNullOrWhiteSpace(definition.DetailTable) && definition.DetailFields.Count > 0)
        {
            var keyValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < definition.MasterPkOrder.Count; index++)
            {
                keyValues[definition.MasterPkOrder[index]] = keys[index];
            }

            details = await gateway.GetRowsAsync(
                definition, detail: true, keyValues, page: 1, pageSize: Limits.DetailMaxRows, token,
                dataFilter: permission.Rights.DataFilter);
        }

        return ToolExecutionResult.Success(Compress(definition, row, details, Limits));
    }

    /// <summary>输出全部可见主表列（限列数与值长），含字段标签；空值跳过。</summary>
    /// <param name="limits">输出上限；为 null 时用参数默认值（单测直接调用时不必造参数对象）。</param>
    /// <param name="details">明细行；为 null 表示模块没有明细或明细字段不可见。</param>
    internal static string Compress(
        WorkbenchDefinition definition, Dictionary<string, object?> row,
        WorkbenchData? details = null, AssistantToolLimitsOptions? limits = null)
    {
        limits ??= new AssistantToolLimitsOptions();
        var sb = new StringBuilder($"module={definition.ModuleId}({definition.Title}) detail:");
        sb.AppendLine();
        int taken = 0;
        foreach (var field in definition.MasterFields)
        {
            if (taken >= limits.DetailMaxColumns) break;
            if (!row.TryGetValue(field.Key, out var value)) continue;
            var text = value?.ToString();
            if (string.IsNullOrEmpty(text)) continue;
            sb.Append("- ").Append(field.Label).Append('=')
                .Append(text.Length > limits.DetailMaxValueLength
                    ? text[..limits.DetailMaxValueLength] + "…"
                    : text);
            sb.AppendLine();
            taken++;
        }

        if (taken == 0)
        {
            sb.AppendLine("(所有字段均为空)");
        }

        if (details is not null)
        {
            sb.AppendLine();
            sb.Append("明细（共 ").Append(details.Total).Append(" 行，显示前 ")
                .Append(details.Items.Count).Append(" 行）：");
            sb.AppendLine();
            foreach (var detail in details.Items)
            {
                sb.Append("- ");
                if (detail.TryGetValue("SERIAL_NO", out var serial) && serial is not null)
                {
                    // 修改既有明细行要靠 SERIAL_NO 对齐（detail_serials）；它可能不在可见明细列里，
                    // 所以单独补在最前面，避免模型"看得到行、改不了行"。
                    sb.Append("项次=").Append(serial).Append("；");
                }

                int rowTaken = 0;
                foreach (var field in definition.DetailFields)
                {
                    if (rowTaken >= limits.DetailRowMaxColumns) break;
                    if (field.Key.Equals("SERIAL_NO", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!detail.TryGetValue(field.Key, out var value)) continue;
                    var text = value?.ToString();
                    if (string.IsNullOrEmpty(text)) continue;
                    sb.Append(field.Label).Append('=')
                        .Append(text.Length > limits.DetailRowMaxValueLength
                            ? text[..limits.DetailRowMaxValueLength] + "…"
                            : text)
                        .Append("；");
                    rowTaken++;
                }

                if (rowTaken == 0) sb.Append("(空行)");
                sb.AppendLine();
            }

            // 每行列数有上限：把**被截掉的列名**说出来，否则模型会把"只看到 8 列"
            // 当成"这行就这些字段"，然后告诉用户"明细里没有料号/数量"。
            var firstRow = details.Items.FirstOrDefault();
            if (firstRow is not null)
            {
                var omitted = definition.DetailFields
                    .Where(field => !field.Key.Equals("SERIAL_NO", StringComparison.OrdinalIgnoreCase))
                    .Where(field => firstRow.TryGetValue(field.Key, out var value)
                                    && !string.IsNullOrEmpty(value?.ToString()))
                    .Skip(limits.DetailRowMaxColumns)
                    .Select(field => field.Label)
                    .ToList();
                if (omitted.Count > 0)
                {
                    var shown = omitted.Take(6).ToArray();
                    sb.Append("（每行还有 ").Append(omitted.Count).Append(" 列未展示：")
                        .Append(string.Join("、", shown))
                        .Append(omitted.Count > shown.Length ? "…" : string.Empty)
                        .Append("；完整字段见单据页）");
                    sb.AppendLine();
                }
            }

            if (details.Total > details.Items.Count)
            {
                sb.Append("（其余 ").Append(details.Total - details.Items.Count).Append(" 行未展示）");
                sb.AppendLine();
            }
        }

        var keys = definition.MasterPkOrder
            .Select(key => row.TryGetValue(key, out var value) ? value?.ToString() ?? string.Empty : string.Empty)
            .ToArray();
        if (keys.Any(key => key.Length > 0))
        {
            sb.AppendLine();
            sb.Append("查看：").Append(AssistantRecordLinks.View(definition.ModuleId, keys));
            if (definition.HasEdit)
            {
                sb.Append("；编辑：").Append(AssistantRecordLinks.Edit(definition.ModuleId, keys));
            }

            sb.Append("；打印：").Append(AssistantRecordLinks.Print(definition.ModuleId, keys));
        }

        return sb.ToString().TrimEnd();
    }
}
