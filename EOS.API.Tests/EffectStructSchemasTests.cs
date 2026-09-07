using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class EffectStructSchemasTests
{
    [Fact]
    public void ValidateReverse_AcceptsKnownKinds_AndRejectsUnknown()
    {
        Assert.Empty(EffectStructSchemas.ValidateReverse("""{"kind":"auto-reverse","note":"数量 -=；日期不回退"}"""));
        Assert.Empty(EffectStructSchemas.ValidateReverse("""{"kind":"reverse-flow"}"""));

        var issues = EffectStructSchemas.ValidateReverse("""{"kind":"magic","extra":1}""");
        Assert.Contains(issues, issue => issue.Contains("kind 'magic'"));
        Assert.Contains(issues, issue => issue.Contains("未登记键 'extra'"));
    }

    [Fact]
    public void ValidateParams_InventoryFieldMap_QtyExpressionRejected()
    {
        var issues = EffectStructSchemas.ValidateParams(
            "inventory-move",
            """{"direction":"IN","mrp":false,"fieldMap":{"masterDate":"OCCUR_DATE","qty":"QTY+SPARE_QTY","detail":["PRO_NO"]}}""");

        Assert.Contains(issues, issue => issue.Contains("fieldMap.qty"));
        Assert.Contains(issues, issue => issue.Contains("QTY+SPARE_QTY"));
    }

    [Fact]
    public void ValidateParams_InventoryFieldMap_TermsObjectAccepted()
    {
        var issues = EffectStructSchemas.ValidateParams(
            "inventory-move",
            """{"direction":"IN","mrp":false,"fieldMap":{"masterDate":"OCCUR_DATE","qty":{"terms":[{"field":"QTY","coef":1},{"field":"SPARE_QTY","coef":1}]},"detail":["SERIAL_NO","PRO_NO"]}}""");

        Assert.Empty(issues);
    }

    [Fact]
    public void ValidateParams_InventoryRowFilter_Accepted()
    {
        var issues = EffectStructSchemas.ValidateParams(
            "inventory-move",
            """{"direction":"IN","mrp":false,"fieldMap":{"masterDate":"RETURN_DATE","qty":{"terms":[{"field":"BAD_QTY","coef":1}]},"detail":["SERIAL_NO"]},"rowFilter":{"anyPositive":["BAD_QTY","BAD_SPARE_QTY"]}}""");
        Assert.Empty(issues);
    }

    [Fact]
    public void ValidateParams_InventoryRowFilter_RejectsUnknownKeyAndEmpty()
    {
        var issues = EffectStructSchemas.ValidateParams(
            "inventory-move",
            """{"direction":"IN","mrp":false,"fieldMap":{"masterDate":"RETURN_DATE","qty":"QTY","detail":[]},"rowFilter":{"anyPositive":[],"other":1}}""");
        Assert.Contains(issues, issue => issue.Contains("rowFilter 含未登记键 'other'"));
        Assert.Contains(issues, issue => issue.Contains("anyPositive 必须是非空"));
    }

    [Fact]
    public void ValidateParams_UnknownRootKeyAndReservedKeyWithParams_Rejected()
    {
        var issues = EffectStructSchemas.ValidateParams(
            "inventory-move",
            """{"direction":"IN","freeText":"anything"}""");
        Assert.Contains(issues, issue => issue.Contains("未登记根键 'freeText'"));

        var reserved = EffectStructSchemas.ValidateParams("meta-link", """{"a":1}""");
        Assert.Contains(reserved, issue => issue.Contains("尚未登记参数 Schema"));
    }
}
