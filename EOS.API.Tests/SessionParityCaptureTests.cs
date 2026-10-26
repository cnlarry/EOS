using EOS.API.Tests.Parity;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 从**库内真实会话**重建 parity 用例，写进 <c>artifacts/parity/cases</c>（已被 .gitignore 排除）。
///
/// <para>
/// 为什么不入仓库：用例里是真实业务问答（料号、客户名、单号），而本仓库不含任何业务数据。
/// 生成物只留在本机，需要时重跑即可。
/// </para>
///
/// <para>
/// 跑法：<c>dotnet test --filter FullyQualifiedName~SessionParityCapture</c>，
/// 之后用 <c>EOS_PARITY_CASES=artifacts/parity/cases</c> 跑 <see cref="LegacyParityTests"/> 回放。
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class SessionParityCaptureTests
{
    [Fact]
    public async Task 从真实会话重建用例()
    {
        var connectionString = Environment.GetEnvironmentVariable("MSSQL_ERP_CONN")
            ?? throw new InvalidOperationException(
                "真库集成测试需要 MSSQL_ERP_CONN；未配置即失败（无连接跳过≠已验证）。");

        var cases = await SessionParityCaseGenerator.ReadAsync(connectionString, maxCases: 10);

        if (cases.Count == 0)
        {
            // 库里还没有真实助手会话（数据前置缺失，不是缺陷）：没有可重建的对象。
            return;
        }

        var output = Path.Combine(ApiSourceProbe.FindRepoRoot(), "artifacts", "parity", "cases");
        Directory.CreateDirectory(output);

        // 生成物目录整批重建：上一轮的用例名可能已经不在库里，留着会让对比端读到孤儿文件。
        foreach (var stale in Directory.GetFiles(output, "*.json"))
        {
            File.Delete(stale);
        }

        foreach (var parityCase in cases)
        {
            var path = Path.Combine(output, parityCase.Name + ".json");
            ParityCaseFile.Save(path, parityCase);

            // 回读校验：生成的东西必须能被同一个读取端解析回来，否则对比端会在下一步静默少跑。
            var roundTrip = ParityCaseFile.Load(path);
            Assert.Equal(parityCase.Name, roundTrip.Name);
            Assert.NotEmpty(roundTrip.Calls);
            Assert.False(string.IsNullOrWhiteSpace(roundTrip.UserInput));
            Assert.Equal(parityCase.Tools.Count, roundTrip.Tools.Count);
        }
    }
}
