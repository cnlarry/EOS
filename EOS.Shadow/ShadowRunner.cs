using System.Diagnostics;
using Agent.Harness.Abstractions;
using Agent.Harness.Core;
using Agent.Harness.Maf;
using EOS.API.Features.Assistant;
using EOS.API.Features.Assistant.ModelAccess;
using EOS.API.Features.Assistant.Tools;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using EosChatModel = EOS.API.Features.Assistant.ModelAccess.IChatModel;

namespace EOS.Shadow;

/// <summary>
/// 影子运行：**同一个容器、同一批真实工具、真实模型**，换成 Harness 的 <see cref="MafAgentEngine"/>
/// 跑一轮只读对话，打印工具调用序列与耗时。
///
/// <para>
/// 它是 P2（读路径灰度）的证据面，不是灰度本身：不接管 HTTP 端点、不切流量、不写业务表。
/// 灰度真正的开关要等"EOS 怎么引用 Harness"定下来（见 docs/guide/06）。
/// </para>
///
/// <para>
/// 用法：
/// <c>dotnet run --project EOS.Shadow -p:AgentHarnessRoot="..\..\Agent Harness" -- --question "..." --user admin</c>
/// </para>
/// </summary>
internal static class ShadowRunner
{
    private static async Task<int> Main(string[] args)
    {
        var options = ParseOptions(args);
        if (!options.TryGetValue("question", out var question) || string.IsNullOrWhiteSpace(question))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        var userId = options.GetValueOrDefault("user", "admin");
        var maxToolRounds = int.TryParse(options.GetValueOrDefault("rounds", "4"), out var rounds) ? rounds : 4;

        // 用真实容器：工具的依赖闭包很深（工作台仓储、权限、参数目录），手写装配只会装出一套假环境。
        await using var factory = new WebApplicationFactory<Program>();
        using var scope = factory.Services.CreateScope();
        var provider = scope.ServiceProvider;

        var registry = provider.GetRequiredService<AssistantToolRegistry>();
        var runtime = provider.GetRequiredService<IAssistantRuntimeConfig>();
        var model = provider.GetRequiredService<EosChatModel>();

        if (!model.IsConfigured)
        {
            Console.Error.WriteLine(
                "当前模型未配置（管理面没设当前模型，或那把密钥没填）：影子运行需要真实模型，直接退出。");
            return 1;
        }

        // 工具面：注册表里**已启用**的那些，按风险分级决定是否只读。
        var tools = new List<ITool>();
        foreach (var definition in registry.Definitions)
        {
            if (registry.TryGet(definition.Name, out var tool))
            {
                tools.Add(new EosToolAdapter(tool, userId));
            }
        }

        var readOnly = tools.Count(tool => tool.Descriptor.ReadOnly && tool.Descriptor.Risk == ToolRisk.Read);

        Console.WriteLine($"模型：{model.ModelName}");
        Console.WriteLine($"工具面：{tools.Count} 个（其中只读 {readOnly} 个）");
        Console.WriteLine($"用户：{userId}；工具轮上限：{maxToolRounds}");
        Console.WriteLine($"问题：{question}");
        Console.WriteLine(new string('-', 72));

        var engine = new MafAgentEngine(
            new EosChatModelAdapter(model),
            new ToolRegistry(tools),
            new AllowAllPolicy(),
            NullLoggerFactory.Instance,
            new MafAgentEngineOptions
            {
                // 读路径灰度：只暴露只读工具。写类动作留给 P3，且必须显式关掉这一项。
                ReadOnlyToolsOnly = true,
                AllowConcurrentToolInvocation = false,
                MaxEmptyResponseRetries = 1,
                FinalRoundInstruction = ChatService.FinalRoundInstruction,
            });

        var request = new AgentTurnRequest(
            TenantId: "eos",
            UserId: userId,
            ConversationId: $"shadow-{DateTimeOffset.Now:yyyyMMdd-HHmmss}",
            Input: question,
            History: [],
            SystemPrompt: runtime.Current.Settings.SystemPrompt,
            CorrelationId: Guid.NewGuid().ToString("N"),
            MaxToolRounds: maxToolRounds);

        var stopwatch = Stopwatch.StartNew();
        var text = string.Empty;
        var toolCalls = 0;
        string? finishReason = null;
        string? failure = null;

        try
        {
            await foreach (var evt in engine.StreamAsync(request))
            {
                switch (evt)
                {
                    case AgentEvent.ToolStarted started:
                        toolCalls++;
                        Console.WriteLine($"  → 调用 {started.Name}（{started.Risk}）");
                        break;

                    case AgentEvent.ToolFinished finished:
                        Console.WriteLine(
                            $"  ← {finished.Name} ok={finished.Ok}：{Preview(finished.Digest)}");
                        break;

                    case AgentEvent.Completed completed:
                        text = completed.Result.Text;
                        finishReason = completed.Result.FinishReason;
                        break;

                    case AgentEvent.Failed failed:
                        failure = $"{failed.Code}：{failed.Message}";
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = ex.GetType().Name + "：" + ex.Message;
        }

        stopwatch.Stop();

        Console.WriteLine(new string('-', 72));
        if (failure is not null)
        {
            Console.WriteLine($"失败：{failure}");
        }

        Console.WriteLine($"工具调用：{toolCalls} 次；结束原因：{finishReason ?? "<null>"}");
        Console.WriteLine($"耗时：{stopwatch.ElapsedMilliseconds} ms（含模型出网）");
        Console.WriteLine();
        Console.WriteLine("回答：");
        Console.WriteLine(text);

        return failure is null ? 0 : 1;
    }

    private const string Usage = """
        用法：
          dotnet run --project EOS.Shadow -p:AgentHarnessRoot="<Agent Harness 源码根>" -- \
              --question "<用户问题>" [--user admin] [--rounds 4]
        """;

    private static string Preview(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<空>";
        }

        var single = text.ReplaceLineEndings(" ");
        return single.Length <= 90 ? single : single[..90] + "…";
    }

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < args.Length; index++)
        {
            var current = args[index];
            if (!current.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var name = current[2..];
            var value = index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : string.Empty;

            options[name] = value;
        }

        return options;
    }
}
