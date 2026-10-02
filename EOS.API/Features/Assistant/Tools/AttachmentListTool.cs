using System.Globalization;
using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Models;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 单据附件的读取入口（窄契约）。单独立出来的理由与报表、历史一致：工具要能**离线**验证
/// 自己的权限门与输出（无附件权限、没有附件、超上限截断），而附件仓储是绑着数据库连接的具体类。
/// </summary>
public interface IAttachmentGateway
{
    /// <summary>按模块 + 主表 + 主键值数组列附件**元数据**（不含文件二进制）。</summary>
    Task<IReadOnlyList<AttachmentDto>> ListAsync(
        int moduleId, string masterTable, IReadOnlyList<string> keys, CancellationToken token);
}

/// <summary><see cref="IAttachmentGateway"/> 的默认实现：只做转发，权限与查询都在既有仓储里。</summary>
public sealed class AttachmentGateway(AttachmentRepository attachments) : IAttachmentGateway
{
    /// <inheritdoc />
    public Task<IReadOnlyList<AttachmentDto>> ListAsync(
        int moduleId, string masterTable, IReadOnlyList<string> keys, CancellationToken token) =>
        attachments.ListAsync(moduleId, masterTable, EncodeKeys(keys), token);

    /// <summary>
    /// 入库时的键格式：**主键值数组的 JSON 编码**（附件端点的 List 也这么传，两边必须一字不差）。
    ///
    /// <para>
    /// 在这里手拼一个逗号串看着更"自然"，但那样一条也匹配不到——而"匹配不到"会被读成"没有附件"，
    /// 是最难发现的一类错：它不报错，只是安静地给出错误的事实。所以把它抽成一个可断言的小函数，
    /// 而不是散在调用点的一行 <c>JsonSerializer.Serialize</c>。
    /// </para>
    /// </summary>
    internal static string EncodeKeys(IReadOnlyList<string> keys) => JsonSerializer.Serialize(keys);
}

/// <summary>
/// list_attachments：一张单据的附件清单（只读，**只给元数据**）。
///
/// <para>
/// 它补的是"这张单有没有附件、附的是什么"这一格——系统里 `ATTACHMENT` 表与上传下载链路早已齐备，
/// 但助手此前无从知道它们的存在。三条口径：
/// </para>
///
/// <para>
/// **附件另有一道权限位**：能看单据不等于能看它的附件，所以除了 <c>CanBrowse</c>，
/// 还要判 <c>FILE_VIEW</c>（与附件端点同一口径）。这里必须自己判，不能指望"能浏览单据"顺带放行。
/// </para>
///
/// <para>
/// **只给元数据，不给内容**：文件二进制要走附件对话框（那条路径有自己的权限与审计），
/// 助手无权代替用户下载文件；把二进制塞进模型上下文既昂贵又无从授权。
/// </para>
///
/// <para>
/// **先证明记录可见**：与历史工具同一套定位口径（<see cref="AssistantRecordLocator"/>），
/// 否则列附件就等于泄露数据范围之外记录的存在性。
/// </para>
/// </summary>
public sealed class AttachmentListTool(
    AssistantRecordLocator locator,
    IAttachmentGateway attachments,
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase, IPageContextTool
{
    public const string ToolName = "list_attachments";

    private PageContext? _page;

    private AssistantToolLimitsOptions Limits => runtime?.Current.Policy.ToolLimits ?? new();

    public override string Name => ToolName;

    public override AssistantToolRisk Risk => AssistantToolRisk.Read;

    public override string Description =>
        "列出一张单据的附件清单（文件名、大小、上传人、上传时间、备注）。"
        + "问「这张单有没有附件」「附的什么文件」时使用。"
        + "**只给元数据、不含文件内容**：要看内容请让用户在单据的附件对话框里自己打开。";

    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "module_id": { "type": "integer", "description": "模块 ID；与 module_title 二选一" },
            "module_title": { "type": "string", "description": "模块中文名关键字，如「客户订单」" },
            "_keys": { "type": "array", "items": { "type": "string" }, "description": "可选：单据主键值数组；缺省时按当前页面处境推断" }
          }
        }
        """;

    /// <summary>服务端注入页面处境（单据号 / 选中行）：只作定位，不作权限依据。</summary>
    public void UsePageContext(PageContext page) => _page = page;

    public override async Task<ToolExecutionResult> ExecuteAsync(
        string userId, JsonElement arguments, CancellationToken token)
    {
        var (target, deny) = await locator.LocateAsync(userId, arguments, _page, token);
        if (target is null) return deny ?? ToolExecutionResult.Deny("无法定位该单据，请给出模块与主键。");

        // 附件是**另一道权限位**：能看单据不等于能看它的附件（与附件端点同一口径）
        if (!target.Permission.Can(PermissionAction.FileView))
        {
            return ToolExecutionResult.Deny("当前用户没有该模块的附件查看权限（FILE_VIEW），无法列出附件。");
        }

        var items = await attachments.ListAsync(
            target.ModuleId, target.Definition.MasterTable, target.Keys, token);

        var output = new StringBuilder();
        output.Append("单据：模块 #").Append(target.ModuleId).Append(' ').Append(target.Definition.Title)
            .Append(" / ").Append(string.Join("-", target.Keys)).AppendLine();

        if (items.Count == 0)
        {
            // 如实说"没有"：我们只知道这里没有行，不猜"是不是没传上去"
            output.Append("附件：0 个（这张单没有上传过附件）");
            return ToolExecutionResult.Success(output.ToString());
        }

        var max = Math.Max(1, Limits.AttachmentListMax);
        output.Append("附件 ").Append(items.Count).Append(" 个");
        if (items.Count > max)
        {
            output.Append("（只列出前 ").Append(max).Append(" 个）");
        }

        output.AppendLine("：");
        foreach (var item in items.Take(max))
        {
            // 文件名给用户上传时的那个（ClientFileName）；缺失才回落到服务器落盘名
            output.Append("- ")
                .Append(string.IsNullOrWhiteSpace(item.ClientFileName) ? item.FileName : item.ClientFileName)
                .Append(" · ").Append(FormatSize(item.SizeBytes))
                .Append(" · ").Append(string.IsNullOrWhiteSpace(item.UploadedByDisplay) ? item.UploadedBy : item.UploadedByDisplay)
                // 与附件对话框同一口径：按库里存的值原样显示，不在这里另做时区换算
                .Append(" · ").Append(item.UploadedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(item.Remark))
            {
                output.Append(" · ").Append(item.Remark);
            }

            output.AppendLine();
        }

        output.Append("说明：以上只有文件元数据，不含文件内容。");
        return ToolExecutionResult.Success(output.ToString());
    }

    /// <summary>字节数按人读的单位给——把 1048576 直接扔给模型，它多半会换算错。</summary>
    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };
}
