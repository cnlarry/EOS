using System.Text;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>受控表达式 token（P5/P6）：Ref / Number / String / Func / Case 结构 / 比较 / 运算符。</summary>
internal sealed record VirtualArithmeticToken(string Kind, string? Table, string? Column, string Text);

/// <summary>
/// VIRTUAL_EXP 受控表达式解析器（2026-08-15，P5 算术 + P6 条件/函数）：
/// - 支持：`表.列` 引用、数值/字符串字面量、`+ - * /`、括号、一元正负号、
///   字符串拼接、比较（= &lt;&gt; &lt; &lt;= &gt; &gt;=）、AND/OR、
///   CASE WHEN（searched/simple，可嵌套）、白名单函数
///   （ROUND(x,n)/CEILING(x)/DATENAME(unit,x)/DATEDIFF(unit,x,y)/GETDATE()）；
/// - 拒绝：未白名单函数、子查询、分号/注释、裸标识符、其它字符。
/// 纯语法解析不访问数据库；表/列白名单与物理存在性由调用方校验。
/// </summary>
internal static class VirtualArithmeticParser
{
    private static readonly Regex NumberPattern = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, (int MinArgs, int MaxArgs, bool FirstArgString)> Functions =
        new Dictionary<string, (int, int, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["ROUND"] = (2, 2, false),
            ["CEILING"] = (1, 1, false),
            ["DATENAME"] = (2, 2, true),
            ["DATEDIFF"] = (3, 3, true),
            ["GETDATE"] = (0, 0, false),
            ["RTRIM"] = (1, 1, false),
            ["LTRIM"] = (1, 1, false),
            ["f_get_unit_type_desc"] = (1, 1, false),
            ["CAST"] = (2, 2, false),
        };

