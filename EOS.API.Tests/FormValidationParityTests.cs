using System.Globalization;
using System.Text.Json;
using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// ADR-006 决策 2.4 校验单一来源对拍矩阵（服务端侧）：
/// 消费共享 fixture `form-validation-parity.json`，按保存管线
/// （ValidateSubmitted → ApplyDefaults(无默认) → CheckRequiredAndRegex）复现每个用例，
/// 判定码与规范化值必须与前端 evaluateField 结论一致。规则变更必须先改 fixture。
/// </summary>
public class FormValidationParityTests
{
    public sealed record ParityFile(List<ParityCase> Cases);
    public sealed record ParityCase(string Name, ParityField Field, string? Input, string ExpectedCode, string? ExpectedValue);
    public sealed record ParityField(string Key, string DataType, int? MaxLength, bool? IsRequired, string? Regex, int? Precision, int? Scale);

    private static string FindFixturePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && directory is not null; depth++, directory = directory.Parent)
        {
            var direct = Path.Combine(directory.FullName, "form-validation-parity.json");
            if (directory.Name == "EOS.API.Tests" && File.Exists(direct)) return direct;
            var nested = Path.Combine(directory.FullName, "EOS.API.Tests", "form-validation-parity.json");
            if (File.Exists(nested)) return nested;
        }
        throw new InvalidOperationException("未找到 form-validation-parity.json（对拍 fixture）。");
    }

    private static FormFieldDefinition ToDefinition(ParityField field) =>
        new(field.Key, $"label-{field.Key}", field.DataType, 100, null,
            IsRequired: field.IsRequired ?? false, VerifyIndex: null, Regex: field.Regex, DefaultValue: null,
            IsReadonly: false, IsVisible: true, OnlyChoose: false, ChooseMultiple: false, ChoosePage: null,
            Choosers: Array.Empty<FieldChooserSource>(), IsPrimaryKey: false, IsAutoIncrement: false, IsVirtual: false,
            IsCost: false, IsSecrecy: false, ServerFilled: false, MaxLength: field.MaxLength,
            Precision: field.Precision, Scale: field.Scale);

    /// <summary>服务端转换值 → 与客户端 canonicalize 输出可比的不变文化串（date 类型仅日期部分）。</summary>
    private static string Serialize(object? value, string dataType) => value switch
    {
        null => string.Empty,
        bool bit => bit ? "1" : "0",
        DateTime dateTime when dataType.Equals("date", StringComparison.OrdinalIgnoreCase)
            => dateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        DateTime dateTime => dateTime.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
        decimal number => number.ToString(CultureInfo.InvariantCulture),
        double number => number.ToString(CultureInfo.InvariantCulture),
        int number => number.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    public static IEnumerable<object[]> Cases()
    {
        var path = FindFixturePath();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip };
        var file = JsonSerializer.Deserialize<ParityFile>(File.ReadAllText(path), options) ?? throw new InvalidOperationException("fixture 解析失败。");
        foreach (var parityCase in file.Cases) yield return new object[] { parityCase };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ServerPipeline_MatchesFixtureVerdict(ParityCase parityCase)
    {
        var fields = new[] { ToDefinition(parityCase.Field) };
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (parityCase.Input is not null) values[parityCase.Field.Key] = parityCase.Input;

        var validation = RecordPayloadValidator.ValidateSubmitted(fields, values);
        string actualCode;
        object? converted = null;
        if (validation.Errors.Count > 0)
        {
            actualCode = validation.Errors[0].Code;
        }
        else
        {
            var merged = new Dictionary<string, object?>(validation.Converted, StringComparer.OrdinalIgnoreCase);
            var finalErrors = RecordPayloadValidator.CheckRequiredAndRegex(fields, merged);
            if (finalErrors.Count > 0)
            {
                actualCode = finalErrors[0].Code;
            }
            else
            {
                actualCode = "ok";
                merged.TryGetValue(parityCase.Field.Key, out converted);
            }
        }

        Assert.True(actualCode == parityCase.ExpectedCode,
            $"用例「{parityCase.Name}」判定不一致：期望 {parityCase.ExpectedCode}，实际 {actualCode}。");
        if (parityCase.ExpectedCode == "ok" && parityCase.ExpectedValue is not null)
        {
            var serialized = Serialize(converted, parityCase.Field.DataType);
            Assert.True(serialized == parityCase.ExpectedValue,
                $"用例「{parityCase.Name}」规范化值不一致：期望 {parityCase.ExpectedValue}，实际 {serialized}。");
        }
    }
}
