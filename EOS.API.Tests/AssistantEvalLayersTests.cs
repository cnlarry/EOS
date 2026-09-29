using System.Reflection;
using System.Text.Json;
using EOS.API.Data.Workbench;
using EOS.API.Features.Assistant.Config;
using EOS.API.Features.Assistant.Situation;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 评估集**四层**的入口与口径检查：处境 / 诊断 / 动作 / 配置各有一份可跑的种子文件，
/// 且样本里声明的原因码必须落在代码自己的闭集内。
///
/// <para>
/// 这层不做正确率判定（那需要 LLM-as-judge，见 AssistantEval/README）：
/// 它断的是"样本与代码没有漂移"——闭集改了而种子文件没跟上，或种子文件开始自造原因码，
/// 都会在这里变红。运行方式：
/// <c>dotnet test EOS.API.Tests/EOS.API.Tests.csproj --filter FullyQualifiedName~AssistantEval</c>。
/// </para>
/// </summary>
public sealed class AssistantEvalLayersTests
{
    /// <summary>四层的种子文件（相对 AssistantEval 目录）。</summary>
    public static TheoryData<string, string> LayerFiles => new()
    {
        { "situation", "seed-01.jsonl" },
        { "diagnosis", "golden.jsonl" },
        { "action", "seed-01.jsonl" },
        { "config", "seed-01.jsonl" },
    };

    [Theory]
    [MemberData(nameof(LayerFiles))]
    public void 每层都有可跑的种子文件(string layer, string fileName)
    {
        var lines = ReadLines(layer, fileName);
        Assert.True(lines.Count > 0, $"{layer} 层没有样本：入口存在但等于不可跑。");
        foreach (var line in lines)
        {
            Assert.True(line.TryGetProperty("id", out var id), $"{layer} 层样本缺 id。");
            Assert.False(string.IsNullOrWhiteSpace(id.GetString()));
            if (layer != "diagnosis")
            {
                // 诊断黄金集来自元数据枚举（字段已是"标准答案"），其余三层的种子要自带一句"它考什么"
                Assert.True(line.TryGetProperty("note", out _), $"{layer} 层样本缺 note：样本要能说清它考的是什么。");
            }
        }
    }

    [Fact]
    public void 处境层的摘要类别来自代码闭集()
    {
        var kinds = ConstStrings(typeof(SituationDigestKinds));
        Assert.NotEmpty(kinds);

        foreach (var line in ReadLines("situation", "seed-01.jsonl"))
        {
            var expected = line.GetProperty("expect");
            var declared = expected.GetProperty("kinds").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToList();
            foreach (var kind in declared)
            {
                Assert.Contains(kind, kinds);
            }
            // 打开即见必须零模型调用：这是主来源能"打开就说话"的前提
            Assert.True(expected.GetProperty("zeroModel").GetBoolean());
        }
    }

    [Fact]
    public void 动作层的原因码来自策略层闭集()
    {
        var codes = ConstStrings(typeof(WorkbenchDenialCodes));
        Assert.NotEmpty(codes);

        foreach (var line in ReadLines("action", "seed-01.jsonl"))
        {
            var action = line.GetProperty("action").GetString();
            Assert.Contains(action, new[] { "insert", "update", "delete" });
            Assert.NotEmpty(line.GetProperty("rows").EnumerateArray());

            var code = line.GetProperty("expect").GetProperty("moduleCode");
            if (code.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            Assert.Contains(code.GetString(), codes);
        }
    }

    [Fact]
    public void 配置层的原因码来自不一致规则闭集()
    {
        var codes = ConstStrings(typeof(ConfigConsistencyRules));
        Assert.Equal(3, codes.Count);

        foreach (var line in ReadLines("config", "seed-01.jsonl"))
        {
            Assert.Contains(line.GetProperty("surface").GetString(),
                new[] { "fields", "datasource", "buttons", "effect" });

            var code = line.GetProperty("expectCode");
            if (code.ValueKind == JsonValueKind.Null)
            {
                continue;
            }
            Assert.Contains(code.GetString(), codes);
        }
    }

    [Fact]
    public void 诊断层的黄金集可追溯到元数据()
    {
        var lines = ReadLines("diagnosis", "golden.jsonl");
        Assert.True(lines.Count >= 10, "诊断黄金集样本过少，跑分没有意义。");
        foreach (var line in lines)
        {
            foreach (var key in new[] { "id", "moduleId", "stage", "validationKey", "message", "suggestedClass" })
            {
                Assert.True(line.TryGetProperty(key, out var value), $"诊断样本缺 {key}。");
                Assert.False(string.IsNullOrWhiteSpace(value.ToString()));
            }

            // 样本的证据段必须是诊断工具真会产出的那一类：段名与 stage 的搭配不能自造
            var suggestedClass = line.GetProperty("suggestedClass").GetString();
            Assert.Contains(suggestedClass, new[] { "validation", "fieldGuard", "blockers" });
            var stage = line.GetProperty("stage").GetString();
            if (suggestedClass == "validation")
            {
                Assert.Contains(stage, new[] { "SAVE", "APPROVE", "DEAPPROVE", "DELETE" });
            }
        }
    }

    private static IReadOnlyList<string> ConstStrings(Type type) =>
        [.. type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => field.GetRawConstantValue() as string)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)];

    private static IReadOnlyList<JsonElement> ReadLines(string layer, string fileName)
    {
        var path = EvalPath(Path.Combine(layer, fileName));
        Assert.True(File.Exists(path), $"评估集缺失：{path}");
        return [.. File.ReadAllLines(path)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())];
    }

    /// <summary>
    /// 输出目录可能是仓库外的临时路径（`--artifacts-path`），故先看编译期注入的仓库根；
    /// 与 <c>AssistantEvalRunnerTests</c> 用同一条定位规则，不另建一套。
    /// </summary>
    private static string EvalPath(string relative)
    {
        var root = typeof(AssistantEvalLayersTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepoRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(root))
        {
            var injected = Path.Combine(root, "EOS.API.Tests", "AssistantEval", relative);
            if (File.Exists(injected)) return injected;
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "AssistantEval", relative);
            if (File.Exists(candidate)) return candidate;
            var nested = Path.Combine(directory.FullName, "EOS.API.Tests", "AssistantEval", relative);
            if (File.Exists(nested)) return nested;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"评估集缺失：{relative}");
    }
}
