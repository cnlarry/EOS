using System.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 冻结 / 预留两表的结构口径与可用量初始化的真库用例（只读断言，不改数据）。
///
/// 覆盖三条判据：
///   ⒜ **维度键与库存行同维**——冻结/预留落在 (料号, 库别, 库位, 批次) 上，且四列的类型与长度
///      与 `INV_PRO_DEPOT` 逐列一致：少一列或类型不同，"冻结了多少"与"该键还剩多少"就对不上；
///   ⒝ **建表口径**——状态位与建立组必须 NOT NULL + 默认值（可空的状态位会让"未设置"与"已取消"混为一谈）；
///   ⒞ **可用量已初始化**——存在非零可用量，且没有一行是"从未初始化"（NULL）。
///      判别性：不做初始化时全库 1797 行瞬时可用量全是 0 ⇒ 本用例第一条断言即红，
///      而"全库可用量为 0"正是明令禁止的形态。
///
/// **口径边界（本用例不断言的东西）**：`USEABLE_QTY` 目前只有初始值，库存移动引擎写余额行时
/// **不维护**它（全仓无一处代码引用该列）。所以"数量非零 ⇒ 可用量等于数量"这个恒等式**今天不成立**，
/// 不能写成常驻断言——它要等可用量服务与出库校验落地（冻结/预留的入口与消费）才谈得上维持。
/// 本用例只断言"初始化确实发生过"，不假装不变量已经存在。
/// </summary>
[Collection("live-database")]
public sealed class InventoryFreezeReserveSchemaLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    private static readonly string[] DimensionKeys = ["PRO_NO", "DEPOT_ID", "LOCATION_NO", "BATCH_NO"];

    [Theory]
    [InlineData("INV_FREEZE")]
    [InlineData("INV_RESERVE")]
    public async Task 冻结预留表_维度键与库存行同维(string table)
    {
        await using var connection = await OpenAsync();

        Assert.True(await TableExistsAsync(connection, table), $"缺少表 dbo.{table}。");

        var balanceColumns = await ReadColumnsAsync(connection, "INV_PRO_DEPOT", DimensionKeys);
        var targetColumns = await ReadColumnsAsync(connection, table, DimensionKeys);
        foreach (var key in DimensionKeys)
        {
            Assert.True(targetColumns.ContainsKey(key), $"dbo.{table} 缺少维度键列 {key}。");
            Assert.Equal(balanceColumns[key], targetColumns[key]);
        }

        // 主键必须覆盖四键 + 来源单据：来源不同（不同单据的冻结）不能互相覆盖
        var primaryKey = await ReadPrimaryKeyAsync(connection, table);
        Assert.Equal(
            new[] { "PRO_NO", "DEPOT_ID", "LOCATION_NO", "BATCH_NO", "SOURCE_TYPE", "SOURCE_NO" },
            primaryKey);
    }

    [Theory]
    [InlineData("INV_FREEZE")]
    [InlineData("INV_RESERVE")]
    public async Task 冻结预留表_状态位与建立组不可空且有默认值(string table)
    {
        await using var connection = await OpenAsync();

        await using var command = new SqlCommand("""
            SELECT c.name, c.is_nullable, CASE WHEN c.default_object_id = 0 THEN 0 ELSE 1 END AS has_default
              FROM sys.columns c
             WHERE c.object_id = OBJECT_ID('dbo.' + @table)
               AND c.name IN (N'STATUS', N'CREATE_PERSON', N'CREATE_DATE');
            """, connection);
        command.Parameters.Add("@table", SqlDbType.NVarChar, 60).Value = table;

        await using var reader = await command.ExecuteReaderAsync();
        var checkedNames = new List<string>();
        while (await reader.ReadAsync())
        {
            var name = reader.GetString(0);
            checkedNames.Add(name);
            // CASE 表达式是 INT 而不是 BIT，按值转换读，不直接 GetBoolean
            var nullable = Convert.ToInt32(reader.GetValue(1)) == 1;
            var hasDefault = Convert.ToInt32(reader.GetValue(2)) == 1;
            Assert.False(nullable, $"dbo.{table}.{name} 可空：状态位/建立组必须 NOT NULL。");
            Assert.True(hasDefault, $"dbo.{table}.{name} 没有默认值。");
        }
        Assert.Equal(new[] { "CREATE_DATE", "CREATE_PERSON", "STATUS" }, checkedNames.OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task 可用量已初始化_全库不再出现可用量为零的形态()
    {
        await using var connection = await OpenAsync();

        await using var command = new SqlCommand("""
            SELECT COUNT(*) AS TotalRows,
                   SUM(CASE WHEN USEABLE_QTY IS NULL THEN 1 ELSE 0 END) AS NullUseable,
                   SUM(CASE WHEN ISNULL(USEABLE_QTY, 0) <> 0 THEN 1 ELSE 0 END) AS NonZeroUseable
              FROM dbo.INV_PRO_DEPOT;
            """, connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var total = reader.GetInt32(0);
        var nullUseable = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetValue(1));
        var nonZero = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetValue(2));

        Assert.Equal(0, nullUseable);
        // 初始化之前这一条是 0（1797 行全是 0），正是被禁止的"全库可用量为零"形态
        Assert.True(nonZero > 0, $"可用量未初始化：{total} 行余额里没有任何一行可用量非零。");
    }

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string table)
    {
        await using var command = new SqlCommand(
            "SELECT CASE WHEN OBJECT_ID('dbo.' + @table, 'U') IS NULL THEN 0 ELSE 1 END;", connection);
        command.Parameters.Add("@table", SqlDbType.NVarChar, 60).Value = table;
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    /// <summary>列签名（类型 + 长度 + 可空性）：两侧逐列比，"同维"才算成立。</summary>
    private static async Task<Dictionary<string, string>> ReadColumnsAsync(
        SqlConnection connection, string table, IReadOnlyList<string> names)
    {
        await using var command = new SqlCommand("""
            SELECT c.name, CONCAT(TYPE_NAME(c.user_type_id), '/', c.max_length, '/', c.is_nullable)
              FROM sys.columns c
             WHERE c.object_id = OBJECT_ID('dbo.' + @table) AND c.name IN (@k0, @k1, @k2, @k3);
            """, connection);
        command.Parameters.Add("@table", SqlDbType.NVarChar, 60).Value = table;
        for (var index = 0; index < names.Count; index++)
            command.Parameters.Add($"@k{index}", SqlDbType.NVarChar, 60).Value = names[index];

        await using var reader = await command.ExecuteReaderAsync();
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync())
            columns[reader.GetString(0).Trim()] = reader.GetString(1);
        return columns;
    }

    private static async Task<string[]> ReadPrimaryKeyAsync(SqlConnection connection, string table)
    {
        await using var command = new SqlCommand("""
            SELECT c.name
              FROM sys.index_columns ic
              JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
              JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
             WHERE ic.object_id = OBJECT_ID('dbo.' + @table)
               AND i.is_primary_key = 1 AND ic.is_included_column = 0
             ORDER BY ic.key_ordinal;
            """, connection);
        command.Parameters.Add("@table", SqlDbType.NVarChar, 60).Value = table;

        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>();
        while (await reader.ReadAsync()) columns.Add(reader.GetString(0).Trim());
        return [.. columns];
    }
}
