using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>
/// DATA_FILTER / MODULES.FILTER 受限解析（M2 安全子集 + M79 受控扩展）。
/// 接受「白名单字段 运算符 值」谓词（字段可带主表名前缀），AND/OR 组合与括号（深度 ≤ 8）。
/// 运算符：=、&lt;&gt;、&gt;、&lt;、&gt;=、&lt;=。
/// 值（右侧）限单引号字符串（'' 转义）、数字字面量，或受控函数表达式：
///   getdate()、dateadd(month,&lt;整数&gt;,getdate())、convert(varchar(7),&lt;日期表达式&gt;,120)、
///   及字符串拼接（`+ '字面量'`，如 convert(...)+'-26'）；函数在服务端求值为常量后参数化。
/// 左侧支持同表白名单列间算术（+ - * /，操作数为列或数字），如 QTY-RECEIVE_QTY&gt;0。
/// 解析成功编译为参数化谓词；其余一律拒绝。
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
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Identifier) return false;
        if (!TryResolveField(tokens[position].Text, masterTable, allowed, out var field)) return false;
        var left = $"[{field}]";
        position++;
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
                if (!TryResolveField(tokens[position].Text, masterTable, allowed, out var operand)) return false;
                left += $"{arithOp}[{operand}]";
                position++;
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
        if (position >= tokens.Count || tokens[position].Kind != TokenKind.Operator) return false;
        var comparison = tokens[position].Text;
        if (comparison is not ("=" or "<>" or ">" or "<" or ">=" or "<=")) return false;
        position++;
        // 直接列比较的数值字面量以字符串参数绑定（如 PRO_TYPE=1，PRO_TYPE 为 char，
        // 若绑 decimal 会触发 char→numeric 隐式转换，含非数字值时报 8114）；
        // 列间算术的右值保持 decimal（如 QTY-RECEIVE_QTY>0 的 0）。
        if (!ParseValueExpression(tokens, ref position, out var value, numericAsString: !leftIsArithmetic)) return false;
        var parameterName = $"@df{values.Count}";
        values.Add(value);
        expression = $"{left} {comparison} {parameterName}";
        return true;
    }

    private static bool IsArithmeticOperator(Token token) =>
        token.Kind is TokenKind.Plus or TokenKind.Minus or TokenKind.Asterisk or TokenKind.Slash;

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
    /// convert(varchar(7),&lt;日期表达式&gt;,120)（等价 yyyy-MM）。
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
