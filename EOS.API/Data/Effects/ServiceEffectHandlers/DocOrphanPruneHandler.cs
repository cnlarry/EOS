using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// doc-orphan-prune: 单据内的"主/明细互不引用行"清理循环（工单 BOM 保存后使用）：
///   ① 删主表中"没有任何明细引用其产品、且不是本行产品、也不是制令主产品"的行；
///   ② 删明细中"没有主表行对应其产品"的行；
///   ③ 重复 ①② 直到一轮下来两表都无删除（上限 <see cref="MaxIterations"/> 轮，超限即报错）。
/// 语义与原 `MocDomainRules.MocBomStruAfterSaveAsync` 逐字一致（含两处排除项）。
/// 参数闭合：主/明细表 + 六个列名 + 制令根表与根列，全部校验为物理列；
/// "本行产品"取模块主键第三列的值（循环内不删本行）。
/// </summary>
public sealed class DocOrphanPruneHandler : IEffectServiceHandler
{
    private const int MaxIterations = 100;

    public string EffectKey => "doc-orphan-prune";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        var root = context.Action.Params ?? throw new EffectConfigException("doc-orphan-prune 缺少参数。");
        if (context.ExecutionEvent is not (EffectEvent.ApproveEffect or EffectEvent.Save)) return 0;
        var plan = context.Plan;
        if (plan.MasterPkOrder.Count < 3 || context.MasterKeyValues.Count < 3)
            throw new EffectConfigException("doc-orphan-prune 需要三列主键（类型/单号/行产品）。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = Parse(root, columns);
        var type = context.MasterKeyValues[0] ?? string.Empty;
        var no = context.MasterKeyValues[1] ?? string.Empty;
        var keepProduct = context.MasterKeyValues[2] ?? string.Empty;

        var rootProduct = await ReadRootProductAsync(context, config, type, no, token);
        var affected = 0;
        for (var round = 0; round < MaxIterations; round++)
        {
            var deleteMaster = "DELETE m FROM dbo." + ServiceEffectSql.Q(config.MasterTable) + " m"
                + " WHERE m." + ServiceEffectSql.Q(config.TypeField) + "=@type AND m." + ServiceEffectSql.Q(config.NoField) + "=@no"
                + " AND m." + ServiceEffectSql.Q(config.MasterProductField) + "<>@keep"
                + " AND m." + ServiceEffectSql.Q(config.MasterProductField) + "<>@root"
                + " AND m." + ServiceEffectSql.Q(config.MasterProductField) + " NOT IN (SELECT "
                + ServiceEffectSql.Q(config.DetailRefField) + " FROM dbo." + ServiceEffectSql.Q(config.DetailTable)
                + " WHERE " + ServiceEffectSql.Q(config.TypeField) + "=@type AND " + ServiceEffectSql.Q(config.NoField) + "=@no);";
            var removedMaster = await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, deleteMaster,
                [
                    new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no),
                    new EffectSqlParameter("@keep", keepProduct), new EffectSqlParameter("@root", rootProduct),
                ], token);

            var deleteDetail = "DELETE d FROM dbo." + ServiceEffectSql.Q(config.DetailTable) + " d"
                + " WHERE d." + ServiceEffectSql.Q(config.TypeField) + "=@type AND d." + ServiceEffectSql.Q(config.NoField) + "=@no"
                + " AND d." + ServiceEffectSql.Q(config.DetailProductField) + " NOT IN (SELECT "
                + ServiceEffectSql.Q(config.MasterProductField) + " FROM dbo." + ServiceEffectSql.Q(config.MasterTable)
                + " WHERE " + ServiceEffectSql.Q(config.TypeField) + "=@type AND " + ServiceEffectSql.Q(config.NoField) + "=@no);";
            var removedDetail = await ServiceEffectSql.ExecAsync(context.Connection, context.Transaction, deleteDetail,
                [new EffectSqlParameter("@type", type), new EffectSqlParameter("@no", no)], token);

            affected += removedMaster + removedDetail;
            if (removedMaster == 0 && removedDetail == 0) return affected;
        }
        throw new EffectConfigException($"doc-orphan-prune 在 {MaxIterations} 轮内未收敛，已中止（请检查主/明细互引关系）。");
    }

    private static async Task<string> ReadRootProductAsync(
        ServiceEffectContext context, DocOrphanPruneConfig config, string type, string no, CancellationToken token)
    {
        var sql = "SELECT TOP 1 LTRIM(RTRIM(ISNULL(" + ServiceEffectSql.Q(config.RootField) + ",''))) FROM dbo."
            + ServiceEffectSql.Q(config.RootTable) + " WHERE " + ServiceEffectSql.Q(config.TypeField)
            + "=@type AND " + ServiceEffectSql.Q(config.NoField) + "=@no;";
        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.AddWithValue("@type", type);
        command.Parameters.AddWithValue("@no", no);
        return (await command.ExecuteScalarAsync(token) as string)?.Trim() ?? string.Empty;
    }

    /// <summary>参数解析（fail-closed：所有列名必须是物理列）。</summary>
    internal static DocOrphanPruneConfig Parse(JsonElement root, ISet<string> columns)
    {
        var config = new DocOrphanPruneConfig(
            Required(root, "masterTable"), Required(root, "detailTable"), Required(root, "typeField"),
            Required(root, "noField"), Required(root, "masterProductField"), Required(root, "detailProductField"),
            Required(root, "detailRefField"), Required(root, "rootTable"), Required(root, "rootField"));
        foreach (var (table, column) in new[]
                 {
                     (config.MasterTable, config.TypeField), (config.MasterTable, config.NoField),
                     (config.MasterTable, config.MasterProductField), (config.DetailTable, config.TypeField),
                     (config.DetailTable, config.NoField), (config.DetailTable, config.DetailProductField),
                     (config.DetailTable, config.DetailRefField), (config.RootTable, config.TypeField),
                     (config.RootTable, config.NoField), (config.RootTable, config.RootField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"doc-orphan-prune 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static string Required(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"doc-orphan-prune 缺少字符串字段 {name}。");
}

internal sealed record DocOrphanPruneConfig(
    string MasterTable,
    string DetailTable,
    string TypeField,
    string NoField,
    string MasterProductField,
    string DetailProductField,
    string DetailRefField,
    string RootTable,
    string RootField);
