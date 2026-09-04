using System.Data;
using System.Security.Cryptography;
using System.Text;
using EOS.API.Features.Assistant.ModelAccess;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

public sealed record KbCollectionInfo(
    string CollectionId, string Title, string EmbeddingModel, int Dimension, string DefaultVisibility);

public sealed record KbDocumentInfo(
    long DocId, string CollectionId, string Title, string? SourceUri,
    string Visibility, string Status, int Version);

public sealed record KbHit(
    long DocId, string Title, string? SourceUri, int SerialNo, string Content, double Distance);

public static class KbVisibility
{
    public const string All = "ALL";
    public const string Consultant = "CONSULTANT";
    public const string Ops = "OPS";

    private static readonly HashSet<string> Allowed =
        new(StringComparer.OrdinalIgnoreCase) { All, Consultant, Ops };

    public static string Normalize(string? visibility, string fallback)
    {
        var normalized = (visibility ?? fallback ?? All).Trim().ToUpperInvariant();
        if (!Allowed.Contains(normalized)) throw new ArgumentException("可见性仅支持 ALL/CONSULTANT/OPS。");
        return normalized;
    }

    /// <summary>Caller resolves tiers from 2302/2306 CanSetup; the store only sees the allow-list.</summary>
    public static IReadOnlyList<string> AllowedFor(bool consultantSetup, bool opsSetup)
    {
        var result = new List<string> { All };
        if (consultantSetup) result.Add(Consultant);
        if (opsSetup) result.Add(Ops);
        return result;
    }
}

public interface IKnowledgeRepository
{
    Task EnsureCollectionAsync(
        string collectionId, string title, string embeddingModel, int dimension,
        string defaultVisibility, CancellationToken token);

    Task<KbCollectionInfo?> GetCollectionAsync(string collectionId, CancellationToken token);

    /// <summary>
    /// Content-hash idempotent ingest: same collection + hash returns the existing
    /// active doc; changed content retires the same-sourceUri version first.
    /// </summary>
    Task<(long DocId, bool Reused)> IngestDocumentAsync(
        string collectionId, string title, string? sourceUri, string content,
        string visibility, IReadOnlyList<(string Content, float[] Vector)> chunks,
        string updatedBy, CancellationToken token);

    /// <summary>Soft-delete the doc tombstone + physically remove its vectors.</summary>
    Task<bool> DeleteDocumentAsync(long docId, CancellationToken token);

    Task<(KbDocumentInfo? Document, IReadOnlyList<(int SerialNo, string Content)> Chunks)> GetDocumentAsync(
        long docId, CancellationToken token);

    Task<IReadOnlyList<KbHit>> SearchAsync(
        string queryVectorJson, int dimension, int topK, IReadOnlyList<string> visibilities, CancellationToken token);
}

