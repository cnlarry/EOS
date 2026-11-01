using System.Text.Json;
using EOS.API.Data;
using EOS.API.Data.DocumentActions;
using EOS.API.Data.Workbench;

namespace EOS.API.Features.Assistant.Tools;

/// <summary>一个可执行（且当前用户获授权）的单据动作。</summary>
public sealed record AssistantDocumentActionInfo(
    int ModuleId,
    string ModuleTitle,
    string Key,
    string Label,
    bool ConfirmTag,
    string Placement,
    JsonElement? Params);

public sealed record AssistantDocumentActionListing(
    string ModuleTitle,
    IReadOnlyList<AssistantDocumentActionInfo> Actions);

/// <summary>
/// 自定义单据动作的窄接口。列动作、探路与执行都复用
/// <see cref="WorkbenchAccessPolicy.AuthorizeActionAsync"/>、按钮级授权
/// （<see cref="DocumentActionAuthorization"/>）与 <see cref="DocumentActionExecutor"/>，
/// 助手侧不重新判定权限，也不新增写路径。
/// </summary>
public interface IDocumentActionGateway
{
    Task<AssistantDocumentActionListing?> ListAsync(string userId, int moduleId, CancellationToken token);

    Task<AssistantDocumentActionInfo?> GetAsync(
        string userId, int moduleId, string actionKey, CancellationToken token);

    Task<DocumentActionExecution?> ExecuteAsync(
        string userId, string employeeName, int moduleId, string actionKey,
        IReadOnlyList<string> keys, JsonElement? parameters, bool confirm,
        string idempotencyKey, CancellationToken token);
}

public sealed class DocumentActionGateway(
    WorkbenchAccessPolicy policy,
    DocumentActionAuthorization authorization,
    DocumentActionRegistry registry,
    DocumentActionExecutor executor) : IDocumentActionGateway
{
    public async Task<AssistantDocumentActionListing?> ListAsync(
        string userId, int moduleId, CancellationToken token)
    {
        var access = await policy.AuthorizeActionAsync(userId, moduleId, token);
        if (!access.Allowed || access.Value is null)
        {
            return null;
        }

        var definition = access.Value.Definition;
        var configured = DocumentActionConfigs.Parse(definition.BusinessActions);
        if (configured.Count == 0)
        {
            return new AssistantDocumentActionListing(definition.Title, []);
        }

        // 按钮级授权 fail-closed：没授权的动作连名字都不下发。
        var authorized = await authorization.AuthorizedKeysAsync(
            userId, moduleId, configured.Select(config => config.Key).ToList(), token);
        var actions = configured
            .Where(config => authorized.Contains(config.Key))
            .Select(config => new AssistantDocumentActionInfo(
                definition.ModuleId,
                definition.Title,
                config.Key,
                config.Label.Length > 0 ? config.Label : registry.LabelOf(config.Key),
                config.ConfirmTag,
                registry.PlacementOf(config.Key),
                config.Params))
            .ToList();
        return new AssistantDocumentActionListing(definition.Title, actions);
    }

    public async Task<AssistantDocumentActionInfo?> GetAsync(
        string userId, int moduleId, string actionKey, CancellationToken token)
    {
        var listing = await ListAsync(userId, moduleId, token);
        return listing?.Actions.FirstOrDefault(action =>
            action.Key.Equals(actionKey.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public async Task<DocumentActionExecution?> ExecuteAsync(
        string userId, string employeeName, int moduleId, string actionKey,
        IReadOnlyList<string> keys, JsonElement? parameters, bool confirm,
        string idempotencyKey, CancellationToken token)
    {
        var access = await policy.AuthorizeActionAsync(userId, moduleId, token);
        if (!access.Allowed || access.Value is null)
        {
            return null;
        }

        var (definition, form, rights) = (access.Value.Definition, access.Value.Form, access.Value.Rights);
        return await executor.ExecuteAsync(
            definition,
            form,
            actionKey.Trim(),
            new DocumentActionRequest(keys, parameters, confirm),
            userId,
            employeeName,
            rights.DataFilter,
            idempotencyKey,
            token);
    }
}
