using EOS.API.Data;
using EOS.API.Features.Assistant.Parameters;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 助手参数目录与真库的**一致性门禁**（ADR-030 §9 断言 2 的连库部分）。
///
/// <para>
/// 为什么是单测而不是 PowerShell 脚本：这条断言要拿目录的**对象**逐字段比对，脚本只能去解析 C# 源码，
/// 那是把编译期事实降级成文本匹配。真库单测直接引用 <see cref="AssistantParameterCatalog"/>，
/// 比对的是同一份声明。
/// </para>
///
/// <para>
/// 它挡住的漂移是：改了目录的默认值 / 类型 / 分组却没跟迁移，或者库里被人手改过定义——
/// 表现会是"界面显示 5 元、代码其实是 8 元"，而那是最难查的一类问题。
/// 连接串与跳过策略沿用 <see cref="AssistantRepositoryIntegrationTests"/>（没有
/// <c>MSSQL_ERP_CONN</c> 时静默跳过，不把缺库变成红灯）。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class AssistantParameterIntegrationTests
{
    private static readonly Lazy<string?> ConnectionString =
        new(() => Environment.GetEnvironmentVariable("MSSQL_ERP_CONN"));

    /// <summary>
    /// 前置迁移：本组断言比对的是**迁移后的库**，所以先跑一次与生产同一套 DbUp 脚本
    /// （沿用 <see cref="AssistantAdminRepositoryIntegrationTests.EnsureSchema"/> 的做法）。
    /// 否则"没跑过迁移"会被读成"参数没落库"，把一次环境未就绪报成一次真实的不一致。
    /// </summary>
    public AssistantParameterIntegrationTests()
    {
        var connectionString = ConnectionString.Value;
        if (connectionString is null) return;

        var result = DbUp.DeployChanges.To
            .SqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                typeof(ErpDatabaseInitializer).Assembly,
                name => name.Contains(".Data.Migrations.", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithTransactionPerScript()
            .JournalToSqlTable("dbo", "ERP_SCHEMA_JOURNAL")
            .LogToConsole()
            .Build()
            .PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException("测试前置：助手参数的迁移未成功（迁移 294 等）。", result.Error);
        }
    }

    /// <summary>
    /// 作用域查询只带回**当前当事人**的两层：他自己的用户覆盖、当前模块的模块覆盖。
    /// 别人的用户行、别的模块的行**都不回来**。
    ///
    /// <para>
    /// 过滤发生在 <see cref="AssistantParameterScopeStore.ListForAsync"/> 的 SQL <c>WHERE</c> 里，
    /// 而不是"读回来再筛"——后者等于把"别人被单独设过什么"带进了这次请求的内存，
    /// 而这一层的意义正在于它只读该读的。这条断言盯的就是那两行带括号的谓词。
    /// </para>
    ///
    /// <para>
    /// 走<b>写</b>路径造数据（而不是直接 INSERT）：写路径与生效路径共用同一份判断，
    /// 用 SQL 造假数据会绕开"这层能不能被覆盖"的校验，测出来的就不是真实形态了。
    /// 造出来的行在 finally 里清干净——这台库是开发库，不留痕。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scope_Query_Returns_Only_The_Requested_Module_And_User()
    {
        if (ConnectionString.Value is null) return;

        var store = ScopeStore();
        var token = CancellationToken.None;
        // 模块号取**不存在**的随机值（避免碰到真实模块的配置），并且必须是纯数字——
        // 模块层在权限模型里是 int，用带十六进制字母的随机串会在这里就抛 FormatException。
        var moduleId = Random.Shared.Next(9_000_000, 9_999_999);
        var otherModuleId = Random.Shared.Next(8_000_000, 8_999_999);
        var moduleKey = moduleId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var otherModuleKey = otherModuleId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var userId = "t-" + Guid.NewGuid().ToString("N")[..8];
        var otherUserId = userId + "-x";

        try
        {
            // 直接 INSERT 造行：本组断言测的是**读侧的过滤**（那两行带括号的谓词），
            // 写侧的校验另有离线断言（AssistantParameterScopeTests）与它自己的集成用例。
            // 走 Upsert 写会连带把审计链路一起拉进来（审计写入器需要请求上下文），
            // 那测的就不是这件事了——测试的失败原因必须与被测行为对得上。
            await SeedAsync(
                (AssistantParameterScopeRules.Module, moduleKey, "ACTION_DELETE", "0"),
                (AssistantParameterScopeRules.User, userId, "USER_DAILY_CAP_YUAN", "8"),
                // 下面两行是**不该被读出来**的：别人的用户覆盖、别的模块的模块覆盖
                (AssistantParameterScopeRules.User, otherUserId, "USER_DAILY_CAP_YUAN", "9"),
                (AssistantParameterScopeRules.Module, otherModuleKey, "ACTION_DELETE", "0"));

            var rows = await store.ListForAsync(userId, moduleId, token);

            // 断言按**身份**判，不按条数判：这张表是共享的开发库，别的用例可能同时往里写别的键，
            // "恰好两条"这种断言会把别人的行为算到自己头上（本组一开始就是这么假红的）。
            // 而身份断言更强：`@User` 与 `@Module` 两个参数若接反，下面四条会同时失败——
            // 接反时该回来的不回来、不该回来的（别人的键）反而回来。
            Assert.Contains(
                rows,
                row => row.ScopeType == AssistantParameterScopeRules.Module
                    && row.ScopeKey == moduleKey
                    && row.ParamKey == "ACTION_DELETE");
            Assert.Contains(
                rows,
                row => row.ScopeType == AssistantParameterScopeRules.User
                    && row.ScopeKey == userId
                    && row.ParamKey == "USER_DAILY_CAP_YUAN");
            Assert.DoesNotContain(rows, row => row.ScopeKey == otherUserId);
            Assert.DoesNotContain(rows, row => row.ScopeKey == otherModuleKey);

            // 只给用户、不给模块时，模块层一条都不该回来（反过来同理）
            Assert.DoesNotContain(
                await store.ListForAsync(userId, null, token),
                row => row.ScopeType == AssistantParameterScopeRules.Module);
            Assert.DoesNotContain(
                await store.ListForAsync(null, moduleId, token),
                row => row.ScopeType == AssistantParameterScopeRules.User);
        }
        finally
        {
            await CleanupAsync(moduleKey, userId, otherModuleKey, otherUserId);
        }
    }

    /// <summary>
    /// 管理列表要带**显示名**，且名字来自 JOIN——"1401 / 客户订单"能认，光一个"1401"认不出是谁。
    ///
    /// <para>
    /// 还要断言**解析不到时留空**：模块被删、账号没登记姓名时，把号当名字显示等于假装解析成功了，
    /// 而界面上根本看不出这是"名字"还是"号"。
    /// </para>
    ///
    /// <para>
    /// 真实对象上若已有同样的覆盖行，说明那是**真实配置**——用例直接让位，不改写它
    /// （测试造的数据只允许是自己造的那几行）。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scope_List_Resolves_Display_Names_From_Master_Tables()
    {
        if (ConnectionString.Value is null) return;

        var module = await PickModuleWithNameAsync();
        var user = await PickUserWithNameAsync();
        // 前置是**数据事实**（这台库里每个模块都有名字、账号都登记了姓名），不是"环境缺失"：
        // 拿不到就当场红。写成 return 会让"根本没跑到断言"与"断言通过"长得一模一样——
        // 这条用例的第一次运行就是靠手工查库才确认它真的跑了。
        Assert.True(module.HasValue, "库里应当有带名字的模块（MODULES.M_DESC 非空）。");
        Assert.True(user.HasValue, "库里应当有登记了姓名的账号（SYSDN.EMP_NAME 非空）。");

        var moduleKey = module.Value.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var ghostModuleKey = Random.Shared.Next(9_000_000, 9_999_999)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (await ScopeRowExistsAsync(AssistantParameterScopeRules.Module, moduleKey, "ACTION_DELETE")) return;
        if (await ScopeRowExistsAsync(AssistantParameterScopeRules.User, user.Value.UserId, "USER_DAILY_CAP_YUAN")) return;

        try
        {
            await SeedAsync(
                (AssistantParameterScopeRules.Module, moduleKey, "ACTION_DELETE", "0"),
                (AssistantParameterScopeRules.User, user.Value.UserId, "USER_DAILY_CAP_YUAN", "8"),
                // 不存在的模块：LEFT JOIN 落空，名字必须是空的
                (AssistantParameterScopeRules.Module, ghostModuleKey, "ACTION_DELETE", "0"));

            var rows = await ScopeStore().ListAllAsync(CancellationToken.None);

            var moduleRow = rows.First(row =>
                row.ScopeType == AssistantParameterScopeRules.Module && row.ScopeKey == moduleKey);
            Assert.Equal(module.Value.Name, moduleRow.Label);

            var userRow = rows.First(row =>
                row.ScopeType == AssistantParameterScopeRules.User && row.ScopeKey == user.Value.UserId);
            Assert.Equal(user.Value.Name, userRow.Label);

            var ghostRow = rows.First(row =>
                row.ScopeType == AssistantParameterScopeRules.Module && row.ScopeKey == ghostModuleKey);
            Assert.Null(ghostRow.Label);
        }
        finally
        {
            await CleanupAsync(moduleKey, user.Value.UserId, ghostModuleKey);
        }
    }

    private static async Task<(int Id, string Name)?> PickModuleWithNameAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT TOP 1 M_IDX, LTRIM(RTRIM(ISNULL(M_DESC, N'')))
            FROM dbo.MODULES WITH (NOLOCK)
            WHERE LTRIM(RTRIM(ISNULL(M_DESC, N''))) <> N''
            ORDER BY M_IDX;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetInt32(0), reader.GetString(1)) : null;
    }

    private static async Task<(string UserId, string Name)?> PickUserWithNameAsync()
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT TOP 1 LTRIM(RTRIM(l.USER_ID)), LTRIM(RTRIM(n.EMP_NAME))
            FROM dbo.SYSDL l WITH (NOLOCK)
            JOIN dbo.SYSDN n WITH (NOLOCK) ON l.EMP_ID = n.EMP_ID
            WHERE LTRIM(RTRIM(ISNULL(n.EMP_NAME, N''))) <> N''
            ORDER BY l.USER_ID;
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    /// <summary>这一行覆盖是否**真实存在**（存在就不许动）：先问库，再决定要不要造数据。</summary>
    private static async Task<bool> ScopeRowExistsAsync(string scopeType, string scopeKey, string paramKey)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            SELECT COUNT(1) FROM dbo.ASSISTANT_PARAM_SCOPE WITH (NOLOCK)
            WHERE SCOPE_TYPE = @Type AND SCOPE_KEY = @Key AND PARAM_KEY = @Param;
            """, connection);
        command.Parameters.AddWithValue("@Type", scopeType);
        command.Parameters.AddWithValue("@Key", scopeKey);
        command.Parameters.AddWithValue("@Param", paramKey);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) > 0;
    }

    /// <summary>
    /// 供本组断言用的作用域存储。审计写入器只在**写**路径上被用到，读路径不碰它——
    /// 于是这里传一个"不该被用到"的实参，若哪天读路径真的写了审计，这条会当场炸。
    /// </summary>
    private static AssistantParameterScopeStore ScopeStore()
    {
        var connections = new DbConnectionFactory(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:ErpDatabase"] = ConnectionString.Value,
                })
                .Build());
        return new AssistantParameterScopeStore(connections, auditWriter: null!);
    }

    private static async Task SeedAsync(
        params (string ScopeType, string ScopeKey, string ParamKey, string Value)[] rows)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        foreach (var row in rows)
        {
            await using var command = new SqlCommand("""
                INSERT INTO dbo.ASSISTANT_PARAM_SCOPE
                    (SCOPE_TYPE, SCOPE_KEY, PARAM_KEY, PARAM_VALUE, CREATE_PERSON, CREATE_DATE)
                VALUES (@Type, @Key, @Param, @Value, N'test', GETDATE());
                """, connection);
            command.Parameters.AddWithValue("@Type", row.ScopeType);
            command.Parameters.AddWithValue("@Key", row.ScopeKey);
            command.Parameters.AddWithValue("@Param", row.ParamKey);
            command.Parameters.AddWithValue("@Value", row.Value);
            await command.ExecuteNonQueryAsync();
        }
    }

    /// <summary>清掉本次造的行（开发库不留痕）。用 IN 一次删完，键都带随机后缀，不会误伤真实配置。</summary>
    private static async Task CleanupAsync(params string[] scopeKeys)
    {
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        var names = scopeKeys.Select((_, index) => $"@k{index}").ToArray();
        await using var command = new SqlCommand(
            $"DELETE FROM dbo.ASSISTANT_PARAM_SCOPE WHERE SCOPE_KEY IN ({string.Join(", ", names)});",
            connection);
        for (var index = 0; index < scopeKeys.Length; index++)
        {
            command.Parameters.AddWithValue(names[index], scopeKeys[index]);
        }

        await command.ExecuteNonQueryAsync();
    }

    private sealed record StoredParameter(
        string Key,
        string? Value,
        string ValueType,
        string? DefaultValue,
        string GroupCode,
        string GroupLabel,
        int GroupSeq,
        int SeqNo,
        string EffectScope,
        string Description);

    private static async Task<IReadOnlyList<StoredParameter>> LoadAsync(int ownerModule)
    {
        const string sql = """
            SELECT PARAM_KEY, PARAM_VALUE, VALUE_TYPE, DEFAULT_VALUE, GROUP_CODE, GROUP_LABEL,
                   GROUP_SEQ, SEQ_NO, EFFECT_SCOPE, DESC_TEXT
            FROM dbo.SYSSS WHERE OWNER_MODULE = @Owner ORDER BY PARAM_KEY;
            """;
        var items = new List<StoredParameter>();
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Owner", ownerModule);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            items.Add(new StoredParameter(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7),
                reader.GetString(8),
                reader.GetString(9)));
        }

        return items;
    }

    /// <summary>
    /// 目录里的每一条参数都必须在库里有行，且**定义逐字段一致**。
    ///
    /// <para>
    /// 目录是唯一真源，库里的行是它的落地快照；两者不一致意味着"目录说是一回事、运行时按另一回事跑"。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Catalog_And_Database_Declarations_Agree()
    {
        if (ConnectionString.Value is null) return;

        var rows = await LoadAsync(AssistantParameterCatalog.OwnerModule);
        var byKey = rows.ToDictionary(row => row.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var descriptor in AssistantParameterCatalog.All)
        {
            Assert.True(byKey.ContainsKey(descriptor.Key),
                $"参数 {descriptor.Key} 在 dbo.SYSSS（OWNER_MODULE = {AssistantParameterCatalog.OwnerModule}）里没有行。");

            var row = byKey[descriptor.Key];
            var group = AssistantParameterCatalog.FindGroup(descriptor.Group)!;

            Assert.Equal(descriptor.ValueType, row.ValueType);
            Assert.Equal(descriptor.Group, row.GroupCode);
            Assert.Equal(group.Label, row.GroupLabel);
            Assert.Equal(group.Seq, row.GroupSeq);
            Assert.Equal(AssistantParameterCatalog.SeqNoOf(descriptor), row.SeqNo);
            Assert.Equal("immediate", row.EffectScope);
            Assert.False(string.IsNullOrWhiteSpace(row.Description));

            // 字符串型参数的默认值留在代码里（DEFAULT_VALUE 为 NULL）；标量型必须写进列，且与目录一致
            if (AssistantParameterCatalog.WritesColumnDefault(descriptor))
            {
                Assert.Equal(descriptor.DefaultValue, row.DefaultValue);
            }
            else
            {
                Assert.Null(row.DefaultValue);
            }
        }
    }

    /// <summary>库里不能有目录之外的键：那多半是参数下线后没清理，界面也读不到它。</summary>
    [Fact]
    public async Task Database_Has_No_Keys_Outside_The_Catalog()
    {
        if (ConnectionString.Value is null) return;

        var rows = await LoadAsync(AssistantParameterCatalog.OwnerModule);
        foreach (var row in rows)
        {
            Assert.True(AssistantParameterCatalog.Find(row.Key) is not null,
                $"dbo.SYSSS 里有目录之外的助手参数键：{row.Key}（参数下线后应当一并清理）。");
        }
    }

    /// <summary>
    /// 红线与凭据**在任何归属模块下都不是合法参数键**。
    ///
    /// <para>
    /// 红线的含义是"没有把它配成关的表达方式"（预演、幂等、越权上限、批核族），凭据则是
    /// "参数类型里没有任何一种能表达它"。它们的合法性不该取决于落到了哪一行。
    /// </para>
    /// </summary>
    [Fact]
    public async Task No_RedLine_Or_Credential_Keys_Anywhere_In_The_Parameter_Table()
    {
        if (ConnectionString.Value is null) return;

        const string sql = "SELECT OWNER_MODULE, PARAM_KEY FROM dbo.SYSSS;";
        var offenders = new List<string>();
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using (var command = new SqlCommand(sql, connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var owner = reader.GetInt32(0);
                var key = reader.GetString(1);
                if (AssistantParameterCatalog.RedLineKeys.Contains(key)
                    || AssistantParameterCatalog.CredentialKeys.Contains(key))
                {
                    offenders.Add($"{owner}|{key}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            $"参数表里出现了红线 / 凭据键：{string.Join('、', offenders)}（ADR-030 §3 / §6.4 禁止）。");
    }

    /// <summary>
    /// 助手自己的设置表已退役（迁移 294）。它一旦复活，就意味着参数又有两个地方可写。
    /// </summary>
    [Fact]
    public async Task Legacy_Assistant_Setting_Table_Is_Gone()
    {
        if (ConnectionString.Value is null) return;

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT OBJECT_ID(N'dbo.ASSISTANT_SETTING', N'U');", connection);
        var result = await command.ExecuteScalarAsync();

        Assert.True(result is null or DBNull,
            "dbo.ASSISTANT_SETTING 仍然存在：助手参数应当只在 dbo.SYSSS 里（ADR-030 §6.3）。");
    }

    /// <summary>
    /// 作用域表里只能出现在目录中声明为"可作用域化"的键（批 B 落地后这张表才存在，
    /// 所以现在它多半是"表不存在"这一支）。
    /// </summary>
    [Fact]
    public async Task Scope_Table_Only_Holds_Scoped_Keys()
    {
        if (ConnectionString.Value is null) return;

        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();

        await using (var exists = new SqlCommand(
            "SELECT OBJECT_ID(N'dbo.ASSISTANT_PARAM_SCOPE', N'U');", connection))
        {
            var table = await exists.ExecuteScalarAsync();
            if (table is null or DBNull) return;
        }

        await using var command = new SqlCommand(
            "SELECT SCOPE_TYPE, SCOPE_KEY, PARAM_KEY FROM dbo.ASSISTANT_PARAM_SCOPE;", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var scopeType = reader.GetString(0);
            var scopeKey = reader.GetString(1);
            var key = reader.GetString(2);

            Assert.True(scopeType is "MODULE" or "USER", $"未知的作用域类型：{scopeType}");

            var descriptor = AssistantParameterCatalog.Find(key);
            Assert.True(descriptor is not null, $"作用域表里有目录之外的键：{key}");
            Assert.True(
                descriptor!.ScopePolicy != AssistantParameterScopePolicy.None,
                $"参数 {key} 在目录里声明为不可作用域化，却在作用域表（{scopeType} {scopeKey}）里有值。");
            Assert.True(
                AssistantParameterCatalog.AllowsLayer(descriptor, scopeType),
                $"参数 {key} 没有声明可被 {scopeType} 层覆盖，却在作用域表里有值。");
        }
    }
}
