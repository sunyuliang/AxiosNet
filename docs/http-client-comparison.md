# AxiosNet 与主流 .NET HTTP 客户端对比

对比对象：`HttpClient`（BCL 基线）、Refit、RestSharp、Flurl、AxiosNet。
维度：功能、性能、易用性。文末给出选型建议与性能口径的边界。

- 性能数据来自本仓库 `test/AxiosNet.Comparison` 的现场运行（2026-11-01 10:13:55 +08:00），不是引用旧结果。
- 功能与易用性结论来自各库公开 API 与本仓库源码，属于定性判断，已在文中标注。

---

## 一、结论先行

**性能：AxiosNet 与「裸 HttpClient」不是同一档，但只差 8.5%；与同为源码生成/轻封装的 Refit（92.8%）、
Flurl（89.5%）几乎同档；RestSharp 明显落后（58.1%），且每请求分配高出一个数量级。**

| 实现 | RPS | 相对 HttpClient | 净分配/请求 | p99 | 一句话 |
| --- | ---: | ---: | ---: | ---: | --- |
| HttpClient | 84,389 | — | 3.6 KB | 218 µs | 天花板，但功能最少 |
| Refit | 78,353 | 92.8% | 4.5 KB | 232 µs | 声明式接口，源生成开销小 |
| Flurl | 75,553 | 89.5% | 5.9 KB | 262 µs | 链式写法，轻封装 |
| **AxiosNet** | **77,183** | **91.5%** | **5.0 KB** | **251 µs** | **功能最全，成本可控** |
| RestSharp | 49,014 | 58.1% | 31.4 KB | 1,551 µs | 功能全面但分配重 |
| RawSocket | 106,945 | 126.7% | 0 B | 149 µs | 回环服务器上限，不是客户端 |

**功能：只有 AxiosNet 把重试、熔断、并发对冲、备用端点、响应缓存、在途并发闸门、限流、流式分帧、
声明式 API 与 OpenAPI 导入、内置遥测集中在一个零第三方依赖的包里。** 其余四家在这些能力上
要么完全没有，要么需要叠加 Polly / 自写 Handler / 引入缓存库。

**易用性：单次调用最省代码的是 Flurl（一行链式）；端点多、要类型安全的是 Refit（写接口）；
AxiosNet 与 axios 写法同构，并额外提供「模型式 / 接口式 / OpenAPI 导入」三种声明式入口。**

---

## 二、对比对象与版本

| 实现 | 版本 / 形态 | 在本仓库基准里的用法 |
| --- | --- | --- |
| HttpClient | BCL（.NET 8） | `HttpClient` + `GetFromJsonAsync<User>`（基线） |
| Refit | 16.4.0 | `RestService.ForGenerated<IUserApi>`（只走源生成，不走反射回退） |
| RestSharp | 113.0.0 | 单例 `RestClient` + 每请求新建 `RestRequest` + `ExecuteAsync<User>` |
| Flurl | Flurl.Http 4.0.2 | 单例 `FlurlClient` + `Request(url).GetJsonAsync<User>()` |
| AxiosNet | 仓库源码（net8.0） | 默认 `AxiosClient`，不做任何调优 |
| RawSocket | 本仓库基准内实现 | 裸 TCP 预编码请求，仅作服务端上限参照 |

> 四个第三方实现与 AxiosNet 都注入了同一份「与 AxiosNet 默认连接池逐项对齐」的
> `SocketsHttpHandler`，因此测到的是实现差异，不是默认值差异。

---

## 三、功能对比

### 3.1 请求能力

