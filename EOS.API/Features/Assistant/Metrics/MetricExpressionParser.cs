namespace EOS.API.Features.Assistant.Metrics;

/// <summary>
/// Controlled metric expression AST. Only node kinds produced by
/// <see cref="MetricExpressionParser"/> are representable; the plan compiler
/// renders SQL exclusively from these nodes, so any construct outside the
/// whitelist simply cannot be expressed.
/// </summary>
public abstract record MetricExpression;

public sealed record MetricColumn(string Name) : MetricExpression;

public sealed record MetricLiteral(decimal Value) : MetricExpression;

public sealed record MetricAggregate(string Function, MetricExpression Argument, bool Distinct) : MetricExpression;

public sealed record MetricNullIf(MetricExpression Left, MetricExpression Right) : MetricExpression;

public sealed record MetricBinary(char Operator, MetricExpression Left, MetricExpression Right) : MetricExpression;

public sealed record MetricParseResult(MetricExpression? Expression, string? Error)
{
    public bool Ok => Error is null;

    public static MetricParseResult Fail(string error) => new(null, error);
}

/// <summary>
/// Tokenizer + recursive-descent parser for the controlled metric SQL subset:
/// aggregates SUM/COUNT/MIN/MAX/AVG (COUNT with optional DISTINCT), arithmetic
/// + - * /, NULLIF, parentheses, numeric literals and column identifiers.
/// Anything else (subqueries, CASE, unknown functions, comments, strings,
/// nested aggregates) is rejected with a deterministic error — the raw
/// REPORT_METRIC.DEFINITION text never reaches SQL execution.
/// </summary>
public static class MetricExpressionParser
{
    public const int MaxLength = 500;
    public const int MaxNodes = 64;

    private static readonly string[] AggregateFunctions = ["SUM", "COUNT", "MIN", "MAX", "AVG"];

    public static MetricParseResult Parse(string? definition)
    {
        if (string.IsNullOrWhiteSpace(definition))
        {
            return MetricParseResult.Fail("口径定义为空。");
        }
        if (definition.Length > MaxLength)
        {
            return MetricParseResult.Fail($"口径定义超过 {MaxLength} 字符上限。");
        }

        var tokens = Tokenize(definition);
        if (tokens.Error is not null)
        {
            return MetricParseResult.Fail(tokens.Error);
        }

        var cursor = 0;
        var aggregateDepth = 0;
        var nodeCount = 0;
        var expression = ParseExpression(tokens.List, ref cursor, ref aggregateDepth, ref nodeCount, out var parseError);
        if (parseError is not null)
        {
            return MetricParseResult.Fail(parseError);
        }
        if (cursor != tokens.List.Count)
        {
            return MetricParseResult.Fail("口径定义包含无法解析的多余内容。");
        }
        return new MetricParseResult(expression, null);
    }

    private static (List<Token> List, string? Error) Tokenize(string input)
    {
        var tokens = new List<Token>();
        var position = 0;
        while (position < input.Length)
        {
            var c = input[position];
            if (char.IsWhiteSpace(c))
            {
                position++;
                continue;
            }
            if (char.IsAsciiLetter(c) || c == '_')
            {
                var start = position;
                while (position < input.Length && (char.IsAsciiLetterOrDigit(input[position]) || input[position] == '_'))
                {
                    position++;
                }
                tokens.Add(new Token(TokenKind.Identifier, input[start..position].ToUpperInvariant()));
                continue;
            }
            if (char.IsAsciiDigit(c))
            {
                var start = position;
                while (position < input.Length && (char.IsAsciiDigit(input[position]) || input[position] == '.'))
                {
                    position++;
                }
                tokens.Add(new Token(TokenKind.Number, input[start..position]));
                continue;
            }
            if ("+-*/(),".Contains(c))
            {
                tokens.Add(new Token(TokenKind.Symbol, c.ToString()));
                position++;
                continue;
            }
            return ([], $"口径定义包含不受支持的字符「{c}」(" +
                       "仅允许聚合函数、算术运算、NULLIF、括号与列名)。");
        }
        if (tokens.Count > MaxNodes * 2)
        {
            return ([], "口径定义过长。");
        }
        return (tokens, null);
    }

    private readonly record struct Token(TokenKind Kind, string Text);

    private enum TokenKind
    {
        Identifier,
        Number,
        Symbol,
    }

    private static MetricExpression ParseExpression(
        List<Token> tokens, ref int cursor, ref int aggregateDepth, ref int nodeCount, out string? error)
    {
        var left = ParseTerm(tokens, ref cursor, ref aggregateDepth, ref nodeCount, out error);
        if (error is not null)
        {
            return null!;
        }
        while (cursor < tokens.Count && tokens[cursor] is { Kind: TokenKind.Symbol, Text: "+" or "-" })
        {
            var op = tokens[cursor].Text[0];
            cursor++;
            var right = ParseTerm(tokens, ref cursor, ref aggregateDepth, ref nodeCount, out error);
            if (error is not null)
            {
                return null!;
            }
            if (++nodeCount > MaxNodes)
            {
                error = "口径定义过于复杂。";
                return null!;
            }
            left = new MetricBinary(op, left, right);
        }
        return left;
    }

