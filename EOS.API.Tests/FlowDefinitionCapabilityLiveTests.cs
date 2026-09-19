using System.Data;
using EOS.API.Data;
using Microsoft.Data.SqlClient;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 模块批核能力三开关列的读端类型回归。
///
/// `CASE … THEN 1 ELSE 0 END` 的结果类型是 int，`SqlDataReader.GetBoolean` 取它会抛
/// `InvalidCastException`；流程设计器保存（`FlowDefinitionService.SaveFlowAsync`）曾因其中一列
/// 漏了 `CONVERT(bit, …)` 而整链接 500，且编译、单测、黄金链路 E2E 都发现不了。
///
/// 用例直接执行生产代码共用的列表达式 `FlowDefinitionService.ApproveCapabilityColumns`，
/// 按与读端相同的 0..2 序取三个开关，并回查 MODULES / WFFORM 原始值对拍——
/// 表达式一旦退回 int，这里立刻红。
/// </summary>
[Collection("live-database")]
public sealed class FlowDefinitionCapabilityLiveTests
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("EOS_ERP_TEST_CONNECTION")
        ?? throw new InvalidOperationException(
            "真库集成测试需要 EOS_ERP_TEST_CONNECTION；未配置即失败（无连接跳过≠已验证）。");

    [Theory]
    [InlineData(1404)]   // 效果引擎接管
    [InlineData(1202)]   // 自动批核
    [InlineData(1906)]   // 已配置流程（WFFORM 有行）
    [InlineData(110310)] // 三开关全假
    public async Task 批核能力开关列_按GetBoolean读取不抛类型异常(int moduleId)
    {
        var token = CancellationToken.None;
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync(token);

        bool autoApprove, effectEngine, hasFlow;
        await using (var command = new SqlCommand(
            $"SELECT {FlowDefinitionService.ApproveCapabilityColumns} FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX=@ModuleId;",
            connection))
        {
            command.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
            await using var reader = await command.ExecuteReaderAsync(token);
            Assert.True(await reader.ReadAsync(token), $"模块 {moduleId} 不存在");
            // 生产读端就是这三行：任一类开关不是 bit 即在这里抛 InvalidCastException
            autoApprove = reader.GetBoolean(0);
            effectEngine = reader.GetBoolean(1);
            hasFlow = reader.GetBoolean(2);
        }

        await using var raw = new SqlCommand("""
            SELECT ISNULL(AUTO_APPROVE,0), ISNULL(EFFECT_ENGINE_TAG,0),
                   CASE WHEN EXISTS (SELECT 1 FROM dbo.WFFORM wf WITH (NOLOCK) WHERE wf.WF_M_IDX=m.M_IDX)
                        THEN 1 ELSE 0 END
            FROM dbo.MODULES m WITH (NOLOCK) WHERE m.M_IDX=@ModuleId;
            """, connection);
        raw.Parameters.Add("@ModuleId", SqlDbType.Int).Value = moduleId;
        await using var rawReader = await raw.ExecuteReaderAsync(token);
        Assert.True(await rawReader.ReadAsync(token));
        Assert.Equal(Convert.ToBoolean(rawReader.GetValue(0)), autoApprove);
        Assert.Equal(Convert.ToBoolean(rawReader.GetValue(1)), effectEngine);
        Assert.Equal(Convert.ToBoolean(rawReader.GetValue(2)), hasFlow);
    }
}
