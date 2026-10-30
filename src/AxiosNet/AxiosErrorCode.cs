namespace AxiosNet;

/// <summary>
/// 错误码的枚举形式（<see cref="AxiosException.ErrorCode"/>）：与 <see cref="AxiosException.Code"/> 的字符串常量一一对应，
/// <c>switch</c> 能直接穷举、不会把字面量拼错，跨语言对照仍看字符串码。
/// </summary>
public enum AxiosErrorCode
{
    /// <summary>无法识别：<see cref="AxiosException.Code"/> 为 <c>null</c>，或由派生类写入了非内置的码。</summary>
    Unknown = 0,

    /// <summary><c>ERR_BAD_REQUEST</c>：状态码非法（4xx），或请求阶段就失败（载荷序列化、上传 / 发送前回调）。</summary>
    BadRequest,

    /// <summary><c>ERR_BAD_RESPONSE</c>：服务端 5xx，或响应体无法按目标类型解析、超出长度上限。</summary>
    BadResponse,

    /// <summary><c>ECONNABORTED</c>：超时（<see cref="AxiosConfig.Timeout"/> 或 <see cref="AxiosConfig.TotalTimeout"/>）。</summary>
    ConnAborted,

    /// <summary><c>ERR_NETWORK</c>：网络层失败（连接被拒、DNS 失败、正文传输出错等）。</summary>
    Network,

    /// <summary><c>ERR_CANCELED</c>：请求被取消。</summary>
    Canceled,

    /// <summary><c>ERR_INVALID_URL</c>：URL 非法（含相对 URL 但未提供 baseURL）。</summary>
    InvalidUrl,

    /// <summary><c>ERR_BAD_OPTION_VALUE</c>：配置项非法，或无法按请求生效（例如按请求设置代理）。</summary>
    BadOptionValue,

    /// <summary><c>ERR_QUEUE_TIMEOUT</c>：并发闸门排队超时，请求没有发出。</summary>
    QueueTimeout,

    /// <summary><c>ERR_QUEUE_FULL</c>：并发闸门的排队队列已满，请求没有发出。</summary>
    QueueFull,

    /// <summary><c>ERR_CIRCUIT_OPEN</c>：熔断打开，请求没有发出。</summary>
    CircuitOpen,

    /// <summary><c>ERR_POOL_EXHAUSTED</c>：连接池 origin 基数饱和，请求没有发出。</summary>
    PoolExhausted
}

/// <summary>字符串错误码与枚举的映射：两侧必须同步（由用例锁定每个内置常量都能映射到非 <see cref="AxiosErrorCode.Unknown"/>）。</summary>
internal static class AxiosErrorCodes
{
    public static AxiosErrorCode FromString(string? code) => code switch
    {
        AxiosException.ErrBadRequest => AxiosErrorCode.BadRequest,
        AxiosException.ErrBadResponse => AxiosErrorCode.BadResponse,
        AxiosException.ErrConnAborted => AxiosErrorCode.ConnAborted,
        AxiosException.ErrNetwork => AxiosErrorCode.Network,
        AxiosException.ErrCanceled => AxiosErrorCode.Canceled,
        AxiosException.ErrInvalidUrl => AxiosErrorCode.InvalidUrl,
        AxiosException.ErrBadOptionValue => AxiosErrorCode.BadOptionValue,
        AxiosException.ErrQueueTimeout => AxiosErrorCode.QueueTimeout,
        AxiosException.ErrQueueFull => AxiosErrorCode.QueueFull,
        AxiosException.ErrCircuitOpen => AxiosErrorCode.CircuitOpen,
        AxiosException.ErrPoolExhausted => AxiosErrorCode.PoolExhausted,
        _ => AxiosErrorCode.Unknown
    };
}
