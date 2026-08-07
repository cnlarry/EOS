using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class FormDefaultRulesTests
{
    private static FormFieldDefinition Field(string key, string dataType = "nvarchar", bool readOnly = false, bool serverFilled = false) =>
        new(key, $"label-{key}", dataType, 100, null, false, null, null, null,
            readOnly, true, false, false, null, [], false, false, false, false, false, serverFilled, null);

    [Fact]
    public void RegisteredModuleDefault_IsApplied()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        FormDefaultRules.Apply(1405, [Field("REBATE", "float")], values);
        Assert.Equal(100.0, values["REBATE"]);
    }

    [Fact]
    public void UnknownModule_HasNoDefaults()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        FormDefaultRules.Apply(1501, [Field("REMARK")], values);
        Assert.Empty(values);
    }

    [Fact]
    public void ExistingValue_IsNotOverwritten()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["REBATE"] = 88.0 };
        FormDefaultRules.Apply(1405, [Field("REBATE", "float")], values);
        Assert.Equal(88.0, values["REBATE"]);
    }

    [Fact]
    public void ReadonlyOrServerFilledFields_AreSkipped()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        FormDefaultRules.Apply(1405, [Field("REBATE", "float", readOnly: true)], values);
        Assert.Empty(values);
    }

    [Fact]
    public void TodayToken_AppliesDateOnly()
    {
        var rules = new Dictionary<string, string> { ["DATE_FIELD"] = "@today", ["TEXT_FIELD"] = "@today" };
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        FormDefaultRules.Apply(9999, [Field("DATE_FIELD", "datetime"), Field("TEXT_FIELD", "nvarchar")], values, rules);
        Assert.Equal(DateTime.Today, values["DATE_FIELD"]);
        Assert.False(values.ContainsKey("TEXT_FIELD"));
    }
}
