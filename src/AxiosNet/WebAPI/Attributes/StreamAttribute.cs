namespace AxiosNet.WebAPI.Attributes;

/// <summary>
/// 流式响应的帧格式声明：接口方法上用于返回 <c>Task&lt;AxiosResponse&lt;IAsyncEnumerable&lt;T&gt;&gt;&gt;</c> 的方法；
/// 请求模型类 / 属性上作为该类默认的帧格式。
/// </summary>
/// <remarks>
/// 省略时按 <see cref="AxiosStreamFormat.NewlineDelimitedJson"/>（NDJSON）读取。
/// 类 / 属性两层都显式声明且取值不同时在构建期抛 <c>ERR_BAD_OPTION_VALUE</c>；调用
/// <c>RequestStreamAsync(..., format, ...)</c> 的显式参数优先于声明。
/// </remarks>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class | AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class StreamAttribute : Attribute
{
    /// <summary>按 NDJSON 读取帧（默认值）。</summary>
    public StreamAttribute()
        : this(AxiosStreamFormat.NewlineDelimitedJson)
    {
    }

    /// <summary>按指定格式读取帧。</summary>
    /// <param name="format">帧格式（NDJSON / SSE / JSON 数组）。</param>
    public StreamAttribute(AxiosStreamFormat format)
    {
        Format = format;
    }

    /// <summary>帧格式。</summary>
    public AxiosStreamFormat Format { get; }
}
