using System.Data;
using System.Globalization;
using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;
using Xunit.Abstractions;

namespace EOS.API.Tests;

/// <summary>
/// 单据自动编号集成测试（直连 EOS.ERP）：独立发号器的并发唯一性、与旧"最大号 +1"算法的
/// 等价性，以及迁移 098 的幂等性。
/// 连接串来自 env MSSQL_ERP_CONN；拿不到连接串时整类测试空跑跳过。
/// 前置：执行迁移 098（BILL_NO_SEQUENCE）建表并回填计数器——只跑这一条迁移，
/// 不触发其它待落地迁移（它们的落地时机由各自负责人决定）。
/// </summary>
[Trait("Category", "Integration")]
public sealed class BillNoGeneratorIntegrationTests
{
    private const string MigrationFile = "098_bill_no_sequence.sql";

    /// <summary>并发用例使用的合成单别：只在测试期间存在，结束即删。</summary>
    private const string SyntheticBillCode = "ZZSEQ";
    private const int SyntheticModuleId = 999901;
    private const string SyntheticExpression = "ZZSEQ{YYMMDD}0000";

    private static readonly Lazy<string?> ConnectionString = new(() =>
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN"));

    private readonly ITestOutputHelper _output;

    public BillNoGeneratorIntegrationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task 并发取号_同一单别互不重复()
    {
        const int parallelism = 32;
        var connectionString = ConnectionString.Value;
        if (connectionString is null) return;

        await EnsureSequenceTableAsync();
        await ResetSyntheticRuleAsync(connectionString);
        try
        {
            var tasks = Enumerable.Range(0, parallelism).Select(async _ =>
            {
                await using var connection = new SqlConnection(connectionString);
                await connection.OpenAsync();
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
                var billNo = await BillNoGenerator.GenerateAsync(connection, transaction, SyntheticModuleId, CancellationToken.None);
                await transaction.CommitAsync();
                return billNo;
            });

            var numbers = await Task.WhenAll(tasks);
            _output.WriteLine("并发 {0} 路取号结果：{1}", parallelism, string.Join(", ", numbers.OrderBy(value => value, StringComparer.Ordinal)));

            Assert.All(numbers, number => Assert.False(string.IsNullOrWhiteSpace(number)));
            Assert.Equal(parallelism, numbers.Distinct(StringComparer.Ordinal).Count());

            var serials = numbers
                .Select(number => long.Parse(number![^4..], CultureInfo.InvariantCulture))
                .OrderBy(value => value)
                .ToArray();
            Assert.Equal(Enumerable.Range(1, parallelism).Select(value => (long)value), serials);

            var prefix = BillNoGenerator.Parse(SyntheticExpression, DateTime.Now).Title;
            Assert.All(numbers, number => Assert.StartsWith(prefix, number!, StringComparison.Ordinal));
        }
        finally
        {
            await DropSyntheticRuleAsync(connectionString);
        }
    }

    [Fact]
    public async Task 预览取号不消耗流水()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null) return;

        await EnsureSequenceTableAsync();
        await ResetSyntheticRuleAsync(connectionString);
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();

            var first = await BillNoGenerator.PeekAsync(connection, null, SyntheticModuleId, CancellationToken.None);
            var second = await BillNoGenerator.PeekAsync(connection, null, SyntheticModuleId, CancellationToken.None);
            Assert.Equal(first, second);

            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
            var taken = await BillNoGenerator.GenerateAsync(connection, transaction, SyntheticModuleId, CancellationToken.None);
            await transaction.CommitAsync();
            Assert.Equal(first, taken);

