using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 报表身份的解析：报表编号 → 归属模块，必须**由服务端查**、且与库内一致。
///
/// 这条路是整个"按身份打开报表"的权限锚点：解析出哪个模块，就拿哪个模块的权限去判。
/// 一旦解析口径与库内不一致（例如又被改回按承载页列取），权限就会锚到别的模块上——
/// 表现是"能打开本不该打开的报表"或"打不开本该打开的报表"，两种都不会报错、不会 403。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ReportIdentityLiveTests
{
    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private static ReportRepository CreateRepository()
        => new(PolicyServiceFactory.Connections(RequireConnection()), NullLogger<ReportRepository>.Instance);

    private static (string ReportId, int ModuleId) FirstReport()
    {
        using var connection = new SqlConnection(RequireConnection());
        connection.Open();
        using var command = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(REPORT_ID)),M_IDX FROM dbo.REPORT WITH (NOLOCK) ORDER BY REPORT_ID;", connection);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read(), "REPORT 为空，无法验证身份解析。");
        return (reader.GetString(0), reader.GetInt32(1));
    }

    [Fact]
    public async Task 按报表编号解析出的归属模块与库内一致()
    {
        var (reportId, moduleId) = FirstReport();
        var identity = await CreateRepository().FindIdentityAsync(reportId, CancellationToken.None);

        Assert.NotNull(identity);
        Assert.Equal(reportId, identity!.ReportId);
        Assert.Equal(moduleId, identity.ModuleId);
        Assert.False(string.IsNullOrWhiteSpace(identity.ModuleName), "模块名不得为空（载荷要用它做标题）");
    }

    [Fact]
    public async Task 不存在的报表编号解析为空()
    {
        var identity = await CreateRepository().FindIdentityAsync("NO_SUCH_REPORT_XYZ", CancellationToken.None);
        Assert.Null(identity);
    }

    [Fact]
    public async Task 空白编号解析为空_不得回落成默认报表()
    {
        // 回落成"默认报表"是这条路上最危险的一种错：编号写错了却打开了另一张，
        // 调用方还以为自己开的是点的那张。
        var repository = CreateRepository();
        Assert.Null(await repository.FindIdentityAsync(string.Empty, CancellationToken.None));
        Assert.Null(await repository.FindIdentityAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task 模块名按归属模块取()
    {
        var (_, moduleId) = FirstReport();
        var name = await CreateRepository().FindModuleNameAsync(moduleId, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }
}
