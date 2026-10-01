using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>One parameter row as the settings page consumes it (already interpreted for display).</summary>
public sealed record SystemParameterItem(
    string Key,
    string? Value,
    string ValueType,
    string? DefaultValue,
    string GroupCode,
    string GroupLabel,
    string Description,
    string EffectScope,
    int SeqNo,
    string? Options,
    string? UpdatedBy,
    DateTimeOffset? UpdatedAt,
    bool IsReferenced)
{
    /// <summary>Value actually in force: the stored value, else the declared default.</summary>
    public string? EffectiveValue => Value ?? DefaultValue;

    /// <summary>True when the parameter is set by declaring a default rather than a stored value.</summary>
    public bool UsesDefault => Value is null;
}

/// <summary>Parameters of one page group (one tab).</summary>
public sealed record SystemParameterGroup(string GroupCode, string GroupLabel, IReadOnlyList<SystemParameterItem> Parameters);

/// <summary>Parameter list of one owner module, grouped for the settings page.</summary>
public sealed record SystemParameterList(int OwnerModule, string Scope, IReadOnlyList<SystemParameterGroup> Groups);

/// <summary>Save outcome: errors that rejected the whole submission, or the number of rows changed.</summary>
public sealed record SystemParameterSaveResult(int Updated, IReadOnlyList<string> Errors);

