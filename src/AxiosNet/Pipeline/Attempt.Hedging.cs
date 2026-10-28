using System.Runtime.ExceptionServices;
using AxiosNet.Internal;
using AxiosNet.Retry;

namespace AxiosNet.Pipeline;

// 「一次尝试」的对冲：首发之后按 Delay 放出延迟副本，第一个成功结果胜出，其余分支取消并收尾。
public static partial class Attempt
{
    /// <summary>
    /// 对冲：首发之后按 <see cref="AxiosConfig.HedgingSettings.Delay"/> 的间隔再放出延迟副本，
    /// <b>第一个成功结果胜出</b>（不比较耗时，谁先返回用谁）；其余分支取消并等它们真正收尾。
    /// <b>所有分支都失败</b>时把失败原样抛给外层的重试；没配重试就是调用方看到的错误。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 位置在 <see cref="Retry"/>、<see cref="Failover"/>、<see cref="Circuit"/> 之内、<see cref="Gate"/> 之外：
    /// 熔断看的是整组分支的最终结果（一次逻辑尝试只记一笔，与重试的记账口径一致），
    /// 每条分支则各自过并发闸门、各占一个槽位 —— 所以开启对冲后这次请求占用的并发额度是 <c>1 + MaxHedges</c> 倍，
    /// <see cref="AxiosConfig.LimitSettings.MaxConcurrentRequests"/> 要按这个倍数预留。
    /// </para>
    /// <para>
    /// 未配置（<c>Hedging.MaxHedges = 0</c>）时是纯直通。以下请求按单次发送处理，不降级也不报错：
    /// 流式响应（两条分支都会在响应头处「成功」，败者的正文无人消费）、
    /// 非幂等方法（未显式打开 <see cref="AxiosConfig.HedgingSettings.HedgeNonIdempotentMethods"/>）、
    /// 正文不能并发重放（流 / multipart / 上传进度包装都带共享可变状态）、
    /// 响应侧带共享状态（下载进度回调与限速器按一条流设计，两条分支会各报一份）。
    /// </para>
    /// <para>
    /// <c>transformResponse</c> 与响应侧回调在每条分支上各执行一次（含最终落败的分支）——
    /// 带副作用的转换请自行保证幂等，或关掉对冲。
    /// </para>
    /// </remarks>
    public static AxiosAttemptMiddleware Hedging { get; } = next =>
    {
        ArgumentNullException.ThrowIfNull(next);

        // 快路径不建状态机：绝大多数请求不配对冲。
        return (request, ctx) =>
            ctx.Hedging is { MaxHedges: > 0 } hedging && CanHedge(request, ctx, hedging)
                ? HedgeAsync(next, request, ctx, hedging)
                : next(request, ctx);
    };

    /// <summary>这次请求能不能对冲：不满足时按一次普通发送处理（对冲是延迟优化，不是语义保证）。</summary>
    private static bool CanHedge(
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        AxiosConfig.HedgingSettings hedging)
    {
        // 流式响应：胜出者之外的分支也会在响应头处成功，败者的正文没人读，释放责任无处安放。
        if (ctx.StreamResponse)
        {
            return false;
        }

        // 非幂等方法默认不对冲：重发可能让服务端收到两次写操作。
        if (!hedging.HedgeNonIdempotentMethods && !RetryPolicy.IsIdempotent(ctx.Config.Method))
        {
            return false;
        }

        // 响应侧带共享状态：下载进度回调与限速器都是按「一条流」设计的，两条分支会各报一份（进度翻倍、限速失效）。
        if (ctx.Config.CallbacksOrNull?.OnDownloadProgress is not null || ctx.Config.LimitsOrNull?.MaxRate is > 0)
        {
            return false;
        }

        // 正文必须能并发重放：内存缓冲的正文没有状态，两条分支同时序列化是安全的；
        // 流 / multipart / 进度包装都带共享可变状态（读指针、计量器），并发写会互相踩。
        return IsParallelSafeBody(request.Content);
    }

