using EOS.API.Features.Assistant;
using EOS.API.Tests.Parity;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 引擎 parity 的**旧引擎**一侧：逐条回放用例、写出归一化结果，交给 Agent Harness 仓的
/// <c>Agent.Harness.Parity</c> 与新引擎（MAF）对比。
///
/// <para>
/// 这里不做对比、不下 PASS/FAIL 结论——比较器在 Harness 仓（它拥有判定口径）。
/// 本测试只保证一件事：**旧引擎能按脚本跑完并如实产出**。跑完之后执行
/// <c>dotnet run --project tools/Agent.Harness.Parity -- compare ...</c> 才得到报告。
/// </para>
///
/// <para>
/// 用例目录默认 <c>EOS.API.Tests/parity-cases</c>，结果默认 <c>artifacts/parity/legacy</c>
/// （<c>artifacts/</c> 已被 .gitignore 排除）。可用环境变量
/// <c>EOS_PARITY_CASES</c> / <c>EOS_PARITY_OUT</c> 覆盖。
/// </para>
/// </summary>
public sealed class LegacyParityTests
{
    private static string CasesDirectory =>
        Environment.GetEnvironmentVariable("EOS_PARITY_CASES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(ApiSourceProbe.FindRepoRoot(), "EOS.API.Tests", "parity-cases");

    private static string OutputDirectory =>
        Environment.GetEnvironmentVariable("EOS_PARITY_OUT") is { Length: > 0 } custom
            ? custom
            : Path.Combine(ApiSourceProbe.FindRepoRoot(), "artifacts", "parity", "legacy");

    public static IEnumerable<object[]> Cases() =>
        ParityCaseFile.ListCaseFiles(CasesDirectory)
            .Select(path => new object[] { Path.GetFileNameWithoutExtension(path) });

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task 旧引擎回放用例并写出归一化结果(string caseName)
    {
        var parityCase = ParityCaseFile.Load(Path.Combine(CasesDirectory, caseName + ".json"));

        // 文件名与用例名必须一致：结果文件按用例名命名，不一致会让对比端找不到文件。
        Assert.Equal(caseName, parityCase.Name);

        var outcome = await LegacyParityRunner.RunAsync(parityCase);
        ParityCaseFile.Save(Path.Combine(OutputDirectory, caseName + ".legacy.json"), outcome);

        // 用例里抄的收尾轮说明必须与产品代码逐字相同：措辞是产品资产，
        // 改了 ChatService.FinalRoundInstruction 而用例没跟上，对比就变成"拿旧措辞比新行为"。
        if (parityCase.FinalRoundInstruction is not null)
        {
            Assert.Equal(ChatService.FinalRoundInstruction, parityCase.FinalRoundInstruction);
        }

        Assert.Equal("legacy", outcome.Engine);
        // 脚本化的模型调用必须被全部消费：漏调说明用例写少了，多调会直接抛异常。
        Assert.Equal(parityCase.Calls.Count, outcome.ModelCalls);
    }
}
