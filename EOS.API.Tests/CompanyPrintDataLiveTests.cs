using EOS.API.Data;
using EOS.API.Data.Reports;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 公司表数据源（110101 / <c>COMPANY</c>）真库用例：
/// 字段白名单、按显式编号读取（不回落）、报表绑定列的存在性/非空约束。
/// 只读取，不写入、不迁移；<c>MSSQL_ERP_CONN</c> 未设置时按既有集成测试约定直接返回。
/// </summary>
[Trait("Category", "Integration")]
public sealed class CompanyPrintDataLiveTests
{
    private static readonly Lazy<string?> ConnectionString =
        new(() => Environment.GetEnvironmentVariable("MSSQL_ERP_CONN"));

    private static SqlConnection? Open()
    {
        var connectionString = ConnectionString.Value;
        if (string.IsNullOrWhiteSpace(connectionString)) return null;
        var connection = new SqlConnection(connectionString);
        connection.Open();
        return connection;
    }

    [Fact]
    public async Task 字段白名单包含公司名与联系方式()
    {
        await using var connection = Open();
        if (connection is null) return;

        var fields = await CompanyPrintData.ReadFieldsAsync(connection, CancellationToken.None);
        var keys = fields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("COMPANY_ID", keys);
        Assert.Contains("NAME_CN", keys);
        Assert.Contains("NAME_EN", keys);
        // 页头常用的联系方式/地址：在 110101 的字段维护里应为可见物理列
        Assert.Contains("TEL", keys);
        Assert.All(fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Label)));
    }

    [Fact]
    public async Task 只按显式编号读取_编号为空或不存在时返回空()
    {
        await using var connection = Open();
        if (connection is null) return;

        var explicitId = await CompanyPrintData.ReadPreviewCompanyIdAsync(connection, 1405, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(explicitId));

        var hit = await CompanyPrintData.ReadAsync(connection, explicitId, CancellationToken.None);
        Assert.NotNull(hit);
        Assert.Equal(explicitId, hit!.CompanyId);
        Assert.True(hit.Fields.ContainsKey("NAME_CN"));

        // 不搞"找不到就换一家"：空编号与不存在的编号都必须返回 null
        Assert.Null(await CompanyPrintData.ReadAsync(connection, null, CancellationToken.None));
        Assert.Null(await CompanyPrintData.ReadAsync(connection, "  ", CancellationToken.None));
        Assert.Null(await CompanyPrintData.ReadAsync(
            connection, "NO_SUCH_COMPANY_FOR_TEST", CancellationToken.None));
    }

    [Fact]
    public async Task 报表绑定公司列非空且存量均落显式编号()
    {
        await using var connection = Open();
        if (connection is null) return;

        // 迁移 449：列必须存在、NOT NULL、带 DEFAULT 且禁止空串
        await using (var command = new SqlCommand("""
            SELECT c.is_nullable,
                   CASE WHEN dc.object_id IS NULL THEN 0 ELSE 1 END,
                   CASE WHEN ck.object_id IS NULL THEN 0 ELSE 1 END,
                   (SELECT COUNT(*) FROM dbo.REPORT WHERE COMPANY_ID IS NULL OR LTRIM(RTRIM(COMPANY_ID))=N'')
            FROM sys.columns c
            LEFT JOIN sys.default_constraints dc
                   ON dc.parent_object_id=c.object_id AND dc.parent_column_id=c.column_id
            LEFT JOIN sys.check_constraints ck
                   ON ck.parent_object_id=c.object_id AND ck.name=N'CK_REPORT_COMPANY_ID'
            WHERE c.object_id=OBJECT_ID(N'dbo.REPORT') AND c.name=N'COMPANY_ID';
            """, connection))
        {
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.False(reader.GetBoolean(0), "COMPANY_ID 必须 NOT NULL");
            Assert.Equal(1, Convert.ToInt32(reader.GetValue(1))); // 默认值约束存在
            Assert.Equal(1, Convert.ToInt32(reader.GetValue(2))); // 禁空 CHECK 存在
            Assert.Equal(0, Convert.ToInt32(reader.GetValue(3))); // 没有空值/空串行
        }

        var options = await CompanyPrintData.ReadOptionsAsync(connection, CancellationToken.None);
        Assert.NotEmpty(options);
        Assert.Contains(options, option => option.CompanyId == CompanyPrintData.DefaultCompanyId);
        Assert.All(options, option =>
        {
            Assert.False(string.IsNullOrWhiteSpace(option.CompanyId));
            Assert.False(string.IsNullOrWhiteSpace(option.NameCn));
        });
    }
}
