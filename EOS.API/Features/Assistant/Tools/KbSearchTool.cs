using System.Text;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Features.Assistant.Kb;
using EOS.API.Features.Assistant.ModelAccess;
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
    IWorkbenchSearchGateway gateway) : AssistantToolBase
{
    public const string ToolName = "kb_search";
    private const int MaxHits = 5;
    private const int MaxContentLength = 300;

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

        var consultant = (await permissions.GetAsync(userId, 2302, token)).CanSetup;
        var ops = (await permissions.GetAsync(userId, 2306, token)).CanSetup;
        var hits = await repository.SearchAsync(EmbeddingJson.ToJson(queryVector),
            embedding.Dimension, MaxHits, KbVisibility.AllowedFor(consultant, ops), token);
        if (hits.Count == 0) return ToolExecutionResult.Success("知识库中没有相关内容。");

        var sb = new StringBuilder();
        var kept = 0;
        foreach (var hit in hits)
        {
            // R2:含业务引用的片段须逐条复核，失败即丢弃该片段。
            if (!await ReferencesAllowedAsync(userId, hit.Content, token)) continue;
            var excerpt = hit.Content.Length <= MaxContentLength
                ? hit.Content
                : hit.Content[..MaxContentLength] + "…";
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
            if (!permission.CanBrowse) return false;
            var scope = permission.Scope();
            var definition = await gateway.GetDefinitionAsync(reference.ModuleId, userId,
                scope.ExecTag, scope.CanViewCost, scope.CanViewSecrecy,
                scope.DeniedMaster, scope.DeniedDetail, token);
            if (definition is null || reference.Keys.Count != definition.MasterPkOrder.Count) return false;
            var rows = await gateway.GetExportRowsByKeysAsync(definition, [reference.Keys], token,
                dataFilter: permission.Rights.DataFilter);
            if (rows.Count == 0) return false;
        }

        return true;
    }
}
