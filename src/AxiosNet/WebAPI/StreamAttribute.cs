namespace AxiosNet.WebAPI;

/// <summary>
/// 接口级流式响应的帧格式声明：只用于返回 <c>Task&lt;AxiosResponse&lt;IAsyncEnumerable&lt;T&gt;&gt;&gt;</c> 的方法。
/// 省略时按 <see cref="AxiosStreamFormat.NewlineDelimitedJson"/>（NDJSON）读取；等价于类模型的 <c>RequestStreamAsync(..., format, ...)</c> 参数。
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false, AllowMultiple = false)]
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
