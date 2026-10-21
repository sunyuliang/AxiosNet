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

    private IReadOnlyDictionary<string, IEnumerable<string>>? _headers;

    /// <summary>合并了响应头与内容头的只读字典（大小写不敏感）。</summary>
    /// <remarks>
    /// 延迟创建：请求管线里这个字典是现成的（直接整体赋值），构造期先建一个空字典
    /// 只会在每个响应上白分配一次。手动 <c>new AxiosResponse()</c> 时读到的是空字典。
    /// </remarks>
    public IReadOnlyDictionary<string, IEnumerable<string>> Headers
    {
        get => _headers ??= new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        set => _headers = value;
    }

    public AxiosConfig? Config { get; set; }

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
    /// 把非泛型响应复制成 <typeparamref name="TData"/> 的响应。
    /// <para>
    /// 只有<b>响应拦截器把响应对象换掉</b>时才走这里（它写不出具体的泛型参数，只能给回非泛型对象）：
    /// 正常路径由 <c>AxiosClient.SendTypedAsync</c> 预先造好泛型对象、管线就地填充，不产生第二个响应。
    /// </para>
    /// </summary>
    internal AxiosResponse<TData> AsTyped<TData>()
    {
        return new AxiosResponse<TData>
        {
            Data = DataCast.To<TData>(Data, Config, Request),
            Status = Status,
            StatusText = StatusText,
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
    /// 泛型请求的响应对象由调用方预先造好、在管线里就地填充（见 <c>AxiosClient.SendTypedAsync</c>），
    /// 那时还没法走类型化的 <see cref="Data"/>：填充点只知道 <c>object</c>，校验与拆箱留给
    /// <see cref="Internal.DataCast.EnsureCompatible{T}"/>。少了这个入口就只能把对象强转回基类去写。
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
    /// 转换在请求返回前就完成了（管线就地填充，或拦截器换对象时经
    /// <see cref="AxiosResponse.AsTyped{TData}"/> 复制），因此这里只做拆箱，不再每次读取都跑一遍转换，
    /// 也不把类型不匹配的检查推迟到属性 getter
    /// （getter 抛业务异常会让日志、调试器、序列化统统在这里炸掉）。
    /// </para>
    /// </summary>
    public new TData? Data
    {
        get => base.Data is null ? default : (TData)base.Data;
        set => base.Data = value;
    }
}
