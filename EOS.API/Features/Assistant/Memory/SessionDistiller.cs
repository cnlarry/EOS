using EOS.API.Features.Assistant.Governance;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Parameters;

namespace EOS.API.Features.Assistant.Memory;

/// <summary>
/// 回合结束后的**记忆提炼**：把这一轮对话交给模型提炼成候选记忆，落成待用户确认的条目。
///
/// <para>
/// 它是后台增强，不是对话的一部分：没配记忆存储、或当日用量不够时静默跳过；
/// 提炼失败只记 Debug，不影响已经落库的回答与已经下发的 <c>done</c> 事件。
/// </para>
///
/// <para>
/// 提炼自己也是一次模型调用，因此同样受窗口与成本约束：喂的是**裁剪后的历史**（它要的本来就是最近几轮），
/// 并且走同一本用量账（<see cref="AssistantUsageLedger"/>）预留与结算。
/// </para>
/// </summary>
public sealed class SessionDistiller(
    IAssistantMemoryStore? memoryStore,
    IChatModel model,
    AssistantUsageLedger ledger,
    ILogger logger)
{
    public async Task DistillAsync(
        string userId,
        long messageId,
        IReadOnlyList<(int Role, string Content)> history,
        string finalText,
        DateTimeOffset dayStart,
        AssistantSettings settings,
        AssistantMemoryLimitsOptions memory,
        CancellationToken token)
    {
        if (memoryStore is null || !settings.EnableAutoDistill)
        {
            return;
        }

        var exchanges = history
            .Where(item => item.Role is 1 or 2 && !string.IsNullOrWhiteSpace(item.Content))
            .Select(item => (Role: item.Role == 1 ? "user" : "assistant", item.Content))
            .ToList();
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            exchanges.Add(("assistant", finalText));
        }

        if (exchanges.Count == 0)
        {
            return;
        }

        // 提炼也是一次独立模型调用，纳入成本限额：先按单轮额度预留，超限时静默跳过
        // （记忆提炼是后台增强，不做也不影响已完成的对话），成功后按实际用量结算。
        var reserveMicro = settings.Cost.ReserveMicroYuanPerRequest;
        // 提炼是后台行为，与当前页面无关，所以只看用户级覆盖（moduleId 传 null）
        var distillUserCapYuan = await ledger.UserDailyCapYuanAsync(userId, null, settings, token);
        var reserved = await ledger.TryReserveAsync(userId, dayStart, reserveMicro, distillUserCapYuan, settings, token);
        if (!reserved)
        {
            logger.LogDebug("助手记忆提炼跳过（当日用量不足，session={SessionId}）", messageId);
            return;
        }

        try
        {
            var prompt = MemoryDistiller.BuildDistillPrompt(exchanges, memory);
            var output = new System.Text.StringBuilder();
            ChatUsage? usage = null;
            await foreach (var delta in model.StreamAsync(
                [new ChatMessage(ChatRole.User, prompt)], null, token))
            {
                if (delta.Usage is not null)
                {
                    usage = delta.Usage;
                }

                output.Append(delta.ContentDelta);
            }

            var estimated = usage is null;
            var promptTokens = usage?.PromptTokens ?? Math.Max(1, prompt.Length);
            var completionTokens = usage?.CompletionTokens ?? Math.Max(1, output.Length);
            await ledger.SettleAsync(
                userId, dayStart, reserveMicro,
                AssistantUsageLedger.ToMicroYuan(AssistantCost.Calculate(
                    promptTokens, completionTokens, settings.Cost,
                    settings.InputPerMillionYuan, settings.OutputPerMillionYuan)),
                completed: false);
            if (estimated)
            {
                logger.LogInformation(
                    "助手记忆提炼用量为估算（session={SessionId} prompt={Prompt} completion={Completion}）",
                    messageId, promptTokens, completionTokens);
            }

            foreach (var candidate in MemoryDistiller.Parse(output.ToString(), memory))
            {
                var pending = await memoryStore.AddPendingAsync(userId, candidate.Type, candidate.Key,
                    candidate.Value, messageId, candidate.Confidence, token);
                // 达到建议门槛的候选**自动转正**（覆盖同名），以下维持待确认。
                // 门槛来自参数目录（MEM_SUGGEST_THRESHOLD）——它真影响"记忆是否自动生效"。
                if (candidate.Confidence >= memory.SuggestThreshold)
                {
                    await memoryStore.ResolvePendingAsync(userId, pending.Id, confirm: true, token);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 取消：提炼不产生可用结果，释放预留（不计费），与对话取消口径一致。
            await ledger.SettleAsync(userId, dayStart, reserveMicro, 0, completed: false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 失败按预留额计入（与对话失败口径一致）；提炼失败不影响对话。
            await ledger.SettleAsync(userId, dayStart, reserveMicro, reserveMicro, completed: false);
            logger.LogDebug(ex, "助手记忆提炼失败（已忽略，不影响对话）");
        }
    }
}
