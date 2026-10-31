# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/badge/nuget-AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![tests](https://img.shields.io/badge/tests-2057%2F2050%20passed-success)](#构建与测试)
[![coverage](https://img.shields.io/badge/coverage-91.7%25_(branch_87.2%25)-success)](#构建与测试)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**简体中文** | [English](README.en.md)

axios 风格的 .NET HTTP 客户端。写法对齐 JS [axios](https://github.com/axios/axios)，并补齐服务端真正需要的部分：按 origin 复用的连接池、重试、熔断、并发对冲、备用端点、流式响应、在途并发闸门、声明式 API 客户端（模型式 / 接口式）、OpenTelemetry 观测点。

| 项 | 值 |
| --- | --- |
| 包 | `AxiosNet`（核心 + 声明式客户端同包）、`AxiosNet.DependencyInjection`（可选） |
| 目标框架 | `net6.0` / `net8.0` 双目标：.NET 8 及以上取 `lib/net8.0`，.NET 6 / 7 取 `lib/net6.0` |
| 依赖 | 零第三方，只用 BCL |
| 风格 | 全异步：`Task` + `CancellationToken` |
| 观测 | trace / metrics / events 默认开启，无人订阅时零产出 |

> 仓库：[Gitee（itsm/axios-net）](https://gitee.com/itsm/axios-net) 是提交主仓，PR / tag 以 Gitee 为准；[GitHub](https://github.com/sunyuliang/AxiosNet) 为只读镜像。

---

## 目录

| 分组 | 章节 |
| --- | --- |
| 入门 | [安装](#安装) · [快速上手](#快速上手) · [声明式 API](#声明式-api)（[模型式](#模型式-api) / [接口式](#接口式-api)） · [能力地图](#能力地图) · [令牌管理](#令牌管理) |
| 请求与响应 | [客户端](#客户端) · [回放 response.Config](#回放-responseconfig) · [方法与 URL](#方法与-url) · [查询参数 params](#查询参数-params) · [请求体 data 与 ContentType](#请求体-data-与-contenttype) · [请求体压缩](#请求体压缩) · [表单与文件上传](#表单与文件上传) · [流式请求体](#流式请求体) · [响应类型与下载](#响应类型与下载) · [流式响应](#流式响应) · [超时与取消](#超时与取消) · [进度、限速与大小上限](#进度限速与大小上限) · [发送前钩子](#发送前钩子签名--最终请求) · [原始响应字节](#原始响应字节) · [在途并发上限](#在途并发上限) |
| 数据与错误 | [错误处理](#错误处理) · [拦截器](#拦截器) · [数据转换](#数据转换) · [序列化](#序列化) |
| 弹性与治理 | [重试](#重试) · [全局重试预算](#全局重试预算retrybudgetpolicy) · [熔断](#熔断) · [并发对冲](#并发对冲) · [备用端点](#备用端点) · [幂等键](#幂等键) · [尝试管线](#尝试管线) · [短路响应](#短路响应) · [响应缓存](#响应缓存) |
| 连接与观测 | [连接池](#连接池) · [传输档](#传输档) · [HTTP 版本](#http-版本) · [自定义 Handler](#自定义-handler) · [可观测性](#可观测性) |
| 工程化 | [依赖注入](#依赖注入) · [配置速查](#配置速查) · [与 axios 对照](#与-axios-对照) · [示例项目](#示例项目) · [运行时与 AOT](#运行时与-aot) · [构建与测试](#构建与测试) · [性能基准](#性能基准) · [项目结构](#项目结构) · [License](#license) |

按目标选入口：

| 你的目标 | 从哪开始 |
| --- | --- |
| 第一次使用 | [安装](#安装) → [快速上手](#快速上手) → [能力地图](#能力地图) |
| 从 axios 迁移 | [与 axios 对照](#与-axios-对照) → [有意差异](#与-axios-对照) → [快速上手](#快速上手) |
| 只查某个选项 | [配置速查](#配置速查) |
| 做服务端治理（重试 / 熔断 / 限流 / 缓存） | [弹性与治理](#重试) 各组 |
| 排查线上问题 | [错误处理](#错误处理) → [可观测性](#可观测性) |

---

## 安装

```bash
dotnet add package AxiosNet                       # 核心客户端 + 声明式 API 客户端（同一包）
dotnet add package AxiosNet.DependencyInjection   # 可选：DI 注册扩展
```

引用本仓库源码：`dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj`

## 快速上手

两个热门入口，底层能力完全一致，区别只在「请求怎么描述」：

| 入口 | 一句话 | 适合 | 见 |
| --- | --- | --- | --- |
| 原始 HTTP | `Axios.GetAsync<T>(url)`，URL / 参数 / 头写在调用处 | 零散调用、脚本、端点数得过来 | 本节 |
| 声明式 | 每个端点写成模型或接口，调用只剩一句 | 端点多、路由与绑定要集中管理 | [声明式 API](#声明式-api) |

先看原始 HTTP：

```csharp
using AxiosNet;

// 1) 原文：不解析，直接拿字符串
var text = await Axios.GetAsync<string>("https://api.example.com/ping");
Console.WriteLine(text.Data);          // "pong"

// 2) dynamic：不定义 DTO，像 JS 一样按字段取值
var dyn = await Axios.GetAsync<dynamic>("https://api.example.com/users/1");
Console.WriteLine($"{dyn.Data.name}, {dyn.Data.address.city}");   // dyn.Data["name"] 同样可用

// 3) 强类型：Data 直接是 User
var response = await Axios.GetAsync<User>("https://api.example.com/users/1");
Console.WriteLine($"{response.Status} {response.Data!.Name}");
```

要返回什么，由泛型参数决定：

| 写法 | `Data` 的类型 | 适合 |
| --- | --- | --- |
| `GetAsync<string>` | `string`（原文，不解析） | 纯文本接口、调试、自己解析 |
| `GetAsync<dynamic>` | `AxiosDynamic`（成员 / 索引 / 缺省字段返回 `null`） | 字段不固定、快速验证 |
| `GetAsync<T>` | `T` | 生产代码 |
| `GetAsync()`（非泛型） | `JsonElement`（非法 JSON 回退原文，二进制给 `byte[]`） | 只看结构、不建类型 |

带上 baseURL / 超时 / 默认头：

```csharp
// 等价 axios.create({ baseURL, timeout, headers })
var http = Axios.Create(c =>
{
    c.BaseAddress = "https://api.example.com/";
    c.Timeout = TimeSpan.FromSeconds(10);
    c.Headers.Set("Authorization", "Bearer xxx");
});

await http.PostAsync("users", new { name = "bob", age = 30 });   // POST https://api.example.com/users
```

只拼 URL 不发请求（等价 `axios.getUri`）：

```csharp
string url = http.GetUri(c =>
{
    c.Url = "users";
    c.Params = new { page = 2, ids = new[] { 1, 2 } };
});
// -> https://api.example.com/users?page=2&ids[]=1&ids[]=2
```

到这里已经能发请求。接下来先看[声明式 API](#声明式-api)；需要按能力找入口，再往下看[能力地图](#能力地图)。

## 声明式 API

端点一多，URL、参数、请求头散落在每个调用处就难管。声明式入口把每个端点写成**请求模型**或**接口**，调用只剩一句 `client.RequestAsync(request)` / `api.GetAsync(5)`；两种写法由同一个 `ApiClient` 承载，只是描述形式不同。命名空间 `AxiosNet.WebAPI`，与核心同包。

| 写法 | 长什么样 | 适合 | 代价 |
| --- | --- | --- | --- |
| 请求模型 | `class GetUserRequest : IApiRequest<User>`，属性上标 `[Path]` / `[Query]` / `[Body]` | 端点相对独立，请求要当对象传递或复用 | 每个端点一个类型 |
| 接口声明 | `interface IUserApi`，路由与绑定写在方法 / 参数上 | 端点成组，调用像本地方法 | 源生成器烘焙真实代理（JIT / Native AOT 同一路径）；未生成时回退 `DispatchProxy`（仅 JIT） |

两种写法共用同一套内核：路径模板校验、重名检测、默认 Body 推断、标量护栏、静态头、授权语义全部一致。先看请求模型。

### 模型式 API

```csharp
using AxiosNet.WebAPI;

[Get("users/{id}")]
[Headers("Accept: application/json")]                    // 类级静态头
public sealed class GetUserRequest : IApiRequest<User>
{
    [Path] public int Id { get; set; }
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
    Authorization = new BearerAuthorizationProvider(ct => GetTokenAsync(ct))
};

var response = await client.RequestAsync(new GetUserRequest { Id = 5 });
string url = client.GetUri(new GetUserRequest { Id = 5 });   // 只拼 URL
```

| 绑定 | 特性 | 说明 |
| --- | --- | --- |
| 路径段 | `[Path]` | 替换 `{name}`，值 URL 转义，`null` 报错 |
| 查询参数 | `[Query]` / 无标注属性 | 默认绑 Query；数组 `a[]=v`、嵌套 `a[b]=v` |
| 字典查询 | `[Query]` + 字典 | 未命名平铺 `?a=1&b=2`，显式命名保留 `?filters[a]=1` |
| 请求头 | `[Header]` / `[HeaderCollection]` | `null` 跳过 |
| 静态头 | `[Headers]`（类 / 接口 / 接口方法） | `"Name: Value"`；只写名字表示删除该头；方法级覆盖接口级同名项 |
| 表单 | `[Multipart]` + `[Part]` | 模型自身即表单；`[Part]` 覆盖字段名 / 文件名 / 部件类型 |
| 请求体 | `[Body]` | 每个模型最多一个；`null` 不发请求体 |
| 排除 | `[ApiIgnore]` | 完全不绑定 |

- **单负载模型可省 `[Body]`**：POST / PUT / PATCH 且模型只有一个未标注的**非标量**属性时默认按 Body 绑定（字典属性除外，始终按查询串）。
- 一个属性可同时标多个绑定特性（`[Path]` + `[Header]`），值只读一次、分别应用。
- 按请求微调：继承 `ApiRequest<T>` 覆写 `Configure(config)`，或 `RequestAsync(request, config => ...)`。
- 模型定义非法（缺路由、占位符未绑定、多个 Body…）首次调用抛 `InvalidOperationException`；启动期体检用 `ApiClient.ValidateModels(typeof(A), ...)` 或 `ValidateModels(assembly)`（返回全部错误消息，不抛）。按类型重载在 Native AOT 下同样可用（已生成的声明直接走注册表）；`ValidateModels(assembly)` 依赖反射枚举，AOT 下改调生成的 `AxiosNet.Generated.AxiosNetValidation_<程序集名>.ValidateAll()`。
- 授权：`[Authorize]` / `[Authorize("Basic")]` 声明需要授权，`[AllowAnonymous]` 跳过整条链路；`IAuthorizationProvider` 是扩展点，内置 `BearerAuthorizationProvider`。
- 强类型错误：`client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()` 后状态码错误抛 `ApiError<TError>`（网络 / 超时 / 取消不经过工厂）。`ApiError<TError>` 继承自 `AxiosException`，只写 `catch (AxiosException)` 也能接住；`Code` / `ErrorCode` / `Config` / `Request` / `ToJson()` 都从基类继承。
- 其它：`client.UrlParameterFormatter` 接管 `[Path]` / `[Query]` 文本形态；`client.Axios` 直接进底层 `AxiosClient`（拦截器、池、Handler 全可用）；流式正文的超时口径跟随该客户端配置的 `Timeout` / `TotalTimeout`，没有独立的空闲超时旋钮。

### 接口式 API

不想为每个端点写请求模型时，可直接把接口交给 `client.Create<T>()`：路由写在方法上、绑定特性写在参数上。启用包内源生成器时，代理类在编译期生成（JIT / Native AOT 同一路径）；未生成时才回退 `DispatchProxy`（仅 JIT）。

```csharp
[Headers("Accept: application/json")]                    // 接口级静态头
public interface IUserApi
{
    [Get("users/{id}")]
    Task<AxiosResponse<User>> GetAsync(
        [Path] int id,
        [Query("verbose")] string? verbose = null,
        [Header("X-Trace")] string? trace = null,
        CancellationToken cancellationToken = default);

    [Post("users")]
    Task<AxiosResponse<User>> CreateAsync(UserPayload payload, CancellationToken cancellationToken = default);
}

var api = client.Create<IUserApi>();
var response = await api.GetAsync(5);
```

multipart 与流式同样直接写在接口上（语义与请求模型一一对应）：

```csharp
[Post("upload")]
[Multipart]                                              // 参数即表单
Task<AxiosResponse<UploadResult>> UploadAsync(
    [Part("Title")] string title,
    [Part(FileName = "data.bin")] byte[] blob,
    CancellationToken cancellationToken = default);

[Get("stream/events")]
[Stream(AxiosStreamFormat.ServerSentEvents)]            // 省略 = NDJSON
Task<AxiosResponse<IAsyncEnumerable<Chunk>>> EventsAsync(CancellationToken cancellationToken = default);
```

- 每个方法必须标注且只标注一个路由特性。返回类型有四种：`Task<AxiosResponse<T>>`（完整响应）/ `Task<AxiosResponse>`（正文按 `JsonElement` 原样交付）/ `Task<T>`（只交付 `Data`）/ `Task`（只完成请求，不解析正文——非 JSON 正文不会变成解析错误）。不支持同步返回 / `ValueTask` / 泛型方法 / `ref`·`out`。返回 `Task<AxiosResponse<IAsyncEnumerable<T>>>` 即流式方法（裸 `Task<IAsyncEnumerable<T>>` 缺少响应元数据与释放归属，启动期校验即报错）。
- 四种形态的失败口径一致：非 2xx 抛 `AxiosException`，配置了 `ErrorFactory` 时照常替换/包装；用 `Task` 形态时 `ErrorFactory` 仍能拿到响应（只是正文被丢弃）。
- `CancellationToken` 参数按名字识别（最多一个），不参与绑定；其余参数按 `[Path]` / `[Query]` / `[Header]` / `[HeaderCollection]` / `[Part]` / `[Body]` / `[ApiIgnore]` 或「未标注」规则处理，默认 Body 推断同样生效。
- `[Headers]` / `[Authorize]` / `[AllowAnonymous]` 可标在接口或接口方法上；继承基接口时，静态头与授权按**当前派生接口作用域**合并（同一基方法在不同派生接口下可以有不同静态头）。方法级声明压过接口级：静态头同名项覆盖、`[AllowAnonymous]` 可让接口里单个方法匿名、`[Authorize("Basic")]` 可让单个方法换 scheme；方法上同标 `[Authorize]` 与 `[AllowAnonymous]` 属二义性（`Create<T>()` / `ValidateInterfaces` 报错）。
- 接口写在非法（缺路由、多个 Body、`[Part]` 未配 `[Multipart]`、`[Multipart]` 与 `[Body]` 同用、`[Stream]` 用在非流式方法、参数重名…）时，`Create<T>()` 抛 `InvalidOperationException`；启动期体检用 `ApiClient.ValidateInterfaces(typeof(IA), ...)` 或 `ValidateInterfaces(assembly)`（返回全部错误消息，不抛；同一接口的多个方法错误会聚合，而不是只报第一条）。按类型重载在 Native AOT 下同样可用；`ValidateInterfaces(assembly)` 在 AOT 下抛 `PlatformNotSupportedException`，改调生成的 `ValidateAll()`。
- 授权回调与错误工厂拿到的是 `ApiInterfaceInvocation`（接口类型、方法名、参数快照），不是模型实例；需要按请求分支时用这三项。
- 接口级流式：帧格式用 `[Stream(AxiosStreamFormat.ServerSentEvents)]` / `[Stream(AxiosStreamFormat.JsonArray)]` 声明，省略按 NDJSON；返回流只能枚举一次，超时与错误口径与 `RequestStreamAsync(...)` 完全一致。
- 接口方法不支持方法级 `configure` 钩子，需要逐请求微调时用请求模型。
- **Native AOT**：接口所在程序集启用源生成器即生成真实代理（内部接口同样支持）；闭合泛型接口（如 `IRepo<User>`）在调用点 `Create<T>()` / `AddApiInterface<T>()` 出现时生成，只有开放泛型声明本身会给出 `AXA011` 警告。未生成 / 未注册时 AOT 下 `Create<T>()` 抛 `AxiosAotMetadataException`（带修复指引），JIT 下回退 `DispatchProxy`（需 public 接口）。生成路径不要求 `TrimmerRootAssembly`，也不再需要 root 接口类型。

## 能力地图

按能力分四组，点能力名直达对应章节。这里只给入口，边界条件都在章节里。

**请求与响应**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| [四种调用形态](#快速上手) | 原文 / `JsonElement` / `dynamic` / 强类型 | `GetAsync<string>` / `GetAsync()` / `GetAsync<dynamic>` / `GetAsync<T>` |
| [声明式 API](#声明式-api) | 用模型或接口描述请求，`ApiClient` 生成调用 | `[Get("users/{id}")] class X : IApiRequest<User>` / `client.Create<T>()` |
| [客户端实例](#客户端) | 一套 baseURL + 超时 + 默认头 | `Axios.Create(c => ...)` / `new AxiosClient(config)` |
| [只拼 URL](#方法与-url) | 不发请求，拿最终 URL | `client.GetUri(c => ...)` |
| [查询参数](#查询参数-params) | 对象 / 字典 / 集合 / 已编码串 | `c.Params = new { page = 2 }` |
| [请求体](#请求体-data-与-contenttype) | 编码只看 `ContentType`，不猜类型 | `c.ContentType = AxiosContentType.FormUrlEncoded` |
| [表单与上传](#表单与文件上传) | multipart 字段 + 文件，或裸流直传 | `PostFormAsync(...)` / `FormFile` / `Stream` |
| [流式请求体](#流式请求体) | 异步序列边算边发，不缓冲整段 | `data = IAsyncEnumerable<byte[]>`（或 `ReadOnlyMemory<byte>` 块） |
| [响应类型](#响应类型与下载) | JSON / 文本 / 字节 / 流 / dynamic 五种 | `c.ResponseType = ResponseType.Stream` |
| [流式响应](#流式响应) | NDJSON / SSE / JSON 数组 → `IAsyncEnumerable<T>` | `client.RequestStreamAsync<T>(req, AxiosStreamFormat.ServerSentEvents)` |
| [超时与取消](#超时与取消) | 单次尝试预算 + 整条请求总预算 + `CancellationToken` | `c.Timeout = ...` / `c.TotalTimeout = ...` / `cancellationToken:` |
| [令牌管理](#令牌管理) | 令牌缓存、提前刷新、并发刷新单飞、401 重放 | `AccessTokenManager` / `AccessTokenProvider` |

**流量治理**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| [进度与限速](#进度限速与大小上限) | 上传下载进度、字节限速、体积上限 | `c.Callbacks.OnDownloadProgress` / `c.Limits.MaxRate` |
| [在途并发闸门](#在途并发上限) | 限制同时发出的请求条数，排队深度可设上限 | `c.Limits.MaxConcurrentRequests` + `MaxQueueLength` / `QueueTimeout` |
| [弹性重试](#重试) | 指数退避 / 抖动 / `Retry-After` / 幂等门禁 / 全局重试预算 | `c.Retry.Policy = RetryPolicy.Exponential(...)` |
| [熔断](#熔断) | 按 origin（默认 `host:port`）计数的 Closed/Open/HalfOpen 状态机：滚动窗口失败率 + 连续失败两条打开规则，恢复期慢启动 | `c.Circuit.Policy = new CircuitBreaker()` |
| [并发对冲](#并发对冲) | 同一份请求并发多条分支，先成功的胜出 | `c.Hedging.MaxHedges = 1` |
| [备用端点](#备用端点) | 当前端点用不了时换镜像重发；失败端点在客户端内跨请求冷却，主端点挂掉时不再每条请求都先撞它 | `c.Failover.Endpoints = new[] { new Uri("https://mirror") }` |
| [幂等键](#幂等键) | 一个逻辑请求一个键，重试 / 换端点 / 对冲共用 | `c.Idempotency.Enabled = true` |
| [尝试管线](#尝试管线) | 一次尝试 = 可组合中间件链，可插可重排 | `Axios.Create(..., pipeline: AxiosAttemptPipeline.Default.Append(...))` |
| [短路响应](#短路响应) | 不发请求，直接交付响应（缓存 / mock / 回放） | `c.ShortCircuit = new AxiosShortCircuit { ... }` |
| [响应缓存](#响应缓存) | 按 URL 与身份缓存 2xx 响应，命中不发请求；同键并发只发一次；过期条目走 `ETag` / 304 条件复用 | `client.Defaults.Cache = new AxiosCache(...)` |

**数据与错误**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| [统一错误模型](#错误处理) | 所有失败都是 `AxiosException`，带稳定 `Code` | `catch (AxiosException ex) { ex.Code }` |
| [拦截器](#拦截器) | 请求 / 响应双向，可异步 | `client.Interceptors.Request.Use(...)` |
| [数据转换](#数据转换) | axios 同构的两条 transform 链 | `c.TransformRequest.Add(...)` |
| [序列化可替换](#序列化) | JSON（默认）/ XML 内置，任意格式可插 | `AxiosContent.Register(...)` / `client.ContentSerializers` |
| [请求体压缩](#请求体压缩) | gzip / deflate / br，压缩后自动写 `Content-Encoding` | `c.RequestCompression = AxiosCompression.Gzip` |
| [可观测性](#可观测性) | `ActivitySource` + `Meter` + `DiagnosticListener` | `AddSource("AxiosNet")` / `AddMeter("AxiosNet")` |

**连接与工程化**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| [连接池](#连接池) | 每个 origin 一条调优过的 Handler，共享长连接 | 默认自建；`new HttpConnectionPool(...)` 或 `HttpConnectionPool.GetOrCreate("key")` 可共享 |
| [传输档](#传输档) | 一条池多条出口：按请求选代理 / 凭据 / 客户端证书 / Cookie 会话，各档连接与会话互不干扰 | `ConnectionPoolOptions.AddTransport("egress-a", ...)` + `c.Connection.Transport` |
| [HTTP 版本](#http-版本) | 默认 HTTP/2 可降级，支持 HTTP/3 | `c.Connection.HttpVersion` |
| [自定义 Handler](#自定义-handler) | 套 `DelegatingHandler`，或完全接管传输层 | `ConnectionPoolOptions.HandlerFactory` |
| [依赖注入](#依赖注入) | 单例注册，多站点命名解析 | `AddAxiosClient(...)` / `AddApiClient(...)` |
| [Native AOT](#运行时与-aot) | 已声明 `IsTrimmable`（裁剪 0 警告），`net8.0` 目标另开 `IsAotCompatible`（AOT 分析器 0 警告），内置请求模型 / 接口代理源生成器，零 `TrimmerRootAssembly` | 引用即生效；序列化另配 `JsonSerializerContext` |

## 令牌管理

`IAccessTokenProvider` 只是「每次请求取一次令牌」的回调。需要缓存与自动刷新时，用内置的 `AccessTokenManager`：它负责**何时刷新、并发刷新只发一次**，而「怎么换令牌」的 HTTP 调用仍由你给（库不替你调 token 端点）。

```csharp
using var tokens = new AccessTokenManager(new AccessTokenOptions(AcquireAsync)
{
    RefreshAsync = RefreshAsync,                        // 省略时回落到 AcquireAsync
    RefreshAhead = TimeSpan.FromSeconds(30),            // 默认 1 分钟
    RefreshTimeout = TimeSpan.FromSeconds(10)           // 默认 30 秒
});

using var client = new ApiClient("https://api.example.com/") { AccessTokenProvider = tokens };

// 两个回调都返回令牌与过期时间（ExpiresAt 为 null 表示不过期）
static ValueTask<AccessToken> AcquireAsync(CancellationToken ct) { ... }
static ValueTask<AccessToken> RefreshAsync(CancellationToken ct) { ... }
```

- **缓存命中零等待**：令牌未进入刷新窗口时直接返回，不碰回调。
- **并发刷新单飞**：同一时刻只有一个刷新在途，其余请求等同一个结果；刷新失败不缓存，下一次取令牌会重试。
- **刷新不跟随调用方取消**：一个请求断开不会让同批等待者一起失败，也不会白刷一次（刷新自身受 `RefreshTimeout` 约束，超时抛 `TimeoutException`）。
- **提前量自动收敛**：实际提前量 = min(`RefreshAhead`, 令牌寿命的一半)，短寿命令牌不会「刚拿到就该刷」。
- **401 后强制刷新**：`tokens.Invalidate(staleToken)` 作废当前令牌（带旧令牌值可避免并发下把别人刚刷新的新令牌也作废），下一次取令牌必然刷新。
- **401 自动重放**：收到 401 且本次 `Authorization` 头由令牌提供方写入时，刷新令牌后重放**一次**（第二次仍 401 就按错误模型抛出）。正文不可重放（流 / 流式请求体 / multipart）、声明里显式写了 `Authorization`、`[AllowAnonymous]` 一律不重放；重放沿用同一个幂等键。用 `ApiClient.RetryOnUnauthorized = false` 关掉。
- 容器里注册 `IAccessTokenProvider` 即可自动挂到每个 `ApiClient`（见[依赖注入](#依赖注入)）。

## 客户端

三种客户端形态，底层能力完全一致：

| 形态 | 创建方式 | 适用 |
| --- | --- | --- |
| 静态门面 | `Axios.GetAsync(...)` / `Axios.Defaults` / `Axios.Interceptors` | 脚本、单站点应用 |
| 实例 | `Axios.Create(c => ...)` 或 `new AxiosClient(config)` | 常规用法：长生命周期持有，一套默认值 + 一条连接池 |
| 声明式 | `new ApiClient(baseAddress)` | 接口多，用模型或接口描述请求（见[模型式 API](#模型式-api)） |

**实例默认值 vs 单次请求**：`Axios.Create` 里配的是实例默认值，快捷方法末尾的 `configure` 配本次请求。`configure` 拿到的是实例默认值的**副本**，所以能真正摘掉默认头：

```csharp
await http.GetAsync("slow", c => c.Timeout = TimeSpan.FromSeconds(30));   // 只影响这次
await http.GetAsync("x", c => c.Headers.Delete("X-Foo"));                 // 默认头被摘掉
```

- **只在实例级生效**：`Connection.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `PoolOptions`（建池时固化）、`Limits.MaxConcurrentRequests` / `Limits.MaxQueueLength`（首个受限请求时固化）。单次请求写**不同的值**会抛 `ERR_BAD_OPTION_VALUE`（复述实例的值则放行），避免「设了没生效」。
- **`Params` / `Data` 按「有没有赋值过」合并，不按是否为 `null`**：不碰这两项 = 继承实例默认值；显式写 `c.Params = null` / `c.Data = null` = 摘掉实例默认值（与 `Headers.Delete` 用「删除」表达摘头同一个道理）。所以 `PostAsync(url)` 这种不传 `data` 的调用仍会带上 `Defaults.Data`，要发空正文得显式写 `Data = null`。
- **一个站点一个实例，不要按请求新建**：每个实例自建一条连接池，`Axios.Create` / `new AxiosClient` 各建一套 Handler；按请求新建等于退化成 `new HttpClient()` 那个经典坑——长连接不复用，连接与句柄持续增长。要换默认值就换实例，要共享连接就共享池（见[连接池](#连接池)）；DI 场景直接用 `AddAxiosClient` 注册单例。
- 实例用 `using`（或交给 DI 容器持有）收尾，连接池与 Handler 随之释放；在途请求不会被打断，只是不再接受新请求。
- **静态门面可以重置**：`Axios.Reset()` 释放全局默认实例，下次访问 `Axios.Defaults` / `Axios.Interceptors` 或发请求时按全新配置重建。在途请求不受影响（释放不打断它们），旧的默认值与拦截器一并丢弃——典型场景是测试之间隔离全局状态，或默认实例被误释放后恢复（释放过的实例对所有请求恒抛 `ERR_CANCELED`）。

### 回放 `response.Config`

`AxiosResponse.Config`（以及 `AxiosException.Config`）是**那一次请求的生效配置**：实例默认值、末尾 `configure`、请求拦截器都已经合进去。把它交回 `RequestAsync` / 收 `AxiosConfig` 的重载，会当作「按请求项」重新合并到**当前实例**的默认值上：

```csharp
var first = await a.GetAsync("items/1");            // a 的默认值 + 本次请求项
var again = await b.RequestAsync(first.Config!);    // b 的实例级设置 + first 的按请求项
```

**属于实例的项不跟着回放**——永远取当前实例的值，在那个对象上重写也无效：

| 项 | 回放时的取值 |
| --- | --- |
| `BaseAddress` | 当前实例的默认值 |
| `CancellationToken` | 当前实例的默认值（要换令牌，用快捷方法的 `cancellationToken:` 参数） |
| `Connection.Auth` / `Proxy` / `Credentials` / `WithCredentials` / `PoolOptions` / `MaxRedirects` | 当前实例的默认值（上一次的按请求 `Auth` 不会被带走） |
| `Limits.MaxConcurrentRequests` / `Limits.MaxQueueLength` | 当前实例的默认值 |
| `Serialization.ContentSerializers` | 当前实例的注册表（来源实例登记的媒体类型不会带过来） |
| `ShortCircuit` | 不生效（短路响应只属于那一次调用） |

其余按请求项照搬：`Url` / `Method` / `Headers` / `Params` / `Data` / `ContentType` / `RequestCompression` / `Timeout` / `TotalTimeout` / `ResponseType` / 重试 / 熔断 / 对冲 / 备用端点 / 遥测 / 序列化其余项 / `Limits` 其余项 / `Connection.HttpVersion` / `VersionPolicy` / transform 链（作为追加段，与实例默认段按引用去重）。

- 「实例来源」只针对**库产出的配置**：`response.Config`、它的 `Clone()` 副本、快捷方法内部组装的生效配置。自己 `new AxiosConfig { BaseAddress = ... }` 提交的配置不在此列，写什么就用什么。
- 要换主机 / 换令牌再发一次：换实例（`Axios.Create(c => c.BaseAddress = ...)`）或自己写 `new AxiosConfig { Url = ..., Method = ... }`；只想在当前实例上补改几项，用 `RequestAsync(c => ...)` 这类 Action 重载（它在当前实例默认值的副本上修改）。

## 请求

### 方法与 URL

| 方法 | 说明 |
| --- | --- |
| `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` | 每个都有「非泛型 → `JsonElement`」与「泛型 → `T`」两种，都可带 `data`、`configure`、`CancellationToken` |
| `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` | 默认按 `multipart/form-data` 发送 |
| `RequestAsync(config)` / `RequestAsync<T>(config)` | 传完整 `AxiosConfig` |
| `GetUri(config)` / `GetUri(c => ...)` | 只拼 URL，不发请求 |
| `Interceptors` / `Defaults` / `Pool` / `UsesConnectionPool` / `AttemptPipeline` | 实例成员 |

以上七个方法还各有一套直接收完整 `AxiosConfig` 的重载（`GetAsync(url, config)` / `PostAsync(url, config)` …）：`config.Url` / `config.Method` 会被 `url` / 方法名覆盖，其余项按常规合并到实例默认值。`PostAsync(url, null)` 因此按「没有要传的配置」处理，等价于 `PostAsync(url)`（发空正文），不再抛 `ArgumentNullException`。

- `url` 可相对（拼 `BaseAddress`）可绝对；`null` / 空白串抛 `ERR_INVALID_URL`——要请求基地址本身请显式传 `"/"`。
- `Method` 直接用 BCL 的 `HttpMethod`，自定义谓词照发：`new HttpMethod("PROPFIND")`。
- 路径段特殊字符用 `AxiosUrl.EncodePathSegment(value)` 转义。

### 查询参数 params

```csharp
await http.GetAsync("search", c => c.Params = new
{
    q = "a b",                        // q=a%20b
    ids = new[] { 1, 2 },             // ids[]=1&ids[]=2
    filter = new { kind = "book" }    // filter[kind]=book
});

c.Params = "a=1&b=2";                 // 已序列化好的查询串，原样透传
c.Params = new[] { 1, 2 };            // 根集合：[0]=1&[1]=2
```

- 集合形态由 `Serialization.CollectionFormat` 决定：`Brackets`（默认）/ `Multi` / `Csv` / `Ssv` / `Tsv` / `Pipes`。
- 键与值走 `Uri.EscapeDataString`（空格 `%20`）；手写在 `Url` 里的查询串不二次编码。
- `Params` 传标量抛 `ERR_BAD_OPTION_VALUE`（没有键名）。完全自定义：`c.Serialization.ParamsSerializer = p => "a=1&b=2"`。
- 标量文本形态（`Params` / `[Path]` / `[Header]` / 表单共用）：`bool` → `true`/`false`，枚举 → 名字，`DateTime`/`DateTimeOffset` → UTC 毫秒 ISO（`Unspecified` 按 UTC），其它走 `IFormattable` + `InvariantCulture`。

### 请求体 data 与 ContentType

**编码格式只由 `ContentType` 决定，与 `data` 的运行时类型无关**（默认 `application/json`）。

| `ContentType` | 对象 / 字典 / dynamic / 集合 | 基础类型（数值 / 布尔 / 枚举 / Guid / 日期…） | 原始内容（`string` / `byte[]` / `Memory<byte>` / `Stream` / `HttpContent` / 字节块异步序列） |
| --- | --- | --- | --- |
| `application/json`（默认） | 当前序列化器输出 JSON | 原值文本（`42`、`true`、`Paid`） | 原样发送 |
| `application/x-www-form-urlencoded` | 键值对表单（`a[b]=v`、`a[]=v`、null 跳过） | 忽略（空正文） | 原样发送 |
| `multipart/form-data` | 字段 + 文件部件 | 抛 `ERR_BAD_OPTION_VALUE`（放进对象字段） | 抛 `ERR_BAD_OPTION_VALUE`（`HttpContent` 原样透传） |
| 其他（`application/xml` / `yaml`…） | 交给当前序列化器 | 原值文本 | 原样发送 |

```csharp
await http.PostAsync("users", new { name = "bob" });                    // JSON

await http.PostAsync("login", new Dictionary<string, object?>          // 表单
{
    ["username"] = "alice",
    ["tags"] = new[] { "a", "b" }                                       // -> tags[]=a&tags[]=b
}, c => c.ContentType = AxiosContentType.FormUrlEncoded);

await http.PostAsync("bin", bytes, c => c.ContentType = AxiosContentType.OctetStream);   // 二进制：显式声明

await http.GetAsync("reports/query", new { from = "2024-01-01" });      // 任意方法都能带 body
await http.DeleteAsync("users/1", new { reason = "duplicated" });
```

常用常量：`AxiosContentType.Json` / `FormUrlEncoded` / `MultipartFormData` / `OctetStream` / `Xml` / `JsonPatch` / `ProblemJson` / `TextPlain` / `TextHtml`；也可直接写带参字符串：`"multipart/form-data; boundary=----axios-net"`。

### 请求体压缩

`RequestCompression`（`Serialization` 之外的单次 / 实例项，默认 `null` = 不压缩）让库把正文压好再发，并自动写 `Content-Encoding`：

```csharp
await http.PostAsync("users", new { name = "bob" }, c => c.RequestCompression = AxiosCompression.Gzip);

var client = new AxiosClient(new AxiosConfig { RequestCompression = AxiosCompression.Brotli });   // 实例默认
await client.PostAsync(url, data, c => c.RequestCompression = AxiosCompression.None);             // 单次关掉
```

- 算法：`Gzip` / `Deflate` / `Brotli`（`br`），压缩级别固定 `Fastest`——HTTP 的瓶颈通常在网络，省 CPU 比压到最小更划算。
- 只作用于**库按字节缓冲出来的正文**：对象 / 标量 / `string` / `byte[]` / `Memory<byte>` / 表单编码。压缩后 `Content-Length` 是压缩后长度，`Limits.MaxBodyLength` 也按压缩后字节数校验。
- 拒绝（`ERR_BAD_OPTION_VALUE`）：`Stream` 与流式正文（没有完整长度可压，要压缩请先压好再传）、调用方自带的 `HttpContent`（压缩与编码归内容自己）、multipart（部件里可能含流式文件）、以及同时手写 `Content-Encoding` 头（与 `RequestCompression` 二选一）。
- 服务端必须自己按 `Content-Encoding` 解压请求体（ASP.NET Core 等框架默认不解压请求体）；对端不支持时不要开启。

### 表单与文件上传

```csharp
// 1) multipart：字段 + 文件（FormFile / Stream / byte[] / HttpContent 都行）
await http.PostFormAsync("upload", new
{
    title = "季度报告",
    files = new[]
    {
        new FormFile("a.pdf", File.OpenRead("a.pdf"), "application/pdf"),  // 流随请求释放
        FormFile.FromPath("b.zip"),                                        // 库负责打开 / 释放
        FormFile.FromBytes("c.txt", bytes, "text/plain")
    }
});

// 2) 裸流直传（大文件首选，内存占用恒定）
await using var source = File.OpenRead(@"D:\data\report.bin");
await http.PostAsync<UploadResult>("api/files/raw", source, c =>
{
    c.ContentType = "application/octet-stream";
    c.Headers.Set("X-File-Name", "report.bin");     // 裸流没有文件名，用头带过去
});

// 3) 自己拼 MultipartFormDataContent
using var multipart = new MultipartFormDataContent();
multipart.Add(new StringContent("alice"), "name");
await http.PostAsync("upload", multipart);          // 原样透传，保留自带头与边界
```

- 字段名取属性名 / 字典键（识别 `[JsonPropertyName]` / `[JsonIgnore]`），嵌套写 `meta[author]`，集合展开成同名重复部件；规则可用 `Serialization.FormFieldNameSelector` 改。
- 根节点是文件数组时没有键名，用 `FormFile.FieldName` 指定。
- 部件随请求释放（成功 / 异常 / 取消都 `Dispose`）；要复用请先读成 `byte[]`。
- 字段名 / 文件名含 `"`、`\`、CR/LF、控制字符会破坏 `Content-Disposition`，发出前直接报错。
- ASP.NET Core 项目里 `FormFile` 有歧义：`using ClientFile = AxiosNet.FormFile;`。

### 流式请求体

正文由调用方边算边给、库不缓冲：`IAsyncEnumerable<byte[]>` 或 `IAsyncEnumerable<ReadOnlyMemory<byte>>` 直接作为 `data`。

```csharp
// 逐块产出（这里从流里读；数据库游标、压缩管道、日志导出同理）
static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunks(
    Stream source, [EnumeratorCancellation] CancellationToken token = default)
{
    var buffer = new byte[64 * 1024];
    int read;
    while ((read = await source.ReadAsync(buffer, token)) > 0)
    {
        yield return buffer.AsMemory(0, read);
    }
}

await using var file = File.OpenRead(@"D:\data\export.csv");
await http.PostAsync<UploadResult>("ingest", ReadChunks(file), c =>
{
    c.ContentType = "text/csv";          // 与其它原始内容一致：只应用 Content-Type
});
```

- **长度未知**：不预读、不算总长，传输层按 `Transfer-Encoding: chunked`（HTTP/1.1；默认策略下明文 `http://` 落在 1.1）或 HTTP/2 DATA 帧发送。因此 `Limits.MaxBodyLength` 拦不住它——该上限只对能算出长度的正文生效。
- **一次性**：序列被消费后无法回拨，所以开启重试或备用端点时在**发出前**抛 `ERR_BAD_OPTION_VALUE`（不需要重发就关掉它们）。并发对冲不报错：它只在正文能安全并发重放（内存缓冲）时才生效，流式正文按单次发送处理。要可重放，请改用 `byte[]` 或可 seek 的流。
- **块大小由调用方定**：库把每块原样写出，不再切分或合并；64–256 KB 是常见甜点，过小的块会放大 chunked 框架自身开销。
- **只认字节块**：`IAsyncEnumerable<T>`（如 `IAsyncEnumerable<Order>`）不是正文，直接报 `ERR_BAD_OPTION_VALUE` 并给出指引；要发 JSON 数组请先写成 `Stream` 或 `byte[]`。
- **取消**：请求令牌会传给你的迭代器（签名里用 `[EnumeratorCancellation]` 接住），取消即停在上一次 `yield` 之后。
- **307 / 308 重定向**：默认跟随重定向，这两个状态码会把正文再发一遍——库会重新枚举一次序列，所以生产者必须能重启（迭代器重新 `GetAsyncEnumerator`，或自定义实现每次返回新枚举器）。一次性来源（例如直接转发一条已消费的网络流）请把客户端选项 `Connection.MaxRedirects` 设为 `0`（建池时等价于 `PoolOptions.AllowAutoRedirect = false`）。
- **`PipeReader`**：net6 / net8 的基础框架不含 `System.IO.Pipelines`，库不为此引依赖；用 `pipe.Reader.AsStream()` 包成 `Stream` 传入即可（走不可 seek 流路径，同样一块一块写出）。

### 响应类型与下载

```csharp
var typed  = await http.GetAsync<User>("users/1");                        // 强类型
var raw    = await http.GetAsync("users/1");                              // JsonElement
var text   = await http.GetAsync("ping", c => c.ResponseType = ResponseType.Text);
var bytes  = await http.GetAsync("file", c => c.ResponseType = ResponseType.ArrayBuffer);
var stream = await http.GetAsync("file", c => c.ResponseType = ResponseType.Stream);
var dyn    = await http.GetAsync<dynamic>("users/1");                     // 无需 DTO
```

`<Stream>` / `<byte[]>` / `<dynamic>` 会自动切到对应响应类型，不必手写 `ResponseType`。

下载：

```csharp
// 大文件：流式落盘，内存占用恒定
var response = await http.GetAsync<Stream>("files/report.zip");
await using (var source = response.Data!)
await using (var target = File.Create(@"D:\downloads\report.zip"))
{
    await source.CopyToAsync(target);
}

// 小文件
var bytes = (await http.GetAsync<byte[]>("files/icon.png")).Data!;
await File.WriteAllBytesAsync(@"D:\downloads\icon.png", bytes);
```

- 流**必须释放**：读完或释放后连接才回池；只读一半就释放，这条 TCP 连接会被丢弃。
- 下载期间该 origin 的租约保持占用，空闲驱逐不会打断长下载。
- 流式请求遇非 2xx 会立即丢弃响应体并归还连接（`ex.Response.Data` 为 `null`，`Status` / `Headers` 仍可用）。

### 流式响应

`RequestStreamAsync` 把响应逐帧解析成 `IAsyncEnumerable<T>`：`await` 拿到状态码与响应头，帧在枚举时才读。

```csharp
var response = await client.RequestStreamAsync(
    new ChatStreamRequest { Prompt = "hi" },
    AxiosStreamFormat.ServerSentEvents);          // 不传格式 = NDJSON

await foreach (var chunk in response.Data!)
{
    Console.Write(chunk.Text);
}
```

| 格式 | 帧边界 | 典型来源 |
| --- | --- | --- |
| `NewlineDelimitedJson`（默认） | 一行一个 JSON 值，空行跳过 | 日志导出、逐行下发 |
| `ServerSentEvents` | 空行结束一帧，`data: [DONE]` 结束流 | LLM 增量输出、行情推送 |
| `JsonArray` | 顶层数组的每个元素 | 批量 / 分页列表 |

```csharp
var pages = await client.RequestStreamAsync(new ListOrdersRequest(), AxiosStreamFormat.JsonArray);
await foreach (var order in pages.Data!)
{
    Console.WriteLine(order.Id);                   // 元素收尾即交付，不等整个数组下完
}
```

- **超时**：正文与响应头共用同一份预算——流式响应的正文读取也计入 `Timeout`（`TotalTimeout` 生效时是它的剩余额度），读到一半到点即 `ECONNABORTED`；不设超时时正文不限时，靠取消令牌退出。
- **单帧上限**：默认 8 MB，合法 1–64 MB（`Limits.MaxStreamFrameLength`），越界发送前抛 `ERR_BAD_OPTION_VALUE`。没有「不限制」语义，峰值内存约为上限的 2–4 倍。
- **编码**：NDJSON / SSE 要求 ASCII 兼容 charset，宽字符编码取流时报 `ERR_BAD_RESPONSE`；`JsonArray` 必须 UTF-8。UTF-8 BOM 会跳过。
- **所有权**：流只能枚举一次；`await foreach` 结束或 `break` 自动释放，手动 `MoveNextAsync` 需自己 `DisposeAsync`。
- **错误**：状态码错误在 `await` 抛出；帧解析失败 `ERR_BAD_RESPONSE`，中途断流 `ERR_NETWORK`，取消 `ERR_CANCELED`。

### 超时与取消

```csharp
await client.GetAsync("slow", c => c.Timeout = TimeSpan.FromMilliseconds(500));   // 单次
await client.GetAsync("forever", c => c.Timeout = TimeSpan.Zero);                 // 关掉实例默认超时

using var cts = new CancellationTokenSource();                                    // 等价 AbortController
var task = client.GetAsync("slow", cancellationToken: cts.Token);
cts.CancelAfter(TimeSpan.FromSeconds(1));
```

- `Timeout` 是**单次尝试**的预算（建连 + 等响应头 + 读正文），默认 `Zero` 表示不超时；上限约 49.7 天，超出收敛到上限。
- `TotalTimeout` 是**整条逻辑请求**的预算（从调用开始，含拦截器、排队、重试等待、对冲分支）：`Timeout` 会被重试 / 备用端点 / 对冲各自复制一份，`TotalTimeout` 只有一份，每次尝试实际拿到 `min(Timeout, 剩余)`。
- 超时抛 `ECONNABORTED`（消息可用 `Callbacks.TimeoutMessage` 覆盖），取消抛 `ERR_CANCELED`，原始 `OperationCanceledException` 留在 `Cause`。

```csharp
// 调用方承诺「15 秒内一定给出结果」：重试 5 次 × 单次 10s 也不再是上界
await client.GetAsync("orders/scan", c =>
{
    c.Timeout  = TimeSpan.FromSeconds(10);   // 单次尝试
    c.TotalTimeout = TimeSpan.FromSeconds(15);   // 整条请求
    c.Retry.Policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(200));
});
```

- 总预算用尽时下一次尝试**根本不发出**：抛 `ECONNABORTED`，消息指明是第几次（`... before attempt #7 could be sent`）；被总预算压小的那次超时，消息会写 `(remaining TotalTimeout budget; Timeout = 10000ms)`，避免误判成 `Timeout` 没生效。「根本没发出」这一类不参与熔断失败计数、不重试、也不换端点（见[熔断](#熔断)的失败口径）。两者错误码相同，要区分就用 `AxiosException.IsTotalTimeoutExceeded`：`true` = 发出前就耗尽（别重试，预算已经空了），`false` = 发出后超时（下游不响应，换端点有意义）。
- **被预算压小的那次超时到点即收手**：上限本来就取自剩余总预算，到点时剩下的零头（不足一个计时器 tick）已经发不出任何有意义的尝试，因此直接报「预算耗尽」而不是拿这点余额再发一次；同理，退避睡不完剩余预算时也不白等，立刻退回令牌并报耗尽。
- 排队时间**计入** `TotalTimeout`（`Timeout` 则不计排队）——总预算量的是调用方感知的时长；因此剩余预算比 `Limits.QueueTimeout` 更紧时，**排队等待会被剩余预算截断**，失败码是 `ECONNABORTED` 而不是 `ERR_QUEUE_TIMEOUT`（报错要指向真正的约束）。`QueueTimeout` 为空（一直等）时同样受这条约束。
- **流式正文也在预算之内**：正文读取沿用被预算压小后的剩余额度，读到一半到点即 `ECONNABORTED`；长连接（SSE / 大文件下载）不要设 `Timeout` / `TotalTimeout`，改用取消令牌退出。
- `TotalTimeout = null` 不设总预算（默认），`Zero` 与 `null` 同义，负值 `ERR_BAD_OPTION_VALUE`；按请求显式写 `null` 可以关掉实例默认的总预算。

### 进度、限速与大小上限

```csharp
await http.PostAsync("upload", payload, c =>
{
    c.Callbacks.OnUploadProgress   = e => Console.WriteLine($"↑ {e.Progress:P0} {e.Rate / 1024:F1} KB/s 剩余 {e.Estimated}");
    c.Callbacks.OnDownloadProgress = e => Console.WriteLine($"↓ {e.Progress:P0} {e.Rate / 1024:F1} KB/s");
    c.Limits.MaxRate = 1024 * 1024;                 // 单向限速 1 MB/s
    c.Limits.MaxBodyLength = 50 * 1024 * 1024;      // 请求体上限，发送前拦截
    c.Limits.MaxContentLength = 10 * 1024 * 1024;   // 响应体上限，边读边计数
});
```

`AxiosProgressEvent`：`Loaded` / `Total`（未知 `null`）/ `Progress`（0–1，封顶 100%，`Total` 未知为 `null`）/ `Rate`（平均字节每秒）/ `Estimated`（预估剩余）/ `Upload`。

- 回调**同步**执行，耗时逻辑自行转后台；报告节流 50 ms 一次。
- 回调抛异常会中断请求：上传阶段 `ERR_BAD_REQUEST`、下载阶段 `ERR_BAD_RESPONSE`，原异常在 `Cause`。
- `MaxContentLength`：普通响应边读边累计越界即中止；`ResponseType.Stream` 先按声明长度预检，再读取时累计。
- `MaxContentLength` 默认 `null`（不限，与 axios 的 `maxContentLength = -1` 同口径），库不会为此发告警或日志：缓冲型响应（非 `ResponseType.Stream`）会把整个正文读进内存，对不可信或体量未知的下游，生产环境请显式设上限。
- `Content-Length` 只是提示，不是截断依据：声明值比实际正文短时（代理改头、自定义 `HttpContent`、注入的 Handler 不按声明截断）照样读到流结束，既不丢尾部也不算漏 `MaxContentLength`。
- `MaxBodyLength` 只对能算出长度的正文生效（JSON / 表单 / `byte[]` / 可 seek 流）；multipart 与长度未知的正文（不可 seek 的流、`IAsyncEnumerable` 异步序列）不拦截。
- 负值一律 `ERR_BAD_OPTION_VALUE`；`MaxRate = 0` 等于不限速。
- `MaxRate` 的计时从**实际传输首字节**起算：拿到响应到开始读之间的空档（线程排队、业务先做别的事）不算已消耗预算，晚读不会让限速变松。

### 发送前钩子（签名 / 最终请求）

需要在**真正发出之前**改写最终请求（签名、时间戳）时，用 `OnSendingRequest`：

```csharp
await client.PostAsync<OrderResult>("orders", order, c =>
{
    c.Callbacks.OnSendingRequest = async (request, config, ct) =>
    {
        // request.Content 已是按 Data 序列化好的最终字节
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(ct);

        request.Headers.TryAddWithoutValidation(
            "X-Signature", Sign(request.Method.Method, request.RequestUri!.PathAndQuery, body));
    };
});
```

- 触发时点：URL 已合并、正文已序列化、请求头已写入，**取连接与发送之前**；拿到的 `HttpRequestMessage` 就是这条请求真正使用的对象。
- 每次尝试各触发一次：重试 / 换端点 / 对冲的每条分支都重新触发，所以时间戳、nonce 每次发送都是新的。
- 要在多次尝试间**保持不变**的键（幂等键）不要写在这里 —— 每次重试都会换一个值，等于把同一份写操作当成两次。请用 [`Idempotency`](#幂等键)。
- 改写只作用于本次发送：库在触发前给本次尝试复制一份请求消息（正文共享），第一次尝试写进头里的内容不会被后续尝试复制叠加。
- 读正文的代价：已缓冲的正文（JSON / 表单 / `byte[]` / `string`）直接取字节；流式请求体（`IAsyncEnumerable<byte[]>`、不可 seek 的 `Stream`）会被读走，包了进度 / 限速的内容会再序列化一遍 —— 签名这类场景请只对已缓冲的正文下手，或只签请求头。
- 抛异常即中断请求（`ERR_BAD_REQUEST`，原异常在 `Cause`），此时不占连接、请求没有发出；钩子里抛出的取消 / 超时按本次尝试归属（`ERR_CANCELED` / `ECONNABORTED`）。
- 钩子换掉正文同样受 `Limits.MaxBodyLength` 约束：换上的内容长度已知且超限时按 `ERR_BAD_REQUEST` 拒绝（长度未知的正文同上面 `Limits` 所述，不拦截）。
- 按请求设置会整体覆盖实例默认，与其余 `Callbacks` 项一致。

### 原始响应字节

需要在**反序列化之前**拿到响应正文（解密、验签、留档审计）时，用 `OnRawResponse`：

```csharp
await client.GetAsync<User>("users/1", c =>
{
    c.Callbacks.OnRawResponse = (response, bytes) =>
    {
        Console.WriteLine($"{response.Status} {response.MediaType} {bytes.Length}B");
        audit.Write(response.GetHeader("X-Trace"), bytes.Span);
    };
});
```

- 触发时点：正文读完、文本解码与反序列化（以及 `transformResponse`）之前；拿到的是 `HttpClient` 交付的字节（已按 `AutomaticDecompression` / `Decompression` 解压）。
- 参数是 `AxiosResponseInfo`：`Status` / `StatusText` / `Headers`（与 `response.Headers` 同一实例）/ `MediaType` / `CharSet`，可用 `GetHeader` 取单个头。
- 状态码被拒绝时，正文照样先读完再抛错，所以 4xx / 5xx 也会触发（便于记录错误体）。
- `ReadOnlyMemory<byte>` 只在回调期间有效，需要保留请自行复制。
- 只对**非流式**的网络响应触发：`ResponseType.Stream` 没有完整字节，短路响应不经过网络读取路径。
- 每读一份正文触发一次：被重试放弃、被对冲丢弃的分支同样触发，对冲时多条分支会**并发**触发（回调需线程安全）；回调同步执行，抛异常中断请求（`ERR_BAD_RESPONSE`，原异常在 `Cause`）。
- 按请求设置会整体覆盖实例默认，与其余 `Callbacks` 项一致。

### 在途并发上限

`MaxRate` 限的是**字节速度**，拦不住「每秒几百个小请求」——那来自同时在途的请求条数：

```csharp
var client = Axios.Create(c =>
{
    c.Limits.MaxConcurrentRequests = 4;                 // 本实例最多 4 条在途
    c.Limits.MaxQueueLength = 16;                       // 最多 16 条排队，再多就以 ERR_QUEUE_FULL 拒绝
    c.Limits.QueueTimeout = TimeSpan.FromSeconds(2);    // 排上了最多等 2 秒
});

await client.GetAsync("fast", c => c.Limits.QueueTimeout = TimeSpan.Zero);   // 满了立刻失败
```

| 项 | 默认 | 语义 |
| --- | --- | --- |
| `MaxConcurrentRequests` | `null`（不限） | 本实例同时在途条数；正整数 |
| `MaxQueueLength` | `null`（不限） | 排队深度上限：超出立刻 `ERR_QUEUE_FULL`；正整数，只在设了 `MaxConcurrentRequests` 时起作用 |
| `QueueTimeout` | `null`（一直等） | 等槽位上限：`null` 一直等 / `Zero` 不排队 / `>0` 超时失败；实际等待还被 `TotalTimeout` 剩余预算封顶 |

- 排队超时抛 **`ERR_QUEUE_TIMEOUT`**，队列满抛 **`ERR_QUEUE_FULL`**（都不是 `ECONNABORTED`）：请求根本没发出去，该降速 / 扩容 / 退避。
- 两者都是「入口太挤」而不是「下游失败」：默认策略不重试、也不换备用端点——换端点解决不了本实例的槽位竞争，调用方退避后再试。
- 中途某次尝试撞上闸门拒绝时，如果前面已经有真实失败，抛出的仍是那条真实失败（与对冲同一口径）：「没排上队」不会盖掉能定位问题的故障。
- 拒绝计数在 `axiosnet.client.gate.rejections`（`axiosnet.gate.reason` = `queue_timeout` / `queue_full`），可与 `gate.queue_depth` 一起看背压水位。
- `MaxQueueLength` 与 `MaxConcurrentRequests` 一起在首个受限请求时固化：按请求写不同值、或构造后改 `Defaults` 都抛 `ERR_BAD_OPTION_VALUE`（按请求没提这一项不算表态，沿用闸门的值）；要「不排队」用 `QueueTimeout = TimeSpan.Zero`。
- 排队**不计入** `Timeout`（重试每次尝试各自计一次），排队期间可用 `CancellationToken` 取消。
- 槽位覆盖「真正在途」全程：重试每次尝试各自取槽（退避不占位），**流式响应一直占到正文流释放**。
- **自锁风险**：在拦截器 / 进度回调里用同一实例发子请求会互抢槽位，请给 `QueueTimeout` 设上限或换实例。
- **与重试同时开**：`ERR_QUEUE_TIMEOUT` / `ERR_QUEUE_FULL` 本身不重试 —— 闸门满载说明本实例在途已顶格，立刻重发只会再撞同一个闸门。要「等并发降下来再试」，请在调用方外层自己做（降速 / 扩容 / 换实例）。
- 与 `MaxConnectionsPerServer` 分工不同：那个管**连接**（h2 下一条连接可跑很多请求），这里管**请求**。

## 错误处理

所有失败统一抛 `AxiosException`：

```csharp
try
{
    await client.GetAsync("missing");
}
catch (AxiosException ex)
{
    Console.WriteLine(ex.Code);      // ERR_BAD_REQUEST
    Console.WriteLine(ex.Status);    // 404
    Console.WriteLine(ex.Cause);     // 底层异常，可能为 null
    Console.WriteLine(ex.ToJson());  // 结构化快照，可直接进日志
}

if (Axios.IsAxiosError(ex) && !Axios.IsCancel(ex)) { /* ... */ }
```

| Code | 触发场景 |
| --- | --- |
| `ERR_BAD_REQUEST` | 4xx；请求体超 `MaxBodyLength`；请求体序列化失败；上传进度回调抛异常 |
| `ERR_BAD_RESPONSE` | 5xx；响应无法反序列化为目标类型；响应体超 `MaxContentLength`；泛型与 `ResponseType` 不匹配；下载进度回调 / 原始字节回调 / `transformResponse` 抛异常 |
| `ECONNABORTED` | 超过 `Timeout`（含流式正文阶段），或 `TotalTimeout` 总预算已耗尽 |
| `ERR_NETWORK` | 连接被拒 / DNS 失败 / 连接中断 / 正文截断 |
| `ERR_CANCELED` | `CancellationToken` 取消，或客户端已释放 |
| `ERR_QUEUE_TIMEOUT` | 并发槽位一直占满，超过 `QueueTimeout`（请求未发出） |
| `ERR_QUEUE_FULL` | 排队深度已达 `MaxQueueLength`（请求未发出、也没入队） |
| `ERR_CIRCUIT_OPEN` | 熔断打开，请求**没有发出**（`RetryAfter` 给建议等待） |
| `ERR_POOL_EXHAUSTED` | 连接池 origin 基数饱和（全部在途、无可驱逐空闲节点），请求未发出；可观测、默认不自动重发，降扇出 / 调大上限 / `Clear()` 回收后再重试 |
| `ERR_INVALID_URL` | URL 非法、相对地址但没有 `BaseAddress`、非 http(s) scheme、长度超过 65519 字符（与 .NET 10 之前的 `Uri` 上限一致，由库自己守住） |
| `ERR_BAD_OPTION_VALUE` | 选项取值非法（按请求写实例级选项、multipart 头体矛盾、`Params` 是标量、重试 / 熔断配置越界等） |

分支判断优先用 `ex.ErrorCode`（枚举，`switch` 能穷举）或 `ex.Code`（与 axios 对齐的字符串），不要解析消息文本：

```csharp
switch (ex.ErrorCode)
{
    case AxiosErrorCode.ConnAborted: /* 超时：同类请求可以再试一次 */ break;
    case AxiosErrorCode.BadRequest:  /* 4xx：请求本身有问题，重发没用 */ break;
    case AxiosErrorCode.Canceled:    return;
    default: throw;
}
```

`ErrorCode` 与 `Code` 一一对应；`Code` 不是内置常量（例如派生类写入的自定义码）时取 `AxiosErrorCode.Unknown`，此时仍以 `Code` 为准。

自定义合法状态码（默认 2xx）：

```csharp
await client.GetAsync("maybe-404", c => c.Callbacks.ValidateStatus = s => s is >= 200 and < 500);
```

## 拦截器

```csharp
// 请求拦截器：按注册顺序【逆序】执行，同步 / 异步两种重载
client.Interceptors.Request.Use(config =>
{
    config.Headers.Set("X-Trace-Id", TraceId.New());
    return config;
});

client.Interceptors.Request.Use(async config =>
{
    config.Headers.Set("Authorization", $"Bearer {await tokenService.GetTokenAsync()}");
    return config;
});

// 响应拦截器：按注册顺序执行；rejected 分支可吞错并返回兜底响应
client.Interceptors.Response.Use(
    onFulfilled: response => response,
    onRejected: ex =>
    {
        var error = (AxiosException)ex;
        if (error.Code == AxiosException.ErrBadRequest && error.Status == 401)
        {
            // 刷新令牌后重放，或返回兜底数据
        }
        throw ex;
    });

// 移除
var id = client.Interceptors.Request.Use(cfg => cfg);
client.Interceptors.Request.Eject(id);
```

## 数据转换与序列化

两条 transform 链负责「同一种格式内的加工」，序列化器负责「正文整体是什么格式」。请求方向先 transformRequest 再编码，响应方向先反序列化再 transformResponse。

### 数据转换

与 axios 同构的两条链：请求方向在拦截器之后、编码之前；响应方向在反序列化之后。

```csharp
var http = Axios.Create(c =>
{
    c.TransformRequest.Add((data, cfg) =>
    {
        if (data is IDictionary<string, object?> map)
        {
            map["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
        return data;
    });

    // 统一剥掉 { code, data } 外壳
    c.TransformResponse.Add((data, cfg) =>
        data is JsonElement { ValueKind: JsonValueKind.Object } e && e.TryGetProperty("data", out var inner)
            ? inner
            : data);
});

await http.GetAsync("users/1", c => c.TransformResponse.Add((d, _) => Normalize(d)));   // 实例链在前，本次在后
```

- 链是 `IList<Func<object?, AxiosConfig, object?>>`，返回 `null` 合法，会继续往后传。
- `TransformRequest` 的返回值仍按 `ContentType` 编码。
- `TransformResponse` 在 `Json` / `Text` / `ArrayBuffer` / `Dynamic` 下执行，`Stream` 不执行（数据还没读）。

### 序列化

请求体 / 响应体默认由 System.Text.Json 处理，可整体替换。

**JSON 设置三层覆盖**（后者优先）：全局 `AxiosJson.Options` → 实例 `c.Serialization.JsonSerializerOptions` → 单次请求同名项。默认 Web 风格（输出 camelCase、输入大小写不敏感）。替换 `AxiosJson.Options` 请给新实例（STJ 会冻结用过的选项）。

**内置 XML**：

```csharp
using AxiosNet.Serialization;

// 方式 A：全局 / 实例注入，请求头自动变成 application/xml
AxiosContent.Serializer = XmlContentSerializer.Instance;
await http.PostAsync("orders", order);

// 方式 B：按 Content-Type 自动选（同一应用混用多种格式）
AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
await http.PostAsync("orders", order, c => c.ContentType = AxiosContentType.Xml);

// 方式 C：只给这个实例登记（推荐：多个站点 / 测试之间互不干扰）
http.ContentSerializers.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
```

**优先级**（后者优先）：内置 JSON → 全局注册表 `AxiosContent.Register` → 全局 `AxiosContent.Serializer` → 实例 `client.ContentSerializers` → 显式 `c.Serialization.ContentSerializer`。

- 实例注册表是每客户端一份的 `ContentSerializerRegistry`（查表规则与全局表一致：精确匹配 → `text/xml` 等别名 → 结构化后缀 `+xml`），登记不影响其它实例和全局表；测试里改全局表会污染同进程的其它用例，需要隔离就用实例表。
- 按请求替换整张表：`c => c.Serialization.ContentSerializers = new ContentSerializerRegistry()`，本次请求只认这张新表，不回流到实例。
- 表对象是共享的：按请求拿到的 `c.Serialization.ContentSerializers` 就是实例那张表，直接 `.Register(...)` 会改到实例，要只在本次请求生效请 new 一张表再赋值；同一个 `AxiosConfig` 交给多个客户端、或 `Defaults.Clone()` 时也共享同一张表。
- 注册表属实例级：回放 `response.Config` 时注册表取目标实例的，来源实例的登记不会跟着走。

**自定义格式**：实现 `IContentSerializer`（文本格式两个方法；二进制格式再加字节通道 `SerializeToBytes` / `Deserialize(ReadOnlyMemory<byte>)`；可选 `MediaType` 自报媒体类型）。

```csharp
public sealed class YamlSerializer : IContentSerializer
{
    public string? MediaType => "application/yaml";

    public string Serialize(object? value, Type type, AxiosConfig config) => YamlConvert.Serialize(value, type);

    public object? Deserialize(string text, Type type, AxiosConfig config) => YamlConvert.Deserialize(text, type);
}

AxiosContent.Serializer = new YamlSerializer();
```

- 序列化器可写请求头（`config.Headers.Set(...)`），拿到的 `config` 是本次请求独占副本，不会污染实例默认值。
- 响应正文里的业务对象统一走字节入口（合法 UTF-8 文本直接按字节解析，不做 UTF-16 中转）；正文不是合法 UTF-8 时改走文本入口，坏字节按 U+FFFD 替换，与只解码的老行为一致。文本格式无需实现字节入口，接口默认实现会按响应字符集解码后转调文本入口。
- 表单 / multipart、基础类型、`string` / `byte[]` / `Stream` 不经过序列化器。
- 实现需线程安全（会被并发共享）；异常统一包装成 `ERR_BAD_REQUEST` / `ERR_BAD_RESPONSE`。
- 文本编码：`Serialization.RequestEncoding` / `ResponseEncoding`（默认 UTF-8 / 自动识别）。GBK 等代码页需先 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`，否则静默乱码。

## 重试

默认不重试，挂策略即启用：

```csharp
using AxiosNet.Retry;

var client = Axios.Create(c => c.Retry.Policy = new RetryPolicy { MaxRetries = 3 });

config.Retry.Policy = RetryPolicy.Constant(3, TimeSpan.FromSeconds(1));                                      // 固定间隔
config.Retry.Policy = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));    // 指数退避

await client.GetAsync("orders", c => c.Retry.Policy = null);   // 单次关掉实例默认策略（显式 null ≠ 未设置）

client.Defaults.Retry.OnRetry = ctx => logger.LogWarning(
    "第 {Attempt}/{Max} 次失败（{Code}），{Delay}ms 后重试",
    ctx.Attempt, ctx.MaxRetries, ctx.ErrorCode, ctx.Delay.TotalMilliseconds);
```

`RetryPolicy` 默认判定（每项都可改）：

| 情况 | 默认 |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED`（**已发出**后超时） | 重试 |
| `ECONNABORTED`（`TotalTimeout` 在发出前耗尽，`IsTotalTimeoutExceeded`） | 不重试（预算是整条请求的，重发必然在同一处立刻失败） |
| `ECONNABORTED`（单次超时被 `TotalTimeout` 压小，到点时预算已花完） | 不重试（对外仍报 `IsTotalTimeoutExceeded`） |
| 408 / 429 / 5xx | 重试（`RetryStatusCodes` 可换） |
| 其它 4xx、配置 / URL 错误 | 不重试 |
| `ERR_CANCELED` | 不重试 |
| POST / PATCH 等非幂等方法 | 不重试（`RetryNonIdempotentMethods = true` 打开） |

库级强制不重试（自定义策略也改不掉）：`ERR_CANCELED` / `ERR_CIRCUIT_OPEN` / `ERR_QUEUE_TIMEOUT` / `ERR_QUEUE_FULL` / `ERR_BAD_OPTION_VALUE` / `TotalTimeout` 已耗尽（`IsTotalTimeoutExceeded`） —— 这些情况下请求要么没发出，要么立刻重发只会撞同一个闸门，要么是管线里的配置 / 编程错误。

其它成员：`Delay` / `BackoffFactor` / `MaxDelay` / `Jitter`（默认 `true`，等待落在 50%–100%）/ `HonorRetryAfter`（默认 `true`，服务端给了 `Retry-After` 就按它等，仍受 `MaxDelay` 封顶）/ `RetryWhen`。

- `Timeout` 是**每次尝试**的预算（3 次重试 × 10s 最坏 40s）；要「整条请求」的上界用 `TotalTimeout`，也可以在调用方用 `CancellationToken`。
- 拦截器、`transformRequest`、正文序列化只跑一次；响应拦截器看到最终结果。
- 正文必须可重放：可 seek 的流每次重试前自动拨回起点；不可 seek 的流、`StreamContent`（含 multipart 部件）与流式请求体（`IAsyncEnumerable`）在开启重试 / 备用端点时直接抛 `ERR_BAD_OPTION_VALUE`。调用方自带的 `HttpContent` 按**白名单**放行：`ByteArrayContent`（含 `StringContent` / `FormUrlEncodedContent`）、`ReadOnlyMemoryContent`、`JsonContent`（每次从持有的值重新序列化），以及全部部件都可重放的 `MultipartContent`；其余自定义内容一律在**发送前**拒绝 —— 「长度已知」不等于「可重放」，一次性流包一层自定义内容照样只发得出一次。
- 自定义策略：实现 `IAxiosRetryPolicy`（`MaxRetries` / `ShouldRetry(ctx)` / `GetDelay(ctx)`），`ctx` 提供 `Attempt` / `MaxRetries` / `Exception` / `Response` / `StatusCode` / `ErrorCode` / `Delay` / `Config` / `Method`。策略被同一实例的并发请求共享，必须线程安全（推荐无状态；需要跨请求共享状态时自行同步，内置的 `RetryBudgetPolicy` 就是这样）。

### 全局重试预算（`RetryBudgetPolicy`）

逐请求的重试在下游整体故障时会跟着请求数放大：100 个并发请求各重试 3 次就是 300 次多余发送，足够把一次抖动压成一次雪崩。`RetryBudgetPolicy` 给任意策略套一层**实例级共享**的令牌桶，只限速重试、不动首发：

```csharp
// 容量 20、每秒回补 5：允许一次连发 20 次重试，长期稳态 5 次/秒
client.Defaults.Retry.Policy = new RetryBudgetPolicy(capacity: 20, refillPerSecond: 5);

// 内层策略决定「该不该重试 / 等多久」，桶只决定「还有没有预算」
client.Defaults.Retry.Policy = new RetryBudgetPolicy(
    RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200)), capacity: 50, refillPerSecond: 10);

var budget = (RetryBudgetPolicy)client.Defaults.Retry.Policy;
budget.OnBudgetExhausted = ctx => logger.LogWarning("重试预算耗尽，{Code} 直接上抛", ctx.ErrorCode);
budget.RefusedRetries;   // 累计被预算拦下的重试次数，随时可读
```

- **只拦重试，不拦首发**：桶空了只是「不再重试」，调用方拿到的是内层策略判定为可重试的那次**真实失败**，不会被预算改写成新错误。
- **只被「会发出」的重试消耗**：内层或库级说「不该重试」的失败（4xx、取消、闸门拒绝、次数已到顶等）根本不取令牌；总预算已耗尽、这次重试注定发不出去时，刚扣的令牌会立刻退回（只认内建 `RetryBudgetPolicy`，自定义装饰器包住它时识别不到）；已经放行、但退避睡不完剩余总预算的那一次同样会**退回令牌并直接报耗尽**（不再白等一段只为「下一次尝试立刻失败」）—— 共享桶不会被空扣，也就不会连累同实例的其它请求。放行之后才发生的意外（进管线后被闸门拦下）仍计一次：决定那一刻不可预知，多计只让预算更紧，不会放大流量；退避等待期间被取消同样退回令牌 —— 那一发也没发出去。
- **令牌管的是「重试轮次」，不是「发送次数」**：一轮重试若开了备用端点 / 对冲，实际发送量最多放大到 `(1 + 端点数) × (1 + MaxHedges)` 倍（失败会提前结束一轮、跳过的端点不发送，实际通常更小）；要按发送量估算容量，记得除以这个系数。首发不取令牌。
- **桶按策略对象共享**：`Retry.Policy` 在配置克隆间是同一个引用，所以同一个客户端实例的所有请求共用一个桶；某次请求想单独用一个桶，就在那次请求里 `new RetryBudgetPolicy(...)`。
- 桶初始是满的；`refillPerSecond = 0` 表示「只靠初始容量、不随时间恢复」（适合批处理 / 测试）。
- 线程安全，检查路径不产生分配；`OnBudgetExhausted` 在锁外触发，回调里可以放心做日志 / IO。
- 被拒次数计入 `axiosnet.client.retry.budget_exhausted`（无标签，受 `Telemetry.Enabled` 控制）；要按请求上下文处理就用 `OnBudgetExhausted` 回调。

## 熔断

重试解决「这一次失败了要不要再试」；熔断解决「下游整体不可用时还要不要试」。默认不熔断：

```csharp
using AxiosNet.Circuit;

var breaker = new CircuitBreaker
{
    FailureThreshold = 5,                       // 连续 5 次下游故障 → 打开
    WindowSize = 20,                            // 另看最近 20 次的失败率
    MinimumSamples = 10,                        // 样本不足 10 个时不判率
    FailureRateThreshold = 0.5,                 // 窗口内一半失败就打开
    BreakDuration = TimeSpan.FromSeconds(30),   // 冷却 30 秒后放探测请求
    HalfOpenAttempts = 1,                       // 慢启动起点：半开态先放 1 个在途
    ProbeTimeout = TimeSpan.FromSeconds(30),    // 探测 30 秒不回报就整轮作废、重新打开
    RecoverySuccesses = 3                       // 连续 3 次探测成功才全量放开
};

breaker.StateChanged += change => logger.LogWarning(
    "{Host} 熔断：{From} -> {To}", change.Host, change.Previous, change.Current);

var client = Axios.Create(c => c.Circuit.Policy = breaker);

await client.GetAsync("health", c => c.Circuit.Policy = null);   // 探针不该被熔断挡住
breaker.Reset();                                                 // 运维手工恢复
```

状态机：`Closed` →（连续失败达 `FailureThreshold`，**或**窗口内失败率达 `FailureRateThreshold`）→ `Open` →（冷却 `BreakDuration`）→ `HalfOpen` →（连续 `RecoverySuccesses` 次探测成功）→ `Closed`；期间任何探测失败**立刻**回 `Open` 并重新计时。

**两条打开规则为什么并存**：只看连续失败，低流量的「半死不活」下游永远熔不断——每夹一次成功就把计数清零，而它恰恰最该被熔断；只看窗口失败率，小流量又填不满样本。所以各管一段：样本攒够看比率，攒不够时连续失败兜底。窗口是**按最近 N 次结果**算的，与时间无关（不记时间戳，也不需要定期清理），旧账靠覆盖自然衰减。

失败口径与重试门禁对齐（**取消 / 4xx 不计失败**）：

| 情况 | 计入失败？ |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED`（尝试已发出后超时） | 计入 |
| 5xx | 计入 |
| 4xx（含 408 / 429） | 不计入（也不清零） |
| `ERR_CANCELED` | 不计入 |
| 配置 / URL / 序列化 / 反序列化错误 | 不计入 |
| `ECONNABORTED`（`TotalTimeout` 在请求**发出前**耗尽） | 不计入（本地约束先到期，下游一个字节都没收到） |

- **「没发出」不算下游故障**：排队 / 入口处就耗尽 `TotalTimeout` 而抛的 `ECONNABORTED`（`AxiosCircuitContext.TotalTimeoutExpiredBeforeSend`）不进失败计数。过载时排队变长足以让每条请求都撞上它——记成故障就等于「越拥堵越容易熔断健康的下游」，把局部排队放大成整条链路不可用。已经发出去之后的超时仍照常计入，那才是下游不响应。
- **按 origin 计数**：默认维度 `host:port`（`uri.Authority`）；`KeySelector` 可改成 `ctx => ctx.Uri.Host` 或按路由前缀隔离租户。
- **打开条件取「或」**：连续失败达 `FailureThreshold`，或窗口（`WindowSize`）内失败数 ≥ `ceil(样本数 × FailureRateThreshold)` 且样本数达 `MinimumSamples`。`WindowSize = 0` 表示只看连续失败；`MinimumSamples` 大于 `WindowSize` 会在发请求前报 `ERR_BAD_OPTION_VALUE`（那种配法永远触发不了）。
- **慢启动恢复**：半开态允许的在途探测数是 `HalfOpenAttempts + 已连续成功次数`，凑满 `RecoverySuccesses` 次才转闭合。默认 1 → 2 → 3 逐步放宽，避免下游刚缓过来就被打回满负荷。`RecoverySuccesses = 0`（或 1）退化成「探测一次成功即闭合」。探测失败会清零进度并立刻重开；被忽略的结果（4xx / 取消 / 预算耗尽没发出）只归还名额、不影响进度，且只有真的领到名额的探测才推进度。熔断打开前就已放行、冷却结束之后才回报的迟到结果按代次丢弃：它属于上一轮，不会把刚进入的半开打回打开，也不会替这一轮归还探测名额。
- **卡死的探测不会永久占住名额**：半开态的探测在 `ProbeTimeout`（默认 30 秒，`null` 关闭）内没有回报就视为卡死——整轮半开作废、重新打开并重新冷却，迟到的结果按代次丢弃。请求本身没配 `Timeout` 时，一个挂住的探测原本会让该维度永远停在半开；探测自身的超时预算更长时把本项调大（非正值报 `ERR_BAD_OPTION_VALUE`）。
- 被拒请求**不发出**：不建连、不占租约，直接抛 `ERR_CIRCUIT_OPEN`，`AxiosException.RetryAfter` 给建议等待。
- 与重试同时开：闸门在**每次尝试之前**，熔断打开后重试的下一轮会被立刻拦下；`ERR_CIRCUIT_OPEN` 本身不重试。
- 开着重试时**每次尝试各记一笔**：3 次重试 + 首败 = 4 笔。
- **流式响应只按响应头记账**：正文读取发生在记账之后，中途断流（`IOException` / 连接重置）会以 `ERR_NETWORK` 抛给调用方，但不回补熔断失败、也不会触发重试。需要把「正文完整性」纳入失败统计时请在消费方自行处理，或改用 `ResponseType.ByteArray` 让整段正文进入记账范围。
- 状态表有上限（`MaxTrackedHosts`，默认 512，**硬上限**）：满员时先淘汰「丢了不改变判定」的维度（闭合态里优先丢窗口中没有失败样本的那条；连续失败数可能已被一次成功清零，但失败率台账仍然有效）；仍放不下就**不再登记**新维度，新维度按闭合态直通（不参与熔断判定、不记账），已有维度的判定不受影响——熔断保护失效总好过状态表无界增长。未登记的次数见 `circuit.untracked_host` 指标。
- 自定义策略：实现 `IAxiosCircuitPolicy`（`ShouldAllow(ctx)` / `GetRetryAfter(ctx)` / `Record(ctx)`）；`ctx` 提供 `Host` / `Uri` / `Config` / `Exception` / `StatusCode` / `ErrorCode` / `Method` / `IsSuccess` / `IsFailure` / `TotalTimeoutExpiredBeforeSend`。策略**有状态**且被并发共享，需自己保证线程安全。用装饰器包住内置 `CircuitBreaker` 时拿不到准入票（接口上没有票号），迟到结果会按旧口径记账 —— 与 `RetryBudgetPolicy` 被装饰器包住时认不出退费同类的保守偏差。

## 并发对冲

重试与熔断解决「失败了怎么办」；对冲解决「还没失败但太慢了」。默认关闭，`MaxHedges` 是**额外**分支数：

```csharp
var client = Axios.Create(c =>
{
    c.Hedging.MaxHedges = 1;                       // 首发 + 1 条对冲分支
    c.Hedging.Delay = TimeSpan.FromMilliseconds(80); // 第 n 条分支的起跑延迟 = n × Delay（默认 80ms）
});

// 单次请求打开 / 关掉
await client.GetAsync("search", c => c.Hedging.MaxHedges = 2);
await client.GetAsync("orders", c => c.Hedging.MaxHedges = 0);
```

首发照常立刻发出，之后每隔 `Delay` 再放出下一条分支；**第一个成功返回的结果胜出**（不比较耗时，谁先回谁算赢），其余分支取消并收尾。所有分支都失败时把失败交给外层重试。

- **熔断在整组之外，只记一笔**：一次逻辑尝试 = 一次熔断记账，与重试口径一致。所以「一败一成」算成功，「全败」算一次失败，不会因为分支多而放大失败计数。
- **并发额度按 `1 + MaxHedges` 倍占用**：每条分支各自过闸门、各占一个槽位。`Limits.MaxConcurrentRequests` 要按这个倍数预留 —— 留得太小、`QueueTimeout` 又是 `Zero` 时，分支会直接在闸门处 `ERR_QUEUE_TIMEOUT`，对冲静默失效（它只是延迟优化，不会让请求失败）。
- **败者收尾有等待上限**：胜出后最多等败者 2 秒收尾（不等就会在返回后继续占着连接租约与槽位）；超时后改由后台续延，等分支真正结束再释放。库内中间件都响应 `ctx.AttemptToken`，正常情况立即收尾；忽略它的自写中间件最多把调用方拖住 2 秒。调用方取消时不等收尾（全部转后台续延），取消即时生效。
- **`transformResponse` 与响应侧回调在每条分支上各跑一次**（含落败分支）：带副作用的转换请自行保证幂等，或关掉对冲。
- **以下请求按单次发送处理**，不降级也不报错：流式响应（败者的正文无人消费）、非幂等方法（默认 `HedgeNonIdempotentMethods = false`）、正文不能并发重放（`Stream` / multipart / 上传进度包装）、响应侧带共享状态（`OnDownloadProgress` 或 `MaxRate > 0` 时进度会报两份、限速翻倍）。
- 泛型入口照常对冲：每条分支各填一份自己的响应对象，胜出者的那份交给调用方，调用方从 `config.Response` 读到的也是胜出者的状态码与响应头。
- `MaxHedges` 上限 16：再多分支追不回延迟，只会把下游打满。

## 备用端点

当前端点「用不了」时换下一个端点重发同一份请求，顺序即优先级（冷却中的端点自动退到末尾）：

```csharp
var client = Axios.Create(c => c.Failover.Endpoints = new[]
{
    new Uri("https://backup1.example.com"),        // 先试它
    new Uri("https://backup2.example.com/api")     // 还不行再试它（可带路径前缀）
});

// 单次关掉（显式 null / 空集合 = 不换端点）
await client.GetAsync("health", c => c.Failover.Endpoints = null);
```

端点提供 **origin 与路径前缀**，请求自己的 path / query 原样接在后面。请求 `https://primary/v1/users?page=2` + 端点 `https://mirror/api` → `https://mirror/api/v1/users?page=2`（端点前缀是**加**在请求路径前面，不是替换它 —— 别把请求里已有的前缀再写一遍）。

| 情况 | 换端点？ |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED`（**已发出**后超时） / `ERR_POOL_EXHAUSTED` | 换 |
| `ECONNABORTED`（`TotalTimeout` 在请求**发出前**耗尽） | 不换 —— 预算是整条请求的墙钟时间，换端点不会让它变长（见下） |
| 5xx（`ERR_BAD_RESPONSE` 且状态码 ≥ 500） | 换 |
| `ERR_CIRCUIT_OPEN` | 换 —— 熔断按 `host:port` 独立成账，主端点开着挡不住备用端点 |
| 4xx、其它 `ERR_BAD_RESPONSE`（如 2xx 的畸形正文） | 不换（换镜像改变不了） |
| `ERR_QUEUE_TIMEOUT` / `ERR_QUEUE_FULL` | 不换（本机没抢到槽位 / 没排上队，与下游无关） |
| 配置 / 编程错误、取消 | 不换 |

- **预算耗尽不换端点**：与[熔断的失败口径](#熔断)同一条边界 —— `TotalTimeout` 在发出前就耗尽时，换端点既不能让预算变长，又要在备用端点白走一遍管线（占一次熔断判定与闸门入口，然后必然在同一处立刻失败）。已经发出去之后的超时照常换端点，那才是「换个下游可能就好」。
- **端点健康状态跨请求记忆**：同一个端点连续失败达 `FailureThreshold`（默认 2）后进入冷却（`Cooldown`，默认 30 秒，按失败次数翻倍、封顶 `MaxCooldown` 5 分钟）。冷却期内它不再作为首跳，主端点持续不可用时不必每条请求都先白撞一次。只有「换一个下游可能好」的故障计失败（与上表同源），4xx / 取消 / 配置错误既不加也不减；冷却到期自动重新参与，**所有端点都在冷却时仍按原顺序逐个试**——健康表只决定先后，不会把请求挡住。设 `FailureThreshold = 0` 或 `Cooldown = TimeSpan.Zero` 关闭。
- **每次重试都会重新过一遍端点顺序**：端点表在重试之内，一轮换完所有端点才把第一条失败交给重试；健康记忆只影响这一轮的先后，不影响「都试过才算失败」。
- **换端点会重算 `Host`**：调用方显式设过的 `Host` 头在换端点时丢掉，由目标地址重新算 —— 留着会让镜像按原端点的虚拟主机路由（`HttpClient` 只认 `Headers.Host`，它优先于 URI authority）。要按端点用不同的 `Host`，请写尝试中间件按 `ctx.Uri` 设置（`config.Headers` 只应用在模板上）。
- **端点表不能重复**：两个端点拼出同一个 base URL（尾斜杠不算区别）会在发请求前抛 `ERR_BAD_OPTION_VALUE` —— 那只是把同一份请求对同一个地址多打一遍。
- **端点拼出来就是当前地址时跳过**：端点与请求同 origin 且**不带路径前缀**（典型是只写 `https://backup.example.com`）时，换过去等于原地重发，直接跳过继续下一个端点。
- **`Timeout` 是「每个端点每次发送」的预算**：与重试同口径，3 个端点 + 10s 最坏 30s；要整条请求的上界请设 `TotalTimeout`（跨端点共享，见[超时与取消](#超时与取消)）。上传进度同样跨端点累计（与重试重发正文一致，不回跳）。
- **正文要能按顺序重放**：与重试同一份要求（流必须可 seek），不可 seek 的流与 `StreamContent` 在开启备用端点时直接抛 `ERR_BAD_OPTION_VALUE`。
- **流式响应只在响应头阶段能换端点**：正文交给调用方之后，中途断流既不换端点也不重试（与熔断「只按响应头记账」同口径）。
- **端点必须是绝对的 `http` / `https` 地址，且不能带 query / fragment**：端点只当 base URL 用，端点里的 query 与请求自己的 query 怎么拼没有明确规则，因此直接报 `ERR_BAD_OPTION_VALUE`（要让某个参数只出现在备用端点上，请用请求拦截器按 `ctx` 拼 URL）。
- **端点表赋值即快照**：之后改调用方自己的列表不影响已生效的配置；读回来的就是内部那一版，调用方不得修改。
- **换端点会重判 HTTP/3 护栏**：跑不了当前 HTTP 版本的端点（如明文镜像 + 严格 h3）直接跳过继续下一个，全部不可用时才抛 `ERR_BAD_OPTION_VALUE`。
- 每次换端点产生一条尝试事件，`AttemptNumber` 不变、`EndpointIndex` 递增（见[可观测性](#可观测性)）。

## 幂等键

一个**逻辑请求**一个键，写进请求头；重试 / 换端点 / 对冲的各次发送带的是同一个键，服务端因此只把它当作一次写操作。

```csharp
var client = Axios.Create(c => c.Idempotency.Enabled = true);        // 默认关闭

await client.PostAsync("orders", order);                             // Idempotency-Key: <32 位 Guid>
await client.PostAsync("orders", order, c => c.Idempotency.Key = "upstream-7f3a");  // 透传上游的键
```

- **默认关闭**：非幂等方法自动带键会把「重复写」变成「静默去重」，必须由你确认接口支持幂等键。
- **重试本身要先放行非幂等方法**：`RetryPolicy` 默认只重试幂等方法，键再好也不会让 POST 被重发 —— 需要同时打开 `RetryPolicy.RetryNonIdempotentMethods`（见[重试](#重试)）。
- **键在请求模板上写一次**，模板被复制时随头一起搬走 —— 重试不会换键。`OnSendingRequest` 是**每次尝试**一次，在那里写键是错的（每次重试都会换一个键）。
- **一个逻辑请求一个键**：每调用一次 `RequestAsync` 都是新的生效配置、新键；认证失败后的重放要沿用原键，请把它写进 `Idempotency.Key` 再发。
- `Key`（显式值）优先于 `KeyFactory`；两者都没有时用 `Guid` 的 `N` 格式。
- `HeaderName` 默认 `Idempotency-Key`；已经显式写了同名头的请求不会被覆盖。
- 键工厂返回空白、头名非法都在发请求前抛 `ERR_BAD_OPTION_VALUE`。
- **对冲 + 幂等键**：两条分支同键，服务端去重后其中一条会拿到「已处理」的结果。这正是幂等键该有的行为，但要求服务端真的按键去重 —— 否则只是把同一份写操作发了两次。

## 尝试管线

「一次尝试」（单次发送）被抽象成可组合的中间件链，默认链是 **重试 → 备用端点 → 熔断 → 对冲 → 闸门 → 审计 → 发送**。新增每次尝试的能力插一个中间件即可，不必再往客户端里加分支。

```csharp
using AxiosNet.Pipeline;

// 插一个「每次尝试」的探针：Append 落在最内层（审计之后、发送之前），重试 N 次就调用 N+1 次
var pipeline = AxiosAttemptPipeline.Default.Append(next => async (request, ctx) =>
{
    var clock = Stopwatch.StartNew();
    try
    {
        return await next(request, ctx);
    }
    finally
    {
        logger.LogDebug("第 {Attempt} 次尝试耗时 {Ms}ms", ctx.AttemptNumber, clock.ElapsedMilliseconds);
    }
});

using var client = Axios.Create(c => c.Retry.Policy = new RetryPolicy(2), pipeline: pipeline);
```

- **上下文 `AxiosAttemptContext` 是只读结构体**：`Config` / `Uri` / `AttemptNumber` / `EndpointIndex` / `AttemptToken` / `ExternalToken` / `DataType` / `Target` / `StreamResponse` / `Retry` / `Circuit` / `Hedging` / `Failover` / `Slot`，只能用 `WithXxx` 派生副本。它是结构体而不是类，因为中间件链构造期编译一次、每次尝试零分配，且并发类中间件需要按分支拷贝上下文。
- **两个编号分开走**：`AttemptNumber` 由重试推进，`EndpointIndex` 由备用端点推进（`0` = 请求自己的地址）。换端点不推进尝试号，所以同一个尝试号下可能有多条尝试事件。
- **中间件恒在链上，是否干活由它自己看 `ctx.Config` 决定**：没配重试策略时重试中间件就是纯直通。链因此可以在构造时固化，不必每次请求重建。
- **审计默认在链上**：`Attempt.Audit` 是默认链第 6 项（闸门之后、终端之前），订阅 `AxiosNet.Attempt` 就能拿到每次尝试的耗时 / 状态码 / 错误码，不订阅时只做一次开关判定、不建状态机。它已在链上，再 `Append(Attempt.Audit)` 会被当作重复内置项拒绝。
- **顺序是契约**：重试在最外层，于是每次尝试都会重过一遍备用端点、熔断、对冲与闸门；备用端点在熔断之外，于是每个端点各自过熔断；熔断在对冲之外，于是整组分支只记一笔；熔断排在闸门之前，被拒的请求不占槽位。
- **重排与摘除**：`AxiosAttemptPipeline.Create(...)` 显式列出全部中间件（第一个最外层），`Remove(Attempt.Gate)` 摘掉某项；不可变，每次编辑返回新实例。内置项重复出现会报错。
- **不提供「放到最外层」的入口**：那个位置每次请求只调用一次，与响应拦截器完全同构。`InsertBefore(Attempt.Retry, ...)` 会明确拒绝并指向 `client.Interceptors.Response`——它还有 `OnRejected`、运行时增删、覆盖短路三个额外好处。
- **并发槽位不用你管**：闸门自己兜底归还 —— 中间件不调 `next`（自己给一份响应）或自己抛异常，槽位也不会漏。唯一不让闸门归还的情况是**库内**的流式响应：槽位随正文交给 `StreamOwner`，由流的 `Dispose` 释放，正文下载期间该请求一直占着额度。中间件自建 `StreamOwner` 不算这一例外：闸门照常归还自己的槽位，自建凭证不占并发额度。
- **谁创建谁释放**：中间件自己 clone 的 `HttpRequestMessage` 由自己释放（模板仍归管线）。替换响应时必须处置旧的 `AxiosAttemptResult.StreamOwner`，或把它带进新结果（`new AxiosAttemptResult(newResponse, old.StreamOwner)`）—— 它持有连接租约与并发槽位，丢弃即泄漏。
- **中间件自身的耗时不计入 `Timeout`**：超时预算在终端内部起算，闸门排队与中间件前置逻辑都在它之外。
- 响应侧读请求头用 `response.GetRequestHeader(name)`（先查请求头、再查内容头）；短路响应没有底层请求消息，返回 `null`。

与其他扩展点的分工：

| 想做的事 | 用哪个 |
| --- | --- |
| 改 URL / 头 / 请求体、注入短路（一次请求一次） | 请求拦截器 |
| 在序列化前加工要发的 `Data` | `transformRequest` |
| 在发送前改写最终请求消息（签名、时间戳、幂等键，每次尝试） | `c.Callbacks.OnSendingRequest` |
| 在反序列化前拿原始响应字节（每次尝试） | `c.Callbacks.OnRawResponse` |
| 在反序列化后加工回来的 `Data`（每次尝试） | `transformResponse` |
| 加工最终响应、把异常兜成响应 | 响应拦截器 |
| 观察或干预**每一次尝试**（含审计） | 尝试中间件 |
| 改「重试几次 / 退避多久 / 什么时候熔断 / 要不要对冲 / 换哪些端点」 | 对应的 `Policy` 或子配置，不换中间件 |

## 短路与缓存

两者都让请求「不发出也能拿到响应」，区别在谁决定：短路由调用方直接给一份响应，缓存由库按键查。先讲手动的短路，再讲内置缓存。

### 短路响应

有时不该发请求，但仍然需要一份「响应」——缓存命中、mock、离线回放、契约测试。设置 `c.ShortCircuit` 即可：本次请求**不发送**，直接交付这份结果。

```csharp
// 缓存命中：请求拦截器是天然的位置——它只能改配置，ShortCircuit 就是「直接给响应」的出口
client.Interceptors.Request.Use(config =>
{
    if (config.Url is { } url && cache.TryGetValue(url, out var entry))
    {
        config.ShortCircuit = new AxiosShortCircuit
        {
            Status = entry.Status,
            ContentType = entry.ContentType,                              // 含 charset，丢了会乱码
            Headers = new Dictionary<string, string> { ["X-Cache"] = "HIT" },
            Body = entry.Body                                            // 字节：按本次调用的类型重新解析
        };
    }

    return config;
});

// mock / 固定夹具：数据已经成型，原样交付
var response = await client.GetAsync<User>("users/1",
    c => c.ShortCircuit = AxiosShortCircuit.From(new User { Id = 1, Name = "stub" }));
```

跳过的都是「一次 HTTP 操作」本身，保留的是「响应方向」的语义：

| 阶段 | 短路时 |
| --- | --- |
| 并发闸门 / 排队、连接租约、发送、重试、熔断 | 不参与 |
| 遥测 span 与在途指标 | 不产生（命中率由你的缓存自己统计） |
| 范围校验（`Timeout` / 各项上限） | 照常（配置自身的错误不因命中而消失） |
| 按 `ResponseType` / 泛型参数反序列化、`transformResponse` | 照常 |
| `ValidateStatus`（非 2xx 抛 `AxiosException`）、响应拦截器 | 照常 |

- **两种载荷二选一**：`Body` 是原始字节（缓存用它——同一份字节喂给 `GetAsync<User>` 与 `GetAsync<string>` 应得到不同形态）；`Data` 是已成型结果（mock 用它，不解析、不跑 `transformResponse`，所有权归调用方）。
- **`Body` + `Stream` / `byte[]`**：流每次新建 `MemoryStream`，字节数组每次复制一份——两次调用拿到的永远是独立对象，缓存条目不会被某次写入污染。
- **只属于本次请求**：`AxiosConfig.Clone` 会清空它，所以放在 `Defaults` 上不生效；回放 `response.Config` 也不会重放上一次的短路结果。
- **取消仍然生效**：已取消的令牌直接抛 `ERR_CANCELED`，不会因为「正好命中」而返回成功。
- **响应头一并过闸**：`Headers` 的名字必须是合法 token、值不得含控制字符（与请求头同一把尺子），非法时抛 `ERR_BAD_OPTION_VALUE` —— 脏头一样会进日志、喂给 `transformResponse`。
- 可辨痕迹只有 `response.Request == null`（没有发出过请求）；`ProtocolVersion` 不替你编，默认 `null`。
- 缓存条目最少要存 `Status` / `ContentType` / `Body` 三样；响应头的 `Content-Type` 决定文本解码，丢了字符集会让非 UTF-8 正文在命中时乱码。

### 响应缓存

`c.Cache` 是内置的响应缓存：命中时**不发请求**，直接把上次的响应交付（走与[短路响应](#短路响应)同一条路径）。它同时负责**单飞**——同一个键的并发请求只发一次，其余等这一次的结果。

```csharp
var cache = new AxiosCache(new AxiosCacheOptions
{
    TimeToLive = TimeSpan.FromSeconds(30),   // 默认 30 秒
    SingleFlight = true,                     // 默认 true
    MaxEntries = 1024,                       // 默认 1024
    MaxBytes = 64L * 1024 * 1024,            // 默认 64 MB，0 = 不限制
    StoreTimeout = TimeSpan.FromSeconds(2),  // 默认 2 秒，null = 不限时（只约束自建存储）
    FreshnessMode = CacheFreshnessMode.Configured  // 默认：只信 TimeToLive；另有 Server / Intersection
});

client.Defaults.Cache = cache;               // 挂在实例默认值上，整个实例生效

var user = await client.GetAsync<User>("users/1");    // 发请求并落库
var again = await client.GetAsync<User>("users/1");   // 命中：没有发出去
```

| 项 | 默认 | 说明 |
| --- | --- | --- |
| `TimeToLive` | 30 秒 | 条目存活时长，必须大于零 |
| `FreshnessMode` | `Configured` | 缓存时长怎么来：`Configured` 只信配置 TTL；`Server` 只信服务器；`Intersection` 取交集（见下） |
| `SingleFlight` | `true` | 同键并发只发一次 |
| `ConditionalRequests` | `true` | 过期条目带 `ETag` / `Last-Modified` 时发条件请求，304 直接复用缓存的正文（见下） |
| `Store` | `null` | 条目存储，`null` = 内置 `MemoryCacheStore`；接 Redis / 共享缓存时换成自己的 `IAxiosCacheStore` |
| `MaxEntries` | 1024 | 内置内存存储的条目上限：满了先清过期条目，仍满就不再收新条目（自建 `Store` 时本项不生效） |
| `MaxBytes` | 64 MB | 内置内存存储的近似字节上限（正文 + 头长度估算，`0` = 不限制）：和条目满同一口径，放不下就不收新条目（自建 `Store` 时本项不生效） |
| `StoreTimeout` | 2 秒 | 单次落库超时：存储挂起时按「这次没缓存」处理，不把响应一起拖住（`null` / 非正数 = 不限时；只约束自建存储；实际等待还被剩余 `TotalTimeout` 封顶） |
| `KeySelector` | `null` | 自定义键（参数是生效配置与最终 URL），默认 `AxiosCache.CreateKey` |
| `ShouldCache` | 默认口径 | 哪些请求参与缓存，默认为 `AxiosCache.IsCacheable`（GET / HEAD、无短路响应、非流式、无请求体、非范围请求），可在此基础上组合 |

**键 = 方法 + 绝对 URL（+ 凭据摘要）**：凭据（`Authorization` 头或 `Connection.Auth`）参与键，且只以 64 位摘要进键——同一实例上不同用户的同名接口不会串用同一条缓存。用 Cookie 或自定义头区分身份、或要按租户 / 语言分区时，给 `KeySelector`。

**存的是原始正文字节**，命中时按本次调用的 `ResponseType` 与泛型参数重新解析：同一份字节喂给 `GetAsync<User>` 与 `GetAsync<string>` 各得该有的形态。

只有同时满足「状态码 2xx」「拿到了原始字节（非流式）」「`ShouldCache` 通过」才落库；空正文同样算原始字节（HEAD / 204 本就没有正文，缓存的是头）。流式响应、响应拦截器换了响应对象、写缓存失败都只是「这次不入缓存」，不影响请求本身。范围请求（带 `Range` 头）默认不参与缓存，206 / 带 `Content-Range` 的响应也不落库——键只有方法 + URL，分段正文会被后续普通 GET 当成整份内容交付。

**流式请求不参与缓存**：`ResponseType = Stream` 的请求默认连查询都不做（调用方要的是流，命中会把整份正文缓冲进内存）；已落库的条目仍可被普通调用命中。要让流式请求也查缓存，用 `ShouldCache` 放行 —— 但请确认这正是你要的所有权语义。

**三种取值方式**（`FreshnessMode`）：

| 模式 | 落库判定 | 生效 TTL |
| --- | --- | --- |
| `Configured`（默认） | 只走客户端闸门，忽略请求与响应的 HTTP 指令；**落库前不解析响应头**（存储读策略的 HTTP 字段时才按需解析） | `TimeToLive` |
| `Server` | `no-store` / `no-cache` / `Vary: *` / 已过期或非法的 `Expires` 不落库；**没有声明任何新鲜期也不落库** | 服务器剩余新鲜期（`min` 上请求的 `max-age`），不受 `TimeToLive` 上限约束 |
| `Intersection` | 同上（服务器与请求侧否决都优先） | `min(TimeToLive, 剩余新鲜期, 请求的 max-age)`；无新鲜期时用 `TimeToLive` |

剩余新鲜期 = `max-age`（没有则 `Expires - Date`）减去龄期（`Age` 头与「收到时刻 − `Date`」的较大值）。`max-age` 出现但值非法或为负时按零（RFC 9111 §4.2.1：无效的新鲜期信息按已过期处理），此时 `Expires` 不再兜底。两种解析模式都不做 `Last-Modified` 启发式估算。

`Server` / `Intersection` 同时解析**请求**侧的 `Cache-Control` / `Pragma`，与响应侧取交集：

| 请求指令 | 查缓存 | 落库 |
| --- | --- | --- |
| `no-store` | 跳过 | 不落库 |
| `no-cache`（或旧式 `Pragma: no-cache`） | 跳过新鲜查询，但拿旧条目发条件请求（304 直接复用正文） | 仍可落库 |
| `max-age=0`（含非法或负数值，按零处理） | 跳过 | 不落库 |
| `max-age=N`（正数） | 照常 | 落库 TTL 不超过 N |

`Configured` 是「强制按配置执行」：请求与响应的 HTTP 指令都不参与取舍。

**条件请求（`ETag` / 304 复用）**默认开启（`ConditionalRequests = true`）：新鲜条目命中照旧不发请求；只有「新鲜条目没有、但存储里还留着一条带校验器的旧条目」时，把校验器写成条件头再发一次——

- **`ETag` 优先**，写成 `If-None-Match`；没有 `ETag` 才用 `Last-Modified` 写 `If-Modified-Since`；两者都没有就照常完整请求。
- **304 的交付语义**：状态码与正文取缓存里的那一份，响应头按 304 更新（`Content-Length` / `Transfer-Encoding` / `Content-Range` 不覆盖——304 没有正文；`Set-Cookie` 这类可重复的头按多条原样交付，不折叠），随后按新的响应头**重新落库续上 TTL**（这次请求本身允许落库时），下一次调用直接命中。`response.Request` 不为 `null`（与「命中没发请求」区分）。
- **调用方自己写了条件头时以调用方为准**：库既不覆盖它，也不接管由此得到的 304（那仍是一次普通的非 2xx 响应，按 `ValidateStatus` 处理）。
- **存储要配合**：`MemoryCacheStore` 会保留带校验器的过期条目（不带校验器的过期条目照旧在读到时摘除并退还字节配额）。自建存储实现 `IAxiosCacheRevalidationStore.GetRevalidationAsync` 即可参与；只实现基础 `IAxiosCacheStore` 的存储拿不出旧条目，退化成每次完整下载，请求本身不受影响。
- **请求指令照常生效**：`no-store` 不读不写；`no-cache` / `max-age=0` 不发新鲜查询，但会拿旧条目做条件请求（RFC 9111 §5.2.1.4），304 时仍交付缓存的正文。
- **遥测**：304 复用记 `cache.revalidations`（无标签）并在 span 上打 `axiosnet.cache.revalidated=true`；它按未命中计入命中率（请求确实发出去了），单独看这条才知道省了多少带宽。

库按**私有缓存**解释：`private` / `public` 不改变落库判定，`s-maxage` 不采用，`Vary` 不参与键（分区仍用 `KeySelector`；`Vary: *` 在解析模式下不落库）；不支持 `stale-while-revalidate` 之类的 `stale-*` 服务。这些信息不会丢——它们都在 `AxiosCacheEntryPolicy` 里交给存储。

| | 短路响应 | 响应缓存 |
| --- | --- | --- |
| 谁决定「不发」 | 调用方直接给一份响应 | 库按键查缓存 |
| 存活范围 | 只属于那一次请求 | 实例级共享，跨请求复用 |
| 命中时 | 反序列化 / `transformResponse` / `ValidateStatus` / 响应拦截器照常 | 同上 |
| 并发闸门 / 遥测 span | 不参与 | 不参与 |

**命中与落库**

- **命中不发请求**：不占并发闸门、不产生遥测 span 与在途计数，`response.Request` 为 `null`（与短路响应同一处可辨痕迹）。
- **响应头按首值存**：短路响应的头字典是单值的；`Set-Cookie` / `Set-Cookie2` 不进缓存——会话态只属于那一次响应，命中回放等于把它塞给下一次请求。
- **键不含请求体**：默认口径只缓存无正文的 GET / HEAD；用 `ShouldCache` 放行带正文的方法时，必须在 `KeySelector` 里把正文特征算进键，否则不同正文会串用同一条缓存。
- **策略挂在实例上**：一个 `AxiosCache` 就是一份策略；要按接口区分存活时长或键规则，建多个实例分别挂。

**单飞与预算**

- **单飞的失败是共享的**：领导者抛错，等待者拿到同一个错误（同一个只读的 `AxiosException` 实例，`Response` 也是那一份）；但领导者被**它自己的调用方**取消时不算失败——等待者各自补发一次。
- **等待者可以被取消**：`CancellationToken` 取消只放弃等待，不会取消那次共享的发送（`ERR_CANCELED`）。
- **等待受总预算约束**：跟随者的等待不超过剩余 `TotalTimeout`，到点按 `ECONNABORTED`（`IsTotalTimeoutExceeded`）抛出，不会挂在原地等一个慢领导者。
- **查询同样吃总预算**：缓存读取也受剩余 `TotalTimeout` 约束，存储挂起（自建存储不可用 / 网络分区）时按 `ECONNABORTED`（`IsTotalTimeoutExceeded`）抛出，不会把请求挂在查缓存上；调用方取消按 `ERR_CANCELED` 报出，存储自己抛的取消按「没命中」处理。实现 `IAxiosCacheStore.GetAsync` 时应当尊重传入的令牌。
- **落库同样吃总预算**：写入受 `StoreTimeout` 与剩余 `TotalTimeout` 中较短者约束，超出就按「这次没缓存」处理（跟随者各自补发），不会把已经拿到的响应拖过预算；落库前预算就已耗尽时连正文副本都不复制。

**存储与运维**

- **强制清除**：`cache.ClearAsync()` 清空全部条目，`cache.RemoveAsync(key)` 摘掉单条（键由 `cache.KeyFor(config, uri)` 得到）；已发出的请求不受影响，完成后照常落库。
- **自定义存储收到完整策略**：`IAxiosCacheStore` 新增带 `AxiosCacheEntryPolicy` 的 `SetAsync` 重载（生效 / 配置 TTL、HTTP 新鲜期、龄期、解析后的 `Cache-Control`、`Date` / `Expires` / `Vary`）。默认实现转发到旧签名，已经写好的存储不用改；要按 `private` 拒写共享缓存、按 `Vary` 分区、或用 `stale-while-revalidate` 延长可服务窗口时覆写它即可。`Configured` 模式下落库前不解析响应头，这些 HTTP 字段在存储读取时才按需解析；只读 TTL 的存储没有任何解析开销。
- **存储可换**：`AxiosCache` 自己不存条目 —— 存取全交给 `IAxiosCacheStore`（`GetAsync` / `SetAsync` ×2 / `RemoveAsync` / `ClearAsync`，`AxiosShortCircuit` 本身就是「状态码 + 头 + 原始字节」，可直接序列化）。默认实现是进程内 `MemoryCacheStore`；接 Redis 或跨实例共享缓存时实现这个接口，多个客户端挂同一个存储即跨实例命中。存储读写失败只等于「这次没缓存」，请求照发；读取挂起超出剩余 `TotalTimeout` 则按 `ECONNABORTED` 报出（见上）。
- **命名空间是 `AxiosNet.Cache`**（与 `Retry/` / `Circuit/` 一致）：`AxiosCache` / `AxiosCacheOptions` / `CacheFreshnessMode` / `AxiosCacheEntryPolicy` / `CacheControlDirectives` / `IAxiosCacheStore` / `IAxiosCacheRevalidationStore` / `MemoryCacheStore` 都在里面，`AxiosShortCircuit` 仍在 `AxiosNet`（它不是缓存专用的，mock / 回放也在用）。
- 回放 `response.Config` 会带着同一个缓存对象（它是共享状态，不是「那次请求的结果」）。

## 连接池

`AxiosClient` 默认自建一条按 `scheme://host:port`（以及传输档，见[传输档](#传输档)）隔离的连接池（`HttpConnectionPool`）：每个 origin 一个调优过的 `SocketsHttpHandler`，请求共享 TCP 长连接。

> **不要按请求建客户端**（原因见[客户端](#客户端)）：一个站点一个实例；多个客户端要共享连接，就显式传同一条池（下面的用法 2），DI 里用 `AddAxiosClient` 注册单例。
>
> **丢弃实例前必须 `Dispose`**：未释放的池由后台驱逐定时器持有，GC 收不走，它的连接要等到 `IdleHandlerLifetime`（默认 5 分钟）之后才被回收。

```csharp
using System.Net;
using AxiosNet.Pooling;

// 1) 自建池（构造时配置）
var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = true,                                             // 总开关
    Decompression = DecompressionMethods.GZip | DecompressionMethods.Brotli    // 只解这两种（默认 All）
});

// 2) 多客户端共享一条池
await using var sharedPool = new HttpConnectionPool(new ConnectionPoolOptions { MaxConnectionsPerServer = 64 });
using var orderClient = new AxiosClient(pool: sharedPool);
using var payClient   = new AxiosClient(pool: sharedPool);
using var payClient2  = Axios.Create(c => c.BaseAddress = "https://pay.example.com/", sharedPool);

// 3) 看状态 / 手动驱逐
ConnectionPoolStatistics stats = sharedPool.GetStatistics();
sharedPool.EvictIdleEndpoints();

// 4) 命名共享池：按名字取同一条进程级池（options 只在首次创建时生效）
var named = HttpConnectionPool.GetOrCreate("orders", new ConnectionPoolOptions { MaxConnectionsPerServer = 64 });
using var a = Axios.Create(c => c.BaseAddress = "https://order.example.com/", pool: HttpConnectionPool.GetOrCreate("orders"));
using var b = Axios.Create(c => c.BaseAddress = "https://pay.example.com/",   pool: HttpConnectionPool.GetOrCreate("orders"));

HttpConnectionPool.Release("orders");   // 摘除并优雅释放；此后同名再取会新建一条
```

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| `MaxConnectionsPerServer` | 100 | 单站点最大 TCP 连接数，超出的请求在连接层排队 |
| `PooledConnectionLifetime` | 10 分钟 | 连接最长复用时长，到期后下次取用换新（不打断在途）；实际落到 Handler 的值有 ±10% 抖动，避免同一时刻批量断连重连 |
| `PooledConnectionIdleTimeout` | 2 分钟 | 空闲连接关闭 |
| `ConnectTimeout` | 15 秒 | 建连超时 |
| `ConnectCallback` | `null` | 自定义建连：接管「端点 → 流」（UDS / 本机进程内传输 / 自管 DNS）；TLS 仍由 Handler 在返回的流上完成 |
| `MaxResponseHeadersLength` | 64 KB | 响应头体积上限，必须 `> 0`（底层没有「0 = 不限制」的写法）；h1 / h2 都生效，超限按 `ERR_NETWORK` 失败 |
| `IdleHandlerLifetime` | 5 分钟 | origin 节点空闲多久被回收 |
| `EvictionInterval` | 30 秒 | 后台清扫间隔；`<= 0` 不启动定时器 |
| `AllowAutoRedirect` / `MaxAutomaticRedirections` | `true` / 5 | 自动重定向；`MaxRedirects = 0` 关闭 |
| `AutomaticDecompression` | `true` | 自动解压总开关；关掉等价于 `DecompressionMethods.None` |
| `Decompression` | `All` | 解压算法：`GZip` / `Deflate` / `Brotli` 任意组合或 `All`（全开，默认）；`None` 一个都不解，其余位建池时抛 `ERR_BAD_OPTION_VALUE` |
| `EnableMultipleHttp2Connections` | `true` | h2 下允许按 `MaxConnectionsPerServer` 继续加连接 |
| `KeepAlivePingDelay` / `KeepAlivePingTimeout` | `InfiniteTimeSpan`（不发）/ 20 秒 | h2 keep-alive ping 的间隔与等 ACK 的上限；默认与 `SocketsHttpHandler` 一致（关闭），设正值即打开。h1 无影响 |
| `KeepAlivePingPolicy` | `WithActiveRequests` | 何时 ping：`WithActiveRequests`（有在途请求时）/ `Always`（空闲也维持）|
| `MaxEndpointCount` | 1024 | origin 节点上限，满了先驱逐最久未用的空闲节点；若全部在途（无可驱逐），新建节点抛 `ERR_POOL_EXHAUSTED`（请求未发出，可观测、默认不自动重发，降扇出 / 调大上限 / `Clear()` 回收后再重试） |
| `UseCookies` | `false` | 默认档打开后有一份 `CookieContainer`，可通过 `pool.Cookies` 读写（未开启时为 `null`），登出用 `pool.ClearCookies()`；命名传输档各自一份，见[传输档](#传输档) |
| `CookieCapacity` / `CookiePerDomainCapacity` / `CookieMaxCookieSize` | `300` / `20` / `4096` | Cookie 容器容量（总条数 / 单域名条数 / 单条字节数），超出按容器规则淘汰最早的、超长 cookie 直接丢弃；仅 `UseCookies` 打开时生效 |
| `Proxy` / `Credentials` | `null` | 不设代理时**不使用**系统 / 环境代理；要走系统代理请设 `HttpClient.DefaultProxy` |
| `ServerCertificateCustomValidationCallback` / `EnabledSslProtocols` / `ConfigureSslOptions` | `null` | 证书校验、TLS 版本、客户端证书 |
| `HandlerFactory` | `null` | 每个 origin 创建 Handler 时调用，见[自定义 Handler](#自定义-handler) |

- `Dispose()` 是优雅关闭：空闲节点立即释放，有在途请求的节点等租约归还后回收；`Clear()` 立即释放全部节点（会打断在途）。
- DNS 变更的收敛窗口由 `PooledConnectionLifetime` 决定；需要自定义建连（拨到 UDS / 本机传输 / 自管 DNS）时直接设 `ConnectionPoolOptions.ConnectCallback`。
- `ConnectCallback` 与 `MaxResponseHeadersLength` 只作用于库默认 Handler：`HandlerFactory` 里用 `ctx.CreateDefaultHandler()` 建内层 Handler 会带过去，完全自建 Handler 则需自行映射。
- **h2 keep-alive 默认关闭**（与 `SocketsHttpHandler` 一致，不悄悄改变线上行为）：把 `KeepAlivePingDelay` 设成正值（常见 30 秒）即打开，只在有在途请求时发。它对付的是「静默掐断」——AWS ALB 默认 60 秒空闲断连、NAT 表 30–120 秒，客户端以为连接还在池里，下一个请求撞上已关闭的连接，要么多付一次 TLS 握手，要么在 h2 上收到 `GOAWAY` / 流重置（表现为偶发 `ERR_NETWORK`）。代价是一个 17 字节的帧，比一次重连便宜得多；低频高价值调用（webhook 回调、定时任务）可以配 `Always` 连空闲期也维持。反过来，链路上若有**不回应 `PING`** 的代理 / 中间盒，打开后连接会被判死重连（`ERR_NETWORK` 变多）——这也是它默认关闭的原因。
- **`UseCookies` 只按域 / 路径隔离，不按业务身份隔离**：默认档一份容器，命名传输档各一份（见[传输档](#传输档)）。多租户、爬虫、按用户切身份的进程请给每个身份一条档，否则不同身份会串 cookie。容器可用 `pool.Cookies` / `pool.GetCookies("档名")` 直接读写（线程安全），用于启动预置或排查某个 cookie 为什么没发出去；登出用 `pool.ClearCookies()`（会换掉**所有档**的容器并回收现有 origin 节点，后续请求因此重建连接；在途请求不打断，可能仍带旧 cookie）。注意开启容器后请求上的 `Cookie` 头与容器内容是**合并**发送、不能覆盖同名 cookie；`Clear()` / `Dispose()` 都不会清空容器。命名共享池上调用 `ClearCookies()` 会影响所有共用该池的客户端；容器容量默认 300 条 / 单域 20 条 / 单条 4096 字节，超出会静默淘汰最早的，可用 `CookieCapacity` / `CookiePerDomainCapacity` / `CookieMaxCookieSize` 调整。
- **Trailers 目前不读取**：chunked 响应的尾部头（校验和、配额）停留在 `HttpResponseMessage.TrailingHeaders`，没有暴露到 `AxiosResponse`。需要时用 `HandlerFactory` 包一层在正文读完后取，或直接注入自定义 Handler。
- **代理 / 凭据默认是池级单份**：`Proxy` 落在 Handler 实例上，按请求改代理会抛 `ERR_BAD_OPTION_VALUE`。要多出口 IP / 多租户会话，用下面的[传输档](#传输档)在同一条池里挂多套出口，而不是每个出口各建一条池。
- **命名共享池**（用法 4）解决「多客户端想复用连接又不方便传池对象」：key 由调用方给，**不做配置指纹**——`HandlerFactory` / `ConnectCallback` / `Proxy` / `Credentials` / 证书回调这类引用成员没有可靠的结构等价，两份「长得一样」的配置宁可报错也不共用一条池。规则只有三条：同名取同一条；`options` 只在首次创建时生效（重复传一份**不同**的配置抛 `ERR_BAD_OPTION_VALUE`，不静默忽略）；表里的池不归任何客户端所有，客户端 `Dispose` 不会关掉它，收尾用 `Release(key)`。
- 想知道「池是不是被建太多了」看指标 `axiosnet.client.pool.active_pools`（存活池数）与 `.shared_pools`（命名池条目数）。

### 传输档

同一个池里挂多套出口（代理 / 凭据 / 客户端证书 / Cookie 会话），由请求自己选一套：

```csharp
using AxiosNet.Pooling;

// 默认档：ConnectionPoolOptions 上的 Proxy / Credentials / TLS 字段
var pool = new HttpConnectionPool(new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 64
})
.AddTransport("egress-a", new ConnectionTransportOptions
{
    Proxy = proxyA,
    UseCookies = true                                  // 该档自己的 Cookie 会话
})
.AddTransport("egress-b", new ConnectionTransportOptions
{
    Proxy = proxyB,
    ConfigureSslOptions = ssl => ssl.ClientCertificates = tenantBCerts   // 该档自己的 mTLS 证书
});

using var client = new AxiosClient(pool: pool);

await client.GetAsync("orders", c => c.Connection.Transport = "egress-a");
await client.GetAsync("orders", c => c.Connection.Transport = "egress-b");   // 同一个 origin，另一条出口

// 也可以固定在实例默认值上，单次请求写 null 回到默认档
using var tenantClient = new AxiosClient(
    new AxiosConfig { BaseAddress = "https://api.example.com/", Connection = { Transport = "egress-a" } },
    pool: pool);
```

- **档不继承默认档的传输字段**：`Proxy` / `Credentials` / 证书回调 / `ConnectCallback` / `HandlerFactory` / `UseCookies` 都是一整套。想在某个档上继续用默认档的代理，把同一个 `IWebProxy` 实例赋给它（按引用共享，零成本）。
- **池级策略不按档拆**：连接数上限、连接生命周期、空闲驱逐、压缩、重定向、HTTP/2 与 ping 仍由 `ConnectionPoolOptions` 统一决定 —— 它们描述的是「怎么用连接」，不是「从哪出去」。但上限是**按 Handler 生效**的：每个档一套 Handler，所以 N 个档的实际连接总量上限是 `N × MaxConnectionsPerServer`（想要「整池共用一份预算」得自己按档分摊）。
- **每档每 origin 一个独立节点**：独立的 Handler、独立的 TCP 连接、独立的 Cookie 容器（`pool.GetCookies("档名")` 取）。所以 `MaxEndpointCount` 的计数维度是 **origin × 档**，`GetStatistics().Endpoints[i].Transport` 能看出每个节点属于哪一档。
- **未登记的档直接报错**：`c.Connection.Transport = "missing"` 在发出前抛 `ERR_BAD_OPTION_VALUE`（消息里列出已登记的档）—— 静默回落默认档会把「以为走了专线」变成安全事故。
- **注入自定义 Handler 时档无效**：那条路径不走连接池，写非空档名同样报 `ERR_BAD_OPTION_VALUE`；需要多出口请用池 + `AddTransport`，或在 Handler 里自己选路。
- **档自己的 `HandlerFactory` 优先于池级**：工厂通过 `ctx.Profile` / `ctx.Transport` 拿到本档，可以按档返回不同传输实现；`ctx.CreateDefaultHandler()` 会自动带上本档的出口设置。
- **命名共享池会比较档表**：同一 key 上第二次 `GetOrCreate` 传了不同的档名集合或不同的引用成员，抛 `ERR_BAD_OPTION_VALUE`（与其它选项同一口径）。
- 指标不按档打标签（`pool.*` 保持低基数）：要区分出口用 span 的 `server.address` / 审计事件的端点序号，或 `GetStatistics()`。

### HTTP 版本

默认 `HttpVersion = Version20` + `VersionPolicy = RequestVersionOrLower`：https 上协商出 h2 就用 h2，服务端只给 1.1 就在同一次 TLS 握手里降级；明文 `http://` 一律 1.1。

```csharp
var client = new AxiosClient();                                       // 默认即可

await client.GetAsync(url, c => c.Connection.HttpVersion = HttpVersion.Version11);   // 退回 1.1

await client.GetAsync(url, c =>                                        // 要求必须 h2
{
    c.Connection.HttpVersion = HttpVersion.Version20;
    c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
});

await client.GetAsync(url, c => c.Connection.HttpVersion = HttpVersion.Version30);   // h3，不支持则降级
```

| 配置 | 实际发出 |
| --- | --- |
| 默认（`Version20` + `OrLower`） | https 协商 h2，否则 1.1；明文一律 1.1 |
| `Version20` + `Exact` | 只发 h2，协商不上报错（明文上等于 h2c 先验知识） |
| `Version11`（策略默认） | HTTP/1.1 |
| `Version30`（策略默认） | 有 QUIC 且服务端支持就用 h3，否则降级 |
| `Version30` + `Exact` | 只发 h3；环境不支持（明文 / 无 QUIC）发请求前报 `ERR_BAD_OPTION_VALUE`，服务端不支持报 `ERR_NETWORK` |

- 实际版本看 `response.ProtocolVersion`，请求意图看 `Config.Connection.HttpVersion`。
- 本机能否跑 h3 查 `Http3Support.IsSupported` / `UnsupportedReason`（.NET 6 默认关闭 h3，需 runtimeconfig 的 `System.Net.SocketsHttpHandler.Http3Support=true` 或环境变量 `DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=1`，且系统有 MsQuic）。
- `HttpVersion` / `VersionPolicy` 是按请求项，实例默认与单次请求都能设，重试沿用。
- `WithCredentials = true` 走 `CredentialCache.DefaultCredentials`，NTLM / Negotiate 这类连接级认证在 h2 上不可用——面向 IIS / 域内服务请显式 `Version11`。

### 自定义 Handler

| 需求 | 用法 | 走连接池 |
| --- | --- | --- |
| 套一层日志 / 签名 / 埋点 `DelegatingHandler` | `ConnectionPoolOptions.HandlerFactory` | 是 |
| 完全接管连接层（测试桩、自定义传输） | `new AxiosClient(handler: ...)` | 否 |

```csharp
using AxiosNet.Pooling;

// 方式一：每个 origin 创建一次，内层用池配好的 SocketsHttpHandler
using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    HandlerFactory = ctx => new TraceHandler { InnerHandler = ctx.CreateDefaultHandler() }
});

// 方式二：直接注入，不建池
using var client2 = new AxiosClient(
    defaults: new AxiosConfig { BaseAddress = "https://api.example.com/" },
    handler: new TraceHandler { InnerHandler = new SocketsHttpHandler() },
    disposeHandler: true);
```

- `HandlerFactory` 每个 origin 调用一次，必须返回独立实例，由池负责释放；返回 `null` 在首次请求该 origin 时抛 `InvalidOperationException`。
- 注入 Handler 后 `client.Pool` 为 `null`、`UsesConnectionPool` 为 `false`；连接级选项（`MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials`）改由 Handler 决定，在 defaults 上设置抛 `ERR_BAD_OPTION_VALUE`。
- 超时、进度、限速、大小上限等数据层能力不受影响。

## 可观测性

三路观测点默认开启，名字统一 `AxiosNet`。

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(AxiosTelemetry.ActivitySourceName))   // "AxiosNet"
    .WithMetrics(m => m
        .AddMeter(AxiosTelemetry.MeterName)                             // "AxiosNet"
        .AddView(AxiosTelemetry.RequestDurationInstrumentName,
            new ExplicitBucketHistogramConfiguration
            {
                // 耗时直方图按秒，OTel 默认边界是按毫秒设计的，必须自己配
                Boundaries = new[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
            }));
```

**Trace**：一个逻辑请求一个 span（重试不另开），span 名取 HTTP 方法。

| 标签 | 说明 |
| --- | --- |
| `http.request.method` / `url.scheme` / `server.address` / `server.port` / `url.path` | 目标与方法 |
| `url.full` | 完整 URL（去 userinfo，默认不含查询串）；`Telemetry.CaptureUrl = false` 时不写，`CaptureQuery = true` 时才带查询串 |
| `http.response.status_code` / `network.protocol.version` | 状态码 / 真正用到的协议版本 |
| `http.request.body.size` / `http.response.body.size` | 字节数（未知不写） |
| `http.request.resend_count` | 已重试次数 |
| `error.type` / `axiosnet.error.code` | 状态码 → 错误码 → 异常类型名；以及库错误码 |
| `axiosnet.pool.endpoint.reused` | 是否命中池里已有 origin 节点（`false` = 顺手新建） |
| `axiosnet.cache.revalidated` | 这次响应由条件请求的 304 合并出来（正文来自缓存） |

- 4xx / 5xx 即使被 `validateStatus` 接受也标 `Error`；失败另记一条 `exception` 事件；每次决定重试加一条 `retry` 事件（带 `axiosnet.retry.delay_ms` / `axiosnet.retry.reason`，以及 `server.address` / `server.port` —— 与 span / 指标同一份逻辑地址，事件自带是为了只看事件的消费方不必回头拼 span 标签；换端点 / 对冲分支实际连的主机不在标签里，要看那条分支的尝试事件 —— `Attempt.Audit` 默认就在链上，订阅 `AxiosNet.Attempt*` 即可）。失败但收到了响应（4xx / 5xx）时照常写 `http.response.status_code` / `network.protocol.version` / `http.response.body.size`。
- `ResponseType.Stream` 的 span 在拿到响应头时结束，耗时等于首包时间。

**Metrics**（源名 `AxiosNet`）：

| 指标 | 类型 | 单位 | 标签 |
| --- | --- | --- | --- |
| `axiosnet.client.request.duration` | `Histogram<double>` | `s` | method / server.address / server.port / status_code / error.type |
| `axiosnet.client.active_requests` | `ObservableGauge<long>` | `{request}` | 无 |
| `axiosnet.client.request.retries` | `Counter<long>` | `{retry}` | method / server.address / server.port / error.type / `axiosnet.retry.reason` |
| `axiosnet.client.retry.budget_exhausted` | `Counter<long>` | `{retry}` | 无 |
| `axiosnet.client.gate.queue_depth` | `ObservableGauge<long>` | `{request}` | 无 |
| `axiosnet.client.gate.rejections` | `Counter<long>` | `{request}` | `axiosnet.gate.reason`（`queue_timeout` / `queue_full`） |
| `axiosnet.client.circuit.state` | `ObservableGauge<long>` | `{circuit}` | `axiosnet.circuit.state`（`closed` / `open` / `half_open`） |
| `axiosnet.client.circuit.untracked_host` | `Counter<long>` | `{host}` | 无 |
| `axiosnet.client.pool.leases` | `Counter<long>` | `{lease}` | `axiosnet.pool.endpoint.reused` |
| `axiosnet.client.pool.endpoints.created` | `Counter<long>` | `{endpoint}` | 无 |
| `axiosnet.client.pool.endpoints.evicted` | `Counter<long>` | `{endpoint}` | `axiosnet.pool.eviction.reason` |
| `axiosnet.client.pool.active_leases` | `ObservableGauge<long>` | `{lease}` | 无 |
| `axiosnet.client.pool.active_pools` | `ObservableGauge<long>` | `{pool}` | 无 |
| `axiosnet.client.pool.shared_pools` | `ObservableGauge<long>` | `{pool}` | 无 |
| `axiosnet.client.cache.hits` | `Counter<long>` | `{request}` | 无 |
| `axiosnet.client.cache.misses` | `Counter<long>` | `{request}` | 无 |
| `axiosnet.client.cache.stores` | `Counter<long>` | `{entry}` | 无 |
| `axiosnet.client.cache.store_failures` | `Counter<long>` | `{entry}` | 无 |
| `axiosnet.client.cache.revalidations` | `Counter<long>` | `{request}` | 无 |

- 缓存命中率 = `cache.hits / (cache.hits + cache.misses)`（跟随者拿到共享结果计命中；请求侧 `no-cache` / `no-store` 绕开新鲜查询的请求两边都不计，`no-cache` 走成条件请求时只计入 `cache.revalidations`；条件请求拿到 304 计一次未命中）；`cache.store_failures` 单独看，别混进命中率。
- 指标只用低基数标签；URL 路径只进 span。**池指标不带 host / port**：网关 / 爬虫 / 多租户场景下一个 host 一条时间序列会把后端打爆；要定位到具体 origin 看 span 的 `server.address` / `server.port` 与 `axiosnet.pool.endpoint.reused`。
- `axiosnet.retry.reason` 取值有界：有状态码统一报 `status`（具体码在 `error.type`），否则是库错误码（`ECONNABORTED` / `ERR_NETWORK` …）——「该调策略（429）」和「该修下游（5xx）」在面板上直接分得开。
- `gate.queue_depth` 是此刻正在等槽位的请求数（进程级），`gate.rejections` 是入口拒绝计数（排队超时 / 队列满），`circuit.state` 是「多少个熔断维度处于该状态」（熔断器实例 × key），`circuit.untracked_host` 是「状态表满员后按闭合态直通」的次数，`retry.budget_exhausted` 是「想重试但被共享预算拦下」的次数，缓存五件套（`cache.hits` / `misses` / `stores` / `store_failures` / `revalidations`）也不带标签——都不带 host。
- `request.duration` 是逻辑请求耗时（含重试、退避与缓存落库等待）；`active_requests` 请求结束即减（流式到响应头），`active_leases` 要等正文流释放——「长下载占了多少并发额度」看后者。
- 池域指标描述池这个资源，不受 `Telemetry.Enabled` 影响。
- `pool.active_pools` 是存活池数（建 +1 / `Dispose` -1），`pool.shared_pools` 是命名池表的条目数：前者持续上涨 = 有代码在反复建池（典型是「按请求 new 客户端」）。

**Events**（`DiagnosticListener` 名 `AxiosNet.DiagnosticListener`）：`AxiosNet.RequestStart` / `RequestStop` / `RequestFailed` / `Retry`，载荷是公开类型（`AxiosRequestStartEvent` / `StopEvent` / `FailedEvent` / `AxiosRetryContext`）。

**尝试审计**（`AxiosNet.AttemptStop` / `AxiosNet.AttemptFailed`，载荷 `AxiosAttemptEvent`）**默认就在链上**（`Attempt.Audit` 是默认链第 6 项，闸门之后、终端之前），订阅即可：

```csharp
// 默认链已包含 Attempt.Audit；自定义链时显式列出它
var pipeline = AxiosAttemptPipeline.Create(
    Attempt.Retry, Attempt.Failover, Attempt.Circuit, Attempt.Hedging, Attempt.Gate, Attempt.Audit);
using var client = Axios.Create(pipeline: pipeline);
```

- 覆盖**真正到达发送的尝试**：熔断拒绝、闸门排队超时、短路响应都不产生尝试事件（审计在它们之内）。
- 事件条数是「**端点 × 分支**」：换端点每换一个一条（`AttemptNumber` 不变，靠 `EndpointIndex` 区分），对冲每条分支一条；被胜出者取消的败者不发失败事件（那是清理，不是下游故障），在胜者出现之前就真实失败的败者照常发。
- 没人订阅时零成本直通：不建状态机、不取时间戳。
- 再 `Append(Attempt.Audit)` 会作为重复内置项被拒绝（每次尝试发两条事件只会污染口径）。

**按请求关闭或裁剪**：

```csharp
await client.GetAsync<object>("health", c => c.Telemetry.Enabled = false);      // 完全不产出
await client.GetAsync<object>("api",    c => c.Telemetry.CaptureUrl = false);   // 不写 url.full
await client.GetAsync<object>("api?token=...", c => c.Telemetry.CaptureQuery = true);   // 把查询串也写进 url.full
```

## 依赖注入

```csharp
using AxiosNet.DependencyInjection;
using AxiosNet.WebAPI.DependencyInjection;

builder.Services.AddAxiosClient(config =>                    // 只要核心客户端
{
    config.BaseAddress = "https://api.example.com";
    config.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddAxiosClient(                             // 顺带换掉尝试管线
    configure: config => config.Timeout = TimeSpan.FromSeconds(10),
    pipeline: AxiosAttemptPipeline.Default.Append(auditAttempt));

builder.Services.AddApiClient("https://api.example.com",     // 声明式，自建底层客户端
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.Authorization = new BearerAuthorizationProvider(token));

builder.Services.AddApiClient(                               // 声明式做门面，复用上面的 AxiosClient 单例
    configure: client => client.Authorization = new BearerAuthorizationProvider(token));

builder.Services.AddApiClient("a", "https://a.example.com", configure: c => c.Authorization = providerA);   // 多站点
builder.Services.AddApiClient("b", "https://b.example.com", configure: c => c.Authorization = providerB);
```

| 扩展方法 | 注册内容 |
| --- | --- |
| `AddAxiosClient(configure, pipeline)` | 单例 `AxiosClient`（可选尝试管线） |
| `AddApiClient(baseAddress, configureDefaults, configure)` | 单例 `ApiClient`（自建底层客户端） |
| `AddApiClient(configure)` | 单例 `ApiClient`（复用容器里的 `AxiosClient`） |
| `AddApiClient(name, baseAddress, ...)` | 单例 `IApiClientResolver` + 命名站点 |
| `AddApiInterface<T>()` / `AddApiInterface<T>(name)` | 单例接口代理（默认 / 命名站点） |

- TryAdd 语义：已注册不重复注册，不会建出第二条池。
- 容器里的 `IAuthorizationProvider` / `IAccessTokenProvider` / `IApiErrorFactory` 自动挂到 `ApiClient`，`configure` 里显式设置的优先。
- 生命周期归容器：`using` 掉注入的 `ApiClient` 不会释放共享的底层客户端。
- 多站点用 `IApiClientResolver.Get(name)` / `TryGet` / `Names`；.NET 8+ 也可用键名服务（`AddKeyedSingleton` + `[FromKeyedServices]`）。
- 接口级声明可直接注册：`services.AddApiInterface<IUserApi>()`（取未命名 `ApiClient`）或 `AddApiInterface<IUserApi>("a")`（取命名站点）；代理按单例复用，仅在 JIT 下可用。
- 一个接口只能绑一个站点：换站点再注册一次（含未命名 ↔ 命名）在注册期抛 `InvalidOperationException`，不会静默丢掉第二条；两个站点共用方法时定义派生接口再注册到另一个站点。

## 配置速查

`AxiosConfig` 顶层（请求本身）：

| 成员 | 默认 | 说明 |
| --- | --- | --- |
| `BaseAddress` | `null` | 等价 axios `baseURL` |
| `Url` | `null` | 相对或绝对地址 |
| `Method` | `Get` | BCL `HttpMethod`，支持自定义谓词 |
| `Headers` | 空 | `AxiosHeaders`，大小写不敏感，写入即校验 |
| `RemoveHeaders` | 空 | 要摘掉的实例默认头 |
| `Params` | `null` | 对象 / 字典 / 集合 / 已编码串 |
| `ContentType` | `application/json` | 决定 `Data` 的编码方式 |
| `RequestCompression` | `null`（不压缩） | 请求体压缩：`Gzip` / `Deflate` / `Brotli`，`None` 覆盖实例默认 |
| `Data` | `null` | 请求体 |
| `Timeout` | `Zero`（不超时） | 单次尝试的预算（建连 + 响应头 + 正文） |
| `TotalTimeout` | `null`（不设） | 整条请求的总预算：跨重试 / 端点 / 对冲共享，并与 `Timeout` 取小 |
| `ResponseType` | `Json` | `Json` / `Text` / `ArrayBuffer` / `Stream` / `Dynamic` |
| `CancellationToken` | `default` | 外部取消 |
| `TransformRequest` / `TransformResponse` | 空 | 转换链 |
| `Response` | `null` | 响应阶段可读（状态码 / 头 / 媒体类型） |
| `ShortCircuit` | `null` | 短路响应：不发请求直接交付（只对本次请求生效） |
| `Cache` | `null` | 响应缓存（`AxiosCache`）：命中不发请求，跨请求复用 |

十个子对象（首次访问才创建）：

| 子对象 | 关键成员（默认值 / 说明） |
| --- | --- |
| `Serialization` | `JsonSerializerOptions`（Web 风格）/ `ContentSerializer`（`null` = 内置 JSON）/ `ContentSerializers`（实例注册表，构造时必有）/ `RequestEncoding` / `ResponseEncoding` / `ParamsSerializer` / `FormFieldNameSelector` / `CollectionFormat`（`Brackets`） |
| `Connection` | `Auth`（`new AxiosAuth(user, pass)`）/ `MaxRedirects`（5，`0` 不跟随，仅实例级）/ `WithCredentials`（仅实例级）/ `Proxy` / `Credentials` / `PoolOptions`（仅实例级）/ `HttpVersion`（`null` = 2.0）/ `VersionPolicy`（`null` = `OrLower`）/ `Transport`（`null` = 默认档，见[传输档](#传输档)） |
| `Limits` | `MaxContentLength` / `MaxBodyLength` / `MaxRate`（均 `null` = 不限）/ `MaxConcurrentRequests` / `MaxQueueLength`（`null`，仅实例级）/ `QueueTimeout`（`null` 一直等）/ `MaxStreamFrameLength`（`null` = 8 MB，1–64 MB） |
| `Callbacks` | `ValidateStatus`（`Func<int,bool>`，默认 2xx）/ `TimeoutMessage` / `OnUploadProgress` / `OnDownloadProgress` / `OnSendingRequest` / `OnRawResponse` |
| `Retry` | `Policy`（`null` = 不重试）/ `OnRetry` |
| `Circuit` | `Policy`（`null` = 不熔断）/ `OnRejected` |
| `Hedging` | `MaxHedges`（`0` = 不对冲，上限 16）/ `Delay`（80ms，第 n 条分支起跑于 `n × Delay`，`Delay × MaxHedges` 不得超过计时器范围）/ `HedgeNonIdempotentMethods`（`false`） |
| `Failover` | `Endpoints`（`null` = 不换端点；有序的绝对 `http`/`https` 地址，可带路径前缀，不能带 query / fragment，不能重复）/ `FailureThreshold`（2，`0` 关闭健康记忆）/ `Cooldown`（30 秒）/ `MaxCooldown`（5 分钟） |
| `Idempotency` | `Enabled`（`false`）/ `HeaderName`（`Idempotency-Key`）/ `Key`（`null` = 自动生成）/ `KeyFactory`（`null` = `Guid` 的 `N` 格式） |
| `Telemetry` | `Enabled`（`null` = 开启）/ `CaptureUrl`（`null` = 记录，不含查询串）/ `CaptureQuery`（`null` = 不记查询串） |

> 按请求显式赋 `null` 可以关掉实例默认的 `Retry.Policy` / `Circuit.Policy` / `Failover.Endpoints`，这与「没设置」是两种语义。

## 与 axios 对照

| axios (JS) | AxiosNet (.NET) |
| --- | --- |
| `axios(config)` / `axios(url, config)` | `client.RequestAsync(new AxiosConfig { ... })` / `client.GetAsync(url, c => ...)` |
| `axios.get/post/put/patch/delete/head/options` | `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` |
| `axios.create(config)` | `Axios.Create(config)` / `Axios.Create(config, sharedPool)` |
| `axios.getUri(config)` | `client.GetUri(config)` / `client.GetUri(c => ...)` |
| `axios.postForm / putForm / patchForm` | `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` |
| `axios.defaults` | `Axios.Defaults` / `client.Defaults` |
| `res.data / status / statusText / headers / config` | `Data` / `Status` / `StatusText` / `Headers` / `Config`（另有 `ProtocolVersion` / `Request`） |
| `interceptors.request/response.use` / `eject` | `Interceptors.Request/Response.Use` / `Eject` |
| `config.params` / `config.data` | `c.Params` / `c.Data`（编码由 `c.ContentType` 决定） |
| `responseType: 'json' / 'text' / 'arraybuffer' / 'stream'` | `ResponseType.Json` / `Text` / `ArrayBuffer` / `Stream` |
| `validateStatus` / `timeout` / `AbortController` | `c.Callbacks.ValidateStatus` / `c.Timeout` / `c.CancellationToken` |
| `auth: { username, password }` | `c.Connection.Auth = new AxiosAuth(user, pass)` |
| `withCredentials` / `proxy` / `maxRedirects` | `Connection` 子对象同名属性（仅实例级） |
| `maxContentLength` / `maxBodyLength` / `maxRate` | `Limits` 子对象同名属性 |
| `onUploadProgress` / `onDownloadProgress` / `timeoutMessage` | `Callbacks` 子对象同名属性 |
| `paramsSerializer` / `formSerializer` | `Serialization.ParamsSerializer` / `FormFieldNameSelector` |
| `transformRequest` / `transformResponse` | 同名属性 |
| `AxiosError.code` / `isAxiosError` / `isCancel` | `AxiosException.Code` / `Axios.IsAxiosError` / `Axios.IsCancel` |
| `axios-retry` 插件 | `c.Retry.Policy` / `c.Retry.OnRetry` |
| —（需接 opossum 之类） | `c.Circuit.Policy` / `c.Circuit.OnRejected` |
| —（需接 cockatiel 之类） | `c.Hedging.MaxHedges`（并发对冲）、`c.Failover.Endpoints`（备用端点） |
| —（需接 OTel JS SDK） | 内置 `ActivitySource` / `Meter` / `DiagnosticListener` |
| —（axios-cache-interceptor 之类） | `c.Cache = new AxiosCache(...)`（内置响应缓存 + 单飞）、`c.ShortCircuit`（手写缓存命中 / mock / 离线回放） |
| —（无对应） | `HttpConnectionPool`、声明式 `ApiClient`、`RequestStreamAsync`、`MaxConcurrentRequests` / `MaxQueueLength` / `QueueTimeout`、`IAsyncEnumerable` 流式请求体 |

与 axios 的有意差异：

1. 方法名带 `Async` 后缀；`timeout` 用 `TimeSpan`；取消用 `CancellationToken`。
2. 请求体编码由 `ContentType` 决定（默认 JSON），不按 data 类型推断——字典不会自动变表单。
3. 非泛型文本响应的 `Data` 是 `JsonElement`（非法 JSON 回退原文），二进制是 `byte[]`；要 JS 式访问用 `GetAsync<dynamic>`。
4. 实例级选项按请求写**不同的值**会立即抛 `ERR_BAD_OPTION_VALUE`（axios 是静默不生效）。
5. `params` 用 `Uri.EscapeDataString` 严格编码（空格 `%20`，不是 `+`）。
6. `transformRequest` / `transformResponse` 的元素类型是 `object?`（C# 无法像 JS 那样丢失类型再收窄）。
7. 请求头写入即校验（RFC 7230 token、禁止 CR/LF），非法直接抛 `ArgumentException`。
8. `MaxRate` / 进度回调实现在 `HttpContent` 与 `Stream` 层（BCL 无原生支持）。
9. `Timeout` 上限约 49.7 天，超出收敛到上限；真正无限超时用 `Timeout = Zero`。

## 示例项目

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) 是离线可运行的控制台示例，自带 `TcpListener` 演示服务器，共 **51 项**：

```bash
dotnet run --project samples/AxiosNet.Simple
```

启动后是交互菜单（反射列出全部示例，输入编号运行，`a` 跑全部，`q` 退出）。

| 套件 | 项数 | 覆盖 |
| --- | ---: | --- |
| 核心 | 36 | 静态门面与 `Axios.Reset`、实例创建、JSON / 表单 / multipart、params、Basic Auth、dynamic 请求与响应、text / arraybuffer、拦截器、错误与取消、连接池、上传下载、getUri 与大小限制、进度限速、自定义 Handler、序列化替换（Newtonsoft / XML / 二进制）、DNS 切换、Bearer 令牌、请求签名、弹性重试、全局重试预算、幂等键、HTTP/2·3、可观测性、熔断、短路响应、响应缓存、命名共享池、尝试管线、并发对冲与备用端点、排队上限与背压 |
| 声明式 | 14 | GET 详情 / 列表、POST 创建、授权与 `[AllowAnonymous]`、错误处理、全局日志、签名验签、流式响应（SSE / JSON 数组）、multipart 上传、单帧上限、接口级声明、接口级授权与校验、接口级 multipart、接口级流式 |

另有两个示例：

- [`samples/AxiosNet.Simple.DependencyInjection`](samples/AxiosNet.Simple.DependencyInjection)：Generic Host 六个 DI 场景。
- [`samples/AxiosNet.Aot`](samples/AxiosNet.Aot)：Native AOT + 裁剪，自带回环服务。

<a id="native-aot"></a>

## 运行时与 AOT

类库 `net6.0` / `net8.0` 双目标：.NET 8 及以上引用 `lib/net8.0`，.NET 6 / 7 引用 `lib/net6.0`，无需配置。两份资产来自同一份源码，运行时行为不随 TFM 变化（都绑定到消费方运行时自带的 BCL）；`net8.0` 目标的价值是可以使用 net8 一档的 BCL 和 net7+ 的分析器。不支持 .NET Framework 与 .NET Core 3.1 及更早。

两个目标都声明 `IsTrimmable`（自身裁剪 0 警告），`net8.0` 目标另开 `IsAotCompatible`，AOT 分析器 0 警告（`net6.0` 目标上 SDK 不提供该分析器）。

### 请求模型与接口：内置源生成器

包内自带 C# 源生成器（打包在 `analyzers/dotnet/cs`，引用即生效），编译期为每个带路由特性的请求模型烘焙元数据声明，并为接口级声明生成真实代理类（含继承接口、授权、multipart、流式），全部在模块初始化器里注册。它取代了旧版「root 模型程序集」的做法：

- **零配置**：不需要 `TrimmerRootAssembly`，也不需要手写注册；
- **两条路径同一份声明**：生成器只写事实（路由 / 成员 / 绑定 / 授权），校验与绑定计划仍由运行时共享构建器完成，JIT 与 AOT 的行为、错误文本一致；
- **运行时不再反射模型 / 接口**：请求模型把成员取值写成直接 getter 委托；接口代理只做「取参 → 调用共享绑定实现」，不 `MakeGenericMethod`；AOT 下未命中注册表会抛 `AxiosAotMetadataException`（带修复指引），不会静默丢字段；
- **JIT 专用的反射实现整段被裁掉**：接口声明 / 请求模型 / 自定义字典 / 对象形状的反射读取器与 `DispatchProxy` 回退（集中在 `Reflection/` 与 `WebAPI/Reflection/`），只写在 `RuntimeFeature.IsDynamicCodeSupported` 为 true 的分支里；ILC 折成常量 `false` 后整段不可达，连同它们的类型一起被裁剪，包消费方发布时不会为「只在 JIT 下用得到」的代码付体积；
- **接口代理的生成位置**：非泛型接口在声明处生成；闭合泛型接口（`IRepo<User>`）在 `Create<T>()` / `AddApiInterface<T>()` / `ValidateInterfaces(typeof(...))` 调用点生成，开放泛型声明本身（含外层类型为开放泛型的嵌套接口）只提示 `AXA011`（同一编译里已有闭合调用点时不报）。被引用程序集已经为同一接口生成过代理时，使用方按生成的类名识别并直接复用对方注册，不重复生成（重复注册会在启动期抛错）；只有被引用程序集没启用生成器时，使用方才在调用点生成；
- **回退路径**：没有生成元数据时 JIT 回退 `DispatchProxy`；Native AOT 下没有回退，直接抛 `AxiosAotMetadataException`；
- **前提**：消费方需 C# 9 及以上（`[ModuleInitializer]`）；生成器本身是 `netstandard2.0`，随包分发（按 Roslyn 4.0.1 编译，.NET SDK 6.0.100 / VS 2022 17.0 及以上的编译器均可加载）；
- **自定义路由特性**：`HttpApiAttribute` 的自定义子类无法在编译期执行构造器 → 警告 `AXA010` 并跳过生成；JIT 下仍走反射，AOT 下请改用 `[HttpApi("METHOD", "path")]` 或内置的 `[Get]` / `[Post]` 等；
- **关闭生成器**：`<AxiosNetSourceGen>false</AxiosNetSourceGen>`。JIT 下自动回退反射 / `DispatchProxy`；`PublishAot` 为 true 时给出警告 `AXA012`。

### 对象形状：params / 表单 / multipart 的展平

`params`、`form-urlencoded` 与 `multipart` 都要把对象展开成「键 → 值」。JIT 下按反射构建成员清单；启用源生成器时，形状在编译期烘焙并注册，AOT 下不再反射：

- **自动发现**：`[AxiosShape]` 类型、路由请求模型、接口方法参数类型、Axios 调用点实参（含 `PostFormAsync` 的 data）、`Params` / `Data` 赋值与对象初始化器、匿名对象创建；嵌套对象、集合元素、字典值递归展开；
- **命名类型**：用 `default(T)` 推断类型参数注册，取值委托是编译期写死的属性访问；只收录 public 读访问器（非 public getter 在生成与反射路径上一致跳过），ref struct / 指针成员无法装箱、两条路径同样跳过，`[JsonPropertyName]` / `[JsonIgnore]`、基类 `new` 隐藏与 `override` 的语义与反射路径逐字段一致；
- **匿名类型**：按「属性名 / 类型 / 顺序」生成结构相同的样本（`RegisterGeneratedShapeFromSample`）推断匿名类型参数；匿名元素数组写成 `new[] { ... }` 让编译器推断，结构不一致的匿名类型不会误命中；
- **自定义只读字典**：只生成枚举访问器（不注册 POCO 形状），`params` / 表单 / `multipart` 三条展平通道都走它；声明为接口类型时按接口登记，运行时沿具体类型的接口命中；访问器给出的 `null` 键在展平时跳过，回收 / 重试的遍历路径在 AOT 下没有访问器时停止下钻而不是抛错；
- **缺失形状显式失败**：AOT 下运行时类型没有形状就抛 `AxiosAotMetadataException`，消息带类型与字段路径（如 `位置：Payload`），不会静默丢字段。静态类型是 `object` / 接口 / 泛型参数的位置无法静态推断，可给具体类型标 `[AxiosShape]`，或手工调用 `AxiosMetadata.RegisterShape<T>()` / `RegisterShapeFromSample(...)` / `RegisterMap(typeof(T), ...)` 兜底；
- **不可见类型**：private 嵌套 / file-local / 跨程序集 internal 的类型无法被生成代码引用，跳过并给出 `AXA013` 警告（AOT 下用到会显式失败）。

| 诊断 | 级别 | 场景 |
| --- | --- | --- |
| `AXA001` | Error | 接口含无法代理的成员：属性 / 事件 / 静态成员 |
| `AXA002` | Error | 接口方法形态不支持：泛型方法、`ref` / `out` / `in`、ref struct / 指针参数、非 public 抽象成员、`CancellationToken` 附带绑定特性 |
| `AXA003` | Error | 必须且只能标注一个路由特性（模型与接口方法同一条）：标了多个，或接口方法没有路由 |
| `AXA004` | Error | 接口方法返回形态不支持（同步返回 / `ValueTask` 等） |
| `AXA005` | Error | 接口方法的 `[Stream]` 声明无效或帧格式值未定义 |
| `AXA006` | Error | `[ApiIgnore]` 与 Path / Query / Header / HeaderCollection / Part / Body 同用 |
| `AXA007` | Error | 路由特性存在但参数为空白或无法在编译期求值（与 `AXA003` 分开：修法不同） |
| `AXA008` | Error | `[Authorize]` 与 `[AllowAnonymous]` 标在同一层（模型或接口 / 方法） |
| `AXA009` | Error | 模型 / 接口无法生成元数据：静态类 / 不可访问 / 泛型 / ref struct / file-local / 成员 getter 非 public / 类型不可见 |
| `AXA010` | Warning | 模型 / 接口使用了自定义 `HttpApiAttribute` 子类（已跳过生成） |
| `AXA011` | Warning | 开放泛型接口（含外层类型为开放泛型的嵌套接口）没有闭合调用点（AOT 下 `Create` 会显式失败） |
| `AXA012` | Warning | `PublishAot` 下设置了 `AxiosNetSourceGen=false` |
| `AXA013` | Warning | 形状类型的访问级别对生成代码不可见（已跳过形状；AOT 下展平到该类型会显式失败） |

`System.Text.Json` 的序列化元数据仍需要按需注册（源生成 `JsonSerializerContext`）：

```csharp
[JsonSerializable(typeof(Person))]
internal partial class ApiJsonContext : JsonSerializerContext { }

var client = new AxiosClient(new AxiosConfig
{
    Serialization = new()
    {
        JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = ApiJsonContext.Default
        }
    }
});
```

`TypeInfoResolver` 是 .NET 7 才有的属性：`net6.0` 目标（被 .NET 6 / 7 应用引用）的 `JsonSerializerOptions` 上没有它，因此那份资产无法接入源生成元数据，AOT 下请改用自定义 `IContentSerializer`，或把目标框架提升到 `net8.0`。反射被关掉时两份资产报同一种 `NotSupportedException`，`net6.0` 的消息会额外点明这一点。

`JsonSerializerDefaults.Web` 不能省：换成裸 `new JsonSerializerOptions()` 会把 camelCase 换成属性原名，请求 / 响应字段名跟着变，而源生成上下文里的 `JsonSourceGenerationOptions` 不会替你补回来（`AxiosJson.Options` 的默认值就是 Web）。

AOT 下缺少元数据不会静默丢字段，而是抛出带修复指引的异常：请求模型 / 接口缺元数据时抛 `AxiosAotMetadataException`（消息含类型名与修复方式；也可用隐藏 API `AxiosApiMetadata.RegisterRequestModel` / `RegisterInterface` 手工注册声明兜底）；`System.Text.Json` 缺类型元数据时抛 `NotSupportedException`，提示把类型加入 `JsonSerializerContext` 并设置 `TypeInfoResolver`（匿名类型无法写入 `[JsonSerializable]`，消息会直接建议改用命名类型或 params / 表单）；`XmlContentSerializer` 与 `AxiosDynamic` 的 dynamic 成员语法抛 `PlatformNotSupportedException`（`AxiosDynamic` 的索引访问与 `IDictionary` 接口仍可用）。

AOT 下不可用：`dynamic` 成员语法（`data.name`）、运行期才确定的类型、`XmlContentSerializer`、程序集扫描（`ValidateModels(Assembly)` / `ValidateInterfaces(Assembly)` 依赖反射枚举，AOT 下抛 `PlatformNotSupportedException`；改用生成的 `AxiosNet.Generated.AxiosNetValidation_<程序集名>.ValidateAll()`，它覆盖本程序集里源生成器生成过元数据的全部请求模型与接口，或按类型调用 `ValidateModels(typeof(A), ...)` / `ValidateInterfaces(typeof(IA), ...)`）、接口代理的回退路径（未生成 / 未注册时的 `DispatchProxy` 依赖 Reflection.Emit，仅 JIT）。生成的类名把程序集名里的非标识符字符换成 `_`（`AxiosNet.Aot` → `AxiosNetValidation_AxiosNet_Aot`）；本程序集没有任何可生成的模型 / 接口时不会生成该类。匿名对象作为 params / 表单值不需要额外配置：生成器会为结构烘焙样本（见上文「对象形状」）。

容器注册与冻结都是可选加固。引用了 `AxiosNet.DependencyInjection` 时，生成器会额外产出 `AxiosNetGeneratedDependencyInjection.AddAxiosGeneratedApiInterfaces(services[, siteName])`，把本程序集里声明过路由的接口逐个按单例注册：它逐条调用公开的 `AddApiInterface<T>()`，站点冲突 / 命名站点 / 单例语义与手写完全同一份实现，区别是类型在编译期绑定——接口改名会让它编译失败，而不是运行时少注册一个服务。启动末尾想锁死元数据可调用隐藏 API `AxiosMetadata.Freeze()`：之后任何真正写入（新程序集的模块初始化器、手工注册）都会抛错并指出类型与程序集；等价重复注册仍是空操作。不调用它完全不影响使用。

**已知限制**：等价重复注册「保留先注册的一份」是为「多个互不引用的程序集各为同一接口生成代理」设计的。在可卸载 `AssemblyLoadContext`（插件热重载）里，第二次注册会被静默丢弃，代理工厂仍指向先注册那次加载的程序集——重载插件时不要依赖注册表自动换新，改用独立的进程或卸载后重新进入新的 `AssemblyLoadContext` 边界。

## 构建与测试

```bash
dotnet build AxiosNet.sln
dotnet test AxiosNet.sln
dotnet build AxiosNet.sln -c Release
```

| 测试项目 | 用例数 | 目标框架 | 执行次数 |
| --- | ---: | --- | ---: |
| `AxiosNet.Tests`（核心 + 声明式） | 1991 / 1984 | `net8.0` / `net6.0` | 3975 |
| `AxiosNet.DependencyInjection.Tests` | 66 | `net8.0` / `net6.0` | 132 |
| **合计** | **2057 / 2050** | | **4107** |

测试用自研 `TcpListener` HTTP/1.1 Keep-Alive 服务器跑真实 TCP 往返，覆盖请求编码、拦截器与转换、错误码、超时取消、进度限速、连接池与协议协商、传输档、流式响应、重试、熔断、并发对冲、备用端点与端点健康记忆、短路响应、响应缓存、遥测、声明式绑定、接口级代理、对象形状（生成 vs 反射）与 DI 注册。

行覆盖率 **91.7%**、分支覆盖率 **87.2%**（`AxiosNet.Tests` / net8.0，`--collect:"XPlat Code Coverage"`，排除性能与分配类用例；测试项目引用源生成器后覆盖率统计同时包含 `AxiosNet` 与 `AxiosNet.SourceGen` 两个程序集，两次采集有 ±0.1 抖动；对比历史值请沿用同一配置——下面的命令是 Debug，Release 下序列点更少、行分母会明显变小，两种口径不可混用。生成器的 DI 注册扩展只在 `AxiosNet.DependencyInjection.Tests` 里被驱动（主测试项目不引用 DI 包），它的行不在本口径内，这也是这两项比上一版略低的原因）。
采集时必须排除两类用例——性能比值与分配护栏在插桩下都会失真（护栏按「每请求分配字节数」断言，插桩本身就在分配）：

```bash
dotnet test test/AxiosNet.Tests/AxiosNet.Tests.csproj -f net8.0 --collect:"XPlat Code Coverage" \
  --filter "FullyQualifiedName!~ConcurrencyPerformanceTests&FullyQualifiedName!~AllocationSweepTests&FullyQualifiedName!~AllocationTests&FullyQualifiedName!~HeadersCopyOnWriteTests"
```

普通 `dotnet test` 仍然全跑（含被排除的这几类）。

### 运行时矩阵

「编译目标」与「运行时」是两个维度，覆盖的东西不同：

| 维度 | CI 覆盖 | 为什么必须逐项跑 |
| --- | --- | --- |
| 编译目标 | `net6.0` / `net8.0` 两份 `lib/` 资产 | 源码里有 9 处 `NET*_OR_GREATER` 条件编译，另一侧分支在别的目标里根本没进 IL |
| 运行时 | .NET 6.0 与 .NET 10.0 | 同一份 `lib/net8.0` 资产被 .NET 8 / 9 / 10 / 11 加载，BCL 实现各不相同 |

前一个维度由 `build-test` 覆盖（`dotnet test` 不带 `-f`，两个 TFM 各执行一次）；后一个维度由 `test-runtime` 覆盖——不改 `TargetFrameworks`、不加 TFM、不改包结构，只用 host 前滚让 `net8.0` 测试宿主在更高版本的运行时上执行，也就是消费者引用 `net8.0` 资产的主流形态：

```bash
DOTNET_ROLL_FORWARD=LatestMajor dotnet test <project> -f net8.0
```

必须是 `LatestMajor`：`Major` 会优先留在同一 major 的最新补丁（8.0.x），只有 `LatestMajor` 才真的跨 major。执行前先断言目标运行时已安装——前滚找不到更高版本时会静默回落到 net8，回落之后测试照样全绿，覆盖等于没做。

只加 .NET 10、不加 9 / 11：.NET 8 与 9 同在 2026-11-10 退出支持，10 是 LTS（到 2028-11-14）且是当前唯一处于活动支持期的版本，11 尚未 GA。并发性能用例不参与这一轮——它判的是与 `HttpClient` 的配对比值，换运行时等于换基线；要验高版本运行时上的性能得另建基线。

### Native AOT 验证

CI 另有一组 `aot` 任务（`win-x64` / `linux-x64` 矩阵），把 `IL2026` / `IL3050` / `IL3051` 直接当错误，因此「发布成功」等于「裁剪与 AOT 分析都是 0 警告」：

```bash
# 示例：不再 root 整个程序集，能否通过完全取决于源生成器烘焙的元数据
dotnet publish samples/AxiosNet.Aot -c Release -r linux-x64 -p:TrimmerSingleWarn=false \
  -p:WarningsAsErrors=IL2026%3BIL3050%3BIL3051
./samples/AxiosNet.Aot/bin/Release/net8.0/linux-x64/publish/AxiosNet.Aot

# 包消费：pack → 只引用包的临时项目 → AOT 发布 → 运行
bash eng/aot-package-smoke.sh
```

`eng/aot-package-smoke.sh` 先断言 nupkg 里带 `analyzers/dotnet/cs/AxiosNet.SourceGen.dll` 与 `buildTransitive/AxiosNet.props`，再把包装进一个只引用它的临时项目发布运行。它补的是示例覆盖不到的一环——示例用 `ProjectReference`，看不到「包内分析器是否真的生效」和「包内 dll 的 AOT 标注是否自洽」；脚本每次用唯一版本号打包，避免 NuGet 复用全局缓存里的旧 dll（版本不变时它不会重新解包，等于一直在验证上一次的包）。

体积与启动都在预算内才算过：示例与包 smoke 都断言发布产物不超过预算——win-x64 上限 9 MiB（当前示例 8.3 MiB、包 smoke 8.1 MiB），linux-x64 上限 11 MiB（当前 10.2 MiB）。两个平台差约 2 MiB 属平台差异、不是体积退化：Linux 把 OpenSSL、`System.Native`、压缩、globalization 这些 shim 全部静态链进可执行文件，Windows 走系统 DLL 或预烘焙的 `Aot.lib`，所以预算按平台分别定。超了基本只有两种原因——又引入了反射依赖把代码拉回来，或者程序集被整体 root（`TrimmerRootAssembly` / 生成器没生效）。启动 + 完整跑完用 10 s 拦网，只拦数量级退化，不当性能结论；性能对比看 `test/AxiosNet.Benchmarks`。

生成代码本身也有快照基线（`test/AxiosNet.Tests/WebAPI/Aot/Snapshots/GeneratedSources.snap.txt`）：生成器输出一变，diff 就直接落在基线文件上；用 `AXIOS_UPDATE_SNAPSHOTS=1` 重生成并逐行审阅。

已知代价：接口代理每次调用仍有 1 次实参数组分配——实参要交给共享绑定（`IValueSource.GetValue(object target)` 按位置取值），生成器只烘焙声明、不复刻绑定逻辑。要真做到零分配得把绑定计划按方法内联，那等于把校验与绑定规则复制成 N 份，与「单一构建入口」冲突，因此不做；代理层能省的（每次调用的只读包装）已经改成按需创建。

### 用例按功能分类

`test/AxiosNet.Tests` 的目录结构就是功能清单——找用例先看目录，再找文件名：

| 目录 | 覆盖范围 |
| --- | --- |
| `Client/` | 静态门面 `Axios`、实例选项与默认值、默认头、配置合并与十个子对象 |
| `Requests/` | URL 与 params、请求体（JSON / 表单 / multipart）、响应解析与缓冲、流式响应、超时取消、Content-Type 解析 |
| `Governance/` | 并发闸门、进度回调与限速 |
| `Resilience/` | 重试策略与计时、总预算（`TotalTimeout`）、熔断状态机、短路响应 |
| `Caching/` | 响应缓存与单飞、条件请求（`ETag` / 304）与 `IAxiosCacheRevalidationStore`、`IAxiosCacheStore` 契约与自建存储、键规则与身份隔离、命中路径 |
| `Serialization/` | `IContentSerializer` 契约与注册表、JSON 默认选项 |
| `Pipelines/` | 请求 / 响应拦截器、`transformRequest` / `transformResponse`、尝试管线（内置顺序、插入位置、重排校验）、并发对冲、备用端点与端点健康记忆 |
| `Connectivity/` | 连接池（含命名共享池、命名传输档）、Handler 注入、HTTP/2·3 协商与降级、重定向、本机 h3 能力探测 |
| `Diagnostics/` | Activity / Meter / DiagnosticListener 三路遥测、尝试审计事件 |
| `Errors/` | 错误模型、错误码、`ToJson` 快照 |
| `Regression/` | 分配护栏（独占集合 + 精确计数；`AllocationTests` 走进程级均值，`AllocationSweepTests` 走线程级多窗最小值——被测路径同步完成时真实值是个定值，噪声只会往上加）、并发吞吐 |
| `WebAPI/` | 声明式模型绑定与校验、授权、流式、接口级代理与启动期校验、请求模型源生成器（生成路径 vs 反射路径逐字段对照） |
| `Architecture/` | 架构护栏：核心不得引用 `WebAPI/` 扩展（扫源码断言，破坏「可整体删除」即失败） |
| `Infrastructure/` | 测试基建：HTTP 服务器、模型、遥测探针、独占集合 |

时序敏感用例（断言上限耗时、靠「占位 vs 预算」区分口径）与分配用例各自独占运行，见 `Infrastructure/TimingCollection.cs`、`Infrastructure/AllocationCollection.cs`。

`AxiosNet.DependencyInjection.Tests` 同规则分两组：`Registration/`（注册扩展与命名注册）、`Lifecycle/`（并发解析与持有者释放）。

### 性能基准

[`test/AxiosNet.Benchmarks`](test/AxiosNet.Benchmarks) 是独立 BenchmarkDotNet 项目（单目标 `net8.0`、`IsPackable=false`、不被 `src/` 引用），把「这次优化有没有效果」变成可读数字。它不参与 `dotnet test`，CI 也不跑（基准对机器状态敏感）。

**与并发正确性测试的分工**

- `AxiosNet.Tests/ConcurrencyPerformanceTests` 判**并发正确性**，拦塌方：5 轮取最好一轮，要求 ≥ 85% 能力线（Debug 70%）；中位 ≥ 75%（Debug 60%）当地板，防整体退化。
- 量化「快了多少」看基准：基准给分项成本；正确性测试只回答「有没有退到不可用」。
- 它判的是配对比值，两侧编译状态必须可比：`HttpClient` 是 ReadyToRun，进程起来就是优化代码；本库是普通 IL，要先跑 Tier0、等分层编译换成 Tier1 + PGO。因此 CI 里它**独占进程、排在整套用例之前**，整套用例按功能过滤掉它后逐 TFM 串行跑。

**预热为什么按比值收敛**

- 编译爬坡按时钟推进，runner 越慢越忙、固定预热里换掉的比例越少，测到的就是编译进度而不是代码。
- 所以预热不拍固定秒数：1 秒一窗，至少 4 窗、最多 10 窗，最近 3 窗的比值极差 ≤ 3 个百分点才开始判定轮。实测前 4 窗可能一直平在 82%~87%，第 5~6 窗才跳到 95%；收敛后判定轮稳定在 93.5%~96.2%，固定 2 秒预热的对照组第 1 轮只有 84.1%。
- 分层编译保持默认开启（真机稳态靠 PGO，不做特殊配置）。

**没达标先看 CPU 份额**

比值没达标时，先看进程实际拿到的 CPU 份额（`Process.TotalProcessorTime` ÷ 墙钟 ÷ 核数，判定轮取中位）。同一台机器同一份代码实测：吃到 50%~56% 时比值稳定在 77%~87%；被挤到 30%~32% 时比值在 54%~90% 之间乱跳。因此门槛定在 42%，够不着就只打印数字、不做断言——达标不用解释，没达标才看这个门槛。

```bash
dotnet run -c Release --project test/AxiosNet.Benchmarks                       # 交互菜单
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*Merge*'  # 参数直传 BDN
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*' --isolated
```

默认**同进程**执行（不生成临时项目、不重建）；要写进提交信息或与历史基线对比时加 `--isolated`（临时项目 + 每用例独立进程），两种方式的绝对耗时不能混比（`Allocated` 两者都可信）。

| 组 | 热点 | 基线 |
| --- | --- | --- |
| `RequestPipelineBenchmarks` | 请求全链路（真实回环 socket） | `HttpClient_GetString` |
| `ConcurrentPipelineBenchmarks` | 8 条在途时的每请求成本 | `HttpClient_Concurrent` |
| `ConfigMergeBenchmarks` | `MergeConfig` / 请求头合并 | `GetUri_NoMerge` |
| `ConfigPrimitiveBenchmarks` | `AxiosConfig.Clone` / `AxiosHeaders` 原语 | —（看绝对值） |
| `FormEncodingBenchmarks` | 表单展平 + 百分号编码 | `PostForm_FlatAscii` |
| `DynamicDeserializationBenchmarks` | `dynamic` 反序列化 | `Typed` |
| `StreamingFramingBenchmarks` | 流式分帧（NDJSON / SSE / JSON 数组） | `Ndjson` |
| `LargeBodyUploadBenchmarks` | 大正文上传（1 / 8 / 32MB：`byte[]` / `ReadOnlyMemory` / 流 / 异步序列，真实回环 socket） | `HttpClient_Upload_ByteArray` |
| `LargeBodyDownloadBenchmarks` | 大正文下载（1 / 8 / 32MB：缓冲成 `byte[]` vs 流式读完） | `HttpClient_Download_Buffered` |
| `LargeBodyJsonBenchmarks` | 大正文 JSON 响应（1 / 8 / 32MB：`byte[]` / 强类型 / `JsonElement`） | `HttpClient_GetBytes` |

读法、前后对比流程与已知噪声见 [`test/AxiosNet.Benchmarks/README.md`](test/AxiosNet.Benchmarks/README.md)。

跨实现横向对比另见 [`test/AxiosNet.Comparison`](test/AxiosNet.Comparison)：同一回环服务器、同一负载（默认 8 并发 × 1s × 5 轮、每轮轮转顺序），对比裸 `HttpClient` / Refit / RestSharp / Flurl / AxiosNet 的 RPS、p50 / p99 / max 与每请求分配，输出控制台表 + Markdown / CSV 报告；加 `--min-ratio` / `--max-alloc-ratio` 可当回归门禁（不设阈值时只出数字、不判定）。它不进 CI、也不被 `dotnet test` 收集（`IsTestProject=false`，放在 sln 里只为 IDE 直接打开）；口径、读法与一次本机参考数据见该工程 README。

## 项目结构

```
.
├─ src/
│  ├─ AxiosNet/                       # 类库（net6.0 / net8.0 双目标）
│  │  ├─ Axios.cs                     # 静态门面
│  │  ├─ AxiosClient*.cs              # 实例 + 请求管线 + 快捷方法
│  │  ├─ AxiosConfig*.cs              # 配置（顶层 + 十个子对象）
│  │  ├─ AxiosHeaders.cs              # 请求头
│  │  ├─ AxiosResponse.cs / AxiosException.cs / AxiosErrorCode.cs / AxiosShortCircuit.cs
│  │  ├─ AxiosDynamic.cs / AxiosContent.cs / AxiosJson.cs / FormFile.cs（顶层公共类型）
│  │  ├─ Cache/                       # AxiosCache（响应缓存 + 单飞）/ IAxiosCacheStore / MemoryCacheStore
│  │  ├─ Interceptors/                # 请求 / 响应拦截器
│  │  ├─ Pipeline/                    # 尝试中间件链：AxiosAttemptPipeline + 内置重试 / 备用端点 / 熔断 / 对冲 / 闸门 / 审计
│  │  ├─ Retry/                       # IAxiosRetryPolicy / RetryPolicy / RetryBudgetPolicy
│  │  ├─ Circuit/                     # IAxiosCircuitPolicy / CircuitBreaker
│  │  ├─ Serialization/               # IContentSerializer + JSON / XML 实现
│  │  ├─ Telemetry/                   # ActivitySource / Meter / DiagnosticListener
│  │  ├─ Pooling/                     # HttpConnectionPool + 池选项 / 统计 + Handler 注入
│  │  ├─ Metadata/                    # 生成元数据注册入口（对象形状 / 字典映射 / 冻结）+ 形状构建器
│  │  ├─ WebAPI/                      # 声明式 API 客户端（可选扩展，可整体删除；Metadata/ 公开注册入口，Internal/ 共享契约，Reflection/ 仅 JIT）
│  │  ├─ Reflection/                  # 对象形状与 STJ 的反射回退（只在 JIT 下可达，AOT 镜像里整块裁掉）
│  │  └─ Internal/                    # URL、正文编码、响应读取、流量计量
│  ├─ AxiosNet.SourceGen/             # 源生成器（netstandard2.0 分析器）：与 Reflection/ 对称的生成期路径
│  └─ AxiosNet.DependencyInjection/   # DI 扩展（独立包）
├─ samples/                           # Simple（51 项）/ DI / Aot
├─ eng/                               # CI 脚本（AOT 包消费 smoke，见「Native AOT 验证」）
└─ test/
   ├─ AxiosNet.Tests/                  # 核心 + 声明式（目录 = 功能分类，见「构建与测试」）
   ├─ AxiosNet.DependencyInjection.Tests/
   ├─ AxiosNet.Benchmarks/             # BenchmarkDotNet（单实现分项成本）
   └─ AxiosNet.Comparison/             # 跨实现横向对比（RPS / p99 / 分配，可选门禁）
```

目录约定三条：领域目录（`Metadata/`、`Serialization/`、`Internal/`）按功能分家；**只在 JIT 下可达的反射实现一律放进 `Reflection/` 目录**，且只有两处——根级 `Reflection/` 装跨领域共用的底层（对象形状反射、STJ 反射回退），`WebAPI/Reflection/` 装属于声明式 API 这个领域的反射读取与 `DispatchProxy` 回退（不在 `Reflection/` 目录里的代码不直接碰反射）；**`WebAPI/` 是可整体删除的扩展**——删掉它，核心与原始 HTTP 照样编译、照样能用，所以核心不得反向引用它（对象形状、字典映射、生成注册表、AOT 守卫与异常都因此住在 `Metadata/` 与 `Internal/`）。

## License

[MIT](LICENSE)
