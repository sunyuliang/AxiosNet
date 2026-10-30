using System.Collections.Concurrent;
using AxiosNet.Cache;
using AxiosNet.Internal;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>请求 / 实例配置，字段与 axios 的 config 一一对应。</summary>
public sealed partial class AxiosConfig
{
    // 按领域分组的可选设置：首次读取才分配，Clone 时各自复制。
    private SerializationSettings? _serialization;
    private ConnectionSettings? _connection;
    private LimitSettings? _limits;
    private CallbackSettings? _callbacks;
    private RetrySettings? _retry;
    private CircuitSettings? _circuit;
    private HedgingSettings? _hedging;
    private FailoverSettings? _failover;
    private TelemetrySettings? _telemetry;
    private IdempotencySettings? _idempotency;

    /// <summary>序列化相关设置：JSON 选项、内容序列化器、文本编码、查询串与表单字段命名；首次读取时懒创建。</summary>
    public SerializationSettings Serialization
    {
        get => _serialization ??= new SerializationSettings();
        set => _serialization = value;
    }

    /// <summary>连接相关设置：代理 / 凭据 / 连接池选项 / HTTP 版本与协商策略 / Basic 认证 / 凭据开关 / 重定向次数；首次读取时懒创建。</summary>
    public ConnectionSettings Connection
    {
        get => _connection ??= new ConnectionSettings();
        set => _connection = value;
    }

    /// <summary>体积、速率、在途并发上限、排队上限与排队超时、流式单帧上限；首次读取时懒创建。</summary>
    public LimitSettings Limits
    {
        get => _limits ??= new LimitSettings();
        set => _limits = value;
    }

    /// <summary>进度 / 原始字节回调、状态校验与超时文案；首次读取时懒创建。</summary>
    public CallbackSettings Callbacks
    {
        get => _callbacks ??= new CallbackSettings();
        set => _callbacks = value;
    }

    /// <summary>弹性重试：策略与每次重试前的回调；不设置 <see cref="RetrySettings.Policy"/> 即不重试。</summary>
    public RetrySettings Retry
    {
        get => _retry ??= new RetrySettings();
        set => _retry = value;
    }

    /// <summary>熔断：策略与被拒绝时的回调；不设置 <see cref="CircuitSettings.Policy"/> 即不熔断。</summary>
    public CircuitSettings Circuit
    {
        get => _circuit ??= new CircuitSettings();
        set => _circuit = value;
    }

    /// <summary>并发对冲：首发的延迟副本与分支数；<see cref="HedgingSettings.MaxHedges"/> 为 0 即不对冲。</summary>
    public HedgingSettings Hedging
    {
        get => _hedging ??= new HedgingSettings();
        set => _hedging = value;
    }

    /// <summary>备用端点：当前端点用不了时按顺序换端点重发；<see cref="FailoverSettings.Endpoints"/> 为空即不换。</summary>
    public FailoverSettings Failover
    {
        get => _failover ??= new FailoverSettings();
        set => _failover = value;
    }

    /// <summary>可观测性：链路 / 指标 / 事件三路观测点的按请求开关（<see cref="TelemetrySettings.Enabled"/>）与 URL 采集策略（<see cref="TelemetrySettings.CaptureUrl"/>）。</summary>
    public TelemetrySettings Telemetry
    {
        get => _telemetry ??= new TelemetrySettings();
        set => _telemetry = value;
    }

    /// <summary>幂等键：打开后每个逻辑请求写一个键，重试 / 换端点 / 对冲共用同一个键（默认关闭）。</summary>
    public IdempotencySettings Idempotency
    {
        get => _idempotency ??= new IdempotencySettings();
        set => _idempotency = value;
    }

    // 库内读取一律走这几个访问器：公共 getter 会懒创建对象。
    internal SerializationSettings? SerializationOrNull => _serialization;

    internal ConnectionSettings? ConnectionOrNull => _connection;

