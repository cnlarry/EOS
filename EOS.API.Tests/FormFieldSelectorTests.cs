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
        bool canCopy = true,
        bool isPrimaryKey = false,
        IReadOnlyList<FormChooserRow>? choosers = null,
        int tabNo = 1,
        int? formOrder = null,
        int span = 1,
        bool newLine = false,
        string? cellGroup = null,
        int cellRole = 0,
        string? options = null,
        bool isPhysical = true) =>
        new(key, $"label-{key}", "nvarchar", 100, null, required, null, null, null,
            readOnly, visible, false, false, null, choosers ?? [], isVirtual, cost, secrecy, false, canCopy, isPrimaryKey, null,
            tabNo, formOrder, span, newLine, cellGroup, cellRole, options, isPhysical);

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
    public void Choosers_OnlyActiveWithTable_AndFilterPassesThrough()
    {
        var choosers = new List<FormChooserRow>
        {
            new(true, "PRODUCT", "产品", 1201, "PRO_NO=@1", "PRO_TYPE='1'"),
            new(false, "COLOR", null, null, null, null),
            new(true, "", null, null, null, null),
        };

        var field = Select([Row("P", choosers: choosers)]).Single();
        Assert.Single(field.Choosers);
        Assert.Equal("PRODUCT", field.Choosers[0].Table);
        Assert.Equal("PRO_TYPE='1'", field.Choosers[0].Filter); // CHOOSE_FILTER 受控传递，由服务端解析器校验
    }

    [Fact]
    public void AuditColumns_AreServerOwned()
    {
        var field = Select([Row("LAST_UPDATE_BY")]).Single();
        Assert.True(field.ServerFilled);
        Assert.True(field.IsReadonly);
    }

    [Fact]
    public void ParseOptions_SplitsKeyValueAndSkipsInvalid()
    {
        var options = FormFieldSelector.ParseOptions("O=外含税;I=内含税;;bad;N=不含税");
        Assert.Equal(3, options.Count);
        Assert.Equal("O", options[0].Value);
        Assert.Equal("外含税", options[0].Label);
        Assert.Empty(FormFieldSelector.ParseOptions(null));
        Assert.Empty(FormFieldSelector.ParseOptions(""));
    }

    [Fact]
    public void ParseReturnMapping_And_NormalizeTarget()
    {
        // 旧库 CHOOSE_RETURNVAL 约定逗号分隔（1082 个多对映射全为逗号），兼容分号
        var mapping = FormFieldSelector.ParseReturnMapping("txt_CLIENT_ID=client_id,txt_CLIENT_NAME=client_name;txt_SALES_ID=emp_id");
        Assert.Equal(3, mapping.Count);
        Assert.Equal("txt_CLIENT_ID", mapping[0].Target);
        Assert.Equal("client_id", mapping[0].Column);
        Assert.Equal("txt_CLIENT_NAME", mapping[1].Target);
        Assert.Equal("txt_SALES_ID", mapping[2].Target);
        Assert.Equal("CLIENT_ID", FormFieldSelector.NormalizeChooserTarget("txt_CLIENT_ID"));
        Assert.Equal("CLIENT_NAME", FormFieldSelector.NormalizeChooserTarget("txt_CLIENT_NAME"));
    }

    [Fact]
    public void PhantomCompanion_KeptDisplayOnly_ReadonlyVisible_NotServerFilled()
    {
        var field = Select([Row("CLIENT_NAME", visible: false, cellGroup: "CLIENT", cellRole: 2, isPhysical: false)]).Single();
        Assert.True(field.DisplayOnly);
        Assert.True(field.IsReadonly);
        Assert.True(field.IsVisible);
        Assert.False(field.ServerFilled);
        Assert.Equal("CLIENT", field.CellGroup);
        Assert.Equal(2, field.CellRole);
    }

    [Fact]
    public void PhysicalCompanion_NotDisplayOnly_KeepsEditableState()
    {
        var field = Select([Row("CURR_RATE", cellGroup: "CURR", cellRole: 2, isPhysical: true)]).Single();
        Assert.False(field.DisplayOnly);
        Assert.False(field.IsReadonly);
        Assert.True(field.IsVisible);
    }

    [Fact]
    public void LayoutAttributes_PassThrough()
    {
        var field = Select([Row("A", tabNo: 2, formOrder: 5, span: 2, newLine: true, options: "X=甲;Y=乙")]).Single();
        Assert.Equal(2, field.TabNo);
        Assert.Equal(5, field.FormOrder);
        Assert.Equal(2, field.Span);
        Assert.True(field.NewLine);
        Assert.Equal(2, field.Options?.Count);
        Assert.Equal("Y", field.Options?[1].Value);
    }

    [Fact]
    public void StatusTagFields_AreExcludedFromForm()
    {
        var result = Select([Row("CONFIRM_TAG"), Row("FINISHED_TAG"), Row("A")]);
        Assert.Equal(["A"], result.Select(field => field.Key));
    }
}
