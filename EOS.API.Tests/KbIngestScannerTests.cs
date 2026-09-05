using EOS.API.Features.Assistant.Kb;
using Xunit;

namespace EOS.API.Tests;

public sealed class KbIngestScannerTests
{
    [Fact]
    public void Scan_FlagsSensitiveKinds()
    {
        var result = KbIngestScanner.Scan("联系人电话 13800138000，身份证 110101199001011234。");

        Assert.Contains("MOBILE", result.SensitiveKinds);
        Assert.Contains("ID_CARD", result.SensitiveKinds);
        Assert.Empty(result.References);
    }

    [Fact]
    public void Scan_FlagsBankCard_AndCredential()
    {
        var result = KbIngestScanner.Scan("卡号 6222021234567890，配置 password=secret。");

        Assert.Contains("BANK_CARD", result.SensitiveKinds);
        Assert.Contains("CREDENTIAL", result.SensitiveKinds);
    }

    [Fact]
    public void Scan_CleanText_Passes()
    {
        var result = KbIngestScanner.Scan("送货单由客户订单转入，批核后不得直接删除。");

        Assert.Empty(result.SensitiveKinds);
        Assert.Empty(result.References);
    }

    [Fact]
    public void ExtractReferences_PairsModuleWithFollowingKeys()
    {
        var references = KbIngestScanner.ExtractReferences(
            "参见 module=1405 的单据 _keys=[\"DD\",\"26080001\"]，以及 module=1606 _keys=[\"CG\",\"1\"]。");

        Assert.Equal(2, references.Count);
        Assert.Equal(1405, references[0].ModuleId);
        Assert.Equal(["DD", "26080001"], references[0].Keys);
        Assert.Equal(1606, references[1].ModuleId);
    }

    [Fact]
    public void ExtractReferences_IgnoresUnpairable_AndMalformed()
    {
        Assert.Empty(KbIngestScanner.ExtractReferences("module=1405 没有主键。"));
        Assert.Empty(KbIngestScanner.ExtractReferences("module=0 的单据 _keys=[\"A\"]。"));
        Assert.Empty(KbIngestScanner.ExtractReferences("module=1405 _keys=[\"A\",]。"));
        Assert.Empty(KbIngestScanner.ExtractReferences("module=1405 _keys=[]。"));
    }
}
