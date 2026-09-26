using System.Data;

using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Workbench;

/// <summary>
/// 主表派生列补齐：界面由「来源列」带出的只读联动列（如币别带出汇率），在保存主表**之前**
/// 由服务端补齐。这类字段前端不会提交（只读字段进不了提交集合），而模块的
/// MODULES.DETAIL_NO_FIELDS 又要求它有值，若不补齐就成了「既不许传、又必须传」的死结。
///
/// 补齐改的是即将落库的那份主表值——判据与 INSERT/UPDATE 读的是同一份值，
/// 只补判定用的副本会让列落库成 NULL。
///
/// 规则是服务端常量：表名/列名不接受请求传入，取值一律参数化；来源列有值而主档查不到时
/// fail-closed（报具名错误），不静默填默认值。目标列已有非空值时不覆盖——用户提交的
/// 值与既有数据都以原值为准。
/// </summary>
internal static class MasterDerivedColumnFiller
{
    /// <summary>派生列的取值来源。</summary>
    internal enum DerivedSource
    {
        /// <summary>主表来源列的值 → 查主档表取目标列（来源列是主档的键）。</summary>
        MasterLookup,

        /// <summary>本次提交的明细行携带的同名列（各行必须同值）。</summary>
        DetailCarry,
    }

    /// <summary>
    /// 派生列登记。<paramref name="MasterTable"/> 为空表示适用于任何同时具备来源列与目标列的主表；
    /// 非空表示该规则只对该主表生效。<paramref name="NotFoundCode"/> 用于「来源有值但主档查不到」，
    /// <paramref name="AmbiguousCode"/> 用于「明细行引用了不同来源」，都是 fail-closed 的具名错误。
    /// </summary>
    internal sealed record Rule(
        string TargetColumn,
        DerivedSource Source,
        string? MasterTable = null,
        string? SourceColumn = null,
        string? LookupTable = null,
        string? LookupKeyColumn = null,
        string? LookupValueColumn = null,
        string? NotFoundCode = null,
        string? NotFoundMessage = null,
        string? AmbiguousCode = null,
        string? AmbiguousMessage = null);

    /// <summary>
    /// 首批覆盖两类已实测成立的联动：币别带出汇率（任一含这两列的主表），
    /// 采购变更单引用采购单（单别+单号才唯一，服务端无法由单别反查）。
    /// 将来补其它联动列（如税别带税率）只在这里加一条，并在同一提交里附取证结论。
    /// </summary>
    internal static readonly IReadOnlyList<Rule> Rules =
    [
        new Rule(
            TargetColumn: "CURR_RATE",
            Source: DerivedSource.MasterLookup,
            SourceColumn: "CURR_ID",
            LookupTable: "CURR",
            LookupKeyColumn: "CURR_ID",
            LookupValueColumn: "CURR_RATE",
            NotFoundCode: "CURRENCY_NOT_FOUND",
            NotFoundMessage: "币别主档中不存在该币别，无法带出汇率。"),
        new Rule(
            TargetColumn: "PURCHASE_NO",
            Source: DerivedSource.DetailCarry,
            MasterTable: "PUR_PURCHASE_CHANGE_M",
            AmbiguousCode: "DERIVED_COLUMN_AMBIGUOUS",
            AmbiguousMessage: "明细行引用了不同的采购单号，无法确定主表采购单号。"),
    ];

    /// <summary>补齐结果：<paramref name="Error"/> 非空即应中止保存；<paramref name="Filled"/> 是本次实际写入的列。</summary>
    internal sealed record FillResult(FieldError? Error, IReadOnlyList<(string Column, object? Value)> Filled);

