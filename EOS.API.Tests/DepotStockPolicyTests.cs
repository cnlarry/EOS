using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存策略的求值口径与组合规则。求值必须收口在 <see cref="DepotStockPolicyService"/>：
/// 库别行**整行覆盖**部署级默认行，不做列级逐字段继承。
/// 组合校验里未实现档位（现只剩容量）的拒绝放在服务端——界面灰显只是体验，
/// 直连 API 仍可复现"保存成功却不生效"。
/// </summary>
[Collection("live-database")]
public sealed class DepotStockPolicyTests
{
    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    private static DepotStockPolicyService CreateService() => PolicyServiceFactory.Create(RequireConnection());

    /// <summary>测试用构造：六个维度 + 月结两维 + 过期批次档位（默认取部署级口径 2）。</summary>
    private static DepotStockPolicy Policy(
        string depot, int locationMode, string storageMode, int batchMode, int capacityMode,
        bool mixProduct = true, bool mixBatch = true,
        bool monthCloseByBatch = true, bool monthCloseByLocation = false, int expiryMode = 2)
        => new(depot, locationMode, storageMode, batchMode, capacityMode, mixProduct, mixBatch,
            monthCloseByBatch, monthCloseByLocation, false, expiryMode);

    // ---------- 组合规则（纯逻辑，不需要数据库） ----------

    [Fact]
    public void 未实现档位在保存期被拒()
    {
        // 批次档位 3 已经实现，不再被拒；仍被拒的只剩容量档位。
        var batch3 = Policy("CP", 3, "FIXED", 3, 0, true, true);
        var (batchErrors, _) = DepotStockPolicyService.Validate(batch3);
        Assert.DoesNotContain(batchErrors, message => message.Contains("批次档位"));

        var capacity = Policy("CP", 3, "FIXED", 0, 2, true, true);
        var (errors2, _) = DepotStockPolicyService.Validate(capacity);
        Assert.Contains(errors2, message => message.Contains("容量档位"));
    }

    [Fact]
    public void 过期批次档位取值域被拦()
    {
        var (tooHigh, _) = DepotStockPolicyService.Validate(Policy("CP", 3, "FIXED", 0, 0, true, true, true, false, 3));
        Assert.Contains(tooHigh, message => message.Contains("过期批次档位"));

        var (negative, _) = DepotStockPolicyService.Validate(Policy("CP", 3, "FIXED", 0, 0, true, true, true, false, -1));
        Assert.Contains(negative, message => message.Contains("过期批次档位"));
    }

    /// <summary>
    /// 档位组合说不通时**只提示不拒存**：客户可以先配好效期管控、再逐步把批次档位开起来。
    /// 但"配了却不会生效"必须说出来——不说，客户会以为已经管起来了。
    /// </summary>
    [Fact]
    public void 过期批次开启但批次档位为零时给出告警()
    {
        var (errors, warnings) = DepotStockPolicyService.Validate(
            Policy("CP", 3, "FIXED", 0, 0, true, true, true, false, 1));

        Assert.Empty(errors);
        Assert.Contains(warnings, message => message.Contains("批次档位为 0"));
    }

