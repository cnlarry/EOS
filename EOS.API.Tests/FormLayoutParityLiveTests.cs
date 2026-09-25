using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Data.Forms;
using EOS.API.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 模块级表单版式的「观感零变化」对拍（进程内版）：把改造前用
/// `scripts/compare-form-layout-parity.ps1 -Capture` 抓下的基线读回来，逐模块重建定义与表单，
/// 比对**字段集合与顺序**、页签与列数——版式改造若动了任何一处，这里立刻红。
///
/// 为什么不直接比 JSON 字符串：基线的定义 JSON 带发布期归一化（业务动作/效果引擎段、用户占位），
/// 进程内的重建无法原样复刻那些段；而本改造真正可能影响的是**字段视图**，故按字段序列比对，
/// 逐字比 JSON 由对拍脚本在做新构建的 API 上完成。
///
/// 需要真库连接；基线文件由环境变量 <c>EOS_FORM_LAYOUT_PARITY_BASELINE</c> 指定。
/// 未提供基线（或文件不存在）时不执行比对；提供即必须全绿（对拍基线是外部产物，不入库）。
/// </summary>
[Collection("live-database")]
public sealed class FormLayoutParityLiveTests
{
    private const string BaselineEnv = "EOS_FORM_LAYOUT_PARITY_BASELINE";

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static DbConnectionFactory Connections()
    {
        if (ConnectionString.Value is null)
        {
            throw new InvalidOperationException("真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");
        }
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        return new DbConnectionFactory(config);
    }

    [Fact]
    public async Task RebuiltFieldViews_MatchCapturedBaseline()
    {
        var baselinePath = Environment.GetEnvironmentVariable(BaselineEnv);
        if (string.IsNullOrWhiteSpace(baselinePath) || !File.Exists(baselinePath))
        {
            // 基线是外部产物（十几 MB，不入库）：未提供时不执行本对拍。一旦提供，比对必须全绿。
            return;
        }

        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var builder = new WorkbenchDefinitionBuilder(connections, provider,
            Options.Create(new UnifiedFormEditorSettings()), NullLogger<WorkbenchDefinitionBuilder>.Instance);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var baseline = JsonDocument.Parse(File.ReadAllBytes(baselinePath));
        var mismatches = new List<string>();
        var checkedDefinitions = 0;
        var checkedForms = 0;

        foreach (var module in baseline.RootElement.GetProperty("modules").EnumerateArray())
        {
            var moduleId = module.GetProperty("moduleId").GetInt32();
            var title = module.GetProperty("title").GetString() ?? string.Empty;
            var baselineDefinition = module.GetProperty("definition").GetString() ?? string.Empty;

            // ① 发布路径重建（与基线同源：都是从当前元数据重建的定义）。
            // 基线里没有定义 JSON 的模块是当时未过发布校验的那几个，没有可比对的基线，跳过定义侧。
            if (baselineDefinition.Length > 0)
            {
                var definition = await builder.GetDefinitionAsync(moduleId, "admin", "Z", true, true,
                    empty, empty, CancellationToken.None, forPublish: true);
                if (definition is null)
                {
                    mismatches.Add($"module {moduleId} {title}：基线有定义、现重建不出定义");
                }
                else
                {
                    checkedDefinitions++;
                    using var baselineJson = JsonDocument.Parse(baselineDefinition);
                    var root = baselineJson.RootElement;
                    CompareKeys(mismatches, moduleId, title, "masterFields",
                        Keys(root, "MasterFields"), definition.MasterFields.Select(field => field.Key));
                    CompareKeys(mismatches, moduleId, title, "detailFields",
                        Keys(root, "DetailFields"), definition.DetailFields.Select(field => field.Key));
                    CompareValue(mismatches, moduleId, title, "formTabs",
                        root.TryGetProperty("FormTabs", out var tabs) && tabs.ValueKind == JsonValueKind.String
                            ? tabs.GetString() ?? "null" : "null",
                        definition.FormTabs ?? "null");
                    CompareValue(mismatches, moduleId, title, "formColumns",
                        root.TryGetProperty("FormColumns", out var columns) && columns.ValueKind != JsonValueKind.Null
                            ? columns.ToString() : "null",
                        definition.FormColumns?.ToString() ?? "null");
                }
            }

            // ② 表单定义（用户实际看到的录入视图）：与基线的 form 段同源（运行态路径 + view 模式）
            var formStatus = module.GetProperty("formStatus").GetInt32();
            var baselineForm = module.GetProperty("form").GetString() ?? string.Empty;
            if (formStatus != 200 || baselineForm.Length == 0) continue;

            // 成本/保密可见性必须按该用户的**生效权限**传入（个人覆盖组）：基线是接口按真实权限
            // 抓的，这里硬传 true 会把保密字段带出来——那是比对口径问题，不是回归。
            var (canViewCost, canViewSecrecy) = await ResolveCostSecrecyAsync(connections, moduleId);
            var runtimeDefinition = await builder.GetDefinitionAsync(moduleId, "admin", "Z",
                canViewCost, canViewSecrecy, empty, empty, CancellationToken.None);
            if (runtimeDefinition is null)
            {
                mismatches.Add($"module {moduleId} {title}：基线有表单定义、现取不到运行态定义");
                continue;
            }
            var form = await builder.GetFormDefinitionAsync(runtimeDefinition, "admin", "view",
                canViewCost, canViewSecrecy,
                empty, empty, empty, empty, empty, empty, CancellationToken.None);
            if (form is null)
            {
                mismatches.Add($"module {moduleId} {title}：基线有表单定义、现重建不出表单定义");
                continue;
            }

            checkedForms++;
            using var baselineFormJson = JsonDocument.Parse(baselineForm);
            var formRoot = baselineFormJson.RootElement;
            CompareKeys(mismatches, moduleId, title, "form.masterFields",
                Keys(formRoot, "masterFields"), form.MasterFields.Select(field => field.Key));
            CompareKeys(mismatches, moduleId, title, "form.detailFields",
                Keys(formRoot, "detailFields"), form.DetailFields.Select(field => field.Key));
            CompareValue(mismatches, moduleId, title, "form.columns",
                formRoot.TryGetProperty("columns", out var formColumns) ? formColumns.ToString() : "null",
                form.Columns.ToString());
            CompareTabs(mismatches, moduleId, title, formRoot, form.Tabs);
        }

        Assert.True(checkedDefinitions > 250, $"对拍覆盖不足：仅比对了 {checkedDefinitions} 个模块的定义。");
        Assert.True(checkedForms > 200, $"对拍覆盖不足：仅比对了 {checkedForms} 个模块的表单定义。");
        Assert.True(mismatches.Count == 0,
            $"字段视图与基线不一致（{mismatches.Count} 处）：{string.Join(" ｜ ", mismatches.Take(20))}");
    }

