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
    bool MixBatch);

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
            "SELECT TOP 1 DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH "
            + "FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID IN (@scope, @default) "
            + "ORDER BY CASE WHEN DEPOT_ID = @scope THEN 0 ELSE 1 END",
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
    /// R-S2 的判据：该库别下存在批管料件（有库存余额行）而批次档位为 0。
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

    private static DepotStockPolicy Read(SqlDataReader reader) => new(
        reader.GetString(0).Trim(),
        reader.GetInt32(1),
        reader.GetString(2).Trim().ToUpperInvariant(),
        reader.GetInt32(3),
        reader.GetInt32(4),
        reader.GetBoolean(5),
        reader.GetBoolean(6));
}
