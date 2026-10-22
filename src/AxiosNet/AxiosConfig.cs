using System.Net;
using System.Text.Json;
using AxiosNet.Internal;
using AxiosNet.Pooling;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>
/// 请求 / 实例配置，字段与 axios 的 config 一一对应。
/// </summary>
public sealed class AxiosConfig
{
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
    /// 对应 axios params，会被序列化为 query string。
    /// <para>
    /// 支持对象 / 字典（含 <c>dynamic</c>）/ 集合（根集合用 <c>[0]=v</c>，嵌套数组 <c>a[]=v</c>，
    /// 嵌套对象 <c>a[b]=v</c>，null 跳过）/ 已序列化好的查询串（<see cref="string"/>，原样拼进 URL）。
    /// </para>
    /// <para>标量无法表达键名，抛 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）而不是静默丢弃。</para>
    /// </summary>
    public object? Params { get; set; }

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
    /// 其余类型交给当前 <see cref="ContentSerializer"/> 编码（XML / YAML 需配套序列化器）。
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

    /// <summary>
    /// 对应 axios data：请求体原始数据。
    /// 对象 / 字典 / dynamic / 集合按 <see cref="ContentType"/> 编码；基础类型直接取文本；
    /// string / byte[] / ReadOnlyMemory&lt;byte&gt; / Stream / HttpContent 原样发送。
    /// </summary>
    public object? Data { get; set; }

    private TimeSpan _timeout;

    /// <summary>
    /// 对应 axios timeout，零表示不超时；按请求显式赋值即可覆盖实例默认值。
    /// <para>
    /// 语义是<b>全程总预算</b>（建连 + 等待响应头 + 读取正文），流式响应同样覆盖到正文读完；
    /// 需要空闲超时请用 <see cref="CancellationToken"/> 自行控制。
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

    /// <summary>对应 axios auth（HTTP Basic）。</summary>
    public AxiosAuth? Auth { get; set; }

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

    /// <summary>对应 axios validateStatus，返回 false 时抛出异常；null 表示默认（2xx 合法）。</summary>
    public Func<int, bool>? ValidateStatus { get; set; }

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

    /// <summary>HTTP 版本，默认 1.1。</summary>
    public Version? HttpVersion { get; set; }

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
    internal bool WithCredentialsSet { get; private set; }

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
    internal bool MaxRedirectsSet { get; private set; }

    private long? _maxContentLength;

