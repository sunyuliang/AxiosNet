#if NETSTANDARD2_0
namespace AxiosNet;

/// <summary>版本协商策略（netstandard2.0 兼容资产上的等价定义）。</summary>
/// <remarks>
/// 保留同名枚举是为了配置形状在三个资产上一致。兼容资产的 <c>HttpClientHandler</c> 只能发 HTTP/1.1，
/// 显式要求 HTTP/2 / HTTP/3 会在发出前被拒绝，不静默降级。
/// </remarks>
public enum HttpVersionPolicy
{
    /// <summary>可以降级到更低版本（库默认值）。</summary>
    RequestVersionOrLower = 0,

    /// <summary>只用请求指定的版本，协商不上就失败。</summary>
    RequestVersionExact = 1,

    /// <summary>可以升级到更高版本。</summary>
    RequestVersionOrHigher = 2
}
#endif
