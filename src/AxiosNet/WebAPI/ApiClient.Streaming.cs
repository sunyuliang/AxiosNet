using System.Diagnostics.CodeAnalysis;

namespace AxiosNet.WebAPI;

// 流式响应：把响应流按帧解析成 IAsyncEnumerable<T>，状态码 / 响应头按 AxiosResponse 原样保留。
public sealed partial class ApiClient
{
    /// <summary>
    /// 读取流式响应：<c>Data</c> 是逐帧反序列化的 <see cref="IAsyncEnumerable{T}"/>；帧格式取请求模型类 / 属性上的 <c>[Stream]</c> 声明，未声明时按 NDJSON。
    /// </summary>
    /// <typeparam name="TItem">每个帧（行）反序列化后的类型。</typeparam>
    public Task<AxiosResponse<IAsyncEnumerable<TItem>>> RequestStreamAsync<TItem>(
        IApiRequest<TItem> request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = AcquireMetadata(request.GetType());
        var format = metadata.HasStreamAttribute
            ? metadata.StreamFormat
            : AxiosStreamFormat.NewlineDelimitedJson;

        return RequestStreamAsync(request, format, configure: null, cancellationToken);
    }

    /// <summary>以指定帧格式（NDJSON / SSE / JSON 数组）逐帧读取流式响应。</summary>
    /// <remarks>
    /// 三种格式共用同一层字节读取与单帧上限（默认 8 MB）；正文超时与响应头同一份预算，超出抛 <c>ECONNABORTED</c>，不设超时时靠 <paramref name="cancellationToken"/> 退出。
    /// 响应流只能枚举一次；解析失败抛 <c>ERR_BAD_RESPONSE</c>，中途断流抛 <c>ERR_NETWORK</c>，取消抛 <c>ERR_CANCELED</c>。
    /// <c>ResponseType</c> 未设置时自动切 <c>Stream</c>，显式设成非 <c>Stream</c> 抛 <c>ERR_BAD_OPTION_VALUE</c>。
    /// </remarks>
    /// <typeparam name="TItem">每个帧反序列化后的类型。</typeparam>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "请求模型的运行时类型无法静态标注：AOT 下由 AxiosNet 源生成器在编译期写入成员取值委托，注册表命中后不再反射；"
            + "未命中且无动态代码时抛 AxiosAotMetadataException，不会反射模型成员（README「运行时与 AOT」）。")]
    public async Task<AxiosResponse<IAsyncEnumerable<TItem>>> RequestStreamAsync<TItem>(
        IApiRequest<TItem> request,
        AxiosStreamFormat format,
        Action<AxiosConfig>? configure = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = AcquireMetadata(request.GetType());
        var config = BuildConfig(metadata, request, configure, cancellationToken);

        return await SendStreamAsync<TItem>(request, metadata.Authorization, config, cancellationToken, format, request.GetType().Name)
            .ConfigureAwait(false);
    }

    /// <summary>流式发送内核：请求模型与接口级声明（<c>[Stream]</c>）共用这一份实现。</summary>
    internal async Task<AxiosResponse<IAsyncEnumerable<TItem>>> SendStreamAsync<TItem>(
        object request,
        ApiAuthorization authorization,
        AxiosConfig config,
        CancellationToken cancellationToken,
        AxiosStreamFormat format,
        string targetName)
    {
        // 未显式设置时才自动切到 Stream；显式设成别的类型直接报配置错误。
        var declaredResponseType = config.ResponseTypeSet
            ? config.ResponseType
            : Axios.Defaults.ResponseTypeSet
                ? Axios.Defaults.ResponseType
                : (ResponseType?)null;

        if (declaredResponseType is { } declared)
        {
            if (declared != ResponseType.Stream)
            {
                throw AxiosException.InvalidOption(
                    $"流式读取要求 ResponseType = Stream，但本次请求显式设成了 {declared}："
                    + "删掉显式设置（由流式入口自动切到 Stream），或改用非流式 RequestAsync。",
                    config);
            }
        }
        else
        {
            // 正文超时与响应头同一份预算；不设超时时正文不限时、靠取消令牌退出。
            config.SetResponseTypeAuto(ResponseType.Stream);
        }

        var auth = ApplyAuthorization(authorization, request, config);

        var raw = await SendAsync<Stream>(request, config, auth, cancellationToken, targetName).ConfigureAwait(false);
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
            // 构造失败时正文流已在本方法手里，必须兜底释放。
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
