using System.Diagnostics.CodeAnalysis;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

// 流式响应：把响应流按帧解析成 IAsyncEnumerable<T>，状态码 / 响应头按 AxiosResponse 原样保留。
public sealed partial class ApiClient
{
    /// <summary>
    /// 以 <b>NDJSON</b>（行分隔 JSON）读取流式响应：<c>Data</c> 是逐帧反序列化的
    /// <see cref="IAsyncEnumerable{T}"/>，状态码 / 响应头 / 配置与普通响应同形。
    /// <para>等价于 <see cref="RequestStreamAsync{TItem}(IApiRequest{TItem}, AxiosStreamFormat, Action{AxiosConfig}?, CancellationToken)"/>
    /// 传 <see cref="AxiosStreamFormat.NewlineDelimitedJson"/>。</para>
    /// </summary>
    /// <typeparam name="TItem">每个帧（行）反序列化后的类型。</typeparam>
    /// <param name="request">请求模型实例；泛型参数即<b>单个帧</b>的类型。</param>
    /// <param name="cancellationToken">外部取消令牌（贯穿整个枚举过程）。</param>
    public Task<AxiosResponse<IAsyncEnumerable<TItem>>> RequestStreamAsync<TItem>(
        IApiRequest<TItem> request,
        CancellationToken cancellationToken = default)
        => RequestStreamAsync(
            request,
            AxiosStreamFormat.NewlineDelimitedJson,
            configure: null,
            cancellationToken);

    /// <summary>
    /// 以指定帧格式读取流式响应（NDJSON / SSE）。
    /// <para>
    /// <b>超时口径</b>：<see cref="AxiosConfig.Timeout"/> 只覆盖到响应头；正文由
    /// <see cref="StreamIdleTimeout"/> 控制（默认不限时，只受取消令牌约束）。
    /// </para>
    /// <para>
    /// <b>所有权</b>：<c>await foreach</c> 结束或 <c>break</c> 时枚举器被释放，连接随之归还；
    /// 手动 <c>MoveNextAsync</c> 的调用方必须自己调用 <c>DisposeAsync</c>，否则该连接会一直被占用。
    /// 响应流只能枚举一次。
    /// </para>
    /// <para>
    /// <b>错误</b>：状态码不合法在 <c>await</c> 这一步抛出（走 <see cref="ErrorFactory"/>）；
    /// 帧解析失败抛 <c>ERR_BAD_RESPONSE</c>，中途断流抛 <c>ERR_NETWORK</c>，取消抛 <c>ERR_CANCELED</c>。
    /// </para>
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

        // 正文按帧消费：responseType 固定 Stream；Timeout 只到响应头，正文口径交给 StreamIdleTimeout。
        // 优先级：按请求 configure 显式写过的值 > 本门面显式设置过的值 > 底层客户端默认值 > 本门面默认值。
        // 「底层客户端配过、本门面没表态」时必须让它生效，不能被本门面的默认 Zero 静默压成「不限时」。
        // 用自动切换而非 setter：这不是调用方意图，回放 response.Config 时不能把 Stream 泄漏给下一次请求。
        // 必须按「显式设置过」判断，不能看值：未设置时读到的也是 null，而显式设成 null
        // 表示「正文与响应头共享 Timeout 预算」，是有效意图（与 AxiosClient 构造期判 MaxRedirectsSet 同理）。
        config.SetResponseTypeAuto(ResponseType.Stream);
        if (!config.StreamIdleTimeoutSet && (_streamIdleTimeoutSet || !Axios.Defaults.StreamIdleTimeoutSet))
        {
            config.Limits.StreamIdleTimeout = _streamIdleTimeout;
        }

        await ApplyAuthorizationAsync(metadata, request, config, cancellationToken).ConfigureAwait(false);

        var raw = await SendAsync<Stream>(request, config, cancellationToken).ConfigureAwait(false);
        var body = raw.Data
            ?? throw new InvalidOperationException(
                "流式响应没有拿到正文流：响应拦截器把 Data 换成了 null，流式请求不能被拦截器替换成非流对象。");

        var info = raw.Config?.Response;
        var frames = new JsonStreamEnumerable<TItem>(
            body,
            raw.Config ?? config,
            raw.Request,
            format,
            info?.CharSet,
            info?.MediaType);

        return new AxiosResponse<IAsyncEnumerable<TItem>>
        {
            Data = frames,
            Status = raw.Status,
            StatusText = raw.StatusText,
            Headers = raw.Headers,
            Config = raw.Config,
            Request = raw.Request
        };
    }
}