| 能力 | HttpClient | Refit | RestSharp | Flurl | AxiosNet |
| --- | --- | --- | --- | --- | --- |
| 强类型反序列化 | `GetFromJsonAsync<T>` | 接口返回类型 | `ExecuteAsync<T>` | `GetJsonAsync<T>` | `GetAsync<T>` |
| 原文 / `dynamic` / `JsonElement` | 需自己处理 | 需自己处理 | 需自己处理 | 需自己处理 | 四种形态内置 |
| 声明式端点 | 无 | **接口 + 特性**（源生成/反射两档） | 无 | 无 | **模型式 + 接口式** |
| OpenAPI 导入生成客户端 | 无 | 无（需第三方 Refitter） | 无 | 无 | **内置 `axiosnet-gen`** |
| 只拼 URL（不发请求） | 手写 | 无 | 无 | URL 构建器 | `GetUri(...)` |
| 查询参数对象化 | 手写 | `[Query]` / 参数展开 | `AddQueryParameter` | `SetQueryParams` | `Params`（对象/字典/集合） |
| multipart 表单 + 文件 | 手写 | 支持 | 支持 | 支持 | `PostFormAsync` / `FormFile` |
| 流式请求体（边算边发） | 需自定义 `HttpContent` | 有限 | 有限 | 有限 | `IAsyncEnumerable<byte[]>` / 内存块 |
| 流式响应分帧（SSE / NDJSON / JSON 数组） | 需自己读流 | 返回 `Stream` 自行处理 | 返回流自行处理 | 返回流自行处理 | `RequestStreamAsync<T>` 内置三种格式 |
| 上传/下载进度 | 需自定义 | 无 | 有进度事件 | 无 | 回调内置 |
| 字节限速 / 体积上限 | 无 | 无 | 无 | 无 | `Limits.MaxRate` / 大小上限 |
| 请求体压缩（gzip/deflate/br） | 手写 | 无 | 无 | 无 | `RequestCompression` |
| 统一凭据扩展点 + 令牌缓存 / 提前刷新 / 401 重放 | 无 | 部分（授权头） | 部分（OAuth 助手） | 无 | `IApiCredentialProvider` / `AccessTokenManager` |
| 声明式鉴权（`[Authorize]` / `[AllowAnonymous]` + 401 自动重放） | 无 | 无 | 无 | 无 | 模型 / 接口级特性 + `ApiClient.CredentialProvider` |

### 3.2 弹性与治理（本表是差距最大的一块）

| 能力 | HttpClient | Refit | RestSharp | Flurl | AxiosNet |
| --- | --- | --- | --- | --- | --- |
| 重试（退避/抖动/`Retry-After`） | 需 Polly | 需 Polly | 无内置 | 无内置 | **内置策略** |
| 全局重试预算 | 需自写 | 需自写 | 无 | 无 | **`RetryBudgetPolicy`** |
| 熔断（Closed/Open/HalfOpen） | 需 Polly | 需 Polly | 无 | 无 | **内置状态机** |
| 并发对冲（Hedging） | 无 | 无 | 无 | 无 | **内置** |
| 备用端点 / 端点健康记忆 | 自写 | 自写 | 自写 | 自写 | **内置** |
| 幂等键（重试/换端共用） | 自写 | 自写 | 自写 | 自写 | **内置** |
| 在途并发闸门 + 排队上限 | 自写 | 自写 | 自写 | 自写 | **内置** |
| 响应缓存 + 单飞 + 条件复用 | 需缓存库 | 需缓存库 | 需缓存库 | 需缓存库 | **内置** |
| 短路响应（mock / 回放） | 自写 | 自写 | 自写 | 自写 | **内置** |
| 单次预算 vs 整条请求预算分离 | 只有 `Timeout` | 继承 HttpClient | 有超时 | 有超时 | `Timeout` + `TotalTimeout` |

> 「需 Polly」指需要额外引入第三方弹性库并把它装进 `DelegatingHandler` 链；这本身是可行方案，
> 但它是**另一个包 + 另一套配置模型**，与本表里「同一次配置调用里就能写」不是一回事。

### 3.3 连接、观测与工程化

