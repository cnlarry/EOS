using System.Globalization;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 新增态日期默认值的真库用例：元数据默认值里的日期哨兵（<c>FIELDS.DFT_VALUE='D'</c>）必须在下发给
/// 客户端之前展开成当天。
///
/// 客户端只会把默认值**原样套用**，它不认识哨兵。若哨兵原样下发，客户端会把它当普通日期值提交，
/// 保存侧因无法转换为日期而整单拒绝——表现为"什么都没改，一点保存就报格式错"。
///
/// 用例取员工资料（180102）的转正日期 ON_DUTY_DATE：全库唯一一个以哨兵作默认值的字段。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class FormNewDefaultDateSentinelLiveTests
{
    private const int ModuleId = 180102;
    private const string FieldKey = "ON_DUTY_DATE";

    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private static DbConnectionFactory Connections()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString,
            })
            .Build();
        return new DbConnectionFactory(config);
    }

    [Fact]
    public async Task NewFormDefaults_ExpandDateSentinelIntoAParseableDate()
    {
        var connections = Connections();
        var provider = new WorkbenchDefinitionProvider(connections, NullLogger<WorkbenchDefinitionProvider>.Instance);
        var builder = new WorkbenchDefinitionBuilder(connections, provider,
            Options.Create(new UnifiedFormEditorSettings()), NullLogger<WorkbenchDefinitionBuilder>.Instance);
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var definition = await builder.GetDefinitionAsync(ModuleId, "admin", "Z", true, true,
            empty, empty, CancellationToken.None, forPublish: true);
        Assert.NotNull(definition);
        var form = await builder.GetFormDefinitionAsync(definition!, "admin", "new", true, true,
            empty, empty, empty, empty, empty, empty, CancellationToken.None);
        Assert.NotNull(form);

        var field = form!.MasterFields.FirstOrDefault(item => item.Key.Equals(FieldKey, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(field);
        // 前提：该字段确实带日期哨兵默认值，否则本用例断言不到这条机制
        Assert.Equal("D", field!.DefaultValue?.Trim(), ignoreCase: true);

        Assert.True(form.DefaultValues.TryGetValue(FieldKey, out var actual),
            $"新增态默认值缺 {FieldKey}：日期哨兵未被展开，客户端会原样提交 'D' 并被保存侧拒绝。");
        Assert.True(DateTime.TryParseExact(actual, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsed),
            $"{FieldKey} 的新增态默认值不是可解析日期：{actual}。");
        Assert.Equal(DateTime.Today, parsed);
    }
}
