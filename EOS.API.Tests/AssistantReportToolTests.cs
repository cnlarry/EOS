using System.Text.Json;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 报表只读工具的**门禁与输出**（离线，全部走假网关）：
/// 无浏览权一律拒绝、报表不存在按防探测口径回答、条件序号必须真实存在、输出按上限截断。
///
/// <para>
/// 这几条是"只读工具也不能绕权限"的最小证据：真实取数链路（定义构建 / 数据范围 / 条件参数化）
/// 在报表仓储里，此处不重复验证；这里只钉住**工具自己**会做错的那部分。
/// </para>
/// </summary>
public sealed class AssistantReportToolTests
{
    private const string ReportId = "bom-structure";

    private static ModuleRights Rights(bool canBrowse = true, string dataFilter = "") => new(
        CanBrowse: canBrowse, CanViewCost: false, CanViewSecrecy: false, CanSetup: false,
        DeniedMasterFields: new HashSet<string>(), DeniedDetailFields: new HashSet<string>(),
        CanAddNew: false, CanEdit: false, CanDelete: false, CanApprove: false, CanDeapprove: false,
        CanEndCase: false, CanUnEndCase: false, CanFileView: false, CanFileUpda: false,
        CanFileEdit: false, CanFileDele: false,
        DenyNewMasterFields: new HashSet<string>(), DenyNewDetailFields: new HashSet<string>(),
        DenyModiMasterFields: new HashSet<string>(), DenyModiDetailFields: new HashSet<string>(),
        DataFilter: dataFilter, ExecuteTag: "A");

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>拼 run_report 的参数 JSON：条件片段原样拼接，避免原始字符串里的花括号歧义。</summary>
    private static JsonElement RunArgs(string? conditions = null) =>
        Args("{\"report_id\":\"" + ReportId + "\""
            + (conditions is null ? string.Empty : ",\"conditions\":" + conditions)
            + "}");

    private static AssistantPolicyValues Policy(AssistantToolLimitsOptions limits) =>
        AssistantPolicyValues.Default with { ToolLimits = limits };

    private static ReportDefinition Definition(params string[] columnLabels)
    {
        var columns = columnLabels
            .Select((label, index) => new ReportColumn($"C{index}", label, "string"))
            .ToList();
        return new ReportDefinition(
            ModuleId: 1204,
            Title: "产品BOM表",
            MasterTable: "BOM_STRU_M",
            DetailTable: null,
            Conditions:
            [
                new ReportCondition(1, "PRO_NO", "产品编号", 2, null, null, "@P1", []),
            ],
            Columns: columns,
            MasterPkOrder: ["BOM_NO"],
            SortFields: []);
    }

