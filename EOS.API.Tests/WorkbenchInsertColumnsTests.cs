using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class WorkbenchInsertColumnsTests
{
    private static HashSet<string> Physical(params string[] columns) =>
        new(columns, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void FormKeys_KeepOrder_ServerExtrasAppendedSorted()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = 1, ["A"] = 2, ["CREATED_BY"] = "u", ["MODIFIED_BY"] = "u",
        };
        var result = WorkbenchCommandHandler.BuildInsertColumns(
            ["B", "A"], values, Physical("A", "B", "CREATED_BY", "MODIFIED_BY"), []);
        Assert.Equal(["B", "A", "CREATED_BY", "MODIFIED_BY"], result);
    }

    [Fact]
    public void GhostAndIllegalAndIdentityExtras_Excluded()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = 1, ["GHOST"] = 1, ["BAD COL"] = 1, ["ID"] = 1,
        };
        var result = WorkbenchCommandHandler.BuildInsertColumns(
            ["A"], values, Physical("A", "ID"), ["ID"]);
        Assert.Equal(["A"], result);
    }

    [Fact]
    public void EmptyFormAndNoExtras_YieldsEmpty()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["GHOST"] = 1,
        };
        Assert.Empty(WorkbenchCommandHandler.BuildInsertColumns(
            [], values, Physical("A"), []));
    }

    [Fact]
    public void DuplicateFormKeys_Deduplicated()
    {
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["A"] = 1 };
        var result = WorkbenchCommandHandler.BuildInsertColumns(
            ["A", "a"], values, Physical("A"), []);
        Assert.Equal(["A"], result);
    }
}
