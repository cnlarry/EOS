using EOS.API.Security;

namespace EOS.API.Features.Assistant.Tools;

public abstract class AssistantSchemaToolBase(IPermissionService permissions) : AssistantToolBase
{
    protected const int SchemaPermissionModuleId = 2302;

    protected async Task<ToolExecutionResult?> RequireSetupAsync(string userId, CancellationToken token)
    {
        var permission = await permissions.GetAsync(userId, SchemaPermissionModuleId, token);
        return permission.CanSetup ? null : ToolExecutionResult.Deny("用户没有全局 schema 内省所需的 CanSetup 权限。");
    }
}
