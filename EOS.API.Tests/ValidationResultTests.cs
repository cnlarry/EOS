using EOS.API.Validation;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 共用校验结果契约：Ok / Messages 是全部校验器与调用方共同依赖的两个面，
/// 这里把它们钉死，避免契约本身被改动时各处静默走偏。
/// </summary>
public sealed class ValidationResultTests
{
    [Fact]
    public void Success_HasNoIssuesAndOk()
    {
        Assert.True(ValidationResult.Success.Ok);
        Assert.Empty(ValidationResult.Success.Messages);
        Assert.Empty(ValidationResult.Success.Issues);
    }

    [Fact]
    public void Add_AppendsIssueAndFlipsOk()
    {
        var result = new ValidationResult();

        result.Add("字段 A 无效。", code: "INVALID_FIELD", field: "A");

        Assert.False(result.Ok);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("字段 A 无效。", issue.Message);
        Assert.Equal("INVALID_FIELD", issue.Code);
        Assert.Equal("A", issue.Field);
        Assert.Equal(ValidationSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Add_WithoutCodeAndField_LeavesThemNull()
    {
        var result = new ValidationResult();

        result.Add("仅消息。");

        var issue = Assert.Single(result.Issues);
        Assert.Null(issue.Code);
        Assert.Null(issue.Field);
    }

    [Fact]
    public void Messages_ProjectsIssueMessagesInOrder()
    {
        var result = new ValidationResult();
        result.Add("第一条。");
        result.Add("第二条。");

        Assert.Equal(["第一条。", "第二条。"], result.Messages);
    }

    [Fact]
    public void AddRange_MergesAnotherResult()
    {
        var first = ValidationResult.FromMessages(["甲"]);
        var second = ValidationResult.FromMessages(["乙", "丙"]);

        first.AddRange(second);

        Assert.Equal(["甲", "乙", "丙"], first.Messages);
    }

    [Fact]
    public void FromMessages_EmptySource_IsOk()
    {
        Assert.True(ValidationResult.FromMessages([]).Ok);
    }

    [Fact]
    public void Constructor_FromIssues_PreservesSeverity()
    {
        var result = new ValidationResult([new ValidationIssue("提示项", Severity: ValidationSeverity.Warning)]);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
        Assert.False(result.Ok);
    }
}
