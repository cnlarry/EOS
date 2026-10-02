namespace EOS.API.Features.Assistant.Parameters;

/// <summary>
/// 工具名 ↔ 能力面参数键的**机械映射**（ADR-030 §5.3.4）。
///
/// <para>
/// 键名由工具名生成：`TOOL_` + 工具名转大写（下划线原样保留，`describe_module` → `TOOL_DESCRIBE_MODULE`）。
/// 于是"哪一个开关管哪一个工具"不需要第三处登记，也不会出现"改了工具名、参数键没跟着改"的悬空参数。
/// </para>
///
/// <para>
/// 这里写下的是 29 个工具名（其中 2 个为批后追加，见 <see cref="AppendedToolNames"/>）。
/// **它们与工具类里 <c>ToolName</c> 常量的一致性由离线门禁断言**
/// （扫源码里的 <c>ToolName = "…"</c> 并逐名比对）：新加一个工具却忘了加开关，门禁当场变红——
/// 而"忘了加开关"的表现正是新工具绕过了能力面管理。
/// </para>
/// </summary>
public static class AssistantToolKeys
{
    /// <summary>
    /// **批后追加**的工具名：清单与中文标题照常覆盖它们，但开关的**声明位置**排在能力面组的最后。
    ///
    /// <para>
    /// 理由只有一个，但很硬：组内序号由声明顺序推导（按 10 递增），而 CAPABILITY 组的声明顺序是
    /// "工具开关 → 动作族开关"。把新工具的名字插回 <see cref="ToolNames"/> 的中间，会让动作族与
    /// 其后所有参数的序号整体后移，与**已经落库的行**对不上（连库门禁逐字段比对序号，正是为此存在）。
    /// 于是新工具一律追加在组末——就像新增参数一律追加在各自域的末尾一样。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> AppendedToolNames { get; } =
        ["list_reports", "run_report", "get_record_history", "list_attachments"];

    /// <summary>工具名（与各工具类的 <c>ToolName</c> 常量逐一对应）。</summary>
    public static IReadOnlyList<string> ToolNames { get; } =
    [
        "list_modules",
        "list_tables",
        "list_views",
        "list_procedures",
        "describe_module",
        "describe_table",
        "get_form_schema",
        "get_field_relations",
        "search_records",
        "get_record_detail",
        "get_my_digest",
        "diagnose_record",
        "diagnose_module",
        "get_module_flow",
        "list_my_capabilities",
        "enum_metrics",
        "resolve_metric",
        "kb_search",
        "describe_mechanism",
        "draft_record",
        "apply_changeset",
        "preview_record_action",
        "apply_record_action",
        "preview_batch_decision",
        "clone_module_config",
        "preview_config_change",
        "apply_config_change",
        // 批后追加：清单需要覆盖它们（标题与开关覆盖面按 ToolNames 断言），声明位置见 AppendedToolNames
        .. AppendedToolNames,
    ];

    /// <summary>
    /// 工具开关在 3105 页面上的中文标题（键 = 工具名）。
    ///
    /// <para>
    /// 为什么单有一张表：开关标题此前就是工具名本身（<c>search_records</c>），而 3105 的读者是管理员——
    /// 他要判断的是"该不该关掉它"，读标识符不比读中文快。**标题与工具清单必须一一对应**，
    /// 由离线门禁断言（多一个少一个都红）：少一个只是标题退回标识符，多一个则说明表里躺着
    /// 一个不存在的工具——后者会让人以为"关掉它有用"。
    /// </para>
    ///
    /// <para>
    /// 中文标题里不写"工具"二字（一屏 27 行都带它只会变噪），动宾结构尽量对齐：
    /// 读类用"查/看/列"、试算类用"试算"、执行类用"执行"——**风险级别看标题就能分辨**。
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<string, string> Labels { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 结构与元数据（只读）
            ["list_modules"] = "列出模块",
            ["list_tables"] = "列出数据表",
            ["list_views"] = "列出视图",
            ["list_procedures"] = "列出存储过程",
            ["describe_module"] = "查看模块结构",
            ["describe_table"] = "查看表结构",
            ["get_form_schema"] = "查看表单结构",
            ["get_field_relations"] = "查看字段关系",
            // 业务数据（只读，逐条过权限门）
            ["search_records"] = "搜索单据",
            ["get_record_detail"] = "查看单据详情",
            ["get_my_digest"] = "查看我的待办摘要",
            ["diagnose_record"] = "诊断单据卡在哪",
            ["diagnose_module"] = "诊断模块配置",
            ["get_module_flow"] = "查看单据流转",
            ["list_my_capabilities"] = "查看我的操作权限",
            // 指标与知识
            ["enum_metrics"] = "查看指标口径",
            ["resolve_metric"] = "按口径取数",
            ["kb_search"] = "检索知识库",
            ["describe_mechanism"] = "查看机制说明",
            // 报表（只读）
            ["list_reports"] = "列出模块报表",
            ["run_report"] = "取报表数据",
            ["get_record_history"] = "查看单据历史",
            ["list_attachments"] = "查看单据附件清单",
            // 试算与执行（风险由低到高）
            ["draft_record"] = "试算：起草单据",
            ["apply_changeset"] = "试算：应用变更集",
            ["preview_record_action"] = "预演：记录动作",
            ["apply_record_action"] = "执行：记录动作",
            ["preview_batch_decision"] = "准备批核请求卡（只准备，不处置）",
            ["clone_module_config"] = "试算：克隆模块配置",
            ["preview_config_change"] = "预演：配置变更",
            ["apply_config_change"] = "执行：配置变更",
        };

    /// <summary>取中文标题。未登记时退回工具名——门禁（<c>Tool_Labels_Cover_Every_Tool</c>）会先红。</summary>
    public static string LabelOf(string toolName) =>
        Labels.TryGetValue(toolName, out var label) ? label : toolName;

    /// <summary>按工具名生成参数键。</summary>
    public static string ParameterKeyOf(string toolName) =>
        "TOOL_" + toolName.Trim().ToUpperInvariant();

    /// <summary>反向：参数键 → 工具名。不是工具开关的键返回 false。</summary>
    public static bool TryGetToolName(string? parameterKey, out string toolName)
    {
        toolName = string.Empty;
        var key = parameterKey?.Trim();
        if (string.IsNullOrEmpty(key) || !key.StartsWith("TOOL_", StringComparison.OrdinalIgnoreCase)) return false;

        var wanted = key["TOOL_".Length..];
        foreach (var name in ToolNames)
        {
            if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                toolName = name;
                return true;
            }
        }

        return false;
    }
}
