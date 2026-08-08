using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EOS.API.Models;
using Microsoft.AspNetCore.SignalR.Client;
using Xunit;

namespace EOS.API.Tests;

/// <summary>
/// 真实端到端测试：直连运行中的 EOS.API（默认 http://localhost:5000），
/// 用真实账号（默认 admin/admin）经 HTTP 登录 + SignalR Hub 走完整 IM 流程。
/// 环境变量：EOS_API_LIVE_URL / EOS_API_LIVE_ADMIN / EOS_API_LIVE_PASSWORD /
///           EOS_API_LIVE_PARTNER / EOS_API_LIVE_PO。
/// 测试数据在 Dispose 中从 EOS.IM 库清理（直连开发库）。
/// </summary>
[Trait("Category", "Live")]
public sealed class ImLiveE2eTests : IAsyncLifetime
{
    private static readonly string BaseUrl = Env("EOS_API_LIVE_URL", "http://localhost:5000");
    private static readonly string AdminUser = Env("EOS_API_LIVE_ADMIN", "admin");
    private static readonly string AdminPassword = Env("EOS_API_LIVE_PASSWORD", "admin");
    private static readonly string PartnerUser = Env("EOS_API_LIVE_PARTNER", "pz-02");
    private static readonly string ThirdUser = Env("EOS_API_LIVE_THIRD", "lory");
    private static readonly string PoNumber = Env("EOS_API_LIVE_PO", "CGD18070016");
    private static readonly string TransportName = Env("EOS_API_LIVE_TRANSPORT", "WebSockets");

    private readonly CookieContainer _cookies = new();
    private readonly HttpClient _http;
    private readonly List<long> _createdConversations = [];
    private readonly List<ImMessageDto> _receivedMessages = [];
    private readonly List<ImRecalledEvent> _recalledEvents = [];
    private readonly List<ImUpdatedEvent> _updatedEvents = [];
    private readonly List<(string Code, string Message)> _hubErrors = [];
    private readonly object _gate = new();
    private HubConnection? _hub;
    private string _userId = string.Empty;
    private string? _closeInfo;

    public ImLiveE2eTests()
    {
        var handler = new HttpClientHandler { UseCookies = true, CookieContainer = _cookies };
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task InitializeAsync()
    {
        using var login = await _http.PostAsJsonAsync("/api/auth/login", new { userId = AdminUser, password = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var body = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        _userId = body.RootElement.GetProperty("userId").GetString() ?? AdminUser;

        _hub = new HubConnectionBuilder()
            .WithUrl($"{BaseUrl}/api/hubs/im", options =>
            {
                options.Cookies = _cookies;
                options.Transports = ParseTransport(TransportName);
            })
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(1)])
            .Build();
        _hub.On<ImMessageDto>("MessageReceived", message =>
        {
            lock (_gate)
            {
                _receivedMessages.Add(message);
            }
        });
        _hub.On<ImRecalledEvent>("MessageRecalled", e =>
        {
            lock (_gate)
            {
                _recalledEvents.Add(e);
            }
        });
        _hub.On<ImUpdatedEvent>("ConversationUpdated", e =>
        {
            lock (_gate)
            {
                _updatedEvents.Add(e);
            }
        });
        _hub.On<string, string>("HubError", (code, message) =>
        {
            lock (_gate)
            {
                _hubErrors.Add((code, message));
            }
        });
        _hub.Closed += exception =>
        {
            lock (_gate)
            {
                _closeInfo = exception?.ToString() ?? "服务端关闭连接";
            }

            return Task.CompletedTask;
        };
        await _hub.StartAsync();
        Assert.True(_hub.State == HubConnectionState.Connected);
    }

    public async Task DisposeAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
        }