            var after = await BillNoGenerator.PeekAsync(connection, null, SyntheticModuleId, CancellationToken.None);
            Assert.NotEqual(taken, after);
        }
        finally
        {
            await DropSyntheticRuleAsync(connectionString);
        }
    }

    [Theory]
    [InlineData(1404, "COP_QUOTE_M", "QUOTE_NO", "QUOTE_TYPE")]
    [InlineData(1406, "COP_SEND_M", "SEND_NO", "SEND_TYPE")]
    [InlineData(1606, "PUR_PURCHASE_M", "PURCHASE_NO", "PURCHASE_TYPE")]
    [InlineData(1607, "PUR_RECEIVE_M", "RECEIVE_NO", "RECEIVE_TYPE")]
    [InlineData(1615, "PUR_APPLY_M", "APPLY_NO", "APPLY_TYPE")]
    public async Task 既有自动编号模块_取号结果与旧最大号算法一致(int moduleId, string masterTable, string billNoField, string billTypeField)
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null) return;

        await EnsureSequenceTableAsync();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        var billCode = await BillNoGenerator.GetDefaultBillCodeAsync(connection, null, moduleId, CancellationToken.None);
        Assert.False(string.IsNullOrWhiteSpace(billCode));

        var expression = await LoadExpressionAsync(connection, billCode!);
        Assert.False(string.IsNullOrWhiteSpace(expression));

        var template = BillNoGenerator.Parse(expression!, DateTime.Now);
        var expected = await LegacyNextNoAsync(connection, masterTable, billNoField, billTypeField, billCode!, template);
        var actual = await BillNoGenerator.PeekAsync(connection, null, moduleId, CancellationToken.None);
        _output.WriteLine("模块 {0}（{1}，单别 {2}）：既有算法 {3} / 新发号器 {4}",
            moduleId, masterTable, billCode, expected, actual);

        Assert.Equal(template.Title, actual[..template.Title.Length]);
        Assert.True(actual.Length == expected.Length, $"号长应与既有算法一致：旧 {expected} / 新 {actual}");
        // 迁移期这里曾要求与"旧最大号算法"逐字一致——该等式只在 098 回填刚完成时成立：
        // 发号器允许跳号（取号后单据被删、事务回滚，号码作废不回收），所以真实号只会 ≥ 既有算法结果。
        // 断言不变式：同字头与日期段、同宽度，且不小于既有算法（绝不回头撞已用号）。
        var expectedSerial = long.Parse(expected[template.Title.Length..], CultureInfo.InvariantCulture);
        var actualSerial = long.Parse(actual[template.Title.Length..], CultureInfo.InvariantCulture);
        Assert.True(actualSerial >= expectedSerial,
            $"发号器不得落后于既有算法（旧 {expected} / 新 {actual}）；大于属允许的跳号");
    }

    [Fact]
    public async Task 迁移重复执行_计数器不回退也不重复建行()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null) return;

        await EnsureSequenceTableAsync();
        var before = await SnapshotCountersAsync(connectionString);
        await ExecuteMigrationAsync(connectionString);
        var after = await SnapshotCountersAsync(connectionString);

        Assert.NotEmpty(before);
        Assert.Equal(before, after);
    }

    // ---------- 支撑方法 ----------

    private static async Task<string?> LoadExpressionAsync(SqlConnection connection, string billCode)
    {
        const string sql = """
            SELECT TOP 1 LTRIM(RTRIM(USED_BILL_NO))
            FROM dbo.BILLKIND WITH (NOLOCK)
            WHERE BILL_CODE = @BillCode AND IS_AUTO = 1;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@BillCode", System.Data.SqlDbType.NVarChar, 20).Value = billCode;
        return await command.ExecuteScalarAsync() as string;
    }

    /// <summary>既有算法：主表内同单别 + 同字头下的最大单号 + 1（切换前的取号口径）。</summary>
    private static async Task<string> LegacyNextNoAsync(
        SqlConnection connection, string masterTable, string billNoField, string billTypeField,
        string billCode, BillNoGenerator.Template template)
    {
        var sql = $"""
            SELECT MAX(LTRIM(RTRIM([{billNoField}])))
            FROM dbo.[{masterTable}] WITH (NOLOCK)
            WHERE [{billTypeField}] = @BillCode AND [{billNoField}] LIKE @Prefix + '%';
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@BillCode", System.Data.SqlDbType.NVarChar, 20).Value = billCode;
        command.Parameters.Add("@Prefix", System.Data.SqlDbType.NVarChar, 100).Value = template.Title;
        var maxNo = await command.ExecuteScalarAsync() as string;

        var serial = 1L;
        if (!string.IsNullOrWhiteSpace(maxNo) && maxNo!.Length > template.Title.Length)
        {
            var tail = maxNo[template.Title.Length..];
            if (long.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                serial = parsed + 1;
            }
        }
        return template.Title + serial.ToString(CultureInfo.InvariantCulture).PadLeft(template.Width, '0');
    }

    private static async Task ResetSyntheticRuleAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        const string sql = """
            DELETE dbo.BILL_NO_SEQUENCE WHERE BILL_CODE = @BillCode;
            DELETE dbo.BILLKIND WHERE BILL_CODE = @BillCode;
            INSERT dbo.BILLKIND (BILL_CODE, BILL_NAME, B_M_IDX, IS_AUTO, IS_DEFAULT, USED_BILL_NO)
            VALUES (@BillCode, N'自动编号并发用例', @ModuleId, 1, 1, @Expression);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@BillCode", System.Data.SqlDbType.NVarChar, 20).Value = SyntheticBillCode;
        command.Parameters.Add("@ModuleId", System.Data.SqlDbType.Int).Value = SyntheticModuleId;
        command.Parameters.Add("@Expression", System.Data.SqlDbType.NVarChar, 100).Value = SyntheticExpression;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropSyntheticRuleAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        const string sql = """
            DELETE dbo.BILLKIND WHERE BILL_CODE = @BillCode;
            DELETE dbo.BILL_NO_SEQUENCE WHERE BILL_CODE = @BillCode;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@BillCode", System.Data.SqlDbType.NVarChar, 20).Value = SyntheticBillCode;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> SnapshotCountersAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        const string sql = """
            SELECT BILL_CODE + '|' + PERIOD_KEY + '|' + CAST(CURRENT_NO AS NVARCHAR(20))
            FROM dbo.BILL_NO_SEQUENCE;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    /// <summary>建表 + 回填计数器：执行迁移 098（内嵌资源），重复执行幂等。</summary>
    private static async Task EnsureSequenceTableAsync()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null) return;

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var probe = new SqlCommand(
            "SELECT 1 WHERE OBJECT_ID(N'dbo.BILL_NO_SEQUENCE', N'U') IS NOT NULL;", connection);
        if (await probe.ExecuteScalarAsync() is null)
        {
            await ExecuteMigrationAsync(connectionString);
        }
    }

    private static async Task ExecuteMigrationAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(LoadMigrationSql(), connection)
        {
            CommandTimeout = 300,
        };
        await command.ExecuteNonQueryAsync();
    }

    private static string LoadMigrationSql()
    {
        var assembly = typeof(ErpDatabaseInitializer).Assembly;
        var resource = assembly.GetManifestResourceNames()
            .First(name => name.EndsWith(MigrationFile, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"迁移资源缺失：{MigrationFile}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
