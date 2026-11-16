using AxiosNet.Telemetry;

namespace AxiosNet.Pipeline;

public static partial class Attempt
{
    /// <summary>
    /// 每次尝试的审计：在终端前后计时，按尝试发出 <see cref="AxiosTelemetry.AttemptStopEventName"/> /
    /// <see cref="AxiosTelemetry.AttemptFailedEventName"/>（载荷 <see cref="AxiosAttemptEvent"/>）。
    /// </summary>
    /// <remarks>
    /// 已在默认链上（闸门之后、终端之前），未订阅时只做一次开关判定。只覆盖真正到达终端的尝试：熔断拒绝、
    /// 闸门排队超时与短路响应不产生事件。对冲每条分支、备用端点每个端点各一条事件，被胜出者取消的败者不发失败事件。
    /// 计时不含闸门排队与退避等待，流式响应只到响应头。重复 <c>Append</c> 会被当作重复内置项拒绝。
    /// </remarks>
    public static AxiosAttemptMiddleware Audit { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        return (request, ctx) =>
            AxiosTelemetry.Dispatch.IsAttemptAuditEnabled(ctx.Config)
                ? AuditAsync(next, request, ctx)
                : next(request, ctx);
    };

    /// <summary>给一次尝试计时并上报起点 / 终点事件；只有订阅了尝试审计时才走这条分支。</summary>
    private static async ValueTask<AxiosAttemptResult> AuditAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage request,
        AxiosAttemptContext ctx)
    {
        var startTimestamp = AxiosTelemetry.Timestamp;

        try
        {
            var result = await next(request, ctx).ConfigureAwait(false);

            // is { } 防止中间件返回 default 结果时变成空引用。
            AxiosTelemetry.Dispatch.AttemptSucceeded(ctx, result.Response is { } response ? response.Status : null, startTimestamp);

            return result;
        }
        catch (Exception exception)
        {
            // 被胜出者收掉的对冲败者不算失败的尝试。
            if (!IsCleanupCancellation(exception, ctx))
            {
                AxiosTelemetry.Dispatch.AttemptFailed(ctx, exception, startTimestamp);
            }

            throw;
        }
    }
}
