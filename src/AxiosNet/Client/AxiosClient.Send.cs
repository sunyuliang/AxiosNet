using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using AxiosNet.Pipeline;
using AxiosNet.Pooling;
using AxiosNet.Telemetry;

namespace AxiosNet;

// AxiosClient 的尝试终端：超时预算、连接租约、发送与响应读取。
public sealed partial class AxiosClient
{
    /// <summary>
    /// 一次尝试的时间预算：超时令牌源 + 流式响应的剩余预算计时器；每次重试各有独立预算，
    /// 设了总预算时取 <c>min(Timeout, 剩余总预算)</c>。
    /// </summary>
    private readonly struct AttemptBudget : IDisposable
    {
        private readonly CancellationTokenSource? _timeoutCts;
        private readonly CancellationToken _externalToken;
        private readonly Stopwatch? _clock;
        private readonly TimeSpan _timeout;
        private readonly bool _clampedByTotalTimeout;

        private AttemptBudget(
            CancellationTokenSource? timeoutCts,
            CancellationToken externalToken,
            Stopwatch? clock,
            TimeSpan timeout,
            bool clampedByTotalTimeout)
        {
            _timeoutCts = timeoutCts;
            _externalToken = externalToken;
            _clock = clock;
            _timeout = timeout;
            _clampedByTotalTimeout = clampedByTotalTimeout;
            Token = timeoutCts?.Token ?? externalToken;
        }

        /// <summary>交给底层每次 I/O 的令牌：无超时时就是调用方令牌（None 时不注册、不分配）。</summary>
        public CancellationToken Token { get; }

        /// <summary>流式响应的剩余预算：只有「流式 + 有超时」才有值。</summary>
        public TimeSpan? StreamRemaining => _clock is null ? null : _timeout - _clock.Elapsed;

        /// <summary>被总预算压小后的真实上限（未压小为 <c>null</c>）：流式正文阶段报超时时用它。</summary>
        public TimeSpan? EffectiveTimeout => _clampedByTotalTimeout ? _timeout : null;

        /// <summary>取消类异常的归属：调用方取消 → <c>ERR_CANCELED</c>，本次尝试超时 → <c>ECONNABORTED</c>，其余 OCE 归一为 <c>ERR_CANCELED</c>。</summary>
        public AxiosException Translate(
            OperationCanceledException exception,
            AxiosConfig config,
            HttpRequestMessage request)
        {
            var kind = CancellationClassifier.Classify(_externalToken, _timeoutCts?.Token, CancellationToken.None);
            return kind == CancellationKind.Timeout
                ? AxiosException.Timeout(config, request, exception, EffectiveTimeout)
                : AxiosException.Canceled(config, request, exception);
        }

        /// <summary>释放这次尝试用的定时器（取消源）；外部令牌不归本对象管。</summary>
        public void Dispose() => _timeoutCts?.Dispose();

        /// <summary>按生效配置建预算：总预算已耗尽则不发本次尝试，否则取 <c>min(Timeout, 剩余)</c>。</summary>
        /// <exception cref="AxiosException">总预算已耗尽（<c>ECONNABORTED</c>）。</exception>
        public static AttemptBudget Start(in AxiosAttemptContext ctx, HttpRequestMessage request)
        {
            var config = ctx.Config;
            var timeout = config.Timeout;
            var clampedByTotalTimeout = false;

            if (ctx.RemainingTotalTimeout is { } remaining)
            {
                if (TotalTimeoutBudget.IsExhausted(remaining))
                {
                    throw AxiosException.TotalTimeoutExceeded(config, request, ctx.AttemptNumber, config.TotalTimeoutValue);
                }

                if (timeout <= TimeSpan.Zero || remaining < timeout)
                {
                    timeout = remaining;
                    clampedByTotalTimeout = true;
                }
            }

            var hasTimeout = timeout > TimeSpan.Zero;

            var timeoutCts = hasTimeout
                ? (ctx.AttemptToken.CanBeCanceled
                    ? CancellationTokenSource.CreateLinkedTokenSource(ctx.AttemptToken)
                    : new CancellationTokenSource())
                : null;

            if (timeoutCts is not null)
            {
                // CancelAfter 上限约 49.7 天，收敛到上限。
                timeoutCts.CancelAfter(Timeouts.Clamp(timeout));
            }

            // 流式 + 有超时才用得上计时器（口径见 StreamRemaining）。
            var clock = ctx.StreamResponse && hasTimeout ? Stopwatch.StartNew() : null;

            return new AttemptBudget(timeoutCts, ctx.AttemptToken, clock, timeout, clampedByTotalTimeout);
        }
    }

