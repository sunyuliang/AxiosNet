using AxiosNet.Internal;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>
/// 请求 / 实例配置，字段与 axios 的 config 一一对应。
/// </summary>
public sealed partial class AxiosConfig
{
    // 按领域分组的可选设置：平时不用的功能项不占对象本体，首次写入才分配，Clone 时按需复制。
    private SerializationSettings? _serialization;
    private ConnectionSettings? _connection;
    private LimitSettings? _limits;
    private CallbackSettings? _callbacks;
    private RetrySettings? _retry;
    private CircuitSettings? _circuit;
    private TelemetrySettings? _telemetry;

    /// <summary>
    /// 序列化相关设置：JSON 选项、内容序列化器、文本编码、查询串与表单字段命名。
    /// <para>首次读取时懒创建；不写入任何项时不分配对象。</para>
    /// </summary>
    public SerializationSettings Serialization
    {
        get => _serialization ??= new SerializationSettings();
        set => _serialization = value;
    }

    /// <summary>
    /// 连接相关设置：代理 / 凭据 / 连接池选项 / HTTP 版本与协商策略 / Basic 认证 / 凭据开关 / 重定向次数。
    /// <para>首次读取时懒创建；不写入任何项时不分配对象。</para>
    /// </summary>
    public ConnectionSettings Connection
    {
        get => _connection ??= new ConnectionSettings();
        set => _connection = value;
    }

    /// <summary>
    /// 体积、速率、在途并发上限、流式超时与流式单帧上限。
    /// <para>首次读取时懒创建；不写入任何项时不分配对象。</para>
    /// </summary>
    public LimitSettings Limits
    {
        get => _limits ??= new LimitSettings();
        set => _limits = value;
    }

    /// <summary>
    /// 进度回调、状态校验与超时文案。
    /// <para>首次读取时懒创建；不写入任何项时不分配对象。</para>
    /// </summary>
    public CallbackSettings Callbacks
    {
        get => _callbacks ??= new CallbackSettings();
        set => _callbacks = value;
    }

    /// <summary>
    /// 弹性重试：策略与每次重试前的回调。<b>默认不重试</b>（不设置 <see cref="RetrySettings.Policy"/> 即可）。
    /// <para>首次读取时懒创建；不写入任何项时不分配对象。</para>
    /// </summary>
    public RetrySettings Retry
    {
        get => _retry ??= new RetrySettings();
        set => _retry = value;
    }

    /// <summary>
    /// 熔断：策略与被拒绝时的回调。<b>默认不熔断</b>（不设置 <see cref="CircuitSettings.Policy"/> 即可）。
    /// <para>首次读取时懒创建；不写入任何项时不分配对象。</para>
    /// </summary>
    public CircuitSettings Circuit
    {
        get => _circuit ??= new CircuitSettings();
        set => _circuit = value;
    }

    /// <summary>
    /// 可观测性：链路 / 指标 / 事件三路观测点的按请求开关（<see cref="TelemetrySettings.Enabled"/>）与
    /// URL 采集策略（<see cref="TelemetrySettings.CaptureUrl"/>）。
    /// <para>首次读取时懒创建；不写入任何项时不分配对象。</para>
    /// </summary>
    public TelemetrySettings Telemetry
    {
        get => _telemetry ??= new TelemetrySettings();
        set => _telemetry = value;
    }

    // 库内读取一律走这几个访问器：公共 getter 会懒创建，热路径上每次请求都建对象。
    internal SerializationSettings? SerializationOrNull => _serialization;

    internal ConnectionSettings? ConnectionOrNull => _connection;

    internal LimitSettings? LimitsOrNull => _limits;

    internal CallbackSettings? CallbacksOrNull => _callbacks;

    internal RetrySettings? RetryOrNull => _retry;

    internal CircuitSettings? CircuitOrNull => _circuit;

    internal TelemetrySettings? TelemetryOrNull => _telemetry;

    /// <summary>对应 axios baseURL。</summary>
    public string? BaseAddress { get; set; }

