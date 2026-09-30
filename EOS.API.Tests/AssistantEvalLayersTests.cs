using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using EOS.API.Data;
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

    /// <summary>检索层与回答层的种子文件（与其余层共用同一条 dotnet test 命令）。</summary>
    public static TheoryData<string> CorpusLayers => new() { "functional", "business" };

    /// <summary>
    /// 把 <c>functional/</c>（检索）与 <c>business/</c>（回答）两层的**来源锚定**变成自动断言：
    /// 每条样本声明的依据必须在仓库里真实存在（模块映射已登记、工具名在工具源码里、文件在仓库里、
    /// 类型或成员在 EOS.API 程序集里）。这层判的是"样本没有自造依据"，不是"模型的自然语言回答对不对"
    /// （后者需 LLM-as-judge，见 AssistantEval/README）——人工抽样前先把"依据可核对"这一半自动化。
    /// </summary>
    [Theory]
    [MemberData(nameof(CorpusLayers))]
    public void 检索与回答层的样本来源锚定到代码或仓库(string layer)
    {
        var lines = ReadLines(layer, "seed-01.jsonl");
        Assert.True(lines.Count >= 30, $"{layer} 层样本不足 30 条：人工抽样面没有被压下来。");

        var problems = new List<string>();
        foreach (var line in lines)
        {
            var question = line.GetProperty("question").GetString() ?? string.Empty;
            var expected = line.GetProperty("expected").GetString() ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(question), $"{layer} 层样本缺 question。");
            Assert.False(string.IsNullOrWhiteSpace(expected), $"{layer} 层样本缺 expected。");

            var sources = line.GetProperty("sources").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .ToList();
            Assert.NotEmpty(sources);

            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    problems.Add($"[空来源] {question}");
                    continue;
                }
                if (!TryAnchor(source, out var reason))
                {
                    problems.Add($"[{reason}] {question} -> {source}");
                }
            }
        }

        Assert.True(problems.Count == 0,
            $"{layer} 层有 {problems.Count} 条来源无法锚定到代码或仓库（人工抽样时无从核对）：\n  - "
            + string.Join("\n  - ", problems));
    }

    /// <summary>
    /// 来源形态共四种：模块映射 <c>ModuleBusinessMap[1404]</c>、工具名（小写 snake_case）、
    /// 仓库文件（<c>.sql</c> / <c>.md</c> / <c>.ts</c>）、代码符号（<c>Type</c> 或 <c>Type.Member</c>）。
    /// </summary>
    private static bool TryAnchor(string source, out string reason)
    {
        var map = Regex.Match(source, @"^ModuleBusinessMap\[(\d+)\]$");
        if (map.Success)
        {
            var moduleId = int.Parse(map.Groups[1].Value, CultureInfo.InvariantCulture);
            var anchored = ModuleBusinessMap.Get(moduleId) is not null
                || ModuleBusinessMap.MasterTables.ContainsKey(moduleId);
            reason = anchored ? "模块映射" : "模块未在 ModuleBusinessMap 登记";
            return anchored;
        }

        if (Regex.IsMatch(source, @"^[a-z][a-z0-9_]*$"))
        {
            var anchored = ToolSourceTexts.Value.Any(
                text => text.Contains($"\"{source}\"", StringComparison.Ordinal));
            reason = anchored ? "工具名" : "工具名在工具源码里找不到";
            return anchored;
        }

        if (source.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith(".ts", StringComparison.OrdinalIgnoreCase))
        {
            var root = RepoRoot();
            var anchored = root is not null
                && Directory.EnumerateFiles(root, source, SearchOption.AllDirectories).Any(path => !IsNoise(path));
            reason = anchored ? "仓库文件" : "仓库内无此文件";
            return anchored;
        }

        // 全大写标识符：数据库对象名或错误码（如 WFFORM_FLOW / WORKBENCH_IDEMPOTENCY / MEMORY_PII_RISK）。
        if (Regex.IsMatch(source, @"^[A-Z][A-Z0-9_]{2,}$"))
        {
            var anchored = RepoCodeTexts.Value.Any(
                text => Regex.IsMatch(text, $@"\b{Regex.Escape(source)}\b"));
            reason = anchored ? "库对象或错误码" : "仓库代码与 SQL 里都找不到该标识符";
            return anchored;
        }

        var symbol = AnchorSymbol(source);
        reason = symbol ? "代码符号" : "EOS.API 里找不到该类型或成员";
        return symbol;
    }

    private static bool AnchorSymbol(string source)
    {
        var types = ApiTypes.Value;
        var parts = source.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        // 无点符号：先当类型名（含接口与静态类），再当"某个类型上的成员"（如 CombineDataFilters）。
        if (parts.Length == 1)
        {
            return types.Any(item => string.Equals(item.Name, parts[0], StringComparison.Ordinal))
                || types.Any(item => HasMember(item, parts[0]));
        }

        var member = parts[^1];
        var typeName = parts[^2];

        // Type.Member
        var type = types.FirstOrDefault(item => string.Equals(item.Name, typeName, StringComparison.Ordinal));
        if (type is not null && HasMember(type, member))
        {
            return true;
        }

        // Namespace.Last.Type：末段本身是类型名。
        if (types.Any(item => string.Equals(item.Name, member, StringComparison.Ordinal)))
        {
            return true;
        }

        // Admin.GetUserRightDetail 这类：前缀是命名空间末段，成员挂在其中某个类型上。
        return types.Any(item => HasMember(item, member)
            && item.Namespace?.EndsWith("." + typeName, StringComparison.Ordinal) == true);
    }

    private static bool HasMember(Type type, string member) =>
        type.GetMember(member, BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.Static).Length > 0;

    private static readonly Lazy<IReadOnlyList<Type>> ApiTypes =
        new(() => [.. typeof(ModuleBusinessMap).Assembly.GetTypes()]);

    /// <summary>
    /// 仓库内业务代码与 SQL 的文本（缓存）：供"数据库对象名 / 错误码"这类全大写标识符锚定。
    /// 刻意排除测试目录——否则样本可以被测试代码自己"证明"。
    /// </summary>
    private static readonly Lazy<IReadOnlyList<string>> RepoCodeTexts = new(() =>
    {
        var root = RepoRoot();
        if (root is null)
        {
            return [];
        }

        var texts = new List<string>();
        foreach (var pattern in new[] { "*.cs", "*.sql" })
        {
            texts.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .Where(path => !IsNoise(path)
                    && !path.Contains($"{Path.DirectorySeparatorChar}EOS.API.Tests{Path.DirectorySeparatorChar}",
                        StringComparison.OrdinalIgnoreCase))
                .Select(File.ReadAllText));
        }
        return texts;
    });

    /// <summary>工具源码文本：工具名必须在这里被声明过（避免样本自造工具名）。</summary>
    private static readonly Lazy<IReadOnlyList<string>> ToolSourceTexts = new(() =>
    {
        var root = RepoRoot();
        if (root is null)
        {
            return [];
        }
        var directory = Path.Combine(root, "EOS.API", "Features", "Assistant", "Tools");
        return Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories).Select(File.ReadAllText)]
            : [];
    });

    /// <summary>仓库根：优先取编译期注入（输出目录可能在仓库外），否则从输出目录向上找。</summary>
    private static string? RepoRoot()
    {
        var injected = typeof(AssistantEvalLayersTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(item => item.Key == "RepoRoot")?.Value;
        if (!string.IsNullOrWhiteSpace(injected) && Directory.Exists(injected))
        {
            // 注入值可能是相对路径（形如 `EOS.API.Tests\..`）；不规范化会让后续路径里残留 `..` 段，
            // 使"按目录排除"之类的文本判断误伤（曾导致扫描到 0 个文件）。
            return Path.GetFullPath(injected);
        }

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "EOS.API.Tests", "AssistantEval")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }

    private static bool IsNoise(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

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
