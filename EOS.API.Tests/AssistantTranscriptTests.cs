using System.Text.Json;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Recording;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 录制器的离线用例：**不连库、不连模型**。
///
/// <para>
/// 录制的价值全在"能不能如实还原一个回合"，所以这里钉三件事：回合边界怎么切、
/// 非对话调用（记忆提炼）会不会被混进来、两个回合会不会被写成同一份文件。
/// 转录转用例的部分见 <see cref="TranscriptParityPipelineTests"/>。
/// </para>
/// </summary>
public sealed class AssistantTranscriptTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eos-transcript-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void 一个回合内的多次调用落成一份文件()
    {
        var recorder = CreateRecorder(out var directory);

        // 第 1 次调用：新回合（System 开头、User 结尾）。
        recorder.Capture("m1", Messages("SYS", "帮我查 DD2608"),
            [Tool("search_records")], [ToolCallDelta("call_1", "search_records", """{"keyword":"DD2608"}""")]);

        // 第 2 次调用：同一回合的续轮（末尾是 tool 结果）。
        recorder.Capture("m1",
            Messages("SYS", "帮我查 DD2608", AssistantWithCall("call_1", "search_records"), ToolResult("call_1", "total=1")),
            [Tool("search_records")], [new ChatDelta("找到了。", null)]);

        recorder.Flush();

        var file = Assert.Single(Directory.GetFiles(directory, "turn-*.json"));
        var turn = JsonSerializer.Deserialize<TranscriptTurn>(File.ReadAllText(file), TranscriptJson.Options)!;

        Assert.Equal(2, turn.Calls.Count);
        Assert.Equal("m1", turn.ModelName);
        Assert.Equal("system", turn.Calls[0].Messages[0].Role);
        Assert.Equal("total=1", turn.Calls[1].Messages[^1].Content);
    }

    [Fact]
    public void 记忆提炼这类非对话调用不会被混进回合()
    {
        var recorder = CreateRecorder(out var directory);

        recorder.Capture("m1", Messages("SYS", "你好"), null, [new ChatDelta("你好。", null)]);

        // 提炼调用没有 System 消息：它不在同一段对话上下文里。
        recorder.Capture("m1", [new ChatMessage(ChatRole.User, "从这段对话里提炼要点")],
            null, [new ChatDelta("- 要点", null)]);

        recorder.Flush();

        var file = Assert.Single(Directory.GetFiles(directory, "turn-*.json"));
        var turn = JsonSerializer.Deserialize<TranscriptTurn>(File.ReadAllText(file), TranscriptJson.Options)!;
        Assert.Single(turn.Calls);
    }

    [Fact]
    public void 第二个回合会被切成第二份文件()
    {
        var recorder = CreateRecorder(out var directory);

        recorder.Capture("m1", Messages("SYS", "第一问"), null, [new ChatDelta("一答", null)]);
        recorder.Capture("m1", Messages("SYS", "第二问"), null, [new ChatDelta("二答", null)]);

        recorder.Flush();

        Assert.Equal(2, Directory.GetFiles(directory, "turn-*.json").Length);
    }

    private AssistantTranscriptRecorder CreateRecorder(out string directory)
    {
        directory = Path.Combine(_root, "transcripts");
        var settings = Options.Create(new AssistantTranscriptSettings
        {
            Enabled = true,
            Directory = Path.GetRelativePath(_root, directory),
            MaxTurns = 100,
        });

        return new AssistantTranscriptRecorder(
            settings,
            _root,
            NullLogger<AssistantTranscriptRecorder>.Instance);
    }

    internal static List<ChatMessage> Messages(string system, string user, params ChatMessage[] rest) =>
        [new(ChatRole.System, system), new(ChatRole.User, user), .. rest];

    internal static ChatMessage AssistantWithCall(string id, string name) =>
        new(ChatRole.Assistant, string.Empty, [new CompletedToolCall(id, name, "{}")]);

    internal static ChatMessage ToolResult(string id, string content) =>
        new(ChatRole.Tool, content, null, id);

    internal static ToolDefinition Tool(string name) =>
        new(name, $"[READ] {name} 的描述", """{"type":"object","properties":{}}""");

    internal static ChatDelta ToolCallDelta(string id, string name, string arguments) =>
        new(null, null, [new ProposedToolCallFragment(0, id, name, arguments)], "tool_calls");
}
