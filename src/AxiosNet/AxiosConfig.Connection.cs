using AxiosNet.Pooling;

namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 连接相关设置（<see cref="AxiosConfig.Connection"/>）。
    /// </summary>
    public sealed class ConnectionSettings
    {
        /// <summary>
        /// 代理设置。仅实例级生效，按请求设置会抛 <see cref="AxiosException"/>；
        /// 注入自定义 Handler 时由 Handler 决定。
        /// </summary>
        public IWebProxy? Proxy { get; set; }

        /// <summary>
        /// 显式凭据。仅实例级生效，按请求设置会抛 <see cref="AxiosException"/>；
        /// 注入自定义 Handler 时由 Handler 决定。
        /// </summary>
        public ICredentials? Credentials { get; set; }

        /// <summary>
        /// 连接池选项。仅在构造 AxiosClient（自建池）时生效；
        /// 单次请求上设置会抛 <see cref="AxiosException"/>（池此时已建好）。
        /// </summary>
        public ConnectionPoolOptions? PoolOptions { get; set; }

        /// <summary>
        /// HTTP 版本。默认未设置（<c>null</c>）= HTTP/2（<see cref="System.Net.HttpVersion.Version20"/>）。
        /// <para>
        /// <b>默认 2.0 是安全的</b>：协商策略默认是 <c>RequestVersionOrLower</c>（见 <see cref="VersionPolicy"/>），
        /// TLS 上通过 ALPN 请求 h2，服务端只认 1.1 就在同一次握手里谈成 1.1，<b>不多一次往返、不多一条连接</b>，
        /// 调用方感知不到差别（只是拿不到多路复用的收益）。明文 HTTP（http://）没有 ALPN 可协商，
        /// <c>OrLower</c> 一律用 1.1，不会尝试 h2c 先验知识 —— 所以本项默认值只影响 https。
        /// </para>
        /// <para>
        /// <b>退回 HTTP/1.1</b>：某些老网关 / 中间设备、NTLM / Negotiate 这类连接级认证（h2 不支持）、
        /// 或者必须显式使用 <c>Connection</c> / <c>Keep-Alive</c> / <c>Transfer-Encoding</c> /
        /// <c>Upgrade</c> 这类 HTTP/2 禁止的连接级头时，显式设
        /// <c>HttpVersion = HttpVersion.Version11</c>（可只对单次请求设置）。
        /// </para>
        /// <para>
        /// <b>HTTP/3</b>：显式设 <c>HttpVersion.Version30</c> 即要求 h3。h3 只能跑在 QUIC（TLS 1.3 over UDP）上，
        /// 所以只有 https 目标有意义，且要求运行时 / 系统组件齐备 —— 本机行不行查 <see cref="Http3Support"/>。
        /// 与默认策略 <c>OrLower</c> 搭配时，环境或服务端不支持就<b>静默降级</b>到 h2 / 1.1（真实版本看
        /// <see cref="AxiosResponse.ProtocolVersion"/>）；与 <c>Exact</c> 搭配时是「必须 h3」，环境上不可能做到
        /// （明文目标、运行时没有 QUIC）会在发请求前抛 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）。
        /// 默认值仍是 2.0：h3 是显式选择，不会因为「本机支持」就自动升级。
        /// </para>
        /// <para>按请求项：实例 defaults 与单次请求都可设置，单次请求覆盖实例默认值。</para>
        /// </summary>
        public Version? HttpVersion { get; set; }

        /// <summary>
        /// 版本协商策略（<see cref="System.Net.Http.HttpVersionPolicy"/>），默认
        /// <c>RequestVersionOrLower</c>：按 <see cref="HttpVersion"/> 发起，服务端不支持就自动降级。
        /// <para>
        /// 默认版本是 2.0，本项就是「服务端不支持 h2 时怎么办」的开关 —— 保持默认即自动降级（推荐）；
        /// 设为 <c>RequestVersionExact</c> 则是「只用该版本，协商不上就报错」，用于必须确认对端说 h2 的场景。
        /// 想在明文 HTTP 上跑 HTTP/2（h2c 先验知识）也必须用 <c>RequestVersionExact</c>，
        /// <c>RequestVersionOrLower</c> 在明文连接上一律用 1.1。
        /// </para>
        /// <para>
        /// 配合 <c>HttpVersion = Version30</c> 时，本项就是 h3 的严格度开关：<c>OrLower</c>（默认）=
        /// 「有 h3 就用 h3、没有就降级」，<c>Exact</c> = 「必须 h3」—— 环境上不可能做到（明文目标 /
        /// 运行时没有 QUIC）在发请求前就报配置错误，环境可以但服务端不支持才报 <c>ERR_NETWORK</c>。
        /// </para>
        /// <para>按请求项：实例 defaults 与单次请求都可设置，单次请求覆盖实例默认值。</para>
        /// </summary>
        public HttpVersionPolicy? VersionPolicy { get; set; }

        private AxiosAuth? _auth;

        /// <summary>对应 axios auth（HTTP Basic）。</summary>
        /// <remarks>
        /// 手写属性是为了记住「显式赋过值」：<c>Auth = null</c> 的语义是「本次请求不带认证」，
        /// 必须在合并时压住实例默认的凭据 —— 用 <c>perCall.Auth ?? merged.Auth</c> 会让
        /// 实例凭据静默发到本应匿名的端点。
        /// </remarks>
        public AxiosAuth? Auth
        {
            get => _auth;
            set
            {
                _auth = value;
                AuthSet = true;
            }
        }

        /// <summary>是否被显式赋值过（含赋 <c>null</c>），用于合并配置时区分「未设置」与「显式清除」。</summary>
        internal bool AuthSet { get; set; }

        private bool _withCredentials;

        /// <summary>
        /// 对应 axios withCredentials（使用系统默认凭据 / Cookie）。
        /// 仅实例级生效：构造 AxiosClient 时应用到连接池；按请求写<b>不同的值</b>抛异常（相同的值放行）。
        /// 注入自定义 Handler 时由 Handler 自己处理。
        /// <para>
        /// 判「相同」比的是<b>连接池的生效值</b>（<see cref="ConnectionPoolOptions.UseCookies"/>），不是
        /// 本属性：构造后再改本属性不会影响已建成的池，若按本属性判定，会把「复述池真实值」的请求误拒。
        /// </para>
        /// </summary>
        public bool WithCredentials
        {
            get => _withCredentials;
            set
            {
                _withCredentials = value;
                WithCredentialsSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于拒绝「按请求设置实例级选项」。</summary>
        internal bool WithCredentialsSet { get; set; }

        private int _maxRedirects = 5;

        /// <summary>
        /// 对应 axios maxRedirects，默认 5；<b>0 表示不跟随重定向</b>（返回 3xx 响应本身，默认 validateStatus 视为错误）。
        /// 仅实例级生效：按请求写<b>不同的值</b>抛异常（相同的值放行）；注入自定义 Handler 时由 Handler 决定。
        /// </summary>
        public int MaxRedirects
        {
            get => _maxRedirects;
            set
            {
                _maxRedirects = value;
                MaxRedirectsSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于拒绝「按请求设置实例级选项」。</summary>
        internal bool MaxRedirectsSet { get; set; }

        /// <summary>
        /// 把 <paramref name="incoming"/> 里的「按请求」连接项覆盖到本实例（合并层内部使用）。
        /// <para>
        /// 只由合并层在「来源不是库产出的配置」时调用：<see cref="HttpVersion"/> / <see cref="VersionPolicy"/>
        /// 属按请求项（库产出的配置同样要带过来），由合并层单独处理，不在这里覆盖。
        /// Auth / WithCredentials / MaxRedirects 的「显式赋值过」标记是语义的一部分
        /// （显式 null 表示本次不带认证），因此保留判断。
        /// </para>
        /// </summary>
        internal void MergeFrom(ConnectionSettings incoming)
        {
            Proxy = incoming.Proxy ?? Proxy;
            Credentials = incoming.Credentials ?? Credentials;
            PoolOptions = incoming.PoolOptions ?? PoolOptions;

            if (incoming.AuthSet)
            {
                Auth = incoming.Auth;
            }

            if (incoming.WithCredentialsSet)
            {
                WithCredentials = incoming.WithCredentials;
            }

            if (incoming.MaxRedirectsSet)
            {
                MaxRedirects = incoming.MaxRedirects;
            }
        }

        internal ConnectionSettings Clone() => (ConnectionSettings)MemberwiseClone();
    }
}
