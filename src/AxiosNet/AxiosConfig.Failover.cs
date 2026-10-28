namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 备用端点（<see cref="AxiosConfig.Failover"/>）：当前端点「用不了」时（网络错误 / 超时 / 连接池耗尽 / 熔断打开 / 5xx），
    /// 把同一份请求嫁接到下一个端点的 base URL 上重发；全部失败才交给重试。
    /// <see cref="Endpoints"/> 为空（默认）时不换端点。
    /// </summary>
    /// <remarks>
    /// 端点只替换 origin 与路径前缀，请求本身的 path / query 原样保留；顺序即优先级，每次重试都从第一个端点重新开始。
    /// 换端点要求请求体能按顺序重放（与重试同一份要求：流必须可 seek）。
    /// 4xx、<c>ERR_QUEUE_TIMEOUT</c>、配置 / 编程错误与取消不换端点；<c>ERR_CIRCUIT_OPEN</c> 换 ——
    /// 熔断按 <c>host:port</c> 独立成账，主端点开着的那一个挡不住备用端点。
    /// 流式响应只在响应头阶段能换端点：正文由调用方消费，中途断流不会再换端点。
    /// </remarks>
    public sealed class FailoverSettings
    {
        private IReadOnlyList<Uri>? _endpoints;

        /// <summary>
        /// 备用端点（有序，可带路径前缀），例如 <c>["https://backup1.example.com", "https://backup2.example.com/api"]</c>；
        /// <c>null</c> / 空集合（默认）= 不换端点。必须是绝对的 <c>http</c> / <c>https</c> 地址，且不能带 query / fragment
        /// （端点只当 base URL 用：替换 origin 与路径前缀，请求自己的 path / query 原样接上去）。
        /// </summary>
        /// <remarks>
        /// 赋值时快照成数组：之后改调用方自己的列表不会影响已生效的配置，克隆也能安全共享这一版。
        /// 读回来的就是这一版内部数组，<b>调用方不得修改</b>（要改请重新赋值）。
        /// </remarks>
        public IReadOnlyList<Uri>? Endpoints
        {
            get => _endpoints;
            set
            {
                _endpoints = value is null ? null : Snapshot(value);
                EndpointsSet = true;
            }
        }

        private static Uri[] Snapshot(IReadOnlyList<Uri> endpoints)
        {
            var copy = new Uri[endpoints.Count];
            for (var i = 0; i < copy.Length; i++)
            {
                copy[i] = endpoints[i];
            }

            return copy;
        }

        /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「显式关闭」。</summary>
        internal bool EndpointsSet { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里的端点表覆盖到本实例（合并层内部使用）。</summary>
        internal void MergeFrom(FailoverSettings incoming)
        {
            if (incoming.EndpointsSet)
            {
                // 两边都是不可变快照：直接共享这一版，不必每请求再复制一次。
                _endpoints = incoming._endpoints;
                EndpointsSet = true;
            }
        }

        internal FailoverSettings Clone() => (FailoverSettings)MemberwiseClone();
    }
}