    /// <summary>对应 axios url（相对或绝对地址）。</summary>
    public string? Url { get; set; }

    private HttpMethod _method = HttpMethod.Get;

    /// <summary>
    /// 对应 axios method，默认 GET。直接使用 BCL 的 <see cref="HttpMethod"/>，
    /// 因此 <c>HttpMethod.Patch</c>、<c>new HttpMethod("PROPFIND")</c> 都能直接用。
    /// </summary>
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

    // 集合成员延迟创建：快捷方法每请求都 new 一个 AxiosConfig，而绝大多数字段是空的。
    // 字段为 null 表示「还没有内容」，首次读写才创建；库内读路径一律先用 Has* 判定。
    private AxiosHeaders? _headers;

    /// <summary>
    /// 对应 axios headers（大小写不敏感）。
    /// <para>
    /// 序列化器也可在 <see cref="IContentSerializer.Serialize"/> 里写它：拿到的 config 是本次请求独占的副本，
    /// 写入的头在正文构造完成后统一应用（可覆盖 Content-Type 的默认值），不会泄漏到并发请求。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">赋值为 <c>null</c>（在赋值处拒绝，避免管线深处抛空引用）。</exception>
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

    /// <summary>
    /// 按请求要删除的 Header 名（大小写不敏感）：在 defaults 的头的合并完成之后生效，
    /// 用于摘掉实例默认头（合并语义下没法用「设为 null」表达删除）。
    /// <para>线程安全：可在请求进行中增删，枚举走快照。</para>
    /// </summary>
    /// <remarks>
    /// 手写属性 + 私有字段而非自动属性：<see cref="Clone"/> 用 MemberwiseClone 之后必须把集合成员换成独立副本。
    /// </remarks>
    public ICollection<string> RemoveHeaders => _removeHeaders ??= new SynchronizedList<string>();

    /// <summary>是否有要摘除的头（不创建集合）。</summary>
    internal bool HasRemoveHeaders => _removeHeaders is { Count: > 0 };

    /// <summary>
    /// 删头名单的只读快照（不可变数组，<c>foreach</c> 零分配）。
    /// <para>库内热路径用它代替「直接枚举 <see cref="RemoveHeaders"/>」：后者是 <c>ICollection&lt;string&gt;</c>，
    /// 走接口会为枚举器装箱（每次 32 B）。</para>
    /// </summary>
    internal string[]? RemoveHeadersSnapshot
        => _removeHeaders is { Count: > 0 } ? _removeHeaders.Snapshot() : null;

    private object? _params;

    /// <summary>
    /// 对应 axios params，会被序列化为 query string。
    /// <para>
    /// 支持对象 / 字典（含 <c>dynamic</c>）/ 集合（根集合用 <c>[0]=v</c>，嵌套数组 <c>a[]=v</c>，
    /// 嵌套对象 <c>a[b]=v</c>，null 跳过）/ 已序列化好的查询串（<see cref="string"/>，原样拼进 URL）。
    /// </para>
    /// <para>标量无法表达键名，抛 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）而不是静默丢弃。</para>
    /// </summary>
    public object? Params
    {
        get => _params;
        set
        {
            _params = value;
            ParamsSet = true;
        }
    }

    /// <summary>
    /// 是否被显式赋值过（包括赋 <c>null</c>）。
    /// <para>合并时用它区分「没写」与「写了 null」：后者用于摘掉实例默认值。</para>
    /// </summary>
    internal bool ParamsSet { get; private set; }

    /// <summary>库默认的请求体媒体类型（等价于 <see cref="AxiosContentType.Json"/>）。</summary>
    public const string JsonContentType = AxiosContentType.Json;

    /// <summary>表单编码媒体类型常量（等价于 <see cref="AxiosContentType.FormUrlEncoded"/>，配合 <see cref="ContentType"/> 使用）。</summary>
    public const string FormUrlEncodedContentType = AxiosContentType.FormUrlEncoded;

    private string _contentType = JsonContentType;

