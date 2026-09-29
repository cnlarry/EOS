using EOS.API.Features.Assistant.Diagnosis;
using EOS.API.Models;
using EOS.API.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EOS.API.Tests;

/// <summary>诊断相关测试的共用替身：不连库、不发 SQL，事实全部由夹具给出。</summary>
internal static class DiagnosisDoubles
{
    /// <summary>扮演"事实读取"的替身：事实由用例给定（真库读取路径另有实现）。</summary>
    internal sealed class FakeReader : IRecordDiagnosisReader
    {
        public RecordDiagnosisFacts? Facts { get; set; }

        public int Calls { get; private set; }

        public Task<RecordDiagnosisFacts?> LoadAsync(
            string userId, DiagnosisContext context, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(Facts);
        }
    }

    internal static RecordDiagnosisService Service(
        IRecordDiagnosisReader reader, Action<AssistantDiagnosisOptions>? configure = null)
    {
        var options = new AssistantDiagnosisOptions();
        configure?.Invoke(options);
        return new RecordDiagnosisService(
            reader,
            AssistantSituationDoubles.Budget(),
            Options.Create(options),
            NullLogger<RecordDiagnosisService>.Instance);
    }

    internal static WorkbenchDefinition Definition(
        int moduleId = 1204,
        string title = "产品BOM表",
        IReadOnlyList<string>? pk = null)
    {
        var order = pk ?? ["BOM_NO"];
        var fields = order
            .Select(key => new WorkbenchField(key, key, "nvarchar", 100, null, true))
            .ToArray();
        return new WorkbenchDefinition(
            moduleId, title, $"T{moduleId}", null, fields, [], null,
            HasAdd: true, HasEdit: true, DetailNoSave: false,
            MasterPkOrder: order, DetailNoFields: string.Empty, HasWorkflow: false);
    }

    internal static ModulePermission Permission(
        bool canBrowse = true, bool canEdit = true, bool canApprove = false,
        bool canDelete = false, bool canEndCase = false, bool canUnEndCase = false)
    {
        var rights = new ModuleRights(
            CanBrowse: canBrowse,
            CanViewCost: true,
            CanViewSecrecy: true,
            CanSetup: false,
            DeniedMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DeniedDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            CanAddNew: canEdit,
            CanEdit: canEdit,
            CanDelete: canDelete,
            CanApprove: canApprove,
            CanDeapprove: canApprove,
            CanEndCase: canEndCase,
            CanUnEndCase: canUnEndCase,
            CanFileView: false,
            CanFileUpda: false,
            CanFileEdit: false,
            CanFileDele: false,
            DenyNewMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyNewDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyModiMasterFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DenyModiDetailFields: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            DataFilter: string.Empty,
            ExecuteTag: "A");
        return new ModulePermission(rights);
    }

    internal static DiagnosisContext Context(
        WorkbenchDefinition? definition = null,
        ModulePermission? permission = null,
        IReadOnlyList<string>? keys = null,
        IReadOnlyList<string>? attemptedFields = null) =>
        new(definition ?? Definition(),
            permission ?? Permission(),
            keys ?? ["BOM-1"],
            attemptedFields ?? []);

    internal static RecordDiagnosisFacts Facts(
        IReadOnlyList<DiagnosisRule>? rules = null,
        IReadOnlyList<DiagnosisRuleSignal>? signals = null,
        IReadOnlyList<DiagnosisFieldFact>? fields = null,
        IReadOnlyList<DiagnosisProvenanceFact>? provenance = null,
        IReadOnlyList<DiagnosisEffectFact>? effects = null,
        DiagnosisFlowFact? flow = null,
        DiagnosisFailureFact? lastFailure = null,
        IReadOnlyList<string>? missing = null,
        bool confirmed = false,
        bool finished = false,
        IReadOnlyList<string>? recordKey = null) =>
        new(recordKey ?? ["BOM-1"],
            "BOM-1",
            confirmed,
            finished,
            rules ?? [],
            signals ?? [],
            fields ?? [],
            provenance ?? [],
            effects ?? [],
            flow,
            lastFailure,
            missing ?? []);
}