    /// <summary>
    /// 重建出来的定义必须带上模块级版式段——它随快照下发，是运行期渲染与 P1 设计态的共同来源；
    /// 同时钉住"改造只多这一段"的前提：段存在且页签/字段视图不变（字段视图由上一个用例逐模块比对）。
    /// </summary>
    [Fact]
    public async Task RebuiltDefinition_CarriesModuleLevelFormLayoutSection()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var builder = new WorkbenchDefinitionBuilder(connections, provider,
            Options.Create(new UnifiedFormEditorSettings()), NullLogger<WorkbenchDefinitionBuilder>.Instance);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var definition = await builder.GetDefinitionAsync(1405, "admin", "Z", true, true,
            empty, empty, CancellationToken.None, forPublish: true);

        Assert.NotNull(definition);
        var layout = definition!.FormLayout;
        Assert.NotNull(layout);
        // 统一表单固定四子列（忽略 MODULES.FORM_COLUMNS：用户拍板"全局固定一行四列"）
        Assert.Equal(FormLayoutDerivation.DefaultColumns, layout!.Columns);
        Assert.NotEmpty(layout.Master);
        // 版式段必须与"当前是否已定制"自洽：库里该模块有行才算定制（无行是推导默认）
        var storedRows = await ReadStoredRowCountAsync(definition!.ModuleId);
        Assert.Equal(storedRows > 0, layout.MasterCustomized);

