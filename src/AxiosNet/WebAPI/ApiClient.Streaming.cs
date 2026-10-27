using System.Diagnostics.CodeAnalysis;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

// 流式响应：把响应流按帧解析成 IAsyncEnumerable<T>，状态码 / 响应头按 AxiosResponse 原样保留。
public sealed partial class ApiClient
{
    /// <summary>以 NDJSON（行分隔 JSON）读取流式响应：<c>Data</c> 是逐帧反序列化的 <see cref="IAsyncEnumerable{T}"/>。</summary>
    /// <typeparam name="TItem">每个帧（行）反序列化后的类型。</typeparam>
    /// <param name="request">请求模型实例；泛型参数即<b>单个帧</b>的类型。</param>
    /// <param name="cancellationToken">外部取消令牌（贯穿整个枚举过程）。</param>
    public Task<AxiosResponse<IAsyncEnumerable<TItem>>> RequestStreamAsync<TItem>(
        IApiRequest<TItem> request,
        CancellationToken cancellationToken = default)
        => RequestStreamAsync(request, AxiosStreamFormat.NewlineDelimitedJson, configure: null, cancellationToken);

    /// <summary>
    /// 以指定帧格式读取流式响应（NDJSON / SSE / JSON 数组）；三种格式共用同一套字节级分帧、空闲超时与单帧上限（默认 8 MB）。
    /// <see cref="AxiosConfig.Timeout"/> 只覆盖到响应头，正文由 <see cref="StreamIdleTimeout"/> 控制；JSON 数组模式要求响应是 UTF-8。
    /// 响应流只能枚举一次；帧解析失败抛 <c>ERR_BAD_RESPONSE</c>，中途断流抛 <c>ERR_NETWORK</c>，取消抛 <c>ERR_CANCELED</c>。
    /// </summary>
    /// <typeparam name="TItem">每个帧反序列化后的类型。</typeparam>
    /// <param name="request">请求模型实例。</param>
    /// <param name="format">帧格式。</param>
    /// <param name="configure">按请求微调配置（在模型绑定之后、授权之前调用）。</param>
    /// <param name="cancellationToken">外部取消令牌（贯穿整个枚举过程）。</param>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注；AOT 下需由消费方 root 模型程序集（README「Native AOT / 裁剪」）。")]
    public async Task<AxiosResponse<IAsyncEnumerable<TItem>>> RequestStreamAsync<TItem>(
        IApiRequest<TItem> request,
        AxiosStreamFormat format,
        Action<AxiosConfig>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = RequestModelMetadata.Get(request.GetType());
        var config = BuildConfig(metadata, request, configure, cancellationToken);

        return await SendStreamAsync<TItem>(request, metadata.Authorization, config, cancellationToken, format)
            .ConfigureAwait(false);
    }

    /// <summary>流式发送内核：请求模型与接口级声明（<c>[Stream]</c>）共用这一份实现。</summary>
    internal async Task<AxiosResponse<IAsyncEnumerable<TItem>>> SendStreamAsync<TItem>(
        object request,
        ApiAuthorization authorization,
        AxiosConfig config,
        CancellationToken cancellationToken,
        AxiosStreamFormat format)
    {
        // 正文按帧消费：responseType 固定 Stream，正文超时口径交给 StreamIdleTimeout。
        // 优先级：按请求 configure 显式写过的值 > 本门面显式设置过的值 > 底层客户端默认值。
        config.SetResponseTypeAuto(ResponseType.Stream);
        if (!config.StreamIdleTimeoutSet && (_streamIdleTimeoutSet || !Axios.Defaults.StreamIdleTimeoutSet))
        {
            config.Limits.StreamIdleTimeout = _streamIdleTimeout;
        }

        await ApplyAuthorizationAsync(authorization, request, config, cancellationToken).ConfigureAwait(false);

        var raw = await SendAsync<Stream>(request, config, cancellationToken).ConfigureAwait(false);
        var body = raw.Data ?? throw AxiosException.BadResponse(raw.Config ?? config, raw.Request, "Stream", new InvalidOperationException("流式响应没有拿到正文流：响应拦截器把 Data 换成了 null，流式请求不能被拦截器替换成非流对象。"));

        var info = raw.Config?.Response;
        JsonStreamEnumerable<TItem> frames;
        try
        {
            frames = new JsonStreamEnumerable<TItem>(
                body,
                raw.Config ?? config,
                raw.Request,
                format,
                info?.CharSet,
                info?.MediaType);
        }
        catch
        {
            // 构造失败时正文流已在本方法手里，必须兜底释放，否则会漏掉一份连接额度。
            body.Dispose();
            throw;
        }

        return new AxiosResponse<IAsyncEnumerable<TItem>>
        {
            Data = frames,
            Status = raw.Status,
            StatusText = raw.StatusText,
            ProtocolVersion = raw.ProtocolVersion,
            // 流式响应的正文字节数未知（恒为 null），字段照搬一份保持一致。
            BodyBytes = raw.BodyBytes,
            Headers = raw.Headers,
            Config = raw.Config,
            Request = raw.Request
        };
    }
}
