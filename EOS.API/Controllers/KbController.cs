using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Features.Assistant.Kb;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EOS.API.Controllers;

/// <summary>
/// RAG 知识库：入库是运维域动作（2302 CanSetup），检索对登录用户开放、
/// 可见性三档（ALL/实施顾问/运维）在 API 内过滤。向量是派生索引，不是事实源。
/// </summary>
[ApiController, Authorize, Route("api/v1/assistant/kb")]
public sealed class KbController(
    IKnowledgeRepository repository,
    IAssistantEmbeddingResolver embeddingResolver,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext,
    WorkbenchAuditWriter auditWriter,
    IWorkbenchSearchGateway gateway,
    // 入库切块的块长与重叠来自参数目录的 KB 域（ADR-030 §5.3.9）：块长直接决定
    // "检索回来的片段够不够回答一个问题"，它是运维会想调的，不是实现细节。
    IAssistantRuntimeConfig runtime) : ControllerBase
{
    private const int StewardModuleId = 2302;
    private const int OpsModuleId = 2306;

    public sealed record EnsureCollectionRequest(
        string CollectionId, string Title, string EmbeddingModel, int Dimension, string DefaultVisibility);

    public sealed record IngestDocumentRequest(
        string CollectionId, string Title, string? SourceUri, string Content, string? Visibility);

    public sealed record SearchRequest(string Query, int TopK = 5);

    [HttpPost("collections/ensure")]
    public async Task<IActionResult> EnsureCollection([FromBody] EnsureCollectionRequest request, CancellationToken token)
    {
        if (!await CanStewardAsync(token)) return Forbid();
        try
        {
            await repository.EnsureCollectionAsync(request.CollectionId, request.Title,
                request.EmbeddingModel, request.Dimension, request.DefaultVisibility, token);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message));
        }
    }

    [HttpPost("documents")]
    public async Task<IActionResult> IngestDocument([FromBody] IngestDocumentRequest request, CancellationToken token)
    {
        if (!await CanStewardAsync(token)) return Forbid();
        if (request is null || string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Content))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "文档标题与内容不能为空。"));
        // R1:入库前扫描——敏感信息直接拒入；业务引用须逐条通过权限复核，否则整篇不入可检索集合。
        var scan = KbIngestScanner.Scan(request.Content);
        if (scan.SensitiveKinds.Count > 0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "KB_SENSITIVE_BLOCKED",
                $"文档含敏感信息（{string.Join("、", scan.SensitiveKinds)}），请删除后重试。"));
        var refError = await VerifyReferencesAsync(userContext.UserId, scan.References, token);
        if (refError is not null)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "KB_REF_DENIED",
                $"文档内业务引用复核未通过（{refError}），请删除该引用后重试。"));
        var texts = KbChunker.Split(
            request.Content, runtime.Current.Policy.Kb);
        if (texts.Count == 0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "文档切块为空，无法入库。"));
        var chunks = new List<(string Content, float[] Vector)>();
        try
        {
            var embedding = await embeddingResolver.ResolveAsync(token);
            // 一次批量：厂商对单请求条数有上限，分批在客户端做（百炼 10 条、智谱 ≤64 条）。
            // 逐块一次请求的写法在一篇百余块的文档上就是一百次出网，第一次真入库就会撞限流
            var vectors = await embedding.EmbedManyAsync(texts, token);
            for (var i = 0; i < texts.Count; i++)
            {
                chunks.Add((texts[i], vectors[i]));
            }
        }
        catch (EmbeddingNotConfiguredException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, "KB_EMBEDDING_NOT_CONFIGURED", ex.Message));
        }
        catch (AssistantModelException ex)
        {
            // 厂商侧的失败要带**它自己的原因码**：把"密钥被拒"混进"未配置"里，
            // 管理员会去 3102 反复确认模型配好了没有，而问题在密钥
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, ex.Code, ex.UserMessage));
        }

        try
        {
            var collection = await repository.GetCollectionAsync(request.CollectionId, token);
            var visibility = KbVisibility.Normalize(request.Visibility, collection?.DefaultVisibility ?? KbVisibility.All);
            var (docId, reused) = await repository.IngestDocumentAsync(request.CollectionId,
                request.Title, request.SourceUri, request.Content, visibility, chunks,
                userContext.UserId, token);
            await auditWriter.WriteBestEffortAsync(null, $"kb-doc:{docId}",
                reused ? "KB_INGEST_REUSED" : "KB_INGEST",
                $"知识库文档入库（{request.Title}，{chunks.Count} 块）", userContext.UserId,
                "KNOWLEDGE_BASE", result: 1, null, token);
            return Ok(new { docId = docId.ToString(), reused, chunks = chunks.Count });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", ex.Message));
        }
    }

    [HttpDelete("documents/{docId:long}")]
    public async Task<IActionResult> DeleteDocument(long docId, CancellationToken token)
    {
        if (!await CanStewardAsync(token)) return Forbid();
        var deleted = await repository.DeleteDocumentAsync(docId, token);
        if (deleted)
        {
            await auditWriter.WriteBestEffortAsync(null, $"kb-doc:{docId}", "KB_DELETE",
                "知识库文档删除（向量已同步移除）", userContext.UserId,
                "KNOWLEDGE_BASE", result: 1, null, token);
            return NoContent();
        }

        return NotFound(ApiProblem.Create(StatusCodes.Status404NotFound, "NOT_FOUND", "文档不存在。"));
    }

    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] SearchRequest request, CancellationToken token)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Query))
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "检索关键字不能为空。"));
        float[] queryVector;
        int dimension;
        try
        {
            var embedding = await embeddingResolver.ResolveAsync(token);
            dimension = embedding.Dimension;
            queryVector = await embedding.EmbedAsync(request.Query.Trim(), token);
        }
        catch (EmbeddingNotConfiguredException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, "KB_EMBEDDING_NOT_CONFIGURED", ex.Message));
        }
        catch (AssistantModelException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, ex.Code, ex.UserMessage));
        }

        // TopK 由调用方给，但**必须有上界**：不然一句请求就能让检索返回任意条数（内存与出网检索成本）。
        // 上界取自参数目录的 KB 域，与助手工具的 KB_SEARCH_MAX_HITS **分开**：
        // 那个管"进模型上下文几条"，这个管"界面能翻出几条"——把两者合成一个，
        // 管理员为省 token 调小工具上限时会把检索页一起缩水，而他从界面上看不出这层关联。
        var topK = Math.Clamp(request.TopK, 1, runtime.Current.Policy.Kb.EndpointMaxHits);
        var hits = await repository.SearchAsync(EmbeddingJson.ToJson(queryVector),
            dimension, topK, await AllowedVisibilitiesAsync(token), token);
        // R2:与 kb_search 工具同口径——命中片段含业务引用时逐条复核，失败片段不返回。
        hits = await KbReferenceVerifier.FilterHitsAsync(
            userContext.UserId, hits, VerifyReferencesAsync, token);
        return Ok(new
        {
            // 回显实际生效的条数：被夹住时调用方看得见，而不是"我要 100 条、拿到 20 条"却不明白为什么
            topK,
            hits = hits.Select(hit => new
            {
                docId = hit.DocId.ToString(),
                title = hit.Title,
                sourceUri = hit.SourceUri,
                serialNo = hit.SerialNo,
                content = hit.Content,
            }),
        });
    }

    /// <summary>来源原文查看（前端引用链接落点；不可见文档统一 404）。</summary>
    [HttpGet("documents/{docId:long}")]
    public async Task<IActionResult> GetDocument(long docId, CancellationToken token)
    {
        var (document, chunks) = await repository.GetDocumentAsync(docId, token);
        if (document is null || document.Status != "active") return NotFound();
        var allowed = await AllowedVisibilitiesAsync(token);
        if (!allowed.Contains(document.Visibility, StringComparer.OrdinalIgnoreCase)) return NotFound();
        return Ok(new
        {
            docId = document.DocId.ToString(),
            title = document.Title,
            sourceUri = document.SourceUri,
            chunks = chunks.Select(chunk => new { serialNo = chunk.SerialNo, content = chunk.Content }),
        });
    }

    private async Task<bool> CanStewardAsync(CancellationToken token) =>
        (await rightsRepository.GetAsync(userContext.UserId, StewardModuleId, token)).CanSetup;

    /// <summary>
    /// R1:业务引用二次复核——逐条按既有权限门（CanBrowse + 数据范围）验证，
    /// 任一条失败即整篇拒入。返回失败原因，null 表示全部通过。
    /// 规则实现见 KbReferenceVerifier，入库/检索工具/HTTP 检索共用。
    /// </summary>
    private async Task<string?> VerifyReferencesAsync(
        string userId, IReadOnlyList<BusinessReference> references, CancellationToken token)
    {
        foreach (var reference in references)
        {
            var rights = await rightsRepository.GetAsync(userId, reference.ModuleId, token);
            var denied = await KbReferenceVerifier.FirstDeniedReasonAsync(
                userId, reference, rights, gateway, token);
            if (denied is not null) return $"模块 #{reference.ModuleId} {denied}";
        }

        return null;
    }

    private async Task<IReadOnlyList<string>> AllowedVisibilitiesAsync(CancellationToken token)
    {
        var consultant = (await rightsRepository.GetAsync(userContext.UserId, StewardModuleId, token)).CanSetup;
        var ops = (await rightsRepository.GetAsync(userContext.UserId, OpsModuleId, token)).CanSetup;
        return KbVisibility.AllowedFor(consultant, ops);
    }
}
