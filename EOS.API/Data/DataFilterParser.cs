using System.Text;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// DATA_FILTER 受限解析（M2 安全子集）。
/// 仅接受「白名单字段 = '值'」谓词（字段可带主表名前缀），AND/OR 组合与括号（深度 ≤ 8），
/// 值仅限单引号字符串字面量（'' 转义）。解析成功编译为参数化谓词；其余一律拒绝。
/// 空过滤由调用方视为"无行级限制"；解析失败时调用方必须拒绝执行（读/写返回 403）。
/// </summary>
internal static class DataFilterParser
{
    private const int MaxDepth = 8;
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    public static bool TryParse(
        string? filter,
        string masterTable,
        IReadOnlySet<string> allowedFields,
        out string predicate,
        out IReadOnlyList<object> parameters)
    {
        predicate = string.Empty;
        parameters = [];
        if (string.IsNullOrWhiteSpace(filter)) return false;
        try
        {
            var tokens = Tokenize(filter);
            var position = 0;
            var values = new List<object>();
            if (!ParseOr(tokens, ref position, masterTable, allowedFields, out var expression, values, 0))
                return false;
            if (position != tokens.Count || string.IsNullOrWhiteSpace(expression)) return false;
            predicate = expression;
            parameters = values;
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
        out string expression,
        List<object> values,
        int depth)
    {
        if (depth > MaxDepth) { expression = string.Empty; return false; }
        if (!ParseAnd(tokens, ref position, masterTable, allowed, out var left, values, depth))
        {
            expression = string.Empty;
            return false;
        }
        var parts = new List<string> { left };
        while (position < tokens.Count && tokens[position].Kind == TokenKind.AndOr
               && tokens[position].Text.Equals("or", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            if (!ParseAnd(tokens, ref position, masterTable, allowed, out var right, values, depth))
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
        out string expression,
        List<object> values,
        int depth)
    {
        if (!ParsePrimary(tokens, ref position, masterTable, allowed, out var left, values, depth))
        {
            expression = string.Empty;
            return false;
        }
        var parts = new List<string> { left };
        while (position < tokens.Count && tokens[position].Kind == TokenKind.AndOr
               && tokens[position].Text.Equals("and", StringComparison.OrdinalIgnoreCase))
        {
            position++;
            if (!ParsePrimary(tokens, ref position, masterTable, allowed, out var right, values, depth))
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
        out string expression,
        List<object> values,
        int depth)
    {
        if (position >= tokens.Count)
        {
            expression = string.Empty;
            return false;
        }
        if (tokens[position].Kind == TokenKind.LeftParen)
        {
            position++;
            if (!ParseOr(tokens, ref position, masterTable, allowed, out var inner, values, depth + 1))
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
            expression = $"({inner})";
            return true;
        }
        return ParsePredicate(tokens, ref position, masterTable, allowed, out expression, values);
    }

    private static bool ParsePredicate(
        IReadOnlyList<Token> tokens,
        ref int position,
        string masterTable,
        IReadOnlySet<string> allowed,
        out string expression,
        List<object> values)
    {
        expression = string.Empty;
        if (position + 2 >= tokens.Count) return false;
        if (tokens[position].Kind != TokenKind.Identifier
            || tokens[position + 1].Kind != TokenKind.Equals
            || tokens[position + 2].Kind != TokenKind.Literal) return false;
        if (!TryResolveField(tokens[position].Text, masterTable, allowed, out var field)) return false;
        var parameterName = $"@df{values.Count}";
        values.Add(tokens[position + 2].Text);
        position += 3;
        expression = $"[{field}] = {parameterName}";
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
        return Identifier.IsMatch(field) && allowed.Contains(field);
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
                tokens.Add(new Token(TokenKind.Equals, "="));
                index++;
            }
            else if (ch == '\'')
            {
                var builder = new StringBuilder();
                index++;
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
            else if (char.IsLetter(ch) || ch == '_')
            {
                var start = index;
                while (index < input.Length && (char.IsLetterOrDigit(input[index]) || input[index] is '_' or '.'))
                    index++;
                var text = input[start..index];
                tokens.Add(new Token(
                    text.Equals("and", StringComparison.OrdinalIgnoreCase) || text.Equals("or", StringComparison.OrdinalIgnoreCase)
                        ? TokenKind.AndOr
                        : TokenKind.Identifier,
                    text));
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
        Equals,
        LeftParen,
        RightParen,
        AndOr,
    }

    private sealed record Token(TokenKind Kind, string Text);
}
