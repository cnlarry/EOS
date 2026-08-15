using System.Data;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>受控表达式类型。</summary>
public enum RestrictedExpressionKind
{
    VirtualExp,
    ConvertFunction,
    DataSourceSql,
}

public sealed record ExpressionValidationResult(
    bool Ok,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Hints,
    int WhiteListVersion);

public sealed record ExpressionPreviewResult(
    bool Ok,
    IReadOnlyList<string> Errors,
    IReadOnlyList<Dictionary<string, object?>> Rows,
    string? Sql,
    long ElapsedMs);

public sealed record ExpressionStaleEntry(string Kind, string Table, string Field, string Expression, IReadOnlyList<string> Errors);
public sealed record ExpressionRescanResult(int WhiteListVersion, int Total, IReadOnlyList<ExpressionStaleEntry> Stale);

public sealed record ExpressionOverview(
    int WhiteListVersion,
    int TotalVisible,
    int Hidden,
    int VirtualExp,
    int ConvertFunction,
    int DataSourceSql,
    int Stale,
    IReadOnlyList<ExpressionStaleEntry> StaleItems);

/// <summary>
/// 受控表达式解析工作流（P1/P2，2026-08-15，设计见 docs/plans/受控表达式工作流.md）：
/// VIRTUAL_EXP / CONVERT_FUNCTION / DATASOURCE_SQL 三套受限语言的服务端校验、只读预览与发布审计。
/// - 校验：语法解析 → 表/列物理存在 → 白名单命中，任何失败不进入运行时；
/// - 发布：事务内写 FIELDS + SYSDF 审计（TYPE=EXPR_PUBLISH），幂等，乐观锁；
/// - 白名单版本：常量版本号，随解析器/注册表变更递增（P3 将版本化入库）。
/// </summary>
public sealed class RestrictedExpressionService(
    DbConnectionFactory connections,
    ILogger<RestrictedExpressionService> logger)
{
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);
    private static readonly Regex QuotedString = new("^'(?:[^']|'')*'$", RegexOptions.Compiled);
    private static readonly Regex NumericLiteral = new("^[+-]?\\d+(\\.\\d+)?$", RegexOptions.Compiled);

    /// <summary>白名单版本：解析器语法或注册表变更时递增（P3 将入库版本化）。</summary>
    public const int WhiteListVersion = 1;

    /// <summary>
    /// CONVERT_FUNCTION 受控函数注册表：值 = 注册表内函数名，参数约定为该字段当前值。
    /// 新增函数必须先登记（含参数/返回语义）再可被引用。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> ConvertFunctionRegistry =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["f_get_table_kind_desc"] = "T_KIND 代码 → 中文描述（参数 = 字段值）",
            ["f_get_emp_name_by_id"] = "员工编号 → 姓名（参数 = 字段值）",
            ["f_get_approve_state_desc"] = "批核状态 → 中文描述（参数 = 字段值）",
        };

    public async Task<ExpressionValidationResult> ValidateAsync(
        RestrictedExpressionKind kind,
        string table,
        string field,
        string? expression,
        CancellationToken token)
    {
        var version = await ReadWhiteListVersionAsync(token);
        var errors = new List<string>();
        var hints = new List<string>();
        var value = (expression ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(value))
        {
            errors.Add("表达式不能为空（清空请使用发布空值语义，或直接保留原值）。");
            return new(false, errors, hints, version);
        }
        if (value.Length > 2000)
        {
            errors.Add("表达式长度超出限制（最多 2000 字符）。");
            return new(false, errors, hints, version);
        }

        switch (kind)
        {
            case RestrictedExpressionKind.VirtualExp:
                await ValidateVirtualExpAsync(table, value, errors, hints, token);
                break;
            case RestrictedExpressionKind.ConvertFunction:
                if (ValidateConvertFunction(value) is { } convertError) errors.Add(convertError);
                break;
            case RestrictedExpressionKind.DataSourceSql:
                await ValidateDataSourceSqlAsync(table, value, errors, hints, token);
                break;
            default:
                errors.Add("未知表达式类型。");
                break;
        }
        return new(errors.Count == 0, errors, hints, version);
    }

    public async Task<ExpressionPreviewResult> PreviewAsync(
        RestrictedExpressionKind kind,
        string table,
        string field,
        string? expression,
        CancellationToken token)
    {
        var validation = await ValidateAsync(kind, table, field, expression, token);
        if (!validation.Ok)
            return new(false, validation.Errors, [], null, 0);
        var value = (expression ?? string.Empty).Trim();
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        string? generatedSql = null;
        try
        {
            var (sql, parameters) = kind switch
            {
                RestrictedExpressionKind.VirtualExp => BuildVirtualPreviewSql(connection, table, field, value, token).GetAwaiter().GetResult(),
                RestrictedExpressionKind.ConvertFunction => BuildConvertPreviewSql(table, field, value),
                RestrictedExpressionKind.DataSourceSql => BuildDataSourcePreviewSql(value),
                _ => throw new InvalidOperationException("未知表达式类型。"),
            };
            generatedSql = sql;
            await using var command = new SqlCommand(sql, connection);
            foreach (var (name, paramValue) in parameters)
                command.Parameters.AddWithValue(name, paramValue);
            await using var reader = await command.ExecuteReaderAsync(token);
            var rows = new List<Dictionary<string, object?>>();
            while (await reader.ReadAsync(token))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
                if (rows.Count >= 20) break;
            }
            stopwatch.Stop();
            return new(true, [], rows, sql, stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "受控表达式预览失败 kind={Kind} table={Table} field={Field}", kind, table, field);
            return new(false, [$"预览执行失败：{ex.Message}（SQL：{generatedSql}）"], [], null, stopwatch.ElapsedMilliseconds);
        }
    }

    /// <summary>
    /// 发布受控表达式：事务内更新 FIELDS + SYSDF 审计；乐观锁（original 不匹配返回 false）。
    /// 幂等：同值重复发布为 no-op（不产生冗余审计）。
    /// </summary>
    public async Task<PublishExpressionOutcome> PublishAsync(
        RestrictedExpressionKind kind,
        string table,
        string field,
        string? expression,
        string? original,
        string employeeName,
        string userId,
        CancellationToken token)
    {
        var value = (expression ?? string.Empty).Trim();
        var version = await ReadWhiteListVersionAsync(token);
        var validation = await ValidateAsync(kind, table, field, value, token);
        if (!validation.Ok)
            return new(PublishExpressionStatus.Invalid, validation.Errors);
        var column = kind switch
        {
            RestrictedExpressionKind.VirtualExp => "VIRTUAL_EXP",
            RestrictedExpressionKind.ConvertFunction => "CONVERT_FUNCTION",
            RestrictedExpressionKind.DataSourceSql => "DATASOURCE_SQL",
            _ => throw new InvalidOperationException("未知表达式类型。"),
        };
        if (!Identifier.IsMatch(table) || !Identifier.IsMatch(field))
            return new(PublishExpressionStatus.Invalid, ["表名或字段名无效。"]);

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            string? current;
            await using (var read = new SqlCommand(
                $"SELECT LTRIM(RTRIM(ISNULL({column},''))) FROM dbo.FIELDS WITH (NOLOCK) WHERE LTRIM(RTRIM(T_ID))=@Table AND LTRIM(RTRIM(F_ID))=@Field;",
                connection, transaction))
            {
                read.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
                read.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = field.Trim();
                current = (await read.ExecuteScalarAsync(token) as string)?.Trim();
            }
            if (current is null)
                return new(PublishExpressionStatus.NotFound, ["字段元数据不存在。"]);
            if (!string.Equals(current, (original ?? string.Empty).Trim(), StringComparison.Ordinal))
                return new(PublishExpressionStatus.ConcurrentModified, ["字段内容已被他人修改，请刷新后重试。"]);
            if (string.Equals(current, value, StringComparison.Ordinal))
                return new(PublishExpressionStatus.NoChange, []);

            await using (var update = new SqlCommand(
                $"UPDATE dbo.FIELDS SET {column}=@Value,LAST_UPDATE_BY=@By,LAST_UPDATE_DATE=GETDATE() WHERE LTRIM(RTRIM(T_ID))=@Table AND LTRIM(RTRIM(F_ID))=@Field;",
                connection, transaction))
            {
                update.Parameters.AddWithValue("@Value", string.IsNullOrEmpty(value) ? DBNull.Value : value);
                update.Parameters.AddWithValue("@By", employeeName);
                update.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
                update.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = field.Trim();
                await update.ExecuteNonQueryAsync(token);
            }
            await using (var audit = new SqlCommand(
                "INSERT INTO dbo.SYSDF (M_IDX,RECORD_IDX,CONTENT,TYPE,EXEC_BY,EXEC_DATE,OPERFLAG) VALUES (2302,@Record,@Content,'EXPR_PUBLISH',@By,GETDATE(),1);",
                connection, transaction))
            {
                audit.Parameters.AddWithValue("@Record", $"{table.Trim()}.{field.Trim()}");
                audit.Parameters.AddWithValue("@Content",
                    $"[{kind}] {column} {table.Trim()}.{field.Trim()}：{(string.IsNullOrEmpty(current) ? "(空)" : current)} → {(string.IsNullOrEmpty(value) ? "(空)" : value)}（白名单 v{version}，发布人 {employeeName}）");
                audit.Parameters.AddWithValue("@By", userId);
                await audit.ExecuteNonQueryAsync(token);
            }
            await transaction.CommitAsync(token);
            logger.LogInformation("受控表达式发布 kind={Kind} table={Table} field={Field} by={User}", kind, table, field, userId);
            return new(PublishExpressionStatus.Published, []);
        }
        catch
        {
            await transaction.RollbackAsync(token);
            throw;
        }
    }

    /// <summary>对全部已发布表达式按当前白名单版本重校验（P3：版本升级后标记需复核项）。</summary>
    public async Task<ExpressionRescanResult> RescanAsync(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var version = await ReadWhiteListVersionAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(T_ID)),LTRIM(RTRIM(F_ID)),
                   LTRIM(RTRIM(ISNULL(VIRTUAL_EXP,''))),LTRIM(RTRIM(ISNULL(CONVERT_FUNCTION,''))),LTRIM(RTRIM(ISNULL(DATASOURCE_SQL,'')))
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE (LTRIM(RTRIM(ISNULL(VIRTUAL_EXP,'')))<>''
                OR LTRIM(RTRIM(ISNULL(CONVERT_FUNCTION,'')))<>''
                OR LTRIM(RTRIM(ISNULL(DATASOURCE_SQL,'')))<>'')
              AND COALESCE(IS_VISIBLE,1)=1;
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        var stale = new List<ExpressionStaleEntry>();
        var total = 0;
        while (await reader.ReadAsync(token))
        {
            var table = reader.GetString(0);
            var field = reader.GetString(1);
            var virtualExp = reader.GetString(2);
            var convertFunction = reader.GetString(3);
            var dataSourceSql = reader.GetString(4);
            foreach (var (kind, value) in new[]
            {
                (RestrictedExpressionKind.VirtualExp, virtualExp),
                (RestrictedExpressionKind.ConvertFunction, convertFunction),
                (RestrictedExpressionKind.DataSourceSql, dataSourceSql),
            })
            {
                if (string.IsNullOrEmpty(value)) continue;
                total++;
                var validation = await ValidateAsync(kind, table, field, value, token);
                if (!validation.Ok)
                    stale.Add(new ExpressionStaleEntry(kind.ToString(), table, field, value, validation.Errors));
            }
        }
        return new ExpressionRescanResult(version, total, stale);
    }

    /// <summary>表达式审计总览：版本 + 可见/隐藏/分类计数 + 重校验结果（供 2302 表达式审计面板与顾问报表）。</summary>
    public async Task<ExpressionOverview> OverviewAsync(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var version = await ReadWhiteListVersionAsync(token);
        var virtualExp = 0;
        var convertFunction = 0;
        var dataSourceSql = 0;
        var visible = 0;
        var hidden = 0;
        const string sql = """
            SELECT CAST(COALESCE(IS_VISIBLE,1) AS bit),
                   CAST(CASE WHEN LTRIM(RTRIM(ISNULL(VIRTUAL_EXP,'')))<>'' THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN LTRIM(RTRIM(ISNULL(CONVERT_FUNCTION,'')))<>'' THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN LTRIM(RTRIM(ISNULL(DATASOURCE_SQL,'')))<>'' THEN 1 ELSE 0 END AS bit)
            FROM dbo.FIELDS WITH (NOLOCK);
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var isVisible = reader.GetBoolean(0);
            var hasVirtual = reader.GetBoolean(1);
            var hasConvert = reader.GetBoolean(2);
            var hasDataSource = reader.GetBoolean(3);
            if (hasVirtual) virtualExp++;
            if (hasConvert) convertFunction++;
            if (hasDataSource) dataSourceSql++;
            if (isVisible) visible++;
            else hidden++;
        }
        var rescan = await RescanAsync(token);
        return new ExpressionOverview(version, visible, hidden, virtualExp, convertFunction, dataSourceSql, rescan.Stale.Count, rescan.Stale);
    }

    private async Task<int> ReadWhiteListVersionAsync(CancellationToken token)
    {
        try
        {
            await using var connection = connections.Create();
            await connection.OpenAsync(token);
            await using var command = new SqlCommand(
                "SELECT MAX(VERSION) FROM dbo.EXPRESSION_WHITELIST_VERSION WITH (NOLOCK);", connection);
            return Convert.ToInt32(await command.ExecuteScalarAsync(token) ?? WhiteListVersion);
        }
        catch
        {
            return WhiteListVersion;
        }
    }

    private async Task ValidateVirtualExpAsync(string table, string expression, List<string> errors, List<string> hints, CancellationToken token)
    {
        IReadOnlyList<VirtualArithmeticToken>? arithmeticTokens = null;
        var simple = VirtualExpressionParser.TryParseExpression(expression, out var refTable, out var refColumn);
        if (!simple)
        {
            // P5：受控算术/常量子集（列引用 + 数值 + 四则运算 + 括号；字符串常量独立使用）
            if (!VirtualArithmeticParser.TryParse(expression, out arithmeticTokens, out var parseError))
            {
                errors.Add($"虚拟表达式不支持：{parseError}");
                return;
            }
        }
        if (!Identifier.IsMatch(table))
        {
            errors.Add("基表名无效。");
            return;
        }
        var refs = new List<(string Table, string Column)>();
        if (simple)
            refs.Add((refTable, refColumn));
        else
            foreach (var item in arithmeticTokens!.Where(item => item.Kind == "Ref"))
                refs.Add((item.Table ?? table, item.Column!));
        var referencedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasCrossTable = refs.Any(item => !item.Table.Equals(table, StringComparison.OrdinalIgnoreCase));
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        IReadOnlyList<VirtualJoin> joins = [];
        if (hasCrossTable)
        {
            string? relation;
            await using (var relationCommand = new SqlCommand(
                "SELECT LTRIM(RTRIM(ISNULL(QUERY_RELATION,''))) FROM dbo.TABLES WITH (NOLOCK) WHERE LTRIM(RTRIM(T_ID))=@Table;",
                connection))
            {
                relationCommand.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
                relation = await relationCommand.ExecuteScalarAsync(token) as string;
            }
            if (string.IsNullOrWhiteSpace(relation))
            {
                errors.Add($"表 {table} 未配置 QUERY_RELATION，无法解析跨表引用。");
                return;
            }
            if (!VirtualExpressionParser.TryParseRelation(relation, table, out joins, out var relationError))
            {
                errors.Add($"QUERY_RELATION 解析失败：{relationError}");
                return;
            }
        }
        foreach (var (refTableName, refColumnName) in refs)
        {
            referencedTables.Add(refTableName);
            string targetTable;
            if (refTableName.Equals(table, StringComparison.OrdinalIgnoreCase))
            {
                targetTable = table;
            }
            else
            {
                var join = joins.FirstOrDefault(candidate => candidate.Table.Equals(refTableName, StringComparison.OrdinalIgnoreCase)
                    || candidate.Alias.Equals(refTableName, StringComparison.OrdinalIgnoreCase));
                if (join is null)
                {
                    errors.Add($"表 {refTableName} 不在 {table} 的 QUERY_RELATION 白名单内。");
                    continue;
                }
                targetTable = join.Table;
            }
            if (!await ColumnExistsAsync(connection, targetTable, refColumnName, token))
                errors.Add($"列 {refTableName}.{refColumnName} 在物理表中不存在。");
        }
        if (errors.Count == 0)
            hints.Add($"引用：{(referencedTables.Count == 0 ? "(常量)" : string.Join(',', referencedTables))}（白名单 v{WhiteListVersion}）");
    }

    internal static string? ValidateConvertFunction(string expression)
    {
        expression = expression.Trim();
        if (!Identifier.IsMatch(expression))
            return "转换函数必须是受控注册表内的函数名（如 f_get_emp_name_by_id）。";
        if (!ConvertFunctionRegistry.ContainsKey(expression))
            return $"函数 {expression} 不在受控注册表内；新增函数须先登记注册表。";
        return null;
    }

    private async Task ValidateDataSourceSqlAsync(string table, string expression, List<string> errors, List<string> hints, CancellationToken token)
    {
        if (!TryParseDataSourceSql(expression, out var parsed, out var parseError))
        {
            errors.Add(parseError);
            return;
        }
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        if (parsed.Table is not null)
        {
            if (!await TableExistsAsync(connection, parsed.Table, token))
            {
                errors.Add($"表 {parsed.Table} 不存在。");
                return;
            }
            foreach (var column in parsed.Columns)
            {
                if (!await ColumnExistsAsync(connection, parsed.Table, column, token))
                    errors.Add($"列 {parsed.Table}.{column} 在物理表中不存在。");
            }
            if (parsed.WhereColumn is not null && !await ColumnExistsAsync(connection, parsed.Table, parsed.WhereColumn, token))
                errors.Add($"WHERE 列 {parsed.Table}.{parsed.WhereColumn} 在物理表中不存在。");
            if (parsed.OrderColumn is not null && !await ColumnExistsAsync(connection, parsed.Table, parsed.OrderColumn, token))
                errors.Add($"ORDER BY 列 {parsed.Table}.{parsed.OrderColumn} 在物理表中不存在。");
        }
        if (errors.Count == 0 && parsed.Table is not null)
            hints.Add($"数据源：{parsed.Table}（{string.Join(',', parsed.Columns)}）");
    }

    private (string Sql, List<(string Name, object? Value)> Parameters) BuildConvertPreviewSql(string table, string field, string function)
    {
        // 函数白名单 + 物理列（已校验），参数 = 字段当前值；只读 SELECT TOP 20
        return ($"SELECT TOP 20 dbo.[{function}]([{field}]) AS [{field}] FROM dbo.[{table}] WITH (NOLOCK);", []);
    }

    private (string Sql, List<(string Name, object? Value)> Parameters) BuildDataSourcePreviewSql(string expression)
    {
        // 受限 SELECT（解析器已校验），套 TOP 20 只读执行
        return ($"SELECT TOP 20 * FROM ({expression}) AS [__preview];", []);
    }

    private async Task<(string Sql, List<(string Name, object? Value)> Parameters)> BuildVirtualPreviewSql(
        SqlConnection connection, string table, string field, string expression, CancellationToken token)
    {
        IReadOnlyList<VirtualArithmeticToken>? arithmeticTokens = null;
        string? fragment;
        bool hasCrossTable;
        bool hasBaseRef;
        if (VirtualExpressionParser.TryParseExpression(expression, out var refTable, out var refColumn))
        {
            fragment = $"[{refTable}].[{refColumn}]";
            hasCrossTable = !refTable.Equals(table, StringComparison.OrdinalIgnoreCase);
            hasBaseRef = refTable.Equals(table, StringComparison.OrdinalIgnoreCase);
        }
        else if (VirtualArithmeticParser.TryParse(expression, out arithmeticTokens, out _))
        {
            fragment = RenderArithmeticFragment(arithmeticTokens, table);
            hasCrossTable = arithmeticTokens.Any(item => item.Kind == "Ref" && item.Table is not null && !item.Table.Equals(table, StringComparison.OrdinalIgnoreCase));
            hasBaseRef = arithmeticTokens.Any(item => item.Kind == "Ref" && (item.Table is null || item.Table.Equals(table, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            fragment = null;
            hasCrossTable = false;
            hasBaseRef = false;
        }
        if (fragment is null)
            return ("SELECT TOP 0 NULL;", []);
        string fromClause;
        if (hasCrossTable)
        {
            string? relation;
            await using (var relationCommand = new SqlCommand(
                "SELECT LTRIM(RTRIM(ISNULL(QUERY_RELATION,''))) FROM dbo.TABLES WITH (NOLOCK) WHERE LTRIM(RTRIM(T_ID))=@Table;",
                connection))
            {
                relationCommand.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
                relation = await relationCommand.ExecuteScalarAsync(token) as string;
            }
            fromClause = string.IsNullOrWhiteSpace(relation) ? $"dbo.[{table}] WITH (NOLOCK)" : relation;
        }
        else
        {
            // 常量（无引用）不需要 FROM；基表引用用基表
            fromClause = hasBaseRef ? $"dbo.[{table}] WITH (NOLOCK)" : string.Empty;
        }
        // QUERY_RELATION 本身即受控 LEFT JOIN（解析器已校验），直接作为预览 FROM；
        // 引用列按 别名.列 限定，避免 JOIN 同名歧义
        return ($"SELECT TOP 20 {fragment} AS [{field}] {(fromClause.Length == 0 ? string.Empty : $"FROM {fromClause}")};", []);
    }

    private static string RenderArithmeticFragment(IReadOnlyList<VirtualArithmeticToken> tokens, string baseTable)
    {
        var parts = new List<string>();
        foreach (var token in tokens)
        {
            if (token.Kind == "Ref")
                parts.Add($"[{token.Table ?? baseTable}].[{token.Column}]");
            else if (token.Kind == "String")
                parts.Add($"N'{token.Text.Replace("'", "''")}'");
            else
                parts.Add(token.Text);
        }
        return string.Join(' ', parts);
    }

    internal static bool TryParseDataSourceSql(
        string expression,
        out DataSourceSql parsed,
        out string error)
    {
        parsed = null!;
        error = "";
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            error = "数据源 SQL 必须以 SELECT 开头。";
            return false;
        }
        if (trimmed.IndexOf(';') >= 0 || trimmed.IndexOf("--", StringComparison.Ordinal) >= 0
            || trimmed.IndexOf("/*", StringComparison.Ordinal) >= 0)
        {
            error = "数据源 SQL 不允许分号或注释。";
            return false;
        }
        // 纯字面量 UNION 例外（如 T_KIND：SELECT 'P' AS col,'主表' AS col2 UNION …，无表访问）
        if (trimmed.IndexOf("UNION", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            if (IsLiteralUnionOnly(trimmed))
            {
                parsed = new DataSourceSql(null, [], null, null, trimmed);
                return true;
            }
            error = "仅允许纯字面量 UNION（SELECT 常量 AS 列 …），不允许 UNION 访问表。";
            return false;
        }
        var fromIndex = IndexOfKeyword(trimmed, "FROM");
        if (fromIndex < 0)
        {
            error = "数据源 SQL 缺少 FROM 子句。";
            return false;
        }
        var selectPart = trimmed[..fromIndex].Trim();
        var rest = trimmed[(fromIndex + 4)..].Trim();
        var columns = selectPart["SELECT".Length..]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(column => column.Trim('[', ']', ' '))
            .ToList();
        if (columns.Count == 0 || columns.Count > 20 || columns.Any(column => !Identifier.IsMatch(column)))
        {
            error = "SELECT 列清单无效（每列须为简单标识符，最多 20 列）。";
            return false;
        }
        string? whereColumn = null;
        string? whereValue = null;
        string? orderColumn = null;
        string? orderDirection = null;
        var whereIndex = IndexOfKeyword(rest, "WHERE");
        var orderIndex = IndexOfKeyword(rest, "ORDER BY");
        if (whereIndex >= 0 && orderIndex >= 0 && orderIndex < whereIndex)
        {
            error = "ORDER BY 必须位于 WHERE 之后。";
            return false;
        }
        var tableEnd = rest.Length;
        if (whereIndex >= 0) tableEnd = Math.Min(tableEnd, whereIndex);
        if (orderIndex >= 0) tableEnd = Math.Min(tableEnd, orderIndex);
        var tableClause = rest[..tableEnd].Trim();
        if (whereIndex >= 0)
        {
            var whereEnd = orderIndex > whereIndex ? orderIndex : rest.Length;
            var wherePart = rest[(whereIndex + 5)..whereEnd].Trim();
            var match = Regex.Match(wherePart, @"^([A-Za-z_][A-Za-z0-9_]{0,127})\s*=\s*(.+)$");
            if (!match.Success)
            {
                error = "WHERE 仅支持「列 = 常量」，常量须为带引号字符串或数值字面量。";
                return false;
            }
            whereColumn = match.Groups[1].Value;
            var literal = match.Groups[2].Value.Trim();
            if (!QuotedString.IsMatch(literal) && !NumericLiteral.IsMatch(literal))
            {
                error = "WHERE 仅支持「列 = 常量」，常量须为带引号字符串或数值字面量。";
                return false;
            }
            whereValue = literal;
        }
        if (orderIndex >= 0)
        {
            var orderPart = rest[(orderIndex + 9)..].Trim();
            var orderTokens = orderPart.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (orderTokens.Length is < 1 or > 2)
            {
                error = "ORDER BY 仅支持单列 [ASC|DESC]。";
                return false;
            }
            orderColumn = orderTokens[0].Trim('[', ']');
            if (!Identifier.IsMatch(orderColumn))
            {
                error = "ORDER BY 列名无效。";
                return false;
            }
            orderDirection = orderTokens.Length == 2 ? orderTokens[1].ToUpperInvariant() : "ASC";
            if (orderDirection is not ("ASC" or "DESC"))
            {
                error = "ORDER BY 方向仅支持 ASC/DESC。";
                return false;
            }
        }
        var tableName = tableClause.Trim('[', ']', ' ');
        if (!Identifier.IsMatch(tableName))
        {
            error = "FROM 表名无效。";
            return false;
        }
        parsed = new DataSourceSql(tableName, columns, whereColumn, whereValue, null, orderColumn, orderDirection);
        return true;
    }

    private static bool IsLiteralUnionOnly(string sql)
    {
        // 每一段必须是 SELECT '字面量' [AS 列][, ...]，无 FROM/表访问；
        // 首段必须声明列名，后续段可省略（列继承自首段，对齐 T_KIND 存量）
        var first = true;
        foreach (var segment in sql.Split("UNION", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!segment.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return false;
            if (IndexOfKeyword(segment, "FROM") >= 0) return false;
            var rest = segment["SELECT".Length..].Trim();
            if (rest.Length == 0) return false;
            var parts = rest.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var part in parts)
            {
                var match = Regex.Match(part, @"^'((?:[^']|'')*)'(?:\s+AS\s+([A-Za-z_][A-Za-z0-9_]{0,127}))?$");
                if (!match.Success) return false;
                if (first && !match.Groups[2].Success) return false;
            }
            first = false;
        }
        return true;
    }

    private static int IndexOfKeyword(string text, string keyword)
    {
        var index = 0;
        while ((index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var leftOk = index == 0 || char.IsWhiteSpace(text[index - 1]);
            var end = index + keyword.Length;
            var rightOk = end >= text.Length || char.IsWhiteSpace(text[end]);
            if (leftOk && rightOk) return index;
            index += keyword.Length;
        }
        return -1;
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string table, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@Table) THEN 1 ELSE 0 END;",
            connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        return (int)(await command.ExecuteScalarAsync(token) ?? 0) == 1;
    }

    private static async Task<bool> ColumnExistsAsync(SqlConnection connection, string table, string column, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA='dbo' AND TABLE_NAME=@Table AND COLUMN_NAME=@Column) THEN 1 ELSE 0 END;",
            connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table;
        command.Parameters.Add("@Column", SqlDbType.NVarChar, 100).Value = column;
        return (int)(await command.ExecuteScalarAsync(token) ?? 0) == 1;
    }
}

public enum PublishExpressionStatus
{
    Published,
    NoChange,
    Invalid,
    NotFound,
    ConcurrentModified,
}

public sealed record PublishExpressionOutcome(PublishExpressionStatus Status, IReadOnlyList<string> Errors);

internal sealed record DataSourceSql(
    string? Table,
    IReadOnlyList<string> Columns,
    string? WhereColumn,
    string? WhereValue,
    string? Raw = null,
    string? OrderColumn = null,
    string? OrderDirection = null);
