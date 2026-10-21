using System.Net;
using System.Text.Json;
using AxiosNet.Internal;
using AxiosNet.Pooling;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>
/// 请求/实例配置，字段与 axios 的 config 一一对应。
/// </summary>
public sealed class AxiosConfig
{
    /// <summary>对应 axios baseURL。</summary>
    public string? BaseAddress { get; set; }

    /// <summary>对应 axios url（相对或绝对地址）。</summary>
    public string? Url { get; set; }

    private HttpMethod _method = HttpMethod.Get;

    /// <summary>
    /// 对应 axios method，默认 <see cref="HttpMethod.Get"/>。
    /// 直接使用 BCL 的 <see cref="System.Net.Http.HttpMethod"/>，因此
    /// <c>HttpMethod.Patch</c>、<c>new HttpMethod("PROPFIND")</c> 等自定义方法都能直接用。
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

    // 集合成员延迟创建：快捷方法每请求都会 new 一个 AxiosConfig，而绝大多数字段是空的。
    // 构造期直接 new 出 Headers + 3 个列表是每请求 ~700 B 的白分配（Clone 里还有一份，
    // 合计约 1.2 KB）——热路径上比绝大多数真实工作都贵。字段保留 null 表示「还没有内容」，
    // 只有第一次访问/写入才真正创建；库内读路径一律先用 Has* 判定，不为「看一眼」付创建成本。
    private AxiosHeaders? _headers;

    /// <summary>
    /// 对应 axios headers（大小写不敏感）。
    /// <para>
    /// 序列化器在 <see cref="IContentSerializer.Serialize"/> 里也可以写它：拿到的 config 是本次请求
    /// 独占的副本，写入的头在请求体构造完成后统一应用（能覆盖 <see cref="ContentType"/> 生成的默认头），
    /// 不会泄漏到实例默认值或并发请求。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException">赋值为 <c>null</c>。</exception>
    /// <remarks>
    /// 赋 null 会让后续的配置合并（<c>perCall.Headers.Count</c>）在请求管线深处抛空引用，
    /// 报错点与真正的错误位置（这一行赋值）相隔很远；这里在赋值处就拒绝。
    /// </remarks>
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
    /// 按请求要删除的 Header 名（大小写不敏感）：在 defaults 的头合并完成之后生效，
    /// 用于摘掉实例默认头（合并语义下没法用「设为 null」表达删除）。
    /// <para>线程安全：可在请求进行中增删，枚举走快照（与 Headers 的并发语义一致）。</para>
    /// </summary>
    /// <remarks>
    /// 手写属性 + 私有字段而不是自动属性：<see cref="Clone"/> 用 MemberwiseClone 之后
    /// 必须把集合成员换成独立副本，自动属性的编译体 backing field 在类外不可赋值。
    /// </remarks>
    public ICollection<string> RemoveHeaders => _removeHeaders ??= new SynchronizedList<string>();

    /// <summary>是否有要摘除的头（不创建集合）。</summary>
    internal bool HasRemoveHeaders => _removeHeaders is { Count: > 0 };