        /// <summary>一次尝试的终端：建预算 → 取连接 → 发送 → 读取 → 转换；失败路径归还槽位与连接租约，流式成功时随正文转交。</summary>
        /// <remarks>并发槽位由闸门中间件占好，排队时间不计入 <see cref="AxiosConfig.Timeout"/>。</remarks>
    // netstandard2.0 编译器不接受方法级 AsyncMethodBuilder（CS0592）：兼容资产上摘掉该特性。
#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private async ValueTask<AxiosAttemptResult> SendTerminalAsync(
        HttpRequestMessage request,
        AxiosAttemptContext ctx)
    {
        var config = ctx.Config;

        PoolLease? lease = null;

        // 流式正文的所有权随后随返回值交给调用方（拦截器抛异常时由调用方兜底释放）。
        IDisposable? streamOwner = null;

        // 租约由闸门 / 前置中间件登记在上下文里，失败路径由这里归还。
        IDisposable? leases = ctx.Lease;

        // 发送前钩子的专用副本，改写只作用于本次发送。
        HttpRequestMessage? hookedRequest = null;

        try
        {
            // 阶段 2：时间预算 —— 一次尝试一份超时令牌源；总预算已耗尽时这里直接抛出。
            using var budget = AttemptBudget.Start(ctx, request);

            // 阶段 3 前：发送前钩子，每次尝试一次，失败时不占连接。
            if (config.ObservabilityOrNull?.OnSendingRequest is { } onSendingRequest)
            {
                // 钩子的改写落在独占副本上，重试各自签名不会互相污染。
                var templateContent = request.Content;
                hookedRequest = Attempt.CloneRequest(request);
                request = hookedRequest;

                // 正文对象共享，用头隔离外壳把钩子对内容头的改写隔开。
                HttpContent? wrappedContent = null;
                if (templateContent is not null)
                {
                    wrappedContent = new HeaderIsolatedContent(templateContent);
                    request.Content = wrappedContent;
                }

                try
                {
                    await onSendingRequest(request, config, budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    throw budget.Translate(ex, config, request);
                }
                catch (Exception ex) when (ex is not AxiosException)
                {
                    throw AxiosException.CallbackFailed(
                        config, request, nameof(AxiosConfig.ObservabilitySettings.OnSendingRequest), ex, upload: true);
                }

                // 钩子换掉了正文：外壳已无人引用，单独释放。
                var contentReplaced = !ReferenceEquals(request.Content, wrappedContent);
                if (contentReplaced)
                {
                    wrappedContent?.Dispose();
                }

                // 钩子换了正文时，对替换后的已知长度再判一次上限。
                if (contentReplaced
                    && config.LimitsOrNull?.MaxRequestBytes is long maxBody
                    && request.Content?.Headers.ContentLength is long replacementLength
                    && replacementLength > maxBody)
                {
                    throw AxiosException.MaxRequestBytes(config, request, replacementLength, maxBody);
                }
            }

            // 阶段 3：取连接。
            var (httpClient, leased) = await LeaseClientAsync(request, ctx, budget).ConfigureAwait(false);
            lease = leased;

            // 阶段 4：发送 —— 只到响应头，正文留给读取阶段。
            var responseMessage = await SendRequestAsync(httpClient, request, ctx, budget).ConfigureAwait(false);

            // 条件请求的 304：正文在缓存里，用 304 的头更新那条再装配响应。
            if (ctx.Config.RevalidationEntry is not null
                && responseMessage.StatusCode == HttpStatusCode.NotModified)
            {
                try
                {
                    return CompleteNotModified(responseMessage, request, ctx, _maxRedirects);
                }
                finally
                {
                    responseMessage.Dispose();
                }
            }

            // 阶段 5：读取 —— 状态码接受性只判一次，它同时决定宽松解析与是否抛错。
            var (response, statusAccepted) = await ReadResponseAsync(responseMessage, request, ctx, budget).ConfigureAwait(false);

            // 阶段 6：转换 —— 状态码 → 异常；流式正文 → 计时流 + 资源所有权转移。
            if (!ctx.StreamResponse)
            {
                responseMessage.Dispose();
            }

            if (!statusAccepted)
            {
                if (ctx.StreamResponse)
                {
                    // 失败响应没人读正文：先抢救一段错误体，再释放内容把连接还回池。
                    response.RawText = await ReadStreamErrorBodyAsync(response.Data as Stream, config, budget.Token).ConfigureAwait(false);

                    (response.Data as IDisposable)?.Dispose();
                    response.Data = null;
                    responseMessage.Dispose();
                }

                throw AxiosException.FromResponse(config, request, response, _maxRedirects, ctx.AttemptNumber);
            }

            if (ctx.StreamResponse && response.Data is Stream body)
            {
                // 流式正文与响应头共用同一份预算；不设 Timeout / TotalTimeout 时为 null = 不限时。
                // effectiveTimeout 只在被预算压小时带上。
                Stream wrapped = new TimeoutStream(
                    body, budget.StreamRemaining, ctx.ExternalToken, config, request, budget.EffectiveTimeout);

                // 租约与消息一并转移给流，流 Dispose 时统一释放。
                wrapped = new ResponseStreamOwner(wrapped, responseMessage, lease, leases);
                lease = null;
                leases = null;

                response.Data = wrapped;
                streamOwner = wrapped;
            }

            // 流式成功：租约已随正文交给 StreamOwner，告知闸门不要再归还。
            return new AxiosAttemptResult(response, streamOwner, leasesTransferred: streamOwner is not null);
        }
        catch (ObjectDisposedException ex)
        {
            // 池在请求在途时被释放：翻译成「请求被中止」。
            throw AxiosException.Canceled(config, request, "客户端或其连接池在请求在途时被释放，请求被中止", ex);
        }
        finally
        {
            lease?.Dispose();

            // 租约在所有失败路径上都归还；流式成功路径已转交（leases = null）。
            leases?.Dispose();

            // 副本只释放自己，正文仍归模板。
            hookedRequest?.Dispose();
        }
    }

    /// <summary>阶段 3 —— 取连接：注入 Handler 时固定用它的 <see cref="HttpClient"/>，否则按 origin 租一条。</summary>
    private async ValueTask<(HttpClient Client, PoolLease? Lease)> LeaseClientAsync(
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        AttemptBudget budget)
    {
        // 注入 Handler：固定 HttpClient，没有租约要归还。
        if (_customClient is { } custom)
        {
            if (!string.IsNullOrEmpty(ctx.Config.ProfileOrNull))
            {
                throw AxiosException.InvalidOption(
                    $"注入自定义 Handler 时不走连接池，Transport.Profile（'{ctx.Config.ProfileOrNull}'）无法生效："
                    + "需要多出口请改用 ConnectionPoolOptions.AddTransport 配合池，或在 Handler 自己里选路。",
                    ctx.Config);
            }

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

        AxiosTelemetry.Dispatch.TagPoolReuse(lease.EndpointReused);
        return (lease.Client, lease);
    }

    /// <summary>阶段 4 —— 发送：只到响应头（<see cref="HttpCompletionOption.ResponseHeadersRead"/>），正文留给读取阶段；取消 / 超时、网络与库异常都在这里翻译成统一错误模型。</summary>
    /// <remarks>
    /// 返回的 <see cref="ValueTask{TResult}"/> 必须且只被 await 一次：唯一调用点是 <see cref="SendTerminalAsync"/>，拿到后立即等待。
    /// </remarks>
    // netstandard2.0 编译器不接受方法级 AsyncMethodBuilder（CS0592）：兼容资产上摘掉该特性。
#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<HttpResponseMessage> SendRequestAsync(
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
            // 正文序列化 / 上传回调抛出的库异常被包成 HttpRequestException：原样还原。
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
            // 自定义 Handler / Content 不支持某种能力时不要漏裸异常。
            throw AxiosException.Network(ctx.Config, request, ex);
        }
    }

    /// <summary>阶段 5 —— 读取：先判定状态码接受性，再把正文读成 <see cref="AxiosResponse"/>；接受性只判一次，不合法时按宽松口径解析，任何失败路径都先释放响应消息。</summary>
    /// <remarks>
    /// 返回的 <see cref="ValueTask{TResult}"/> 必须且只被 await 一次：唯一调用点是 <see cref="SendTerminalAsync"/>，拿到后立即解构等待。
    /// </remarks>
    // netstandard2.0 编译器不接受方法级 AsyncMethodBuilder（CS0592）：兼容资产上摘掉该特性。
