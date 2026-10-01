using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.Governance;

/// <summary>
/// 助手可执行动作的阈值与红线开关的**单一事实源**。
///
/// <para>
/// 上限只在这里声明一次，业务代码引用这些常量而不是写死数值——否则"某次调参"会散落在多处，
/// 且事后无从判断当时生效的是哪个值。红线开关是 <c>const</c>：它们没有"关掉"的表达方式，
/// 配置层即使写了同名的键，也只会被判为一次覆盖尝试（见 <see cref="AssistantActionLimitsValidator"/>）。
/// </para>
/// <para>首版不落库：阈值随配置/环境变量生效，生效值随审计留一条快照。</para>
/// </summary>
public static class AssistantActionLimits
{
    /// <summary>一次动作请求最多处理的行数：不设上限等于允许一句"全删了"。</summary>
    public const int MaxRowsPerAction = 50;

    /// <summary>一次「操作请求卡」最多列出的单据数。</summary>
    public const int MaxApprovalRequestRecords = 25;

    /// <summary>审计里保留的资源键条数上限（审计是检索入口，不是数据出口）。</summary>
    public const int MaxAuditResourceKeys = 20;

    /// <summary>一次"照 A 配 B"最多搬运的对象数。</summary>
    public const int MaxConfigCloneObjects = 100;

    /// <summary>阈值的最小合法值：比它还小的一律视为填错（0 是"停机"语义，只对摘要来源成立）。</summary>
    public const int MinimumThreshold = 1;

    // ===== 红线开关：恒为开，没有"配成关"的表达方式 =====

    /// <summary>预演恒为开：动作先在同一事务内跑完整条路径后回滚，判不下来的行不写。</summary>
    public const bool DryRunRequired = true;

    /// <summary>幂等恒为开：执行类动作的幂等键由服务端推导，不因配置放开。</summary>
    public const bool IdempotencyRequired = true;

    /// <summary>越权动作数恒为 0：执行前按当前用户独立重新授权，fail-closed。</summary>
    public const int MaxUnauthorizedActions = 0;

    /// <summary>批核族动作在动作面与注册表里的条目数恒为 0（它们是职权行使，不可代签）。</summary>
    public const int ApprovalFamilyActionCount = 0;
}

/// <summary>
/// 可调阈值。**取值来自 3105 助手设置**（<c>dbo.SYSSS</c> 的 <c>OWNER_MODULE = 3105</c>，
/// 键 <c>ACTION_MAX_*</c>，见 ADR-030 §5.3.5），不再从配置节绑定。
///
/// <para>
/// 属性初始值就是代码默认值（引用 <see cref="AssistantActionLimits"/> 的常量，不写第二份数字），
/// 也是"库里没有行"时的取值。本类与 <see cref="SectionName"/> 现在只剩一个用途：
/// 让启动期校验器认出并**拒绝**残留在配置里的同名键（见 <see cref="AssistantActionLimitsValidator"/>）。
/// </para>
/// </summary>
public sealed class AssistantActionLimitsOptions
{
    /// <summary>已废弃的配置节名：保留它只为让"还有人往这里写"这件事在启动期失败，而不是静默无效。</summary>
    public const string SectionName = "AssistantActionLimits";

    public int MaxRowsPerAction { get; set; } = AssistantActionLimits.MaxRowsPerAction;

    public int MaxApprovalRequestRecords { get; set; } = AssistantActionLimits.MaxApprovalRequestRecords;

    public int MaxAuditResourceKeys { get; set; } = AssistantActionLimits.MaxAuditResourceKeys;

    public int MaxConfigCloneObjects { get; set; } = AssistantActionLimits.MaxConfigCloneObjects;

    /// <summary>生效阈值快照：随审计落一条，使"当时用的是哪个阈值"可回查。</summary>
    public string Snapshot() => AssistantActionLimitsSnapshot.Describe(this);
}

