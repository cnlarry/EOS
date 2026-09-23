using System.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

// `restock-scope-restock`（换区重盘）：用户在盘点单上点一下，按新盘点范围把明细整单换掉。
//
// 与保存期的 `stocktake-scope-generate` 同源（同一张生成 SQL：范围内有量的库存行摊成明细，
// 位置/批次原样带入，账面数与盘点数按当前量起算），差别只有两处：
//   ① 生成器只在"明细为空"时跑（保护人工录入），本操作专为"范围选错了、整单重来"准备，
//      因此先删本单既有明细再生成；
//   ② 范围不取单头 LOCATION_ROOT_NO，而取本次点击带进来的参数 scopeRoot。
//
// 三条有意写死的边界（与生成器不同的取舍，改前先读）:
//   · 必须未结案且未转过调整单：已结案/已转单的明细是追溯凭据，替换等于销毁证据，直接拒绝；
//   · 不要求已批核：重盘的典型场景恰恰是"单子还没批、范围填错了"，要求先批核再重盘等于逼人
//     把一张错单先变成既成事实（与 recalc/generate 两按钮的"已批核才能点"有意不同）；
//   · 项次从 1 重新编号：这是整单替换，不是增量补行；下游按项次引用的单据（送/退货项次）
//     在替换后可能错位——确认框文案点名这件事，审计留痕备查。
// 负库存行不进明细：来源库位余额为负时拒绝并点名，而不是生成一条盘点数为负的行
// （统一表单的"盘点数不得小于零"规则走保存期，本操作直接写库，必须自己把这道门带上）。
internal sealed class RestockScopeHandler : IDocumentUserAction, IDocumentActionPlacement
{
    public const string ActionKey = "restock-scope";

    public const string ScopeParameter = "scopeRoot";

    private const string MasterTable = "INV_CHECK_STOCK_M";
    private const string DetailTable = "INV_CHECK_STOCK_D";
    private const string TypeField = "CHECK_STOCK_TYPE";
    private const string NoField = "CHECK_STOCK_NO";
    private const string ScopeField = "LOCATION_ROOT_NO";
    private const string MasterDepotField = "DEPOT_ID";
    private const string FinishedField = "FINISHED_TAG";
    private const string AdjustNoField = "ADJUST_NO";
    private const string SerialField = "SERIAL_NO";
    private const string DetailProductField = "PRO_NO";
    private const string DetailDepotField = "DEPOT_ID";
    private const string DetailAccountField = "ACCOUNT_QTY";
    private const string DetailCheckField = "CHECK_QTY";
    private const string DetailLocationField = "LOCATION_NO";
    private const string DetailBatchField = "BATCH_NO";
    private const string LocationTable = "DEPOT_LOCATION";
    private const string LocationDepotField = "DEPOT_ID";
    private const string LocationField = "LOCATION_NO";
    private const string PathField = "LOCATION_PATH";
    private const string StockTable = "INV_PRO_DEPOT";
    private const string StockDepotField = "DEPOT_ID";
    private const string StockProductField = "PRO_NO";
    private const string StockLocationField = "LOCATION_NO";
    private const string StockBatchField = "BATCH_NO";
    private const string StockQtyField = "QTY";
    private const string SentinelLocationNo = "-";

    public string Key => ActionKey;

    public string Label => "换区重盘";

    /// <summary>换的是明细行，按钮落在子表标题栏。</summary>
    public string Placement => DocumentActionPlacements.Detail;

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var definition = context.Definition;
        if (definition.DetailTable is null)
        {
            throw new InvalidOperationException("该模块没有明细表，无法换区重盘。");
        }

        var q = ServiceEffectSql.Q;
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        foreach (var (table, column) in new[]
                 {
                     (MasterTable, TypeField), (MasterTable, NoField),
                     (MasterTable, ScopeField), (MasterTable, MasterDepotField),
                     (MasterTable, FinishedField), (MasterTable, AdjustNoField),
                     (DetailTable, TypeField), (DetailTable, NoField),
                     (DetailTable, SerialField), (DetailTable, DetailProductField),
                     (DetailTable, DetailDepotField), (DetailTable, DetailAccountField),
                     (DetailTable, DetailCheckField), (DetailTable, DetailLocationField),
                     (DetailTable, DetailBatchField),
                     (LocationTable, LocationDepotField), (LocationTable, LocationField),
                     (LocationTable, PathField),
                     (StockTable, StockDepotField), (StockTable, StockProductField),
                     (StockTable, StockLocationField), (StockTable, StockBatchField),
                     (StockTable, StockQtyField),
                 })
        {
            if (!columns.Contains(table + "." + column))
            {
                throw new InvalidOperationException($"换区重盘需要 {table}.{column}，该模块或库存表没有这一列，请联系管理员调整。");
            }
        }