    /// <summary>
    /// 对应 axios params，会被序列化为 query string。
    /// <para>
    /// 支持：对象 / 字典（含 <c>dynamic</c>）/ 集合（根集合用 <c>[0]=v</c> 下标，
    /// 嵌套数组用 <c>a[]=v</c>、嵌套对象用 <c>a[b]=v</c>，null 跳过）/
    /// 已经序列化好的查询串（<see cref="string"/>，如 <c>"a=1&amp;b=2"</c>，原样拼进 URL）。
    /// </para>
    /// <para>
    /// 标量（<see cref="int"/> / <see cref="bool"/> / <see cref="DateTime"/> …）无法表达键名，
    /// 会抛 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）而不是被静默丢弃。
    /// </para>
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
    /// 请求体的编码格式由本项决定，<b>与 <see cref="Data"/> 的运行时类型无关</b>：
    /// 设为 <c>application/x-www-form-urlencoded</c> 时转为键值对表单（嵌套用 <c>a[b]=v</c>、数组用 <c>a[]=v</c>）；
    /// 设为 <c>multipart/form-data</c> 时构建字段 + 文件部件（文件用 <see cref="FormFile"/> / Stream / byte[] / HttpContent 表示）；
    /// 其余媒体类型下，结构化数据交给当前 <see cref="ContentSerializer"/>（默认 JSON）编码。
    /// 也就是说 <c>application/xml</c> / <c>application/yaml</c> 要配合对应的序列化器使用 ——
    /// 只改本项而用默认 JSON 序列化器，会得到「头部 XML、正文 JSON」的自相矛盾请求。
    /// </para>
    /// <para>
    /// 反过来，<b>不设置本项</b>时头由序列化器自报：若生效的 <see cref="IContentSerializer"/>
    /// 声明了 <see cref="IContentSerializer.MediaType"/>（内置 JSON 是 <c>application/json</c>、
    /// 内置 XML 是 <c>application/xml</c>），请求头就用它。所以
    /// <c>ContentSerializer = XmlContentSerializer.Instance</c> 一处改动即可完成换格式。
    /// 一旦显式设置过本项（单次或实例 <c>Defaults</c>），序列化器的声明不再生效。
    /// </para>
    /// <para>
    /// 传入 string / byte[] / Stream / HttpContent 时本项只影响头部：内容原样发送。
    /// 注意默认值是 json，二进制载荷（byte[]/Stream）请显式设为 <c>application/octet-stream</c>。
    /// </para>
    /// </summary>
    public string ContentType
    {
        get => _contentType;
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                _contentType = JsonContentType;
                ContentTypeSet = true;
                return;
            }

            var trimmed = value.Trim();

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
    /// 对应 axios data：请求体原始数据，支持任意类型 ——
    /// 对象/字典/dynamic/集合（按 <see cref="ContentType"/> 序列化，字典与对象同一套逻辑），
    /// 基础数据类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…，直接取其文本，不经过序列化器），
    /// 以及 string / byte[] / ReadOnlyMemory&lt;byte&gt; / Stream / HttpContent（原样发送）。
    /// </summary>
    public object? Data { get; set; }

    private TimeSpan _timeout;

    /// <summary>
    /// 对应 axios timeout，零表示不超时。
    /// <para>
    /// 按请求设置时<b>显式赋值即可覆盖实例默认值</b>，包括用 <see cref="TimeSpan.Zero"/>
    /// 关闭实例级超时（未赋值则沿用实例默认）。
    /// </para>
    /// <para>
    /// 语义是<b>全程总预算</b>（建连 + 等待响应头 + 读取正文），不是单次读写的空闲超时：
    /// 流式响应（<see cref="ResponseType.Stream"/>）下同样覆盖到正文读取结束——
    /// 响应头阶段耗时也会从正文的剩余预算里扣除。需要"长连接式"的空闲超时请用
    /// <see cref="CancellationToken"/> 自行控制。
    /// </para>
    /// <para>
    /// 底层计时器的上限约为 49.7 天（<c>uint.MaxValue-1</c> 毫秒）：超过这个值会被<b>收敛到上限</b>，
    /// 不会抛异常。需要真正的无限超时请用 <see cref="TimeSpan.Zero"/>。负值非法，会抛
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
    /// 这份配置是否<b>已经是合并后的生效配置</b>（由快捷方法在实例默认值的副本上直接填写而来，
    /// 见 <c>AxiosClient.Shortcut</c>）。管线据此跳过二次合并。
    /// <para>
    /// 标记只在<b>管线内部</b>有效：<see cref="Clone"/> 不继承它（与 <see cref="Response"/> 同理），
    /// 所以调用方拿到手、再交回来的配置（典型是回放 <see cref="AxiosResponse.Config"/>）一律
    /// 走「按请求项合并到实例默认值」的常规路径 —— 否则它会把上一个实例的生效值当成按请求项，
    /// 静默跳过新实例的默认值（连 <c>Proxy</c>/<c>PoolOptions</c> 这类实例级项都会被误判成
    /// 「按请求设置」而错报）。
    /// </para>
    /// </summary>
    internal bool EffectiveMerged { get; set; }

