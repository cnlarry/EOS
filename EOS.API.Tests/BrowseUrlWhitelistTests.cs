using EOS.API.Data;
using Xunit;

namespace EOS.API.Tests;

public class BrowseUrlWhitelistTests
{
    private static IReadOnlySet<string> Fields(params string[] values) =>
        values.ToHashSet(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ValidTemplate_WithAllowedPlaceholders_IsKept()
    {
        const string url = "~/Admin/MagEmployee?IDX=EMP_ID={CONFIRM_PERSON}";
        Assert.Equal(url, WorkbenchDefinitionBuilder.SanitizeBrowseUrl(url, Fields("CONFIRM_PERSON")));
    }

    [Fact]
    public void MultiplePlaceholders_AllAllowed_IsKept()
    {
        const string url = "~/BOM/Product?PRO={PRO_NO}&M={PRODUCE_NO}";
        Assert.Equal(url, WorkbenchDefinitionBuilder.SanitizeBrowseUrl(url, Fields("PRO_NO", "PRODUCE_NO")));
    }

    [Fact]
    public void PlaceholderMatching_IsCaseInsensitive()
    {
        const string url = "~/Admin/MagCurr?IDX=CURR_ID={curr_id}";
        Assert.Equal(url, WorkbenchDefinitionBuilder.SanitizeBrowseUrl(url, Fields("CURR_ID")));
    }

    [Fact]
    public void UnknownPlaceholder_IsRejected()
    {
        Assert.Null(WorkbenchDefinitionBuilder.SanitizeBrowseUrl(
            "~/Admin/MagEmployee?IDX=EMP_ID={HACKED_FIELD}", Fields("CONFIRM_PERSON")));
    }

    [Fact]
    public void MixedPlaceholders_OneUnknown_IsRejected()
    {
        Assert.Null(WorkbenchDefinitionBuilder.SanitizeBrowseUrl(
            "~/x?a={A}&b={UNKNOWN}", Fields("A", "B")));
    }

    [Fact]
    public void NonRelativeUrl_IsRejected()
    {
        Assert.Null(WorkbenchDefinitionBuilder.SanitizeBrowseUrl("http://evil.example/x?k={A}", Fields("A")));
        Assert.Null(WorkbenchDefinitionBuilder.SanitizeBrowseUrl("/Admin/x?k={A}", Fields("A")));
        Assert.Null(WorkbenchDefinitionBuilder.SanitizeBrowseUrl("javascript:alert(1)", Fields("A")));
    }

    [Fact]
    public void InvalidPlaceholderToken_IsRejected()
    {
        Assert.Null(WorkbenchDefinitionBuilder.SanitizeBrowseUrl("~/x?k={A B}", Fields("A", "B")));
        Assert.Null(WorkbenchDefinitionBuilder.SanitizeBrowseUrl("~/x?k={}", Fields()));
    }

    [Fact]
    public void EmptyOrNull_ReturnsNull()
    {
        Assert.Null(WorkbenchDefinitionBuilder.SanitizeBrowseUrl(null, Fields("A")));
        Assert.Null(WorkbenchDefinitionBuilder.SanitizeBrowseUrl("  ", Fields("A")));
    }
}
