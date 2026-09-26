using System.Data;

using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workbench;

/// <summary>
/// 明细行「引用带出」：本行用一组引用键指向来源单据的一张明细行时，把来源行上的列值补进本行。
///
/// 与 <see cref="MasterDerivedColumnFiller"/> 同款纪律——表名/列名是服务端常量，只做标识符
/// 与存在性校验；取值一律参数化；来源行查不到时 fail-closed 报具名错误，不静默填默认值。
///
/// 与 <c>WorkbenchCommandHandler.FillReferencedAmountsAsync</c> 的分工：后者把来源行的
/// **金额**（AMOUNT/AMOUNT_TAX/TAX_SUM/QTY）整列覆盖到本行，语义是"金额以来源单据为准"，
/// 因此跑在金额复算之后；本类补的是**复算的输入列**（如对账单价），必须跑在复算之前，
/// 复算才能算出与来源一致的金额。目标列仍是只读——带出由服务端完成，不开放客户端提交。
/// </summary>
internal static class DetailReferenceColumnFiller
{
    /// <summary>引用键的一对列：目标明细行的列 → 来源明细表的列。</summary>
    internal sealed record KeyPair(string Target, string Source);

    /// <summary>
    /// 带出规则。<paramref name="DetailTable"/> 限定了生效的明细表（同样的列名在别的单据上
    /// 语义不同，不做全局匹配）。
    /// </summary>
    internal sealed record Rule(
        string DetailTable,
        string TargetColumn,
        IReadOnlyList<KeyPair> Keys,
        string SourceTable,
        string SourceValueColumn,
        string NotFoundCode,
        string NotFoundMessage);

    internal static readonly IReadOnlyList<Rule> Rules =
    [
        // 送货单（1406）明细的**单价**只读、不接受客户端提交：由被引用的客户订单明细行带出
        // （旧过程 P_WF_COP_SEND 也是按 ORDER_TYPE/ORDER_NO/ORDER_SERIAL_NO 关联 COP_ORDER_D）。
        // 金额复算随后用它算出 AMOUNT/AMOUNT_TAX。
        new Rule(
            DetailTable: "COP_SEND_D",
            TargetColumn: "PRICE",
            Keys:
            [
                // ORDER_TYPE 不参与匹配：送货单明细上该列不可见（界面与提交都没有它），
                // 而 ORDER_NO 跨单别唯一（实测无冲突组），ORDER_NO + 项次 已足以定位订单行。
                new KeyPair("ORDER_NO", "ORDER_NO"),
                new KeyPair("ORDER_SERIAL_NO", "SERIAL_NO"),
            ],
            SourceTable: "COP_ORDER_D",
            SourceValueColumn: "PRICE",
            NotFoundCode: "REFERENCED_ORDER_NOT_FOUND",
            NotFoundMessage: "引用的订单明细行不存在，无法带出单价。"),

        // 应付货款单（170201）明细的**对账单价**是成本列（FIELDS.IS_COST=1）：只读、不接受客户端提交，
        // 由被引用的收料明细行带出。金额复算随后用它算出 AMOUNT/AMOUNT_TAX。
        new Rule(
            DetailTable: "PUR_DUE_D",
            TargetColumn: "PRICE",
            Keys:
            [
                new KeyPair("RECEIVE_TYPE", "RECEIVE_TYPE"),
                new KeyPair("RECEIVE_NO", "RECEIVE_NO"),
                new KeyPair("RECEIVE_SERIAL_NO", "SERIAL_NO"),
            ],
            SourceTable: "PUR_RECEIVE_D",
            SourceValueColumn: "PRICE",
            NotFoundCode: "REFERENCED_RECEIVE_NOT_FOUND",
            NotFoundMessage: "引用的收料明细行不存在，无法带出对账单价。"),
    ];

    /// <summary>
    /// 逐行补齐。返回的 <c>Error</c> 非空即应中止保存（错误带行号，前端能定位到具体行）。
    /// </summary>
    internal static async Task<FieldError?> FillAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        string detailTable,
        IReadOnlyList<FormFieldDefinition> fields,
        IReadOnlyList<Dictionary<string, object?>> rows,
        CancellationToken token)
    {
        foreach (var rule in Rules)
        {
            if (!rule.DetailTable.Equals(detailTable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var field = fields.FirstOrDefault(item => item.Key.Equals(rule.TargetColumn, StringComparison.OrdinalIgnoreCase));
            if (field is null)
            {
                continue;
            }
            // 表/列名来自规则常量：非法标识符或物理列不存在一律跳过（不拼进 SQL）
            if (!WorkbenchSql.Identifier.IsMatch(rule.SourceTable)
                || !WorkbenchSql.Identifier.IsMatch(rule.SourceValueColumn)
                || rule.Keys.Any(key => !WorkbenchSql.Identifier.IsMatch(key.Source))
                || !await WorkbenchSql.TableExistsAsync(connection, rule.SourceTable, token, transaction))
            {
                continue;
            }
            var sourceColumns = rule.Keys.Select(key => key.Source).Append(rule.SourceValueColumn).ToList();
            if (!await WorkbenchSql.ColumnsExistAsync(connection, rule.SourceTable, sourceColumns, token, transaction))
            {
                continue;
            }

            for (var index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                if (HasValue(row, rule.TargetColumn))
                {
                    continue;
                }
                var values = new List<string>(rule.Keys.Count);
                var hasReference = true;
                foreach (var key in rule.Keys)
                {
                    if (!TryGetText(row, key.Target, out var text))
                    {
                        hasReference = false;
                        break;
                    }
                    values.Add(text);
                }
                // 没有引用键：本行不引用来源（如无采购/无收料的场景），不补也不报错
                if (!hasReference)
                {
                    continue;
                }

                var conditions = new List<string>(rule.Keys.Count);
                for (var i = 0; i < rule.Keys.Count; i++)
                {
                    conditions.Add($"[{rule.Keys[i].Source}]=@{i}");
                }
                var sql = $"SELECT TOP 1 [{rule.SourceValueColumn}] FROM dbo.[{rule.SourceTable}] WHERE {string.Join(" AND ", conditions)};";
                await using var command = new SqlCommand(sql, connection, transaction);
                for (var i = 0; i < values.Count; i++)
                {
                    command.Parameters.Add($"@{i}", SqlDbType.NVarChar, 200).Value = values[i];
                }
                var value = await command.ExecuteScalarAsync(token);
                if (value is null or DBNull)
                {
                    // 有引用却查不到来源行：不能猜一个值（静默按 0 算金额会把对账金额算错）
                    return new FieldError(rule.TargetColumn, rule.NotFoundMessage, rule.NotFoundCode) { RowIndex = index };
                }
                row[rule.TargetColumn] = RecordPayloadValidator.TryConvert(field.DataType, WorkbenchSql.ValueToString(value).Trim(), out var converted)
                    ? converted
                    : value;
            }
        }
        return null;
    }

    private static bool HasValue(IReadOnlyDictionary<string, object?> row, string column) =>
        row.TryGetValue(column, out var value) && value is not null
        && (value is not string text || !string.IsNullOrWhiteSpace(text));

    private static bool TryGetText(IReadOnlyDictionary<string, object?> row, string column, out string text)
    {
        text = string.Empty;
        if (!row.TryGetValue(column, out var value) || value is null)
        {
            return false;
        }
        var converted = WorkbenchSql.ValueToString(value).Trim();
        if (converted.Length == 0)
        {
            return false;
        }
        text = converted;
        return true;
    }
}
