using System.Data;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests.Tools;

/// <summary>
/// Read-only audit of every configured module effect against the physical schema: runs
/// <see cref="EffectParamPhysicalValidator"/> (which reuses the handlers' own parameter
/// parsers) over MODULE_BUSINESS_ACTION/PARAM_STRUCT, plus the unscoped-write guard on
/// the formula rows. Writes a report to logs/adr012-acceptance/ and never writes the
/// workspace configuration, sends HTTP or starts services.
/// Run with EOS_AUDIT_RUN=1:
///   dotnet vstest ... --TestCaseFilter:"FullyQualifiedName~EffectConfigAuditRunner"
/// </summary>
[Trait("Category", "Tool")]
public sealed class EffectConfigAuditRunner
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    private sealed record ModuleRow(int ModuleId, string Description, string? MasterTable, string? DetailTable, bool EffectEngineTag);

    private sealed record ActionRow(long ActionId, int ModuleId, int Seq, string EventCode, string EffectKey, string? Params, string? Condition);

    private sealed record OpRow(long ActionId, int ModuleId, int Seq, int OpSeq, string TargetTable, string TargetField, string OpCode, string? Match, string? Condition);

    private sealed record Finding(string Module, int Seq, string EffectKey, string Kind, string Message);

    [Fact]
    public async Task Audit_AllModuleEffectConfigs_AgainstPhysicalSchema()
    {
        if (Environment.GetEnvironmentVariable("EOS_AUDIT_RUN") != "1")
        {
            return;
        }
        Assert.False(ConnectionString.Value is null, "拿不到开发库连接串（EOS.API/appsettings.Development.json）。");

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        var columns = await LoadPhysicalColumnsAsync(connection);
        var modules = await LoadModulesAsync(connection);
        var actions = await LoadActionsAsync(connection);
        var ops = await LoadOpsAsync(connection);
        var dirty = await LoadDirtyModulesAsync(connection);

        var findings = new List<Finding>();
        var uncoveredKeys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var checkedActions = 0;
        var skippedActions = 0;

        foreach (var module in modules)
        {
            var plan = new ModuleEffectPlan(
                module.ModuleId,
                module.MasterTable,
                module.DetailTable,
                null,
                module.MasterTable is null
                    ? Array.Empty<string>()
                    : (await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, module.MasterTable, CancellationToken.None)).ToArray(),
                Array.Empty<EffectActionPlan>(),
                Array.Empty<EffectValidationPlan>());

            foreach (var action in actions.Where(item => item.ModuleId == module.ModuleId))
            {
                if (string.IsNullOrWhiteSpace(action.Params))
                {
                    skippedActions++;
                    continue;
                }
                checkedActions++;
                if (!EffectParamPhysicalValidator.IsCovered(action.EffectKey))
                {
                    uncoveredKeys.Add(action.EffectKey);
                    continue;
                }
                foreach (var issue in EffectParamPhysicalValidator.Validate(action.EffectKey, action.Params, plan, columns))
                {
                    findings.Add(new Finding(module.ModuleId.ToString(CultureInfo.InvariantCulture), action.Seq, action.EffectKey, "参数物理校验", issue));
                }
                foreach (var issue in EffectStructSchemas.ValidateConditionJson(action.Condition, $"动作 SEQ={action.Seq} 条件"))
                {
                    findings.Add(new Finding(module.ModuleId.ToString(CultureInfo.InvariantCulture), action.Seq, action.EffectKey, "条件结构", issue));
                }
            }
        }

        foreach (var op in ops)
        {
            if (string.IsNullOrWhiteSpace(op.TargetTable) || string.IsNullOrWhiteSpace(op.OpCode))
            {
                continue; // translation-phase placeholder row on a service effect
            }
            var module = modules.FirstOrDefault(item => item.ModuleId == op.ModuleId);
            if (module is null)
            {
                continue;
            }
            if (!string.Equals(op.TargetTable, module.MasterTable, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(op.Match)
                && string.IsNullOrWhiteSpace(op.Condition))
            {
                findings.Add(new Finding(
                    op.ModuleId.ToString(CultureInfo.InvariantCulture), op.Seq, op.OpCode, "无条件公式行",
                    $"公式行 OP_SEQ={op.OpSeq} 目标 {op.TargetTable}.{op.TargetField} 无定位键也无条件，执行时会更新整表。"));
            }
            if (!columns.Contains(op.TargetTable + "." + op.TargetField))
            {
                findings.Add(new Finding(
                    op.ModuleId.ToString(CultureInfo.InvariantCulture), op.Seq, op.OpCode, "公式行物理列",
                    $"公式行 OP_SEQ={op.OpSeq} 目标列不存在：{op.TargetTable}.{op.TargetField}。"));
            }
            foreach (var issue in EffectStructSchemas.ValidateConditionJson(op.Condition, $"公式行 OP_SEQ={op.OpSeq} 条件"))
            {
                findings.Add(new Finding(
                    op.ModuleId.ToString(CultureInfo.InvariantCulture), op.Seq, op.OpCode, "公式行条件结构", issue));
            }
        }

        var report = new StringBuilder();
        report.AppendLine("# 效果配置物理审计");
        report.AppendLine();
        report.AppendLine($"- 运行时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        report.AppendLine($"- 模块数：{modules.Count}（有业务动作配置）；动作行：{actions.Count}；公式行：{ops.Count}");
        report.AppendLine($"- 参数校验覆盖：{checkedActions} 条带参数动作已校验，{skippedActions} 条无参数动作跳过");
        report.AppendLine($"- 发现项：**{findings.Count}**");
        report.AppendLine();
        report.AppendLine("| 模块 | SEQ | 效果键 | 类别 | 说明 |");
        report.AppendLine("|---|---|---|---|---|");
        foreach (var finding in findings)
        {
            report.AppendLine($"| {finding.Module} | {finding.Seq} | {finding.EffectKey} | {finding.Kind} | {finding.Message.Replace("|", "/")} |");
        }
        report.AppendLine();
        report.AppendLine("## 无配置驱动表列引用的效果键（按设计跳过）");
        report.AppendLine();
        report.AppendLine(uncoveredKeys.Count == 0
            ? "（无）"
            : string.Join("、", uncoveredKeys));
        report.AppendLine();
        report.AppendLine("## 模块状态");
        report.AppendLine();
        report.AppendLine("| 模块 | 名称 | 主表 | 明细表 | EE | 未发布改动 |");
        report.AppendLine("|---|---|---|---|---|---|");
        foreach (var module in modules)
        {
            report.AppendLine($"| {module.ModuleId} | {module.Description.Replace("|", "/")} | {module.MasterTable} | {module.DetailTable ?? "-"} " +
                              $"| {(module.EffectEngineTag ? "ON" : "OFF")} | {(dirty.Contains(module.ModuleId) ? "是" : "")} |");
        }

        var directory = Path.Combine(RepoRoot.Value, "logs", "adr012-acceptance");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"effect-config-audit-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        await File.WriteAllTextAsync(path, report.ToString());

        Console.WriteLine($"audit modules={modules.Count} paramsChecked={checkedActions} findings={findings.Count} report={path}");
        foreach (var finding in findings)
        {
            Console.WriteLine($"  [{finding.Module} seq{finding.Seq} {finding.EffectKey}] {finding.Kind}：{finding.Message}");
        }
    }

    private static async Task<ISet<string>> LoadPhysicalColumnsAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT o.name, c.name
            FROM sys.objects o
            JOIN sys.columns c ON c.object_id = o.object_id
            WHERE o.type IN ('U','V') AND SCHEMA_NAME(o.schema_id) = N'dbo';
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            result.Add(table);
            result.Add(table + "." + reader.GetString(1));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ModuleRow>> LoadModulesAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT M_IDX, ISNULL(M_DESC, N''), NULLIF(LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))), N''),
                   NULLIF(LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))), N''), ISNULL(EFFECT_ENGINE_TAG, 0)
            FROM dbo.MODULES WITH (NOLOCK)
            WHERE M_IDX IN (SELECT DISTINCT M_IDX FROM dbo.MODULE_BUSINESS_ACTION WITH (NOLOCK))
            ORDER BY M_IDX;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<ModuleRow>();
        while (await reader.ReadAsync())
        {
            result.Add(new ModuleRow(
                reader.GetInt32(0),
                reader.GetString(1).Trim(),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<ActionRow>> LoadActionsAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT ACTION_ID, M_IDX, SEQ, EVENT_CODE, EFFECT_KEY, PARAM_STRUCT, CONDITION_STRUCT
            FROM dbo.MODULE_BUSINESS_ACTION WITH (NOLOCK)
            ORDER BY M_IDX, SEQ;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<ActionRow>();
        while (await reader.ReadAsync())
        {
            result.Add(new ActionRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetString(3).Trim(),
                reader.GetString(4).Trim(),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<OpRow>> LoadOpsAsync(SqlConnection connection)
    {
        const string sql = """
            SELECT o.ACTION_ID, a.M_IDX, a.SEQ, o.OP_SEQ, o.TARGET_TABLE, o.TARGET_FIELD, o.OP_CODE,
                   o.MATCH_STRUCT, o.CONDITION_STRUCT
            FROM dbo.MODULE_BUSINESS_ACTION_OP o WITH (NOLOCK)
            JOIN dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK) ON a.ACTION_ID = o.ACTION_ID
            ORDER BY a.M_IDX, a.SEQ, o.OP_SEQ;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<OpRow>();
        while (await reader.ReadAsync())
        {
            result.Add(new OpRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4).Trim(),
                reader.GetString(5).Trim(),
                reader.GetString(6).Trim(),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }
        return result;
    }

    private static async Task<HashSet<int>> LoadDirtyModulesAsync(SqlConnection connection)
    {
        const string sql = "SELECT M_IDX FROM dbo.WORKBENCH_MODULE_DIRTY WITH (NOLOCK);";
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new HashSet<int>();
        while (await reader.ReadAsync())
        {
            result.Add(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture));
        }
        return result;
    }

    private static string? ResolveConnectionString()
    {
        var settingsPath = Path.Combine(RepoRoot.Value, "EOS.API", "appsettings.Development.json");
        if (!File.Exists(settingsPath))
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return document.RootElement.TryGetProperty("ConnectionStrings", out var section)
            && section.TryGetProperty("ErpDatabase", out var value)
            ? value.GetString()
            : null;
    }

    private static string FindRepoRoot()
    {
        var attribute = typeof(EffectConfigAuditRunner).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepoRoot");
        if (attribute?.Value is { Length: > 0 } root && Directory.Exists(root))
        {
            return Path.GetFullPath(root);
        }
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EOS.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
