using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Kb;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// kb_search：RAG 知识库检索（ERP/MES/HR 常识与制度）。
/// 集合级可见性在 API 内过滤；命中含 module + _keys 业务引用的片段，逐条按既有
/// 权限门复核（R2），失败的片段直接丢弃、不返回模型。
/// </summary>
public sealed class KbSearchTool(
    IKnowledgeRepository repository,
    IEmbeddingModel embedding,
    IPermissionService permissions,
    IWorkbenchSearchGateway gateway,
    // 容器会把运行期配置注进来；离线构造（单测）不传时退回参数默认值——这两项是长度与条数上限，
    // 退回默认不会放宽任何权限。
    IAssistantRuntimeConfig? runtime = null) : AssistantToolBase
{
    public const string ToolName = "kb_search";

    public override string Name => ToolName;
    public override AssistantToolRisk Risk => AssistantToolRisk.Read;
    public override string Description =>
        "检索企业知识库（ERP/MES/HR 常识、制度、业务规则）。用户询问概念、规则、操作说明时使用；返回片段均带来源标记。";
    public override string ParametersJson => """
        {
          "type": "object",
          "properties": {
            "query": { "type": "string", "description": "检索问题或关键字" }
          },
          "required": ["query"]
        }
        """;

    public override async Task<ToolExecutionResult> ExecuteAsync(string userId, JsonElement arguments, CancellationToken token)
    {
        var query = arguments.GetStringArg("query").Trim();
        if (query.Length == 0) return ToolExecutionResult.Deny("检索关键字不能为空。");
        float[] queryVector;
        try
        {
            queryVector = await embedding.EmbedAsync(query, token);
        }
        catch (InvalidOperationException ex)
        {
            return ToolExecutionResult.Deny(ex.Message);
        }

        // 命中条数与片段长度取自参数目录的 KB 域：它们是运维真想调的数字
        // （检索回来几条、每条能带多长），此前写死在工具里。
        var kb = runtime?.Current.Policy.Kb ?? new AssistantKbLimitsOptions();
        var consultant = (await permissions.GetAsync(userId, 2302, token)).CanSetup;
        var ops = (await permissions.GetAsync(userId, 2306, token)).CanSetup;
        var hits = await repository.SearchAsync(EmbeddingJson.ToJson(queryVector),
            embedding.Dimension, kb.SearchMaxHits, KbVisibility.AllowedFor(consultant, ops), token);
        if (hits.Count == 0) return ToolExecutionResult.Success("知识库中没有相关内容。");

        var sb = new StringBuilder();
        var kept = 0;
        foreach (var hit in hits)
        {
            // R2:含业务引用的片段须逐条复核，失败即丢弃该片段。
            if (!await ReferencesAllowedAsync(userId, hit.Content, token)) continue;
            var excerpt = hit.Content.Length <= kb.SearchMaxContentLength
                ? hit.Content
                : hit.Content[..kb.SearchMaxContentLength] + "…";
            sb.AppendLine().Append($"- [来源：{hit.Title}#{hit.SerialNo}] {excerpt}");
            sb.AppendLine().Append($"  source: kb://doc/{hit.DocId}#c{hit.SerialNo}");
            kept++;
        }

        if (kept == 0) return ToolExecutionResult.Success("知识库中没有相关内容。");
        return ToolExecutionResult.Success($"知识库命中（{kept}）：" + sb.ToString().TrimEnd());
    }

    private async Task<bool> ReferencesAllowedAsync(string userId, string content, CancellationToken token)
    {
        var references = KbIngestScanner.ExtractReferences(content);
        foreach (var reference in references)
        {
            var permission = await permissions.GetAsync(userId, reference.ModuleId, token);
            var denied = await KbReferenceVerifier.FirstDeniedReasonAsync(
                userId, reference, permission.Rights, gateway, token);
            if (denied is not null) return false;
        }

        return true;
    }
}
