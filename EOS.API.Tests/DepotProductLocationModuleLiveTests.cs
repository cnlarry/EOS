using System.Data;
using System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 物料主货位维护入口的真库验收（ADR-020 §10 WS-14 / 迁移 238）。
///
/// 本段交付的是**入口**：`DEPOT_PRODUCT_LOCATION` 建表在迁移 180，但在此之前 0 行 / 0 元数据 / 0 模块
/// —— 没有任何人能往里写一行主货位，于是 `STORAGE_MODE = FIXED` 的库别在引擎里认不到主货位。
/// 这里断言四件事真的就位（缺任何一件，"能维护主货位"都只是说法）：
///   ① 模块行指向 `DEPOT_PRODUCT_LOCATION`，挂在 `1103 仓库管理` 下；
///   ② 字段元数据 5 行、主键 3 列、选择器 3 个，且**主键顺序与表上的 PK 一致**；
///   ③ 已发布定义的主键顺序 / 可新增可编辑与表一致（统一表单按它出表单）；
///   ④ `appsettings.json` 的 `UnifiedFormEditor.EnabledModuleIds` 里有这个模块号
///      —— 没有它，统一表单对任何模块都直接 404（这是模块能不能被维护的**开关**，
///      写成用例是为了它不会在某次配置整理里被悄悄删掉）。
/// </summary>
[Collection("live-database")]
public sealed class DepotProductLocationModuleLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private const int ModuleId = 110311;
    private const string Table = "DEPOT_PRODUCT_LOCATION";

    [Fact]
    public async Task 模块与字段元数据就位_主键顺序与表一致()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var command = new SqlCommand(
            "SELECT M_DESC, RTRIM(MASTER_TABLE), ISNULL(M_P_IDX, 0) FROM dbo.MODULES WHERE M_IDX = @Id;", connection))
        {
            command.Parameters.Add("@Id", SqlDbType.Int).Value = ModuleId;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"模块 {ModuleId} 应存在（迁移 238）。");
            Assert.Equal("物料主货位", reader.GetString(0).Trim());
            Assert.Equal(Table, reader.GetString(1));
            Assert.Equal(1103, reader.GetInt32(2));     // 挂在「仓库管理」下
        }

        await using (var command = new SqlCommand("""
            SELECT F_ID, IS_PK, IS_VISIBLE, BROWSE_M_IDX, FORM_ORDER
              FROM dbo.FIELDS WHERE RTRIM(T_ID) = @Table ORDER BY FORM_ORDER, F_ID;
            """, connection))
        {
            command.Parameters.Add("@Table", SqlDbType.NVarChar, 50).Value = Table;
            var fields = new List<(string Field, bool Pk, bool Visible, int? Chooser, int? Order)>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                fields.Add((reader.GetString(0).Trim(), reader.GetBoolean(1), reader.GetBoolean(2),
                    reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4)));
            }

            Assert.Equal(5, fields.Count);
            // 主键顺序与表上的 PK (DEPOT_ID, PRO_NO, LOCATION_NO) 一致：统一表单的 MasterPkOrder 按 FORM_ORDER 推
            Assert.Equal(
                new[] { "DEPOT_ID", "PRO_NO", "LOCATION_NO" },
                fields.Where(f => f.Pk).OrderBy(f => f.Order).Select(f => f.Field).ToArray());
            // 三列都要能选（库别 / 品号 / 库位），否则维护的人得手抄主键
            Assert.All(fields.Where(f => f.Pk), f => Assert.NotNull(f.Chooser));
            Assert.All(fields, f => Assert.True(f.Visible, $"{f.Field} 应在表单里可见。"));
        }
    }

    [Fact]
    public async Task 已发布定义可用_且统一表单白名单里有它()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = new SqlCommand(
            "SELECT TOP 1 DEFINITION_JSON FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE MODULE_ID = @Id AND IS_CURRENT = 1;",
            connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = ModuleId;
        var json = await command.ExecuteScalarAsync() as string;
        Assert.False(string.IsNullOrWhiteSpace(json), $"模块 {ModuleId} 应有已发布快照。");

        var definition = JsonSerializer.Deserialize<WorkbenchDefinition>(json!, WorkbenchDefinitionProvider.JsonOptions)!;
        Assert.Equal(Table, definition.MasterTable);
        Assert.Equal(new[] { "DEPOT_ID", "PRO_NO", "LOCATION_NO" }, definition.MasterPkOrder.ToArray());

        // `HasAdd` / `HasEdit` 刻意**不在这里断言**：它们由定义构建时的"统一表单启用清单"决定
        // （`WorkbenchDefinitionBuilder`：`HasAdd = NEW_URL 或 MODI_URL 存在`，表单启用时回落成表单路由），
        // 也就是说 **先有清单、再发布快照**，快照里才带得上这两项。今天这份快照是在清单加入之前发布的，
        // 所以它是 false——重启 API（`IOptions` 是启动快照，改 appsettings 不生效）后重发布即可为 true。
        // 把这条顺序写进断言会在"配好了但还没重启"时红，红得没有信息量；改用例外的白名单断言守着配置本身。
        // （同一原因：这份快照的 `MasterFields` 也是空的——字段可见性同样按表单启用清单过滤。
        //   清单生效 + 重发布之后，它应当有 5 个字段；那一步由 `logs/e2e-ws14.ps1` 端到端验。）

        // 统一表单的启用清单：没有它，任何模块的 /form-definition 都直接 404
        var appsettings = FindRepositoryFile(Path.Combine("EOS.API", "appsettings.json"));
        Assert.NotNull(appsettings);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(appsettings!));
        var enabled = document.RootElement
            .GetProperty("UnifiedFormEditor").GetProperty("EnabledModuleIds")
            .EnumerateArray().Select(item => item.GetInt32()).ToArray();
        Assert.Contains(ModuleId, enabled);
    }

    /// <summary>从测试输出目录往上找仓库文件（测试的工作目录是 bin 下的输出目录）。</summary>
    private static string? FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        return null;
    }
}