    internal static async Task<FillResult> FillAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        WorkbenchDefinition definition,
        IReadOnlyList<FormFieldDefinition> masterFields,
        IDictionary<string, object?> masterValues,
        IReadOnlyList<IReadOnlyDictionary<string, string?>>? details,
        CancellationToken token)
    {
        var filled = new List<(string Column, object? Value)>();
        foreach (var rule in Rules)
        {
            if (rule.MasterTable is { } scoped
                && !scoped.Equals(definition.MasterTable, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // 已有非空值（本次提交的或记录里既有的）保持原值，补齐只负责"空的时候填上"
            if (HasValue(masterValues, rule.TargetColumn)) continue;
            if (!await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.MasterTable, rule.TargetColumn, token))
            {
                continue;
            }
            switch (rule.Source)
            {
                case DerivedSource.MasterLookup:
                {
                    if (rule.SourceColumn is null || !TryGetText(masterValues, rule.SourceColumn, out var sourceValue))
                    {
                        // 没有来源就什么都不做：缺值仍由 DETAIL_NO_FIELDS 判据按原样拒绝
                        continue;
                    }
                    var lookedUp = await LookupAsync(connection, transaction, rule, sourceValue, token);
                    if (lookedUp is null)
                    {
                        // 来源有值而主档查不到：不能猜一个值（静默填 1/0 会让外币单据按本币计价）
                        return new FillResult(new FieldError(rule.SourceColumn,
                            rule.NotFoundMessage ?? "来源主档中查不到对应记录，无法带出该字段。",
                            rule.NotFoundCode ?? "DERIVED_SOURCE_NOT_FOUND"), filled);
                    }
                    masterValues[rule.TargetColumn] = lookedUp;
                    filled.Add((rule.TargetColumn, lookedUp));
                    continue;
                }
                case DerivedSource.DetailCarry:
                {
                    var carried = await CarryFromDetailsAsync(connection, transaction, rule, definition, masterFields, details, token);
                    if (carried.Ambiguous)
                    {
                        return new FillResult(
                            new FieldError(rule.TargetColumn, rule.AmbiguousMessage!, rule.AmbiguousCode!), filled);
                    }
                    if (carried.Value is null) continue;
                    masterValues[rule.TargetColumn] = carried.Value;
                    filled.Add((rule.TargetColumn, carried.Value));
                    continue;
                }
                default:
                    continue;
            }
        }
        return new FillResult(null, filled);
    }

    private readonly record struct CarriedValue(object? Value, bool Ambiguous);

    /// <summary>
    /// 来源值 → 主档取值。表/列名取自规则常量并做标识符与存在性校验，来源值参数化；
    /// 主档表/列不存在、或该行取不到值，一律返回 null 由调用方 fail-closed 处置。
    /// </summary>
    private static async Task<object?> LookupAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Rule rule,
        string sourceValue,
        CancellationToken token)
    {
        var table = rule.LookupTable!;
        if (!WorkbenchSql.Identifier.IsMatch(table) || !WorkbenchSql.Identifier.IsMatch(rule.LookupKeyColumn!)
            || !WorkbenchSql.Identifier.IsMatch(rule.LookupValueColumn!))
        {
            return null;
        }
        if (!await WorkbenchSql.TableExistsAsync(connection, table, token, transaction)) return null;
        if (!await WorkbenchSql.ColumnsExistAsync(connection, table, [rule.LookupKeyColumn!, rule.LookupValueColumn!], token, transaction))
        {
            return null;
        }
        var sql = $"SELECT TOP 1 [{rule.LookupValueColumn}] FROM dbo.[{table}] WHERE [{rule.LookupKeyColumn}]=@Value;";
        await using var command = new SqlCommand(sql, connection, transaction);
        command.Parameters.Add("@Value", SqlDbType.NVarChar, 200).Value = sourceValue;
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : value;
    }

    /// <summary>
    /// 明细行携带的同名列取值：全部非空值必须一致（不一致即无法确定主表该填哪个，fail-closed），
    /// 一致时按主表字段类型转换后返回。明细没有该列、未携带值或转换失败都不补——
    /// 缺值留给 DETAIL_NO_FIELDS 判据报"必须填写该主表字段"。
    /// </summary>
    private static async Task<CarriedValue> CarryFromDetailsAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Rule rule,
        WorkbenchDefinition definition,
        IReadOnlyList<FormFieldDefinition> masterFields,
        IReadOnlyList<IReadOnlyDictionary<string, string?>>? details,
        CancellationToken token)
    {
        if (definition.DetailTable is null || details is null || details.Count == 0) return new CarriedValue(null, false);
        // 明细列必须是物理列：虚拟列的值不落库，不能当来源
        if (!await WorkbenchSql.ColumnExistsAsync(connection, transaction, definition.DetailTable, rule.TargetColumn, token))
        {
            return new CarriedValue(null, false);
        }
        var distinct = new List<string>();
        foreach (var row in details)
        {
            if (!TryGetDetailText(row, rule.TargetColumn, out var text)) continue;
            if (distinct.Contains(text, StringComparer.OrdinalIgnoreCase)) continue;
            distinct.Add(text);
        }
        if (distinct.Count == 0) return new CarriedValue(null, false);
        if (distinct.Count > 1) return new CarriedValue(null, true);
        var field = masterFields.FirstOrDefault(item => item.Key.Equals(rule.TargetColumn, StringComparison.OrdinalIgnoreCase));
        if (field is null) return new CarriedValue(distinct[0], false);
        return RecordPayloadValidator.TryConvert(field.DataType, distinct[0], out var converted)
            ? new CarriedValue(converted, false)
            : new CarriedValue(null, false);
    }

    private static bool HasValue(IDictionary<string, object?> values, string column)
    {
        if (!values.TryGetValue(column, out var value) || value is null) return false;
        return value is not string text || !string.IsNullOrWhiteSpace(text);
    }

    private static bool TryGetText(IDictionary<string, object?> values, string column, out string text)
    {
        text = string.Empty;
        if (!values.TryGetValue(column, out var value) || value is null) return false;
        var converted = WorkbenchSql.ValueToString(value).Trim();
        if (converted.Length == 0) return false;
        text = converted;
        return true;
    }

    /// <summary>提交载荷的键大小写由客户端决定，取值按大小写不敏感匹配。</summary>
    private static bool TryGetDetailText(IReadOnlyDictionary<string, string?> row, string column, out string text)
    {
        text = string.Empty;
        string? raw = null;
        if (!row.TryGetValue(column, out raw))
        {
            foreach (var (key, value) in row)
            {
                if (!key.Equals(column, StringComparison.OrdinalIgnoreCase)) continue;
                raw = value;
                break;
            }
        }
        var trimmed = raw?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return false;
        text = trimmed;
        return true;
    }
}
