using System.Runtime.CompilerServices;
using System.Text.Json;
using AI.Extension;
using AI.Extension.Chat;
using AI.Extension.Mcp;
using AI.Extension.Providers;
using Demo.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Demo.Tests;

/// <summary>验证模型收到完整 MCP 结果，展示摘要与执行失败状态保持一致。</summary>
public sealed class ToolResultSemanticsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LongToolResult_ReachesModelWhileTraceAndHistoryStayBounded(bool streaming, bool fallback)
    {
        await using var scope = await HostScope.StartAsync();
        CallToolResult expected = await scope.Bridge.Client.CallToolAsync("order_search", new Dictionary<string, object?>());
        string expectedText = GetText(expected);
        Assert.True(expectedText.Length > 400);

        var client = new CapturingClient("order_search", new(), fallback);
        var conversation = new Conversation { Title = "结果语义测试" };
        ChatSession session = scope.CreateSession(client, conversation);
        AiChatReply reply = await SendAsync(session, streaming);

        JsonElement payload = Assert.IsType<JsonElement>(Assert.Single(client.Results).Result);
        Assert.Equal(expectedText, payload.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Contains("SO-1005", payload.GetProperty("content")[0].GetProperty("text").GetString());
        AiToolCallTrace trace = Assert.Single(reply.ToolCalls);
        Assert.True(trace.Success);
        Assert.Equal(expectedText[..400] + "…", trace.ResultPreview);
        Assert.DoesNotContain("SO-1005", trace.ResultPreview);
        Assert.DoesNotContain("SO-1005", JsonSerializer.Serialize(conversation));
        Assert.Equal(CapturingClient.CallId, client.Results[0].CallId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BusinessFailure_IsFailureForModelTraceAndPersistedConversation(bool streaming, bool fallback)
    {
        await using var scope = await HostScope.StartAsync();
        var client = new CapturingClient("order_search", new() { ["status"] = "NotAStatus" }, fallback);
        var conversation = new Conversation { Title = "错误语义测试" };
        ChatSession session = scope.CreateSession(client, conversation);
        var events = new List<AiToolCallTrace>();
        session.ToolCallCompleted += events.Add;

        AiChatReply reply = await SendAsync(session, streaming);

        AiToolCallTrace trace = Assert.Single(reply.ToolCalls);
        Assert.False(trace.Success);
        Assert.Equal(trace, Assert.Single(events));
        JsonElement payload = Assert.IsType<JsonElement>(Assert.Single(client.Results).Result);
        Assert.True(payload.GetProperty("isError").GetBoolean());
        using JsonDocument body = JsonDocument.Parse(payload.GetProperty("content")[0].GetProperty("text").GetString()!);
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains("NotAStatus", body.RootElement.GetProperty("error").GetString());
        Assert.Contains("\"Success\":false", JsonSerializer.Serialize(conversation));
    }

    [Theory]
    [InlineData("order_search", "{\"status\":\"NotAStatus\"}")]
    [InlineData("order_get", "{\"orderId\":\"SO-MISSING\"}")]
    [InlineData("order_cancel", "{\"orderId\":\"SO-1004\",\"reason\":\"测试取消\"}")]
    [InlineData("order_create", "{\"customerName\":\"测试\",\"amount\":0}")]
    [InlineData("order_update", "{\"orderId\":\"SO-1001\",\"amount\":-1}")]
    [InlineData("order_change_status", "{\"orderId\":\"SO-1004\",\"status\":\"Paid\"}")]
    [InlineData("order_delete", "{\"orderId\":\"SO-1001\",\"confirm\":false}")]
    public async Task DirectMcpCall_BusinessFailureSetsIsError(string tool, string arguments)
    {
        await using var scope = await HostScope.StartAsync();
        var args = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments)!;
        CallToolResult result = await scope.Bridge.Client.CallToolAsync(tool, args);

        Assert.True(result.IsError);
        using JsonDocument body = JsonDocument.Parse(GetText(result));
        Assert.False(body.RootElement.GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("error").GetString()));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StructuredAndNonTextContent_ArePreservedForModel(bool streaming, bool fallback)
    {
        var result = new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "first block" },
                new TextContentBlock { Text = "second block" },
                new ImageContentBlock { MimeType = "image/png", Data = "AQID"u8.ToArray() },
            ],
            StructuredContent = JsonSerializer.SerializeToElement(new { marker = "structured-tail", count = 5 }),
        };
        await using var scope = await HostScope.StartAsync(result);
        var client = new CapturingClient("review_payload", new(), fallback);
        AiChatReply reply = await SendAsync(scope.CreateSession(client), streaming);

        AiToolCallTrace trace = Assert.Single(reply.ToolCalls);
        Assert.True(trace.Success, trace.ResultPreview);
        JsonElement payload = Assert.IsType<JsonElement>(Assert.Single(client.Results).Result);
        JsonElement content = payload.GetProperty("content");
        Assert.True(content.GetArrayLength() == 3, payload.GetRawText());
        Assert.Equal("first block", content[0].GetProperty("text").GetString());
        Assert.Equal("second block", content[1].GetProperty("text").GetString());
        Assert.Equal("image", content[2].GetProperty("type").GetString());
        Assert.Equal("AQID", content[2].GetProperty("data").GetString());
        Assert.Equal("structured-tail", payload.GetProperty("structuredContent").GetProperty("marker").GetString());
        Assert.True(Assert.Single(reply.ToolCalls).Success);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StructuredOnlyResult_HasPreviewAndCompleteModelPayload(bool streaming)
    {
        string value = new string('x', 500) + "structured-tail";
        var result = new CallToolResult
        {
            Content = [],
            StructuredContent = JsonSerializer.SerializeToElement(new { value }),
        };
        await using var scope = await HostScope.StartAsync(result);
        var client = new CapturingClient("review_payload", new());
        AiChatReply reply = await SendAsync(scope.CreateSession(client), streaming);

        JsonElement payload = Assert.IsType<JsonElement>(Assert.Single(client.Results).Result);
        Assert.Equal(value, payload.GetProperty("structuredContent").GetProperty("value").GetString());
        AiToolCallTrace trace = Assert.Single(reply.ToolCalls);
        Assert.StartsWith("{\"value\":", trace.ResultPreview);
        Assert.Equal(401, trace.ResultPreview.Length);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingTool_IsReportedWithMcpErrorPayload(bool streaming)
    {
        await using var scope = await HostScope.StartAsync();
        var client = new CapturingClient("missing_tool", new());
        AiChatReply reply = await SendAsync(scope.CreateSession(client), streaming);

        Assert.False(Assert.Single(reply.ToolCalls).Success);
        JsonElement payload = Assert.IsType<JsonElement>(Assert.Single(client.Results).Result);
        Assert.True(payload.GetProperty("isError").GetBoolean());
        Assert.Contains("工具不存在", payload.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(CapturingClient.CallId, client.Results[0].CallId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task LongToolError_ReachesModelWithoutTruncation(bool streaming, bool fallback)
    {
        string error = new string('x', 500) + "error-tail";
        await using var scope = await HostScope.StartAsync(new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = error }],
        });
        var client = new CapturingClient("review_payload", new(), fallback);
        AiChatReply reply = await SendAsync(scope.CreateSession(client), streaming);

        AiToolCallTrace trace = Assert.Single(reply.ToolCalls);
        Assert.False(trace.Success);
        Assert.Equal(error[..400] + "…", trace.ResultPreview);
        JsonElement payload = Assert.IsType<JsonElement>(Assert.Single(client.Results).Result);
        Assert.True(payload.GetProperty("isError").GetBoolean());
        Assert.Equal(error, payload.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ToolException_IsReportedAsFailure(bool streaming, bool fallback)
    {
        await using var scope = await HostScope.StartAsync(customTool:
            (Func<CallToolResult>)(() => throw new InvalidOperationException("测试工具异常")));
        var client = new CapturingClient("review_payload", new(), fallback);
        AiChatReply reply = await SendAsync(scope.CreateSession(client), streaming);

        AiToolCallTrace trace = Assert.Single(reply.ToolCalls);
        Assert.False(trace.Success);
        Assert.False(string.IsNullOrWhiteSpace(trace.ResultPreview));
        JsonElement payload = Assert.IsType<JsonElement>(Assert.Single(client.Results).Result);
        Assert.True(payload.GetProperty("isError").GetBoolean());
        Assert.Equal(CapturingClient.CallId, client.Results[0].CallId);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancellationDuringToolCall_PropagatesInsteadOfBecomingToolFailure(bool streaming, bool fallback)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scope = await HostScope.StartAsync(customTool:
            (Func<CancellationToken, Task<CallToolResult>>)(async cancellationToken =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return new CallToolResult { Content = [] };
            }));
        var client = new CapturingClient("review_payload", new(), fallback);
        ChatSession session = scope.CreateSession(client);
        var events = new List<AiToolCallTrace>();
        session.ToolCallCompleted += events.Add;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task<AiChatReply> pending = SendAsync(session, streaming, cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(client.Results);
        Assert.Empty(events);
    }

    private static Task<AiChatReply> SendAsync(ChatSession session, bool streaming, CancellationToken cancellationToken = default)
        => streaming
            ? session.SendStreamingAsync("验证工具结果", cancellationToken)
            : session.SendAsync("验证工具结果", cancellationToken);

    private static string GetText(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(block => block.Text));

    private sealed class HostScope(IHost host) : IAsyncDisposable
    {
        public McpServerBridge Bridge { get; } = host.Services.GetRequiredService<McpServerBridge>();

        public static async Task<HostScope> StartAsync(CallToolResult? customResult = null, Delegate? customTool = null)
        {
            IHost host = await DemoBootstrap.StartDemoHostAsync(
                providerStore: new InMemoryAiProviderStore(),
                configureServices: services =>
                {
                    if (customTool is not null)
                    {
                        services.AddSingleton(McpServerTool.Create(
                            customTool,
                            new McpServerToolCreateOptions { Name = "review_payload" }));
                    }
                    else if (customResult is not null)
                    {
                        services.AddSingleton(McpServerTool.Create(
                            () => customResult,
                            new McpServerToolCreateOptions { Name = "review_payload" }));
                    }
                });
            return new HostScope(host);
        }

        public ChatSession CreateSession(IChatClient client, Conversation? conversation = null)
            => new(new FixedAccessor(client), Bridge, host.Services.GetRequiredService<IUiActivityTracker>(),
                new AiChatOptions { AutoTitleFromFirstRound = false }, conversation, store: null);

        public async ValueTask DisposeAsync()
        {
            await Bridge.DisposeAsync();
            await host.StopAsync();
            host.Dispose();
        }
    }

    private sealed class FixedAccessor(IChatClient client) : IAiChatClientAccessor
    {
        public IChatClient Current => client;
        public string DisplayName => "结果语义测试";
        public bool IsConfigured => false;
        public event EventHandler? Changed { add { } remove { } }
    }

    private sealed class CapturingClient(string tool, Dictionary<string, object?> arguments, bool fallback = false) : IChatClient
    {
        public const string CallId = "result-semantics-call";
        public List<FunctionResultContent> Results { get; } = new();

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ChatMessage last = messages.Last();
            if (last.Role == ChatRole.Tool)
            {
                Results.AddRange(last.Contents.OfType<FunctionResultContent>());
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "验证完成")));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                new List<AIContent> { new FunctionCallContent(CallId, tool, arguments) })));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (fallback)
            {
                throw new NotSupportedException("测试非流式回退");
            }

            ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (ChatMessage message in response.Messages)
            {
                yield return new ChatResponseUpdate(message.Role, message.Contents);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
