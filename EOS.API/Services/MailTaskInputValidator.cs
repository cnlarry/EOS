using EOS.API.Models;

namespace EOS.API.Services;

/// <summary>
/// 邮件待办输入校验（纯逻辑，与数据库解耦，便于单元测试）。
/// 校验要点：标题必填且限长、描述与来源标识限长、优先级范围、截止时间合理性。
/// </summary>
public static class MailTaskInputValidator
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 1000;
    public const int MaxSourceFieldLength = 255;

    private static readonly DateTimeOffset EarliestDue = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LatestDue = new(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>返回校验错误消息列表；为空表示通过。</summary>
    public static IReadOnlyList<string> ValidateCreate(MailTaskCreateRequest input)
    {
        var errors = new List<string>();
        var title = input.Title?.Trim() ?? string.Empty;
        if (title.Length == 0)
        {
            errors.Add("任务标题不能为空");
        }
        else if (title.Length > MaxTitleLength)
        {
            errors.Add($"任务标题不能超过 {MaxTitleLength} 个字符");
        }

        ValidateCommon(input.Description, input.SourceMailbox, input.SourceMessageId, input.SourceSubject,
            input.DueDate, input.Priority, errors);
        return errors;
    }

    /// <summary>返回校验错误消息列表；为空表示通过。</summary>
    public static IReadOnlyList<string> ValidateUpdate(MailTaskUpdateRequest input)
    {
        var errors = new List<string>();
        if (input.Title is not null)
        {
            var title = input.Title.Trim();
            if (title.Length == 0)
            {
                errors.Add("任务标题不能为空");
            }
            else if (title.Length > MaxTitleLength)
            {
                errors.Add($"任务标题不能超过 {MaxTitleLength} 个字符");
            }
        }

        if (input.Description is { Length: > MaxDescriptionLength })
        {
            errors.Add($"任务描述不能超过 {MaxDescriptionLength} 个字符");
        }

        if (input.Priority is < 1 or > 3)
        {
            errors.Add("优先级必须是 1（高）、2（中）或 3（低）");
        }

        if (input.DueDate is { } due && !IsReasonableDue(due))
        {
            errors.Add("截止时间必须在 2000-01-01 至 2100-01-01 之间");
        }

        return errors;
    }

    private static void ValidateCommon(
        string? description,
        string? sourceMailbox,
        string? sourceMessageId,
        string? sourceSubject,
        DateTimeOffset? dueDate,
        int priority,
        List<string> errors)
    {
        if (description is { Length: > MaxDescriptionLength })
        {
            errors.Add($"任务描述不能超过 {MaxDescriptionLength} 个字符");
        }

        if (sourceMailbox is { Length: > MaxSourceFieldLength })
        {
            errors.Add("来源邮箱不能超过 255 个字符");
        }

        if (sourceMessageId is { Length: > MaxSourceFieldLength })
        {
            errors.Add("来源邮件标识不能超过 255 个字符");
        }

        if (sourceSubject is { Length: > MaxSourceFieldLength })
        {
            errors.Add("来源邮件主题不能超过 255 个字符");
        }

        if (priority is < 1 or > 3)
        {
            errors.Add("优先级必须是 1（高）、2（中）或 3（低）");
        }

        if (dueDate is { } due && !IsReasonableDue(due))
        {
            errors.Add("截止时间必须在 2000-01-01 至 2100-01-01 之间");
        }
    }

    private static bool IsReasonableDue(DateTimeOffset due)
        => due >= EarliestDue && due <= LatestDue;
}
