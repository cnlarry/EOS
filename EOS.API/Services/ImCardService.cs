using EOS.API.Data;
using EOS.API.Models;

namespace EOS.API.Services;

/// <summary>业务卡片解析服务：类型白名单 + 复用工作台模块授权 + 服务端生成快照。</summary>
public interface IImCardService
{
    bool IsSupported(string cardType);

    /// <summary>解析卡片快照 JSON；类型不支持或当前用户无权限查看实体时返回 null。</summary>
    Task<string?> ResolveAsync(string cardType, string entityId, string userId, CancellationToken token);
}

public sealed class ImCardService(
    DocumentWorkbenchRepository repository,
    LegacyRightsRepository rightsRepository,
    ILogger<ImCardService> logger) : IImCardService
{
    /// <summary>卡片类型白名单（cardType → 工作台模块标题）。新增类型必须在这里登记并配套客户端渲染器。</summary>
    private static readonly Dictionary<string, string> AllowedCardTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["purchase-order"] = "采购单",
        ["inventory-count"] = "库存盘点单",
    };

    public bool IsSupported(string cardType)
        => !string.IsNullOrWhiteSpace(cardType) && AllowedCardTypes.ContainsKey(cardType);

    public async Task<string?> ResolveAsync(
        string cardType, string entityId, string userId, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(entityId) ||
            !AllowedCardTypes.TryGetValue(cardType ?? string.Empty, out var moduleTitle))
        {
            return null;
        }

        var moduleId = await repository.FindGenericModuleIdByTitleAsync(moduleTitle, token);
        if (moduleId is null)
        {
            logger.LogWarning("卡片解析失败 cardType={CardType} reason=ModuleNotFound", cardType);
            return null;
        }

        var rights = await rightsRepository.GetAsync(userId.Trim(), moduleId.Value, token);
        if (!rights.CanBrowse)
        {
            logger.LogWarning("卡片解析失败 cardType={CardType} userId={UserId} reason=Forbidden", cardType, userId);
            return null;
        }

        var definition = await repository.GetDefinitionAsync(
            moduleId.Value,
            userId.Trim(),
            rights.ExecuteTag,
            rights.CanViewCost,
            rights.CanViewSecrecy,
            rights.DeniedMasterFields,
            rights.DeniedDetailFields,
            token);
        if (definition is null)
        {
            return null;
        }

        var data = await repository.GetRowsAsync(
            definition,
            detail: false,
            keys: new Dictionary<string, string>(),
            page: 1,
            pageSize: 1,
            token,
            query: null,
            keyword: entityId.Trim(),
            sortField: null,
            sortDirection: null);
        var row = data.Rows.FirstOrDefault();
        if (row is null)
        {
            logger.LogInformation("卡片解析失败 cardType={CardType} entityId={EntityId} reason=NotFound", cardType, entityId);
            return null;
        }

        var labels = definition.MasterFields.ToDictionary(
            field => field.Key, field => field.Label, StringComparer.OrdinalIgnoreCase);
        return ImCardSnapshot.Build(cardType!, moduleTitle, entityId.Trim(), labels, row);
    }
}
