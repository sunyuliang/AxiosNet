using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;

namespace AxiosNet.Tests.WebAPI;

// 声明式请求模型公共样例：路由在类上，参数绑定在属性上（与 README 示例保持同一形态）。

[Get("users/{id}")]
public sealed class GetUserRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }

    [Query("verbose")]
    public string? Verbose { get; set; }

    [Header("X-Trace")]
    public string? Trace { get; set; }
}

[Get("users")]
public sealed class ListUsersRequest : IApiRequest<Person[]>
{
    /// <summary>未标注任何参数特性：默认按 Query 绑定（键名 = 属性名）。</summary>
    public int[]? Ids { get; set; }

    public string? Role { get; set; }
}

[Post("users")]
public sealed class CreateUserRequest : IApiRequest<Person>
{
    [Body]
    public Person Person { get; set; } = null!;
}

[Post("users/empty")]
public sealed class NullBodyRequest : IApiRequest<Person>
{
    [Body]
    public Person? Person { get; set; }
}

[Delete("users/{id}")]
public sealed class DeleteUserRequest : IApiRequest<object>
{
    [Path]
    public int Id { get; set; }
}

[Head("ping")]
public sealed class PingRequest : IApiRequest<object>
{
}

[Post("login")]
public sealed class LoginRequest : IApiRequest<string>
{
    [Body]
    public Person? Credentials { get; set; }
}

/// <summary>经基类 Configure 钩子加头。</summary>
[Get("users/{id}")]
public sealed class ConfiguredGetRequest : ApiRequest<Person>
{
    [Path]
    public int Id { get; set; }

    protected override void Configure(AxiosConfig config)
    {
        config.Headers.Set("X-Configured", "from-model");
    }
}

/// <summary>头绑定声明在基类上：绑定需沿继承链收集。</summary>
public abstract class TracedRequest<T> : ApiRequest<T>
{
    [Header("X-Trace")]
    public string? Trace { get; set; }
}

[Get("users/{id}")]
public sealed class InheritedHeaderRequest : TracedRequest<Person>
{
    [Path]
    public int Id { get; set; }
}

[Get("users/{name}/profile")]
public sealed class EscapedPathRequest : IApiRequest<Person>
{
    [Path]
    public string Name { get; set; } = "";
}

[Get("http://127.0.0.1:{port}/abs")]
public sealed class AbsoluteUrlRequest : IApiRequest<Person>
{
    [Path]
    public int Port { get; set; }
}

// ---- 默认 Body（POST/PUT/PATCH + 恰好一个未标注的非标量属性）----

[Post("orders")]
public sealed class DefaultBodyRequest : IApiRequest<Person>
{
    /// <summary>未标注：单负载模型，默认按 Body 绑定（无需 [Body]）。</summary>
    public Person Payload { get; set; } = null!;
}

[Put("items/{id}")]
public sealed class PutDefaultBodyRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }

    /// <summary>未标注：PUT 同样享受默认 Body。</summary>
    public Person Payload { get; set; } = null!;
}

[Post("search/scalar")]
public sealed class ScalarDefaultStaysQueryRequest : IApiRequest<Person>
{
    /// <summary>未标注标量：标量护栏生效，保持 Query，不静默变请求体。</summary>
    public int Page { get; set; }
}

[Post("search/mixed")]
public sealed class TwoUnannotatedRequest : IApiRequest<Person>
{
    /// <summary>未标注：多个未标注属性时歧义，全部维持 Query。</summary>
    public int Page { get; set; }

    public Person? Payload { get; set; }
}

[Post("search/explicit-query")]
public sealed class ExplicitQueryThenDefaultBodyRequest : IApiRequest<Person>
{
    /// <summary>显式 [Query] 不算「未标注」：剩下的唯一未标注属性仍享受默认 Body。</summary>
    [Query("page")]
    public int Page { get; set; }

    public Person? Payload { get; set; }
}

[Delete("items/{id}")]
public sealed class DeleteDefaultStaysQueryRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }

    /// <summary>未标注：DELETE 不参与默认 Body，维持 Query。</summary>
    public Person? Payload { get; set; }
}

// ---- [ApiIgnore]：把属性从绑定中完全排除 ----

[Get("profile")]
public sealed class IgnoredQueryRequest : IApiRequest<Person>
{
    public string? Name { get; set; }

    /// <summary>内部状态：不进查询串。</summary>
    [ApiIgnore]
    public string? CacheKey { get; set; }
}

