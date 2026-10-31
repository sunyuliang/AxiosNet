using AxiosNet.Telemetry;

namespace AxiosNet.Pipeline;

public static partial class Attempt
{
    /// <summary>
    /// 每次尝试的审计：在终端前后计时，按尝试发出 <see cref="AxiosTelemetry.AttemptStopEventName"/> /
    /// <see cref="AxiosTelemetry.AttemptFailedEventName"/>（载荷 <see cref="AxiosAttemptEvent"/>）。
    /// </summary>
    /// <remarks>
    /// <b>已在默认链上</b>（<see cref="AxiosAttemptPipeline.Default"/> 的末位，闸门之后、终端之前），
    /// 订阅 <c>AxiosNet.Attempt</c> 就能拿到每次尝试的耗时、状态码与错误；未订阅时只做一次开关判定。
    /// 它只覆盖真正到达终端的尝试 —— 熔断拒绝、闸门排队超时与短路响应都不产生尝试事件。
    /// 对冲的每条分支、备用端点的每个端点各一条事件（尝试号相同；同一端点下的对冲分支各项序号都相同，
    /// 事件里没有可区分它们的字段）；被胜出者取消的败者不发失败事件。
    /// 计时从进入终端前到终端返回，不含闸门排队与退避等待，流式响应只到响应头。
    /// 已是默认链成员：再 <c>Append(Attempt.Audit)</c> 会被当作重复内置项拒绝；自定义链用 <see cref="AxiosAttemptPipeline.Create"/> 时显式列出即可。
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
