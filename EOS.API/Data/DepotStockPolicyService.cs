using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data;

/// <summary>
/// 库存策略参数组的一条记录。作用域是库别，或用 <see cref="DepotStockPolicyService.DeploymentScope"/>
/// 表示部署级默认。六个维度彼此独立，不打包成档位。
/// </summary>
public sealed record DepotStockPolicy(
    string DepotId,
    int LocationMode,
    string StorageMode,
    int BatchMode,
    int CapacityMode,
    bool MixProduct,
    bool MixBatch,
    bool MonthCloseByBatch,
    bool MonthCloseByLocation);

/// <summary>
/// 库存策略的一个可配置维度：键、显示名、说明与候选档位（含"本版未实现"标记，界面据此灰显）。
/// </summary>
public sealed record DepotStockPolicyTier(
    string Key,
    string Label,
    string Description,
    IReadOnlyList<DepotStockPolicyTierOption> Options);

/// <summary>档位候选值。<paramref name="Implemented"/> 为假表示本版未实现：可见但不可选。</summary>
public sealed record DepotStockPolicyTierOption(string Value, string Label, bool Implemented);

/// <summary>
/// 库存策略的唯一求值入口。策略决定"管到多细"（位置 / 存放 / 批次 / 容量 / 混品号 / 混批次），
/// 求值口径是**两跳且整行覆盖**：库别行存在则整行采用，否则回落到部署级默认行。
///
/// 求值必须收口在这里 —— 各处自行拼装默认值会让"库别无行时取什么"出现多个口径，而这类
/// 分歧只在某个库别真的没有配置行时才显形。
/// </summary>
public sealed class DepotStockPolicyService(DbConnectionFactory connections, WorkbenchAuditWriter auditWriter)
{
    /// <summary>部署级默认行的作用域键。</summary>
    public const string DeploymentScope = "*";

    /// <summary>余额表里"未指定位置"的哨兵值。</summary>
    public const string SentinelLocationNo = "-";

    /// <summary>本版未实现的批次档位（必填 + 效期）：保存期一律拒绝。</summary>
    public const int UnimplementedBatchMode = 3;

    /// <summary>本版支持的容量档位上限（恒为 0，即不校验）。</summary>
    public const int MaxSupportedCapacityMode = 0;

    /// <summary>
    /// 档位目录（界面渲染用）。与服务端判定**共用上面的常量**：界面能选什么、服务端接受什么
    /// 必须是同一份事实，否则又会出现"看得见却存不进去"或"存得进去却不生效"。
    /// 未实现的档位仍然出现在目录里（<see cref="DepotStockPolicyTierOption.Implemented"/> 为假），
    /// 界面按"可见但不可选"渲染——隐藏会让人以为这个能力不存在。
    /// </summary>
    public static IReadOnlyList<DepotStockPolicyTier> Tiers { get; } =
    [
        new("locationMode", "位置档位", "货在哪里记到多细",
        [
            new("0", "不管", true),
            new("1", "可填", true),
            new("2", "建议", true),
            new("3", "强制", true),
        ]),
        new("storageMode", "存放方式", "固定储位还是随机存放",
        [
            new("FIXED", "FIXED 固定", true),
            new("RANDOM", "RANDOM 随机", true),
            new("MIXED", "MIXED 混合", true),
        ]),
        new("batchMode", "批次档位", "批号记不记、要不要必填",
        [
            new("0", "归零", true),
            new("1", "保留", true),
            new("2", "必填", true),
            new("3", "必填 + 效期", false),
        ]),
        new("capacityMode", "容量档位", "要不要校验库位容量",
        [
            new("0", "不校验", true),
            new("1", "告警", false),
            new("2", "强制", false),
        ]),
        new("mixProduct", "混品号", "同一库位能否放不同料号",
        [
            new("1", "允许", true),
            new("0", "禁止", true),
        ]),
        new("mixBatch", "混批次", "同一库位能否放不同批次",
        [
            new("1", "允许", true),
            new("0", "禁止", true),
        ]),
        new("monthCloseByBatch", "月结按批次", "月结快照是否按批次细分",
        [
            new("1", "是", true),
            new("0", "否", true),
        ]),
        new("monthCloseByLocation", "月结按库位", "月结快照是否按库位细分",
        [
            new("1", "是", true),
            new("0", "否", true),
        ]),
    ];

    /// <summary>
    /// 作用域键的最大长度，与 <c>DEPOT_STOCK_POLICY.DEPOT_ID</c> 的列宽一致。
    /// 列宽与 CHECK 约束是最后一道防线：拿它当校验，越界值会以"字符串将被截断"/"违反 CHECK"
    /// 的 SqlException 冒成 **500**，而调用方拿到的是"服务器内部错误"而不是"你传的参数不对"。
    /// </summary>
    public const int ScopeMaxLength = 10;

    private static readonly string[] StorageModes = ["FIXED", "RANDOM", "MIXED"];

    /// <summary>
    /// 两跳求值：库别行整行覆盖 → 部署级默认行。不做列级逐字段继承，避免"改了 A 仓的批号
    /// 策略却意外继承了 B 仓的位置策略"这类难查的问题。
    /// </summary>
    public async Task<DepotStockPolicy> ResolveAsync(string? depotId, CancellationToken token = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        return await ResolveAsync(depotId, connection, null, token);
    }

