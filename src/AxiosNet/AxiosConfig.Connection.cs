using AxiosNet.Pooling;

namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>连接相关设置（<see cref="AxiosConfig.Connection"/>）。</summary>
    public sealed class ConnectionSettings
    {
        /// <summary>代理设置，仅实例级生效（按请求设置抛 <see cref="AxiosException"/>）；注入自定义 Handler 时由 Handler 决定。</summary>
        public IWebProxy? Proxy { get; set; }

        /// <summary>显式凭据，仅实例级生效（按请求设置抛 <see cref="AxiosException"/>）；注入自定义 Handler 时由 Handler 决定。</summary>
        public ICredentials? Credentials { get; set; }

        /// <summary>连接池选项，仅在构造 AxiosClient（自建池）时生效；单次请求上设置会抛 <see cref="AxiosException"/>。</summary>
        public ConnectionPoolOptions? PoolOptions { get; set; }

        /// <summary>
        /// HTTP 版本，<c>null</c>（默认）= HTTP/2（<see cref="System.Net.HttpVersion.Version20"/>），配合默认协商策略可降级到 1.1。
        /// 设 <c>Version30</c> 即要求 h3（环境支持见 <see cref="Http3Support"/>）；按请求项：单次请求覆盖实例默认值。
        /// </summary>
        public Version? HttpVersion { get; set; }

        /// <summary>
        /// 版本协商策略（<see cref="System.Net.Http.HttpVersionPolicy"/>），默认 <c>RequestVersionOrLower</c>（服务端不支持就降级）；
        /// <c>RequestVersionExact</c> 表示只用该版本，协商不上就报错。按请求项：单次请求覆盖实例默认值。
        /// </summary>
        public HttpVersionPolicy? VersionPolicy { get; set; }

        private string? _transport;

        /// <summary>
        /// 传输档名（默认 <c>null</c> / 空串 = 默认档）：选择连接池上由
        /// <see cref="ConnectionPoolOptions.AddTransport"/> 登记的一套出口设置（代理 / 凭据 / 客户端证书 / Cookie 会话）。
        /// </summary>
        /// <remarks>
        /// <para>按请求项：单次请求可以走另一条出口，不需要新建客户端或连接池。</para>
        /// <para>档必须已在池上登记，否则请求发出前抛 <c>ERR_BAD_OPTION_VALUE</c>（不静默回落默认档）。</para>
        /// <para>直接注入自定义 <see cref="HttpMessageHandler"/> 时不走连接池，本项无效：此时传非空档名会报错。</para>
        /// </remarks>
        public string? Transport
        {
            get => _transport;
            set
            {
                _transport = value;
                TransportSet = true;
            }
        }

        /// <summary>是否被显式赋值过（含显式赋 <c>null</c>），用于合并配置时区分「未设置」与「显式回到默认档」。</summary>
        internal bool TransportSet { get; set; }

        private AxiosAuth? _auth;

        /// <summary>对应 axios auth（HTTP Basic）。</summary>
        /// <remarks><c>Auth = null</c> 表示本次请求不带认证，会压住实例默认凭据。</remarks>
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
        /// 对应 axios withCredentials（使用系统默认凭据 / Cookie），仅实例级生效：按请求写不同的值抛异常，相同的值放行。
        /// 判「相同」比的是连接池的生效值（<see cref="ConnectionPoolOptions.UseCookies"/>），不是本属性。
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

        /// <summary>对应 axios maxRedirects，默认 5，0 表示不跟随重定向；仅实例级生效，按请求写不同的值抛异常。</summary>
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

        /// <summary>把 <paramref name="incoming"/> 里的按请求连接项覆盖到本实例（合并层内部使用）；HttpVersion / VersionPolicy 由合并层单独处理。</summary>
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
