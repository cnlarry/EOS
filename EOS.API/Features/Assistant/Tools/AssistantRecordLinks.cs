namespace EOS.API.Features.Assistant.Tools;

/// <summary>
/// 助手工具输出里的**站内深链**：由服务端按模块号与主键生成，模型只负责原样转述，
/// 不自行拼接（模型编出来的 URL 既可能 404，也可能指向别的模块）。
///
/// <para>
/// 编码口径与前端 <c>workbenchPath.ts</c> 同一套：主键每段单独 URL 编码、段间用 <c>/</c>，
/// 空段保留（主键列值可以为空）。尾随空格按同一规则修剪，避免定长字符主键带出一长串 %20。
/// </para>
/// </summary>
public static class AssistantRecordLinks
{
    public static string View(int moduleId, IReadOnlyList<string> keys) => Build("view", moduleId, keys);

    public static string Edit(int moduleId, IReadOnlyList<string> keys) => Build("edit", moduleId, keys);

    public static string List(int moduleId) => $"/workbench/{moduleId}";

    /// <summary>打印页：主键按既有打印页约定以 JSON 数组放在 query 的 key 里。</summary>
    public static string Print(int moduleId, IReadOnlyList<string> keys)
    {
        var normalized = keys.Select(key => (key ?? string.Empty).TrimEnd()).ToArray();
        return $"/print/{moduleId}?key={Uri.EscapeDataString(System.Text.Json.JsonSerializer.Serialize(normalized))}";
    }

    private static string Build(string action, int moduleId, IReadOnlyList<string> keys)
    {
        var segments = keys
            .Select(key => Uri.EscapeDataString((key ?? string.Empty).TrimEnd()))
            .ToArray();
        return segments.Length == 0
            ? $"/workbench/{moduleId}/{action}"
            : $"/workbench/{moduleId}/{action}/{string.Join('/', segments)}";
    }
}
