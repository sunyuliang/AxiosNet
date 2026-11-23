using AxiosNet.Pooling;

namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>传输相关设置（<see cref="AxiosConfig.Transport"/>）。</summary>
    public sealed class TransportSettings
    {
        /// <summary>
        /// HTTP 版本，<c>null</c>（默认）= HTTP/2，配合默认协商策略可降级到 1.1；netstandard2.0 资产默认 1.1，
        /// 显式要求 2.0 / 3.0 会在发送前以 <c>ERR_BAD_OPTION_VALUE</c> 拒绝。
        /// </summary>
        /// <remarks>显式写 <c>null</c> 是「显式回到库默认版本」，会压过实例默认值；要沿用实例默认值就不要碰本项。</remarks>
        public Version? HttpVersion
        {
            get => _httpVersion;
            set
            {
                _httpVersion = value;
                HttpVersionSet = true;
            }
        }

        private Version? _httpVersion;

        /// <summary>是否被显式赋值过（含显式赋 <c>null</c>），用于合并配置时区分「未设置」与「显式回到库默认」。</summary>
        internal bool HttpVersionSet { get; set; }

        /// <summary>
        /// 版本协商策略（<c>HttpVersionPolicy</c>），默认 <c>RequestVersionOrLower</c>；<c>RequestVersionExact</c> 表示只用该版本。
        /// netstandard2.0 资产只有 HTTP/1.1，<see cref="HttpVersion"/> 设为 2.0 / 3.0 时本项不参与协商，发送前直接拒绝。
        /// </summary>
        /// <remarks>显式写 <c>null</c> 是「显式回到库默认值」，会压过实例默认值。</remarks>
        public HttpVersionPolicy? VersionPolicy
        {
            get => _versionPolicy;
            set
            {
                _versionPolicy = value;
                VersionPolicySet = true;
            }
        }

        private HttpVersionPolicy? _versionPolicy;

        /// <summary>是否被显式赋值过（含显式赋 <c>null</c>），用于合并配置时区分「未设置」与「显式回到库默认」。</summary>
        internal bool VersionPolicySet { get; set; }

        private string? _profile;

        /// <summary>
        /// 传输档名（默认 <c>null</c> / 空串 = 默认档）：选择连接池上由
        /// <see cref="ConnectionPoolOptions.AddTransport"/> 登记的一套出口设置（代理 / 凭据 / 客户端证书 / Cookie 会话）。
        /// </summary>
        /// <remarks>
        /// <para>按请求项：单次请求可以走另一条出口，不需要新建客户端或连接池。</para>
        /// <para>档必须已在池上登记，否则请求发出前抛 <c>ERR_BAD_OPTION_VALUE</c>；注入自定义 Handler 时本项无效，传非空档名会报错。</para>
        /// </remarks>
        public string? Profile
        {
            get => _profile;
            set
            {
                _profile = value;
                ProfileSet = true;
            }
        }

        /// <summary>是否被显式赋值过（含显式赋 <c>null</c>），用于合并配置时区分「未设置」与「显式回到默认档」。</summary>
        internal bool ProfileSet { get; set; }

        private AxiosCredential? _auth;

        /// <summary>凭据来源的唯一槽位（对应 axios <c>auth</c>）：静态 Basic、静态令牌、完整头值，或每请求调用一次的提供方。</summary>
        /// <remarks>
        /// <c>Auth = null</c> 表示本次请求不带认证，会压住实例默认凭据；显式 <c>Authorization</c> 头与本项同时设置视为配置冲突（<c>ERR_BAD_OPTION_VALUE</c>）。
        /// </remarks>
        public AxiosCredential? Auth
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

        private long? _maxRate;

        /// <summary>令牌桶限速（字节/秒），<c>null</c>（默认）不限；按请求项，单次请求可以覆盖。</summary>
        public long? MaxRate
        {
            get => _maxRate;
            set
            {
                _maxRate = value;
                MaxRateSet = true;
            }
        }

        /// <summary>是否被显式赋值过（含显式 <c>null</c>），用于合并配置时区分「未设置」与「显式清除」。</summary>
        internal bool MaxRateSet { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里的按请求传输项覆盖到本实例（合并层内部使用）；HttpVersion / VersionPolicy 由合并层单独处理。</summary>
        internal void MergeFrom(TransportSettings incoming)
        {
            if (incoming.ProfileSet)
            {
                Profile = incoming.Profile;
            }

            if (incoming.AuthSet)
            {
                Auth = incoming.Auth;
            }

            if (incoming.MaxRateSet)
            {
                MaxRate = incoming.MaxRate;
            }
        }

        /// <summary>浅拷贝（合并层用）：策略与委托共用，不深克隆。</summary>
        internal TransportSettings Clone() => (TransportSettings)MemberwiseClone();
    }
}
