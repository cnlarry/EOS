using System.Globalization;
using EOS.API.Data;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Extensions.Options;

namespace EOS.API.Features.Assistant.Config;

/// <summary>
/// 助手配置写的**唯一入口**：先把两道门过完（逐类准入开关 + 与既有写端点同一把权限门），
/// 再规划，最后只应用被勾选的项。
///
/// <para>
/// 三条不可逾越的约束：
/// <list type="number">
/// <item>**不新增写路径**：字段与数据来源走字段维护仓储，按钮与效果走模块业务配置仓储，
/// 校验（含受限表达式）、脏标记与审计都在既有路径里，助手不绕过任何一道；</item>
/// <item>**应用内容不回传**：界面提交的只有"要应用哪些 id"，每一项的写入值由服务端现场重新规划得出，
/// 因此请求体里进不了任意值；</item>
/// <item>**幂等键服务端生成**：由本次调用身份 + 面 + 请求意图推导，重放同一批不重复写。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ConfigWriteService(
    IOptions<AssistantConfigWriteOptions> options,
    IPermissionService permissions,
    DbConnectionFactory connections,
    WorkbenchIdempotency idempotency,
    ConfigClonePlanner planner,
    ConfigDryRunner dryRunner,
    FieldAdminRepository fields,
    ModuleBusinessConfigRepository moduleConfig,
    ILogger<ConfigWriteService> logger)
{
    /// <summary>字段维护的权限门（与字段维护写端点同一个模块号）。</summary>
    public const int FieldAdminModuleId = 2302;

    /// <summary>配置面的权限门（与预演端点、2301 行为配置写端点同一个模块号）。</summary>
    public const int MenuAdminModuleId = 2301;

    /// <summary>该类配置在助手侧尚未开放时的原因码。</summary>
    public const string SurfaceDisabledCode = "CONFIG_SURFACE_DISABLED";

    /// <summary>调用者不具备该类配置的写权限时的原因码。</summary>
    public const string ForbiddenCode = "CONFIG_WRITE_FORBIDDEN";

    /// <summary>没有勾选任何改动项时的原因码。</summary>
    public const string NoSelectionCode = "NO_SELECTION";

    /// <summary>两边本来就没有差异时的原因码。</summary>
    public const string NoChangesCode = "NO_CHANGES";

    /// <summary>重放时逐项的说明：这一批此前已经应用过，本次没有重复写入。</summary>
    public const string ReplayNote = "（重放：该次改动此前已应用，本次没有重复写入）";

    /// <summary>规划：过两道门后按面算出逐项对照（纯读）。</summary>
    public async Task<ConfigChangePlan> PlanAsync(
        string userId, ConfigCloneRequest request, CancellationToken token)
    {
        if (Denied(request.Surface, await AllowedAsync(userId, request.Surface, token), out var blocked))
        {
            return new ConfigChangePlan(
                request.Surface, "（未指定）", "（未指定）", blocked.Code, blocked.Message, [], []);
        }
        var plan = await planner.PlanAsync(request, token);
        logger.LogInformation(
            "助手配置规划 surface={Surface} items={Items} blocked={Blocked}",
            request.Surface, plan.Items.Count, plan.BlockedCode ?? "-");
        return plan;
    }

    /// <summary>预演/自检：能预演的跑真实预演，不能预演的逐项标注原因。</summary>
    public Task<ConfigDryRunReport> DryRunAsync(
        ConfigChangePlan plan,
        ConfigCloneRequest request,
        IReadOnlyList<string>? itemIds,
        string userId,
        CancellationToken token)
        => dryRunner.DryRunAsync(plan, request, itemIds, userId, token);

    /// <summary>
    /// 应用被勾选的改动项。服务端**重新规划**后只写被点名的项——
    /// 界面回传的 id 只决定"改哪些对象"，改什么值由服务端算。
    /// </summary>
    public async Task<ConfigApplyResult> ApplyAsync(
        string userId,
        string employeeName,
        ConfigCloneRequest request,
        IReadOnlyList<string>? itemIds,
        AssistantActionKeySeed seed,
        CancellationToken token)
    {
        if (Denied(request.Surface, await AllowedAsync(userId, request.Surface, token), out var blocked))
        {
            return new ConfigApplyResult(request.Surface, "（未指定）", blocked.Code, blocked.Message, [], []);
        }
        if (itemIds is not { Count: > 0 })
        {
            return new ConfigApplyResult(request.Surface, "（未指定）", NoSelectionCode,
                "没有勾选任何改动项：请先选择要应用的项。", [], []);
        }

        var plan = await planner.PlanAsync(request, token);
        if (plan.BlockedCode is not null)
        {
            return new ConfigApplyResult(
                request.Surface, plan.TargetLabel, plan.BlockedCode, plan.BlockedMessage, [], plan.Notes);
        }

        var canonical = Canonicalize(request, itemIds);
        var batchKey = BatchKeyFor(seed, request.Surface, request, itemIds);
        // 重放判定：配置写的各面没有共享事务，批级留痕就是"这一批做过没有"的依据。
        if (await WorkbenchIdempotency.TryReadAsync(connections, batchKey, token) is { ResultKey: not null })
        {
            var applied = plan.Items.Where(item => itemIds.Contains(item.Id, StringComparer.Ordinal)).ToList();
            return new ConfigApplyResult(request.Surface, plan.TargetLabel, null, null,
                [.. applied.Count > 0
                    ? applied.Select(item => new ConfigApplyItemResult(item.Id, item.Target, true, null, ReplayNote, batchKey))
                    : itemIds.Select(id => new ConfigApplyItemResult(id, id, true, null, ReplayNote, batchKey))],
                plan.Notes);
        }

        var selected = plan.Items.Where(item => itemIds.Contains(item.Id, StringComparer.Ordinal)).ToList();
        if (selected.Count == 0)
        {
            // 两边本来就没有差异，与"勾的项已经不适用"是两件事，如实分开说。
            return plan.Items.Count == 0
                ? new ConfigApplyResult(request.Surface, plan.TargetLabel, NoChangesCode,
                    "源与目标已经一致，没有需要应用的改动。", [], plan.Notes)
                : new ConfigApplyResult(request.Surface, plan.TargetLabel, NoSelectionCode,
                    "勾选的项在当前配置里已经没有可应用的差异（可能是别人已经改过）。", [], plan.Notes);
        }

        var results = request.Surface switch
        {
            ConfigSurface.Fields or ConfigSurface.DataSources => await ApplyFieldItemsAsync(
                selected, employeeName, request.Surface, seed, canonical, token),
            _ => await ApplyActionItemsAsync(request, plan, selected, employeeName, batchKey, token),
        };
        if (results.Any(item => item.Applied))
        {
            await RecordBatchAsync(batchKey, request.Surface, plan.TargetLabel, token);
        }
        logger.LogInformation(
            "助手配置应用 surface={Surface} selected={Selected} applied={Applied}",
            request.Surface, itemIds.Count, results.Count(item => item.Applied));
        return new ConfigApplyResult(request.Surface, plan.TargetLabel, null, null, results, plan.Notes);
    }

    /// <summary>字段与数据来源：逐项走字段维护仓储的写入（含受限表达式校验与脏标记）。</summary>
    private async Task<List<ConfigApplyItemResult>> ApplyFieldItemsAsync(
        IReadOnlyList<ConfigChangeItem> selected,
        string employeeName,
        ConfigSurface surface,
        AssistantActionKeySeed seed,
        string canonical,
        CancellationToken token)
    {
        var results = new List<ConfigApplyItemResult>(selected.Count);
        foreach (var item in selected)
        {
            var key = ItemKeyFor(seed, surface, item.Id, canonical);
            try
            {
                var (tableId, fieldId, input, original) = item.Payload switch
                {
                    FieldMetaPayload meta => (meta.TableId, meta.FieldId, meta.Overlay, meta.Target),
                    FieldDataSourcePayload dataSource => (
                        dataSource.TableId, dataSource.FieldId,
                        dataSource.Target with { Choosers = dataSource.Choosers }, dataSource.Target),
                    _ => (string.Empty, string.Empty, null, null),
                };
                if (input is null || tableId.Length == 0)
                {
                    results.Add(new ConfigApplyItemResult(item.Id, item.Target, false, "UNSUPPORTED_ITEM",
                        "这一项的写入载荷不可用，已跳过。", key));
                    continue;
                }

                await fields.UpdateIdempotentAsync(tableId, fieldId, input, original, employeeName, key, token);
                results.Add(new ConfigApplyItemResult(item.Id, item.Target, true, null, null, key));
            }
            catch (Exception exception) when (!token.IsCancellationRequested
                && exception is not OperationCanceledException)
            {
                // 请求已被取消（用户停手/连接断开）时不吞异常：取消不是"这一项失败"，它中断的是整次调用。
                logger.LogWarning(exception, "配置项应用失败 surface={Surface} item={Item}", surface, item.Id);
                results.Add(new ConfigApplyItemResult(item.Id, item.Target, false, "CONFIG_APPLY_FAILED",
                    exception.Message, key));
            }
        }
        return results;
    }

    /// <summary>
    /// 按钮与效果：既有保存端点是**整模块替换**语义，因此把选中项合并进目标模块的当前配置后一次保存
    /// （一次克隆操作 = 一次保存，与人工在配置页点一次保存等价，不逐项反复重建动作行）。
    /// 幂等键随保存一起进事务，重放同一批不会重复写。
    /// </summary>
    private async Task<List<ConfigApplyItemResult>> ApplyActionItemsAsync(
        ConfigCloneRequest request,
        ConfigChangePlan plan,
        IReadOnlyList<ConfigChangeItem> selected,
        string employeeName,
        string batchKey,
        CancellationToken token)
    {
        if (request.TargetModuleId is not int moduleId || request.TargetModuleId == request.SourceModuleId)
        {
            return
            [
                new ConfigApplyItemResult(string.Empty, plan.TargetLabel, false, "INVALID_TARGET",
                    "目标模块缺失，或与源模块是同一个模块（照抄自己不会产生改动）。", string.Empty),
            ];
        }
        var current = await moduleConfig.GetAsync(moduleId, token);
        if (current is null)
        {
            return
            [
                new ConfigApplyItemResult(string.Empty, plan.TargetLabel, false, "TARGET_MODULE_NOT_FOUND",
                    $"模块 #{moduleId} 不存在。", string.Empty),
            ];
        }

        var actions = current.Actions.OrderBy(action => action.Seq).ToList();
        var nextSeq = actions.Count == 0 ? 1 : actions.Max(action => action.Seq) + 1;
        var applied = new List<ConfigApplyItemResult>(selected.Count);
        foreach (var item in selected)
        {
            if (item.Payload is not ModuleActionPayload payload)
            {
                applied.Add(new ConfigApplyItemResult(item.Id, item.Target, false, "UNSUPPORTED_ITEM",
                    "这一项的写入载荷不可用，已跳过。", batchKey));
                continue;
            }
            var index = payload.TargetSeq is int seq
                ? actions.FindIndex(action => action.Seq == seq)
                : -1;
            if (index >= 0)
            {
                actions[index] = payload.Source with { Seq = actions[index].Seq };
            }
            else
            {
                actions.Add(payload.Source with { Seq = nextSeq++ });
            }
            applied.Add(new ConfigApplyItemResult(item.Id, item.Target, true, null, null, batchKey));
        }

        try
        {
            await moduleConfig.SaveAsync(
                moduleId, new SaveModuleBusinessConfigRequest([.. actions], [.. current.ValidationRules]),
                employeeName, token, batchKey);
        }
        catch (Exception exception) when (!token.IsCancellationRequested
            && exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "配置动作应用失败 surface={Surface} module={ModuleId}", plan.Surface, moduleId);
            return [.. applied.Select(item => item with
            {
                Applied = false,
                Code = item.Applied ? "CONFIG_SAVE_FAILED" : item.Code,
                Message = item.Applied ? exception.Message : item.Message,
            })];
        }
        return applied;
    }

    /// <summary>
    /// 批级幂等键：由**本次调用身份 + 配置面 + 请求意图**推导，与库内当前状态无关。
    ///
    /// <para>
    /// 内容（要写入的值）不进键是对的：值变了必然源于新的一次意图
    /// （新的工具调用标识或用户新一次确认），不会被误判成重放；
    /// 而"同一批被重放"的键完全一致，重放因此可识别。
    /// </para>
    /// </summary>
    internal static string BatchKeyFor(
        AssistantActionKeySeed seed,
        ConfigSurface surface,
        ConfigCloneRequest request,
        IReadOnlyList<string> itemIds)
        => AssistantActionIdempotency.Create(
            seed.ConversationId, seed.CallId, ConfigSurfaceNames.ActionOf(surface), Canonicalize(request, itemIds));

    /// <summary>请求意图的规范化文本：面 + 源与目标 + 点名对象 + 本次勾选的项，顺序稳定。</summary>
    internal static string Canonicalize(ConfigCloneRequest request, IReadOnlyList<string> itemIds)
        => string.Join('\u001f',
            ConfigSurfaceNames.Of(request.Surface),
            request.SourceTableId ?? string.Empty,
            request.TargetTableId ?? string.Empty,
            request.SourceModuleId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            request.TargetModuleId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            string.Join('\u001e', (request.Objects ?? []).OrderBy(item => item, StringComparer.OrdinalIgnoreCase)),
            string.Join('\u001e', itemIds.OrderBy(item => item, StringComparer.Ordinal)));

    /// <summary>逐项键：同一批里不同项各自成键（字段面逐项写入，重放时逐项也拦得住）。</summary>
    private static string ItemKeyFor(
        AssistantActionKeySeed seed, ConfigSurface surface, string itemId, string canonical)
        => AssistantActionIdempotency.Create(
            seed.ConversationId, seed.CallId, ConfigSurfaceNames.ActionOf(surface),
            itemId + '\u001d' + canonical);

    /// <summary>写成功后留一条批级痕迹。留痕失败不影响已完成的写入，但必须留下可观测的告警。</summary>
    private async Task RecordBatchAsync(
        string batchKey, ConfigSurface surface, string resultKey, CancellationToken token)
    {
        try
        {
            await idempotency.RecordAsync(
                connections, batchKey, moduleId: 0, ConfigSurfaceNames.ActionOf(surface), resultKey, token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "配置写幂等留痕失败 batch={BatchKey}", batchKey);
        }
    }

    /// <summary>
    /// 两道门：该类是否开放（逐类准入）+ 调用者是否有该类配置的写权限。
    /// 权限门与既有写端点**同一把**——字段/数据来源用字段维护门，按钮/效果用配置面门，不新造一档角色。
    /// </summary>
    private async Task<bool> AllowedAsync(string userId, ConfigSurface surface, CancellationToken token)
    {
        if (!options.Value.IsEnabled(surface))
        {
            return false;
        }
        var moduleId = surface is ConfigSurface.Fields or ConfigSurface.DataSources
            ? FieldAdminModuleId
            : MenuAdminModuleId;
        var permission = await permissions.GetAsync(userId, moduleId, token);
        return surface is ConfigSurface.Fields or ConfigSurface.DataSources
            ? permission.CanSetup
            : permission is { CanSetup: true, CanModuleConfig: true };
    }

    /// <summary>门不通过时给出可读原因：关闭说"尚未开放"，无权说"这一类配置的写权限不足"。</summary>
    private bool Denied(ConfigSurface surface, bool allowed, out (string Code, string Message) blocked)
    {
        if (allowed)
        {
            blocked = default;
            return false;
        }
        blocked = options.Value.IsEnabled(surface)
            ? (ForbiddenCode, surface is ConfigSurface.Fields or ConfigSurface.DataSources
                ? "你没有字段维护权限（需要字段维护的设置权），助手无法代为修改配置。"
                : "你没有配置面的写权限（需要设置权与模块配置权），助手无法代为修改配置。")
            : (SurfaceDisabledCode, $"「{SurfaceLabel(surface)}」这一类配置在助手侧尚未开放写入——"
                + "每一类配置由独立的开关放行，未放开之前助手只做只读的解释与不一致检查。");
        return true;
    }

    private static string SurfaceLabel(ConfigSurface surface) => surface switch
    {
        ConfigSurface.Fields => "字段配置",
        ConfigSurface.DataSources => "数据来源",
        ConfigSurface.Buttons => "自定义按钮",
        ConfigSurface.Effects => "效果键",
        _ => surface.ToString(),
    };
}
