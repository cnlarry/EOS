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
    IAssistantEmbeddingResolver embeddingResolver,
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
        int dimension;
        try
        {
            var embedding = await embeddingResolver.ResolveAsync(token);
            dimension = embedding.Dimension;
            queryVector = await embedding.EmbedAsync(query, token);
        }
        catch (EmbeddingNotConfiguredException ex)
        {
            // 「没配」与「服务坏了」分开说：前者管理员一改就好，后者只能等。
            // 两句都说成"知识库不可用"时，用户唯一能做的就是来找我们。
            // 措辞是给**模型**的指令（Deny 的正文会回喂给它）：如实告知 + 照常回答其余问题 +
            // 别让用户反复重试一个重试不好的动作。管理员那半截附在后面，供用户转述给运维
            return ToolExecutionResult.Deny(
                "知识库当前不可用（未配置嵌入模型）——本次回答没有用到知识库内容。"
                + "请如实告诉用户这一点，并照常回答其余问题，不要让用户反复重试。"
                + $"（给管理员的原因：{ex.Message}）");
        }
        catch (AssistantModelException ex)
        {
            // 厂商侧失败：带它自己的原因码。检索工具不因为一次取不到就中断整轮回答——
            // 用户要知道的是"这次没查到知识库"，而不是"系统内部错误，请换个问法"
            return ToolExecutionResult.Deny(
                $"知识库检索暂不可用（{ex.Code}）——这次没有取到知识库内容。"
                + "请如实告诉用户这一点，并照常回答其余问题，不要让用户反复重试。"
                + $"（原因：{ex.UserMessage}）");
        }

        // 命中条数与片段长度取自参数目录的 KB 域：它们是运维真想调的数字
        // （检索回来几条、每条能带多长），此前写死在工具里。
        var kb = runtime?.Current.Policy.Kb ?? new AssistantKbLimitsOptions();
        var consultant = (await permissions.GetAsync(userId, 2302, token)).CanSetup;
        var ops = (await permissions.GetAsync(userId, 2306, token)).CanSetup;
        var hits = await repository.SearchAsync(EmbeddingJson.ToJson(queryVector),
            dimension, kb.SearchMaxHits, KbVisibility.AllowedFor(consultant, ops), token);
        if (hits.Count == 0) return ToolExecutionResult.Success("知识库中没有相关内容。");

        // 相关性截断（ADR-031 §4.1）：只丢"明显更差"的尾巴，且永远保留最佳命中。
        // 放在逐条权限复核**之前**：先按相关性丢掉尾巴，再为留下的每一条付复核成本
        var relevant = KbRelevance.Cut(hits, kb.RelevanceMarginPct);
        var dropped = hits.Count - relevant.Count;

        var sb = new StringBuilder();
        var kept = 0;
        foreach (var hit in relevant)
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
        // 开头那句"未必都相关"是给模型的判断依据：相对截断永远保留最佳命中，所以"整批都不相关"时
        // 仍会带一条回来——不能让模型默认"检索到了就等于答案在这里"
        var cut = dropped > 0 ? $"，另有 {dropped} 条相关性明显更低未列出" : string.Empty;
        return ToolExecutionResult.Success(
            $"以下是知识库检索结果，未必都与问题相关：命中 {kept} 条{cut}" + sb.ToString().TrimEnd());
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
