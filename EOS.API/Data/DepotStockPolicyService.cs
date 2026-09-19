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
/// 库存策略的唯一求值入口。策略决定"管到多细"（位置 / 存放 / 批次 / 容量 / 混品号 / 混批次），
/// 求值口径是**两跳且整行覆盖**：库别行存在则整行采用，否则回落到部署级默认行。
///
/// 求值必须收口在这里 —— 各处自行拼装默认值会让"库别无行时取什么"出现多个口径，而这类
/// 分歧只在某个库别真的没有配置行时才显形。
/// </summary>
public sealed class DepotStockPolicyService(DbConnectionFactory connections)
{
    /// <summary>部署级默认行的作用域键。</summary>
    public const string DeploymentScope = "*";

    /// <summary>本版未实现的批次档位（必填 + 效期）：保存期一律拒绝。</summary>
    public const int UnimplementedBatchMode = 3;

    /// <summary>本版支持的容量档位上限（恒为 0，即不校验）。</summary>
    public const int MaxSupportedCapacityMode = 0;

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

    /// <summary>保存一条策略行的结果：硬性规则违例即拒存，软性规则作为告警返回。</summary>
    public sealed record SavePolicyResult(
        bool Saved,
        IReadOnlyList<string> Errors,
        IReadOnlyList<string> Warnings);

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
    /// </summary>
    public async Task<SavePolicyResult> SaveAsync(
        DepotStockPolicy candidate, string actor, CancellationToken token = default)
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

        // 收紧混放限制时给出存量违规清单：按 §3.10.2 允许保存，但必须让人知道哪些库位要整改。
        // 基准是**生效中的旧策略**（库别行存在则取它自己，否则取部署级默认），与"整行覆盖"同口径。
        var previous = await ResolveAsync(candidate.DepotId, connection, transaction, token);
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

        await transaction.CommitAsync(token);
        return new SavePolicyResult(true, Array.Empty<string>(), warnings);
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