    internal LimitSettings? LimitsOrNull => _limits;

    internal CallbackSettings? CallbacksOrNull => _callbacks;

    internal RetrySettings? RetryOrNull => _retry;

    internal CircuitSettings? CircuitOrNull => _circuit;

    internal HedgingSettings? HedgingOrNull => _hedging;

    internal FailoverSettings? FailoverOrNull => _failover;

    internal TelemetrySettings? TelemetryOrNull => _telemetry;

    internal IdempotencySettings? IdempotencyOrNull => _idempotency;

    /// <summary>对应 axios baseURL。</summary>
    public string? BaseAddress { get; set; }

    /// <summary>对应 axios url（相对或绝对地址）。</summary>
    public string? Url { get; set; }

    private HttpMethod _method = HttpMethod.Get;

    /// <summary>对应 axios method，默认 GET；直接使用 BCL 的 <see cref="HttpMethod"/>。</summary>
    public HttpMethod Method
    {
        get => _method;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _method = value;
            MethodSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool MethodSet { get; private set; }

    // 集合成员延迟创建：字段为 null 表示还没有内容，首次读写才创建；库内读路径先用 Has* 判定。
    private AxiosHeaders? _headers;

    /// <summary>对应 axios headers（大小写不敏感）；序列化器可在 <see cref="IContentSerializer.Serialize"/> 里写入，正文构造完成后统一应用。</summary>
    /// <exception cref="ArgumentNullException">赋值为 <c>null</c>。</exception>
    public AxiosHeaders Headers
    {
        get => _headers ??= new AxiosHeaders();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _headers = value;
        }
    }

    /// <summary>是否已经有请求头内容（不创建集合）。供库内热路径先判后取。</summary>
    internal bool HasHeaders => _headers is { Count: > 0 };

    private SynchronizedList<string>? _removeHeaders;

    /// <summary>按请求要删除的 Header 名（大小写不敏感），在 defaults 的头合并完成之后生效；线程安全，枚举走快照。</summary>
    public ICollection<string> RemoveHeaders => _removeHeaders ??= new SynchronizedList<string>();

    /// <summary>是否有要摘除的头（不创建集合）。</summary>
    internal bool HasRemoveHeaders => _removeHeaders is { Count: > 0 };

    /// <summary>删头名单的只读快照（不可变数组），调用方不得修改；库内热路径用它代替枚举 <see cref="RemoveHeaders"/>。</summary>
    internal string[]? RemoveHeadersSnapshot
        => _removeHeaders is { Count: > 0 } ? _removeHeaders.Snapshot() : null;

    private object? _params;

    /// <summary>对应 axios params，会被序列化为 query string：支持对象 / 字典 / 集合 / 已序列化好的查询串；标量抛 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）。</summary>
    public object? Params
    {
        get => _params;
        set
        {
            _params = value;
            ParamsSet = true;
        }
    }

    /// <summary>是否被显式赋值过（包括赋 <c>null</c>）：合并时区分「没写」与「写了 null」，后者摘掉实例默认值。</summary>
    internal bool ParamsSet { get; private set; }

    /// <summary>库默认的请求体媒体类型（等价于 <see cref="AxiosContentType.Json"/>）。</summary>
    public const string JsonContentType = AxiosContentType.Json;

    /// <summary>表单编码媒体类型常量（等价于 <see cref="AxiosContentType.FormUrlEncoded"/>，配合 <see cref="ContentType"/> 使用）。</summary>
    public const string FormUrlEncodedContentType = AxiosContentType.FormUrlEncoded;

    private string _contentType = JsonContentType;

