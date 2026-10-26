using System.Text.Json;
using System.Text.Json.Serialization;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;

namespace EOS.API.Tests.Parity;

/// <summary>
/// 引擎 parity 用例：模型脚本 + 工具桩 + 回合输入。
///
/// <para>
/// **格式的真源在 Agent Harness 仓**（<c>src/Agent.Harness.Testing/ParityCase.cs</c>），
/// 这里只实现读取端。两仓不做项目引用、只靠这份 JSON 对齐——各自的 <c>clone</c> 都能独立构建。
/// 改动字段名等于破坏契约，两仓必须同批改。
/// </para>
///
/// <para>
/// 用例里**不录请求**（消息正文与工具声明都不进文件）：旧引擎自带 system 消息、
/// 新引擎走 <c>ChatOptions.Instructions</c>，两端组装的请求本来就不同，录进来也没法逐字比。
/// 请求只按角色序列记一笔，见 <see cref="LegacyTurnOutcome.RequestShapes"/>。
/// </para>
/// </summary>
internal sealed record ParityToolStub(
    string Name,
    string Description,
    string ParametersJson,
    AssistantToolRisk Risk = AssistantToolRisk.Read,
    bool ReadOnly = true,
    string ResultContent = "",
    bool Ok = true);

/// <summary>一次模型调用的脚本化输出。</summary>
internal sealed record ParityModelCall(IReadOnlyList<ChatDelta> Output);

internal sealed record ParityCase(
    string Name,
    string ModelName,
    string SystemPrompt,
    string UserInput,
    int MaxToolRounds,
    IReadOnlyList<ParityToolStub> Tools,
    IReadOnlyList<ParityModelCall> Calls,
    string? Note = null,
    string? FinalRoundInstruction = null);

/// <summary>归一化的一次工具调用（与 Harness 侧 <c>OutcomeToolCall</c> 同形）。</summary>
internal sealed record LegacyOutcomeToolCall(
    string Name,
    string ArgumentsJson,
    string ResultDigest,
    bool Ok);

internal sealed record LegacyOutcomeUsage(int PromptTokens, int CompletionTokens);

/// <summary>
/// 旧引擎一次回合的归一化结果（与 Harness 侧 <c>TurnOutcome</c> 同形）。
/// 比较在 Harness 仓做（它拥有比较器），这里只负责如实产出。
/// </summary>
internal sealed record LegacyTurnOutcome(
    string Case,
    string Engine,
    int ModelCalls,
    string FinalText,
    string? FinishReason,
    IReadOnlyList<LegacyOutcomeToolCall> ToolCalls,
    LegacyOutcomeUsage Usage,
    IReadOnlyList<string> RequestShapes,
    string? FailureCode = null,
    string? FailureMessage = null);

/// <summary>用例与结果的 JSON 读写。序列化口径必须与 Harness 侧逐项一致。</summary>
internal static class ParityCaseFile
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文不转 \uXXXX：用例与报告要能直接读、能进 diff。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ParityCase Load(string path)
    {
        var item = JsonSerializer.Deserialize<ParityCase>(File.ReadAllText(path), Json);
        return item ?? throw new InvalidOperationException($"用例无法解析：{path}");
    }

    public static IReadOnlyList<string> ListCaseFiles(string directory) =>
        Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.Ordinal)]
            : [];

    public static void Save<T>(string path, T value)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }
}
