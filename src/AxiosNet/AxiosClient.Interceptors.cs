using AxiosNet.Interceptors;
using AxiosNet.Internal;
using AxiosNet.Pipeline;

namespace AxiosNet;

// AxiosClient 的响应拦截器责任链与短路响应的交付。
public sealed partial class AxiosClient
{
    /// <summary>阶段 6 —— 发送：尝试管线（重试 / 熔断 / 闸门 / 终端），外面按注册顺序套上响应拦截器责任链。管线在链内，拦截器只看到最终结果；返回的 <c>StreamOwner</c> 是流式正文的所有权凭证。</summary>
    private ValueTask<AxiosAttemptResult> RunResponseInterceptorsAsync(
        HttpRequestMessage template,
        AxiosAttemptContext ctx)
    {
        var responseInterceptors = Interceptors.Response.Snapshot();

        if (responseInterceptors.Length == 0)
        {
            // 空链：直接交回管线的 ValueTask，无需兜底释放。
            return _attemptPipeline(template, ctx);
        }

        return RunResponseChainAsync(template, ctx, responseInterceptors);
    }

    /// <summary>响应拦截器责任链（只在有拦截器时走到）：按注册顺序包装，最内层是尝试管线；任何一层抛异常都要归还流式正文的租约。</summary>
    private async ValueTask<AxiosAttemptResult> RunResponseChainAsync(
        HttpRequestMessage template,
        AxiosAttemptContext ctx,
        ResponseInterceptor[] responseInterceptors)
    {
        // 流式正文的所有权：拦截器抛异常或丢弃响应时由这里兜底释放。
        IDisposable? streamOwner = null;

        try
        {
            // 责任链的末端：真正发送（重试 / 熔断 / 闸门都在这一层里面）
            Func<Task<AxiosResponse>> invoker = async () =>
            {
                var attempt = AxiosAttemptResult.Ensure(
                    await _attemptPipeline(template, ctx).ConfigureAwait(false),
                    ctx.Config);

                streamOwner = attempt.StreamOwner;
                return attempt.Response;
            };

            var chain = WrapWithInterceptors(invoker, responseInterceptors, ctx.Config);
            return new AxiosAttemptResult(await chain().ConfigureAwait(false), streamOwner);
        }
        catch
        {
            try
            {
                streamOwner?.Dispose();
            }
            catch
            {
                // 兜底释放失败不能掩盖拦截器的原始异常
            }

            throw;
        }
    }

    /// <summary>把拦截器按注册顺序套在末端调用外面，最内层是「真正产生响应的那一步」；网络路径与短路路径共用。返回 <c>null</c> 就地报错。</summary>
    private static Func<Task<AxiosResponse>> WrapWithInterceptors(
        Func<Task<AxiosResponse>> invoker,
        ResponseInterceptor[] responseInterceptors,
        AxiosConfig config)
    {
        var chain = invoker;

        foreach (var interceptor in responseInterceptors)
        {
            var previous = chain;
            var fulfilled = interceptor.OnFulfilled;
            var rejected = interceptor.OnRejected;
            chain = async () =>
            {
                AxiosResponse response;
                try
                {
                    response = await previous().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (rejected is null)
                    {
                        throw;
                    }

                    // 返回 null 会让调用方随后炸 NRE，明确报错。
                    return (await rejected(ex).ConfigureAwait(false))
                        ?? throw NullInterceptorResult(config);
                }

                return (await fulfilled(response).ConfigureAwait(false))
                    ?? throw NullInterceptorResult(config);
            };
        }

        return chain;
    }

    /// <summary>阶段 4c —— 短路：交付调用方给的响应，本次请求不发送。反序列化、transformResponse、validateStatus 与响应拦截器照常执行，只跳过并发闸门、编码、租约、发送、重试、熔断与遥测。</summary>
    private async Task<AxiosResponse> CompleteShortCircuitAsync(
        AxiosShortCircuit reply,
        AxiosConfig config,
        Type? dataType,
        AxiosResponse? target)
    {
        // 没有 I/O 也尊重取消意图。
        if (config.CancellationToken.IsCancellationRequested)
        {
            throw AxiosException.Canceled(config, null);
        }

        var validateStatus = config.CallbacksOrNull?.ValidateStatus ?? DefaultValidateStatus;
        var statusAccepted = validateStatus(reply.Status);

        // 状态码不合法时按宽松口径解析，让 404 + HTML 也能带着 Status/Response 抛错。
        var response = ResponseReader.ReadShortCircuit(reply, config, dataType, target, lenient: !statusAccepted);

        // 末端：不合法状态码在这里抛，放在链内才能被 OnRejected 接住。
        Task<AxiosResponse> Terminal()
        {
            if (!statusAccepted)
            {
                throw AxiosException.FromResponse(config, null, response);
            }

            return Task.FromResult(response);
        }

        var responseInterceptors = Interceptors.Response.Snapshot();
        if (responseInterceptors.Length == 0)
        {
            // 空链：直接交付。
            return await Terminal().ConfigureAwait(false);
        }

        var chain = WrapWithInterceptors(Terminal, responseInterceptors, config);
        return await chain().ConfigureAwait(false);
    }

    /// <summary>响应拦截器返回 <c>null</c>：返回类型非空，就地报错。</summary>
    private static AxiosException NullInterceptorResult(AxiosConfig config)
        => AxiosException.InvalidOption(
            "响应拦截器返回了 null：OnFulfilled / OnRejected 必须返回一个 AxiosResponse 实例"
            + "（要中断这次请求请抛异常，不要返回 null）。",
            config);
}
