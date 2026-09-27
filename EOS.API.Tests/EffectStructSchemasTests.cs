using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class EffectStructSchemasTests
{
    [Fact]
    public void ValidateParams_InventoryDetail_AcceptsClosedConstantEntries()
    {
        var accepted = EffectStructSchemas.ValidateParams(
            "inventory-move",
            """{"direction":"OUT","fieldMap":{"masterDate":"BACK_DATE","qty":"QTY","detail":["SERIAL_NO","PRO_NO",{"column":"AMOUNT","constant":0},{"column":"CURR_ID","constant":""}]}}""");
        Assert.Empty(accepted);

        var rejected = EffectStructSchemas.ValidateParams(
            "inventory-move",
            """{"direction":"OUT","fieldMap":{"masterDate":"BACK_DATE","qty":"QTY","detail":[{"column":"AMOUNT","constant":"0","extra":1}]}}""");
        Assert.Contains(rejected, issue => issue.Contains("未登记键 'extra'"));

        var missing = EffectStructSchemas.ValidateParams(
            "inventory-move",
            """{"direction":"OUT","fieldMap":{"masterDate":"BACK_DATE","qty":"QTY","detail":[{"column":"AMOUNT"}]}}""");
        Assert.Contains(missing, issue => issue.Contains("缺少 constant"));
    }

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
    public void ValidateParams_LinkStamp_DetailSourceShapeAccepted()
    {
        var issues = EffectStructSchemas.ValidateParams("link-stamp",
            """
            {"targets":[{"table":"COP_CHAFFER_D","refs":["CHAFFER_TYPE","CHAFFER_NO","SERIAL_NO"],
                         "fromDetail":true,
                         "sourceRefs":["CHAFFER_TYPE","CHAFFER_NO","CHAFFER_SERIAL_NO"]}],
             "fields":["QUOTE_TYPE","QUOTE_NO",{"target":"QUOTE_SERIAL_NO","source":"SERIAL_NO"}]}
            """);
        Assert.Empty(issues);
    }

    [Fact]
    public void ValidateParams_LinkStamp_BaselineShapesStillAccepted()
    {
        Assert.Empty(EffectStructSchemas.ValidateParams("link-stamp",
            """{"targetTable":"MOU_ASSESS_M","field":"APPLY_NO","mode":"assign"}"""));
        Assert.Empty(EffectStructSchemas.ValidateParams("link-stamp",
            """
            {"targets":[{"table":"CUS_ACCOUNT_M","ref":["ACCOUNT_TYPE","ACCOUNT_NO"]}],
             "fields":["EXPORT_TYPE","EXPORT_NO"],"finish":true}
            """));
    }

    [Fact]
    public void ValidateParams_LinkStamp_RejectsUnknownTargetKeyAndBrokenDetailShape()
    {
        var unknownKey = EffectStructSchemas.ValidateParams("link-stamp",
            """{"targets":[{"table":"COP_CHAFFER_D","joinOn":["X"]}],"fields":["QUOTE_NO"]}""");
        Assert.Contains(unknownKey, issue => issue.Contains("未登记键 'joinOn'"));

        var missingSource = EffectStructSchemas.ValidateParams("link-stamp",
            """
            {"targets":[{"table":"COP_CHAFFER_D","refs":["CHAFFER_TYPE"],"fromDetail":true}],
             "fields":["QUOTE_NO"]}
            """);
        Assert.Contains(missingSource, issue => issue.Contains("sourceRefs"));

        var mismatch = EffectStructSchemas.ValidateParams("link-stamp",
            """
            {"targets":[{"table":"COP_CHAFFER_D","refs":["CHAFFER_TYPE","CHAFFER_NO","SERIAL_NO"],
                         "fromDetail":true,"sourceRefs":["CHAFFER_TYPE","CHAFFER_NO"]}],
             "fields":["QUOTE_NO"]}
            """);
        Assert.Contains(mismatch, issue => issue.Contains("列数必须一致"));

        var badField = EffectStructSchemas.ValidateParams("link-stamp",
            """{"targets":[{"table":"COP_CHAFFER_D"}],"fields":["QUOTE_NO",{"target":"QUOTE_TYPE"}]}""");
        Assert.Contains(badField, issue => issue.Contains("source"));
    }

    [Fact]
    public void ValidateParams_ChangeApplyProjection_AcceptedForProduce_AndClosuredElsewhere()
    {
        var ok = """{"master":{"fields":["QTY","SPARE_QTY"]},"detail":{"fields":["NEED_QTY"]},"projection":{"mode":"net-replace"}}""";
        Assert.Empty(EffectStructSchemas.ValidateParams("produce-change-apply", ok));

        var badMode = EffectStructSchemas.ValidateParams("produce-change-apply",
            """{"master":{"fields":["QTY"]},"detail":{"fields":["NEED_QTY"]},"projection":{"mode":"release-old"}}""");
        Assert.Contains(badMode, issue => issue.Contains("projection.mode 仅支持 net-replace"));

        var unknownKey = EffectStructSchemas.ValidateParams("produce-change-apply",
            """{"master":{"fields":["QTY"]},"detail":{"fields":["NEED_QTY"]},"projection":{"mode":"net-replace","targets":[]}}""");
        Assert.Contains(unknownKey, issue => issue.Contains("projection 含未登记键 'targets'"));

        // 1609/1418 param roots do not accept the projection section.
        var purchase = EffectStructSchemas.ValidateParams("purchase-change-apply",
            """{"master":{"fields":["PAY_CONDITION"]},"detail":{"fields":["QTY"]},"projection":{"mode":"net-replace"}}""");
        Assert.Contains(purchase, issue => issue.Contains("未登记根键 'projection'"));
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

    [Fact]
    public void ValidateCondition_RequiresScopeField_And_ValidatesNullAsMatch()
    {
        Assert.Empty(EffectStructSchemas.ValidateConditionJson(
            """{"logic":"AND","items":[{"type":"value-neq","field":{"scope":"MASTER","field":"BACK_CODE"},"value":"1","nullAsMatch":true}]}""",
            "测试条件"));

        var flat = EffectStructSchemas.ValidateConditionJson(
            """{"logic":"AND","items":[{"type":"value-eq","field":"BACK_CODE","value":"1"}]}""",
            "测试条件");
        Assert.Contains(flat, issue => issue.Contains("field 必须是"));

        var badFlag = EffectStructSchemas.ValidateConditionJson(
            """{"logic":"AND","items":[{"type":"value-neq","field":{"scope":"MASTER","field":"BACK_CODE"},"value":"1","nullAsMatch":"yes"}]}""",
            "测试条件");
        Assert.Contains(badFlag, issue => issue.Contains("nullAsMatch 必须是布尔"));
    }
}
