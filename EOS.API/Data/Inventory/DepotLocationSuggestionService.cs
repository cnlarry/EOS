using System.Text.Json;
// 「库别列 → 库位列」的派生规则只有一处（InventoryMovePlan），批核期与保存期共用它；
// 另外写一份 `DEPOT_ID → LOCATION_NO` 的拼法看着更省事，但两份规则迟早分叉。
using EOS.API.Data.Effects.ServiceEffectHandlers;
using EOS.API.Models;
using Microsoft.Data.SqlClient;

namespace EOS.API.Data.Inventory;

/// <summary>
/// 保存期的「建议位置」（D1-d / WS-16）：`LOCATION_MODE = 2`「建议」档下，
/// 明细行没填位置时，**保存时**就把系统建议的位置写进去——用户在草稿上看得见，也改得动。
/// </summary>
/// <remarks>
/// 三条边界，都是刻意的：
/// <list type="bullet">
/// <item>**只做档 2**。档 0/1 是"位置由人定"，系统替人填会凭空改变库存键（R1 等价性）；
/// 档 3「强制」也不做——保存期拒绝会让草稿存不下来（D1-a 刻意如此），
/// 而替它填上等于把"强制"悄悄降级成"建议"，两条都不可以。</item>
/// <item>只填**空 / 哨兵**的位置：单据里已经写了位置的照单据走——与人填的东西冲突时，人赢。</item>
/// <item>口径与批核期**同一处**：候选来自 <see cref="DepotLocationService"/>、决策来自
/// <see cref="DepotLocationResolver"/>、档位来自 <see cref="DepotStockPolicyService"/>。
/// 保存期另写一份解析会立刻与批核期分叉（"保存时看到的位置"和"记账落的位置"不是同一个）。</item>
/// </list>
/// 保存期只处理**入库方向**（`direction = IN`）：出库的位置是"从哪儿拿"，必须由单据指明。
/// </remarks>
public static class DepotLocationSuggestionService
{
    /// <summary>库存明细表的料号列名在 覆盖的 24 张表里统一是 `PRO_NO`；取不到就跳过该行。</summary>
    private const string ProductField = "PRO_NO";

    /// <summary>本次保存实际写入明细的集合 + 给用户看的说明（每个"库别→位置"一条，不按行刷屏）。</summary>
    public sealed record SuggestionResult(
        IReadOnlyList<IReadOnlyDictionary<string, string?>>? Details,
        IReadOnlyList<SaveWarning> Warnings);

