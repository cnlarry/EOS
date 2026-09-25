using System.Data;
using System.Globalization;
using EOS.API.Data.Inventory;
using EOS.API.Data.Effects;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.DocumentActions.Handlers;

/// <summary>
/// 库存冻结与解冻（ADR-020 §9.7 D7-⑦ / §10 WS-21）。
/// </summary>
/// <remarks>
/// **宿主与身份**：动作挂在模块 `1303 料件库存资料` 上，其主表 `INV_PRO_DEPOT` 的物理主键
/// 正好是 `(PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO)` —— **库存格子就是这一行**，
/// 所以"对哪一格冻结"由框架给的主键值决定，不需要另造一套格子身份。
///
/// **授权**：走 ADR-018 的**按钮级授权**（fail-closed 名单），**不挂配置权**（D7-⑦：
/// 冻结是品检/客服发起的业务动作，不该要求 `CanSetup`）。未授权者连按钮都拿不到，
/// 直调也会在框架层被 403（<c>DocumentActionExecutor</c> 的鉴权排在读单据之前）。
///
/// **同步 `USEABLE_QTY`**（本段的硬要求）：写完成占用后，**按可用量服务的口径**重算这一格的
/// 可用量并落列——口径只有一处（<see cref="InventoryAvailabilityService"/>），本处理器不自己减。
/// 顺带治愈历史：WS-19 只做过一次性初始化，此后新产生的余额行可用量仍是 0；
/// 每次冻结/解冻都按口径重算，这一格就回到正确值。
///
/// **不设"冻结量不得超过可用量"的上限**：质量扣货必须永远表达得出来（货就在那儿），
/// 占用超过在库时可用量为负——那是一个**该被看见的信号**（报表与出库校验都会按它拦），
/// 不是一条该被拒绝的输入。数字写错由探路文案（`CONFIRM_TAG=1`）与可逆性兜。
/// </remarks>
internal sealed class InventoryFreezeHandler : IDocumentUserAction
{
    public const string ActionKey = "inventory-freeze";

    internal const string QuantityParameter = "quantity";
    internal const string ReasonParameter = "reason";

    private readonly WorkbenchAuditWriter _audit;

    public InventoryFreezeHandler(WorkbenchAuditWriter audit) => _audit = audit;

    public string Key => ActionKey;

    public string Label => "库存冻结";

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var quantity = InventoryFreezeAction.ReadQuantity(context, "冻结");
        var reason = InventoryFreezeAction.ReadReason(context);
        var slot = InventoryFreezeAction.ReadSlot(context);

        var before = await InventoryFreezeAction.ReadUseableAsync(context, slot, token);
        if (context.Preview)
        {
            var available = await InventoryFreezeAction.ReadAvailableAsync(context, slot, token);
            return new DocumentActionResult(DocumentActionOutcome.Message,
                $"将对 {slot.ProductNo}/{slot.DepotId}/{slot.LocationNo}/{slot.BatchNo} 冻结 {quantity}："
                + $"可用量 {InventoryFreezeAction.Show(available)} → {InventoryFreezeAction.Show(available - quantity)}。"
                + "本次未改动任何数据。");
        }

        // 一笔冻结 = 一行占用：同一来源的重复冻结不互相覆盖（来源为空串即"手工冻结"）。
        await using (var command = new SqlCommand("""
            INSERT INTO dbo.INV_FREEZE
                (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, SOURCE_TYPE, SOURCE_NO, FREEZE_QTY, REASON, STATUS,
                 CREATE_PERSON, CREATE_DATE)
                VALUES (@pro, @depot, @location, @batch, N'', N'', @qty, @reason, N'A', @actor, GETDATE());
            """, context.Connection, context.Transaction))
        {
            command.Parameters.AddWithValue("@pro", slot.ProductNo);
            command.Parameters.AddWithValue("@depot", slot.DepotId);
            command.Parameters.AddWithValue("@location", slot.LocationNo);
            command.Parameters.AddWithValue("@batch", slot.BatchNo);
            command.Parameters.AddWithValue("@qty", quantity);
            command.Parameters.AddWithValue("@reason", (object?)reason ?? DBNull.Value);
            command.Parameters.AddWithValue("@actor", context.Executor);
            await command.ExecuteNonQueryAsync(token);
        }