    /// <summary>
    /// 对应 axios maxContentLength：响应体最大字节数，null 不限。
    /// 声明了 Content-Length 时在读取正文前拒绝，否则边读边计数，超出抛 <see cref="AxiosException"/>。
    /// 按请求显式设为 null 可清除实例默认上限。
    /// </summary>
    public long? MaxContentLength
    {
        get => _maxContentLength;
        set
        {
            _maxContentLength = value;
            MaxContentLengthSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool MaxContentLengthSet { get; private set; }

    private long? _maxBodyLength;

    /// <summary>
    /// 对应 axios maxBodyLength：请求体最大字节数，null 不限。
    /// 仅在长度可确定时校验（未知长度的 Stream 不校验），超出抛 <see cref="AxiosException"/>。
    /// 按请求显式设为 null 可清除实例默认上限。
    /// </summary>
    public long? MaxBodyLength
    {
        get => _maxBodyLength;
        set
        {
            _maxBodyLength = value;
            MaxBodyLengthSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool MaxBodyLengthSet { get; private set; }

    private long? _maxRate;

    /// <summary>
    /// 限速（字节/秒），null 不限速；同时作用于上传与下载。
    /// 按请求显式设为 null 可清除实例默认限速。
    /// <para>契约：响应流须单线程顺序读取（进度计量非线程安全）。</para>
    /// </summary>
    public long? MaxRate
    {
        get => _maxRate;
        set
        {
            _maxRate = value;
            MaxRateSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool MaxRateSet { get; private set; }

    /// <summary>自定义超时错误消息（对应 axios timeoutMessage），为空时使用默认文案。</summary>
    public string? TimeoutMessage { get; set; }

    /// <summary>
    /// 上传进度回调（对应 axios onUploadProgress）。写入请求体时触发，约 50ms 一次；
    /// 回调在请求线程上同步执行，抛异常会中断请求。
    /// <para>
    /// 重定向（307/308）或重试重发正文时，<see cref="AxiosProgressEvent.Loaded"/> <b>跨次累计</b>
    /// （进度不回跳，百分比封顶 100%）。
    /// </para>
    /// </summary>
    public Action<AxiosProgressEvent>? OnUploadProgress { get; set; }

    /// <summary>
    /// 下载进度回调（对应 axios onDownloadProgress）。读取响应体时触发；
    /// 流式响应（<see cref="ResponseType.Stream"/>）在调用方读取流的过程中触发。
    /// <para>契约：同一请求的响应流须单线程顺序读取（进度计量非线程安全）。</para>
    /// </summary>
    public Action<AxiosProgressEvent>? OnDownloadProgress { get; set; }

    /// <summary>
    /// 对应 axios paramsSerializer：自定义 query string 生成方式，返回不含前导 <c>?</c> 的查询串。
    /// null 时使用内置规则（数组 <c>a[]=v</c>、嵌套 <c>a[b]=v</c>、null 跳过）。
    /// </summary>
    public Func<object?, string>? ParamsSerializer { get; set; }

    /// <summary>
    /// 表单 / multipart 字段名生成规则：参数为（父级前缀, 键名），返回实际字段名。
    /// 默认 <c>prefix[key]</c>（无前缀即 key）；可改成 <c>prefix.key</c> 或忽略前缀。
    /// <para>
    /// 表单不走序列化器，所以这是字段命名的<b>唯一入口</b>（属性级覆盖用 <c>[JsonPropertyName]</c>）：
    /// 与 <see cref="JsonSerializerOptions"/> / <see cref="ContentSerializer"/> 完全无关，换序列化器不影响字段名。
    /// </para>
    /// </summary>
    public Func<string?, string, string>? FormFieldNameSelector { get; set; }

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

    private IList<Func<object?, AxiosConfig, object?>>? _transformRequest;

    /// <summary>是否有请求 transform（不创建集合）。</summary>
    internal bool HasTransformRequest => _transformRequest is { Count: > 0 };

    private IList<Func<object?, AxiosConfig, object?>>? _transformResponse;

    /// <summary>是否有响应 transform（不创建集合）。</summary>
    internal bool HasTransformResponse => _transformResponse is { Count: > 0 };

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
    /// JSON 序列化选项，<b>只对默认 JSON 序列化器生效</b>（请求序列化 / 响应反序列化）。
    /// <para>表单与 multipart 的字段命名与本项无关，见 <see cref="FormFieldNameSelector"/>。</para>
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>
    /// 本次请求（或本实例默认）使用的内容序列化器，<b>优先级最高</b>：
    /// 高于 <see cref="AxiosContent.Serializer"/>，也高于按媒体类型的自动匹配。
    /// <para>null 表示不覆盖，继续走「全局注入 → 按 Content-Type 自动匹配 → 内置 JSON」。</para>
    /// <para>
    /// 只有<b>业务对象</b>经过它：string / byte[] / Stream / HttpContent 请求体原样发送，
    /// 响应侧 string / byte[] / Stream / 基础类型 / dynamic 由库自己处理。
    /// 响应侧走文本还是字节入口，由响应 <c>Content-Type</c> 判定。
    /// </para>
    /// </summary>
    public IContentSerializer? ContentSerializer { get; set; }

    /// <summary>
    /// 请求体文本编码（string / 对象 / 表单），决定写入 socket 的字节与 Content-Type 的 charset。
    /// null 时使用 UTF-8（axios 的默认行为）。byte[] / Stream 原样透传，不受该项影响。
    /// </summary>
    public Encoding? RequestEncoding { get; set; }

    /// <summary>
    /// 响应体文本编码。null 时自动识别（Content-Type charset → BOM → UTF-8）；
    /// 设置后强制按指定编码解码原始字节（用于服务端 charset 缺失/错误的情况）。
    /// </summary>
    public Encoding? ResponseEncoding { get; set; }

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
    /// 连接池选项。仅在构造 AxiosClient（自建池）时生效；
    /// 单次请求上设置会抛 <see cref="AxiosException"/>（池此时已建好）。
    /// </summary>
    public ConnectionPoolOptions? PoolOptions { get; set; }

    /// <summary>深拷贝（Headers / transform 链独立，Params / Data 等引用成员按引用共享）。</summary>
    /// <remarks>
    /// 用 <see cref="object.MemberwiseClone"/> 而非 new + 逐属性赋值：构造函数会创建 4 个集合，
    /// 且各带 <c>*Set</c> 标记的 setter 会把「未显式设置」误置为 true；字段级复制原样保留真实状态。
    /// <para>源没有内容的集合保持 <c>null</c>（读取方一律先用 <c>Has*</c> 判定），避免热路径上的空集合分配。</para>
    /// </remarks>
    public AxiosConfig Clone()
    {
        var clone = (AxiosConfig)MemberwiseClone();

        clone._headers = _headers?.Clone();
        clone._removeHeaders = _removeHeaders is { Count: > 0 }
            ? new SynchronizedList<string>(_removeHeaders)
            : null;
        clone._transformRequest = CopiedTransforms(_transformRequest);
        clone._transformResponse = CopiedTransforms(_transformResponse);

        // 响应上下文属于「某一次响应」：带进下一次请求只会让序列化器读到上一个请求的状态。
        clone.Response = null;

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
    /// 把外部传入的 transform 集合包成线程安全实现（已是则原样复用）。
    /// 代价是 <see cref="TransformRequest"/> / <see cref="TransformResponse"/> 不再返回调用方传入的那个实例，
    /// 换来「无论传入什么集合，运行期增删 + 并发枚举都不抛」的稳定契约。
    /// </summary>
    private static SynchronizedList<Func<object?, AxiosConfig, object?>> WrapTransforms(
        IList<Func<object?, AxiosConfig, object?>> values)
        => values as SynchronizedList<Func<object?, AxiosConfig, object?>>
            ?? new SynchronizedList<Func<object?, AxiosConfig, object?>>(values);

    /// <summary>transform 链的独立副本；空链返回 <c>null</c>（调用方只在有内容时才创建）。</summary>
    private static IList<Func<object?, AxiosConfig, object?>>? CopiedTransforms(
        IList<Func<object?, AxiosConfig, object?>>? source)
        => source is { Count: > 0 }
            ? new SynchronizedList<Func<object?, AxiosConfig, object?>>(source)
            : null;
}
