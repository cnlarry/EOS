using System.Data;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 统一表单修改的**并发快照**验收（真库，模块 110306 仓库资料 / <c>DEPOT</c>）。
///
/// <para>
/// 现象：改仓库名称被 <c>CONCURRENT_MODIFIED</c>（"字段内容已被他人修改，请刷新后重试"）挡下，
/// 而这条记录并没有别人动过——老数据的字符列里存的是**空串**（不是 NULL）。读取侧把字符值统一
/// Trim 成空串，提交侧把空值解析成 null，两侧对"没有值"的表示不同，没被动过的列于是被判成被改过。
/// </para>
///
/// <para>
/// 用例夹具是自造库别 <c>ZZCM</c>（收尾删自己造的行），保存走 <c>dryRun</c>（真实事务内跑完整条
/// 路径后回滚），因此用例自身零落库。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class RecordEditConcurrencyLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 110306;
    private const string MasterTable = "DEPOT";
    /// <summary>用例自造库别；真实仓库资料的行值一律不参与写入。</summary>
    private const string TestDepot = "ZZCM";
    private const string TestDepotName = "ZZCM 并发用例仓";
    private const string TestUser = "ZZCM0001";
    private const string TestEmployee = "ZZCM 测试员";

    private DbConnectionFactory _connections = null!;

    public async Task InitializeAsync()
    {
        _connections = AssistantActionTestHarness.Connections(ConnectionString);
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await CleanupAsync(connection);
    }

    /// <summary>只删自造库别及其库位（<c>DEPOT_LOCATION</c> 外键指向库别）。</summary>
    private static async Task CleanupAsync(SqlConnection connection)
    {
        await ExecAsync(connection, "DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID = @id;", ("@id", TestDepot));
        await ExecAsync(connection, "DELETE FROM dbo.DEPOT WHERE DEPOT_ID = @id;", ("@id", TestDepot));
    }

    // ===== ① 库里是空串的列，不是"被他人修改" =====

    [Fact]
    public async Task 库内存空串的可写列_提交空值不算并发冲突()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAsync(connection);

        var handler = Handler();
        var definition = Definition();
        var form = Form();

        var read = await handler.GetRecordAsync(definition, form, [TestDepot], null, CancellationToken.None);
        Assert.Equal(RecordAccessStatus.Ok, read.Status);
        // 用例前提：这几列在库里是**空串**而不是 NULL（NULL 会读出 null），否则不构成这个缺陷
        Assert.Equal(string.Empty, read.Bundle!.Master["TEL"]);
        Assert.Equal(string.Empty, read.Bundle.Master["PRINCIPAL"]);
        Assert.Equal(string.Empty, read.Bundle.Master["REMARK"]);

        // 与前端同形：original = 打开表单时读到的快照，values = 改过之后的界面值
        var original = Snapshot(read.Bundle, form);
        var values = new Dictionary<string, string?>(original, StringComparer.OrdinalIgnoreCase)
        {
            ["DEPOT_NAME"] = "ZZCM 改名后的仓库",
        };

        var result = await handler.UpdateRecordAsync(definition, form, [TestDepot],
            new SaveRecordRequest(values, Original: original),
            TestEmployee, TestUser, null, CancellationToken.None, dryRun: true);

        Assert.True(result.Status == RecordAccessStatus.Ok,
            $"库内存空串的列把整单保存挡成了 {result.Status}："
            + string.Join('；', (result.FieldErrors ?? []).Select(error => $"{error.Field}={error.Code}")));
        // 预演零落库：夹具行的名称仍是种子值
        Assert.Equal(TestDepotName, await DepotNameAsync(connection));
    }

    // ===== ② 真被改过的字段仍必须报冲突（判别性：上面那条不是把闸门关掉）=====

    [Fact]
    public async Task 他人已改该字段_仍然报并发冲突()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAsync(connection);

        var handler = Handler();
        var definition = Definition();
        var form = Form();

        var read = await handler.GetRecordAsync(definition, form, [TestDepot], null, CancellationToken.None);
        Assert.Equal(RecordAccessStatus.Ok, read.Status);
        var original = Snapshot(read.Bundle!, form);
        var values = new Dictionary<string, string?>(original, StringComparer.OrdinalIgnoreCase)
        {
            ["DEPOT_NAME"] = "ZZCM 我改的名字",
        };

        // 客户端拿到快照之后，别人改了同一个字段
        await ExecAsync(connection, "UPDATE dbo.DEPOT SET DEPOT_NAME = N'ZZCM 他人改名' WHERE DEPOT_ID = @id;",
            ("@id", TestDepot));

        var result = await handler.UpdateRecordAsync(definition, form, [TestDepot],
            new SaveRecordRequest(values, Original: original),
            TestEmployee, TestUser, null, CancellationToken.None, dryRun: true);

        Assert.Equal(RecordAccessStatus.ConcurrentModified, result.Status);
        Assert.Contains(result.FieldErrors!, error => error.Field.Equals("DEPOT_NAME", StringComparison.OrdinalIgnoreCase)
            && error.Code == "CONCURRENT_MODIFIED");
        Assert.Equal("ZZCM 他人改名", await DepotNameAsync(connection));
    }

    // ===== 装配（与 Program.cs 同源：真实写管线 + 真库）=====

    private WorkbenchCommandHandler Handler() =>
        AssistantActionTestHarness.CommandHandler(
            _connections, ConnectionString, AssistantActionTestHarness.AuditWriter(_connections));

    private static WorkbenchDefinition Definition() => new(
        ModuleId: ModuleId, Title: "仓库资料", MasterTable: MasterTable, DetailTable: null,
        MasterFields: [], DetailFields: [], DefaultSort: null,
        HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["DEPOT_ID"], DetailNoFields: "", HasWorkflow: false,
        UserId: TestUser, ExecTag: "A",
        FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>与模块 110306 的编辑态表单同形：可写列就是这几个（归属/生命周期系统列在编辑态不进表单）。</summary>
    private static FormDefinition Form() => new(
        ModuleId, "仓库资料", MasterTable, null, true, true, "edit",
        [
            Field("DEPOT_ID", "库位", "nvarchar", primaryKey: true, required: true),
            Field("DEPOT_NAME", "库别", "nvarchar"),
            Field("MRP", "参与MRP运算", "bit"),
            Field("PRINCIPAL", "负责人", "nvarchar"),
            Field("TEL", "电话", "nvarchar"),
            Field("ADDRESS", "地址", "nvarchar"),
            Field("REMARK", "备注", "nvarchar"),
        ],
        [], ["DEPOT_ID"], string.Empty, string.Empty);

    private static FormFieldDefinition Field(
        string key, string label, string dataType, bool primaryKey = false, bool required = false) =>
        new(key, label, dataType, 100, null, required, null, null, null, false, true, false, false, null,
            [], primaryKey, false, false, false, false, false, null);

    /// <summary>前端 original 快照：只含可写字段（可见、非只读、非虚拟、非服务端填充），值原样回传、缺值即空串。</summary>
    private static Dictionary<string, string?> Snapshot(RecordBundle bundle, FormDefinition form)
    {
        var snapshot = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in form.MasterFields.Where(field => !field.IsReadonly && !field.IsVirtual && !field.ServerFilled))
        {
            snapshot[field.Key] = bundle.Master.TryGetValue(field.Key, out var value)
                ? value?.ToString() ?? string.Empty
                : string.Empty;
        }
        return snapshot;
    }

    // ===== 真库探针 =====

    /// <summary>种子：字符列**存空串**（老数据的普遍形态），这正是缺陷的触发条件。</summary>
    private static async Task SeedAsync(SqlConnection connection) =>
        await ExecAsync(connection, """
            INSERT INTO dbo.DEPOT
                (DEPOT_ID, DEPOT_NAME, TEL, ADDRESS, PRINCIPAL, REMARK, MRP,
                 CREATE_PERSON, CREATE_DATE, CONFIRM_TAG)
            VALUES (@id, @name, N'', N'', N'', N'', 1, N'ZZCM', SYSDATETIME(), 0);
            """, ("@id", TestDepot), ("@name", TestDepotName));

    private static Task<string?> DepotNameAsync(SqlConnection connection) =>
        ScalarAsync<string>(connection, "SELECT DEPOT_NAME FROM dbo.DEPOT WHERE DEPOT_ID = @id;", ("@id", TestDepot));

    private static async Task ExecAsync(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T?> ScalarAsync<T>(
        SqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = new SqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        var scalar = await command.ExecuteScalarAsync();
        return scalar is null or DBNull ? default : (T)Convert.ChangeType(scalar, typeof(T));
    }
}
