using System.Data;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>受控表达式类型。</summary>
public enum RestrictedExpressionKind
{
    VirtualExp,
    ConvertFunction,
}

public sealed record ExpressionValidationResult(
    bool Ok,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Hints,
    int WhiteListVersion);

public sealed record ExpressionStaleEntry(string Kind, string Table, string Field, string Expression, IReadOnlyList<string> Errors);
public sealed record ExpressionRescanResult(int WhiteListVersion, int Total, IReadOnlyList<ExpressionStaleEntry> Stale);

public sealed record ExpressionOverview(
    int WhiteListVersion,
    int TotalVisible,
    int Hidden,
    int VirtualExp,
    int ConvertFunction,
    int Stale,
    IReadOnlyList<ExpressionStaleEntry> StaleItems);

/// <summary>转换函数注册表条目（构建器下拉）：Name = 注册表函数名，Description = 取值语义说明。</summary>
public sealed record ExpressionRegistryItem(string Name, string Description);

/// <summary>受控表达式注册表（白名单版本 + 转换函数名）：构建器下拉的唯一来源，前端不得硬编码。</summary>
public sealed record ExpressionRegistry(int WhiteListVersion, IReadOnlyList<ExpressionRegistryItem> ConvertFunctions);

/// <summary>表达式结构回读的形态取值（前端构建器据此决定控件是否可编辑）。</summary>
public static class ExpressionStructureModes
{
    /// <summary>虚拟表达式「表.列」单引用：构建器可编辑。</summary>
    public const string Reference = "reference";
    /// <summary>虚拟表达式受控算术/常量子集：构建器不覆盖，只能原始文本编辑。</summary>
    public const string Arithmetic = "arithmetic";
    /// <summary>转换函数命中注册表。</summary>
    public const string Registry = "registry";
    /// <summary>语法非法或构建器不覆盖的形态。</summary>
    public const string Raw = "raw";
}

/// <summary>
/// 表达式结构回读结果（构建器初始化）。Mode 取值见 <see cref="ExpressionStructureModes"/>；
/// 只用受控解析器，不触库、不执行表达式。
/// </summary>
public sealed record ExpressionStructure(
    string Kind,
    string Mode,
    string? Table = null,
    string? Column = null,
    string? Function = null);

