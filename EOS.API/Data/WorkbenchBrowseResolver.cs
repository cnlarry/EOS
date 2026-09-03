using System.Collections.Concurrent;
using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

using EOS.API.Models;

namespace EOS.API.Data;

/// <summary>
/// Resolves cross-module field browse links (FIELDS.BROWSE_M_IDX + BROWSE_URL) into modern
/// record navigation links. The BROWSE_URL template (e.g. ~?IDX=CLIENT_ID={CLIENT_ID})
/// is mapped to the modern workbench:
/// - Target is workbench-hosted (M_URL=/workbench) and keys resolve fully → record browse
/// `/workbench/{m}/view/{key-values}`, key source columns mapped per target primary key order;
/// - Target is workbench-hosted but keys cannot resolve → degraded to target module list link;
/// - Target is a special page (M_URL != /workbench) → no link (plain text).
/// This resolver only produces link descriptors (static metadata, snapshotable); target browse
/// permissions are enforced by the frontend bootstrap (UX gate) and the target /view, /record
/// endpoints (CanBrowse + data scope, final authorization).
/// 双重把关。动态标识符一律来自 FIELDS 元数据并经 sys.* 校验，不信任前端输入。
/// </summary>
public static class WorkbenchBrowseResolver
{
    private static readonly Regex Placeholder = new(@"\{([^{}]*)\}", RegexOptions.Compiled);

    private static readonly ConcurrentDictionary<int, (bool IsWorkbench, string? MasterTable)> ModuleCache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<string>> PrimaryKeyCache = new();
    private static readonly ConcurrentDictionary<string, bool> ColumnCache = new();

    /// <summary>对一列工作台字段解析浏览链接描述符（返回新列表，入参不修改）。</summary>
    public static async Task<IReadOnlyList<WorkbenchField>> ResolveAsync(
        SqlConnection connection,
        IReadOnlyList<WorkbenchField> fields,
        string sourceTable,
        IReadOnlySet<int> unifiedFormWhitelist,
        CancellationToken token)
    {
        if (fields.Count == 0)
        {
            return fields;
        }
        var result = new List<WorkbenchField>(fields.Count);
        foreach (var field in fields)
        {
            if (field.BrowseModuleId is not int moduleId || moduleId <= 0)
            {
                result.Add(field);
                continue;
            }
            result.Add(await ResolveFieldAsync(connection, field, moduleId, sourceTable, unifiedFormWhitelist, token));
        }
        return result;
    }

    private static async Task<WorkbenchField> ResolveFieldAsync(
        SqlConnection connection,
        WorkbenchField field,
        int moduleId,
        string sourceTable,
        IReadOnlySet<int> unifiedFormWhitelist,
        CancellationToken token)
    {
        var (isWorkbench, masterTable) = await GetModuleAsync(connection, moduleId, token);
        if (!isWorkbench || string.IsNullOrWhiteSpace(masterTable))
        {
            return field with { BrowseModuleId = null, BrowseUrl = null };
        }

        // BROWSE_URL 为空（含被 SanitizeBrowseUrl 按白名单剔除的非法模板）时：
        // 目标是工作台模块 → 降级为目标模块列表链接；不做记录浏览。
        if (string.IsNullOrWhiteSpace(field.BrowseUrl))
        {
            return field with { BrowseUrl = null, BrowseKeyFields = null };
        }

        var mapping = ParseBrowseUrl(field.BrowseUrl!);
        var targetPk = await GetPrimaryKeyAsync(connection, masterTable, token);
        if (targetPk.Count == 0)
        {
            return field with { BrowseUrl = null, BrowseKeyFields = null };
        }

        var sourceColumns = new List<string>(targetPk.Count);
        var resolvable = true;
        foreach (var pk in targetPk)
        {
            if (!mapping.TryGetValue(pk, out var source))
            {
                resolvable = false;
                break;
            }
            source = source.Trim('[', ']');
            if (!WorkbenchSql.Identifier.IsMatch(source) || !await ColumnExistsAsync(connection, sourceTable, source, token))
            {
                resolvable = false;
                break;
            }
            sourceColumns.Add(source);
        }

        if (!resolvable || sourceColumns.Count == 0 || !unifiedFormWhitelist.Contains(moduleId))
        {
            // 目标是工作台模块但记录浏览不可用 → 降级为目标模块列表链接
            return field with { BrowseUrl = null, BrowseKeyFields = null };
        }
        return field with { BrowseUrl = null, BrowseKeyFields = sourceColumns };
    }

    /// <summary>
    /// 解析 BROWSE_URL 模板为「目标列 → 来源列」映射（大小写不敏感）。
    /// 优先取 IDX 段（COL={SRC}^COL2={SRC2}）；无 IDX 段时回退到裸查询参数
    /// （值为单一 {SRC} 占位符的参数视为 参数名=目标列、占位符=来源列）。
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseBrowseUrl(string url)
    {
        var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var queryStart = url.IndexOf('?');
        if (queryStart < 0)
        {
            return mapping;
        }
        foreach (var pair in url[(queryStart + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }
            var name = pair[..eq].Trim();
            var value = pair[(eq + 1)..];
            if (!name.Equals("IDX", StringComparison.OrdinalIgnoreCase))
            {
                var match = Placeholder.Match(value);
                if (match.Success && value.Length == match.Value.Length && WorkbenchSql.Identifier.IsMatch(name))
                {
                    mapping[name] = match.Groups[1].Value.Trim();
                }
                continue;
            }
            foreach (var segment in value.Split('^', StringSplitOptions.RemoveEmptyEntries))
            {
                var segEq = segment.IndexOf('=');
                if (segEq <= 0)
                {
                    continue;
                }
                var targetColumn = segment[..segEq].Trim();
                var segmentMatch = Placeholder.Match(segment[(segEq + 1)..]);
                if (segmentMatch.Success && WorkbenchSql.Identifier.IsMatch(targetColumn))
                {
                    mapping[targetColumn] = segmentMatch.Groups[1].Value.Trim();
                }
            }
        }
        return mapping;
    }

    private static async Task<(bool IsWorkbench, string? MasterTable)> GetModuleAsync(
        SqlConnection connection, int moduleId, CancellationToken token)
    {
        if (ModuleCache.TryGetValue(moduleId, out var cached))
        {
            return cached;
        }
        const string sql = "SELECT M_URL,MASTER_TABLE FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX=@ModuleId";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync(token);
        (bool, string?) result;
        if (await reader.ReadAsync(token))
        {
            var url = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            var masterTable = reader.IsDBNull(1) ? null : reader.GetString(1).Trim();
            result = (ModuleRouteValidator.IsWorkbenchUrl(url) && !string.IsNullOrWhiteSpace(masterTable), masterTable);
        }
        else
        {
            result = (false, null);
        }
        ModuleCache[moduleId] = result;
        return result;
    }

    private static async Task<IReadOnlyList<string>> GetPrimaryKeyAsync(
        SqlConnection connection, string table, CancellationToken token)
    {
        if (PrimaryKeyCache.TryGetValue(table, out var cached))
        {
            return cached;
        }
        var pks = await WorkbenchSql.GetPrimaryKeyColumnsAsync(connection, null, table, token);
        PrimaryKeyCache[table] = pks;
        return pks;
    }

    private static async Task<bool> ColumnExistsAsync(
        SqlConnection connection, string table, string column, CancellationToken token)
    {
        var key = table + "\u0001" + column;
        if (ColumnCache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var exists = await WorkbenchSql.ColumnExistsAsync(connection, null, table, column, token);
        ColumnCache[key] = exists;
        return exists;
    }
}