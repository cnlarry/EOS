using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class ChooserColumnSelectorTests
{
    private static FormChooserColumnRow Row(string key, bool cost = false, bool secrecy = false) =>
        new(key, $"label-{key}", "nvarchar", cost, secrecy);

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
}
