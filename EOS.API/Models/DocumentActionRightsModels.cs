using System.Text.Json.Serialization;

namespace EOS.API.Models;

/// <summary>
/// 自定义按钮授权的矩阵行（用户/组权限页的「按钮权限」页签）。
/// 授权是 fail-closed 名单：没有名单行即不可点；个人名单行一旦存在，该模块整个个人通道接管组通道
/// （与模块/报表权限同款：个人覆盖组，多组经 SYSDG_USER 落到人后取 OR）。
/// </summary>
public sealed record DocumentActionRightsRow(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("moduleTitle")] string ModuleTitle,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("label")] string Label,
    // 该目标（用户/组）自身的名单行是否授权。
    [property: JsonPropertyName("granted")] bool Granted,
    // 该目标在该模块是否已存在名单行（个人通道是否已接管组通道）。
    [property: JsonPropertyName("hasOverrideRow")] bool HasOverrideRow,
    // 用户矩阵：其所在组是否已授权（组矩阵恒为 false）。
    [property: JsonPropertyName("groupGranted")] bool GroupGranted);

/// <summary>保存自定义按钮授权（按 (模块, 按钮) 重写该目标的名单行）。</summary>
public sealed record SaveDocumentActionRightsRequest(
    [property: JsonPropertyName("items")] IReadOnlyList<DocumentActionRightsInput> Items);

public sealed record DocumentActionRightsInput(
    [property: JsonPropertyName("moduleId")] int ModuleId,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("granted")] bool Granted);