    public static async Task<SuggestionResult> FillAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        WorkbenchDefinition definition,
        IReadOnlyList<IReadOnlyDictionary<string, string?>>? details,
        DepotStockPolicyService policies,
        CancellationToken token)
    {
        if (details is null || details.Count == 0)
        {
            return new SuggestionResult(details, []);
        }

        var legs = ReadInboundLegs(definition);
        if (legs.Count == 0)
        {
            return new SuggestionResult(details, []);
        }

        var rows = new List<IReadOnlyDictionary<string, string?>>(details.Count);
        var warnings = new List<SaveWarning>();
        // 同一库别在一次保存里只问一次策略（明细几十行不该问几十次库）
        var modes = new Dictionary<string, (int LocationMode, string StorageMode)>(StringComparer.OrdinalIgnoreCase);
        var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in details)
        {
            var filled = new Dictionary<string, string?>(row, StringComparer.OrdinalIgnoreCase);
            var changed = false;

            foreach (var (depotField, locationField) in legs)
            {
                if (!IsBlank(filled.GetValueOrDefault(locationField)))
                {
                    continue;   // 单据已给位置：人填的优先
                }
                var depotId = filled.GetValueOrDefault(depotField)?.Trim();
                var proNo = filled.GetValueOrDefault(ProductField)?.Trim();
                if (string.IsNullOrEmpty(depotId) || string.IsNullOrEmpty(proNo))
                {
                    continue;   // 库别或料号还没填，无从建议（保存期允许草稿不完整）
                }

                if (!modes.TryGetValue(depotId, out var policy))
                {
                    var resolved = await policies.ResolveAsync(depotId, connection, transaction, token);
                    policy = (resolved.LocationMode, resolved.StorageMode);
                    modes[depotId] = policy;
                }
                if (policy.LocationMode != SuggestionLocationMode)
                {
                    continue;   // 档 0/1 位置由人定；档 3 保持"批核期拒绝"的行为不变
                }

                var resolution = await DepotLocationService.ResolveInboundAsync(
                    connection, transaction, policy.LocationMode, policy.StorageMode, depotId, proNo,
                    DepotLocationResolver.DefaultSentinel, token);
                if (resolution.IsSentinel)
                {
                    continue;   // 解析不出来就保持空着，由批核期的既有口径处理（缺配置不是保存失败）
                }

                filled[locationField] = resolution.LocationNo;
                changed = true;
                if (reported.Add($"{depotId}|{resolution.LocationNo}"))
                {
                    warnings.Add(new SaveWarning("LOCATION_SUGGESTED",
                        $"库别 {depotId} 未填位置，已按建议填入 {resolution.LocationNo}（位置档 2「建议」，可自行修改）。"));
                }
            }

            rows.Add(changed ? filled : row);
        }

        return new SuggestionResult(rows, warnings);
    }

    /// <summary>档 2 = 「建议」：系统给出位置但不拦人。</summary>
    private const int SuggestionLocationMode = 2;

    private static bool IsBlank(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || string.Equals(value.Trim(), DepotLocationResolver.DefaultSentinel, StringComparison.Ordinal);

    /// <summary>
    /// 从模块的 `inventory-move` 动作里读出「库别列 → 库位列」的对子（与批核期同一处派生规则）。
    /// 只取入库方向：出库的位置必须由单据指明。
    /// </summary>
    private static IReadOnlyList<(string DepotField, string LocationField)> ReadInboundLegs(
        WorkbenchDefinition definition)
    {
        if (definition.BusinessActions is not { ValueKind: JsonValueKind.Array } actions)
        {
            return [];
        }
        var legs = new List<(string, string)>();
        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            // 属性名取快照里的实际写法 `effectKey`（不是 `effect`）；停用的动作不参与
            if (!action.TryGetProperty("effectKey", out var effect) || effect.ValueKind != JsonValueKind.String
                || !string.Equals(effect.GetString(), "inventory-move", StringComparison.Ordinal))
            {
                continue;
            }
            if (action.TryGetProperty("enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False)
            {
                continue;
            }
            if (ReadParams(action) is not { } parameters)
            {
                continue;
            }
            var direction = parameters.TryGetProperty("direction", out var d) && d.ValueKind == JsonValueKind.String
                ? d.GetString()?.Trim().ToUpperInvariant()
                : null;
            if (direction != "IN")
            {
                continue;
            }
            var depotField = parameters.TryGetProperty("depotField", out var df) && df.ValueKind == JsonValueKind.String
                ? df.GetString()!.Trim()
                : "DEPOT_ID";
            if (depotField.Length == 0)
            {
                depotField = "DEPOT_ID";
            }
            var leg = (depotField, InventoryMovePlan.DeriveLocationField(depotField));
            if (!legs.Contains(leg))
            {
                legs.Add(leg);
            }
        }
        return legs;
    }

    /// <summary>
    /// 动作参数在**快照里是字符串**（内含 JSON 文本），在别的来源可能是对象——两种都认。
    /// 只认一种会在"用快照跑"与"用配置跑"之间产生行为差异，而那正是最难查的一类。
    /// </summary>
    private static JsonElement? ReadParams(JsonElement action)
    {
        if (!action.TryGetProperty("params", out var raw))
        {
            return null;
        }
        return raw.ValueKind switch
        {
            JsonValueKind.Object => raw,
            JsonValueKind.String => ParseObject(raw.GetString()),
            _ => null,
        };
    }

    private static JsonElement? ParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;    // 参数不是合法 JSON：当作"读不懂"，不猜
        }
    }
}
