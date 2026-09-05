using EOS.API.Data;
using EOS.API.Features.Assistant.Tools;
using EOS.API.Models;

namespace EOS.API.Features.Assistant.Kb;

/// <summary>
/// Single rule set for re-checking business references inside knowledge-base content:
/// CanBrowse gate, permission-filtered workbench definition, master-key length match,
/// then row-level data-scope lookup. Shared by ingest, the kb_search tool and the HTTP
/// search endpoint so permission semantics cannot drift between entry points.
/// </summary>
public static class KbReferenceVerifier
{
    /// <summary>Returns null when the reference passes; otherwise a reason (module id added by caller).</summary>
    public static async Task<string?> FirstDeniedReasonAsync(
        string userId, BusinessReference reference, ModuleRights rights,
        IWorkbenchSearchGateway gateway, CancellationToken token)
    {
        if (!rights.CanBrowse) return "无浏览权限";
        var definition = await gateway.GetDefinitionAsync(reference.ModuleId, userId,
            rights.ExecuteTag, rights.CanViewCost, rights.CanViewSecrecy,
            rights.DeniedMasterFields, rights.DeniedDetailFields, token);
        if (definition is null) return "不是可查询模块";
        if (reference.Keys.Count != definition.MasterPkOrder.Count) return "主键长度不符";
        var rows = await gateway.GetExportRowsByKeysAsync(definition, [reference.Keys], token,
            dataFilter: rights.DataFilter);
        return rows.Count == 0 ? "记录不在数据范围内" : null;
    }

    /// <summary>
    /// Drops search hits whose embedded business references fail verification, mirroring
    /// the kb_search tool: fragments without references are kept without extra checks.
    /// verifyAll returns null when every reference passes.
    /// </summary>
    public static async Task<IReadOnlyList<KbHit>> FilterHitsAsync(
        string userId, IReadOnlyList<KbHit> hits,
        Func<string, IReadOnlyList<BusinessReference>, CancellationToken, Task<string?>> verifyAll,
        CancellationToken token)
    {
        var kept = new List<KbHit>();
        foreach (var hit in hits)
        {
            var references = KbIngestScanner.ExtractReferences(hit.Content);
            if (references.Count > 0 && await verifyAll(userId, references, token) is not null) continue;
            kept.Add(hit);
        }

        return kept;
    }
}