| 能力 | HttpClient | Refit | RestSharp | Flurl | AxiosNet |
| --- | --- | --- | --- | --- | --- |
| 连接池复用 | `SocketsHttpHandler` | 继承 | 继承 | 继承 | 按 origin 自建池，可命名共享 |
| 多出口（代理/凭据/证书/Cookie 会话分档） | 多 Handler 手写 | 多 Handler 手写 | 有限 | 有限 | **传输档 `Transport`** |
| HTTP/2 / HTTP/3 | 支持 | 支持 | 通过底层 | 通过底层 | 支持，可降级 |
| DI 注册 | `IHttpClientFactory` | `Refit.HttpClientFactory` | 手写 | 手写 | `AddAxiosClient` / `AddApiClient` |
| 遥测 | 内置 `HttpClient` 事件 | 继承 | 继承 | 继承 | **`ActivitySource` + `Meter` + `DiagnosticListener`，尝试级事件** |
| 统一错误模型 | `HttpRequestException` 等 | `ApiException` | `RestResponse` 状态 | `FlurlHttpException` | `AxiosException` + 稳定 `Code` |
| Native AOT / 裁剪 | 原生支持 | 源生成支持 | 反射较重，需配置 | 反射较重，需配置 | `IsTrimmable` / `IsAotCompatible`，生成器烘焙 |
| 第三方运行时依赖 | 0（BCL） | 1 个包 | 1 个包 | 2 个包 | **0（只用 BCL）** |

> 「依赖数」指运行时需要额外引入的 NuGet 包数量；Refit/Flurl 还各自带源生成器或子包，未重复计。

---

## 四、性能对比

### 4.1 口径（不要跳过这一节）

| 项 | 值 |
| --- | --- |
| 时间 / 环境 | 2026-11-01 10:13:55 +08:00；Windows 10.0.19045；18 核；.NET 8.0.31；Server GC=False；Release |
| 负载 | 每实现 8 并发常驻 × 1 s 窗口 × 5 轮（本次运行实际用的轮数；工具现行默认 10 轮），轮间整体轮转实现顺序，预热 3 s + 冷却 2 s |
| 请求 | HTTP/1.1 明文回环 GET，正文 51 B JSON，反序列化为同一个 `User` 模型 |
| 延迟 | `Stopwatch.GetTimestamp` 包住「发请求 + 读正文 + 反序列化」，含服务端处理与回环 RTT |
| 分配 | 进程级 `GC.GetTotalAllocatedBytes` ÷ 完成请求数；`净分配` 已减掉 RawSocket 基线 |
| 汇总 | 每列取 5 轮的**中位数**（抗单轮抖动） |
| 本次规模 | 窗口内服务器共回复 4,257,633 个请求 |

### 4.2 结果

| 实现 | RPS | p50 (µs) | p99 (µs) | max (µs) | 分配/请求 | 净分配/请求 | CPU | RPS vs HttpClient |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| HttpClient | 84,389 | 84 | 218 | 18,503 | 3.7 KB | 3.6 KB | 49% | — |
| Refit | 78,353 | 88 | 232 | 30,602 | 4.6 KB | 4.5 KB | 48% | 92.8% |
| RestSharp | 49,014 | 116 | 1,551 | 16,373 | 31.5 KB | 31.4 KB | 44% | 58.1% |
| Flurl | 75,553 | 90 | 262 | 16,762 | 6.0 KB | 5.9 KB | 49% | 89.5% |
| AxiosNet | 77,183 | 87 | 251 | 25,264 | 5.2 KB | 5.0 KB | 49% | 91.5% |
| RawSocket | 106,945 | 66 | 149 | 30,744 | 131 B | 0 B | 45% | 126.7% |

每轮原始 RPS（看稳定性，避免只信一个漂亮数字）：

| 实现 | 第 1 轮 | 第 2 轮 | 第 3 轮 | 第 4 轮 | 第 5 轮 |
| --- | ---: | ---: | ---: | ---: | ---: |
| HttpClient | 94,717 | 82,191 | 84,408 | 80,174 | 84,389 |
| Refit | 84,992 | 77,327 | 79,008 | 78,353 | 77,919 |
| RestSharp | 51,606 | 48,710 | 48,609 | 49,014 | 49,421 |
| Flurl | 78,292 | 75,553 | 75,685 | 74,609 | 74,110 |
| AxiosNet | 79,078 | 77,183 | 76,708 | 76,026 | 78,199 |
| RawSocket | 107,612 | 104,077 | 106,945 | 106,334 | 111,036 |

### 4.3 怎么读

- **AxiosNet 的 8.5% 吞吐差，换来的是「一个包替代 Polly + 缓存库 + 自写 Handler 链」。**
  同一窗口里它的 CPU 份额与 HttpClient 相同（49%），p50 只差 3 µs，说明差的不是传输层，
  而是每次请求上多走的配置合并、拦截器/转换链、统一错误模型与遥测判定。
