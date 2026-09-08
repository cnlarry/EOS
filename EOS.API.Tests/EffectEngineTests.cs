using System.Text.Json;
using EOS.API.Data.Effects;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

/// <summary>Unit tests for the effect plan loader, condition compiler and formula SQL builder (no DB).</summary>
public class EffectEngineTests
{
    private static WorkbenchDefinition Definition(JsonElement businessActions) => new(
        1607, "收料单", "PUR_RECEIVE_M", "PUR_RECEIVE_D",
        Array.Empty<WorkbenchField>(), Array.Empty<WorkbenchField>(),
        null, true, true, false, Array.Empty<string>(), string.Empty,
        HasWorkflow: false, DefinitionVersion: "module-1607-v1",
        BusinessActions: businessActions);

    private static JsonElement ActionsJson(params string[] items) =>
        JsonDocument.Parse("[" + string.Join(",", items) + "]").RootElement.Clone();

    [Fact]
    public void Load_rejects_unknown_effect_key()
    {
        var loader = new EffectPlanLoader();
        var json = ActionsJson(
            """{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"not-in-catalog"}""");
        Assert.Throws<EffectConfigException>(() => loader.Load(Definition(json)));
    }

    [Fact]
    public void Load_rejects_unknown_op_code()
    {
        var loader = new EffectPlanLoader();
        var json = ActionsJson(
            """{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"field-accumulate","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"IN_BUY_QTY","opCode":"MULTIPLY","sourceScope":"DETAIL","sourceField":"QTY"}]}""");
        Assert.Throws<EffectConfigException>(() => loader.Load(Definition(json)));
    }

    [Fact]
    public void Load_accepts_accum_with_terms_and_reverse_kind()
    {
        var loader = new EffectPlanLoader();
        var json = ActionsJson(
            """{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"adjust-projection","reverse":{"kind":"auto-reverse","note":"x"},"ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"IN_BUY_QTY","opCode":"DEACCUM","sourceScope":"DETAIL","sourceTerms":[{"field":"QTY","coef":1},{"field":"SPARE_QTY","coef":1}],"match":[{"target":"PRO_NO","source":{"scope":"DETAIL","field":"PRO_NO"}}]}]}""");
        var plan = loader.Load(Definition(json));
        var action = Assert.Single(plan.Actions);
        var op = Assert.Single(action.Ops);
        Assert.Equal(2, op.Terms!.Count);
        Assert.Equal("DEACCUM", op.OpCode);
    }

    [Fact]
    public void Load_accepts_structured_json_stored_as_text()
    {
        var loader = new EffectPlanLoader();
        // Published snapshots serialize PARAM_STRUCT / REVERSE_STRUCT / MATCH_STRUCT
        // columns as text; the loader must parse them back into element shapes.
        var json = ActionsJson(
            """{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"field-accumulate","reverse":"{\"kind\":\"auto-reverse\",\"note\":\"x\"}","params":"{\"mode\":\"x\"}","ops":[{"opSeq":1,"targetTable":"PRODUCT","targetField":"IN_BUY_QTY","opCode":"ACCUM","sourceScope":"DETAIL","sourceTerms":"[{\"field\":\"QTY\",\"coef\":1}]","match":"[{\"target\":\"PRO_NO\",\"source\":{\"scope\":\"DETAIL\",\"field\":\"PRO_NO\"}}]"}]}""");
        var plan = loader.Load(Definition(json));
        var action = Assert.Single(plan.Actions);
        var op = Assert.Single(action.Ops);
        Assert.Equal(1, op.Terms!.Count);
        Assert.Single(op.Match!);
        Assert.Equal("auto-reverse", action.Reverse!.Value.GetProperty("kind").GetString());
    }

