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
            {"checks":[{"scope":"DETAILX","field":"","triggers":[{"scope":"MASTER","field":"BAD_QTY","op":"LIKE","value":"x"}]}]}
            """));
        Assert.Contains(bad, issue => issue.Contains("scope 仅允许 DETAIL/MASTER"));
        Assert.Contains(bad, issue => issue.Contains("field 不能为空"));
        Assert.Contains(bad, issue => issue.Contains("op 仅允许"));
        Assert.Contains(bad, issue => issue.Contains("value 必须是数字"));
        // 触发器作用域必须与同一 check 一致：否则判据会写成两个关系之间的无意义比较
        Assert.Contains(bad, issue => issue.Contains("必须与同一 check 的 scope 一致"));
        var empty = Validate("line-require", Params("""{"checks":[]}"""));
        Assert.Contains(empty, issue => issue.Contains("checks 必须是非空数组"));
    }

    [Fact]
    public void LineRequire_AcceptsValueAssertionAndMasterScope()
    {
        // 断言形态：无触发器（无条件生效）＋数值比较＋主表行作用域（无明细表的模块，如货币资料）
        var ok = Validate("line-require", Params("""
            {"checks":[{"scope":"MASTER","field":"CURR_RATE","assert":{"op":"EQ","value":1},
             "triggers":[{"scope":"MASTER","field":"IS_BASE","op":"EQ","value":1}],
             "message":"本位币汇率只能为1"}]}
            """));
        Assert.Empty(ok);
        // 明细行无条件下界断言：assert 存在即无需 triggers/condition
        var unconditional = Validate("line-require", Params("""
            {"checks":[{"scope":"DETAIL","field":"CHECK_QTY","assert":{"op":"GE","value":0},
             "message":"以下序号项盘点数小于0 ","diagnosticFields":["SERIAL_NO"]}]}
            """));
        Assert.Empty(unconditional);

        var badAssert = Validate("line-require", Params("""
            {"checks":[{"scope":"DETAIL","field":"CHECK_QTY","assert":{"op":"LIKE","value":"x","extra":1}}]}
            """));
        Assert.Contains(badAssert, issue => issue.Contains("assert.op 仅允许"));
        Assert.Contains(badAssert, issue => issue.Contains("需要数值 value 或字符串 compareField"));
        Assert.Contains(badAssert, issue => issue.Contains("未知参数键"));
        // compareField 形态：与同行另一列比较，和数值 value 互斥（空值不违规由执行器保证）
        var compareAssert = Validate("line-require", Params("""
            {"checks":[{"scope":"MASTER","field":"END_DATE","assert":{"op":"GE","compareField":"BEGIN_DATE"}}]}
            """));
        Assert.Empty(compareAssert);
        var bothAssert = Validate("line-require", Params("""
            {"checks":[{"scope":"MASTER","field":"END_DATE","assert":{"op":"GE","value":0,"compareField":"BEGIN_DATE"}}]}
            """));
        Assert.Contains(bothAssert, issue => issue.Contains("value 与 compareField 不能同时配置"));
    }

    [Fact]
    public void LineRequire_AcceptsStructuredConditionTrigger()
    {
        // 跨表条件触发器：无需 triggers，改用 condition（DETAIL 域即明细行别名，可表达"产品为批管"）
        var ok = Validate("line-require", Params("""
            {"checks":[{"scope":"DETAIL","field":"BATCH_NO","message":"以下序号项需要输入批号 ",
             "condition":{"logic":"AND","items":[
                {"type":"not-exists","targetTable":"PRODUCT","negate":true,
                 "condition":{"type":"value-eq","field":{"scope":"TARGET","field":"MANAGE_BATCH"},"value":1},
                 "match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]}]}}]}
            """));
        Assert.Empty(ok);

        // 两者皆无 → 拒绝
        var none = Validate("line-require", Params("""
            {"checks":[{"scope":"DETAIL","field":"BATCH_NO","message":"x"}]}
            """));
        Assert.Contains(none, issue => issue.Contains("需要非空 triggers 或 condition"));

        // condition 形状非法 → 拒绝；未知键 → 拒绝
        var badCondition = Validate("line-require", Params("""
            {"checks":[{"scope":"DETAIL","field":"BATCH_NO","condition":{"logic":"XOR","items":[]}}]}
            """));
        Assert.Contains(badCondition, issue => issue.Contains("condition"));
        var unknown = Validate("line-require", Params("""
            {"checks":[{"scope":"DETAIL","field":"BATCH_NO","triggers":[{"scope":"DETAIL","field":"QTY","op":"GT","value":0}],"conditionX":{}}]}
            """));
        Assert.Contains(unknown, issue => issue.Contains("未知参数键"));
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
    public void QuantityCheck_AcceptsSaveSideExtensionsAndRejectsBadOnes()
    {
        // B4 保存侧新能力：offset 容差、thisQty.agg 分组求和、诊断行、诊断分隔符
        var ok = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"PUR_PURCHASE_D",
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["RECEIVE_QTY"]},
             "limit":{"scope":"TARGET","fields":["QTY"]},
             "offset":0.1,
             "diagnosticFields":[{"scope":"TARGET","field":"QTY"},{"scope":"THIS"}],
             "diagnosticRowSeparator":"  ","diagnosticCellSeparator":"    ",
             "message":"超出数量 \r\n{ROWS}"}]}
            """));
        Assert.Empty(ok);

        var badAgg = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"PUR_PURCHASE_D",
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "thisQty":{"scope":"DETAIL","agg":"AVG","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["RECEIVE_QTY"]},
             "limit":{"scope":"TARGET","fields":["QTY"]}}]}
            """));
        Assert.Contains(badAgg, issue => issue.Contains("agg 仅允许 SUM"));

        var badDiagnostic = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"PUR_PURCHASE_D",
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["RECEIVE_QTY"]},
             "limit":{"scope":"TARGET","fields":["QTY"]},
             "diagnosticFields":[{"scope":"OTHER","field":"QTY"}],
             "message":"超出数量"}]}
            """));
        Assert.Contains(badDiagnostic, issue => issue.Contains("scope 仅允许 SOURCE/TARGET/THIS"));
        Assert.Contains(badDiagnostic, issue => issue.Contains("必须包含 {ROWS} 占位符"));
    }

    [Fact]
    public void QuantityCheck_AcceptsUsageOnlyAndRejectsConflictingShapes()
    {
        // usageOnly：判据两侧都取自被引用侧（如"退料合计 > 收料合计"），本单侧贡献为 0，
        // 因此不写 thisQty；多量纲（数量 + 备品）依旧可用。
        var ok = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"V_PUR_CANCEL_ALLOC","usageOnly":true,
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "dimensions":[
              {"usage":{"scope":"TARGET","fields":["RET_QTY"]},"limit":{"scope":"TARGET","fields":["REC_QTY"]}},
              {"usage":{"scope":"TARGET","fields":["RET_SPARE_QTY"]},"limit":{"scope":"TARGET","fields":["REC_SPARE_QTY"]}}],
             "diagnosticFields":["SERIAL_NO",{"scope":"TARGET","field":"RET_QTY"}],
             "message":"以下序号项退料数量大于收料 \r\n{ROWS}"}]}
            """));
        Assert.Empty(ok);

        // 单量纲同样可省略 thisQty
        var single = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"V_PUR_CANCEL_ALLOC","usageOnly":true,
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "usage":{"scope":"TARGET","fields":["RET_QTY"]},
             "limit":{"scope":"TARGET","fields":["REC_QTY"]}}]}
            """));
        Assert.Empty(single);

        // 与 thisQty 互斥（写了也无处参与比较）
        var withThis = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"V_PUR_CANCEL_ALLOC","usageOnly":true,
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["RET_QTY"]},
             "limit":{"scope":"TARGET","fields":["REC_QTY"]}}]}
            """));
        Assert.Contains(withThis, issue => issue.Contains("usageOnly 与 thisQty 互斥"));

        // 仅适用于 usage-not-exceed 模式
        var wrongMode = Validate("qty-not-exceed", Params("""
            {"mode":"this-not-exceed","checks":[{"targetTable":"V_PUR_CANCEL_ALLOC","usageOnly":true,
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "usage":{"scope":"TARGET","fields":["RET_QTY"]},
             "limit":{"scope":"TARGET","fields":["REC_QTY"]}}]}
            """));
        Assert.Contains(wrongMode, issue => issue.Contains("usageOnly 仅适用于 usage-not-exceed"));

        // 非布尔值即拒绝
        var wrongType = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"V_PUR_CANCEL_ALLOC","usageOnly":"yes",
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "usage":{"scope":"TARGET","fields":["RET_QTY"]},
             "limit":{"scope":"TARGET","fields":["REC_QTY"]}}]}
            """));
        Assert.Contains(wrongType, issue => issue.Contains("usageOnly 必须是布尔值"));

        // 不写 usageOnly 时 thisQty 仍必须给出（既有口径不放宽）
        var missingThis = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"V_PUR_CANCEL_ALLOC",
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "usage":{"scope":"TARGET","fields":["RET_QTY"]},
             "limit":{"scope":"TARGET","fields":["REC_QTY"]}}]}
            """));
        Assert.Contains(missingThis, issue => issue.Contains("thisQty 缺失"));
    }

    [Fact]
    public void QuantityCheck_AcceptsOrMergedDimensionsAndRejectsBadOnes()
    {
        // 多量纲：旧实现把"数量"与"备品"合成 `WHERE a OR b`，用 dimensions 表达（诊断行只输出一次）
        var ok = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"COP_ORDER_D",
             "match":[{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
             "dimensions":[
              {"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},
               "usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},
               "limit":{"scope":"TARGET","fields":["QTY"]}},
              {"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"SPARE_QTY","coef":1}]},
               "usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_SPARE_QTY"]},
               "limit":{"scope":"TARGET","fields":["SPARE_QTY"]}}],
             "diagnosticFields":[{"scope":"TARGET","field":"ORDER_NO"},{"scope":"THIS","dimension":1},
                                 {"scope":"THIS","dimension":2}],
             "message":"已备货超出 \r\n{ROWS}"}]}
            """));
        Assert.Empty(ok);

        // 互斥：用了 dimensions 就不能再写单量纲块
        var mixed = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"COP_ORDER_D",
             "match":[{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "dimensions":[{"thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
                            "usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},
                            "limit":{"scope":"TARGET","fields":["QTY"]}}]}]}
            """));
        Assert.Contains(mixed, issue => issue.Contains("不能再写 thisQty/usage/limit/offset"));

        // 各量纲必须一致地分组：否则分组子查询与逐行比较会混在同一句 SQL 里
        var inconsistent = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"COP_ORDER_D",
             "match":[{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
             "dimensions":[
              {"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},
               "usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},
               "limit":{"scope":"TARGET","fields":["QTY"]}},
              {"thisQty":{"scope":"DETAIL","terms":[{"field":"SPARE_QTY","coef":1}]},
               "usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_SPARE_QTY"]},
               "limit":{"scope":"TARGET","fields":["SPARE_QTY"]}}]}]}
            """));
        Assert.Contains(inconsistent, issue => issue.Contains("一致地使用"));

        // 量纲序号越界、空数组
        var badDimension = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"COP_ORDER_D",
             "match":[{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
             "dimensions":[
              {"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},
               "usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_QTY"]},
               "limit":{"scope":"TARGET","fields":["QTY"]}},
              {"thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"SPARE_QTY","coef":1}]},
               "usage":{"scope":"TARGET","fields":["FINISHED_FITOUT_SPARE_QTY"]},
               "limit":{"scope":"TARGET","fields":["SPARE_QTY"]}}],
             "diagnosticFields":[{"scope":"THIS","dimension":3}],
             "message":"x \r\n{ROWS}"}]}
            """));
        Assert.Contains(badDimension, issue => issue.Contains("dimension 必须是 1..2"));

        var empty = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"COP_ORDER_D",
             "match":[{"target":"ORDER_NO","source":{"scope":"DETAIL","field":"ORDER_NO"}}],
             "dimensions":[]}]}
            """));
        Assert.Contains(empty, issue => issue.Contains("dimensions 必须是非空数组"));
    }

    [Fact]
    public void QuantityCheck_AcceptsTargetAggregateAndSourceRowDiagnostics()
    {
        // 被引用行按定位键聚合（MAX/MIN/SUM）+ 逐源明细行的诊断（diagnosticRows=SOURCE）
        var ok = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"MOC_PRODUCE_PROCESS_D",
             "match":[{"target":"PRODUCE_TYPE","source":{"scope":"DETAIL","field":"PRODUCE_TYPE"}},
                      {"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],
             "thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"FINISHED_QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["FINISHED_PLAN_QTY"]},
             "limit":{"scope":"TARGET","fields":["PROCESS_OVER_QTY"]},
             "targetAgg":"MAX","diagnosticRows":"SOURCE",
             "diagnosticFields":["SERIAL_NO"],
             "message":"超出允许数量 \r\n{ROWS}"}]}
            """));
        Assert.Empty(ok);

        var badAgg = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"MOC_PRODUCE_PROCESS_D",
             "match":[{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],
             "thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"FINISHED_QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["FINISHED_PLAN_QTY"]},
             "limit":{"scope":"TARGET","fields":["PROCESS_OVER_QTY"]},
             "targetAgg":"AVG"}]}
            """));
        Assert.Contains(badAgg, issue => issue.Contains("targetAgg 仅允许 MAX/MIN/SUM"));

        // 逐明细诊断要求分组形态，且诊断列不能取被引用行或求和投影
        var notGrouped = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"MOC_PRODUCE_PROCESS_D",
             "match":[{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"FINISHED_QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["FINISHED_PLAN_QTY"]},
             "limit":{"scope":"TARGET","fields":["PROCESS_OVER_QTY"]},
             "targetAgg":"MAX","diagnosticRows":"SOURCE","diagnosticFields":["SERIAL_NO"]}]}
            """));
        Assert.Contains(notGrouped, issue => issue.Contains("需要 thisQty.agg=SUM 的分组形态"));

        var badScope = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"MOC_PRODUCE_PROCESS_D",
             "match":[{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],
             "thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"FINISHED_QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["FINISHED_PLAN_QTY"]},
             "limit":{"scope":"TARGET","fields":["PROCESS_OVER_QTY"]},
             "targetAgg":"MAX","diagnosticRows":"SOURCE",
             "diagnosticFields":[{"scope":"TARGET","field":"PROCESS_OVER_QTY"}],
             "message":"超出 \r\n{ROWS}"}]}
            """));
        Assert.Contains(badScope, issue => issue.Contains("仅允许 SOURCE"));

        var badRows = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"MOC_PRODUCE_PROCESS_D",
             "match":[{"target":"PRODUCE_NO","source":{"scope":"DETAIL","field":"PRODUCE_NO"}}],
             "thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"FINISHED_QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["FINISHED_PLAN_QTY"]},
             "limit":{"scope":"TARGET","fields":["PROCESS_OVER_QTY"]},
             "diagnosticRows":"GROUP"}]}
            """));
        Assert.Contains(badRows, issue => issue.Contains("diagnosticRows 仅允许 SOURCE"));
    }

    [Fact]
    public void QuantityCheck_ControllerKeys()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[{"targetTable":"PUR_PURCHASE_D",
             "match":[{"target":"PURCHASE_NO","source":{"scope":"DETAIL","field":"PURCHASE_NO"}}],
             "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
             "usage":{"scope":"TARGET","fields":["RECEIVE_QTY"]},
             "limit":{"scope":"TARGET","fields":["QTY"]},
             "unknownKey":1}]}
            """));
        Assert.Contains(issues, issue => issue.Contains("未知参数键"));
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

    [Fact]
    public void ReferenceCheck_RefCondition_IsAccepted()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"COP_SEND_D",
             "join":[{"target":"SEND_NO","source":{"scope":"DETAIL","field":"S_R_NO"}}],
             "refCondition":{"logic":"AND","items":[{"type":"blank","field":{"scope":"TARGET","field":"CALLBACK_NO"},"negate":true}]},
             "lineField":"SERIAL_NO","message":"以下序号项送、退货已有回执\r\n{ROWS}"}]}
            """));

        Assert.DoesNotContain(issues, issue => issue.Contains("未知参数键") || issue.Contains("refCondition"));
    }

    [Fact]
    public void ReferenceCheck_RefConditionWithMismatch_IsRejected()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"COP_SEND_D",
             "join":[{"target":"SEND_NO","source":{"scope":"DETAIL","field":"S_R_NO"}}],
             "mismatch":{"target":"CALLBACK_NO","source":{"scope":"DETAIL","field":"S_R_NO"}},
             "refCondition":{"logic":"AND","items":[{"type":"blank","field":{"scope":"TARGET","field":"CALLBACK_NO"},"negate":true}]}}]}
            """));

        Assert.Contains(issues, issue => issue.Contains("mismatch 与 refCondition 不能同时配置"));
    }

    [Fact]
    public void ReferenceCheck_RefConditionMustBeObject()
    {
        var issues = Validate("reference-exists", Params("""
            {"checks":[{"refTable":"COP_SEND_D",
             "join":[{"target":"SEND_NO","source":{"scope":"DETAIL","field":"S_R_NO"}}],
             "refCondition":"非空"}]}
            """));

        Assert.Contains(issues, issue => issue.Contains("refCondition 必须是条件对象"));
    }

    [Fact]
    public void QuantityCheck_ModuleScopeGate_IsAccepted()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[
              {"targetTable":"T","match":[{"target":"A","source":{"scope":"DETAIL","field":"B"}}],
               "thisQty":{"scope":"DETAIL","agg":"SUM","terms":[{"field":"QTY","coef":1}]},
               "usage":{"scope":"TARGET","fields":["X"]},
               "limit":{"scope":"TARGET","fields":["Y"]},
               "switch":{"gates":[{"scope":"MODULE","key":"ERROR_NO_SAVE","expect":1},
                                  {"scope":"SYSSS","key":"FITOUT_TAG","expect":1}]}}]}
            """));

        Assert.DoesNotContain(issues, issue => issue.Contains("switch") || issue.Contains("未知参数键"));
    }

    [Fact]
    public void QuantityCheck_UnknownGateScope_IsRejected()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[
              {"targetTable":"T","match":[{"target":"A","source":{"scope":"DETAIL","field":"B"}}],
               "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
               "usage":{"scope":"TARGET","fields":["X"]},
               "limit":{"scope":"TARGET","fields":["Y"]},
               "switch":{"gates":[{"scope":"SYSDD","key":"ERROR_NO_SAVE","expect":1}]}}]}
            """));

        Assert.Contains(issues, issue => issue.Contains("scope 仅支持 SYSSS / MODULE"));
    }

    [Fact]
    public void QuantityCheck_EmptyGateList_IsRejected()
    {
        var issues = Validate("qty-not-exceed", Params("""
            {"mode":"usage-not-exceed","checks":[
              {"targetTable":"T","match":[{"target":"A","source":{"scope":"DETAIL","field":"B"}}],
               "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
               "usage":{"scope":"TARGET","fields":["X"]},
               "limit":{"scope":"TARGET","fields":["Y"]},
               "switch":{"gates":[]}}]}
            """));

        Assert.Contains(issues, issue => issue.Contains("gates 不能为空数组"));
    }
}

