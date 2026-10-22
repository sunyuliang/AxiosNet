# AxiosNet.WebAPI

[简体中文](#声明式-api-客户端) | [English](#declarative-api-client)

**Declarative API client built on [AxiosNet](https://www.nuget.org/packages/AxiosNet)** — define request models and authorization, and the call site is a single `client.RequestAsync(request)`. The response type is inferred from the request model (the RestSharp / Flurl / Refit niche, while keeping the axios-style response shape, the connection pool, interceptors and the full AxiosNet pipeline underneath).

## 声明式 API 客户端

只管三件事：**定义请求模型、定义返回模型、写好授权**，即可访问任意 API——返回类型由请求模型自动确定。

```csharp
using AxiosNet.WebAPI;

// 1) 请求模型：路由在类上，参数绑定在属性上；IApiRequest<TResponse> 的泛型即返回类型
[Get("users/{id}")]
public sealed class GetUserRequest : IApiRequest<User>
{
    [Path] public int Id { get; set; }                        // 填充 {id}（URL 转义，与占位符大小写不敏感匹配）
    [Query("verbose")] public string? Verbose { get; set; }   // 查询参数
    [Header("X-Trace")] public string? Trace { get; set; }    // 请求头
}

[Post("users")]
public sealed class CreateUserRequest : IApiRequest<User>
{
    [Body] public UserPayload Payload { get; set; } = null!;  // 请求体（JSON / 表单 / multipart / 原始内容）
}

// 2) 客户端与授权：IAuthorizationProvider 是扩展点（内置 Bearer；令牌缓存 / 刷新 / 签名自行实现）
var client = new ApiClient("https://api.example.com/")
{
    Authorization = new BearerAuthorizationProvider(() => GetTokenAsync())
};

// 3) 调用：返回类型按请求模型自动确定，response.Data 已是 User
var response = await client.RequestAsync(new GetUserRequest { Id = 5, Trace = "t-1" });
Console.WriteLine($"{response.Status} {response.Data!.Name}");
await client.RequestAsync(new CreateUserRequest { Payload = payload });   // 返回 AxiosResponse<User>
```

## 绑定规则

| 绑定 | 特性 | 说明 |
| --- | --- | --- |
| 路径段 | `[Path]` | 替换模板 `{name}`，值 URL 转义，`null` 报错 |
| 查询参数 | `[Query]` / 无标注属性 | 无标注的公共可读属性默认按 Query 绑定；数组 `a[]=v`、嵌套 `a[b]=v` 走 AxiosNet params 规则 |
| 请求头 | `[Header]` | `null` 跳过 |
| 请求体 | `[Body]` | 每个模型最多一个；`null` 不发请求体 |
| 排除 | `[ApiIgnore]` | 属性完全不绑定（不进路径/查询串/头/请求体），内部状态与敏感数据用 |

**`[Body]` 可省**：POST/PUT/PATCH 且模型恰好有一个未标注的**非标量**属性（基元类型、枚举、string、decimal、DateTime/DateTimeOffset、TimeSpan、Guid 及其可空形态除外）时，该属性默认按 Body 绑定；其余一切场景（标量、多个未标注属性、GET/DELETE 等）维持 Query，需要时显式标注。

**多特性并存**：同一属性可同时绑定多处（如 `[Path("id")]` + `[Header("X-Id")]` + `[Body]`），值只读一次、分别应用。护栏：`[Path]` / `[Header]` 的属性必须是标量（复杂对象构建期报错）；`[Query]` / `[Body]` 不限。

- 按请求微调（超时 / Content-Type / 序列化器 / responseType）：继承 `ApiRequest<T>` 覆写 `Configure(config)`，或 `RequestAsync(request, config => ...)`（在模型绑定之后执行）
- 模型定义非法（缺路由特性、占位符没绑定、多个请求体、同名参数）在首次调用时抛 `InvalidOperationException`，消息直接指名属性
- 授权在绑定与 `Configure` 之后执行：签名类实现拿到的就是最终 URL 与最终头
- `client.GetUri(request)` 只拼 URL 不发请求；`client.Axios` 进入底层 `AxiosClient`（拦截器 / 连接池 / Handler 注入全部可用）

## Declarative API client

Three things only: **define the request model, define the response model, write the authorization** — and the response type is determined automatically by the request model.

```csharp
[Get("users/{id}")]
public sealed class GetUserRequest : IApiRequest<User>
{
    [Path] public int Id { get; set; }                        // fills {id} (URL-escaped, case-insensitive token match)
    [Query("verbose")] public string? Verbose { get; set; }
    [Header("X-Trace")] public string? Trace { get; set; }
}

[Post("users")]
public sealed class CreateUserRequest : IApiRequest<User>
{
    [Body] public UserPayload Payload { get; set; } = null!;
}

var client = new ApiClient("https://api.example.com/")
{
    Authorization = new BearerAuthorizationProvider(() => GetTokenAsync())
};

var response = await client.RequestAsync(new GetUserRequest { Id = 5 });
Console.WriteLine($"{response.Status} {response.Data!.Name}");
```

Binding rules: `[Path]` → template tokens; unannotated properties (and `[Query]`) → query string; `[Header]` → request headers; `[Body]` → the request body (at most one per model). `[Body]` is optional for single-payload models: on POST/PUT/PATCH, exactly one unannotated non-scalar property (primitives/enums/string/decimal/DateTime/TimeSpan/Guid excluded) binds as the body by default; everything else stays query. Per-request tweaks via the `Configure(config)` hook of `ApiRequest<T>` or the `RequestAsync(request, configure)` overload. Authorization (`IAuthorizationProvider`, built-in `BearerAuthorizationProvider`) runs after binding, so signing implementations see the final URL and headers. `client.Axios` exposes the underlying `AxiosClient` for interceptors, the connection pool, and handler injection.

## Runtime support

Single `net6.0` assembly — runs on .NET 6 / 7 / 8 / 9 / 10 / 11. Zero third-party dependencies.
