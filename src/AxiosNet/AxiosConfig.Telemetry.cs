namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>可观测性（<see cref="AxiosConfig.Telemetry"/>）：链路 / 指标 / 事件三类观测点的按请求开关，没人订阅时不产出。</summary>
    public sealed class TelemetrySettings
    {
        /// <summary>本次请求是否产出遥测；<c>null</c>（默认）= 开启，设为 <c>false</c> 时不建 Activity、不写指标、不发事件。</summary>
        public bool? Enabled { get; set; }

        /// <summary>是否把 URL 写进 span 的 <c>url.full</c> 标签；<c>null</c>（默认）= 写（仅到 path，查询串见 <see cref="CaptureQuery"/>），只影响链路标签。</summary>
        public bool? CaptureUrl { get; set; }

        /// <summary>是否把查询串并进 <c>url.full</c>；<c>null</c>（默认）= 不写，仅在 <see cref="CaptureUrl"/> 生效时有意义。</summary>
        public bool? CaptureQuery { get; set; }

        /// <summary>把 <paramref name="incoming"/> 里显式设置过的项覆盖到本实例（合并层内部使用）；三项都是 <c>bool?</c>，<c>null</c> 即「没表态」。</summary>
        internal void MergeFrom(TelemetrySettings incoming)
        {
            if (incoming.Enabled is bool enabled)
            {
                Enabled = enabled;
            }

            if (incoming.CaptureUrl is bool captureUrl)
            {
                CaptureUrl = captureUrl;
            }

            if (incoming.CaptureQuery is bool captureQuery)
            {
                CaptureQuery = captureQuery;
            }
        }

        internal TelemetrySettings Clone() => (TelemetrySettings)MemberwiseClone();
    }
}
