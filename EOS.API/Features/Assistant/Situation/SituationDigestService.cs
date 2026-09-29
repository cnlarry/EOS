using EOS.API.Features.Assistant.Diagnosis;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Situation;

/// <summary>
/// 摘要条目类别：<c>overdue</c> 超期滞留（此刻状态）、<c>blocked-now</c> 此刻办不下去（此刻状态）、
/// <c>rejected</c> 最近被拒（**历史事件**，与"这张单此刻过不了"不可互相替代）。
/// </summary>
public static class SituationDigestKinds
{
    public const string Overdue = "overdue";

    /// <summary>此刻过不了保存校验 / 状态不允许改动。命名带 <c>now</c>，与"最近被拒"这种历史事件区分。</summary>
    public const string BlockedNow = "blocked-now";

    public const string Rejected = "rejected";
}

/// <summary>打开即见的一条结构化摘要（主键与原因均由服务端规则算出，不经模型）。</summary>
public sealed record SituationDigestItem(
    string Kind,
    int ModuleId,
    string ModuleTitle,
    string Key,
    string Reason,
    string? OccurredAt,
    int AgeDays);

/// <summary>打开即见的结构化摘要：条目 + 用到的来源 + 覆盖率提醒。</summary>
public sealed record SituationDigest(
    IReadOnlyList<SituationDigestItem> Items,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Caveats)
{
    public bool IsEmpty => Items.Count == 0;
}

