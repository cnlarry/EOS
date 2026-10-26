using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Recording;
using EOS.API.Tests.Parity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using static EOS.API.Tests.AssistantTranscriptTests;

namespace EOS.API.Tests;

/// <summary>
/// 转录 → parity 用例的流水线：**录制 → 转换 → 用例**。
///
/// <para>
/// 录制器本身的边界（回合怎么切、非对话调用怎么排除）由
/// <see cref="AssistantTranscriptTests"/> 守着；这里守的是"录下来的东西能不能变成一条可回放的用例"——
/// 这条链断了，真实回合就进不了 parity。
/// </para>
/// </summary>
public sealed class TranscriptParityPipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "eos-transcript-pipeline", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void 转录能转成用例并被同一读取端解析回来()
    {
        var recorder = CreateRecorder(out var directory);

        recorder.Capture("m1", Messages("SYS-PROMPT", "把产品备注改成验收复核"),
            [Tool("search_records")],
            [ToolCallDelta("call_1", "search_records", """{"keyword":"E2ESH"}""")]);
        recorder.Capture("m1",
            Messages("SYS-PROMPT", "把产品备注改成验收复核",
                AssistantWithCall("call_1", "search_records"), ToolResult("call_1", "module=1201 total=1")),
            [Tool("search_records")],
            [
                new ChatDelta("已经找到该料件。", null),
                new ChatDelta(null, new ChatUsage(100, 20, 300)),
                new ChatDelta(null, null, null, "stop"),
            ]);
        recorder.Flush();

        var cases = Path.Combine(_root, "cases");
        var name = Assert.Single(TranscriptParityCaseConverter.ConvertAll(directory, cases));
        var parityCase = ParityCaseFile.Load(Path.Combine(cases, name + ".json"));

        Assert.Equal("SYS-PROMPT", parityCase.SystemPrompt);
        Assert.Equal("把产品备注改成验收复核", parityCase.UserInput);
        Assert.Equal(2, parityCase.Calls.Count);
        Assert.Equal(4, parityCase.MaxToolRounds); // 真实 1 轮工具，下限 4
        var tool = Assert.Single(parityCase.Tools);
        Assert.Equal("search_records", tool.Name);
        Assert.Equal("module=1201 total=1", tool.ResultContent);
        Assert.True(tool.Ok);
        Assert.Null(parityCase.FinalRoundInstruction);
    }

    [Fact]
    public void 收尾轮说明会被识别并写进用例()
    {
        var recorder = CreateRecorder(out var directory);

        recorder.Capture("m1", Messages("SYS-PROMPT", "一直查"),
            [Tool("search_records")], [ToolCallDelta("call_1", "search_records", "{}")]);

        // 收尾轮：旧引擎在末尾追加一条 System 说明。
        var finalMessages = new List<ChatMessage>(Messages("SYS-PROMPT", "一直查",
            AssistantWithCall("call_1", "search_records"), ToolResult("call_1", "none")))
        {
            new(ChatRole.System, ChatService.FinalRoundInstruction),
        };
        recorder.Capture("m1", finalMessages, null, [new ChatDelta("已经查过了。", null)]);
        recorder.Flush();

        var cases = Path.Combine(_root, "cases");
        var name = Assert.Single(TranscriptParityCaseConverter.ConvertAll(directory, cases));
        var parityCase = ParityCaseFile.Load(Path.Combine(cases, name + ".json"));

        Assert.Equal(ChatService.FinalRoundInstruction, parityCase.FinalRoundInstruction);
    }

    [Fact]
    public async Task 合成用例经真实引擎跑一遍后能重新生成用例()
    {
        var recorder = CreateRecorder(out var directory);

        // 拿仓库里的合成用例当输入：它经过**真实 ChatService** 跑一遍，再由录制器落成转录、
        // 转换器转回用例——这条链就是"真实回合怎么进 parity"。
        var source = ParityCaseFile.Load(Path.Combine(
            ApiSourceProbe.FindRepoRoot(), "EOS.API.Tests", "parity-cases", "single-tool-search.json"));

        await LegacyParityRunner.RunWithRecordingAsync(source, recorder);
        recorder.Flush();

        var output = Path.Combine(ApiSourceProbe.FindRepoRoot(), "artifacts", "parity", "cases-recorded");
        Directory.CreateDirectory(output);
        foreach (var stale in Directory.GetFiles(output, "recorded-*.json"))
        {
            File.Delete(stale);
        }

        var name = Assert.Single(TranscriptParityCaseConverter.ConvertAll(directory, output));
        var converted = ParityCaseFile.Load(Path.Combine(output, name + ".json"));

        Assert.Equal(2, converted.Calls.Count);
        // 录到的是**拼装后**的系统提示词（产品提示 + 行为规则 + 日期行），不是 settings 里那一句。
        Assert.StartsWith(source.SystemPrompt, converted.SystemPrompt);
        Assert.Equal(source.UserInput, converted.UserInput);

        var tool = Assert.Single(converted.Tools);
        Assert.Equal("search_records", tool.Name);
        Assert.Equal(source.Tools[0].ResultContent, tool.ResultContent);
        Assert.True(tool.ReadOnly);
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
}
