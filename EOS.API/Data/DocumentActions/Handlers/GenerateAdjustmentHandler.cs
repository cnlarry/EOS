using System.Data;
using EOS.API.Data.Effects;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Data.Inventory;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

/// <summary>
/// `generate-adjustment`（生成调整单）：用户在盘点单上点一下，把**盈亏行**转成一张库存调整单（130107）并过账。
///
/// 与既有实现同一件事（`btnAdjust`）：有差异才转、差异为 0 的行不转、
/// 转完把调整单号写回盘点单。两处**有意不同于既有实现**：
///   ① 逐行携带**库位与批次**（旧 SQL 只带库别与料号，库位维度落地后会转丢位置——见 P2-12）；
///   ② 下游单据走统一记录创建路径（`WorkbenchCommandHandler.CreateRecordAsync`）：单号、归属、校验、
///      审计与效果链一律照常生效，处理器不自己拼 INSERT。
///
/// **业务幂等（`once`＝转后结案锁死）**：生成成功后在同一事务里置盘点单 `FINISHED_TAG=1` 并写回
/// `ADJUST_TYPE/ADJUST_NO`；再次点击会被这两道守卫拒绝。结案锁死配套的服务端守卫见
/// `WorkbenchApprovalService`（已结案拒绝解批），前端另在已结案时禁用编辑/删除/解批。
///
/// **补偿语义（有意写清，不是遗漏）**：下游单据由创建路径在**它自己的事务**里落库，
/// 与"回写来源 + 结案"不在同一事务（要求走统一创建路径，两者不可兼得）。因此若创建成功、
/// 回写失败，动作会返回失败并**带上已生成的调整单号**，请按该单号核对后处理——审计里同时留有记录。
/// 调整单解批/删除由它自己的生命周期规则管（库存由 `inventory-move` 的反向语义回退）；
/// 盘点单上的 `ADJUST_TYPE/ADJUST_NO` 不在解批时自动清空——它记录的是"这张盘点单转过单"这个事实。
/// </summary>
internal sealed class GenerateAdjustmentHandler(
    IPermissionService permissions,
    WorkbenchDefinitionBuilder definitionBuilder,
    WorkbenchCommandHandler commandHandler,
    WorkbenchApprovalService approvalService,
    ILogger<GenerateAdjustmentHandler> logger) : IDocumentUserAction, IDocumentActionPlacement
{
    public const string ActionKey = "generate-adjustment";

    /// <summary>库存调整单模块（既有实现按 MODULES.M_ALIAS='INV_OCCUR_ADJUST' 找默认单别）。</summary>
    private const int TargetModuleId = 130107;

    private const string CurrencyTable = "CURR";
    private const string ProductTable = "PRODUCT";
    private const string DepotField = "DEPOT_ID";
    private const string ProductField = "PRO_NO";
    private const string LocationField = "LOCATION_NO";
    private const string BatchField = "BATCH_NO";
    private const string AccountQtyField = "ACCOUNT_QTY";
    private const string CheckedQtyField = "CHECK_QTY";
    private const string AdjustTypeField = "ADJUST_TYPE";
    private const string AdjustNoField = "ADJUST_NO";
    private const string FinishedTagField = "FINISHED_TAG";
    private const string CountDateField = "COUNT_DATE";
    private const string TargetDateField = "OCCUR_DATE";
    private const string TargetQtyField = "QTY";
    private const string TargetPriceField = "PRICE";
    private const string TargetUnitField = "UNIT_ID";
    private const string TargetCurrencyField = "CURR_ID";
    private const string TargetRateField = "CURR_RATE";
    private const string TargetRemarkField = "REMARK";
    private const string TargetAmountField = "AMOUNT";

    public string Key => ActionKey;

    public string Label => "生成调整单";

    /// <summary>作用于本单的盈亏行（明细级），按钮落在子表标题栏。</summary>
    public string Placement => DocumentActionPlacements.Detail;

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var source = context.Definition;
        if (source.DetailTable is null)
        {
            throw new InvalidOperationException("该模块没有明细表，无法按差异生成调整单。");
        }

        var q = ServiceEffectSql.Q;
        var columns = await new EffectPhysicalColumns().LoadAsync(context.Connection, token, context.Transaction);
        foreach (var (table, column) in new[]
                 {
                     (source.MasterTable, CountDateField), (source.MasterTable, AdjustTypeField),
                     (source.MasterTable, AdjustNoField), (source.MasterTable, FinishedTagField),
                     (source.DetailTable, ProductField), (source.DetailTable, DepotField),
                     (source.DetailTable, LocationField), (source.DetailTable, BatchField),
                     (source.DetailTable, AccountQtyField), (source.DetailTable, CheckedQtyField),
                 })
        {
            if (!columns.Contains(table + "." + column))
            {
                throw new InvalidOperationException($"生成调整单需要 {table}.{column}，该模块没有这一列，请联系管理员调整。");
            }
        }

        var state = await ReadSourceStateAsync(context, token);
        if (state.Finished)
        {
            throw new InvalidOperationException("该盘点单已结案（已转单锁死），不能再次生成调整单。");
        }
        if (state.AdjustNo.Length > 0)
        {
            throw new InvalidOperationException($"该盘点单已经生成过调整单 {state.AdjustNo.Trim()}，不能重复生成。");
        }

        var differences = await ReadDifferenceRowsAsync(context, state.Depot, token);
        if (differences.Count == 0)
        {
            return new DocumentActionResult(DocumentActionOutcome.Message, "该盘点单没有盈亏（盘点数＝账面数），不需要调整。");
        }

        // 探路：下游单据由统一创建路径在**它自己的事务**里落库，外层"执行后回滚"兜不住它，
        // 所以这里必须显式只做预检——把将发生什么说清楚，一行都不写（幂等键也照旧不占）。
        if (context.Preview)
        {
            return new DocumentActionResult(DocumentActionOutcome.Message,
                $"将生成库存调整单：{differences.Count} 行盈亏，过账后本盘点单结案。");
        }

        // 下游单据的权限门：由 API 判断，不因为"是系统生成的"而跳过——生成与手工新建同一把尺子。
        var targetRights = (await permissions.GetAsync(context.ExecutorUserId, TargetModuleId, token)).Rights;
        if (!targetRights.CanAddNew)
        {
            throw new InvalidOperationException("没有库存调整单的新增权限，请联系管理员开通后再生成。");
        }

        var targetDefinition = await definitionBuilder.GetDefinitionAsync(
            TargetModuleId, context.ExecutorUserId, targetRights.ExecuteTag, targetRights.CanViewCost,
            targetRights.CanViewSecrecy, targetRights.DeniedMasterFields, targetRights.DeniedDetailFields, token)
            ?? throw new InvalidOperationException($"找不到模块 {TargetModuleId} 的定义，无法生成调整单。");
        var targetForm = await definitionBuilder.GetFormDefinitionAsync(
            targetDefinition, context.ExecutorUserId, "new", targetRights.CanViewCost, targetRights.CanViewSecrecy,
            targetRights.DeniedMasterFields, targetRights.DeniedDetailFields,
            targetRights.DenyNewMasterFields, targetRights.DenyNewDetailFields,
            targetRights.DenyModiMasterFields, targetRights.DenyModiDetailFields, token,
            targetRights.CanAddNew, targetRights.CanEdit, targetRights.CanDelete, targetRights.CanApprove,
            targetRights.CanDeapprove, targetRights.CanEndCase, targetRights.CanUnEndCase,
            targetRights.CanFileView, targetRights.CanFileUpda, targetRights.CanFileEdit, targetRights.CanFileDele);
        if (targetForm is null)
        {
            throw new InvalidOperationException("库存调整单的表单定义不可用，无法生成调整单。");
        }

        // 只提交目标表单认得的列（与手工新建同一把尺子）：按权限/可见性被剔除的列不硬塞，
        // 否则创建路径会以"字段不在表单定义中"整单拒绝；缺必填列则照旧被它拦下。
        var allowedDetailKeys = targetForm.DetailFields
            .Where(field => !field.IsVirtual)
            .Select(field => field.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var request = new SaveRecordRequest(
            BuildTargetMasterValues(state, differences),
            differences
                .Select(row => (IReadOnlyDictionary<string, string?>)row.Values
                    .Where(pair => allowedDetailKeys.Contains(pair.Key))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase))
                .ToList());
        // 统一创建路径自带事务与连接（单号/归属/校验/审计/效果链一并生效）。
        var created = await commandHandler.CreateRecordAsync(
            targetDefinition, targetForm, request, context.Executor,
            context.ExecutorUserId, dataFilter: null, token);
        if (created.Status != RecordAccessStatus.Ok || created.Key is null)
        {
            var errors = created.FieldErrors is { Count: > 0 }
                ? "（" + string.Join("；", created.FieldErrors.Take(5).Select(error =>
                    error.RowIndex is int row ? $"第{row + 1}行 {error.Field}：{error.Message}" : $"{error.Field}：{error.Message}")) + "）"
                : string.Empty;
            throw new InvalidOperationException($"生成调整单失败：{created.ErrorMessage ?? created.ErrorCode ?? "未知原因"}{errors}");
        }
        var targetKey = created.Key;
        if (targetKey.Count < 2)
        {
            throw new InvalidOperationException(
                $"调整单已生成但主键不完整（{string.Join(',', targetKey)}），请到库存调整单里核对。");
        }
        var adjustType = targetKey[0];
        var adjustNo = targetKey[1];

        // 过账：库存调整单的批核链里挂着 inventory-move（写库存流水）。既有实现在没有流程时同样自动批核。
        var approved = await approvalService.WorkflowAsync(
            targetDefinition, targetKey, approve: true, context.Executor, context.ExecutorUserId,
            idempotencyKey: null, token, message: $"由盘点单 {state.Type}/{state.No} 的盈亏生成");
        if (approved.Status != RecordAccessStatus.Ok)
        {
            throw new InvalidOperationException(
                $"调整单 {adjustNo} 已生成但过账失败：{approved.ErrorMessage ?? approved.ErrorCode}；请到库存调整单里核对该单后处理。");
        }

        // 来源回写 + 结案锁死（同一事务）：这一步就是"这张盘点单已经转出过调整单"的唯一凭据。
        await WriteBackAsync(context, adjustType, adjustNo, token);
        logger.LogInformation("盘点单生成调整单 module={ModuleId} source={Type}/{No} target={AdjustNo} diffRows={Rows}",
            source.ModuleId, state.Type, state.No, adjustNo, differences.Count);

        return new DocumentActionResult(
            DocumentActionOutcome.Navigated,
            $"已生成库存调整单 {adjustNo}（差异 {differences.Count} 行）并过账，盘点单已结案。",
            TargetModuleId,
            targetKey);
    }

    // ===== 源单据 =====

    private sealed record SourceState(string Type, string No, string Depot, DateTime? CountDate, bool Finished, string AdjustNo);

    private static async Task<SourceState> ReadSourceStateAsync(DocumentActionContext context, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var definition = context.Definition;
        var where = string.Join(" AND ", definition.MasterPkOrder.Select((column, index) => $"{q(column)}=@k{index}"));
        await using var command = new SqlCommand(
            $"""
            SELECT ISNULL({q(DepotField)}, N''), {q(CountDateField)},
                   ISNULL({q(FinishedTagField)}, 0), ISNULL({q(AdjustNoField)}, N'')
            FROM dbo.{q(definition.MasterTable)} WITH (UPDLOCK, HOLDLOCK) WHERE {where};
            """, context.Connection, context.Transaction);
        for (var index = 0; index < definition.MasterPkOrder.Count; index++)
        {
            command.Parameters.AddWithValue($"@k{index}", context.KeyValues[index]);
        }
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
        {
            throw new InvalidOperationException("找不到目标盘点单。");
        }
        var depot = reader.GetString(0).Trim();
        var countDate = reader.IsDBNull(1) ? null : (DateTime?)reader.GetDateTime(1);
        var finished = reader.GetBoolean(2);
        var adjustNo = reader.GetString(3).Trim();
        await reader.DisposeAsync();
        return new SourceState(context.KeyValues[0].Trim(), context.KeyValues[1].Trim(), depot, countDate, finished, adjustNo);
    }

    private sealed record DifferenceRow(IReadOnlyDictionary<string, string?> Values);

    /// <summary>
    /// 盈亏行＝盘点数≠账面数的行。逐行携带库位与批次（既有实现只带库别与料号），
    /// 单价取该库存行的成本价、币别取本位币（与既有实现同：无成本价按 0 起算）。
    ///
    /// 库存侧的读法交给 <see cref="InventoryQueryService"/>：四键怎么对齐在那里，
    /// 这里只把明细行的四key 用同一把尺子去查回来。没有库存记录的行单价按 0，与改造前一致。
    /// </summary>
    private static async Task<IReadOnlyList<DifferenceRow>> ReadDifferenceRowsAsync(
        DocumentActionContext context, string masterDepot, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var definition = context.Definition;
        var detailTable = definition.DetailTable!;
        var keyFilter = string.Join(" AND ", definition.MasterPkOrder.Select((column, index) => $"d.{q(column)}=@k{index}"));
        await using var command = new SqlCommand(
            $"""
            SELECT LTRIM(RTRIM(d.{q(ProductField)})),
                   ISNULL(NULLIF(LTRIM(RTRIM(d.{q(DepotField)})), N''), @masterDepot),
                   ISNULL(LTRIM(RTRIM(d.{q(LocationField)})), N''),
                   ISNULL(LTRIM(RTRIM(d.{q(BatchField)})), N''),
                   ISNULL(d.{q(CheckedQtyField)}, 0) - ISNULL(d.{q(AccountQtyField)}, 0),
                   ISNULL(NULLIF(LTRIM(RTRIM(p.UNIT_ID)), N''), N'')
            FROM dbo.{q(detailTable)} d WITH (UPDLOCK, HOLDLOCK)
            LEFT JOIN dbo.{q(ProductTable)} p ON LTRIM(RTRIM(p.{q(ProductField)}))=LTRIM(RTRIM(d.{q(ProductField)}))
            WHERE {keyFilter} AND ISNULL(d.{q(CheckedQtyField)}, 0) - ISNULL(d.{q(AccountQtyField)}, 0) <> 0
            ORDER BY d.{q("SERIAL_NO")};
            """, context.Connection, context.Transaction);
        for (var index = 0; index < definition.MasterPkOrder.Count; index++)
        {
            command.Parameters.AddWithValue($"@k{index}", context.KeyValues[index]);
        }
        command.Parameters.AddWithValue("@masterDepot", masterDepot);

        var currency = await ReadBaseCurrencyAsync(context, token);
        var pending = new List<PendingDifference>();
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                pending.Add(new PendingDifference(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    Convert.ToDouble(reader.GetValue(4)),
                    reader.GetString(5)));
            }
        }
        if (pending.Count == 0) return [];

        var costPrices = await ReadCostPricesAsync(context, masterDepot, pending, token);
        return pending.Select(row => new DifferenceRow(new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [ProductField] = row.Product,
            [DepotField] = row.Depot,
            [LocationField] = row.Location,
            [BatchField] = row.Batch,
            [TargetQtyField] = row.Difference.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            [TargetUnitField] = row.Unit,
            [TargetPriceField] = costPrices.GetValueOrDefault(StockKey(row.Depot, row.Product, row.Location, row.Batch), 0d)
                .ToString("0.######", System.Globalization.CultureInfo.InvariantCulture),
            [TargetCurrencyField] = currency,
            [TargetRateField] = "1",
        })).ToList();
    }

    /// <summary>
    /// 明细四键 → 成本价。成本价是库别级字段（同一 (料号, 库别) 各行同值），这里按行取出即可，
    /// 读取范围限定在本单涉及的料号与库别，避免把整张余额表拉进内存。
    /// 库存行怎么读、位置/批次怎么归一都由 <see cref="InventoryQueryService"/> 决定。
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, double>> ReadCostPricesAsync(
        DocumentActionContext context, string masterDepot, IReadOnlyList<PendingDifference> rows, CancellationToken token)
    {
        var products = rows.Select(row => row.Product).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        var depots = rows.Select(row => row.Depot.Length > 0 ? row.Depot : masterDepot)
            .Distinct(StringComparer.Ordinal).ToList();
        var stock = await InventoryQueryService.GetRowsAsync(
            context.Connection, context.Transaction,
            new InventoryQueryService.RowScope { ProductNos = products, DepotIds = depots },
            InventoryQueryService.ReadLock.None, token);
        var prices = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var row in stock)
        {
            prices[StockKey(row.DepotId, row.ProductNo, row.LocationNo, row.BatchNo)] = row.CostPrice ?? 0d;
        }
        return prices;
    }

    private sealed record PendingDifference(
        string Product, string Depot, string Location, string Batch, double Difference, string Unit);

    /// <summary>四键的对齐键：两侧都按同一套归一化（空值/哨兵不产生差异）。</summary>
    private static string StockKey(string depot, string product, string location, string batch) =>
        string.Join('|',
            depot.Trim(),
            product.Trim(),
            location.Trim().Length == 0 ? InventoryQueryService.LocationSentinel : location.Trim(),
            batch.Trim());

    /// <summary>本位币（CURR.IS_BASE=1）：既有实现取 Application["BASE_CURR"]，本系统按同一事实读库。</summary>
    private static async Task<string> ReadBaseCurrencyAsync(DocumentActionContext context, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        await using var command = new SqlCommand(
            $"SELECT TOP 1 LTRIM(RTRIM({q(TargetCurrencyField)})) FROM dbo.{q(CurrencyTable)} WHERE IS_BASE=1;",
            context.Connection, context.Transaction);
        return (await command.ExecuteScalarAsync(token) as string)?.Trim() ?? string.Empty;
    }

    private static IReadOnlyDictionary<string, string?> BuildTargetMasterValues(
        SourceState state, IReadOnlyList<DifferenceRow> differences)
    {
        var remark = $"由盘点单 {state.Type}/{state.No} 的盈亏生成（差异 {differences.Count} 行）";
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [TargetRemarkField] = remark.Length > 200 ? remark[..200] : remark,
        };
        if (state.CountDate is { } date)
        {
            values[TargetDateField] = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        return values;
    }

    /// <summary>来源回写 + 结案：只动这几个业务列（与 P0 同一口径，不整单重写）。</summary>
    private static async Task WriteBackAsync(DocumentActionContext context, string adjustType, string adjustNo, CancellationToken token)
    {
        var q = ServiceEffectSql.Q;
        var definition = context.Definition;
        var where = string.Join(" AND ", definition.MasterPkOrder.Select((column, index) => $"{q(column)}=@k{index}"));
        await using var command = new SqlCommand(
            $"""
            UPDATE dbo.{q(definition.MasterTable)}
               SET {q(AdjustTypeField)}=@adjustType, {q(AdjustNoField)}=@adjustNo,
                   {q(FinishedTagField)}=1,
                   {q("FINISHED_PERSON")}=@finishedPerson, {q("FINISHED_DATE")}=SYSDATETIME(),
                   {q("LAST_UPDATE_BY")}=@finishedPerson, {q("LAST_UPDATE_DATE")}=SYSDATETIME()
             WHERE {where} AND ISNULL({q(FinishedTagField)}, 0)=0 AND ISNULL({q(AdjustNoField)}, N'')=N'';
            """, context.Connection, context.Transaction);
        command.Parameters.Add("@adjustType", SqlDbType.NVarChar, 20).Value = adjustType;
        command.Parameters.Add("@adjustNo", SqlDbType.NVarChar, 40).Value = adjustNo;
        command.Parameters.Add("@finishedPerson", SqlDbType.NVarChar, 50).Value = context.Executor;
        for (var index = 0; index < definition.MasterPkOrder.Count; index++)
        {
            command.Parameters.AddWithValue($"@k{index}", context.KeyValues[index]);
        }
        if (await command.ExecuteNonQueryAsync(token) == 0)
        {
            throw new InvalidOperationException($"调整单 {adjustNo} 已生成，但盘点单的来源回写/结案未生效（可能已被并发修改）；请核对该盘点单后处理。");
        }
    }

}
