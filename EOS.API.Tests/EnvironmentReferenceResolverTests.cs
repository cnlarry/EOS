using EOS.API.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 配置值里的 ${VAR} 环境变量引用解析：已定义即替换、未定义按空值并记问题、非引用值原样不动。
/// </summary>
public class EnvironmentReferenceResolverTests
{
    private static IConfiguration Build(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    [Fact]
    public void Reference_to_defined_variable_is_replaced()
    {
        var config = Build(("Assistant:ApiKey", "${TEST_KEY}"));
        var issues = new List<string>();

        var resolved = EnvironmentReferenceResolver.Resolve(
            config, issues, name => name == "TEST_KEY" ? "secret-value" : null);

        Assert.Equal("secret-value", resolved["Assistant:ApiKey"]);
        Assert.Empty(issues);
    }

    [Fact]
    public void Multiple_references_in_one_value_are_all_replaced()
    {
        var config = Build(("Demo:Path", "${ROOT}/sub/${NAME}"));
        var issues = new List<string>();

        var resolved = EnvironmentReferenceResolver.Resolve(config, issues, name => name switch
        {
            "ROOT" => "C:",
            "NAME" => "eos",
            _ => null,
        });

        Assert.Equal("C:/sub/eos", resolved["Demo:Path"]);
        Assert.Empty(issues);
    }

    [Fact]
    public void Undefined_reference_becomes_empty_and_records_issue()
    {
        var config = Build(("ConnectionStrings:ErpDatabase", "Server=x;${NO_SUCH_VAR}"));
        var issues = new List<string>();

        var resolved = EnvironmentReferenceResolver.Resolve(config, issues, _ => null);

        Assert.Equal("Server=x;", resolved["ConnectionStrings:ErpDatabase"]);
        var issue = Assert.Single(issues);
        Assert.Contains("NO_SUCH_VAR", issue);
        Assert.Contains("ConnectionStrings:ErpDatabase", issue);
    }

    [Fact]
    public void Values_without_reference_are_untouched()
    {
        var config = Build(("Workflow:OverdueDays", "3"), ("AllowedHosts", "*"));
        var issues = new List<string>();

        var resolved = EnvironmentReferenceResolver.Resolve(config, issues, _ => "x");

        Assert.Empty(resolved);
        Assert.Empty(issues);
    }

    [Fact]
    public void Malformed_reference_is_left_as_is()
    {
        var config = Build(("Demo:Value", "${1BAD} ${} ${OK}"));
        var issues = new List<string>();

        var resolved = EnvironmentReferenceResolver.Resolve(
            config, issues, name => name == "OK" ? "v" : null);

        Assert.Equal("${1BAD} ${} v", resolved["Demo:Value"]);
    }

    [Fact]
    public void Empty_values_are_skipped()
    {
        var config = Build(("Demo:Null", null), ("Demo:Blank", ""));
        var issues = new List<string>();

        var resolved = EnvironmentReferenceResolver.Resolve(config, issues, _ => "x");

        Assert.Empty(resolved);
        Assert.Empty(issues);
    }
}