- **Refit 与 AxiosNet 同档（92.8% vs 91.5%），二者都是「源生成/轻封装 + 少量每请求分配」的代表。**
  Refit 分配更低（4.5 KB vs 5.0 KB），因为它不承担治理管线；AxiosNet 多出来的 ~0.5 KB 是这些管线的固定成本。
- **RestSharp 的 p99（1,551 µs）是其它实现的 6～7 倍，与它 31.4 KB/请求的净分配是同一件事的两个侧面**：
  每请求新建 `RestRequest` + 自身序列化路径带来更高 Gen0/Gen2 压力，窗口内 GC 暂停直接体现在尾延迟上。
  这不是服务端或适配器带进来的——用不经过 socket 的伪 Handler 单独复测，`ExecuteAsync<User>` 单次约 29.4 KB，同量级。
- **RawSocket 不是客户端**：它是回环服务器的上限。HttpClient 已到它的 79%，说明在这套负载下瓶颈开始向服务端转移，
  各客户端的 RPS 都被一定程度上限。
- **只看比值，不看绝对值**：同一台机器上两次运行绝对 RPS 可以差一倍（功耗策略、P/E 核、后台负载）。
  跨版本比较必须同机、同 Tag、同机器状态。

### 4.4 这套性能数据不覆盖什么

- 只测 HTTP/1.1 明文回环 GET + 51 B JSON。TLS、HTTP/2、HTTP/3、上传、大正文、流式响应都不在范围内。
- 服务端与客户端同进程，所以分配指标必须减基线，CPU 指标含服务端。
- 每个实现按各自文档的惯用写法，不做「为跑分而调优」（例如 RestSharp 每请求新建 `RestRequest` 就是它的正常用法）。
- Refit 只测源生成模式；其反射回退路径是另一档性能，不在本表内。
- 大正文与流式的分项成本见 `test/AxiosNet.Benchmarks`（BenchmarkDotNet），两套数字不可互换。

---

## 五、易用性对比

以「GET 一个 JSON 成强类型对象」为例，各库的惯用写法：

```csharp
// HttpClient
var http = new HttpClient { BaseAddress = new Uri("https://api.example.com/") };
var user = await http.GetFromJsonAsync<User>("users/1");

// Refit
public interface IUserApi
{
    [Get("/users/{id}")]
    Task<User> GetUserAsync(int id, CancellationToken ct = default);
}
var api = RestService.For<IUserApi>("https://api.example.com");
var user = await api.GetUserAsync(1);

// RestSharp
var client = new RestClient("https://api.example.com");
var res = await client.ExecuteAsync<User>(new RestRequest("users/1"));
if (!res.IsSuccessful) throw new InvalidOperationException(res.ErrorMessage);
var user = res.Data;

// Flurl
var user = await "https://api.example.com/users/1".GetJsonAsync<User>();

// AxiosNet
var http = Axios.Create(c =>
{
    c.BaseAddress = "https://api.example.com/";
    c.Timeout = TimeSpan.FromSeconds(10);
});
var user = (await http.GetAsync<User>("users/1")).Data;
```

### 5.1 Flurl 的「省」省在哪：一个带查询参数、鉴权与超时的例子

场景：`GET /users?page=2&tags=dev&tags=ops`，带 Bearer 令牌，10 秒超时，返回 `List<User>`。

