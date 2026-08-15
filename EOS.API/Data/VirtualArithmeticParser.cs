using System.Text;
using System.Text.RegularExpressions;

namespace EOS.API.Data;

/// <summary>受控算术表达式 token（P5）：Ref=表.列 / Number / String / Op / LParen / RParen。</summary>
internal sealed record VirtualArithmeticToken(string Kind, string? Table, string? Column, string Text);

/// <summary>
/// VIRTUAL_EXP 受控算术/常量子集解析器（2026-08-15，P5）：
/// - 支持：`表.列` 引用 + 数值字面量 + `+ - * /` + 括号 + 一元正负号；
/// - 字符串常量仅允许「独立使用」（存量 12 条常量默认值，如 'RMB'/'CP'）；
/// - 拒绝：函数调用、CASE/IIF、子查询、字符串拼接、未限定列名、其它字符。
/// 纯语法解析不访问数据库；表/列白名单与物理存在性由调用方（Resolver/Validate）校验。
/// </summary>
internal static class VirtualArithmeticParser
{
    private static readonly Regex NumberPattern = new(@"^\d+(\.\d+)?$", RegexOptions.Compiled);
    private static readonly Regex Identifier = new("^[A-Za-z_][A-Za-z0-9_]{0,127}$", RegexOptions.Compiled);

    public static bool TryParse(string expression, out IReadOnlyList<VirtualArithmeticToken> tokens, out string? error)
    {
        tokens = [];
        error = null;
        var raw = expression.Trim();
        if (raw.Length == 0)
        {
            error = "表达式为空。";
            return false;
        }
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
            if (ch is '+' or '-' or '*' or '/' or '(' or ')')
            {
                parsed.Add(new VirtualArithmeticToken(ch is '(' ? "LParen" : ch is ')' ? "RParen" : "Op", null, null, ch.ToString()));
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
                var first = raw[start..index];
                if (index < raw.Length && raw[index] == '.')
                {
                    index++;
                    var columnStart = index;
                    while (index < raw.Length && (char.IsLetterOrDigit(raw[index]) || raw[index] == '_')) index++;
                    var column = raw[columnStart..index];
                    if (column.Length == 0 || !Identifier.IsMatch(column)) { error = $"列名无效：{first}."; return false; }
                    if (!Identifier.IsMatch(first)) { error = $"表名无效：{first}"; return false; }
                    parsed.Add(new VirtualArithmeticToken("Ref", first, column, $"{first}.{column}"));
                    continue;
                }
                error = $"表达式仅支持限定列引用（表.列），不允许裸标识符：{first}。";
                return false;
            }
            error = $"表达式包含不允许的字符：{ch}";
            return false;
        }

        // 字符串常量仅允许独立使用（存量常量默认值形态）
        if (parsed.Any(token => token.Kind == "String") && parsed.Count > 1)
        {
            error = "字符串常量仅允许独立使用，不能参与运算。";
            return false;
        }
        if (parsed.Count == 1 && parsed[0].Kind is "String" or "Number")
        {
            tokens = parsed;
            return true;
        }

        // 递归下降：expr := term (('+'|'-') term)*；term := factor (('*'|'/') factor)*；factor := ('-'|'+')? primary
        var cursor = 0;
        if (!ParseExpression(parsed, ref cursor, out error))
            return false;
        if (cursor != parsed.Count)
        {
            error = "表达式末尾存在多余内容。";
            return false;
        }
        tokens = parsed;
        return true;
    }

    private static bool ParseExpression(IReadOnlyList<VirtualArithmeticToken> tokens, ref int cursor, out string? error)
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
        if (token.Kind is "Ref" or "Number")
        {
            cursor++;
            error = null;
            return true;
        }
        if (token.Kind == "LParen")
        {
            cursor++;
            if (!ParseExpression(tokens, ref cursor, out error)) return false;
            if (cursor >= tokens.Count || tokens[cursor].Kind != "RParen")
            {
                error = "括号不匹配。";
                return false;
            }
            cursor++;
            error = null;
            return true;
        }
        error = "表达式不完整（预期列引用、数值或括号）。";
        return false;
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
