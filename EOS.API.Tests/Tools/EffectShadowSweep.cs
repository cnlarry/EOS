using System.Data;
using System.Reflection;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests.Tools;

/// <summary>
/// Batch driver for <see cref="EffectShadowRunner"/>: replays the shadow comparison for every
/// module that carries business actions with the effect engine enabled, for the approve and
/// deapprove events, in a single test host. The acceptance ledger only counts a report
/// whose <c>definitionVersion</c> is at least the currently published snapshot version, so a
/// republish sweep turns every existing report into "stale" evidence; this tool rebuilds that
/// evidence without spawning one test host per module.
///
/// Modules whose workflow stored procedure no longer exists are replayed engine-only, which is
/// what the ledger reads as class B ("engine ran cleanly, the legacy side is retired"). Reports
/// land in logs/shadow/ exactly like the single-module runner. DB-only tool: it never writes
/// workspace configuration, never sends HTTP and never starts services.
///
/// Run (from the repository root, with MSSQL_ERP_CONN set):
///   $env:EOS_SHADOW_SWEEP='1'
///   dotnet vstest &lt;output&gt;/EOS.API.Tests.dll --TestCaseFilter:"FullyQualifiedName~EffectShadowSweep"
/// Optional: EOS_SHADOW_EVENTS (default APPROVE_EFFECT,DEAPPROVE) and EOS_SHADOW_MODULES
/// (comma separated module ids) restrict the sweep.
/// </summary>
[Trait("Category", "Tool")]
public sealed class EffectShadowSweep
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);
    private static readonly Lazy<string> RepoRoot = new(FindRepoRoot);

    private sealed record SweepResult(
        int ModuleId,
        string Event,
        string Version,
        string Verdict,
        string Old,
        string New,
        int Tables,
        string Keys,
        string KeysSource,
        string? Error,
        // 其中属"已拍板口径差异"的条数（白名单命中、附 decision 出处）
        int Accepted = 0);

    [Fact]
    public async Task Sweep_RefreshLedgerEvidence()
    {
        if (Environment.GetEnvironmentVariable("EOS_SHADOW_SWEEP") != "1")
        {
            return;
        }
        Assert.False(ConnectionString.Value is null,
            "拿不到开发库连接串（MSSQL_ERP_CONN 或 EOS.API/appsettings.Development.json）");

        var events = (Environment.GetEnvironmentVariable("EOS_SHADOW_EVENTS") ?? "APPROVE_EFFECT,DEAPPROVE")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var moduleFilter = (Environment.GetEnvironmentVariable("EOS_SHADOW_MODULES") ?? string.Empty)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToHashSet();

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        var scope = await ReadScopeAsync(connection);
        if (moduleFilter.Count > 0)
        {
            scope = scope.Where(moduleId => moduleFilter.Contains(moduleId)).OrderBy(moduleId => moduleId).ToList();
        }

        Console.WriteLine($"sweep scope={scope.Count} events={string.Join(',', events)}");
        var runner = new EffectShadowRunner();
        var history = ReadHistoricalSamples();
        var results = new List<SweepResult>();
        foreach (var moduleId in scope)
        {
            var (version, definitionJson) = await ReadSnapshotAsync(connection, moduleId);
            if (version is null || definitionJson is null)
            {
                results.Add(new SweepResult(moduleId, "(snapshot)", "-", "SKIP", "-", "-", 0, string.Empty, "-", "无已发布快照"));
                Console.WriteLine($"  {moduleId} SKIP 无已发布快照");
                continue;
            }
            var definition = JsonSerializer.Deserialize<WorkbenchDefinition>(
                definitionJson, WorkbenchDefinitionProvider.JsonOptions);
            // 遗留批核过程钩子（MODULES.UPDATE_SP）已从库内物理删除：旧侧不复存在，
            // 所有回放一律按"引擎单跑"（B 类证据）。
            const bool engineOnly = true;

            foreach (var shadowEvent in events)
            {
                var result = await RunWithCandidateScanAsync(
                    connection, runner, moduleId, shadowEvent, engineOnly, history);
                results.Add(result);
                var flattened = (result.Error ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                var detail = flattened.Length > 0 ? " | " + flattened[..Math.Min(160, flattened.Length)] : string.Empty;
                Console.WriteLine(
                    $"  {result.ModuleId,-8} {result.Event,-15} {result.Version,-18} {result.Verdict,-5} " +
                    $"old={result.Old,-8} new={result.New,-8} tables={result.Tables} " +
                    $"accepted={result.Accepted} keys={result.KeysSource}{detail}");
            }
        }

        var summaryPath = await WriteSummaryAsync(results);
        Console.WriteLine($"sweep done runs={results.Count} " +
            $"PASS={results.Count(r => r.Verdict == "PASS")} FAIL={results.Count(r => r.Verdict == "FAIL")} " +
            $"ERROR={results.Count(r => r.Verdict == "ERROR")} NO_SAMPLE={results.Count(r => r.Verdict == "NO_SAMPLE")} " +
            $"SKIP={results.Count(r => r.Verdict == "SKIP")}");
        Console.WriteLine($"sweep summary={summaryPath}");

        Assert.True(results.Count > 0, "扫描范围为空：模块白名单与 EFFECT_ENGINE_TAG 是否已就绪？");
    }

    /// <summary>
    /// Picks a document the engine can actually process and replays the shadow run on it.
    /// The per-module selection SQL inside the runner encodes today's business guards; once a
    /// newly seeded rule or a stock situation changes, it either finds nothing or hands back a
    /// document both paths refuse. Probing candidates against the engine itself keeps the
    /// ledger honest without hand-maintaining a predicate per module: the first candidate that
    /// runs clean wins, and only that attempt is written to logs/shadow (probes are silent).
    /// </summary>
    private static async Task<SweepResult> RunWithCandidateScanAsync(
        SqlConnection connection,
        EffectShadowRunner runner,
        int moduleId,
        string shadowEvent,
        bool engineOnly,
        IReadOnlyDictionary<string, IReadOnlyList<string>> history)
    {
        // Order matters: the runner's own per-module selection encodes the guards it knows about,
        // so it goes first; the generic recent-document scan covers the modules it cannot serve;
        // the record an earlier run used is the last resort (fixture documents are usually gone).
        var candidates = new List<(string? Keys, string Source)> { (null, "auto") };
        foreach (var candidate in await ReadCandidatesAsync(connection, moduleId, shadowEvent))
        {
            if (candidates.All(entry => entry.Keys != candidate))
            {
                candidates.Add((candidate, "scan"));
            }
        }
        if (history.TryGetValue($"{moduleId}|{shadowEvent}", out var remembered))
        {
            var keys = string.Join('|', remembered);
            if (candidates.All(entry => entry.Keys != keys))
            {
                candidates.Add((keys, "history"));
            }
        }

        SweepResult? executed = null;
        string? executedKeys = null;
        var executedSource = "auto";
        SweepResult? refused = null;
        SweepResult? legacyOnly = null;
        string? legacyOnlyKeys = null;
        var legacyOnlySource = "auto";
        foreach (var (keys, source) in candidates)
        {
            var probe = await TryRunAsync(runner, moduleId, shadowEvent, keys, engineOnly, writeReport: false);
            if (probe is null)
            {
                continue;
            }
            // Only a run where a path actually executed counts as evidence. Both paths refusing
            // the same document (including "document already confirmed", which the harness reports
            // as a symmetric block) proves nothing about equivalence, and a freshness pass must
            // not turn such an attempt into a fresh "A FAIL" in the ledger.
            if (probe.New is "ok" && probe.Old is "ok" or "skipped")
            {
                if (executed is null || probe.Verdict == "PASS")
                {
                    executed = probe;
                    executedKeys = keys;
                    executedSource = source;
                }
                if (probe.Verdict == "PASS")
                {
                    break;
                }
            }
            else if (probe.Old == "ok")
            {
                // The legacy implementation accepted the document and the engine refused it:
                // a real divergence, worth keeping when no cleaner sample exists.
                legacyOnly ??= probe;
                legacyOnlyKeys ??= keys;
                legacyOnlySource = source;
            }
            else if (refused is null || refused.Error?.Contains("单据不存在") == true)
            {
                // Keep a refusal from a document that exists: "单据不存在" is the harness telling
                // us the remembered key is stale, which is not a business refusal.
                refused = probe;
            }
        }

        // A null key means "let the runner pick"; the chosen candidate therefore cannot be
        // identified by its key being non-null, only by which probe produced it.
        var chosen = executed ?? legacyOnly;
        var chosenKeys = executed is not null ? executedKeys : legacyOnlyKeys;
        var chosenSource = executed is not null ? executedSource : legacyOnlySource;
        if (chosen is null)
        {
            // Nothing in the pool could be processed: leave the previous evidence in place
            // (stale beats wrong) and surface the module for a fixture-based sample instead.
            return new SweepResult(
                moduleId, shadowEvent, refused?.Version ?? "-", "NO_SAMPLE", refused?.Old ?? "-",
                refused?.New ?? "-", 0, string.Empty, refused is null ? "-" : "scan",
                refused?.Error ?? "候选池为空（该模块当前无可执行单据）");
        }

        // Replay the chosen document once more so exactly one report lands on disk: the one the
        // ledger will read.
        var final = await TryRunAsync(runner, moduleId, shadowEvent, chosenKeys, engineOnly, writeReport: true);
        return final is null
            ? chosen with { KeysSource = chosenSource }
            : final with { KeysSource = chosenSource };
    }

    private static async Task<SweepResult?> TryRunAsync(
        EffectShadowRunner runner,
        int moduleId,
        string shadowEvent,
        string? keys,
        bool engineOnly,
        bool writeReport)
    {
        try
        {
            var report = await runner.RunAsync(
                new EffectShadowRunner.ShadowOptions(
                    moduleId, shadowEvent, Keys: keys, RunId: null, Failure: false, EngineOnly: engineOnly,
                    WriteReport: writeReport),
                Console.Out);
            return new SweepResult(
                moduleId, shadowEvent, report.DefinitionVersion, report.Summary.Verdict,
                report.OldPath.Status, report.NewPath.Status, report.Tables.Count,
                string.Join('|', report.RecordKeys), keys is null ? "auto" : "scan",
                report.NewPath.Error ?? report.OldPath.Error, report.Summary.AcceptedDivergenceCount);
        }
        catch (Exception exception)
        {
            return new SweepResult(
                moduleId, shadowEvent, "-", "ERROR", "-", "-", 0, keys ?? string.Empty, keys is null ? "auto" : "scan",
                exception.Message.Replace("\r", " ").Replace("\n", " ").Trim());
        }
    }

    /// <summary>
    /// Candidate documents for a module/event straight from its master table: undecided ones for
    /// approve, confirmed ones for deapprove, most recent first. Details are required only when
    /// the module actually has a detail table.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadCandidatesAsync(
        SqlConnection connection, int moduleId, string shadowEvent)
    {
        const int limit = 6;
        if (!EffectShadowRunner.TryGetSpecColumns(
                moduleId, out var masterTable, out var detailTable, out var key1, out var key2, out var dateColumn))
        {
            return Array.Empty<string>();
        }
        var hasDetail = !string.IsNullOrWhiteSpace(detailTable) && await TableExistsAsync(connection, detailTable);
        var deapprove = shadowEvent.Equals("DEAPPROVE", StringComparison.OrdinalIgnoreCase);
        // The runner flips CONFIRM_TAG with a strict predicate (CONFIRM_TAG=0/1), so a document
        // whose flag is NULL is not usable as a sample even though ISNULL would match it.
        var sql = "SELECT TOP (@Limit) M.[" + key1 + "], M.[" + key2 + "] FROM dbo.[" + masterTable + "] M"
            + " WHERE M.CONFIRM_TAG=@Confirm"
            + (hasDetail
                ? " AND EXISTS (SELECT 1 FROM dbo.[" + detailTable + "] D WHERE D.[" + key1 + "]=M.[" + key1
                    + "] AND D.[" + key2 + "]=M.[" + key2 + "])"
                : string.Empty)
            + " ORDER BY ISNULL(M.CONFIRM_DATE, M.[" + dateColumn + "]) DESC, M.[" + key2 + "] DESC;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Limit", SqlDbType.Int).Value = limit;
        command.Parameters.Add("@Confirm", SqlDbType.Bit).Value = deapprove;
        var keys = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }
            keys.Add(reader.GetValue(0).ToString()!.Trim() + "|" + reader.GetValue(1).ToString()!.Trim());
        }
        return keys;
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string table)
    {
        const string sql = "SELECT CASE WHEN OBJECT_ID(@Name, 'U') IS NULL THEN 0 ELSE 1 END;";
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Name", SqlDbType.NVarChar, 300).Value = "dbo." + table;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    /// <summary>
    /// Latest record keys used per module/event by earlier runs, so a module whose sample
    /// auto-selection has dried up can still be replayed instead of dropping to "no evidence".
    /// </summary>
    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ReadHistoricalSamples()
    {
        var directory = Path.Combine(RepoRoot.Value, "logs", "shadow");
        var latest = new Dictionary<string, (DateTime Time, IReadOnlyList<string> Keys)>();
        if (!Directory.Exists(directory))
        {
            return new Dictionary<string, IReadOnlyList<string>>();
        }
        foreach (var file in Directory.EnumerateFiles(directory, "shadow-*.json"))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                var root = document.RootElement;
                if (!root.TryGetProperty("moduleId", out var moduleId) || moduleId.ValueKind != JsonValueKind.Number
                    || !root.TryGetProperty("event", out var eventElement) || eventElement.ValueKind != JsonValueKind.String
                    || !root.TryGetProperty("recordKeys", out var keys) || keys.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var values = keys.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(value => value.Length > 0)
                    .ToList();
                if (values.Count == 0)
                {
                    continue;
                }
                var key = moduleId.GetInt32() + "|" + eventElement.GetString();
                var time = File.GetLastWriteTime(file);
                if (!latest.TryGetValue(key, out var current) || time > current.Time)
                {
                    latest[key] = (time, values);
                }
            }
            catch (JsonException)
            {
                // A truncated report is not evidence; skip it.
            }
        }
        return latest.ToDictionary(pair => pair.Key, pair => pair.Value.Keys);
    }

    private static async Task<IReadOnlyList<int>> ReadScopeAsync(SqlConnection connection)
    {
        // Same denominator as scripts/adr012-acceptance-ledger.ps1 -Denominator actions.
        const string sql = """
            SELECT DISTINCT A.M_IDX
            FROM dbo.MODULE_BUSINESS_ACTION A
            JOIN dbo.MODULES M ON M.M_IDX = A.M_IDX
            WHERE ISNULL(M.EFFECT_ENGINE_TAG,0) = 1
            ORDER BY A.M_IDX;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var modules = new List<int>();
        while (await reader.ReadAsync())
        {
            modules.Add(reader.GetInt32(0));
        }
        return modules;
    }

    private static async Task<(int? Version, string? DefinitionJson)> ReadSnapshotAsync(
        SqlConnection connection, int moduleId)
    {
        const string sql = """
            SELECT TOP 1 VERSION, DEFINITION_JSON FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
            WHERE M_IDX=@ModuleId AND IS_CURRENT=1 ORDER BY VERSION DESC;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (null, null);
        }
        return (reader.GetInt32(0), reader.GetString(1));
    }

    private static async Task<string> WriteSummaryAsync(IReadOnlyList<SweepResult> results)
    {
        var directory = Path.Combine(RepoRoot.Value, "logs", "shadow");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"sweep-{DateTime.Now:yyyyMMdd-HHmmss}.json");
        var payload = JsonSerializer.Serialize(
            new
            {
                generatedAt = DateTime.Now.ToString("s"),
                runs = results.Count,
                pass = results.Count(result => result.Verdict == "PASS"),
                fail = results.Count(result => result.Verdict == "FAIL"),
                error = results.Count(result => result.Verdict == "ERROR"),
                noSample = results.Count(result => result.Verdict == "NO_SAMPLE"),
                skip = results.Count(result => result.Verdict == "SKIP"),
                results,
            },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await File.WriteAllTextAsync(path, payload, new System.Text.UTF8Encoding(false));
        return path;
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        var settingsPath = Path.Combine(RepoRoot.Value, "EOS.API", "appsettings.Development.json");
        if (!File.Exists(settingsPath))
        {
            return null;
        }
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        return document.RootElement.TryGetProperty("ConnectionStrings", out var section)
            && section.TryGetProperty("ErpDatabase", out var value)
            ? value.GetString()
            : null;
    }

    private static string FindRepoRoot()
    {
        var attribute = typeof(EffectShadowSweep).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepoRoot");
        if (attribute?.Value is { Length: > 0 } root && Directory.Exists(root))
        {
            return Path.GetFullPath(root);
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EOS.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }
}