        var type = context.KeyValues.Count > 0 ? context.KeyValues[0].Trim() : string.Empty;
        var no = context.KeyValues.Count > 1 ? context.KeyValues[1].Trim() : string.Empty;
        if (type.Length == 0 || no.Length == 0)
        {
            throw new InvalidOperationException("缺少单据主键，禁止无条件重盘。");
        }

        var (depot, finished, adjustNo) = await ReadHeaderAsync(context, type, no, token);
        if (finished)
        {
            throw new InvalidOperationException("该盘点单已结案，明细是追溯凭据，不能换区重盘。");
        }
        if (adjustNo.Length > 0)
        {
            throw new InvalidOperationException($"该盘点单已生成过调整单 {adjustNo}，明细是追溯凭据，不能换区重盘。");
        }
        if (depot.Length == 0)
        {
            throw new InvalidOperationException("单据未填库别，无法按库区重盘。");
        }

        var scope = context.Parameters.TryGetValue(ScopeParameter, out var scopeValue)
            ? scopeValue?.Trim() ?? string.Empty
            : string.Empty;
        if (scope.Length == 0)
        {
            throw new InvalidOperationException("请填写盘点范围（库区位置号），再换区重盘。");
        }
        if (string.Equals(scope, SentinelLocationNo, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("盘点范围不能是『未指定位置』本身，请选择实际的库区。");
        }
        var rootPath = await ReadScopePathAsync(context, depot, scope, token);

        var existing = await CountDetailsAsync(context, type, no, token);
        var negatives = await ListNegativeStockAsync(context, depot, rootPath, token);
        if (negatives.Count > 0)
        {
            throw new InvalidOperationException(
                "范围内有负库存，无法生成盘点明细（盘点数不得小于零）：" + string.Join("、", negatives.Take(5))
                + (negatives.Count > 5 ? $"……等 {negatives.Count} 处" : string.Empty));
        }
        var incoming = await CountIncomingAsync(context, depot, rootPath, token);
        if (incoming == 0)
        {
            throw new InvalidOperationException($"盘点范围 {scope} 下没有账面库存，生成不出盘点明细。");
        }

        // 探路与执行走同一事务：删明细 + 按新范围重插都在调用方事务里，
        // 框架"执行后回滚"即可兜住探路，处理器不必自行分支。
        await DeleteDetailsAsync(context, type, no, token);
        await InsertDetailsAsync(context, type, no, depot, rootPath, token);

        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已按库区 {scope} 重盘：原 {existing} 行明细已替换，新生成 {incoming} 行（项次从 1 重新编号，账面数与盘点数按当前库存起算）。"
            + "下游若有单据按原明细项次引用，请核对引用是否错位。");
    }

    private static async Task<(string Depot, bool Finished, string AdjustNo)> ReadHeaderAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT ISNULL({q(MasterDepotField)}, N''), ISNULL({q(FinishedField)}, 0), ISNULL({q(AdjustNoField)}, N'') "
            + $"FROM dbo.{q(MasterTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            throw new InvalidOperationException($"找不到盘点单 {type}/{no}。");
        }
        return (reader.GetString(0).Trim(), reader.GetBoolean(1), reader.GetString(2).Trim());
    }

    /// <summary>范围列上的库区位置号解析成物化路径；范围必须是该库别里存在的库位。</summary>
    private static async Task<string> ReadScopePathAsync(
        DocumentActionContext context, string depot, string scope, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT {q(PathField)} FROM dbo.{q(LocationTable)} "
            + $"WHERE {q(LocationDepotField)}=@depot AND {q(LocationField)}=@scope;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@depot", SqlDbType.NVarChar, 10).Value = depot;
        command.Parameters.Add("@scope", SqlDbType.NVarChar, 30).Value = scope;
        var path = await command.ExecuteScalarAsync(token);
        if (path is null || path is DBNull)
        {
            throw new InvalidOperationException($"盘点范围 {depot}/{scope} 在库位主档里不存在，无法重盘。");
        }
        return ((string)path).Trim();
    }

    private static async Task<int> CountDetailsAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM dbo.{q(DetailTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    /// <summary>范围内数量为负的库存行（料号/库位/批次）：有一行即整单拒绝。</summary>
    private static async Task<IReadOnlyList<string>> ListNegativeStockAsync(
        DocumentActionContext context, string depot, string rootPath, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT TOP 6 LTRIM(RTRIM(s.{q(StockProductField)})) + '/' + LTRIM(RTRIM(s.{q(StockLocationField)})) + '/' + LTRIM(RTRIM(ISNULL(s.{q(StockBatchField)}, N''))) "
            + $"FROM dbo.{q(StockTable)} s "
            + $"JOIN dbo.{q(LocationTable)} l ON l.{q(LocationDepotField)}=s.{q(StockDepotField)} "
            + $" AND l.{q(LocationField)}=s.{q(StockLocationField)} "
            + $"WHERE s.{q(StockDepotField)}=@depot AND ISNULL(s.{q(StockQtyField)}, 0) < 0 "
            + $"AND (l.{q(PathField)}=@rootPath OR SUBSTRING(l.{q(PathField)}, 1, LEN(@rootPath)+1) = @rootPath + '/');",
            context.Connection, context.Transaction);
        command.Parameters.Add("@depot", SqlDbType.NVarChar, 10).Value = depot;
        command.Parameters.Add("@rootPath", SqlDbType.NVarChar, 300).Value = rootPath;
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add(reader.GetString(0));
        }
        return rows;
    }

    /// <summary>新范围下能生成几行（有量的库存行数）：探路文案与空范围拒绝共用。</summary>
    private static async Task<int> CountIncomingAsync(
        DocumentActionContext context, string depot, string rootPath, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM dbo.{q(StockTable)} s "
            + $"JOIN dbo.{q(LocationTable)} l ON l.{q(LocationDepotField)}=s.{q(StockDepotField)} "
            + $" AND l.{q(LocationField)}=s.{q(StockLocationField)} "
            + $"WHERE s.{q(StockDepotField)}=@depot AND ISNULL(s.{q(StockQtyField)}, 0) <> 0 "
            + $"AND (l.{q(PathField)}=@rootPath OR SUBSTRING(l.{q(PathField)}, 1, LEN(@rootPath)+1) = @rootPath + '/');",
            context.Connection, context.Transaction);
        command.Parameters.Add("@depot", SqlDbType.NVarChar, 10).Value = depot;
        command.Parameters.Add("@rootPath", SqlDbType.NVarChar, 300).Value = rootPath;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    private static async Task DeleteDetailsAsync(
        DocumentActionContext context, string type, string no, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"DELETE FROM dbo.{q(DetailTable)} WHERE {q(TypeField)}=@type AND {q(NoField)}=@no;",
            context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// 按新范围插入明细：与保存期生成器同一把尺子（范围内有量行，位置/批次原样带入，
    /// 账面数与盘点数都取当前量），项次从 1 起排（整单替换，不保留原编号）。
    /// </summary>
    private static async Task InsertDetailsAsync(
        DocumentActionContext context, string type, string no, string depot, string rootPath, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var sql = "INSERT INTO dbo." + q(DetailTable)
            + " (" + string.Join(", ", new[]
            {
                TypeField, NoField, SerialField, DetailProductField,
                DetailDepotField, DetailAccountField, DetailCheckField,
                DetailLocationField, DetailBatchField,
            }.Select(q)) + ") "
            + "SELECT @type, @no, ROW_NUMBER() OVER (ORDER BY s." + q(StockProductField)
            + ", s." + q(StockLocationField) + ", s." + q(StockBatchField) + "), "
            + "s." + q(StockProductField) + ", s." + q(StockDepotField)
            + ", s." + q(StockQtyField) + ", s." + q(StockQtyField)
            + ", s." + q(StockLocationField) + ", s." + q(StockBatchField)
            + " FROM dbo." + q(StockTable) + " s "
            + "JOIN dbo." + q(LocationTable) + " l ON l." + q(LocationDepotField) + "=s." + q(StockDepotField)
            + " AND l." + q(LocationField) + "=s." + q(StockLocationField)
            + " WHERE s." + q(StockDepotField) + "=@depot AND s." + q(StockQtyField) + "<>0 AND (l." + q(PathField)
            + "=@rootPath OR SUBSTRING(l." + q(PathField) + ", 1, LEN(@rootPath)+1) = @rootPath + '/');";

        await using var command = new SqlCommand(sql, context.Connection, context.Transaction);
        command.Parameters.Add("@type", SqlDbType.NVarChar, 20).Value = type;
        command.Parameters.Add("@no", SqlDbType.NVarChar, 40).Value = no;
        command.Parameters.Add("@depot", SqlDbType.NVarChar, 10).Value = depot;
        command.Parameters.Add("@rootPath", SqlDbType.NVarChar, 300).Value = rootPath;
        await command.ExecuteNonQueryAsync(token);
    }
}
