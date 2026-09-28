using EOS.API.Data;
using EOS.API.Models;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 汇总报表的**参数序号 ↔ 库内条件行**绑定：注册表按序号从前端条件取值，序号对不上就等于
/// "筛选项和查询参数错位"——面板上选的值不会生效，而且不报错。
///
/// 条件行按模块读，归位（承载页 → 归属模块）时同一模块会汇聚多份条件，序号可能被重排；
/// 本用例把"注册表声明的序号在归属模块上确有对应行"钉成不变量。
///
/// 只钉序号不钉参数名：绑定本身是**纯位置绑定**（前端按序号提交、服务端按序号取值），
/// `PARA_NAME` 目前只是展示用的名字。实测两者并不总是一致（库存日报的条件行 PARA_NAME 为空、
/// 考勤分析表写的是 `@date` 而注册表声明 `@date1`），那是既有情况，不在本用例的判定范围内。
/// </summary>
[Trait("Category", "live-database")]
[Collection("live-database")]
public sealed class ReportAggregateConditionBindingLiveTests
{
    private static string RequireConnection()
    {
        var value = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN");
        Assert.False(string.IsNullOrWhiteSpace(value),
            "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");
        return value!;
    }

    [Fact]
    public async Task 注册表里带序号的参数在归属模块上都有对应条件行()
    {
        await using var connection = new SqlConnection(RequireConnection());
        await connection.OpenAsync();
        var checkedParameters = 0;
        var seen = new HashSet<ReportAggregate>();

        foreach (var reportId in ReportAggregateRegistry.RegisteredReportIds)
        {
            var aggregate = ReportAggregateRegistry.Find(reportId)!;
            if (!seen.Add(aggregate)) continue;   // 同一数据源的多个报表变体只检查一次

            var parameters = aggregate.Parameters.Where(item => item.SerialNo > 0).ToList();
            if (parameters.Count == 0) continue;

            var moduleId = await ReadModuleIdAsync(connection, reportId);
            Assert.True(moduleId > 0, $"汇总报表 {reportId} 没有归属模块。");

            foreach (var parameter in parameters)
            {
                await using var command = new SqlCommand("""
                    SELECT COUNT(*) FROM dbo.SYSQR_DEFAULT WITH (NOLOCK)
                    WHERE M_IDX = @ModuleId AND SERIAL_NO = @SerialNo;
                    """, connection);
                command.Parameters.AddWithValue("@ModuleId", moduleId);
                command.Parameters.AddWithValue("@SerialNo", parameter.SerialNo);
                Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
                checkedParameters++;
            }
        }

        Assert.True(checkedParameters > 0, "没有任何带序号的参数被检查到——用例失去了判别力。");
    }

    private static async Task<int> ReadModuleIdAsync(SqlConnection connection, string reportId)
    {
        await using var command = new SqlCommand(
            "SELECT M_IDX FROM dbo.REPORT WITH (NOLOCK) WHERE LTRIM(RTRIM(REPORT_ID)) = @ReportId;", connection);
        command.Parameters.AddWithValue("@ReportId", reportId.Trim());
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }
}
