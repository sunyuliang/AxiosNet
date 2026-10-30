namespace AxiosNet.Cache;

/// <summary>缓存时长的取值方式：只信客户端配置、只信服务器，或两者取交集。</summary>
public enum CacheFreshnessMode
{
    /// <summary>
    /// 只信配置：TTL 恒为 <see cref="AxiosCacheOptions.TimeToLive"/>，落库前不解析响应头（默认）。
    /// HTTP 信息仍随 <see cref="AxiosCacheEntryPolicy"/> 交给存储：存储读到这些字段时才解析。
    /// </summary>
    Configured,

    /// <summary>
    /// 只信服务器：用剩余新鲜期作为 TTL，不受 <see cref="AxiosCacheOptions.TimeToLive"/> 上限约束（请求侧的 <c>max-age</c> 仍会封顶）。
    /// <c>no-store</c> / <c>no-cache</c>、已过期或非法的 <c>Expires</c>、以及没有声明任何新鲜期的响应都不落库。
    /// </summary>
    Server,

    /// <summary>
    /// 取交集：服务器否决优先（<c>no-store</c> / <c>no-cache</c> / 已过期不落库），TTL 取
    /// <see cref="AxiosCacheOptions.TimeToLive"/> 与剩余新鲜期的较小值；服务器没给新鲜期时用配置值。
    /// </summary>
    Intersection
}
