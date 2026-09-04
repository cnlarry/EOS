using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Admin;

/// <summary>M8 admin-write v1: metadata-only changeset (register tables in TABLES,
/// batch-generate field metadata for existing physical columns). No DDL, no MODULES
/// rows, no physical column creation. Trial never writes; execute re-validates first.</summary>
public sealed record ChangeSetTableResult(
    string Table, string Action, string Status,
    IReadOnlyList<string> WouldCreate, IReadOnlyList<string> WouldSkip, IReadOnlyList<string> Errors);

public sealed record ChangeSetTrial(
    string Goal, bool Blocked,
    IReadOnlyList<ChangeSetTableResult> Tables, IReadOnlyList<string> Errors);

public sealed record ChangeSetApplyResult(
    string Goal, int TablesRegistered, int FieldsCreated, int FieldsSkipped,
    IReadOnlyList<string> Notes);

public interface IChangeSetWriter
{
    Task RegisterTableAsync(string tableId, string description, string? kind, string updatedBy, CancellationToken token);
    Task<(int Created, int Skipped, IReadOnlyList<string> Reasons)> AddFieldsAsync(
        string tableId, IReadOnlyList<string> fieldIds, string updatedBy, CancellationToken token);
}

public sealed class ChangeSetService(IModulePlanCatalog catalog, IChangeSetWriter writer)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    public async Task<ChangeSetTrial> TrialAsync(JsonElement changeset, CancellationToken token)
    {
        var errors = new List<string>();
        var goal = changeset.ValueKind == JsonValueKind.Object && changeset.TryGetProperty("goal", out var goalEl)
            && goalEl.ValueKind == JsonValueKind.String ? goalEl.GetString()!.Trim() : string.Empty;
        if (goal.Length == 0) errors.Add("变更集缺少 goal。");
        var tables = new List<ChangeSetTableResult>();
        if (changeset.ValueKind != JsonValueKind.Object
            || !changeset.TryGetProperty("tables", out var tablesEl)
            || tablesEl.ValueKind != JsonValueKind.Array
            || tablesEl.GetArrayLength() == 0)
        {
            errors.Add("变更集 tables 数组不能为空。");
            return new(goal, true, tables, errors);
        }

        var registered = (await catalog.ListTablesAsync(token))
            .ToDictionary(table => table.TableId, StringComparer.OrdinalIgnoreCase);
        foreach (var item in tablesEl.EnumerateArray())
        {
            tables.Add(await TrialTableAsync(item, registered, token));
        }

        var blocked = errors.Count > 0 || tables.Any(table => table.Status == "blocked");
        return new(goal, blocked, tables, errors);
    }

    public async Task<ChangeSetApplyResult> ExecuteAsync(
        JsonElement changeset, string updatedBy, CancellationToken token)
    {
        var trial = await TrialAsync(changeset, token);
        if (trial.Blocked)
        {
            throw new InvalidOperationException("变更集试算未通过，拒绝执行：" + string.Join("；", trial.Errors
                .Concat(trial.Tables.Where(table => table.Status == "blocked")
                    .SelectMany(table => table.Errors.Select(error => $"{table.Table}: {error}")))));
        }

        var tablesRegistered = 0;
        var fieldsCreated = 0;
        var fieldsSkipped = 0;
        var notes = new List<string>();
        var index = 0;
        foreach (var item in changeset.GetProperty("tables").EnumerateArray())
        {
            var table = item.GetProperty("table").GetString()!;
            var action = item.GetProperty("action").GetString()!;
            if (action == "register_table")
            {
                var description = item.TryGetProperty("description", out var descEl)
                    && descEl.ValueKind == JsonValueKind.String ? descEl.GetString()!.Trim() : table;
                var kind = item.TryGetProperty("kind", out var kindEl)
                    && kindEl.ValueKind == JsonValueKind.String ? kindEl.GetString()!.Trim() : null;
                await writer.RegisterTableAsync(table, description, kind, updatedBy, token);
                tablesRegistered++;
                notes.Add($"{table}：已登记表元数据");
            }
            else
            {
                var fields = item.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array
                    ? fieldsEl.EnumerateArray()
                        .Where(element => element.ValueKind == JsonValueKind.String)
                        .Select(element => element.GetString()!.Trim())
                        .Where(field => field.Length > 0)
                        .ToArray()
                    : [];
                // Only create fields the trial marked creatable; trial-skipped ones stay skipped.
                var creatable = trial.Tables[index].WouldCreate;
                var (created, skipped, reasons) = await writer.AddFieldsAsync(table, creatable, updatedBy, token);
                fieldsCreated += created;
                fieldsSkipped += skipped;
                notes.Add($"{table}：新增字段 {created} 个，跳过 {skipped} 个");
                notes.AddRange(reasons.Select(reason => $"{table}：{reason}"));
            }

            index++;
        }

        return new(trial.Goal, tablesRegistered, fieldsCreated, fieldsSkipped, notes);
    }

    private async Task<ChangeSetTableResult> TrialTableAsync(
        JsonElement item, IReadOnlyDictionary<string, FieldAdminTable> registered, CancellationToken token)
    {
        var wouldCreate = new List<string>();
        var wouldSkip = new List<string>();
        var errors = new List<string>();
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("table", out var tableEl) || tableEl.ValueKind != JsonValueKind.String
            || !item.TryGetProperty("action", out var actionEl) || actionEl.ValueKind != JsonValueKind.String)
        {
            return new(string.Empty, string.Empty, "blocked", wouldCreate, wouldSkip, ["表条目须含 table/action。"]);
        }

        var table = tableEl.GetString()!.Trim();
        var action = actionEl.GetString()!.Trim();
        if (!Identifier.IsMatch(table))
        {
            return new(table, action, "blocked", wouldCreate, wouldSkip, [$"非法表名：{table}。"]);
        }

        if (action != "register_table" && action != "add_fields")
        {
            return new(table, action, "blocked", wouldCreate, wouldSkip,
                [$"不支持的动作：{action}（v1 仅支持 register_table/add_fields）。"]);
        }

        if (action == "register_table")
        {
            if (registered.ContainsKey(table))
            {
                return new(table, action, "blocked", wouldCreate, wouldSkip, ["该表已在 TABLES 登记，无需重复登记。"]);
            }

            wouldCreate.Add($"登记表元数据 {table}");
            return new(table, action, "ok", wouldCreate, wouldSkip, errors);
        }

        if (!registered.ContainsKey(table))
        {
            return new(table, action, "blocked", wouldCreate, wouldSkip, ["该表未在 TABLES 登记，请先 register_table。"]);
        }

        var fields = item.TryGetProperty("fields", out var fieldsEl) && fieldsEl.ValueKind == JsonValueKind.Array
            ? fieldsEl.EnumerateArray()
                .Where(element => element.ValueKind == JsonValueKind.String)
                .Select(element => element.GetString()!.Trim())
                .Where(field => field.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray()
            : [];
        if (fields.Length == 0)
        {
            return new(table, action, "blocked", wouldCreate, wouldSkip, ["add_fields 须提供非空 fields 数组。"]);
        }

        var illegal = fields.Where(field => !Identifier.IsMatch(field)).ToArray();
        if (illegal.Length > 0)
        {
            return new(table, action, "blocked", wouldCreate, wouldSkip,
                illegal.Select(field => $"非法字段名：{field}。").ToArray());
        }

        var existing = (await catalog.ListFieldsAsync(table, token))
            .Select(field => field.FieldId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unmanaged = (await catalog.ListUnmanagedAsync(table, token))
            .Select(field => field.FieldId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            if (existing.Contains(field)) wouldSkip.Add($"{field}：已有元数据，跳过");
            else if (unmanaged.Contains(field)) wouldCreate.Add(field);
            else wouldSkip.Add($"{field}：物理列不存在，跳过（v1 不建物理列）");
        }

        return new(table, action, "ok", wouldCreate, wouldSkip, errors);
    }
}
