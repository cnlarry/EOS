using System.Text;
using EOS.API.Data.Assistant;
using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Quality;

/// <summary>一次判分请求的结果：模型原文、用量与按当前单价折算的花费。</summary>
/// <param name="Content">模型原文（解析在 <see cref="AssistantJudgeVerdictParser"/>）。</param>
/// <param name="PromptTokens">输入 token；模型没报用量时按字符数估算。</param>
/// <param name="CompletionTokens">输出 token；同上。</param>
/// <param name="Estimated">用量是否为估算值（没拿到厂商用量时）。</param>
/// <param name="CostYuan">本次判定的花费（元），按当前判官模型单价折算。</param>
public sealed record AssistantJudgeCompletion(
    string Content, int PromptTokens, int CompletionTokens, bool Estimated, double CostYuan);

/// <summary>
/// 判官用的模型通道：按 **JUDGE 槽**解析一次、固定下来，再把一次判定发出去。
///
/// <para>
/// 与对话那个 <see cref="ResolvingChatModel"/> 分开是有意的：判官是**离线批处理**，
/// 不参与"一次请求内模型固定"的会话语义；两者共用同一个 <see cref="AssistantChatModelFactory"/>
/// 保证走同一类客户端，但不共享实例与快照。
/// </para>
/// </summary>
public interface IAssistantJudgeModel
{
    /// <summary>实际用的模型（`供应商标识/型号`），写进 verdict 的 <c>judgeModel</c>。</summary>
    string Descriptor { get; }

    /// <summary>是否回落到对话当前模型（JUDGE 槽没配时为 true，同样写进 verdict）。</summary>
    bool FellBackToChat { get; }

    /// <summary>发一次判定请求，返回模型原文、用量与花费。</summary>
    Task<AssistantJudgeCompletion> CompleteAsync(string system, string user, CancellationToken token);
}

/// <summary>
/// 判官模型的解析与调用。**先取 JUDGE 槽，取不到回落到 CHAT**——回落不是"随便找一个模型"，
/// 而是"至少用你正在用的那个"，并且如实标记，不假装用的是判官模型。
/// </summary>
public sealed class AssistantJudgeModel(
    IAssistantModelCatalog catalog,
    AssistantRuntimeRegistry runtime,
    IHttpClientFactory httpClientFactory) : IAssistantJudgeModel
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IChatModel? _inner;
    private AssistantSettings? _settings;
    private AssistantModelDescriptor? _descriptor;

    /// <inheritdoc />
    public string Descriptor => _descriptor?.Label ?? "未配置";

    /// <inheritdoc />
    public bool FellBackToChat { get; private set; }

    /// <inheritdoc />
    public async Task<AssistantJudgeCompletion> CompleteAsync(string system, string user, CancellationToken token)
    {
        var inner = await ResolveAsync(token);
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, system),
            new(ChatRole.User, user),
        };

        var builder = new StringBuilder();
        ChatUsage? usage = null;
        await foreach (var delta in inner.StreamAsync(messages, tools: null, token))
        {
            if (delta.Usage is not null) usage = delta.Usage;
            if (!string.IsNullOrEmpty(delta.ContentDelta)) builder.Append(delta.ContentDelta);
        }

        // 厂商没报用量时按字符数估算（与记忆提炼同一口径）：判定的成本上限要用它，
        // 估不出来就只能按 0 计——那等于这个上限不存在。
        var estimated = usage is null;
        var promptTokens = usage?.PromptTokens ?? Math.Max(1, system.Length + user.Length);
        var completionTokens = usage?.CompletionTokens ?? Math.Max(1, builder.Length);
        var cost = AssistantCost.Calculate(
            promptTokens, completionTokens, _settings?.Cost ?? new AssistantCostOptions(),
            _settings?.InputPerMillionYuan, _settings?.OutputPerMillionYuan);

        return new AssistantJudgeCompletion(builder.ToString(), promptTokens, completionTokens, estimated, cost);
    }

    private async Task<IChatModel> ResolveAsync(CancellationToken token)
    {
        if (_inner is not null) return _inner;

        await _gate.WaitAsync(token);
        try
        {
            if (_inner is not null) return _inner;

            var judge = await catalog.GetActiveAsync(AssistantModelKind.Judge, token);
            FellBackToChat = judge is null;
            var active = judge ?? await catalog.GetActiveAsync(AssistantModelKind.Chat, token);
            if (active is null)
            {
                throw new InvalidOperationException(
                    "判官没有可用模型：3102 里既没有把任何模型设为「判官」的当前模型，"
                    + "也没有可回落的对话当前模型。判定需要模型，因此这一步失败而不是静默跳过。");
            }

            _settings = runtime.ComposeFor(active);
            _descriptor = new AssistantModelDescriptor(
                active.Provider.Code, active.Model.ModelCode,
                $"{active.Provider.Code}/{active.Model.ModelCode}");
            _inner = AssistantChatModelFactory.Create(httpClientFactory, _settings, active.Provider.Code);
            return _inner;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>实际使用的判官模型标识（供 verdict 记录与排障）。</summary>
public sealed record AssistantModelDescriptor(string ProviderCode, string ModelCode, string Label);