        var json = JsonSerializer.Serialize(definition);
        Assert.Contains("\"FormLayout\":", json);
        Assert.Contains("\"Master\":", json);
    }

    /// <summary>
    /// 该账号在某模块上的成本/保密可见性：个人权限行存在即完全采用个人行，否则取所属各组的布尔 OR
    /// （与权限引擎同一规则；组位不同取值时个人覆盖组，两者都不能省）。
    /// </summary>
    private static async Task<(bool Cost, bool Secrecy)> ResolveCostSecrecyAsync(
        DbConnectionFactory connections, int moduleId)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync();
        const string personalSql = """
            SELECT TOP 1 CAST(ISNULL(COST_TAG, 0) AS int), CAST(ISNULL(SECRECY_TAG, 0) AS int)
            FROM dbo.SYSDD WITH (NOLOCK) WHERE USER_ID = @UserId AND M_IDX = @ModuleId;
            """;
        await using (var command = new Microsoft.Data.SqlClient.SqlCommand(personalSql, connection))
        {
            command.Parameters.Add("@UserId", System.Data.SqlDbType.NChar, 10).Value = "admin";
            command.Parameters.Add("@ModuleId", System.Data.SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                return (reader.GetInt32(0) != 0, reader.GetInt32(1) != 0);
            }
        }
        const string groupSql = """
            SELECT ISNULL(MAX(CAST(h.COST_TAG AS INT)), 0), ISNULL(MAX(CAST(h.SECRECY_TAG AS INT)), 0)
            FROM dbo.SYSDH h WITH (NOLOCK)
            INNER JOIN dbo.SYSDG_USER gu WITH (NOLOCK) ON gu.G_IDX = h.G_IDX
            WHERE gu.USER_ID = @UserId AND h.M_IDX = @ModuleId;
            """;
        await using var groupCommand = new Microsoft.Data.SqlClient.SqlCommand(groupSql, connection);
        groupCommand.Parameters.Add("@UserId", System.Data.SqlDbType.NChar, 10).Value = "admin";
        groupCommand.Parameters.Add("@ModuleId", System.Data.SqlDbType.Int).Value = moduleId;
        await using var groupReader = await groupCommand.ExecuteReaderAsync();
        return await groupReader.ReadAsync()
            ? (groupReader.GetInt32(0) != 0, groupReader.GetInt32(1) != 0)
            : (false, false);
    }

    private static async Task<int> ReadStoredRowCountAsync(int moduleId)
    {
        await using var connection = Connections().Create();
        await connection.OpenAsync();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT COUNT(*) FROM dbo.MODULE_FORM_LAYOUT WHERE M_IDX = @ModuleId;", connection);
        command.Parameters.Add("@ModuleId", System.Data.SqlDbType.Int).Value = moduleId;
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static IEnumerable<string> Keys(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return array.EnumerateArray()
            .Select(item => item.TryGetProperty("Key", out var upper) ? upper.GetString()
                : item.TryGetProperty("key", out var lower) ? lower.GetString() : null)
            .Where(key => !string.IsNullOrEmpty(key))
            .Select(key => key!);
    }

    private static void CompareKeys(List<string> mismatches, int moduleId, string title, string label,
        IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var left = expected.ToArray();
        var right = actual.ToArray();
        if (left.SequenceEqual(right, StringComparer.OrdinalIgnoreCase)) return;
        var firstDifference = 0;
        while (firstDifference < left.Length && firstDifference < right.Length
               && string.Equals(left[firstDifference], right[firstDifference], StringComparison.OrdinalIgnoreCase))
        {
            firstDifference++;
        }
        var added = right.Except(left, StringComparer.OrdinalIgnoreCase);
        var removed = left.Except(right, StringComparer.OrdinalIgnoreCase);
        mismatches.Add($"module {moduleId} {title} {label}：{left.Length} -> {right.Length} 列，"
            + $"首个差异位 {firstDifference}（{left.ElementAtOrDefault(firstDifference) ?? "<无>"}"
            + $" -> {right.ElementAtOrDefault(firstDifference) ?? "<无>"}）"
            + $"；新增 [{string.Join(",", added)}] 移除 [{string.Join(",", removed)}]");
    }

    private static void CompareValue(List<string> mismatches, int moduleId, string title, string label,
        string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
        mismatches.Add($"module {moduleId} {title} {label}：{expected} -> {actual}");
    }

    /// <summary>页签比对：基线是表单接口的 camelCase 响应，重建侧是强类型，按 (号, 标题) 归一后比。</summary>
    private static void CompareTabs(List<string> mismatches, int moduleId, string title,
        JsonElement formRoot, IReadOnlyList<FormTabDefinition> actual)
    {
        var expected = new List<string>();
        if (formRoot.TryGetProperty("tabs", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var no = item.TryGetProperty("no", out var noElement) || item.TryGetProperty("No", out noElement)
                    ? noElement.GetInt32() : 0;
                var text = (item.TryGetProperty("title", out var titleElement)
                        || item.TryGetProperty("Title", out titleElement))
                    && titleElement.ValueKind == JsonValueKind.String
                        ? titleElement.GetString() ?? string.Empty : string.Empty;
                expected.Add($"{no}:{text}");
            }
        }
        var rebuilt = actual.Select(tab => $"{tab.No}:{tab.Title}").ToList();
        if (expected.SequenceEqual(rebuilt, StringComparer.Ordinal)) return;
        mismatches.Add($"module {moduleId} {title} form.tabs：[{string.Join(",", expected)}]"
            + $" -> [{string.Join(",", rebuilt)}]");
    }
}