    /// <summary>
    /// 请求体媒体类型（Content-Type），默认 <c>application/json</c>。
    /// <para>
    /// 编码格式由本项决定，<b>与 <see cref="Data"/> 的运行时类型无关</b>：
    /// <c>application/x-www-form-urlencoded</c> 转键值对表单，<c>multipart/form-data</c> 构建字段 + 文件部件，
    /// 其余类型交给当前 <see cref="SerializationSettings.ContentSerializer"/> 编码（XML / YAML 需配套序列化器）。
    /// </para>
    /// <para>
    /// 不设置本项时头由序列化器自报（见 <see cref="IContentSerializer.MediaType"/>），
    /// 换序列化器即可换格式；显式设置过则以本项为准。
    /// </para>
    /// <para>string / byte[] / Stream / HttpContent 原样发送，本项只影响头部；二进制载荷请显式设为 octet-stream。</para>
    /// </summary>
    public string ContentType
    {
        get => _contentType;
        set
        {
            // 非 null 引用类型：赋 null 是编程错误，不能静默变成「显式设置 application/json」
            // —— 那会把序列化器自报的 MediaType 压掉（合并层只认 ContentTypeSet）
            ArgumentNullException.ThrowIfNull(value);

            var trimmed = value.Trim();

            if (trimmed.Length == 0)
            {
                throw new ArgumentException(
                    "ContentType 不能为空白；不设置时头由序列化器自报（见 IContentSerializer.MediaType），请不要赋值。",
                    nameof(value));
            }

            // 注入的第一道闸：这个值最终经 TryAddWithoutValidation 写进 Content-Type，
            // 而 .NET 该 API 不拦 CR/LF（已抓包证实能注入任意额外请求头）。
            // 与 AxiosHeaders 用同一套校验，避免「头集合拦了、ContentType 没拦」这种缺口。
            AxiosHeaders.ValidateHeaderValue("Content-Type", trimmed);

            _contentType = trimmed;
            ContentTypeSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool ContentTypeSet { get; private set; }

    private object? _data;

    /// <summary>
    /// 对应 axios data：请求体原始数据。
    /// 对象 / 字典 / dynamic / 集合按 <see cref="ContentType"/> 编码；基础类型直接取文本；
    /// string / byte[] / ReadOnlyMemory&lt;byte&gt; / Stream / HttpContent 原样发送。
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

    /// <summary>
    /// 是否被显式赋值过（包括赋 <c>null</c>）。
    /// <para>合并时用它区分「没写」与「写了 null」：后者用于摘掉实例默认值。</para>
    /// </summary>
    internal bool DataSet { get; private set; }

    /// <summary>
    /// 短路响应：设置后本次请求<b>不发送</b>，直接以这份结果作为响应（细节见 <see cref="AxiosNet.AxiosShortCircuit"/>）。
    /// <para>
    /// 这是给「请求拦截器里查缓存并命中」准备的出口：拦截器只能改配置、无法直接返回响应，把本项填上，
    /// 管线会在拦截器之后、编码与发送之前转去交付它 —— 闸门 / 连接 / 重试 / 熔断 / 遥测都不参与，
    /// 但反序列化、transformResponse、状态码校验与响应拦截器照常执行。
    /// </para>
    /// <para>
    /// <b>只属于本次请求</b>：<see cref="Clone"/> 不复制它，因此放在 <see cref="AxiosClient.Defaults"/> 上不生效，
    /// 回放 <see cref="AxiosResponse.Config"/> 也不会重放上一次的短路结果（否则那条请求会永远不发出去）。
    /// </para>
    /// </summary>
    public AxiosShortCircuit? ShortCircuit { get; set; }

    private TimeSpan _timeout;

    /// <summary>
    /// 对应 axios timeout，零表示不超时；按请求显式赋值即可覆盖实例默认值。
    /// <para>
    /// 语义是<b>全程总预算</b>（建连 + 等待响应头 + 读取正文），流式响应同样覆盖到正文读完；
    /// 需要空闲超时请用 <see cref="CancellationToken"/> 自行控制。
    /// </para>
    /// <para>
    /// 例外：设置了 <see cref="LimitSettings.StreamIdleTimeout"/> 时，流式正文不再计入本预算
    /// （本项只覆盖到响应头），正文改按空闲超时或完全不限时。
    /// </para>
    /// <para>
    /// 超过计时器上限（约 49.7 天）会收敛到上限而不抛异常；负值非法，抛
    /// <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）。
    /// </para>
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

    /// <summary>
    /// 这份配置是否<b>已经是合并后的生效配置</b>（快捷方法在实例默认值的副本上填写），管线据此跳过二次合并。
    /// <para>
    /// 标记只在管线内部有效，<see cref="Clone"/> 不继承它：调用方拿回来再交回去的配置（回放
    /// <see cref="AxiosResponse.Config"/>）一律重新合并，否则会把上一个实例的生效值当成按请求项。
    /// </para>
    /// </summary>
    internal bool EffectiveMerged { get; set; }

    /// <summary>
    /// 这份配置里的<b>实例级选项</b>（WithCredentials / MaxRedirects / Proxy / Credentials / PoolOptions）
    /// 是某个实例的生效值，不是按请求写的意图，合并时一律沿用本实例的默认值。
    /// <para>
    /// 只有库产出的配置才有这个标记（快捷方法与 <c>MergeConfig</c> 的结果），调用方 new 出来的没有，
    /// 所以按请求设置这些项照样会被拦下。与 <see cref="EffectiveMerged"/> 不同，它随 <see cref="Clone"/> 继承。
    /// </para>
    /// </summary>
    internal bool InstanceOptionsInherited { get; set; }

    /// <summary>
    /// transform 链里「继承自实例默认」的前缀长度（库产出配置才有；调用方 new 的为 0）。
    /// <para>
    /// 合并层据此只对前 <see cref="TransformInheritedRequest"/> / <see cref="TransformInheritedResponse"/>
    /// 项去重：回放 response.Config 时不重复执行实例默认 transform，而本次追加的项
    /// （含与默认链同引用的有意重复注册）保持原样。随 <see cref="Clone"/> 继承。
    /// </para>
    /// </summary>
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

    /// <summary>
    /// 管线内部的 responseType 自动切换（泛型 <c>&lt;object&gt;</c> / <c>&lt;Stream&gt;</c>）：
    /// 只改值、<b>不置</b> <see cref="ResponseTypeSet"/>。自动切换不是调用方意图，
    /// 置了标记会让回放 <see cref="AxiosResponse.Config"/> 时把它当成显式值合并进下一次请求。
    /// </summary>
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
    internal bool StreamIdleTimeoutSet => _limits?.StreamIdleTimeoutSet ?? false;

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool CollectionFormatSet => _serialization?.CollectionFormatSet ?? false;

    /// <summary>
    /// 对应 axios transformRequest：拦截器之后、请求体编码之前依次作用于 <see cref="Data"/>，
    /// 每个函数返回新的数据（可返回 string / byte[] / Stream / HttpContent 等）。
    /// <para>列表线程安全：可在请求进行中追加/移除，枚举走快照；整体赋值任意 <see cref="IList{T}"/> 也会被包装成同一实现。</para>
    /// </summary>
    public IList<Func<object?, AxiosConfig, object?>> TransformRequest
    {
        get => _transformRequest ??= new SynchronizedList<Func<object?, AxiosConfig, object?>>();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            // 整体替换时同样保证线程安全：直接存 List<T> 会让「运行期可增删、枚举走快照」的承诺
            // 变成调用方传入类型决定的行为（List 在并发枚举时抛 InvalidOperationException）。
            _transformRequest = WrapTransforms(value);
        }
    }

    private SynchronizedList<Func<object?, AxiosConfig, object?>>? _transformRequest;

    /// <summary>是否有请求 transform（不创建集合）。</summary>
    internal bool HasTransformRequest => _transformRequest is { Count: > 0 };

    /// <summary>
    /// 请求 transform 链的只读快照（管线热路径：不可变数组，<c>foreach</c> 走结构体枚举器、零分配）。
    /// <para>调用方不得修改返回的数组。</para>
    /// </summary>
    internal Func<object?, AxiosConfig, object?>[]? TransformRequestSnapshot
        => _transformRequest is { Count: > 0 } ? _transformRequest.Snapshot() : null;

    private SynchronizedList<Func<object?, AxiosConfig, object?>>? _transformResponse;

    /// <summary>是否有响应 transform（不创建集合）。</summary>
    internal bool HasTransformResponse => _transformResponse is { Count: > 0 };

    /// <summary>响应 transform 链的只读快照（响应阶段热路径，同 <see cref="TransformRequestSnapshot"/>）。</summary>
    internal Func<object?, AxiosConfig, object?>[]? TransformResponseSnapshot
        => _transformResponse is { Count: > 0 } ? _transformResponse.Snapshot() : null;

    /// <summary>
    /// 对应 axios transformResponse：响应体转换/反序列化之后依次作用于 Data
    /// （例如统一拆包 <c>{code,data}</c>）。流式响应不适用。
    /// <para>可读 <see cref="Response"/> 拿响应头（如把 <c>X-Total-Count</c> 贴到分页对象上）——
    /// 此时解析已完成，适合「加工已造好的对象」，而不是「决定怎么解析」。</para>
    /// <para>列表线程安全：可在请求进行中追加/移除，枚举走快照；整体赋值任意 <see cref="IList{T}"/> 也会被包装成同一实现。</para>
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
    /// 响应阶段的只读上下文：状态码、响应头、媒体类型与字符集。
    /// <para>
    /// <b>只在响应方向有值</b>：请求阶段（<see cref="IContentSerializer.Serialize"/>）读到的是 <c>null</c>；
    /// 反序列化与 <see cref="TransformResponse"/> 可据此读响应头（如 <c>X-Schema-Version</c>、<c>X-Total-Count</c>）。
    /// </para>
    /// <para>
    /// 由库写入，调用方只读；<see cref="Headers"/> 与 <see cref="AxiosResponse.Headers"/> 是同一份字典。
    /// <see cref="Clone"/> 不复制它，否则上一个响应的头会被带进下一次请求。
    /// </para>
    /// </summary>
    public AxiosResponseInfo? Response { get; internal set; }

    /// <summary>
    /// 清掉<b>实例级选项</b>的「显式赋值过」标记。
    /// <para>
    /// 合并起点是 <c>Defaults.Clone()</c>，克隆会把这些标记一起带过来；而校验层（
    /// <c>ValidatePerRequestOptions</c>）只看标记 —— 不清理的话「构造之后改 Defaults」
    /// 会被当成「本次按请求写了不同的值」，每个请求都抛错（文档承诺的是静默不生效）。
    /// </para>
    /// </summary>
    internal void ClearInstanceOptionFlags()
    {
        if (_connection is not null)
        {
            _connection.WithCredentialsSet = false;
            _connection.MaxRedirectsSet = false;
        }
    }

    /// <summary>深拷贝（Headers / transform 链 / Params / Data 的字典容器独立；其余引用成员按引用共享）。</summary>
    /// <remarks>
    /// 用 <see cref="object.MemberwiseClone"/> 而非 new + 逐属性赋值：构造函数会创建 4 个集合，
    /// 且各带 <c>*Set</c> 标记的 setter 会把「未显式设置」误置为 true；字段级复制原样保留真实状态。
    /// <para>源没有内容的集合保持 <c>null</c>（读取方一律先用 <c>Has*</c> 判定），避免热路径上的空集合分配。</para>
    /// </remarks>
    public AxiosConfig Clone()
    {
        var clone = (AxiosConfig)MemberwiseClone();

        clone._headers = _headers?.Clone();

        // transform 链与删头名单走写时复制共享（见 SynchronizedList.ShareIfNotEmpty）：只读用法零拷贝，
        // 任意一方写入都发布自己的新数组。此前每请求都整份拷贝，实例配了 transform 时白付一次全量复制。
        // 空链在克隆侧保持 null（ShareIfNotEmpty 读的是同一版不可变数组，不存在「判空后被清空」的窗口）。
        clone._removeHeaders = _removeHeaders?.ShareIfNotEmpty();
        clone._transformRequest = _transformRequest?.ShareIfNotEmpty();
        clone._transformResponse = _transformResponse?.ShareIfNotEmpty();
        clone._serialization = _serialization?.Clone();
        clone._connection = _connection?.Clone();
        clone._limits = _limits?.Clone();
        clone._callbacks = _callbacks?.Clone();
        clone._retry = _retry?.Clone();
        clone._circuit = _circuit?.Clone();
        clone._telemetry = _telemetry?.Clone();

        // Params / Data 是引用成员：字典最容易被就地修改（请求拦截器往 Params 里加一项是常见写法），
        // 与实例默认值共享同一份会污染后续所有请求。这里只复制容器，元素仍按引用共享（不深拷贝业务对象）。
        clone._params = CloneContainer(_params);
        clone._data = CloneContainer(_data);

        // 响应上下文属于「某一次响应」：带进下一次请求只会让序列化器读到上一个请求的状态。
        clone.Response = null;

        // 短路响应同属「那一次请求」：跟着克隆走，会让下一次请求不发出去就返回旧结果
        // （回放 response.Config 是最常见的触发方式）；作为实例默认值也没有意义。
        clone.ShortCircuit = null;

        // 「显式赋过 Auth」属于那一次赋值，不属于「这份配置的内容」：
        // 回放 response.Config（或任何克隆）时，Auth 的值已是生效结果，不能再被当成按请求意图 ——
        // 目标实例用自己的默认凭据（与 Proxy / BaseAddress 的 instanceSourced 门禁同构，
        // 契约由 Audit20261024P2FixTests 锁定）。
        if (clone._connection is not null)
        {
            clone._connection.AuthSet = false;
        }

        // 合并标记属于「那一次合并」：克隆出来的是新请求的输入，尚未合并。
        if (clone.EffectiveMerged)
        {
            clone.EffectiveMerged = false;

            // 但「这些实例级选项是实例的值」这条信息必须留下：否则合并时会把源实例的
            // Proxy/PoolOptions 搬进本实例，既用错连接池，又被判成非法按请求项。
            clone.InstanceOptionsInherited = true;
        }

        return clone;
    }

    /// <summary>
    /// <see cref="Params"/> / <see cref="Data"/> 的容器级复制：只认最容易被就地修改且语义明确的字典，
    /// 其余类型（匿名对象 / 集合 / 流 / 字符串）原样共享 —— 深拷贝任意业务对象既不可靠，也不该在配置克隆里做。
    /// </summary>
    private static object? CloneContainer(object? value) => value switch
    {
        Dictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary, dictionary.Comparer),
        IDictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary),
        Dictionary<string, string> dictionary => new Dictionary<string, string>(dictionary, dictionary.Comparer),
        IDictionary<string, string> dictionary => new Dictionary<string, string>(dictionary),
        _ => value
    };

    /// <summary>
    /// 把外部传入的 transform 集合包成线程安全实现（已是则原样复用）。
    /// 代价是 <see cref="TransformRequest"/> / <see cref="TransformResponse"/> 不再返回调用方传入的那个实例，
    /// 换来「无论传入什么集合，运行期增删 + 并发枚举都不抛」的稳定契约。
    /// </summary>
    private static SynchronizedList<Func<object?, AxiosConfig, object?>> WrapTransforms(
        IList<Func<object?, AxiosConfig, object?>> values)
        => values as SynchronizedList<Func<object?, AxiosConfig, object?>>
            ?? new SynchronizedList<Func<object?, AxiosConfig, object?>>(values);
}
