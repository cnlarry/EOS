using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 报表定制（2201）读取路径的真库校验。
///
/// 为什么需要这条：`REPORT` 的 SELECT 列表是按位置喂给 `ReportAdminDraft` 的，列序错位不会编译报错，
/// 只在读取那一刻把文本列当布尔读（`InvalidCastException`）或把两个字段对调，端点直接 500。
/// 新增列（如报表专有版式 `FORMAT_ID`）时最容易发生，因此按"逐列与库内值对应"钉住。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class ReportAdminRepositoryLiveTests
{
    private static readonly string? ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");

    private static ReportAdminRepository CreateRepository(string connectionString) =>
        new(new DbConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = connectionString,
            })
            .Build()));

    private sealed record ReportRow(string FooterText, bool IsDefault, string ReportFilter, string Remark, string FormatId);

    [Fact]
    public async Task 报表列表读取与_REPORT_表逐列对应()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var token = CancellationToken.None;
        var repository = CreateRepository(ConnectionString);

        var all = await repository.ListAllReportsAsync(token);
        Assert.NotEmpty(all);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var expected = new Dictionary<string, ReportRow>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand(
            """
            SELECT LTRIM(RTRIM(REPORT_ID)), LTRIM(RTRIM(ISNULL(FOOTER_TEXT,''))), ISNULL(IS_DEFAULT,0),
                   LTRIM(RTRIM(ISNULL(REPORT_FILTER,''))), LTRIM(RTRIM(ISNULL(REMARK,''))),
                   LTRIM(RTRIM(ISNULL(FORMAT_ID,'')))
            FROM dbo.REPORT WITH (NOLOCK);
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                expected[reader.GetString(0)] = new ReportRow(
                    reader.GetString(1), reader.GetBoolean(2), reader.GetString(3), reader.GetString(4), reader.GetString(5));
            }
        }

        Assert.Equal(expected.Count, all.Count);
        foreach (var draft in all)
        {
            Assert.True(expected.TryGetValue(draft.ReportId, out var row), $"报表 {draft.ReportId} 不在 REPORT 表中。");
            Assert.Equal(row.FooterText, draft.FooterText ?? string.Empty);
            Assert.Equal(row.IsDefault, draft.IsDefault);
            Assert.Equal(row.ReportFilter, draft.ReportFilter ?? string.Empty);
            Assert.Equal(row.Remark, draft.Remark ?? string.Empty);
            Assert.Equal(row.FormatId, draft.FormatId ?? string.Empty);
        }
    }

    [Fact]
    public async Task 按模块取报表同样逐列对应()
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        var token = CancellationToken.None;
        var repository = CreateRepository(ConnectionString);

        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        var moduleId = 0;
        await using (var command = new SqlCommand(
            "SELECT TOP 1 M_IDX FROM dbo.REPORT WITH (NOLOCK) ORDER BY M_IDX;", connection))
        {
            var value = await command.ExecuteScalarAsync(token);
            Assert.False(value is null or DBNull);
            moduleId = Convert.ToInt32(value);
        }

        var rows = await repository.ListReportsAsync(moduleId, token);
        Assert.NotEmpty(rows);
        Assert.All(rows, draft => Assert.Equal(moduleId, draft.ModuleId));
        Assert.All(rows, draft => Assert.False(string.IsNullOrWhiteSpace(draft.ReportId)));
    }
}