    [Fact]
    public void 档位目录与保存规则同源_未实现的档位在目录里标为不可选()
    {
        // 目录是界面渲染的唯一来源，它标"能选"的档位必须真的存得进去，
        // 标"不可选"的必须真的被拒 —— 否则就是"看得见存不进"或"选得到却不生效"。
        // 批次档位 3（必填 + 效期）已解锁，目录里必须同步标成"能选"。
        var batch = DepotStockPolicyService.Tiers.Single(tier => tier.Key == "batchMode");
        var batch3 = batch.Options.Single(option => option.Value == DepotStockPolicyService.ExpiryBatchMode.ToString());
        Assert.True(batch3.Implemented);
        Assert.All(batch.Options, option => Assert.True(option.Implemented));

        var capacity = DepotStockPolicyService.Tiers.Single(tier => tier.Key == "capacityMode");
        Assert.All(capacity.Options, option =>
            Assert.Equal(
                int.Parse(option.Value, System.Globalization.CultureInfo.InvariantCulture) <= DepotStockPolicyService.MaxSupportedCapacityMode,
                option.Implemented));

        // 自检：目录里所有"能选"的档位，单独放进一条合规策略里必须不被拒。
        foreach (var tier in DepotStockPolicyService.Tiers)
        {
            foreach (var option in tier.Options.Where(item => item.Implemented))
            {
                var policy = tier.Key switch
                {
                    "locationMode" => Policy("CP", int.Parse(option.Value), "FIXED", 0, 0, true, true),
                    "storageMode" => Policy("CP", 3, option.Value, 0, 0, true, true),
                    "batchMode" => Policy("CP", 3, "FIXED", int.Parse(option.Value), 0, true, true),
                    "capacityMode" => Policy("CP", 3, "FIXED", 0, int.Parse(option.Value), true, true),
                    "mixProduct" => Policy("CP", 3, "FIXED", 0, 0, option.Value == "1", true),
                    "mixBatch" => Policy("CP", 3, "FIXED", 0, 0, true, option.Value == "1"),
                    // 过期批次档位：位置 3 + 批次 3 是它的推荐组合，两者都满足时才不产生告警。
                    "expiryMode" => Policy("CP", 3, "FIXED", 3, 0, true, true, true, false, int.Parse(option.Value)),
                    "monthCloseByBatch" => Policy("CP", 3, "FIXED", 0, 0, true, true, option.Value == "1", false),
                    "monthCloseByLocation" => Policy("CP", 3, "FIXED", 0, 0, true, true, true, option.Value == "1"),
                    // 本维度**只标了"否"可选**（"是"要等快照侧 WS-18b），所以这里只会走到 "0" 这一支；
                    // "是"被拒的断言在 MonthCloseHalfStockScopeLiveTests 里（那条规矩落在
                    // ValidateMonthCloseScope 而不是 Validate）。
                    "monthCloseScopeHalfStock" => Policy("CP", 3, "FIXED", 0, 0, true, true, true, true)
                        with { MonthCloseScopeHalfStock = option.Value == "1" },
                    _ => throw new InvalidOperationException($"目录里出现了未登记的维度 {tier.Key}"),
                };
                var (errors, _) = DepotStockPolicyService.Validate(policy);
                Assert.True(errors.Count == 0,
                    $"目录标为可选的 {tier.Key}={option.Value}（{option.Label}）被保存规则拒绝：{string.Join("；", errors)}");
            }
        }
    }

    [Fact]
    public void 随机存放要求位置强制()
    {
        // R-C1：位置档位 0/1（不管 / 可填）配随机或混合存放 ⇒ 货必然丢失
        foreach (var locationMode in new[] { 0, 1 })
        {
            var (errors, _) = DepotStockPolicyService.Validate(Policy("CP", locationMode, "RANDOM", 0, 0, true, true));
            Assert.Contains(errors, message => message.Contains("随机存放"));
        }

        // 档位 2/3 不触发硬性拒绝；档位 2 只出软性告警。
        // 过期批次档位取 0（最松）——默认值 2 与批次档位 0 是"配了不生效"的组合，会另出一条告警。
        var (okErrors, okWarnings) = DepotStockPolicyService.Validate(
            Policy("CP", 3, "RANDOM", 0, 0, true, true, true, false, 0));
        Assert.Empty(okErrors);
        Assert.Empty(okWarnings);

        var (_, warned) = DepotStockPolicyService.Validate(Policy("CP", 2, "MIXED", 0, 0, true, true));
        Assert.Contains(warned, message => message.Contains("扫码"));
    }

    [Fact]
    public void 容量与效期都以位置为前提()
    {
        // R-C2：位置档位 0 时不能启用容量校验
        var (errors, _) = DepotStockPolicyService.Validate(Policy("CP", 0, "FIXED", 0, 1, true, true));
        Assert.Contains(errors, message => message.Contains("容量校验"));

        // R-C3：效期追溯要求位置强制（档位 3）
        var (errors2, _) = DepotStockPolicyService.Validate(Policy("CP", 2, "FIXED", 3, 0, true, true));
        Assert.Contains(errors2, message => message.Contains("效期追溯"));
    }

