namespace EOS.API.Models;

/// <summary>邮件待办创建请求。字段全部为任务元数据，不接收邮件正文。</summary>
public sealed record MailTaskCreateRequest
{
    public string Title { get; init; } = string.Empty;

    public string? Description { get; init; }

    /// <summary>来源邮件账户（如 lina@eos-corp.com）。</summary>
    public string? SourceMailbox { get; init; }

    /// <summary>来源邮件 Message-Id（服务端唯一标识，客户端缓存中亦有）。</summary>
    public string? SourceMessageId { get; init; }

    /// <summary>来源邮件主题（仅作来源标识，不存正文）。</summary>
    public string? SourceSubject { get; init; }

    public DateTimeOffset? DueDate { get; init; }

    /// <summary>1 高 / 2 中 / 3 低。</summary>
    public int Priority { get; init; } = 2;
}

/// <summary>邮件待办更新请求（仅提交需要修改的字段）。</summary>
public sealed record MailTaskUpdateRequest
{
    public string? Title { get; init; }

    public string? Description { get; init; }

    public DateTimeOffset? DueDate { get; init; }

    /// <summary>置空截止时间（与 DueDate 互斥，优先于 DueDate）。</summary>
    public bool ClearDueDate { get; init; }

    public int? Priority { get; init; }
}

/// <summary>邮件待办输出 DTO。只含任务元数据，不含任何邮件正文；时间统一 UTC。</summary>
public sealed record MailTaskDto(
    long Id,
    string Title,
    string? Description,
    string SourceKind,
    string? SourceMailbox,
    string? SourceMessageId,
    string? SourceSubject,
    DateTime? DueDate,
    int Priority,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt,
    DateTime UpdatedAt);

/// <summary>邮件待办列表响应。</summary>
public sealed record MailTaskListDto(
    IReadOnlyList<MailTaskDto> Tasks,
    int Total,
    int Limit,
    int Offset);
