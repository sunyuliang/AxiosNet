using System.Text.Json;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI;

/// <summary>状态码错误的异常工厂：状态码不合法的请求会先经过这里，由实现方决定抛什么异常。返回 <c>null</c> 表示不接管；网络错误、超时、取消、配置错误不会调用本接口。</summary>
public interface IApiErrorFactory
{
    /// <summary>根据错误上下文构造要抛出的异常；返回 <c>null</c> 表示保持原始 <see cref="AxiosException"/>。</summary>
    ValueTask<Exception?> CreateAsync(ApiErrorContext context, CancellationToken cancellationToken);
}

/// <summary>错误上下文：请求模型、生效配置、响应与原始异常。</summary>
public sealed class ApiErrorContext
{
    internal ApiErrorContext(object request, AxiosException error)
    {
        Request = request;
        Error = error;
    }

    /// <summary>本次请求载荷：请求模型实例；接口级声明时为 <see cref="ApiInterfaceInvocation"/>（可按接口类型 / 方法名 / 参数做分支）。</summary>
    public object Request { get; }

    /// <summary>本次请求载荷类型（即 <c>Request.GetType()</c>；接口级声明时为 <see cref="ApiInterfaceInvocation"/>）。</summary>
    public Type RequestType => Request.GetType();

    /// <summary>本次请求的生效配置（实例默认值 + 载荷绑定 + 按请求回调的合并结果）。</summary>
    public AxiosConfig Config => Error.Config!;

    /// <summary>原始异常（<c>Code</c> / <c>Cause</c> / <c>Request</c> 都在它上面）。</summary>
    public AxiosException Error { get; }

    /// <summary>触发本次错误的响应。<b>非 null</b>（本上下文只用于状态码错误）。</summary>
    public AxiosResponse Response => Error.Response!;

    /// <summary>HTTP 状态码。</summary>
    public int Status => Response.Status;

    /// <summary>错误响应正文原文：<see cref="AxiosResponse.RawText"/> 优先，其次按 <see cref="AxiosResponse.Data"/> 的实际形态取文本。
    /// byte[] 按响应 charset / 强制 ResponseEncoding 解码；都不适用时为 <c>null</c>。</summary>
    public string? Body => Response.RawText ?? Response.Data switch
    {
        null => null,
        string text => text,
        JsonElement element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
        byte[] bytes => ResponseTextDecoder.Decode(bytes, Config.Response?.CharSet, Config.SerializationOrNull?.ResponseEncoding),
        _ => null
    };

    /// <summary>把错误体反序列化成 <typeparamref name="TError"/>（用按响应 <c>Content-Type</c> 解析出的生效序列化器）。正文为空、无法解析或类型不匹配时返回 <c>default</c>。</summary>
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
            return new ValueTask<TError?>(serializer.Deserialize(body, typeof(TError), Config) is TError parsed ? parsed : default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 解析失败时按没有强类型错误体处理。
            return new ValueTask<TError?>(default(TError));
        }
    }
}

/// <summary>
/// 内置的强类型错误异常：携带反序列化后的错误体与原始 <see cref="AxiosException"/>。
/// 它本身也是 <see cref="AxiosException"/>，因此 <c>catch (AxiosException)</c> 与 <c>catch (ApiError&lt;T&gt;)</c> 都能接住。
/// </summary>
/// <typeparam name="TError">错误体模型（如 RFC 7807 的 ProblemDetails）。</typeparam>
public sealed class ApiError<TError> : AxiosException
{
    /// <summary>按错误上下文构造异常（自定义工厂里也可用它 / 或自己继承 <see cref="Exception"/>）。</summary>
    /// <param name="error">已反序列化的错误体；解析失败时为 <c>null</c>。</param>
    /// <param name="context">错误上下文。</param>
    public ApiError(TError? error, ApiErrorContext context)
        : base(
            Ensure(context).Error.Message,
            context.Error.Code,
            context.Error.Config,
            context.Error.Request,
            context.Response,
            context.Error)
    {
        Error = error;
        StatusText = context.Response.StatusText;
        Response = context.Response;
        Original = context.Error;
    }

    // 基类构造参数里要先解引用 context，因此空值校验放在参数列表的第一个实参位置（实参从左到右求值）。
    private static ApiErrorContext Ensure(ApiErrorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context;
    }

    /// <summary>反序列化后的错误体；错误体缺失或无法解析时为 <c>null</c>。</summary>
    public TError? Error { get; }

    /// <summary>HTTP 状态码（等价于 <see cref="Response"/> 的 <c>Status</c>）。</summary>
    public new int Status => Response.Status;

    /// <summary>HTTP 状态原因的文本形式（如 <c>Bad Request</c>）。</summary>
    public string StatusText { get; }

    /// <summary>原始响应（头、正文原文、请求信息）。</summary>
    public new AxiosResponse Response { get; }

    /// <summary>原始 <see cref="AxiosException"/>（与 <see cref="Exception.InnerException"/> 同一实例）。</summary>
    public AxiosException Original { get; }
}

/// <summary>内置错误工厂：把错误体读成 <typeparamref name="TError"/> 并抛出 <see cref="ApiError{TError}"/>。</summary>
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
