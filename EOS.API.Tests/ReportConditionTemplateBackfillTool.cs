using System.Data;
using System.Text;
using System.Text.RegularExpressions;
using EOS.API.Data;
using EOS.API.Tests.Tools;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// One-off tool (no-op by default): deterministically converts legacy SYSQR_DEFAULT
/// F_TYPE/F_EXPR condition DSL rows into FILTER_TEMPLATE JSON
/// (reusing the same conversion pipeline used for structured chooser conditions).
///
/// Background: report condition definitions move from a dedicated DSL (F_TYPE ranges/options/
/// data sources) to structured FILTER_TEMPLATE with {p.X} placeholders.
/// 转换成功后生成静态 UPDATE 迁移（024_report_condition_template.sql 的转换段，
/// 漂移守卫：LEGACY 原文一致且 FILTER_TEMPLATE 仍为 NULL 才落）；失败行入人工清单
/// （logs/report-condition-migration/manual-list.csv，FILTER_TEMPLATE 保持 NULL 运行期 fail-closed）。
///
/// 运行（显式指 csproj，从仓库根，须已设 MSSQL_ERP_CONN）：
///   dotnet test EOS.API.Tests\EOS.API.Tests.csproj --filter ReportConditionTemplateBackfillTool
///   并设环境变量 EOS_TOOL_REPORT_CONDITION_RERUN=1
/// </summary>
[Trait("Category", "Tool")]
public sealed class ReportConditionTemplateBackfillTool
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    [Fact]
    public async Task Rerun_ConvertExistingConditionDsl()
    {
        if (Environment.GetEnvironmentVariable("EOS_TOOL_REPORT_CONDITION_RERUN") != "1")
        {
            return;
        }

        var (converted, manual) = await ConvertRowsAsync();
        var root = FindRepoRoot();
        var sql = BuildMigrationSql(converted);
        var migrationPath = Path.Combine(root, "EOS.API", "Data", "Migrations", "024_report_condition_template.sql");
        await File.WriteAllTextAsync(migrationPath, sql, new UTF8Encoding(false));

        var reportDir = Path.Combine(root, "logs", "report-condition-migration");
        Directory.CreateDirectory(reportDir);
        await WriteManualListAsync(Path.Combine(reportDir, "manual-list.csv"), manual);

        Assert.True(converted.Count + manual.Count > 0, "应至少处理一行");
    }

    private sealed record SourceRow(int ModuleId, int SerialNo, int Type, string Field, string Expr, string DefaultValue, string ParaName);
    private sealed record ConvertedRow(SourceRow Row, string Template, string SourceDsl);

    private async Task<(List<ConvertedRow> Converted, List<SourceRow> Manual)> ConvertRowsAsync()
    {
        const string sql = """
            SELECT M_IDX, CONVERT(INT,SERIAL_NO), COALESCE(TRY_CONVERT(INT,F_TYPE),0),
                   LTRIM(RTRIM(ISNULL(F_ID,''))),LTRIM(RTRIM(ISNULL(F_EXPR,''))),
                   LTRIM(RTRIM(ISNULL(F_VALUE,''))),LTRIM(RTRIM(ISNULL(PARA_NAME,'')))
            FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
            ORDER BY M_IDX, SERIAL_NO;
            """;
        var converted = new List<ConvertedRow>();
        var manual = new List<SourceRow>();
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new SourceRow(
                reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
            var legacy = $"{row.Type}|{row.Expr}";
            var result = ConditionTemplateDslConverter.Convert(row.Type, row.Expr, row.DefaultValue, row.Field, row.ParaName);
            if (result.Template is not null && result.Error is null)
                converted.Add(new ConvertedRow(row, result.Template, legacy));
            else
                manual.Add(row);
        }
        return (converted, manual);
    }

    private static string BuildMigrationSql(List<ConvertedRow> converted)
    {
        var builder = new StringBuilder();
        builder.AppendLine("-- SYSQR_DEFAULT 存量 F_TYPE/F_EXPR DSL → FILTER_TEMPLATE 转换（工具生成）");
        builder.AppendLine($"--       本迁移由 ReportConditionTemplateBackfillTool 生成（{converted.Count} 行自动转换），");
        builder.AppendLine("--       失败行保持 FILTER_TEMPLATE=NULL（运行期 fail-closed），入 logs/report-condition-migration/manual-list.csv。");
        builder.AppendLine("-- 漂移守卫：LEGACY 原文一致且 FILTER_TEMPLATE 仍为 NULL 才落，不覆盖已人工配置值。");
        builder.AppendLine();
        builder.AppendLine("SET NOCOUNT ON;");
        builder.AppendLine();
        builder.AppendLine("DECLARE @GUARD_MESSAGE NVARCHAR(400) = N'本脚本只能在 EOS.ERP 数据库内执行，当前库为 ' + DB_NAME() + N'。';");
        builder.AppendLine("IF DB_NAME() <> N'EOS.ERP'");
        builder.AppendLine("    THROW 50000, @GUARD_MESSAGE, 1;");
        builder.AppendLine();
        builder.AppendLine("BEGIN TRANSACTION;");
        builder.AppendLine();
        builder.AppendLine("-- 1. SYSQR_DEFAULT 加 FILTER_TEMPLATE 列");
        builder.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSQR_DEFAULT') AND name = 'FILTER_TEMPLATE')");
        builder.AppendLine("    ALTER TABLE dbo.SYSQR_DEFAULT ADD [FILTER_TEMPLATE] NVARCHAR(MAX) NULL;");
        builder.AppendLine();
        builder.AppendLine("-- 2. 存量转换（漂移守卫：LEGACY 原文一致且 FILTER_TEMPLATE 仍为 NULL 才落）");
        builder.AppendLine("CREATE TABLE #TEMPLATE (M_IDX INT NOT NULL, SERIAL_NO INT NOT NULL, LEGACY NVARCHAR(MAX) NOT NULL, TEMPLATE NVARCHAR(MAX) NOT NULL);");
        builder.AppendLine("INSERT INTO #TEMPLATE (M_IDX, SERIAL_NO, LEGACY, TEMPLATE) VALUES");
        for (var i = 0; i < converted.Count; i++)
        {
            var c = converted[i];
            var comma = i < converted.Count - 1 ? "," : ";";
            builder.AppendLine($"  ({c.Row.ModuleId}, {c.Row.SerialNo}, N'{SqlEscape(c.SourceDsl)}', N'{SqlEscape(c.Template)}'){comma}");
        }
        builder.AppendLine();
        builder.AppendLine("UPDATE dbo.SYSQR_DEFAULT");
        builder.AppendLine("SET FILTER_TEMPLATE = t.TEMPLATE");
        builder.AppendLine("FROM dbo.SYSQR_DEFAULT d");
        builder.AppendLine("INNER JOIN #TEMPLATE t ON t.M_IDX = d.M_IDX AND t.SERIAL_NO = d.SERIAL_NO");
        builder.AppendLine("WHERE d.FILTER_TEMPLATE IS NULL");
        builder.AppendLine("  AND LTRIM(RTRIM(ISNULL(CAST(d.F_TYPE AS NVARCHAR(10)),''))) + '|' + LTRIM(RTRIM(ISNULL(d.F_EXPR,''))) = t.LEGACY;");
        builder.AppendLine();
        builder.AppendLine("DROP TABLE #TEMPLATE;");
        builder.AppendLine();
        builder.AppendLine("-- 3. 扩展属性");
        builder.AppendLine("IF NOT EXISTS (SELECT 1 FROM sys.extended_properties WHERE major_id = OBJECT_ID('dbo.SYSQR_DEFAULT') AND minor_id = (SELECT column_id FROM sys.columns WHERE object_id = OBJECT_ID('dbo.SYSQR_DEFAULT') AND name = 'FILTER_TEMPLATE'))");
        builder.AppendLine("    EXEC sp_addextendedproperty @name = N'MS_Description', @value = N'结构化参数定义（FILTER_TEMPLATE JSON，复用 FILTER_STRUCT 契约 + {p.X} 参数占位符）', @level0type = N'SCHEMA', @level0name = N'dbo', @level1type = N'TABLE', @level1name = N'SYSQR_DEFAULT', @level2type = N'COLUMN', @level2name = N'FILTER_TEMPLATE';");
        builder.AppendLine();
        builder.AppendLine("COMMIT TRANSACTION;");
        return builder.ToString();
    }

    private static async Task WriteManualListAsync(string path, List<SourceRow> manual)
    {
        var lines = new List<string> { "M_IDX,SERIAL_NO,F_TYPE,F_ID,F_EXPR,F_VALUE,PARA_NAME" };
        lines.AddRange(manual.Select(row =>
            $"{row.ModuleId},{row.SerialNo},{row.Type},{CsvEscape(row.Field)},{CsvEscape(row.Expr)},{CsvEscape(row.DefaultValue)},{CsvEscape(row.ParaName)}"));
        await File.WriteAllLinesAsync(path, lines, new UTF8Encoding(false));
    }

    private static string SqlEscape(string value) => value.Replace("'", "''");
    private static string CsvEscape(string value) => value.Contains(',') ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

    private static string FindRepoRoot()
    {
        var envRoot = Environment.GetEnvironmentVariable("EOS_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(envRoot) && File.Exists(Path.Combine(envRoot, "EOS.slnx")))
            return Path.GetFullPath(envRoot);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var depth = 0; depth < 12 && dir is not null; depth++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EOS.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("未找到仓库根（含 EOS.slnx，可设 EOS_REPO_ROOT）。");
    }

    private static string? ResolveConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        return null;
    }
}
