using System.Data;
using System.Text.Json;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EOS.API.Data;

/// <summary>
/// Workbench Definition 快照服务：
/// 状态（脏标记 + 当前快照）、dry-run 校验、发布（校验→版本递增→写快照→清脏）、
/// 已启用模块回填。元数据写路径只标脏（WorkbenchDirtyMarker），不逐次生成快照。
/// 运行时不可变快照加载与 definitionVersion 传播按 ADR 实施边界保持挂起。
/// </summary>
public sealed class WorkbenchDefinitionSnapshotService(
    DbConnectionFactory connections,
    WorkbenchDefinitionValidator validator,
    WorkbenchDefinitionProvider definitionProvider,
    WorkbenchAuditWriter auditWriter,
    IOptions<UnifiedFormEditorSettings> formSettings,
    ILogger<WorkbenchDefinitionSnapshotService> logger)
{
    public async Task<IReadOnlyList<WorkbenchModuleSnapshotStatus>> GetStatusAsync(CancellationToken token)
    {
        var whitelist = string.Join(",", formSettings.Value.EnabledModuleIds);
        const string sql = """
            SELECT m.M_IDX, LTRIM(RTRIM(ISNULL(m.M_DESC,''))), LTRIM(RTRIM(ISNULL(m.M_URL,''))),
                   ISNULL(d.DIRTY_TAG,0), d.LAST_MODIFIED_BY, d.LAST_MODIFIED_AT,
                   s.VERSION, s.PUBLISHED_AT, s.PUBLISHED_BY, s.VALIDATION_STATUS
            FROM dbo.MODULES m WITH (NOLOCK)
            LEFT JOIN dbo.WORKBENCH_MODULE_DIRTY d WITH (NOLOCK) ON d.MODULE_ID=m.M_IDX
            LEFT JOIN dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK) ON s.MODULE_ID=m.M_IDX AND s.IS_CURRENT=1
            WHERE LTRIM(RTRIM(ISNULL(m.M_URL,''))) LIKE '/workbench%'
               OR d.MODULE_ID IS NOT NULL
            ORDER BY m.M_IDX;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var enabled = formSettings.Value.EnabledModuleIds.ToHashSet();
        var result = new List<WorkbenchModuleSnapshotStatus>();
        while (await reader.ReadAsync(token))
        {
            var moduleId = reader.GetInt32(0);
            var version = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
            result.Add(new WorkbenchModuleSnapshotStatus(
                moduleId,
                reader.GetString(1),
                enabled.Contains(moduleId),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetDateTime(5),
                version,
                reader.IsDBNull(7) ? null : reader.GetDateTime(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                version is null ? null : $"module-{moduleId}-v{version}"));
        }
        return result;
    }

    public Task<WorkbenchDefinitionValidationReport> ValidateAsync(int moduleId, string userId, CancellationToken token)
        => validator.ValidateAsync(moduleId, userId, token);

    /// <summary>
    /// 「快照 vs 重建定义」的落后检测：逐模块把定义按**当前代码 + 当前元数据**重建，
    /// 与已发布快照的 <c>DEFINITION_JSON</c> 比较。判据与发布时的"内容未变即复用版本"
    /// 完全同源（同一个 <see cref="WorkbenchDefinitionContentComparer"/>），不另立一套标准——
    /// 否则会出现"发布说复用、检测说落后"的自相矛盾。**只读**：不写快照、不动脏标记。
    ///
    /// **重建必须用快照记录的发布者身份**（<c>PUBLISHED_BY</c>）：字段清单是**按用户**解析的
    /// （`WorkbenchDefinitionBuilder.ReadFields` 读 `SYSQL_FIELDS` 里该用户保存的选择列），
    /// 拿另一个身份去重建，字段集合与顺序必然不同 ⇒ 对每个有列偏好的用户都会误报"落后"。
    /// 用发布者身份重建，检测器的结论才与"该用户现在点一次发布会不会产生新版本"一致。
    ///
    /// 输出里同时带 <c>Dirty</c>：<c>Stale &amp;&amp; !Dirty</c> 才是静默落后（配置变了却没有任何
    /// 标记提示需要重发布，运行期仍按旧定义跑）。清一色的 <c>Stale &amp;&amp; Dirty</c> 属已知的
    /// 待发布积压，不算异常。
    /// </summary>
    public async Task<IReadOnlyList<WorkbenchSnapshotStaleness>> DetectStalenessAsync(CancellationToken token)
    {
        var snapshotSql = """
            SELECT s.MODULE_ID, LTRIM(RTRIM(ISNULL(m.M_DESC,''))), ISNULL(d.DIRTY_TAG,0), s.VERSION, s.DEFINITION_JSON,
                   LTRIM(RTRIM(ISNULL(s.PUBLISHED_BY,'')))
            FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT s WITH (NOLOCK)
            JOIN dbo.MODULES m WITH (NOLOCK) ON m.M_IDX = s.MODULE_ID
            LEFT JOIN dbo.WORKBENCH_MODULE_DIRTY d WITH (NOLOCK) ON d.MODULE_ID = s.MODULE_ID
            WHERE s.IS_CURRENT = 1
            ORDER BY s.MODULE_ID;
            """;
        var rows = new List<(int ModuleId, string Title, bool Dirty, int Version, string StoredJson, string PublishedBy)>();
        await using (var connection = connections.Create())
        {
            await connection.OpenAsync(token);
            await using var command = new SqlCommand(snapshotSql, connection);
            await using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3),
                    reader.GetString(4), reader.GetString(5)));
            }
        }

        var enabled = formSettings.Value.EnabledModuleIds.ToHashSet();
        var result = new List<WorkbenchSnapshotStaleness>(rows.Count);
        foreach (var row in rows)
        {
            string? rebuilt = null;
            string? reason = null;
            try
            {
                var report = await validator.ValidateAsync(row.ModuleId, row.PublishedBy, token);
                rebuilt = report.DefinitionJson;
                if (rebuilt is null)
                {
                    reason = "当前配置重建不出定义（未通过发布校验）";
                }
            }
            catch (Exception exception)
            {
                reason = $"重建定义失败：{exception.Message}";
            }

            var stale = reason is not null
                || !WorkbenchDefinitionContentComparer.AreEquivalent(row.StoredJson, rebuilt);
            result.Add(new WorkbenchSnapshotStaleness(
                row.ModuleId, row.Title, enabled.Contains(row.ModuleId), row.Version, row.Dirty, stale, reason,
                row.StoredJson.Length, rebuilt?.Length ?? 0,
                stale ? DescribeFirstDifference(row.StoredJson, rebuilt) : null));
        }

        logger.LogInformation("快照落后检测完成 modules={Checked} stale={Stale} silent={Silent}",
            result.Count, result.Count(item => item.Stale), result.Count(item => item.Stale && !item.Dirty));
        return result;
    }

    /// <summary>
    /// 首个差异位置的上下文片段（两侧各取 60 字符）。落后报告只给"是/否"没法定位，
    /// 给一段上下文就能一眼看出是字段元数据、业务动作还是别的东西变了。
    /// </summary>
    private static string? DescribeFirstDifference(string stored, string? rebuilt)
    {
        if (rebuilt is null)
        {
            return null;
        }
        var limit = Math.Min(stored.Length, rebuilt.Length);
        var index = 0;
        while (index < limit && stored[index] == rebuilt[index])
        {
            index++;
        }
        if (index == limit && stored.Length == rebuilt.Length)
        {
            return null;
        }
        const int padding = 60;
        var start = Math.Max(0, index - padding);
        var storedSnippet = stored.Substring(start, Math.Min(padding * 2, stored.Length - start));
        var rebuiltSnippet = rebuilt.Substring(start, Math.Min(padding * 2, rebuilt.Length - start));
        return $"位置 {index}：快照…{storedSnippet}… vs 重建…{rebuiltSnippet}…";
    }

    /// <summary>发布：逐模块校验，通过则写快照（版本递增、IS_CURRENT 切换）并清脏；失败不发布、脏标记保留。</summary>
    public async Task<IReadOnlyList<WorkbenchPublishResult>> PublishAsync(
        IReadOnlyList<int> moduleIds,
        string publishedBy,
        CancellationToken token)
    {
        var results = new List<WorkbenchPublishResult>();
        foreach (var moduleId in moduleIds.Distinct())
        {
            results.Add(await PublishOneAsync(moduleId, publishedBy, token));
        }
        return results;
    }

    /// <summary>回填：对统一表单白名单（已启用）模块全部发布（用于已启用模块完成快照化）。</summary>
    public async Task<IReadOnlyList<WorkbenchPublishResult>> BackfillAsync(string publishedBy, CancellationToken token)
    {
        var status = await GetStatusAsync(token);
        var ids = status.Where(item => item.Enabled).Select(item => item.ModuleId).Distinct().OrderBy(id => id).ToArray();
        logger.LogInformation("快照回填开始 modules={Count}", ids.Length);
        var results = await PublishAsync(ids, publishedBy, token);
        logger.LogInformation("快照回填完成 modules={Count} published={Published}",
            ids.Length, results.Count(result => result.Published));
        return results;
    }

    private async Task<WorkbenchPublishResult> PublishOneAsync(int moduleId, string publishedBy, CancellationToken token)
    {
        var report = await validator.ValidateAsync(moduleId, publishedBy, token);
        if (!report.Passed)
        {
            logger.LogWarning("快照发布被校验拦截 module={ModuleId} checks={FailedChecks}",
                moduleId, report.Checks.Count(check => !check.Passed));
            return new WorkbenchPublishResult(moduleId, report.Title, false, null,
                $"module-{moduleId}-draft", false, report.Checks);
        }

        var (masterTable, detailTable) = await ReadModuleTablesAsync(moduleId, token);
        var sourceVersion = await ReadSourceMetadataVersionAsync(moduleId, masterTable, detailTable, token);
        var reportJson = JsonSerializer.Serialize(report.Checks);

        var definitionJson = report.DefinitionJson ?? string.Empty;
        int version;
        var reused = false;
        await using (var connection = connections.Create())
        {
            await connection.OpenAsync(token);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
            try
            {
                // 当前快照内容（同一把锁下读，避免与并发发布交叉判断）
                const string currentSql = """
                    SELECT TOP 1 VERSION, DEFINITION_JSON FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WITH (UPDLOCK, HOLDLOCK)
                    WHERE MODULE_ID=@ModuleId AND IS_CURRENT=1 ORDER BY VERSION DESC;
                    """;
                int? currentVersion = null;
                string? currentJson = null;
                await using (var current = new SqlCommand(currentSql, connection, transaction))
                {
                    current.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                    await using var reader = await current.ExecuteReaderAsync(token);
                    if (await reader.ReadAsync(token))
                    {
                        currentVersion = reader.GetInt32(0);
                        currentJson = reader.GetString(1);
                    }
                }

                const string clearDirtySql = "DELETE FROM dbo.WORKBENCH_MODULE_DIRTY WHERE MODULE_ID=@ModuleId;";
                await using (var clearDirty = new SqlCommand(clearDirtySql, connection, transaction))
                {
                    clearDirty.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                    await clearDirty.ExecuteNonQueryAsync(token);
                }

                // 定义内容未变即复用当前版本：重发布不再制造新版本，否则每次"重发一遍"
                // 都会把既有验收证据判成过期快照（对拍报告按定义版本判定新鲜度）。
                // 比较按**语义等价**（忽略运行期看不见的差异），故整理配置——清掉占位公式行、
                // 把空串写成缺省——不会再顶掉版本、连带作废该模块的对拍证据。
                if (currentVersion is not null
                    && WorkbenchDefinitionContentComparer.AreEquivalent(currentJson, definitionJson))
                {
                    version = currentVersion.Value;
                    reused = true;
                }
                else
                {
                    const string nextVersionSql = """
                        SELECT ISNULL(MAX(VERSION),0)+1 FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WITH (UPDLOCK, HOLDLOCK)
                        WHERE MODULE_ID=@ModuleId;
                        """;
                    await using var versionCommand = new SqlCommand(nextVersionSql, connection, transaction);
                    versionCommand.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                    version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(token));

                    const string retireSql = "UPDATE dbo.WORKBENCH_DEFINITION_SNAPSHOT SET IS_CURRENT=0 WHERE MODULE_ID=@ModuleId AND IS_CURRENT=1;";
                    await using (var retire = new SqlCommand(retireSql, connection, transaction))
                    {
                        retire.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                        await retire.ExecuteNonQueryAsync(token);
                    }

                    const string insertSql = """
                        INSERT INTO dbo.WORKBENCH_DEFINITION_SNAPSHOT
                            (MODULE_ID, VERSION, DEFINITION_JSON, SOURCE_METADATA_VERSION, VALIDATION_STATUS,
                             VALIDATION_REPORT_JSON, PUBLISHED_BY, PUBLISHED_AT, IS_CURRENT)
                        VALUES (@ModuleId, @Version, @DefinitionJson, @SourceVersion, N'PASS', @ReportJson, @PublishedBy, SYSDATETIME(), 1);
                        """;
                    await using var insert = new SqlCommand(insertSql, connection, transaction);
                    insert.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
                    insert.Parameters.Add("@Version", SqlDbType.Int).Value = version;
                    insert.Parameters.Add("@DefinitionJson", SqlDbType.NVarChar, -1).Value = definitionJson;
                    insert.Parameters.Add("@SourceVersion", SqlDbType.NVarChar, 100).Value = (object?)sourceVersion ?? DBNull.Value;
                    insert.Parameters.Add("@ReportJson", SqlDbType.NVarChar, -1).Value = reportJson;
                    insert.Parameters.Add("@PublishedBy", SqlDbType.NVarChar, 100).Value = publishedBy;
                    await insert.ExecuteNonQueryAsync(token);
                }

                await transaction.CommitAsync(token);
            }
            catch
            {
                await transaction.RollbackAsync(token);
                throw;
            }
        }

        // 提交后的非事务工作：刷新该模块缓存（无当前快照时移除基线）、审计、日志。
        // 放在事务 try/catch 之外，避免缓存刷新异常被误当作回滚失败。
        await definitionProvider.RefreshModuleAsync(moduleId, token);
        var summary = reused
            ? $"复用模块定义快照 module-{moduleId}-v{version}（规范化后内容未变，不递增版本）"
            : $"发布模块定义快照 module-{moduleId}-v{version}";
        await auditWriter.WriteBestEffortAsync(
            moduleId, "WORKBENCH_DEFINITION_SNAPSHOT", "PUBLISH",
            summary, publishedBy, "MENU",
            result: 1, fieldChanges: null, token);
        if (reused)
        {
            logger.LogInformation("快照规范化后内容未变，复用 module={ModuleId} version={Version} by={PublishedBy}",
                moduleId, version, publishedBy);
        }
        else
        {
            logger.LogInformation("快照发布 module={ModuleId} version={Version} by={PublishedBy}",
                moduleId, version, publishedBy);
        }
        return new WorkbenchPublishResult(moduleId, report.Title, !reused, version,
            $"module-{moduleId}-v{version}", true, report.Checks,
            Error: null);
    }

    private async Task<(string Master, string? Detail)> ReadModuleTablesAsync(int moduleId, CancellationToken token)
    {
        const string sql = """
            SELECT LTRIM(RTRIM(ISNULL(MASTER_TABLE,''))),LTRIM(RTRIM(ISNULL(DETAIL_TABLE,'')))
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            return (string.Empty, null);
        }
        var master = reader.GetString(0);
        var detail = string.IsNullOrWhiteSpace(reader.GetString(1)) ? null : reader.GetString(1);
        return (master, detail);
    }

    /// <summary>来源元数据版本：模块行与主/子表字段 LAST_UPDATE_DATE 的最大时间戳（ISO-8601）。</summary>
    private async Task<string?> ReadSourceMetadataVersionAsync(
        int moduleId, string masterTable, string? detailTable, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(masterTable))
        {
            return null;
        }
        const string sql = """
            SELECT CONVERT(nvarchar(30), MAX(v), 126) FROM (
                SELECT ISNULL(LAST_UPDATE_DATE, GETDATE()) AS v FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId
                UNION ALL
                SELECT ISNULL(LAST_UPDATE_DATE, GETDATE()) FROM dbo.FIELDS WITH (NOLOCK) WHERE T_ID=@Master
                UNION ALL
                SELECT ISNULL(LAST_UPDATE_DATE, GETDATE()) FROM dbo.FIELD_DATASOURCE WITH (NOLOCK) WHERE T_ID=@Master
            ) t;
            """;
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        command.Parameters.Add("@Master", SqlDbType.NVarChar, 100).Value = masterTable;
        if (!string.IsNullOrWhiteSpace(detailTable))
        {
            command.CommandText = command.CommandText.Replace(
                "WHERE T_ID=@Master", "WHERE T_ID=@Master OR T_ID=@Detail", StringComparison.Ordinal);
            command.Parameters.Add("@Detail", SqlDbType.NVarChar, 100).Value = detailTable;
        }
        return await command.ExecuteScalarAsync(token) as string;
    }
}