[Post("reports")]
public sealed class IgnoredDoesNotBlockDefaultBodyRequest : IApiRequest<Person>
{
    /// <summary>被忽略的属性不算「未标注」：剩下的唯一未标注属性仍享受默认 Body。</summary>
    [ApiIgnore]
    public string? InternalState { get; set; }

    public Person Payload { get; set; } = null!;
}

[Get("broken-ignore")]
public sealed class IgnoreWithQueryRequest : IApiRequest<Person>
{
    /// <summary>[ApiIgnore] 与参数特性同用：二义性，构建期报错。</summary>
    [ApiIgnore]
    [Query("page")]
    public int Page { get; set; }
}

// ---- 多特性并存：同一属性绑定到多处 ----

[Get("users/{id}/scores")]
public sealed class PathHeaderRequest : IApiRequest<Person>
{
    /// <summary>同一值：既填充路径占位符，又进请求头。</summary>
    [Path("id")]
    [Header("X-User-Id")]
    public int Id { get; set; }
}

[Get("trace")]
public sealed class QueryHeaderRequest : IApiRequest<Person>
{
    /// <summary>同一值：既进查询串，又进请求头。</summary>
    [Query("trace")]
    [Header("X-Trace")]
    public string? Trace { get; set; }
}

[Post("orders/{orderId}")]
public sealed class PathBodyHeaderRequest : IApiRequest<Person>
{
    /// <summary>同一标量值：路径占位符 + 请求头 + 请求体三处并存（标量体按 AxiosNet 规则走原始文本）。</summary>
    [Path("orderId")]
    [Header("X-Order-Id")]
    [Body]
    public long OrderId { get; set; }
}

[Post("reports")]
public sealed class QueryBodyRequest : IApiRequest<Person>
{
    /// <summary>复杂对象：进请求体（序列化器处理）+ 进查询串（嵌套规则）；不能进路径/头（标量护栏）。</summary>
    [Query("reportId")]
    [Body]
    public ReportPayload Report { get; set; } = null!;
}

[Get("broken/{payload}")]
public sealed class PathOnComplexRequest : IApiRequest<Person>
{
    /// <summary>复杂对象进路径：构建期报错（路径段就是文本，复杂对象只有 ToString 垃圾值）。</summary>
    [Path("payload")]
    public ReportPayload Report { get; set; } = null!;
}

[Get("broken-header")]
public sealed class HeaderOnComplexRequest : IApiRequest<Person>
{
    /// <summary>复杂对象进请求头：构建期报错，理由同上。</summary>
    [Header("X-Report")]
    public ReportPayload Report { get; set; } = null!;
}

/// <summary>多绑定测试用的负载模型（ToString 会展开属性，便于断言头值）。</summary>
public sealed record OrderPayload(long OrderId, string Item, int Quantity);

/// <summary>多绑定测试用的负载模型。</summary>
public sealed record ReportPayload(long ReportId, string Title);

/// <summary>表单编码用例的负载模型。</summary>
public sealed record UserPayload(string Name, int Age);

// ---- 边界场景补充 ----

/// <summary>占位符与绑定名大小写不敏感匹配：[Path("UID")] ↔ 模板 {uid}。</summary>
[Get("users/{uid}")]
public sealed class CaseInsensitivePathRequest : IApiRequest<Person>
{
    [Path("UID")]
    public int Id { get; set; }
}

/// <summary>仅大小写不同的占位符：按 IgnoreCase 判重复。</summary>
[Get("items/{id}/{ID}")]
public sealed class DuplicateCaseVariantRequest : IApiRequest<object>
{
    [Path]
    public int Id { get; set; }
}

/// <summary>默认 Body + null 值：不发请求体。</summary>
[Post("reports/null")]
public sealed class DefaultBodyNullRequest : IApiRequest<Person>
{
    public Person? Payload { get; set; }
}

/// <summary>未标注 int[]：非标量 → 默认整个数组作为 JSON 请求体（钉死该语义）。</summary>
[Post("tags")]
public sealed class ScalarArrayDefaultBodyRequest : IApiRequest<Person>
{
    public int[]? Tags { get; set; }
}

/// <summary>[Body] + Configure 改 ContentType：编码规则与核心包 data 一致（表单）。</summary>
[Post("form")]
public sealed class FormBodyRequest : ApiRequest<Person>
{
    [Body]
    public UserPayload Payload { get; set; } = null!;

