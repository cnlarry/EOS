using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.Bom;
using EOS.API.Data.Effects;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 子件版本（`BOM_STRU_D.ELEMENT_EDITION`，迁移 416）的两件事：
///   ① **展开**按明细指定的版本走——同一份父 BOM 换子件版本，子树随之不同；
///   ② **保存期校验** `require-when-referenced`：半成品必填、材料必须留空、填了必须存在。
///
/// 为什么这两条要单独测：导入路径有自己的校验（见 `BomImportLiveTests`），
/// 但**保存走的是效果引擎**。两条链路的判据必须一致，否则出现"导入能过、保存被拒"。
///
/// 造数<b>提交</b>而不是事务回滚：<see cref="BomRepository"/> 自己开连接，
/// 读不到未提交的数据。校验用例例外——它接受调用方的事务，所以直接在事务里造数。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class BomElementEditionLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    private const string Parent = "ADR41EDPARENT";
    private const string Sub = "ADR41EDSUB";
    private const string LeafA = "ADR41EDLEAFA";
    private const string LeafB = "ADR41EDLEAFB";

    private static BomRepository CreateRepository() =>
        new(new DbConnectionFactory(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:ErpDatabase"] = ConnectionString,
            })
            .Build()));

    /// <summary>
    /// 同一份父成品两个版次，各自把同一个半成品指向**不同的子件版本**——
    /// 这正是"子件版本写进明细"要表达的能力：同一半成品在不同父件里可以用不同版本。
    /// </summary>
    [Fact]
    public async Task 展开_按明细指定的子件版本走()
    {
        await SeedTreeAsync();
        try
        {
            var repository = CreateRepository();

            var ed01 = await repository.ExpandAsync(Parent, "01", BomRepository.DefaultTreeDepth, CancellationToken.None);
            var ed02 = await repository.ExpandAsync(Parent, "02", BomRepository.DefaultTreeDepth, CancellationToken.None);

            // 01 版：P → S(01) → LeafA ；02 版：P → S(02) → LeafB
            Assert.Contains(ed01, row => row.ProNo == LeafA);
            Assert.DoesNotContain(ed01, row => row.ProNo == LeafB);
            Assert.Contains(ed02, row => row.ProNo == LeafB);
            Assert.DoesNotContain(ed02, row => row.ProNo == LeafA);

            // 半成品 S 自己在两棵树里都存在，只是往下展开的版本不同
            Assert.Contains(ed01, row => row.ProNo == Sub && row.OwnEdition == "01");
            Assert.Contains(ed02, row => row.ProNo == Sub && row.OwnEdition == "02");
        }
        finally
        {
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task 校验_半成品缺子件版次被拒()
    {
        var error = await Assert.ThrowsAsync<EffectValidationException>(() =>
            ValidateDetailAsync(Sub, elementEdition: null));

        Assert.Contains("必须指定子件版次", error.Message);
    }

    [Fact]
    public async Task 校验_材料留空通过()
    {
        // 材料没有下阶 BOM，目标表里查不到它 → 规则整行跳过，不该被拦
        await ValidateDetailAsync(LeafA, elementEdition: null);
    }

    [Fact]
    public async Task 校验_子件版次不存在被拒()
    {
        var error = await Assert.ThrowsAsync<EffectValidationException>(() =>
            ValidateDetailAsync(Sub, elementEdition: "07"));

        // 规则只有一句文案，不区分"为空"与"不存在"——两者都是"版次没用对"。
        // 用户靠 {ROWS} 里的序号定位到具体行，所以断言到"子件版次"这一层即可。
        Assert.Contains("子件版次", error.Message);
    }

    /// <summary>
    /// 在事务里造一行明细，然后跑效果引擎的校验链，最后回滚。
    ///
    /// 用事务而不是提交 + 清理：`ValidateAsync` 接受调用方的事务，
    /// 所以这次造数不需要落库，也不会污染真库。
    /// </summary>
    private static async Task ValidateDetailAsync(string child, string? elementEdition)
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(token);
        try
        {
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION) VALUES (@parent, N'01');
                INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION) VALUES (@sub, N'01');
                INSERT INTO dbo.BOM_STRU_D
                    (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_EDITION, ELEMENT_QTY, BASE_QTY, LOST_RATE)
                VALUES (@parent, N'01', 1, @child, @childEdition, 1, 1, 0);
                """, connection, transaction))
            {
                seed.Parameters.Add("@parent", SqlDbType.NVarChar, 60).Value = Parent;
                seed.Parameters.Add("@sub", SqlDbType.NVarChar, 60).Value = Sub;
                seed.Parameters.Add("@child", SqlDbType.NVarChar, 60).Value = child;
                seed.Parameters.Add("@childEdition", SqlDbType.NVarChar, 2).Value =
                    (object?)elementEdition ?? DBNull.Value;
                await seed.ExecuteNonQueryAsync(token);
            }

            await new EffectValidationExecutor().ValidateAsync(
                connection, transaction, BuildPlan(), "SAVE", token, [Parent, "01"]);
        }
        finally
        {
            // 无论断言成不成立，都不留痕
            await transaction.RollbackAsync(token);
        }
    }

    /// <summary>与迁移 416 写进 `MODULE_VALIDATION_RULE` 的参数同形（同一条规则的手工装配）。</summary>
    private static ModuleEffectPlan BuildPlan()
    {
        using var document = JsonDocument.Parse("""
            {"checks":[{"sourceField":"ELEMENT_PRO_NO","requiredField":"ELEMENT_EDITION",
            "targetTable":"BOM_STRU_M","targetKeyField":"PRO_NO","targetValueField":"EDITION",
            "message":"以下序号项的子件有下阶 BOM，必须指定子件版次\r\n{ROWS}","lineField":"SERIAL_NO"}]}
            """);
        var rule = new EffectValidationPlan(
            6, "SAVE", "require-when-referenced", true, document.RootElement.Clone(),
            "以下序号项的子件有下阶 BOM，必须指定子件版次");
        return new ModuleEffectPlan(
            1204, "BOM_STRU_M", "BOM_STRU_D", "test", ["PRO_NO", "EDITION"], [], [rule]);
    }

    /// <summary>
    /// 树：Parent 有 01/02 两版；Sub 也有 01/02 两版，分别指向 LeafA / LeafB。
    /// Parent 的 01 版把 Sub 指向 01，02 版指向 02 —— 展开路径因此分叉。
    /// </summary>
    private static async Task SeedTreeAsync()
    {
        await CleanupAsync();
        await ExecuteAsync("""
            INSERT INTO dbo.PRODUCT (PRO_NO, PRO_NAME) VALUES
                (N'ADR41EDPARENT', N'版本测试父件'),
                (N'ADR41EDSUB', N'版本测试半成品'),
                (N'ADR41EDLEAFA', N'版本测试材料A'),
                (N'ADR41EDLEAFB', N'版本测试材料B');

            INSERT INTO dbo.BOM_STRU_M (PRO_NO, EDITION) VALUES
                (N'ADR41EDPARENT', N'01'), (N'ADR41EDPARENT', N'02'),
                (N'ADR41EDSUB', N'01'), (N'ADR41EDSUB', N'02');

            -- 父件两版：同一半成品，指向它不同的两个版本
            INSERT INTO dbo.BOM_STRU_D
                (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_EDITION, ELEMENT_QTY, BASE_QTY, LOST_RATE)
            VALUES
                (N'ADR41EDPARENT', N'01', 1, N'ADR41EDSUB', N'01', 2, 1, 0),
                (N'ADR41EDPARENT', N'02', 1, N'ADR41EDSUB', N'02', 2, 1, 0);

            -- 半成品两版：各自指向不同的材料（材料无 BOM，版次留空）
            INSERT INTO dbo.BOM_STRU_D
                (PRO_NO, EDITION, SERIAL_NO, ELEMENT_PRO_NO, ELEMENT_EDITION, ELEMENT_QTY, BASE_QTY, LOST_RATE)
            VALUES
                (N'ADR41EDSUB', N'01', 1, N'ADR41EDLEAFA', NULL, 3, 1, 0),
                (N'ADR41EDSUB', N'02', 1, N'ADR41EDLEAFB', NULL, 3, 1, 0);
            """);
    }

    private static async Task CleanupAsync()
        => await ExecuteAsync("""
            DELETE FROM dbo.BOM_STRU_D WHERE PRO_NO IN (N'ADR41EDPARENT', N'ADR41EDSUB');
            DELETE FROM dbo.BOM_STRU_M WHERE PRO_NO IN (N'ADR41EDPARENT', N'ADR41EDSUB');
            DELETE FROM dbo.PRODUCT WHERE PRO_NO IN
                (N'ADR41EDPARENT', N'ADR41EDSUB', N'ADR41EDLEAFA', N'ADR41EDLEAFB');
            """);

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