    /// <summary>
    /// 在调用方已开启的连接 / 事务内求值。保存期校验必须走这一条：另开连接读同一张表
    /// 会被调用方尚未提交的写事务挡住。
    /// </summary>
    public async Task<DepotStockPolicy> ResolveAsync(
        string? depotId, SqlConnection connection, SqlTransaction? transaction, CancellationToken token = default)
    {
        var scope = string.IsNullOrWhiteSpace(depotId) ? DeploymentScope : depotId.Trim();

        await using var command = new SqlCommand(
            // 月结维度一律取部署级行的值：库别行即使被直连改库写入，运行时口径也不会分裂。
            "SELECT r.DEPOT_ID, r.LOCATION_MODE, r.STORAGE_MODE, r.BATCH_MODE, r.CAPACITY_MODE, r.MIX_PRODUCT, r.MIX_BATCH, "
            + "d.MONTH_CLOSE_BY_BATCH, d.MONTH_CLOSE_BY_LOCATION "
            + "FROM (SELECT TOP 1 DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH "
            + "        FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID IN (@scope, @default) "
            + "       ORDER BY CASE WHEN DEPOT_ID = @scope THEN 0 ELSE 1 END) r "
            + "CROSS JOIN (SELECT MONTH_CLOSE_BY_BATCH, MONTH_CLOSE_BY_LOCATION "
            + "              FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = @default) d",
            connection, transaction);
        command.Parameters.AddWithValue("@scope", scope);
        command.Parameters.AddWithValue("@default", DeploymentScope);

        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token))
            throw new InvalidOperationException("库存策略缺少部署级默认行（DEPOT_ID = '*'）。");

        return Read(reader);
    }

    /// <summary>
    /// 组合规则校验。硬性规则返回错误（保存期拒绝），软性规则返回告警（可保存但需提示）。
    /// 未实现档位的拒绝放在**服务端**：界面灰显只是体验，直连 API 仍可复现"保存成功却不生效"。
    /// </summary>
    public static (IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings) Validate(DepotStockPolicy policy)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        // 取值域：与列宽 / CHECK 约束逐条对齐，越界在此拦下（400），不留给数据库抛 500。
        if (policy.DepotId.Length > ScopeMaxLength)
            errors.Add($"库别代号最长 {ScopeMaxLength} 个字符（当前 {policy.DepotId.Length} 个）。");
        if (!StorageModes.Contains(policy.StorageMode, StringComparer.OrdinalIgnoreCase))
            errors.Add("存放方式只能取 FIXED / RANDOM / MIXED。");

        // R-C4：未实现档位不得被保存为生效配置
        if (policy.BatchMode == UnimplementedBatchMode)
            errors.Add("批次档位 3（必填 + 效期）本版未实现，不能保存为生效配置。");
        if (policy.CapacityMode > MaxSupportedCapacityMode)
            errors.Add("容量档位本版未实现，不能保存为生效配置。");

        // R-C1：不强制记位置却随机存放，货必然丢失
        if (policy.LocationMode <= 1 && policy.StorageMode is "RANDOM" or "MIXED")
            errors.Add("位置档位为 0/1 时不能使用随机存放或混合存放。");

        // R-C2：没有位置，无从校验容量
        if (policy.LocationMode == 0 && policy.CapacityMode > 0)
            errors.Add("位置档位为 0 时不能启用容量校验。");

        // R-C3：效期追溯的前提是先能定位
        if (policy.LocationMode < 3 && policy.BatchMode == UnimplementedBatchMode)
            errors.Add("效期追溯要求位置档位为 3（强制）。");

        // R-S1：随机存放缺少扫码写入手段时，位置数据当天即失真
        if (policy.StorageMode is "RANDOM" or "MIXED" && policy.LocationMode == 2)
            warnings.Add("随机/混合存放建议配合扫码写入；位置档位仅为 2（建议）时位置数据可能很快失真。");

        return (errors, warnings);
    }

    /// <summary>
    /// 月结维度只在部署级生效：库别行提交与部署级不同的取值一律拒绝。若允许按库别配不同
    /// 粒度，同一个月的月结数据粒度就不一致，跨仓汇总会重复计数或漏计，且事后无法补救
    /// （月结是历史快照）。运行时求值也只取部署级值，所以这里拦的是"配上了却不生效"的配置。
    /// </summary>
    public static string? ValidateMonthCloseScope(DepotStockPolicy candidate, DepotStockPolicy deployment) =>
        candidate.DepotId != DeploymentScope
        && (candidate.MonthCloseByBatch != deployment.MonthCloseByBatch
            || candidate.MonthCloseByLocation != deployment.MonthCloseByLocation)
            ? "月结维度只在部署级生效，库别行不能覆盖（跨仓粒度不一致会让汇总重复计数或漏计）。"
            : null;

    /// <summary>
    /// 保存一条策略行的结果：硬性规则违例即拒存，软性规则作为告警返回。
    /// <paramref name="RequiresConfirmation"/> 为真时表示**这是一次破坏性档位下调且尚未确认**，
    /// 调用方确认后应原样重发并带上确认标志（区别于"参数非法"）。
    /// </summary>
    public sealed record SavePolicyResult(
        bool Saved,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings,
        bool RequiresConfirmation = false);

    /// <summary>
    /// 一次哨兵行归位的结果：<paramref name="Relocated"/> 为真表示这一笔已经改记到目标库位，
    /// <paramref name="Relocated"/> 为假且 <paramref name="Errors"/> 为空，表示"没有存量需要归位"（无事发生，不是失败）。
    /// </summary>
    public sealed record RelocateSentinelResult(
        bool Relocated,
        IReadOnlyList<string> Errors,
        string Message);

    /// <summary>
    /// 把某库别记在『未指定位置』上的存量整批改记到目标库位——与升档时的代搬是同一段实现，
    /// 这里把它独立成入口：归位是一次**账面认定**（视为这批货就在目标库位），
    /// 不必为了再归一次就去改策略配置。
    ///
    /// 调用方必须提供连接与事务：归位与它的审计记录必须在同一个事务里，不能各写一半。
    /// 目标库位的合法性（存在、启用、不是哨兵本身）在这里复核，不依赖界面。
    /// </summary>
    public async Task<RelocateSentinelResult> RelocateSentinelStockAsync(
        string depotId,
        string relocateTo,
        string actor,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken token = default)
    {
        var depot = (depotId ?? string.Empty).Trim();
        var target = (relocateTo ?? string.Empty).Trim();
        if (depot.Length == 0)
        {
            return new RelocateSentinelResult(false, ["缺少库别，无法归位。"], string.Empty);
        }
        if (target.Length == 0)
        {
            return new RelocateSentinelResult(false, ["未指定目标库位。"], string.Empty);
        }
        if (string.Equals(target, SentinelLocationNo, StringComparison.Ordinal))
        {
            return new RelocateSentinelResult(false, ["目标库位不能是『未指定位置』本身。"], string.Empty);
        }
        if (!await LocationExistsAsync(depot, target, connection, transaction, token))
        {
            return new RelocateSentinelResult(false, [$"目标库位 {depot}/{target} 不存在或已停用，无法归位。"], string.Empty);
        }

        var pending = await CountSentinelRowsToRelocateAsync(depot, connection, transaction, token);
        if (pending == 0)
        {
            return new RelocateSentinelResult(false, [],
                "该库别没有记在『未指定位置』上的存量，无需归位。");
        }

        await RelocateSentinelAsync(depot, target, connection, transaction, token);
        var message = $"已完成归位：{pending} 组（料号/批次）记在『未指定位置』上的存量已改记到 {target}，库别总量不变。";
        await auditWriter.WriteEventAsync(
            connection, transaction, StockPolicyModuleId, depot, "RELOCATE", message, actor,
            "DEPOT_STOCK_POLICY", result: 1, fieldChanges: null, token);
        return new RelocateSentinelResult(true, [], message);
    }

    /// <summary>一个仓库的代号与名称（新增策略行时选库别用）。</summary>
    public sealed record DepotRef(string DepotId, string DepotName);
    /// <summary>列出全部仓库（代号 + 名称，按代号排序）。</summary>
    public async Task<IReadOnlyList<DepotRef>> ListDepotsAsync(CancellationToken token = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT DEPOT_ID, DEPOT_NAME FROM dbo.DEPOT ORDER BY DEPOT_ID", connection);
        var rows = new List<DepotRef>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(new DepotRef(reader.GetString(0).Trim(), reader.GetString(1).Trim()));
        return rows;
    }

    /// <summary>一个库位的位置编号与名称（归位选目标库位用，不含『未指定位置』本身）。</summary>
    public sealed record DepotLocationRef(string LocationNo, string LocationName);

    /// <summary>列出某库别下可用的目标库位（启用中，不含哨兵行，按排序号）。</summary>
    public async Task<IReadOnlyList<DepotLocationRef>> ListLocationsAsync(string depotId, CancellationToken token = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT LOCATION_NO, LOCATION_NAME FROM dbo.DEPOT_LOCATION "
            + "WHERE DEPOT_ID = @depot AND LOCATION_NO <> N'-' AND STATUS = N'A' "
            + "ORDER BY SEQ_NO, LOCATION_NO", connection);
        command.Parameters.AddWithValue("@depot", (depotId ?? string.Empty).Trim());
        var rows = new List<DepotLocationRef>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(new DepotLocationRef(
                reader.GetString(0).Trim(),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim()));
        return rows;
    }

    /// <summary>列出全部策略行（部署级默认 + 各库别覆盖）。</summary>
    public async Task<IReadOnlyList<DepotStockPolicy>> ListAsync(CancellationToken token = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH, "
            + "MONTH_CLOSE_BY_BATCH, MONTH_CLOSE_BY_LOCATION FROM dbo.DEPOT_STOCK_POLICY "
            + "ORDER BY CASE WHEN DEPOT_ID = N'*' THEN 0 ELSE 1 END, DEPOT_ID",
            connection);
        var rows = new List<DepotStockPolicy>();
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
            rows.Add(Read(reader));
        return rows;
    }

    /// <summary>
    /// 保存一条策略行。硬性组合规则（R-C1/C2/C3/C4）与月结作用域违规一律**拒存**
    /// （fail-closed：服务端拒绝，不依赖界面拦截）；软性规则作为告警随结果返回，配置照常保存。
    ///
    /// 两处**服务端**（而非界面）强制的门槛：① 档位下调属破坏性变更，必须显式二次确认；
    /// ② 全部变更写 `AUDIT_EVENT` + `AUDIT_FIELD_CHANGE`（含变更前后值），且**与策略写在同一个事务里**——
    /// §3.10.3 要求的是"必须留痕"，best-effort 写在配置变更失败时不会有人发现。
    /// </summary>
    public async Task<SavePolicyResult> SaveAsync(
        DepotStockPolicy candidate, string actor, bool confirmDestructive = false, string? relocateTo = null,
        CancellationToken token = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        // 月结维度的基准是部署级行，必须在同一事务内读取。
        var deployment = await ResolveAsync(DeploymentScope, connection, transaction, token);

        var validation = Validate(candidate);
        var errors = new List<string>(validation.Errors);
        var warnings = new List<string>(validation.Warnings);

        var scopeError = ValidateMonthCloseScope(candidate, deployment);
        if (scopeError is not null)
            errors.Add(scopeError);

        if (candidate.BatchMode == 0 && candidate.DepotId != DeploymentScope
            && await HasBatchManagedProductAsync(candidate.DepotId, token))
            warnings.Add("该库别有批管料件，但批次档位为 0：产品级「需要批号」仍然生效，仓库侧不额外记录批号。");

        if (errors.Count > 0)
            return new SavePolicyResult(false, errors, warnings);

        // 旧行（可能不存在）与"生效中的旧策略"是两回事：审计要记的是前者的真实前后值，
        // 而档位下调 / 混放收紧的判据要用后者（库别无行时以部署级默认为基准）。
        var existing = await ReadExistingAsync(candidate.DepotId, connection, transaction, token);
        var previous = existing ?? deployment;

        // 位置档位下调时，存量不能只靠"确认"了事：档位一降，系统就只看哨兵行，
        // 还留在位置行上的数量会**看着凭空减少**。§3.10.2 要的是"拒绝直接降档、必须先归并"——
        // 归并与写档位放在同一个事务里，要么都成、要么都不成。
        // 这一条必须排在通用的"档位下调需确认"之前：否则通用那条先返回，用户永远看不到
        // "会归并掉哪些存量"，确认也就成了盲签。
        var pendingMerge = candidate.LocationMode < previous.LocationMode
            ? await CountRowsToMergeAsync(candidate.DepotId, connection, transaction, token)
            : 0;

        if (pendingMerge > 0 && !confirmDestructive)
        {
            return new SavePolicyResult(
                false,
                new[]
                {
                    $"位置档位 {previous.LocationMode}→{candidate.LocationMode} 会让系统只看『未指定位置』行，"
                    + $"而该库别还有 {pendingMerge} 组（料号/批次）的存量分散在具体库位上——直接降档会让这些数量看着凭空减少。"
                    + "请二次确认后重发：确认时会先做归并（把它们并入『未指定位置』行、总量不变），再完成降档。",
                },
                warnings,
                RequiresConfirmation: true);
        }

        // 批次档位下调：行保留不合并，但同属破坏性语义变更，一律要确认。
        // 位置档位下调的确认由上面 pendingMerge 那条决定——该库别没有位置存量时它没有破坏性，不该拦。
        if (candidate.BatchMode < previous.BatchMode && !confirmDestructive)
        {
            var detail = DescribeDowngrade(candidate, previous);
            return new SavePolicyResult(
                false,
                new[] { $"档位下调属于破坏性变更（{detail}），需二次确认后重发并带上确认标志。确认前不写入任何改动。" },
                warnings,
                RequiresConfirmation: true);
        }

        if (pendingMerge > 0)
        {
            await MergeLocationsIntoSentinelAsync(candidate.DepotId, connection, transaction, token);
            warnings.Add($"已完成归并：{pendingMerge} 组（料号/批次）分散在库位上的存量已并入『未指定位置』行，库别总量不变。");
        }

        // 升档归位（§3.10.2 的另一半）：档位一升，系统就按库位出入库，而还记在『未指定位置』上的货
        // **按库位取不出来**（那个库位账上是 0）。调用方给了目标库位就代搬；没给就要求明确表态，
        // 不能静默放过——否则仓库会在"已经开了位置管理"的错觉下卡住出库。
        if (candidate.LocationMode > previous.LocationMode)
        {
            var sentinelPending = await CountSentinelRowsToRelocateAsync(candidate.DepotId, connection, transaction, token);
            if (sentinelPending > 0)
            {
                if (string.IsNullOrWhiteSpace(relocateTo))
                {
                    if (!confirmDestructive)
                    {
                        return new SavePolicyResult(
                            false,
                            new[]
                            {
                                $"位置档位 {previous.LocationMode}→{candidate.LocationMode} 后系统将按库位出入库，"
                                + $"而该库别还有 {sentinelPending} 组（料号/批次）的货记在『未指定位置』上——不先归位，这些货按库位取不出来。"
                                + "两条出路：① 在请求里指定目标库位（`relocateTo`，例如收货暂存区），确认时由系统代搬；"
                                + "② 带确认标志表示暂不归位，稍后用盘点按实际位置逐步归位。",
                            },
                            warnings,
                            RequiresConfirmation: true);
                    }
                    warnings.Add(
                        $"该库别还有 {sentinelPending} 组（料号/批次）的货记在『未指定位置』上：升档后按库位出库取不到它们，"
                        + "请用盘点按实际位置逐步归位。");
                }
                else
                {
                    var target = relocateTo.Trim();
                    if (string.Equals(target, SentinelLocationNo, StringComparison.Ordinal))
                        return new SavePolicyResult(false, new[] { "目标库位不能是『未指定位置』本身。" }, warnings);
                    if (!await LocationExistsAsync(candidate.DepotId, target, connection, transaction, token))
                        return new SavePolicyResult(false,
                            new[] { $"目标库位 {candidate.DepotId}/{target} 不存在或已停用，无法归位。" }, warnings);

                    await RelocateSentinelAsync(candidate.DepotId, target, connection, transaction, token);
                    warnings.Add(
                        $"已完成归位：{sentinelPending} 组（料号/批次）记在『未指定位置』上的存量已改记到 {target}，库别总量不变。");
                }
            }
        }

        // 收紧混放限制时给出存量违规清单：按 §3.10.2 允许保存，但必须让人知道哪些库位要整改。
        warnings.AddRange(await ListMixedLocationWarningsAsync(candidate, previous, connection, transaction, token));

        // 部署级行的月结维度由它自己定义；库别行一律写入部署级取值（求值也只读部署级）。
        var monthBatch = candidate.DepotId == DeploymentScope ? candidate.MonthCloseByBatch : deployment.MonthCloseByBatch;
        var monthLocation = candidate.DepotId == DeploymentScope ? candidate.MonthCloseByLocation : deployment.MonthCloseByLocation;

        await using (var upsert = new SqlCommand(
            "MERGE dbo.DEPOT_STOCK_POLICY AS target "
            + "USING (SELECT @depot AS DEPOT_ID) AS source ON target.DEPOT_ID = source.DEPOT_ID "
            + "WHEN MATCHED THEN UPDATE SET LOCATION_MODE=@locationMode, STORAGE_MODE=@storageMode, "
            + "  BATCH_MODE=@batchMode, CAPACITY_MODE=@capacityMode, MIX_PRODUCT=@mixProduct, MIX_BATCH=@mixBatch, "
            + "  MONTH_CLOSE_BY_BATCH=@monthBatch, MONTH_CLOSE_BY_LOCATION=@monthLocation, "
            + "  LAST_UPDATE_BY=@actor, LAST_UPDATE_DATE=GETDATE() "
            + "WHEN NOT MATCHED THEN INSERT (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, "
            + "  MIX_PRODUCT, MIX_BATCH, MONTH_CLOSE_BY_BATCH, MONTH_CLOSE_BY_LOCATION, LAST_UPDATE_BY, LAST_UPDATE_DATE) "
            + "  VALUES (@depot, @locationMode, @storageMode, @batchMode, @capacityMode, @mixProduct, @mixBatch, "
            + "          @monthBatch, @monthLocation, @actor, GETDATE());",
            connection, transaction))
        {
            upsert.Parameters.AddWithValue("@depot", candidate.DepotId);
            upsert.Parameters.AddWithValue("@locationMode", candidate.LocationMode);
            upsert.Parameters.AddWithValue("@storageMode", candidate.StorageMode);
            upsert.Parameters.AddWithValue("@batchMode", candidate.BatchMode);
            upsert.Parameters.AddWithValue("@capacityMode", candidate.CapacityMode);
            upsert.Parameters.AddWithValue("@mixProduct", candidate.MixProduct);
            upsert.Parameters.AddWithValue("@mixBatch", candidate.MixBatch);
            upsert.Parameters.AddWithValue("@monthBatch", monthBatch);
            upsert.Parameters.AddWithValue("@monthLocation", monthLocation);
            upsert.Parameters.AddWithValue("@actor", actor);
            await upsert.ExecuteNonQueryAsync(token);
        }

        // 落库的月结维度是上面算出来的实际写入值，审计必须记实际值而不是请求里的值。
        var stored = new DepotStockPolicy(
            candidate.DepotId, candidate.LocationMode, candidate.StorageMode, candidate.BatchMode, candidate.CapacityMode,
            candidate.MixProduct, candidate.MixBatch, monthBatch, monthLocation);
        await auditWriter.WriteEventAsync(
            connection, transaction, StockPolicyModuleId, candidate.DepotId,
            existing is null ? "CREATE" : "UPDATE",
            DescribeChange(existing, stored),
            actor, "DEPOT_STOCK_POLICY", result: 1,
            BuildFieldChanges(existing, stored), token);

        await transaction.CommitAsync(token);
        return new SavePolicyResult(true, Array.Empty<string>(), warnings);
    }

    /// <summary>删除一条库别策略行的结果：删除后该库别回落到部署级默认行。</summary>
    public sealed record DeletePolicyResult(bool Deleted, IReadOnlyList<string> Errors, string Message);

    /// <summary>
    /// 删除一条库别策略行（部署级默认行不允许删除）。删除只去掉"覆盖"，
    /// 该库别此后按部署级默认行求值；库存存量本身不动。
    /// 删除与它的审计记在同一个事务里。
    /// </summary>
    public async Task<DeletePolicyResult> DeleteAsync(string depotId, string actor, CancellationToken token = default)
    {
        var depot = (depotId ?? string.Empty).Trim();
        if (depot.Length == 0)
            return new DeletePolicyResult(false, ["缺少库别，无法删除。"], string.Empty);
        if (string.Equals(depot, DeploymentScope, StringComparison.Ordinal))
            return new DeletePolicyResult(false, ["部署级默认行不允许删除（它是所有无覆盖库别的回落值）。"], string.Empty);

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);

        var existing = await ReadExistingAsync(depot, connection, transaction, token);
        if (existing is null)
            return new DeletePolicyResult(false, [$"库别 {depot} 没有独立的策略行，无需删除（当前按部署级默认行使）。"], string.Empty);

        await using (var command = new SqlCommand(
            "DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = @depot", connection, transaction))
        {
            command.Parameters.AddWithValue("@depot", depot);
            await command.ExecuteNonQueryAsync(token);
        }

        var changes = new List<AuditFieldChange>
        {
            new("LOCATION_MODE", Format(existing.LocationMode), null, null),
            new("STORAGE_MODE", Format(existing.StorageMode), null, null),
            new("BATCH_MODE", Format(existing.BatchMode), null, null),
            new("CAPACITY_MODE", Format(existing.CapacityMode), null, null),
            new("MIX_PRODUCT", Format(existing.MixProduct), null, null),
            new("MIX_BATCH", Format(existing.MixBatch), null, null),
            new("MONTH_CLOSE_BY_BATCH", Format(existing.MonthCloseByBatch), null, null),
            new("MONTH_CLOSE_BY_LOCATION", Format(existing.MonthCloseByLocation), null, null),
        };
        var message = $"库存策略删除（{depot}）："
            + string.Join("、", changes.Select(c => $"{c.FieldName} {c.OldValue}→已删除"));
        await auditWriter.WriteEventAsync(
            connection, transaction, StockPolicyModuleId, depot,
            "DELETE", message, actor, "DEPOT_STOCK_POLICY", result: 1, changes, token);

        await transaction.CommitAsync(token);
        return new DeletePolicyResult(true, Array.Empty<string>(), message + "；该库别此后按部署级默认行使。");
    }

    /// <summary>独立归位（不改策略配置）的预览：只读不写，返回将搬几组。</summary>
    public sealed record RelocatePreviewResult(IReadOnlyList<string> Errors, int PendingGroups, string Message);

    /// <summary>
    /// 独立归位预览：校验目标库位并数出压在哨兵行上的组数，一行不写。
    /// 界面先调它拿到"将会发生什么"，用户确认后再调 <see cref="RelocateStandaloneAsync"/>。
    /// </summary>
    public async Task<RelocatePreviewResult> PreviewRelocateAsync(
        string depotId, string relocateTo, CancellationToken token = default)
    {
        var depot = (depotId ?? string.Empty).Trim();
        var target = (relocateTo ?? string.Empty).Trim();
        if (depot.Length == 0 || string.Equals(depot, DeploymentScope, StringComparison.Ordinal))
            return new RelocatePreviewResult(["归位按库别执行：请选中具体库别的策略行（部署级默认不是库别）。"], 0, string.Empty);
        if (target.Length == 0)
            return new RelocatePreviewResult(["未指定目标库位：请填写要把这批货记到哪个库位。"], 0, string.Empty);
        if (string.Equals(target, SentinelLocationNo, StringComparison.Ordinal))
            return new RelocatePreviewResult(["目标库位不能是『未指定位置』本身。"], 0, string.Empty);

        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        if (!await LocationExistsAsync(depot, target, connection, null, token))
            return new RelocatePreviewResult([$"目标库位 {depot}/{target} 不存在或已停用，无法归位。"], 0, string.Empty);

        var pending = await CountSentinelRowsToRelocateAsync(depot, connection, null, token);
        if (pending == 0)
            return new RelocatePreviewResult([], 0, "该库别没有记在『未指定位置』上的存量，无需归位。");
        return new RelocatePreviewResult([], pending,
            $"将把 {pending} 组（料号/批次）记在『未指定位置』上的存量改记到 {target}，库别总量不变。");
    }

    /// <summary>
    /// 独立归位执行（不改策略配置）：自带连接与事务，归位与它的审计要么都落库、要么都不落。
    /// </summary>
    public async Task<RelocateSentinelResult> RelocateStandaloneAsync(
        string depotId, string relocateTo, string actor, CancellationToken token = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        var result = await RelocateSentinelStockAsync(
            depotId, relocateTo, actor, connection, transaction, token);
        if (!result.Relocated)
        {
            await transaction.RollbackAsync(token);
            return result;
        }
        await transaction.CommitAsync(token);
        return result;
    }

    /// <summary>策略配置所在的模块号（110310 库存策略）；审计的 <c>MODULE_ID</c> 用它。</summary>
    public const int StockPolicyModuleId = 110310;

    /// <summary>
    /// 破坏性下调的判据：位置档位或批次档位**降低**。
    /// 按 §3.10.2 这两种下调都会让已有库存"看着还在、实际按新档位用不了"，所以要先确认——
    /// 注意这里是"要确认"而不是"拒绝"：档位下调本身是合法操作。
    /// </summary>
    public static bool IsDestructiveDowngrade(DepotStockPolicy candidate, DepotStockPolicy previous) =>
        candidate.LocationMode < previous.LocationMode || candidate.BatchMode < previous.BatchMode;

    private static string DescribeDowngrade(DepotStockPolicy candidate, DepotStockPolicy previous)
    {
        var parts = new List<string>();
        if (candidate.LocationMode < previous.LocationMode)
            parts.Add($"位置档位 {previous.LocationMode}→{candidate.LocationMode}");
        if (candidate.BatchMode < previous.BatchMode)
            parts.Add($"批次档位 {previous.BatchMode}→{candidate.BatchMode}");
        return string.Join("、", parts);
    }

    /// <summary>读取该作用域**自身**的策略行；不存在返回 null（区别于"回落部署级默认"）。</summary>
    private static async Task<DepotStockPolicy?> ReadExistingAsync(
        string depotId, SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH, "
            + "MONTH_CLOSE_BY_BATCH, MONTH_CLOSE_BY_LOCATION FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID = @depot",
            connection, transaction);
        command.Parameters.AddWithValue("@depot", depotId);
        await using var reader = await command.ExecuteReaderAsync(token);
        return await reader.ReadAsync(token) ? Read(reader) : null;
    }

    /// <summary>只记录**真正变化**的维度：新增行记全部（旧值为空），更新行记差异，避免审计被噪声淹没。</summary>
    private static IReadOnlyList<AuditFieldChange> BuildFieldChanges(DepotStockPolicy? before, DepotStockPolicy after)
    {
        var changes = new List<AuditFieldChange>();
        void Compare(string field, object? oldValue, object? newValue)
        {
            var oldText = Format(oldValue);
            var newText = Format(newValue);
            if (before is not null && string.Equals(oldText, newText, StringComparison.Ordinal))
                return;
            changes.Add(new AuditFieldChange(field, before is null ? null : oldText, newText, null));
        }

        Compare("LOCATION_MODE", before?.LocationMode, after.LocationMode);
        Compare("STORAGE_MODE", before?.StorageMode, after.StorageMode);
        Compare("BATCH_MODE", before?.BatchMode, after.BatchMode);
        Compare("CAPACITY_MODE", before?.CapacityMode, after.CapacityMode);
        Compare("MIX_PRODUCT", before?.MixProduct, after.MixProduct);
        Compare("MIX_BATCH", before?.MixBatch, after.MixBatch);
        Compare("MONTH_CLOSE_BY_BATCH", before?.MonthCloseByBatch, after.MonthCloseByBatch);
        Compare("MONTH_CLOSE_BY_LOCATION", before?.MonthCloseByLocation, after.MonthCloseByLocation);
        return changes;
    }

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        bool flag => flag ? "1" : "0",
        _ => value.ToString() ?? string.Empty,
    };

    private static string DescribeChange(DepotStockPolicy? before, DepotStockPolicy after)
    {
        var prefix = before is null ? "库存策略新增" : "库存策略更新";
        var changes = BuildFieldChanges(before, after);
        return changes.Count == 0
            ? $"{prefix}（{after.DepotId}）：无字段变化"
            : $"{prefix}（{after.DepotId}）：" + string.Join("、", changes.Select(c => $"{c.FieldName} {c.OldValue}→{c.NewValue}"));
    }

    /// <summary>R-S2 的判据：该库别下存在批管料件（有库存余额行）而批次档位为 0。
    /// 「取严者胜」会兜住产品级要求，但仍应提示，避免客户以为批次在这里被管起来了。
    /// </summary>
    public async Task<bool> HasBatchManagedProductAsync(string depotId, CancellationToken token = default)
    {
        await using var connection = connections.Create();
        await connection.OpenAsync(token);
        await using var command = new SqlCommand(
            "SELECT TOP 1 1 FROM dbo.INV_PRO_DEPOT d "
            + "JOIN dbo.PRODUCT p ON p.PRO_NO = d.PRO_NO "
            + "WHERE d.DEPOT_ID = @depot AND ISNULL(p.MANAGE_BATCH, 0) = 1",
            connection);
        command.Parameters.AddWithValue("@depot", depotId.Trim());
        return await command.ExecuteScalarAsync(token) is not null;
    }

    /// <summary>软性提示：批次档位为 0 但该库别有批管料件时给出告警。</summary>
    public async Task<IReadOnlyList<string>> WarnBatchManagedAsync(DepotStockPolicy policy, CancellationToken token = default)
    {
        if (policy.BatchMode != 0 || policy.DepotId == DeploymentScope)
            return Array.Empty<string>();

        return await HasBatchManagedProductAsync(policy.DepotId, token)
            ? new[] { "该库别有批管料件，但批次档位为 0：产品级「需要批号」仍然生效，仓库侧不额外记录批号。" }
            : Array.Empty<string>();
    }

    /// <summary>
    /// `MIX_*` 由「允许」收紧为「禁止」时的**当前违规位置清单**。
    ///
    /// §3.10.2 对这种变更的处置是"允许保存，但必须给出清单，由人工逐步整改"——收紧本身没错
    /// （客户可以决定从此不再混放），但只写库不给清单，客户会以为**存量也已经合规了**。
    ///
    /// 受影响范围按「整行覆盖」求值：库别行只影响它自己；部署级默认行只影响**没有自己策略行**的
    /// 库别——有库别行的按其自身取值，不受部署级改动影响。
    /// </summary>
    private async Task<IReadOnlyList<string>> ListMixedLocationWarningsAsync(
        DepotStockPolicy candidate,
        DepotStockPolicy previous,
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken token)
    {
        var tightenProduct = previous.MixProduct && !candidate.MixProduct;
        var tightenBatch = previous.MixBatch && !candidate.MixBatch;
        if (!tightenProduct && !tightenBatch)
            return Array.Empty<string>();

        await using var command = new SqlCommand(
            "SELECT LTRIM(RTRIM(d.DEPOT_ID)) + N'/' + d.LOCATION_NO, "
            + "CAST(COUNT(DISTINCT LTRIM(RTRIM(d.PRO_NO))) AS varchar(10)), "
            + "CAST(COUNT(DISTINCT ISNULL(d.BATCH_NO, N'')) AS varchar(10)) "
            + "FROM dbo.INV_PRO_DEPOT d "
            // 刻意不加脏读提示：这是保存路径上的**存量合规判定**，读脏数据会把"当前是否混放"判错；
            // 与求值/校验同处一个事务，读已提交数据才是应有口径。
            // 哨兵库位是"未指定位置"的兜底行，存量本来就会堆在一起，不构成需要整改的混放。
            + "WHERE ISNULL(d.QTY,0) <> 0 AND d.LOCATION_NO <> N'-' "
            + "AND ((@scope = N'*' AND NOT EXISTS (SELECT 1 FROM dbo.DEPOT_STOCK_POLICY p WHERE p.DEPOT_ID = d.DEPOT_ID)) "
            + "  OR (@scope <> N'*' AND d.DEPOT_ID = @scope)) "
            + "GROUP BY d.DEPOT_ID, d.LOCATION_NO "
            + "HAVING COUNT(DISTINCT LTRIM(RTRIM(d.PRO_NO))) > 1 OR COUNT(DISTINCT ISNULL(d.BATCH_NO, N'')) > 1 "
            + "ORDER BY 1",
            connection, transaction);
        command.Parameters.AddWithValue("@scope", candidate.DepotId);

        var rows = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token))
                rows.Add($"{reader.GetString(0)}（{reader.GetString(1)} 个品号 / {reader.GetString(2)} 个批次）");

        if (rows.Count == 0)
            return Array.Empty<string>();

        const int maxShown = 20;
        var suffix = rows.Count > maxShown ? $"……以及另外 {rows.Count - maxShown} 处" : string.Empty;
        var which = tightenProduct && tightenBatch ? "品号与批次" : tightenProduct ? "品号" : "批次";
        return new[]
        {
            $"已收紧混放限制（禁止混{which}），但当前有 {rows.Count} 个库位处于混放状态，需人工逐步整改："
            + string.Join("、", rows.Take(maxShown)) + suffix
        };
    }

    /// <summary>该库别有多少组（料号 / 批次）的存量还留在具体库位上——降档是否具有破坏性就看它。</summary>
    private static async Task<int> CountRowsToMergeAsync(
        string depotId, SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM (SELECT PRO_NO, BATCH_NO FROM dbo.INV_PRO_DEPOT "
            + "WHERE DEPOT_ID=@depot AND LOCATION_NO <> N'-' AND ISNULL(QTY,0) <> 0 "
            + "GROUP BY PRO_NO, BATCH_NO) x", connection, transaction);
        command.Parameters.AddWithValue("@depot", depotId);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// 位置归并（§3.10.2）：把该库别所有非哨兵位置行的数量并入哨兵行，位置行数量清零但**行保留**。
    ///
    /// 目标行按 **(料号, 批次)** 分组——只丢"位置"这一维，批次粒度保留；连批次一起并掉
    /// 会让一次降档顺带毁掉批次账的可追溯性。
    ///
    /// 库别级三字段（`INIT_QTY` / `COST_PRICE` / `COST_AMOUNT`）**一律不动**：归并只在同一库别内
    /// 重分配数量，库别合计不变，它们本就仍然同键一致。新补的哨兵行按既有口径取 **MAX** 复制，
    /// 绝不 SUM——这几列本来就是每行冗余同一个库别值（§3.19）。
    /// </summary>
    private static async Task MergeLocationsIntoSentinelAsync(
        string depotId, SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        // 1) 先给缺哨兵行的 (料号, 批次) 补出来：没有目标行，下一步的加法无处可加，
        //    而再下一步的清零会把数量直接抹掉——这正是"静默数据损失"的入口。
        await using (var ensure = new SqlCommand(
            "INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT) "
            + "SELECT s.PRO_NO, s.DEPOT_ID, N'-', s.BATCH_NO, 0, "
            + "  (SELECT MAX(ISNULL(b.INIT_QTY,0)) FROM dbo.INV_PRO_DEPOT b WHERE b.PRO_NO=s.PRO_NO AND b.DEPOT_ID=s.DEPOT_ID), "
            + "  (SELECT MAX(ISNULL(b.COST_PRICE,0)) FROM dbo.INV_PRO_DEPOT b WHERE b.PRO_NO=s.PRO_NO AND b.DEPOT_ID=s.DEPOT_ID), "
            + "  (SELECT MAX(ISNULL(b.COST_AMOUNT,0)) FROM dbo.INV_PRO_DEPOT b WHERE b.PRO_NO=s.PRO_NO AND b.DEPOT_ID=s.DEPOT_ID) "
            + "FROM (SELECT DISTINCT PRO_NO, DEPOT_ID, BATCH_NO FROM dbo.INV_PRO_DEPOT "
            + "      WHERE DEPOT_ID=@depot AND LOCATION_NO <> N'-') s "
            + "WHERE NOT EXISTS (SELECT 1 FROM dbo.INV_PRO_DEPOT d WHERE d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID "
            + "                  AND d.LOCATION_NO=N'-' AND d.BATCH_NO=s.BATCH_NO);", connection, transaction))
        {
            ensure.Parameters.AddWithValue("@depot", depotId);
            await ensure.ExecuteNonQueryAsync(token);
        }

        // 2) 数量并入哨兵行（按 料号 + 批次 分组，只合并位置这一维）
        await using (var move = new SqlCommand(
            "UPDATE d SET d.QTY = ISNULL(d.QTY,0) + x.QTY "
            + "FROM dbo.INV_PRO_DEPOT d JOIN (SELECT PRO_NO, BATCH_NO, SUM(ISNULL(QTY,0)) AS QTY "
            + "  FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@depot AND LOCATION_NO <> N'-' GROUP BY PRO_NO, BATCH_NO) x "
            + "  ON x.PRO_NO=d.PRO_NO AND x.BATCH_NO=d.BATCH_NO "
            + "WHERE d.DEPOT_ID=@depot AND d.LOCATION_NO=N'-';", connection, transaction))
        {
            move.Parameters.AddWithValue("@depot", depotId);
            await move.ExecuteNonQueryAsync(token);
        }

        // 3) 位置行清零（行保留：与 BATCH 降档"行保留不合并"同口径，也不破坏流水历史）
        await using var clear = new SqlCommand(
            "UPDATE dbo.INV_PRO_DEPOT SET QTY = 0 "
            + "WHERE DEPOT_ID=@depot AND LOCATION_NO <> N'-' AND ISNULL(QTY,0) <> 0;", connection, transaction);
        clear.Parameters.AddWithValue("@depot", depotId);
        await clear.ExecuteNonQueryAsync(token);
    }

    /// <summary>该库别有多少组（料号 / 批次）的存量还压在哨兵行上——升档是否需要归位就看它。</summary>
    private static async Task<int> CountSentinelRowsToRelocateAsync(
        string depotId, SqlConnection connection, SqlTransaction? transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM (SELECT PRO_NO, BATCH_NO FROM dbo.INV_PRO_DEPOT "
            + "WHERE DEPOT_ID=@depot AND LOCATION_NO = @sentinel AND ISNULL(QTY,0) <> 0 "
            + "GROUP BY PRO_NO, BATCH_NO) x", connection, transaction);
        command.Parameters.AddWithValue("@depot", depotId);
        command.Parameters.AddWithValue("@sentinel", SentinelLocationNo);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token));
    }

    private static async Task<bool> LocationExistsAsync(
        string depotId, string locationNo, SqlConnection connection, SqlTransaction? transaction, CancellationToken token)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.DEPOT_LOCATION WHERE DEPOT_ID=@depot AND LOCATION_NO=@loc AND STATUS=N'A'",
            connection, transaction);
        command.Parameters.AddWithValue("@depot", depotId);
        command.Parameters.AddWithValue("@loc", locationNo);
        return Convert.ToInt32(await command.ExecuteScalarAsync(token)) > 0;
    }

    /// <summary>
    /// 升档归位（§3.10.2）：把哨兵行的数量改记到**目标库位**上，哨兵行数量清零但**行保留**。
    ///
    /// 与降档归并（<see cref="MergeLocationsIntoSentinelAsync"/>）严格对称：同样按 **(料号, 批次)**
    /// 分组、同样先补目标行、同样按 **MAX** 复制库别级三字段（绝不 SUM），因此 `SUM(QTY)` 逐库别守恒。
    ///
    /// 注意这是**一次账面认定**，不是实地发现：它把"未指定位置"的货**当作**就在目标库位。
    /// 适用前提是"知道这批货大致在哪，或接受先认到一个默认位"；货散在各处时应当用盘点逐步归位。
    /// </summary>
    private static async Task RelocateSentinelAsync(
        string depotId, string targetLocation, SqlConnection connection, SqlTransaction transaction, CancellationToken token)
    {
        await using (var ensure = new SqlCommand(
            "INSERT INTO dbo.INV_PRO_DEPOT (PRO_NO, DEPOT_ID, LOCATION_NO, BATCH_NO, QTY, INIT_QTY, COST_PRICE, COST_AMOUNT) "
            + "SELECT s.PRO_NO, s.DEPOT_ID, @target, s.BATCH_NO, 0, "
            + "  (SELECT MAX(ISNULL(b.INIT_QTY,0)) FROM dbo.INV_PRO_DEPOT b WHERE b.PRO_NO=s.PRO_NO AND b.DEPOT_ID=s.DEPOT_ID), "
            + "  (SELECT MAX(ISNULL(b.COST_PRICE,0)) FROM dbo.INV_PRO_DEPOT b WHERE b.PRO_NO=s.PRO_NO AND b.DEPOT_ID=s.DEPOT_ID), "
            + "  (SELECT MAX(ISNULL(b.COST_AMOUNT,0)) FROM dbo.INV_PRO_DEPOT b WHERE b.PRO_NO=s.PRO_NO AND b.DEPOT_ID=s.DEPOT_ID) "
            + "FROM (SELECT DISTINCT PRO_NO, DEPOT_ID, BATCH_NO FROM dbo.INV_PRO_DEPOT "
            + "      WHERE DEPOT_ID=@depot AND LOCATION_NO = @sentinel) s "
            + "WHERE NOT EXISTS (SELECT 1 FROM dbo.INV_PRO_DEPOT d WHERE d.PRO_NO=s.PRO_NO AND d.DEPOT_ID=s.DEPOT_ID "
            + "                  AND d.LOCATION_NO=@target AND d.BATCH_NO=s.BATCH_NO);", connection, transaction))
        {
            ensure.Parameters.AddWithValue("@depot", depotId);
            ensure.Parameters.AddWithValue("@target", targetLocation);
            ensure.Parameters.AddWithValue("@sentinel", SentinelLocationNo);
            await ensure.ExecuteNonQueryAsync(token);
        }

        await using (var move = new SqlCommand(
            "UPDATE d SET d.QTY = ISNULL(d.QTY,0) + x.QTY "
            + "FROM dbo.INV_PRO_DEPOT d JOIN (SELECT PRO_NO, BATCH_NO, SUM(ISNULL(QTY,0)) AS QTY "
            + "  FROM dbo.INV_PRO_DEPOT WHERE DEPOT_ID=@depot AND LOCATION_NO=@sentinel GROUP BY PRO_NO, BATCH_NO) x "
            + "  ON x.PRO_NO=d.PRO_NO AND x.BATCH_NO=d.BATCH_NO "
            + "WHERE d.DEPOT_ID=@depot AND d.LOCATION_NO=@target;", connection, transaction))
        {
            move.Parameters.AddWithValue("@depot", depotId);
            move.Parameters.AddWithValue("@target", targetLocation);
            move.Parameters.AddWithValue("@sentinel", SentinelLocationNo);
            await move.ExecuteNonQueryAsync(token);
        }

        await using var clear = new SqlCommand(
            "UPDATE dbo.INV_PRO_DEPOT SET QTY = 0 "
            + "WHERE DEPOT_ID=@depot AND LOCATION_NO=@sentinel AND ISNULL(QTY,0) <> 0;", connection, transaction);
        clear.Parameters.AddWithValue("@depot", depotId);
        clear.Parameters.AddWithValue("@sentinel", SentinelLocationNo);
        await clear.ExecuteNonQueryAsync(token);
    }

    private static DepotStockPolicy Read(SqlDataReader reader) => new(
        reader.GetString(0).Trim(),
        reader.GetInt32(1),
        reader.GetString(2).Trim().ToUpperInvariant(),
        reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetBoolean(5),
        reader.GetBoolean(6),
        reader.GetBoolean(7),
        reader.GetBoolean(8));
}
