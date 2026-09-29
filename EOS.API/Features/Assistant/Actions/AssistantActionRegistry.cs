using EOS.API.Features.Assistant.Config;

namespace EOS.API.Features.Assistant.Actions;

/// <summary>
/// 助手**可代理**动作的登记项：一条动作的六要素 + 落点。
///
/// <para>
/// 六要素 = 谁（<see cref="ActorSubject"/>）· 对什么（<see cref="Target"/>）· 做什么（<see cref="Name"/>）·
/// 参数（<see cref="Parameters"/>）· 幂等键（<see cref="IdempotencyKey"/>）· 审计（<see cref="AuditAction"/>）；
/// 另有 <see cref="Endpoint"/>（写路径入口，供门禁做"动作名 + 目标端点"双重匹配）
/// 与 <see cref="Implementation"/>（实现位置，供门禁指到代码）。
/// </para>
/// <para>
/// 登记**不改变行为**：注册表只是目录，动作的执行仍然走各自既有的实现；
/// 它的价值是让"助手能做哪些动作、每个动作的键与审计从哪来"变成一处可检索、可断言的事实。
/// </para>
/// </summary>
public sealed record AssistantActionDefinition(
    string Name,
    string ActorSubject,
    string Target,
    string Parameters,
    string IdempotencyKey,
    string AuditAction,
    string Endpoint,
    string Implementation);

/// <summary>
/// 助手可代理动作的受控目录。
///
/// <para>
/// 目录内只有两类动作：记录动作（新增 / 修改 / 删除）与配置写（字段 / 数据来源 / 自定义按钮 / 效果键）。
/// **职权行使类动作（批核族与权限授予类配置）不在目录里**——它们不是"约定不要放"，
/// 而是能力面上表达不出来：动作枚举没有对应成员，写入口也不在可触达的仓储方法集合里
/// （结构断言见 <c>NoApprovalEndpointCallTests</c>）。
/// </para>
/// </summary>
public static class AssistantActionRegistry
{
    /// <summary>谁：动作的提交者恒为当前登录用户（审计里 Agent 只是"代表用户"的标记）。</summary>
    private const string User = "当前登录用户（ACTOR_USER_ID 恒为真人；ACTOR_TYPE=3 表示 Agent 代表用户提交）";

    /// <summary>对什么：模块定义的一行（模块号 + 主键值数组），新增行的主键由写管线生成。</summary>
    private const string MasterRow = "模块定义的主表行：module_id + 主键值数组（新增行的主键由写管线生成）";

    /// <summary>对什么：字段元数据（表.字段）。</summary>
    private const string FieldMetadata = "字段元数据：表 T_ID + 字段 F_ID";

    /// <summary>对什么：模块的动作配置（模块号 + 事件 + 效果键）。</summary>
    private const string ModuleAction = "模块的动作配置：module_id + 事件 + 效果键";

