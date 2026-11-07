using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AI.Extension.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace AI.Extension.Chat;

/// <summary>
/// AI 对话会话：把「大模型」「MCP 工具」「UI 上下文日志」串成一条闭环。
/// <para>
/// 工具循环是显式实现的（而不是交给中间件），原因：
/// 1) 需要把每次工具调用实时上报给 UI（工具轨迹面板）；
/// 2) 需要在这里做权限/确认拦截（敏感操作先问用户）；
/// 3) 需要把失败结果以「可读错误」回灌给模型，让它自我纠正。
/// </para>
/// </summary>
public sealed class ChatSession
{
    private const int MaxPreviewLength = 400;

    private static readonly JsonSerializerOptions TraceJsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly IAiChatClientAccessor _chatClientAccessor;
    private readonly McpServerBridge _bridge;
    private readonly IUiActivityTracker _activityTracker;
    private readonly AiChatOptions _options;
    private readonly Conversation? _conversation;
    private readonly IConversationStore? _store;
    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _turnGate = new(1, 1);

    /// <summary>构造会话（不绑定持久化会话，兼容旧用法/Blazor 单会话）。</summary>
    public ChatSession(
        IAiChatClientAccessor chatClientAccessor,
        McpServerBridge bridge,
        IUiActivityTracker activityTracker,
        AiChatOptions options)
        : this(chatClientAccessor, bridge, activityTracker, options, conversation: null, store: null)
    {
    }

    /// <summary>构造绑定到某个 <see cref="Conversation"/> 的会话（多标签/子智能体用）。</summary>
    public ChatSession(
        IAiChatClientAccessor chatClientAccessor,
        McpServerBridge bridge,
        IUiActivityTracker activityTracker,
        AiChatOptions options,
        Conversation? conversation,
        IConversationStore? store)
    {
        _chatClientAccessor = chatClientAccessor ?? throw new ArgumentNullException(nameof(chatClientAccessor));
        _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
        _activityTracker = activityTracker ?? throw new ArgumentNullException(nameof(activityTracker));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _conversation = conversation;
        _store = store;

        if (_conversation is not null)
        {
            LoadHistoryFromConversation(_conversation);
        }
    }

    /// <summary>工具调用完成事件（UI 可订阅以实时刷新轨迹）。</summary>
    public event Action<AiToolCallTrace>? ToolCallCompleted;

    /// <summary>会话标题变化（AI 自动生成或用户重命名）。</summary>
    public event Action<string>? TitleChanged;

    /// <summary>流式文本增量（仅 <see cref="SendStreamingAsync"/> 触发）。</summary>
    public event Action<string>? TextDelta;

    /// <summary>绑定的会话 Id（未绑定时为 null）。</summary>
    public string? ConversationId => _conversation?.Id;

    /// <summary>绑定的会话标题（未绑定时为 null）。</summary>
    public string? ConversationTitle => _conversation?.Title;

    /// <summary>绑定的会话是否已归档。</summary>
    public bool IsArchived => _conversation?.Archived ?? false;

    /// <summary>已持久化的消息列表（仅展示用；未绑定会话时为空）。</summary>
    public IReadOnlyList<ConversationMessage> ConversationMessages
        => _conversation?.Messages ?? (IReadOnlyList<ConversationMessage>)Array.Empty<ConversationMessage>();

    /// <summary>历史消息条数。</summary>
    public int HistoryCount
    {
        get
        {
            lock (_history)
            {
                return _history.Count;
            }
        }
    }

    /// <summary>清空对话历史（UI 的「新对话」）。</summary>
    public void ClearHistory()
    {
        lock (_history)
        {
            _history.Clear();
        }

        if (_conversation is not null)
        {
            _conversation.Messages.Clear();
            _conversation.UpdatedAt = DateTimeOffset.Now;
            _ = PersistSafeAsync();
        }
    }

