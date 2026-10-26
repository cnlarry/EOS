using System.Text.Json;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Recording;
using EOS.API.Features.Assistant.Tools;

namespace EOS.API.Tests.Parity;

/// <summary>
/// 把**进程内录制**的回合转录成 parity 用例。
///
/// <para>
/// 与库内重建（<see cref="SessionParityCaseGenerator"/>）的分工：这里能拿到重建拿不到的三样——
/// **流式分片**（正文里混入工具调用标记时旧引擎会剥离）、**多轮工具的真实轮次边界**、
/// **系统提示词快照**。代价是必须在开关打开的情况下真跑过一轮对话。
/// </para>
///
/// <para>
/// 工具桩的**结果内容**取自录制里回喂给模型的那条 tool 消息：这样回放时模型看到的
/// 与当初一致（模型输出虽然是脚本固定的，但结果摘要会参与对比，必须同源）。
/// </para>
/// </summary>
internal static class TranscriptParityCaseConverter
{
    /// <summary>把目录下所有 <c>turn-*.json</c> 转成用例，写到 <paramref name="outputDirectory"/>；返回生成的用例名。</summary>
    public static IReadOnlyList<string> ConvertAll(string transcriptDirectory, string outputDirectory)
    {
        if (!Directory.Exists(transcriptDirectory))
        {
            return [];
        }

        Directory.CreateDirectory(outputDirectory);

        var names = new List<string>();
        foreach (var path in Directory
            .EnumerateFiles(transcriptDirectory, "turn-*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            var transcript = JsonSerializer.Deserialize<TranscriptTurn>(
                File.ReadAllText(path), TranscriptJson.Options);

            if (transcript is null || transcript.Calls.Count == 0)
            {
                continue;
            }

            var parityCase = Convert(transcript, Path.GetFileNameWithoutExtension(path));
            ParityCaseFile.Save(Path.Combine(outputDirectory, parityCase.Name + ".json"), parityCase);
            names.Add(parityCase.Name);
        }

        return names;
    }

    /// <summary>一份转录 → 一条用例。</summary>
    public static ParityCase Convert(TranscriptTurn transcript, string name)
    {
        ArgumentNullException.ThrowIfNull(transcript);

        var first = transcript.Calls[0];
        var systemPrompt = first.Messages
            .FirstOrDefault(message => string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            ?.Content ?? string.Empty;
        var userInput = first.Messages
            .LastOrDefault(message => string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
            ?.Content ?? string.Empty;

        var results = CollectToolResults(transcript.Calls);
        var toolRounds = transcript.Calls.Count(call => call.Output.Any(delta => delta.ToolCalls is { Count: > 0 }));

        return new ParityCase(
            Name: "recorded-" + name,
            ModelName: transcript.ModelName,
            SystemPrompt: systemPrompt,
            UserInput: userInput,
            // 轮数上限必须 ≥ 真实工具轮数，否则回放会在中途被截断，比出来的是"上限不同"而不是"引擎不同"。
            MaxToolRounds: Math.Max(4, toolRounds),
            Tools: BuildStubs(first.Tools, results),
            Calls: [.. transcript.Calls.Select(call => new ParityModelCall([.. call.Output.Select(ToDelta)]))],
            Note: BuildNote(transcript.Calls.Count, toolRounds, name),
            FinalRoundInstruction: DetectFinalRoundInstruction(transcript));
    }

    /// <summary>
    /// 识别收尾轮说明：旧引擎在工具轮上限处把 <see cref="ChatService.FinalRoundInstruction"/>
    /// 作为**最后一条 System 消息**追加。识别到就写进用例，回放时新引擎才会收到同一段文本。
    /// </summary>
    private static string? DetectFinalRoundInstruction(TranscriptTurn transcript)
    {
        foreach (var call in transcript.Calls)
        {
            if (call.Messages.Count == 0)
            {
                continue;
            }

            var last = call.Messages[^1];
            if (string.Equals(last.Role, "system", StringComparison.OrdinalIgnoreCase)
                && string.Equals(last.Content, ChatService.FinalRoundInstruction, StringComparison.Ordinal))
            {
                return ChatService.FinalRoundInstruction;
            }
        }

        return null;
    }

    /// <summary>从回喂消息里收集"工具名 → 当初的结果内容"（同名只留第一条）。</summary>
    private static Dictionary<string, string> CollectToolResults(IReadOnlyList<TranscriptCall> calls)
    {
        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var call in calls)
        {
            var nameById = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var message in call.Messages)
            {
                if (message.ToolCalls is not null)
                {
                    foreach (var toolCall in message.ToolCalls)
                    {
                        nameById[toolCall.Id] = toolCall.Name;
                    }
                }

                if (message.ToolCallId is not null
                    && nameById.TryGetValue(message.ToolCallId, out var name))
                {
                    results.TryAdd(name, message.Content);
                }
            }
        }

        return results;
    }