```csharp
// HttpClient：请求消息、鉴权头、超时 CTS、读正文、反序列化，五步都自己写
var url = "https://api.example.com/users?page=2&tags=dev&tags=ops";
using var req = new HttpRequestMessage(HttpMethod.Get, url);
req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var res = await http.SendAsync(req, cts.Token);
res.EnsureSuccessStatusCode();
var users = await res.Content.ReadFromJsonAsync<List<User>>(cts.Token);

// Refit：接口声明一次，调用一行；但查询数组、鉴权头、超时都要靠特性/HttpClient 配置表达
public interface IUserApi
{
    [Get("/users")]
    Task<List<User>> SearchAsync(
        [Query] int page,
        [Query] string[] tags,
        [Header("Authorization")] string authorization,
        CancellationToken ct = default);
}

// RestSharp：请求对象上逐个加参数与头
var req = new RestRequest("users")
    .AddQueryParameter("page", 2)
    .AddQueryParameter("tags", "dev")
    .AddQueryParameter("tags", "ops")
    .AddHeader("Authorization", $"Bearer {token}");
var res = await client.ExecuteAsync<List<User>>(req, ct);

// Flurl：URL + 查询 + 鉴权 + 超时 + 反序列化，全部链在一行
var users = await "https://api.example.com/users"
    .SetQueryParam("page", 2)
    .SetQueryParam("tags", new[] { "dev", "ops" })
    .WithOAuthBearerToken(token)
    .WithTimeout(TimeSpan.FromSeconds(10))
    .GetJsonAsync<List<User>>(cancellationToken: ct);

// AxiosNet：配置对象集中表达，Params 与 Headers 是一个对象的两个字段
var response = await http.GetAsync<List<User>>("users", c =>
{
    c.Params = new { page = 2, tags = new[] { "dev", "ops" } };
    c.Headers.Set("Authorization", $"Bearer {token}");
}, ct);
```

声明式客户端还能把这一步收进实例：设置 `ApiClient.CredentialProvider`（`IApiCredentialProvider`），由库按 `[Authorize]` 的 scheme 写 `Authorization` 头；配 `AccessTokenManager` 时自动刷新，401 后用新凭据重放一次。标了 `[AllowAnonymous]` 的登录接口跳过整条凭据链路，因此取令牌可以直接调同一个 `ApiClient`。

再看上传文件，差距更明显：

```csharp
// HttpClient：MultipartFormDataContent + StreamContent + 手工文件名/内容类型
using var form = new MultipartFormDataContent();
form.Add(new StringContent("avatar"), "name");
using var file = File.OpenRead("photo.jpg");
var fileContent = new StreamContent(file);
fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
form.Add(fileContent, "file", "photo.jpg");
var res = await http.PostAsync("https://api.example.com/upload", form, ct);

// Flurl：同样一行
await "https://api.example.com/upload"
    .WithTimeout(TimeSpan.FromMinutes(5))
    .PostMultipartAsync(mp => mp
        .AddString("name", "avatar")
        .AddFile("file", "photo.jpg"));
```

**Flurl 省在四处**：

1. **字符串就是请求**——不需要 `HttpRequestMessage` / `HttpResponseMessage` / `HttpContent` 三件套，
   请求装饰（头、鉴权、超时）可以就地链式叠加。
2. **URL 与编码交给库**——路径拼接、查询参数、数组→重复键、百分号转义由 `Flurl.Url` 处理，
   不用手写 `Uri.EscapeDataString`，也不容易漏编码。
3. **返回类型直接是 `T`**——`GetJsonAsync<T>` 把「发送 + 读正文 + 反序列化」压成一步；
   `GetStringAsync` / `GetBytesAsync` / `DownloadFileAsync` 是同一套链式写法。
4. **一套 API 覆盖多种请求形态**——JSON、表单、multipart、下载、流都在同一个链上，不用换模型。

**Flurl 不省在三点**（与本报告功能/性能表的结论一致）：

1. **治理全在外面**：重试、熔断、并发对冲、缓存、限流都没有内置，要么接 Polly，要么自写循环。
2. **字符串 URL 没有编译期检查**：端点多、路由集中管理时不如 Refit / AxiosNet 的声明式入口。
3. **不是零成本**：每次链式调用仍会创建 `IFlurlRequest` 等对象；基准里净分配 5.9 KB/请求，
   高于 HttpClient 的 3.6 KB，也高于 AxiosNet 的 5.0 KB。

**附：JS axios 的同一例（写法参照，不是 .NET 客户端）**

```js
// axios（JS）：配置对象集中表达，和 AxiosNet 的形状几乎一一对应
const { data: users } = await axios.get('https://api.example.com/users', {
  params: { page: 2, tags: ['dev', 'ops'] },   // 默认序列化为 tags[]=dev&tags[]=ops
  headers: { Authorization: `Bearer ${token}` },
  timeout: 10_000,                              // 毫秒
});
```