    /// <summary>登记项清单。顺序固定：先记录动作，再配置写四类。</summary>
    public static IReadOnlyList<AssistantActionDefinition> All { get; } =
    [
        new AssistantActionDefinition(
            Name: "insert",
            ActorSubject: User,
            Target: MasterRow,
            Parameters: "AssistantRecordActionArguments.ParametersJson（module_id/module_title、action、rows[values/details]）",
            IdempotencyKey: "服务端按工具调用身份推导 SHA256(会话+调用标识+动作名+规范化参数)；界面点击走本次确认键",
            AuditAction: "INSERT（与业务同事务）；DRYRUN（预演留痕，落在回滚之后）；ACTION_CONFIRM（用户确认点击）",
            Endpoint: "DocumentWorkbenchRepository.CreateRecordAsync",
            Implementation: "AssistantRecordActionService.InvokeAsync"),

        new AssistantActionDefinition(
            Name: "update",
            ActorSubject: User,
            Target: MasterRow,
            Parameters: "AssistantRecordActionArguments.ParametersJson（rows[keys/values/details]）",
            IdempotencyKey: "服务端按工具调用身份推导 SHA256(会话+调用标识+动作名+规范化参数)；界面点击走本次确认键",
            AuditAction: "UPDATE（与业务同事务）；DRYRUN（预演留痕）；ACTION_CONFIRM（用户确认点击）",
            Endpoint: "DocumentWorkbenchRepository.UpdateRecordAsync",
            Implementation: "AssistantRecordActionService.InvokeAsync"),

        new AssistantActionDefinition(
            Name: "delete",
            ActorSubject: User,
            Target: MasterRow,
            Parameters: "AssistantRecordActionArguments.ParametersJson（rows[keys]；逐行摊开，不给单一按钮）",
            IdempotencyKey: "服务端按工具调用身份推导 SHA256(会话+调用标识+动作名+规范化参数)；界面点击走本次确认键",
            AuditAction: "DELETE（与业务同事务）；DRYRUN（预演留痕）；ACTION_CONFIRM（用户确认点击）",
            Endpoint: "DocumentWorkbenchRepository.DeleteRecordAsync",
            Implementation: "AssistantRecordActionService.InvokeAsync"),

        new AssistantActionDefinition(
            Name: "CFG_FIELD",
            ActorSubject: User,
            Target: FieldMetadata,
            Parameters: "AssistantConfigArguments.ParametersJson（surface=fields、source_table/target_table、objects、items）",
            IdempotencyKey: "服务端按调用身份与配置面推导批级键 sha256(调用身份+配置面+请求意图)，不含要写入的值",
            AuditAction: "CFG_FIELD（与业务同事务）；ACTION_CONFIRM（用户确认点击）",
            Endpoint: "FieldAdminRepository.UpdateIdempotentAsync",
            Implementation: "ConfigWriteService.ApplyAsync → ConfigSurface.Fields"),

        new AssistantActionDefinition(
            Name: "CFG_DS",
            ActorSubject: User,
            Target: FieldMetadata,
            Parameters: "AssistantConfigArguments.ParametersJson（surface=datasource）",
            IdempotencyKey: "服务端按调用身份与配置面推导批级键 sha256(调用身份+配置面+请求意图)，不含要写入的值",
            AuditAction: "CFG_DS（与业务同事务）；ACTION_CONFIRM（用户确认点击）",
            Endpoint: "FieldAdminRepository.UpdateIdempotentAsync",
            Implementation: "ConfigWriteService.ApplyAsync → ConfigSurface.DataSources"),

        new AssistantActionDefinition(
            Name: "CFG_BTN",
            ActorSubject: User,
            Target: ModuleAction,
            Parameters: "AssistantConfigArguments.ParametersJson（surface=buttons、source_module_id/target_module_id）",
            IdempotencyKey: "服务端按调用身份与配置面推导批级键 sha256(调用身份+配置面+请求意图)，不含要写入的值",
            AuditAction: "CFG_BTN（与业务同事务）；ACTION_CONFIRM（用户确认点击）",
            Endpoint: "ModuleBusinessConfigRepository.SaveAsync",
            Implementation: "ConfigWriteService.ApplyAsync → ConfigSurface.Buttons（不含按钮授权）"),

        new AssistantActionDefinition(
            Name: "CFG_EFFECT",
            ActorSubject: User,
            Target: ModuleAction,
            Parameters: "AssistantConfigArguments.ParametersJson（surface=effect）",
            IdempotencyKey: "服务端按调用身份与配置面推导批级键 sha256(调用身份+配置面+请求意图)，不含要写入的值",
            AuditAction: "CFG_EFFECT（与业务同事务）；ACTION_CONFIRM（用户确认点击）",
            Endpoint: "ModuleBusinessConfigRepository.SaveAsync",
            Implementation: "ConfigWriteService.ApplyAsync → ConfigSurface.Effects"),
    ];

