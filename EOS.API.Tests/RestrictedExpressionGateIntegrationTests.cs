using System.Data;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 受控表达式保存路径集成测试（直连 EOS.ERP 开发库，拿不到连接串时跳过）：
/// ① 虚拟表达式只能落在虚拟字段（IS_VIRTUAL=1）上，非虚拟字段一律拒绝；
/// ② 表达式随字段一起保存，空值 = 清空，保存时由服务端受控校验，不通过整单拒绝；
/// ③ 转换函数对物理字段依然有效（未被形态门误拦）。
/// 写入用例只落在用例自造的 FIELDS 行上（用完即删），借真实字段的取样仅用于只读校验。
/// </summary>
[Trait("Category", "Integration")]
public sealed class RestrictedExpressionGateIntegrationTests
{
    /// <summary>写入用例的夹具表名：不存在的物理表，表达式解析必然失败，门一坏也写不进任何业务字段。</summary>
    private const string GateTable = "ZZ_EXPRESSION_GATE_TEST";
    private const string GateField = "GATE_COL";

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private readonly DbConnectionFactory _connections;
    private readonly RestrictedExpressionService _service;
    private readonly FieldAdminRepository _repository;

    public RestrictedExpressionGateIntegrationTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
            })
            .Build();
        _connections = new DbConnectionFactory(config);
        _service = new RestrictedExpressionService(_connections);
        _repository = new FieldAdminRepository(
            _connections,
            new WorkbenchDirtyMarker(_connections),
            new WorkbenchAuditWriter(_connections, new Microsoft.AspNetCore.Http.HttpContextAccessor(),
                new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
                Microsoft.Extensions.Options.Options.Create(new AuditSettings())),
            _service,
            NullLogger<FieldAdminRepository>.Instance);
    }

    [Fact]
    public async Task VirtualExpression_OnPhysicalField_Rejected()
    {
        if (ConnectionString.Value is null) return;
        var target = await ReadPhysicalFieldAsync();
        Assert.NotNull(target);
        var (table, field) = target!.Value;

        var validation = await _service.ValidateAsync(
            RestrictedExpressionKind.VirtualExp, table, field, $"{table}.{field}", CancellationToken.None);
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, error => error.Contains("不是虚拟字段"));
    }

    [Fact]
    public async Task EmptyExpression_IsAcceptedAsClear()
    {
        if (ConnectionString.Value is null) return;
        var target = await ReadPhysicalFieldAsync();
        Assert.NotNull(target);
        var (table, field) = target!.Value;

        var virtualResult = await _service.ValidateAsync(RestrictedExpressionKind.VirtualExp, table, field, "", CancellationToken.None);
        Assert.True(virtualResult.Ok, string.Join("；", virtualResult.Errors));

        var convertResult = await _service.ValidateAsync(RestrictedExpressionKind.ConvertFunction, table, field, "  ", CancellationToken.None);
        Assert.True(convertResult.Ok, string.Join("；", convertResult.Errors));
    }

    [Fact]
    public async Task ConvertFunction_OnPhysicalField_StillAccepted()
    {
        if (ConnectionString.Value is null) return;
        var target = await ReadPhysicalFieldAsync();
        Assert.NotNull(target);
        var (table, field) = target!.Value;

        var validation = await _service.ValidateAsync(
            RestrictedExpressionKind.ConvertFunction, table, field, "f_get_emp_name_by_id", CancellationToken.None);
        Assert.True(validation.Ok, string.Join("；", validation.Errors));

        var unknown = await _service.ValidateAsync(
            RestrictedExpressionKind.ConvertFunction, table, field, "f_not_registered", CancellationToken.None);
        Assert.False(unknown.Ok);
        Assert.Contains(unknown.Errors, error => error.Contains("不在受控注册表内"));
    }

    [Fact]
    public async Task VirtualExpression_OnVirtualField_PassesFieldShapeGate()
    {
        if (ConnectionString.Value is null) return;
        var target = await ReadVirtualFieldAsync();
        if (target is null) return;
        var (table, field, expression) = target.Value;

        var validation = await _service.ValidateAsync(
            RestrictedExpressionKind.VirtualExp, table, field, expression, CancellationToken.None);
        Assert.DoesNotContain(validation.Errors, error => error.Contains("不是虚拟字段"));
    }

    [Fact]
    public async Task VirtualExpression_OnUnknownField_Rejected()
    {
        if (ConnectionString.Value is null) return;
        var validation = await _service.ValidateAsync(
            RestrictedExpressionKind.VirtualExp, "NO_SUCH_TABLE_FOR_GATE", "NO_SUCH_FIELD", "X.Y", CancellationToken.None);
        Assert.False(validation.Ok);
        Assert.Contains(validation.Errors, error => error.Contains("元数据不存在"));
    }

    [Fact]
    public async Task Save_RejectsInvalidExpressionAndPersistsValidConvertFunction()
    {
        if (ConnectionString.Value is null) return;
        await CreateGateFieldRowAsync();
        try
        {
            // ① 非虚拟字段携带虚拟表达式：整单拒绝，且不落库
            var rejected = await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpdateAsync(
                GateTable, GateField, FixtureInput(virtualExpression: $"{GateTable}.{GateField}"), null, "integration-test", CancellationToken.None));
            Assert.Contains("不是虚拟字段", rejected.Message);
            Assert.Equal(string.Empty, await ReadExpressionAsync(GateTable, GateField, "VIRTUAL_EXP"));

            // ② 未登记的函数：整单拒绝
            var badFunction = await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpdateAsync(
                GateTable, GateField, FixtureInput(convertFunction: "f_not_registered"), null, "integration-test", CancellationToken.None));
            Assert.Contains("不在受控注册表内", badFunction.Message);

            // ③ 注册表内的转换函数：随字段保存落库
            await _repository.UpdateAsync(
                GateTable, GateField, FixtureInput(convertFunction: "f_get_emp_name_by_id"), null, "integration-test", CancellationToken.None);
            Assert.Equal("f_get_emp_name_by_id", await ReadExpressionAsync(GateTable, GateField, "CONVERT_FUNCTION"));

            // ④ 空值 = 清空
            await _repository.UpdateAsync(
                GateTable, GateField, FixtureInput(convertFunction: ""), null, "integration-test", CancellationToken.None);
            Assert.Equal(string.Empty, await ReadExpressionAsync(GateTable, GateField, "CONVERT_FUNCTION"));
        }
        finally
        {
            await DeleteGateFieldRowAsync();
        }
    }

    private static FieldAdminInput FixtureInput(string? virtualExpression = null, string? convertFunction = null) =>
        new("受控表达式用例夹具", "nvarchar", 100, "left", "center", null,
            true, true, true, false, false, false, false, null, null, null, null, null, null,
            false, false, null, [], true, null, virtualExpression, convertFunction);

    /// <summary>取一个物理列字段（IS_VIRTUAL=0、无表达式、物理列存在）作为「非虚拟字段」只读样本。</summary>
    private async Task<(string Table, string Field)?> ReadPhysicalFieldAsync()
    {
        const string sql = """
            SELECT TOP 1 LTRIM(RTRIM(f.T_ID)), LTRIM(RTRIM(f.F_ID))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE COALESCE(f.IS_VIRTUAL,0)=0
              AND LTRIM(RTRIM(ISNULL(f.VIRTUAL_EXP,'')))=''
              AND EXISTS (SELECT 1 FROM sys.columns c
                          JOIN sys.objects o ON c.object_id=o.object_id AND o.type IN ('U','V')
                          JOIN sys.schemas s ON o.schema_id=s.schema_id
                          WHERE s.name=N'dbo' AND o.name=f.T_ID AND c.name=LTRIM(RTRIM(f.F_ID)))
            ORDER BY f.T_ID, f.F_ID;
            """;
        return await ReadPairAsync(sql);
    }

    /// <summary>取一个已配表达式的虚拟字段作为「虚拟字段」只读样本。</summary>
    private async Task<(string Table, string Field, string Expression)?> ReadVirtualFieldAsync()
    {
        const string sql = """
            SELECT TOP 1 LTRIM(RTRIM(f.T_ID)), LTRIM(RTRIM(f.F_ID)), LTRIM(RTRIM(f.VIRTUAL_EXP))
            FROM dbo.FIELDS f WITH (NOLOCK)
            WHERE COALESCE(f.IS_VIRTUAL,0)=1
              AND LTRIM(RTRIM(ISNULL(f.VIRTUAL_EXP,'')))<>''
            ORDER BY f.T_ID, f.F_ID;
            """;
        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? (reader.GetString(0), reader.GetString(1), reader.GetString(2))
            : null;
    }

    private async Task<(string Table, string Field)?> ReadPairAsync(string sql)
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private async Task<string> ReadExpressionAsync(string table, string field, string column)
    {
        // column 来自本文件的闭式清单（VIRTUAL_EXP / CONVERT_FUNCTION），不是外部输入
        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"SELECT LTRIM(RTRIM(ISNULL({column},''))) FROM dbo.FIELDS WITH (NOLOCK) WHERE LTRIM(RTRIM(T_ID))=@Table AND LTRIM(RTRIM(F_ID))=@Field;",
            connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = field;
        return (await command.ExecuteScalarAsync() as string ?? string.Empty).Trim();
    }

    /// <summary>造一行非虚拟字段元数据（物理列不存在，表达式解析必失败），写入用例只用这一行。</summary>
    private async Task CreateGateFieldRowAsync()
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            DELETE FROM dbo.FIELDS WHERE T_ID=@Table AND F_ID=@Field;
            INSERT INTO dbo.FIELDS
                (T_ID,F_ID,F_DESC,F_TYPE,IS_QUERY,IS_DEFAULT_FIELDS,IS_VISIBLE,IS_VIRTUAL,IS_COST,IS_SECRECY,
                 IS_READONLY,CAN_COPY,DISPLAY_LENGTH,LAST_UPDATE_BY,LAST_UPDATE_DATE)
            VALUES
                (@Table,@Field,N'受控表达式用例夹具',N'nvarchar',1,1,1,0,0,0,0,1,100,N'integration-test',GETDATE());
            """, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = GateTable;
        command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = GateField;
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteGateFieldRowAsync()
    {
        await using var connection = _connections.Create();
        await connection.OpenAsync();
        await using var command = new SqlCommand("DELETE FROM dbo.FIELDS WHERE T_ID=@Table AND F_ID=@Field;", connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = GateTable;
        command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = GateField;
        await command.ExecuteNonQueryAsync();
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
