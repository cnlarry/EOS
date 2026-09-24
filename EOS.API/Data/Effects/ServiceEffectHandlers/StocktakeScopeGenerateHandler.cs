using System.Data;
using System.Text.Json;
using EOS.API.Data.Inventory;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Effects.ServiceEffectHandlers;

/// <summary>
/// `stocktake-scope-generate`：按盘点范围（库区）生成盘点单明细。
///
/// 盘点单不直接改库存，它的价值全在明细：明细把"这个库里有哪些货、各多少"逐行摊开，
/// 人再逐行填实际盘点数，差异行转成调整单。库位维度落地后，"哪些货"的答案依赖
/// 位置——只按库别列出全部料号，按库位盘出的差异会被库别层面的平均抹平，盘点等于白盘。
///
/// 因此本动作把"该盘哪些行"交给库区范围推导，而不是靠人逐行挑：
///   ① 单头填了盘点范围（库位主档里的库区位置号）且本单还没有明细时才生成——已经有人工
///      录入的明细一律不动，避免覆盖；
///   ② 明细行 = 该库别下**当前**记在范围内库位（含库区自身）上、数量不为零的库存行；
///      区属取库位主档的物化路径（当前态），托盘移出该库区后就不再出现在该区盘点明细里；
///   ③ 账面数与盘点数初始都取当前库存量，差异为 0 起算，人只需改实际数。
///
/// 范围为空时不做任何事而直接报错：盘点单没有明细就是一张空单，而"无明细不可保存"的
/// 兜底判定在那时也会拦下它，这里给出一句更有指向性的说明。
///
/// 与用户点击的 `recalc-account`（重算账面数量）是一对紧挨着的口径，改任一处都要同时读另一处：
///   · 本动作＝**首次生成**：明细还没有时按范围把库存行摊成明细，行数/位置/批次在这里定下来；
///   · `recalc-account`＝**已有明细的重算**：只把每行账面数刷成当前库存量，不新增行、不删行、不动盘点数。
/// 明细已存在时本动作直接返回（不覆盖人工录入），这正是"重算"要独立成按钮的原因。
/// </summary>
public sealed class StocktakeScopeGenerateHandler : IEffectServiceHandler
{
    public string EffectKey => "stocktake-scope-generate";

    public async Task<int> ExecuteAsync(ServiceEffectContext context, CancellationToken token)
    {
        if (context.ExecutionEvent != EffectEvent.Save) return 0;
        var plan = context.Plan;
        if (plan.MasterTable is null || plan.DetailTable is null)
            throw new EffectConfigException("stocktake-scope-generate 需要主表与明细表。");
        if (plan.MasterPkOrder.Count < 2 || context.MasterKeyValues.Count < 2)
            throw new EffectConfigException("stocktake-scope-generate 需要两列主键（单别 / 单号）。");

        var root = context.Action.Params ?? throw new EffectConfigException("stocktake-scope-generate 缺少参数。");
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        var config = StocktakeScopeConfig.Parse(root, plan.MasterTable, plan.DetailTable, columns);

        var type = (context.MasterKeyValues[0] ?? string.Empty).Trim();
        var no = (context.MasterKeyValues[1] ?? string.Empty).Trim();
        if (type.Length == 0 || no.Length == 0)
            throw new EffectConfigException("stocktake-scope-generate 主键值为空，禁止无条件生成。");
        // 主键值按效果计划的主键顺序传入，配置里的列名必须与之一致：不一致会拿单别当单号用，
        // 静默写到别的单据上。
        if (!config.TypeField.Equals(plan.MasterPkOrder[0], StringComparison.OrdinalIgnoreCase)
            || !config.NoField.Equals(plan.MasterPkOrder[1], StringComparison.OrdinalIgnoreCase))
            throw new EffectConfigException(
                $"stocktake-scope-generate 的单别 / 单号列与模块主键顺序不一致（主键为 {plan.MasterPkOrder[0]}, {plan.MasterPkOrder[1]}）。");

        // 明细已存在就什么都不做：明细要么由人录、要么由本动作生成，生成不得覆盖人工录入。
        if (await HasDetailAsync(context, config, type, no, token))
            return 0;

        var (scope, depot) = await ReadHeaderAsync(context, config, type, no, token);
        if (depot.Length == 0)
            throw new EffectValidationException("单据未填库别，无法按库区生成盘点明细。");
        if (scope.Length == 0)
            throw new EffectValidationException("该模块无明细资料不可保存：请填写盘点范围（或手工录入明细）。");
        if (string.Equals(scope, config.SentinelLocationNo, StringComparison.Ordinal))
            throw new EffectValidationException($"盘点范围不能是『未指定位置』本身，请选择实际的库区。");

        var rootPath = await ReadScopePathAsync(context, config, depot, scope, token);
        var serial = await NextSerialAsync(context, config, type, no, token);
        var inserted = await InsertDetailsAsync(context, config, type, no, depot, rootPath, serial, token);
        if (inserted == 0)
            throw new EffectValidationException(
                $"盘点范围 {scope} 下没有账面库存，生成不出盘点明细：该模块无明细资料不可保存。");
        return inserted;
    }

