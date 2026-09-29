using EOS.API.Models;

namespace EOS.API.Data.Workbench;

/// <summary>
/// 定义装配出口：策略层按权限上下文取模块定义与统一表单定义。
/// 与具体仓储实现解耦，使等价性测试可以注入基准实现逐格比对判定结果。
/// </summary>
public interface IWorkbenchDefinitionSource
{
    Task<WorkbenchDefinition?> GetDefinitionAsync(
        int moduleId, string userId, string? execTag, bool canViewCost, bool canViewSecrecy,
        IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
        CancellationToken token);

    Task<FormDefinition?> GetFormDefinitionAsync(
        WorkbenchDefinition definition, string userId, string mode,
        bool canViewCost, bool canViewSecrecy,
        IReadOnlySet<string> deniedMasterFields, IReadOnlySet<string> deniedDetailFields,
        IReadOnlySet<string> deniedNewMasterFields, IReadOnlySet<string> deniedNewDetailFields,
        IReadOnlySet<string> deniedModiMasterFields, IReadOnlySet<string> deniedModiDetailFields,
        CancellationToken token,
        bool canAddNew = false, bool canEdit = false, bool canDelete = false,
        bool canApprove = false, bool canDeapprove = false,
        bool canEndCase = false, bool canUnEndCase = false,
        bool canFileView = false, bool canFileUpda = false,
        bool canFileEdit = false, bool canFileDele = false, bool canSetup = false);
}
