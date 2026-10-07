using System.Data;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 主档编号（主表主键）不可改的真库验收（模块 110306 仓库资料 / <c>DEPOT</c>）。
///
/// <para>
/// 三件事逐项成立：① 编辑态的表单定义把主键标只读、新增态保持可填（手填编号的主档要靠新增建档）；
/// ② 绕过界面直接提交"改过的编号"仍被写路径拒（<c>PRIMARY_KEY_IMMUTABLE</c>，字段级错误）——
/// 只读是体验，边界在服务端；③ 只挡编号本身，改名称照常保存。
/// </para>
///
/// <para>
/// 夹具是自造库别 <c>ZZPKR</c>（收尾删自己造的行），保存走 <c>dryRun</c>（真实事务内跑完整条路径后回滚），
/// 因此用例自身零落库。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class FormPrimaryKeyReadonlyLiveTests : IAsyncLifetime
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 110306;
    private const string MasterTable = "DEPOT";
    /// <summary>用例自造库别；真实仓库资料的行值一律不参与写入。</summary>
    private const string TestDepot = "ZZPKR";
    private const string TestDepotName = "ZZPKR 编号只读用例仓";
    private const string TestEmployee = "ZZPKR 测试员";

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

    private static async Task CleanupAsync(SqlConnection connection)
    {
        await ExecAsync(connection, "DELETE FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID = @id;", ("@id", TestDepot));
        await ExecAsync(connection, "DELETE FROM dbo.DEPOT WHERE DEPOT_ID = @id;", ("@id", TestDepot));
    }

    // ===== ① 定义口径：编辑态只读、新增态可填 =====

    [Fact]
    public async Task 编辑态主键只读_新增态保持可填()
    {
        var builder = Builder();
        var definition = Definition();

        var edit = await builder.GetFormDefinitionAsync(definition, "ADMIN", "edit", true, true,
            Empty, Empty, Empty, Empty, Empty, Empty, CancellationToken.None);
        var created = await builder.GetFormDefinitionAsync(definition, "ADMIN", "new", true, true,
            Empty, Empty, Empty, Empty, Empty, Empty, CancellationToken.None);

        Assert.NotNull(edit);
        Assert.NotNull(created);

        var editKey = edit!.MasterFields.Single(field => field.Key == "DEPOT_ID");
        Assert.True(editKey.IsPrimaryKey);
        Assert.True(editKey.IsReadonly);
        // 只读 ≠ 服务端填充：编号仍要显示在表单上（灰显），值也仍由客户端原样回传
        Assert.False(editKey.ServerFilled);

        // 手填编号的主档必须在新增态填得出编号
        Assert.False(created!.MasterFields.Single(field => field.Key == "DEPOT_ID").IsReadonly);

        // 只读的是主键，其它字段照旧可改
        Assert.False(edit.MasterFields.Single(field => field.Key == "DEPOT_NAME").IsReadonly);
    }

    // ===== ②③ 写路径：改编号被拒（零落库）、改名称照常保存（dryRun 回滚） =====

    [Fact]
    public async Task 改编号被拒_改名称照常保存_且零落库()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await SeedAsync(connection);

        var builder = Builder();
        var definition = Definition();
        var form = await builder.GetFormDefinitionAsync(definition, "ADMIN", "edit", true, true,
            Empty, Empty, Empty, Empty, Empty, Empty, CancellationToken.None);
        Assert.NotNull(form);
        var handler = AssistantActionTestHarness.CommandHandler(
            _connections, ConnectionString, AssistantActionTestHarness.AuditWriter(_connections));

        // ① 改编号：值不等于记录键即拒。只读且必填的主键在校验层是放行的（那是给只读联动列留的口子），
        //    所以这一条只能由主键专用闸拦——不拦就会被"跳过主键列"的写入静默丢掉，用户以为改成了。
        var renamedKey = await handler.UpdateRecordAsync(definition, form!, [TestDepot],
            new SaveRecordRequest(new Dictionary<string, string?>
            {
                ["DEPOT_ID"] = "ZZPKR2",
                ["DEPOT_NAME"] = "ZZPKR 改编号的尝试",
            }),
            TestEmployee, "ADMIN", null, CancellationToken.None, dryRun: true);

        Assert.Equal(RecordAccessStatus.ValidationFailed, renamedKey.Status);
        Assert.Equal(RecordPayloadValidator.ImmutableKeyCode, renamedKey.ErrorCode);
        var fieldError = Assert.Single(renamedKey.FieldErrors!, error => error.Field == "DEPOT_ID");
        Assert.Equal(RecordPayloadValidator.ImmutableKeyCode, fieldError.Code);
        Assert.Equal(TestDepotName, await DepotNameAsync(connection));

        // ② 不改编号（原样回传）只改名称：照常保存
        var renamed = await handler.UpdateRecordAsync(definition, form!, [TestDepot],
            new SaveRecordRequest(new Dictionary<string, string?>
            {
                ["DEPOT_ID"] = TestDepot,
                ["DEPOT_NAME"] = "ZZPKR 改名后",
            }),
            TestEmployee, "ADMIN", null, CancellationToken.None, dryRun: true);

        Assert.True(renamed.Status == RecordAccessStatus.Ok,
            $"只改名称也存不进去：{renamed.Status} {renamed.ErrorCode}");
        // 预演零落库：名称仍是种子值
        Assert.Equal(TestDepotName, await DepotNameAsync(connection));
    }

    // ===== 装配（与 Program.cs 同源：真实定义构建 + 真实写管线 + 真库） =====

    private WorkbenchDefinitionBuilder Builder() =>
        new(_connections,
            new WorkbenchDefinitionProvider(_connections, NullLogger<WorkbenchDefinitionProvider>.Instance),
            Options.Create(new UnifiedFormEditorSettings { EnabledModuleIds = [ModuleId] }),
            NullLogger<WorkbenchDefinitionBuilder>.Instance);

    private static IReadOnlySet<string> Empty => new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static WorkbenchDefinition Definition() => new(
        ModuleId: ModuleId, Title: "仓库资料", MasterTable: MasterTable, DetailTable: null,
        MasterFields: [], DetailFields: [], DefaultSort: null,
        HasAdd: true, HasEdit: true, DetailNoSave: false,
        MasterPkOrder: ["DEPOT_ID"], DetailNoFields: "", HasWorkflow: false,
        UserId: "ADMIN", ExecTag: "A",
        FilterFieldKeys: new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static async Task SeedAsync(SqlConnection connection) =>
        await ExecAsync(connection, """
            INSERT INTO dbo.DEPOT
                (DEPOT_ID, DEPOT_NAME, TEL, ADDRESS, PRINCIPAL, REMARK, MRP, CI, OWNER, OWNER_G,
                 CREATE_PERSON, CREATE_DATE, CONFIRM_TAG)
            VALUES (@id, @name, N'', N'', N'', N'', 1, N'DEFAULT', N'', N'', N'ZZPKR', SYSDATETIME(), 0);
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