    private static async Task<bool> HasDetailAsync(
        ServiceEffectContext context, StocktakeScopeConfig config, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT TOP 1 1 FROM dbo.{q(config.DetailTable)} "
            + $"WHERE {q(config.TypeField)}=@type AND {q(config.NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        return await command.ExecuteScalarAsync(token) is not null;
    }

    private static async Task<(string Scope, string Depot)> ReadHeaderAsync(
        ServiceEffectContext context, StocktakeScopeConfig config, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT ISNULL({q(config.ScopeField)}, N''), ISNULL({q(config.MasterDepotField)}, N'') "
            + $"FROM dbo.{q(config.MasterTable)} WHERE {q(config.TypeField)}=@type AND {q(config.NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new EffectConfigException($"stocktake-scope-generate 找不到单据 {type}/{no}。");
        return (reader.GetString(0).Trim(), reader.GetString(1).Trim());
    }

    /// <summary>把范围列上的库区位置号解析成物化路径；范围必须是该库别里存在且未停用的库位。</summary>
    private static async Task<string> ReadScopePathAsync(
        ServiceEffectContext context, StocktakeScopeConfig config, string depot, string scope, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT {q(config.PathField)} FROM dbo.{q(config.LocationTable)} "
            + $"WHERE {q(config.LocationDepotField)}=@depot AND {q(config.LocationField)}=@scope;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@depot", SqlDbType.NVarChar, 10).Value = depot;
        command.Parameters.Add("@scope", SqlDbType.NVarChar, 30).Value = scope;
        var path = await command.ExecuteScalarAsync(token);
        if (path is null || path is DBNull)
            throw new EffectValidationException($"盘点范围 {depot}/{scope} 在库位主档里不存在，无法生成盘点明细。");
        return ((string)path).Trim();
    }

    private static async Task<int> NextSerialAsync(
        ServiceEffectContext context, StocktakeScopeConfig config, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT ISNULL(MAX({q(config.SerialField)}), 0) + 1 FROM dbo.{q(config.DetailTable)} "
            + $"WHERE {q(config.TypeField)}=@type AND {q(config.NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// 插入范围内"当前有量"的库存行。区属判定用截断比较而不是 LIKE：库位号里可能含
    /// `%` 或 `_`，LIKE 会把它们当通配符，把范围外的库位也圈进来。
    /// </summary>
    /// <summary>
    /// 明细行 = 该库别下记在范围内库位（含库区自身）且数量不为零的库存行。
    ///
    /// 库存行怎么读交给 <see cref="InventoryQueryService"/>（范围按库位主档的物化路径展开、
    /// 位置/批次怎么归一都在那里）；配置里声明的库存表必须就是余额表本身，
    /// 否则"读的是哪张表"会与配置脱节。
    /// 项次由 C# 按服务返回的顺序编号（料号 → 库位 → 批次），与改造前 `ROW_NUMBER()` 的排序一致。
    /// </summary>
    private static async Task<int> InsertDetailsAsync(
        ServiceEffectContext context, StocktakeScopeConfig config, string type, string no, string depot,
        string rootPath, int serial, CancellationToken token)
    {
        EnsureStockSourceIsBalanceTable(config);
        var rows = await InventoryQueryService.GetRowsAsync(
            context.Connection, context.Transaction,
            new InventoryQueryService.RowScope
            {
                DepotId = depot,
                LocationPathPrefix = rootPath,
                Quantity = InventoryQueryService.RowQuantity.NonZero,
            },
            InventoryQueryService.ReadLock.None, token);
        if (rows.Count == 0) return 0;

        var q = ServiceEffectSql.Q;
        var columnList = string.Join(", ", new[]
        {
            config.TypeField, config.NoField, config.SerialField, config.DetailProductField,
            config.DetailDepotField, config.DetailAccountField, config.DetailCheckField,
            config.DetailLocationField, config.DetailBatchField,
        }.Select(q));
        const int chunkSize = 200;
        var inserted = 0;
        for (var offset = 0; offset < rows.Count; offset += chunkSize)
        {
            var chunk = rows.Skip(offset).Take(chunkSize).ToList();
            var tuples = string.Join(", ", chunk.Select((_, index) =>
                $"(@type, @no, @s{index}, @p{index}, @d{index}, @q{index}, @q{index}, @l{index}, @b{index})"));
            await using var command = new SqlCommand(
                $"INSERT INTO dbo.{q(config.DetailTable)} ({columnList}) VALUES {tuples};",
                context.Connection, context.Transaction);
            command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
            command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
            for (var index = 0; index < chunk.Count; index++)
            {
                var row = chunk[index];
                var nextSerial = serial + offset + index;
                command.Parameters.Add($"@s{index}", SqlDbType.SmallInt).Value =
                    nextSerial > short.MaxValue ? short.MaxValue : (short)nextSerial;
                command.Parameters.Add($"@p{index}", SqlDbType.NVarChar, 60).Value = row.ProductNo;
                command.Parameters.Add($"@d{index}", SqlDbType.NVarChar, 20).Value = row.DepotId;
                command.Parameters.Add($"@q{index}", SqlDbType.Float).Value = (object?)row.Quantity ?? 0d;
                command.Parameters.Add($"@l{index}", SqlDbType.NVarChar, 60).Value = row.LocationNo;
                command.Parameters.Add($"@b{index}", SqlDbType.NVarChar, 60).Value = row.BatchNo;
            }
            inserted += await command.ExecuteNonQueryAsync(token);
        }
        return inserted;
    }

    /// <summary>配置的"库存来源"必须就是余额表本身：允许指向别的表会让收口变成静默读错。</summary>
    private static void EnsureStockSourceIsBalanceTable(StocktakeScopeConfig config)
    {
        foreach (var (actual, expected) in new[]
                 {
                     (config.StockTable, InventoryQueryService.BalanceTable),
                     (config.StockProductField, InventoryQueryService.ProductColumn),
                     (config.StockDepotField, InventoryQueryService.DepotColumn),
                     (config.StockLocationField, InventoryQueryService.LocationColumn),
                     (config.StockQtyField, InventoryQueryService.QuantityColumn),
                     (config.StockBatchField, InventoryQueryService.BatchColumn),
                 })
        {
            if (!string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase))
                throw new EffectConfigException(
                    $"stocktake-scope-generate 的库存来源必须是 {expected}（{InventoryQueryService.BalanceTable} 的库存读取出口），实际配置为 {actual}。");
        }
    }
}