    /// <summary>
    /// 这份配置里的<b>实例级选项</b>（<see cref="WithCredentials"/> / <see cref="MaxRedirects"/> /
    /// <see cref="Proxy"/> / <see cref="Credentials"/> / <see cref="PoolOptions"/>）是「某个实例的生效值」，
    /// 不是本次按请求写的意图。合并时一律沿用本实例的默认值。
    /// <para>
    /// 只有库自己产出的配置才有这个标记：快捷方法在 <see cref="AxiosClient.Defaults"/> 副本上填写，
    /// 合并结果（<c>AxiosClient.MergeConfig</c>）也是从实例默认值拷出来的。调用方 <c>new</c> 出来的配置
    /// 没有它，所以在按请求设置这些项时照样会被拦下（<c>ERR_BAD_OPTION_VALUE</c>）。
    /// </para>
    /// <para>
    /// 与 <see cref="EffectiveMerged"/> 的区别：那个标记表示「已经合并完，管线别再合并」，用一次就清；
    /// 这个标记表示「值的来源是实例」，会随 <see cref="Clone"/> 一起继承下去 —— 否则回放
    /// <see cref="AxiosResponse.Config"/> 到另一个实例时，源实例的池/代理会被当成按请求项搬过去
    /// （用错连接池，且被校验判成非法按请求项而错报）。
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
    /// 对应 axios withCredentials（使用系统默认凭据/Cookie）。
    /// 仅实例级生效：在构造 AxiosClient 时应用到连接池 Handler；
    /// 按请求写<b>与实例生效值不同</b>的值会抛 <see cref="AxiosException"/>（写相同的值等价于不写，放行）。
    /// 若注入了自定义 Handler，本项需由 Handler 自己处理（在 defaults 上设置会抛 <see cref="AxiosException"/>）。
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
    /// 对应 axios maxRedirects，默认 5；<b>设为 0 表示不跟随重定向</b>（返回 3xx 响应本身，默认 validateStatus 会视为错误）。
    /// 仅实例级生效：在构造 AxiosClient 时应用到连接池 Handler；
    /// 按请求写<b>与实例生效值不同</b>的值会抛 <see cref="AxiosException"/>（写相同的值等价于不写，放行）。
    /// 注入自定义 Handler 时由 Handler 决定（在 defaults 上设置会抛 <see cref="AxiosException"/>）。
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
    /// 对应 axios maxContentLength：响应体最大字节数，null 表示不限。
    /// 超出时抛 <see cref="AxiosException"/>（<see cref="AxiosException.ErrBadResponse"/>）；
    /// 服务端声明了 Content-Length 时在读取正文前就拒绝，否则边读边计数。
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
    /// 对应 axios maxBodyLength：请求体最大字节数，null 表示不限。
    /// 仅在长度可确定时校验（对象/字符串/byte[] 等；未知长度的 Stream 不校验），
    /// 超出时抛 <see cref="AxiosException"/>（<see cref="AxiosException.ErrBadRequest"/>）。
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
    /// 限速（字节/秒），null 表示不限速。同时作用于上传与下载（与进度回调共用同一套流量包装）。
    /// 按请求显式设为 null 可清除实例默认限速。
    /// <para>契约：同一请求的响应流须单线程顺序读取（进度计量非线程安全）。</para>
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
    /// 上传进度回调（对应 axios onUploadProgress）。写入请求体时按已发送字节触发，约 50ms 一次。
    /// 回调在请求线程上同步执行，避免阻塞；抛异常会中断请求。
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
    /// 表单/multipart 字段名生成规则：参数为（父级前缀, 键名），返回实际字段名。
    /// 默认 <c>prefix[key]</c>（无前缀时就是 key）；可改成 <c>prefix.key</c>、下划线或忽略前缀。
    /// <para>
    /// 这是字段命名的<b>唯一入口</b>（属性级覆盖用 <c>[JsonPropertyName]</c>，忽略用 <c>[JsonIgnore]</c>）：
    /// 表单不走序列化器，所以与 <see cref="JsonSerializerOptions"/> / <see cref="ContentSerializer"/> 完全无关 ——
    /// 换 XML、YAML 序列化器不会改变表单字段名。
    /// </para>
    /// </summary>
    public Func<string?, string, string>? FormFieldNameSelector { get; set; }

