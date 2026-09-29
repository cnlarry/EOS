using EOS.API.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Workbench;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Actions;

/// <summary>
/// 助手记录动作的执行体：**预演**与**执行**都走既有的统一表单写管线
/// （<see cref="DocumentWorkbenchRepository"/> 的新增/修改/删除），不新增任何写 SQL、
/// 不绕过校验、效果链与审计——"界面没走过的路，助手也不走"。
///
/// <para>
/// 两件事在这里同时成立：
/// <list type="number">
/// <item>**门禁 1（服务端）**：每次执行前按当前用户**独立重新授权**
/// （<see cref="AssistantActionGate"/> → <see cref="WorkbenchAccessPolicy"/>），
/// 不复用预演结论，fail-closed；</item>
/// <item>**预演恒为开**：执行前先在同一段代码里跑一遍"真实事务内执行后强制回滚"，
/// 预演判不下来的行**不会**进入写入——预演与执行天然一致，不存在只校验不执行的第二套实现。</item>
/// </list>
/// </para>
/// </summary>
public sealed class AssistantRecordActionService(
    AssistantActionGate gate,
    DocumentWorkbenchRepository repository,
    EffectPlanLoader effectPlans,
    AgentWriteContext agentWrites,
    WorkbenchAuditWriter auditWriter,
    ILogger<AssistantRecordActionService> logger)
{
    /// <summary>预演审计的动作码：预演本身不留业务痕迹，只在审计里留一条 best-effort 记录。</summary>
    public const string DryRunAuditAction = "DRYRUN";

    /// <summary>用户确认执行的动作码：AI 发起与用户确认各留一条，确认可独立检索。</summary>
    public const string ConfirmAuditAction = "ACTION_CONFIRM";

    /// <summary>
    /// 预演：逐行给出"能不能做 / 为什么不能"，删除另附配置面的级联影响面。
    /// 预演期间**不提交任何事务**（写管线在 <c>dryRun</c> 下跑完整条路径后无条件回滚）。
    /// </summary>
    public async Task<AssistantActionPreview> PreviewAsync(
        string userId, string employeeName, AssistantActionRequest request, CancellationToken token)
    {
        var decision = await gate.EvaluateAsync(userId, request.ModuleId, request.Kind, token);
        if (!decision.Allowed)
        {
            return new AssistantActionPreview(
                request.ModuleId, string.Empty, request.Kind, decision.Code, decision.Message, [], []);
        }

        var access = decision.Access!;
        var notes = new List<string> { "预演在同一事务内跑完整条路径后无条件回滚，与真实执行共用同一段代码。" };
        var rows = new List<AssistantActionRowOutcome>(request.Rows.Count);
        foreach (var row in request.Rows)
        {
            var result = await DryRunAsync(access, request.Kind, row, userId, employeeName, token);
            var allowed = result.Status == RecordAccessStatus.Ok;
            rows.Add(new AssistantActionRowOutcome(
                ReferenceKeys(request.Kind, row, result),
                allowed,
                allowed ? null : result.ErrorCode ?? "ACTION_FAILED",
                allowed ? null : Describe(result),
                request.Kind == AssistantRecordActionKind.Delete
                    ? BuildImpacts(access.Definition, notes)
                    : null));
        }

        await WriteDryRunAuditAsync(access.Definition, request, rows, userId, token);
        return new AssistantActionPreview(
            request.ModuleId, access.Definition.Title, request.Kind, null, null, rows, notes);
    }

    /// <summary>
    /// 用户在操作卡上点了"确认执行"：**先留一条确认审计**，再照常执行。
    ///
    /// <para>
    /// 确认与执行各留一条审计（AI 发起一条、用户确认一条），"谁在什么时候点了确认"因此可独立检索。
    /// 确认审计是 best-effort 的：它写不进去不该拦住用户已经点下的那一次执行。
    /// </para>
    /// </summary>
    public async Task<AssistantActionExecution> ConfirmAndExecuteAsync(
        string userId, string employeeName, AssistantActionRequest request, string confirmKey, CancellationToken token)
    {
        using var agentScope = agentWrites.Begin();
        await WriteConfirmAuditAsync(request, userId, token);
        return await ExecuteAsync(userId, employeeName, request, AssistantActionKeySeed.FromUserConfirm(confirmKey), token);
    }

    /// <summary>
    /// 执行：先按门禁 1 重新授权，再逐行预演（预演判不下来的行不写），最后经既有写管线落库。
    /// 幂等键按行由服务端生成（调用身份 + 动作名 + 规范化参数），重放同一键不重复写。
    /// </summary>
    public async Task<AssistantActionExecution> ExecuteAsync(
        string userId, string employeeName, AssistantActionRequest request,
        AssistantActionKeySeed seed, CancellationToken token)
    {
        var decision = await gate.EvaluateAsync(userId, request.ModuleId, request.Kind, token);
        if (!decision.Allowed)
        {
            // 模块级就进不去时逐行结论为空——此时逐行原因只是同一句重复，没有信息量。
            return new AssistantActionExecution(
                request.ModuleId, string.Empty, request.Kind, decision.Code, decision.Message, []);
        }

        var access = decision.Access!;
        var actionName = AssistantRecordActionNames.For(request.Kind);
        var results = new List<AssistantActionRowResult>(request.Rows.Count);
        using var agentScope = agentWrites.Begin();
        foreach (var row in request.Rows)
        {
            var idempotencyKey = AssistantActionIdempotency.Create(
                seed.ConversationId, seed.CallId, actionName, AssistantActionIdempotency.Canonicalize(request, row));

            var preview = await DryRunAsync(access, request.Kind, row, userId, employeeName, token);
            if (preview.Status != RecordAccessStatus.Ok)
            {
                results.Add(new AssistantActionRowResult(
                    ReferenceKeys(request.Kind, row, preview), false,
                    preview.ErrorCode ?? "ACTION_FAILED", Describe(preview), idempotencyKey));
                continue;
            }

            var written = await WriteAsync(access, request.Kind, row, userId, employeeName, idempotencyKey, token);
            var succeeded = written.Status == RecordAccessStatus.Ok;
            results.Add(new AssistantActionRowResult(
                succeeded ? written.Key ?? ReferenceKeys(request.Kind, row, written) : ReferenceKeys(request.Kind, row, written),
                succeeded,
                succeeded ? null : written.ErrorCode ?? "ACTION_FAILED",
                succeeded ? null : Describe(written),
                idempotencyKey,
                written.Key));
        }

        return new AssistantActionExecution(
            request.ModuleId, access.Definition.Title, request.Kind, null, null, results);
    }

    /// <summary>真实事务内执行后强制回滚：写管线的 <c>dryRun</c> 分支就是这条路径本身。</summary>
    private Task<RecordSaveResult> DryRunAsync(
        WorkbenchFormAccess access, AssistantRecordActionKind kind, AssistantActionRow row,
        string userId, string employeeName, CancellationToken token)
        => InvokeAsync(access, kind, row, userId, employeeName, idempotencyKey: null, dryRun: true, token);

    private Task<RecordSaveResult> WriteAsync(
        WorkbenchFormAccess access, AssistantRecordActionKind kind, AssistantActionRow row,
        string userId, string employeeName, string idempotencyKey, CancellationToken token)
        => InvokeAsync(access, kind, row, userId, employeeName, idempotencyKey, dryRun: false, token);

    private Task<RecordSaveResult> InvokeAsync(
        WorkbenchFormAccess access, AssistantRecordActionKind kind, AssistantActionRow row,
        string userId, string employeeName, string? idempotencyKey, bool dryRun, CancellationToken token)
    {
        var definition = access.Definition;
        var form = access.Form;
        var dataFilter = access.Rights.DataFilter;
        return kind switch
        {
            AssistantRecordActionKind.Insert => repository.CreateRecordAsync(
                definition, form,
                new SaveRecordRequest(
                    row.Values ?? new Dictionary<string, string?>(),
                    row.Details,
                    IdempotencyKey: idempotencyKey,
                    DetailSerials: row.DetailSerials),
                employeeName, userId, dataFilter, token, dryRun),
            AssistantRecordActionKind.Update => repository.UpdateRecordAsync(
                definition, form, row.Keys,
                new SaveRecordRequest(
                    row.Values ?? new Dictionary<string, string?>(),
                    row.Details,
                    IdempotencyKey: idempotencyKey,
                    DetailSerials: row.DetailSerials),
                employeeName, userId, dataFilter, token, dryRun),
            AssistantRecordActionKind.Delete => repository.DeleteRecordAsync(
                definition, form, row.Keys, userId, dataFilter, token, idempotencyKey, dryRun),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知的记录动作。"),
        };
    }

    /// <summary>结论里的键：写入成功取业务生成的主键，其余（含新增被拒）取请求给出的键。</summary>
    private static IReadOnlyList<string> ReferenceKeys(
        AssistantRecordActionKind kind, AssistantActionRow row, RecordSaveResult result)
        => result.Key is { Count: > 0 } ? result.Key : row.Keys;

    /// <summary>拒绝原因的完整文案：服务端消息 + 字段级错误，都是用户能照着改的话。</summary>
    private static string Describe(RecordSaveResult result)
    {
        var message = result.ErrorMessage ?? result.ErrorCode ?? "操作被拒绝。";
        if (result.FieldErrors is not { Count: > 0 } errors)
        {
            return message;
        }
        return message + " " + string.Join("；", errors.Select(error => $"{error.Field}：{error.Message}"));
    }

    /// <summary>
    /// 删除的级联影响面：列出该模块**声明**的生效链（批核 / 保存阶段）会碰到的目标表与字段。
    /// 删除本身不跑效果链，因此这张表回答的是"这张单据带着哪些业务副作用"，
    /// 而不是"删除会执行这些效果"——不写清楚就会变成一句误导。
    /// </summary>
    private IReadOnlyList<AssistantActionImpact> BuildImpacts(WorkbenchDefinition definition, List<string> notes)
    {
        try
        {
            var plan = effectPlans.Load(definition);
            var impacts = new List<AssistantActionImpact>();
            foreach (var action in plan.Actions)
            {
                if (!action.Enabled) continue;
                if (!EffectEventMapper.AppliesTo(action.EventCode, EffectEvent.Save)
                    && !EffectEventMapper.AppliesTo(action.EventCode, EffectEvent.ApproveEffect))
                {
                    continue;
                }
                if (action.Ops.Count == 0)
                {
                    // 服务型效果键没有公式行，影响面写在参数里，这里只能给出键本身。
                    impacts.Add(new AssistantActionImpact(
                        action.EffectKey, action.EventCode, action.EffectName, string.Empty, string.Empty, string.Empty));
                    continue;
                }
                foreach (var op in action.Ops)
                {
                    impacts.Add(new AssistantActionImpact(
                        action.EffectKey, action.EventCode, action.EffectName,
                        op.TargetTable, op.TargetField, op.OpCode));
                }
            }
            if (impacts.Count > 0)
            {
                notes.Add("影响面来自本模块声明的效果链（批核/保存阶段），删除本身不执行效果链；"
                    + "它回答的是这张单据带着哪些业务副作用。");
            }
            return impacts;
        }
        catch (EffectConfigException ex)
        {
            logger.LogWarning(ex, "删除影响面读取失败 module={ModuleId}", definition.ModuleId);
            notes.Add($"影响面读取失败（配置未通过校验）：{ex.Message}");
            return [];
        }
    }

    /// <summary>
    /// 确认留痕：用户在操作卡上点了"确认执行"。执行主体是这次点击，不是模型自己的决定，
    /// 因此它单独成条、与随后的业务写入审计分得开。
    /// </summary>
    private async Task WriteConfirmAuditAsync(
        AssistantActionRequest request, string userId, CancellationToken token)
    {
        var actionName = AssistantRecordActionNames.For(request.Kind);
        var keys = string.Join(',', request.Rows.SelectMany(row => row.Keys).Where(key => key.Length > 0).Take(20));
        var summary = $"用户确认执行{actionName}：{request.Rows.Count} 行。";
        try
        {
            await auditWriter.WriteBestEffortAsync(
                request.ModuleId > 0 ? request.ModuleId : null, keys, ConfirmAuditAction, summary, userId,
                "WORKBENCH_RECORD", result: 1, fieldChanges: null, token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "确认审计写入失败 module={ModuleId}", request.ModuleId);
        }
    }

    /// <summary>预演留痕：best-effort，且**必须落在回滚之后**——否则它自己会被回滚掉。</summary>
    private async Task WriteDryRunAuditAsync(
        WorkbenchDefinition definition, AssistantActionRequest request,
        IReadOnlyList<AssistantActionRowOutcome> rows, string userId, CancellationToken token)
    {
        var allowed = rows.Count(row => row.Allowed);
        var denied = rows.Count - allowed;
        var keys = string.Join(',', rows.SelectMany(row => row.Keys).Where(key => key.Length > 0).Take(20));
        var summary = $"预演{AssistantRecordActionNames.For(request.Kind)}：可执行 {allowed} 行、不可执行 {denied} 行。";
        try
        {
            using var agentScope = agentWrites.Begin();
            await auditWriter.WriteBestEffortAsync(
                definition.ModuleId, keys, DryRunAuditAction, summary, userId, "WORKBENCH_RECORD", result: 1,
                fieldChanges: null, token);
        }
        catch (Exception ex)
        {
            // best-effort：审计写不进去不该让预演失败（与读路径留痕同一口径）。
            logger.LogWarning(ex, "预演审计写入失败 module={ModuleId}", definition.ModuleId);
        }
    }
}
