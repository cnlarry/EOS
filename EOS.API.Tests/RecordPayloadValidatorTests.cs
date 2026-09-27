using EOS.API.Data;
using EOS.API.Models;
using Xunit;

namespace EOS.API.Tests;

public class RecordPayloadValidatorTests
{
    private static FormFieldDefinition Field(
        string key = "F1",
        string dataType = "nvarchar",
        bool required = false,
        bool readOnly = false,
        bool serverFilled = false,
        bool isVirtual = false,
        string? regex = null,
        string? defaultValue = null,
        bool isPrimaryKey = false,
        int? maxLength = null,
        bool displayOnly = false,
        int? precision = null,
        int? scale = null,
        IReadOnlyList<FieldChooserSource>? choosers = null) =>
        new(key, $"label-{key}", dataType, 100, null, required, null, regex, defaultValue,
            readOnly, true, false, false, null, choosers ?? [], isPrimaryKey, false, isVirtual, false, false, serverFilled, maxLength,
            DisplayOnly: displayOnly, Precision: precision, Scale: scale);

    [Fact]
    public void UnknownField_IsRejected()
    {
        var result = RecordPayloadValidator.ValidateSubmitted([Field("A")], new Dictionary<string, string?> { ["B"] = "1" });
        var error = Assert.Single(result.Errors);
        Assert.Equal("UNKNOWN_FIELD", error.Code);
        Assert.Empty(result.Converted);
    }

    [Fact]
    public void ServerFilledVirtualAndPlainReadonlyFields_AreRejected()
    {
        // 裸只读字段（界面上打不开、也没有选择器回填）同样不可提交：否则构造请求就能改写
        // "界面上只读"的业务列，例如按保密/离职标志分模块的工资表判别字段——改后记录会落到
        // 另一个模块的可见范围。
        var fields = new[] { Field("R", readOnly: true), Field("S", serverFilled: true), Field("V", isVirtual: true) };
        var result = RecordPayloadValidator.ValidateSubmitted(fields,
            new Dictionary<string, string?> { ["R"] = "1", ["S"] = "1", ["V"] = "1" });
        Assert.Equal(3, result.Errors.Count);
        Assert.All(result.Errors, error => Assert.Equal("READONLY_FIELD", error.Code));
        Assert.Empty(result.Converted);
    }

    [Fact]
    public void ReadonlyButLinkedFields_AreAccepted()
    {
        // 只读的**联动**字段仍放行，与前端 writableFields 同一把尺子：
        // 必填联动（如 CURR_RATE 汇率，由币别带出）与带选择器的回填字段（如 TAX_ID）。
        var linked = Field("CURR_RATE", dataType: "decimal", required: true, readOnly: true);
        var chosen = Field("TAX_ID", readOnly: true,
            choosers: [new FieldChooserSource(true, "TAX", "税别", null, null, null, 1)]);
        var result = RecordPayloadValidator.ValidateSubmitted([linked, chosen],
            new Dictionary<string, string?> { ["CURR_RATE"] = "1.5", ["TAX_ID"] = "T1" });
        Assert.Empty(result.Errors);
        Assert.True(result.Converted.ContainsKey("CURR_RATE"));
        Assert.True(result.Converted.ContainsKey("TAX_ID"));
    }

    [Fact]
    public void DisplayOnlyPhantomCompanion_IsRejected()
    {
        var fields = new[] { Field("CLIENT_NAME", displayOnly: true) };
        var result = RecordPayloadValidator.ValidateSubmitted(fields,
            new Dictionary<string, string?> { ["CLIENT_NAME"] = "某客户" });
        var error = Assert.Single(result.Errors);
        Assert.Equal("READONLY_FIELD", error.Code);
        Assert.Empty(result.Converted);
    }

    [Fact]
    public void AuditColumn_IsServerOwned()
    {
        Assert.True(RecordPayloadValidator.IsAuditColumn("CREATE_PERSON"));
        Assert.True(RecordPayloadValidator.IsAuditColumn("last_update_date"));
        Assert.True(RecordPayloadValidator.IsAuditColumn("OWNER"));
        Assert.True(RecordPayloadValidator.IsAuditColumn("owner_g"));
        Assert.True(RecordPayloadValidator.IsAuditColumn("CI"));
        Assert.False(RecordPayloadValidator.IsAuditColumn("REMARK"));
    }