    /// <summary>发送一条用户消息，返回最终回复与工具轨迹。</summary>
    public async Task<AiChatReply> SendAsync(string userInput, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendCoreAsync(userInput.Trim(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    /// <summary>发送一条用户消息（流式）：边生成边通过 <see cref="TextDelta"/> 推送增量。</summary>
    public async Task<AiChatReply> SendStreamingAsync(string userInput, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userInput);
        await _turnGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await SendStreamingCoreAsync(userInput.Trim(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private async Task<AiChatReply> SendCoreAsync(string userInput, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt()),
        };

        lock (_history)
        {
            messages.AddRange(_history);
        }

        messages.Add(new ChatMessage(ChatRole.User, userInput));

        var traces = new List<AiToolCallTrace>();
        string finalText = string.Empty;

        for (int iteration = 0; iteration < _options.MaxToolIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chatOptions = new ChatOptions
            {
                Tools = _bridge.Tools.Cast<AITool>().ToList(),
                ToolMode = ChatToolMode.Auto,
            };

            ChatResponse response = await _chatClientAccessor.Current
                .GetResponseAsync(messages, chatOptions, cancellationToken)
                .ConfigureAwait(false);

            messages.AddRange(response.Messages);

            List<FunctionCallContent> functionCalls = response.Messages
                .SelectMany(static message => message.Contents)
                .OfType<FunctionCallContent>()
                .ToList();

            if (functionCalls.Count == 0)
            {
                finalText = response.Text ?? string.Empty;
                break;
            }

            foreach (FunctionCallContent call in functionCalls)
            {
                ToolInvocation invocation = await InvokeToolAsync(call, cancellationToken).ConfigureAwait(false);
                traces.Add(invocation.Trace);
                ToolCallCompleted?.Invoke(invocation.Trace);
                messages.Add(invocation.ResultMessage);
            }
        }

        if (string.IsNullOrWhiteSpace(finalText))
        {
            finalText = BuildFallbackText(traces);
        }

        await SaveTurnAsync(userInput, finalText, traces, cancellationToken).ConfigureAwait(false);
        return new AiChatReply(finalText, traces);
    }

    private async Task<AiChatReply> SendStreamingCoreAsync(string userInput, CancellationToken cancellationToken)
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemPrompt()),
        };

        lock (_history)
        {
            messages.AddRange(_history);
        }

        messages.Add(new ChatMessage(ChatRole.User, userInput));

        var traces = new List<AiToolCallTrace>();
        string finalText = string.Empty;

        for (int iteration = 0; iteration < _options.MaxToolIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chatOptions = new ChatOptions
            {
                Tools = _bridge.Tools.Cast<AITool>().ToList(),
                ToolMode = ChatToolMode.Auto,
            };

            List<FunctionCallContent> functionCalls;
            string iterationText;
            try
            {
                (iterationText, functionCalls) = await StreamIterationAsync(messages, chatOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (NotSupportedException)
            {
                // 兜底客户端/供应商不支持流式：退回一次性返回
                ChatResponse response = await _chatClientAccessor.Current
                    .GetResponseAsync(messages, chatOptions, cancellationToken)
                    .ConfigureAwait(false);

                messages.AddRange(response.Messages);
                functionCalls = response.Messages
                    .SelectMany(static message => message.Contents)
                    .OfType<FunctionCallContent>()
                    .ToList();
                iterationText = response.Text ?? string.Empty;
            }

            if (functionCalls.Count == 0)
            {
                finalText = iterationText;
                break;
            }

            foreach (FunctionCallContent call in functionCalls)
            {
                ToolInvocation invocation = await InvokeToolAsync(call, cancellationToken).ConfigureAwait(false);
                traces.Add(invocation.Trace);
                ToolCallCompleted?.Invoke(invocation.Trace);
                messages.Add(invocation.ResultMessage);
            }
        }

        if (string.IsNullOrWhiteSpace(finalText))
        {
            finalText = BuildFallbackText(traces);
        }

        await SaveTurnAsync(userInput, finalText, traces, cancellationToken).ConfigureAwait(false);
        return new AiChatReply(finalText, traces);
    }

    /// <summary>读取一轮流式结果：累积文本、去重工具调用，并把文本增量通过 <see cref="TextDelta"/> 推出。</summary>
    private async Task<(string Text, List<FunctionCallContent> Calls)> StreamIterationAsync(
        List<ChatMessage> messages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        var contents = new List<AIContent>();
        var textBuilder = new StringBuilder();
        var functionCalls = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        bool receivedAnyUpdate = false;

        await foreach (ChatResponseUpdate update in _chatClientAccessor.Current
            .GetStreamingResponseAsync(messages, chatOptions, cancellationToken)
            .ConfigureAwait(false))
        {
            receivedAnyUpdate = true;

            foreach (AIContent content in update.Contents)
            {
                switch (content)
                {
                    case TextContent text:
                        string delta = text.Text ?? string.Empty;
                        if (delta.Length > 0)
                        {
                            textBuilder.Append(delta);
                            TextDelta?.Invoke(delta);
                        }

                        contents.Add(content);
                        break;

                    case FunctionCallContent call:
                        functionCalls[call.CallId ?? call.Name] = call;
                        contents.Add(content);
                        break;

                    default:
                        contents.Add(content);
                        break;
                }
            }
        }

        if (!receivedAnyUpdate)
        {
            throw new NotSupportedException("当前聊天客户端不支持流式返回。");
        }

        if (contents.Count > 0)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, contents));
        }

