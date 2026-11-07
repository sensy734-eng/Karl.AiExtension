using System.ComponentModel;
using AI.Extension;
using AI.Extension.Generated.Demo.Contract;
using Demo.Model;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Demo.MCP;

/// <summary>
/// 订单 AI 工具（开发者手写的「轻量语义封装」）。
/// <para>
/// 这一层的职责边界：
/// 1) 面向 AI 的语义（工具名、描述、扁平入参），而不是 ViewModel 的 UI 语义；
/// 2) 参数二次校验与权限/业务前置检查；
/// 3) 异常兜底：通过 <see cref="ToolOutcome{T}"/> 保留业务 JSON，并标记 MCP 错误结果，让 AI 能读懂并自我纠正；
/// 4) 把调用写入 UI 上下文日志（供后续对话读取）。
/// 业务逻辑一律通过源生成器产出的 <see cref="OrderServiceBridge"/> 转发，绝不在这里写业务。
/// </para>
/// </summary>
[McpServerToolType]
public sealed class OrderTools
{
    private readonly OrderServiceBridge _bridge;
    private readonly IUiActivityTracker _activityTracker;

    /// <summary>构造工具。桥接与追踪器都由 DI 注入。</summary>
    public OrderTools(OrderServiceBridge bridge, IUiActivityTracker activityTracker)
    {
        _bridge = bridge;
        _activityTracker = activityTracker;
    }

    /// <summary>检索订单。</summary>
    [McpServerTool(Name = "order_search")]
    [Description("按关键词（订单号 / 客户名 / 备注）与状态检索订单，返回匹配的订单列表。status 省略表示不限状态。")]
    public async Task<CallToolResult> SearchOrdersAsync(
        [Description("关键词，可留空表示不按关键词过滤")] string? keyword = null,
        [Description("订单状态：Pending / Paid / Shipped / Completed / Cancelled，可留空")] string? status = null,
        CancellationToken cancellationToken = default)
    {
        _activityTracker.Record(
            UiActivityCategory.AgentAction,
            $"AI 调用 order_search（keyword={keyword ?? "-"}, status={status ?? "-"}）");

        if (!TryParseStatus(status, out OrderStatus? parsedStatus, out string? statusError))
        {
            return ToolOutcome<IReadOnlyList<Order>>.Fail(statusError!).ToCallToolResult();
        }

        try
        {
            IReadOnlyList<Order> orders = await _bridge
                .SearchAsync(keyword, parsedStatus, cancellationToken)
                .ConfigureAwait(false);

            return ToolOutcome<IReadOnlyList<Order>>.Ok(orders).ToCallToolResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolOutcome<IReadOnlyList<Order>>.Fail($"检索订单失败：{exception.Message}").ToCallToolResult();
        }
    }

    /// <summary>获取订单详情。</summary>
    [McpServerTool(Name = "order_get")]
    [Description("按订单号精确获取订单详情。订单号形如 SO-1002。")]
    public async Task<CallToolResult> GetOrderAsync(
        [Description("订单号，例如 SO-1002")] string orderId,
        CancellationToken cancellationToken = default)
    {
        _activityTracker.Record(UiActivityCategory.AgentAction, $"AI 调用 order_get（orderId={orderId}）");

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return ToolOutcome<Order>.Fail("订单号不能为空。").ToCallToolResult();
        }