        var after = await InventoryFreezeAction.SyncUseableAsync(context, slot, token);
        await InventoryFreezeAction.WriteAuditAsync(context, _audit, slot, "FREEZE",
            $"冻结 {quantity}（{reason ?? "未填原因"}）", before, after, token);

        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已冻结 {quantity}：可用量 {InventoryFreezeAction.Show(before)} → {InventoryFreezeAction.Show(after)}"
            + $"（在库数量不变，占用增加 {quantity}）。");
    }
}

/// <summary>
/// 库存解冻（ADR-020 §9.7 D7-⑥ 的"手工释放兜底"，WS-21）。
/// </summary>
/// <remarks>
/// 按**先建先解**（`CREATE_DATE, SOURCE_NO`）把这一格的占用逐笔释放：够一笔就整笔置 `STATUS='C'`，
/// 不够就减该笔的 `FREEZE_QTY`（部分释放）。释放完照样**按可用量服务的口径重算** `USEABLE_QTY`——
/// 与冻结同一个收口点，这样"解冻后可用量回升"不是另写的一段算术。
/// </remarks>
internal sealed class InventoryUnfreezeHandler : IDocumentUserAction
{
    public const string ActionKey = "inventory-unfreeze";

    private readonly WorkbenchAuditWriter _audit;

    public InventoryUnfreezeHandler(WorkbenchAuditWriter audit) => _audit = audit;

    public string Key => ActionKey;

    public string Label => "库存解冻";

    public async Task<DocumentActionResult> ExecuteAsync(DocumentActionContext context, CancellationToken token)
    {
        var quantity = InventoryFreezeAction.ReadQuantity(context, "解冻");
        var slot = InventoryFreezeAction.ReadSlot(context);
        var before = await InventoryFreezeAction.ReadUseableAsync(context, slot, token);

        // 可解的 = 这一格当前有效的冻结合计（已取消的不算）
        var active = await InventoryFreezeAction.ReadActiveRowsAsync(
            context, "INV_FREEZE", "FREEZE_QTY", slot, token);
        var activeTotal = active.Sum(row => row.Quantity);
        if (context.Preview)
        {
            return new DocumentActionResult(DocumentActionOutcome.Message,
                $"将对 {slot.ProductNo}/{slot.DepotId}/{slot.LocationNo}/{slot.BatchNo} 解冻 {quantity}"
                + $"（当前冻结合计 {InventoryFreezeAction.Show(activeTotal)}）。本次未改动任何数据。");
        }
        if (quantity > activeTotal)
        {
            throw new EffectValidationException(
                $"解冻 {quantity} 超过该格当前冻结合计 {InventoryFreezeAction.Show(activeTotal)}："
                + "没有那么多占用来释放。");
        }

        await InventoryFreezeAction.ReleaseSlotAsync(
            context, "INV_FREEZE", "FREEZE_QTY", slot, quantity, active, token);

        var after = await InventoryFreezeAction.SyncUseableAsync(context, slot, token);
        await InventoryFreezeAction.WriteAuditAsync(context, _audit, slot, "UNFREEZE",
            $"解冻 {quantity}", before, after, token);

        return new DocumentActionResult(DocumentActionOutcome.Refreshed,
            $"已解冻 {quantity}：可用量 {InventoryFreezeAction.Show(before)} → {InventoryFreezeAction.Show(after)}"
            + $"（在库数量不变，占用减少 {quantity}）。");
    }
}

/// <summary>两个冻结动作共用的取参、取格、同步与审计。</summary>
internal static class InventoryFreezeAction
{
    internal const double Epsilon = 1e-9;