        return (textBuilder.ToString(), functionCalls.Values.ToList());
    }

    private static string BuildFallbackText(IReadOnlyList<AiToolCallTrace> traces)
        => traces.Count == 0
            ? "模型没有返回内容，请检查模型配置。"
            : $"已完成 {traces.Count} 次工具调用，但模型未给出最终答复（可能达到轮次上限）。";

    /// <summary>把本轮对话写入历史、会话文件（含自动标题）与活动日志。</summary>
    private async Task SaveTurnAsync(
        string userInput,
        string finalText,
        IReadOnlyList<AiToolCallTrace> traces,
        CancellationToken cancellationToken)
    {
        lock (_history)
        {
            _history.Add(new ChatMessage(ChatRole.User, userInput));
            _history.Add(new ChatMessage(ChatRole.Assistant, finalText));
        }

        if (_conversation is not null)
        {
            DateTimeOffset now = DateTimeOffset.Now;
            _conversation.Messages.Add(new ConversationMessage("user", userInput, now));
            _conversation.Messages.Add(new ConversationMessage(
                "assistant",
                finalText,
                now,
                traces.Select(static trace => new ConversationToolCall(
                    trace.ToolName,
                    trace.ArgumentsJson,
                    trace.Success,
                    trace.ResultPreview,
                    (long)trace.Duration.TotalMilliseconds)).ToArray()));
            _conversation.UpdatedAt = now;

            string? generatedTitle = null;
            if (string.IsNullOrWhiteSpace(_conversation.Title) || _conversation.Title == "新对话")
            {
                // 首轮结束：让 AI 根据这一轮对话生成标题，失败时用首条消息截断兜底
                generatedTitle = (_conversation.Messages.Count == 2
                        ? await TryGenerateTitleAsync(userInput, finalText, cancellationToken).ConfigureAwait(false)
                        : null)
                    ?? BuildTitle(userInput);
                _conversation.Title = generatedTitle;
            }

            await PersistSafeAsync(cancellationToken).ConfigureAwait(false);
            if (generatedTitle is not null)
            {
                TitleChanged?.Invoke(generatedTitle);
            }
        }

        _activityTracker.Record(
            UiActivityCategory.AgentAction,
            $"AI 对话完成：调用 {traces.Count} 次工具",
            traces.Count == 0 ? null : string.Join(" → ", traces.Select(static trace => trace.ToolName)));
    }

    private async Task<ToolInvocation> InvokeToolAsync(FunctionCallContent call, CancellationToken cancellationToken)
    {
        string argumentsJson = call.Arguments is null
            ? "{}"
            : JsonSerializer.Serialize(call.Arguments, TraceJsonOptions);

        McpClientTool? tool = _bridge.Tools.FirstOrDefault(
            candidate => string.Equals(candidate.Name, call.Name, StringComparison.Ordinal));

        if (tool is null)
        {
            string available = string.Join(", ", _bridge.Tools.Select(static t => t.Name));
            return CreateToolInvocation(
                call,
                argumentsJson,
                CreateToolError($"工具不存在。可用工具：{available}"),
                TimeSpan.Zero);
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            IReadOnlyDictionary<string, object?>? arguments =
                call.Arguments is null ? null : new Dictionary<string, object?>(call.Arguments);

            CallToolResult result = await tool
                .CallAsync(arguments, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            stopwatch.Stop();

            return CreateToolInvocation(call, argumentsJson, result, stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            return CreateToolInvocation(
                call,
                argumentsJson,
                CreateToolError(exception.Message),
                stopwatch.Elapsed);
        }
    }

    // 模型使用 SDK 转换的完整结果（含 isError / structuredContent / 全部内容块）。
    // 截断只用于 UI 与会话文件的轨迹，不能再把 ResultPreview 当作执行结果回灌。
    private static ToolInvocation CreateToolInvocation(
        FunctionCallContent call,
        string argumentsJson,
        CallToolResult result,
        TimeSpan duration)
        => new(
            new AiToolCallTrace(call.Name, argumentsJson, result.IsError is not true, Truncate(ExtractText(result)), duration),
            result.ToChatMessage(call.CallId));

    private static CallToolResult CreateToolError(string message)
        => new()
        {
            IsError = true,
            Content = [new TextContentBlock { Text = message }],
        };

    private sealed record ToolInvocation(AiToolCallTrace Trace, ChatMessage ResultMessage);

    private static string ExtractText(CallToolResult result)
    {
        var builder = new StringBuilder();
        foreach (ContentBlock block in result.Content ?? [])
        {
            if (block is TextContentBlock text)
            {
                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(text.Text);
            }
        }

        string preview = builder.ToString().Trim();
        return preview.Length > 0 ? preview : result.StructuredContent?.GetRawText() ?? string.Empty;
    }

    private static string Truncate(string value)
        => value.Length <= MaxPreviewLength ? value : value.Substring(0, MaxPreviewLength) + "…";

    /// <summary>重命名当前会话（持久化）。</summary>
    public async Task RenameAsync(string title, CancellationToken cancellationToken = default)
    {
        if (_conversation is null)
        {
            return;
        }

        _conversation.Title = string.IsNullOrWhiteSpace(title) ? "新对话" : title.Trim();
        _conversation.UpdatedAt = DateTimeOffset.Now;
        await PersistSafeAsync(cancellationToken).ConfigureAwait(false);
        TitleChanged?.Invoke(_conversation.Title);
    }

    /// <summary>归档当前会话（移动到 store.json）。</summary>
    public async Task ArchiveAsync(CancellationToken cancellationToken = default)
    {
        if (_conversation is null || _store is null)
        {
            return;
        }

        _conversation.Archived = true;
        _conversation.UpdatedAt = DateTimeOffset.Now;
        await _store.SaveAsync(_conversation, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>删除当前会话（两个存储文件里都移除）。</summary>
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (_conversation is null || _store is null)
        {
            return;
        }

        await _store.DeleteAsync(_conversation.Id, cancellationToken).ConfigureAwait(false);
    }

    private void LoadHistoryFromConversation(Conversation conversation)
    {
        foreach (ConversationMessage message in conversation.Messages)
        {
            ChatRole role = message.Role switch
            {
                "user" => ChatRole.User,
                "assistant" => ChatRole.Assistant,
                _ => ChatRole.System,
            };

            _history.Add(new ChatMessage(role, message.Text));
        }
    }

    private async Task PersistSafeAsync(CancellationToken cancellationToken = default)
    {
        if (_conversation is null || _store is null)
        {
            return;
        }

        try
        {
            await _store.SaveAsync(_conversation, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 会话落盘失败不阻塞对话（界面仍可继续使用）
            System.Diagnostics.Debug.WriteLine($"保存会话失败: {exception}");
        }
    }

    /// <summary>首轮结束后让模型生成会话标题；失败/为空时返回 null（由调用方兜底）。</summary>
    private async Task<string?> TryGenerateTitleAsync(string userInput, string assistantReply, CancellationToken cancellationToken)
    {
        if (!_options.AutoTitleFromFirstRound || !_chatClientAccessor.IsConfigured)
        {
            return null;
        }

        try
        {
            int maxLength = Math.Clamp(_options.AutoTitleMaxLength, 4, 40);
            var messages = new List<ChatMessage>
            {
                new(ChatRole.System, $"你是一个会话标题生成器。请根据对话内容生成一个简短的中文标题，不超过 {maxLength} 个字；不要引号、不要句号、不要解释，只输出标题本身。"),
                new(ChatRole.User, $"用户：{userInput}\n助手：{assistantReply}"),
            };

            ChatResponse response = await _chatClientAccessor.Current
                .GetResponseAsync(messages, null, cancellationToken)
                .ConfigureAwait(false);

            return CleanTitle(response.Text, maxLength);
        }
        catch
        {
            // 标题只是锦上添花，失败时回退到首条消息截断
            return null;
        }
    }

    private static string? CleanTitle(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string title = text.Trim();
        title = title.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
        title = title.Trim('"', '\'', '“', '”', '‘', '’', '「', '」', '《', '》', '。', '，', ',', '.', ' ', '：', ':');
        if (title.Length > maxLength)
        {
            title = title[..maxLength];
        }

        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static string BuildTitle(string userInput)
    {
        string title = userInput.Trim().ReplaceLineEndings(" ");
        return title.Length <= 30 ? title : title[..30] + "…";
    }

    private string BuildSystemPrompt()
    {
        string uiContext = _activityTracker.BuildContextSummary(_options.UiContextEntryCount);

        return $"""
            {_options.SystemPrompt}

            # 当前界面上下文
            以下是用户最近在界面上的操作（由 UI 活动环形日志在唤醒时读取，不是实时推送）：
            {uiContext}
            """;
    }
}



