using EOS.API.Data.ValidationRules;
using Xunit;

namespace EOS.API.Tests;

public class ValidationRuleLoaderTests
{
    [Fact]
    public void Load_Valid1607Rule_ReturnsSuccess()
    {
        const string json = """
        {
          "moduleId": 1607,
          "validationRules": [
            {
              "ruleId": "v1607-receive-not-exceed",
              "validationKey": "qty-not-exceed",
              "stage": "APPROVE",
              "params": {
                "mode": "usage-not-exceed",
                "checks": [
                  {
                    "match": [
                      { "target": "PURCHASE_TYPE", "source": { "scope": "DETAIL", "field": "PURCHASE_TYPE" } },
                      { "target": "PURCHASE_NO", "source": { "scope": "DETAIL", "field": "PURCHASE_NO" } },
                      { "target": "SERIAL_NO", "source": { "scope": "DETAIL", "field": "PURCHASE_SERIAL_NO" } }
                    ],
                    "thisQty": { "scope": "DETAIL", "terms": [ { "field": "QTY", "coef": 1 } ] },
                    "usage": { "scope": "TARGET", "fields": [ "RECEIVE_QTY" ] },
                    "limit": { "scope": "TARGET", "fields": [ "QTY" ] }
                  }
                ]
              }
            }
          ]
        }
        """;
        var result = ModuleValidationDefinitionLoader.Load(json);
        Assert.True(result.Success);
        Assert.Single(result.Rules);
    }

    [Fact]
    public void Load_InvalidCoefAndStage_ReturnsIssues()
    {
        const string json = """
        {
          "validationRules": [
            {
              "ruleId": "bad",
              "validationKey": "qty-not-exceed",
              "stage": "AFTER_SAVE",
              "params": {
                "mode": "usage-not-exceed",
                "checks": [
                  {
                    "match": [ { "target": "A", "source": { "scope": "DETAIL", "field": "B" } } ],
                    "thisQty": { "scope": "DETAIL", "terms": [ { "field": "QTY", "coef": 2 } ] },
                    "usage": { "scope": "TARGET", "fields": [ "X" ] },
                    "limit": { "scope": "TARGET", "fields": [ "Y" ] }
                  }
                ]
              }
            }
          ]
        }
        """;
        var result = ModuleValidationDefinitionLoader.Load(json);
        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Contains("stage"));
        Assert.Contains(result.Issues, issue => issue.Contains("coef"));
    }

    [Fact]
    public void Load_UnknownKeyAndDuplicateMissingMode_ReturnsIssues()
    {
        const string json = """
        {
          "validationRules": [
            { "ruleId": "u1", "validationKey": "no-such-template", "stage": "SAVE", "params": {} },
            { "ruleId": "u2", "validationKey": "duplicate-check", "stage": "SAVE", "params": { "keyFields": [ "EMP_ID" ] } }
          ]
        }
        """;
        var result = ModuleValidationDefinitionLoader.Load(json);
        Assert.False(result.Success);
        Assert.Contains(result.Issues, issue => issue.Contains("no-such-template"));
        Assert.Contains(result.Issues, issue => issue.Contains("mode"));
    }
}
