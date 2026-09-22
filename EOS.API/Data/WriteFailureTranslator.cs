using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 写入期 SqlException 的受控翻译：把数据库层的拒绝（必填列 NULL、超长、外键不存在、
/// 唯一键/主键冲突）翻成字段级可读错误，交给调用方以 400 返回。
///
/// 这些都属于**用户可预期的输入问题**（少填一列、编号撞车），不是异常状态；让它们穿透成
/// 500 既拿不到字段信息、又会把库错误原文带进日志与响应。主表新增路径已有同款处置，
/// 本类把口径统一，供主表写入与明细逐行写入共用。
/// </summary>
internal static class WriteFailureTranslator
{
    /// <summary>可翻译的 SQL 错误号：唯一键/主键冲突、NOT NULL 违例、字符串截断、外键约束。</summary>
    internal static bool IsExpectedWriteFailure(SqlException ex) => ex.Number is 2601 or 2627 or 515 or 8152 or 547;

    /// <summary>
    /// 翻译成字段级错误。能定位到列时把错误挂在那一列上（前端据此高亮该格）；
    /// 定位不到时字段留空、只给可读文案。RowIndex 由调用方补。
    /// </summary>
    internal static FieldError Translate(SqlException ex, string table, int? rowIndex = null)
    {
        var column = ReadQuotedIdentifier(ex.Message);
        return ex.Number switch
        {
            515 => new FieldError(
                column ?? string.Empty,
                string.IsNullOrWhiteSpace(column) ? "该字段不能为空。" : "该字段不能为空。",
                "REQUIRED_FIELD_MISSING",
                rowIndex),
            8152 or 2628 => new FieldError(
                column ?? string.Empty,
                "内容长度超出限制。",
                "VALUE_TOO_LONG",
                rowIndex),
            547 => new FieldError(
                column ?? string.Empty,
                "引用的资料不存在或已被其它单据使用，请检查后重试。",
                "REFERENCE_CONFLICT",
                rowIndex),
            2601 or 2627 => new FieldError(
                column ?? string.Empty,
                $"该记录已存在（{DescribeConflict(ex.Message, table)}），请检查编号/唯一字段后重试。",
                "DUPLICATE_RECORD_KEY",
                rowIndex),
            _ => new FieldError(string.Empty, "数据写入失败，请检查输入后重试。", "WRITE_FAILED", rowIndex),
        };
    }

    /// <summary>
    /// 从数据库错误消息里取第一个被引用的列名。
    /// 覆盖中英两种句式：`列 'REBATE'` / `column 'REBATE'` / `列 "REBATE"`，
    /// 也覆盖截断消息里的 `列 'X'`（新版本 SQL Server 会带上列名）。
    /// </summary>
    private static string? ReadQuotedIdentifier(string message)
    {
        var match = Regex.Match(message,
            @"(?:列|column)\s*['""](?<name>[^'""]+)['""]",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["name"].Value : null;
    }

    /// <summary>冲突定位信息：优先后端约束/索引名，其次重复键值（便于用户判断是哪些字段重复）。</summary>
    private static string DescribeConflict(string message, string table)
    {
        var index = Regex.Match(message, @"(?:约束|constraint|索引|index)\s*[“'""](?<name>[^”'""]+)[”'""]");
        var keyValue = Regex.Match(message, @"(?:重复键值|duplicate key value)\s*(?:is\s*)?\((?<value>[^)]*)\)");
        var parts = new List<string>();
        if (index.Success)
        {
            parts.Add(index.Groups["name"].Value);
        }
        else if (!string.IsNullOrWhiteSpace(table))
        {
            parts.Add(table);
        }
        if (keyValue.Success && !string.IsNullOrWhiteSpace(keyValue.Groups["value"].Value))
        {
            parts.Add($"重复值：{keyValue.Groups["value"].Value.Trim()}");
        }
        return parts.Count > 0 ? string.Join('，', parts) : "唯一键冲突";
    }
}