    [Fact]
    public void 最松配置不产生任何错误或告警()
    {
        // "最松"必须把过期批次档位也显式关掉（0）：默认的 2 与批次档位 0 组合起来是"配了不生效"，
        // 那条告警是对的，不该被当成噪声抹掉。
        var (errors, warnings) = DepotStockPolicyService.Validate(
            Policy("*", 0, "FIXED", 0, 0, true, true, true, false, 0));
        Assert.Empty(errors);
        Assert.Empty(warnings);
    }

    /// <summary>
    /// 取值域越界必须在服务端拦下（400），而不是让数据库以"字符串将被截断"/"违反 CHECK"
    /// 抛 SqlException 冒成 500——调用方看到"服务器内部错误"就无从知道是自己传错了参数。
    /// </summary>
    [Fact]
    public void 取值域越界在保存期被拒而非由数据库抛异常()
    {
        var tooLong = Policy("ADR14POLTOOLONG", 0, "FIXED", 0, 0);
        Assert.Contains(DepotStockPolicyService.Validate(tooLong).Errors, message => message.Contains("库别代号"));

        foreach (var storage in new[] { "LIFO", "" })
        {
            var bad = Policy("CP", 0, storage, 0, 0);
            Assert.Contains(DepotStockPolicyService.Validate(bad).Errors, message => message.Contains("存放方式"));
        }

        // 边界内不报错（10 字符正好等于列宽）
        Assert.Empty(DepotStockPolicyService.Validate(Policy("0123456789", 0, "fixed", 0, 0)).Errors);
    }

    [Fact]
    public void 月结维度不接受库别覆盖()
    {
        var deployment = Policy("*", 0, "FIXED", 0, 0, true, true, true, false);

        // 部署级自身：任何取值都合法（它就是基准）
        Assert.Null(DepotStockPolicyService.ValidateMonthCloseScope(
            Policy("*", 0, "FIXED", 0, 0, true, true, false, true), deployment));

        // 库别行与部署级一致：放行
        Assert.Null(DepotStockPolicyService.ValidateMonthCloseScope(
            Policy("CP", 3, "RANDOM", 2, 0, false, false, true, false), deployment));

        // 库别行试图改月结粒度：拒绝（跨仓粒度不一致会让汇总重复计数或漏计）
        Assert.NotNull(DepotStockPolicyService.ValidateMonthCloseScope(
            Policy("CP", 3, "RANDOM", 2, 0, false, false, false, false), deployment));
        Assert.NotNull(DepotStockPolicyService.ValidateMonthCloseScope(
            Policy("CP", 3, "RANDOM", 2, 0, false, false, true, true), deployment));
    }

