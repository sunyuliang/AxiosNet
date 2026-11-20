
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.WebAPI;

// 接口级声明（阶段 0/1/2）：正常契约与各类非法声明放在一起，
// 因为启动期校验的用例要逐条比对「哪些能过、哪些必须报错」。

// 接口级声明公共样例：路由在方法上，绑定特性在参数上（与 README 示例保持同一形态）。

[Headers("Accept: application/json")]
public interface IUserApi
{
    [Get("users/{id}")]
    Task<AxiosResponse<Person>> GetAsync(
        [Path] int id,
        [Query("verbose")] string? verbose = null,
        [Header("X-Trace")] string? trace = null,
        CancellationToken cancellationToken = default);

    [Post("users")]
    Task<AxiosResponse<Person>> CreateAsync(Person payload, CancellationToken cancellationToken = default);

    [Post("users/empty")]
    Task<AxiosResponse<Person>> CreateNullAsync([RootBody] Person? payload, CancellationToken cancellationToken = default);

    [Get("users")]
    Task<AxiosResponse<Person[]>> ListAsync(
        [Query] int[]? ids = null,
        string? role = null,
        CancellationToken cancellationToken = default);

    [Get("headers")]
    Task<AxiosResponse<Person>> HeaderCollectionAsync(
        [HeaderCollection] IDictionary<string, string>? headers,
        CancellationToken cancellationToken = default);

    [Get("ignore")]
    Task<AxiosResponse<Person>> IgnoredAsync(
        [ApiIgnore] string? internalState,
        string? name,
        CancellationToken cancellationToken = default);

    [Get("ping")]
    Task<AxiosResponse<Person>> PingAsync();

    [Get("cancel")]
    Task<AxiosResponse<Person>> CancelAsync(CancellationToken cancellationToken = default);
}

[Headers("X-Base: base")]
public interface IBaseApi
{
    [Get("base/{id}")]
    Task<AxiosResponse<Person>> GetAsync([Path] int id);
}

[Headers("X-Scope: derived")]
public interface IDerivedApi : IBaseApi
{
    [Get("extra")]
    Task<AxiosResponse<Person>> ExtraAsync();
}

[Authorize]
public interface IAuthorizedApi
{
    [Get("secure")]
    Task<AxiosResponse<Person>> GetAsync();
}

[AllowAnonymous]
public interface IAnonymousApi
{
    [Get("open")]
    Task<AxiosResponse<Person>> GetAsync();
}

public interface IPathNullApi
{
    [Get("users/{name}")]
    Task<AxiosResponse<Person>> GetAsync([Path] string? name);
}

public interface IStatusApi
{
    [Get("status")]
    Task<AxiosResponse<Person>> GetAsync();
}

// ---- 非法声明：校验 API 必须逐条报告 ----

public interface IMissingRouteApi
{
    Task<AxiosResponse<Person>> GetAsync(int id);
}

public interface IMultipleBodyApi
{
    /// <summary>合法：POST 上 [Body] 与 [RootBody] 合并到同一层，接口式与模型式同一原则。</summary>
    [Post("x")]
    Task<AxiosResponse<Person>> PostAsync([RootBody] Person first, [Body] Person second);

    /// <summary>合法：未标注参数在 POST 上默认进正文，与 [RootBody] 展开的属性合并到同一层。</summary>
    [Post("y")]
    Task<AxiosResponse<Person>> PostUnannotatedAsync(
        [RootBody] Person payload,
        string? tag = null,
        CancellationToken cancellationToken = default);
}

public interface IWrongReturnApi
{
    [Get("x")]
    ValueTask<AxiosResponse<Person>> GetAsync();
}

public interface IStreamingApi
{
    [Get("x")]
    Task<AxiosResponse<IAsyncEnumerable<StreamFrame>>> GetAsync();
}

// ---- 阶段 2：接口级 multipart / 流式 ----

public interface IUploadApi
{
    [Post("upload")]
    [Multipart]
    Task<AxiosResponse<Person>> UploadAsync(
        [Part("title")] string title,
        [Part("blob")] byte[] blob,
        [Part("extra", FileName = "a.txt", ContentType = "text/plain")] byte[] extra,
        CancellationToken cancellationToken = default);

    [Post("upload/default")]
    [Multipart]
    Task<AxiosResponse<Person>> UploadDefaultAsync(string note, byte[] payload);
}

public interface IBrokenMultipartApi
{
    [Post("upload/broken")]
    [Multipart]
    Task<AxiosResponse<Person>> UploadAsync([Part(FileName = "x")] IEnumerable<string> values);
}

public interface IStreamApi
{
    [Get("stream/sse")]
    [Stream(AxiosStreamFormat.ServerSentEvents)]
    Task<AxiosResponse<IAsyncEnumerable<StreamFrame>>> SseAsync();

    [Get("stream/json-array")]
    [Stream(AxiosStreamFormat.JsonArray)]
    Task<AxiosResponse<IAsyncEnumerable<StreamFrame>>> JsonArrayAsync();
}

public sealed record StreamFrame(int N, string? Text);

// ---- 阶段 2 非法声明 ----

public interface IStreamWithoutMultipartApi
{
    [Post("x")]
    Task<AxiosResponse<Person>> PostAsync([Part] string value);
}

public interface IMultipartWithBodyApi
{
    [Post("x")]
    [Multipart]
    Task<AxiosResponse<Person>> PostAsync([RootBody] Person payload);
}

public interface IStreamOnPlainApi
{
    [Get("x")]
    [Stream]
    Task<AxiosResponse<Person>> GetAsync();
}

public interface IInvalidStreamFormatApi
{
    [Get("x")]
    [Stream((AxiosStreamFormat)99)]
    Task<AxiosResponse<IAsyncEnumerable<Person>>> GetAsync();
}
