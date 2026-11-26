using System.Collections.Concurrent;
using AxiosNet.Cache;
using AxiosNet.Metadata;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>请求 / 实例配置，字段与 axios 的 config 一一对应。</summary>
public sealed partial class AxiosConfig
{
    // 按领域分组的可选设置：首次读取才分配。
    private SerializationSettings? _serialization;
    private TransportSettings? _transport;
    private LimitsSettings? _limits;
    private ObservabilitySettings? _observability;
    private RetrySettings? _retry;
    private CircuitSettings? _circuit;
    private HedgingSettings? _hedging;
    private FailoverSettings? _failover;
    private IdempotencySettings? _idempotency;

    /// <summary>序列化相关设置：JSON 选项、内容序列化器、文本编码、查询串与表单字段命名；首次读取时懒创建。</summary>
    public SerializationSettings Serialization
    {
        get => _serialization ??= new SerializationSettings();
        set => _serialization = value;
    }

    /// <summary>传输相关设置：代理 / 凭据 / 连接池 / HTTP 版本与协商策略 / 传输档 / Basic 认证 / 重定向次数 / 限速；首次读取时懒创建。</summary>
    public TransportSettings Transport
    {
        get => _transport ??= new TransportSettings();
        set => _transport = value;
    }

    /// <summary>可观测性：采集档位与进度 / 原始字节 / 发送前回调、超时文案；首次读取时懒创建。</summary>
    public ObservabilitySettings Observability
    {
        get => _observability ??= new ObservabilitySettings();
        set => _observability = value;
    }

    /// <summary>未设置时为 <c>null</c>，读取不触发懒创建。</summary>
    internal SerializationSettings? SerializationOrNull => _serialization;

    /// <summary>未设置时为 <c>null</c>，读取不触发懒创建。</summary>
    internal TransportSettings? TransportOrNull => _transport;

    /// <summary>本次请求选中的传输档名（<c>null</c> / 空串 = 默认档）；不懒创建传输组。</summary>
    internal string? ProfileOrNull => _transport?.Profile;

    /// <summary>未设置时为 <c>null</c>；读取不触发懒创建。</summary>
    internal LimitsSettings? LimitsOrNull => _limits;

    /// <summary>未设置时为 <c>null</c>；读取不触发懒创建。</summary>
    internal ObservabilitySettings? ObservabilityOrNull => _observability;

    /// <summary>未设置时为 <c>null</c>；读取不触发懒创建。</summary>
    internal IdempotencySettings? IdempotencyOrNull => _idempotency;

    /// <summary>重试 / 熔断 / 对冲 / 备用端点 / 并发闸门的子对象存储；懒创建由 <see cref="Resilience"/> 负责。</summary>
    internal RetrySettings? RetryOrNull => _retry;

    /// <summary>未设置时为 <c>null</c>；读取不触发懒创建。</summary>
    internal CircuitSettings? CircuitOrNull => _circuit;

    /// <summary>未设置时为 <c>null</c>；读取不触发懒创建。</summary>
    internal HedgingSettings? HedgingOrNull => _hedging;

    /// <summary>未设置时为 <c>null</c>；读取不触发懒创建。</summary>
    internal FailoverSettings? FailoverOrNull => _failover;

    /// <summary>体积与流式分帧上限：<see cref="LimitsSettings.MaxRequestBytes"/> / <see cref="LimitsSettings.MaxResponseBytes"/> / <see cref="LimitsSettings.MaxStreamFrameBytes"/> 的唯一入口。</summary>
    public LimitsSettings Limits
    {
        get => _limits ??= new LimitsSettings();
        set => _limits = value;
    }

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

    // 集合成员延迟创建：字段为 null 表示还没有内容。
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

    /// <summary>库内按请求要删除的 Header 名（大小写不敏感），在 defaults 的头合并完成之后生效；线程安全，枚举走快照。</summary>
    internal ICollection<string> RemoveHeaders => _removeHeaders ??= new SynchronizedList<string>();

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