/// <summary>
/// The single read/write entry point of system parameters (table dbo.SYSSS, one row per parameter).
///
/// Rows are owned by the module that presents them (OWNER_MODULE): 110111 system parameters,
/// 180213 / 180662 attendance parameters. Reads and writes are always scoped by that owner, so the
/// same key may exist for different owners (the two attendance tables share their key names).
///
/// Value interpretation happens here and only here: a NULL value falls back to DEFAULT_VALUE, and a
/// still-missing value falls back to the caller's code default. Type conversion on save is
/// fail-closed: an unknown key, a foreign owner's key or a value that does not parse is rejected
/// before anything is written. Saves are audited (AUDIT_EVENT + AUDIT_FIELD_CHANGE) in the same
/// transaction as the row updates, so "who turned which switch off" is always answerable.
/// </summary>
public sealed class SystemParameterService(DbConnectionFactory connections, WorkbenchAuditWriter auditWriter)
{
    /// <summary>HTTP path segment / page scope → owner module (menu URLs and page routes are unchanged).</summary>
    private static readonly Dictionary<string, int> ScopeModules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["system"] = ModuleIds.SystemSettings,
        ["hr-setup"] = ModuleIds.HrSetup,
        ["hrm-setup"] = ModuleIds.HrmSetup,
    };

    /// <summary>
    /// Baseline table names still used as scope tokens inside stored configuration
    /// (<c>{"scope":"SYSSS","key":…}</c>, <c>{"table":"HR_SETUP","flagField":…}</c>): the names are
    /// kept so runtime configuration needs no change, but they resolve to the same owner modules.
    /// </summary>
    private static readonly Dictionary<string, int> ConfigScopes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SYSSS"] = ModuleIds.SystemSettings,
        ["HR_SETUP"] = ModuleIds.HrSetup,
        ["HRM_SETUP"] = ModuleIds.HrmSetup,
    };

    private static readonly Dictionary<int, string> ScopeTokens = ScopeModules
        .ToDictionary(entry => entry.Value, entry => entry.Key);

    private static readonly Dictionary<int, string> ConfigTokens = ConfigScopes
        .ToDictionary(entry => entry.Value, entry => entry.Key);

    /// <summary>
    /// Owner modules that are deliberately **not** reachable through the generic settings endpoint, but
    /// still want a readable audit scope token.
    ///
    /// <para>
    /// 3105 助手设置就在这里，且是有意为之：把它登记进 <see cref="ScopeModules"/> 会让助手参数
    /// 多出一条 <c>CanEdit</c> 就能写的通道，而该模块声明的要求是 <c>CanSetup</c>——那是一次降权。
    /// 于是只登记审计令牌，让留痕里写的是 <c>assistant</c> 而不是一个光秃秃的模块号。
    /// </para>
    /// </summary>
    private static readonly Dictionary<int, string> AuditOnlyScopeTokens = new()
    {
        [PermissionModules.AssistantAdmin.Settings] = "assistant",
    };

    /// <summary>Audit-facing scope token: registered page scopes first, then audit-only owners.</summary>
    private static string AuditScopeToken(int ownerModule) =>
        ScopeTokens.TryGetValue(ownerModule, out var token) ? token
        : AuditOnlyScopeTokens.TryGetValue(ownerModule, out var auditOnly) ? auditOnly
        : ownerModule.ToString(CultureInfo.InvariantCulture);

    /// <summary>Stored-configuration scope token of an owner module (the "<c>SYSSS</c>" form).</summary>
    public static string ConfigScopeToken(int ownerModule) =>
        ConfigTokens.TryGetValue(ownerModule, out var token) ? token : ownerModule.ToString(CultureInfo.InvariantCulture);

    /// <summary>Owner modules registered as parameter owners.</summary>
    public static IReadOnlyCollection<int> OwnerModules { get; } = ScopeModules.Values.Distinct().ToArray();

    /// <summary>
    /// Parameter keys that C# code reads directly, written as "<c>&lt;owner module&gt;|&lt;key&gt;</c>"
    /// because reads are always scoped to one owner: the two attendance tables share their key names
    /// and only one of them is actually read. Listed here so the settings page can distinguish
    /// "this parameter is read" from "this parameter has no reader at all" (the latter is still
    /// editable, it just does nothing yet); `check-system-params.ps1` asserts that every key it finds
    /// in code appears in this set, so a new direct read cannot leave the page mislabelling it.
    /// </summary>
    private static readonly HashSet<string> CodeReferencedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "110111|PRO_MRP",                    // MrpPlanAllocHandler / InventoryMoveHandler
        "110111|EXPIRY_ALERT_DAYS",          // ReportAggregateRegistry（临期清单的阈值参数）
        "180213|SAT_REST_DAY", "180213|SUN_REST_DAY",     // AttendanceCalcService
        "180213|WAGE_ADD", "180213|WAGE_WORK", "180213|WAGE_OVER", "180213|WAGE_REST",
        "180213|WAGE_HOLIDAY", "180213|WAGE_WORKTIME", "180213|WAGE_OVERTIME",
        "180213|WAGE_RESTTIME", "180213|WAGE_HOLITIME",   // HumanResourceJobsService
    };

    private static bool IsCodeReferenced(int ownerModule, string key) =>
        CodeReferencedKeys.Contains($"{ownerModule.ToString(CultureInfo.InvariantCulture)}|{key}");

    /// <summary>Owner module behind the stored-configuration scope token <c>SYSSS</c> (system switches).</summary>
    public static int SystemOwner => ModuleIds.SystemSettings;

    /// <summary>Owner module behind the stored-configuration scope token <c>HR_SETUP</c>.</summary>
    public static int AttendanceOwner => ModuleIds.HrSetup;

    /// <summary>Owner module behind the stored-configuration scope token <c>HRM_SETUP</c>.</summary>
    public static int AttendanceMonthlyOwner => ModuleIds.HrmSetup;

    // Key existence changes only through migrations, so the set is cached across requests.
    private static readonly ConcurrentDictionary<int, (DateTime LoadedAt, IReadOnlySet<string> Keys)> KeyCache = new();
    private static readonly TimeSpan KeyCacheLifetime = TimeSpan.FromMinutes(5);

    /// <summary>Resolves a page scope token to its owner module, or null when the scope is not registered.</summary>
    public static int? ModuleForScope(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        var token = scope.Trim();
        return ScopeModules.TryGetValue(token, out var module) ? module
            : ConfigScopes.TryGetValue(token, out module) ? module
            : null;
    }

    /// <summary>Page scope token of an owner module (round-trips <see cref="ModuleForScope"/>).</summary>
    public static string ScopeToken(int ownerModule) =>
        ScopeTokens.TryGetValue(ownerModule, out var token) ? token : ownerModule.ToString(CultureInfo.InvariantCulture);

    /// <summary>Drops the cached key set (used when a migration or test changes the parameter rows).</summary>
    public static void InvalidateKeyCache() => KeyCache.Clear();

    /// <summary>
    /// SQL predicate reading a switch by parameter key, for statements that must evaluate the
    /// parameter inside SQL (effect conditions and per-rule gates). Missing key or empty value
    /// reads as 0, keeping "switch absent = off" (fail-closed). The key travels as a parameter.
    /// </summary>
    public static string BoolSwitchSql(int ownerModule, string parameterName) =>
        $"""
        COALESCE((SELECT MAX(CAST(ISNULL(PARAM_VALUE, DEFAULT_VALUE) AS int)) FROM dbo.SYSSS WITH (NOLOCK)
                  WHERE OWNER_MODULE = {ownerModule.ToString(CultureInfo.InvariantCulture)} AND PARAM_KEY = {parameterName}), 0)
        """;

    /// <summary>Registered parameter keys of an owner module (cached).</summary>
    public async Task<IReadOnlySet<string>> GetKeysAsync(int ownerModule, CancellationToken token)
    {
        if (KeyCache.TryGetValue(ownerModule, out var cached) && DateTime.UtcNow - cached.LoadedAt < KeyCacheLifetime)
            return cached.Keys;

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var keys = await LoadKeysAsync(connection, null, ownerModule, token);
        KeyCache[ownerModule] = (DateTime.UtcNow, keys);
        return keys;
    }

    /// <summary>Key-set predicate for callers that must validate a key while compiling SQL.</summary>
    public async Task<Func<string, bool>> BuildKeyPredicateAsync(int ownerModule, CancellationToken token)
    {
        var keys = await GetKeysAsync(ownerModule, token);
        return key => keys.Contains(key);
    }

    /// <summary>Synchronous key check against the cached set; unknown while cold reads as absent (fail-closed).</summary>
    public static bool IsKnownKey(int ownerModule, string key) =>
        KeyCache.TryGetValue(ownerModule, out var cached) && cached.Keys.Contains(key);

    /// <summary>
    /// Loads the parameters of an owner module grouped for the settings page. Group order comes from
    /// GROUP_SEQ and within-group order from SEQ_NO — both are data, so adding or reordering groups
    /// needs no code change. Each item carries whether anything reads it today (configuration or
    /// code): a parameter without a reader is still editable, it just has no effect yet, and the page
    /// says so instead of letting an operator assume the switch is live.
    /// </summary>
    public async Task<SystemParameterList> ListAsync(int ownerModule, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var referenced = await LoadConfigReferencedKeysAsync(connection, ownerModule, token);
        var items = await LoadItemsCoreAsync(connection, ownerModule, token);

        var groups = new List<SystemParameterGroup>();
        var currentCode = string.Empty;
        var currentLabel = string.Empty;
        List<SystemParameterItem>? current = null;
        foreach (var item in items)
        {
            if (current is null || !string.Equals(item.GroupCode, currentCode, StringComparison.Ordinal))
            {
                if (current is not null) groups.Add(new SystemParameterGroup(currentCode, currentLabel, current));
                currentCode = item.GroupCode;
                currentLabel = item.GroupLabel;
                current = [];
            }

            current.Add(item with
            {
                IsReferenced = referenced.Contains(item.Key) || IsCodeReferenced(ownerModule, item.Key),
            });
        }

        if (current is not null) groups.Add(new SystemParameterGroup(currentCode, currentLabel, current));

        return new SystemParameterList(ownerModule, ScopeToken(ownerModule), groups);
    }

    /// <summary>
    /// Reads the raw parameter rows of one owner module without touching the request scope.
    ///
    /// <para>
    /// 存在的理由：助手运行期快照的构建者是**单例**（进程内只有一份，改动后重建），
    /// 而 <see cref="SystemParameterService"/> 依赖审计写入器、注册为请求作用域，单例注入不进来。
    /// 于是把"读参数行"这一段提成静态入口——**同一段 SQL**，避免为了绕开生命周期再造一份查询
    /// （两份 SQL 早晚会在"要不要 `WITH (NOLOCK)`"这类细节上分叉）。
    /// </para>
    /// </summary>
    public static async Task<IReadOnlyList<SystemParameterItem>> LoadItemsAsync(
        DbConnectionFactory connections, int ownerModule, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        return await LoadItemsCoreAsync(connection, ownerModule, token);
    }

    /// <summary>The one query behind both <see cref="ListAsync"/> and <see cref="LoadItemsAsync"/>.</summary>
    private static async Task<List<SystemParameterItem>> LoadItemsCoreAsync(
        SqlConnection connection, int ownerModule, CancellationToken token)
    {
        const string sql = """
            SELECT PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE, GROUP_CODE, GROUP_LABEL,
                   DESC_TEXT, EFFECT_SCOPE, SEQ_NO, OPTIONS, LAST_UPDATE_BY, LAST_UPDATE_DATE
            FROM dbo.SYSSS WITH (NOLOCK)
            WHERE OWNER_MODULE = @Owner
            ORDER BY GROUP_SEQ, SEQ_NO, PARAM_KEY;
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Owner", SqlDbType.Int).Value = ownerModule;
        await using var reader = await command.ExecuteReaderAsync(token);

        var items = new List<SystemParameterItem>();
        while (await reader.ReadAsync(token))
        {
            items.Add(new SystemParameterItem(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetInt32(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10).Trim(),
                reader.IsDBNull(11)
                    ? null
                    : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(11), DateTimeKind.Utc)),
                IsReferenced: false));
        }

        return items;
    }

    /// <summary>
    /// Keys the live configuration references: validation rules and effect actions name switches and
    /// settings as JSON string values, so a key counts as referenced when its quoted name appears in
    /// one of those structures. Published snapshots are derived from these two tables, which is why
    /// scanning them is enough for a page-level hint (the gate script scans snapshots as well, where
    /// it needs to catch drift rather than describe the present).
    /// </summary>
    private static async Task<ISet<string>> LoadConfigReferencedKeysAsync(
        SqlConnection connection, int ownerModule, CancellationToken token)
    {
        const string sql = """
            SELECT k.PARAM_KEY
            FROM (SELECT PARAM_KEY FROM dbo.SYSSS WITH (NOLOCK) WHERE OWNER_MODULE = @Owner) k
            WHERE EXISTS (SELECT 1 FROM dbo.MODULE_VALIDATION_RULE r WITH (NOLOCK)
                          WHERE CHARINDEX(N'"' + k.PARAM_KEY + N'"', CAST(r.PARAM_STRUCT AS nvarchar(max))) > 0)
               OR EXISTS (SELECT 1 FROM dbo.MODULE_BUSINESS_ACTION a WITH (NOLOCK)
                          WHERE CHARINDEX(N'"' + k.PARAM_KEY + N'"', CAST(a.CONDITION_STRUCT AS nvarchar(max))) > 0
                             OR CHARINDEX(N'"' + k.PARAM_KEY + N'"', CAST(a.PARAM_STRUCT AS nvarchar(max))) > 0
                             OR CHARINDEX(N'"' + k.PARAM_KEY + N'"', CAST(a.REVERSE_STRUCT AS nvarchar(max))) > 0);
            """;
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Owner", SqlDbType.Int).Value = ownerModule;
        await using var reader = await command.ExecuteReaderAsync(token);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token)) keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>
    /// Saves submitted parameters of one owner module. Keys outside the owner, unknown keys and
    /// values that do not match the declared type reject the whole submission; unchanged values are
    /// skipped so the audit log stays meaningful. Row updates, the audit trail and the
    /// quick-search side effect share one transaction.
    /// </summary>
    public async Task<SystemParameterSaveResult> SaveAsync(
        int ownerModule,
        IReadOnlyDictionary<string, string?> values,
        string user,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(values);
        var changed = new List<(string Key, string? Old, string? New)>();
        var errors = new List<string>();

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        string? quickSearchAll = null;

        await using (var read = new SqlCommand(
            "SELECT PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE FROM dbo.SYSSS WITH (UPDLOCK) WHERE OWNER_MODULE = @Owner;",
            connection))
        {
            read.Parameters.Add("@Owner", SqlDbType.Int).Value = ownerModule;
            await using var reader = await read.ExecuteReaderAsync(token);
            var known = new Dictionary<string, (string? Value, string Type)>(StringComparer.OrdinalIgnoreCase);
            while (await reader.ReadAsync(token))
                known[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2));

            foreach (var (rawKey, rawValue) in values)
            {
                var key = (rawKey ?? string.Empty).Trim();
                if (!known.TryGetValue(key, out var current))
                {
                    errors.Add($"参数 '{rawKey}' 不属于该设置模块。");
                    continue;
                }
                if (!TryNormalize(current.Type, rawValue, out var normalized, out var reason))
                {
                    errors.Add($"参数 '{key}' 的值不合法：{reason}");
                    continue;
                }
                if (string.Equals(current.Value, normalized, StringComparison.Ordinal)) continue;
                changed.Add((key, current.Value, normalized));
                if (string.Equals(key, "QUICK_SEARCH_ALL", StringComparison.OrdinalIgnoreCase))
                    quickSearchAll = normalized;
            }
        }

        if (errors.Count > 0) return new SystemParameterSaveResult(0, errors);
        if (changed.Count == 0) return new SystemParameterSaveResult(0, []);

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            var changes = new List<AuditFieldChange>(changed.Count);
            foreach (var (key, _, newValue) in changed)
            {
                await using var update = new SqlCommand(
                    """
                    UPDATE dbo.SYSSS
                    SET PARAM_VALUE = @Value, LAST_UPDATE_BY = @User, LAST_UPDATE_DATE = GETDATE()
                    WHERE OWNER_MODULE = @Owner AND PARAM_KEY = @Key;
                    """, connection, transaction);
                update.Parameters.Add("@Value", SqlDbType.NVarChar, 4000).Value = (object?)newValue ?? DBNull.Value;
                update.Parameters.Add("@User", SqlDbType.NChar, 10).Value = Truncate(user, 10);
                update.Parameters.Add("@Owner", SqlDbType.Int).Value = ownerModule;
                update.Parameters.Add("@Key", SqlDbType.NVarChar, 64).Value = key;
                await update.ExecuteNonQueryAsync(token);
            }

            foreach (var (key, oldValue, newValue) in changed)
                changes.Add(new AuditFieldChange(key, oldValue, newValue, null));

            await auditWriter.WriteEventAsync(
                connection,
                transaction,
                ownerModule,
                AuditScopeToken(ownerModule),
                "PARAM_SAVE",
                $"保存系统参数 {changed.Count} 项：{string.Join('、', changed.Select(item => item.Key))}",
                Truncate(user, 20),
                "SYSTEM_PARAM",
                result: 1,
                changes,
                token);

            // Disabling "quick search all fields" drops the stored all-fields condition so it no
            // longer applies to queries.
            if (quickSearchAll is not null && !IsTruthy(quickSearchAll))
            {
                await using var sideEffect = new SqlCommand(
                    "DELETE FROM dbo.SYSQQ WHERE F_ID = @FieldId;", connection, transaction);
                sideEffect.Parameters.Add("@FieldId", SqlDbType.NVarChar, 100).Value = "all";
                await sideEffect.ExecuteNonQueryAsync(token);
            }

            await transaction.CommitAsync(token);
            return new SystemParameterSaveResult(changed.Count, []);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>
    /// Qualified parameter keys in the "<c>&lt;config scope&gt;.&lt;KEY&gt;</c>" form
    /// (e.g. <c>SYSSS.SEND_TAG</c>, <c>HR_SETUP.REQUIRE_ENACTMENT</c>). Configuration validators
    /// check every referenced table/column against one whitelist; folding the parameter keys into
    /// that same whitelist keeps "the switch exists" and "the column exists" a single judgement.
    /// </summary>
    public static async Task<ISet<string>> LoadQualifiedKeysAsync(
        SqlConnection connection, SqlTransaction? transaction, CancellationToken token)
    {
        const string sql = "SELECT OWNER_MODULE, PARAM_KEY FROM dbo.SYSSS WITH (NOLOCK);";
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var wasOpen = connection.State == ConnectionState.Open;
        if (!wasOpen) await connection.OpenAsync(token);
        await using (var command = new SqlCommand(sql, connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var scope = ConfigScopeToken(reader.GetInt32(0));
                result.Add(scope + "." + reader.GetString(1));
            }
        }
        if (!wasOpen) await connection.CloseAsync();
        return result;
    }

    /// <summary>Reads one parameter as text; null means the key is absent or has no value and no default.</summary>
    public static async Task<string?> GetStringAsync(
        SqlConnection connection, SqlTransaction? transaction, int ownerModule, string key, CancellationToken token)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP 1 ISNULL(PARAM_VALUE, DEFAULT_VALUE) FROM dbo.SYSSS WITH (NOLOCK)
            WHERE OWNER_MODULE = @Owner AND PARAM_KEY = @Key;
            """, connection, transaction);
        command.Parameters.Add("@Owner", SqlDbType.Int).Value = ownerModule;
        command.Parameters.Add("@Key", SqlDbType.NVarChar, 64).Value = key;
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads one parameter as a switch; absent or unparsable values read as <paramref name="fallback"/>.</summary>
    public static async Task<bool> GetBoolAsync(
        SqlConnection connection, SqlTransaction? transaction, int ownerModule, string key, bool fallback, CancellationToken token)
    {
        var raw = await GetStringAsync(connection, transaction, ownerModule, key, token);
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        return IsTruthy(raw);
    }

    /// <summary>Reads one parameter as an integer; absent or unparsable values read as <paramref name="fallback"/>.</summary>
    public static async Task<int?> GetIntAsync(
        SqlConnection connection, SqlTransaction? transaction, int ownerModule, string key, CancellationToken token)
    {
        var raw = await GetStringAsync(connection, transaction, ownerModule, key, token);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>Reads one parameter on its own connection.</summary>
    public async Task<bool> GetBoolAsync(int ownerModule, string key, bool fallback, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        return await GetBoolAsync(connection, null, ownerModule, key, fallback, token);
    }

    /// <summary>Reads one parameter on its own connection.</summary>
    public async Task<int?> GetIntAsync(int ownerModule, string key, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        return await GetIntAsync(connection, null, ownerModule, key, token);
    }

    /// <summary>Reads one parameter on its own connection.</summary>
    public async Task<string?> GetStringAsync(int ownerModule, string key, CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        return await GetStringAsync(connection, null, ownerModule, key, token);
    }

    private static async Task<IReadOnlySet<string>> LoadKeysAsync(
        SqlConnection connection, SqlTransaction? transaction, int ownerModule, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT PARAM_KEY FROM dbo.SYSSS WITH (NOLOCK) WHERE OWNER_MODULE = @Owner;", connection, transaction);
        command.Parameters.Add("@Owner", SqlDbType.Int).Value = ownerModule;
        await using var reader = await command.ExecuteReaderAsync(token);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(token)) keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>Normalizes a submitted value to its stored text form; unparsable input is rejected.</summary>
    private static bool TryNormalize(string valueType, string? raw, out string? normalized, out string reason)
    {
        var type = valueType.ToLowerInvariant();
        var text = raw?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            normalized = null;
            reason = string.Empty;
            return true;
        }
        switch (type)
        {
            case "bit":
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1" || text == "是")
                {
                    normalized = "1";
                    reason = string.Empty;
                    return true;
                }
                if (text.Equals("false", StringComparison.OrdinalIgnoreCase) || text == "0" || text == "否")
                {
                    normalized = "0";
                    reason = string.Empty;
                    return true;
                }
                normalized = null;
                reason = "应为开关值（是/否）";
                return false;
            case "int":
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                {
                    normalized = number.ToString(CultureInfo.InvariantCulture);
                    reason = string.Empty;
                    return true;
                }
                normalized = null;
                reason = "应为整数";
                return false;
            case "decimal":
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                {
                    normalized = amount.ToString(CultureInfo.InvariantCulture);
                    reason = string.Empty;
                    return true;
                }
                normalized = null;
                reason = "应为数值";
                return false;
            default:
                if (text.Length > 4000)
                {
                    normalized = null;
                    reason = "长度超出 4000 字符";
                    return false;
                }
                normalized = text;
                reason = string.Empty;
                return true;
        }
    }

    private static bool IsTruthy(string? raw) =>
        raw is not null
        && (raw.Equals("true", StringComparison.OrdinalIgnoreCase) || raw == "1" || raw == "是");

    private static string Truncate(string? value, int length)
    {
        var text = value ?? string.Empty;
        return text.Length <= length ? text : text[..length];
    }
}