    /// <summary>读数量参数：必填、正数（0 或负数的"冻结"没有意义，拒掉）。</summary>
    internal static double ReadQuantity(DocumentActionContext context, string what)
    {
        var raw = context.Parameters.GetValueOrDefault(InventoryFreezeHandler.QuantityParameter);
        if (string.IsNullOrWhiteSpace(raw)
            || !double.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var quantity))
        {
            throw new EffectValidationException($"{what}数量必填且必须是数字。");
        }
        if (quantity <= Epsilon)
        {
            throw new EffectValidationException($"{what}数量必须大于 0（收到 {raw.Trim()}）。");
        }
        return quantity;
    }

    /// <summary>读必填的文本参数（来源单别/单号这类"没有它这笔占用就没有归属"的输入）。</summary>
    internal static string ReadRequired(DocumentActionContext context, string parameter, string label) =>
        context.Parameters.GetValueOrDefault(parameter)?.Trim() is { Length: > 0 } value
            ? value
            : throw new EffectValidationException($"{label}必填。");

    internal static string? ReadReason(DocumentActionContext context) =>
        context.Parameters.GetValueOrDefault(InventoryFreezeHandler.ReasonParameter)?.Trim() is { Length: > 0 } reason
            ? reason
            : null;

    /// <summary>
    /// 从框架给的主键值取库存格子。**列名与顺序都来自模块定义**（`INV_PRO_DEPOT` 的物理主键就是
    /// `(PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO)`），本处理器不猜列名。
    /// </summary>
    internal static InventoryAvailabilityService.SlotKey ReadSlot(DocumentActionContext context)
    {
        var columns = context.MasterPkOrder;
        if (columns.Count != 4 || context.KeyValues.Count != 4)
        {
            throw new EffectConfigException(
                $"库存冻结要求模块主键是四键（料号/库别/库位/批次），当前是 {columns.Count} 键。");
        }
        string Value(string name)
        {
            for (var index = 0; index < columns.Count; index++)
            {
                if (columns[index].Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    return context.KeyValues[index]?.Trim() ?? string.Empty;
                }
            }
            throw new EffectConfigException($"库存主键里没有 {name} 列，无法定位库存格子。");
        }

        return new InventoryAvailabilityService.SlotKey(
            Value(InventoryQueryService.ProductColumn),
            Value(InventoryQueryService.DepotColumn),
            Value(InventoryQueryService.LocationColumn),
            Value(InventoryQueryService.BatchColumn)).Trimmed();
    }

    /// <summary>读该格余额行上的可用量列（改前值——审计与前后的对账都靠它）。</summary>
    internal static async Task<double> ReadUseableAsync(
        DocumentActionContext context, InventoryAvailabilityService.SlotKey slot, CancellationToken token)
    {
        await using var command = new SqlCommand(
            $"SELECT ISNULL(USEABLE_QTY,0) FROM dbo.{InventoryQueryService.BalanceTable} WHERE {WhereClause};",
            context.Connection, context.Transaction);
        AddSlotParameters(command, slot);
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? 0 : Convert.ToDouble(value);
    }

    /// <summary>按可用量服务的口径读这一格当前可用量（探路文案用）。</summary>
    internal static async Task<double> ReadAvailableAsync(
        DocumentActionContext context, InventoryAvailabilityService.SlotKey slot, CancellationToken token)
    {
        var result = await InventoryAvailabilityService.ForSlotsAsync(
            context.Connection, context.Transaction, [slot], token);
        return result[slot].Available;
    }

    /// <summary>
    /// **同步 `USEABLE_QTY`**：占用写完之后，按可用量服务的口径重算这一格并落列。
    /// 口径只有一处（服务），这里只负责把它写回余额行——所以"冻结 ⇒ 可用量下降"与
    /// "服务算出来的可用量"永远相等（WS-25 的门禁就断言这条等式）。
    /// </summary>
    internal static async Task<double> SyncUseableAsync(
        DocumentActionContext context, InventoryAvailabilityService.SlotKey slot, CancellationToken token)
    {
        var available = await ReadAvailableAsync(context, slot, token);
        // 落列走服务的唯一维护出口（口径与写入都在那里，这里不自己拼减法）。
        var written = await InventoryAvailabilityService.SyncSlotsAsync(
            context.Connection, context.Transaction, [slot], token);
        if (written == 0)
        {
            throw new EffectValidationException(
                $"库存格子 {slot.ProductNo}/{slot.DepotId}/{slot.LocationNo}/{slot.BatchNo} 不存在，无法冻结。");
        }
        return available;
    }

    /// <summary>读这一格当前**有效**的占用行（`STATUS='A'`），按"先建先解"的顺序给释放用。</summary>
    internal static async Task<IReadOnlyList<(string SourceType, string SourceNo, double Quantity)>> ReadActiveRowsAsync(
        DocumentActionContext context, string table, string quantityColumn,
        InventoryAvailabilityService.SlotKey slot, CancellationToken token)
    {
        await using var command = new SqlCommand(
            $"SELECT LTRIM(RTRIM(SOURCE_TYPE)), LTRIM(RTRIM(SOURCE_NO)), ISNULL({quantityColumn},0) FROM dbo.{table} "
            + $"WHERE {WhereClause} AND RTRIM(STATUS) = N'A' ORDER BY CREATE_DATE, SOURCE_NO;",
            context.Connection, context.Transaction);
        AddSlotParameters(command, slot);
        var rows = new List<(string, string, double)>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add((reader.GetString(0), reader.GetString(1),
                reader.IsDBNull(2) ? 0 : Convert.ToDouble(reader.GetValue(2))));
        }
        return rows;
    }

    /// <summary>
    /// 从一张占用表里按**先建先解**释放指定数量：够一笔就整笔置 `STATUS='C'`，不够就减该笔数量（部分释放）。
    /// 冻结的解冻与预留的释放共用这一处实现——"释放"只有一种语义，不该有两份算术。
    /// </summary>
    internal static async Task ReleaseSlotAsync(
        DocumentActionContext context, string table, string quantityColumn,
        InventoryAvailabilityService.SlotKey slot, double quantity,
        IReadOnlyList<(string SourceType, string SourceNo, double Quantity)> active, CancellationToken token)
    {
        var remaining = quantity;
        foreach (var row in active)
        {
            if (remaining <= Epsilon)
            {
                break;
            }
            var release = Math.Min(row.Quantity, remaining);
            remaining -= release;
            var whole = row.Quantity - release <= Epsilon;
            await using var command = new SqlCommand(
                whole
                    ? $"UPDATE dbo.{table} SET STATUS = N'C', LAST_UPDATE_BY = @actor, LAST_UPDATE_DATE = GETDATE() "
                      + $"WHERE {WhereClause} AND SOURCE_TYPE = @sourceType AND SOURCE_NO = @sourceNo;"
                    : $"UPDATE dbo.{table} SET {quantityColumn} = {quantityColumn} - @release, "
                      + "LAST_UPDATE_BY = @actor, LAST_UPDATE_DATE = GETDATE() "
                      + $"WHERE {WhereClause} AND SOURCE_TYPE = @sourceType AND SOURCE_NO = @sourceNo;",
                context.Connection, context.Transaction);
            AddSlotParameters(command, slot);
            command.Parameters.AddWithValue("@sourceType", row.SourceType);
            command.Parameters.AddWithValue("@sourceNo", row.SourceNo);
            command.Parameters.AddWithValue("@actor", context.Executor);
            if (!whole)
            {
                command.Parameters.AddWithValue("@release", release);
            }
            await command.ExecuteNonQueryAsync(token);
        }
    }

    /// <summary>
    /// 按**来源单据**整笔释放（来源结案 / 取消时的钩子用它）：把该单据名下的有效占用全部置 `STATUS='C'`，
    /// 返回被释放的格子清单供调用方逐个同步 `USEABLE_QTY`。
    /// </summary>
    internal static async Task<IReadOnlyList<(InventoryAvailabilityService.SlotKey Slot, double Quantity)>> ReleaseBySourceAsync(
        SqlConnection connection, SqlTransaction? transaction, string table, string quantityColumn,
        string sourceType, string sourceNo, string actor, CancellationToken token)
    {
        var released = new List<(InventoryAvailabilityService.SlotKey, double)>();
        await using (var read = new SqlCommand(
            $"SELECT LTRIM(RTRIM(PRO_NO)), LTRIM(RTRIM(DEPOT_ID)), ISNULL(LOCATION_NO, N'-'), ISNULL(BATCH_NO, N''), "
            + $"ISNULL({quantityColumn},0) FROM dbo.{table} "
            + "WHERE RTRIM(STATUS) = N'A' AND LTRIM(RTRIM(SOURCE_TYPE)) = @sourceType "
            + "AND LTRIM(RTRIM(SOURCE_NO)) = @sourceNo;", connection, transaction))
        {
            read.Parameters.AddWithValue("@sourceType", sourceType);
            read.Parameters.AddWithValue("@sourceNo", sourceNo);
            await using var reader = await read.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token))
            {
                released.Add((new InventoryAvailabilityService.SlotKey(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)).Trimmed(),
                    reader.IsDBNull(4) ? 0 : Convert.ToDouble(reader.GetValue(4))));
            }
        }
        if (released.Count == 0)
        {
            return released;
        }

        await using (var update = new SqlCommand(
            $"UPDATE dbo.{table} SET STATUS = N'C', LAST_UPDATE_BY = @actor, LAST_UPDATE_DATE = GETDATE() "
            + "WHERE RTRIM(STATUS) = N'A' AND LTRIM(RTRIM(SOURCE_TYPE)) = @sourceType "
            + "AND LTRIM(RTRIM(SOURCE_NO)) = @sourceNo;", connection, transaction))
        {
            update.Parameters.AddWithValue("@sourceType", sourceType);
            update.Parameters.AddWithValue("@sourceNo", sourceNo);
            update.Parameters.AddWithValue("@actor", actor);
            await update.ExecuteNonQueryAsync(token);
        }
        return released;
    }

    /// <summary>把若干格子的 `USEABLE_QTY` 逐个按口径重算并落列（钩子释放多个格子时用）。</summary>
    internal static async Task SyncManyAsync(
        SqlConnection connection, SqlTransaction? transaction, string actor, string reason,
        IReadOnlyList<(InventoryAvailabilityService.SlotKey Slot, double Quantity)> slots,
        WorkbenchAuditWriter audit, int moduleId, string recordKey, CancellationToken token)
    {
        // 落列走服务的唯一维护出口（多格一并重算）
        await InventoryAvailabilityService.SyncSlotsAsync(
            connection, transaction, slots.Select(item => item.Slot).ToList(), token);

        await audit.WriteEventAsync(connection, transaction, moduleId, recordKey, "UNFREEZE", reason, actor,
            "INV_RESERVE", result: 1, fieldChanges: null, token,
            detailJson: System.Text.Json.JsonSerializer.Serialize(slots.Select(item => new
            {
                product = item.Slot.ProductNo,
                depot = item.Slot.DepotId,
                location = item.Slot.LocationNo,
                batch = item.Slot.BatchNo,
                quantity = item.Quantity,
            })));
    }

    /// <summary>审计：动作事件 + `USEABLE_QTY` 的前后值（本段的验收项之一）。</summary>
    internal static async Task WriteAuditAsync(
        DocumentActionContext context, WorkbenchAuditWriter audit,
        InventoryAvailabilityService.SlotKey slot, string action, string message,
        double before, double after, CancellationToken token) =>
        await audit.WriteEventAsync(
            context.Connection, context.Transaction, context.Definition.ModuleId, context.RecordKey,
            action, $"{message}（{slot.ProductNo}/{slot.DepotId}/{slot.LocationNo}/{slot.BatchNo}）",
            context.Executor, "INV_FREEZE", result: 1,
            fieldChanges: [new AuditFieldChange("USEABLE_QTY", Show(before), Show(after), null)],
            token);

    internal static string Show(double value) =>
        value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string WhereClause =>
        "PRO_NO = @pro AND DEPOT_ID = @depot AND LOCATION_NO = @location AND BATCH_NO = @batch";

    private static void AddSlotParameters(SqlCommand command, InventoryAvailabilityService.SlotKey slot)
    {
        command.Parameters.AddWithValue("@pro", slot.ProductNo);
        command.Parameters.AddWithValue("@depot", slot.DepotId);
        command.Parameters.AddWithValue("@location", slot.LocationNo);
        command.Parameters.AddWithValue("@batch", slot.BatchNo);
    }
}
