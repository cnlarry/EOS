using EOS.API.Data;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

public class ModuleBusinessConfigValidatorTests
{
    [Fact]
    public void Validate_Valid1607LikeConfig_ReturnsNoIssues()
    {
        var request = new SaveModuleBusinessConfigRequest(
            [
                new BusinessActionDto(
                    1,
                    "APPROVE_EFFECT",
                    "field-accumulate",
                    "收料量回写采购单",
                    Ops:
                    [
                        new BusinessActionOpDto(
                            1,
                            "PUR_PURCHASE_D",
                            "RECEIVE_QTY",
                            "ACCUM",
                            "DETAIL",
                            SourceField: "QTY",
                            SourceAgg: "SUM",
                            Match: """[{"target":"PURCHASE_TYPE","source":{"scope":"DETAIL","field":"PURCHASE_TYPE"}}]""")
                    ])
            ],
            [
                new ModuleValidationRuleDto(
                    1,
                    "APPROVE",
                    "qty-not-exceed",
                    Params: """
                    {"mode":"usage-not-exceed","checks":[{"match":[{"target":"SERIAL_NO","source":{"scope":"DETAIL","field":"PURCHASE_SERIAL_NO"}}],
                    "thisQty":{"scope":"DETAIL","terms":[{"field":"QTY","coef":1}]},
                    "usage":{"scope":"TARGET","fields":["RECEIVE_QTY"]},
                    "limit":{"scope":"TARGET","fields":["QTY"]}}]}
                    """)
            ]);

        var issues = ModuleBusinessConfigValidator.Validate(request);

        Assert.Empty(issues);
    }

    [Fact]
    public void Validate_UnknownEffectKeyAndOpCode_ReturnsIssues()
    {
        var request = new SaveModuleBusinessConfigRequest(
            [
                new BusinessActionDto(1, "APPROVE_EFFECT", "not-a-key",
                    Ops:
                    [
                        new BusinessActionOpDto(1, "A", "B", "MAGIC", "DETAIL", SourceField: "X")
                    ])
            ],
            []);

        var issues = ModuleBusinessConfigValidator.Validate(request);

        Assert.Contains(issues, issue => issue.Contains("未知效果键"));
        Assert.Contains(issues, issue => issue.Contains("未知运算"));
    }

    [Fact]
    public void Validate_OrderLint_AcceptsWriteBackBeforeCompletionAndInventory()
    {
        var request = new SaveModuleBusinessConfigRequest(
            [
                new BusinessActionDto(1, "APPROVE_EFFECT", "field-accumulate", Ops: []),
                new BusinessActionDto(2, "APPROVE_EFFECT", "completion-close", Ops: []),
                new BusinessActionDto(3, "APPROVE_EFFECT", "inventory-move", Ops: []),
            ],
            []);
        var issues = ModuleBusinessConfigValidator.Validate(request);
        Assert.DoesNotContain(issues, issue => issue.Contains("顺序 lint"));
    }

    [Fact]
    public void Validate_OrderLint_AllowsInventoryWithoutWriteBack()
    {
        // A pure inventory module (e.g. opening balance) has no write-back step in the
        // chain; the prerequisite applies only when a write-back step exists.
        var request = new SaveModuleBusinessConfigRequest(
            [
                new BusinessActionDto(1, "APPROVE_EFFECT", "inventory-move", Ops: []),
            ],
            []);
        var issues = ModuleBusinessConfigValidator.Validate(request);
        Assert.DoesNotContain(issues, issue => issue.Contains("顺序 lint"));
    }

    [Fact]
    public void Validate_RejectsWriteBackAfterInventory()
    {
        var request = new SaveModuleBusinessConfigRequest(
            [
                new BusinessActionDto(1, "APPROVE_EFFECT", "inventory-move", Ops: []),
                new BusinessActionDto(2, "APPROVE_EFFECT", "field-accumulate", Ops: []),
            ],
            []);
        var issues = ModuleBusinessConfigValidator.Validate(request);
        Assert.Contains(issues, issue => issue.Contains("顺序 lint"));
    }