    /// <summary>
    /// 请求体媒体类型（Content-Type），默认 <c>application/json</c>；编码格式由本项决定，与 <see cref="Data"/> 的运行时类型无关。
    /// 未显式设置时由序列化器自报（见 <see cref="IContentSerializer.MediaType"/>）。
    /// </summary>
    public string ContentType
    {
        get => _contentType;
        set
        {
            // 拒绝 null：会压掉序列化器自报的 MediaType。
            ArgumentNullException.ThrowIfNull(value);

            var trimmed = value.Trim();

            if (trimmed.Length == 0)
            {
                throw new ArgumentException("ContentType 不能为空白；不设置时头由序列化器自报（见 IContentSerializer.MediaType），请不要赋值。", nameof(value));
            }

            // 这个值最终写入 Content-Type，与头集合用同一套校验。
            AxiosHeaders.ValidateHeaderValue("Content-Type", trimmed);

            _contentType = trimmed;
            ContentTypeSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool ContentTypeSet { get; private set; }

    /// <summary>
    /// 请求体压缩算法（默认 <c>null</c> = 不压缩；<see cref="AxiosCompression.None"/> 用于按请求关掉实例默认的压缩）：
    /// 作用于按字节缓冲出来的正文（文本 / 二进制 / 表单），压缩后自动补 <c>Content-Encoding</c>。
    /// 流式正文（<see cref="Stream"/>、<c>IAsyncEnumerable&lt;byte[]&gt;</c>）、调用方自带的 <see cref="HttpContent"/>
    /// 与 multipart 正文不压缩（会直接报 <c>ERR_BAD_OPTION_VALUE</c>）。
    /// </summary>
    public AxiosCompression? RequestCompression { get; set; }

    private object? _data;

    /// <summary>
    /// 对应 axios data：请求体原始数据，按 <see cref="ContentType"/> 编码；string / byte[] / Stream / HttpContent 原样发送，
    /// <c>IAsyncEnumerable&lt;byte[]&gt;</c> / <c>IAsyncEnumerable&lt;ReadOnlyMemory&lt;byte&gt;&gt;</c> 按块流式发送（长度未知）。
    /// 合并按「是否赋值过」区分：不碰继承实例默认值，显式赋 <c>null</c> 发空正文。
    /// </summary>
    public object? Data
    {
        get => _data;
        set
        {
            _data = value;
            DataSet = true;
        }
    }

    /// <summary>是否被显式赋值过（包括赋 <c>null</c>）：合并时区分「没写」与「写了 null」，后者摘掉实例默认值。</summary>
    internal bool DataSet { get; private set; }

    /// <summary>短路响应：设置后本次请求不发送，直接以这份结果作为响应；只属于本次请求，<see cref="Clone"/> 不复制它（细节见 <see cref="AxiosNet.AxiosShortCircuit"/>）。</summary>
    public AxiosShortCircuit? ShortCircuit { get; set; }

    /// <summary>响应缓存：设置后命中的请求不发出去，直接交付缓存里的那份响应（细节见 <see cref="AxiosNet.Cache.AxiosCache"/>）。</summary>
    /// <remarks>与 <see cref="ShortCircuit"/> 不同，它是实例级的共享状态：<see cref="Clone"/> 保留同一个实例，挂在 Defaults 上对整个实例生效。</remarks>
    public AxiosCache? Cache { get; set; }

    /// <summary>本次请求是否要留原始响应字节：缓存阶段决定，响应读取据此决定留不留（不留时字节读完即弃）。</summary>
    internal bool CacheCaptureRawBody { get; set; }

    private TimeSpan _timeout;

    /// <summary>
    /// 对应 axios timeout，零表示不超时；一次尝试的预算（建连 + 等待响应头 + 读取正文），重试 / 备用端点 / 对冲各复制一份，
    /// 要限制整条逻辑请求用 <see cref="TotalTimeout"/>。
    /// 流式响应的正文也计入本预算；零值下正文不限时。负值抛 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）。
    /// </summary>
    public TimeSpan Timeout
    {
        get => _timeout;
        set
        {
            _timeout = value;
            TimeoutSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool TimeoutSet { get; private set; }

    private TimeSpan? _totalTimeout;

    /// <summary>
    /// 整条逻辑请求的总预算（<c>null</c> = 不设，默认）：从调用开始计时，<b>跨重试、备用端点与对冲分支共享</b>。
    /// 每次尝试实际上限为 <c>min(Timeout, 剩余预算)</c>，剩余不足时该次尝试不发出并抛 <c>ECONNABORTED</c>；
    /// <c>Zero</c> 与 <c>null</c> 等价，负值抛 <c>ERR_BAD_OPTION_VALUE</c>。
    /// </summary>
    /// <remarks>排队时间与流式正文都在预算之内。</remarks>
    public TimeSpan? TotalTimeout
    {
        get => _totalTimeout;
        set
        {
            _totalTimeout = value;
            TotalTimeoutSet = true;
        }
    }

    /// <summary>是否被显式赋值过（含赋 <c>null</c>），用于合并配置时区分「未设置」与「清掉实例默认值」。</summary>
    internal bool TotalTimeoutSet { get; private set; }

    /// <summary>这份配置是否已是合并后的生效配置，管线据此跳过二次合并；<see cref="Clone"/> 不继承它。</summary>
    internal bool EffectiveMerged { get; set; }

    /// <summary>这份配置里的实例级选项（WithCredentials / MaxRedirects / Proxy / Credentials / PoolOptions）是实例的生效值，合并时沿用本实例默认值；随 <see cref="Clone"/> 继承。</summary>
    internal bool InstanceOptionsInherited { get; set; }

    /// <summary>transform 链里继承自实例默认的前缀长度（库产出的配置才有，调用方 new 的为 0），合并层据此去重；随 <see cref="Clone"/> 继承。</summary>
    internal int TransformInheritedRequest { get; set; }

    /// <summary><see cref="TransformInheritedRequest"/> 的响应侧对应项。</summary>
    internal int TransformInheritedResponse { get; set; }

    /// <summary>是否被显式赋值过（含赋 <c>null</c>），用于合并配置时区分「未设置」与「显式清除」。</summary>
    internal bool AuthSet => _connection?.AuthSet ?? false;

    private ResponseType _responseType = ResponseType.Json;

    /// <summary>对应 axios responseType，默认 Json。</summary>
    public ResponseType ResponseType
    {
        get => _responseType;
        set
        {
            _responseType = value;
            ResponseTypeSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool ResponseTypeSet { get; private set; }

    /// <summary>管线内部的 responseType 自动切换：只改值、不置 <see cref="ResponseTypeSet"/>。</summary>
    internal void SetResponseTypeAuto(ResponseType value) => _responseType = value;

    private CancellationToken _cancellationToken;

    /// <summary>外部取消令牌。未显式设置时沿用实例默认（与 timeout 共同构成取消来源）。</summary>
    public CancellationToken CancellationToken
    {
        get => _cancellationToken;
        set
        {
            _cancellationToken = value;
            CancellationTokenSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool CancellationTokenSet { get; private set; }

    /// <summary>是否被显式赋值过，用于拒绝「按请求设置实例级选项」。</summary>
    internal bool WithCredentialsSet => _connection?.WithCredentialsSet ?? false;

    /// <summary>是否被显式赋值过，用于拒绝「按请求设置实例级选项」。</summary>
    internal bool MaxRedirectsSet => _connection?.MaxRedirectsSet ?? false;

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool MaxContentLengthSet => _limits?.MaxContentLengthSet ?? false;

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool MaxBodyLengthSet => _limits?.MaxBodyLengthSet ?? false;

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool MaxRateSet => _limits?.MaxRateSet ?? false;

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool CollectionFormatSet => _serialization?.CollectionFormatSet ?? false;

    /// <summary>对应 axios transformRequest：拦截器之后、请求体编码之前依次作用于 <see cref="Data"/>，每个函数返回新的数据。列表线程安全，枚举走快照。</summary>
    public IList<Func<object?, AxiosConfig, object?>> TransformRequest
    {
        get => _transformRequest ??= new SynchronizedList<Func<object?, AxiosConfig, object?>>();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            // 整体替换也包成同一线程安全实现。
            _transformRequest = WrapTransforms(value);
        }
    }

    private SynchronizedList<Func<object?, AxiosConfig, object?>>? _transformRequest;

    /// <summary>是否有请求 transform（不创建集合）。</summary>
    internal bool HasTransformRequest => _transformRequest is { Count: > 0 };

    /// <summary>请求 transform 链的只读快照（不可变数组），调用方不得修改。</summary>
    internal Func<object?, AxiosConfig, object?>[]? TransformRequestSnapshot
        => _transformRequest is { Count: > 0 } ? _transformRequest.Snapshot() : null;

    private SynchronizedList<Func<object?, AxiosConfig, object?>>? _transformResponse;

    /// <summary>是否有响应 transform（不创建集合）。</summary>
    internal bool HasTransformResponse => _transformResponse is { Count: > 0 };

    /// <summary>响应 transform 链的只读快照（响应阶段热路径，同 <see cref="TransformRequestSnapshot"/>）。</summary>
    internal Func<object?, AxiosConfig, object?>[]? TransformResponseSnapshot
        => _transformResponse is { Count: > 0 } ? _transformResponse.Snapshot() : null;

    /// <summary>
    /// 对应 axios transformResponse：响应体反序列化之后依次作用于 Data（如统一拆包），流式响应不适用；
    /// 可读 <see cref="Response"/> 拿响应头。列表线程安全，枚举走快照。
    /// </summary>
    public IList<Func<object?, AxiosConfig, object?>> TransformResponse
    {
        get => _transformResponse ??= new SynchronizedList<Func<object?, AxiosConfig, object?>>();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _transformResponse = WrapTransforms(value);
        }
    }

    /// <summary>
    /// 响应阶段的只读上下文：状态码、响应头、媒体类型与字符集；请求阶段为 <c>null</c>，由库写入、调用方只读。
    /// <see cref="Clone"/> 不复制它。
    /// </summary>
    public AxiosResponseInfo? Response { get; internal set; }

    /// <summary>清掉实例级选项的「显式赋值过」标记。</summary>
    internal void ClearInstanceOptionFlags()
    {
        if (_connection is not null)
        {
            _connection.WithCredentialsSet = false;
            _connection.MaxRedirectsSet = false;
        }
    }

    /// <summary>深拷贝（Headers / transform 链 / Params / Data 的字典容器独立；其余引用成员按引用共享）。</summary>
    /// <remarks>克隆一份生效配置（如 <see cref="AxiosResponse.Config"/>）后，它仍是「库产出的配置」：回放时实例级项取自目标实例，改 <c>BaseAddress</c> 之类不会生效。</remarks>
    public AxiosConfig Clone()
    {
        var clone = (AxiosConfig)MemberwiseClone();

        // ① 库自有集合共享一版不可变数组；② 调用方容器复制一份；③ 设置分组各自 Clone()。
        clone._headers = _headers?.Clone();

        // transform 链与删头名单写时复制共享；空链在克隆侧保持 null。
        clone._removeHeaders = _removeHeaders?.ShareIfNotEmpty();
        clone._transformRequest = _transformRequest?.ShareIfNotEmpty();
        clone._transformResponse = _transformResponse?.ShareIfNotEmpty();
        clone._serialization = _serialization?.Clone();
        clone._connection = _connection?.Clone();
        clone._limits = _limits?.Clone();
        clone._callbacks = _callbacks?.Clone();
        clone._retry = _retry?.Clone();
        clone._circuit = _circuit?.Clone();
        clone._hedging = _hedging?.Clone();
        clone._failover = _failover?.Clone();
        clone._telemetry = _telemetry?.Clone();
        clone._idempotency = _idempotency?.Clone();

        // Params / Data 是可变的调用方容器，克隆时隔离一份。
        clone._params = IsolateMutableContainer(_params);
        clone._data = IsolateMutableContainer(_data);

        // 响应上下文属于某一次响应，不随克隆带过去。
        clone.Response = null;

        // 短路响应只属于那一次请求，克隆时清空。
        clone.ShortCircuit = null;

        // 缓存是实例级共享状态，随克隆保留（MemberwiseClone 已复制，这里只作说明）。

        // 「要不要留原始字节」属于那一次请求的缓存判定，克隆出来的是新请求的输入。
        clone.CacheCaptureRawBody = false;

        // 已生成的幂等键属于某一次发送：克隆是「新请求的输入」，不能带着旧键走。
        if (clone._idempotency is not null)
        {
            clone._idempotency.GeneratedKey = null;
        }

        // Auth 的值已是生效结果，克隆后清掉「显式赋值过」标记。
        if (clone._connection is not null)
        {
            clone._connection.AuthSet = false;
        }

        // 合并标记属于那一次合并：克隆出来的是新请求的输入，尚未合并。
        if (clone.EffectiveMerged)
        {
            clone.EffectiveMerged = false;

            // 实例级选项的来源信息保留下来。
            clone.InstanceOptionsInherited = true;
        }

        return clone;
    }

    /// <summary>
    /// 派生一份按尝试隔离的浅拷贝：子设置与集合全部按引用共享，只把 <see cref="Response"/> 清空。
    /// </summary>
    internal AxiosConfig CloneForAttempt()
    {
        var clone = (AxiosConfig)MemberwiseClone();
        clone.Response = null;
        return clone;
    }

    /// <summary>
    /// 把 <see cref="Params"/> / <see cref="Data"/> 里调用方持有的可变字典复制一份，避免克隆体与实例默认值指向同一对象；
    /// 其余类型（匿名对象 / 集合 / 流 / 字符串）原样共享。
    /// </summary>
    /// <remarks>字符串键字典按原类型 + 原 comparer 复制；其余 <see cref="IDictionary{TKey,TValue}"/> 退化成 <see cref="Dictionary{TKey,TValue}"/>（内容与顺序不变，运行时类型会变）。</remarks>
    private static object? IsolateMutableContainer(object? value)
    {
        // 先挡掉 null。
        if (value is null)
        {
            return null;
        }

        return value switch
        {
            // 具体类型排在接口分支之前。
            Dictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary, dictionary.Comparer),
            Dictionary<string, string> dictionary => new Dictionary<string, string>(dictionary, dictionary.Comparer),
            SortedDictionary<string, object?> dictionary => new SortedDictionary<string, object?>(dictionary, dictionary.Comparer),
            SortedDictionary<string, string> dictionary => new SortedDictionary<string, string>(dictionary, dictionary.Comparer),
            SortedList<string, object?> dictionary => new SortedList<string, object?>(dictionary, dictionary.Comparer),
            SortedList<string, string> dictionary => new SortedList<string, string>(dictionary, dictionary.Comparer),
            ConcurrentDictionary<string, object?> dictionary => new ConcurrentDictionary<string, object?>(dictionary, dictionary.Comparer),
            ConcurrentDictionary<string, string> dictionary => new ConcurrentDictionary<string, string>(dictionary, dictionary.Comparer),

            // 其余 IDictionary 实现退化成 Dictionary。
            IDictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary),
            IDictionary<string, string> dictionary => new Dictionary<string, string>(dictionary),
            _ => value
        };
    }

    /// <summary>把外部传入的 transform 集合包成线程安全实现（已是则原样复用）。</summary>
    private static SynchronizedList<Func<object?, AxiosConfig, object?>> WrapTransforms(
        IList<Func<object?, AxiosConfig, object?>> values)
        => values as SynchronizedList<Func<object?, AxiosConfig, object?>> ?? new SynchronizedList<Func<object?, AxiosConfig, object?>>(values);
}