    [Fact]
    public async Task List_Reports_Denies_When_Module_Not_Browsable()
    {
        var tool = new ListReportsTool(
            new FakePermissions(Rights(canBrowse: false)), new FakeGateway());

        var result = await tool.ExecuteAsync("u1", Args("""{"module_id":1204}"""), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("没有模块", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_Reports_Falls_Back_To_The_Page_Module()
    {
        // 用户不必先报模块号：页面处境（服务端注入）里的模块就是默认受审对象
        var gateway = new FakeGateway();
        var tool = new ListReportsTool(new FakePermissions(Rights()), gateway);
        tool.UsePageContext(new PageContext(1204, "产品BOM表", "list", null));

        var result = await tool.ExecuteAsync("u1", Args("{}"), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1204, gateway.LastListedModuleId);
        Assert.Contains(ReportId, result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_Report_Without_Module_Argument_Uses_Servers_Own_Resolution()
    {
        // 归属模块只能由服务端解析：工具的任何入参都不参与"用哪个模块的权限"
        var gateway = new FakeGateway();
        var tool = new RunReportTool(new FakePermissions(Rights()), gateway);

        var result = await tool.ExecuteAsync("u1", RunArgs(), CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal(1204, gateway.LastDefinitionModuleId);
    }

    [Fact]
    public async Task Run_Report_Answers_Not_Found_In_Probe_Safe_Wording()
    {
        var gateway = new FakeGateway { Identity = null };
        var tool = new RunReportTool(new FakePermissions(Rights()), gateway);

        var result = await tool.ExecuteAsync("u1", Args("""{"report_id":"no-such"}"""), CancellationToken.None);

        Assert.False(result.Ok);
        // 不区分"不存在"与"不在数据范围内"：沿用既有防探测口径，助手侧不得改口
        Assert.Equal(AssistantToolExtensions.NotFoundMessage, result.ContentForModel);
    }

    [Fact]
    public async Task Run_Report_Rejects_Condition_Serial_That_The_Report_Does_Not_Declare()
    {
        var tool = new RunReportTool(new FakePermissions(Rights()), new FakeGateway());

        // 序号写错若不拦，就会得到一份"看起来用了条件、其实是全量"的数据——比报错危险得多
        var result = await tool.ExecuteAsync(
            "u1", RunArgs("""{"9":"P-1"}"""), CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Contains("条件序号 9", result.ContentForModel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_Report_Passes_Conditions_And_Renders_Truncated_Rows()
    {
        var gateway = new FakeGateway
        {
            Definition = Definition("产品编号", "产品名称"),
            Result = new ReportQueryResult(
                Rows:
                [
                    new Dictionary<string, object?> { ["C0"] = "P-1", ["C1"] = "这是一个很长的产品名称" },
                    new Dictionary<string, object?> { ["C0"] = "P-2", ["C1"] = "第二个" },
                ],
                Total: 37, Page: 1, PageSize: 20),
        };
        var tool = new RunReportTool(
            new FakePermissions(Rights(dataFilter: "AND 1=1")), gateway,
            AssistantTestRuntime.Fixed(
                new AssistantSettings(),
                Policy(new AssistantToolLimitsOptions { ReportMaxRows = 1, ReportMaxColumns = 2, ReportMaxValueLength = 6 })));

        var result = await tool.ExecuteAsync(
            "u1", RunArgs("""{"1":{"from":"P-1","to":"P-9"}}"""), CancellationToken.None);

        Assert.True(result.Ok);
        // 条件按"序号 → 值"落在参数契约里（范围条件同时给 from/to）
        Assert.Equal("P-1", gateway.LastRequest!.Values[1]);
        Assert.Equal("P-9", gateway.LastRequest.ValuesTo[1]);
        // 数据范围随取数一起下发，工具自己不加第二套判定
        Assert.Equal("AND 1=1", gateway.LastDataFilter);
        Assert.Contains("命中=37", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("产品编号", result.ContentForModel, StringComparison.Ordinal);
        // 单值截断到 6 字符并带省略号（截断语义：留前 N 个字符 + 一个省略号）
        Assert.Contains("这是一个很长…", result.ContentForModel, StringComparison.Ordinal);
        // 行数上限 1：第二条不列出，但要说清还有多少行没列
        Assert.DoesNotContain("P-2 ", result.ContentForModel, StringComparison.Ordinal);
        Assert.Contains("本页还有 1 行未列出", result.ContentForModel, StringComparison.Ordinal);
    }

    private sealed class FakePermissions(ModuleRights rights) : IPermissionService
    {
        public Task<ModulePermission> GetAsync(string userId, int moduleId, CancellationToken cancellationToken) =>
            Task.FromResult(new ModulePermission(rights));

        public Task<ModulePermission> RequireAsync(string userId, int moduleId, PermissionAction action, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>假网关：只回放预设的清单 / 身份 / 定义 / 结果，并记下工具传下来的参数。</summary>
    private sealed class FakeGateway : IReportGateway
    {
        public ReportIdentity? Identity { get; init; } = new(1204, ReportId, "BOM 结构表", "产品BOM表");

        public ReportDefinition? Definition { get; init; } = AssistantReportToolTests.Definition("产品编号");

        public ReportQueryResult Result { get; init; } = new([], 0, 1, 20);

        public int? LastListedModuleId { get; private set; }

        public int? LastDefinitionModuleId { get; private set; }

        public ReportQueryRequest? LastRequest { get; private set; }

        public string? LastDataFilter { get; private set; }

        public Task<IReadOnlyList<ReportSibling>> ListReportsAsync(int moduleId, string userId, CancellationToken token)
        {
            LastListedModuleId = moduleId;
            return Task.FromResult<IReadOnlyList<ReportSibling>>(
                [new ReportSibling(ReportId, "BOM 结构表", true)]);
        }

        public Task<ReportIdentity?> FindIdentityAsync(string reportId, CancellationToken token) =>
            Task.FromResult(reportId == ReportId ? Identity : null);

        public Task<ReportDefinition?> GetDefinitionAsync(
            int moduleId, string userId, ModuleRights rights, string reportId, CancellationToken token)
        {
            LastDefinitionModuleId = moduleId;
            return Task.FromResult(Definition);
        }

        public Task<ReportQueryResult> QueryAsync(
            ReportDefinition definition, ReportQueryRequest request, int page, int pageSize,
            string? dataFilter, CancellationToken token)
        {
            LastRequest = request;
            LastDataFilter = dataFilter;
            return Task.FromResult(Result);
        }
    }
}