    [Fact]
    public void Validate_ToleratesPlaceholderOpOnServiceEffect()
    {
        var request = new SaveModuleBusinessConfigRequest(
            [
                new BusinessActionDto(
                    1, "APPROVE_EFFECT", "inventory-move", Ops:
                    [
                        new BusinessActionOpDto(1, "", "", "", "", SourceField: null),
                    ]),
            ],
            []);
        var issues = ModuleBusinessConfigValidator.Validate(request);
        Assert.DoesNotContain(issues, issue => issue.Contains("公式行"));
    }

    [Fact]
    public void Validate_DuplicateEventSeqAndRuleStageSeq_ReturnsIssues()
    {
        var request = new SaveModuleBusinessConfigRequest(
            [
                new BusinessActionDto(1, "APPROVE_EFFECT", "inventory-move"),
                new BusinessActionDto(1, "APPROVE_EFFECT", "set-state")
            ],
            [
                new ModuleValidationRuleDto(1, "SAVE", "duplicate-check",
                    Params: """{"mode":"entity","table":"T","keyFields":["K"]}"""),
                new ModuleValidationRuleDto(1, "SAVE", "reference-exists",
                    Params: """{"checks":[{"refTable":"R","refKey":{"scope":"MASTER","field":"F"}}]}""")
            ]);

        var issues = ModuleBusinessConfigValidator.Validate(request);

        Assert.Contains(issues, issue => issue.Contains("顺序号重复"));
        Assert.Equal(2, issues.Count(issue => issue.Contains("顺序号重复")));
    }

    [Fact]
    public void Validate_ScopeCombinations_AreEnforced()
    {
        var missingConstant = new BusinessActionDto(1, "SAVE", "set-state",
            Ops: [new BusinessActionOpDto(1, "M", "F", "SET_WHEN", "CONSTANT")]);
        var constantWithField = new BusinessActionDto(2, "SAVE", "set-state",
            Ops: [new BusinessActionOpDto(1, "M", "F", "SET_WHEN", "CONSTANT",
                SourceConstant: "1", SourceField: "QTY")]);
        var tableWithoutTable = new BusinessActionDto(3, "SAVE", "field-copy",
            Ops: [new BusinessActionOpDto(1, "M", "F", "ASSIGN", "TABLE", SourceField: "X")]);
        var detailWithoutField = new BusinessActionDto(4, "SAVE", "field-copy",
            Ops: [new BusinessActionOpDto(1, "M", "F", "ASSIGN", "DETAIL")]);
        var badCoef = new BusinessActionDto(5, "SAVE", "field-copy",
            Ops: [new BusinessActionOpDto(1, "M", "F", "ASSIGN", "DETAIL",
                SourceTerms: """[{"field":"QTY","coef":2}]""")]);

        var request = new SaveModuleBusinessConfigRequest(
            [missingConstant, constantWithField, tableWithoutTable, detailWithoutField, badCoef], []);
        var issues = ModuleBusinessConfigValidator.Validate(request);

        Assert.Contains(issues, issue => issue.Contains("必须提供 sourceConstant"));
        Assert.Contains(issues, issue => issue.Contains("不得再提供字段"));
        Assert.Contains(issues, issue => issue.Contains("必须提供 sourceTable"));
        Assert.Contains(issues, issue => issue.Contains("需要 sourceField 或 sourceTerms"));
        Assert.Contains(issues, issue => issue.Contains("非法 coef"));
    }

    [Fact]
    public void Validate_InvalidJsonAndUnknownValidationKey_ReturnsIssues()
    {
        var request = new SaveModuleBusinessConfigRequest(
            [
                new BusinessActionDto(1, "APPROVE_EFFECT", "inventory-move",
                    Params: "{not-json")
            ],
            [
                new ModuleValidationRuleDto(1, "SAVE", "no-such-template",
                    Params: "{}")
            ]);

        var issues = ModuleBusinessConfigValidator.Validate(request);

        Assert.Contains(issues, issue => issue.Contains("不是合法 JSON"));
        Assert.Contains(issues, issue => issue.Contains("未知校验模板键") || issue.Contains("no-such-template"));
    }
}