    /// <summary>
    /// 保存期拒存：硬性规则与月结作用域违规一律 fail-closed（服务端拒绝，不靠界面拦截），
    /// 合规配置正常落库。本用例会真实写入，故收尾显式清理。
    /// </summary>
    [Fact]
    public async Task 保存期拒绝未实现档位与月结库别覆盖()
    {
        const string depot = "ADR14POLSV";
        var service = CreateService();
        try
        {
            // 批次档位 3（必填 + 效期）已实现：保存期不再拒绝（保留本条是为了钉住"解锁"这件事本身，
            // 组合规则 R-C3 由 Validate 用例覆盖）。这里只走 Validate，避免为断言写库。
            var expiryTier = DepotStockPolicyService.Validate(Policy(depot, 3, "FIXED", 3, 0));
            Assert.DoesNotContain(expiryTier.Errors, message => message.Contains("批次档位"));

            // 未实现档位：直接提交（等价于绕过界面 POST）必须被拒
            var capacity = await service.SaveAsync(Policy(depot, 3, "FIXED", 0, 2), "adr14-test");
            Assert.False(capacity.Saved);
            Assert.Contains(capacity.Errors, message => message.Contains("容量档位"));

            // 月结维度：库别行与部署级不一致即拒
            var scopeOverride = await service.SaveAsync(Policy(depot, 0, "FIXED", 0, 0, true, true, false, true), "adr14-test");
            Assert.False(scopeOverride.Saved);
            Assert.Contains(scopeOverride.Errors, message => message.Contains("月结维度"));

            // 取值域越界：走真实保存路径也必须是被拒（而不是抛 SqlException）
            var tooLong = await service.SaveAsync(Policy("ADR14POLTOOLONG", 0, "FIXED", 0, 0), "adr14-test");
            Assert.False(tooLong.Saved);
            Assert.Contains(tooLong.Errors, message => message.Contains("库别代号"));
            var badStorage = await service.SaveAsync(Policy(depot, 0, "LIFO", 0, 0), "adr14-test");
            Assert.False(badStorage.Saved);
            Assert.Contains(badStorage.Errors, message => message.Contains("存放方式"));

            // 合规配置：落库，且月结维度被写成部署级取值（库别行不得携带自己的粒度）
            var saved = await service.SaveAsync(Policy(depot, 3, "FIXED", 2, 0, false, false, true, false), "adr14-test");
            Assert.True(saved.Saved);
            Assert.Empty(saved.Errors);

            var stored = (await service.ListAsync()).Single(row => row.DepotId == depot);
            Assert.Equal(3, stored.LocationMode);
            Assert.Equal(2, stored.BatchMode);
            Assert.False(stored.MixProduct);
        }
        finally
        {
            await using var connection = new SqlConnection(RequireConnection());
            await connection.OpenAsync();
            await using var cleanup = new SqlCommand("DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@d", connection);
            cleanup.Parameters.AddWithValue("@d", depot);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    // ---------- 两跳求值（真库） ----------

    [Fact]
    public async Task 库别无行时回落部署级默认()
    {
        var service = CreateService();
        var policy = await service.ResolveAsync("ADR14POLNONE");

        Assert.Equal(DepotStockPolicyService.DeploymentScope, policy.DepotId);
        Assert.Equal(0, policy.LocationMode);
        Assert.Equal("FIXED", policy.StorageMode);
        Assert.Equal(0, policy.BatchMode);
        Assert.Equal(0, policy.CapacityMode);
        Assert.True(policy.MixProduct);
        Assert.True(policy.MixBatch);
    }

    [Fact]
    public async Task 库别行整行覆盖而不做列级继承()
    {
        const string depot = "ADR14POLDP";
        var connectionString = RequireConnection();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using (var insert = new SqlCommand(
                "DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@d; "
                + "INSERT INTO dbo.DEPOT_STOCK_POLICY (DEPOT_ID, LOCATION_MODE, STORAGE_MODE, BATCH_MODE, CAPACITY_MODE, MIX_PRODUCT, MIX_BATCH) "
                + "VALUES (@d, 3, N'RANDOM', 2, 0, 0, 0);", connection, transaction))
            {
                insert.Parameters.AddWithValue("@d", depot);
                await insert.ExecuteNonQueryAsync();
            }

            var service = CreateService();
            // 在调用方事务内求值：另开连接会被这里的未提交写锁住（这本身也是
            // 策略求值在保存校验路径里的正确用法）。
            var policy = await service.ResolveAsync(depot, connection, transaction);

            // 库别行存在 ⇒ 整行采用：六个维度全部来自库别行，没有任何一个继承自部署级默认
            // （部署级是 0 / FIXED / 0 / 0 / 1 / 1）。
            Assert.Equal(depot, policy.DepotId);
            Assert.Equal(3, policy.LocationMode);
            Assert.Equal("RANDOM", policy.StorageMode);
            Assert.Equal(2, policy.BatchMode);
            Assert.False(policy.MixProduct);
            Assert.False(policy.MixBatch);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    // ---------- 变更审计与破坏性下调二次确认（真库） ----------

    private const string AuditDepot = "ADR14AUDP";

    /// <summary>
    /// 策略变更必须留审计痕迹，且**记的是真实前后值**。审计与策略写在同一个事务里
    /// （要求的是"必须留痕"，best-effort 写在配置变更失败时不会有人发现）。
    ///
    /// 断言方式沿用仓库既有做法：取 `MAX(EVENT_ID)` 基线，只看基线之后本用例资源键的行——
    /// 审计表是事实源，测试**不删审计行**。
    /// </summary>
    [Fact]
    public async Task 策略变更写审计并记录前后档位值()
    {
        var connectionString = RequireConnection();
        var service = CreateService();
        try
        {
            await CleanupAsync(connectionString);
            var baseline = await MaxEventIdAsync(connectionString);

            // 新增：旧值为空
            var created = await service.SaveAsync(Policy(AuditDepot, 3, "FIXED", 2, 0), "adr14-audit");
            Assert.True(created.Saved, string.Join("；", created.Errors));

            var createEvent = await ReadAuditAsync(connectionString, AuditDepot, baseline);
            Assert.NotNull(createEvent);
            Assert.Equal("CREATE", createEvent!.Action);
            Assert.Contains("库存策略新增", createEvent.Summary);
            Assert.Contains(createEvent.Changes, c => c.Field == "LOCATION_MODE" && c.Old is null && c.New == "3");
            Assert.Contains(createEvent.Changes, c => c.Field == "BATCH_MODE" && c.Old is null && c.New == "2");

            // 更新：只记**真正变化**的维度，且带前后值。
            // 这里刻意只改存放方式：任何档位**降低**都会触发二次确认（见下一个用例），
            // 用它来验"只记变化维度"会把两件事混在一起。
            var updateBaseline = await MaxEventIdAsync(connectionString);
            var updated = await service.SaveAsync(Policy(AuditDepot, 3, "RANDOM", 2, 0), "adr14-audit");
            Assert.True(updated.Saved, string.Join("；", updated.Errors));

            var updateEvent = await ReadAuditAsync(connectionString, AuditDepot, updateBaseline);
            Assert.NotNull(updateEvent);
            Assert.Equal("UPDATE", updateEvent!.Action);
            var storage = Assert.Single(updateEvent.Changes, c => c.Field == "STORAGE_MODE");
            Assert.Equal("FIXED", storage.Old);
            Assert.Equal("RANDOM", storage.New);
            // 没变的维度不该出现在审计里（否则真实变更会被噪声淹没）
            Assert.DoesNotContain(updateEvent.Changes, c => c.Field == "LOCATION_MODE");
            Assert.DoesNotContain(updateEvent.Changes, c => c.Field == "BATCH_MODE");
        }
        finally
        {
            await CleanupAsync(connectionString);
        }
    }

    /// <summary>
    /// 档位下调是破坏性变更：未确认时**不写入任何改动**（fail-closed），确认后才落库。
    /// 门槛放在服务端而不是界面——直连 API 同样绕不过去。
    /// </summary>
    [Fact]
    public async Task 档位下调需二次确认且未确认时不写入任何改动()
    {
        var connectionString = RequireConnection();
        var service = CreateService();
        try
        {
            await CleanupAsync(connectionString);
            var seeded = await service.SaveAsync(Policy(AuditDepot, 3, "FIXED", 2, 0), "adr14-audit");
            Assert.True(seeded.Saved, string.Join("；", seeded.Errors));

            var baseline = await MaxEventIdAsync(connectionString);
            var unconfirmed = await service.SaveAsync(Policy(AuditDepot, 1, "FIXED", 0, 0), "adr14-audit");

            Assert.False(unconfirmed.Saved);
            Assert.True(unconfirmed.RequiresConfirmation);
            Assert.Contains(unconfirmed.Errors, message => message.Contains("二次确认"));
            Assert.Contains(unconfirmed.Errors, message => message.Contains("位置档位 3→1"));
            Assert.Contains(unconfirmed.Errors, message => message.Contains("批次档位 2→0"));

            // 未确认 = 什么都没发生：库里的行没变，审计也没有多出一条
            var stored = (await service.ListAsync()).Single(row => row.DepotId == AuditDepot);
            Assert.Equal(3, stored.LocationMode);
            Assert.Equal(2, stored.BatchMode);
            Assert.Null(await ReadAuditAsync(connectionString, AuditDepot, baseline));

            // 确认后放行
            var confirmed = await service.SaveAsync(Policy(AuditDepot, 1, "FIXED", 0, 0), "adr14-audit", confirmDestructive: true);
            Assert.True(confirmed.Saved, string.Join("；", confirmed.Errors));
            Assert.False(confirmed.RequiresConfirmation);
            var afterConfirm = (await service.ListAsync()).Single(row => row.DepotId == AuditDepot);
            Assert.Equal(1, afterConfirm.LocationMode);
            Assert.Equal(0, afterConfirm.BatchMode);
        }
        finally
        {
            await CleanupAsync(connectionString);
        }
    }

    /// <summary>升档（或平级重存）不是破坏性操作，不该向用户要求确认。</summary>
    [Fact]
    public async Task 升档不需要二次确认()
    {
        var connectionString = RequireConnection();
        var service = CreateService();
        try
        {
            await CleanupAsync(connectionString);
            var seeded = await service.SaveAsync(Policy(AuditDepot, 1, "FIXED", 1, 0), "adr14-audit");
            Assert.True(seeded.Saved, string.Join("；", seeded.Errors));

            var upgraded = await service.SaveAsync(Policy(AuditDepot, 3, "FIXED", 2, 0), "adr14-audit");
            Assert.True(upgraded.Saved, string.Join("；", upgraded.Errors));
            Assert.False(upgraded.RequiresConfirmation);

            // 平级重存同样不要求确认
            var same = await service.SaveAsync(Policy(AuditDepot, 3, "RANDOM", 2, 0), "adr14-audit");
            Assert.True(same.Saved, string.Join("；", same.Errors));
            Assert.False(same.RequiresConfirmation);
        }
        finally
        {
            await CleanupAsync(connectionString);
        }
    }

    private static async Task CleanupAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cleanup = new SqlCommand("DELETE FROM dbo.DEPOT_STOCK_POLICY WHERE DEPOT_ID=@d", connection);
        cleanup.Parameters.AddWithValue("@d", AuditDepot);
        await cleanup.ExecuteNonQueryAsync();
    }

    private static async Task<long> MaxEventIdAsync(string connectionString)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT ISNULL(MAX(EVENT_ID),0) FROM dbo.AUDIT_EVENT", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private sealed record AuditedChange(string Field, string? Old, string? New);

    private sealed record AuditedEvent(string Action, string Summary, IReadOnlyList<AuditedChange> Changes);

    private static async Task<AuditedEvent?> ReadAuditAsync(string connectionString, string depot, long baseline)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();

        long eventId;
        string action;
        string summary;
        await using (var command = new SqlCommand(
            "SELECT EVENT_ID, ACTION, ISNULL(SUMMARY, N'') FROM dbo.AUDIT_EVENT "
            + "WHERE EVENT_ID > @baseline AND RESOURCE_TYPE = N'DEPOT_STOCK_POLICY' AND RESOURCE_KEY = @key",
            connection))
        {
            command.Parameters.AddWithValue("@baseline", baseline);
            command.Parameters.AddWithValue("@key", depot);
            await using var reader = await command.ExecuteReaderAsync();
            if (!await reader.ReadAsync())
                return null;
            eventId = reader.GetInt64(0);
            action = reader.GetString(1).Trim();
            summary = reader.GetString(2);
        }

        var changes = new List<AuditedChange>();
        await using (var command = new SqlCommand(
            "SELECT FIELD_NAME, OLD_VALUE, NEW_VALUE FROM dbo.AUDIT_FIELD_CHANGE WHERE EVENT_ID = @id ORDER BY FIELD_NAME",
            connection))
        {
            command.Parameters.AddWithValue("@id", eventId);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                changes.Add(new AuditedChange(
                    reader.GetString(0).Trim(),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return new AuditedEvent(action, summary, changes);
    }
}
