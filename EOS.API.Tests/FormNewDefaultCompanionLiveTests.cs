using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 新增态「复合格主字段默认值 → 同格从字段名称」回填的真库用例。
///
/// 主字段的默认值有两条来源：服务端生成（单别/单号/日期）与字段元数据 FIELDS.DFT_VALUE
/// （新增态由客户端本地套用，服务端不感知）。后者若不被服务端感知，同格的名称从字段就没有人
/// 解析——界面只出现代号（如库别 CP），名称空白。
///
/// 断言不写死名称：主字段默认值从定义里读，期望名称按该字段选择器的来源表与 RETURN_ITEMS
/// 回写映射查库取回，再与下发的新增态默认值比对。需要 MSSQL_ERP_CONN。
/// </summary>
[Collection("live-database")]
public sealed class FormNewDefaultCompanionLiveTests
{
    /// <summary>客户订单（1405）：库别是「代号 + 名称」复合格，且代号带元数据默认值。</summary>
    private const int ModuleId = 1405;
    private const string MainFieldKey = "DEPOT_ID";
    private const string CompanionFieldKey = "DEPOT_NAME";

    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

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
    public async Task NewFormDefaults_CarryCompanionNameForMetadataDefaultedMainField()
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

        var main = form!.MasterFields.FirstOrDefault(field => field.Key.Equals(MainFieldKey, StringComparison.OrdinalIgnoreCase));
        var companion = form.MasterFields.FirstOrDefault(field => field.Key.Equals(CompanionFieldKey, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(main);
        Assert.NotNull(companion);
        // 前提：这两个字段确实是同一格的复合格主/从，否则断言不到本机制
        Assert.True(main!.CellRole == 1 && companion!.CellRole == 2
            && string.Equals(main.CellGroup, companion.CellGroup, StringComparison.OrdinalIgnoreCase),
            $"{MainFieldKey}/{CompanionFieldKey} 已不是同格复合格（主/从），用例前提不成立。");
        Assert.False(string.IsNullOrWhiteSpace(main.DefaultValue), $"{MainFieldKey} 没有元数据默认值，用例前提不成立。");

        var (table, keyColumn, nameColumn) = ResolveChooserMapping(main, companion);
        var expected = await ReadSourceValueAsync(table, keyColumn, nameColumn, main.DefaultValue!);
        Assert.False(string.IsNullOrWhiteSpace(expected),
            $"来源表 {table} 查不到 {keyColumn}={main.DefaultValue} 的 {nameColumn}，用例前提不成立。");

        Assert.True(form.DefaultValues.TryGetValue(CompanionFieldKey, out var actual),
            $"新增态默认值缺 {CompanionFieldKey}：主字段 {MainFieldKey}={main.DefaultValue} 的伴生名称未回填。");
        Assert.Equal(expected, actual);
    }

    /// <summary>按主字段选择器的来源表与回归映射，取出「来源表 / 主键列 / 伴生名称列」。</summary>
    private static (string Table, string KeyColumn, string NameColumn) ResolveChooserMapping(
        FormFieldDefinition main, FormFieldDefinition companion)
    {
        var source = main.Choosers.FirstOrDefault(item => item.Active && !string.IsNullOrWhiteSpace(item.Table))
            ?? throw new InvalidOperationException($"{main.Key} 没有启用中的选择器来源，用例前提不成立。");
        var mapping = ChooserReturnItems.Parse(source.ReturnMapping)
            ?? throw new InvalidOperationException($"{main.Key} 的选择器回写映射不可解析，用例前提不成立。");
        var keyColumn = mapping.FirstOrDefault(pair =>
            string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target), main.Key, StringComparison.OrdinalIgnoreCase))?.Column
            ?? main.Key;
        var nameColumn = mapping.FirstOrDefault(pair =>
            string.Equals(FormFieldSelector.NormalizeChooserTarget(pair.Target), companion.Key, StringComparison.OrdinalIgnoreCase))?.Column
            ?? throw new InvalidOperationException($"{main.Key} 的选择器回写映射里没有 {companion.Key}，用例前提不成立。");
        return (source.Table!.Trim(), keyColumn, nameColumn);
    }

    private static async Task<string?> ReadSourceValueAsync(string table, string keyColumn, string nameColumn, string keyValue)
    {
        foreach (var identifier in new[] { table, keyColumn, nameColumn })
        {
            if (!IdentifierPattern.IsMatch(identifier))
            {
                throw new InvalidOperationException($"来源表/列名不是合法标识符：{identifier}。");
            }
        }
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        var sql = $"SELECT TOP 1 LTRIM(RTRIM([{nameColumn}])) FROM dbo.[{table}] WITH (NOLOCK) WHERE [{keyColumn}]=@Key;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 200).Value = keyValue;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