    /// <summary>CAST 的目标类型白名单（AS char(20) 等）。</summary>
    private static readonly IReadOnlySet<string> CastTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "char", "varchar", "nchar", "nvarchar", "int", "bigint", "smallint", "tinyint",
        "decimal", "numeric", "float", "real", "money", "smallmoney", "date", "datetime", "bit",
    };

    /// <summary>DATENAME/DATEDIFF 的日期单位关键字（旧系统不加引号，如 MM/WEEKDAY）。</summary>
    private static readonly IReadOnlySet<string> DateParts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "YEAR", "QUARTER", "MONTH", "DAYOFYEAR", "DAY", "WEEK", "WEEKDAY", "HOUR", "MINUTE", "SECOND",
        "MILLISECOND", "MICROSECOND", "NANOSECOND", "ISO_WEEK", "MM", "YY", "DD", "HH", "MI", "SS",
    };

    public static bool TryParse(string expression, out IReadOnlyList<VirtualArithmeticToken> tokens, out string? error)
    {
        tokens = [];
        error = null;
        var raw = expression.Trim();
        if (raw.Length == 0) { error = "表达式为空。"; return false; }
        if (raw.Contains(';') || raw.IndexOf("--", StringComparison.Ordinal) >= 0 || raw.IndexOf("/*", StringComparison.Ordinal) >= 0)
        {
            error = "表达式不允许分号或注释。";
            return false;
        }

        var parsed = new List<VirtualArithmeticToken>();
        var index = 0;
        while (index < raw.Length)
        {
            var ch = raw[index];
            if (char.IsWhiteSpace(ch)) { index++; continue; }
            if (ch is '+' or '-' or '*' or '/' or '(' or ')' or ',')
            {
                parsed.Add(new VirtualArithmeticToken(ch is '(' ? "LParen" : ch is ')' ? "RParen" : ch == ',' ? "Comma" : "Op", null, null, ch.ToString()));
                index++;
                continue;
            }
            if (ch is '=' or '<' or '>')
            {
                if (ch == '<' && index + 1 < raw.Length && raw[index + 1] == '>')
                {
                    parsed.Add(new VirtualArithmeticToken("Cmp", null, null, "<>"));
                    index += 2;
                    continue;
                }
                if (ch == '<' && index + 1 < raw.Length && raw[index + 1] == '=') { parsed.Add(new VirtualArithmeticToken("Cmp", null, null, "<=")); index += 2; continue; }
                if (ch == '>' && index + 1 < raw.Length && raw[index + 1] == '=') { parsed.Add(new VirtualArithmeticToken("Cmp", null, null, ">=")); index += 2; continue; }
                parsed.Add(new VirtualArithmeticToken("Cmp", null, null, ch.ToString()));
                index++;
                continue;
            }
            if (ch == '\'')
            {
                var (literal, next) = ReadStringLiteral(raw, index);
                if (literal is null) { error = "字符串字面量格式错误（须成对单引号，'' 转义）。"; return false; }
                parsed.Add(new VirtualArithmeticToken("String", null, null, literal));
                index = next;
                continue;
            }
            if (char.IsDigit(ch))
            {
                var start = index;
                while (index < raw.Length && (char.IsDigit(raw[index]) || raw[index] == '.')) index++;
                var number = raw[start..index];
                if (!NumberPattern.IsMatch(number)) { error = $"数值字面量无效：{number}"; return false; }
                parsed.Add(new VirtualArithmeticToken("Number", null, null, number));
                continue;
            }
            if (char.IsLetter(ch) || ch == '_')
            {
                var start = index;
                while (index < raw.Length && (char.IsLetterOrDigit(raw[index]) || raw[index] == '_')) index++;
                var word = raw[start..index];
                if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase)) { parsed.Add(new VirtualArithmeticToken("Case", null, null, "CASE")); continue; }
                if (word.Equals("WHEN", StringComparison.OrdinalIgnoreCase)) { parsed.Add(new VirtualArithmeticToken("When", null, null, "WHEN")); continue; }
                if (word.Equals("THEN", StringComparison.OrdinalIgnoreCase)) { parsed.Add(new VirtualArithmeticToken("Then", null, null, "THEN")); continue; }
                if (word.Equals("ELSE", StringComparison.OrdinalIgnoreCase)) { parsed.Add(new VirtualArithmeticToken("Else", null, null, "ELSE")); continue; }
                if (word.Equals("END", StringComparison.OrdinalIgnoreCase)) { parsed.Add(new VirtualArithmeticToken("End", null, null, "END")); continue; }
                if (word.Equals("AND", StringComparison.OrdinalIgnoreCase)) { parsed.Add(new VirtualArithmeticToken("And", null, null, "AND")); continue; }
                if (word.Equals("OR", StringComparison.OrdinalIgnoreCase)) { parsed.Add(new VirtualArithmeticToken("Or", null, null, "OR")); continue; }
                if (word.Equals("AS", StringComparison.OrdinalIgnoreCase)) { parsed.Add(new VirtualArithmeticToken("As", null, null, "AS")); continue; }
                if (word.Equals("dbo", StringComparison.OrdinalIgnoreCase) && index < raw.Length && raw[index] == '.')
                {
                    index++;
                    var fnStart = index;
                    while (index < raw.Length && (char.IsLetterOrDigit(raw[index]) || raw[index] == '_')) index++;
                    var functionName = raw[fnStart..index];
                    if (functionName.Length == 0 || index >= raw.Length || raw[index] != '(' || !Functions.ContainsKey(functionName))
                    {
                        error = $"未知受控函数：dbo.{functionName}。";
                        return false;
                    }
                    parsed.Add(new VirtualArithmeticToken("Func", null, null, functionName));
                    continue;
                }
                if (DateParts.Contains(word))
                {
                    parsed.Add(new VirtualArithmeticToken("Keyword", null, null, word));
                    continue;
                }
                if (CastTypes.Contains(word))
                {
                    parsed.Add(new VirtualArithmeticToken("Keyword", null, null, word));
                    continue;
                }
                if (index < raw.Length && raw[index] == '(' && Functions.ContainsKey(word))
                {
                    parsed.Add(new VirtualArithmeticToken("Func", null, null, word));
                    continue;
                }
                if (index < raw.Length && raw[index] == '(')
                {
                    error = $"未知函数：{word}（受控函数白名单：ROUND/CEILING/DATENAME/DATEDIFF/GETDATE/CAST/RTRIM/LTRIM/f_get_unit_type_desc）。";
                    return false;
                }
                if (index < raw.Length && raw[index] == '.')
                {
                    index++;
                    var columnStart = index;
                    while (index < raw.Length && (char.IsLetterOrDigit(raw[index]) || raw[index] == '_')) index++;
                    var column = raw[columnStart..index];
                    if (column.Length == 0 || !WorkbenchSql.Identifier.IsMatch(column)) { error = $"列名无效：{word}."; return false; }
                    if (!WorkbenchSql.Identifier.IsMatch(word)) { error = $"表名无效：{word}"; return false; }
                    parsed.Add(new VirtualArithmeticToken("Ref", word, column, $"{word}.{column}"));
                    continue;
                }
                // Unqualified column reference (e.g. DATEDIFF(MM,IN_DATE,GETDATE())): treat as base-table column.
                // The caller resolves the base table; if the column does not exist physically, it is safely degraded.
                parsed.Add(new VirtualArithmeticToken("Ref", null, word, word));
                continue;
            }
            error = $"表达式包含不允许的字符：{ch}";
            return false;
        }

        var cursor = 0;
        if (!ParseOr(parsed, ref cursor, out error)) return false;
        if (cursor != parsed.Count)
        {
            error = "表达式末尾存在多余内容。";
            return false;
        }
        tokens = parsed;
        return true;
    }

    private static bool ParseOr(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
    {
        if (!ParseAnd(tokens, ref cursor, out error)) return false;
        while (cursor < tokens.Count && tokens[cursor].Kind == "Or")
        {
            cursor++;
            if (!ParseAnd(tokens, ref cursor, out error)) return false;
        }
        return true;
    }

    private static bool ParseAnd(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
    {
        if (!ParseComparison(tokens, ref cursor, out error)) return false;
        while (cursor < tokens.Count && tokens[cursor].Kind == "And")
        {
            cursor++;
            if (!ParseComparison(tokens, ref cursor, out error)) return false;
        }
        return true;
    }

    private static bool ParseComparison(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
    {
        if (!ParseAdditive(tokens, ref cursor, out error)) return false;
        if (cursor < tokens.Count && tokens[cursor].Kind == "Cmp")
        {
            cursor++;
            if (!ParseAdditive(tokens, ref cursor, out error)) return false;
        }
        return true;
    }

    private static bool ParseAdditive(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
    {
        if (!ParseTerm(tokens, ref cursor, out error)) return false;
        while (cursor < tokens.Count && tokens[cursor] is { Kind: "Op", Text: "+" or "-" })
        {
            cursor++;
            if (!ParseTerm(tokens, ref cursor, out error)) return false;
        }
        return true;
    }

    private static bool ParseTerm(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
    {
        if (!ParseFactor(tokens, ref cursor, out error)) return false;
        while (cursor < tokens.Count && tokens[cursor] is { Kind: "Op", Text: "*" or "/" })
        {
            cursor++;
            if (!ParseFactor(tokens, ref cursor, out error)) return false;
        }
        return true;
    }

    private static bool ParseFactor(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
    {
        if (cursor < tokens.Count && tokens[cursor] is { Kind: "Op", Text: "+" or "-" })
            cursor++;
        return ParsePrimary(tokens, ref cursor, out error);
    }

    private static bool ParsePrimary(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
    {
        if (cursor >= tokens.Count)
        {
            error = "表达式不完整（缺少操作数）。";
            return false;
        }
        var token = tokens[cursor];
        if (token.Kind is "Ref" or "Number" or "String")
        {
            cursor++;
            error = null;
            return true;
        }
        if (token.Kind == "LParen")
        {
            cursor++;
            if (!ParseOr(tokens, ref cursor, out error)) return false;
            if (cursor >= tokens.Count || tokens[cursor].Kind != "RParen")
            {
                error = "括号不匹配。";
                return false;
            }
            cursor++;
            error = null;
            return true;
        }
        if (token.Kind == "Func")
            return ParseFunction(tokens, ref cursor, out error);
        if (token.Kind == "Case")
            return ParseCase(tokens, ref cursor, out error);
        error = "表达式不完整（预期列引用、数值、字符串、函数、CASE 或括号）。";
        return false;
    }

    private static bool ParseFunction(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
    {
        var name = tokens[cursor].Text;
        var spec = Functions[name];
        cursor++;
        if (cursor >= tokens.Count || tokens[cursor].Kind != "LParen")
        {
            error = $"函数 {name} 缺少左括号。";
            return false;
        }
        cursor++;
        var argCount = 0;
        if (name.Equals("CAST", StringComparison.OrdinalIgnoreCase))
        {
            if (!ParseOr(tokens, ref cursor, out error)) return false;
            if (cursor >= tokens.Count || tokens[cursor].Kind != "As")
            {
                error = "CAST 缺少 AS 类型。";
                return false;
            }
            cursor++;
            if (cursor >= tokens.Count || tokens[cursor].Kind != "Keyword")
            {
                error = "CAST 目标类型不在白名单内（char/varchar/nvarchar/int/decimal 等）。";
                return false;
            }
            cursor++;
            if (cursor < tokens.Count && tokens[cursor].Kind == "LParen")
            {
                cursor++;
                if (cursor >= tokens.Count || tokens[cursor].Kind != "Number")
                {
                    error = "CAST 类型长度参数无效。";
                    return false;
                }
                cursor++;
                if (cursor >= tokens.Count || tokens[cursor].Kind != "RParen")
                {
                    error = "CAST 类型长度括号不匹配。";
                    return false;
                }
                cursor++;
            }
            if (cursor >= tokens.Count || tokens[cursor].Kind != "RParen")
            {
                error = "CAST 缺少右括号。";
                return false;
            }
            cursor++;
            error = null;
            return true;
        }
        if (cursor < tokens.Count && tokens[cursor].Kind == "RParen")
        {
            cursor++;
        }
        else
        {
            while (true)
            {
                if (spec.FirstArgString && argCount == 0)
                {
                    if (cursor >= tokens.Count || tokens[cursor].Kind is not ("String" or "Keyword"))
                    {
                        error = $"函数 {name} 第一个参数必须是字符串字面量或日期单位关键字（如 'WEEKDAY'/MM）。";
                        return false;
                    }
                    cursor++;
                }
                else if (!ParseOr(tokens, ref cursor, out error))
                {
                    return false;
                }
                argCount++;
                if (cursor >= tokens.Count)
                {
                    error = $"函数 {name} 参数不完整。";
                    return false;
                }
                if (tokens[cursor].Kind == "RParen") { cursor++; break; }
                if (tokens[cursor].Kind != "Comma")
                {
                    error = $"函数 {name} 参数间缺少逗号。";
                    return false;
                }
                cursor++;
            }
        }
        if (argCount < spec.MinArgs || argCount > spec.MaxArgs)
        {
            error = $"函数 {name} 参数个数须为 {spec.MinArgs}~{spec.MaxArgs}（实际 {argCount}）。";
            return false;
        }
        error = null;
        return true;
    }

    private static bool ParseCase(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
    {
        cursor++;
        var searched = cursor < tokens.Count && tokens[cursor].Kind == "When";
        if (!searched)
        {
            if (!ParseOr(tokens, ref cursor, out error)) return false;
        }
        var branches = 0;
        while (cursor < tokens.Count && tokens[cursor].Kind == "When")
        {
            cursor++;
            if (searched)
            {
                if (!ParseOr(tokens, ref cursor, out error)) return false;
            }
            else if (!ParseAdditive(tokens, ref cursor, out error))
            {
                return false;
            }
            if (cursor >= tokens.Count || tokens[cursor].Kind != "Then")
            {
                error = "CASE WHEN 缺少 THEN。";
                return false;
            }
            cursor++;
            if (!ParseOr(tokens, ref cursor, out error)) return false;
            branches++;
        }
        if (branches == 0)
        {
            error = "CASE 至少需要一个 WHEN 分支。";
            return false;
        }
        if (cursor < tokens.Count && tokens[cursor].Kind == "Else")
        {
            cursor++;
            if (!ParseOr(tokens, ref cursor, out error)) return false;
        }
        if (cursor >= tokens.Count || tokens[cursor].Kind != "End")
        {
            error = "CASE 缺少 END。";
            return false;
        }
        cursor++;
        error = null;
        return true;
    }

    private static (string? Literal, int Next) ReadStringLiteral(string text, int start)
    {
        var builder = new StringBuilder();
        var index = start + 1;
        while (index < text.Length)
        {
            if (text[index] == '\'')
            {
                if (index + 1 < text.Length && text[index + 1] == '\'')
                {
                    builder.Append('\'');
                    index += 2;
                    continue;
                }
                return (builder.ToString(), index + 1);
            }
            builder.Append(text[index]);
            index++;
        }
        return (null, text.Length);
    }
}
