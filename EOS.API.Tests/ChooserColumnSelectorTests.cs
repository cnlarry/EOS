using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ChooserColumnSelectorTests
{
    private static FormChooserColumnRow Row(string key, bool cost = false, bool secrecy = false, bool visible = true, string? displayFormat = null) =>
        new(key, $"label-{key}", "nvarchar", cost, secrecy, 0, visible, displayFormat);

    [Fact]
    public void InvisibleColumns_ExcludedFromDisplayButNotFromWhitelist()
    {
        var rows = new[] { Row("CLIENT_ID", visible: false), Row("PRO_NO"), Row("PRO_NAME") };
        var result = ChooserColumnSelector.Select(rows, true, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["PRO_NO", "PRO_NAME"], result.Select(column => column.Key));
        // 白名单（调用方用全部 rows）仍包含不可见列，CHOOSE_FILTER 可引用
        Assert.Contains(rows, row => row.Key.Equals("CLIENT_ID", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CostAndSecrecyColumns_FilteredByRights()
    {
        var rows = new[] { Row("A", cost: true), Row("B", secrecy: true), Row("C") };
        var without = ChooserColumnSelector.Select(rows, false, false, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["C"], without.Select(column => column.Key));
        var with = ChooserColumnSelector.Select(rows, true, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(3, with.Count);
    }

    [Fact]
    public void DeniedColumns_AreRemoved()
    {
        var result = ChooserColumnSelector.Select(
            new[] { Row("A"), Row("B"), Row("C") },
            true,
            true,
            new HashSet<string>(new[] { "B" }, StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["A", "C"], result.Select(column => column.Key));
    }

    [Fact]
    public void MaxColumns_IsRespected()
    {
        var rows = Enumerable.Range(0, 10).Select(index => Row($"F{index}")).ToArray();
        var result = ChooserColumnSelector.Select(rows, true, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase), max: 4);
        Assert.Equal(4, result.Count);
    }

    [Fact]
    public void DisplayFormat_IsCarriedThrough()
    {
        var rows = new[] { Row("AMOUNT", displayFormat: "0.##"), Row("NAME") };
        var result = ChooserColumnSelector.Select(rows, true, true, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("0.##", result.Single(column => column.Key == "AMOUNT").DisplayFormat);
        Assert.Null(result.Single(column => column.Key == "NAME").DisplayFormat);
    }
}