    /// <summary>
    /// 对应 axios transformRequest：在拦截器之后、请求体编码之前依次作用于 <see cref="Data"/>，
    /// 每个函数返回新的数据（可返回 string/byte[]/Stream/HttpContent 等）。
    /// <para>列表线程安全：可在请求进行中追加/移除，枚举走快照。</para>
    /// </summary>
    public IList<Func<object?, AxiosConfig, object?>> TransformRequest
    {
        get => _transformRequest ??= new SynchronizedList<Func<object?, AxiosConfig, object?>>();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _transformRequest = value;
        }
    }

    private IList<Func<object?, AxiosConfig, object?>>? _transformRequest;

    /// <summary>是否有请求 transform（不创建集合）。</summary>
    internal bool HasTransformRequest => _transformRequest is { Count: > 0 };

    private IList<Func<object?, AxiosConfig, object?>>? _transformResponse;

    /// <summary>是否有响应 transform（不创建集合）。</summary>
    internal bool HasTransformResponse => _transformResponse is { Count: > 0 };

    /// <summary>
    /// 对应 axios transformResponse：在响应体转换/反序列化之后依次作用于 Data
    /// （例如统一拆包 <c>{code,data}</c>）。流式响应不适用。
    /// <para>
    /// 这里可以读 <see cref="Response"/> 拿响应头（如把 <c>X-Total-Count</c> 贴到分页对象上）——
    /// 反序列化已经完成，所以适合「加工已造好的对象」，而不是「决定怎么解析」。
    /// </para>
    /// <para>列表线程安全：可在请求进行中追加/移除，枚举走快照。</para>
    /// </summary>
    public IList<Func<object?, AxiosConfig, object?>> TransformResponse
    {
        get => _transformResponse ??= new SynchronizedList<Func<object?, AxiosConfig, object?>>();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _transformResponse = value;
        }
    }

    /// <summary>
    /// JSON 序列化选项。<b>只对默认 JSON 序列化器生效</b>
    /// （请求体 JSON 序列化 / 响应体 JSON 反序列化）。
    /// <para>
    /// 表单与 multipart 的字段命名与本项<b>无关</b>：它们不经过序列化器，命名规则见
    /// <see cref="FormFieldNameSelector"/> 与属性上的 <c>[JsonPropertyName]</c>。
    /// </para>
    /// </summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>
    /// 本次请求（或本实例默认）使用的内容序列化器，<b>优先级最高</b>：
    /// 高于 <see cref="AxiosContent.Serializer"/>，也高于按媒体类型的自动匹配（<see cref="AxiosContent.Register"/>）。
    /// <para>
    /// null 表示不覆盖，继续走「全局注入 → 按 <see cref="ContentType"/> 自动匹配 → 内置 JSON」。
    /// 只有<b>业务对象</b>经过它：请求方向把 <see cref="Data"/> 序列化成文本，
    /// 响应方向把正文反序列化成目标类型。库不理解它背后是 JSON、XML、YAML 还是 Protobuf。
    /// </para>
    /// <para>
    /// 响应侧走哪个入口由响应 <c>Content-Type</c> 判定：文本类型进 <c>Deserialize(string)</c>，
    /// 非文本类型进 <c>Deserialize(ReadOnlyMemory&lt;byte&gt;)</c> 且不做文本解码。
    /// </para>
    /// <para>
    /// string / byte[] / Stream / HttpContent 请求体原样发送；响应侧 string / byte[] / Stream /
    /// 基础数据类型 / dynamic 由库自己处理，都不经过序列化器。
    /// </para>
    /// </summary>
    public IContentSerializer? ContentSerializer { get; set; }



    /// <summary>
    /// 请求体文本编码（string / 对象 JSON / 表单），决定写入 socket 的字节与 Content-Type 中的 charset。
    /// null 时使用 UTF-8（axios 的默认行为）。byte[] / Stream 原样透传，不受该项影响。
    /// </summary>
    public Encoding? RequestEncoding { get; set; }

    /// <summary>
    /// 响应体文本编码。null 时自动识别（Content-Type charset → BOM → UTF-8）；
    /// 设置后强制按指定编码解码原始字节（用于服务端 charset 缺失/错误的情况）。
    /// </summary>
    public Encoding? ResponseEncoding { get; set; }

    /// <summary>
    /// 响应阶段的只读上下文：状态码、响应头、响应的媒体类型与字符集。
    /// <para>
    /// <b>只在响应方向有值</b>——请求阶段（<see cref="IContentSerializer.Serialize"/>）读到的是 <c>null</c>。
    /// 反序列化（<see cref="IContentSerializer.Deserialize(string, Type, AxiosConfig)"/> 与字节入口）
    /// 和 <see cref="TransformResponse"/> 都能拿到它：
    /// 前者用来按响应头决定解析规则（如 <c>X-Schema-Version</c>），
    /// 后者用来搬运元数据（如 <c>X-Total-Count</c>）。
    /// </para>
    /// <para>
    /// 由库写入，调用方只读；<see cref="Headers"/> 与 <see cref="AxiosResponse.Headers"/> 是同一份字典，
    /// 不额外占内存。<see cref="Clone"/> 不会复制它 —— 否则上一个响应的头会被带进下一次请求。
    /// </para>
    /// </summary>
    public AxiosResponseInfo? Response { get; internal set; }

    /// <summary>
    /// 连接池选项。仅在构造 AxiosClient（自建连接池）时生效；
    /// 也可以在构造 AxiosClient 时直接传入共享的 <see cref="HttpConnectionPool"/>。
    /// 单次请求上设置会抛 <see cref="AxiosException"/>（池此时已建好）。
    /// </summary>
    public ConnectionPoolOptions? PoolOptions { get; set; }

    /// <summary>深拷贝（Headers 独立，引用类型成员如 Params/Data 按引用共享）。</summary>
    /// <remarks>
    /// 用 <see cref="object.MemberwiseClone"/> 而不是 new + 逐属性赋值：
    /// <list type="bullet">
    /// <item>构造函数会创建 4 个集合对象，随后立刻被覆盖——每请求一次纯浪费；</item>
    /// <item>各带 <c>*Set</c> 标记的属性 setter 会把「未显式设置」误置为 true，需要末尾逐个还原；
    /// 字段级复制原样保留真实状态，合并语义反而更精确。</item>
    /// </list>
    /// MemberwiseClone 是浅拷贝：Params/Data/委托等引用成员与源共享（文档语义），
    /// 集合成员（Headers / RemoveHeaders / Transform*）则必须在下面换成独立副本。
    /// <para>
    /// 源没有内容的集合保持 <c>null</c>：本方法在请求热路径上每请求执行一次，
    /// 「空集合副本」是纯分配（4 个对象 + 2 次锁内快照），而读取方一律先用 <c>Has*</c> 判定。
    /// </para>
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

        // 响应上下文属于「某一次响应」：克隆出来的是给下一次请求用的配置，
        // 带上旧响应头只会让序列化器读到上一个请求的状态（而且这种脏读极难定位）。
        clone.Response = null;

        // 合并标记属于「那一次合并」：克隆出来的是新请求的输入，尚未与本实例的默认值合并。
        // 漏掉这一行，回放 response.Config 就会带上旧实例的全部生效值并跳过本次合并。
        if (clone.EffectiveMerged)
        {
            clone.EffectiveMerged = false;

            // 但「这些实例级选项是实例的值、不是按请求项」这条信息必须留下（MemberwiseClone 已带过来，
            // 这里只补上面向生效配置的一步）：否则合并时会把源实例的 Proxy/PoolOptions 搬进本实例，
            // 结果既用错连接池，又被校验判成「按请求设置实例级选项」而错报。
            clone.InstanceOptionsInherited = true;
        }

        return clone;
    }

    /// <summary>transform 链的独立副本；空链返回 <c>null</c>（调用方只在有内容时才创建）。</summary>
    private static IList<Func<object?, AxiosConfig, object?>>? CopiedTransforms(
        IList<Func<object?, AxiosConfig, object?>>? source)
        => source is { Count: > 0 }
            ? new SynchronizedList<Func<object?, AxiosConfig, object?>>(source)
            : null;
}
