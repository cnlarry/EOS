using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// M8a read-only probe: diagnose which tables/fields a new page needs and preview
/// the metadata changeset as JSON. Zero writes by construction: the catalog only
/// exposes FieldAdmin read methods, so no write path exists to call.
/// </summary>
public sealed record PlanFieldRequest(string Field, string? Label = null);

public sealed record PlanTableRequest(string Table, IReadOnlyList<PlanFieldRequest>? Fields = null);

public interface IModulePlanCatalog
{
    Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(CancellationToken token);
    Task<IReadOnlyList<FieldAdminFieldSummary>> ListFieldsAsync(string tableId, CancellationToken token);
    Task<IReadOnlyList<FieldAdminUnmanagedField>> ListUnmanagedAsync(string tableId, CancellationToken token);
}

public sealed class ModulePlanCatalog(FieldAdminRepository fieldAdmin) : IModulePlanCatalog
{
    public Task<IReadOnlyList<FieldAdminTable>> ListTablesAsync(CancellationToken token) =>
        fieldAdmin.GetTablesAsync(null, token);

    public async Task<IReadOnlyList<FieldAdminFieldSummary>> ListFieldsAsync(string tableId, CancellationToken token)
    {
        var fields = new List<FieldAdminFieldSummary>();
        var page = 1;
        FieldAdminPageResult result;
        do
        {
            result = await fieldAdmin.GetFieldsAsync(tableId, null, page, 100, token);
            fields.AddRange(result.Items);
            page++;
        } while (fields.Count < result.Total && result.Items.Count > 0);

        return fields;
    }

    public Task<IReadOnlyList<FieldAdminUnmanagedField>> ListUnmanagedAsync(string tableId, CancellationToken token) =>
        fieldAdmin.GetUnmanagedFieldsAsync(tableId, token);
}

public sealed class DiagnoseModuleTool(
    IModulePlanCatalog catalog,
    IPermissionService permissions) : AssistantSchemaToolBase(permissions)
{
    public const string ToolName = "diagnose_module";

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description =>
        "只读诊断：搭一个新模块/表单页需要哪些表与字段、牵动哪些元数据，并生成变更集 JSON 预览。不执行任何写入。";
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "goal": { "type": "string", "description": "目标，如「加一页客户回访单」" },
            "tables": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "table": { "type": "string" },
                  "fields": { "type": "array", "items": { "type": "string" } }
                },
                "required": ["table"]
              }
            }
          },
          "required": ["goal", "tables"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var denied = await RequireSetupAsync(userId, token);
        if (denied is not null) return denied;
        var goal = arguments.GetStringArg("goal").Trim();
        if (goal.Length == 0) return ToolExecutionResult.Deny("请说明要加什么页（goal 不能为空）。");
        if (!arguments.TryGetProperty("tables", out var tablesEl) || tablesEl.ValueKind != JsonValueKind.Array)
            return ToolExecutionResult.Deny("请提供候选表清单（tables 数组，至少一张表）。");

        var requested = new List<PlanTableRequest>();
        foreach (var item in tablesEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return ToolExecutionResult.Deny("tables 数组元素须为对象。");
            var table = item.TryGetProperty("table", out var tableEl) && tableEl.ValueKind == JsonValueKind.String
                ? tableEl.GetString()!.Trim() : string.Empty;
            if (table.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(table, "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
                return ToolExecutionResult.Deny($"非法表名：{table}。");
            var fields = new List<PlanFieldRequest>();
            if (item.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var fieldEl in fieldsEl.EnumerateArray())
                {
                    var field = fieldEl.ValueKind == JsonValueKind.String ? fieldEl.GetString()!.Trim() : string.Empty;
                    if (field.Length == 0 || !System.Text.RegularExpressions.Regex.IsMatch(field, "^[A-Za-z_][A-Za-z0-9_]{0,127}$"))
                        return ToolExecutionResult.Deny($"非法字段名：{field}（表 {table}）。");
                    fields.Add(new(field));
                }
            }

            requested.Add(new(table, fields));
        }

        if (requested.Count == 0) return ToolExecutionResult.Deny("请提供候选表清单（tables 数组，至少一张表）。");

        var registered = (await catalog.ListTablesAsync(token))
            .ToDictionary(table => table.TableId, StringComparer.OrdinalIgnoreCase);
        var tableReports = new List<object>();
        var changesetTables = new List<object>();
        foreach (var request in requested)
        {
            if (!registered.TryGetValue(request.Table, out var tableMeta))
            {
                tableReports.Add(new { table = request.Table, status = "未在 TABLES 登记" });
                changesetTables.Add(new { table = request.Table, action = "register_table", fields = request.Fields });
                continue;
            }

            var fields = await catalog.ListFieldsAsync(tableMeta.TableId, token);
            var byId = fields.ToDictionary(field => field.FieldId, StringComparer.OrdinalIgnoreCase);
            var unmanaged = (await catalog.ListUnmanagedAsync(tableMeta.TableId, token))
                .Select(field => field.FieldId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var missing = new List<string>();
            var ghosts = new List<string>();
            foreach (var wanted in request.Fields ?? [])
            {
                if (!byId.TryGetValue(wanted.Field, out var summary))
                {
                    missing.Add(unmanaged.Contains(wanted.Field)
                        ? $"{wanted.Field}（物理列存在、字典未注册，可批量生成）"
                        : $"{wanted.Field}（字典与物理列均无，需新增物理列 + 字段元数据）");
                }
                else if (!summary.PhysicalExists && !summary.IsVirtual)
                {
                    ghosts.Add($"{wanted.Field}（幽灵字段：字典有、物理列无，不可用）");
                }
            }

            tableReports.Add(new
            {
                table = tableMeta.TableId,
                status = $"已登记（{tableMeta.Description}，字典 {fields.Count} 字段）",
                missing,
                ghosts,
            });
            if (missing.Count > 0 || ghosts.Count > 0)
            {
                changesetTables.Add(new { table = tableMeta.TableId, action = "add_fields", missing, ghosts });
            }
        }

        var changeset = new
        {
            version = 1,
            goal,
            previewOnly = true,
            tables = changesetTables,
            moduleRow = new { action = "insert_modules_row", note = "MODULES 新增一行由顾问在菜单管理中登记，本探针不写库" },
        };
        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        var sb = new StringBuilder($"诊断：{goal}");
        foreach (var report in tableReports)
        {
            sb.AppendLine().Append(JsonSerializer.Serialize(report, jsonOptions));
        }

        sb.AppendLine().Append("变更集预览（JSON，未执行任何写入）：");
        sb.Append(JsonSerializer.Serialize(changeset, jsonOptions));
        return ToolExecutionResult.Success(sb.ToString());
    }
}
