using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class FormFieldSelectorTests
{
    private static FormFieldRow Row(
        string key = "F1",
        bool required = false,
        bool readOnly = false,
        bool visible = true,
        bool cost = false,
        bool secrecy = false,
        bool isVirtual = false,
        bool isPrimaryKey = false,
        IReadOnlyList<FormChooserRow>? choosers = null) =>
        new(key, $"label-{key}", "nvarchar", 100, null, required, null, null, null,
            readOnly, visible, false, false, null, choosers ?? [], isVirtual, cost, secrecy, false, isPrimaryKey, null);

    private static IReadOnlySet<string> Set(params string[] values) =>
        values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<FormFieldDefinition> Select(
        IReadOnlyList<FormFieldRow> rows,
        string mode = "new",
        bool canViewCost = false,
        bool canViewSecrecy = false,
        IReadOnlySet<string>? deniedView = null,
        IReadOnlySet<string>? deniedNew = null,
        IReadOnlySet<string>? deniedModi = null) =>
        FormFieldSelector.Select(rows, mode, canViewCost, canViewSecrecy,
            deniedView ?? Set(), deniedNew ?? Set(), deniedModi ?? Set());

    [Fact]
    public void CostAndSecrecyFields_FilteredByRights()
    {
        var rows = new[] { Row("A", cost: true), Row("B", secrecy: true), Row("C") };

        var without = Select(rows);
        Assert.Equal(["C"], without.Select(field => field.Key));

        var with = Select(rows, canViewCost: true, canViewSecrecy: true);
        Assert.Equal(3, with.Count);
    }

    [Fact]
    public void DeniedView_AlwaysRemoved()
    {
        var result = Select([Row("A"), Row("B")], deniedView: Set("A"));
        Assert.Equal(["B"], result.Select(field => field.Key));
    }

    [Fact]
    public void DenyNew_And_DenyModi_ApplyPerMode()
    {
        var rows = new[] { Row("A"), Row("B") };

        var newMode = Select(rows, "new", deniedNew: Set("A"));
        Assert.Equal(["B"], newMode.Select(field => field.Key));

        var editMode = Select(rows, "edit", deniedModi: Set("B"));
        Assert.Equal(["A"], editMode.Select(field => field.Key));
    }

    [Fact]
    public void VirtualField_KeptAndForcedReadonly()
    {
        var field = Select([Row("V", isVirtual: true)]).Single();
        Assert.True(field.IsVirtual);
        Assert.True(field.IsReadonly);
        Assert.False(field.ServerFilled);
    }

    [Fact]
    public void HiddenRequiredField_FlaggedServerFilled()
    {
        var field = Select([Row("H", required: true, visible: false)]).Single();
        Assert.True(field.ServerFilled);
        Assert.False(field.IsVisible);
    }

    [Fact]
    public void HiddenOptionalField_ExcludedFromForm()
    {
        var result = Select([Row("H", required: false, visible: false)]);
        Assert.Empty(result);
    }

    [Fact]
    public void VisibleReadonlyRequiredField_NotServerFilled_ForLinkedValue()
    {
        var field = Select([Row("R", required: true, readOnly: true)]).Single();
        // 必填但只读可见字段（如 CURR_RATE 汇率，前端联动带出）保留客户端提交，不误判服务端填充
        Assert.False(field.ServerFilled);
        Assert.True(field.IsReadonly);
    }

    [Fact]
    public void InputOrder_IsPreserved()
    {
        var rows = new[] { Row("C"), Row("A"), Row("B") };
        Assert.Equal(["C", "A", "B"], Select(rows).Select(field => field.Key));
    }

    [Fact]
    public void Choosers_OnlyActiveWithTable_AndFilterAlwaysNull()
    {
        var choosers = new List<FormChooserRow>
        {
            new(true, "PRODUCT", "产品", 1201, "PRO_NO=@1"),
            new(false, "COLOR", null, null, null),
            new(true, "", null, null, null),
        };

        var field = Select([Row("P", choosers: choosers)]).Single();
        Assert.Single(field.Choosers);
        Assert.Equal("PRODUCT", field.Choosers[0].Table);
        Assert.Null(field.Choosers[0].Filter); // 高危过滤表达式不返回普通用户
    }

    [Fact]
    public void AuditColumns_AreServerOwned()
    {
        var field = Select([Row("LAST_UPDATE_BY")]).Single();
        Assert.True(field.ServerFilled);
        Assert.True(field.IsReadonly);
    }
}
