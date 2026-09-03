using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Tests.Tools;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// One-off tool (no-op by default) for CHOOSER_FILTER_MIGRATION_LOG rows with STATUS=MANUAL:
/// deterministic re-conversion / parity comparison / residual triage.
///
/// Background: the earlier offline pipeline could misattribute the source table per row,
/// so some MANUAL rows are actually self-references with an explicit source-table prefix
/// (e.g. OWNER on SYSDL with condition ISNULL(SYSDL.ACTIVE_TAG,0)=1) and were misjudged as
/// cross-table dead configs, leaving FILTER_STRUCT=NULL (fail-closed empty at runtime).
/// The committed ChooserFilterDslConverter / ChooserFilterCompiler handle self-table
/// references correctly (ResolveColumn has an "explicit table == source table" branch);
/// this tool re-runs with FIELD_DATASOURCE.SOURCE_T_ID as the authoritative source table:
///   - rerun mode: writes EOS.API/Data/Migrations/021_fields_chooser_rerun.sql
///     (drift guard: only writes when original text unchanged and current value still NULL;
///     log status converges; affected modules marked dirty; same transaction);
///   - parity mode: read-only old/new predicate comparison → logs/fields-chooser-rerun/parity.csv;
///   - triage mode: residual rows grouped by unique pattern → logs/fields-chooser-rerun/triage.csv.
/// 运行（显式指 csproj，从仓库根）：
///   dotnet test EOS.API.Tests\EOS.API.Tests.csproj --filter ChooserBackfillRerunTool
/// 并按需设环境变量 EOS_TOOL_CHOOSER_RERUN / EOS_TOOL_CHOOSER_PARITY / EOS_TOOL_CHOOSER_TRIAGE = 1。
/// 迁移通道已关闭（CHOOSER_FILTER_MIGRATION_LOG 已于迁移 041 删除）；本工具仅历史重跑/审计参考。
/// </summary>
[Trait("Category", "Tool")]
public sealed class ChooserBackfillRerunTool
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    /// <summary>CHOOSE 跨表 JOIN 白名单（存量 5 表）：运行期仅这些表可绑定。</summary>
    private static readonly IReadOnlySet<string> DslJoinWhitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PRODUCT", "CLIENT_PRICE_M", "CLIENT", "SUPPLIER", "COP_SEND_M",
    };

    private static readonly Regex TablePrefixRef = new(@"\b(?<table>[A-Za-z_][A-Za-z0-9_]{2,})\s*\.", RegexOptions.Compiled);
    private static readonly Regex TemplateToken = new(@"\{(?:(?<kind>m|d)\.(?<col>[A-Za-z_][A-Za-z0-9_]*)|(?<mod>module)|(?<bare>[A-Za-z_][A-Za-z0-9_]*))\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public async Task Rerun_ManualFilters_WithAuthoritativeSourceTable()
    {
        if (Environment.GetEnvironmentVariable("EOS_TOOL_CHOOSER_RERUN") != "1")
        {
            return;
        }

        var (converted, keepExisting, stillManual) = await ConvertManualRowsAsync();
        var root = FindRepoRoot();
        var sql = BuildRerunMigrationSql(converted, keepExisting);
        var migrationPath = Path.Combine(root, "EOS.API", "Data", "Migrations", "038_chooser_b_rerun.sql");
        await File.WriteAllTextAsync(migrationPath, sql, new UTF8Encoding(false));

        var reportDir = EnsureReportDir(root);
        await WriteReportAsync(Path.Combine(reportDir, "rerun-report.csv"), converted, keepExisting, stillManual);
        await WriteStillManualAsync(Path.Combine(reportDir, "still-manual.csv"), stillManual);

        Assert.NotEmpty(converted);
        Assert.True(converted.Count + keepExisting.Count + stillManual.Count > 0, "应至少处理一行");
    }

    [Fact]
    public async Task Parity_DslText_Vs_CompiledPredicate()
    {
        if (Environment.GetEnvironmentVariable("EOS_TOOL_CHOOSER_PARITY") != "1")
        {
            return;
        }

        var (converted, keepExisting, _) = await ConvertManualRowsAsync();
        var parityRows = converted.Select(row => (row.Row, row.Struct, row.Predicate, row.Parameters, row.Joins, Existing: false))
            .Concat(keepExisting.Select(row => (row.Row, row.Struct, row.Predicate, row.Parameters, row.Joins, Existing: true)))
            .ToList();
        var root = FindRepoRoot();
        var reportDir = EnsureReportDir(root);
        var rows = new List<string> { "T_ID,F_ID,SERIAL_NO,SOURCE_T_ID,EXISTING_STRUCT,VERDICT,OLD_ROWS,NEW_ROWS,MODE,BINDING,OLD_ONLY_SAMPLE,NEW_ONLY_SAMPLE,NOTE" };
        int match = 0, mismatch = 0, untestable = 0, emptyBoth = 0;

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        foreach (var (row, filter, predicate, parameters, joins, existing) in parityRows)
        {
            try
            {
                var verdict = await RunParityRowAsync(connection, row, filter, predicate, parameters, joins);
                rows.Add(ToCsvLine(row.TableId, row.FieldId, row.SerialNo.ToString(), row.SourceTable ?? "", existing ? "Y" : "N",
                    verdict.Verdict, verdict.OldCount.ToString(), verdict.NewCount.ToString(), verdict.Mode,
                    verdict.Binding, verdict.OldOnlySample, verdict.NewOnlySample, verdict.Note));
                switch (verdict.Verdict)
                {
                    case "MATCH": match++; if (verdict.OldCount == 0) emptyBoth++; break;
                    case "MISMATCH": mismatch++; break;
                    default: untestable++; break;
                }
            }
            catch (Exception error)
            {
                untestable++;
                rows.Add(ToCsvLine(row.TableId, row.FieldId, row.SerialNo.ToString(), row.SourceTable ?? "", existing ? "Y" : "N", "UNTESTABLE", "", "", "", "", "", "", error.Message));
            }
        }

        await File.WriteAllLinesAsync(Path.Combine(reportDir, "parity.csv"), rows, new UTF8Encoding(false));
        await File.WriteAllTextAsync(
            Path.Combine(reportDir, "parity-summary.txt"),
            $"rows={parityRows.Count} (existing={keepExisting.Count}) match={match} (emptyBoth={emptyBoth}) mismatch={mismatch} untestable={untestable}{Environment.NewLine}",
            new UTF8Encoding(false));

        Assert.NotEmpty(parityRows);
    }

    [Fact]
    public async Task Triage_StillManualRows_WithReachabilityEvidence()
    {
        if (Environment.GetEnvironmentVariable("EOS_TOOL_CHOOSER_TRIAGE") != "1")
        {
            return;
        }

        var (_, _, stillManual) = await ConvertManualRowsAsync();
        var root = FindRepoRoot();
        var reportDir = EnsureReportDir(root);

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        var groups = stillManual
            .GroupBy(row => (Text: Normalize(row.Row.SourceDsl), row.Row.SourceTable))
            .OrderBy(group => group.Key.SourceTable, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(group => group.Count())
            .ToList();
        var lines = new List<string>
        {
            "SOURCE_T_ID,ROW_COUNT,TABLES_REFERENCED,UNREACHABLE_TABLES,OLD_SYSTEM_BEHAVIOR,SUGGESTED_ACTION,LEGACY_FILTER",
        };
        var markdown = new StringBuilder("# 残余 MANUAL 行分诊（按唯一模式）\n\n");
        foreach (var group in groups)
        {
            var text = group.Key.Text ?? string.Empty;
            var refs = TablePrefixRef.Matches(text)
                .Select(match => match.Groups["table"].Value)
                .Where(table => !table.Equals(group.Key.SourceTable, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var catalog = group.Key.SourceTable is null
                ? null
                : await ChooserJoinCatalog.GetAsync(connection, group.Key.SourceTable, CancellationToken.None);
            var unreachable = refs.Where(table => catalog is null || !catalog.Aliases.Contains(table)).ToList();
            var oldBehavior = refs.Count == 0
                ? "无外部表引用（转换/物理校验失败，见 rerun-report）"
                : unreachable.Count == 0
                    ? "引用表均可达"
                    : refs.Any(table => DslJoinWhitelist.Contains(table))
                        ? "部分引用表在白名单但 QUERY_RELATION 未覆盖"
                        : "引用表不可达（运行期绑定失败）";
            var action = refs.Count == 0
                ? "REVIEW：查 rerun-report.csv 的失败原因"
                : unreachable.Count == 0
                    ? "REVIEW：转换失败原因（可能为物理列缺失/超复杂）"
                    : refs.Any(table => DslJoinWhitelist.Contains(table)) || unreachable.Count < refs.Count
                        ? "EXTEND_QUERY_RELATION：为源表补标准关联后重转，否则保持 NULL"
                        : "KEEP_NULL（默认）；确需此过滤则补 QUERY_RELATION 并重转";
            lines.Add(ToCsvLine(group.Key.SourceTable ?? "", group.Count().ToString(),
                string.Join(";", refs), string.Join(";", unreachable), oldBehavior, action, text));
            markdown.AppendLine($"- **{group.Key.SourceTable ?? "(无源表)"}** ×{group.Count()}：`{text}`");
            markdown.AppendLine($"  引用={string.Join(",", refs.Count > 0 ? refs : new[] { "（无）" })}；不可达={string.Join(",", unreachable.Count > 0 ? unreachable : new[] { "（无）" })}");
            markdown.AppendLine($"  现状：{oldBehavior}；建议：{action}");
        }

        await File.WriteAllLinesAsync(Path.Combine(reportDir, "triage.csv"), lines, new UTF8Encoding(false));
        await File.WriteAllTextAsync(Path.Combine(reportDir, "triage.md"), markdown.ToString(), new UTF8Encoding(false));
        Assert.True(stillManual.Count >= 0);
    }

    // ==================== 重转核心 ====================

    private sealed record ManualRow(
        int LogId, string TableId, string FieldId, int SerialNo, string SourceDsl, string Tier,
        string? SourceTable, int? SourceModuleId, bool Active, string? CurrentStruct);

    private sealed record ConvertedRow(
        ManualRow Row, ChooserFilterStruct Struct, string StructJson, string Predicate,
        IReadOnlyList<ChooserFilterParameter> Parameters, IReadOnlyList<string> Joins,
        IReadOnlyList<(string Table, string Column)> ForeignColumns);

    /// <summary>017/018 已落结构但日志状态仍为 MANUAL 的行：按既有结构比对；FreshCompare=SAME 时日志可直接收敛。</summary>
    private sealed record KeepExistingRow(
        ManualRow Row, ChooserFilterStruct Struct, string StructJson, string Predicate,
        IReadOnlyList<ChooserFilterParameter> Parameters, IReadOnlyList<string> Joins,
        IReadOnlyList<(string Table, string Column)> ForeignColumns,
        string FreshCompare);

    private sealed record StillManualRow(ManualRow Row, string Reason);

    private static async Task<(List<ConvertedRow> Converted, List<KeepExistingRow> KeepExisting, List<StillManualRow> StillManual)> ConvertManualRowsAsync()
    {
        Assert.False(ConnectionString.Value is null, "拿不到开发库连接串（EOS_ERP_TEST_CONNECTION 或 ~/.codex/config.toml）");

        var rows = new List<ManualRow>();
        var converted = new List<ConvertedRow>();
        var keepExisting = new List<KeepExistingRow>();
        var stillManual = new List<StillManualRow>();
        var catalogs = new Dictionary<string, ChooserSourceJoins>(StringComparer.OrdinalIgnoreCase);

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        const string loadSql = """
            SELECT l.ID, l.T_ID, l.F_ID, l.SERIAL_NO, LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER, N''))) AS LEGACY, l.TIER,
                   LTRIM(RTRIM(ISNULL(d.SOURCE_T_ID, N''))) AS SOURCE_T_ID, d.SOURCE_M_IDX, d.ACTIVE_TAG, d.FILTER_STRUCT
            FROM dbo.CHOOSER_FILTER_MIGRATION_LOG l
            JOIN dbo.FIELD_DATASOURCE d ON d.T_ID = l.T_ID AND d.F_ID = l.F_ID AND d.SERIAL_NO = l.SERIAL_NO
            WHERE l.STATUS = N'MANUAL';
            """;
        await using (var command = new SqlCommand(loadSql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                rows.Add(new ManualRow(
                    reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3),
                    reader.GetString(4), reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetInt32(7),
                    !reader.IsDBNull(8) && reader.GetBoolean(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }
        }

        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.SourceTable))
            {
                stillManual.Add(new StillManualRow(row, "MISSING_SOURCE：FIELD_DATASOURCE.SOURCE_T_ID 为空"));
                continue;
            }
            if (!catalogs.TryGetValue(row.SourceTable, out var catalog))
            {
                catalog = await ChooserJoinCatalog.GetAsync(connection, row.SourceTable, CancellationToken.None);
                catalogs[row.SourceTable] = catalog;
            }

            var result = ChooserFilterDslConverter.Convert(
                row.SourceDsl, row.SourceTable,
                new ChooserFilterDslConverter.ConvertOptions(JoinAliases: catalog.Aliases));

            if (row.CurrentStruct is not null)
            {
                // 017/018 已落结构、日志状态未收敛：以既有结构为准比对；与重转结果一致时日志可安全收敛
                if (!ChooserFilterStruct.TryParse(row.CurrentStruct, out var existing) || existing is null)
                {
                    stillManual.Add(new StillManualRow(row, "EXISTING_STRUCT_INVALID：既有 FILTER_STRUCT 无法解析"));
                    continue;
                }
                var existingCompile = ChooserFilterCompiler.Compile(existing, row.SourceTable, catalog.Aliases, null);
                if (existingCompile is null)
                {
                    stillManual.Add(new StillManualRow(row, "EXISTING_STRUCT_COMPILE_FAIL：既有结构编译 fail-closed（运行期同样空选项，需重建）"));
                    continue;
                }
                var existingJson = JsonSerializer.Serialize(existing, JsonOptions);
                var freshCompare = result.Struct is not null
                    ? (JsonSerializer.Serialize(result.Struct, JsonOptions) == existingJson ? "SAME" : "DIFF")
                    : "NO_FRESH";
                keepExisting.Add(new KeepExistingRow(row, existing, existingJson, existingCompile.Predicate,
                    existingCompile.Parameters, existingCompile.Joins, existingCompile.ForeignColumns, freshCompare));
                continue;
            }

            if (result.Struct is null)
            {
                stillManual.Add(new StillManualRow(row, $"CONVERT_FAIL(T{result.Tier})：{result.Error}"));
                continue;
            }

            var compile = ChooserFilterCompiler.Compile(result.Struct, row.SourceTable, catalog.Aliases, null);
            if (compile is null)
            {
                stillManual.Add(new StillManualRow(row, "COMPILE_FAIL：编译器 fail-closed"));
                continue;
            }

            // JOIN 闭包护栏（对齐运行期 GetChooserOptionsAsync 口径）：引用了跨表别名但 BuildJoinClause
            // 无法构建时，运行期同样 fail-closed——转换了也不可用，留待分诊
            if (compile.Joins.Count > 0 && ChooserJoinCatalog.BuildJoinClause(catalog, compile.Joins) is null)
            {
                stillManual.Add(new StillManualRow(row, $"JOIN_CLOSURE_FAIL：编译引用[{string.Join(',', compile.Joins)}] 目录Joins={catalog.Joins.Count} 目录Error={catalog.Error ?? "无"}（运行期将 fail-closed）"));
                continue;
            }

            var physical = await ValidatePhysicalColumnsAsync(connection, row.SourceTable, result.Struct);
            if (physical is not null)
            {
                stillManual.Add(new StillManualRow(row, $"PHYSICAL_MISSING：{physical}"));
                continue;
            }

            converted.Add(new ConvertedRow(row, result.Struct,
                JsonSerializer.Serialize(result.Struct, JsonOptions), compile.Predicate, compile.Parameters, compile.Joins, compile.ForeignColumns));
        }

        return (converted, keepExisting, stillManual);
    }

    /// <summary>物理存在校验（对齐 018 护栏口径）：引用的表须存在（U/V），列须存在；返回 null 表示通过。</summary>
    private static async Task<string?> ValidatePhysicalColumnsAsync(SqlConnection connection, string sourceTable, ChooserFilterStruct filter)
    {
        var pairs = new List<(string Table, string Column)>();
        CollectItem(filter, sourceTable, pairs);

        var cache = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (table, column) in pairs)
        {
            if (!cache.TryGetValue(table, out var columns))
            {
                columns = await LoadTableColumnsAsync(connection, table);
                cache[table] = columns;
            }
            if (columns is null)
            {
                return $"{table} 无物理表";
            }
            if (column != "*" && !columns.Contains(column))
            {
                return $"{table} 无物理列 {column}";
            }
        }
        return null;

        static async Task<HashSet<string>?> LoadTableColumnsAsync(SqlConnection connection, string table)
        {
            await using var exists = new SqlCommand("SELECT OBJECT_ID(@Qualified)", connection);
            exists.Parameters.Add("@Qualified", SqlDbType.NVarChar, 384).Value = $"dbo.[{table}]";
            if (await exists.ExecuteScalarAsync() is null)
            {
                return null;
            }
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            const string sql = "SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(@Qualified);";
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@Qualified", SqlDbType.NVarChar, 384).Value = $"dbo.[{table}]";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            return columns;
        }

        static void CollectItem(ChooserFilterStruct filter, string defaultTable, List<(string, string)> pairs)
        {
            foreach (var item in filter.Items) CollectFilterItem(item, defaultTable, pairs);
        }

        static void CollectFilterItem(ChooserFilterItem item, string defaultTable, List<(string, string)> pairs)
        {
            AddField(item.Field, defaultTable, pairs);
            CollectExpression(item.Left, defaultTable, pairs);
            CollectExpression(item.Right, defaultTable, pairs);
            if (item.Group is not null) CollectItem(item.Group, defaultTable, pairs);
            if (item.Subquery is not null)
            {
                var subTables = (item.Subquery.From ?? []).ToList();
                foreach (var from in subTables) pairs.Add((from.Table, "*"));
                if (item.Subquery.Column is not null)
                {
                    var parts = item.Subquery.Column.Split('.');
                    if (parts.Length == 2) pairs.Add((parts[0], parts[1]));
                    else if (subTables.Count == 1) pairs.Add((subTables[0].Table, item.Subquery.Column));
                }
                if (subTables.Count == 1)
                {
                    foreach (var sub in item.Subquery.Filter ?? []) CollectFilterItem(sub, subTables[0].Table, pairs);
                }
            }
        }

        static void CollectExpression(ChooserFilterExpression? expression, string defaultTable, List<(string, string)> pairs)
        {
            if (expression is null) return;
            if (expression.Kind == "column" && !string.IsNullOrEmpty(expression.Column))
            {
                var table = string.IsNullOrEmpty(expression.Table) ? defaultTable : expression.Table;
                var column = expression.Column;
                // 带点号列名拆分（对齐运行期 CompileExpression.ResolveColumn 语义）：
                // 子查询裸列表达式经 QualifyBare 后 Table 为空、Column 为 "表.列"，须拆开再校验
                var dot = column.IndexOf('.');
                if (dot > 0 && string.IsNullOrEmpty(expression.Table))
                {
                    table = column[..dot].Trim();
                    column = column[(dot + 1)..].Trim();
                }
                pairs.Add((table, column));
            }
            CollectExpression(expression.Left, defaultTable, pairs);
            CollectExpression(expression.Right, defaultTable, pairs);
        }

        static void AddField(string? field, string defaultTable, List<(string, string)> pairs)
        {
            if (string.IsNullOrWhiteSpace(field)) return;
            var parts = field.Split('.');
            if (parts.Length == 2) pairs.Add((parts[0], parts[1]));
            else pairs.Add((defaultTable, field));
        }
    }

    // ==================== 比对 ====================

    private sealed record ParityVerdict(string Verdict, int OldCount, int NewCount, string Mode, string Binding, string OldOnlySample, string NewOnlySample, string Note);

    private static async Task<ParityVerdict> RunParityRowAsync(
        SqlConnection connection, ManualRow row, ChooserFilterStruct filter, string predicate,
        IReadOnlyList<ChooserFilterParameter> parameters, IReadOnlyList<string> joins)
    {
        var source = row.SourceTable!;
        var legacy = Normalize(row.SourceDsl);

        if (legacy.Contains(';') || legacy.Contains("--") || legacy.Contains("/*"))
        {
            return new ParityVerdict("UNTESTABLE", 0, 0, "", "", "", "", "原文含多语句/注释片段，拒绝执行");
        }
        if (Regex.IsMatch(legacy, @"\b(INSERT|UPDATE|DELETE|DROP|ALTER|EXEC|TRUNCATE|MERGE|CREATE)\b", RegexOptions.IgnoreCase))
        {
            return new ParityVerdict("UNTESTABLE", 0, 0, "", "", "", "", "原文含写操作关键字，拒绝执行");
        }

        // 同一绑定语义：模板列从源表取样（m./d. 共用一份样例），{module} 取 SOURCE_M_IDX，{X} 裸模板保持字面量
        var moduleId = row.SourceModuleId ?? 0;
        var templateColumns = TemplateToken.Matches(legacy)
            .Concat(parameters.Select(parameter => TemplateToken.Match(parameter.RawValue ?? "")))
            .Where(match => match.Success)
            .Select(match => match.Groups["col"].Success ? match.Groups["col"].Value : match.Groups["bare"].Value)
            .Where(column => !string.IsNullOrEmpty(column) && column != "module")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in templateColumns)
        {
            samples[column] = await SampleColumnValueAsync(connection, source, column);
        }
        var bindingText = string.Join(";", samples.Select(pair => $"{pair.Key}={pair.Value}"));

        var boundDsl = BindDslText(legacy, samples, moduleId);

        var catalog = await ChooserJoinCatalog.GetAsync(connection, source, CancellationToken.None);
        var joinClause = joins.Count == 0 ? string.Empty : ChooserJoinCatalog.BuildJoinClause(catalog, joins)
            ?? throw new InvalidOperationException("JOIN 闭包无法构建（运行期亦 fail-closed）");

        var keyColumns = await GetKeyColumnsAsync(connection, source);
        var selectList = keyColumns.Count > 0 ? string.Join(", ", keyColumns.Select(column => $"[{source}].[{column}]")) : "COUNT_BIG(*)";
        var groupSuffix = keyColumns.Count > 0 ? $" GROUP BY {string.Join(", ", keyColumns.Select(column => $"[{source}].[{column}]"))}" : "";
        var topPrefix = keyColumns.Count > 0 ? "TOP 600 " : "";

        var oldSql = $"SELECT {topPrefix}{selectList} FROM [{source}] WITH (NOLOCK) {joinClause} WHERE {boundDsl}{groupSuffix}";
        var newSql = $"SELECT {topPrefix}{selectList} FROM [{source}] WITH (NOLOCK) {joinClause} WHERE {predicate}{groupSuffix}";

        var sqlParameters = parameters.Select(parameter => (parameter.Name,
            Value: ChooserFilterCompiler.BindRuntimeValue(parameter, samples, samples, moduleId))).ToList();
        var oldRows = await ExecuteKeySetAsync(connection, oldSql, keyColumns.Count > 0);
        var newRows = await ExecuteKeySetAsync(connection, newSql, keyColumns.Count > 0, sqlParameters);

        if (oldRows.Count == 0 && newRows.Count == 0)
        {
            return new ParityVerdict("MATCH", 0, 0, keyColumns.Count > 0 ? "KEYS" : "COUNT", bindingText, "", "", "两侧同为空（模板绑定可能不具代表性，复核时注意）");
        }
        if (oldRows.SetEquals(newRows))
        {
            return new ParityVerdict("MATCH", oldRows.Count, newRows.Count, keyColumns.Count > 0 ? "KEYS" : "COUNT", bindingText, "", "", "");
        }
        var oldOnly = string.Join("|", oldRows.Except(newRows).Take(5));
        var newOnly = string.Join("|", newRows.Except(oldRows).Take(5));
        return new ParityVerdict("MISMATCH", oldRows.Count, newRows.Count, keyColumns.Count > 0 ? "KEYS" : "COUNT", bindingText, oldOnly, newOnly, "");
    }

    /// <summary>模板列取样（返回原始值文本，引号处理交给绑定处：模板可能嵌在原文的字符串字面量内）。</summary>
    private static async Task<string> SampleColumnValueAsync(SqlConnection connection, string table, string column)
    {
        try
        {
            await using var command = new SqlCommand($"SELECT TOP 1 [{column}] FROM [{table}] WITH (NOLOCK) WHERE [{column}] IS NOT NULL", connection) { CommandTimeout = 15 };
            var value = await command.ExecuteScalarAsync();
            if (value is null || value is DBNull) return "1";
            return value.ToString() ?? "1";
        }
        catch
        {
            return "1";
        }
    }

    /// <summary>旧原文模板绑定：引号内注入原始值，引号外注入 SQL 字面量（数字裸写 / 字符串 N'…'）。</summary>
    private static string BindDslText(string legacy, Dictionary<string, string> samples, int moduleId)
    {
        var builder = new StringBuilder(legacy.Length + 64);
        var inQuote = false;
        for (var i = 0; i < legacy.Length; i++)
        {
            var c = legacy[i];
            if (c == '\'')
            {
                inQuote = !inQuote;
                builder.Append(c);
                continue;
            }
            if (c == '{')
            {
                var match = TemplateToken.Match(legacy, i);
                if (match.Success && match.Index == i)
                {
                    if (match.Groups["mod"].Success)
                    {
                        builder.Append(moduleId);
                    }
                    else
                    {
                        var column = match.Groups["col"].Success ? match.Groups["col"].Value : match.Groups["bare"].Value;
                        var raw = samples.TryGetValue(column, out var value) ? value : "1";
                        builder.Append(inQuote
                            ? raw
                            : Regex.IsMatch(raw, @"^-?\d+(\.\d+)?$") ? raw : $"N'{raw.Replace("'", "''")}'");
                    }
                    i = match.Index + match.Length - 1;
                    continue;
                }
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    private static async Task<List<string>> GetKeyColumnsAsync(SqlConnection connection, string table)
    {
        const string sql = """
            SELECT c.name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.object_id = OBJECT_ID(@Table) AND i.is_primary_key = 1
            ORDER BY ic.key_ordinal;
            """;
        var columns = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 384).Value = $"dbo.{table}";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
        return columns;
    }

    private static async Task<HashSet<string>> ExecuteKeySetAsync(
        SqlConnection connection, string sql, bool keyed, List<(string Name, string Value)>? parameters = null)
    {
        var rows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 30 };
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, (object?)value ?? DBNull.Value);
            }
        }
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (keyed)
            {
                var cells = new string[reader.FieldCount];
                for (var i = 0; i < reader.FieldCount; i++) cells[i] = reader.IsDBNull(i) ? "" : reader.GetValue(i).ToString() ?? "";
                rows.Add(string.Join('\x1', cells));
            }
            else
            {
                rows.Add(reader.GetValue(0).ToString() ?? "");
            }
        }
        return rows;
    }

    // ==================== 021 迁移生成 ====================

    private static string BuildRerunMigrationSql(List<ConvertedRow> converted, List<KeepExistingRow> keepExisting)
    {
        var convergeSame = keepExisting.Where(row => row.FreshCompare == "SAME").ToList();
        var builder = new StringBuilder();
        builder.AppendLine("-- MANUAL 存量确定性重转");
        builder.AppendLine("-- 背景：018 离线管线按行非确定性传错源表（同一（原文, 源表）组合成功/失败并存），");
        builder.AppendLine($"--       本迁移将 {converted.Count} 行 MANUAL 以 FIELD_DATASOURCE.SOURCE_T_ID 为权威源表，经库内提交的");
        builder.AppendLine("--       ChooserFilterDslConverter + ChooserFilterCompiler 重转通过（含物理表/列存在校验）。");
        builder.AppendLine("-- 漂移守卫：原文一致且 FILTER_STRUCT 仍为 NULL 才落；状态收敛与标脏同事务。");
        builder.AppendLine("-- 生成工具：EOS.API.Tests/ChooserBackfillRerunTool.cs（rerun 模式，提交入库可复现）。");
        builder.AppendLine();
        builder.AppendLine("SET NOCOUNT ON;");
        builder.AppendLine();
        builder.AppendLine("DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';");
        builder.AppendLine("IF DB_NAME() <> N'EOS.ERP'");
        builder.AppendLine("    THROW 50000, @GUARD_MESSAGE, 1;");
        builder.AppendLine();
        builder.AppendLine("BEGIN TRANSACTION;");
        builder.AppendLine();
        builder.AppendLine("CREATE TABLE #RERUN (T_ID NVARCHAR(100) NOT NULL, F_ID NVARCHAR(100) NOT NULL, SERIAL_NO INT NOT NULL, LEGACY NVARCHAR(MAX) NOT NULL, SOURCE NVARCHAR(100) NOT NULL, STRUCT NVARCHAR(MAX) NOT NULL);");

        const int batchSize = 100;
        for (var start = 0; start < converted.Count; start += batchSize)
        {
            builder.Append("INSERT INTO #RERUN (T_ID, F_ID, SERIAL_NO, LEGACY, SOURCE, STRUCT) VALUES");
            var chunk = converted.Skip(start).Take(batchSize).ToList();
            for (var i = 0; i < chunk.Count; i++)
            {
                var item = chunk[i];
                builder.AppendLine();
                builder.Append($"  ({Sql(item.Row.TableId)}, {Sql(item.Row.FieldId)}, {item.Row.SerialNo}, {Sql(Normalize(item.Row.SourceDsl))}, {Sql(item.Row.SourceTable!)}, {Sql(item.StructJson)}){(i < chunk.Count - 1 ? "," : ";")}");
            }
            builder.AppendLine();
            builder.AppendLine();
        }

        builder.AppendLine("-- 1) FILTER_STRUCT 回填（键 + 原文 + 源表三重守卫）");
        builder.AppendLine("""
            UPDATE c SET FILTER_STRUCT = r.STRUCT, LAST_UPDATE_BY = N'EOS-MIG', LAST_UPDATE_DATE = SYSDATETIME()
            FROM dbo.FIELD_DATASOURCE c
            JOIN #RERUN r ON r.T_ID = c.T_ID AND r.F_ID = c.F_ID AND r.SERIAL_NO = c.SERIAL_NO
            JOIN dbo.CHOOSER_FILTER_MIGRATION_LOG l ON l.T_ID = c.T_ID AND l.F_ID = c.F_ID AND l.SERIAL_NO = c.SERIAL_NO
            WHERE c.FILTER_STRUCT IS NULL
              AND l.STATUS <> N'CONVERTED'
              AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER, N''))) = r.LEGACY
              AND LTRIM(RTRIM(ISNULL(c.SOURCE_T_ID, N''))) = r.SOURCE;
            """);
        builder.AppendLine();
        builder.AppendLine("-- 2) 迁移日志状态收敛——(a) 本次回填行；");
        if (convergeSame.Count > 0)
        {
            builder.AppendLine($"--    (b) 017/018 已落结构且与重转结果一致的既有行 {convergeSame.Count} 条（日志滞留 MANUAL）；");
        }
        builder.AppendLine("DECLARE @CONVERGED_BACKFILL INT = (SELECT COUNT(*) FROM #RERUN);");
        builder.AppendLine("""
            UPDATE l SET STATUS = N'CONVERTED', ERROR = NULL
            FROM dbo.CHOOSER_FILTER_MIGRATION_LOG l
            JOIN dbo.FIELD_DATASOURCE c ON c.T_ID = l.T_ID AND c.F_ID = l.F_ID AND c.SERIAL_NO = l.SERIAL_NO
            JOIN #RERUN r ON r.T_ID = l.T_ID AND r.F_ID = l.F_ID AND r.SERIAL_NO = l.SERIAL_NO
            WHERE l.STATUS <> N'CONVERTED'
              AND c.FILTER_STRUCT IS NOT NULL
              AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER, N''))) = r.LEGACY
              AND LTRIM(RTRIM(ISNULL(c.SOURCE_T_ID, N''))) = r.SOURCE;
            """);
        if (convergeSame.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("-- (b) 既有结构行：结构非空且与重转结果一致，仅收敛日志状态");
            builder.AppendLine("CREATE TABLE #CONVERGE (T_ID NVARCHAR(100) NOT NULL, F_ID NVARCHAR(100) NOT NULL, SERIAL_NO INT NOT NULL, LEGACY NVARCHAR(MAX) NOT NULL, SOURCE NVARCHAR(100) NOT NULL);");
            const int convergeBatch = 100;
            for (var start = 0; start < convergeSame.Count; start += convergeBatch)
            {
                builder.Append("INSERT INTO #CONVERGE (T_ID, F_ID, SERIAL_NO, LEGACY, SOURCE) VALUES");
                var chunk = convergeSame.Skip(start).Take(convergeBatch).ToList();
                for (var i = 0; i < chunk.Count; i++)
                {
                    var item = chunk[i];
                    builder.AppendLine();
                    builder.Append($"  ({Sql(item.Row.TableId)}, {Sql(item.Row.FieldId)}, {item.Row.SerialNo}, {Sql(Normalize(item.Row.SourceDsl))}, {Sql(item.Row.SourceTable!)}){(i < chunk.Count - 1 ? "," : ";")}");
                }
                builder.AppendLine();
                builder.AppendLine();
            }
            builder.AppendLine("""
                UPDATE l SET STATUS = N'CONVERTED', ERROR = NULL
                FROM dbo.CHOOSER_FILTER_MIGRATION_LOG l
                JOIN dbo.FIELD_DATASOURCE c ON c.T_ID = l.T_ID AND c.F_ID = l.F_ID AND c.SERIAL_NO = l.SERIAL_NO
                JOIN #CONVERGE r ON r.T_ID = l.T_ID AND r.F_ID = l.F_ID AND r.SERIAL_NO = l.SERIAL_NO
                WHERE l.STATUS <> N'CONVERTED'
                  AND c.FILTER_STRUCT IS NOT NULL
                  AND LTRIM(RTRIM(ISNULL(l.LEGACY_FILTER, N''))) = r.LEGACY
                  AND LTRIM(RTRIM(ISNULL(c.SOURCE_T_ID, N''))) = r.SOURCE;

                DROP TABLE #CONVERGE;
                """);
        }
        builder.AppendLine();
        builder.AppendLine("-- 3) 受影响模块标脏");
        builder.AppendLine("""
            INSERT INTO dbo.WORKBENCH_MODULE_DIRTY (MODULE_ID, DIRTY_TAG, LAST_MODIFIED_BY, LAST_MODIFIED_AT)
            SELECT DISTINCT m.M_IDX, 1, N'EOS-MIG', GETDATE()
            FROM dbo.MODULES m
            WHERE (m.MASTER_TABLE IN (SELECT T_ID FROM #RERUN)
                   OR ISNULL(m.DETAIL_TABLE, '') IN (SELECT T_ID FROM #RERUN))
              AND NOT EXISTS (SELECT 1 FROM dbo.WORKBENCH_MODULE_DIRTY d WHERE d.MODULE_ID = m.M_IDX);
            """);
        builder.AppendLine();
        builder.AppendLine("DROP TABLE #RERUN;");
        builder.AppendLine("COMMIT;");
        return builder.ToString();
    }

    // ==================== 报告与杂项 ====================

    private static async Task WriteReportAsync(
        string path, List<ConvertedRow> converted, List<KeepExistingRow> keepExisting, List<StillManualRow> stillManual)
    {
        var lines = new List<string> { "T_ID,F_ID,SERIAL_NO,SOURCE_T_ID,VERDICT,REASON,TIER,LEGACY_FILTER,STRUCT_JSON" };
        foreach (var row in converted)
        {
            lines.Add(ToCsvLine(row.Row.TableId, row.Row.FieldId, row.Row.SerialNo.ToString(), row.Row.SourceTable ?? "", "CONVERTED", "", row.Row.Tier, row.Row.SourceDsl, row.StructJson));
        }
        foreach (var row in keepExisting)
        {
            lines.Add(ToCsvLine(row.Row.TableId, row.Row.FieldId, row.Row.SerialNo.ToString(), row.Row.SourceTable ?? "", $"KEEP_EXISTING_{row.FreshCompare}", "日志状态未收敛（结构已落，随 021 收敛）", row.Row.Tier, row.Row.SourceDsl, row.StructJson));
        }
        foreach (var row in stillManual)
        {
            lines.Add(ToCsvLine(row.Row.TableId, row.Row.FieldId, row.Row.SerialNo.ToString(), row.Row.SourceTable ?? "", "STILL_MANUAL", row.Reason, row.Row.Tier, row.Row.SourceDsl, ""));
        }
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false));
    }

    private static Task WriteStillManualAsync(string path, List<StillManualRow> stillManual)
    {
        var lines = new List<string> { "T_ID,F_ID,SERIAL_NO,SOURCE_T_ID,REASON,LEGACY_FILTER" };
        foreach (var row in stillManual)
        {
            lines.Add(ToCsvLine(row.Row.TableId, row.Row.FieldId, row.Row.SerialNo.ToString(), row.Row.SourceTable ?? "", row.Reason, row.Row.SourceDsl));
        }
        return File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false));
    }

    private static string Normalize(string text) => text.Trim();

    private static string Sql(string? value) => value is null ? "NULL" : $"N'{value.Replace("'", "''")}'";

    private static string ToCsvLine(params string[] cells) => string.Join(",", cells.Select(cell =>
        cell is null ? "" : (cell.Contains(',') || cell.Contains('"') || cell.Contains('\n') ? $"\"{cell.Replace("\"", "\"\"")}\"" : cell)));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EOS.slnx")))
        {
            directory = directory.Parent;
        }
        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string EnsureReportDir(string root)
    {
        var path = Path.Combine(root, "logs", "fields-chooser-rerun");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
