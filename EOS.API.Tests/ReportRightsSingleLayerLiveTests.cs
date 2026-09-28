using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 报表权限只剩一层：**归属模块的 `REPORT_TAG`**（个人 `SYSDD` 优先，否则组 `SYSDH` 取或）。
///
/// <para>
/// 从前的第二层是逐报表例外行（报表级三列勾选 + 逐报表行级过滤，例外表已随迁移 277 退役），
/// 那四列已随管理面退场。这里把"唯一真源"钉住：模块闸门通过 → 看/打/导出全放行且无报表级行过滤；
/// 不通过 → 三者全禁。若哪天有人又把某处例外逻辑加回来，会先在这里露头。
/// </para>
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class ReportRightsSingleLayerLiveTests
{
    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private static ModuleRightsRepository CreateRepository()
        => new(PolicyServiceFactory.Connections(RequireConnection()), NullLogger<ModuleRightsRepository>.Instance);

    /// <summary>取一个"个人行明确授予 REPORT_TAG"的（账号, 模块）组合。</summary>
    private static (string UserId, int ModuleId)? FindGranted()
    {
        using var connection = new SqlConnection(RequireConnection());
        connection.Open();
        using var command = new SqlCommand("""
            SELECT TOP 1 LTRIM(RTRIM(d.USER_ID)), d.M_IDX
            FROM dbo.SYSDD d WITH (NOLOCK)
            WHERE ISNULL(d.REPORT_TAG, 0) = 1
            ORDER BY d.USER_ID, d.M_IDX;
            """, connection);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static (string UserId, int ModuleId)? FindDenied()
    {
        using var connection = new SqlConnection(RequireConnection());
        connection.Open();
        using var command = new SqlCommand("""
            SELECT TOP 1 LTRIM(RTRIM(d.USER_ID)), d.M_IDX
            FROM dbo.SYSDD d WITH (NOLOCK)
            WHERE ISNULL(d.REPORT_TAG, 0) = 0
            ORDER BY d.USER_ID, d.M_IDX;
            """, connection);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return (reader.GetString(0), reader.GetInt32(1));
    }

    private static string FirstReportOf(int moduleId)
    {
        using var connection = new SqlConnection(RequireConnection());
        connection.Open();
        using var command = new SqlCommand(
            "SELECT TOP 1 LTRIM(RTRIM(REPORT_ID)) FROM dbo.REPORT WITH (NOLOCK) WHERE M_IDX=@ModuleId ORDER BY REPORT_ID;",
            connection);
        command.Parameters.AddWithValue("@ModuleId", moduleId);
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    [Fact]
    public async Task 模块授予报表权限时_看打导出全放行且无报表级行过滤()
    {
        var granted = FindGranted();
        Assert.NotNull(granted);
        var reportId = FirstReportOf(granted!.Value.ModuleId);
        Assert.False(string.IsNullOrWhiteSpace(reportId), "该模块下没有报表，换一个组合再跑。");

        var rights = await CreateRepository().GetReportAsync(
            granted.Value.UserId, granted.Value.ModuleId, reportId, CancellationToken.None);

        Assert.True(rights.CanPreview, "模块授予报表权限即应可预览");
        Assert.True(rights.CanPrint, "模块授予报表权限即应可打印");
        Assert.True(rights.CanExport, "模块授予报表权限即应可导出");
        Assert.Equal(string.Empty, rights.DataFilter);
    }

    [Fact]
    public async Task 模块未授予报表权限时_三者全禁()
    {
        var denied = FindDenied();
        Assert.NotNull(denied);
        var reportId = FirstReportOf(denied!.Value.ModuleId);
        Assert.False(string.IsNullOrWhiteSpace(reportId), "该模块下没有报表，换一个组合再跑。");

        var rights = await CreateRepository().GetReportAsync(
            denied.Value.UserId, denied.Value.ModuleId, reportId, CancellationToken.None);

        Assert.False(rights.CanPreview);
        Assert.False(rights.CanPrint);
        Assert.False(rights.CanExport);
    }
}
