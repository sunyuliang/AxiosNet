namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 幂等键（<see cref="AxiosConfig.Idempotency"/>）：打开后每个<b>逻辑请求</b>生成一个键写进请求头，
    /// 重试 / 换端点 / 对冲的各次发送共用同一个键，服务端因此只把它当作一次写操作。
    /// </summary>
    /// <remarks>
    /// 键在请求模板上写一次（模板被复制时随头一起搬），所以「每次尝试换一个键」不会发生；
    /// 键属于<b>生效配置</b>：同一次发送内的重试 / 换端点 / 对冲共用一个键，而每调用一次 <c>RequestAsync</c> 都是一份新的生效配置、一个新键。
    /// 认证失败后的重放要用同一个键，需把它写进 <see cref="Key"/> 再重发。
    /// 默认关闭：只有显式 <see cref="Enabled"/> 才写头，因为非幂等方法自动带键会把「重复写」变成「静默去重」。
    /// </remarks>
    public sealed class IdempotencySettings
    {
        /// <summary>默认承载键的请求头名。</summary>
        internal const string DefaultHeaderName = "Idempotency-Key";

        private bool _enabled;
        private string? _headerName;
        private string? _key;

        /// <summary>是否写幂等键头，默认 <c>false</c>。</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                EnabledSet = true;
            }
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「显式关闭」。</summary>
        internal bool EnabledSet { get; set; }

        /// <summary>承载键的请求头名，默认 <c>Idempotency-Key</c>；空白按默认值处理。</summary>
        public string? HeaderName
        {
            get => _headerName;
            set
            {
                _headerName = value;
                HeaderNameSet = true;
            }
        }

        /// <summary>是否被显式赋值过（含赋 <c>null</c>）。</summary>
        internal bool HeaderNameSet { get; set; }

        /// <summary>显式键：非 <c>null</c> 时压过 <see cref="KeyFactory"/>，用于把上游的幂等键原样透传。</summary>
        public string? Key
        {
            get => _key;
            set
            {
                _key = value;
                KeySet = true;
            }
        }

        /// <summary>是否被显式赋值过（含赋 <c>null</c>）。</summary>
        internal bool KeySet { get; set; }

        /// <summary>键生成器（参数是本次生效配置），默认 <see cref="Guid"/> 的 <c>N</c> 格式；返回空白视为配置错误。</summary>
        public Func<AxiosConfig, string>? KeyFactory { get; set; }

        /// <summary>本次逻辑请求已生成的键：重发沿用，不再问工厂要一次新键。</summary>
        internal string? GeneratedKey { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里的设置覆盖到本实例（合并层内部使用）。</summary>
        internal void MergeFrom(IdempotencySettings incoming)
        {
            if (incoming.EnabledSet)
            {
                Enabled = incoming.Enabled;
            }

            if (incoming.HeaderNameSet)
            {
                HeaderName = incoming.HeaderName;
            }

            if (incoming.KeySet)
            {
                Key = incoming.Key;
            }

            KeyFactory = incoming.KeyFactory ?? KeyFactory;
        }

        internal IdempotencySettings Clone() => (IdempotencySettings)MemberwiseClone();
    }
}
