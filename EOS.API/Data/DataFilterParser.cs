using System.Globalization;
using System.Text;

namespace EOS.API.Data;

/// <summary>
/// DATA_FILTER / MODULES.FILTER 受限解析。
/// 接受「白名单字段 运算符 值」谓词（字段可带主表名前缀），AND/OR 组合与括号（深度 ≤ 8）。
/// 运算符：=、&lt;&gt;、&gt;、&lt;、&gt;=、&lt;=。
/// 值（右侧）限单引号字符串（'' 转义）、数字字面量，或受控函数表达式：
/// getdate()、dateadd(month,&lt;整数&gt;,getdate())、convert(varchar(7),&lt;日期表达式&gt;,120)、
/// 及字符串拼接（`+ '字面量'`，如 convert(...)+'-26'）；函数在服务端求值为常量后参数化。
/// 左侧支持同表白名单列间算术（+ - * /，操作数为列或数字），如 QTY-RECEIVE_QTY&gt;0。
/// 解析成功编译为参数化谓词；其余一律拒绝。
/// 空过滤由调用方视为"无行级限制"；解析失败时调用方必须拒绝执行（读/写返回 403）。
/// </summary>
internal static class DataFilterParser
{
    private const int MaxDepth = 8;

    /// <summary>
    /// CHOOSE_FILTER 子查询白名单（IN (SELECT ...) 受控解析）。
    /// 从 97 条旧配置提取：表/函数名 + 允许引用的列；子查询表名/列名必须在此白名单内。
    /// F_* 为表值函数（dbo.f_get_pro_units / dbo.f_get_under_m_idx），参数模板后续阶段放开。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> SubqueryTableColumns =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["BOM_COST_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO" },
            ["BOM_INSTRUCT_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO" },
            ["BOM_STRU_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO" },
            ["CLIENT_PRICE_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CLIENT_ID", "PRO_NO" },
            ["F_GET_PRO_UNITS"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "UNIT_ID" },
            ["F_GET_UNDER_M_IDX"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "M_IDX" },
            ["HR_APPLY_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "APPLY_NO", "APPLY_TYPE", "EMP_ID" },
            ["HR_BASEPAY_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BASEPAY_NO", "BASEPAY_TYPE", "COUNT_MONTH" },
            ["HR_BASEPAY_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BASEPAY_NO", "BASEPAY_TYPE", "EMP_ID" },
            ["HR_EMPLOYEE_CARD"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EMP_ID" },
            ["HR_ENACTMENT_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EMP_ID", "ENACTMENT_NO", "ENACTMENT_TYPE" },
            ["HR_LEAVE_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EMP_ID", "LEAVE_NO", "LEAVE_TYPE" },
            ["HR_PLAN_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "COUNT_MONTH", "PLAN_NO", "PLAN_TYPE" },
            ["HR_PLAN_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EMP_ID", "PLAN_NO", "PLAN_TYPE" },
            ["HR_WAGE"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "WAGE_FIELD" },
            ["HR_WAGE_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "COUNT_MONTH", "WAGE_NO", "WAGE_TYPE" },
            ["HR_WAGE_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "EMP_ID", "WAGE_NO", "WAGE_TYPE" },
            ["HR_WORKTIME_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "COUNT_DATE", "EMP_ID", "WORKTIME_NO", "WORKTIME_TYPE" },
            ["INV_CHECK_STOCK_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "CHECK_STOCK_NO", "CHECK_STOCK_TYPE", "PRO_NO" },
            ["MODULES"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MASTER_TABLE", "M_IDX", "M_P_IDX" },
            ["MOU_ACCEPT_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "APPLY_NO" },
            ["MOU_ASSESS_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ASSESS_NO", "PRO_NO" },
            ["MOU_BATCHIN_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BATCHIN_NO", "BATCH_NO" },
            ["MOU_BATCHTOP_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BATCH_NO", "SCRAP_NO" },
            ["MOU_BATCH_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BATCH_NO", "SCRAP_NO" },
            ["MOU_GET2_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "APPLY_NO", "GET_NO" },
            ["MOU_PRO_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO" },
            ["QC_APPLY_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "APPLY_NO", "COMPLAIN_NO", "EXCEPTION_NO" },
            ["QC_LOSS_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ANALYSIS_NO", "LOSS_NO" },
            ["QC_REWORK_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "COMPLAIN_NO", "EXCEPTION_NO", "REWORK_NO" },
            ["QC_SAMPLE_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO" },
            ["QC_SCRAP_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ANALYSIS_NO", "COMPLAIN_NO", "EXCEPTION_NO", "REWORK_NO", "SCRAP_NO" },
            ["SFC_PLAN_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PLAN_NO", "PLAN_TYPE", "SORT_IDX" },
            ["SFC_PLAN_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PLAN_NO", "PLAN_TYPE", "SHIPMENT_NO", "SHIPMENT_SERIAL_NO" },
            ["SFC_PROCESS_M"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO" },
            // SAMPLE_PRO 是 PRODUCT 的整表副本（样品产品主档），按 PRO_NO 关联；
            // 受控子查询只读该列，用于"未建样品的成品资料"这类反向清单。
            ["SAMPLE_PRO"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO" },
            ["SUPPLIER_PRICE_D"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "PRO_NO", "SUPPLIER_ID" },
            ["SYSDD"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "M_IDX", "USER_ID" },
            ["SYSDG_USER"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "G_IDX", "USER_ID" },
            ["SYSDH"] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "G_IDX", "M_IDX" },
        };

    public static bool TryParse(
        string? filter,
        string masterTable,
        IReadOnlySet<string> allowedFields,
        out string predicate,
        out IReadOnlyList<object> parameters)
        => TryParseCore(filter, masterTable, allowedFields, null, null, out predicate, out parameters, out _, out _);

    /// <summary>
    /// 带跨表 JOIN 支持的解析（选择器专用）：foreignTables = 外键表名 → 与查询表同名的关联列，
    /// 引用白名单外键表列时输出 [表].[列] 并登记所需 JOIN 与引用列，由调用方校验物理存在后拼装。
    /// </summary>
    public static bool TryParseWithJoins(
        string? filter,
        string masterTable,
        IReadOnlySet<string> allowedFields,
        IReadOnlyDictionary<string, string>? foreignTables,
        IReadOnlyDictionary<string, string>? columnTypes,
        out string predicate,
        out IReadOnlyList<object> parameters,
        out IReadOnlyList<string> joins,
        out IReadOnlyList<(string Table, string Column)> foreignColumns)
        => TryParseCore(filter, masterTable, allowedFields, foreignTables, columnTypes, out predicate, out parameters, out joins, out foreignColumns);

    private sealed class ParseContext
    {
        public IReadOnlyDictionary<string, string>? ForeignTables;
        public IReadOnlyDictionary<string, string>? ColumnTypes;
        public List<string> Joins = [];
        public List<(string Table, string Column)> ForeignColumns = [];
    }

    private static bool TryParseCore(
        string? filter,
        string masterTable,
        IReadOnlySet<string> allowedFields,
        IReadOnlyDictionary<string, string>? foreignTables,
        IReadOnlyDictionary<string, string>? columnTypes,
        out string predicate,
        out IReadOnlyList<object> parameters,
        out IReadOnlyList<string> joins,
        out IReadOnlyList<(string Table, string Column)> foreignColumns)
    {
        predicate = string.Empty;
        parameters = [];
        joins = [];
        foreignColumns = [];
        if (string.IsNullOrWhiteSpace(filter)) return false;
        // FILTER references use {Table.Column} syntax (e.g. {PRODUCT.PRO_TYPE}=1);
        // braces only wrap the reference and are stripped before the same whitelist/parameterized path.
        filter = filter.Replace("{", "").Replace("}", "");
        try
        {
            var tokens = Tokenize(filter);
            var position = 0;
            var values = new List<object>();
            var context = new ParseContext { ForeignTables = foreignTables, ColumnTypes = columnTypes };
            if (!ParseOr(tokens, ref position, masterTable, allowedFields, context, out var expression, values, 0))
                return false;
            if (position != tokens.Count || string.IsNullOrWhiteSpace(expression)) return false;
            predicate = expression;
            parameters = values;
            joins = context.Joins;
            foreignColumns = context.ForeignColumns;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool ParseOr(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        ParseContext context,
        out string expression,
        List<object> values,
        int depth)
    {
        if (depth > MaxDepth) { expression = string.Empty; return false; }
        if (!ParseAnd(tokens, ref position, masterTable, allowed, context, out var left, values, depth))
        {
            expression = string.Empty;
            return false;
        }
        var parts = new List<string> { left };
        while (position < tokens.Count && tokens[position].Kind == TokenKind.AndOr
               && tokens[position].Text.Equals("or", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            if (!ParseAnd(tokens, ref position, masterTable, allowed, context, out var right, values, depth))
            {
                expression = string.Empty;
                return false;
            }
            parts.Add(right);
        }
        expression = string.Join(" OR ", parts);
        return true;
    }

    private static bool ParseAnd(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        ParseContext context,
        out string expression,
        List<object> values,
        int depth)
    {
        if (!ParsePrimary(tokens, ref position, masterTable, allowed, context, out var left, values, depth))
        {
            expression = string.Empty;
            return false;
        }
        var parts = new List<string> { left };
        while (position < tokens.Count && tokens[position].Kind == TokenKind.AndOr
               && tokens[position].Text.Equals("and", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            if (!ParsePrimary(tokens, ref position, masterTable, allowed, context, out var right, values, depth))
            {
                expression = string.Empty;
                return false;
            }
            parts.Add(right);
        }
        expression = string.Join(" AND ", parts);
        return true;
    }

    private static bool ParsePrimary(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        ParseContext context,
        out string expression,
        List<object> values,
        int depth)
    {
        if (position >= tokens.Count)
        {
            expression = string.Empty;
            return false;
        }
        // NOT ( ... )：存量 CHOOSE_FILTER（如 NOT (PRODUCT.BUSINESS_TAG=1 OR PRODUCT.STOP_TAG=1)）
        if (tokens[position].Kind == TokenKind.Identifier
            && tokens[position].Text.Equals("not", StringComparison.OrdinalIgnoreCase)
            && position + 1 < tokens.Count && tokens[position + 1].Kind == TokenKind.LeftParen)
        {
            position += 2;
            if (!ParseOr(tokens, ref position, masterTable, allowed, context, out var inner, values, depth + 1))
            {
                expression = string.Empty;
                return false;
            }
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.RightParen)
            {
                expression = string.Empty;
                return false;
            }
            position++;
            expression = $"NOT ({inner})";
            return true;
        }
        if (tokens[position].Kind == TokenKind.LeftParen)
        {
            position++;
            if (!ParseOr(tokens, ref position, masterTable, allowed, context, out var inner, values, depth + 1))
            {
                expression = string.Empty;
                return false;
            }
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.RightParen)
            {
                expression = string.Empty;
                return false;
            }
            position++;
            // 括号表达式后跟比较符（如 (A-B-C)>0）：比较右值参数化
            if (position < tokens.Count && tokens[position].Kind == TokenKind.Operator)
            {
                var parenComparison = tokens[position].Text;
                if (parenComparison is not ("=" or "<>" or ">" or "<" or ">=" or "<="))
                {
                    expression = string.Empty;
                    return false;
                }
                position++;
                if (!ParseValueExpression(tokens, ref position, out var parenValue, true))
                {
                    expression = string.Empty;
                    return false;
                }
                var parenParameter = $"@df{values.Count}";
                values.Add(parenValue);
                expression = $"({inner}) {parenComparison} {parenParameter}";
                return true;
            }
            expression = $"({inner})";
            return true;
        }
        return ParsePredicate(tokens, ref position, masterTable, allowed, context, out expression, values);
    }

    private static bool ParsePredicate(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        ParseContext context,
        out string expression,
        List<object> values)
    {
        expression = string.Empty;
        // 常量左值比较（如 1=1）：左值直接拼接（来源为服务端配置），右值参数化
        if (tokens[position].Kind == TokenKind.Number)
        {
            var constLeft = tokens[position].Text;
            position++;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.Operator) return false;
            var constComparison = tokens[position].Text;
            if (constComparison is not ("=" or "<>" or ">" or "<" or ">=" or "<=")) return false;
            position++;
            if (!ParseValueExpression(tokens, ref position, out var constValue, true)) return false;
            var constParameter = $"@df{values.Count}";
            values.Add(constValue);
            expression = $"{constLeft} {constComparison} {constParameter}";
            return true;
        }
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier) return false;
        var leftToken = tokens[position].Text;
        string left;
        if (tokens[position].Text.Equals("datediff", StringComparison.OrdinalIgnoreCase)
            && position + 1 < tokens.Count && tokens[position + 1].Kind == TokenKind.LeftParen)
        {
            // DATEDIFF(day, <白名单列>, GETDATE()) 左侧谓词（存量 CHOOSE_FILTER：当日送货选择器）。
            // 列经白名单解析，GETDATE() 为固定 SQL 关键字，无用户输入拼接。
            position += 2;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
                || !tokens[position].Text.Equals("day", StringComparison.OrdinalIgnoreCase))
                return false;
            position++;
            if (!ExpectToken(tokens, ref position, TokenKind.Comma)) return false;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
                || !TryResolveField(tokens[position].Text, masterTable, allowed, context, out var dateColumnSql))
                return false;
            position++;
            if (!ExpectToken(tokens, ref position, TokenKind.Comma)) return false;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
                || !tokens[position].Text.Equals("getdate", StringComparison.OrdinalIgnoreCase))
                return false;
            position++;
            if (!ExpectToken(tokens, ref position, TokenKind.LeftParen)) return false;
            if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
            if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
            left = $"DATEDIFF(day, {dateColumnSql}, GETDATE())";
        }
        else if (tokens[position].Text.Equals("isnull", StringComparison.OrdinalIgnoreCase))
        {
            // Common shorthand ISNULL(col,0)=0 / ISNULL(col,'')='': whitelisted column + constant, parameterized
            if (!ParseIsNullLeft(tokens, ref position, masterTable, allowed, context, out left, values)) return false;
        }
        else
        {
            if (!TryResolveField(tokens[position].Text, masterTable, allowed, context, out var columnSql)) return false;
            left = columnSql;
            position++;
        }
        var leftIsArithmetic = false;
        // 左侧列算术：field (arithop (field|number))+，如 QTY-RECEIVE_QTY
        while (position < tokens.Count && IsArithmeticOperator(tokens[position]))
        {
            leftIsArithmetic = true;
            var arithOp = tokens[position].Text;
            position++;
            if (position >= tokens.Count) return false;
            if (tokens[position].Kind == TokenKind.Identifier)
            {
                if (tokens[position].Text.Equals("cast", StringComparison.OrdinalIgnoreCase))
                {
                    // 拼接操作数 CAST(列 AS CHAR(n))：列经白名单校验
                    if (!ParseCastAsChar(tokens, ref position, token =>
                        TryResolveField(token, masterTable, allowed, context, out var columnSql) ? columnSql : null,
                        out var castSql)) return false;
                    left += $"{arithOp}{castSql}";
                }
                else
                {
                    if (!TryResolveField(tokens[position].Text, masterTable, allowed, context, out var operandSql)) return false;
                    left += $"{arithOp}{operandSql}";
                    position++;
                }
            }
            else if (tokens[position].Kind == TokenKind.Number)
            {
                var name = $"@df{values.Count}";
                values.Add(decimal.Parse(tokens[position].Text, CultureInfo.InvariantCulture));
                left += $"{arithOp}{name}";
                position++;
            }
            else
            {
                return false;
            }
        }
        // 括号内算术表达式（如 (A-B-C)）：右括号处结束，尾部比较符由 ParsePrimary 处理
        if (position < tokens.Count && tokens[position].Kind == TokenKind.RightParen)
        {
            expression = left;
            return true;
        }
        // IN / NOT IN 子查询（受控白名单；阶段 1：单表、无 WHERE）
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier
            && tokens[position].Text.Equals("in", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            if (!ParseInSubquery(tokens, ref position, values, out var subSql)) return false;
            expression = $"{left} IN ({subSql})";
            return true;
        }
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier
            && tokens[position].Text.Equals("not", StringComparison.OrdinalIgnoreCase)
            && position + 1 < tokens.Count && tokens[position + 1].Kind == TokenKind.Identifier
            && tokens[position + 1].Text.Equals("in", StringComparison.OrdinalIgnoreCase))
        {
            position += 2;
            if (!ParseInSubquery(tokens, ref position, values, out var subSql)) return false;
            expression = $"{left} NOT IN ({subSql})";
            return true;
        }
        // LIKE 模式匹配（真实存量：HR_WAGE/HRM_WAGE 工资字段选择器 F_ID LIKE '%_ITEM%'）：
        // 模式串必须为引号字面量，作为参数绑定（通配符来自服务端配置字面量，不拼接用户输入）。
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier
            && tokens[position].Text.Equals("like", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.Literal) return false;
            var likePattern = tokens[position].Text;
            position++;
            var likeParameter = $"@df{values.Count}";
            values.Add(likePattern);
            expression = $"{left} LIKE {likeParameter}";
            return true;
        }
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Operator) return false;
        var comparison = tokens[position].Text;
        if (comparison is not ("=" or "<>" or ">" or "<" or ">=" or "<=")) return false;
        position++;
        // 列对列比较（存量 REPORT_FILTER 真实写法：{MOC_PRODUCE_M.FINISHED_QTY}<{MOC_PRODUCE_M.QTY}）：
        // 右值同样经白名单解析为列，双方均为服务端白名单列，无参数拼接。
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier
            && TryResolveField(tokens[position].Text, masterTable, allowed, context, out var rightColumnSql))
        {
            position++;
            expression = $"{left} {comparison} {rightColumnSql}";
            return true;
        }
        // 直接列比较的数值字面量以字符串参数绑定（如 PRO_TYPE=1，PRO_TYPE 为 char，
        // 若绑 decimal 会触发 char→numeric 隐式转换，含非数字值时报 8114）；
        // 列间算术的右值保持 decimal（如 QTY-RECEIVE_QTY>0 的 0）。
        // 数值字面量的绑定类型按字段类型决定：bit/数值列绑数字（避免隐式转换导致索引失效/全表扫描），
        // char/nvarchar 列绑字符串；未知类型保守绑字符串。
        var numericAsString = !leftIsArithmetic;
        if (!leftIsArithmetic && !leftToken.Equals("isnull", StringComparison.OrdinalIgnoreCase)
            && context.ColumnTypes is not null)
        {
            var leftCol = leftToken.Contains('.') ? leftToken.Split('.')[^1] : leftToken;
            if (context.ColumnTypes.TryGetValue(leftCol, out var dataType))
                numericAsString = !IsNumericSqlType(dataType);
        }
        if (!ParseValueExpression(tokens, ref position, out var value, numericAsString)) return false;
        var parameterName = $"@df{values.Count}";
        values.Add(value);
        expression = $"{left} {comparison} {parameterName}";
        return true;
    }

    private static bool IsNumericSqlType(string dataType)
    {
        var type = dataType.ToLowerInvariant();
        return type is "bit" or "int" or "bigint" or "smallint" or "tinyint"
            or "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney";
    }

    /// <summary>解析左侧 ISNULL(白名单列,常量) 表达式，常量（数字/字符串）参数化绑定。</summary>
    private static bool ParseIsNullLeft(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        ParseContext context,
        out string left,
        List<object> values)
    {
        left = string.Empty;
        var start = position;
        position++; // isnull
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.LeftParen) { position = start; return false; }
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
            || !TryResolveField(tokens[position].Text, masterTable, allowed, context, out var columnSql)) { position = start; return false; }
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Comma) { position = start; return false; }
        position++;
        if (position >= tokens.Count || tokens[position].Kind is not (TokenKind.Number or TokenKind.Literal)) { position = start; return false; }
        var fallback = $"@df{values.Count}";
        values.Add(tokens[position].Text);
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.RightParen) { position = start; return false; }
        position++;
        left = $"ISNULL({columnSql},{fallback})";
        return true;
    }

    /// <summary>
    /// 解析 IN (SELECT [DISTINCT] 列 FROM 白名单表 [WHERE 条件]) 子查询。
    /// 支持：单白名单表（含 WHERE 等值条件）、白名单表值函数（dbo.f_*，参数化）。
    /// 表名/列名/函数名必须命中 SubqueryTableColumns 白名单；值全部参数化，不拼接用户输入。
    /// 多表 FROM、嵌套子查询仍拒绝（保持现状，选择器返回空，不泄漏）。
    /// </summary>
    private static bool ParseInSubquery(
        IReadOnlyList<Token> tokens,
        ref int position,
        List<object> values,
        out string subSql)
    {
        subSql = string.Empty;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.LeftParen) return false;
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
            || !tokens[position].Text.Equals("select", StringComparison.OrdinalIgnoreCase)) return false;
        position++;
        var distinct = false;
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier
            && tokens[position].Text.Equals("distinct", StringComparison.OrdinalIgnoreCase))
        {
            distinct = true;
            position++;
        }
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier) return false;
        var selectCol = tokens[position].Text;
        position++;
        int? selectCastStart = null;
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Plus
            && position + 1 < tokens.Count && tokens[position + 1].Kind == TokenKind.Identifier
            && tokens[position + 1].Text.Equals("cast", StringComparison.OrdinalIgnoreCase))
        {
            // 拼接表达式（SHIPMENT_NO+CAST(...)）：先扫描跳过到 from，from 表解析后再回退解析列
            selectCastStart = position;
            var depth = 0;
            while (position < tokens.Count)
            {
                if (tokens[position].Kind == TokenKind.LeftParen) depth++;
                else if (tokens[position].Kind == TokenKind.RightParen) depth--;
                else if (depth == 0 && tokens[position].Kind == TokenKind.Identifier
                         && tokens[position].Text.Equals("from", StringComparison.OrdinalIgnoreCase)) break;
                position++;
            }
        }
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
            || !tokens[position].Text.Equals("from", StringComparison.OrdinalIgnoreCase)) return false;
        position++;
        // FROM 解析：表值函数（dbo.f_xxx('参数')）或 一个或多个白名单表（可带别名）
        string fromSql;
        string? funcWhitelist = null;
        var tables = new List<(string Table, string Alias)>();
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier
            && tokens[position].Text.Contains('.'))
        {
            var funcParts = tokens[position].Text.Split('.');
            if (funcParts.Length != 2 || !funcParts[0].Equals("dbo", StringComparison.OrdinalIgnoreCase)) return false;
            var funcName = funcParts[1];
            if (!SubqueryTableColumns.TryGetValue(funcName, out _)) return false;
            position++;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.LeftParen) return false;
            position++;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.Literal) return false;
            var parameterName = $"@df{values.Count}";
            values.Add(tokens[position].Text);
            position++;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.RightParen) return false;
            position++;
            fromSql = $"dbo.[{funcName}]({parameterName})";
            funcWhitelist = funcName;
        }
        else
        {
            var fromParts = new List<string>();
            while (true)
            {
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier) return false;
                var table = tokens[position].Text;
                position++;
                if (!SubqueryTableColumns.TryGetValue(table, out _)) return false;
                var alias = table;
                if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier
                    && !tokens[position].Text.Equals("where", StringComparison.OrdinalIgnoreCase))
                {
                    alias = tokens[position].Text;
                    position++;
                }
                if (tables.Any(item => item.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase))) return false;
                tables.Add((table, alias));
                fromParts.Add($"{table} AS {alias}");
                if (position < tokens.Count && tokens[position].Kind == TokenKind.Comma)
                {
                    position++;
                    continue;
                }
                break;
            }
            fromSql = tables.Count == 1
                ? $"dbo.[{tables[0].Table}]"
                : string.Join(" JOIN ", fromParts.Select(p =>
                {
                    var sp = p.Split(" AS ");
                    return $"dbo.[{sp[0]}] AS [{sp[1]}]";
                }));
        }

        // 列归属解析：无前缀列 → 在所有 from 表中找唯一含该列的表；有前缀 → 别名/表名解析
        bool ResolveColumn(string token, out string table, out string column)
        {
            table = "";
            column = "";
            if (token.Contains('.'))
            {
                var parts = token.Split('.');
                if (parts.Length != 2) return false;
                var found = tables.FirstOrDefault(item => item.Alias.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
                if (found.Table is null) return false;
                if (!SubqueryTableColumns.TryGetValue(found.Table, out var cols) || !cols.Contains(parts[1])) return false;
                table = found.Table;
                column = parts[1];
                return true;
            }
            var matches = tables.Where(item =>
                SubqueryTableColumns.TryGetValue(item.Table, out var cols) && cols.Contains(token)).ToList();
            if (matches.Count != 1) return false;
            table = matches[0].Table;
            column = token;
            return true;
        }

        var singleTable = tables.Count == 1 && funcWhitelist is null;
        string selectSql;
        if (selectCastStart is int castStart)
        {
            // 拼接表达式：左侧列 + CAST(列 AS CHAR(n))（from 表已解析，回退到 + 处解析）
            var savePos = position;
            position = castStart;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.Plus) return false;
            position++;
            if (!ResolveColumn(selectCol, out var leftTable, out var leftColumn)) return false;
            var leftAlias = tables.First(item => item.Table.Equals(leftTable, StringComparison.OrdinalIgnoreCase)).Alias;
            if (!ParseCastAsChar(tokens, ref position, token =>
            {
                if (!ResolveColumn(token, out var castTable, out var castColumn)) return null;
                var alias = tables.First(item => item.Table.Equals(castTable, StringComparison.OrdinalIgnoreCase)).Alias;
                return $"[{alias}].[{castColumn}]";
            }, out var castSql)) return false;
            selectSql = $"[{leftAlias}].[{leftColumn}]+{castSql}";
            position = savePos;
        }
        else if (funcWhitelist is not null)
        {
            if (!SubqueryTableColumns.TryGetValue(funcWhitelist, out var funcCols) || !funcCols.Contains(selectCol)) return false;
            selectSql = $"[{selectCol}]";
        }
        else
        {
            if (!ResolveColumn(selectCol, out var selTable, out var selColumn)) return false;
            selectSql = singleTable
                ? $"[{selColumn}]"
                : $"[{tables.First(item => item.Table.Equals(selTable, StringComparison.OrdinalIgnoreCase)).Alias}].[{selColumn}]";
        }

        // WHERE：表间等值 → JOIN ON；列 = 值 → WHERE 参数化
        var joins = new List<string>();
        var conditions = new List<string>();
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier
            && tokens[position].Text.Equals("where", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            while (true)
            {
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier) return false;
                var leftToken = tokens[position].Text;
                position++;
                if (!ResolveColumn(leftToken, out var leftTable, out var leftColumn)) return false;
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.Operator
                    || tokens[position].Text is not ("=" or "<>" or ">" or "<" or ">=" or "<=")) return false;
                var op = tokens[position].Text;
                position++;
                if (position < tokens.Count && tokens[position].Kind == TokenKind.Identifier
                    && op == "="
                    && ResolveColumn(tokens[position].Text, out var rightTable, out var rightColumn)
                    && !leftTable.Equals(rightTable, StringComparison.OrdinalIgnoreCase))
                {
                    position++;
                    var leftAlias = tables.First(item => item.Table.Equals(leftTable, StringComparison.OrdinalIgnoreCase)).Alias;
                    var rightAlias = tables.First(item => item.Table.Equals(rightTable, StringComparison.OrdinalIgnoreCase)).Alias;
                    joins.Add($"[{leftAlias}].[{leftColumn}]=[{rightAlias}].[{rightColumn}]");
                }
                else
                {
                    if (!ParseValueExpression(tokens, ref position, out var condValue, numericAsString: true)) return false;
                    var condParameter = $"@df{values.Count}";
                    values.Add(condValue);
                    var alias = tables.First(item => item.Table.Equals(leftTable, StringComparison.OrdinalIgnoreCase)).Alias;
                    conditions.Add(singleTable
                        ? $"[{leftColumn}] {op} {condParameter}"
                        : $"[{alias}].[{leftColumn}] {op} {condParameter}");
                }
                if (position < tokens.Count && tokens[position].Kind == TokenKind.AndOr
                    && tokens[position].Text.Equals("and", StringComparison.OrdinalIgnoreCase))
                {
                    position++;
                    continue;
                }
                break;
            }
        }
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.RightParen) return false;
        position++;
        if (tables.Count > 1 && joins.Count == 0) return false; // 多表必须有表间关联条件
        subSql = $"SELECT {(distinct ? "DISTINCT " : "")}{selectSql} FROM {fromSql}"
            + (joins.Count > 0 ? $" ON {string.Join(" AND ", joins)}" : "")
            + (conditions.Count > 0 ? $" WHERE {string.Join(" AND ", conditions)}" : "");
        return true;
    }

    private static bool IsArithmeticOperator(Token token) =>
        token.Kind is TokenKind.Plus or TokenKind.Minus or TokenKind.Asterisk or TokenKind.Slash;

    /// <summary>
    /// 解析 CAST(列 AS CHAR(n)) 拼接操作数。
    /// 列必须能解析（白名单校验），宽度 n 限 1~64；resolve 返回列 SQL 片段，null 表示不可解析。
    /// </summary>
    private static bool ParseCastAsChar(
        IReadOnlyList<Token> tokens,
        ref int position,
        Func<string, string?> resolve,
        out string sql)
    {
        sql = "";
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
            || !tokens[position].Text.Equals("cast", StringComparison.OrdinalIgnoreCase)) return false;
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.LeftParen) return false;
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier) return false;
        var columnToken = tokens[position].Text;
        var columnSql = resolve(columnToken);
        if (columnSql is null) return false;
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
            || !tokens[position].Text.Equals("as", StringComparison.OrdinalIgnoreCase)) return false;
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
            || !tokens[position].Text.Equals("char", StringComparison.OrdinalIgnoreCase)) return false;
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.LeftParen) return false;
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Number) return false;
        if (!int.TryParse(tokens[position].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
            || width < 1 || width > 64) return false;
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.RightParen) return false;
        position++;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.RightParen) return false;
        position++;
        sql = $"CAST({columnSql} AS CHAR({width}))";
        return true;
    }

    /// <summary>
    /// 谓词右侧值：字符串/数字字面量、负数，或受控函数表达式（可带 `+ '字面量'` 拼接）。
    /// 求值结果一律作为参数绑定，不拼接进 SQL。
    /// </summary>
    private static bool ParseValueExpression(IReadOnlyList<Token> tokens, ref int position, out object value, bool numericAsString = false)
    {
        value = null!;
        if (position >= tokens.Count) return false;
        if (tokens[position].Kind == TokenKind.Minus
            && position + 1 < tokens.Count
            && tokens[position + 1].Kind == TokenKind.Number)
        {
            value = numericAsString ? "-" + tokens[position + 1].Text : -decimal.Parse(tokens[position + 1].Text, CultureInfo.InvariantCulture);
            position += 2;
            return true;
        }
        if (tokens[position].Kind == TokenKind.Literal)
        {
            value = tokens[position].Text;
            position++;
            return true;
        }
        if (tokens[position].Kind == TokenKind.Number)
        {
            value = numericAsString ? tokens[position].Text : decimal.Parse(tokens[position].Text, CultureInfo.InvariantCulture);
            position++;
            return true;
        }
        if (tokens[position].Kind == TokenKind.Identifier
            && (tokens[position].Text.Equals("true", StringComparison.OrdinalIgnoreCase)
                || tokens[position].Text.Equals("false", StringComparison.OrdinalIgnoreCase)))
        {
            value = tokens[position].Text.Equals("true", StringComparison.OrdinalIgnoreCase);
            position++;
            return true;
        }
        if (tokens[position].Kind == TokenKind.Identifier
            && position + 1 < tokens.Count
            && tokens[position + 1].Kind == TokenKind.LeftParen)
        {
            if (!ParseFunctionExpression(tokens, ref position, out value)) return false;
            while (position + 1 < tokens.Count
                   && tokens[position].Kind == TokenKind.Plus
                   && tokens[position + 1].Kind == TokenKind.Literal)
            {
                if (value is not string text) return false;
                value = text + tokens[position + 1].Text;
                position += 2;
            }
            return true;
        }
        return false;
    }

    /// <summary>
    /// 受控函数白名单（服务端求值为常量后参数化）：
    /// getdate()；dateadd(month,&lt;整数&gt;,&lt;日期表达式&gt;)；
    /// convert(varchar(7),&lt;日期表达式&gt;,120)。
    /// 其余函数一律拒绝。
    /// </summary>
    private static bool ParseFunctionExpression(IReadOnlyList<Token> tokens, ref int position, out object value)
    {
        value = null!;
        var name = tokens[position].Text.ToLowerInvariant();
        position++; // 函数名
        position++; // '('
        switch (name)
        {
            case "getdate":
            {
                if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
                value = DateTime.Now;
                return true;
            }
            case "dateadd":
            {
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
                    || !tokens[position].Text.Equals("month", StringComparison.OrdinalIgnoreCase)) return false;
                position++;
                if (!ExpectToken(tokens, ref position, TokenKind.Comma)) return false;
                if (!ParseInteger(tokens, ref position, out var months)) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.Comma)) return false;
                if (!ParseFunctionExpression(tokens, ref position, out var dateValue) || dateValue is not DateTime date) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
                value = date.AddMonths(months);
                return true;
            }
            case "convert":
            {
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier
                    || !tokens[position].Text.Equals("varchar", StringComparison.OrdinalIgnoreCase)) return false;
                position++;
                if (!ExpectToken(tokens, ref position, TokenKind.LeftParen)) return false;
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.Number
                    || tokens[position].Text != "7") return false;
                position++;
                if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.Comma)) return false;
                if (!ParseFunctionExpression(tokens, ref position, out var dateValue) || dateValue is not DateTime date) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.Comma)) return false;
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.Number
                    || tokens[position].Text != "120") return false;
                position++;
                if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
                value = date.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                return true;
            }
            default:
                return false;
        }
    }

    private static bool ParseInteger(IReadOnlyList<Token> tokens, ref int position, out int value)
    {
        value = 0;
        var negative = false;
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Minus)
        {
            negative = true;
            position++;
        }
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Number) return false;
        if (!int.TryParse(tokens[position].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) return false;
        position++;
        value = negative ? -parsed : parsed;
        return true;
    }

    private static bool ExpectToken(IReadOnlyList<Token> tokens, ref int position, TokenKind kind)
    {
        if (position >= tokens.Count || tokens[position].Kind != kind) return false;
        position++;
        return true;
    }

    /// <summary>解析列引用为完整限定 SQL 列；跨表 JOIN 模式下查询表列带表名前缀避免歧义。</summary>
    private static bool TryResolveField(
        string identifier,
        string masterTable,
        IReadOnlySet<string> allowed,
        ParseContext context,
        out string columnSql)
    {
        columnSql = string.Empty;
        var parts = identifier.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            if (!WorkbenchSql.Identifier.IsMatch(parts[0]) || !allowed.Contains(parts[0])) return false;
            columnSql = context.ForeignTables is null ? $"[{parts[0]}]" : $"[{masterTable}].[{parts[0]}]";
            return true;
        }
        else if (parts.Length == 2)
        {
            if (parts[0].Equals(masterTable, StringComparison.OrdinalIgnoreCase))
            {
                if (!WorkbenchSql.Identifier.IsMatch(parts[1]) || !allowed.Contains(parts[1])) return false;
                columnSql = context.ForeignTables is null ? $"[{parts[1]}]" : $"[{masterTable}].[{parts[1]}]";
                return true;
            }
            else if (context.ForeignTables is { } foreign && foreign.ContainsKey(parts[0]))
            {
                // 白名单外键表列：登记 JOIN 与引用列（物理存在性由调用方在拼 SQL 前校验），
                if (!WorkbenchSql.Identifier.IsMatch(parts[1])) return false;
                context.Joins.Add(parts[0]);
                context.ForeignColumns.Add((parts[0], parts[1]));
                columnSql = $"[{parts[0]}].[{parts[1]}]";
                return true;
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }
    }

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var index = 0;
        while (index < input.Length)
        {
            var ch = input[index];
            if (char.IsWhiteSpace(ch))
            {
                index++;
            }
            else if (ch == '(')
            {
                tokens.Add(new Token(TokenKind.LeftParen, "("));
                index++;
            }
            else if (ch == ')')
            {
                tokens.Add(new Token(TokenKind.RightParen, ")"));
                index++;
            }
            else if (ch == '=')
            {
                tokens.Add(new Token(TokenKind.Operator, "="));
                index++;
            }
            else if (ch == '<' || ch == '>')
            {
                var start = index;
                index++;
                if (index < input.Length && (input[index] == '=' || (ch == '<' && input[index] == '>')))
                    index++;
                var text = input[start..index];
                tokens.Add(new Token(TokenKind.Operator, text));
            }
            else if (ch is '\'' or '"')
            {
                var builder = new StringBuilder();
                var quote = ch;
                index++;
                while (index < input.Length)
                {
                    if (input[index] == quote)
                    {
                        if (index + 1 < input.Length && input[index + 1] == quote)
                        {
                            builder.Append(quote);
                            index += 2;
                        }
                        else
                        {
                            index++;
                            break;
                        }
                    }
                    else
                    {
                        builder.Append(input[index]);
                        index++;
                    }
                }
                tokens.Add(new Token(TokenKind.Literal, builder.ToString()));
            }
            else if (ch == ',')
            {
                tokens.Add(new Token(TokenKind.Comma, ","));
                index++;
            }
            else if (ch == '+')
            {
                tokens.Add(new Token(TokenKind.Plus, "+"));
                index++;
            }
            else if (ch == '-')
            {
                tokens.Add(new Token(TokenKind.Minus, "-"));
                index++;
            }
            else if (ch == '*')
            {
                tokens.Add(new Token(TokenKind.Asterisk, "*"));
                index++;
            }
            else if (ch == '/')
            {
                tokens.Add(new Token(TokenKind.Slash, "/"));
                index++;
            }
            else if (char.IsDigit(ch))
            {
                var start = index;
                while (index < input.Length && (char.IsDigit(input[index]) || input[index] == '.'))
                    index++;
                tokens.Add(new Token(TokenKind.Number, input[start..index]));
            }
            else if (char.IsLetter(ch) || ch == '_')
            {
                var builder = new StringBuilder();
                while (index < input.Length)
                {
                    var c = input[index];
                    if (char.IsLetterOrDigit(c) || c is '_' or '.')
                    {
                        builder.Append(c);
                        index++;
                    }
                    else if (c == '[')
                    {
                        // Bracket column syntax (FIELDS.[T_ID] / [FIELDS.T_ID]): part of the same identifier
                        var close = input.IndexOf(']', index + 1);
                        if (close < 0) throw new FormatException("未闭合的方括号：列引用非法。");
                        builder.Append(input, index + 1, close - index - 1);
                        index = close + 1;
                    }
                    else
                    {
                        break;
                    }
                }
                var text = builder.ToString();
                tokens.Add(new Token(
                    text.Equals("and", StringComparison.OrdinalIgnoreCase) || text.Equals("or", StringComparison.OrdinalIgnoreCase)
                        ? TokenKind.AndOr
                        : TokenKind.Identifier,
                    text));
            }
            else if (ch == '[')
            {
                // 以方括号开头的列引用（[FIELDS.T_ID]）：剥离括号后作为单个标识符
                var close = input.IndexOf(']', index + 1);
                if (close < 0) throw new FormatException("未闭合的方括号：列引用非法。");
                tokens.Add(new Token(TokenKind.Identifier, input[(index + 1)..close]));
                index = close + 1;
            }
            else
            {
                throw new FormatException($"不支持的字符：{ch}");
            }
        }
        return tokens;
    }

    private enum TokenKind
    {
        Identifier,
        Literal,
        Number,
        Operator,
        Comma,
        Plus,
        Minus,
        Asterisk,
        Slash,
        LeftParen,
        RightParen,
        AndOr,
    }

    private sealed record Token(TokenKind Kind, string Text);
}
