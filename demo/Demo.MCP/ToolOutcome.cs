using System.Text.Json;
using AI.Extension;
using ModelContextProtocol.Protocol;

namespace Demo.MCP;

/// <summary>
/// 工具调用的统一返回结构：让 AI 拿到「成功/失败 + 可读原因」，
/// 而不是把 .NET 异常原样抛给模型（异常应被封装层转成工具错误）。
/// </summary>
/// <typeparam name="T">成功时的载荷类型。</typeparam>
/// <param name="Success">是否成功。</param>
/// <param name="Value">成功时的数据。</param>
/// <param name="Error">失败时的可读原因（供 AI 自我纠正）。</param>
public sealed record ToolOutcome<T>(bool Success, T? Value, string? Error)
{
    private static readonly JsonSerializerOptions SerializerOptions = AiExtensionJson.CreateToolSerializerOptions();

    /// <summary>构造成功结果。</summary>
    public static ToolOutcome<T> Ok(T value) => new(true, value, null);

    /// <summary>构造失败结果。</summary>
    public static ToolOutcome<T> Fail(string error) => new(false, default, error);

    /// <summary>
    /// 在 MCP 边界保留原有业务 JSON，并将业务失败映射为工具执行错误。
    /// 不抛协议异常，让内置会话和外部 MCP 客户端都能读取失败原因并纠正参数。
    /// </summary>
    public CallToolResult ToCallToolResult() => new()
    {
        IsError = Success ? null : true,
        Content = [new TextContentBlock { Text = JsonSerializer.Serialize(this, SerializerOptions) }],
    };
}
