using EOS.API.Data;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 单据读取时"哪些虚拟列要连表取数"的挑选口径：定义侧与表单侧的交集。
/// 定义侧已经按 QUERY_RELATION 白名单解析过一遍，解析不了的虚拟列压根不在其中；
/// 表单侧决定用户到底把哪些排进了版式——两边都认的才值得拼进 SQL。
/// </summary>
public sealed class WorkbenchCommandHandlerVirtualFieldTests
{
    private static WorkbenchField Field(string key, bool isVirtual, string? expression = null)
        => new(key, key, "nvarchar", 100, null, IsPrimaryKey: false, IsVisible: true,
            IsVirtual: isVirtual, VirtualExpression: expression);

    private static FormFieldDefinition FormField(string key, bool isVirtual)
        => new(key, key, "nvarchar", 100, null, IsRequired: false, VerifyIndex: null, Regex: null, DefaultValue: null,
            IsReadonly: isVirtual, IsVisible: true, OnlyChoose: false, ChooseMultiple: false, ChoosePage: null,
            Choosers: [], IsPrimaryKey: false, IsAutoIncrement: false, IsVirtual: isVirtual, IsCost: false,
            IsSecrecy: false, ServerFilled: false, MaxLength: null);

    [Fact]
    public void KeepsOnlyVirtualFieldsThatBothSidesKnow()
    {
        var definitionFields = new List<WorkbenchField>
        {
            Field("ORDER_NO", isVirtual: false),
            Field("CLIENT_NAME", isVirtual: true, "CLIENT.CLIENT_NAME"),
            Field("SALES_NAME", isVirtual: true, "SYSDN.EMP_NAME"),
            // 定义侧有、但没排进这张表单：不该跟着查
            Field("PRO_NAME", isVirtual: true, "PRODUCT.PRO_NAME"),
        };
        var formFields = new List<FormFieldDefinition>
        {
            FormField("ORDER_NO", isVirtual: false),
            FormField("CLIENT_NAME", isVirtual: true),
            FormField("SALES_NAME", isVirtual: true),
        };

        var result = WorkbenchCommandHandler.VirtualFieldsFor(definitionFields, formFields);

        Assert.Equal(["CLIENT_NAME", "SALES_NAME"], result.Select(field => field.Key).ToArray());
    }

    [Fact]
    public void NoVirtualFieldsInForm_ReturnsEmpty()
    {
        var definitionFields = new List<WorkbenchField> { Field("CLIENT_NAME", isVirtual: true, "CLIENT.CLIENT_NAME") };
        var formFields = new List<FormFieldDefinition> { FormField("ORDER_NO", isVirtual: false) };

        Assert.Empty(WorkbenchCommandHandler.VirtualFieldsFor(definitionFields, formFields));
    }

    [Fact]
    public void VirtualFieldMissingFromDefinition_IsSkipped()
    {
        // 解析不了（不在 QUERY_RELATION 白名单）的虚拟列不会出现在定义里：表单排了也取不到值
        var definitionFields = new List<WorkbenchField> { Field("ORDER_NO", isVirtual: false) };
        var formFields = new List<FormFieldDefinition>
        {
            FormField("ORDER_NO", isVirtual: false),
            FormField("UNRESOLVABLE_NAME", isVirtual: true),
        };

        Assert.Empty(WorkbenchCommandHandler.VirtualFieldsFor(definitionFields, formFields));
    }
}
