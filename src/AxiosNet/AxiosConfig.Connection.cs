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

        /// <summary>HTTP 版本，默认 1.1。</summary>
        public Version? HttpVersion { get; set; }

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
        /// 只由合并层在「来源不是库产出的配置」时调用：<see cref="HttpVersion"/> 属按请求项，另行合并。
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