/// <summary>
/// 参数解析（fail-closed：每个库表与列名都必须命中物理列白名单）。主表 / 明细表 / 主键列
/// 直接取效果计划，不重复声明，避免配置与模块实际形态出现两份口径。
/// </summary>
internal sealed record StocktakeScopeConfig(
    string MasterTable,
    string DetailTable,
    string TypeField,
    string NoField,
    string ScopeField,
    string MasterDepotField,
    string LocationTable,
    string LocationDepotField,
    string LocationField,
    string PathField,
    string StockTable,
    string StockDepotField,
    string StockProductField,
    string StockLocationField,
    string StockBatchField,
    string StockQtyField,
    string DetailProductField,
    string DetailDepotField,
    string DetailAccountField,
    string DetailCheckField,
    string DetailLocationField,
    string DetailBatchField,
    string SerialField,
    string SentinelLocationNo)
{
    public static StocktakeScopeConfig Parse(JsonElement root, string masterTable, string detailTable, ISet<string> columns)
    {
        const string label = "stocktake-scope-generate";
        var config = new StocktakeScopeConfig(
            masterTable, detailTable,
            Field(root, "typeField", label), Field(root, "noField", label),
            Field(root, "scopeField", label), Field(root, "masterDepotField", label),
            Table(root, "locationTable", label), Field(root, "locationDepotField", label),
            Field(root, "locationField", label), Field(root, "pathField", label),
            Table(root, "stockTable", label), Field(root, "stockDepotField", label),
            Field(root, "stockProductField", label), Field(root, "stockLocationField", label),
            Field(root, "stockBatchField", label), Field(root, "stockQtyField", label),
            Field(root, "detailProductField", label), Field(root, "detailDepotField", label),
            Field(root, "detailAccountField", label), Field(root, "detailCheckField", label),
            Field(root, "detailLocationField", label), Field(root, "detailBatchField", label),
            Field(root, "serialField", label),
            Optional(root, "sentinelLocationNo", label, "-"));

        foreach (var (table, column) in new[]
                 {
                     (config.MasterTable, config.TypeField), (config.MasterTable, config.NoField),
                     (config.MasterTable, config.ScopeField), (config.MasterTable, config.MasterDepotField),
                     (config.DetailTable, config.TypeField), (config.DetailTable, config.NoField),
                     (config.DetailTable, config.SerialField), (config.DetailTable, config.DetailProductField),
                     (config.DetailTable, config.DetailDepotField), (config.DetailTable, config.DetailAccountField),
                     (config.DetailTable, config.DetailCheckField), (config.DetailTable, config.DetailLocationField),
                     (config.DetailTable, config.DetailBatchField),
                     (config.LocationTable, config.LocationDepotField), (config.LocationTable, config.LocationField),
                     (config.LocationTable, config.PathField),
                     (config.StockTable, config.StockDepotField), (config.StockTable, config.StockProductField),
                     (config.StockTable, config.StockLocationField), (config.StockTable, config.StockBatchField),
                     (config.StockTable, config.StockQtyField),
                 })
        {
            if (!columns.Contains(table + "." + column))
                throw new EffectConfigException($"{label} 列不存在：{table}.{column}。");
        }
        return config;
    }

    private static string Table(JsonElement root, string name, string label)
    {
        var table = Required(root, name, label);
        if (!WorkbenchSql.Identifier.IsMatch(table))
            throw new EffectConfigException($"{label} 表名不合法：{table}。");
        return table;
    }

    private static string Field(JsonElement root, string name, string label) => Required(root, name, label);

    private static string Required(JsonElement root, string name, string label)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : throw new EffectConfigException($"{label} 缺少字符串字段 {name}。");

    private static string Optional(JsonElement root, string name, string label, string fallback)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : fallback;
}