    [Fact]
    public void Formula_builds_accum_with_terms_subquery_and_match_exists()
    {
        var executor = new EffectFormulaExecutor();
        var op = new EffectOpPlan(
            1, "PRODUCT", "IN_BUY_QTY", "ACCUM",
            new EffectSourceRef("DETAIL", null, null, null),
            null,
            new[] { new EffectTerm("QTY", 1), new EffectTerm("SPARE_QTY", 1) },
            new[] { new EffectMatchItem("PRO_NO", new EffectSourceRef("DETAIL", null, "PRO_NO", null)) },
            null, null);
        var plan = new ModuleEffectPlan(1607, "PUR_RECEIVE_M", "PUR_RECEIVE_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        plan = plan with { MasterPkOrder = new[] { "RECEIVE_TYPE", "RECEIVE_NO" } };
        var (sql, parameters) = executor.BuildUpdate(op, plan, new[] { "RT1", "R0001" });
        Assert.Contains("UPDATE T SET T.[IN_BUY_QTY] = ISNULL(T.[IN_BUY_QTY], 0) + (", sql);
        Assert.Contains("(SELECT SUM(ISNULL(D.[QTY], 0) + ISNULL(D.[SPARE_QTY], 0)) FROM dbo.[PUR_RECEIVE_D] D", sql);
        Assert.Contains("D.[PRO_NO] = T.[PRO_NO]", sql);
        Assert.Contains("EXISTS (SELECT 1 FROM dbo.[PUR_RECEIVE_D] D JOIN dbo.[PUR_RECEIVE_M] M ON D.[RECEIVE_TYPE] = M.[RECEIVE_TYPE] AND D.[RECEIVE_NO] = M.[RECEIVE_NO] WHERE", sql);
        // value subquery and the WHERE scope each bind the two key values
        Assert.Equal(4, parameters.Count(parameter => parameter.Name.StartsWith("@cp", StringComparison.Ordinal)));
    }

    [Fact]
    public void Formula_master_source_uses_document_key_not_detail_match_columns()
    {
        var executor = new EffectFormulaExecutor();
        var op = new EffectOpPlan(
            3, "PUR_PURCHASE_D", "REAL_DELIVERY_DATE", "ASSIGN",
            new EffectSourceRef("MASTER", null, "RECEIVE_DATE", null),
            null, null,
            new[]
            {
                new EffectMatchItem("PURCHASE_TYPE", new EffectSourceRef("DETAIL", null, "PURCHASE_TYPE", null)),
                new EffectMatchItem("PURCHASE_NO", new EffectSourceRef("DETAIL", null, "PURCHASE_NO", null)),
                new EffectMatchItem("SERIAL_NO", new EffectSourceRef("DETAIL", null, "PURCHASE_SERIAL_NO", null)),
            },
            null, null);
        var plan = new ModuleEffectPlan(1607, "PUR_RECEIVE_M", "PUR_RECEIVE_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        plan = plan with { MasterPkOrder = new[] { "RECEIVE_TYPE", "RECEIVE_NO" } };
        var (sql, _) = executor.BuildUpdate(op, plan, new[] { "CGSL", "SLD18070037" });
        Assert.Contains("(SELECT M.[RECEIVE_DATE] FROM dbo.[PUR_RECEIVE_M] M WHERE M.[RECEIVE_TYPE] = @cp0 AND M.[RECEIVE_NO] = @cp1)", sql);
        Assert.DoesNotContain("M.[PURCHASE_TYPE]", sql);
        Assert.DoesNotContain("SUM(ISNULL(M.[RECEIVE_DATE]", sql);
        // target rows are still positioned through the document detail rows
        Assert.Contains(
            "EXISTS (SELECT 1 FROM dbo.[PUR_RECEIVE_D] D WHERE D.[PURCHASE_TYPE] = T.[PURCHASE_TYPE] "
            + "AND D.[PURCHASE_NO] = T.[PURCHASE_NO] AND D.[PURCHASE_SERIAL_NO] = T.[SERIAL_NO])",
            sql);
    }

    [Fact]
    public void Formula_sysdatetime_marker_compiles_to_function_without_parameter()
    {
        var executor = new EffectFormulaExecutor();
        var op = new EffectOpPlan(
            1, "PUR_PURCHASE_M", "FINISHED_DATE", "SET_WHEN",
            new EffectSourceRef("CONSTANT", null, null, "SYSDATETIME"),
            null, null, null, null, null);
        var plan = new ModuleEffectPlan(1607, "PUR_RECEIVE_M", "PUR_RECEIVE_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        plan = plan with { MasterPkOrder = new[] { "RECEIVE_TYPE", "RECEIVE_NO" } };
        var (sql, parameters) = executor.BuildUpdate(op, plan, new[] { "CGSL", "SLD18070037" });
        Assert.Contains("T.[FINISHED_DATE] = SYSDATETIME()", sql);
        Assert.DoesNotContain("FINISHED_DATE] = @cp", sql);
        Assert.DoesNotContain(parameters, parameter => parameter.Value as string == "SYSDATETIME");
    }

    [Fact]
    public void Condition_negated_not_exists_compiles_to_exists()
    {
        var condition = JsonDocument.Parse(
            """{"logic":"AND","items":[{"type":"not-exists","targetTable":"COP_SEND_D","condition":{"left":{"scope":"TARGET","field":"FINISHED_TAG"},"op":"EQ","right":{"value":0}},"match":[{"target":"SEND_TYPE","source":{"field":"SEND_TYPE"}},{"target":"SEND_NO","source":{"field":"SEND_NO"}}],"negate":true}]}""")
            .RootElement.Clone();
        var fragment = new EffectConditionCompiler().Compile(
            condition, (scope, _) => scope.Equals("TARGET", StringComparison.OrdinalIgnoreCase) ? "T" : null,
            _ => true, "T");
        Assert.Contains("EXISTS (SELECT 1 FROM dbo.[COP_SEND_D]", fragment.Sql);
        Assert.DoesNotContain("NOT EXISTS", fragment.Sql);
    }

    [Fact]
    public void Formula_null_constant_compiles_to_null_parameter()
    {
        var executor = new EffectFormulaExecutor();
        var op = new EffectOpPlan(
            1, "COP_SEND_M", "FINISHED_DATE", "SET_WHEN",
            new EffectSourceRef("CONSTANT", null, null, "NULL"),
            null, null, null, null, null);
        var plan = new ModuleEffectPlan(170101, "COP_ACCOUNT_M", "COP_ACCOUNT_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        plan = plan with { MasterPkOrder = new[] { "ACCOUNT_TYPE", "ACCOUNT_NO" } };
        var (sql, parameters) = executor.BuildUpdate(op, plan, new[] { "YFDZ", "YJD2018187" });
        Assert.Contains("T.[FINISHED_DATE] = @cp", sql);
        Assert.Contains(parameters, parameter => parameter.Value is null);
    }

    [Fact]
    public void Reverse_swaps_accum_and_deaccum_on_deapprove()
    {
        var op = new EffectOpPlan(1, "PRODUCT", "F", "ACCUM",
            new EffectSourceRef("DETAIL", null, "QTY", null), null, null, null, null, null);
        var reversed = EffectFormulaExecutor.ResolveOpForEvent(op, EffectEvent.Deapprove, null);
        Assert.Equal("DEACCUM", reversed!.OpCode);
        var kept = EffectFormulaExecutor.ResolveOpForEvent(
            op, EffectEvent.Deapprove, JsonSerializer.SerializeToElement(new { kind = "recompute" }));
        Assert.Equal("ACCUM", kept!.OpCode);
        var dropped = EffectFormulaExecutor.ResolveOpForEvent(
            op, EffectEvent.Deapprove, JsonSerializer.SerializeToElement(new { kind = "no-reverse" }));
        Assert.Null(dropped);
    }

    [Fact]
    public void Deapprove_mirrors_approve_effect_chain()
    {
        Assert.True(EffectEventMapper.AppliesTo("APPROVE_EFFECT", EffectEvent.Deapprove));
        Assert.True(EffectEventMapper.AppliesTo("DEAPPROVE", EffectEvent.Deapprove));
        Assert.True(EffectEventMapper.AppliesTo("APPROVE_EFFECT", EffectEvent.ApproveEffect));
        Assert.False(EffectEventMapper.AppliesTo("SAVE", EffectEvent.Deapprove));
    }

    [Fact]
    public void Condition_compiler_rejects_expression_in_string_value()
    {
        var compiler = new EffectConditionCompiler();
        var condition = JsonSerializer.SerializeToElement(new
        {
            logic = "AND",
            items = new object[] { new { type = "field-compare", left = new { scope = "TARGET", field = "A" }, op = "GE", right = new { value = "QTY+1" } } },
        });
        var fragment = compiler.Compile(condition, (_, _) => "T", _ => true);
        Assert.Contains("@cp0", fragment.Sql);
        Assert.Equal("QTY+1", fragment.Parameters[0].Value);
    }

    [Fact]
    public void Condition_compiler_builds_switch_predicate()
    {
        var compiler = new EffectConditionCompiler();
        var condition = JsonSerializer.SerializeToElement(new
        {
            logic = "AND",
            items = new object[] { new { type = "switch", key = "PRO_MRP", value = true } },
        });
        var fragment = compiler.Compile(condition, (_, _) => null, column => column == "PRO_MRP");
        Assert.Contains("dbo.SYSSS", fragment.Sql);
        Assert.Contains("= 1", fragment.Sql);
    }

    [Fact]
    public void Condition_compare_builds_terms_sum_with_signed_coefs()
    {
        var compiler = new EffectConditionCompiler();
        var condition = JsonSerializer.SerializeToElement(new
        {
            logic = "AND",
            items = new object[] { new { type = "field-compare",
                left = new { scope = "TARGET", field = "QTY" }, op = "LE",
                right = new { scope = "TARGET", terms = new object[] {
                    new { field = "FINISHED_SEND_QTY", coef = 1 },
                    new { field = "BACK_MATERIAL", coef = 1 },
                    new { field = "BACK_BAD", coef = 1 } } } } },
        });
        var fragment = compiler.Compile(condition, (_, _) => "T", _ => true);
        Assert.Contains("T.[QTY] <=", fragment.Sql);
        Assert.Contains("COALESCE(T.[FINISHED_SEND_QTY], 0)", fragment.Sql);
        Assert.Contains("COALESCE(T.[BACK_MATERIAL], 0)", fragment.Sql);
        Assert.Contains("COALESCE(T.[BACK_BAD], 0)", fragment.Sql);
    }

    [Fact]
    public void InventoryMove_flow_direction_mirrors_on_deapprove()
    {
        Assert.Equal("I", EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMoveSql.FlowDirectionChar(1, true));
        Assert.Equal("O", EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMoveSql.FlowDirectionChar(1, false));
        Assert.Equal("O", EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMoveSql.FlowDirectionChar(-1, true));
        Assert.Equal("I", EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMoveSql.FlowDirectionChar(-1, false));
    }

    [Fact]
    public void Formula_append_uniq_binds_detail_source_in_subquery()
    {
        var executor = new EOS.API.Data.Effects.EffectFormulaExecutor();
        var plan = new EOS.API.Data.Effects.ModuleEffectPlan(
            1505, "MOC_PRODUCT_IN_M", "MOC_PRODUCT_IN_D", "v1",
            new[] { "PRODUCT_IN_TYPE", "PRODUCT_IN_NO" },
            Array.Empty<EOS.API.Data.Effects.EffectActionPlan>(),
            Array.Empty<EOS.API.Data.Effects.EffectValidationPlan>());
        var op = new EOS.API.Data.Effects.EffectOpPlan(
            4, "MOC_PRODUCE_M", "DEPOT_PLACE", "APPEND_UNIQ",
            new EOS.API.Data.Effects.EffectSourceRef("DETAIL", null, "DEPOT_PLACE", null),
            "DISTINCT", null,
            new[] { new EOS.API.Data.Effects.EffectMatchItem("PRODUCE_NO",
                new EOS.API.Data.Effects.EffectSourceRef("DETAIL", null, "PRODUCE_NO", null)) },
            null, null);
        var (sql, _) = executor.BuildUpdate(op, plan, new[] { "RK", "RK201610021" });
        Assert.Contains("SELECT MAX(", sql);
        Assert.Contains("MOC_PRODUCT_IN_D", sql);
        Assert.DoesNotContain("= D.[DEPOT_PLACE], ''", sql);
    }

    [Fact]
    public void Condition_compare_rejects_bad_coef_and_unknown_field()
    {
        var compiler = new EffectConditionCompiler();
        var badCoef = JsonSerializer.SerializeToElement(new
        {
            logic = "AND",
            items = new object[] { new { type = "field-compare",
                left = new { scope = "TARGET", field = "QTY" }, op = "LE",
                right = new { scope = "TARGET", terms = new object[] { new { field = "X", coef = 2 } } } } },
        });
        Assert.Throws<EffectConfigException>(() => compiler.Compile(badCoef, (_, _) => "T", _ => true));
        var badField = JsonSerializer.SerializeToElement(new
        {
            logic = "AND",
            items = new object[] { new { type = "field-compare",
                left = new { scope = "TARGET", field = "QTY" }, op = "LE",
                right = new { scope = "TARGET", terms = new object[] { new { field = "X;DROP", coef = 1 } } } } },
        });
        Assert.Throws<EffectConfigException>(() => compiler.Compile(badField, (_, _) => "T", _ => true));
    }
}
/// <summary>Unit tests for service handler parameter parsing and row-set generation (no DB).</summary>
public class ServiceEffectHandlerTests
{
    [Fact]
    public void InventoryMove_parses_terms_and_direction()
    {
        var plan = EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMovePlan.Parse(
            JsonSerializer.SerializeToElement(new
            {
                direction = "IN",
                mrp = false,
                fieldMap = new
                {
                    masterDate = "RECEIVE_DATE",
                    qty = new { terms = new[] { new { field = "QTY", coef = 1 }, new { field = "SPARE_QTY", coef = 1 } } },
                    detail = new[] { "SERIAL_NO", "PRO_NO", "UNIT_ID" },
                },
            }));
        Assert.Equal(1, plan.Direction);
        Assert.Equal(2, plan.QuantityTerms.Count);
        Assert.Empty(plan.RowPositiveFields);
    }

    [Fact]
    public void InventoryMove_parses_row_filter_and_rejects_unknown_keys()
    {
        var plan = EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMovePlan.Parse(
            JsonSerializer.SerializeToElement(new
            {
                direction = "IN",
                mrp = false,
                fieldMap = new
                {
                    masterDate = "RETURN_DATE",
                    qty = new { terms = new[] { new { field = "BAD_QTY", coef = 1 } } },
                    detail = new[] { "SERIAL_NO" },
                },
                rowFilter = new { anyPositive = new[] { "BAD_QTY", "BAD_SPARE_QTY" } },
            }));
        Assert.Equal(new[] { "BAD_QTY", "BAD_SPARE_QTY" }, plan.RowPositiveFields);
        Assert.Throws<EOS.API.Data.Effects.EffectConfigException>(
            () => EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMovePlan.Parse(
                JsonSerializer.SerializeToElement(new
                {
                    direction = "IN",
                    mrp = false,
                    fieldMap = new
                    {
                        masterDate = "RETURN_DATE",
                        qty = "QTY",
                        detail = new[] { "SERIAL_NO" },
                    },
                    rowFilter = new { anyPositive = new[] { "QTY" }, other = 1 },
                })));
    }

    [Fact]
    public void InventoryMove_rejects_missing_master_keys()
    {
        var plan = EOS.API.Data.Effects.ServiceEffectHandlers.InventoryMovePlan.Parse(
            JsonSerializer.SerializeToElement(new
            {
                direction = "OUT",
                fieldMap = new { masterDate = "BACK_DATE", qty = "QTY", detail = new[] { "SERIAL_NO" } },
            }));
        var modulePlan = new EOS.API.Data.Effects.ModuleEffectPlan(
            1607, "PUR_RECEIVE_M", "PUR_RECEIVE_D", "v1", new[] { "RECEIVE_TYPE", "RECEIVE_NO" },
            Array.Empty<EOS.API.Data.Effects.EffectActionPlan>(),
            Array.Empty<EOS.API.Data.Effects.EffectValidationPlan>());
        Assert.Throws<EOS.API.Data.Effects.EffectConfigException>(
            () => plan.BuildRowSet(modulePlan, Array.Empty<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PUR_RECEIVE_D.QTY", "PUR_RECEIVE_D.SERIAL_NO", "PUR_RECEIVE_D.PRO_NO", "PUR_RECEIVE_D.BACK_DATE", "PUR_RECEIVE_M.BACK_DATE", "PUR_RECEIVE_D.DEPOT_ID" }));
    }

    [Fact]
    public void Loader_skips_placeholder_op_rows()
    {
        var loader = new EOS.API.Data.Effects.EffectPlanLoader();
        var actions = JsonDocument.Parse(
            """[{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"set-state","params":{"targetTable":"MOC_PRODUCE_M","stateField":"START_TAG","stateValue":1},"ops":[{"opSeq":1}]}]""")
            .RootElement.Clone();
        var definition = new WorkbenchDefinition(
            1503, "制令", "MOC_PRODUCE_M", "MOC_PRODUCE_D",
            Array.Empty<WorkbenchField>(), Array.Empty<WorkbenchField>(),
            null, true, true, false, Array.Empty<string>(), string.Empty,
            HasWorkflow: false, DefinitionVersion: "module-1503-v1", BusinessActions: actions);
        var plan = loader.Load(definition);
        Assert.Empty(Assert.Single(plan.Actions).Ops);
    }

    [Fact]
    public void Loader_skips_placeholder_op_rows_with_empty_strings()
    {
        var loader = new EOS.API.Data.Effects.EffectPlanLoader();
        var actions = JsonDocument.Parse(
            """[{"seq":1,"eventCode":"APPROVE_EFFECT","effectKey":"inventory-move","ops":[{"opSeq":1,"opCode":"","targetTable":"","targetField":""}]}]""")
            .RootElement.Clone();
        var definition = new WorkbenchDefinition(
            130102, "期初开帐单", "INV_OCCUR_INIT_M", "INV_OCCUR_INIT_D",
            Array.Empty<WorkbenchField>(), Array.Empty<WorkbenchField>(),
            null, true, true, false, Array.Empty<string>(), string.Empty,
            HasWorkflow: false, DefinitionVersion: "module-130102-v3", BusinessActions: actions);
        var plan = loader.Load(definition);
        Assert.Empty(Assert.Single(plan.Actions).Ops);
    }

    [Fact]
    public void Callback_reprice_is_implemented_and_targets_schema_validated()
    {
        Assert.True(EOS.API.Data.Effects.EffectRegistry.IsImplemented("callback-reprice"));
        var ok = """{"sendTargets":[{"detail":"COP_SEND_D","master":"COP_SEND_M","typeCol":"SEND_TYPE","noCol":"SEND_NO","serialCol":"SERIAL_NO","copyFromCallback":["PRICE"],"amountTo":["AMOUNT","AMOUNT_TAX","TAX_SUM"],"markFromDoc":["CALLBACK_TYPE","CALLBACK_SERIAL_NO"]}],"duplicateGuardMarked":true,"deapprove":"clear-mark"}""";
        Assert.Empty(EOS.API.Data.EffectStructSchemas.ValidateParams("callback-reprice", ok));
        var bad = EOS.API.Data.EffectStructSchemas.ValidateParams("callback-reprice",
            """{"sendTargets":[{"detail":"COP_SEND_D","master":"COP_SEND_M","typo":"X"}],"nonsense":1}""");
        Assert.Contains(bad, issue => issue.Contains("'nonsense'"));
        Assert.Contains(bad, issue => issue.Contains(".typeCol 不能为空"));
    }

    [Fact]
    public void Payment_date_calc_is_implemented_and_schema_accepts_clear_on_deapprove()
    {
        Assert.True(EOS.API.Data.Effects.EffectRegistry.IsImplemented("payment-date-calc"));
        Assert.Empty(EOS.API.Data.EffectStructSchemas.ValidateParams("payment-date-calc",
            """{"targetField":"PRE_RECEIVE_DATE","monthField":"ACCOUNT_MONTH","dateField":"ACCOUNT_DATE","paymentDaysFrom":"CLIENT.PAYMENT_DAY"}"""));
        Assert.Empty(EOS.API.Data.EffectStructSchemas.ValidateReverse("""{"kind":"clear-on-deapprove"}"""));
        var bad = EOS.API.Data.EffectStructSchemas.ValidateParams("payment-date-calc",
            """{"targetField":"pre_receive_date","monthField":"ACCOUNT_MONTH","dateField":"ACCOUNT_DATE","paymentDaysFrom":"client.payment_day"}""");
        Assert.Contains(bad, issue => issue.Contains("必须是全大写标识符"));
        Assert.Contains(bad, issue => issue.Contains("'TABLE.COLUMN'"));
    }

    [Fact]
    public void Payment_date_calc_parse_resolves_party_key_and_rejects_missing_columns()
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "COP_ACCOUNT_M.ACCOUNT_TYPE",
            "COP_ACCOUNT_M.ACCOUNT_NO",
            "COP_ACCOUNT_M.CLIENT_ID",
            "COP_ACCOUNT_M.PRE_RECEIVE_DATE",
            "COP_ACCOUNT_M.ACCOUNT_MONTH",
            "COP_ACCOUNT_M.ACCOUNT_DATE",
            "CLIENT.CLIENT_ID",
            "CLIENT.PAYMENT_DAY",
        };
        var plan = new EOS.API.Data.Effects.ModuleEffectPlan(
            170101, "COP_ACCOUNT_M", null, "module-170101-v3",
            new[] { "ACCOUNT_TYPE", "ACCOUNT_NO" }, Array.Empty<EOS.API.Data.Effects.EffectActionPlan>(),
            Array.Empty<EOS.API.Data.Effects.EffectValidationPlan>());
        var root = JsonDocument.Parse(
            """{"targetField":"PRE_RECEIVE_DATE","monthField":"ACCOUNT_MONTH","dateField":"ACCOUNT_DATE","paymentDaysFrom":"CLIENT.PAYMENT_DAY"}""")
            .RootElement.Clone();
        var config = EOS.API.Data.Effects.ServiceEffectHandlers.PaymentDateConfig.Parse(root, plan, columns);
        Assert.Equal("PRE_RECEIVE_DATE", config.TargetField);
        Assert.Equal("CLIENT", config.PartyTable);
        Assert.Equal("CLIENT_ID", config.PartyKeyColumn);
        Assert.Equal("PAYMENT_DAY", config.DaysColumn);

        var missing = new HashSet<string>(columns) { "CLIENT.PAYMENT_DAY" };
        missing.Remove("CLIENT.PAYMENT_DAY");
        var exception = Assert.Throws<EOS.API.Data.Effects.EffectConfigException>(
            () => EOS.API.Data.Effects.ServiceEffectHandlers.PaymentDateConfig.Parse(root, plan, missing));
        Assert.Contains("主档天数列不存在", exception.Message);
    }

    [Fact]
    public void Payment_date_calc_action_loads_with_placeholder_op_and_clear_reverse()
    {
        var loader = new EOS.API.Data.Effects.EffectPlanLoader();
        var actions = JsonDocument.Parse(
            """[{"seq":3,"eventCode":"APPROVE_EFFECT","effectKey":"payment-date-calc","effectName":"账期推算","enabled":true,"failMode":"BLOCK","params":{"targetField":"PRE_RECEIVE_DATE","monthField":"ACCOUNT_MONTH","dateField":"ACCOUNT_DATE","paymentDaysFrom":"CLIENT.PAYMENT_DAY"},"reverse":{"kind":"clear-on-deapprove"},"ops":[{"opSeq":1,"opCode":"","targetTable":"","targetField":""}]}]""")
            .RootElement.Clone();
        var definition = new WorkbenchDefinition(
            170101, "应收货款单", "COP_ACCOUNT_M", "COP_ACCOUNT_D",
            Array.Empty<WorkbenchField>(), Array.Empty<WorkbenchField>(),
            null, true, true, false, Array.Empty<string>(), string.Empty,
            HasWorkflow: false, DefinitionVersion: "module-170101-v3", BusinessActions: actions);
        var action = Assert.Single(loader.Load(definition).Actions);
        Assert.Equal("payment-date-calc", action.EffectKey);
        Assert.Empty(action.Ops);
        Assert.Equal("clear-on-deapprove", action.Reverse!.Value.GetProperty("kind").GetString());
    }
    [Fact]
    public void Price_sync_keys_implemented_and_schema_validated()
    {
        Assert.True(EOS.API.Data.Effects.EffectRegistry.IsImplemented("client-price-sync"));
        Assert.True(EOS.API.Data.Effects.EffectRegistry.IsImplemented("supplier-price-sync"));
        var ok = """{"master":"CLIENT_PRICE_M","detail":"CLIENT_PRICE_D","preserveOld":true,"quoteRefs":["QUOTE_TYPE","QUOTE_NO","QUOTE_SERIAL_NO"],"overwriteIfNewer":false}""";
        Assert.Empty(EOS.API.Data.EffectStructSchemas.ValidateParams("client-price-sync", ok));
        Assert.Empty(EOS.API.Data.EffectStructSchemas.ValidateReverse("""{"kind":"restore-old-price","note":"还原旧价"}"""));
        var bad = EOS.API.Data.EffectStructSchemas.ValidateParams("client-price-sync", """{"master":"CLIENT_PRICE_M","detail":"CLIENT_PRICE_D","quoteRefs":[]}""");
        Assert.Contains(bad, issue => issue.Contains("quoteRefs 必须是非空数组"));
    }

    [Fact]
    public void Price_sync_parse_resolves_party_and_rejects_missing_columns()
    {
        var plan = new ModuleEffectPlan(1404, "COP_QUOTE_M", "COP_QUOTE_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        plan = plan with { MasterPkOrder = new[] { "QUOTE_TYPE", "QUOTE_NO" } };
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CLIENT_PRICE_M", "CLIENT_PRICE_M.CLIENT_ID", "CLIENT_PRICE_M.CREATE_PERSON", "CLIENT_PRICE_M.CREATE_DATE",
            "CLIENT_PRICE_D", "CLIENT_PRICE_D.CLIENT_ID", "CLIENT_PRICE_D.PRO_NO", "CLIENT_PRICE_D.UNIT_ID",
            "CLIENT_PRICE_D.CURR_ID", "CLIENT_PRICE_D.TAX_ID", "CLIENT_PRICE_D.TAX_TYPE", "CLIENT_PRICE_D.REBATE",
            "CLIENT_PRICE_D.PRICE", "CLIENT_PRICE_D.OLD_PRICE", "CLIENT_PRICE_D.VERIFY_PRICE_DATE", "CLIENT_PRICE_D.IN_EFFECT_DATE",
            "CLIENT_PRICE_D.CLIENT_PRO_NO", "CLIENT_PRICE_D.QUOTE_TYPE", "CLIENT_PRICE_D.QUOTE_NO", "CLIENT_PRICE_D.QUOTE_SERIAL_NO",
            "COP_QUOTE_M", "COP_QUOTE_M.CLIENT_ID", "COP_QUOTE_M.QUOTE_DATE", "COP_QUOTE_M.IN_EFFECT_DATE",
            "COP_QUOTE_M.QUOTE_TYPE", "COP_QUOTE_M.QUOTE_NO", "COP_QUOTE_D", "COP_QUOTE_D.SERIAL_NO", "COP_QUOTE_D.PRO_NO",
        };
        var ok = JsonDocument.Parse("""{"master":"CLIENT_PRICE_M","detail":"CLIENT_PRICE_D","preserveOld":true,"quoteRefs":["QUOTE_TYPE","QUOTE_NO","QUOTE_SERIAL_NO"]}""").RootElement.Clone();
        var cfg = EOS.API.Data.Effects.ServiceEffectHandlers.PriceSyncConfig.Parse(ok, plan, columns);
        Assert.Equal("CLIENT_ID", cfg.PartyColumn);
        Assert.Equal("CLIENT_PRO_NO", cfg.PartyProNo);
        var missing = new HashSet<string>(columns.Where(item => item != "CLIENT_PRICE_D.OLD_PRICE"));
        Assert.Throws<EOS.API.Data.Effects.EffectConfigException>(
            () => EOS.API.Data.Effects.ServiceEffectHandlers.PriceSyncConfig.Parse(ok, plan, missing));
    }
    [Fact]
    public void Quote_parameter_recalc_key_implemented_and_schema_validated()
    {
        Assert.True(EOS.API.Data.Effects.EffectRegistry.IsImplemented("quote-parameter-recalc"));
        var ok = """{"targetTable":"COP_QUOTE_PARAMETER","mode":"recalc-confirmed","feeFields":["MATERIAL_SUM","PRICE","TAX_SUM"]}""";
        Assert.Empty(EOS.API.Data.EffectStructSchemas.ValidateParams("quote-parameter-recalc", ok));
        Assert.Empty(EOS.API.Data.EffectStructSchemas.ValidateReverse("""{"kind":"recalc-confirmed","note":"按已确认生效报价集重算"}"""));
        var bad = EOS.API.Data.EffectStructSchemas.ValidateParams("quote-parameter-recalc", """{"targetTable":"COP_QUOTE_PARAMETER","mode":"recalc-confirmed","feeFields":[]}""");
        Assert.Contains(bad, issue => issue.Contains("quote-parameter-recalc.feeFields 不能为空"));
    }

    [Fact]
    public void Quote_parameter_recalc_parse_resolves_fee_fields_and_rejects_missing_columns()
    {
        var plan = new ModuleEffectPlan(1604, "PUR_QUOTE_M", "PUR_QUOTE_D", "v1",
            Array.Empty<string>(), Array.Empty<EffectActionPlan>(), Array.Empty<EffectValidationPlan>());
        plan = plan with { MasterPkOrder = new[] { "QUOTE_TYPE", "QUOTE_NO" } };
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "COP_QUOTE_PARAMETER", "COP_QUOTE_PARAMETER.STUFF_ID", "COP_QUOTE_PARAMETER.MATERIAL_SUM",
            "COP_QUOTE_PARAMETER.MATERIAL_PCT", "COP_QUOTE_PARAMETER.PRICE", "COP_QUOTE_PARAMETER.TAX_SUM",
            "COP_QUOTE_PARAMETER.TAX_PCT", "STUFF", "STUFF.STUFF_ID",
            "PUR_QUOTE_M", "PUR_QUOTE_M.QUOTE_TYPE", "PUR_QUOTE_M.QUOTE_NO", "PUR_QUOTE_M.CONFIRM_TAG",
            "PUR_QUOTE_M.IN_EFFECT_DATE", "PUR_QUOTE_D", "PUR_QUOTE_D.PRO_NO", "PUR_QUOTE_D.PRICE",
            "PUR_QUOTE_D.CURR_RATE", "PUR_QUOTE_D.TAX_TYPE", "PUR_QUOTE_D.TAX_RATE",
            "PUR_QUOTE_D.QUOTE_TYPE", "PUR_QUOTE_D.QUOTE_NO",
        };
        var ok = JsonDocument.Parse("""{"targetTable":"COP_QUOTE_PARAMETER","mode":"recalc-confirmed","feeFields":["TAX_SUM"]}""").RootElement.Clone();
        var cfg = EOS.API.Data.Effects.ServiceEffectHandlers.QuoteParameterConfig.Parse(ok, plan, columns);
        Assert.Equal("COP_QUOTE_PARAMETER", cfg.TargetTable);
        Assert.Equal(new[] { "TAX_SUM" }, cfg.FeeFields);
        var missing = new HashSet<string>(columns.Where(item => item != "COP_QUOTE_PARAMETER.TAX_PCT"));
        Assert.Throws<EOS.API.Data.Effects.EffectConfigException>(
            () => EOS.API.Data.Effects.ServiceEffectHandlers.QuoteParameterConfig.Parse(ok, plan, missing));
    }
}

public class EffectEngineGateTests
{
    [Fact]
    public void Definition_engine_flag_requires_effectEngine_enabled_true()
    {
        EOS.API.Data.Effects.EffectEngineSettings settings = new() { Enabled = true };
        var invoker = new EOS.API.Data.Effects.EffectEngineInvoker(
            settings, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<EOS.API.Data.Effects.EffectEngineInvoker>.Instance);
        var off = DefinitionWith(JsonDocument.Parse("{}").RootElement.Clone());
        var on = DefinitionWith(JsonDocument.Parse("{\"enabled\":true}").RootElement.Clone());
        Assert.False(invoker.IsEnabledFor(off));
        Assert.True(invoker.IsEnabledFor(on));
    }

    [Fact]
    public void Global_master_switch_gates_everything()
    {
        var invoker = new EOS.API.Data.Effects.EffectEngineInvoker(
            new EOS.API.Data.Effects.EffectEngineSettings { Enabled = false }, null!, null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<EOS.API.Data.Effects.EffectEngineInvoker>.Instance);
        Assert.False(invoker.IsEnabledFor(DefinitionWith(JsonDocument.Parse("{\"enabled\":true}").RootElement.Clone())));
    }

    private static WorkbenchDefinition DefinitionWith(JsonElement effectEngine) => new(
        1607, "收料单", "PUR_RECEIVE_M", null,
        Array.Empty<WorkbenchField>(), Array.Empty<WorkbenchField>(),
        null, true, true, false, Array.Empty<string>(), string.Empty,
        HasWorkflow: false, EffectEngine: effectEngine);
}
