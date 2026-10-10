using System.Globalization;
using System.Text;
using MiniExcelLibs;

namespace EOS.API.Features.Import;

/// <summary>文件本身不可用（格式不支持、超限、内容无法解析）。</summary>
internal sealed class ImportFileException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// 把上传的表格文件读成「列名 + 数据行」的纯数据：
///
/// <list type="bullet">
/// <item>`.csv` / `.txt` / `.tsv`：自行解析，支持引号包裹、引号内换行与转义双引号；
/// 编码按 BOM 判定，无 BOM 时先按严格 UTF-8 试解，失败回落 GB18030（客户旧系统导出的常见编码）。</item>
/// <item>`.xlsx` / `.xlsm`：交给 MiniExcel 读（样式化日期、公式缓存值、内联字符串由它处理）。</item>
/// </list>
///
/// <para>
/// 只负责「读成格子」，不涉及任何业务判定——列该映射到哪个字段由导入定义决定。
/// </para>
/// </summary>
internal static class TabularFileReader
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly Lazy<Encoding?> Gb18030 = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding("GB18030");
        }
        catch (ArgumentException)
        {
            return null;
        }
    });

    public static ImportFileData Read(string fileName, Stream stream)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        if (buffer.Length == 0)
        {
            throw new ImportFileException("IMPORT_FILE_EMPTY", "文件是空的。");
        }
        if (buffer.Length > ImportLimits.MaxFileBytes)
        {
            throw new ImportFileException("IMPORT_FILE_TOO_LARGE",
                $"文件超过 {ImportLimits.MaxFileBytes / 1024 / 1024} MB，请拆分后再导入。");
        }
        buffer.Position = 0;

        var grid = extension switch
        {
            ".xlsx" or ".xlsm" => ReadExcel(buffer),
            ".csv" or ".txt" or ".tsv" => ReadDelimited(DecodeText(buffer.ToArray()), extension),
            ".xls" => throw new ImportFileException("IMPORT_FILE_UNSUPPORTED",
                "旧版 .xls 不受支持，请在 Excel 里另存为 .xlsx 或 .csv 后再导入。"),
            _ => throw new ImportFileException("IMPORT_FILE_UNSUPPORTED",
                "只支持 .csv / .txt / .tsv / .xlsx 文件。"),
        };

        return Normalize(grid, Path.GetFileName(fileName ?? string.Empty));
    }

    /// <summary>表头即第一行；补齐参差行、剔除全空行、按上限截断并回报。</summary>
    private static ImportFileData Normalize(IReadOnlyList<IReadOnlyList<string>> grid, string sourceName)
    {
        if (grid.Count == 0)
        {
            throw new ImportFileException("IMPORT_FILE_EMPTY", "文件里没有可读的内容。");
        }

        var header = grid[0];
        // 列数取表头与数据行的最大值：客户文件里表头常比数据行短（末列为空标题）
        var columnCount = header.Count;
        for (var index = 1; index < grid.Count; index++)
        {
            columnCount = Math.Max(columnCount, grid[index].Count);
        }
        if (columnCount > ImportLimits.MaxColumns)
        {
            throw new ImportFileException("IMPORT_FILE_TOO_WIDE",
                $"文件列数超过 {ImportLimits.MaxColumns} 列，请只保留需要导入的列。");
        }
        if (columnCount == 0)
        {
            throw new ImportFileException("IMPORT_FILE_EMPTY", "文件里没有可读的内容。");
        }

        var columns = new List<string>(columnCount);
        for (var index = 0; index < columnCount; index++)
        {
            var label = index < header.Count ? header[index].Trim() : string.Empty;
            columns.Add(label.Length > 0 ? label : $"列{index + 1}");
        }

        var rows = new List<IReadOnlyList<string>>();
        var truncated = false;
        var total = 0;
        for (var index = 1; index < grid.Count; index++)
        {
            var source = grid[index];
            var row = new string[columnCount];
            var blank = true;
            for (var column = 0; column < columnCount; column++)
            {
                var cell = column < source.Count ? source[column] : string.Empty;
                row[column] = cell;
                if (blank && cell.Length > 0)
                {
                    blank = false;
                }
            }
            if (blank)
            {
                continue;
            }
            total++;
            if (rows.Count >= ImportLimits.MaxRows)
            {
                truncated = true;
                continue;
            }
            rows.Add(row);
        }

        return new ImportFileData(columns, rows, total, truncated, sourceName);
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 非 UTF-8：国内旧系统导出的文本多为 GB18030，回落它比留下乱码更接近用户预期
            return Gb18030.Value is { } gb ? gb.GetString(bytes) : StrictUtf8.GetString(bytes);
        }
    }

    private static List<IReadOnlyList<string>> ReadDelimited(string text, string extension)
    {
        var delimiter = DetectDelimiter(text, extension);
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        cell.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    cell.Append(ch);
                }
                continue;
            }
            if (ch == '"')
            {
                inQuotes = true;
                continue;
            }
            if (ch == delimiter)
            {
                row.Add(cell.ToString());
                cell.Clear();
                continue;
            }
            if (ch is '\r' or '\n')
            {
                if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                continue;
            }
            cell.Append(ch);
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>
    /// 分隔符判定：看首个物理行里各候选在引号外的出现次数，取最多者。
    /// `.tsv` 直接定成制表符——扩展名已经是用户的明确意图。
    /// </summary>
    private static char DetectDelimiter(string text, string extension)
    {
        if (extension == ".tsv")
        {
            return '\t';
        }
        var counts = new Dictionary<char, int> { [','] = 0, ['\t'] = 0, [';'] = 0 };
        var inQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (!inQuotes && (ch is '\r' or '\n'))
            {
                break;
            }
            if (!inQuotes && counts.ContainsKey(ch))
            {
                counts[ch]++;
            }
        }
        var best = counts.OrderByDescending(pair => pair.Value).First();
        return best.Value > 0 ? best.Key : ',';
    }

    private static List<IReadOnlyList<string>> ReadExcel(Stream stream)
    {
        var rows = new List<IReadOnlyList<string>>();
        try
        {
            foreach (IDictionary<string, object> row in stream.Query(useHeaderRow: false, excelType: ExcelType.XLSX))
            {
                rows.Add(Expand(row));
                // 多读一行用于判断"是否还有更多行"，其余不必载入内存
                if (rows.Count > ImportLimits.MaxRows)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not ImportFileException)
        {
            throw new ImportFileException("IMPORT_FILE_UNREADABLE",
                "无法读取该 Excel 文件（可能已损坏或被加密）。请用 Excel 打开另存为 .xlsx 后再试。");
        }
        return rows;
    }

    /// <summary>
    /// 无表头模式下 MiniExcel 以列号字母（A、B、…）为键，且**只为存在的单元格产生键**；
    /// 这里按列号顺序展开成定长行，缺失单元格留空，保证列位置与表头对齐。
    /// </summary>
    private static IReadOnlyList<string> Expand(IDictionary<string, object> row)
    {
        var indexed = new List<(int Index, string Value)>(row.Count);
        foreach (var (key, value) in row)
        {
            if (TryColumnIndex(key, out var index))
            {
                indexed.Add((index, Format(value)));
            }
        }
        indexed.Sort((left, right) => left.Index.CompareTo(right.Index));
        if (indexed.Count == 0)
        {
            return [];
        }
        var last = indexed[^1].Index;
        var cells = new string[last + 1];
        Array.Fill(cells, string.Empty);
        foreach (var (index, value) in indexed)
        {
            cells[index] = value;
        }
        return cells;
    }

    /// <summary>列号字母 → 0 起下标（A=0、Z=25、AA=26）。键不是纯字母时视为无法定位，跳过。</summary>
    private static bool TryColumnIndex(string? key, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }
        foreach (var ch in key)
        {
            if (ch is < 'A' or > 'Z')
            {
                return false;
            }
            index = index * 26 + (ch - 'A' + 1);
        }
        index--;
        return index >= 0;
    }

    private static string Format(object? value) => value switch
    {
        null or DBNull => string.Empty,
        string text => text.Trim(),
        DateTime moment => moment.TimeOfDay == TimeSpan.Zero
            ? moment.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool flag => flag ? "1" : "0",
        double number => number == Math.Floor(number) && Math.Abs(number) < 1e15
            ? ((long)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? string.Empty,
    };
}
