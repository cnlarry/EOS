using System.Text.Json;
using EOS.API.Data.Effects.ServiceEffectHandlers;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// set-state deapprove semantics: the reverse structure drives the cleared inverse
/// ("clear-finish" flips booleans, clears strings and datetimes), while none/no-reverse
/// is a no-op. The pure value mapper is exercised here; the handler-level gating is
/// covered by the shadow comparison once module data exists.
/// </summary>
public class SetStateHandlerTests
{
    private static JsonElement Value(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Clear_finish_reverse_kind_registered()
    {
        Assert.Contains("clear-finish", EOS.API.Data.EffectStructSchemas.AllReverseKinds());
        Assert.Empty(EOS.API.Data.EffectStructSchemas.ValidateReverse(
            """{"kind":"clear-finish","note":"解批清结案"}"""));
    }

    [Theory]
    [InlineData("true", "1")]
    [InlineData("false", "0")]
    [InlineData("5", "5")]
    [InlineData("\"now\"", "SYSDATETIME()")]
    [InlineData("\"SYSTEM\"", "@sv_")]
    public void Forward_value_maps_to_placement(string json, string fragment)
    {
        var (sql, needsParameter) = SetStateHandler.ResolveStateValue(Value(json), clear: false);
        Assert.Equal(fragment, sql);
        Assert.Equal(fragment.StartsWith("@"), needsParameter);
    }

    [Theory]
    [InlineData("true", "0")]
    [InlineData("false", "1")]
    [InlineData("5", "0")]
    [InlineData("\"now\"", "NULL")]
    [InlineData("\"SYSTEM\"", "@cv_")]
    public void Clear_value_maps_to_cleared_inverse(string json, string fragment)
    {
        var (sql, needsParameter) = SetStateHandler.ResolveStateValue(Value(json), clear: true);
        Assert.Equal(fragment, sql);
        Assert.Equal(fragment.StartsWith("@"), needsParameter);
    }
}