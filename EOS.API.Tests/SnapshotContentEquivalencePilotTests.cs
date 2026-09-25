using System.Reflection;
using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 试点取证（真库、真重建）留下的两个定义片段：模块 1406 送货单的动作 SEQ=11（inventory-move）。
///
/// 来路：清理该模块唯一一行全空占位公式行**之前**发布得到 v21、之后发布得到 v22，两者都是
/// 真实重建产物，且经比对为**纯删除 206 字符**——删掉的正是那条占位公式行，其余字节逐一相同
/// （整份定义 97132 → 96926 字节，首个差异位置 63323、公共后缀 33603 字符，差异窗口落在
/// businessActions 段内）。夹具即这两个动作对象的原文。
///
/// 因此本测试钉住的是整条链上最关键的一环：**"清理占位行"前后的两份真实定义，按新判据等价、
/// 按旧判据（逐字节）不等价**——旧判据因此会顶掉快照版本、连带作废该模块的对拍证据。
/// </summary>
public class SnapshotContentEquivalencePilotTests
{
    private static string ReadFixture(string name) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "EOS.API.Tests", "TestData", name));

    private static string AsDefinition(string actionJson) => $$"""{"moduleId":1406,"businessActions":[{{actionJson}}]}""";

    private static string FindRepoRoot()
    {
        var metadata = typeof(SnapshotContentEquivalencePilotTests).Assembly
            .GetCustomAttributes(typeof(AssemblyMetadataAttribute), false)
            .Cast<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "RepoRoot");
        if (metadata is { Value.Length: > 0 } && Directory.Exists(metadata.Value))
        {
            return Path.GetFullPath(metadata.Value);
        }
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EOS.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    [Fact]
    public void 清理占位行前后的两份真实定义_新判据判等价_旧判据判不等价()
    {
        var before = AsDefinition(ReadFixture("pilot-1406-action-before.json"));
        var after = AsDefinition(ReadFixture("pilot-1406-action-after.json"));

        // 旧判据：逐字节不等价 ⇒ 发布侧会 MAX(VERSION)+1，证据跟着作废
        Assert.False(string.Equals(before, after, StringComparison.Ordinal));

        // 新判据：语义等价 ⇒ 发布侧复用当前版本，证据不受影响
        Assert.True(WorkbenchDefinitionContentComparer.AreEquivalent(before, after));

        // 更强的表述：两侧规范化后逐字节相同
        Assert.True(WorkbenchDefinitionContentComparer.TryNormalize(before, out var normalizedBefore));
        Assert.True(WorkbenchDefinitionContentComparer.TryNormalize(after, out var normalizedAfter));
        Assert.Equal(normalizedBefore, normalizedAfter);
    }

    [Fact]
    public void 同样的两份真实定义_真加一条公式行即判不等价()
    {
        var after = AsDefinition(ReadFixture("pilot-1406-action-after.json"));
        var withExtraRow = after.Replace(
            "\"ops\":[]",
            "\"ops\":[{\"opSeq\":1,\"targetTable\":\"PRODUCT\",\"targetField\":\"QTY\",\"opCode\":\"ACCUM\"}]",
            StringComparison.Ordinal);

        Assert.NotEqual(after, withExtraRow);
        Assert.False(WorkbenchDefinitionContentComparer.AreEquivalent(withExtraRow, after));
    }
}
