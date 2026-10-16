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
/// 但它们的界面入口并非不存在：承载页（`MODULES.M_URL`）是统一工作台，且挂着一批
/// 用户点击触发的自定义按钮（改人工字段 / 冻结 / 解冻 / 预留 / 释放）。三者必须同时成立，
/// 缺任何一件，"有入口"或"没有入口"就只是说法：
///   ① 能力侧——模块声明"统一表单可用但**不可写**"（`MODULES.UNIFIED_FORM_ENABLED = 1` 且
///      `UNIFIED_FORM_EDIT_ENABLED = 0`，迁移 355 起能力进库、现读）：表单能打开（浏览态可进），
///      新增/修改/删除端点仍 404；
///   ② 路由侧——`M_URL` 指向统一工作台（否则列表双击没有目的地）；
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
    public async Task 统一表单仅浏览_写路径仍然关闭(int moduleId, string _)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT ISNULL(UNIFIED_FORM_ENABLED, 0), ISNULL(UNIFIED_FORM_EDIT_ENABLED, 0)
            FROM dbo.MODULES WHERE M_IDX = @Id;
            """, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), $"模块 {moduleId} 应存在。");
        // 表单要能打开（浏览态可进、自定义按钮可点），写入必须封死（通用表单写入会绕过移动引擎）
        Assert.True(reader.GetBoolean(0), $"模块 {moduleId} 应声明统一表单可用，否则双击进不去浏览态。");
        Assert.False(reader.GetBoolean(1), $"模块 {moduleId} 不该允许通用表单写入：主表由移动引擎维护。");
    }

    [Theory]
    [MemberData(nameof(ReadOnlyFormModules))]
    public async Task 模块路由仍指向统一表单_且挂着的自定义按钮有宿主(int moduleId, string masterTable)
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using (var command = new SqlCommand(
            "SELECT RTRIM(MASTER_TABLE), ISNULL(M_URL, '') FROM dbo.MODULES WHERE M_IDX = @Id;",
            connection))
        {
            command.Parameters.Add("@Id", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"模块 {moduleId} 应存在。");
            Assert.Equal(masterTable, reader.GetString(0));
            // 列表双击进浏览态靠它：承载页必须是统一工作台（清空即等于把入口摘掉——
            // 浏览态与自定义按钮都无处渲染）。新增/修改路由字段已随迁移 321 删除。
            Assert.StartsWith("/workbench", reader.GetString(1));
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

}
