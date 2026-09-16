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
        Assert.Contains(issues, issue => issue.Contains("allowEmpty 必须是布尔值或数组"));
        Assert.Contains(issues, issue => issue.Contains("activeTag.field 不能为空"));
    }

    [Fact]
    public void ReferenceExists_AcceptsMissingLineFieldAndMultipleTargets()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[
              {"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"},
               "lineField":"SERIAL_NO","maxRows":10,"message":"以下序号项产品编号不存在 \r\n{ROWS}"},
              {"targets":[
                 {"refTable":"COP_SEND_D","join":[{"target":"SEND_NO","source":{"scope":"DETAIL","field":"S_R_NO"}}]},
                 {"refTable":"COP_RETURN_D","activeTag":{"field":"BUSINESS_TAG","expect":0},
                  "join":[{"target":"RETURN_NO","source":{"scope":"DETAIL","field":"S_R_NO"}}]}],
               "allowEmpty":[{"scope":"DETAIL","field":"S_R_TYPE"},"S_R_NO"],
               "lineField":{"scope":"DETAIL","field":"SERIAL_NO"},
               "message":"以下序号项送/退货单不存在 \r\n{ROWS}"}]}
            """));
        Assert.Empty(issues);
    }

    [Fact]
    public void ReferenceExists_LineFieldRequiresRowsPlaceholderAndDetailScope()
    {
        var missingPlaceholder = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"},
             "lineField":"SERIAL_NO","message":"以下序号项产品编号不存在"}]}
            """));
        Assert.Contains(missingPlaceholder, issue => issue.Contains("必须包含 {ROWS} 占位符"));

        var masterScope = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"PRODUCT","refKey":{"scope":"DETAIL","field":"PRO_NO"},
             "lineField":{"scope":"MASTER","field":"PRO_NO"},"message":"{ROWS}"}]}
            """));
        Assert.Contains(masterScope, issue => issue.Contains("lineField.scope 仅允许 DETAIL"));
    }

    [Fact]
    public void ReferenceExists_TargetsCannotMixWithRootReference()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"PRODUCT","targets":[{"refTable":"DEPOT","refKey":{"field":"DEPOT_ID"}}]}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("与 targets 不能并用"));
    }

    [Fact]
    public void ReferenceExists_AllowEmptyTrueNeedsRefKeyAndMaxRowsIsBounded()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"DEPOT","join":[{"target":"DEPOT_ID","source":{"scope":"DETAIL","field":"DEPOT_ID"}}],
             "allowEmpty":true,"maxRows":0}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("allowEmpty=true 需要配合 refKey"));
        Assert.Contains(issues, issue => issue.Contains("maxRows 必须是 1..100 的整数"));
    }

    [Fact]
    public void ReferenceExists_TargetRejectsUnknownKeys()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"targets":[{"refTable":"DEPOT","refKey":{"field":"DEPOT_ID","alias":"d"}}]}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("未知参数键"));
    }

    [Fact]
    public void QuantityCheck_ThisNotExceed_RequiresLimitButNotUsage()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"this-not-exceed","checks":[{"targetTable":"COP_ORDER_D",
             "match":[{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "limit":{"scope":"TARGET","fields":["FINISHED_SEND_QTY"]}}]}
            """));
        Assert.Empty(issues);
        var missingLimit = Validate("qty-not-exceed", Params("""
            {"mode":"this-not-exceed","checks":[{"targetTable":"COP_ORDER_D",
             "match":[{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]}}]}
            """));
        Assert.Contains(missingLimit, issue => issue.Contains("limit 缺失"));
    }

    [Fact]
    public void LineRequire_AcceptsKnownShape_RejectsBadShapes()
    {
        var ok = Validate("line-require", Params("""
            {"checks":[{"scope":"DETAIL","field":"BAD_DEPOT_ID","message":"请输入不良品存放仓库",
             "triggers":[{"scope":"DETAIL","field":"BAD_QTY","op":"GT","value":0},
                         {"scope":"DETAIL","field":"BAD_SPARE_QTY","op":"GT","value":0}]}]}
            """));
        Assert.Empty(ok);
        var bad = Validate("line-require", Params("""
            {"checks":[{"scope":"MASTER","field":"","triggers":[{"scope":"DETAIL","field":"BAD_QTY","op":"LIKE","value":"x"}]}]}
            """));
        Assert.Contains(bad, issue => issue.Contains("scope 仅允许 DETAIL"));
        Assert.Contains(bad, issue => issue.Contains("field 不能为空"));
        Assert.Contains(bad, issue => issue.Contains("op 仅允许"));
        Assert.Contains(bad, issue => issue.Contains("value 必须是数字"));
        var empty = Validate("line-require", Params("""{"checks":[]}"""));
        Assert.Contains(empty, issue => issue.Contains("checks 必须是非空数组"));
    }

    [Fact]
    public void MatchItem_RejectsUnknownSourceKeys()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"PRODUCT","join":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO","hint":"x"}}]}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("未知参数键 checks[0].join[0].source.hint"));
    }

    [Fact]
    public void QuantityCheck_SwitchGate_AcceptsKnownShape()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"COP_ORDER_D",
             "switch":{"key":"SEND_ORDER_TAG","expect":1},
             "match":[{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["FINISHED_SEND_QTY"]},
             "limit":{"scope":"TARGET","fields":["QTY"]}}]}
            """));
        Assert.DoesNotContain(issues, issue => issue.Contains("未知参数键"));
        Assert.DoesNotContain(issues, issue => issue.Contains("switch"));
    }

    [Fact]
    public void QuantityCheck_SwitchGate_RejectsMissingKeyAndUnknownKeys()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"COP_ORDER_D",
             "switch":{"expect":"yes","extra":1},
             "match":[{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["FINISHED_SEND_QTY"]},
             "limit":{"scope":"TARGET","fields":["QTY"]}}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("switch.key 不能为空"));
        Assert.Contains(issues, issue => issue.Contains("switch.expect 必须是数字"));
        Assert.Contains(issues, issue => issue.Contains("未知参数键 checks[0].switch.extra"));
    }

    [Fact]
    public void QuantityCheck_1406SeedParams_PassRegistry()
    {
        // Mirror of logs/adr012-acceptance/fixture/1406-validation-params.json (MODULE_VALIDATION_RULE 1406/APPROVE).
        var issues = Validate("qty-not-exceed", Params("""
{"checks":[{"limit":{"fields":["FINISHED_FITOUT_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"送货数量超过订单已备货数量","switch":{"expect":1,"key":"SEND_ORDER_FITOUT_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_FITOUT_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"送货备品超过订单已备备品数量","switch":{"expect":1,"key":"SEND_ORDER_FITOUT_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SPARE_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_FITOUT_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"送货数量超过工单已备货数量","switch":{"expect":1,"key":"SEND_PRODUCE_FITOUT_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_FITOUT_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"送货备品超过工单已备备品数量","switch":{"expect":1,"key":"SEND_PRODUCE_FITOUT_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SEND_SPARE_QTY"],"scope":"TARGET"}},{"limit":{"fields":["QTY"],"scope":"TARGET"},"match":[{"source":{"field":"FITOUT_TYPE","scope":"DETAIL"},"target":"FITOUT_TYPE"},{"source":{"field":"FITOUT_NO","scope":"DETAIL"},"target":"FITOUT_NO"},{"source":{"field":"FITOUT_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"送货数量超过备货单数量","switch":{"expect":1,"key":"SEND_FITOUT_TAG"},"targetTable":"COP_FITOUT_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["FINISHED_QTY","RETURN_QTY"],"scope":"TARGET"}},{"limit":{"fields":["SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"FITOUT_TYPE","scope":"DETAIL"},"target":"FITOUT_TYPE"},{"source":{"field":"FITOUT_NO","scope":"DETAIL"},"target":"FITOUT_NO"},{"source":{"field":"FITOUT_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"送货备品超过备货单备品数量","switch":{"expect":1,"key":"SEND_FITOUT_TAG"},"targetTable":"COP_FITOUT_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SPARE_QTY","RETURN_SPARE_QTY"],"scope":"TARGET"}},{"limit":{"fields":["QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"送货数量超过订单数量","switch":{"expect":1,"key":"SEND_ORDER_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["BACK_BAD","BACK_MATERIAL","FINISHED_SEND_QTY"],"scope":"TARGET"}},{"limit":{"fields":["SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"送货备品超过订单备品数量","switch":{"expect":1,"key":"SEND_ORDER_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SPARE_QTY"],"scope":"TARGET"}},{"limit":{"fields":["QTY"],"scope":"TARGET"},"match":[{"source":{"field":"SHIPMENT_TYPE","scope":"DETAIL"},"target":"SHIPMENT_TYPE"},{"source":{"field":"SHIPMENT_NO","scope":"DETAIL"},"target":"SHIPMENT_NO"},{"source":{"field":"SHIPMENT_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"送货数量超过排程数量","targetTable":"COP_SHIPMENT_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["FINISHED_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"送货数量超过工单生产数量","switch":{"expect":1,"key":"SEND_PRODUCE_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"送货备品超过工单生产备品数量","switch":{"expect":1,"key":"SEND_PRODUCE_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SEND_SPARE_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_TRANSFER_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"送货数量超过工单已调拨数量","switch":{"expect":1,"key":"SEND_PRODUCE_TRANSFER_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_TRANSFER_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"送货备品超过工单已调拨备品数量","switch":{"expect":1,"key":"SEND_PRODUCE_TRANSFER_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SEND_SPARE_QTY"],"scope":"TARGET"}}],"mode":"usage-not-exceed"}
"""));
        Assert.Empty(issues);
    }

    [Fact]
    public void QuantityCheck_1505ApproveParams_PassRegistry()
    {
        // Mirror of logs/adr012-acceptance/fixture/1505-validation-params-approve.json.
        var issues = Validate("qty-not-exceed", Params("""
{"checks":[{"limit":{"fields":["QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"入库数量超过制令生产数量","targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["FINISHED_QTY","SCRAP_IN_QTY"],"scope":"TARGET"}},{"limit":{"fields":["SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"入库备品超过制令备品数量","targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SPARE_QTY","SCRAP_IN_SPARE_QTY"],"scope":"TARGET"}}],"mode":"usage-not-exceed"}
"""));
        Assert.Empty(issues);
    }

    [Fact]
    public void QuantityCheck_1505DeapproveGuard_PassRegistry()
    {
        // Mirror of logs/adr012-acceptance/fixture/1505-validation-params-deapprove.json.
        var issues = Validate("qty-not-exceed", Params("""
{"checks":[{"limit":{"fields":["FINISHED_FITOUT_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"解批后入库数量将低于备货数量","switch":{"expect":1,"key":"FITOUT_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":-1,"field":"QTY"}]},"usage":{"fields":["FINISHED_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_FITOUT_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"解批后入库备品将低于备货备品数量","switch":{"expect":1,"key":"FITOUT_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":-1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SPARE_QTY"],"scope":"TARGET"}}],"mode":"not-below-progress"}
"""));
        Assert.Empty(issues);
    }

    [Fact]
    public void ReturnRequireRule_PassRegistry()
    {
        // Mirror of logs/adr012-acceptance/fixture/1407-approve-linerequire.json.
        var issues = Validate("line-require", Params("""
{"checks":[{"field":"BAD_DEPOT_ID","message":"以下序号项请输入不良品存放仓库","scope":"DETAIL","triggers":[{"field":"BAD_QTY","op":"GT","scope":"DETAIL","value":0},{"field":"BAD_SPARE_QTY","op":"GT","scope":"DETAIL","value":0}]}]}
"""));
        Assert.Empty(issues);
    }

    [Fact]
    public void ReturnUpperBoundRule_PassRegistry()
    {
        // Mirror of logs/adr012-acceptance/fixture/1407-approve-upperbound.json.
        var issues = Validate("qty-not-exceed", Params("""
{"checks":[{"limit":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"以下退货已超出订单送货数量","switch":{"expect":1,"key":"RETURN_ORDER_SEND_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]}},{"limit":{"fields":["FINISHED_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"以下退货备品已超出订单送货备品数量","switch":{"expect":1,"key":"RETURN_ORDER_SEND_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]}},{"limit":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"以下退货已超出工单送货数量","switch":{"expect":1,"key":"RETURN_PRODUCE_SEND_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]}},{"limit":{"fields":["FINISHED_SEND_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"以下退货备品已超出工单送货备品数量","switch":{"expect":1,"key":"RETURN_PRODUCE_SEND_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]}}],"mode":"this-not-exceed"}
"""));
        Assert.Empty(issues);
    }

    [Fact]
    public void ReturnDeapproveGuards_PassRegistry()
    {
        // Mirror of logs/adr012-acceptance/fixture/1407-deapprove-guards.json.
        var issues = Validate("qty-not-exceed", Params("""
{"checks":[{"limit":{"fields":["QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"以下解批会出现订单已送货数量超出订单数量","switch":{"expect":1,"key":"SEND_ORDER_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"}},{"limit":{"fields":["SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"以下解批会出现订单已送备品超出订单备品数量","switch":{"expect":1,"key":"SEND_ORDER_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SPARE_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_FITOUT_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"以下解批会出现订单已送货数量超出已备货数量","switch":{"expect":1,"key":"SEND_ORDER_FITOUT_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"QTY"}]},"usage":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_FITOUT_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"ORDER_TYPE","scope":"DETAIL"},"target":"ORDER_TYPE"},{"source":{"field":"ORDER_NO","scope":"DETAIL"},"target":"ORDER_NO"},{"source":{"field":"ORDER_SERIAL_NO","scope":"DETAIL"},"target":"SERIAL_NO"}],"message":"以下解批会出现订单已送备品超出已备备品数量","switch":{"expect":1,"key":"SEND_ORDER_FITOUT_TAG"},"targetTable":"COP_ORDER_D","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"SPARE_QTY"}]},"usage":{"fields":["FINISHED_SPARE_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"以下解批会出现工单已送货数量超出已生产数量","switch":{"expect":1,"key":"SEND_PRODUCE_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"GOOD_QTY"}]},"usage":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"以下解批会出现工单已送备品超出已生产备品数量","switch":{"expect":1,"key":"SEND_PRODUCE_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"GOOD_SPARE_QTY"}]},"usage":{"fields":["FINISHED_SEND_SPARE_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_TRANSFER_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"以下解批会出现工单已送货数量超出已调拨数量","switch":{"expect":1,"key":"SEND_PRODUCE_TRANSFER_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"GOOD_QTY"}]},"usage":{"fields":["FINISHED_SEND_QTY"],"scope":"TARGET"}},{"limit":{"fields":["FINISHED_TRANSFER_SPARE_QTY"],"scope":"TARGET"},"match":[{"source":{"field":"PRODUCE_TYPE","scope":"DETAIL"},"target":"PRODUCE_TYPE"},{"source":{"field":"PRODUCE_NO","scope":"DETAIL"},"target":"PRODUCE_NO"}],"message":"以下解批会出现工单已送备品超出已调拨备品数量","switch":{"expect":1,"key":"SEND_PRODUCE_TRANSFER_TAG"},"targetTable":"MOC_PRODUCE_M","thisQty":{"scope":"DETAIL","terms":[{"coef":1,"field":"GOOD_SPARE_QTY"}]},"usage":{"fields":["FINISHED_SEND_SPARE_QTY"],"scope":"TARGET"}}],"mode":"usage-not-exceed"}
"""));
        Assert.Empty(issues);
    }

    private static List<string> ValidateWithMessage(string key, string? message, JsonElement parameters)
    {
        var issues = new List<string>();
        ValidationRuleRegistry.Validate(
            new ValidationRuleConfig("rule-1", key, "SAVE", true, message, parameters), issues);
        return issues;
    }

    [Fact]
    public void DuplicateCheck_EntityFilterAndDiagnostics_PassRegistry()
    {
        var issues = ValidateWithMessage(
            "duplicate-check",
            "\r\n员工工号：{EMP_NO}\r\n已分配给：{EMP_NAME}",
            Params("""
                {"mode":"entity","table":"HR_EMPLOYEE","keyFields":["EMP_NO"],
                 "excludeSelf":{"keyFields":["EMP_ID"]},
                 "filter":{"logic":"AND","items":[{"type":"VALUE-NEQ","field":{"scope":"TARGET","field":"STATE"},"value":5,"nullAsMatch":true}]},
                 "diagnostics":["EMP_NO","EMP_NAME"]}
                """));

        Assert.Empty(issues);
    }

    [Fact]
    public void DuplicateCheck_PlaceholderOutsideDiagnostics_IsRejected()
    {
        var issues = ValidateWithMessage(
            "duplicate-check",
            "已分配给：{EMP_NAME}",
            Params("""
                {"mode":"entity","table":"HR_EMPLOYEE","keyFields":["EMP_NO"],
                 "excludeSelf":{"keyFields":["EMP_ID"]},
                 "diagnostics":["EMP_NO"]}
                """));

        Assert.Contains(issues, issue => issue.Contains("占位符 {EMP_NAME}"));
    }

    [Fact]
    public void DuplicateCheck_FilterLogicOutsideClosedSet_IsRejected()
    {
        var issues = Validate("duplicate-check", Params("""
            {"mode":"entity","table":"HR_EMPLOYEE","keyFields":["EMP_NO"],
             "excludeSelf":{"keyFields":["EMP_ID"]},
             "filter":{"logic":"XOR","items":[]}}
            """));

        Assert.Contains(issues, issue => issue.Contains("filter.logic 仅允许 AND / OR"));
    }

    [Fact]
    public void DuplicateCheck_WithinDocRejectsEntityOnlyKeys()
    {
        var issues = Validate("duplicate-check", Params("""
            {"mode":"within-doc","keyFields":["EMP_ID"],
             "excludeSelf":{"keyFields":["EMP_ID"]},
             "keySource":{"scope":"DETAIL","fields":["EMP_ID"]},
             "diagnostics":["EMP_ID"],
             "filter":{"logic":"AND","items":[]}}
            """));

        Assert.Contains(issues, issue => issue.Contains("excludeSelf 仅用于 entity 模式"));
        Assert.Contains(issues, issue => issue.Contains("filter 仅用于 entity 模式"));
        Assert.Contains(issues, issue => issue.Contains("diagnostics 仅用于 entity 模式"));
        Assert.Contains(issues, issue => issue.Contains("keySource 仅用于 entity 模式"));
    }

    [Fact]
    public void DuplicateCheck_KeySourceFieldCountMismatch_IsRejected()
    {
        var issues = Validate("duplicate-check", Params("""
            {"mode":"entity","table":"HR_PLAN_M","keyFields":["EMP_ID"],
             "keySource":{"scope":"DETAIL","fields":["EMP_ID","SERIAL_NO"]}}
            """));

        Assert.Contains(issues, issue => issue.Contains("keySource.fields 数量"));
    }

    [Fact]
    public void DuplicateCheck_KeySourceScopeOutsideClosedSet_IsRejected()
    {
        var issues = Validate("duplicate-check", Params("""
            {"mode":"entity","table":"HR_PLAN_M","keyFields":["EMP_ID"],
             "keySource":{"scope":"TABLE","fields":["EMP_ID"]}}
            """));

        Assert.Contains(issues, issue => issue.Contains("keySource.scope 仅允许 MASTER / DETAIL"));
    }

    [Fact]
    public void DuplicateCheck_ExcludeSelfSourceBlock_IsRejected()
    {
        var issues = Validate("duplicate-check", Params("""
            {"mode":"entity","table":"MOU_ASSESS_M","keyFields":["PRO_NO"],
             "excludeSelf":{"keyFields":["ASSESS_TYPE","ASSESS_NO"],"source":{"scope":"MASTER","fields":["ASSESS_TYPE","ASSESS_NO"]}}}
            """));

        Assert.Contains(issues, issue => issue.Contains("未知参数键 excludeSelf.source"));
    }

    [Fact]
    public void DuplicateCheck_MasterDetailShape_PassRegistry()
    {
        var issues = ValidateWithMessage(
            "duplicate-check",
            "以下序号项人员当月排班重复 \r\n{ROWS}",
            Params("""
                {"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",
                 "joinFields":{"master":["PLAN_TYPE","PLAN_NO"],"detail":["PLAN_TYPE","PLAN_NO"]},
                 "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],
                 "documentDetailFields":["PLAN_TYPE","PLAN_NO"],"diagnosticFields":["SERIAL_NO"],
                 "when":{"logic":"AND","items":[{"type":"VALUE-NEQ","field":{"scope":"MASTER","field":"COUNT_MONTH"},"value":""}]}}
                """));

        Assert.Empty(issues);
    }

    [Fact]
    public void DuplicateCheck_MasterDetailMissingKeys_AreRejected()
    {
        var issues = Validate("duplicate-check", Params("""{"mode":"master-detail","groupFields":["EMP_ID"]}"""));

        Assert.Contains(issues, issue => issue.Contains("masterTable 必填"));
        Assert.Contains(issues, issue => issue.Contains("detailTable 必填"));
        Assert.Contains(issues, issue => issue.Contains("masterGroupFields 必须是非空数组"));
        Assert.Contains(issues, issue => issue.Contains("joinFields 必填"));
    }

    [Fact]
    public void DuplicateCheck_MasterDetailJoinLengthMismatch_IsRejected()
    {
        var issues = Validate("duplicate-check", Params("""
            {"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",
             "joinFields":{"master":["PLAN_TYPE","PLAN_NO"],"detail":["PLAN_TYPE"]},
             "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"]}
            """));

        Assert.Contains(issues, issue => issue.Contains("joinFields.detail 数量必须与 master 一致"));
    }

    [Fact]
    public void DuplicateCheck_MessagePlaceholderMustResolve()
    {
        var issues = ValidateWithMessage(
            "duplicate-check",
            "重复：{SERIAL_NO}",
            Params("""
                {"mode":"master-detail","masterTable":"HR_PLAN_M","detailTable":"HR_PLAN_D",
                 "joinFields":{"master":["PLAN_TYPE"],"detail":["PLAN_TYPE"]},
                 "masterGroupFields":["COUNT_MONTH"],"groupFields":["EMP_ID"],"diagnosticFields":["EMP_ID"]}
                """));

        Assert.Contains(issues, issue => issue.Contains("占位符 {SERIAL_NO}"));
    }

    [Fact]
    public void WhenConditionShape_IsValidatedForEveryTemplate()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","when":{"logic":"XOR","items":[]},
             "checks":[{"match":[{"target":"A","source":{"scope":"DETAIL","field":"B"}}],
              "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
              "usage":{"scope":"TARGET","fields":["X"]},
              "limit":{"scope":"TARGET","fields":["Y"]}}]}
            """));

        Assert.Contains(issues, issue => issue.Contains("when.logic 仅允许 AND / OR"));
    }

    [Fact]
    public void WhenCondition_IsAcceptedAsSharedKey()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"SUPPLIER","refKey":{"scope":"MASTER","field":"SUPPLIER_ID"}}],
             "when":{"logic":"AND","items":[{"type":"VALUE-NEQ","field":{"scope":"MASTER","field":"SUPPLIER_ID"},"value":""}]}}
            """));

        Assert.DoesNotContain(issues, issue => issue.Contains("未知参数键"));
    }
}

