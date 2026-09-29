using System.Globalization;
using EOS.API.Data;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Config;

/// <summary>
/// 配置克隆的**规划**（纯读）：照 A 配 B，算出"哪些项会变、变成什么、影响面是什么"。
///
/// <para>
/// 三条边界写在这里，因为它们是这个类的全部设计：
/// <list type="number">
/// <item>**目标化**：只覆盖点名的那几个对象（字段名 / 效果键），缺省才是该面上的全部对象；
/// 规划本身不写库，也不产生任何"顺带"的改动；</item>
/// <item>**不迁移高风险表达式与类型**：虚拟表达式、受控转换函数与字段类型都由物理列决定，
/// 跨表照抄会把源表的列引用带过去，因此克隆只搬"显示与校验属性"与"数据来源"；</item>
/// <item>**不碰授权**：按钮的克隆只搬 MODULE_BUSINESS_ACTION 的配置行，
/// 按钮授权（谁能用这个按钮）不随配置迁移——那是分权，不可代劳。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ConfigClonePlanner(
    FieldAdminRepository fields,
    ModuleBusinessConfigRepository moduleConfig,
    ILogger<ConfigClonePlanner> logger)
{
    /// <summary>一次规划覆盖的对象上限：克隆是目标化的操作，不提供"整表重排"。取值来自阈值单一事实源。</summary>
    public const int MaxObjects = Governance.AssistantActionLimits.MaxConfigCloneObjects;

    /// <summary>字段面搬运的属性（列名 → 人话标签 → 取值）。类型、表达式与数据来源不在其中。</summary>
    private static readonly (string Column, string Label, Func<FieldAdminInput, string?> Read)[] FieldMetaColumns =
    [
        ("F_DESC", "显示名", input => input.Label),
        ("DISPLAY_LENGTH", "列宽", input => input.Width.ToString(CultureInfo.InvariantCulture)),
        ("ITEM_ALIGN", "内容对齐", input => input.Align),
        ("HEADER_ALIGN", "表头对齐", input => input.HeaderAlign),
        ("DISPLAY_FORMAT", "显示格式", input => input.Format),
        ("IS_VISIBLE", "可见", input => Flag(input.IsVisible)),
        ("IS_DEFAULT_FIELDS", "默认列", input => Flag(input.IsDefault)),
        ("IS_QUERY", "可查询", input => Flag(input.IsQueryable)),
        ("IS_READONLY", "只读", input => Flag(input.IsReadonly)),
        ("IS_VERIFY", "必填", input => Flag(input.IsRequired)),
        ("IS_COST", "成本位", input => Flag(input.IsCost)),
        ("IS_SECRECY", "保密位", input => Flag(input.IsSecrecy)),
        ("DFT_VALUE", "默认值", input => input.DefaultValue),
        ("VERIFY_INDEX", "必填序号", input => input.VerifyIndex?.ToString(CultureInfo.InvariantCulture)),
        ("REGEX", "正则校验", input => input.Regex),
        ("F_REMARK", "备注", input => input.Remark),
        ("BROWSE_URL", "选择页", input => input.BrowseUrl),
        ("BROWSE_M_IDX", "选择模块", input => input.BrowseModuleId?.ToString(CultureInfo.InvariantCulture)),
        ("ONLY_CHOOSE", "只能选择", input => Flag(input.OnlyChoose)),
        ("CHOOSE_MULTI", "多选", input => Flag(input.ChooseMultiple)),
        ("CHOOSE_PAGE", "选择页类型", input => input.ChoosePage),
        ("CAN_COPY", "可复制", input => Flag(input.CanCopy)),
        ("FORM_OPTIONS", "选项", input => input.Options),
    ];

    /// <summary>动作面搬运的属性（按钮与效果共用同一批列，差别只在参与筛选的事件）。</summary>
    private static readonly (string Column, string Label, Func<BusinessActionDto, string?> Read)[] ActionColumns =
    [
        ("ENABLED", "启用", action => Flag(action.Enabled)),
        ("FAIL_MODE", "失败模式", action => action.FailMode),
        ("LABEL", "按钮标题", action => action.Label),
        ("CONFIRM_TAG", "点击前确认", action => Flag(action.ConfirmTag)),
        ("CONDITION_STRUCT", "触发条件", action => action.Condition),
        ("PARAM_STRUCT", "参数", action => action.Params),
        ("REVERSE_STRUCT", "反向", action => action.Reverse),
        ("REMARK", "备注", action => action.Remark),
        ("OPS", "公式行", action => action.Ops is { Count: > 0 } ops
            ? string.Join("；", ops.OrderBy(op => op.OpSeq)
                .Select(op => $"{op.TargetTable}.{op.TargetField} [{op.OpCode}]"))
            : null),
    ];

    /// <summary>按面规划。即使没有任何差异也是合法结果：它回答的是"要不要改"。</summary>
    public async Task<ConfigChangePlan> PlanAsync(ConfigCloneRequest request, CancellationToken token)
        => request.Surface switch
        {
            ConfigSurface.Fields => await PlanFieldMetaAsync(request, token),
            ConfigSurface.DataSources => await PlanDataSourcesAsync(request, token),
            ConfigSurface.Buttons => await PlanActionsAsync(request, ConfigSurface.Buttons, token),
            ConfigSurface.Effects => await PlanActionsAsync(request, ConfigSurface.Effects, token),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request.Surface, "未知的配置面。"),
        };

    // ===== 字段元数据面 =====

    private async Task<ConfigChangePlan> PlanFieldMetaAsync(ConfigCloneRequest request, CancellationToken token)
    {
        var sourceTable = request.SourceTableId?.Trim() ?? string.Empty;
        var targetTable = request.TargetTableId?.Trim() ?? string.Empty;
        if (sourceTable.Length == 0 || targetTable.Length == 0)
        {
            return Blocked(ConfigSurface.Fields, "缺少源表或目标表",
                "字段配置的克隆按表定位：请给出 source_table 与 target_table。");
        }

        var (sourceFields, notes) = await ReadTableFieldsAsync(sourceTable, request.Objects, token);
        if (sourceFields.Count == 0)
        {
            return Blocked(ConfigSurface.Fields, "源表没有可克隆的字段", $"表 {sourceTable} 没有登记字段元数据。");
        }
        notes.Add("克隆只搬运显示与校验属性：字段类型由物理列决定，虚拟表达式与受控转换函数不随之迁移"
            + "（跨表照抄会把源表的列引用带过去）。");

        var items = new List<ConfigChangeItem>();
        foreach (var source in sourceFields)
        {
            var target = await fields.GetMetadataAsync(targetTable, source.FieldId, token);
            if (target is null)
            {
                notes.Add($"目标表 {targetTable} 没有字段 {source.FieldId} 的元数据，未纳入对照（克隆不新增字段元数据）。");
                continue;
            }

            var changes = DiffColumns(FieldMetaColumns, source.Field, target.Field);
            if (changes.Count == 0)
            {
                continue;
            }

            items.Add(new ConfigChangeItem(
                ItemId(ConfigSurface.Fields, $"{targetTable}.{source.FieldId}"),
                ConfigSurface.Fields,
                $"{targetTable}.{source.FieldId}",
                $"{source.FieldId}（{target.Field.Label}）",
                changes,
                ["字段元数据：列表列与表单呈现、必填与正则校验；不经过效果链。"],
                Previewable: false,
                PreviewNote: "无法预演：字段元数据改动影响的是列表与表单的呈现和校验，不经过效果链，没有可预演的路径。",
                new FieldMetaPayload(
                    targetTable, source.FieldId, target.Field, ApplyFieldMetaOverlay(source.Field, target.Field))));
        }

        return new ConfigChangePlan(ConfigSurface.Fields, sourceTable, targetTable, null, null, items, notes);
    }

    // ===== 数据来源面 =====

    private async Task<ConfigChangePlan> PlanDataSourcesAsync(ConfigCloneRequest request, CancellationToken token)
    {
        var sourceTable = request.SourceTableId?.Trim() ?? string.Empty;
        var targetTable = request.TargetTableId?.Trim() ?? string.Empty;
        if (sourceTable.Length == 0 || targetTable.Length == 0)
        {
            return Blocked(ConfigSurface.DataSources, "缺少源表或目标表",
                "数据来源的克隆按表定位：请给出 source_table 与 target_table。");
        }

        var (sourceFields, notes) = await ReadTableFieldsAsync(sourceTable, request.Objects, token);
        if (sourceFields.Count == 0)
        {
            return Blocked(ConfigSurface.DataSources, "源表没有可克隆的字段", $"表 {sourceTable} 没有登记字段元数据。");
        }
        notes.Add("数据来源整组覆盖：来源表、过滤结构与回填映射一并照抄，"
            + "过滤条件与回填列在保存时按受控解析器重新校验。");

        var items = new List<ConfigChangeItem>();
        foreach (var source in sourceFields)
        {
            if (source.Field.Choosers.Count == 0)
            {
                notes.Add($"源字段 {source.FieldId} 没有登记数据来源，未纳入对照。");
                continue;
            }
            var target = await fields.GetMetadataAsync(targetTable, source.FieldId, token);
            if (target is null)
            {
                notes.Add($"目标表 {targetTable} 没有字段 {source.FieldId} 的元数据，未纳入对照。");
                continue;
            }

            var changes = DiffChoosers(source.Field.Choosers, target.Field.Choosers);
            if (changes.Count == 0)
            {
                continue;
            }

            items.Add(new ConfigChangeItem(
                ItemId(ConfigSurface.DataSources, $"{targetTable}.{source.FieldId}"),
                ConfigSurface.DataSources,
                $"{targetTable}.{source.FieldId}",
                $"{source.FieldId}（{target.Field.Label}）",
                changes,
                [$"数据来源：{source.Field.Choosers.Count} 个来源（源表 / 过滤条件 / 回填映射）。"],
                Previewable: false,
                PreviewNote: "无法预演：数据来源改动影响的是选择器的取数与回填，不经过效果链，没有可预演的路径。",
                new FieldDataSourcePayload(targetTable, source.FieldId, target.Field, source.Field.Choosers)));
        }

        return new ConfigChangePlan(ConfigSurface.DataSources, sourceTable, targetTable, null, null, items, notes);
    }

    // ===== 按钮面 / 效果面 =====

    private async Task<ConfigChangePlan> PlanActionsAsync(
        ConfigCloneRequest request, ConfigSurface surface, CancellationToken token)
    {
        if (request.SourceModuleId is not int sourceModuleId || request.TargetModuleId is not int targetModuleId)
        {
            return Blocked(surface, "缺少源模块或目标模块",
                "按钮与效果的克隆按模块定位：请给出 source_module_id 与 target_module_id。");
        }

        var source = await moduleConfig.GetAsync(sourceModuleId, token);
        if (source is null)
        {
            return Blocked(surface, "源模块不存在", $"模块 #{sourceModuleId} 没有业务动作配置。");
        }
        var moduleLabels = await ReadModuleLabelsAsync([sourceModuleId, targetModuleId], token);
        var sourceLabel = Label(moduleLabels, sourceModuleId);
        var targetLabel = Label(moduleLabels, targetModuleId);

        var target = await moduleConfig.GetAsync(targetModuleId, token)
            ?? new ModuleBusinessConfigDto(targetModuleId, [], []);
        if (target.Actions.Count == 0 && target.ValidationRules.Count == 0)
        {
            // 两表皆空模块禁止配置业务动作（与保存期同一口径）：提前说清楚，别让用户点完才发现。
            return Blocked(surface, "目标模块两表皆空",
                $"模块 #{targetModuleId} 的主表与明细表皆空，配置业务动作会被保存校验拒绝。", sourceLabel, targetLabel);
        }

        var wantsManual = surface == ConfigSurface.Buttons;
        var named = request.Objects is { Count: > 0 } objects
            ? new HashSet<string>(objects.Select(Normalize), StringComparer.OrdinalIgnoreCase)
            : null;
        var notes = new List<string>
        {
            wantsManual
                ? "按钮的克隆只搬配置行：按钮授权（谁能用这个按钮）不随配置迁移，需要单独在权限配置里授予。"
                : "效果键与公式行一并照抄，参数与反向结构在保存时按配置校验重新验证。",
        };
        var sourceActions = source.Actions
            .Where(action => BusinessActionCatalog.IsManualEvent(action.EventCode) == wantsManual)
            .Where(action => named is null || named.Contains(Normalize(action.EffectKey)))
            .OrderBy(action => action.Seq)
            .ToList();
        if (sourceActions.Count == 0)
        {
            return Blocked(surface, "源模块没有可克隆的动作",
                wantsManual ? "源模块没有自定义按钮配置行。" : "源模块没有效果链配置行。", sourceLabel, targetLabel);
        }

        var slots = BuildTargetSlots(target);
        var items = new List<ConfigChangeItem>();
        foreach (var action in sourceActions)
        {
            var match = TakeSlot(slots, action.EventCode, action.EffectKey);
            var existing = match is int seq ? target.Actions.FirstOrDefault(item => item.Seq == seq) : null;
            items.Add(new ConfigChangeItem(
                $"{ConfigSurfaceNames.Of(surface)}:{action.EventCode}@{action.EffectKey}#{action.Seq}",
                surface,
                $"{action.EventCode}@{action.EffectKey}",
                $"{ActionLabel(action)}（{EventLabel(action.EventCode)}）",
                DiffColumns(ActionColumns, action, existing),
                BuildActionImpacts(action),
                // 可预演性由预演报告判定（需要一张真实单据）：这里如实标"未预演"，不在卡上假装已经预演过。
                Previewable: false,
                PreviewNote: PreviewNoteOf(action.EventCode),
                new ModuleActionPayload(action, match)));
        }

        return new ConfigChangePlan(surface, sourceLabel, targetLabel, null, null, items, notes);
    }

    /// <summary>
    /// 目标模块里可被覆盖的槽位：按（事件 + 效果键）分组，给源行按顺序配对。
    /// 配对只决定"覆盖哪一行"或"新增一行"；真正写入时仍是既有的整模块保存语义。
    /// </summary>
    private static Dictionary<string, Queue<int>> BuildTargetSlots(ModuleBusinessConfigDto target)
    {
        var slots = new Dictionary<string, Queue<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in target.Actions.OrderBy(item => item.Seq))
        {
            var key = SlotKey(action.EventCode, action.EffectKey);
            if (!slots.TryGetValue(key, out var queue))
            {
                queue = new Queue<int>();
                slots[key] = queue;
            }
            queue.Enqueue(action.Seq);
        }
        return slots;
    }

    private static int? TakeSlot(Dictionary<string, Queue<int>> slots, string eventCode, string effectKey)
        => slots.TryGetValue(SlotKey(eventCode, effectKey), out var queue) && queue.Count > 0 ? queue.Dequeue() : null;

    private static string SlotKey(string eventCode, string effectKey) => $"{eventCode}\u001f{effectKey}";

    // ===== 共用 =====

    /// <summary>读源表的待克隆字段：点名的对象优先，缺省取该表登记的字段（上限见 <see cref="MaxObjects"/>）。</summary>
    private async Task<(IReadOnlyList<FieldAdminMetadata> Fields, List<string> Notes)> ReadTableFieldsAsync(
        string tableId, IReadOnlyList<string>? objects, CancellationToken token)
    {
        var page = await fields.GetFieldsAsync(tableId, null, 1, MaxObjects, token);
        var available = page.Items.Select(item => item.FieldId.Trim()).ToList();
        var notes = new List<string>();
        List<string> wanted;
        if (objects is { Count: > 0 })
        {
            wanted = [.. objects.Select(Normalize).Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)];
            foreach (var missing in wanted.Where(name => !available.Contains(name, StringComparer.OrdinalIgnoreCase)))
            {
                notes.Add($"点名的字段 {missing} 不在源表 {tableId} 的元数据里，已跳过。");
            }
        }
        else
        {
            wanted = [.. available.Take(MaxObjects)];
            if (page.Total > MaxObjects)
            {
                notes.Add($"源表 {tableId} 共 {page.Total} 个字段，本次只对照前 {MaxObjects} 个；"
                    + "克隆是目标化的操作，需要整表覆盖时请按点名对象分批。");
            }
        }

        var found = new List<FieldAdminMetadata>();
        foreach (var name in wanted.Where(name => available.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            if (await fields.GetMetadataAsync(tableId, name, token) is { } metadata)
            {
                found.Add(metadata);
            }
        }
        return (found, notes);
    }

    private static List<ConfigValueChange> DiffColumns<T>(
        (string Column, string Label, Func<T, string?> Read)[] columns, T source, T? target)
        => [.. columns
            .Select(column => new ConfigValueChange(
                column.Column, column.Label, target is null ? null : column.Read(target), column.Read(source)))
            .Where(change => !string.Equals(change.OldValue ?? "", change.NewValue ?? "", StringComparison.Ordinal))];

    private static List<ConfigValueChange> DiffChoosers(
        IReadOnlyList<FieldAdminChooser> source, IReadOnlyList<FieldAdminChooser> target)
    {
        var max = Math.Max(source.Count, target.Count);
        var changes = new List<ConfigValueChange>();
        for (var index = 0; index < max; index++)
        {
            var from = index < target.Count ? Describe(target[index]) : null;
            var to = index < source.Count ? Describe(source[index]) : null;
            if (!string.Equals(from ?? "", to ?? "", StringComparison.Ordinal))
            {
                changes.Add(new ConfigValueChange($"SERIAL_NO={index + 1}", $"数据来源 {index + 1}", from, to));
            }
        }
        return changes;
    }

    private static string Describe(FieldAdminChooser chooser)
        => $"{chooser.Table}"
            + (chooser.Active ? "（启用）" : "（停用）")
            + (string.IsNullOrWhiteSpace(chooser.Description) ? "" : $" {chooser.Description}")
            + (string.IsNullOrWhiteSpace(chooser.Filter) ? "" : $" 过滤：{chooser.Filter}")
            + (string.IsNullOrWhiteSpace(chooser.ReturnMapping) ? "" : $" 回填：{chooser.ReturnMapping}");

    /// <summary>
    /// 字段面的覆盖值：只把显示与校验属性从源搬过来；
    /// 类型、虚拟表达式、转换函数与数据来源保持目标现值（<see cref="FieldMetaColumns"/> 是这份清单的真源）。
    /// </summary>
    private static FieldAdminInput ApplyFieldMetaOverlay(FieldAdminInput source, FieldAdminInput target) => target with
    {
        Label = source.Label,
        Width = source.Width,
        Align = source.Align,
        HeaderAlign = source.HeaderAlign,
        Format = source.Format,
        IsVisible = source.IsVisible,
        IsDefault = source.IsDefault,
        IsQueryable = source.IsQueryable,
        IsReadonly = source.IsReadonly,
        IsRequired = source.IsRequired,
        IsCost = source.IsCost,
        IsSecrecy = source.IsSecrecy,
        DefaultValue = source.DefaultValue,
        VerifyIndex = source.VerifyIndex,
        Regex = source.Regex,
        Remark = source.Remark,
        BrowseUrl = source.BrowseUrl,
        BrowseModuleId = source.BrowseModuleId,
        OnlyChoose = source.OnlyChoose,
        ChooseMultiple = source.ChooseMultiple,
        ChoosePage = source.ChoosePage,
        CanCopy = source.CanCopy,
        Options = source.Options,
    };

    private static IReadOnlyList<string> BuildActionImpacts(BusinessActionDto action)
    {
        if (action.Ops is not { Count: > 0 } ops)
        {
            return [$"服务型效果（{action.EffectKey}）：影响面写在参数里，执行时由处理器决定目标行。"];
        }
        return [.. ops.OrderBy(op => op.OpSeq).Select(op => $"{op.TargetTable}.{op.TargetField} [{op.OpCode}]")];
    }

    /// <summary>
    /// 事件能否被预演。判定与预演端点的闭集一致，否则会出现"卡上说能预演、点下去 400"。
    /// 这里是**如实标注**：能预演也要说清"还需要一张真实单据"。
    /// </summary>
    private static string PreviewNoteOf(string eventCode) => eventCode.Trim().ToUpperInvariant() switch
    {
        "APPROVE_EFFECT" or "DEAPPROVE" =>
            "可预演：需要一张真实单据的主键才能跑预演；未提供单据时按无法预演处理。",
        "SAVE" =>
            "无法预演：保存阶段的效果链不在预演覆盖内（预演只覆盖批核生效与解批，约占动作行的 82%），"
            + "保存后的效果组合要到真实保存时才看得出。",
        _ =>
            "无法预演：该事件不在预演覆盖内（预演只支持批核生效与解批）。",
    };

    private static string ActionLabel(BusinessActionDto action)
        => !string.IsNullOrWhiteSpace(action.Label)
            ? action.Label!
            : !string.IsNullOrWhiteSpace(action.EffectName) ? action.EffectName! : action.EffectKey;

    private static string EventLabel(string eventCode) =>
        BusinessActionLabels.Events.TryGetValue(eventCode, out var label) ? label : eventCode.Trim();

    private static string ItemId(ConfigSurface surface, string target) => $"{ConfigSurfaceNames.Of(surface)}:{target}";

    private static string Normalize(string? value) => value?.Trim() ?? string.Empty;

    private static string Flag(bool value) => value ? "是" : "否";

    private async Task<IReadOnlyDictionary<int, string>> ReadModuleLabelsAsync(
        IReadOnlyList<int> moduleIds, CancellationToken token)
    {
        var modules = await fields.GetModulesAsync(token);
        return moduleIds.Distinct().ToDictionary(
            id => id,
            id => modules.FirstOrDefault(item => item.Id == id)?.Label ?? $"模块 #{id}");
    }

    private static string Label(IReadOnlyDictionary<int, string> labels, int moduleId)
        => labels.TryGetValue(moduleId, out var label) ? label : $"模块 #{moduleId}";

    private ConfigChangePlan Blocked(
        ConfigSurface surface, string code, string message, string? source = null, string? target = null)
    {
        logger.LogInformation("配置克隆无法规划 surface={Surface} code={Code}", surface, code);
        return new ConfigChangePlan(surface, source ?? "（未指定）", target ?? "（未指定）", code, message, [], []);
    }
}