        _http.Dispose();
        CleanupConversations();
    }

    [Fact]
    public async Task FullFlow_Text_Recall_Card_File_Group_Search_Works()
    {
        // 1) 通讯录存在非管理员用户
        var contacts = await GetJsonAsync<List<ImContactDto>>("/api/im/contacts?keyword=&limit=50");
        Assert.Contains(contacts, c => string.Equals(c.UserId, PartnerUser, StringComparison.OrdinalIgnoreCase));

        // 2) 创建单聊（幂等）
        var conversationId = await CreateDirectAsync(PartnerUser);
        var again = await CreateDirectAsync(PartnerUser);
        Assert.Equal(conversationId, again);
        await InvokeHubAsync("JoinConversation", conversationId);

        // 3) 初始历史为空
        var initial = await GetJsonAsync<List<ImMessageDto>>($"/api/im/conversations/{conversationId}/messages?beforeSeq=9223372036854775807&limit=50");
        Assert.Empty(initial);

        // 4) 发文本 → 收到实时事件
        var textClientId = Guid.NewGuid();
        await InvokeHubAsync("SendText", conversationId, textClientId, "端到端测试消息-特殊关键字E2E");
        var received = await WaitForMessageAsync(m =>
            m.ConversationId == conversationId &&
            m.MessageType == ImMessageType.Text &&
            m.Content.Contains("端到端测试消息", StringComparison.Ordinal));
        Assert.Equal(1, received.Seq);
        Assert.Equal(_userId, received.SenderUserId);

        // 5) 发第二条并撤回
        var recallClientId = Guid.NewGuid();
        await InvokeHubAsync("SendText", conversationId, recallClientId, "这条消息马上撤回");
        var toRecall = await WaitForMessageAsync(m =>
            m.ConversationId == conversationId && m.Content.Contains("马上撤回", StringComparison.Ordinal));
        await InvokeHubAsync("Recall", conversationId, toRecall.Id);
        await WaitForRecalledAsync(conversationId, toRecall.Id);
        var history = await GetJsonAsync<List<ImMessageDto>>($"/api/im/conversations/{conversationId}/messages?afterSeq=0&limit=50");
        Assert.Equal(2, history.Count);
        Assert.True(history.Single(m => m.Id == toRecall.Id).IsRecalled);
        Assert.False(history.Single(m => m.Id == received.Id).IsRecalled);

        // 6) ACK（Hub + REST 兜底）
        await InvokeHubAsync("AckReceived", conversationId, 2L);
        await InvokeHubAsync("AckRead", conversationId, 2L);
        using (var ack = await _http.PostAsJsonAsync(
                   $"/api/im/conversations/{conversationId}/ack",
                   new { lastReceivedSeq = 2L, lastReadSeq = 2L }))
        {
            ack.EnsureSuccessStatusCode();
        }

        var conversations = await GetJsonAsync<List<ImConversationDto>>("/api/im/conversations");
        var mine = Assert.Single(conversations, c => c.Id == conversationId);
        Assert.Equal(0, mine.UnreadCount);

        // 7) 搜索命中且仅本人会话
        var hits = await GetJsonAsync<List<ImSearchHitDto>>("/api/im/search?q=" + Uri.EscapeDataString("特殊关键字E2E"));
        Assert.Contains(hits, h => h.ConversationId == conversationId);

        // 8) 业务卡片（采购单，服务端解析快照）
        var cardClientId = Guid.NewGuid();
        await InvokeHubAsync("SendCard", conversationId, cardClientId, "purchase-order", PoNumber);
        var cardMessage = await WaitForMessageAsync(m =>
            m.ConversationId == conversationId &&
            m.MessageType == ImMessageType.Card &&
            m.Content.Contains(PoNumber, StringComparison.OrdinalIgnoreCase));
        using (var cardJson = JsonDocument.Parse(cardMessage.Content))
        {
            Assert.Equal("purchase-order", cardJson.RootElement.GetProperty("cardType").GetString());
            Assert.True(cardJson.RootElement.GetProperty("fields").GetArrayLength() >= 1);
        }

        // 9) 附件上传 + 文件消息 + 下载
        var fileBytes = "E2E 附件内容"u8.ToArray();
        var meta = await UploadFileAsync(conversationId, "e2e报告.txt", fileBytes);
        var fileClientId = Guid.NewGuid();
        await InvokeHubAsync("SendFile", conversationId, fileClientId, meta.Id);
        var fileMessage = await WaitForMessageAsync(m =>
            m.ConversationId == conversationId && m.MessageType == ImMessageType.File);
        var downloaded = await _http.GetByteArrayAsync($"/api/im/files/{meta.Id}");
        Assert.Equal(fileBytes, downloaded);

        // 10) 群聊全流程
        var groupId = await InvokeHubAsync<long?>("CreateGroup", "端到端测试群", new[] { PartnerUser });
        Assert.NotNull(groupId);
        Track(groupId!.Value);
        await WaitForUpdatedAsync(groupId.Value, "created");
        var members = await GetJsonAsync<List<ImMemberDto>>($"/api/im/conversations/{groupId}/members");
        Assert.Equal(2, members.Count);

        await InvokeHubAsync("AddMember", groupId.Value, new[] { ThirdUser });
        await WaitForUpdatedAsync(groupId.Value, "member-added");
        members = await GetJsonAsync<List<ImMemberDto>>($"/api/im/conversations/{groupId}/members");
        Assert.Equal(3, members.Count);

        await InvokeHubAsync("RemoveMember", groupId.Value, ThirdUser);
        await WaitForUpdatedAsync(groupId.Value, "member-removed");
        members = await GetJsonAsync<List<ImMemberDto>>($"/api/im/conversations/{groupId}/members");
        Assert.Equal(2, members.Count);

        await InvokeHubAsync("RenameGroup", groupId.Value, "端到端改名群");
        await WaitForUpdatedAsync(groupId.Value, "renamed");
        var groupConv = (await GetJsonAsync<List<ImConversationDto>>("/api/im/conversations"))
            .Single(c => c.Id == groupId);
        Assert.Equal("端到端改名群", groupConv.Name);

        await InvokeHubAsync("Leave", groupId.Value);
        await WaitForUpdatedAsync(groupId.Value, "member-left");
        // 退群后本人不再是成员：members 接口按权限返回 403，会话从我的列表消失
        var afterLeave = await GetJsonAsync<List<ImConversationDto>>("/api/im/conversations");
        Assert.DoesNotContain(afterLeave, c => c.Id == groupId);

        Assert.Empty(_hubErrors);
    }

    [Fact]
    public async Task DuplicateClientMessageId_OverHub_IsIdempotent()
    {
        var conversationId = await CreateDirectAsync(PartnerUser);
        var clientId = Guid.NewGuid();
        await InvokeHubAsync("SendText", conversationId, clientId, "幂等消息");
        await WaitForMessageAsync(m => m.ConversationId == conversationId && m.ClientMessageId == clientId);

        await InvokeHubAsync("SendText", conversationId, clientId, "幂等消息");
        await Task.Delay(1500);

        var history = await GetJsonAsync<List<ImMessageDto>>($"/api/im/conversations/{conversationId}/messages?afterSeq=0&limit=50");
        Assert.Single(history);
        Assert.Equal(clientId, history[0].ClientMessageId);
    }

    [Fact]
    public async Task MultipleSends_AllMessagesArrive()
    {
        var conversationId = await CreateDirectAsync(PartnerUser);
        await InvokeHubAsync("JoinConversation", conversationId);

        const int count = 3;
        for (var i = 1; i <= count; i++)
        {
            await InvokeHubAsync("SendText", conversationId, Guid.NewGuid(), $"批量消息-{i}");
            await Task.Delay(300);
        }

        await WaitForMessageCountAsync(count);
        var history = await GetJsonAsync<List<ImMessageDto>>($"/api/im/conversations/{conversationId}/messages?afterSeq=0&limit=50");
        Assert.Equal(count, history.Count);
        Assert.Equal(count, _receivedMessages.Count(m => m.ConversationId == conversationId));
    }

    [Fact]
    public async Task SendWaitSend_MinimalRepro()
    {
        var conversationId = await CreateDirectAsync(PartnerUser);
        await InvokeHubAsync("JoinConversation", conversationId);

        await InvokeHubAsync("SendText", conversationId, Guid.NewGuid(), "第一条A");
        await WaitForPersistedAsync(conversationId, "第一条A");

        await InvokeHubAsync("SendText", conversationId, Guid.NewGuid(), "第二条B");
        await WaitForPersistedAsync(conversationId, "第二条B");
    }

    [Fact]
    public async Task SendWaitSend_WithoutJoinConversation()
    {
        // 与 MinimalRepro 唯一区别：不调用 JoinConversation，验证组挂载是否为触发条件
        var conversationId = await CreateDirectAsync(PartnerUser);
        await InvokeHubAsync("SendText", conversationId, Guid.NewGuid(), "无组A");
        await WaitForPersistedAsync(conversationId, "无组A");

        await InvokeHubAsync("SendText", conversationId, Guid.NewGuid(), "无组B");
        await WaitForPersistedAsync(conversationId, "无组B");
    }

    private async Task WaitForPersistedAsync(long conversationId, string keyword, int timeoutSeconds = 10)
    {
        var stopwatch = Stopwatch.StartNew();
        string lastRaw = "(未请求)";
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            using var response = await _http.GetAsync($"/api/im/conversations/{conversationId}/messages?afterSeq=0&limit=50");
            lastRaw = await response.Content.ReadAsStringAsync();
            var history = JsonSerializer.Deserialize<List<ImMessageDto>>(lastRaw, JsonOptions) ?? [];
            if (history.Any(m => m.Content.Contains(keyword, StringComparison.Ordinal)))
            {
                return;
            }

            await Task.Delay(200);
        }

        string DiagnosticState()
        {
            lock (_gate)
            {
                var errors = _hubErrors.Count == 0
                    ? "无"
                    : string.Join(";", _hubErrors.Select(e => $"{e.Code}:{e.Message}"));
                return $"Hub错误={errors}, 连接状态={_hub?.State}, 关闭信息={_closeInfo ?? "无"}";
            }
        }

        var dbRows = await QueryDbRowsAsync(conversationId);
        throw new TimeoutException(
            $"消息 {keyword} 未在 {timeoutSeconds} 秒内落库。{DiagnosticState()}\nREST 原始响应: {lastRaw}\nDB 直查: {dbRows}");
    }

    private async Task<string> QueryDbRowsAsync(long conversationId)
    {
        var connectionString = ResolveImConnectionString();
        if (connectionString is null)
        {
            return "(无法解析 IM 连接串)";
        }

        try
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT COUNT(*) FROM dbo.im_messages WHERE ConversationId = @Id;
                SELECT ISNULL(LastMessageSeq, -1) FROM dbo.im_conversations WHERE Id = @Id;
                """;
            command.Parameters.Add(new Microsoft.Data.SqlClient.SqlParameter("@Id", System.Data.SqlDbType.BigInt) { Value = conversationId });
            await using var reader = await command.ExecuteReaderAsync();
            long count = -1;
            long lastSeq = -1;
            if (await reader.ReadAsync())
            {
                count = reader.GetInt64(0);
            }

            if (await reader.NextResultAsync() && await reader.ReadAsync())
            {
                lastSeq = reader.GetInt64(0);
            }

            return $"im_messages 行数={count}, im_conversations.LastMessageSeq={lastSeq}";
        }
        catch (Exception ex)
        {
            return $"直查异常: {ex.Message}";
        }
    }

    [Fact]
    public async Task SendPattern_Isolation()
    {
        // 场景 A：发-等事件-再发（已知失败）
        var a = await CreateDirectAsync(PartnerUser);
        await InvokeHubAsync("JoinConversation", a);
        await InvokeHubAsync("SendText", a, Guid.NewGuid(), "A1");
        await WaitForMessageAsync(m => m.ConversationId == a && m.Content.Contains("A1", StringComparison.Ordinal));
        await InvokeHubAsync("SendText", a, Guid.NewGuid(), "A2");

        // 场景 B：连发两条不等事件
        var b = await CreateDirectAsync(PartnerUser);
        await InvokeHubAsync("JoinConversation", b);
        await InvokeHubAsync("SendText", b, Guid.NewGuid(), "B1");
        await InvokeHubAsync("SendText", b, Guid.NewGuid(), "B2");

        // 场景 C：发-等事件-停 2 秒-再发
        var c = await CreateDirectAsync(PartnerUser);
        await InvokeHubAsync("JoinConversation", c);
        await InvokeHubAsync("SendText", c, Guid.NewGuid(), "C1");
        await WaitForMessageAsync(m => m.ConversationId == c && m.Content.Contains("C1", StringComparison.Ordinal));
        await Task.Delay(2000);
        await InvokeHubAsync("SendText", c, Guid.NewGuid(), "C2");

        await WaitForMessagesAsync(m =>
            (m.ConversationId == a && m.Content.Contains("A2", StringComparison.Ordinal)) ||
            (m.ConversationId == b && m.Content.Contains("B1", StringComparison.Ordinal)) ||
            (m.ConversationId == b && m.Content.Contains("B2", StringComparison.Ordinal)) ||
            (m.ConversationId == c && m.Content.Contains("C2", StringComparison.Ordinal)), 4);
    }

    [Fact]
    public async Task SendWaitSend_InvokeVsSendAsync()
    {
        // 场景 D：发-等事件-用 SendAsync（不等待完成）发送，然后经 REST 验证落库
        var d = await CreateDirectAsync(PartnerUser);
        await InvokeHubAsync("JoinConversation", d);
        await InvokeHubAsync("SendText", d, Guid.NewGuid(), "D1");
        await WaitForMessageAsync(m => m.ConversationId == d && m.Content.Contains("D1", StringComparison.Ordinal));
        await _hub!.SendAsync("SendText", d, Guid.NewGuid(), "D2-fire-and-forget");

        // 场景 E：发-等事件-再 Invoke，记录耗时与异常
        var e = await CreateDirectAsync(PartnerUser);
        await InvokeHubAsync("JoinConversation", e);
        await InvokeHubAsync("SendText", e, Guid.NewGuid(), "E1");
        await WaitForMessageAsync(m => m.ConversationId == e && m.Content.Contains("E1", StringComparison.Ordinal));
        string? invokeResult = "ok";
        try
        {
            await InvokeHubAsync("SendText", e, Guid.NewGuid(), "E2-invoke");
        }
        catch (Exception ex)
        {
            invokeResult = $"{ex.GetType().Name}: {ex.Message}";
        }

        var dHistory = await GetJsonAsync<List<ImMessageDto>>($"/api/im/conversations/{d}/messages?afterSeq=0&limit=50");
        var eHistory = await GetJsonAsync<List<ImMessageDto>>($"/api/im/conversations/{e}/messages?afterSeq=0&limit=50");
        Assert.Contains(dHistory, m => m.Content.Contains("D2-fire-and-forget", StringComparison.Ordinal));
        Assert.True(
            eHistory.Any(m => m.Content.Contains("E2-invoke", StringComparison.Ordinal)),
            $"E2 未落库，Invoke 结果: {invokeResult}, E 历史: {string.Join("|", eHistory.Select(m => m.Content))}");
    }

    private async Task WaitForMessagesAsync(Func<ImMessageDto, bool> predicate, int count, int timeoutSeconds = 15)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            lock (_gate)
            {
                if (_receivedMessages.Count(predicate) >= count)
                {
                    return;
                }
            }

            await Task.Delay(100);
        }

        lock (_gate)
        {
            var received = string.Join(" | ", _receivedMessages.Select(m => $"{m.ConversationId}:{m.Seq}:{m.Content}"));
            throw new TimeoutException(
                $"等待 {count} 条匹配消息超时（已收 {_receivedMessages.Count} 条）\n" +
                $"Hub 状态: {_hub?.State}\n关闭信息: {_closeInfo}\n已收: {received}");
        }
    }

    [Fact]
    public async Task Unauthenticated_ImEndpoints_Return401()
    {
        using var anon = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        using var list = await anon.GetAsync("/api/im/conversations");
        Assert.Equal(HttpStatusCode.Unauthorized, list.StatusCode);
        using var create = await anon.PostAsJsonAsync("/api/im/conversations", new { targetUserId = PartnerUser });
        Assert.Equal(HttpStatusCode.Unauthorized, create.StatusCode);
        using var files = await anon.GetAsync("/api/im/files/1");
        Assert.Equal(HttpStatusCode.Unauthorized, files.StatusCode);
    }

    // ---- helpers ----

    private async Task<long> CreateDirectAsync(string target)
    {
        using var response = await _http.PostAsJsonAsync("/api/im/conversations", new { targetUserId = target });
        response.EnsureSuccessStatusCode();
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("conversationId").GetInt64();
        Track(id);
        return id;
    }

    private async Task<T> GetJsonAsync<T>(string path)
    {
        using var response = await _http.GetAsync(path);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"接口返回空 JSON: {path}");
    }

    private async Task<ImAttachmentDto> UploadFileAsync(long conversationId, string fileName, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", fileName);
        content.Add(new StringContent(conversationId.ToString()), "conversationId");
        using var response = await _http.PostAsync("/api/im/files", content);
        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<ImAttachmentDto>(
            await response.Content.ReadAsStringAsync(), JsonOptions)!;
    }

    private Task InvokeHubAsync(string method, params object?[] args)
        => (_hub ?? throw new InvalidOperationException("Hub 未连接"))
            .InvokeCoreAsync(method, args, CancellationToken.None);

    private async Task<T> InvokeHubAsync<T>(string method, params object?[] args)
    {
        var result = await (_hub ?? throw new InvalidOperationException("Hub 未连接"))
            .InvokeCoreAsync<T>(method, args, CancellationToken.None);
        return result;
    }

    private async Task<ImMessageDto> WaitForMessageAsync(
        Func<ImMessageDto, bool> predicate, int timeoutSeconds = 15)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            lock (_gate)
            {
                var match = _receivedMessages.FirstOrDefault(predicate);
                if (match is not null)
                {
                    return match;
                }
            }

            await Task.Delay(100);
        }

        lock (_gate)
        {
            var received = string.Join(" | ", _receivedMessages.Select(m => $"{m.Seq}:{m.MessageType}:{m.Content}"));
            var errors = string.Join(" | ", _hubErrors.Select(e => $"{e.Code}:{e.Message}"));
            throw new TimeoutException(
                $"等待 MessageReceived 超时（已收 {_receivedMessages.Count} 条）\n" +
                $"Hub 状态: {_hub?.State}\n关闭信息: {_closeInfo}\n已收: {received}\n错误: {errors}");
        }
    }

    private async Task WaitForMessageCountAsync(int count, int timeoutSeconds = 15)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            lock (_gate)
            {
                if (_receivedMessages.Count >= count)
                {
                    return;
                }
            }

            await Task.Delay(100);
        }

        lock (_gate)
        {
            var received = string.Join(" | ", _receivedMessages.Select(m => $"{m.Seq}:{m.Content}"));
            throw new TimeoutException(
                $"等待 {count} 条消息超时（已收 {_receivedMessages.Count} 条）\n" +
                $"Hub 状态: {_hub?.State}\n关闭信息: {_closeInfo}\n已收: {received}");
        }
    }

    private async Task WaitForRecalledAsync(long conversationId, long messageId, int timeoutSeconds = 10)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            lock (_gate)
            {
                if (_recalledEvents.Any(e => e.ConversationId == conversationId && e.MessageId == messageId))
                {
                    return;
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("等待 MessageRecalled 超时");
    }

    private async Task WaitForUpdatedAsync(long conversationId, string action, int timeoutSeconds = 10)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(timeoutSeconds))
        {
            lock (_gate)
            {
                if (_updatedEvents.Any(e => e.ConversationId == conversationId && e.Action == action))
                {
                    return;
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"等待 ConversationUpdated({action}) 超时");
    }

    private void Track(long conversationId) => _createdConversations.Add(conversationId);

    private void CleanupConversations()
    {
        if (_createdConversations.Count == 0)
        {
            return;
        }

        try
        {
            var connectionString = ResolveImConnectionString();
            if (connectionString is null)
            {
                return;
            }

            using var connection = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            var ids = string.Join(",", _createdConversations);
            command.CommandText = $"""
                DELETE FROM dbo.im_audit_log WHERE ConversationId IN ({ids});
                DELETE FROM dbo.im_conversations WHERE Id IN ({ids});
                """;
            command.ExecuteNonQuery();
        }
        catch
        {
            // 清理失败仅残留开发库测试数据
        }
    }

    private static string? ResolveImConnectionString()
    {
        var env = Environment.GetEnvironmentVariable("EOS_IM_TEST_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");
            var text = File.ReadAllText(path);
            var match = System.Text.RegularExpressions.Regex.Match(text, "MSSQL_CONNECTION_STRING\\s*=\\s*\"([^\"]+)\"");
            return match.Success
                ? match.Groups[1].Value.Replace("Database=Hiswitek", "Database=EOS.IM")
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Env(string name, string fallback)
        => Environment.GetEnvironmentVariable(name) ?? fallback;

    private static Microsoft.AspNetCore.Http.Connections.HttpTransportType ParseTransport(string name)
        => name.Equals("LongPolling", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.AspNetCore.Http.Connections.HttpTransportType.LongPolling
            : Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

/// <summary>MessageRecalled 事件负载（服务端匿名对象）。</summary>
public sealed record ImRecalledEvent(long ConversationId, long MessageId, string RecalledByUserId);

/// <summary>ConversationUpdated 事件负载（服务端匿名对象，忽略未知字段）。</summary>
public sealed record ImUpdatedEvent(long ConversationId, string Action);
