namespace EOS.API.Features.Assistant.ModelAccess;

/// <summary>
/// 识别与剥离"被模型写进正文的工具调用标记"。
///
/// <para>
/// 工具调用只该走 OpenAI 兼容的结构化通道（<c>delta.tool_calls</c>，见
/// <see cref="OpenAiCompatibleChatModel"/>）。模型偶尔会把调用写进 <c>content</c>：
/// 那段标记里**没有任何工具被执行**，却会被当成回答流给用户、并落进
/// <c>ASSISTANT_MESSAGE</c>——用户看到的是一段看不懂的标签（DeepSeek 的 DSML 语法就是这样漏出来的，
/// 它的全角竖线转义只出现在文本通道，结构化通道里没有这个差异）。
/// </para>
///
/// <para>
/// 本类只做两件**只读**的事：判断"这段内容还可能是标记的开头"（编排层据此暂缓下发该段增量）、
/// 落库前把标记块从正文里剥掉。它**不解析标记、也不把标记变成工具调用**——
/// 模型输出不会因此多出一条工具执行路径。
/// </para>
///
/// <para>
/// 比较口径：竖线折半角、忽略空白、统一小写。标记是流式分片到达的，同一段标记里可能夹着
/// 任意空白与换行，逐字比对原串会漏。
/// </para>
/// </summary>
internal static class ToolCallTextGuard
{
    /// <summary>
    /// 标记的开头（已归一化）。**新增供应商出现同类形态时在这里登记**——不登记等于没有这道防线。
    /// </summary>
    private const string OpenTag = "<||dsml||";

    /// <summary>标记的收尾（已归一化），与 <see cref="OpenTag"/> 成对。</summary>
    private const string CloseTag = "</||dsml||";

    /// <summary>
    /// 正文**是否还可能是**标记的开头。编排层在这个判断成立期间不下发正文增量：
    /// 一旦不成立（出现了标记以外的内容）就把攒下的部分补发，之后的增量照常流式；
    /// 一直成立到本轮结束，就说明这一段就是标记。
    /// </summary>
    public static bool CouldBeMarkupOpen(string text)
    {
        var matched = 0;
        foreach (var ch in text)
        {
            var folded = Fold(ch);
            if (char.IsWhiteSpace(folded)) continue;
            if (matched >= OpenTag.Length) return true; // 已逐字匹配完开头：确证是标记
            if (folded != OpenTag[matched]) return false;
            matched++;
        }

        return true;
    }

    /// <summary>
    /// 从 <paramref name="from"/> 起、正文里**最早出现"可能成为标记开头"的位置**；
    /// 没有这样的位置时返回正文长度（即"整段都可以下发"）。
    ///
    /// <para>
    /// 编排层据此下发 <c>[from, 返回值)</c>、按住 <c>[返回值, 末尾)</c>。取**最早**的位置而不是
    /// 最后一个：标记块可能出现在正文中间（模型先答一半、再想调工具），若按住的是后半截的
    /// <c>'&lt;'</c>，前面的标记就会先被发出去。
    /// 末尾那段空白也一并按住——它可能正是标记前的换行；正文里的"小于号"（后一个字符不是竖线）
    /// 与普通空白都不会因此卡住。
    /// </para>
    /// </summary>
    public static int PossibleMarkupStart(string text, int from)
    {
        var end = text.Length;
        while (end > from && char.IsWhiteSpace(text[end - 1])) end--;
        if (end == from) return from; // 从 from 起全是空白：整段先按住，它可能是标记前的换行

        for (var index = from; index < end; index++)
        {
            if (text[index] == '<' && CouldBeMarkupOpen(text[index..])) return index;
        }

        return end;
    }

    /// <summary>
    /// 剥掉正文里的标记块：从开标记起、到其后的闭标记（含收尾的 <c>&gt;</c>）为止整段删掉，
    /// 多个块逐个删；闭标记缺失时删到末尾——不完整的标记同样是标记，留在正文里只会变成看不懂的一段。
    /// 没有标记时**原样返回**（正常回答连首尾空白都不动）。
    /// </summary>
    public static string Strip(string text)
    {
        // 标记必然含 '<'：没有它的正文连扫都不必扫
        if (text.Length == 0 || !text.Contains('<')) return text;

        var result = text;
        var stripped = false;
        while (true)
        {
            var (compact, positions) = Compact(result);
            var start = compact.IndexOf(OpenTag, StringComparison.Ordinal);
            if (start < 0) break;

            // 收尾按**同名**配对：标记块里还有 invoke / parameter 这些内层元素的收尾，
            // 拿"第一个收尾"截断会把外层收尾剩在正文里。
            var nameEnd = start + OpenTag.Length;
            while (nameEnd < compact.Length
                && (char.IsLetterOrDigit(compact[nameEnd]) || compact[nameEnd] == '_'))
            {
                nameEnd++;
            }

            var end = result.Length;
            var close = compact.IndexOf(CloseTag + compact[start..nameEnd], nameEnd, StringComparison.Ordinal);
            if (close >= nameEnd)
            {
                var tagEnd = compact.IndexOf('>', close);
                if (tagEnd >= 0) end = positions[tagEnd] + 1;
            }

            result = string.Concat(result.AsSpan(0, positions[start]), result.AsSpan(end));
            stripped = true;
        }

        // 删过标记才动首尾空白：标记块常独占一整段，留着会多出空行
        return stripped ? result.Trim() : text;
    }

    /// <summary>归一化后的正文，以及每个字符在**原串**里的下标（剥标记时要按原串下标切片）。</summary>
    private static (string Compact, int[] Positions) Compact(string source)
    {
        var chars = new List<char>(source.Length);
        var positions = new List<int>(source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            var folded = Fold(source[index]);
            if (char.IsWhiteSpace(folded)) continue;
            chars.Add(folded);
            positions.Add(index);
        }

        return (new string([.. chars]), [.. positions]);
    }

    /// <summary>全角竖线（U+FF5C）折半角、统一小写：文本通道里的标记与命令行写法只差这两处。</summary>
    private static char Fold(char ch) => ch is '\uFF5C' ? '|' : char.ToLowerInvariant(ch);
}