    [Theory]
    [InlineData("int", "5", 5)]
    [InlineData("float", "3.5", 3.5)]
    [InlineData("bit", "1", true)]
    [InlineData("bit", "true", true)]
    [InlineData("nvarchar", "文本", "文本")]
    public void TypeConversion_ProducesExpectedClrValue(string dataType, string raw, object expected)
    {
        Assert.True(RecordPayloadValidator.TryConvert(dataType, raw, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void InvalidValue_IsRejected()
    {
        var result = RecordPayloadValidator.ValidateSubmitted([Field("N", dataType: "int")],
            new Dictionary<string, string?> { ["N"] = "abc" });
        var error = Assert.Single(result.Errors);
        Assert.Equal("INVALID_VALUE", error.Code);
    }

    [Fact]
    public void OverLengthValue_IsRejected()
    {
        var result = RecordPayloadValidator.ValidateSubmitted([Field("N", maxLength: 10)],
            new Dictionary<string, string?> { ["N"] = "12345678901" });
        var error = Assert.Single(result.Errors);
        Assert.Equal("VALUE_TOO_LONG", error.Code);
    }

    [Fact]
    public void DateTimeValue_NotSubjectToByteLengthLimit()
    {
        // datetime 的 max_length=8 是字节数，不是字符上限；ISO 日期 10 字符必须放行
        var result = RecordPayloadValidator.ValidateSubmitted(
            [Field("D", dataType: "datetime", maxLength: 8)],
            new Dictionary<string, string?> { ["D"] = "2026-08-23" });
        Assert.Empty(result.Errors);
        Assert.IsType<DateTime>(result.Converted["D"]);
    }

    [Fact]
    public void NumericValue_NotSubjectToByteLengthLimit()
    {
        // int 的 max_length=4 是字节数；较长数值字符串（如 12345）不得误判超长
        var result = RecordPayloadValidator.ValidateSubmitted(
            [Field("N", dataType: "int", maxLength: 4)],
            new Dictionary<string, string?> { ["N"] = "12345" });
        Assert.Empty(result.Errors);
        Assert.Equal(12345, result.Converted["N"]);
    }

    [Fact]
    public void RequiredField_MissingOrBlank_IsRejected()
    {
        var fields = new[] { Field("A", required: true), Field("B", required: true) };
        var errors = RecordPayloadValidator.CheckRequiredAndRegex(fields,
            new Dictionary<string, object?> { ["A"] = "x", ["B"] = null });
        var error = Assert.Single(errors);
        Assert.Equal("B", error.Field);
        Assert.Equal("REQUIRED_FIELD_MISSING", error.Code);
    }

    [Fact]
    public void RequiredBitField_IsNotEnforced()
    {
        var fields = new[] { Field("FLAG", dataType: "bit", required: true) };
        var errors = RecordPayloadValidator.CheckRequiredAndRegex(fields,
            new Dictionary<string, object?> { ["FLAG"] = null });
        Assert.Empty(errors);
    }

    [Fact]
    public void RegexMismatch_IsRejected()
    {
        var fields = new[] { Field("A", regex: "^[0-9]+$") };
        var errors = RecordPayloadValidator.CheckRequiredAndRegex(fields,
            new Dictionary<string, object?> { ["A"] = "abc" });
        Assert.Equal("REGEX_MISMATCH", Assert.Single(errors).Code);
    }

    [Fact]
    public void RegexTimeout_IsRejectedInsteadOfHanging()
    {
        // 回溯失控的模式不得挂住保存请求：带 match timeout 后按不匹配处理
        var fields = new[] { Field("A", regex: "^(a+)+$") };
        var errors = RecordPayloadValidator.CheckRequiredAndRegex(fields,
            new Dictionary<string, object?> { ["A"] = new string('a', 32) + "b" });
        Assert.Equal("REGEX_MISMATCH", Assert.Single(errors).Code);
    }

    [Fact]
    public void UncompilableRegexPattern_DoesNotFailSave()
    {
        // 历史元数据里的坏模式既不抛异常、也不阻断写入；语法由字段维护侧保存校验拦截
        var fields = new[] { Field("A", regex: "[") };
        var errors = RecordPayloadValidator.CheckRequiredAndRegex(fields,
            new Dictionary<string, object?> { ["A"] = "任意值" });
        Assert.Empty(errors);
    }

    [Fact]
    public void Defaults_AreAppliedServerSideOnlyForMissingWritableFields()
    {
        var fields = new[] { Field("A", dataType: "float", defaultValue: "100"), Field("B", dataType: "float", defaultValue: "100") };
        var values = new Dictionary<string, object?> { ["A"] = 1.0 };
        RecordPayloadValidator.ApplyDefaults(fields, values);
        Assert.Equal(1.0, values["A"]);
        Assert.Equal(100.0, values["B"]);
    }

    [Fact]
    public void SerialNumbers_AreAssignedSequentially_WhenMissing()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?>(),
            new Dictionary<string, object?>(),
        };
        RecordPayloadValidator.AssignSerialNumbers(rows, [Field("SERIAL_NO", dataType: "int")]);
        Assert.Equal(1, rows[0]["SERIAL_NO"]);
        Assert.Equal(2, rows[1]["SERIAL_NO"]);
    }

    [Fact]
    public void SerialNumbers_RespectProvidedValues()
    {
        var rows = new List<IDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["SERIAL_NO"] = 9 },
        };
        RecordPayloadValidator.AssignSerialNumbers(rows, [Field("SERIAL_NO", dataType: "int")]);
        Assert.Equal(9, rows[0]["SERIAL_NO"]);
    }

    // ===== trim 契约与 precision/scale 超精度校验 =====

    [Fact]
    public void TextValue_IsTrimmedOnSave()
    {
        var result = RecordPayloadValidator.ValidateSubmitted([Field("A")],
            new Dictionary<string, string?> { ["A"] = "  文本  " });
        Assert.Empty(result.Errors);
        Assert.Equal("文本", result.Converted["A"]);
    }

    [Fact]
    public void TrimmedLength_UsedForMaxLengthCheck()
    {
        // 前后空白不计入长度：trim 后 10 字符应放行（maxLength=10）
        var result = RecordPayloadValidator.ValidateSubmitted([Field("A", maxLength: 10)],
            new Dictionary<string, string?> { ["A"] = " 1234567890 " });
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DecimalScaleExceeded_IsRejected()
    {
        var field = Field("AMT", dataType: "decimal", precision: 18, scale: 2);
        var result = RecordPayloadValidator.ValidateSubmitted([field],
            new Dictionary<string, string?> { ["AMT"] = "1.999" });
        Assert.Equal("SCALE_EXCEEDED", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void DecimalScaleZero_RejectsAnyFraction()
    {
        var field = Field("QTY", dataType: "numeric", precision: 10, scale: 0);
        var result = RecordPayloadValidator.ValidateSubmitted([field],
            new Dictionary<string, string?> { ["QTY"] = "1.5" });
        Assert.Equal("SCALE_EXCEEDED", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void DecimalWithinPrecision_IsAccepted()
    {
        var field = Field("AMT", dataType: "decimal", precision: 18, scale: 2);
        var result = RecordPayloadValidator.ValidateSubmitted([field],
            new Dictionary<string, string?> { ["AMT"] = "12345678901234.56" });
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void DecimalIntegerOverflow_IsRejected()
    {
        var field = Field("AMT", dataType: "decimal", precision: 18, scale: 2);
        var result = RecordPayloadValidator.ValidateSubmitted([field],
            new Dictionary<string, string?> { ["AMT"] = "10000000000000000" });
        Assert.Equal("PRECISION_EXCEEDED", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void FloatAndMoney_NotSubjectToScaleCheck()
    {
        // float 无精度语义、money 固定 scale=4：均不启用超精度校验
        var fields = new[] { Field("F", dataType: "float"), Field("M", dataType: "money") };
        var result = RecordPayloadValidator.ValidateSubmitted(fields,
            new Dictionary<string, string?> { ["F"] = "1.123456789", ["M"] = "1.123456789" });
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ScaleError_CarriesNoRowIndex_ByDefault()
    {
        // 主表级错误 RowIndex 为 null；明细行号由 SaveDetailsAsync 附着
        var field = Field("AMT", dataType: "decimal", precision: 18, scale: 2);
        var result = RecordPayloadValidator.ValidateSubmitted([field],
            new Dictionary<string, string?> { ["AMT"] = "1.999" });
        Assert.Null(Assert.Single(result.Errors).RowIndex);
    }
}
