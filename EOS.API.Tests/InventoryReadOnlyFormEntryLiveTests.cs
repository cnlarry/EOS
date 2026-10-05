using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 库存余额 / 批次账主档的**只读表单入口**（真库 + 配置双面断言）。
///
/// 背景：模块 `1302 料件批号资料`（`INV_BATCH_M`）与 `1303 料件库存资料`（`INV_PRO_DEPOT`）
/// 的主表由库存移动引擎维护，通用表单写入会绕过引擎直接改账，所以它们**不在写名单**里。
/// 但它们的界面入口并非不存在：`MODULES.MODI_URL` 仍指向统一表单编辑模板，且挂着一批
/// 用户点击触发的自定义按钮（改人工字段 / 冻结 / 解冻 / 预留 / 释放）。三者必须同时成立，
/// 缺任何一件，"有入口"或"没有入口"就只是说法：
///   ① 配置侧——模块在 `UnifiedFormEditor.ReadOnlyModuleIds` 内、且**不在** `EnabledModuleIds` 内
///      （在只读名单 = 浏览态可进；不在写名单 = 新增/修改/删除端点仍 404）；
///   ② 路由侧——`MODI_URL` 指向统一表单动作模板（否则列表双击没有目的地）；
///   ③ 动作侧——模块上仍有 `MANUAL` 动作行（自定义按钮的宿主，只有浏览态工具栏渲染它们）。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class InventoryReadOnlyFormEntryLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    /// <summary>只读表单模块 → 主表：余额表 1303、批次账 1302。</summary>
    public static TheoryData<int, string> ReadOnlyFormModules => new()
    {
        { 1303, "INV_PRO_DEPOT" },
        { 1302, "INV_BATCH_M" },
    };

    [Theory]
    [MemberData(nameof(ReadOnlyFormModules))]
    public void 只读名单放行浏览_写名单仍然关闭(int moduleId, string _)
    {
        using var document = ReadAppSettings();
        var section = document.RootElement.GetProperty("UnifiedFormEditor");
        var readOnly = section.GetProperty("ReadOnlyModuleIds").EnumerateArray().Select(item => item.GetInt32()).ToArray();
        var enabled = section.GetProperty("EnabledModuleIds").EnumerateArray().Select(item => item.GetInt32()).ToArray();

        Assert.Contains(moduleId, readOnly);
        Assert.DoesNotContain(moduleId, enabled);
    }

    [Theory]
    [MemberData(nameof(ReadOnlyFormModules))]
    public async Task 模块路由仍指向统一表单_且挂着的自定义按钮有宿主(int moduleId, string masterTable)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var command = new SqlCommand(
            "SELECT RTRIM(MASTER_TABLE), ISNULL(MODI_URL, ''), ISNULL(M_URL, '') FROM dbo.MODULES WHERE M_IDX = @Id;",
            connection))
        {
            command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"模块 {moduleId} 应存在。");
            Assert.Equal(masterTable, reader.GetString(0));
            // 列表双击进浏览态靠它：清空即等于把入口摘掉（浏览态与自定义按钮都无处渲染）
            Assert.Contains("{moduleId}", reader.GetString(1));
            Assert.StartsWith("/workbench", reader.GetString(2));
        }

        await using (var command = new SqlCommand("""
            SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION
             WHERE M_IDX = @Id AND RTRIM(EVENT_CODE) = N'MANUAL' AND ENABLED = 1;
            """, connection))
        {
            command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
            var count = Convert.ToInt32(await command.ExecuteScalarAsync());
            Assert.True(count > 0, $"模块 {moduleId} 的自定义按钮（MANUAL 动作行）不见了：浏览态就没有可点的单据级动作。");
        }
    }

    /// <summary>读 EOS.API 的 appsettings.json（它随项目引用复制到测试输出目录）。</summary>
    private static JsonDocument ReadAppSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        Assert.True(File.Exists(path), $"测试输出目录应带 EOS.API 的 appsettings.json：{path}");
        return JsonDocument.Parse(File.ReadAllText(path));
    }
}