    /// <summary>正文是否可被两条分支同时序列化：只有内存缓冲形态才是安全的。</summary>
    private static bool IsParallelSafeBody(HttpContent? content)
        => content switch
        {
            null => true,

            // string / byte[] / form-urlencoded / JSON 文本都落在 ByteArrayContent 上。
            ByteArrayContent => true,

            // ReadOnlyMemory 的零拷贝正文。
            ReadOnlyMemoryContent => true,

            // StreamContent / MultipartContent / ProgressHttpContent（共享进度计量器）/ 调用方自备内容：一律按不安全处理。
            _ => false
        };

    /// <summary>
    /// 放出各条分支并等待第一个成功结果。失败分支的异常先记账；全部失败时抛第一条失败，
    /// 让外层重试决定是否再来一轮。
    /// </summary>
    private static async ValueTask<AxiosAttemptResult> HedgeAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage template,
        AxiosAttemptContext ctx,
        AxiosConfig.HedgingSettings hedging)
    {
        var branches = hedging.MaxHedges + 1;

        // 链接调用方令牌：胜出者出现时取消败者，调用方取消时连锁取消所有分支。
        using var group = CancellationTokenSource.CreateLinkedTokenSource(ctx.ExternalToken);

        var remaining = new List<Task<AxiosAttemptResult>>(branches);
        for (var index = 0; index < branches; index++)
        {
            remaining.Add(SendHedgeBranchAsync(next, template, ctx, hedging.Delay, index, group.Token));
        }

        Exception? firstFailure = null;
        try
        {
            while (remaining.Count > 0)
            {
                var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
                remaining.Remove(completed);

                try
                {
                    // 第一个成功结果胜出：不比较耗时，谁先返回用谁。
                    // Ensure 挡的是中间件返回 default 结果：那会让下面的 Response.Config 变成裸空引用。
                    var winner = AxiosAttemptResult.Ensure(await completed.ConfigureAwait(false), ctx.Config);

                    // 各分支各持一份隔离的配置（终端往里写 Response），胜出者的那份回填到共享配置：
                    // 响应拦截器与调用方仍按契约从 ctx.Config.Response 读本次响应的状态码与响应头。
                    if (winner.Response.Config is { } branchConfig)
                    {
                        if (branchConfig.Response is { } responseInfo)
                        {
                            ctx.Config.Response = responseInfo;
                        }

                        // 交出去的响应仍指向本次请求的配置：调用方改 response.Config 时不该落到已丢弃的分支副本上。
                        winner.Response.Config = ctx.Config;
                    }

                    return winner;
                }
                catch (Exception ex) when (IsCleanupCancellation(ex, ctx))
                {
                    // 败者在起跑前 / 发送中 / 闸门排队时被链接令牌取消：这是清理，不算一次失败。
                }
                catch (Exception ex)
                {
                    // 排队超时只说明「没抢到槽位」，还会盖掉真正的下游故障（而且库级不重试它）：
                    // 已经记下它时用后到的真实失败替换，让调用方看到能定位问题的那一条。
                    if (firstFailure is null || IsQueueTimeout(firstFailure))
                    {
                        firstFailure = ex;
                    }
                }
            }

            // 全部分支都失败：原样交给外层的重试（它决定要不要再来一轮），没有重试就是调用方看到的错误。
            if (firstFailure is null)
            {
                throw AxiosException.Canceled(ctx.Config, template, "所有对冲分支都被取消");
            }

            ExceptionDispatchInfo.Capture(firstFailure).Throw();
            throw new InvalidOperationException("对冲分支全部失败但没有可抛出的异常。");
        }
        finally
        {
            // 取消仍在路上的分支，并等它们真正收尾：不等就会在返回之后继续占着连接租约与并发槽位。
            group.Cancel();

            foreach (var pending in remaining)
            {
                await DiscardAsync(pending).ConfigureAwait(false);
            }
        }
    }

    /// <summary>这条失败是不是「并发闸门排队超时」：它说明不了下游健康与否。</summary>
    private static bool IsQueueTimeout(Exception exception)
        => exception is AxiosException { Code: AxiosException.ErrQueueTimeout };

    /// <summary>
    /// 收尾一条没被采用的分支：等它真正结束，再释放它可能持有的响应资源。
    /// 已经成功返回的败者同样要收尾 —— 它带着一份完整的响应与（流式）所有权凭证。
    /// </summary>
    private static async ValueTask DiscardAsync(Task<AxiosAttemptResult> branch)
    {
        AxiosAttemptResult result;
        try
        {
            result = await branch.ConfigureAwait(false);
        }
        catch
        {
            // 失败 / 被取消的分支：终端已经在自己的失败路径上归还了租约与槽位。
            return;
        }

        try
        {
            Discard(result);
        }
        catch
        {
            // 收尾失败不能盖掉胜出者的结果。
        }
    }

    /// <summary>
    /// 释放一条败者结果持有的库内资源：流式凭证（正文流 + 连接租约 + 并发槽位）。
    /// 非流式正文在终端里已经读到 EOF、响应消息也已释放，<b>响应正文本身归调用方 / 序列化器所有</b>，
    /// 库不在这里释放它（自定义序列化器或 <c>transformResponse</c> 可能返回池化 / 缓存对象）。
    /// 流式正文也不排空 —— 排空一个没人要的正文等于替败者把整个响应下载完，直接释放即可
    /// （连接被丢弃，但租约与槽位一定归还）。
    /// </summary>
    private static void Discard(AxiosAttemptResult result)
        => result.StreamOwner?.Dispose();

    /// <summary>
    /// 第 <paramref name="index"/> 条分支：先按 <c>index × delay</c> 等待，再换一条自己的请求消息发送。
    /// 第 0 条直接用模板；其余按模板复制（正文沿用同一份，已由 <see cref="IsParallelSafeBody"/> 保证可并发重放）。
    /// </summary>
    private static async Task<AxiosAttemptResult> SendHedgeBranchAsync(
        AxiosAttemptHandler next,
        HttpRequestMessage template,
        AxiosAttemptContext ctx,
        TimeSpan delay,
        int index,
        CancellationToken branchToken)
    {
        if (index > 0 && delay > TimeSpan.Zero)
        {
            // 起跑时刻是 n × delay，按计时器上限收敛。
            var wait = TimeSpan.FromTicks(Math.Min(delay.Ticks * index, Timeouts.Max.Ticks));

            try
            {
                await Task.Delay(wait, branchToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                if (ctx.ExternalToken.IsCancellationRequested)
                {
                    // 调用方在等待期间取消：按统一错误模型报「已取消」，而不是漏出裸 TaskCanceledException。
                    throw AxiosException.Canceled(ctx.Config, template, "请求在对冲分支起跑前被取消", oce);
                }

                // 胜出者已经出现：这条分支不必起跑。
                throw;
            }
        }

        var request = index == 0 ? template : CloneRequest(template);
        try
        {
            // 每条分支用自己的令牌：胜出后取消败者时，只有败者的在途发送被中止。
            // 配置与响应对象也各用各的：终端会把本次响应的上下文写进 config.Response、把结果填进预造对象，
            // 共享同一份时两条分支会互相覆盖（自定义序列化器按契约从 config.Response 读响应头与字符集）。
            var branchContext = ctx
                .WithoutTarget()
                .WithConfig(ctx.Config.CloneForAttempt())
                .WithAttemptToken(branchToken);
            return await next(request, branchContext).ConfigureAwait(false);
        }
        finally
        {
            if (!ReferenceEquals(request, template))
            {
                // 正文是共享的（首发还可能在用）：先摘下来，只释放这条请求本身。
                request.Content = null;
                request.Dispose();
            }
        }
    }
}
