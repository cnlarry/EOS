using EOS.API.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 明细项次（`SERIAL_NO`）必须始终进入明细表单定义。
///
/// 它是明细行的身份而不是普通列：保存管线靠它给新行编号、下游单据靠"单号 + 项次"引用行。
/// 它是否进表单**不能**取决于用户的「选择列」——一旦被隐藏，表单定义里就没有这个字段，
/// 服务端既无法分配项次、INSERT 也会漏列（被翻译成 `REQUIRED_FIELD_MISSING`），
/// 用户看到的是"该字段不能为空"却找不到可填的格子。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class WorkbenchDetailSerialColumnLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    [Fact]
    public async Task Detail_Form_Always_Contains_Server_Filled_Serial_No()
    {
        var connections = PolicyServiceFactory.Connections(ConnectionString);
        var provider = new WorkbenchDefinitionProvider(
            connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var builder = new WorkbenchDefinitionBuilder(
            connections, provider, TestFormCapability.None,
            TestBusinessConfig.Reader(connections), NullLogger<WorkbenchDefinitionBuilder>.Instance);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1404 客户报价：明细是主从表，默认列配置里没有 SERIAL_NO，正是这条缺陷的现场。
        var definition = await builder.GetDefinitionAsync(
            1404, "admin", "Z", true, true, empty, empty, CancellationToken.None);
        Assert.NotNull(definition);

        var form = await builder.GetFormDefinitionAsync(
            definition!, "admin", "new", true, true,
            empty, empty, empty, empty, empty, empty, CancellationToken.None);
        Assert.NotNull(form);
        Assert.NotEmpty(form!.DetailFields);

        var serial = Assert.Single(
            form.DetailFields,
            field => field.Key.Equals("SERIAL_NO", StringComparison.OrdinalIgnoreCase));
        Assert.True(serial.ServerFilled, "SERIAL_NO 必须由服务端分配，客户端不可提交。");
        Assert.True(serial.IsReadonly, "SERIAL_NO 在界面上只能只读展示。");
    }
}
