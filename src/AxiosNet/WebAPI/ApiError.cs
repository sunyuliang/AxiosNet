using System.Text.Json;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI;

/// <summary>
/// 状态码错误的异常工厂：把 <see cref="ApiClient.ErrorFactory"/> 接上后，
/// 「服务端返回了响应但状态码不合法」的错误会先经过这里，由实现方决定抛什么异常。
/// <para>
/// 返回 <c>null</c> 表示不接管（仍抛原始 <see cref="AxiosException"/>）。
/// 网络错误、超时、取消、配置错误不会调用本接口 —— 它们没有响应体可读，
/// 调用方的 <c>catch (AxiosException)</c> 在那里必须继续成立。
/// </para>
/// </summary>
public interface IApiErrorFactory
{
    /// <summary>根据错误上下文构造要抛出的异常；返回 <c>null</c> 表示保持原始 <see cref="AxiosException"/>。</summary>
    ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken);
}

/// <summary>
/// 错误上下文：请求模型、生效配置、响应（状态码 / 头 / 错误体原文）与原始异常。
/// </summary>
public sealed class ApiErrorContext
{
    internal ApiErrorContext(object request, AxiosException error)
    {
        Request = request;
        Error = error;
    }

    /// <summary>本次请求模型实例（可读取其属性做分支处理）。</summary>
    public object Request { get; }

    /// <summary>本次请求模型类型（即 <c>Request.GetType()</c>）。</summary>
    public Type RequestType => Request.GetType();

    /// <summary>
    /// 本次请求的<b>生效配置</b>（<see cref="AxiosException.Config"/>：实例默认值 + 模型绑定 + 按请求回调的合并结果）。
    /// </summary>
    public AxiosConfig Config => Error.Config!;

    /// <summary>原始异常（<c>Code</c> / <c>Cause</c> / <c>Request</c> 都在它上面）。</summary>
    public AxiosException Error { get; }

    /// <summary>触发本次错误的响应。<b>非 null</b>（本上下文只用于状态码错误）。</summary>
    public AxiosResponse Response => Error.Response!;

    /// <summary>HTTP 状态码。</summary>
    public int Status => Response.Status;

    /// <summary>
    /// 错误响应正文原文：<see cref="AxiosResponse.RawText"/> 优先，其次按 <see cref="AxiosResponse.Data"/>
    /// 的实际形态取文本（string / <see cref="JsonElement"/> / byte[]）；都不适用时为 <c>null</c>。
    /// </summary>
    public string? Body => Response.RawText ?? Response.Data switch
    {
        null => null,
        string text => text,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
        byte[] bytes => ResponseTextDecoder.Decode(bytes, charSet: null, Config.SerializationOrNull?.ResponseEncoding),
        _ => null
    };

    /// <summary>
    /// 把错误体反序列化成 <typeparamref name="TError"/>（用响应 <c>Content-Type</c> 选中的序列化器）。
    /// <para>正文为空、无法解析或类型不匹配时返回 <c>default</c>：错误体解析失败不能盖住原始 HTTP 错误。</para>
    /// </summary>
    public ValueTask<TError?> TryReadAsync<TError>(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Response.Data is TError typed)
        {
            return new ValueTask<TError?>(typed);
        }

        var body = Body;
        if (string.IsNullOrWhiteSpace(body))
        {
            return new ValueTask<TError?>(default(TError));
        }

        try
        {
            var serializer = AxiosContent.Get(Config, Response.GetHeader("Content-Type"));
            return new ValueTask<TError?>(serializer.Deserialize(body, typeof(TError), Config) is TError parsed
                ? parsed
                : default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 错误体形态由服务端决定（HTML 错误页、纯文本、字段校验 JSON 各不相同）：
            // 解析失败只说明「没有强类型错误体」，不是新的故障。
            return new ValueTask<TError?>(default(TError));
        }
    }
}

/// <summary>
/// 内置的强类型错误异常：携带反序列化后的错误体与原始 <see cref="AxiosException"/>。
/// <para>
/// 与 <see cref="AxiosException"/> 并存而非替代：默认行为不变，接上
/// <see cref="ApiClient.ErrorFactory"/> 后才抛本类型。<see cref="Exception.InnerException"/> 是原始异常。
/// </para>
/// </summary>
/// <typeparam name="TError">错误体模型（如 RFC 7807 的 ProblemDetails 或自家信封）。</typeparam>
public sealed class ApiError<TError> : Exception
{
    /// <summary>按错误上下文构造异常（自定义工厂里也可用它 / 或自己继承 <see cref="Exception"/>）。</summary>
    /// <param name="error">已反序列化的错误体；解析失败时为 <c>null</c>。</param>
    /// <param name="context">错误上下文。</param>
    public ApiError(TError? error, ApiErrorContext context)
        : base(context is null ? "API request failed" : context.Error.Message, context?.Error)
    {
        ArgumentNullException.ThrowIfNull(context);

        Error = error;
        Status = context.Response.Status;
        StatusText = context.Response.StatusText;
        Response = context.Response;
        Original = context.Error;
    }

    /// <summary>反序列化后的错误体；错误体缺失或无法解析时为 <c>null</c>。</summary>
    public TError? Error { get; }

    /// <summary>HTTP 状态码（等价于 <see cref="Response"/> 的 <c>Status</c>）。</summary>
    public int Status { get; }

    /// <summary>HTTP 状态原因的文本形式（如 <c>Bad Request</c>）。</summary>
    public string StatusText { get; }

    /// <summary>原始响应（头、正文原文、请求信息）。</summary>
    public AxiosResponse Response { get; }

    /// <summary>原始 <see cref="AxiosException"/>（<c>Code</c> / <c>Cause</c> 从它取）。</summary>
    public AxiosException Original { get; }

    /// <summary>错误码，与 <see cref="Original"/> 同一值（如 <c>ERR_BAD_REQUEST</c>）。</summary>
    public string? Code => Original.Code;
}

/// <summary>
/// 内置错误工厂：把错误体读成 <typeparamref name="TError"/> 并抛出 <see cref="ApiError{TError}"/>。
/// </summary>
/// <example>
/// <code>
/// var client = new ApiClient("https://api.example.com")
/// {
///     ErrorFactory = new ApiErrorFactory&lt;ProblemDetails&gt;()
/// };
///
/// try { await client.RequestAsync(new GetUserRequest { Id = 5 }); }
/// catch (ApiError&lt;ProblemDetails&gt; e) { Console.WriteLine(e.Error?.Title); }
/// </code>
/// </example>
/// <typeparam name="TError">错误体模型。</typeparam>
public sealed class ApiErrorFactory<TError> : IApiErrorFactory
{
    /// <inheritdoc />
    public async ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var error = await context.TryReadAsync<TError>(cancellationToken).ConfigureAwait(false);
        return new ApiError<TError>(error, context);
    }
}
