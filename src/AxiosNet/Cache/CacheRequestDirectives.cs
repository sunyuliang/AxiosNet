namespace AxiosNet.Cache;

/// <summary>
/// 一次请求里与缓存相关的指令（<c>Cache-Control</c> / <c>Pragma</c>），只有非
/// <see cref="CacheFreshnessMode.Configured"/> 模式才读取：<c>Configured</c> 是「强制按配置执行」。
/// </summary>
internal readonly struct CacheRequestDirectives
{
    /// <summary>什么都没声明：不影响查与写。</summary>
    internal static readonly CacheRequestDirectives None = default;

    internal CacheRequestDirectives(bool noStore, bool noCache, TimeSpan? maxAge)
    {
        NoStore = noStore;
        NoCache = noCache;
        MaxAge = maxAge;
    }

    /// <summary><c>no-store</c>：本次请求不许读缓存，也不许落库。</summary>
    internal bool NoStore { get; }

    /// <summary><c>no-cache</c>（或旧式 <c>Pragma: no-cache</c>）：不许直接复用已落库的条目；库没有条件请求，故按「跳过查询」处理，响应仍可落库。</summary>
    internal bool NoCache { get; }

    /// <summary>
    /// <c>max-age</c>：调用方愿意接受的最大新鲜期。<c>0</c>（含非法或负数，RFC 9111 §4.2.1）等价于「必须重新取」；
    /// 正数作为落库 TTL 的上限参与取交集。
    /// </summary>
    internal TimeSpan? MaxAge { get; }

    /// <summary>是否跳过查缓存：<c>no-store</c> / <c>no-cache</c> / <c>max-age=0</c>。</summary>
    internal bool SkipLookup => NoStore || NoCache || MaxAge == TimeSpan.Zero;

    /// <summary>响应是否允许落库：<c>no-store</c> 与 <c>max-age=0</c> 拒绝。</summary>
    internal bool CanStore => !NoStore && MaxAge != TimeSpan.Zero;
}