/// <summary>
/// 零输入主动摘要的规则引擎：**不经模型**产出结构化结果，前端渲染为卡片。
///
/// <para>
/// 主来源是**超期滞留单据**（建立日期早于阈值且未批核）——可由生命周期列实时算出，是确定性事实。
/// 待办计数只作辅助：审批流未运行时它恒为 0，若以它为主来源，用户打开会看到一片 0。
/// </para>
/// <para>
/// 滞留扫描**有界**：候选模块取自本人最近的活动记录（上限可配），逐个走
/// <c>IPermissionService</c> + 工作台定义 + 数据范围查询，与列表同源；无权模块直接跳过。
/// 覆盖率不足时如实写进 <c>Caveats</c>，不假装全量。
/// </para>
/// </summary>
public sealed class SituationDigestService(
    IWorkbenchSearchGateway gateway,
    IPermissionService permissions,
    ISituationFactsReader facts,
    AssistantSituationService situation,
    IBlockedRecordProbe blockedProbe,
    AssistantSituationBudget budget,
    ILogger<SituationDigestService> logger)
{
    public async Task<SituationDigest> BuildAsync(string userId, SituationContext where, CancellationToken token)
    {
        var limits = budget.Limits;
        var items = new List<SituationDigestItem>();
        var sources = new List<string>();
        var caveats = new List<string>();

        // 主动巡检**可停**：任一相关阈值配 ≤ 0，对应来源即停。
        // 0 与负数是"关闭"语义，不是"最勤"——否则一个手滑的配置会把扫描放大到全库。
        var overdueEnabled = limits.DigestMaxItems > 0 && limits.DigestModuleScanLimit > 0
            && limits.OverdueDays > 0 && limits.OverdueMaxAgeDays > 0;
        var blockedEnabled = overdueEnabled && limits.BlockedNowScanRecords > 0
            && limits.BlockedNowMaxAgeDays > 0 && limits.BlockedNowProbeRules > 0;
        var rejectedEnabled = limits.DigestMaxItems > 0
            && limits.RecentFailureDays > 0 && limits.RecentFailureLimit > 0;

        var candidates = await LoadCandidatesAsync(userId, where, token);
        IReadOnlyList<SituationRecentFailure> failures = rejectedEnabled
            ? await situation.LoadRecentFailuresAsync(userId, token)
            : [];
        var titles = failures.Count == 0
            ? new Dictionary<int, string>()
            : await LoadModuleTitlesAsync(token);

        var scanned = overdueEnabled ? await LoadOverdueAsync(userId, candidates, items, token) : 0;
        if (scanned > 0) sources.Add($"overdue:扫描 {scanned} 个模块");
        if (!overdueEnabled) caveats.Add("超期滞留来源已按阈值关闭（任一相关阈值配成 0 或负数即停）。");

        var blocked = blockedEnabled ? await LoadBlockedNowAsync(userId, candidates, items, token) : 0;
        if (blocked > 0) sources.Add($"blocked-now:逐单求值 {blocked} 个模块");
        if (!blockedEnabled) caveats.Add("「此刻办不下去」来源已按阈值关闭（任一相关阈值配成 0 或负数即停）。");

        var rejected = 0;
        foreach (var failure in failures)
        {
            if (items.Count >= limits.DigestMaxItems) break;
            items.Add(new SituationDigestItem(
                SituationDigestKinds.Rejected,
                failure.ModuleId ?? 0,
                failure.ModuleId is int moduleId && titles.TryGetValue(moduleId, out var title) ? title : string.Empty,
                string.Empty,
                $"{failure.Action}：{failure.Summary}",
                failure.OccurredAt,
                0));
            rejected++;
        }

        if (rejected > 0) sources.Add($"rejected:最近 {limits.RecentFailureDays} 天");
        if (!rejectedEnabled) caveats.Add("最近被拒来源已按阈值关闭（任一相关阈值配成 0 或负数即停）。");

        var pending = await situation.LoadPendingAsync(userId, token);
        caveats.Add(pending.MyApproval + pending.StartedInFlight == 0
            ? "待我审批与本人在途流程当前计数为 0（审批流未在运行），摘要来源以滞留与被拒为主。"
            : "待办计数只作辅助，逐单处置仍须按权限重新读取。");
        if (candidates.Count >= limits.DigestModuleScanLimit)
        {
            caveats.Add($"滞留扫描仅覆盖本人最近活动过的模块（上限 {limits.DigestModuleScanLimit} 个），并非全库全量。");
        }

        if (blockedEnabled)
        {
            caveats.Add($"「此刻办不下去」只判可只读定论的判据（关联存在性与结案状态）；"
                + $"其余校验类别只能在保存时由引擎判定，摘要不逐条求值（每模块最多看最近 {limits.BlockedNowScanRecords} 单、{limits.BlockedNowMaxAgeDays} 天内）。");
        }

        return new SituationDigest(items, sources, caveats);
    }

    private async Task<int> LoadOverdueAsync(
        string userId, IReadOnlyList<int> moduleIds, List<SituationDigestItem> items, CancellationToken token)
    {
        var limits = budget.Limits;
        var cutoff = DateTime.UtcNow.Date.AddDays(-limits.OverdueDays).ToString("yyyy-MM-dd");
        var oldest = DateTime.UtcNow.Date.AddDays(-limits.OverdueMaxAgeDays).ToString("yyyy-MM-dd");
        var scanned = 0;
        foreach (var moduleId in moduleIds)
        {
            if (items.Count(item => item.Kind == SituationDigestKinds.Overdue) >= limits.DigestMaxItems) break;
            var permission = await permissions.GetAsync(userId, moduleId, token);
            if (!permission.CanBrowse) continue;
            var (execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail) = permission.Scope();
            var definition = await gateway.GetDefinitionAsync(
                moduleId, userId, execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail, token);
            if (definition is null) continue;
            var keys = definition.MasterFields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!keys.Contains("CREATE_DATE") || !keys.Contains("CONFIRM_TAG")) continue;

            scanned++;
            try
            {
                // 取数口径：**按建立日期倒序**（新→旧）并加年龄上界。
                // 按模块默认排序取前 N 条会在遗留数据上取到多年前的未批核单（实测 3120 天前），
                // 那类条目既不是"我手上压着什么"，也会把摘要位挤满。
                var query = new WorkbenchQuery(
                [
                    new WorkbenchQueryCondition("CREATE_DATE", "lte", cutoff, null, null),
                    new WorkbenchQueryCondition("CREATE_DATE", "gte", oldest, null, null),
                    new WorkbenchQueryCondition("CONFIRM_TAG", "eq", "0", null, null),
                ]);
                var remaining = Math.Max(1, limits.DigestMaxItems - items.Count);
                var data = await gateway.GetRowsAsync(
                    definition, detail: false, new Dictionary<string, string>(), page: 1,
                    pageSize: remaining, token, query: query,
                    sortField: "CREATE_DATE", sortDirection: "desc",
                    dataFilter: permission.Rights.DataFilter);
                foreach (var row in data.Rows)
                {
                    // 上限由本服务兜住：不假设查询实现一定会遵守 pageSize
                    if (items.Count >= limits.DigestMaxItems) break;
                    var key = string.Join("|", definition.MasterPkOrder
                        .Select(pk => row.TryGetValue(pk, out var value) ? value?.ToString() ?? string.Empty : string.Empty));
                    var ageDays = AgeDays(row);
                    items.Add(new SituationDigestItem(
                        SituationDigestKinds.Overdue,
                        definition.ModuleId,
                        definition.Title,
                        budget.Clip(key, limits.MaxValueLength),
                        $"已录入 {ageDays} 天仍未批核（共 {data.Total} 条同类滞留）",
                        row.TryGetValue("CREATE_DATE", out var created) ? created?.ToString() : null,
                        ageDays));
                }
            }
            catch (ArgumentException ex)
            {
                // 定义里没有可用的日期/状态列时查询会被拒：如实记为覆盖缺口，不影响其余模块。
                logger.LogWarning(ex, "助手滞留扫描跳过模块（查询条件不被接受）module={ModuleId}", moduleId);
            }
        }

        return scanned;
    }

    /// <summary>
    /// 第三条主来源（**此刻状态**）：逐单求值"当前过不了保存校验 / 状态不允许改动"。
    /// 判据与对象级诊断**同一套**（<see cref="IBlockedRecordProbe"/>），不另写一份口径；
    /// 扫描有界（每模块最近若干单 + 年龄上界），判不了一律不报（宁可少报）。
    /// </summary>
    private async Task<int> LoadBlockedNowAsync(
        string userId, IReadOnlyList<int> moduleIds, List<SituationDigestItem> items, CancellationToken token)
    {
        var limits = budget.Limits;
        var scanned = 0;
        foreach (var moduleId in moduleIds)
        {
            if (items.Count >= limits.DigestMaxItems) break;
            var permission = await permissions.GetAsync(userId, moduleId, token);
            if (!permission.CanBrowse) continue;
            var (execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail) = permission.Scope();
            var definition = await gateway.GetDefinitionAsync(
                moduleId, userId, execTag, canViewCost, canViewSecrecy, deniedMaster, deniedDetail, token);
            if (definition is null) continue;
            var keys = definition.MasterFields.Select(field => field.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!keys.Contains("CREATE_DATE") || !keys.Contains("CONFIRM_TAG")) continue;

            scanned++;
            IReadOnlyList<DiagnosisBlockedRecord> blocked = [];
            try
            {
                blocked = await blockedProbe.ProbeBlockedAsync(userId, definition, permission, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单模块判不了不影响其余模块；如实记录，不假装全量。
                logger.LogWarning(ex, "助手摘要跳过模块的「此刻办不下去」扫描 module={ModuleId}", moduleId);
            }

            foreach (var record in blocked)
            {
                if (items.Count >= limits.DigestMaxItems) break;
                items.Add(new SituationDigestItem(
                    SituationDigestKinds.BlockedNow,
                    definition.ModuleId,
                    definition.Title,
                    budget.Clip(record.Key, limits.MaxValueLength),
                    budget.Clip($"此刻：{Clean(record.Reason)}", limits.DigestTextLength),
                    null,
                    0));
            }
        }

        return scanned;
    }

    /// <summary>摘要文案去掉诊断占位符：<c>{ROWS}</c> 对应的明细行由 diagnose_record 逐单展开。</summary>
    private static string Clean(string text) => text.Replace("{ROWS}", string.Empty).Trim();

    private static int AgeDays(IReadOnlyDictionary<string, object?> row)
    {
        if (!row.TryGetValue("CREATE_DATE", out var value) || value is null) return 0;
        var text = value.ToString();
        if (!DateTime.TryParse(text, out var created)) return 0;
        var days = (int)(DateTime.UtcNow.Date - created.Date).TotalDays;
        return days > 0 ? days : 0;
    }

    /// <summary>模块编号 → 名称（受权模块清单，用于给"最近被拒"条目补上模块名）。</summary>
    private async Task<Dictionary<int, string>> LoadModuleTitlesAsync(CancellationToken token)
    {
        var modules = await gateway.ListAssistantModulesAsync(null, token);
        return modules
            .GroupBy(module => module.Id)
            .ToDictionary(group => group.Key, group => group.First().Title);
    }

    /// <summary>候选模块：当前所在模块 + 本人最近活动过的模块（按最近活动时间倒序，有上限）。</summary>
    private async Task<IReadOnlyList<int>> LoadCandidatesAsync(
        string userId, SituationContext where, CancellationToken token)
    {
        var limits = budget.Limits;
        var candidates = new List<int>();
        if (where.ModuleId is int current) candidates.Add(current);

        var recentModules = await facts.LoadActivityModulesAsync(
            userId, limits.ActivityWindowDays, limits.DigestModuleScanLimit, token);
        foreach (var moduleId in recentModules)
        {
            if (!candidates.Contains(moduleId)) candidates.Add(moduleId);
        }

        return [.. candidates.Take(limits.DigestModuleScanLimit)];
    }
}