    private static MetricExpression ParseTerm(
        List<Token> tokens, ref int cursor, ref int aggregateDepth, ref int nodeCount, out string? error)
    {
        var left = ParseFactor(tokens, ref cursor, ref aggregateDepth, ref nodeCount, out error);
        if (error is not null)
        {
            return null!;
        }
        while (cursor < tokens.Count && tokens[cursor] is { Kind: TokenKind.Symbol, Text: "*" or "/" })
        {
            var op = tokens[cursor].Text[0];
            cursor++;
            var right = ParseFactor(tokens, ref cursor, ref aggregateDepth, ref nodeCount, out error);
            if (error is not null)
            {
                return null!;
            }
            if (++nodeCount > MaxNodes)
            {
                error = "口径定义过于复杂。";
                return null!;
            }
            left = new MetricBinary(op, left, right);
        }
        return left;
    }

    private static MetricExpression ParseFactor(
        List<Token> tokens, ref int cursor, ref int aggregateDepth, ref int nodeCount, out string? error)
    {
        error = null;
        if (cursor >= tokens.Count)
        {
            error = "口径定义不完整。";
            return null!;
        }
        var token = tokens[cursor];
        switch (token.Kind)
        {
            case TokenKind.Number:
            {
                if (!decimal.TryParse(token.Text, out var value))
                {
                    error = $"数字字面量无法解析：{token.Text}";
                    return null!;
                }
                cursor++;
                nodeCount++;
                return new MetricLiteral(value);
            }
            case TokenKind.Identifier when token.Text == "NULLIF":
            {
                cursor++;
                return ParseNullIf(tokens, ref cursor, ref aggregateDepth, ref nodeCount, out error);
            }
            case TokenKind.Identifier when AggregateFunctions.Contains(token.Text):
            {
                if (aggregateDepth > 0)
                {
                    error = "聚合函数不允许嵌套。";
                    return null!;
                }
                cursor++;
                return ParseAggregate(token.Text, tokens, ref cursor, ref nodeCount, out error);
            }
            case TokenKind.Identifier:
            {
                if (!IsPlainIdentifier(token.Text))
                {
                    error = $"列名不符合标识符规则：{token.Text}";
                    return null!;
                }
                cursor++;
                nodeCount++;
                return new MetricColumn(token.Text);
            }
            case TokenKind.Symbol when token.Text == "(":
            {
                cursor++;
                var inner = ParseExpression(tokens, ref cursor, ref aggregateDepth, ref nodeCount, out error);
                if (error is not null)
                {
                    return null!;
                }
                if (cursor >= tokens.Count || tokens[cursor] is not ({ Kind: TokenKind.Symbol, Text: ")" }))
                {
                    error = "括号不匹配。";
                    return null!;
                }
                cursor++;
                return inner;
            }
            default:
                error = $"口径定义包含不受支持的语法：「{token.Text}」。";
                return null!;
        }
    }

    private static MetricExpression ParseNullIf(
        List<Token> tokens, ref int cursor, ref int aggregateDepth, ref int nodeCount, out string? error)
    {
        if (!ExpectOpenParen(tokens, ref cursor, out error))
        {
            return null!;
        }
        var left = ParseExpression(tokens, ref cursor, ref aggregateDepth, ref nodeCount, out error);
        if (error is not null)
        {
            return null!;
        }
        if (cursor >= tokens.Count || tokens[cursor] is not ({ Kind: TokenKind.Symbol, Text: "," }))
        {
            error = "NULLIF 需要两个参数。";
            return null!;
        }
        cursor++;
        var right = ParseExpression(tokens, ref cursor, ref aggregateDepth, ref nodeCount, out error);
        if (error is not null)
        {
            return null!;
        }
        if (!ExpectCloseParen(tokens, ref cursor, out error))
        {
            return null!;
        }
        nodeCount++;
        return new MetricNullIf(left, right);
    }

    private static MetricExpression ParseAggregate(
        string function, List<Token> tokens, ref int cursor, ref int nodeCount, out string? error)
    {
        if (!ExpectOpenParen(tokens, ref cursor, out error))
        {
            return null!;
        }
        var distinct = false;
        if (cursor < tokens.Count && tokens[cursor] is { Kind: TokenKind.Identifier, Text: "DISTINCT" })
        {
            if (function != "COUNT")
            {
                error = "DISTINCT 仅支持 COUNT。";
                return null!;
            }
            distinct = true;
            cursor++;
        }
        // Aggregate arguments accept columns, literals, arithmetic and NULLIF,
        // but never another aggregate — the depth sentinel marks the argument scope.
        var depthInsideAggregate = 1;
        var argument = ParseExpression(tokens, ref cursor, ref depthInsideAggregate, ref nodeCount, out error);
        if (error is not null)
        {
            return null!;
        }
        if (!ExpectCloseParen(tokens, ref cursor, out error))
        {
            return null!;
        }
        nodeCount++;
        return new MetricAggregate(function, argument, distinct);
    }

    private static bool ExpectOpenParen(List<Token> tokens, ref int cursor, out string? error)
    {
        if (cursor >= tokens.Count || tokens[cursor] is not ({ Kind: TokenKind.Symbol, Text: "(" }))
        {
            error = "缺少左括号。";
            return false;
        }
        cursor++;
        error = null;
        return true;
    }

    private static bool ExpectCloseParen(List<Token> tokens, ref int cursor, out string? error)
    {
        if (cursor >= tokens.Count || tokens[cursor] is not ({ Kind: TokenKind.Symbol, Text: ")" }))
        {
            error = "缺少右括号。";
            return false;
        }
        cursor++;
        error = null;
        return true;
    }

    private static bool IsPlainIdentifier(string text) =>
        text.Length <= 128
        && (char.IsAsciiLetter(text[0]) || text[0] == '_')
        && text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
}
