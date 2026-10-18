using System.Data;
using EOS.API.Data;
using EOS.API.Data.Bom;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 产品 BOM「新建版本」的真库验证（端点背后的 `BomRepository.CreateNewVersionAsync`）。
///
/// 断言四件事：
///   ① 版次由服务端算（`MAX+1`），调用方只说"复制哪一版"；
///   ② 明细整棵复制，项次原样保留（两版才能逐行对比）；
///   ③ 状态重置：确认位归零、制单人换成执行者（新版本是新记录，不是旧记录的延续）；
///   ④ 版次到 99 就拒绝，不偷偷进位成三位（那会让 MAX(EDITION) 的字典序重新失效）。
///
/// 造数**提交**而不是事务回滚：仓储自己开连接并提交（它是写路径，不是查询），
/// 读不到未提交的数据。所以用独立前缀 + finally 清理。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class BomNewVersionLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string Root = "ADR39NVROOT";
    private const string Child = "ADR39NVCHILD";
    private const string Leaf = "ADR39NVLEAF";
    private const string Edition = "01";
    private const string Executor = "ADR39NV";

    private static BomRepository CreateRepository() =>
        new(new DbConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString,
            })
            .Build()));

    [Fact]
    public async Task 新建版本_复制结构与明细到下一版次并重置状态()
    {
        var token = CancellationToken.None;
        await CleanupAsync(token);
        try
        {
            await SeedAsync(token);
            var next = await CreateRepository().CreateNewVersionAsync(Root, Edition, Executor, token);

            Assert.Equal("02", next);

            var editions = await ReadAsync(
                "SELECT LTRIM(RTRIM(EDITION)) FROM dbo.BOM_STRU_M WHERE PRO_NO=@pro ORDER BY EDITION;", token);
            Assert.Equal([Edition, "02"], editions);

            var detail = await ReadAsync(
                """
                SELECT LTRIM(RTRIM(EDITION)) + '|' + CONVERT(nvarchar(10), SERIAL_NO) + '|' + LTRIM(RTRIM(ELEMENT_PRO_NO))
                FROM dbo.BOM_STRU_D WHERE PRO_NO=@pro AND EDITION=N'02' ORDER BY SERIAL_NO;
                """, token);
            Assert.Equal([$"02|1|{Child}", $"02|2|{Leaf}"], detail);

            var state = await ReadAsync(
                """
                SELECT LTRIM(RTRIM(CREATE_PERSON)) + '|' + CONVERT(nvarchar(2), CONFIRM_TAG)
                     + '|' + CASE WHEN LAST_UPDATE_DATE IS NULL THEN 'NULL' ELSE 'SET' END
                     + '|' + LTRIM(RTRIM(ISNULL(LAST_UPDATE_BY, N'')))
                FROM dbo.BOM_STRU_M WHERE PRO_NO=@pro AND EDITION=N'02';
                """, token);
            // 新版次是刚写入的记录：最后修改人与时间必须同步落地，
            // 否则列表的「最后修改」列会空着（实测踩到）。
            Assert.Equal([$"{Executor}|0|SET|{Executor}"], state);
        }
        finally
        {
            await CleanupAsync(token);
        }
    }

    [Fact]
    public async Task 新建版本_连续两次得到03而不是覆盖02()
    {
        var token = CancellationToken.None;
        await CleanupAsync(token);
        try
        {
            await SeedAsync(token);
            var repository = CreateRepository();
            Assert.Equal("02", await repository.CreateNewVersionAsync(Root, Edition, Executor, token));
            Assert.Equal("03", await repository.CreateNewVersionAsync(Root, "02", Executor, token));

            var editions = await ReadAsync(
                "SELECT LTRIM(RTRIM(EDITION)) FROM dbo.BOM_STRU_M WHERE PRO_NO=@pro ORDER BY EDITION;", token);
            Assert.Equal([Edition, "02", "03"], editions);
        }
        finally
        {
            await CleanupAsync(token);
        }
    }

    [Fact]
    public async Task 新建版本_版次到99时拒绝()
    {
        var token = CancellationToken.None;
        await CleanupAsync(token);
        try
        {
            await SeedAsync(token, edition: "99");
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateRepository().CreateNewVersionAsync(Root, "99", Executor, token));
            Assert.Contains("两位数字", error.Message);
        }
        finally
        {
            await CleanupAsync(token);
        }
    }

    private static async Task SeedAsync(CancellationToken token, string edition = Edition)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);

        foreach (var (no, name) in new[] { (Root, "根"), (Child, "半成品"), (Leaf, "原料") })
        {
            await ExecAsync(connection,
                "INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME, UNIT_ID) VALUES (@no, @name, N'PCS');",
                token, ("@no", no), ("@name", name));
        }
        await ExecAsync(connection,
            "INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION, BATCH_QTY, ATTENTION) VALUES (@pro, @edition, 100, N'注意');",
            token, ("@pro", Root), ("@edition", edition));
        await ExecAsync(connection,
            """
            INSERT INTO dbo.BOM_STRU_D (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_QTY, BASE_QTY, LOST_RATE)
            VALUES (@pro, @edition, 1, @child, 2, 1, 10),
                   (@pro, @edition, 2, @leaf, 3, 1, 0);
            """,
            token, ("@pro", Root), ("@edition", edition), ("@child", Child), ("@leaf", Leaf));
    }

    /// <summary>按前缀清理：先明细后主表再产品。</summary>
    private static async Task CleanupAsync(CancellationToken token)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await ExecAsync(connection, "DELETE FROM dbo.BOM_STRU_D WHERE PRO_NO=@pro;", token, ("@pro", Root));
        await ExecAsync(connection, "DELETE FROM dbo.BOM_STRU_M WHERE PRO_NO=@pro;", token, ("@pro", Root));
        await ExecAsync(connection,
            "DELETE FROM dbo.PRODUCT WHERE PRO_NO IN (@root, @child, @leaf);",
            token, ("@root", Root), ("@child", Child), ("@leaf", Leaf));
    }

    private static async Task ExecAsync(
        SqlConnection connection, string sql, CancellationToken token, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<List<string>> ReadAsync(string sql, CancellationToken token)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@pro", SqlDbType.NVarChar, 60).Value = Root;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0).ToString()!.Trim());
        }
        return rows;
    }
}
