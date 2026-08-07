using EOS.API.Data;
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
        int? maxLength = null) =>
        new(key, $"label-{key}", dataType, 100, null, required, null, regex, defaultValue,
            readOnly, true, false, false, null, [], isPrimaryKey, false, isVirtual, false, false, serverFilled, maxLength);

    [Fact]
    public void UnknownField_IsRejected()
    {
        var result = RecordPayloadValidator.ValidateSubmitted([Field("A")], new Dictionary<string, string?> { ["B"] = "1" });
        var error = Assert.Single(result.Errors);
        Assert.Equal("UNKNOWN_FIELD", error.Code);
        Assert.Empty(result.Converted);
    }

    [Fact]
    public void ReadonlyAndServerFilledFields_AreRejected()
    {
        var fields = new[] { Field("R", readOnly: true), Field("S", serverFilled: true), Field("V", isVirtual: true) };
        var result = RecordPayloadValidator.ValidateSubmitted(fields,
            new Dictionary<string, string?> { ["R"] = "1", ["S"] = "1", ["V"] = "1" });
        Assert.Equal(3, result.Errors.Count);
        Assert.All(result.Errors, error => Assert.Equal("READONLY_FIELD", error.Code));
    }

    [Fact]
    public void AuditColumn_IsServerOwned()
    {
        Assert.True(RecordPayloadValidator.IsAuditColumn("CREATE_PERSON"));
        Assert.True(RecordPayloadValidator.IsAuditColumn("last_update_date"));
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
}
