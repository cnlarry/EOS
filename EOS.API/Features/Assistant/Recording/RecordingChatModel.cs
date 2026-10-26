using System.Runtime.CompilerServices;
using EOS.API.Features.Assistant.ModelAccess;

namespace EOS.API.Features.Assistant.Recording;

/// <summary>
/// 模型接入的录制装饰器：把每次调用的输入与输出交给 <see cref="AssistantTranscriptRecorder"/>。
///
/// <para>
/// 只在 <see cref="AssistantTranscriptSettings.Enabled"/> 打开时注册（见 <c>Program.cs</c>），
/// 关掉时这层根本不存在——不靠运行期判断，免得"忘了关"变成常态开销。
/// </para>
///
/// <para>
/// 只录**正常完成**的调用：中途失败/取消的那次不落盘（半截分片对重建没有价值）。
/// 录制失败由 recorder 自己吞掉，绝不影响正在流给用户的分片。
/// </para>
/// </summary>
internal sealed class RecordingChatModel(
    IChatModel inner,
    AssistantTranscriptRecorder recorder) : IChatModel
{
    public string ModelName => inner.ModelName;

    public bool IsConfigured => inner.IsConfigured;

    public async IAsyncEnumerable<ChatDelta> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition>? tools,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var output = new List<ChatDelta>();

        await foreach (var delta in inner
            .StreamAsync(messages, tools, cancellationToken)
            .ConfigureAwait(false))
        {
            output.Add(delta);
            yield return delta;
        }

        recorder.Capture(inner.ModelName, messages, tools, output);
    }
}