/// <summary>
/// 生效阈值快照的文本口径：一处生成，审计与只读查询共用同一句。
/// 红线部分直接取自 <see cref="AssistantActionLimits"/>——它们没有"配成关"的取值。
/// </summary>
public static class AssistantActionLimitsSnapshot
{
    public static string Describe(AssistantActionLimitsOptions limits) =>
        "生效阈值["
        + $"行数上限={limits.MaxRowsPerAction}"
        + $",请求卡上限={limits.MaxApprovalRequestRecords}"
        + $",审计键上限={limits.MaxAuditResourceKeys}"
        + $",克隆上限={limits.MaxConfigCloneObjects}"
        + $",预演={(AssistantActionLimits.DryRunRequired ? "开" : "关")}"
        + $",幂等={(AssistantActionLimits.IdempotencyRequired ? "开" : "关")}"
        + $",越权上限={AssistantActionLimits.MaxUnauthorizedActions}"
        + $",批核族条目={AssistantActionLimits.ApprovalFamilyActionCount}]";
}

/// <summary>
/// 阈值的启动期校验：**配置层的覆盖尝试一律启动即失败**，不静默忽略。
///
/// <para>
/// 阈值搬进 <c>dbo.SYSSS</c> 之后，配置节已经**没有任何读取方**——于是判定从两条收紧为一条：
/// **这个节里出现任何键都是覆盖尝试**。上一版只拦红线键与未登记的键，放行四个已知阈值键；
/// 那是按"配置文件仍是来源之一"写的。现在放行它们等于告诉填表的人"配上了"，
/// 而实际上一个字节都不会被读——**一个"配了但没人读"的开关比配错更危险**。
/// </para>
///
/// <para>
/// 红线键另给一句专门的说明（它们不只是无效，而是在试图关掉一条没有"关"这个表达方式的红线）。
/// 取值合法性的校验保留：它现在校验的是**代码默认值**（属性初始值），也就是"库里没有行"时的取值。
/// </para>
///
/// <para>
/// 另外两处（<c>SYSSS</c> 的 <c>OWNER_MODULE = 3105</c>、<c>ASSISTANT_PARAM_SCOPE</c>）里的红线键
/// 由参数解析器与门禁拦住：解析器把它们作为**问题**记进快照（3105 页面与日志可见）并忽略取值。
/// 之所以不在这里"启动失败"：那是库里的一行，而库里的行要能在应用里改——
/// 让一行坏数据把整个进程卡在启动阶段，管理员连修的机会都没有。
/// </para>
/// </summary>
public sealed class AssistantActionLimitsValidator(IConfiguration configuration)
    : IValidateOptions<AssistantActionLimitsOptions>
{
    /// <summary>红线键：这些名字出现在配置里就是"试图关掉红线"。</summary>
    private static readonly string[] RedLineKeys =
    [
        "DryRunRequired", "DryRunEnabled", "IdempotencyRequired", "IdempotencyEnabled",
        "MaxUnauthorizedActions", "UnauthorizedActionLimit",
        "ApprovalFamilyActionCount", "ApprovalFamilyLimit",
    ];

    public ValidateOptionsResult Validate(string? name, AssistantActionLimitsOptions options)
    {
        var failures = new List<string>();
        var section = configuration.GetSection(AssistantActionLimitsOptions.SectionName);

        foreach (var child in section.GetChildren())
        {
            failures.Add(RedLineKeys.Contains(child.Key, StringComparer.OrdinalIgnoreCase)
                ? $"助手动作红线不可覆盖：配置项 {AssistantActionLimitsOptions.SectionName}:{child.Key} "
                  + "试图关掉一条恒为开的红线（预演 / 幂等 / 越权上限 / 批核族不可达）。"
                : $"配置项 {AssistantActionLimitsOptions.SectionName}:{child.Key} 不会生效："
                  + "助手动作阈值现在住在 3105 助手设置（dbo.SYSSS 的 OWNER_MODULE = 3105，键 ACTION_MAX_*），"
                  + "配置文件里的同名键没有任何读取方。请到管理界面改，或删掉这一行。");
        }

        Check(failures, nameof(options.MaxRowsPerAction), options.MaxRowsPerAction);
        Check(failures, nameof(options.MaxApprovalRequestRecords), options.MaxApprovalRequestRecords);
        Check(failures, nameof(options.MaxAuditResourceKeys), options.MaxAuditResourceKeys);
        Check(failures, nameof(options.MaxConfigCloneObjects), options.MaxConfigCloneObjects);

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static void Check(List<string> failures, string key, int value)
    {
        if (value < AssistantActionLimits.MinimumThreshold)
        {
            failures.Add($"助手动作阈值 {key}={value} 小于最小值 {AssistantActionLimits.MinimumThreshold}。");
        }
    }
}
