using System.Buffers;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using AxiosNet.Internal;
using AxiosNet.Pipeline;
using AxiosNet.Pooling;
using AxiosNet.Telemetry;

namespace AxiosNet;

// AxiosClient 的尝试终端：超时预算、连接租约、发送与响应读取。
// 它是中间件链的最内层（一切编排都在它外面），并发槽位由闸门中间件占好后经上下文交进来。
public sealed partial class AxiosClient
{
    /// <summary>一次尝试的时间预算：超时令牌源 + 流式响应的剩余预算计时器。粒度是一次尝试，因此每次重试各有独立预算。无超时时 <see cref="Token"/> 直接是调用方令牌，不建 linked CTS。</summary>
    private readonly struct AttemptBudget : IDisposable
    {
        private readonly CancellationTokenSource? _timeoutCts;
        private readonly CancellationToken _externalToken;
        private readonly Stopwatch? _clock;
        private readonly TimeSpan _timeout;

        private AttemptBudget(
            CancellationTokenSource? timeoutCts,
            CancellationToken externalToken,
            Stopwatch? clock,
            TimeSpan timeout)
        {
            _timeoutCts = timeoutCts;
            _externalToken = externalToken;
            _clock = clock;
            _timeout = timeout;
            Token = timeoutCts?.Token ?? externalToken;
        }

        /// <summary>交给底层每次 I/O 的令牌：无超时时就是调用方令牌（None 时不注册、不分配）。</summary>
        public CancellationToken Token { get; }

        /// <summary>流式响应的剩余预算：只有「流式 + 有超时」才有值；timeout 要延续到正文读完，故记录起点算剩余。</summary>
        public TimeSpan? StreamRemaining => _clock is null ? null : _timeout - _clock.Elapsed;

        /// <summary>取消类异常的归属：调用方取消 → <c>ERR_CANCELED</c>，否则本次尝试超时 → <c>ERR_TIMEOUT</c>。</summary>
        public AxiosException Translate(
            OperationCanceledException exception,
            AxiosConfig config,
            HttpRequestMessage request)
        {
            return !_externalToken.IsCancellationRequested && _timeoutCts is { IsCancellationRequested: true }
                ? AxiosException.Timeout(config, request, exception)
                : AxiosException.Canceled(config, request, exception);
        }

        public void Dispose() => _timeoutCts?.Dispose();

        /// <summary>按生效配置建预算：超时上限收敛到计时器可表示的范围（见 <see cref="Timeouts.Clamp"/>）。</summary>
        public static AttemptBudget Start(
            AxiosConfig config,
            CancellationToken externalToken,
            bool streamResponse)
        {
            var timeout = config.Timeout;
            var hasTimeout = timeout > TimeSpan.Zero;

            var timeoutCts = hasTimeout
                ? (externalToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(externalToken)
                    : new CancellationTokenSource())
                : null;

            if (timeoutCts is not null)
            {
                // CancelAfter 上限约 49.7 天，越界会抛裸异常，故收敛到上限。
                timeoutCts.CancelAfter(Timeouts.Clamp(timeout));
            }

            // 流式 + 有超时才用得上计时器（口径见 StreamRemaining）。
            var clock = streamResponse && hasTimeout ? Stopwatch.StartNew() : null;

            return new AttemptBudget(timeoutCts, externalToken, clock, timeout);
        }
    }

