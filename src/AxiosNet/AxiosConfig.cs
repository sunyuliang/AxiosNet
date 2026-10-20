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

    /// <summary>对应 axios headers（大小写不敏感）。</summary>
    public AxiosHeaders Headers { get; set; } = new();

    /// <summary>对应 axios params，会被序列化为 query string，支持对象/字典/数组/嵌套。</summary>
    public object? Params { get; set; }

    /// <summary>库默认的请求体媒体类型。</summary>
    public const string JsonContentType = "application/json";

    /// <summary>表单编码媒体类型常量（配合 <see cref="ContentType"/> 使用）。</summary>
    public const string FormUrlEncodedContentType = "application/x-www-form-urlencoded";

    private string _contentType = JsonContentType;

    /// <summary>
    /// 请求体媒体类型（Content-Type），默认 <c>application/json</c>。
    /// <para>
    /// 请求体的编码格式由本项决定，<b>与 <see cref="Data"/> 的运行时类型无关</b>：
    /// 对象、字典、dynamic、集合等结构化数据在 json 下都会序列化为 JSON；
    /// 设为 <c>application/x-www-form-urlencoded</c> 时转为键值对表单（嵌套用 <c>a[b]=v</c>、数组用 <c>a[]=v</c>）；
    /// 设为 <c>multipart/form-data</c> 时构建字段 + 文件部件（文件用 <see cref="FormFile"/> / Stream / byte[] / HttpContent 表示）；
    /// 其他媒体类型（text/plain、application/xml 等）下结构化数据仍按 JSON 文本写入，仅头部不同。
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
            _contentType = string.IsNullOrWhiteSpace(value) ? JsonContentType : value.Trim();
            ContentTypeSet = true;
        }
    }

    /// <summary>是否被显式赋值过，用于合并配置时区分「未设置」与「默认值」。</summary>
    internal bool ContentTypeSet { get; private set; }

    /// <summary>
    /// 对应 axios data：请求体原始数据，支持任意类型 ——
    /// 对象/字典/dynamic/集合（按 <see cref="ContentType"/> 序列化），
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
    /// 仅实例级生效：在构造 AxiosClient 时应用到连接池 Handler；按请求设置会抛 <see cref="AxiosException"/>。
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
    /// 仅实例级生效：在构造 AxiosClient 时应用到连接池 Handler；按请求设置会抛 <see cref="AxiosException"/>。
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
    /// </summary>
    public Func<string?, string, string>? FormFieldNameSelector { get; set; }

    /// <summary>
    /// 对应 axios transformRequest：在拦截器之后、请求体编码之前依次作用于 <see cref="Data"/>，
    /// 每个函数返回新的数据（可返回 string/byte[]/Stream/HttpContent 等）。
    /// <para>列表线程安全：可在请求进行中追加/移除，枚举走快照。</para>
    /// </summary>
    public IList<Func<object?, AxiosConfig, object?>> TransformRequest { get; set; }
        = new SynchronizedList<Func<object?, AxiosConfig, object?>>();

    /// <summary>
    /// 对应 axios transformResponse：在响应体转换/反序列化之后依次作用于 Data
    /// （例如统一拆包 <c>{code,data}</c>）。流式响应不适用。
    /// <para>列表线程安全：可在请求进行中追加/移除，枚举走快照。</para>
    /// </summary>
    public IList<Func<object?, AxiosConfig, object?>> TransformResponse { get; set; }
        = new SynchronizedList<Func<object?, AxiosConfig, object?>>();

    /// <summary>JSON 序列化选项（请求体序列化 / 响应体反序列化共用）。</summary>
    public JsonSerializerOptions? JsonSerializerOptions { get; set; }

    /// <summary>
    /// 自定义 JSON 序列化器（例如 Newtonsoft.Json 适配器）。
    /// <para>
    /// null 表示使用 <see cref="AxiosJson.Serializer"/> 的全局默认（System.Text.Json）。
    /// 设置后，请求体 JSON 序列化、响应体反序列化、<c>dynamic</c> 展开与非泛型响应的文档解析都由它负责；
    /// <see cref="JsonSerializerOptions"/> 仍用于表单/multipart 的字段命名与展平（需要 JSON 树语义）。
    /// </para>
    /// </summary>
    public IAxiosJsonSerializer? JsonSerializer { get; set; }

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
    /// 连接池选项。仅在 AxiosClient 自建连接池时生效；
    /// 也可以在构造 AxiosClient 时直接传入共享的 <see cref="HttpConnectionPool"/>。
    /// </summary>
    public ConnectionPoolOptions? PoolOptions { get; set; }

    /// <summary>深拷贝（Headers 独立，引用类型成员如 Params/Data 按引用共享）。</summary>
    public AxiosConfig Clone()
    {
        var clone = new AxiosConfig
        {
            BaseAddress = BaseAddress,
            Url = Url,
            Method = Method,
            Headers = Headers.Clone(),
            Params = Params,
            Data = Data,
            ContentType = ContentType,
            Timeout = Timeout,
            Auth = Auth,
            ResponseType = ResponseType,
            ValidateStatus = ValidateStatus,
            CancellationToken = CancellationToken,
            HttpVersion = HttpVersion,
            WithCredentials = WithCredentials,
            Proxy = Proxy,
            Credentials = Credentials,
            MaxRedirects = MaxRedirects,
            JsonSerializerOptions = JsonSerializerOptions,
            JsonSerializer = JsonSerializer,
            RequestEncoding = RequestEncoding,
            ResponseEncoding = ResponseEncoding,
            PoolOptions = PoolOptions,
            MaxContentLength = MaxContentLength,
            MaxBodyLength = MaxBodyLength,
            MaxRate = MaxRate,
            TimeoutMessage = TimeoutMessage,
            OnUploadProgress = OnUploadProgress,
            OnDownloadProgress = OnDownloadProgress,
            ParamsSerializer = ParamsSerializer,
            FormFieldNameSelector = FormFieldNameSelector,
            TransformRequest = new SynchronizedList<Func<object?, AxiosConfig, object?>>(TransformRequest),
            TransformResponse = new SynchronizedList<Func<object?, AxiosConfig, object?>>(TransformResponse)
        };

        // 属性赋值会把「已显式设置」置为 true，这里还原真实状态，保证合并语义正确。
        clone.MethodSet = MethodSet;
        clone.ResponseTypeSet = ResponseTypeSet;
        clone.CancellationTokenSet = CancellationTokenSet;
        clone.ContentTypeSet = ContentTypeSet;
        clone.WithCredentialsSet = WithCredentialsSet;
        clone.MaxRedirectsSet = MaxRedirectsSet;
        clone.TimeoutSet = TimeoutSet;
        clone.MaxContentLengthSet = MaxContentLengthSet;
        clone.MaxBodyLengthSet = MaxBodyLengthSet;
        clone.MaxRateSet = MaxRateSet;
        return clone;
    }
}