public sealed class KnowledgeRepository(DbConnectionFactory connections) : IKnowledgeRepository
{
    public async Task EnsureCollectionAsync(
        string collectionId, string title, string embeddingModel, int dimension,
        string defaultVisibility, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(collectionId)) throw new ArgumentException("集合标识不能为空。");
        if (dimension is < 8 or > 4096) throw new ArgumentException("向量维度必须在 8-4096 之间。");
        var visibility = KbVisibility.Normalize(defaultVisibility, KbVisibility.All);
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        const string sql = """
            IF NOT EXISTS (SELECT 1 FROM dbo.KB_COLLECTION WHERE COLLECTION_ID=@Id)
                INSERT INTO dbo.KB_COLLECTION (COLLECTION_ID, TITLE, EMBEDDING_MODEL, DIMENSION, DEFAULT_VISIBILITY)
                VALUES (@Id, @Title, @Model, @Dim, @Vis);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.NVarChar, 50).Value = collectionId.Trim();
        command.Parameters.Add("@Title", SqlDbType.NVarChar, 200).Value = title.Trim();
        command.Parameters.Add("@Model", SqlDbType.NVarChar, 100).Value = embeddingModel.Trim();
        command.Parameters.Add("@Dim", SqlDbType.Int).Value = dimension;
        command.Parameters.Add("@Vis", SqlDbType.NVarChar, 20).Value = visibility;
        await command.ExecuteNonQueryAsync(token);
    }

    public async Task<KbCollectionInfo?> GetCollectionAsync(string collectionId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT COLLECTION_ID, TITLE, EMBEDDING_MODEL, DIMENSION, DEFAULT_VISIBILITY FROM dbo.KB_COLLECTION WITH (NOLOCK) WHERE COLLECTION_ID=@Id;",
            connection);
        command.Parameters.Add("@Id", SqlDbType.NVarChar, 50).Value = collectionId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null;
        return new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetString(4));
    }

    public async Task<(long DocId, bool Reused)> IngestDocumentAsync(
        string collectionId, string title, string? sourceUri, string content,
        string visibility, IReadOnlyList<(string Content, float[] Vector)> chunks,
        string updatedBy, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("文档标题不能为空。");
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("文档内容不能为空。");
        if (chunks.Count == 0) throw new ArgumentException("切块为空，无法入库。");
        var normalizedVisibility = KbVisibility.Normalize(visibility, KbVisibility.All);

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var collection = await GetCollectionAsync(collectionId, token)
            ?? throw new ArgumentException("知识集合不存在。");
        if (collection.Dimension != 1024)
            throw new ArgumentException("当前向量列仅支持 1024 维（BGE-M3）；换模型请建新集合并扩展列宽。");
        foreach (var chunk in chunks)
        {
            if (chunk.Vector.Length != collection.Dimension)
                throw new ArgumentException($"向量维度与集合不一致（集合 {collection.Dimension}）。");
        }

        var hash = ContentHash(content);
        var normalizedSource = string.IsNullOrWhiteSpace(sourceUri) ? null : sourceUri.Trim();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        await using (var existing = new SqlCommand(
            "SELECT DOC_ID FROM dbo.KB_DOCUMENT WITH (NOLOCK) WHERE COLLECTION_ID=@Collection AND CONTENT_HASH=@Hash AND STATUS=N'active';",
            connection, transaction))
        {
            existing.Parameters.Add("@Collection", SqlDbType.NVarChar, 50).Value = collectionId;
            existing.Parameters.Add("@Hash", SqlDbType.Char, 64).Value = hash;
            var reused = await existing.ExecuteScalarAsync(token);
            if (reused is not null) return (Convert.ToInt64(reused), true);
        }

        if (normalizedSource is not null)
        {
            await using var retire = new SqlCommand(
                "UPDATE dbo.KB_DOCUMENT SET STATUS=N'deleted' WHERE COLLECTION_ID=@Collection AND SOURCE_URI=@Source AND STATUS=N'active';",
                connection, transaction);
            retire.Parameters.Add("@Collection", SqlDbType.NVarChar, 50).Value = collectionId;
            retire.Parameters.Add("@Source", SqlDbType.NVarChar, 500).Value = normalizedSource;
            await retire.ExecuteNonQueryAsync(token);
            await using var purge = new SqlCommand(
                "DELETE c FROM dbo.KB_CHUNK c INNER JOIN dbo.KB_DOCUMENT d ON d.DOC_ID=c.DOC_ID WHERE d.COLLECTION_ID=@Collection AND d.SOURCE_URI=@Source AND d.STATUS=N'deleted';",
                connection, transaction);
            purge.Parameters.Add("@Collection", SqlDbType.NVarChar, 50).Value = collectionId;
            purge.Parameters.Add("@Source", SqlDbType.NVarChar, 500).Value = normalizedSource;
            await purge.ExecuteNonQueryAsync(token);
        }

        long docId;
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.KB_DOCUMENT (COLLECTION_ID, TITLE, SOURCE_URI, CONTENT_HASH, VISIBILITY, CREATE_BY)
            OUTPUT INSERTED.DOC_ID
            VALUES (@Collection, @Title, @Source, @Hash, @Vis, @By);
            """, connection, transaction))
        {
            insert.Parameters.Add("@Collection", SqlDbType.NVarChar, 50).Value = collectionId;
            insert.Parameters.Add("@Title", SqlDbType.NVarChar, 200).Value = title.Trim();
            insert.Parameters.Add("@Source", SqlDbType.NVarChar, 500).Value = (object?)normalizedSource ?? DBNull.Value;
            insert.Parameters.Add("@Hash", SqlDbType.Char, 64).Value = hash;
            insert.Parameters.Add("@Vis", SqlDbType.NVarChar, 20).Value = normalizedVisibility;
            insert.Parameters.Add("@By", SqlDbType.NVarChar, 50).Value = updatedBy;
            docId = Convert.ToInt64(await insert.ExecuteScalarAsync(token));
        }

        var serial = 0;
        var vectorType = $"VECTOR({collection.Dimension})";
        foreach (var chunk in chunks)
        {
            serial++;
            await using var insertChunk = new SqlCommand(
                $"INSERT INTO dbo.KB_CHUNK (DOC_ID, SERIAL_NO, CONTENT, EMBEDDING) VALUES (@Doc, @Serial, @Content, CAST(@Vector AS {vectorType}));",
                connection, transaction);
            insertChunk.Parameters.Add("@Doc", SqlDbType.BigInt).Value = docId;
            insertChunk.Parameters.Add("@Serial", SqlDbType.Int).Value = serial;
            insertChunk.Parameters.Add("@Content", SqlDbType.NVarChar, -1).Value = chunk.Content;
            insertChunk.Parameters.Add("@Vector", SqlDbType.NVarChar, -1).Value = EmbeddingJson.ToJson(chunk.Vector);
            await insertChunk.ExecuteNonQueryAsync(token);
        }

        await transaction.CommitAsync(token);
        return (docId, false);
    }

    public async Task<bool> DeleteDocumentAsync(long docId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        await using (var purge = new SqlCommand("DELETE FROM dbo.KB_CHUNK WHERE DOC_ID=@Doc;", connection, transaction))
        {
            purge.Parameters.Add("@Doc", SqlDbType.BigInt).Value = docId;
            await purge.ExecuteNonQueryAsync(token);
        }

        int affected;
        await using (var tombstone = new SqlCommand(
            "UPDATE dbo.KB_DOCUMENT SET STATUS=N'deleted' WHERE DOC_ID=@Doc AND STATUS=N'active'; SELECT @@ROWCOUNT;",
            connection, transaction))
        {
            tombstone.Parameters.Add("@Doc", SqlDbType.BigInt).Value = docId;
            affected = Convert.ToInt32(await tombstone.ExecuteScalarAsync(token));
        }

        await transaction.CommitAsync(token);
        return affected > 0;
    }

    public async Task<(KbDocumentInfo? Document, IReadOnlyList<(int SerialNo, string Content)> Chunks)> GetDocumentAsync(
        long docId, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        KbDocumentInfo? document = null;
        await using (var command = new SqlCommand(
            "SELECT DOC_ID, COLLECTION_ID, TITLE, SOURCE_URI, VISIBILITY, STATUS, VERSION FROM dbo.KB_DOCUMENT WITH (NOLOCK) WHERE DOC_ID=@Doc;",
            connection))
        {
            command.Parameters.Add("@Doc", SqlDbType.BigInt).Value = docId;
            await using var reader = await command.ExecuteReaderAsync(token);
            if (await reader.ReadAsync(token))
            {
                document = new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), reader.GetInt32(6));
            }
        }

        if (document is null) return (null, []);
        var chunks = new List<(int SerialNo, string Content)>();
        await using (var command = new SqlCommand(
            "SELECT SERIAL_NO, CONTENT FROM dbo.KB_CHUNK WITH (NOLOCK) WHERE DOC_ID=@Doc ORDER BY SERIAL_NO;",
            connection))
        {
            command.Parameters.Add("@Doc", SqlDbType.BigInt).Value = docId;
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                chunks.Add((reader.GetInt32(0), reader.GetString(1)));
            }
        }

        return (document, chunks);
    }

    public async Task<IReadOnlyList<KbHit>> SearchAsync(
        string queryVectorJson, int dimension, int topK, IReadOnlyList<string> visibilities, CancellationToken token)
    {
        topK = Math.Clamp(topK, 1, 20);
        if (dimension is < 8 or > 4096) throw new ArgumentException("向量维度必须在 8-4096 之间。");
        var allowed = visibilities.Count == 0
            ? new[] { KbVisibility.All }
            : visibilities.Select(v => KbVisibility.Normalize(v, KbVisibility.All)).Distinct().ToArray();
        var names = string.Join(",", allowed.Select((_, index) => $"@V{index}"));
        var vectorType = $"VECTOR({dimension})";
        var sql = $"""
            SELECT TOP (@Top) d.DOC_ID, d.TITLE, d.SOURCE_URI, c.SERIAL_NO, c.CONTENT,
                   VECTOR_DISTANCE('cosine', c.EMBEDDING, CAST(@Query AS {vectorType})) AS DISTANCE
            FROM dbo.KB_CHUNK c WITH (NOLOCK)
            INNER JOIN dbo.KB_DOCUMENT d WITH (NOLOCK) ON d.DOC_ID = c.DOC_ID
            WHERE d.STATUS = N'active' AND d.VISIBILITY IN ({names}) AND c.EMBEDDING IS NOT NULL
            ORDER BY VECTOR_DISTANCE('cosine', c.EMBEDDING, CAST(@Query AS {vectorType}));
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Top", SqlDbType.Int).Value = topK;
        command.Parameters.Add("@Query", SqlDbType.NVarChar, -1).Value = queryVectorJson;
        for (var index = 0; index < allowed.Length; index++)
        {
            command.Parameters.Add($"@V{index}", SqlDbType.NVarChar, 20).Value = allowed[index];
        }

        await using var reader = await command.ExecuteReaderAsync(token);
        var hits = new List<KbHit>();
        while (await reader.ReadAsync(token))
        {
            hits.Add(new(reader.GetInt64(0), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetDouble(5)));
        }

        return hits;
    }

    internal static string ContentHash(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}