    /// <summary>
    /// 一次尝试的终端：建超时预算 → 取连接 → 发送 → 读取 → 转换。
    /// 并发槽位由闸门中间件占好并写进上下文（排队时间不计入 <see cref="AxiosConfig.Timeout"/>），
    /// 这里只负责在所有失败路径归还它；流式成功路径把它随正文转交给调用方。
    /// 连接租约同理：非流式在这里归还，流式随正文走。
    /// </summary>
    private async ValueTask<AxiosAttemptResult> SendTerminalAsync(
        HttpRequestMessage request,
        AxiosAttemptContext ctx)
    {
        var config = ctx.Config;

        PoolLease? lease = null;

        // 流式正文的所有权随后随返回值交给调用方（拦截器抛异常时由调用方兜底释放）。
        IDisposable? streamOwner = null;

        // 槽位由闸门中间件交给本终端：此后任何失败路径都由这里归还（占位后不能漏）。
        IDisposable? slot = ctx.Slot;

        try
        {
            // 阶段 2：时间预算 —— 一次尝试一份超时令牌源；闸门排队在它之前，不计入本预算。
            using var budget = AttemptBudget.Start(config, ctx.AttemptToken, ctx.StreamResponse);

            // 阶段 3：取连接 —— 注入 Handler 时固定用它，默认实例按 origin 租（连接复用 + 并发上限）。
            var (httpClient, leased) = await LeaseClientAsync(request, ctx, budget).ConfigureAwait(false);
            lease = leased;

            // 阶段 4：发送 —— 只到响应头，正文留给读取阶段。
            var responseMessage = await SendRequestAsync(httpClient, request, ctx, budget).ConfigureAwait(false);

            // 阶段 5：读取 —— 状态码接受性只判一次，它同时决定宽松解析与是否抛错。
            var (response, statusAccepted) = await ReadResponseAsync(responseMessage, request, ctx, budget).ConfigureAwait(false);

            // 阶段 6：转换 —— 状态码 → 异常；流式正文 → 计时流 + 资源所有权转移。
            // stream 模式下消息不能释放（正文流从它取出），所有权转移给 ResponseStreamOwner。
            if (!ctx.StreamResponse)
            {
                responseMessage.Dispose();
            }

            if (!statusAccepted)
            {
                if (ctx.StreamResponse)
                {
                    // 失败响应没人读正文：先抢救一段错误体文本，再释放内容把连接还回池。
                    response.RawText = await ReadStreamErrorBodyAsync(response.Data as Stream, config, budget.Token).ConfigureAwait(false);

                    (response.Data as IDisposable)?.Dispose();
                    response.Data = null;
                    responseMessage.Dispose();
                }

                throw AxiosException.FromResponse(config, request, response);
            }

            if (ctx.StreamResponse && response.Data is Stream body)
            {
                // 流式正文超时口径：设了 StreamIdleTimeout 时正文按空闲超时，否则共享剩余预算。
                Stream wrapped = config.LimitsOrNull?.StreamIdleTimeout is { } idleTimeout
                    ? new TimeoutStream(
                        body,
                        remaining: null,
                        ctx.ExternalToken,
                        config,
                        request,
                        idleTimeout > TimeSpan.Zero ? idleTimeout : null)
                    : new TimeoutStream(body, budget.StreamRemaining, ctx.ExternalToken, config, request);

                // 租约、消息与并发槽位一并转移给流：下载期间该 origin 仍算活跃，流 Dispose 时统一释放。
                wrapped = new ResponseStreamOwner(wrapped, responseMessage, lease, slot);
                lease = null;
                slot = null;

                response.Data = wrapped;
                streamOwner = wrapped;
            }

            // 流式成功：槽位已随正文交给 StreamOwner，告知闸门不要再归还（流还活着，该请求仍占并发额度）。
            // 其余情况槽位都在下面的 finally 里归还，闸门的兜底归还对它只是幂等的重复释放。
            return new AxiosAttemptResult(response, streamOwner, slotTransferred: streamOwner is not null);
        }
        catch (ObjectDisposedException ex)
        {
            // 池在请求在途时被释放：翻译成「请求被中止」。
            throw AxiosException.Canceled(config, request, "客户端或其连接池在请求在途时被释放，请求被中止", ex);
        }
        finally
        {
            lease?.Dispose();

            // 槽位在所有失败路径上都归还；流式成功路径已把它转交给正文流（slot = null）。
            slot?.Dispose();
        }
    }

