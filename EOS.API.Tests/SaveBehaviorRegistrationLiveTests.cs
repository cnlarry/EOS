using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 保存后行为登记一致性：代码登记（`CatalogAfterSaveMap`）、库内配置、已发布快照三者不得各说各话。
///
/// 原先由脚本门禁 `check-domain-rule-registry.ps1` 守着，该门禁已退役（见 `scripts/README.md` 退役清单），
/// 本文件承接它的三条断言：
///
///  ① **目录承接模块必须有启用的 SAVE 期校验规则**——`ModuleBusinessMap.CatalogAfterSaveMap` 里的模块
///     不再走 C# 领域规则，若库里没有启用的 SAVE 规则，**保存后行为会静默消失**（不报错、不留痕）；
///  ② **当前快照不得残留已拆除的领域规则族名**（`BusinessRule.DomainRule`）——残留只说明该模块自退役后
///     从未重新发布，属配置面卫生；
///  ③ **`reference-exists` 的引用列必须是物理列**——写错（如表名拼错、列不存在）会在保存时抛 Msg 207
///     让整单 500，而编译与单测都发现不了。
///
/// 只读，不建夹具；无连接串即失败（无连接跳过≠已验证）。
/// </summary>
[Trait("Category", "Integration")]
[Collection("live-database")]
public sealed class SaveBehaviorRegistrationLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
        ?? throw new InvalidOperationException("真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

    [Fact]
    public async Task 目录承接模块必须有启用的SAVE期校验规则()
    {
        var moduleIds = ReadCatalogAfterSaveModules();
        Assert.True(moduleIds.Count > 0,
            "CatalogAfterSaveMap 解析出 0 个模块——要么登记表改名了（本用例的解析器要跟上），要么它空了");

        await using var connection = await OpenAsync();
        var enabled = await RowsAsync(connection, $"""
            SET NOCOUNT ON;
            SELECT DISTINCT CONVERT(nvarchar(20), M_IDX)
              FROM dbo.MODULE_VALIDATION_RULE
             WHERE STAGE = N'SAVE' AND ENABLED = 1
               AND M_IDX IN ({string.Join(", ", moduleIds)});
            """);

        var withEnabled = enabled.Select(int.Parse).ToHashSet();
        var missing = moduleIds.Where(id => !withEnabled.Contains(id)).OrderBy(id => id).ToList();
        Assert.True(missing.Count == 0,
            $"这些模块登记为「目录承接」却在库里没有启用的 SAVE 期校验规则（保存后行为会静默消失）：{string.Join("、", missing)}");
    }

    [Fact]
    public async Task 当前快照不得残留已拆除的领域规则族名()
    {
        await using var connection = await OpenAsync();

        var snapshotRows = await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT WHERE IS_CURRENT = 1;");
        Assert.True(snapshotRows > 0, "当前快照应当存在——为空时这条断言恒真");

        var stale = await RowsAsync(connection, """
            SET NOCOUNT ON;
            SELECT CONCAT(M_IDX, '|', ISNULL(JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.DomainRule'), ''), '|', VERSION)
              FROM dbo.WORKBENCH_DEFINITION_SNAPSHOT
             WHERE IS_CURRENT = 1
               AND ISNULL(JSON_VALUE(DEFINITION_JSON, '$.BusinessRule.DomainRule'), '') <> '';
            """);
        Assert.True(stale.Count == 0,
            $"这些模块的当前快照仍残留已拆除的领域规则族名（自退役后从未重新发布，请重发布一次）：{string.Join("；", stale.Take(10))}");
    }

    [Fact]
    public async Task 引用存在断言的引用列必须是物理列()
    {
        await using var connection = await OpenAsync();

        var ruleCount = await ScalarAsync<int>(connection,
            "SELECT COUNT(*) FROM dbo.MODULE_VALIDATION_RULE WHERE VALIDATION_KEY = N'reference-exists';");
        Assert.True(ruleCount > 0, "库里应当有 reference-exists 规则——一条都没有时这条断言恒真");

        // 四个来源：checks[].refKey.field、checks[].targets[].refKey.field、
        // checks[].join[].target、checks[].targets[].join[].target。
        // 判定：引用列名带非标识符字符、或表/列在 sys.columns 里找不到 ⇒ 保存时 Msg 207。
        var bad = await RowsAsync(connection, """
            SET NOCOUNT ON;
            SELECT CONCAT(x.M_IDX, '|', x.STAGE, '|', x.REF, '|', x.COL)
            FROM (
                SELECT r.M_IDX, r.STAGE,
                       JSON_VALUE(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].refTable') COLLATE DATABASE_DEFAULT AS REF,
                       JSON_VALUE(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].refKey.field') COLLATE DATABASE_DEFAULT AS COL
                FROM dbo.MODULE_VALIDATION_RULE r
                CROSS APPLY OPENJSON(r.PARAM_STRUCT, '$.checks') c
                WHERE r.VALIDATION_KEY = N'reference-exists'
                UNION ALL
                SELECT r.M_IDX, r.STAGE,
                       JSON_VALUE(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].refTable') COLLATE DATABASE_DEFAULT,
                       JSON_VALUE(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].refKey.field') COLLATE DATABASE_DEFAULT
                FROM dbo.MODULE_VALIDATION_RULE r
                CROSS APPLY OPENJSON(r.PARAM_STRUCT, '$.checks') c
                CROSS APPLY OPENJSON(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets') t
                WHERE r.VALIDATION_KEY = N'reference-exists'
                UNION ALL
                SELECT r.M_IDX, r.STAGE,
                       JSON_VALUE(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].refTable') COLLATE DATABASE_DEFAULT,
                       JSON_VALUE(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].join[' + CONVERT(nvarchar(3), j.[key]) + '].target') COLLATE DATABASE_DEFAULT
                FROM dbo.MODULE_VALIDATION_RULE r
                CROSS APPLY OPENJSON(r.PARAM_STRUCT, '$.checks') c
                CROSS APPLY OPENJSON(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].join') j
                WHERE r.VALIDATION_KEY = N'reference-exists'
                UNION ALL
                SELECT r.M_IDX, r.STAGE,
                       JSON_VALUE(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].refTable') COLLATE DATABASE_DEFAULT,
                       JSON_VALUE(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].join[' + CONVERT(nvarchar(3), j.[key]) + '].target') COLLATE DATABASE_DEFAULT
                FROM dbo.MODULE_VALIDATION_RULE r
                CROSS APPLY OPENJSON(r.PARAM_STRUCT, '$.checks') c
                CROSS APPLY OPENJSON(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets') t
                CROSS APPLY OPENJSON(r.PARAM_STRUCT, '$.checks[' + CONVERT(nvarchar(3), c.[key]) + '].targets[' + CONVERT(nvarchar(3), t.[key]) + '].join') j
                WHERE r.VALIDATION_KEY = N'reference-exists'
            ) x
            WHERE x.REF IS NOT NULL AND x.COL IS NOT NULL AND x.COL <> N'-'
              AND (x.REF LIKE N'%[^A-Za-z0-9_]%' OR x.COL LIKE N'%[^A-Za-z0-9_]%'
                   OR OBJECT_ID('dbo.' + x.REF) IS NULL
                   OR NOT EXISTS (SELECT 1 FROM sys.columns sc
                                  WHERE sc.object_id = OBJECT_ID('dbo.' + x.REF) AND sc.name = x.COL));
            """);
        Assert.True(bad.Count == 0,
            $"reference-exists 的引用列不可用（保存时会 Msg 207 整单 500）：{string.Join("；", bad.Take(10))}");
    }

    // ===== 源码侧登记表 =====

    /// <summary>从 `CatalogAfterSaveMap` 的模块集合里取模块号（保存后行为已由校验目录承接的那批）。</summary>
    private static List<int> ReadCatalogAfterSaveModules()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "EOS.API", "Data", "ModuleBusinessMap.cs"));
        var block = Regex.Match(source,
            @"class\s+CatalogAfterSaveMap[\s\S]*?Modules\s*=\s*new\s+HashSet<int>\s*\{(?<ids>[\s\S]*?)\}");
        Assert.True(block.Success, "在 ModuleBusinessMap.cs 里找不到 CatalogAfterSaveMap 的模块集合——解析器需要跟上源码写法");
        return Regex.Matches(block.Groups["ids"].Value, @"\d+")
            .Select(match => int.Parse(match.Value))
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    // ===== 装配 =====

    private static async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<T> ScalarAsync<T>(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        var scalar = await command.ExecuteScalarAsync();
        Assert.NotNull(scalar);
        return (T)Convert.ChangeType(scalar!, typeof(T));
    }

    private static async Task<List<string>> RowsAsync(SqlConnection connection, string sql)
    {
        var rows = new List<string>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        }
        return rows;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EOS.API", "EOS.API.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found (EOS.API/EOS.API.csproj).");
    }
}