#if !NETSTANDARD2_0
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
#endif
    private static async ValueTask<(AxiosResponse Response, bool StatusAccepted)> ReadResponseAsync(
        HttpResponseMessage responseMessage,
        HttpRequestMessage request,
        AxiosAttemptContext ctx,
        AttemptBudget budget)
    {
        try
        {
            var validateStatus = ctx.Config.ValidateStatus ?? DefaultValidateStatus;
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
            // 读取/解析失败：先释放响应。
            responseMessage.Dispose();
            throw;
        }
    }

    /// <summary>默认的成功判定：2xx 才算成功（未配置 <c>ValidateStatus</c> 时用它）。</summary>
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

    /// <summary>把流式失败响应的正文读成文本（上限取 <see cref="StreamErrorBodyLimit"/> 与 <c>Limits.MaxResponseBytes</c> 的较小值）；读取失败 / 超时 / 超限都只保留已读部分。</summary>
    private static async Task<string?> ReadStreamErrorBodyAsync(
        Stream? body,
        AxiosConfig config,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return null;
        }

        var limit = config.LimitsOrNull?.MaxResponseBytes is long max && max < StreamErrorBodyLimit
            ? (int)max
            : StreamErrorBodyLimit;
        if (limit <= 0)
        {
            return null;
        }

        // 缓冲走 ArrayPool，读取上限仍按 limit。
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
                // 错误体读不完不影响「这是一次失败响应」。
            }

            // 解码必须在归还缓冲之前完成。
            return total == 0
                ? null
                : ResponseTextDecoder.Decode(buffer.AsSpan(0, total), config.Response?.CharSet, config.SerializationOrNull?.TextEncoding);
        }
        finally
        {
            // 错误体可能含敏感信息：清空后再归还。
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }
}
