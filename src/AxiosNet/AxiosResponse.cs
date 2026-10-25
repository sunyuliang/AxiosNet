using AxiosNet.Internal;

namespace AxiosNet;

/// <summary>
/// 非泛型响应，对应 axios response 结构：
/// data / status / statusText / headers / config / request。
/// </summary>
public class AxiosResponse
{
    public object? Data { get; set; }

    public int Status { get; set; }

    public string StatusText { get; set; } = string.Empty;

    /// <summary>
    /// 这次请求<b>实际协商到</b>的 HTTP 版本（<c>1.1</c> / <c>2.0</c> / <c>3.0</c>）。
    /// <para>
    /// 与 <see cref="AxiosConfig.ConnectionSettings.HttpVersion"/> 的「请求意图」不同：那个是你要什么，
    /// 这个是线上真正用了什么。默认 2.0 + 自动降级时，只有它能告诉你「这次到底拿到 h2 没有」——
    /// 降级是静默的（服务端只认 1.1 时不报错、不多花钱），没有这个值就无法在日志或测试里确认协议。
    /// </para>
    /// <para>
    /// 取自 <see cref="HttpResponseMessage.Version"/>。自定义 Handler 不设置该属性时是 <c>1.1</c>；
    /// 响应拦截器替换响应对象时，库会把原值搬到新对象上。
    /// </para>
    /// </summary>
    public Version? ProtocolVersion { get; set; }

    private IReadOnlyDictionary<string, IEnumerable<string>>? _headers;

    /// <summary>合并了响应头与内容头的只读字典（大小写不敏感）。</summary>
    /// <remarks>
    /// 延迟创建：管线里这个字典是现成的（直接整体赋值），构造期先建空字典只会在每个响应上白分配一次。
    /// 手动 <c>new AxiosResponse()</c> 时读到的是空字典。
    /// </remarks>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers
    {
        get => _headers ??= new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        set => _headers = value;
    }

    public AxiosConfig? Config { get; set; }

    /// <summary>
    /// 错误响应的正文原文（<b>只有状态码不合法时</b>才有；正常响应、二进制 / 流式响应为 <c>null</c>）。
    /// <para>由库写入，调用方只读：错误体反序列化与日志诊断用它，避免依赖 <see cref="Data"/> 的最终形态。</para>
    /// </summary>
    internal string? RawText { get; set; }

    /// <summary>
    /// 底层 HttpRequestMessage（对应 axios response.request）。
    /// <para>
    /// 请求结束后它已被释放：<c>Method</c> / <c>RequestUri</c> / <c>Headers</c> 等内存态属性可读，
    /// <c>Content</c> 的读取操作会抛 <see cref="ObjectDisposedException"/>。
    /// </para>
    /// </summary>
    public HttpRequestMessage? Request { get; set; }

    /// <summary>便捷获取响应头第一个值。</summary>
    public string? GetHeader(string name)
        => Headers.TryGetValue(name, out var values) ? values.FirstOrDefault() : null;

    /// <summary>
    /// 响应正文字节数（<b>只有非流式响应有值</b>；流式响应读到这里时正文还没下完，宁可不填也不猜
    /// <c>Content-Length</c>）。
    /// <para>
    /// 供遥测把 <c>http.response.body.size</c> 写进 span。不复用 <see cref="Headers"/> 里的
    /// <c>Content-Length</c>：那个值可能被 chunked / 压缩改写，与「实际收了多少字节」不是一回事，
    /// 而展开响应头字典本身还有开销。
    /// </para>
    /// </summary>
    internal long? BodyBytes { get; set; }

    /// <summary>
    /// 把非泛型响应复制成 <typeparamref name="TData"/> 的响应。
    /// <para>
    /// 只在<b>响应拦截器换掉响应对象</b>时走这里（它写不出泛型参数）：
    /// 正常路径由管线就地填充预先造好的泛型对象，不产生第二个响应。
    /// </para>
    /// </summary>
    internal AxiosResponse<TData> AsTyped<TData>()
    {
        return new AxiosResponse<TData>
        {
            Data = DataCast.To<TData>(Data, Config, Request),
            RawText = RawText,
            Status = Status,
            StatusText = StatusText,
            ProtocolVersion = ProtocolVersion,
            BodyBytes = BodyBytes,
            Headers = Headers,
            Config = Config,
            Request = Request
        };
    }
}

/// <summary>
/// 泛型响应，Data 已按 TData 反序列化。
/// </summary>
public sealed class AxiosResponse<TData> : AxiosResponse
{
    /// <summary>
    /// 未装箱的原始 Data（<c>base.Data</c> 本身）。
    /// <para>
    /// 泛型响应由调用方预造、管线就地填充，填充点只知道 <c>object</c>：
    /// 校验与拆箱留给 <see cref="Internal.DataCast.EnsureCompatible{T}"/>，因此需要这个入口写入。
    /// </para>
    /// </summary>
    internal object? RawData
    {
        get => base.Data;
        set => base.Data = value;
    }

    /// <summary>
    /// 已转换成 <typeparamref name="TData"/> 的 Data。
    /// <para>
    /// 转换在请求返回前就完成（管线就地填充，或拦截器换对象时经
    /// <see cref="AxiosResponse.AsTyped{TData}"/> 复制），因此这里只做拆箱；
    /// 也不把类型检查推迟到 getter（getter 抛业务异常会让日志、调试器、序列化一起炸）。
    /// </para>
    /// </summary>
    public new TData? Data
    {
        get => base.Data is null ? default : (TData)base.Data;
        set => base.Data = value;
    }
}
