using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EOS.API.Features.Assistant.Actions;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 准备一张「操作请求卡」：逐行给出此刻能否处置及原因，交由用户点确认。
///
/// <para>
/// 这里**只准备请求，不执行处置**：批核 / 解批 / 结案 / 取消结案是职权行使，不可代签，
/// 助手侧既没有它们的动作注册项，也调不到它们的执行入口。用户在卡片上点确认后，
/// 由界面直接调既有端点完成——执行主体是那次点击，不是模型自己的决定。
/// </para>
/// </summary>
public sealed class PreviewBatchDecisionTool(
    AssistantApprovalRequestService requests,
    IWorkbenchSearchGateway gateway) : AssistantToolBase, IPageContextTool
{
    public const string ToolName = "preview_batch_decision";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private PageContext? _page;

    public void UsePageContext(PageContext page) => _page = page;

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Draft;

    public override string Description =>
        "为一组单据准备「操作请求卡」：逐行给出此刻能否处置（批核 / 解批 / 结案 / 取消结案）及原因，"
        + "由用户在卡片上逐行勾选后点确认执行。助手只准备请求，不执行处置——"
        + "用户没在卡片上点确认之前，什么都不会发生。";

    public override string ParametersJson => AssistantApprovalRequestArguments.ParametersJson;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var parsed = AssistantApprovalRequestArguments.TryParse(arguments, out var error);
        if (parsed is null)
        {
            return ToolExecutionResult.Deny(error);
        }

        var moduleId = parsed.ModuleId > 0
            ? parsed.ModuleId
            : await ResolveModuleIdAsync(parsed.ModuleTitle, token);
        if (moduleId <= 0)
        {
            return ToolExecutionResult.Deny("未找到匹配的 ERP 模块。请向用户确认准确的模块名称或给出 module_id。");
        }

        var preview = await requests.PreviewAsync(userId, moduleId, parsed.Action, WithPageKeys(parsed.Rows), token);
        // 请求卡草稿与界面"重算逐行判定"端点是同一份形状：模型与用户看到的是同一个东西。
        var draft = AssistantApprovalDtos.ToDraft(preview);
        return new ToolExecutionResult(
            preview.ModuleDenialCode is null, Render(preview) + "\n" + JsonSerializer.Serialize(draft, JsonOptions), draft);
    }

    private async Task<int> ResolveModuleIdAsync(string? title, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }
        return await gateway.FindGenericModuleIdByTitleAsync(title.Trim(), token) ?? 0;
    }

    /// <summary>缺主键的行按页面处境补：当前打开的单据优先，其次是列表里选中的那一行。</summary>
    private IReadOnlyList<IReadOnlyList<string>> WithPageKeys(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        if (_page is null || rows.All(row => row.Count > 0))
        {
            return rows;
        }

        IReadOnlyList<string>? fallback = _page.Selection is { Count: > 0 } selection
            ? selection
            : string.IsNullOrWhiteSpace(_page.DocNo) ? null : [_page.DocNo.Trim()];
        if (fallback is null)
        {
            return rows;
        }

        return [.. rows.Select(row => row.Count > 0 ? row : fallback)];
    }

    private static string Render(ApprovalRequestPreview preview)
    {
        var title = preview.ModuleTitle.Length > 0
            ? $"{preview.ModuleTitle}（#{preview.ModuleId}）"
            : $"模块 #{preview.ModuleId}";
        if (preview.ModuleDenialCode is not null)
        {
            return $"无法为{title}准备操作请求卡：{preview.ModuleDenialMessage ?? preview.ModuleDenialCode}"
                + $"（原因码 {preview.ModuleDenialCode}）。";
        }

        var label = AssistantApprovalActionNames.LabelOf(preview.Action);
        var allowed = preview.Rows.Count(row => row.Allowed);
        var text = new StringBuilder($"操作请求卡{label}｜{title}：{preview.Rows.Count} 行，"
            + $"可执行 {allowed} 行、不可执行 {preview.Rows.Count - allowed} 行。"
            + "请用户在卡片上逐行确认后执行，助手不代为执行。");
        foreach (var row in preview.Rows)
        {
            var keys = row.Keys.Count > 0 ? string.Join('/', row.Keys) : "（缺主键）";
            text.AppendLine().Append("- ").Append(keys)
                .Append(row.Status.Length > 0 ? $"（{row.Status}）" : string.Empty)
                .Append(row.Allowed ? "：可执行。" : $"：不可执行（{row.DenialCode}）{row.DenialMessage}");
        }
        foreach (var note in preview.Notes)
        {
            text.AppendLine().Append("备注：").Append(note);
        }
        return text.ToString().TrimEnd();
    }
}
