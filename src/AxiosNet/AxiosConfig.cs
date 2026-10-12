using System.Net;
using System.Text.Json;
using AxiosNet.Pooling;

namespace AxiosNet;

/// <summary>
/// 请求/实例配置，字段与 axios 的 config 一一对应。
/// </summary>
public sealed class AxiosConfig
{
    /// <summary>对应 axios baseURL。</summary>
    public string? BaseAddress { get; set; }

    /// <summary>对应 axios url（相对或绝对地址）。</summary>
    public string? Url { get; set; }

    /// <summary>对应 axios method，默认 GET。</summary>
    public AxiosMethod Method { get; set; } = AxiosMethod.Get;

    /// <summary>对应 axios headers（大小写不敏感）。</summary>
    public AxiosHeaders Headers { get; set; } = new();

    /// <summary>对应 axios params，会被序列化为 query string，支持对象/字典/数组/嵌套。</summary>
    public object? Params { get; set; }

    /// <summary>
    /// 对应 axios data：对象默认 JSON 序列化，字典默认表单编码，
    /// 也可直接传 string / byte[] / Stream / HttpContent。
    /// </summary>
    public object? Data { get; set; }

    /// <summary>对应 axios timeout，零表示不超时。</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.Zero;

    /// <summary>对应 axios auth（HTTP Basic）。</summary>
    public AxiosAuth? Auth { get; set; }

    /// <summary>对应 axios responseType，默认 Json。</summary>
    public ResponseType ResponseType { get; set; } = ResponseType.Json;

    /// <summary>对应 axios validateStatus，返回 false 时抛出异常；null 表示默认（2xx 合法）。</summary>
    public Func<int, bool>? ValidateStatus { get; set; }

    /// <summary>外部取消令牌。</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>HTTP 版本，默认 1.1。</summary>
    public Version? HttpVersion { get; set; }

    /// <summary>对应 axios withCredentials（使用系统默认凭据/Cookie）。</summary>
    public bool WithCredentials { get; set; }

    /// <summary>代理设置。</summary>
    public IWebProxy? Proxy { get; set; }

    /// <summary>显式凭据。</summary>
    public ICredentials? Credentials { get; set; }

    /// <summary>最大自动重定向次数（在连接池 Handler 上生效），默认 5。</summary>
    public int MaxRedirects { get; set; } = 5;

    /// <summary>JSON 序列化选项（请求体序列化 / 响应体反序列化共用）。</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>
    /// 连接池选项。仅在 AxiosClient 自建连接池时生效；
    /// 也可以在构造 AxiosClient 时直接传入共享的 <see cref="HttpConnectionPool"/>。
    /// </summary>
    public ConnectionPoolOptions? PoolOptions { get; set; }

    /// <summary>深拷贝（Headers 独立，引用类型成员如 Params/Data 按引用共享）。</summary>
    public AxiosConfig Clone()
    {
        return new AxiosConfig
        {
            BaseAddress = BaseAddress,
            Url = Url,
            Method = Method,
            Headers = Headers.Clone(),
            Params = Params,
            Data = Data,
            Timeout = Timeout,
            Auth = Auth,
            ResponseType = ResponseType,
            ValidateStatus = ValidateStatus,
            CancellationToken = CancellationToken,
            HttpVersion = HttpVersion,
            WithCredentials = WithCredentials,
            Proxy = Proxy,
            Credentials = Credentials,
            MaxRedirects = MaxRedirects,
            JsonSerializerOptions = JsonSerializerOptions,
            PoolOptions = PoolOptions
        };
    }
}
