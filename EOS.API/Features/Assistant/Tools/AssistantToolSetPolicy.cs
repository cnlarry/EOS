namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 按页面类型裁剪**发给模型的工具声明**（不是权限判定）。
///
/// <para>
/// 权限始终由工具内部按当前用户重新授权，本类只决定“这一页要不要把某个工具摆到模型面前”：
/// 业务页不需要配置写工具，配置页不需要记录写/单据动作工具。减少声明数量可以降低模型选错工具
/// 的概率与每轮 token；被裁掉的工具仍然在注册表里，只是这一轮看不到、也调不到。
/// </para>
///
/// <para>
/// 页面类型未知（无页面处境或非白名单值）时**不裁剪**——宁可多给，不在不确定时让模型少一条路。
/// </para>
/// </summary>
public static class AssistantToolSetPolicy
{
    private static readonly string[] ConfigWrite =
    [
        CloneModuleConfigTool.ToolName,
        PreviewConfigChangeTool.ToolName,
        ApplyConfigChangeTool.ToolName,
    ];

    private static readonly string[] SchemaAdmin =
    [
        ListTablesTool.ToolName,
        ListViewsTool.ToolName,
        ListProceduresTool.ToolName,
        DiagnoseModuleTool.ToolName,
        ApplyChangeSetTool.ToolName,
    ];

    private static readonly string[] RecordWrite =
    [
        DraftRecordTool.ToolName,
        PreviewRecordActionTool.ToolName,
        ApplyRecordActionTool.ToolName,
    ];

    private static readonly string[] DocumentAction =
    [
        ListDocumentActionsTool.ToolName,
        PreviewDocumentActionTool.ToolName,
        ApplyDocumentActionTool.ToolName,
    ];

    private static readonly string[] ApprovalRequest =
    [
        PreviewBatchDecisionTool.ToolName,
    ];

    /// <summary>这一页是否把该工具发给模型。未知页面类型一律 true。</summary>
    public static bool IsOffered(string toolName, string? pageType)
    {
        if (string.IsNullOrWhiteSpace(pageType)) return true;
        var normalized = pageType.Trim().ToLowerInvariant();

        if (normalized.StartsWith("config-", StringComparison.Ordinal))
        {
            return !RecordWrite.Contains(toolName, StringComparer.Ordinal)
                && !DocumentAction.Contains(toolName, StringComparer.Ordinal)
                && !ApprovalRequest.Contains(toolName, StringComparer.Ordinal);
        }

        if (normalized is "list" or "view" or "edit" or "new" or "copy")
        {
            return !ConfigWrite.Contains(toolName, StringComparer.Ordinal)
                && !SchemaAdmin.Contains(toolName, StringComparer.Ordinal);
        }

        return true;
    }
}
