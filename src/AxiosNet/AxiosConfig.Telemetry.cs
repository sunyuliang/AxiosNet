namespace AxiosNet;

public sealed partial class AxiosConfig
{
    /// <summary>
    /// 可观测性（<see cref="AxiosConfig.Telemetry"/>）：链路（<c>ActivitySource</c>）、指标（<c>Meter</c>）
    /// 与事件（<c>DiagnosticListener</c>）三类观测点的**按请求**开关。
    /// <para>
    /// 三者都是「有人订阅才产出」：没有监听者时不建 Activity、不发事件，指标调用退化为几次空检查，
    /// 因此默认开启不会改变请求行为。要**整体关掉**（例如健康检查、探针请求不想污染指标）请用
    /// <see cref="Enabled"/>；只想收敛 URL 里的敏感信息用 <see cref="CaptureUrl"/>。
    /// </para>
    /// <para>
    /// 订阅方式见 <see cref="Telemetry.AxiosTelemetry"/>：OpenTelemetry 用
    /// <c>AddSource("AxiosNet")</c> + <c>AddMeter("AxiosNet")</c>，日志侧订阅
    /// <c>DiagnosticListener</c>「AxiosNet.DiagnosticListener」。
    /// </para>
    /// </summary>
    public sealed class TelemetrySettings
    {
        /// <summary>
        /// 本次请求是否产出遥测；<c>null</c>（默认）= 开启。
        /// <para>
        /// 设为 <c>false</c> 时：不建 Activity、不写指标、不发诊断事件 —— 与「没有这个功能」时的
        /// 执行路径完全一致（连起始时间戳都不取）。用于排除健康检查 / 探针这类「不想算进业务指标」的请求。
        /// </para>
        /// <para>用 <c>bool?</c> 而不是 <c>bool</c>：<c>null</c> 表示「本次没表态」，合并时沿用实例默认值。</para>
        /// </summary>
        public bool? Enabled { get; set; }

        /// <summary>
        /// 是否把完整 URL（含查询串）写进 span 的 <c>url.full</c> 标签；<c>null</c>（默认）= 写。
        /// <para>
        /// 默认与 <c>HttpClient</c> 的行为一致（它同样记录完整 URL），因为查询串往往是定位问题最直接的线索。
        /// 但查询串里可能带令牌 / 签名 / 个人信息，而 span 会进第三方后端：对这类接口设为 <c>false</c>，
        /// 只保留 <c>url.scheme</c> / <c>server.address</c> / <c>server.port</c> / <c>url.path</c>。
        /// </para>
        /// <para>只影响链路标签，不影响指标（指标的标签里本来就没有 URL）。</para>
        /// </summary>
        public bool? CaptureUrl { get; set; }

        /// <summary>
        /// 把 <paramref name="incoming"/> 里显式设置过的项覆盖到本实例（合并层内部使用）。
        /// <para>
        /// 两项都是 <c>bool?</c>，<c>null</c> 就是「没表态」，因此不需要额外的 <c>*Set</c> 标记：
        /// 与 <see cref="RetrySettings.Policy"/> 那种「<c>null</c> 本身有意义」的项不同。
        /// </para>
        /// </summary>
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
        }

        internal TelemetrySettings Clone() => (TelemetrySettings)MemberwiseClone();
    }
}
