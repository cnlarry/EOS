namespace EOS.API.Validation;

/// <summary>校验问题的严重级别（取值直接进响应，与发布报告的 severity 同口径）。</summary>
public static class ValidationSeverity
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>
/// 单条校验问题。message 面向用户；code / field 可选，按需逐步补齐——
/// 不为了"看起来整齐"去发明一套码表，只在真有消费方（前端定位、字段高亮）时才填。
/// </summary>
public sealed record ValidationIssue(
    string Message,
    string? Code = null,
    string? Field = null,
    string Severity = ValidationSeverity.Error);

/// <summary>
/// 校验结果：有序问题集 + 是否通过。凡"调用方需要**检视**校验明细"的校验器一律返回本类型，
/// 不再各自返回 <c>IReadOnlyList&lt;string&gt;</c> 或自定义报告；调用方按同一套判断
/// （<see cref="Ok"/>）与同一套渲染（<see cref="Messages"/>）消费。
///
/// <para><b>与"守卫式参数校验"的分工（有意保留的差异）</b>：
/// <see cref="EOS.API.Data.ReportAdminValidator"/> 那类入口守卫**继续抛
/// <see cref="ArgumentException"/>**——throw 天然 fail-closed，而返回结果要求每个调用点都记得检查，
/// 十几处里漏一处就等于静默放过非法输入。本类型用于"结果要被看"的场景，不是用来统一所有校验入口。</para>
///
/// <para><b>刻意不并入的两种形状</b>（它们各自承载着本类型装不下的东西）：
/// <see cref="EOS.API.Data.RecordModels.FieldError"/> 是本单写入路径的字段级投影，多带 rowIndex
/// 并直接喂给 <c>ApiProblem.WithFieldErrors</c>，是响应线形的一部分；
/// 发布报告的 check 项同时记录**通过**的检查项，不是纯问题列表。</para>
/// </summary>
public sealed class ValidationResult
{
    private readonly List<ValidationIssue> _issues = [];

    public ValidationResult()
    {
    }

    public ValidationResult(IEnumerable<ValidationIssue> issues) => _issues.AddRange(issues);

    /// <summary>无问题的结果。</summary>
    public static ValidationResult Success { get; } = new();

    public IReadOnlyList<ValidationIssue> Issues => _issues;

    public bool Ok => _issues.Count == 0;

    /// <summary>纯文本问题清单：供既有"拼成一句话"或"直接作为 errors 数组下发"的调用方消费。</summary>
    public IReadOnlyList<string> Messages => _issues.Select(issue => issue.Message).ToArray();

    public void Add(string message, string? code = null, string? field = null)
        => _issues.Add(new ValidationIssue(message, code, field));

    public void Add(ValidationIssue issue) => _issues.Add(issue);

    /// <summary>并入另一份结果（分节校验的常见形态）。</summary>
    public void AddRange(ValidationResult other) => _issues.AddRange(other._issues);

    /// <summary>
    /// 由纯文本问题构造：给内部仍按 <c>List&lt;string&gt;</c> 累积的校验器在边界处收口用，
    /// 使对外契约统一而不必先重写其全部内部收集逻辑。
    /// </summary>
    public static ValidationResult FromMessages(IEnumerable<string> messages)
        => new(messages.Select(message => new ValidationIssue(message)));
}
