namespace EOS.API.Security;

/// <summary>
/// 模块级权限动作：
/// 控制器用命名动作代替魔法布尔判断，权限资源由 IPermissionService 集中解释。
/// </summary>
public enum PermissionAction
{
    Browse,
    AddNew,
    Edit,
    Delete,
    Approve,
    Deapprove,
    EndCase,
    UnEndCase,
    Setup,
    FileView,
    FileUpload,
    FileEdit,
    FileDelete,
}
