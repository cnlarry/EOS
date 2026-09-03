using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// GROUP_EXP（MODULES 分组表达式）受限解析器。
/// 分组表达式是直接拼接进 `SELECT DISTINCT &lt;GROUP_EXP&gt; FROM &lt;MASTER_TABLE&gt;`
/// 的高风险表达式（与 DATA_FILTER 同级），本解析器只接受：
/// 白名单列（可带 MASTER_TABLE 前缀）、字符串/数字字面量、+ - * /、括号、
/// CASE（simple/searched）、白名单函数 YEAR/MONTH/DATEPART/REPLICATE/CAST/CONVERT。
/// 输出为完全重写的 SELECT 表达式：标识符仅可能来自白名单，字面量由解析器规范化内联
/// （N'..' 转义 / 数字原文），不存在任何用户输入拼接。
/// 其余 token / 函数 / 越权列一律失败（返回 false，调用方必须拒绝执行或隐藏该组）。
/// </summary>
internal static class GroupExpressionParser
{
    private const int MaxDepth = 8;
    private static readonly Regex Number = new("^[0-9]+(\\.[0-9]+)?$", RegexOptions.Compiled);

    public static bool TryCompile(
        string expression,
        string masterTable,
        IReadOnlySet<string> allowedFields,
        out string selectExpression)
    {
        selectExpression = string.Empty;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        try
        {
            var tokens = Tokenize(expression);
            var position = 0;
            if (!ParseExpr(tokens, ref position, masterTable, allowedFields, out var compiled, 0)) return false;
            if (position != tokens.Count || string.IsNullOrWhiteSpace(compiled)) return false;
            selectExpression = compiled;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // expr := term (('+'|'-') term)*
    private static bool ParseExpr(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        out string expression,
        int depth)
    {
        expression = string.Empty;
        if (depth > MaxDepth) return false;
        if (!ParseTerm(tokens, ref position, masterTable, allowed, out var left, depth)) return false;
        var parts = new List<string> { left };
        while (position < tokens.Count && tokens[position].Kind is TokenKind.Plus or TokenKind.Minus)
        {
            var op = tokens[position].Text;
            position++;
            if (!ParseTerm(tokens, ref position, masterTable, allowed, out var right, depth)) return false;
            parts.Add($"{op}{right}");
        }
        expression = string.Join(string.Empty, parts);
        return true;
    }

    // term := factor (('*'|'/') factor)*
    private static bool ParseTerm(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        out string expression,
        int depth)
    {
        expression = string.Empty;
        if (!ParseFactor(tokens, ref position, masterTable, allowed, out var left, depth)) return false;
        var parts = new List<string> { left };
        while (position < tokens.Count && tokens[position].Kind is TokenKind.Asterisk or TokenKind.Slash)
        {
            var op = tokens[position].Text;
            position++;
            if (!ParseFactor(tokens, ref position, masterTable, allowed, out var right, depth)) return false;
            parts.Add($"{op}{right}");
        }
        expression = string.Join(string.Empty, parts);
        return true;
    }

    private static bool ParseFactor(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        out string expression,
        int depth)
    {
        expression = string.Empty;
        if (position >= tokens.Count) return false;
        var token = tokens[position];
        switch (token.Kind)
        {
            case TokenKind.LeftParen:
            {
                position++;
                if (!ParseExpr(tokens, ref position, masterTable, allowed, out var inner, depth + 1)) return false;
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.RightParen) return false;
                position++;
                expression = $"({inner})";
                return true;
            }
            case TokenKind.Literal:
            {
                position++;
                expression = "N'" + token.Text.Replace("'", "''") + "'";
                return true;
            }
            case TokenKind.Number:
            {
                if (!Number.IsMatch(token.Text)) return false;
                position++;
                expression = token.Text;
                return true;
            }
            case TokenKind.Identifier:
            {
                // CASE 表达式
                if (token.Text.Equals("case", StringComparison.OrdinalIgnoreCase))
                    return ParseCase(tokens, ref position, masterTable, allowed, out expression, depth + 1);
                // 函数调用：identifier '('
                if (position + 1 < tokens.Count && tokens[position + 1].Kind == TokenKind.LeftParen)
                    return ParseFunction(tokens, ref position, masterTable, allowed, out expression, depth + 1);
                // 白名单列引用：TABLE.COL 或 COL
                if (!TryResolveField(token.Text, masterTable, allowed, out var field)) return false;
                position++;
                expression = $"[{field}]";
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// 支持 simple CASE（CASE expr WHEN val THEN expr ...）与 searched CASE
    /// （CASE WHEN pred THEN expr ...），可带 ELSE，必须 END 结尾。
    /// </summary>
    private static bool ParseCase(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        out string expression,
        int depth)
    {
        expression = string.Empty;
        position++; // case
        var builder = new StringBuilder("CASE");
        var searched = false;
        if (position < tokens.Count
            && tokens[position].Kind == TokenKind.Identifier
            && tokens[position].Text.Equals("when", StringComparison.OrdinalIgnoreCase))
        {
            searched = true;
        }
        else
        {
            if (!ParseExpr(tokens, ref position, masterTable, allowed, out var operand, depth)) return false;
            builder.Append(' ').Append(operand);
        }
        var sawWhen = false;
        while (position < tokens.Count
               && tokens[position].Kind == TokenKind.Identifier
               && tokens[position].Text.Equals("when", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            if (searched)
            {
                if (!ParsePredicate(tokens, ref position, masterTable, allowed, out var condition, depth)) return false;
                if (!ExpectKeyword(tokens, ref position, "then")) return false;
                if (!ParseExpr(tokens, ref position, masterTable, allowed, out var result, depth)) return false;
                builder.Append(" WHEN ").Append(condition).Append(" THEN ").Append(result);
            }
            else
            {
                if (!ParseScalar(tokens, ref position, masterTable, allowed, out var whenValue, depth)) return false;
                if (!ExpectKeyword(tokens, ref position, "then")) return false;
                if (!ParseExpr(tokens, ref position, masterTable, allowed, out var result, depth)) return false;
                builder.Append(" WHEN ").Append(whenValue).Append(" THEN ").Append(result);
            }
            sawWhen = true;
        }
        if (!sawWhen) return false;
        if (position < tokens.Count
            && tokens[position].Kind == TokenKind.Identifier
            && tokens[position].Text.Equals("else", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            if (!ParseExpr(tokens, ref position, masterTable, allowed, out var elseValue, depth)) return false;
            builder.Append(" ELSE ").Append(elseValue);
        }
        if (!ExpectKeyword(tokens, ref position, "end")) return false;
        expression = builder.Append(" END").ToString();
        return true;
    }

    /// <summary>searched CASE 的 WHEN 条件：白名单列（或函数表达式）与标量的比较。</summary>
    private static bool ParsePredicate(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        out string expression,
        int depth)
    {
        expression = string.Empty;
        if (!ParseFactor(tokens, ref position, masterTable, allowed, out var left, depth)) return false;
        if (position < tokens.Count && tokens[position].Kind == TokenKind.Operator)
        {
            var op = tokens[position].Text;
            if (op is not ("=" or "<>" or ">" or "<" or ">=" or "<=")) return false;
            position++;
            if (!ParseScalar(tokens, ref position, masterTable, allowed, out var right, depth)) return false;
            expression = $"{left} {op} {right}";
            return true;
        }
        // 无比较符视为布尔列
        return false;
    }

    /// <summary>simple CASE 的 WHEN 值 / 谓词右值：仅字面量、数字或白名单列。</summary>
    private static bool ParseScalar(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        out string expression,
        int depth)
    {
        expression = string.Empty;
        if (position >= tokens.Count) return false;
        var token = tokens[position];
        switch (token.Kind)
        {
            case TokenKind.Literal:
                position++;
                expression = "N'" + token.Text.Replace("'", "''") + "'";
                return true;
            case TokenKind.Number:
                if (!Number.IsMatch(token.Text)) return false;
                position++;
                expression = token.Text;
                return true;
            case TokenKind.Identifier:
                if (!TryResolveField(token.Text, masterTable, allowed, out var field)) return false;
                position++;
                expression = $"[{field}]";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 白名单函数：YEAR(date)、MONTH(date)、DATEPART(part,date)（part 限
    /// year/month/week/day）、REPLICATE(expr,expr)、CAST(expr AS varchar[(n)])、
    /// CONVERT(varchar[(n)], expr [, style])。其余一律拒绝。
    /// </summary>
    private static bool ParseFunction(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        out string expression,
        int depth)
    {
        expression = string.Empty;
        if (depth > MaxDepth) return false;
        var name = tokens[position].Text.ToLowerInvariant();
        position++; // 函数名
        position++; // '('
        switch (name)
        {
            case "year":
            case "month":
            {
                if (!ParseExpr(tokens, ref position, masterTable, allowed, out var arg, depth)) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
                expression = $"{name.ToUpperInvariant()}({arg})";
                return true;
            }
            case "datepart":
            {
                if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier) return false;
                var part = tokens[position].Text.ToLowerInvariant();
                if (part is not ("year" or "month" or "week" or "day")) return false;
                position++;
                if (!ExpectToken(tokens, ref position, TokenKind.Comma)) return false;
                if (!ParseExpr(tokens, ref position, masterTable, allowed, out var dateArg, depth)) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
                expression = $"DATEPART({part},{dateArg})";
                return true;
            }
            case "replicate":
            {
                if (!ParseExpr(tokens, ref position, masterTable, allowed, out var value, depth)) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.Comma)) return false;
                if (!ParseExpr(tokens, ref position, masterTable, allowed, out var count, depth)) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
                expression = $"REPLICATE({value},{count})";
                return true;
            }
            case "cast":
            {
                if (!ParseExpr(tokens, ref position, masterTable, allowed, out var value, depth)) return false;
                if (!ExpectKeyword(tokens, ref position, "as")) return false;
                if (!TryParseVarcharType(tokens, ref position, out var type, out var length)) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
                expression = $"CAST({value} AS {type}{length})";
                return true;
            }
            case "convert":
            {
                if (!TryParseVarcharType(tokens, ref position, out var type, out var length)) return false;
                if (!ExpectToken(tokens, ref position, TokenKind.Comma)) return false;
                if (!ParseExpr(tokens, ref position, masterTable, allowed, out var value, depth)) return false;
                var style = string.Empty;
                if (position < tokens.Count && tokens[position].Kind == TokenKind.Comma)
                {
                    position++;
                    if (position >= tokens.Count || tokens[position].Kind != TokenKind.Number
                        || !Number.IsMatch(tokens[position].Text)) return false;
                    style = ", " + tokens[position].Text;
                    position++;
                }
                if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
                expression = $"CONVERT({type}{length},{value}{style})";
                return true;
            }
            default:
                return false;
        }
    }

    private static bool TryParseVarcharType(
        IReadOnlyList<Token> tokens,
        ref int position,
        out string type,
        out string length)
    {
        type = string.Empty;
        length = string.Empty;
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier) return false;
        var name = tokens[position].Text.ToLowerInvariant();
        if (name is not ("varchar" or "nvarchar")) return false;
        position++;
        if (position < tokens.Count && tokens[position].Kind == TokenKind.LeftParen)
        {
            position++;
            if (position >= tokens.Count || tokens[position].Kind != TokenKind.Number
                || !int.TryParse(tokens[position].Text, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                || n <= 0 || n > 8000) return false;
            length = $"({n})";
            position++;
            if (!ExpectToken(tokens, ref position, TokenKind.RightParen)) return false;
        }
        type = name.ToUpperInvariant();
        return true;
    }

    private static bool TryResolveField(string identifier, string masterTable, IReadOnlySet<string> allowed, out string field)
    {
        field = string.Empty;
        var parts = identifier.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            field = parts[0];
        }
        else if (parts.Length == 2)
        {
            if (!parts[0].Equals(masterTable, StringComparison.OrdinalIgnoreCase)) return false;
            field = parts[1];
        }
        else
        {
            return false;
        }
        return WorkbenchSql.Identifier.IsMatch(field) && allowed.Contains(field);
    }

    private static bool ExpectKeyword(IReadOnlyList<Token> tokens, ref int position, string keyword)
    {
        if (position >= tokens.Count
            || tokens[position].Kind != TokenKind.Identifier
            || !tokens[position].Text.Equals(keyword, StringComparison.OrdinalIgnoreCase))
            return false;
        position++;
        return true;
    }

    private static bool ExpectToken(IReadOnlyList<Token> tokens, ref int position, TokenKind kind)
    {
        if (position >= tokens.Count || tokens[position].Kind != kind) return false;
        position++;
        return true;
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
            else if (ch is '(' or ')')
            {
                tokens.Add(new Token(ch == '(' ? TokenKind.LeftParen : TokenKind.RightParen, ch.ToString()));
                index++;
            }
            else if (ch is '=' or '<' or '>')
            {
                var start = index;
                index++;
                if (index < input.Length && (input[index] == '=' || (ch == '<' && input[index] == '>')))
                    index++;
                tokens.Add(new Token(TokenKind.Operator, input[start..index]));
            }
            else if (ch == '\'')
            {
                var builder = new StringBuilder();
                index++;
                var closed = false;
                while (index < input.Length)
                {
                    if (input[index] == '\'')
                    {
                        if (index + 1 < input.Length && input[index + 1] == '\'')
                        {
                            builder.Append('\'');
                            index += 2;
                        }
                        else
                        {
                            index++;
                            closed = true;
                            break;
                        }
                    }
                    else
                    {
                        builder.Append(input[index]);
                        index++;
                    }
                }
                if (!closed) throw new FormatException("字符串未闭合");
                tokens.Add(new Token(TokenKind.Literal, builder.ToString()));
            }
            else if (ch == ',')
            {
                tokens.Add(new Token(TokenKind.Comma, ","));
                index++;
            }
            else if (ch is '+' or '-' or '*' or '/')
            {
                tokens.Add(new Token(ch switch
                {
                    '+' => TokenKind.Plus,
                    '-' => TokenKind.Minus,
                    '*' => TokenKind.Asterisk,
                    _ => TokenKind.Slash,
                }, ch.ToString()));
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
                var start = index;
                while (index < input.Length && (char.IsLetterOrDigit(input[index]) || input[index] is '_' or '.'))
                    index++;
                tokens.Add(new Token(TokenKind.Identifier, input[start..index]));
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
    }

    private sealed record Token(TokenKind Kind, string Text);
}