```csharp
// AxiosNet：同一个配置对象模型的 C# 版本
var response = await http.GetAsync<List<User>>("users", c =>
{
    c.Params = new { page = 2, tags = new[] { "dev", "ops" } };   // 同样是 tags[]=...
    c.Headers.Set("Authorization", $"Bearer {token}");
    c.Timeout = TimeSpan.FromSeconds(10);
}, cancellationToken: ct);
```

两者的「省」是同一种省法：把 URL、参数、头、超时、响应处理收进一个配置对象/实例默认值，
而不是像 Flurl 那样在调用点就地链式装饰。差别在于 C# 侧的类型与命名约定：方法名带 `Async`、
超时用 `TimeSpan`、取消用 `CancellationToken`、请求体编码由 `ContentType` 决定（不按 data 类型猜）。

同一件事加上「失败重试 3 次 + 熔断 + 缓存」的成本：

| 实现 | 需要做什么 |
| --- | --- |
| HttpClient | 引入 Polly（或 `Microsoft.Extensions.Http.Resilience`），组一条 `DelegatingHandler` 链，再单独接缓存库；配置分散在三处 |
| Refit | 同上，Polly 挂在传给 `RestService.For` 的 `HttpClient` 上；Refit 自身不提供 |
| RestSharp | 自己在循环里重试，熔断与缓存全自写 |
| Flurl | 同上；事件钩子（`OnError`/`OnResponse`）能做记录，但不等于弹性策略 |
| AxiosNet | 同一个配置对象里写 `c.Retry.Policy`、`c.Circuit.Policy`、`client.Defaults.Cache`，一条请求全部生效 |

定性评价（**是判断，不是测量**）：

| 维度 | HttpClient | Refit | RestSharp | Flurl | AxiosNet |
| --- | --- | --- | --- | --- | --- |
| 上手成本 | 低（人人会） | 中（要写接口） | 中（Client/Request 两件套） | **低（一行链式）** | 低（与 axios 同构） |
| 端点多时的可维护性 | 低（模板代码多） | **高（接口集中）** | 中 | 中（URL 字符串散落） | **高（模型/接口 + OpenAPI 生成）** |
| 类型安全 | 中 | **高** | 中 | 中（字符串 URL） | **高** |
| 治理能力的组织方式 | 分散（Handler 链 + 第三方） | 分散 | 分散/自写 | 分散 | **集中（配置对象）** |
| 排障可观测性 | 中（HttpClient 事件） | 中 | 中 | 中 | **高（尝试级事件 + trace/metrics）** |
| 生态与资料 | **最广** | 广 | 广（老项目多） | 中 | 新（本仓库文档即主要资料） |

---

## 六、选型建议

| 场景 | 建议 |
| --- | --- |
| 调用很少、追求零依赖与极致吞吐、团队已熟悉 BCL | `HttpClient`（+ `System.Net.Http.Json`） |
| 端点多、要类型安全、愿意写接口，且不需要内置治理 | Refit |
| 已有大量表单/文件/OAuth 老代码，或需要它成熟的请求模型 | RestSharp（但要接受分配与尾延迟代价） |
| 脚本、探索式调用、URL 拼接多 | Flurl |
| 需要重试/熔断/对冲/备用端点/缓存/限流/观测中的多项，或希望从 OpenAPI 一键生成客户端 | AxiosNet |
| 已经用了 axios（JS）并迁移到 .NET，想保持写法 | AxiosNet |

一句话取舍：**要最少的包和最高的吞吐，选 HttpClient；要最少的自写治理代码和统一的错误/观测模型，
选 AxiosNet——代价是同窗口约 8.5% 的吞吐与每请求多约 1.4 KB 分配。**

---

## 七、复现

```bash
cd test/AxiosNet.Comparison
dotnet run -c Release -- --tag <你的标签>
```

参数、退出码与报告格式见 `test/AxiosNet.Comparison/README.md`。需要门禁时：

```bash
# 吞吐不低于 HttpClient 的 85%，净分配不超过其 2 倍
dotnet run -c Release -- --min-ratio 0.85 --max-alloc-ratio 2
```