/// <summary>
/// 受控表达式校验（VIRTUAL_EXP / CONVERT_FUNCTION 两套受限语言）：
/// 语法解析 → 表/列物理存在 → 白名单命中，任何失败都不进入运行时。
/// 表达式随字段一起保存，保存路径在写库前调用本服务；表达式为空表示清空。
/// 白名单版本为常量，随解析器/注册表变更递增。
/// </summary>
public sealed class RestrictedExpressionService(DbConnectionFactory connections)
{
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
            // 空值 = 清空：保存路径据此把表达式列写回 NULL，无需再解析
            hints.Add("表达式为空，保存后清空该字段的表达式。");
            return new(true, errors, hints, version);
        }
        if (value.Length > 2000)
        {
            errors.Add("表达式长度超出限制（最多 2000 字符）。");
            return new(false, errors, hints, version);
        }

        switch (kind)
        {
            case RestrictedExpressionKind.VirtualExp:
                if (await EnsureVirtualFieldAsync(table, field, errors, hints, token))
                {
                    await ValidateVirtualExpAsync(table, value, errors, hints, token);
                }
                break;
            case RestrictedExpressionKind.ConvertFunction:
                if (ValidateConvertFunction(value) is { } convertError) errors.Add(convertError);
                break;
            default:
                errors.Add("未知表达式类型。");
                break;
        }
        return new(errors.Count == 0, errors, hints, version);
    }

    /// <summary>对全部已发布表达式按当前白名单版本重校验（P3：版本升级后标记需复核项）。</summary>
    public async Task<ExpressionRescanResult> RescanAsync(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var version = await ReadWhiteListVersionAsync(token);
        const string sql = """
            SELECT LTRIM(RTRIM(T_ID)),LTRIM(RTRIM(F_ID)),
                   LTRIM(RTRIM(ISNULL(VIRTUAL_EXP,''))),LTRIM(RTRIM(ISNULL(CONVERT_FUNCTION,'')))
            FROM dbo.FIELDS WITH (NOLOCK)
            WHERE (LTRIM(RTRIM(ISNULL(VIRTUAL_EXP,'')))<>''
                OR LTRIM(RTRIM(ISNULL(CONVERT_FUNCTION,'')))<>'')
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
            foreach (var (kind, value) in new[]
            {
                (RestrictedExpressionKind.VirtualExp, virtualExp),
                (RestrictedExpressionKind.ConvertFunction, convertFunction),
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

    /// <summary>表达式审计总览：版本 + 可见/隐藏/分类计数 + 重校验结果（供 2302 表达式审计面板与实施人员报表）。</summary>
    public async Task<ExpressionOverview> OverviewAsync(CancellationToken token)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        var version = await ReadWhiteListVersionAsync(token);
        var virtualExp = 0;
        var convertFunction = 0;
        var visible = 0;
        var hidden = 0;
        const string sql = """
            SELECT CAST(COALESCE(IS_VISIBLE,1) AS bit),
                   CAST(CASE WHEN LTRIM(RTRIM(ISNULL(VIRTUAL_EXP,'')))<>'' THEN 1 ELSE 0 END AS bit),
                   CAST(CASE WHEN LTRIM(RTRIM(ISNULL(CONVERT_FUNCTION,'')))<>'' THEN 1 ELSE 0 END AS bit)
            FROM dbo.FIELDS WITH (NOLOCK);
            """;
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var isVisible = reader.GetBoolean(0);
            var hasVirtual = reader.GetBoolean(1);
            var hasConvert = reader.GetBoolean(2);
            if (hasVirtual) virtualExp++;
            if (hasConvert) convertFunction++;
            if (isVisible) visible++;
            else hidden++;
        }
        var rescan = await RescanAsync(token);
        return new ExpressionOverview(version, visible, hidden, virtualExp, convertFunction, rescan.Stale.Count, rescan.Stale);
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

    /// <summary>构建器注册表（白名单版本 + 转换函数名）：字段设置构建器下拉只认这里，前端不硬编码。</summary>
    public async Task<ExpressionRegistry> GetRegistryAsync(CancellationToken token) =>
        new(
            await ReadWhiteListVersionAsync(token),
            ConvertFunctionRegistry.Select(item => new ExpressionRegistryItem(item.Key, item.Value)).ToList());

    /// <summary>
    /// 表达式结构回读（字段设置构建器初始化）：把已存文本还原为构建器模型。
    /// 与 ValidateAsync 同源解析器，前端不另立一套语法；不触库、不执行。
    /// </summary>
    public static ExpressionStructure ParseStructure(RestrictedExpressionKind kind, string? expression)
    {
        var text = expression?.Trim() ?? "";
        return kind switch
        {
            RestrictedExpressionKind.VirtualExp => ParseVirtualStructure(text),
            RestrictedExpressionKind.ConvertFunction => ParseConvertStructure(text),
            _ => throw new InvalidOperationException("未知表达式类型。"),
        };
    }

    private static ExpressionStructure ParseVirtualStructure(string expression)
    {
        if (expression.Length == 0) return new("virtual_exp", ExpressionStructureModes.Raw);
        if (VirtualExpressionParser.TryParseExpression(expression, out var table, out var column))
            return new("virtual_exp", ExpressionStructureModes.Reference, table, column);
        return new("virtual_exp", VirtualArithmeticParser.TryParse(expression, out _, out _)
            ? ExpressionStructureModes.Arithmetic
            : ExpressionStructureModes.Raw);
    }

    private static ExpressionStructure ParseConvertStructure(string expression)
    {
        if (ValidateConvertFunction(expression) is not null) return new("convert_function", ExpressionStructureModes.Raw);
        // 注册表大小写不敏感，回读统一给出注册表内的规范名，避免同一函数出现多种写法
        var name = ConvertFunctionRegistry.Keys.First(key => key.Equals(expression, StringComparison.OrdinalIgnoreCase));
        return new("convert_function", ExpressionStructureModes.Registry, Function: name);
    }

    /// <summary>
    /// 虚拟表达式只在虚拟字段（FIELDS.IS_VIRTUAL = 1）上生效：读取侧一律按该位决定是否解析
    /// VIRTUAL_EXP，非虚拟字段上的表达式既不报错也不取值。校验/预览/发布都先过这道门，
    /// 拦下"看着配了、实际不工作"的配置；字段本身不存在时同样拒绝。
    /// </summary>
    private async Task<bool> EnsureVirtualFieldAsync(string table, string field, List<string> errors, List<string> hints, CancellationToken token)
    {
        if (!WorkbenchSql.Identifier.IsMatch(table) || !WorkbenchSql.Identifier.IsMatch(field))
        {
            errors.Add("表名或字段名无效。");
            return false;
        }
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT CAST(COALESCE(IS_VIRTUAL,0) AS bit) FROM dbo.FIELDS WITH (NOLOCK) WHERE LTRIM(RTRIM(T_ID))=@Table AND LTRIM(RTRIM(F_ID))=@Field;",
            connection);
        command.Parameters.Add("@Table", SqlDbType.NVarChar, 100).Value = table.Trim();
        command.Parameters.Add("@Field", SqlDbType.NVarChar, 100).Value = field.Trim();
        var declared = await command.ExecuteScalarAsync(token);
        if (declared is null || declared is DBNull)
        {
            errors.Add($"字段 {table.Trim()}.{field.Trim()} 的元数据不存在。");
            return false;
        }
        if (declared is true)
        {
            return true;
        }
        errors.Add($"字段 {table.Trim()}.{field.Trim()} 不是虚拟字段（IS_VIRTUAL=0），虚拟表达式只在虚拟字段上生效，本字段的表达式不会进入运行时。");
        hints.Add("如需按表达式取值，先把该字段登记为虚拟字段；物理列字段直接取列值即可。");
        return false;
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
        if (!WorkbenchSql.Identifier.IsMatch(table))
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
                // 与运行时共用同一份引用名口径（别名优先、物理表名须唯一命中），保证"校验通过即可渲染"
                if (!VirtualExpressionParser.TryResolveJoin(joins, refTableName, out var join))
                {
                    errors.Add($"表 {refTableName} 不在 {table} 的 QUERY_RELATION 白名单内，或该表名在关系里重复而无法确定关联段。");
                    continue;
                }
                targetTable = join.Table;
            }
            if (!await WorkbenchSql.ColumnExistsAsync(connection, null, targetTable, refColumnName, token))
                errors.Add($"列 {refTableName}.{refColumnName} 在物理表中不存在。");
        }
        if (errors.Count == 0)
            hints.Add($"引用：{(referencedTables.Count == 0 ? "(常量)" : string.Join(',', referencedTables))}（白名单 v{WhiteListVersion}）");
    }

    internal static string? ValidateConvertFunction(string expression)
    {
        expression = expression.Trim();
        if (!WorkbenchSql.Identifier.IsMatch(expression))
            return "转换函数必须是受控注册表内的函数名（如 f_get_emp_name_by_id）。";
        if (!ConvertFunctionRegistry.ContainsKey(expression))
            return $"函数 {expression} 不在受控注册表内；新增函数须先登记注册表。";
        return null;
    }

}
