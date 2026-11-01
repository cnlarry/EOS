using System.Text.Json;
using EOS.API.Features.Assistant.Parameters;
using EOS.API.Features.Assistant.Tools;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 评估样本的"期望工具链"可判定性：`expect_tools` 必须指向真实注册的工具，且在样本声明的
/// 页面类型下确实可见；`reviewed:true` 的样本还必须写清期望答案。
///
/// <para>
/// 为什么需要这一层：样本只做"来源锚定"时，工具改名/被裁出某页面后样本**照样绿**——
/// 它锚的是文档与类型名，不是"这条问句真的能这样被回答"。这里补的是那一段：
/// 断言"样本期望的工具在这条问句的页面上摆得到模型面前"。
/// </para>
///
/// <para>
/// 它仍然**不判模型答得对不对**（那要 judge）；判的是"样本本身没有自相矛盾"。
/// </para>
/// </summary>
public sealed class AssistantEvalToolExpectationTests
{
    /// <summary>
    /// 批核族与权限授予类**在助手侧没有注册项**（红线）。这里显式列名，
    /// 是为了让"将来顺手加进来"变成一条红灯，而不是一句注释。
    /// </summary>
    private static readonly string[] RedLineTools =
    [
        "approve_record", "deapprove_record", "end_case", "unend_case",
        "batch_approve", "grant_module_rights", "set_user_permission", "apply_permission",
    ];

    [Fact]
    public void Expect_Tools_Must_Be_Registered_And_Offered_On_Declared_Page()
    {
        var issues = new List<string>();
        var samplesWithExpectations = 0;
        var distinctTools = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in SeedFiles())
        {
            var layer = Path.GetFileName(Path.GetDirectoryName(file)!) ?? string.Empty;
            var name = layer + "/" + Path.GetFileName(file);
            var lineNumber = 0;
            foreach (var raw in File.ReadAllLines(file))
            {
                lineNumber++;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var where = $"{name}:{lineNumber}";

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(raw);
                }
                catch (JsonException ex)
                {
                    issues.Add($"{where} 不是合法 JSON：{ex.Message}");
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;

                    // 诊断层的"审阅"语义是另一套：样本由 MODULE_VALIDATION_RULE 枚举而来，
                    // 审阅确认的是"规则仍在库、消息对得上、归因唯一"，不是"该用哪个工具"。
                    // 它的完整性由 DiagnosisGoldenSetTests 断言（reviewedBy / reviewedAt 必填）。
                    if (string.Equals(layer, "diagnosis", StringComparison.OrdinalIgnoreCase))
                    {
                        if (IsReviewed(root)
                            && !(HasText(root, "reviewedBy") && HasText(root, "reviewedAt")))
                        {
                            issues.Add($"{where} 标记 reviewed:true 但缺 reviewedBy/reviewedAt");
                        }
                        continue;
                    }

                    var hasExpectation = root.TryGetProperty("expect_tools", out var expectTools);
                    var hasPage = root.TryGetProperty("page", out var pageElement);
                    var page = hasPage && pageElement.ValueKind == JsonValueKind.String
                        ? pageElement.GetString()
                        : null;

                    if (!hasExpectation)
                    {
                        if (hasPage)
                        {
                            issues.Add($"{where} 声明了 page 却没有 expect_tools（等于什么都没断言）");
                        }
                        // reviewed:true 必须同时写清期望答案与工具链
                        if (IsReviewed(root))
                        {
                            issues.Add($"{where} 标记 reviewed:true 但缺 expect_tools");
                        }
                        continue;
                    }

                    samplesWithExpectations++;
                    if (expectTools.ValueKind != JsonValueKind.Array || expectTools.GetArrayLength() == 0)
                    {
                        issues.Add($"{where} expect_tools 必须是非空数组");
                        continue;
                    }

                    foreach (var tool in expectTools.EnumerateArray())
                    {
                        if (tool.ValueKind != JsonValueKind.String)
                        {
                            issues.Add($"{where} expect_tools 里出现非字符串");
                            continue;
                        }
                        var toolName = tool.GetString() ?? string.Empty;
                        distinctTools.Add(toolName);

                        if (RedLineTools.Contains(toolName, StringComparer.Ordinal))
                        {
                            issues.Add($"{where} 期望了红线工具「{toolName}」——批核族与权限授予类不属于助手能力面");
                        }
                        if (!AssistantToolKeys.ToolNames.Contains(toolName, StringComparer.Ordinal))
                        {
                            issues.Add($"{where} 期望的工具「{toolName}」不在注册表（AssistantToolKeys.ToolNames）里");
                            continue;
                        }
                        if (page is not null && !AssistantToolSetPolicy.IsOffered(toolName, page))
                        {
                            issues.Add($"{where} 工具「{toolName}」在页面类型「{page}」下不会发给模型（被页面策略裁掉）");
                        }
                    }

                    if (IsReviewed(root) && !HasText(root, "expected"))
                    {
                        issues.Add($"{where} 标记 reviewed:true 但缺 expected");
                    }
                }
            }
        }

        Assert.True(issues.Count == 0, string.Join("\n", issues));

        // 下限：样本面不能退化成"没人写期望工具链"（否则这条用例形同虚设）。
        Assert.True(samplesWithExpectations >= 10,
            $"声明了 expect_tools 的样本只有 {samplesWithExpectations} 条，至少应有 10 条");
        Assert.True(distinctTools.Count >= 5,
            $"期望工具只覆盖 {distinctTools.Count} 个，至少应覆盖 5 个");
    }

    [Fact]
    public void Red_Line_Tools_Are_Not_Registered()
    {
        // 红线不是"样本别写"，是"能力面里根本没有"：批核/解批/结案/取消结案与权限授予类
        // 在助手侧不存在注册项，模型无从调用。
        foreach (var tool in RedLineTools)
        {
            Assert.DoesNotContain(tool, AssistantToolKeys.ToolNames, StringComparer.Ordinal);
        }
    }

    private static bool IsReviewed(JsonElement root) =>
        root.TryGetProperty("reviewed", out var reviewed)
        && reviewed.ValueKind == JsonValueKind.True;

    private static bool HasText(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static IEnumerable<string> SeedFiles()
    {
        var root = FindRepoRoot();
        var eval = Path.Combine(root, "EOS.API.Tests", "AssistantEval");
        Assert.True(Directory.Exists(eval), $"找不到评估目录：{eval}");
        return Directory.EnumerateFiles(eval, "*.jsonl", SearchOption.AllDirectories);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EOS.API", "EOS.API.csproj")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Repository root not found (EOS.API/EOS.API.csproj).");
    }
}
