using EOS.API.Data;
using EOS.API.Models;
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
        string? options = null,
        bool isPhysical = true) =>
        new(key, $"label-{key}", "nvarchar", 100, null, required, null, null, null,
            readOnly, visible, false, false, null, choosers ?? [], isVirtual, cost, secrecy, false, canCopy, isPrimaryKey, null,
            options, isPhysical);

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
            new(true, "PRODUCT", "产品", 1201, "PRO_NO=@1", "PRO_TYPE='1'", 2),
            new(false, "COLOR", null, null, null, null),
            new(true, "", null, null, null, null),
        };

        var field = Select([Row("P", choosers: choosers)]).Single();
        Assert.Single(field.Choosers);
        Assert.Equal("PRODUCT", field.Choosers[0].Table);
        // FILTER_STRUCT is only visible in field settings; not sent to regular users in the form definition
        // (server picks the authoritative source by SerialNo)
        Assert.Null(field.Choosers[0].Filter);
        Assert.Equal("PRO_NO=@1", field.Choosers[0].ReturnMapping);
        Assert.Equal(2, field.Choosers[0].SerialNo);
    }

    [Fact]
    public void AuditColumns_AreServerOwned()
    {
        var field = Select([Row("LAST_UPDATE_BY")], "view").Single();
        Assert.True(field.ServerFilled);
        Assert.True(field.IsReadonly);
    }

    [Fact]
    public void LifecycleActorColumns_HiddenInNewAndEdit_ShownReadonlyInView()
    {
        var rows = new[] { Row("CREATE_PERSON"), Row("CONFIRM_DATE"), Row("FINISHED_PERSON"), Row("A") };
        Assert.Equal(["A"], Select(rows, "new").Select(field => field.Key));
        Assert.Equal(["A"], Select(rows, "edit").Select(field => field.Key));
        var viewed = Select(rows, "view");
        Assert.Equal(4, viewed.Count);
        foreach (var field in viewed.Where(field => field.Key != "A"))
        {
            Assert.True(field.IsReadonly);
        }
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

    /// <summary>
    /// 标签以 `!` 结尾 = 可见但不可选：未实现的档位要看得见、选不了（隐藏会让人以为能力不存在，
    /// 可选则会让配置进得去、运行期没人读）。
    /// </summary>
    [Fact]
    public void ParseOptions_TrailingBangMarksOptionDisabled()
    {
        var options = FormFieldSelector.ParseOptions("0=不管;1=可填;2=建议;3=必填+效期!");

        Assert.Equal(4, options.Count);
        Assert.False(options[0].Disabled);
        Assert.True(options[3].Disabled);
        Assert.Equal("必填+效期", options[3].Label);
        Assert.Equal("3", options[3].Value);
    }

    [Fact]
    public void ParseOptions_LoneBangIsSkipped()
    {
        // 只有标记没有标签 ⇒ 空标签，按既有容错口径跳过（不产生"看得见但没名字"的选项）
        Assert.Empty(FormFieldSelector.ParseOptions("3=!"));
    }

    [Fact]
    public void NormalizeChooserTarget_StripsControlPrefixes()
    {
        Assert.Equal("CLIENT_ID", FormFieldSelector.NormalizeChooserTarget("txt_CLIENT_ID"));
        Assert.Equal("CLIENT_NAME", FormFieldSelector.NormalizeChooserTarget("txt_CLIENT_NAME"));
        Assert.Equal("SALES_ID", FormFieldSelector.NormalizeChooserTarget("dro_SALES_ID"));
        Assert.Equal("VALUE", FormFieldSelector.NormalizeChooserTarget("VALUE"));
    }

    [Fact]
    public void HiddenNonRequiredCompanion_IsDroppedFromForm()
    {
        // 隐藏的非必填字段（含无物理列的幽灵从字段）一律不进表单：字段级 FORM_CELL_ROLE 退役后，
        // 本阶段已判不出"复合格从字段"，版式只能对已在字段集里的列做排布（只做减法）。
        Assert.Empty(Select([Row("CLIENT_NAME", visible: false, isPhysical: false)]));
    }

    [Fact]
    public void VisibleCompanion_IsReadonlyVisibleAndNotServerFilled()
    {
        var field = Select([Row("CURR_RATE")]).Single();
        Assert.False(field.DisplayOnly);
        Assert.False(field.IsReadonly);
        Assert.True(field.IsVisible);
        Assert.False(field.ServerFilled);
    }

    [Fact]
    public void Select_LeavesLayoutAttributesAtDefaults_AndParsesOptions()
    {
        var field = Select([Row("A", options: "X=甲;Y=乙")]).Single();
        Assert.Equal(2, field.Options?.Count);
        Assert.Equal("Y", field.Options?[1].Value);
        // 版面占位（页签 / 顺序 / 跨度 / 换行 / 复合格角色）由模块级版式在 Select 之后施加
        Assert.Equal(1, field.TabNo);
        Assert.Null(field.FormOrder);
        Assert.Equal(1, field.Span);
        Assert.False(field.NewLine);
        Assert.Null(field.CellGroup);
        Assert.Equal(0, field.CellRole);
    }

    [Fact]
    public void StatusTagFields_AreExcludedFromForm()
    {
        var result = Select([Row("CONFIRM_TAG"), Row("FINISHED_TAG"), Row("A")]);
        Assert.Equal(["A"], result.Select(field => field.Key));
    }

    [Fact]
    public void StatusTagFields_ShownReadonlyInView()
    {
        var viewed = Select([Row("CONFIRM_TAG"), Row("FINISHED_TAG")], "view");
        Assert.Equal(2, viewed.Count);
        foreach (var field in viewed)
        {
            Assert.True(field.IsReadonly);
        }
    }

    [Fact]
    public void OwnershipColumns_HiddenInNewAndEdit_ShownReadonlyInView()
    {
        var rows = new[] { Row("CI"), Row("OWNER"), Row("OWNER_G"), Row("A") };
        Assert.Equal(["A"], Select(rows, "new").Select(field => field.Key));
        Assert.Equal(["A"], Select(rows, "edit").Select(field => field.Key));
        var viewed = Select(rows, "view");
        Assert.Equal(4, viewed.Count);
        foreach (var field in viewed.Where(field => field.Key != "A"))
        {
            Assert.True(field.IsReadonly);
        }
    }
}