    /// <summary>阶段 3 —— 取连接：注入 Handler 时固定用它的 <see cref="HttpClient"/>，否则按 origin 租一条；是否新建 origin 节点写进 span。</summary>
    private async ValueTask<(HttpClient Client, PoolLease? Lease)> LeaseClientAsync(
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        AttemptBudget budget)
    {
        // 注入 Handler：固定 HttpClient，没有租约要归还。
        if (_customClient is { } custom)
        {
            return (custom, null);
        }

        PoolLease lease;
        try
        {
            lease = await _pool!.LeaseAsync(ctx.Uri, ctx.Config, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw budget.Translate(ex, ctx.Config, request);
        }

        AxiosTelemetry.TagPoolReuse(lease.EndpointReused);
        return (lease.Client, lease);
    }

    /// <summary>阶段 4 —— 发送：只到响应头（<see cref="HttpCompletionOption.ResponseHeadersRead"/>），正文留给读取阶段；取消 / 超时、网络与库异常都在这里翻译成统一错误模型。</summary>
    private static async Task<HttpResponseMessage> SendRequestAsync(
        HttpClient client,
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        AttemptBudget budget)
    {
        try
        {
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex)
        {
            throw budget.Translate(ex, ctx.Config, request);
        }
        catch (HttpRequestException ex) when (FindAxiosException(ex) is { } translated)
        {
            // 正文序列化 / 上传回调抛出的库异常被 HttpClient 包成 HttpRequestException：原样还原，否则会误报 ERR_NETWORK。
            ExceptionDispatchInfo.Capture(translated).Throw();
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw AxiosException.Network(ctx.Config, request, ex);
        }
        catch (IOException ex)
        {
            throw AxiosException.Network(ctx.Config, request, ex);
        }
        catch (NotSupportedException ex)
        {
            // 自定义 Handler / Content 不支持某种能力时不要把裸异常漏给调用方。
            throw AxiosException.Network(ctx.Config, request, ex);
        }
    }

    /// <summary>阶段 5 —— 读取：先判定状态码接受性，再把正文读成 <see cref="AxiosResponse"/>；接受性只判一次，不合法时按宽松口径解析，任何失败路径都先释放响应消息。</summary>
    private static async Task<(AxiosResponse Response, bool StatusAccepted)> ReadResponseAsync(
        HttpResponseMessage responseMessage,
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        AttemptBudget budget)
    {
        try
        {
            var validateStatus = ctx.Config.CallbacksOrNull?.ValidateStatus ?? DefaultValidateStatus;
            var statusAccepted = validateStatus((int)responseMessage.StatusCode);

            var response = await ResponseReader.ReadAsync(
                    responseMessage,
                    request,
                    ctx.Config,
                    budget.Token,
                    ctx.DataType,
                    ctx.Target,
                    lenient: !statusAccepted)
                .ConfigureAwait(false);

            return (response, statusAccepted);
        }
        catch (OperationCanceledException ex)
        {
            responseMessage.Dispose();
            throw budget.Translate(ex, ctx.Config, request);
        }
        catch (IOException ex)
        {
            // 连接中途断开 / 正文被截断（HttpIOException）：统一成 ERR_NETWORK。
            responseMessage.Dispose();
            throw AxiosException.Network(ctx.Config, request, ex);
        }
        catch (HttpRequestException ex)
        {
            // 自定义 Handler 的正文流可能直接抛 HttpRequestException，与 IO 失败同一错误码。
            responseMessage.Dispose();
            throw AxiosException.Network(ctx.Config, request, ex);
        }
        catch
        {
            // 读取/解析失败（含长度超限、反序列化失败）：先释放响应，避免连接泄漏
            responseMessage.Dispose();
            throw;
        }
    }

    private static bool DefaultValidateStatus(int status) => status is >= 200 and < 300;

    /// <summary>沿 <see cref="Exception.InnerException"/> 链找库自己的异常（HttpClient 会把正文序列化异常包成 <see cref="HttpRequestException"/>）。</summary>
    private static AxiosException? FindAxiosException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is AxiosException axios)
            {
                return axios;
            }
        }

        return null;
    }
    /// <summary>流式失败响应保留的错误体上限。</summary>
    private const int StreamErrorBodyLimit = 64 * 1024;

    /// <summary>把流式失败响应的正文读成文本（最多 <see cref="StreamErrorBodyLimit"/> 字节）；读取失败 / 超时 / 超限都只保留已读部分。</summary>
    private static async Task<string?> ReadStreamErrorBodyAsync(
        Stream? body,
        AxiosConfig config,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return null;
        }

        var limit = config.LimitsOrNull?.MaxContentLength is long max && max < StreamErrorBodyLimit
            ? (int)max
            : StreamErrorBodyLimit;
        if (limit <= 0)
        {
            return null;
        }

        // 缓冲走 ArrayPool（Rent 可能更大），读取上限仍按 limit。
        var buffer = ArrayPool<byte>.Shared.Rent(limit);
        var total = 0;
        try
        {
            try
            {
                while (total < limit)
                {
                    var read = await body.ReadAsync(buffer.AsMemory(total, limit - total), cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or AxiosException or OperationCanceledException)
            {
                // 错误体读不完不影响「这是一次失败响应」，保留已读部分
            }

            // 解码必须在 finally 归还缓冲之前完成：归还后该数组可能立刻被别的请求租走并覆写。
            return total == 0
                ? null
                : ResponseTextDecoder.Decode(buffer.AsSpan(0, total), config.Response?.CharSet, config.SerializationOrNull?.ResponseEncoding);
        }
        finally
        {
            // 错误体可能含敏感信息：清空后再归还，避免下一个租用方读到上一请求的正文。
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
