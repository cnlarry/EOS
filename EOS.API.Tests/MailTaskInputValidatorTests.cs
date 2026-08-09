using EOS.API.Models;
using EOS.API.Services;
using Xunit;

namespace EOS.API.Tests;

public sealed class MailTaskInputValidatorTests
{
    [Fact]
    public void Create_ValidInput_Passes()
    {
        var request = new MailTaskCreateRequest
        {
            Title = "确认 8 月付款计划",
            Description = "8 月采购付款计划已生成",
            SourceMailbox = "lina@eos-corp.com",
            SourceMessageId = "<abc@eos-corp.com>",
            SourceSubject = "请确认 8 月采购付款计划",
            DueDate = DateTimeOffset.UtcNow.AddDays(1),
            Priority = 1,
        };

        Assert.Empty(MailTaskInputValidator.ValidateCreate(request));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_EmptyTitle_ReturnsError(string title)
    {
        var request = new MailTaskCreateRequest { Title = title };
        Assert.Contains("任务标题不能为空", MailTaskInputValidator.ValidateCreate(request));
    }

    [Fact]
    public void Create_OverlongFields_ReturnsErrors()
    {
        var request = new MailTaskCreateRequest
        {
            Title = new string('长', MailTaskInputValidator.MaxTitleLength + 1),
            Description = new string('描', MailTaskInputValidator.MaxDescriptionLength + 1),
            SourceSubject = new string('题', MailTaskInputValidator.MaxSourceFieldLength + 1),
            SourceMessageId = new string('m', MailTaskInputValidator.MaxSourceFieldLength + 1),
            SourceMailbox = new string('a', MailTaskInputValidator.MaxSourceFieldLength + 1),
            Priority = 4,
            DueDate = new DateTimeOffset(1999, 12, 31, 0, 0, 0, TimeSpan.Zero),
        };

        var errors = MailTaskInputValidator.ValidateCreate(request);
        Assert.Contains(errors, e => e.Contains("任务标题不能超过"));
        Assert.Contains(errors, e => e.Contains("任务描述不能超过"));
        Assert.Contains(errors, e => e.Contains("来源邮箱不能超过"));
        Assert.Contains(errors, e => e.Contains("来源邮件标识不能超过"));
        Assert.Contains(errors, e => e.Contains("来源邮件主题不能超过"));
        Assert.Contains(errors, e => e.Contains("优先级必须是"));
        Assert.Contains(errors, e => e.Contains("截止时间必须"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Create_InvalidPriority_ReturnsError(int priority)
    {
        var request = new MailTaskCreateRequest { Title = "任务", Priority = priority };
        Assert.Contains(MailTaskInputValidator.ValidateCreate(request), e => e.Contains("优先级必须是"));
    }

    [Fact]
    public void Update_NullFields_Passes()
    {
        var request = new MailTaskUpdateRequest { ClearDueDate = false };
        Assert.Empty(MailTaskInputValidator.ValidateUpdate(request));
    }

    [Fact]
    public void Update_EmptyTitle_ReturnsError()
    {
        var request = new MailTaskUpdateRequest { Title = " " };
        Assert.Contains("任务标题不能为空", MailTaskInputValidator.ValidateUpdate(request));
    }

    [Fact]
    public void Update_InvalidPriority_ReturnsError()
    {
        var request = new MailTaskUpdateRequest { Priority = 9 };
        Assert.Contains(MailTaskInputValidator.ValidateUpdate(request), e => e.Contains("优先级必须是"));
    }
}
