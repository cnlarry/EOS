using System.Text.Json;
using EOS.API.Data.ValidationRules;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// Fail-closed parameter schema checks for the validation-template registry: unknown
/// keys, missing mandatory fields and wrong value shapes are rejected per template.
/// </summary>
public class ValidationRuleRegistryFailClosedTests
{
    private static JsonElement Params(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static List<string> Validate(string key, JsonElement parameters)
    {
        var issues = new List<string>();
        ValidationRuleRegistry.Validate(
            new ValidationRuleConfig("rule-1", key, "APPROVE", true, null, parameters), issues);
        return issues;
    }

    [Fact]
    public void DuplicateCheck_EntityMode_RequiresTable()
    {
        var issues = Validate("duplicate-check", Params("""
            {"mode":"entity","keyFields":["EMP_NO"],"excludeSelf":{"keyFields":["EMP_ID"],"source":{"scope":"MASTER","fields":["EMP_ID"]}}}
            """));
        Assert.Contains(issues, issue => issue.Contains("table 必填"));
    }

    [Fact]
    public void DuplicateCheck_EntityMode_AcceptsTable()
    {
        var issues = Validate("duplicate-check", Params("""
            {"mode":"entity","table":"HR_EMPLOYEE","keyFields":["EMP_NO"],"excludeSelf":{"keyFields":["EMP_ID"],"source":{"scope":"MASTER","fields":["EMP_ID"]}}}
            """));
        Assert.DoesNotContain(issues, issue => issue.Contains("table 必填"));
    }

    [Fact]
    public void QuantityCheck_UnknownParameterKey_IsRejected()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","typo":1,"checks":[{"match":[{"target":"A","source":{"scope":"DETAIL","field":"B"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["X"]},
             "limit":{"scope":"TARGET","fields":["Y"]}}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("未知参数键 params.typo"));
    }

    [Fact]
    public void QuantityCheck_NotBelowProgress_DoesNotRequireLimit()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"not-below-progress","checks":[{"match":[{"target":"A","source":{"scope":"DETAIL","field":"B"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["X"]}}]}
            """));
        Assert.DoesNotContain(issues, issue => issue.Contains("limit 缺失"));
        Assert.DoesNotContain(issues, issue => issue.Contains("未知参数键"));
    }

    [Fact]
    public void QuantityCheck_UsageNotExceed_StillRequiresLimit()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"match":[{"target":"A","source":{"scope":"DETAIL","field":"B"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["X"]}}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("limit 缺失"));
    }

    [Fact]
    public void ReferenceExists_RejectsInvalidAllowEmptyAndActiveTag()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"PRODUCT","refKey":{"scope":"MASTER","field":"PRO_NO"},
             "allowEmpty":"yes","activeTag":{"expect":1}}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("allowEmpty 必须是布尔值"));
        Assert.Contains(issues, issue => issue.Contains("activeTag.field 不能为空"));
    }

    [Fact]
    public void MatchItem_RejectsUnknownSourceKeys()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"PRODUCT","join":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO","hint":"x"}}]}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("未知参数键 checks[0].join[].source.hint"));
    }
}