    /// <summary>
    /// 职权类动词：名字面或端点上出现它们，说明职权行使被塞进了助手动作面。
    /// 与 <c>NoApprovalEndpointCallTests</c> 的动词表同一份口径（那边断的是工具面与调用点，这边断的是注册表）。
    /// </summary>
    private static readonly string[] ForbiddenVerbs =
        ["approve", "deapprove", "endcase", "unendcase", "finish", "批核", "解批", "结案", "取消结案", "审批"];

    public static bool TryGet(string? name, out AssistantActionDefinition definition)
    {
        definition = All.FirstOrDefault(item => string.Equals(item.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return definition is not null;
    }

    /// <summary>
    /// 目录自检：六要素齐全、动作名唯一、不含职权类动作、且与写路径的动作名真源逐一对齐。
    /// 返回空集合即通过；不通过时把每一条问题都写出来（门禁与单测共用同一份判据）。
    /// </summary>
    public static IReadOnlyList<string> Validate()
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in All)
        {
            foreach (var (element, value) in Elements(action))
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    problems.Add($"动作 {action.Name} 的{Describe(element)}为空：六要素与落点都不得缺省。");
                }
            }

            if (!seen.Add(action.Name))
            {
                problems.Add($"动作名 {action.Name} 重复登记。");
            }

            foreach (var verb in ForbiddenVerbs)
            {
                if (action.Name.Contains(verb, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"动作名 {action.Name} 含职权类动词 {verb}：职权行使不在可代理动作面内。");
                }
                if (action.Endpoint.Contains(verb, StringComparison.OrdinalIgnoreCase))
                {
                    problems.Add($"动作 {action.Name} 的目标端点 {action.Endpoint} 含职权类动词 {verb}。");
                }
            }
        }

        var expected = new List<string>
        {
            AssistantRecordActionNames.Insert,
            AssistantRecordActionNames.Update,
            AssistantRecordActionNames.Delete,
            ConfigSurfaceNames.ActionOf(ConfigSurface.Fields),
            ConfigSurfaceNames.ActionOf(ConfigSurface.DataSources),
            ConfigSurfaceNames.ActionOf(ConfigSurface.Buttons),
            ConfigSurfaceNames.ActionOf(ConfigSurface.Effects),
        };
        foreach (var name in expected.Where(name => !seen.Contains(name)))
        {
            problems.Add($"已交付动作 {name} 未登记：注册表与实际能力面不一致。");
        }
        foreach (var name in seen.Where(name => !expected.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            problems.Add($"注册表里的 {name} 没有对应的实现动作名：登记项与写路径脱节。");
        }

        return problems;
    }

    private static IEnumerable<(string Element, string Value)> Elements(AssistantActionDefinition action)
    {
        yield return (nameof(action.ActorSubject), action.ActorSubject);
        yield return (nameof(action.Target), action.Target);
        yield return (nameof(action.Name), action.Name);
        yield return (nameof(action.Parameters), action.Parameters);
        yield return (nameof(action.IdempotencyKey), action.IdempotencyKey);
        yield return (nameof(action.AuditAction), action.AuditAction);
        yield return (nameof(action.Endpoint), action.Endpoint);
        yield return (nameof(action.Implementation), action.Implementation);
    }

    private static string Describe(string element) => element switch
    {
        nameof(AssistantActionDefinition.ActorSubject) => "「谁」",
        nameof(AssistantActionDefinition.Target) => "「对什么」",
        nameof(AssistantActionDefinition.Name) => "「做什么」",
        nameof(AssistantActionDefinition.Parameters) => "「参数」",
        nameof(AssistantActionDefinition.IdempotencyKey) => "「幂等键」",
        nameof(AssistantActionDefinition.AuditAction) => "「审计」",
        nameof(AssistantActionDefinition.Endpoint) => "「目标端点」",
        _ => "「实现位置」",
    };
}
