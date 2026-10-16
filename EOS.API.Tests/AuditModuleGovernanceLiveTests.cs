using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 操作审计（模块 2316）承载形态的真库验收：它是统一工作台的**只读**承载页
/// （主表 `AUDIT_EVENT`、子表 `AUDIT_FIELD_CHANGE`），审计记录只能由系统写入。
///
/// 为什么值得单独钉：这三样都不会编译报错、也不会让页面白屏，但都会让审计面塌掉——
///   ① 形态（主表/子表/默认排序/节点形态）：少一样就是"菜单点进去空列表"或"按主键升序看最老的记录"；
///   ② 字段元数据（含默认列两侧同口径）：缺列等于界面上看不见，两处默认列不一致则"选择列"弹窗
///      与实际渲染对不上；
///   ③ 不得配业务动作：只读名单仍会渲染自定义按钮，配一条就等于给审计表开一个可执行的写入口。
/// 「2316 在只读名单、不在写名单」是代码侧判据，由离线的
/// <c>StaticArchitectureGuardTests.审计模块不得进统一表单写名单</c> 守着（那条进 CI）。
///
/// 连接串缺失时跳过：按用例纪律，'没有连接串'属于环境缺失（在别人机器上确实会为假），
/// 而"审计模块的形态与元数据"是数据前提，故下面一律断言。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class AuditModuleGovernanceLiveTests
{
    private const int AuditModuleId = 2316;
    private const string MasterTable = "AUDIT_EVENT";
    private const string DetailTable = "AUDIT_FIELD_CHANGE";

    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    /// <summary>关键字搜索覆盖的文本/时间列：工作台按 IS_QUERY 取候选，再按类型过滤（见用例内注释）。</summary>
    private static readonly string[] SearchableColumns =
    [
        "OCCURRED_AT", "ACTOR_USER_ID", "ACTOR_DISPLAY_NAME", "CLIENT_IP", "ACTION",
        "RESOURCE_TYPE", "RESOURCE_KEY", "CORRELATION_ID", "TRACE_ID", "ERROR_CODE", "SUMMARY",
    ];

    [Fact]
    public async Task 模块是统一工作台只读承载_主表子表与默认排序就位()
    {
        if (ConnectionString.Value is null) return;
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        await using (var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(ISNULL(M_URL, N''))), LTRIM(RTRIM(ISNULL(MASTER_TABLE, N''))),
                   LTRIM(RTRIM(ISNULL(DETAIL_TABLE, N''))), LTRIM(RTRIM(ISNULL(SORT_FIELDS, N''))),
                   CAST(ISNULL(M_TAG, 0) AS bit), LTRIM(RTRIM(ISNULL(FILTER, N''))),
                   CAST(ISNULL(IF_COPY, 0) AS bit), CAST(ISNULL(AUTO_APPROVE, 0) AS bit),
                   CAST(ISNULL(UNIFIED_FORM_ENABLED, 0) AS bit), CAST(ISNULL(UNIFIED_FORM_EDIT_ENABLED, 0) AS bit)
            FROM dbo.MODULES WITH (NOLOCK) WHERE M_IDX = @Id;
            """, connection))
        {
            command.Parameters.Add("@Id", System.Data.SqlDbType.Int).Value = AuditModuleId;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync(), $"模块 {AuditModuleId} 应存在（迁移 353 登记、354 改判）。");
            Assert.Equal("/workbench", reader.GetString(0));
            Assert.Equal(MasterTable, reader.GetString(1));
            Assert.Equal(DetailTable, reader.GetString(2));
            // 默认排序：审计要先看最新的。写成主键升序的话第一屏是最老的记录
            Assert.Equal("AUDIT_EVENT.EVENT_ID DESC", reader.GetString(3));
            // 统一表单能力（迁移 355 起是模块声明，现读）：表单可用（要能双击进浏览态看字段级变更），
            // 但**不允许写入**——审计记录只由审计写入器追加，通用表单写入必须封死。
            Assert.True(reader.GetBoolean(8), "审计模块应声明统一表单可用（否则双击进不去浏览态）。");
            Assert.False(reader.GetBoolean(9), "审计模块不该允许通用表单写入：AUDIT_EVENT 只能由审计写入器追加。");
            Assert.True(reader.GetBoolean(4), "模块应在菜单里可见。");
            // 不加模块级行过滤：审计要看得全（行级 DATA_FILTER 是权限面的事，不在模块上写死）
            Assert.Equal(string.Empty, reader.GetString(5));
            // 只读承载：复制与自动批核都没有意义
            Assert.False(reader.GetBoolean(6));
            Assert.False(reader.GetBoolean(7));
        }

        // 节点形态判据只有视图一处（迁移 340）：工作台模块才装配定义与列表
        await using (var command = new SqlCommand(
            "SELECT NODE_KIND FROM dbo.V_MODULE_NODE WITH (NOLOCK) WHERE M_IDX = @Id;", connection))
        {
            command.Parameters.Add("@Id", System.Data.SqlDbType.Int).Value = AuditModuleId;
            Assert.Equal("WORKBENCH", (await command.ExecuteScalarAsync()) as string);
        }
    }

    [Fact]
    public async Task 默认排序的列必须是已登记的非虚拟字段()
    {
        if (ConnectionString.Value is null) return;
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        // NormalizeSort 只认已登记的非虚拟字段：声明的排序键若对不上，定义装配会静默退回主键升序
        // （旧记录排在最前），界面看不出错，所以要在这里对一次账。
        await using var command = new SqlCommand("""
            SELECT COUNT(*) FROM dbo.FIELDS WITH (NOLOCK)
             WHERE LTRIM(RTRIM(T_ID)) = @Table AND LTRIM(RTRIM(F_ID)) = N'EVENT_ID'
               AND COALESCE(IS_VIRTUAL, 0) = 0 AND COALESCE(IS_VISIBLE, 1) = 1;
            """, connection);
        command.Parameters.Add("@Table", System.Data.SqlDbType.NVarChar, 50).Value = MasterTable;
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task 搜索与查询条件的候选列就位_内部列不暴露()
    {
        if (ConnectionString.Value is null) return;
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        // IS_QUERY=1 = 进"查询条件"面；工作台的关键字谓词再按类型过滤（字符列 + 日期列），
        // 所以事件号 / 模块号 / 结果这类数值列可以当条件（"只看失败"就靠它），
        // 但不会进关键字搜索——它们对任意数字子串都命中，放进去只会把结果淹掉。
        var queryable = new List<string>();
        await using (var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(F_ID)) FROM dbo.FIELDS WITH (NOLOCK)
             WHERE LTRIM(RTRIM(T_ID)) = @Table AND COALESCE(IS_QUERY, 0) = 1 AND COALESCE(IS_VISIBLE, 1) = 1
             ORDER BY F_ID;
            """, connection))
        {
            command.Parameters.Add("@Table", System.Data.SqlDbType.NVarChar, 50).Value = MasterTable;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) queryable.Add(reader.GetString(0));
        }

        // 该在的都在（含时间列：搜 2026-10-15 要能命中那一天的记录）
        foreach (var column in SearchableColumns)
            Assert.Contains(column, queryable);

        // 内部/技术列不做候选：对使用者没有检索意义，列进去只是噪声
        foreach (var internalColumn in new[] { "ACTOR_TYPE", "CLIENT_TYPE", "DEFINITION_VERSION", "DETAIL_JSON" })
            Assert.DoesNotContain(internalColumn, queryable);
    }

    [Fact]
    public async Task 字段元数据齐备_默认列两处同口径()
    {
        if (ConnectionString.Value is null) return;
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        // 物理列一列都不能漏登记：审计表由写入器维护，加了列而元数据没跟上，
        // 表现是"库里查得到、界面看不见"（列表、表单、关键字搜索都读字段元数据）。
        var unregistered = new List<string>();
        await using (var command = new SqlCommand("""
            SELECT c.name FROM sys.columns c
             JOIN sys.objects o ON o.object_id = c.object_id
             JOIN sys.schemas s ON s.schema_id = o.schema_id
             WHERE s.name = N'dbo' AND o.name = @Table
               AND NOT EXISTS (SELECT 1 FROM dbo.FIELDS f WITH (NOLOCK)
                                WHERE LTRIM(RTRIM(f.T_ID)) = @Table AND LTRIM(RTRIM(f.F_ID)) = c.name)
             ORDER BY c.name;
            """, connection))
        {
            command.Parameters.Add("@Table", System.Data.SqlDbType.NVarChar, 50).Value = MasterTable;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) unregistered.Add(reader.GetString(0));
        }
        Assert.True(unregistered.Count == 0,
            $"AUDIT_EVENT 有未登记进字段目录的物理列：{string.Join("、", unregistered)}");

        // 时间列必须带显示格式：没有格式时前端只显示年月日，审计要的是到秒
        await using (var command = new SqlCommand("""
            SELECT LTRIM(RTRIM(ISNULL(DISPLAY_FORMAT, N''))) FROM dbo.FIELDS WITH (NOLOCK)
             WHERE LTRIM(RTRIM(T_ID)) = @Table AND LTRIM(RTRIM(F_ID)) = N'OCCURRED_AT';
            """, connection))
        {
            command.Parameters.Add("@Table", System.Data.SqlDbType.NVarChar, 50).Value = MasterTable;
            Assert.Equal("yyyy-MM-dd HH:mm:ss", (await command.ExecuteScalarAsync()) as string);
        }

        // 默认列两侧同口径：FIELDS.IS_DEFAULT_FIELDS 决定列表初始列，
        // SYSQL_DEFAULT 是"选择列"弹窗与默认列配置面的读数来源；不一致时两处对不上。
        var fieldDefaults = await ReadSetAsync(connection, """
            SELECT LTRIM(RTRIM(F_ID)) FROM dbo.FIELDS WITH (NOLOCK)
             WHERE LTRIM(RTRIM(T_ID)) = @Table AND COALESCE(IS_DEFAULT_FIELDS, 0) = 1;
            """, MasterTable);
        var sqlDefaults = await ReadSetAsync(connection, """
            SELECT LTRIM(RTRIM(F_ID)) FROM dbo.SYSQL_DEFAULT WITH (NOLOCK)
             WHERE T_ID = @Table AND T_ID_R = @Table;
            """, MasterTable);
        Assert.Equal(10, fieldDefaults.Count);
        Assert.Equal(fieldDefaults.OrderBy(x => x), sqlDefaults.OrderBy(x => x));

        // 子表也要有默认列（字段级变更：字段 / 原值 / 新值）
        var detailDefaults = await ReadSetAsync(connection, """
            SELECT LTRIM(RTRIM(F_ID)) FROM dbo.SYSQL_DEFAULT WITH (NOLOCK)
             WHERE T_ID = @Table AND T_ID_R = @Detail;
            """, MasterTable, DetailTable);
        Assert.Equal(["FIELD_NAME", "NEW_VALUE", "OLD_VALUE"], detailDefaults.OrderBy(x => x));
    }

    [Fact]
    public async Task 审计模块不得配业务动作()
    {
        if (ConnectionString.Value is null) return;
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        // 只读名单封的是通用表单的写端点（新增/修改/删除一律 404），**自定义按钮不受它管辖**：
        // 只读模块照样渲染 MODULE_BUSINESS_ACTION 里的 MANUAL 行，按下去就跑存储过程/效果链。
        // 审计表上开这个口子等于"配置即可写"，故一行都不许有。
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.MODULE_BUSINESS_ACTION WHERE M_IDX = @Id;", connection);
        command.Parameters.Add("@Id", System.Data.SqlDbType.Int).Value = AuditModuleId;
        var count = Convert.ToInt32(await command.ExecuteScalarAsync());
        Assert.True(count == 0,
            $"模块 {AuditModuleId} 配了 {count} 行业务动作：审计记录不可执行任何动作，请清掉再提交。");
    }

    private static async Task<HashSet<string>> ReadSetAsync(
        SqlConnection connection, string sql, string table, string? detailTable = null)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Table", System.Data.SqlDbType.NVarChar, 50).Value = table;
        if (detailTable is not null)
            command.Parameters.Add("@Detail", System.Data.SqlDbType.NVarChar, 50).Value = detailTable;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = Regex.Match(text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
            return match.Success && match.Groups[1].Value.Contains("Database=EOS.ERP")
                ? match.Groups[1].Value
                : null;
        }
        catch
        {
            return null;
        }
    }
}
