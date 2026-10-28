using AxiosNet.Telemetry;

namespace AxiosNet.Pipeline;

public static partial class Attempt
{
    /// <summary>
    /// 每次尝试的审计：在终端前后计时，按尝试发出 <see cref="AxiosTelemetry.AttemptStopEventName"/> /
    /// <see cref="AxiosTelemetry.AttemptFailedEventName"/>（载荷 <see cref="AxiosAttemptEvent"/>）。
    /// <para>
    /// 它<b>不在默认链上</b>：用 <c>AxiosAttemptPipeline.Default.Append(Attempt.Audit)</c> 放到最内层
    /// （闸门之后、终端之前），量的才是单次尝试。这个位置只覆盖真正到达终端的尝试 ——
    /// 熔断拒绝、闸门排队超时、短路响应都不会产生尝试事件；请求级事件仍在 <c>RequestStart</c> /
    /// <c>RequestStop</c> / <c>RequestFailed</c> 里。
    /// </para>
    /// <para>
    /// 一个逻辑尝试可能出现多条事件：对冲的每条分支各一条（尝试号相同），换端点后每个端点各一条
    /// （尝试号相同、端点序号不同，见 <see cref="AxiosAttemptEvent.EndpointIndex"/>）。
    /// 被胜出者取消的败者不发失败事件 —— 那是清理，不是下游故障；在胜者出现之前就真实失败的败者照常发。
    /// </para>
    /// <para>
    /// 计时口径：从进入终端前到终端返回，不含闸门排队与退避等待；包含取连接租约与缓冲响应读完正文，
    /// 流式响应只到响应头。没有人订阅尝试事件时直接走下一环，不建状态机、不取时间戳。
    /// </para>
    /// </summary>
    public static AxiosAttemptMiddleware Audit { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        return (request, ctx) =>
            AxiosTelemetry.IsAttemptAuditEnabled(ctx.Config)
                ? AuditAsync(next, request, ctx)
                : next(request, ctx);
    };

    private static async ValueTask<AxiosAttemptResult> AuditAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage request,
        AxiosAttemptContext ctx)
    {
        var startTimestamp = AxiosTelemetry.Timestamp;

        try
        {
            var result = await next(request, ctx).ConfigureAwait(false);

            // Append 的位置保证下一环是终端；is { } 只是不让错误编排（中间件返回 default 结果）变成几层之外的空引用。
            AxiosTelemetry.AttemptSucceeded(ctx, result.Response is { } response ? response.Status : null, startTimestamp);

            return result;
        }
        catch (Exception exception)
        {
            // 对冲败者被胜出者收掉不是一次失败的尝试：发出去会让健康的对冲请求恒有一半「尝试失败」。
            if (!IsCleanupCancellation(exception, ctx))
            {
                AxiosTelemetry.AttemptFailed(ctx, exception, startTimestamp);
            }

            throw;
        }
    }
}
