using EOS.API.Data;
using EOS.API.Errors;
using EOS.API.Features.Assistant.Kb;
using EOS.API.Features.Assistant.ModelAccess;
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
    IEmbeddingModel embedding,
    ModuleRightsRepository rightsRepository,
    CurrentUserContext userContext,
    WorkbenchAuditWriter auditWriter) : ControllerBase
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
        var texts = KbChunker.Split(request.Content);
        if (texts.Count == 0)
            return BadRequest(ApiProblem.Create(StatusCodes.Status400BadRequest, "INVALID_ARGUMENT", "文档切块为空，无法入库。"));
        var chunks = new List<(string Content, float[] Vector)>();
        try
        {
            foreach (var text in texts)
            {
                chunks.Add((text, await embedding.EmbedAsync(text, token)));
            }
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, "KB_EMBEDDING_NOT_CONFIGURED", ex.Message));
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
        try
        {
            queryVector = await embedding.EmbedAsync(request.Query.Trim(), token);
        }
        catch (InvalidOperationException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, "KB_EMBEDDING_NOT_CONFIGURED", ex.Message));
        }

        var hits = await repository.SearchAsync(EmbeddingJson.ToJson(queryVector),
            embedding.Dimension, request.TopK, await AllowedVisibilitiesAsync(token), token);
        return Ok(new
        {
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

    private async Task<IReadOnlyList<string>> AllowedVisibilitiesAsync(CancellationToken token)
    {
        var consultant = (await rightsRepository.GetAsync(userContext.UserId, StewardModuleId, token)).CanSetup;
        var ops = (await rightsRepository.GetAsync(userContext.UserId, OpsModuleId, token)).CanSetup;
        return KbVisibility.AllowedFor(consultant, ops);
    }
}
