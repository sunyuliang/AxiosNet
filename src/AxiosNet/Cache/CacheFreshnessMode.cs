namespace AxiosNet.Cache;

/// <summary>缓存时长的取值方式：只信客户端配置、只信服务器，或两者取交集。</summary>
public enum CacheFreshnessMode
{
    /// <summary>只信配置：TTL 恒为 <see cref="AxiosCacheOptions.TimeToLive"/>，落库前不解析响应头（默认）。</summary>
    Configured,

    /// <summary>只信服务器：TTL 用剩余新鲜期（请求侧 <c>max-age</c> 仍封顶）；无新鲜期或已过期不落库。</summary>
    Server,

    /// <summary>取交集：服务器否决优先，TTL 取 <see cref="AxiosCacheOptions.TimeToLive"/> 与剩余新鲜期的较小值；服务器没给新鲜期时用配置值。</summary>
    Intersection
}
