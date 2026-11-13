#if NETSTANDARD2_0
namespace AxiosNet;

/// <summary>
/// 版本协商策略（仅 netstandard2.0 兼容资产使用本库内的等价定义）。
/// </summary>
/// <remarks>
/// netstandard2.0 契约里没有 <c>System.Net.Http.HttpVersionPolicy</c>，也没有
/// <c>HttpRequestMessage.VersionPolicy</c>。保留同名公共枚举是为了让配置形状在三个资产上一致，
/// 但兼容资产的 <c>HttpClientHandler</c> 只能发 HTTP/1.1：本枚举只参与「请求版本是否可能满足」的前置校验，
/// 不会写进 <c>HttpRequestMessage</c>。显式要求 HTTP/2、HTTP/3 会在发出前被明确拒绝，不静默降级。
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
