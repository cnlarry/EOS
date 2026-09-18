using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// fields-metadata-sync: 工资项目设定保存后把"工资项目明细表"的显隐/名称/格式/备注同步到
/// `FIELDS` 元数据（表单字段定义），用于工资项目增减后自动增删明细表的可见列。
/// 语义与原 `HrDomainRules.HrWageItemAfterSaveAsync` 逐字一致：
///   ① 该表已登记字段中前缀匹配者先置为不可见；② 再按 `F_ID = 工资项目的字段列` 回填四项属性。
/// 参数闭合：目标表（FIELDS）的六个列名 + 来源表（工资项目表）的六个列名 + 目标标识值与字段前缀，
/// 全部来自配置并逐个校验为物理列；标识值与前缀只作参数传入，不拼接进 SQL 文本。
/// 解批无反向语义（reverse.kind = none）：字段元数据属派生结果，重算即可。
/// </summary>
public sealed class FieldsMetadataSyncHandler : IEffectServiceHandler
{
    public string EffectKey => "fields-metadata-sync";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("fields-metadata-sync 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, columns);

        var hide = "UPDATE dbo." + ServiceEffectSql.Q(config.TargetTable)
            + " SET " + ServiceEffectSql.Q(config.TargetVisibleColumn) + "=0"
            + " WHERE " + ServiceEffectSql.Q(config.TargetIdColumn) + "=@targetId"
            + " AND " + ServiceEffectSql.Q(config.TargetFieldColumn) + " LIKE @prefix + '%';";
        var affected = await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, hide,
            [
                new EffectSqlParameter("@targetId", config.TargetId),
                new EffectSqlParameter("@prefix", config.Prefix),
            ], token);

        var sync = "UPDATE f SET f." + ServiceEffectSql.Q(config.TargetVisibleColumn) + "=w." + ServiceEffectSql.Q(config.SourceVisibleColumn)
            + ", f." + ServiceEffectSql.Q(config.TargetDescColumn) + "=w." + ServiceEffectSql.Q(config.SourceDescColumn)
            + ", f." + ServiceEffectSql.Q(config.TargetFormatColumn) + "=w." + ServiceEffectSql.Q(config.SourceFormatColumn)
            + ", f." + ServiceEffectSql.Q(config.TargetRemarkColumn) + "=w." + ServiceEffectSql.Q(config.SourceRemarkColumn)
            + " FROM dbo." + ServiceEffectSql.Q(config.TargetTable) + " f INNER JOIN dbo."
            + ServiceEffectSql.Q(config.SourceTable) + " w ON f." + ServiceEffectSql.Q(config.TargetFieldColumn)
            + "=w." + ServiceEffectSql.Q(config.SourceFieldColumn)
            + " WHERE f." + ServiceEffectSql.Q(config.TargetIdColumn) + "=@targetId;";
        affected += await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, sync,
            [new EffectSqlParameter("@targetId", config.TargetId)], token);
        return affected;
    }

    /// <summary>参数解析（fail-closed：所有列名都必须是物理列；标识值/前缀非空）。</summary>
    internal static FieldsMetadataSyncConfig Parse(JsonElement root, ISet<string> columns)
    {
        var target = Object(root, "target");
        var source = Object(root, "source");
        var config = new FieldsMetadataSyncConfig(
            Required(root, "targetId"),
            Required(root, "prefix"),
            Required(target, "table"),
            Required(target, "idColumn"),
            Required(target, "fieldColumn"),
            Required(target, "visibleColumn"),
            Required(target, "descColumn"),
            Required(target, "formatColumn"),
            Required(target, "remarkColumn"),
            Required(source, "table"),
            Required(source, "fieldColumn"),
            Required(source, "visibleColumn"),
            Required(source, "descColumn"),
            Required(source, "formatColumn"),
            Required(source, "remarkColumn"));
        foreach (var (table, column) in new[]
                 {
                     (config.TargetTable, config.TargetIdColumn), (config.TargetTable, config.TargetFieldColumn),
                     (config.TargetTable, config.TargetVisibleColumn), (config.TargetTable, config.TargetDescColumn),
                     (config.TargetTable, config.TargetFormatColumn), (config.TargetTable, config.TargetRemarkColumn),
                     (config.SourceTable, config.SourceFieldColumn), (config.SourceTable, config.SourceVisibleColumn),
                     (config.SourceTable, config.SourceDescColumn), (config.SourceTable, config.SourceFormatColumn),
                     (config.SourceTable, config.SourceRemarkColumn),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"fields-metadata-sync 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static JsonElement Object(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new EffectConfigException($"fields-metadata-sync 缺少对象字段 {name}。");

    private static string Required(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"fields-metadata-sync 缺少字符串字段 {name}。");
}

internal sealed record FieldsMetadataSyncConfig(
    string TargetId,
    string Prefix,
    string TargetTable,
    string TargetIdColumn,
    string TargetFieldColumn,
    string TargetVisibleColumn,
    string TargetDescColumn,
    string TargetFormatColumn,
    string TargetRemarkColumn,
    string SourceTable,
    string SourceFieldColumn,
    string SourceVisibleColumn,
    string SourceDescColumn,
    string SourceFormatColumn,
    string SourceRemarkColumn);