    private static IReadOnlyList<ParityToolStub> BuildStubs(
        IReadOnlyList<TranscriptTool>? tools,
        IReadOnlyDictionary<string, string> results)
    {
        if (tools is null || tools.Count == 0)
        {
            return [];
        }

        return
        [
            .. tools
                .GroupBy(tool => tool.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .Select(tool =>
                {
                    var risk = RiskOf(tool.Description);
                    return new ParityToolStub(
                        Name: tool.Name,
                        Description: tool.Description,
                        ParametersJson: tool.ParametersJson,
                        Risk: risk,
                        ReadOnly: risk == AssistantToolRisk.Read,
                        ResultContent: results.GetValueOrDefault(tool.Name, string.Empty),
                        // 回放时两端跑的是同一个桩，成功标志必然一致；拒绝类原因以摘要前缀区分。
                        Ok: !IsDenial(results.GetValueOrDefault(tool.Name, string.Empty)));
                }),
        ];
    }

    /// <summary>风险前缀由 <c>AssistantToolRegistry</c> 拼进描述（<c>[READ] …</c>），这里按它还原。</summary>
    private static AssistantToolRisk RiskOf(string description) => description switch
    {
        _ when description.StartsWith("[DRAFT]", StringComparison.Ordinal) => AssistantToolRisk.Draft,
        _ when description.StartsWith("[WRITE]", StringComparison.Ordinal) => AssistantToolRisk.Write,
        _ when description.StartsWith("[ADMINWRITE]", StringComparison.Ordinal) => AssistantToolRisk.AdminWrite,
        _ => AssistantToolRisk.Read,
    };

    private static bool IsDenial(string content) =>
        content.StartsWith("rejected:", StringComparison.Ordinal)
        || content.StartsWith("error:", StringComparison.Ordinal);

    private static ChatDelta ToDelta(TranscriptDelta delta) => new(
        ContentDelta: delta.Content,
        Usage: delta.Usage is null
            ? null
            : new ChatUsage(delta.Usage.PromptTokens, delta.Usage.CompletionTokens, delta.Usage.ElapsedMs),
        ToolCallDeltas: delta.ToolCalls is null
            ? null
            : [.. delta.ToolCalls.Select(fragment =>
                new ProposedToolCallFragment(fragment.Index, fragment.Id, fragment.Name, fragment.Arguments))],
        FinishReason: delta.FinishReason,
        ReasoningDelta: delta.Reasoning);

    private static string BuildNote(int calls, int toolRounds, string source) =>
        $"由进程内录制的真实回合转录而来（{source}）。{calls} 次模型调用 / {toolRounds} 轮工具；"
        + "保留了流式分片、轮次边界与系统提示词快照——这三样是库内会话重建拿不到的。"
        + "工具结果取自当初回喂给模型的 tool 消息；轮数上限按真实轮数取下限 4。";
}