    protected override void Configure(AxiosConfig config)
    {
        config.ContentType = AxiosNet.AxiosContentType.FormUrlEncoded;
    }
}

/// <summary>Guid 进路径：ToString 的 "d" 格式（小写连字符）。</summary>
[Get("tokens/{token}")]
public sealed class GuidPathRequest : IApiRequest<Person>
{
    [Path]
    public Guid Token { get; set; }
}

/// <summary>位置参数 record 直接做请求模型（init-only 属性可读，绑定无碍）。</summary>
[Get("records/{id}")]
public sealed record RecordRequest([property: Path] int Id) : IApiRequest<Person>;

/// <summary>非法头值（CRLF 注入）的 fail-fast 用例。</summary>
[Get("evil")]
public sealed class EvilHeaderRequest : IApiRequest<Person>
{
    [Header("X-Evil")]
    public string? Evil { get; set; }
}

/// <summary>原始响应形态：byte[]。</summary>
[Get("download")]
public sealed class DownloadBytesRequest : IApiRequest<byte[]>
{
}

/// <summary>原始响应形态：Stream（自动切流式响应）。</summary>
[Get("download")]
public sealed class DownloadStreamRequest : IApiRequest<Stream>
{
}

/// <summary>自定义谓词：继承 HttpApiAttribute 直接传 HttpMethod 实例（如 WebDAV 的 PROPFIND）。</summary>
[PropFind("calendar/event")]
public sealed class PropFindRequest : IApiRequest<Person>
{
}

/// <summary>自定义谓词特性：与 AxiosNet 全库一致，直接引用 BCL HttpMethod。</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class PropFindAttribute : HttpApiAttribute
{
    public PropFindAttribute(string path) : base(new HttpMethod("PROPFIND"), path)
    {
    }
}

/// <summary>直用 HttpApiAttribute（字符串方法名）：小写 "delete" 归一化为规范 DELETE。</summary>
[HttpApi("delete", "api/users/{id}")]
public sealed class DirectHttpApiDeleteRequest : IApiRequest<Person>
{
    [Path]
    public int Id { get; set; }
}

/// <summary>直用 HttpApiAttribute：小写 "post" 归一化后应享受默认 Body 规则。</summary>
[HttpApi("post", "api/users")]
public sealed class DirectHttpApiPostRequest : IApiRequest<Person>
{
    public Person? Payload { get; set; }
}

/// <summary>空白方法名：特性构造期即抛错（经绑定层缓存转 InvalidOperationException）。</summary>
[HttpApi("", "x")]
public sealed class BlankMethodRequest : IApiRequest<object>
{
}

/// <summary>空白路径模板：受保护构造的路径校验对字符串构造同样生效。</summary>
[HttpApi("get", " ")]
public sealed class BlankPathRequest : IApiRequest<object>
{
}

// ---- 声明式绑定的边界形态样例（原先与流式替身放在同一个文件里） ----

[Get("items")]
public sealed class AllNullQueryRequest : IApiRequest<Person>
{
    [Query]
    public string? Filter { get; set; }
}

[Get("items")]
public sealed class NoQueryBindingRequest : IApiRequest<Person>
{
    [ApiIgnore]
    public string? Hidden { get; set; }
}

[Post("report")]
public sealed class DateOnlyBodyCandidateRequest : IApiRequest<Person>
{
    public DateOnly When { get; set; }
}

[Post("upload")]
public sealed class UriBodyCandidateRequest : IApiRequest<Person>
{
    public Uri Callback { get; set; } = null!;
}

[Get("reports/{day}")]
public sealed class DateOnlyPathRequest : IApiRequest<Person>
{
    [Path]
    public DateOnly Day { get; set; }
}

[Get("items")]
public sealed class IgnoredInheritedPageRequest : PagedRequestBase, IApiRequest<Person>
{
    [ApiIgnore]
    public override int Page { get; set; }
}

/// <summary>同一个值分别落在路径 / 请求头 / 查询串，用于校验三处文本化一致。</summary>
[Get("flags/{flag}")]
public sealed class ScalarTextRequest : IApiRequest<Person>
{
    [Path]
    public bool Flag { get; set; }

    [Header("X-Dry-Run")]
    public bool DryRun { get; set; }

    [Query]
    public DateTime When { get; set; }
}

/// <summary>基类带查询绑定，派生类用 [ApiIgnore] 覆盖：该属性应完全不参与绑定。</summary>
public class PagedRequestBase
{
    [Query("page")]
    public virtual int Page { get; set; }
}
