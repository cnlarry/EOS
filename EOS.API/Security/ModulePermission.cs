using EOS.API.Models;

namespace EOS.API.Security;

/// <summary>
/// 模块权限结果（ADR-005 §6）：包装 LegacyModuleRights，提供命名动作判定与
/// 字段级权限/数据范围数据（成本/保密/禁止字段/DATA_FILTER/EXEC_TAG）的下发。
/// 控制器只消费本类型，不直接接触 LegacyRightsRepository。
/// </summary>
public sealed class ModulePermission(LegacyModuleRights rights)
{
    public LegacyModuleRights Rights { get; } = rights;

    public bool CanBrowse => Rights.CanBrowse;
    public bool CanAddNew => Rights.CanAddNew;
    public bool CanEdit => Rights.CanEdit;
    public bool CanDelete => Rights.CanDelete;
    public bool CanApprove => Rights.CanApprove;
    public bool CanDeapprove => Rights.CanDeapprove;
    public bool CanEndCase => Rights.CanEndCase;
    public bool CanUnEndCase => Rights.CanUnEndCase;
    public bool CanSetup => Rights.CanSetup;
    public bool CanFileView => Rights.CanFileView;
    public bool CanFileUpda => Rights.CanFileUpda;
    public bool CanFileEdit => Rights.CanFileEdit;
    public bool CanFileDele => Rights.CanFileDele;

    public bool Can(PermissionAction action) => action switch
    {
        PermissionAction.Browse => CanBrowse,
        PermissionAction.AddNew => CanAddNew,
        PermissionAction.Edit => CanEdit,
        PermissionAction.Delete => CanDelete,
        PermissionAction.Approve => CanApprove,
        PermissionAction.Deapprove => CanDeapprove,
        PermissionAction.EndCase => CanEndCase,
        PermissionAction.UnEndCase => CanUnEndCase,
        PermissionAction.Setup => CanSetup,
        PermissionAction.FileView => CanFileView,
        PermissionAction.FileUpload => CanFileUpda,
        PermissionAction.FileEdit => CanFileEdit,
        PermissionAction.FileDelete => CanFileDele,
        _ => false,
    };
}
