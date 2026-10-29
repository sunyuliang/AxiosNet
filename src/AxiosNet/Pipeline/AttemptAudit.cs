using AxiosNet.Telemetry;

namespace AxiosNet.Pipeline;

public static partial class Attempt
{
    /// <summary>
    /// 每次尝试的审计：在终端前后计时，按尝试发出 <see cref="AxiosTelemetry.AttemptStopEventName"/> /
    /// <see cref="AxiosTelemetry.AttemptFailedEventName"/>（载荷 <see cref="AxiosAttemptEvent"/>）。
    /// </summary>
    /// <remarks>
    /// 不在默认链上：用 <c>AxiosAttemptPipeline.Default.Append(Attempt.Audit)</c> 放到闸门之后、终端之前。
    /// 只覆盖真正到达终端的尝试 —— 熔断拒绝、闸门排队超时与短路响应都不产生尝试事件。
    /// 对冲的每条分支、备用端点的每个端点各一条事件（尝试号相同）；被胜出者取消的败者不发失败事件。
    /// 计时从进入终端前到终端返回，不含闸门排队与退避等待，流式响应只到响应头。
    /// </remarks>
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

            // is { } 防止中间件返回 default 结果时变成空引用。
            AxiosTelemetry.AttemptSucceeded(ctx, result.Response is { } response ? response.Status : null, startTimestamp);

            return result;
        }
        catch (Exception exception)
        {
            // 被胜出者收掉的对冲败者不算失败的尝试。
            if (!IsCleanupCancellation(exception, ctx))
            {
                AxiosTelemetry.AttemptFailed(ctx, exception, startTimestamp);
            }

            throw;
        }
    }
}