    /// <summary>追加一个查询参数：<see cref="Params"/> 为空时新建字典，已是可变字典时原地写入；其它形态抛错。</summary>
    /// <param name="name">参数名。</param>
    /// <param name="value">参数值；<c>null</c> 在序列化时跳过。</param>
    /// <exception cref="AxiosException"><see cref="Params"/> 已是无法原地写入的形态（<c>ERR_BAD_OPTION_VALUE</c>）。</exception>
    public void SetQuery(string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_params is null)
        {
            var created = new Dictionary<string, object?>();
            created[name] = value;
            Params = created;
            return;
        }

        if (_params is IDictionary<string, object?> writable)
        {
            writable[name] = value;
            return;
        }

        throw AxiosException.InvalidOption(
            $"Params 当前是 '{_params.GetType()}'，无法追加查询参数 '{name}'："
            + "SetQuery 只接受空值或键为 string、值为 object 的可变字典；"
            + "对象 / 查询串 / 只读字典请先转成 Dictionary<string, object?>。",
            this);
    }

    /// <summary>库默认的请求体媒体类型（等价于 <see cref="AxiosContentType.Json"/>）。</summary>
    public const string JsonContentType = AxiosContentType.Json;

    /// <summary>表单编码媒体类型常量（等价于 <see cref="AxiosContentType.FormUrlEncoded"/>，配合 <see cref="ContentType"/> 使用）。</summary>
    public const string FormUrlEncodedContentType = AxiosContentType.FormUrlEncoded;

    private string _contentType = JsonContentType;

    /// <summary>请求体媒体类型（Content-Type），默认 <c>application/json</c>；未显式设置时由序列化器自报。</summary>
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

            // 与头集合用同一套校验。
            AxiosHeaders.ValidateHeaderValue("Content-Type", trimmed);

            _contentType = trimmed;
            ContentTypeSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool ContentTypeSet { get; private set; }

    /// <summary>请求体压缩算法（默认 <c>null</c> = 不压缩），压缩后自动补 <c>Content-Encoding</c>；只作用于按字节缓冲的正文。</summary>
    public AxiosCompression? Compression
    {
        get => _compression;
        set
        {
            _compression = value;
            CompressionSet = true;
        }
    }

    private AxiosCompression? _compression;

    /// <summary>是否被显式赋值过（包括赋 <c>null</c>）：合并时区分「没写」与「写了 null」，后者摘掉实例默认值。</summary>
    internal bool CompressionSet { get; private set; }

    private object? _data;

    /// <summary>对应 axios data：请求体原始数据，按 <see cref="ContentType"/> 编码；string / byte[] / Stream / HttpContent 原样发送。</summary>
    public object? Body
    {
        get => _data;
        set
        {
            _data = value;
            BodySet = true;
        }
    }

    /// <summary>是否被显式赋值过（包括赋 <c>null</c>）：合并时区分「没写」与「写了 null」，后者摘掉实例默认值。</summary>
    internal bool BodySet { get; private set; }

    // 短路 / 缓存相关的状态同样只在用到时才分配。
    private CacheState? _cacheState;

    private sealed class CacheState
    {
        /// <summary>短路响应：本次请求不发出去，直接交付这份结果（见 <see cref="ShortCircuit"/>）。</summary>
        internal AxiosShortCircuit? ShortCircuit;

        /// <summary>响应缓存实例：实例级共享，<see cref="Clone"/> 保留同一引用（见 <see cref="Caching"/>）。</summary>
        internal AxiosCache? Caching;

        /// <summary>缓存命中 / 写入时是否连原始正文字节一起留存，供条件请求复用与排查。</summary>
        internal bool CacheCaptureRawBody;

        /// <summary>条件请求用的旧条目（带 <c>ETag</c> / <c>Last-Modified</c> 的那份），命中 <c>304</c> 时用它交付正文。</summary>
        internal AxiosShortCircuit? RevalidationEntry;

        /// <summary>按指针浅拷贝。</summary>
        internal CacheState Clone() => (CacheState)MemberwiseClone();
    }

    /// <summary>短路响应：设置后本次请求不发送，直接以这份结果作为响应；只属于本次请求，<see cref="Clone"/> 不复制它（细节见 <see cref="AxiosNet.AxiosShortCircuit"/>）。</summary>
    public AxiosShortCircuit? ShortCircuit
    {
        get => _cacheState?.ShortCircuit;
        set => SetOptional(ref _cacheState, value, static (state, v) => state.ShortCircuit = v);
    }

    /// <summary>响应缓存：设置后命中的请求不发出去，直接交付缓存里的那份响应（细节见 <see cref="AxiosNet.Cache.AxiosCache"/>）。</summary>
    /// <remarks>与 <see cref="ShortCircuit"/> 不同，它是实例级的共享状态：<see cref="Clone"/> 保留同一个实例，挂在 Defaults 上对整个实例生效。</remarks>
    public AxiosCache? Caching
    {
        get => _cacheState?.Caching;
        set => SetOptional(ref _cacheState, value, static (state, v) => state.Caching = v);
    }

    /// <summary>本次请求是否要留原始响应字节：缓存阶段决定，响应读取据此决定留不留（不留时字节读完即弃）。</summary>
    internal bool CacheCaptureRawBody
    {
        get => _cacheState is { CacheCaptureRawBody: true };
        set => SetFlag(ref _cacheState, value, static (state, v) => state.CacheCaptureRawBody = v);
    }

    /// <summary>本次请求要复用的旧缓存条目（条件请求）：管线据此发 <c>If-None-Match</c> / <c>If-Modified-Since</c>，收到 <c>304</c> 时用它交付正文；<see cref="Clone"/> 不复制。</summary>
    internal AxiosShortCircuit? RevalidationEntry
    {
        get => _cacheState?.RevalidationEntry;
        set => SetOptional(ref _cacheState, value, static (state, v) => state.RevalidationEntry = v);
    }

    private TimeSpan _timeout = TimeSpan.FromSeconds(100);

    /// <summary>对应 axios timeout：一次尝试的预算（建连 + 响应头 + 正文），默认 100 秒；零表示不超时，负值抛 <c>ERR_BAD_OPTION_VALUE</c>。重试 / 备用端点 / 对冲各复制一份。</summary>
    /// <remarks>流式响应的正文也计入本预算。</remarks>
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

    /// <summary>整条逻辑请求的总预算（<c>null</c> = 不设，默认）：跨重试、备用端点与对冲共享，每次尝试上限为 <c>min(Timeout, 剩余预算)</c>。</summary>
    /// <remarks>排队时间与流式正文都计入；<c>Zero</c> 等同 <c>null</c>，负值抛 <c>ERR_BAD_OPTION_VALUE</c>。</remarks>
    private bool _totalTimeoutSet;

    /// <summary>总预算的生效值（内部读取用）。</summary>
    internal TimeSpan? TotalTimeoutValue => _totalTimeout;

    /// <summary>这份配置是否已是合并后的生效配置，管线据此跳过二次合并；<see cref="Clone"/> 不继承它。</summary>
    internal bool EffectiveMerged { get; set; }

    /// <summary>这份配置里的实例级项（<c>BaseAddress</c> / <c>CancellationToken</c> / 序列化注册表等）已按实例生效值填好，合并时沿用本实例默认值；随 <see cref="Clone"/> 继承。</summary>
    internal bool InstanceOptionsInherited { get; set; }

    /// <summary>声明式入口的装配失败改判器：管线在请求成型阶段抛「构造失败」时用它换成对外错误；<c>null</c> 表示不改判。</summary>
    /// <remarks>由请求模型 / 接口方法的元数据在绑定期写入，每条声明共用一份委托；非声明式请求保持 <c>null</c>。</remarks>
    internal Func<AxiosException, AxiosException>? ConstructionFailureMap { get; set; }

    // 401 重放与凭据解析的按请求状态，只在真正用到时才分配。
    private AuthReplayState? _authReplay;

    private sealed class AuthReplayState
    {
        /// <summary>本次发送收尾不释放模板正文（401 重放要拿同一份内容再发一次，见 <see cref="RetainBodyContent"/>）。</summary>
        internal bool RetainBodyContent;

        /// <summary>重放时复用的正文外壳：首次发送后仍持有的 <c>HttpContent</c>，只在重试路径上有值。</summary>
        internal HttpContent? RetainedReplayContent;

        /// <summary>本次请求已解析出的 <c>Authorization</c> 头值，用于显式头冲突校验与重放前比对。</summary>
        internal string? ResolvedAuthorization;

        /// <summary>凭据提供方落点写入的值（头 / 查询参数），进响应缓存的身份键。</summary>
        internal string? ResolvedCredentialIdentity;

        /// <summary>本次请求的凭据提供方（见 <see cref="InternalCredentialProvider"/>），401 时据此触发重取。</summary>
        internal IAxiosCredentialProvider? InternalCredentialProvider;

        /// <summary>按指针浅拷贝。</summary>
        internal AuthReplayState Clone() => (AuthReplayState)MemberwiseClone();
    }

    /// <summary>本次发送收尾不释放模板正文：401 重放要在第二次发送前继续持有调用方给的 <c>HttpContent</c>。</summary>
    /// <remarks>合并层每次请求都会把它置 <c>false</c>，所以写假值不能创建状态容器。</remarks>
    internal bool RetainBodyContent
    {
        get => _authReplay is { RetainBodyContent: true };
        set => SetFlag(ref _authReplay, value, static (state, v) => state.RetainBodyContent = v);
    }

    /// <summary>被发送管线保留下来、供 401 重放使用的正文，由门面读走后负责释放。</summary>
    internal HttpContent? RetainedReplayContent
    {
        get => _authReplay?.RetainedReplayContent;
        set => SetOptional(ref _authReplay, value, static (state, v) => state.RetainedReplayContent = v);
    }

    /// <summary>transform 链里继承自实例默认的前缀长度（库产出的配置才有，调用方 new 的为 0），合并层据此去重；随 <see cref="Clone"/> 继承。</summary>
    internal int TransformInheritedRequest { get; set; }

    /// <summary><see cref="TransformInheritedRequest"/> 的响应侧对应项。</summary>
    internal int TransformInheritedResponse { get; set; }

    /// <summary>是否被显式赋值过（含赋 <c>null</c>），用于合并配置时区分「未设置」与「显式清除」。</summary>
    internal bool AuthSet => _transport?.AuthSet ?? false;

    /// <summary>本次请求由凭据槽位解析出的 <c>Authorization</c> 头值（静态形态或提供方返回值），发送前写入请求头。</summary>
    internal string? ResolvedAuthorization
    {
        get => _authReplay?.ResolvedAuthorization;
        set => SetOptional(ref _authReplay, value, static (state, v) => state.ResolvedAuthorization = v);
    }

    /// <summary>本次请求由托管凭据产生的身份值：落点不是 <c>Authorization</c> 头时用它进缓存身份键。</summary>
    internal string? ResolvedCredentialIdentity
    {
        get => _authReplay?.ResolvedCredentialIdentity;
        set => SetOptional(ref _authReplay, value, static (state, v) => state.ResolvedCredentialIdentity = v);
    }

    /// <summary>门面（WebAPI）挂进来的凭据提供方：不占 <see cref="TransportSettings.Auth"/> 槽位；显式设置的槽位优先。</summary>
    internal IAxiosCredentialProvider? InternalCredentialProvider
    {
        get => _authReplay?.InternalCredentialProvider;
        set => SetOptional(ref _authReplay, value, static (state, v) => state.InternalCredentialProvider = v);
    }

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

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool MaxRateSet => _transport?.MaxRateSet ?? false;

    /// <summary>流式单帧上限的默认值（8 MB），与设为 <c>null</c> 等价。</summary>
    public const int DefaultStreamFrameBytes = 8 * 1024 * 1024;

    /// <summary>流式单帧上限的下界（1 MB）。</summary>
    public const int MinStreamFrameBytes = 1024 * 1024;

    /// <summary>流式单帧上限的上界（64 MB）。</summary>
    public const int MaxStreamFrameBytesLimit = 64 * 1024 * 1024;

    private Func<int, bool>? _validateStatus;

    /// <summary>对应 axios validateStatus，返回 false 时抛出异常；null 表示默认（2xx 合法）。</summary>
    public Func<int, bool>? ValidateStatus
    {
        get => _validateStatus;
        set
        {
            _validateStatus = value;
            ValidateStatusSet = true;
        }
    }

    /// <summary>是否被显式赋值过（含显式 <c>null</c>），合并层用。</summary>
    internal bool ValidateStatusSet { get; private set; }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool CollectionFormatSet => _serialization?.CollectionFormatSet ?? false;

    /// <summary>对应 axios transformRequest：拦截器之后、请求体编码之前依次作用于 <see cref="Body"/>。列表线程安全，枚举走快照。</summary>
    internal bool HasTransformRequest => _serialization?.HasTransformRequest ?? false;

    /// <summary>请求 transform 链的只读快照（不可变数组），调用方不得修改。</summary>
    internal Func<object?, AxiosConfig, object?>[]? TransformRequestSnapshot => _serialization?.TransformRequestSnapshot;

    /// <summary>请求 transform 链长度（合并层记录继承段用）。</summary>
    internal int TransformRequestCount => _serialization?.TransformRequestCount ?? 0;

    /// <summary>是否有响应 transform（不创建集合）。</summary>
    internal bool HasTransformResponse => _serialization?.HasTransformResponse ?? false;

    /// <summary>响应 transform 链的只读快照（响应阶段热路径，同 <see cref="TransformRequestSnapshot"/>）。</summary>
    internal Func<object?, AxiosConfig, object?>[]? TransformResponseSnapshot => _serialization?.TransformResponseSnapshot;

    /// <summary>响应 transform 链长度（合并层记录继承段用）。</summary>
    internal int TransformResponseCount => _serialization?.TransformResponseCount ?? 0;

    /// <summary>响应阶段的只读上下文：状态码、响应头、媒体类型与字符集；请求阶段为 <c>null</c>。</summary>
    public AxiosResponseInfo? Response { get; internal set; }

    /// <summary>深拷贝（Headers / transform 链 / Params / Data 的字典容器独立；其余引用成员按引用共享）。</summary>
    /// <remarks>克隆一份生效配置（如 <see cref="AxiosResponse.Config"/>）后，它仍是「库产出的配置」：回放时实例级项取自目标实例，改 <c>BaseAddress</c> 之类不会生效。</remarks>
    public AxiosConfig Clone()
    {
        var clone = (AxiosConfig)MemberwiseClone();

        // ① 库自有集合共享一版不可变数组；② 调用方容器复制一份；③ 设置分组各自 Clone()。
        clone._headers = _headers?.Clone();
        clone._removeHeaders = _removeHeaders?.ShareIfNotEmpty();

        // 设置分组各自 Clone()。
        clone._serialization = _serialization?.Clone();
        clone._transport = _transport?.Clone();
        clone._limits = _limits?.Clone();
        clone._observability = _observability?.Clone();
        clone._retry = _retry?.Clone();
        clone._circuit = _circuit?.Clone();
        clone._hedging = _hedging?.Clone();
        clone._failover = _failover?.Clone();
        clone._idempotency = _idempotency?.Clone();

        // 并发闸门的懒存储不能与来源共享。
        clone._concurrencyState = _concurrencyState?.Clone();

        // 401 重放 / 凭据解析的懒状态：有则复制一份，随后的清理语句把只属于那一次发送的项归零。
        clone._authReplay = _authReplay?.Clone();

        // 短路 / 缓存的懒状态只复制不共享。
        clone._cacheState = _cacheState?.Clone();

        // 分组 facade 必须重建。
        clone._resilienceFacade = null;
        clone._concurrencyFacade = null;

        // Params / Data 是可变的调用方容器，隔离一份。
        clone._params = IsolateMutableContainer(_params);
        clone._data = IsolateMutableContainer(_data);

        // 响应上下文不随克隆带过去。
        clone.Response = null;

        // 短路响应只属于那一次请求。
        clone.ShortCircuit = null;

        // 缓存是实例级共享状态，随克隆保留。

        // 「要不要留原始字节」属于那一次请求的缓存判定。
        clone.CacheCaptureRawBody = false;

        // 条件请求的旧条目只属于那一次请求。
        clone.RevalidationEntry = null;

        // 已生成的幂等键属于某一次发送。
        if (clone._idempotency is not null)
        {
            clone._idempotency.GeneratedKey = null;
        }

        // Auth 的值已是生效结果，克隆后清掉「显式赋值过」标记。
        if (clone._transport is not null)
        {
            clone._transport.AuthSet = false;
        }

        // 401 重放的持有标记与保留正文都属于那一次发送，克隆出来的是新请求的输入。
        clone.RetainBodyContent = false;
        clone.RetainedReplayContent = null;

        // 门面提供方绑定的是那一次请求的模型实例，克隆是「新请求的输入」。
        clone.InternalCredentialProvider = null;

        // 合并标记属于那一次合并：克隆出来的是新请求的输入，尚未合并。
        if (clone.EffectiveMerged)
        {
            clone.EffectiveMerged = false;

            // 实例级选项的来源信息保留下来。
            clone.InstanceOptionsInherited = true;
        }

        return clone;
    }

    /// <summary>派生一份按尝试隔离的浅拷贝：子设置与集合按引用共享，只把 <see cref="Response"/> 清空。</summary>
    internal AxiosConfig CloneForAttempt()
    {
        var clone = (AxiosConfig)MemberwiseClone();
        clone.Response = null;
        return clone;
    }

    /// <summary>把 <see cref="Params"/> / <see cref="Body"/> 里调用方持有的可变字典复制一份，其余类型（匿名对象 / 集合 / 流 / 字符串）原样共享。</summary>
    /// <remarks>字符串键字典按原类型 + 原 comparer 复制；其余 <see cref="IDictionary{TKey,TValue}"/> 退化成 <see cref="Dictionary{TKey,TValue}"/>。</remarks>
    private static object? IsolateMutableContainer(object? value)
    {
        // 必须在 switch 之前挡掉：null 落到 `_ => value` 分支会被当容器共享。
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
#if NETSTANDARD2_0
            // netstandard2.0 的 ConcurrentDictionary 没有 Comparer 属性；克隆沿用 Ordinal（库内字典的默认比较器）。
            ConcurrentDictionary<string, object?> dictionary => new ConcurrentDictionary<string, object?>(dictionary, StringComparer.Ordinal),
            ConcurrentDictionary<string, string> dictionary => new ConcurrentDictionary<string, string>(dictionary, StringComparer.Ordinal),
#else
            ConcurrentDictionary<string, object?> dictionary => new ConcurrentDictionary<string, object?>(dictionary, dictionary.Comparer),
            ConcurrentDictionary<string, string> dictionary => new ConcurrentDictionary<string, string>(dictionary, dictionary.Comparer),
#endif

            // 其余 IDictionary 实现退化成 Dictionary。
            IDictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary),
            IDictionary<string, string> dictionary => new Dictionary<string, string>(dictionary),
            _ => value
        };
    }

    /// <summary>写引用型懒状态：赋 <c>null</c> 时只清旧值，不创建 holder。</summary>
    /// <typeparam name="THolder">状态容器类型，必须有无参构造。</typeparam>
    /// <typeparam name="TValue">被存的值类型。</typeparam>
    /// <param name="holder">状态容器字段（按引用传入，必要时就地创建）。</param>
    /// <param name="value">要写入的值；<c>null</c> 表示清除。</param>
    /// <param name="assign">把值写进容器的动作，由调用方给出静态 lambda。</param>
    private static void SetOptional<THolder, TValue>(
        ref THolder? holder,
        TValue? value,
        Action<THolder, TValue?> assign)
        where THolder : class, new()
        where TValue : class
    {
        if (value is null)
        {
            if (holder is not null)
            {
                assign(holder, null);
            }

            return;
        }

        holder ??= new THolder();
        assign(holder, value);
    }

    /// <summary>写布尔型懒状态：写「假」时只清旧值，不创建 holder。</summary>
    /// <typeparam name="THolder">状态容器类型，必须有无参构造。</typeparam>
    /// <param name="holder">状态容器字段（按引用传入，必要时就地创建）。</param>
    /// <param name="value">要写入的值；<c>false</c> 表示清除。</param>
    /// <param name="assign">把值写进容器的动作，由调用方给出静态 lambda。</param>
    private static void SetFlag<THolder>(
        ref THolder? holder,
        bool value,
        Action<THolder, bool> assign)
        where THolder : class, new()
    {
        if (!value)
        {
            if (holder is not null)
            {
                assign(holder, false);
            }

            return;
        }

        holder ??= new THolder();
        assign(holder, true);
    }
}