        try
        {
            Order? order = await _bridge.GetByIdAsync(orderId.Trim(), cancellationToken).ConfigureAwait(false);
            return order is null
                ? ToolOutcome<Order>.Fail($"未找到订单 {orderId}。").ToCallToolResult()
                : ToolOutcome<Order>.Ok(order).ToCallToolResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolOutcome<Order>.Fail($"获取订单失败：{exception.Message}").ToCallToolResult();
        }
    }

    /// <summary>取消订单。</summary>
    [McpServerTool(Name = "order_cancel")]
    [Description("取消指定订单并记录取消原因。已完成或已取消的订单无法取消，会返回失败原因。这是会修改数据的敏感操作。")]
    public async Task<CallToolResult> CancelOrderAsync(
        [Description("要取消的订单号，例如 SO-1001")] string orderId,
        [Description("取消原因，会写入订单备注，请写清楚业务理由")] string reason,
        CancellationToken cancellationToken = default)
    {
        _activityTracker.Record(UiActivityCategory.AgentAction, $"AI 调用 order_cancel（orderId={orderId}）");

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return ToolOutcome<OrderOperationResult>.Fail("订单号不能为空。").ToCallToolResult();
        }

        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length < 2)
        {
            return ToolOutcome<OrderOperationResult>.Fail("取消原因不能为空，且至少 2 个字符。").ToCallToolResult();
        }

        try
        {
            OrderOperationResult result = await _bridge
                .CancelAsync(orderId.Trim(), reason.Trim(), cancellationToken)
                .ConfigureAwait(false);

            return result.Success
                ? ToolOutcome<OrderOperationResult>.Ok(result).ToCallToolResult()
                : ToolOutcome<OrderOperationResult>.Fail(result.Message).ToCallToolResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolOutcome<OrderOperationResult>.Fail($"取消订单失败：{exception.Message}").ToCallToolResult();
        }
    }

    /// <summary>订单统计摘要。</summary>
    [McpServerTool(Name = "order_summary")]
    [Description("统计当前所有订单：总数、总金额与各状态数量。用户问「现在订单情况怎么样」时使用。")]
    public async Task<CallToolResult> GetSummaryAsync(CancellationToken cancellationToken = default)
    {
        _activityTracker.Record(UiActivityCategory.AgentAction, "AI 调用 order_summary");

        try
        {
            OrderSummary summary = await _bridge.GetSummaryAsync(cancellationToken).ConfigureAwait(false);
            return ToolOutcome<OrderSummary>.Ok(summary).ToCallToolResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolOutcome<OrderSummary>.Fail($"统计订单失败：{exception.Message}").ToCallToolResult();
        }
    }

    /// <summary>新增订单。</summary>
    [McpServerTool(Name = "order_create")]
    [Description("新增一笔订单：客户名称、金额、可选备注。新订单默认为待支付状态。会修改数据。")]
    public async Task<CallToolResult> CreateOrderAsync(
        [Description("客户名称")] string customerName,
        [Description("订单金额，必须大于 0")] decimal amount,
        [Description("备注，可留空")] string? remark = null,
        CancellationToken cancellationToken = default)
    {
        _activityTracker.Record(UiActivityCategory.AgentAction, $"AI 调用 order_create（customer={customerName}, amount={amount}）");

        if (string.IsNullOrWhiteSpace(customerName))
        {
            return ToolOutcome<OrderOperationResult>.Fail("客户名称不能为空。").ToCallToolResult();
        }

        try
        {
            OrderOperationResult result = await _bridge
                .CreateAsync(customerName.Trim(), amount, string.IsNullOrWhiteSpace(remark) ? null : remark.Trim(), cancellationToken)
                .ConfigureAwait(false);

            return result.Success
                ? ToolOutcome<OrderOperationResult>.Ok(result).ToCallToolResult()
                : ToolOutcome<OrderOperationResult>.Fail(result.Message).ToCallToolResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolOutcome<OrderOperationResult>.Fail($"新增订单失败：{exception.Message}").ToCallToolResult();
        }
    }

    /// <summary>修改订单。</summary>
    [McpServerTool(Name = "order_update")]
    [Description("修改订单的客户名称 / 金额 / 备注；未提供的字段保持不变。会修改数据。")]
    public async Task<CallToolResult> UpdateOrderAsync(
        [Description("要修改的订单号")] string orderId,
        [Description("新客户名称，可留空表示不修改")] string? customerName = null,
        [Description("新金额，可留空表示不修改")] decimal? amount = null,
        [Description("新备注；传空字符串表示清空备注，留空表示不修改")] string? remark = null,
        CancellationToken cancellationToken = default)
    {
        _activityTracker.Record(UiActivityCategory.AgentAction, $"AI 调用 order_update（orderId={orderId}）");

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return ToolOutcome<OrderOperationResult>.Fail("订单号不能为空。").ToCallToolResult();
        }

        if (amount is <= 0)
        {
            return ToolOutcome<OrderOperationResult>.Fail("金额必须大于 0。").ToCallToolResult();
        }

        try
        {
            OrderOperationResult result = await _bridge
                .UpdateAsync(orderId.Trim(), customerName, amount, remark, cancellationToken)
                .ConfigureAwait(false);

            return result.Success
                ? ToolOutcome<OrderOperationResult>.Ok(result).ToCallToolResult()
                : ToolOutcome<OrderOperationResult>.Fail(result.Message).ToCallToolResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolOutcome<OrderOperationResult>.Fail($"修改订单失败：{exception.Message}").ToCallToolResult();
        }
    }

    /// <summary>变更订单状态。</summary>
    [McpServerTool(Name = "order_change_status")]
    [Description("变更订单状态，例如把待支付订单标记为 Paid、已支付订单标记为 Shipped / Completed。已完成或已取消的订单不能再变更。会修改数据。")]
    public async Task<CallToolResult> ChangeOrderStatusAsync(
        [Description("订单号，例如 SO-1001")] string orderId,
        [Description("目标状态：Pending / Paid / Shipped / Completed（取消请用 order_cancel）")] string status,
        CancellationToken cancellationToken = default)
    {
        _activityTracker.Record(UiActivityCategory.AgentAction, $"AI 调用 order_change_status（orderId={orderId}, status={status}）");

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return ToolOutcome<OrderOperationResult>.Fail("订单号不能为空。").ToCallToolResult();
        }

        if (!TryParseStatus(status, out OrderStatus? parsedStatus, out string? statusError) || parsedStatus is null)
        {
            return ToolOutcome<OrderOperationResult>.Fail(statusError ?? "无法识别的订单状态。").ToCallToolResult();
        }

        try
        {
            OrderOperationResult result = await _bridge
                .ChangeStatusAsync(orderId.Trim(), parsedStatus.Value, cancellationToken)
                .ConfigureAwait(false);

            return result.Success
                ? ToolOutcome<OrderOperationResult>.Ok(result).ToCallToolResult()
                : ToolOutcome<OrderOperationResult>.Fail(result.Message).ToCallToolResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolOutcome<OrderOperationResult>.Fail($"变更状态失败：{exception.Message}").ToCallToolResult();
        }
    }

    /// <summary>删除订单。</summary>
    [McpServerTool(Name = "order_delete")]
    [Description("物理删除订单，不可恢复。破坏性操作，必须显式传 confirm=true 才会执行。")]
    public async Task<CallToolResult> DeleteOrderAsync(
        [Description("要删除的订单号")] string orderId,
        [Description("必须为 true 才执行删除，用于防止误删")] bool confirm,
        CancellationToken cancellationToken = default)
    {
        _activityTracker.Record(UiActivityCategory.AgentAction, $"AI 调用 order_delete（orderId={orderId}, confirm={confirm}）");

        if (!confirm)
        {
            return ToolOutcome<OrderOperationResult>.Fail("删除是破坏性操作：请先向用户确认，再以 confirm=true 重新调用。").ToCallToolResult();
        }

        if (string.IsNullOrWhiteSpace(orderId))
        {
            return ToolOutcome<OrderOperationResult>.Fail("订单号不能为空。").ToCallToolResult();
        }

        try
        {
            OrderOperationResult result = await _bridge.DeleteAsync(orderId.Trim(), cancellationToken).ConfigureAwait(false);
            return result.Success
                ? ToolOutcome<OrderOperationResult>.Ok(result).ToCallToolResult()
                : ToolOutcome<OrderOperationResult>.Fail(result.Message).ToCallToolResult();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ToolOutcome<OrderOperationResult>.Fail($"删除订单失败：{exception.Message}").ToCallToolResult();
        }
    }

    private static bool TryParseStatus(string? status, out OrderStatus? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (string.IsNullOrWhiteSpace(status))
        {
            return true;
        }

        if (Enum.TryParse(status.Trim(), ignoreCase: true, out OrderStatus value) &&
            Enum.IsDefined(value))
        {
            parsed = value;
            return true;
        }

        error = $"无法识别的订单状态「{status}」，可用值：{string.Join(" / ", Enum.GetNames<OrderStatus>())}。";
        return false;
    }
}
