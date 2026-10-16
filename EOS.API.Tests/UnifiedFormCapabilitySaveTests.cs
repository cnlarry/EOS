using System.Data;
using SystemTextJson = System.Text.Json;
using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 统一表单能力两位（`MODULES.UNIFIED_FORM_ENABLED` / `UNIFIED_FORM_EDIT_ENABLED`，迁移 355）
/// 的两道闸：
///   ① **保存路径的不变量**（离线）：未启用统一表单 ⇒ 写入位必须为假；非工作台形态 ⇒ 两位归零。
///      与库约束 `CK_MODULES_UNIFIED_FORM_EDIT` 同口径，前端的上行联动只是体验层。
///   ② **库约束本身**（真库）：非法组合写不进去——不变量是结构性的，不靠调用方自觉。
/// </summary>
public sealed class UnifiedFormCapabilitySaveTests
{
    private static readonly Lazy<string?> ConnectionString = new(ResolveConnectionString);

    private static MenuAdminModule Workbench(bool formEnabled, bool editEnabled) => new(
        M_IDX: 2316, M_ALIAS: null, M_DESC: "操作审计", M_URL: "/workbench",
        DETAIL_NO_FIELDS: null, DETAIL_NO_SAVE: false, SEARCH_1: false, SEARCH_2: false,
        M_P_IDX: 23, SORT_IDX: 80, M_TAG: true, AUTO_APPROVE: false, IF_COPY: false,
        ERROR_NO_SAVE: false, SORT_FIELDS: "AUDIT_EVENT.EVENT_ID DESC",
        MASTER_TABLE: "AUDIT_EVENT", FILTER: null, DETAIL_TABLE: "AUDIT_FIELD_CHANGE",
        NOT_BACK_FIELDS_M: null, NOT_BACK_FIELDS: null, LAST_UPDATE_BY: null, LAST_UPDATE_DATE: null,
        FORM_TABS: null, FORM_COLUMNS: null, M_ICON: null, Icon: null,
        ApproveEnabled: false, REMARK: null,
        UnifiedFormEnabled: formEnabled, UnifiedFormEditEnabled: editEnabled);

    [Theory]
    [InlineData(false, true, false)]    // 未启用表单却允许写入 ⇒ 写入位被抹平
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]      // 合法组合原样保留
    [InlineData(true, false, false)]
    public void 未启用统一表单时写入位被归一为假(bool formEnabled, bool editEnabled, bool expectedEdit)
    {
        var normalized = MenuAdminRepository.NormalizeForKind(Workbench(formEnabled, editEnabled));
        Assert.Equal(formEnabled, normalized.UnifiedFormEnabled);
        Assert.Equal(expectedEdit, normalized.UnifiedFormEditEnabled);
    }

    [Fact]
    public void 非工作台形态上两位一律归零()
    {
        // 定制承载页（M_URL 是精确路径）：没有表单面，配了也不装配——保存时直接落回默认
        var customPage = Workbench(true, true) with { M_URL = "/admin/audit", MASTER_TABLE = null };
        var normalized = MenuAdminRepository.NormalizeForKind(customPage);
        Assert.False(normalized.UnifiedFormEnabled);
        Assert.False(normalized.UnifiedFormEditEnabled);
    }

    [Fact]
    public async Task 库约束拦住未启用表单却可写()
    {
        if (ConnectionString.Value is null) return;
        await using var connection = new SqlConnection(ConnectionString.Value);
        await connection.OpenAsync();
        // 用一个几乎不可能撞上的编号：这条 INSERT 按设计必然失败，成功就说明约束没了
        var probeId = 98_700_000 + Random.Shared.Next(0, 9_999_999);
        await using var command = new SqlCommand("""
            INSERT INTO dbo.MODULES (M_IDX, M_DESC, UNIFIED_FORM_ENABLED, UNIFIED_FORM_EDIT_ENABLED)
            VALUES (@Id, N'统一表单能力约束探针', 0, 1);
            """, connection);
        command.Parameters.Add("@Id", SqlDbType.Int).Value = probeId;

        try
        {
            await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
        }
        catch
        {
            // 插入居然成功了：清掉探针行再失败（否则留下一个非法组合的模块）
            await using var cleanup = new SqlCommand("DELETE FROM dbo.MODULES WHERE M_IDX = @Id;", connection);
            cleanup.Parameters.Add("@Id", SqlDbType.Int).Value = probeId;
            await cleanup.ExecuteNonQueryAsync();
            Assert.Fail("库约束 CK_MODULES_UNIFIED_FORM_EDIT 没拦住「未启用表单却可写」的插入。");
        }
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
            var match = System.Text.RegularExpressions.Regex.Match(
                text, "\\[mcp_servers\\.mssql\\.env\\][\\s\\S]*?MSSQL_ERP_CONN\\s*=\\s*\"([^\"]+)\"");
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
