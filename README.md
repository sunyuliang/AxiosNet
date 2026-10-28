# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/badge/nuget-AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![tests](https://img.shields.io/badge/tests-1507%20passed-success)](#构建与测试)
[![coverage](https://img.shields.io/badge/coverage-92.7%25_(branch_89.5%25)-success)](#构建与测试)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**简体中文** | [English](README.en.md)

axios 风格的 .NET HTTP 客户端。写法对齐 JS [axios](https://github.com/axios/axios)，并补齐服务端真正需要的部分：按 origin 复用的连接池、重试、熔断、并发对冲、备用端点、流式响应、在途并发闸门、声明式 API 客户端（模型 / 接口两种声明）、OpenTelemetry 观测点。

| 项 | 值 |
| --- | --- |
| 包 | `AxiosNet`（核心 + 声明式客户端同包）、`AxiosNet.DependencyInjection`（可选） |
| 目标框架 | 单目标 `net6.0`，直接跑在 .NET 6 / 7 / 8 / 9 / 10 及以上 |
| 依赖 | 零第三方，只用 BCL |
| 风格 | 全异步：`Task` + `CancellationToken` |
| 观测 | trace / metrics / events 默认开启，无人订阅时零产出 |

> 仓库：[Gitee（itsm/axios-net）](https://gitee.com/itsm/axios-net) 是提交主仓，PR / tag 以 Gitee 为准；[GitHub](https://github.com/sunyuliang/AxiosNet) 为只读镜像。

---

## 目录

| 分组 | 章节 |
| --- | --- |
| 上手 | [安装](#安装) · [快速上手](#快速上手) · [支持什么](#支持什么) · [客户端](#客户端) |
| 请求 | [方法与 URL](#方法与-url) · [查询参数 params](#查询参数-params) · [请求体 data 与 ContentType](#请求体-data-与-contenttype) · [表单与文件上传](#表单与文件上传) · [响应类型与下载](#响应类型与下载) · [流式响应](#流式响应) · [超时与取消](#超时与取消) · [进度、限速与大小上限](#进度限速与大小上限) · [在途并发上限](#在途并发上限) |
| 数据与错误 | [错误处理](#错误处理) · [拦截器](#拦截器) · [数据转换](#数据转换) · [序列化](#序列化) |
| 弹性 | [重试](#重试) · [熔断](#熔断) · [并发对冲](#并发对冲) · [备用端点](#备用端点) · [尝试管线](#尝试管线) · [短路响应](#短路响应) |
| 基础设施 | [连接池](#连接池) · [HTTP 版本](#http-版本) · [自定义 Handler](#自定义-handler) · [可观测性](#可观测性) |
| 工程化 | [声明式 API 客户端](#声明式-api-客户端) · [接口级声明](#接口级声明) · [依赖注入](#依赖注入) · [配置速查](#配置速查) · [与 axios 对照](#与-axios-对照) · [示例项目](#示例项目) · [运行时与 AOT](#运行时与-aot) · [构建与测试](#构建与测试) · [性能基准](#性能基准) · [项目结构](#项目结构) · [License](#license) |

---

## 安装

```bash
dotnet add package AxiosNet                       # 核心客户端 + 声明式 API 客户端（同一包）
dotnet add package AxiosNet.DependencyInjection   # 可选：DI 注册扩展
```

引用本仓库源码：`dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj`

## 快速上手

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

## 支持什么

按能力分四组，细节见对应章节。

**请求与响应**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| 四种调用形态 | 原文 / `JsonElement` / `dynamic` / 强类型 | `GetAsync<string>` / `GetAsync()` / `GetAsync<dynamic>` / `GetAsync<T>` |
| 客户端实例 | 一套 baseURL + 超时 + 默认头 | `Axios.Create(c => ...)` / `new AxiosClient(config)` |
| 只拼 URL | 不发请求，拿最终 URL | `client.GetUri(c => ...)` |
| 查询参数 | 对象 / 字典 / 集合 / 已编码串 | `c.Params = new { page = 2 }` |
| 请求体 | 编码只看 `ContentType`，不猜类型 | `c.ContentType = AxiosContentType.FormUrlEncoded` |
| 表单与上传 | multipart 字段 + 文件，或裸流直传 | `PostFormAsync(...)` / `FormFile` / `Stream` |
| 响应类型 | JSON / 文本 / 字节 / 流 / dynamic 五种 | `c.ResponseType = ResponseType.Stream` |
| 流式响应 | NDJSON / SSE / JSON 数组 → `IAsyncEnumerable<T>` | `client.RequestStreamAsync<T>(req, AxiosStreamFormat.ServerSentEvents)` |
| 超时与取消 | 单次尝试预算 + 整条请求总预算 + `CancellationToken` | `c.Timeout = ...` / `c.Deadline = ...` / `cancellationToken:` |

**流量治理**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| 进度与限速 | 上传下载进度、字节限速、体积上限 | `c.Callbacks.OnDownloadProgress` / `c.Limits.MaxRate` |
| 在途并发闸门 | 限制同时发出的请求条数并排队 | `c.Limits.MaxConcurrentRequests` + `QueueTimeout` |
| 弹性重试 | 指数退避 / 抖动 / `Retry-After` / 幂等门禁 | `c.Retry.Policy = RetryPolicy.Exponential(...)` |
| 熔断 | 按 host 计数的 Closed/Open/HalfOpen 状态机 | `c.Circuit.Policy = new CircuitBreaker()` |
| 并发对冲 | 同一份请求并发多条分支，先成功的胜出 | `c.Hedging.MaxHedges = 1` |
| 备用端点 | 当前端点用不了时换镜像重发 | `c.Failover.Endpoints = new[] { new Uri("https://mirror") }` |
| 尝试管线 | 一次尝试 = 可组合中间件链，可插可重排 | `Axios.Create(..., pipeline: AxiosAttemptPipeline.Default.Append(...))` |
| 短路响应 | 不发请求，直接交付响应（缓存 / mock / 回放） | `c.ShortCircuit = new AxiosShortCircuit { ... }` |

**数据与错误**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| 统一错误模型 | 所有失败都是 `AxiosException`，带稳定 `Code` | `catch (AxiosException ex) { ex.Code }` |
| 拦截器 | 请求 / 响应双向，可异步 | `client.Interceptors.Request.Use(...)` |
| 数据转换 | axios 同构的两条 transform 链 | `c.TransformRequest.Add(...)` |
| 序列化可替换 | JSON（默认）/ XML 内置，任意格式可插 | `AxiosContent.Register(...)` |
| 可观测性 | `ActivitySource` + `Meter` + `DiagnosticListener` | `AddSource("AxiosNet")` / `AddMeter("AxiosNet")` |

**连接与工程化**

| 能力 | 一句话 | 怎么做 |
| --- | --- | --- |
| 连接池 | 每个 origin 一条调优过的 Handler，共享长连接 | 默认自建；`new HttpConnectionPool(...)` 或 `HttpConnectionPool.GetOrCreate("key")` 可共享 |
| HTTP 版本 | 默认 HTTP/2 可降级，支持 HTTP/3 | `c.Connection.HttpVersion` |
| 自定义 Handler | 套 `DelegatingHandler`，或完全接管传输层 | `ConnectionPoolOptions.HandlerFactory` |
| 声明式客户端 | 用模型 + 特性描述请求 | `[Get("users/{id}")] class X : IApiRequest<User>` |
| 接口级声明 | 接口 + 方法特性，`Create<T>()` 直接生成代理 | `[Get("users/{id}")] Task<AxiosResponse<User>> GetAsync([Path] int id)` |
| 依赖注入 | 单例注册，多站点命名解析 | `AddAxiosClient(...)` / `AddApiClient(...)` |
| Native AOT | 已声明 `IsTrimmable`，裁剪 0 警告 | 源生成 `JsonSerializerContext` |

## 客户端

三种入口，能力完全一致：

| 入口 | 创建方式 | 适用 |
| --- | --- | --- |
| 静态门面 | `Axios.GetAsync(...)` / `Axios.Defaults` / `Axios.Interceptors` | 脚本、单站点应用 |
| 实例 | `Axios.Create(c => ...)` 或 `new AxiosClient(config)` | 常规用法：长生命周期持有，一套默认值 + 一条连接池 |
| 声明式 | `new ApiClient(baseAddress)` | 接口多，用模型或接口描述请求（见[声明式](#声明式-api-客户端)） |

**实例默认值 vs 单次请求**：`Axios.Create` 里配的是实例默认值，快捷方法末尾的 `configure` 配本次请求。`configure` 拿到的是实例默认值的**副本**，所以能真正摘掉默认头：

```csharp
await http.GetAsync("slow", c => c.Timeout = TimeSpan.FromSeconds(30));   // 只影响这次
await http.GetAsync("x", c => c.Headers.Delete("X-Foo"));                 // 默认头被摘掉
```

- **只在实例级生效**：`Connection.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `PoolOptions`（建池时固化）、`Limits.MaxConcurrentRequests`（首个受限请求时固化）。单次请求写**不同的值**会抛 `ERR_BAD_OPTION_VALUE`（复述实例的值则放行），避免「设了没生效」。
- **`Params` / `Data` 按「有没有赋值过」合并，不按是否为 `null`**：不碰这两项 = 继承实例默认值；显式写 `c.Params = null` / `c.Data = null` = 摘掉实例默认值（与 `Headers.Delete` 用「删除」表达摘头同一个道理）。所以 `PostAsync(url)` 这种不传 `data` 的调用仍会带上 `Defaults.Data`，要发空正文得显式写 `Data = null`。
- **一个站点一个实例，不要按请求新建**：每个实例自建一条连接池，`Axios.Create` / `new AxiosClient` 各建一套 Handler；按请求新建等于退化成 `new HttpClient()` 那个经典坑——长连接不复用，连接与句柄持续增长。要换默认值就换实例，要共享连接就共享池（见[连接池](#连接池)）；DI 场景直接用 `AddAxiosClient` 注册单例。
- 实例用 `using`（或交给 DI 容器持有）收尾，连接池与 Handler 随之释放；在途请求不会被打断，只是不再接受新请求。

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

| `ContentType` | 对象 / 字典 / dynamic / 集合 | 基础类型（数值 / 布尔 / 枚举 / Guid / 日期…） | `string` / `byte[]` / `Memory<byte>` / `Stream` / `HttpContent` |
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

- **超时**：正文与响应头共用同一份预算——流式响应的正文读取也计入 `Timeout`（`Deadline` 生效时是它的剩余额度），读到一半到点即 `ECONNABORTED`；不设超时时正文不限时，靠取消令牌退出。
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
- `Deadline` 是**整条逻辑请求**的预算（从调用开始，含拦截器、排队、重试等待、对冲分支）：`Timeout` 会被重试 / 备用端点 / 对冲各自复制一份，`Deadline` 只有一份，每次尝试实际拿到 `min(Timeout, 剩余)`。
- 超时抛 `ECONNABORTED`（消息可用 `Callbacks.TimeoutMessage` 覆盖），取消抛 `ERR_CANCELED`，原始 `OperationCanceledException` 留在 `Cause`。

```csharp
// 调用方承诺「15 秒内一定给出结果」：重试 5 次 × 单次 10s 也不再是上界
await client.GetAsync("orders/scan", c =>
{
    c.Timeout  = TimeSpan.FromSeconds(10);   // 单次尝试
    c.Deadline = TimeSpan.FromSeconds(15);   // 整条请求
    c.Retry.Policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(200));
});
```

- 总预算用尽时下一次尝试**根本不发出**：抛 `ECONNABORTED`，消息指明是第几次（`... before attempt #7 could be sent`）；被总预算压小的那次超时，消息会写 `(remaining Deadline budget; Timeout = 10000ms)`，避免误判成 `Timeout` 没生效。
- 排队时间**计入** `Deadline`（`Timeout` 则不计排队）——总预算量的是调用方感知的时长；因此剩余预算比 `Limits.QueueTimeout` 更紧时，**排队等待会被剩余预算截断**，失败码是 `ECONNABORTED` 而不是 `ERR_QUEUE_TIMEOUT`（报错要指向真正的约束）。`QueueTimeout` 为空（一直等）时同样受这条约束。
- **流式正文也在预算之内**：正文读取沿用被预算压小后的剩余额度，读到一半到点即 `ECONNABORTED`；长连接（SSE / 大文件下载）不要设 `Timeout` / `Deadline`，改用取消令牌退出。
- `Deadline = null` 不设总预算（默认），`Zero` 与 `null` 同义，负值 `ERR_BAD_OPTION_VALUE`；按请求显式写 `null` 可以关掉实例默认的总预算。

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
- `Content-Length` 只是提示，不是截断依据：声明值比实际正文短时（代理改头、自定义 `HttpContent`、注入的 Handler 不按声明截断）照样读到流结束，既不丢尾部也不算漏 `MaxContentLength`。
- `MaxBodyLength` 只对能算出长度的正文生效（JSON / 表单 / `byte[]` / 可 seek 流）；multipart 不拦截。
- 负值一律 `ERR_BAD_OPTION_VALUE`；`MaxRate = 0` 等于不限速。
- `MaxRate` 的计时从**实际传输首字节**起算：拿到响应到开始读之间的空档（线程排队、业务先做别的事）不算已消耗预算，晚读不会让限速变松。

### 在途并发上限

`MaxRate` 限的是**字节速度**，拦不住「每秒几百个小请求」——那来自同时在途的请求条数：

```csharp
var client = Axios.Create(c =>
{
    c.Limits.MaxConcurrentRequests = 4;                 // 本实例最多 4 条在途，其余排队
    c.Limits.QueueTimeout = TimeSpan.FromSeconds(2);    // 排队最多等 2 秒
});

await client.GetAsync("fast", c => c.Limits.QueueTimeout = TimeSpan.Zero);   // 满了立刻失败
```

| 项 | 默认 | 语义 |
| --- | --- | --- |
| `MaxConcurrentRequests` | `null`（不限） | 本实例同时在途条数；正整数 |
| `QueueTimeout` | `null`（一直等） | 等槽位上限：`null` 一直等 / `Zero` 不排队 / `>0` 超时失败；实际等待还被 `Deadline` 剩余预算封顶 |

- 排队超时抛 **`ERR_QUEUE_TIMEOUT`**（不是 `ECONNABORTED`）：请求根本没发出去，该降速 / 扩容 / 退避。
- 排队**不计入** `Timeout`（重试每次尝试各自计一次），排队期间可用 `CancellationToken` 取消。
- 槽位覆盖「真正在途」全程：重试每次尝试各自取槽（退避不占位），**流式响应一直占到正文流释放**。
- 闸门按实例共享，属实例级：按请求写不同值、或构造后改 `Defaults` 都抛 `ERR_BAD_OPTION_VALUE`。
- **自锁风险**：在拦截器 / 进度回调里用同一实例发子请求会互抢槽位，请给 `QueueTimeout` 设上限或换实例。
- **与重试同时开**：`ERR_QUEUE_TIMEOUT` 本身不重试 —— 闸门满载说明本实例在途已顶格，立刻重发只会再撞同一个闸门。要「等并发降下来再试」，请在调用方外层自己做（降速 / 扩容 / 换实例）。
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
| `ERR_BAD_RESPONSE` | 5xx；响应无法反序列化为目标类型；响应体超 `MaxContentLength`；泛型与 `ResponseType` 不匹配；下载进度回调 / `transformResponse` 抛异常 |
| `ECONNABORTED` | 超过 `Timeout`（含流式正文阶段），或 `Deadline` 总预算已耗尽 |
| `ERR_NETWORK` | 连接被拒 / DNS 失败 / 连接中断 / 正文截断 |
| `ERR_CANCELED` | `CancellationToken` 取消，或客户端已释放 |
| `ERR_QUEUE_TIMEOUT` | 并发槽位一直占满，超过 `QueueTimeout`（请求未发出） |
| `ERR_CIRCUIT_OPEN` | 熔断打开，请求**没有发出**（`RetryAfter` 给建议等待） |
| `ERR_POOL_EXHAUSTED` | 连接池 origin 基数饱和（全部在途、无可驱逐空闲节点），请求未发出；可观测、默认不自动重发，降扇出 / 调大上限 / `Clear()` 回收后再重试 |
| `ERR_INVALID_URL` | URL 非法、相对地址但没有 `BaseAddress`、非 http(s) scheme |
| `ERR_BAD_OPTION_VALUE` | 选项取值非法（按请求写实例级选项、multipart 头体矛盾、`Params` 是标量、重试 / 熔断配置越界等） |

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

## 数据转换

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

## 序列化

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
```

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
| `ERR_NETWORK` / `ECONNABORTED` | 重试 |
| 408 / 429 / 5xx | 重试（`RetryStatusCodes` 可换） |
| 其它 4xx、配置 / URL 错误 | 不重试 |
| `ERR_CANCELED` | 不重试 |
| POST / PATCH 等非幂等方法 | 不重试（`RetryNonIdempotentMethods = true` 打开） |

库级强制不重试（自定义策略也改不掉）：`ERR_CANCELED` / `ERR_CIRCUIT_OPEN` / `ERR_QUEUE_TIMEOUT` / `ERR_BAD_OPTION_VALUE` —— 这些情况下请求要么没发出，要么立刻重发只会撞同一个闸门，要么是管线里的配置 / 编程错误。

其它成员：`Delay` / `BackoffFactor` / `MaxDelay` / `Jitter`（默认 `true`，等待落在 50%–100%）/ `HonorRetryAfter`（默认 `true`，服务端给了 `Retry-After` 就按它等，仍受 `MaxDelay` 封顶）/ `RetryWhen`。

- `Timeout` 是**每次尝试**的预算（3 次重试 × 10s 最坏 40s）；要「整条请求」的上界用 `Deadline`，也可以在调用方用 `CancellationToken`。
- 拦截器、`transformRequest`、正文序列化只跑一次；响应拦截器看到最终结果。
- 正文必须可重放：可 seek 的流每次重试前自动拨回起点；不可 seek 的流与 `StreamContent`（含 multipart 部件）在开启重试时直接抛 `ERR_BAD_OPTION_VALUE`。
- 自定义策略：实现 `IAxiosRetryPolicy`（`MaxRetries` / `ShouldRetry(ctx)` / `GetDelay(ctx)`），`ctx` 提供 `Attempt` / `MaxRetries` / `Exception` / `Response` / `StatusCode` / `ErrorCode` / `Delay` / `Config` / `Method`。策略被并发共享，需无状态。

## 熔断

重试解决「这一次失败了要不要再试」；熔断解决「下游整体不可用时还要不要试」。默认不熔断：

```csharp
using AxiosNet.Circuit;

var breaker = new CircuitBreaker
{
    FailureThreshold = 5,                       // 连续 5 次下游故障后打开
    BreakDuration = TimeSpan.FromSeconds(30),   // 冷却 30 秒后放探测请求
    HalfOpenAttempts = 1                        // 半开态同时只放 1 个探测
};

breaker.StateChanged += change => logger.LogWarning(
    "{Host} 熔断：{From} -> {To}", change.Host, change.Previous, change.Current);

var client = Axios.Create(c => c.Circuit.Policy = breaker);

await client.GetAsync("health", c => c.Circuit.Policy = null);   // 探针不该被熔断挡住
breaker.Reset();                                                 // 运维手工恢复
```

状态机：`Closed` →（连续失败达 `FailureThreshold`）→ `Open` →（冷却 `BreakDuration`）→ `HalfOpen` →（探测成功）→ `Closed`；探测失败**立刻**回 `Open` 并重新计时。

失败口径与重试门禁对齐（**取消 / 4xx 不计失败**）：

| 情况 | 计入失败？ |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED`（超时） | 计入 |
| 5xx | 计入 |
| 4xx（含 408 / 429） | 不计入（也不清零） |
| `ERR_CANCELED` | 不计入 |
| 配置 / URL / 序列化 / 反序列化错误 | 不计入 |

- **按 host 计数**：默认维度 `host:port`（`uri.Authority`）；`KeySelector` 可改成 `ctx => ctx.Uri.Host` 或按路由前缀隔离租户。
- 被拒请求**不发出**：不建连、不占租约，直接抛 `ERR_CIRCUIT_OPEN`，`AxiosException.RetryAfter` 给建议等待。
- 与重试同时开：闸门在**每次尝试之前**，熔断打开后重试的下一轮会被立刻拦下；`ERR_CIRCUIT_OPEN` 本身不重试。
- 开着重试时**每次尝试各记一笔**：3 次重试 + 首败 = 4 笔。
- **流式响应只按响应头记账**：正文读取发生在记账之后，中途断流（`IOException` / 连接重置）会以 `ERR_NETWORK` 抛给调用方，但不回补熔断失败、也不会触发重试。需要把「正文完整性」纳入失败统计时请在消费方自行处理，或改用 `ResponseType.ByteArray` 让整段正文进入记账范围。
- 状态表有上限（`MaxTrackedHosts`，默认 512，软上限）：满员只淘汰「丢了不改变判定」的维度，正在熔断 / 半开的条目不会被丢。
- 自定义策略：实现 `IAxiosCircuitPolicy`（`ShouldAllow(ctx)` / `GetRetryAfter(ctx)` / `Record(ctx)`）；`ctx` 提供 `Host` / `Uri` / `Config` / `Exception` / `StatusCode` / `ErrorCode` / `Method` / `IsSuccess` / `IsFailure`。策略**有状态**且被并发共享，需自己保证线程安全。

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
- **败者收尾的等待没有上限**：胜出后要等败者真正结束才返回（不等就会在返回后继续占着连接租约与槽位）。库内中间件都响应 `ctx.AttemptToken`；自写中间件忽略它就等于把调用方一起拖住。
- **`transformResponse` 与响应侧回调在每条分支上各跑一次**（含落败分支）：带副作用的转换请自行保证幂等，或关掉对冲。
- **以下请求按单次发送处理**，不降级也不报错：流式响应（败者的正文无人消费）、非幂等方法（默认 `HedgeNonIdempotentMethods = false`）、正文不能并发重放（`Stream` / multipart / 上传进度包装）、响应侧带共享状态（`OnDownloadProgress` 或 `MaxRate > 0` 时进度会报两份、限速翻倍）。
- 泛型入口照常对冲：每条分支各填一份自己的响应对象，胜出者的那份交给调用方，调用方从 `config.Response` 读到的也是胜出者的状态码与响应头。
- `MaxHedges` 上限 16：再多分支追不回延迟，只会把下游打满。

## 备用端点

当前端点「用不了」时换下一个端点重发同一份请求，顺序即优先级：

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
| `ERR_NETWORK` / `ECONNABORTED`（超时） / `ERR_POOL_EXHAUSTED` | 换 |
| 5xx（`ERR_BAD_RESPONSE` 且状态码 ≥ 500） | 换 |
| `ERR_CIRCUIT_OPEN` | 换 —— 熔断按 `host:port` 独立成账，主端点开着挡不住备用端点 |
| 4xx、其它 `ERR_BAD_RESPONSE`（如 2xx 的畸形正文） | 不换（换镜像改变不了） |
| `ERR_QUEUE_TIMEOUT` | 不换（本机没抢到槽位，与下游无关） |
| 配置 / 编程错误、取消 | 不换 |

- **每次重试都从第一个端点重新开始**：端点表在重试之内，一轮换完所有端点才把第一条失败交给重试。
- **换端点会重算 `Host`**：调用方显式设过的 `Host` 头在换端点时丢掉，由目标地址重新算 —— 留着会让镜像按原端点的虚拟主机路由（`HttpClient` 只认 `Headers.Host`，它优先于 URI authority）。要按端点用不同的 `Host`，请写尝试中间件按 `ctx.Uri` 设置（`config.Headers` 只应用在模板上）。
- **端点表不能重复**：两个端点拼出同一个 base URL（尾斜杠不算区别）会在发请求前抛 `ERR_BAD_OPTION_VALUE` —— 那只是把同一份请求对同一个地址多打一遍。
- **端点拼出来就是当前地址时跳过**：端点与请求同 origin 且**不带路径前缀**（典型是只写 `https://backup.example.com`）时，换过去等于原地重发，直接跳过继续下一个端点。
- **`Timeout` 是「每个端点每次发送」的预算**：与重试同口径，3 个端点 + 10s 最坏 30s；要整条请求的上界请设 `Deadline`（跨端点共享，见[超时与取消](#超时与取消)）。上传进度同样跨端点累计（与重试重发正文一致，不回跳）。
- **正文要能按顺序重放**：与重试同一份要求（流必须可 seek），不可 seek 的流与 `StreamContent` 在开启备用端点时直接抛 `ERR_BAD_OPTION_VALUE`。
- **流式响应只在响应头阶段能换端点**：正文交给调用方之后，中途断流既不换端点也不重试（与熔断「只按响应头记账」同口径）。
- **端点必须是绝对的 `http` / `https` 地址，且不能带 query / fragment**：端点只当 base URL 用，端点里的 query 与请求自己的 query 怎么拼没有明确规则，因此直接报 `ERR_BAD_OPTION_VALUE`（要让某个参数只出现在备用端点上，请用请求拦截器按 `ctx` 拼 URL）。
- **端点表赋值即快照**：之后改调用方自己的列表不影响已生效的配置；读回来的就是内部那一版，调用方不得修改。
- **换端点会重判 HTTP/3 护栏**：跑不了当前 HTTP 版本的端点（如明文镜像 + 严格 h3）直接跳过继续下一个，全部不可用时才抛 `ERR_BAD_OPTION_VALUE`。
- 每次换端点产生一条尝试事件，`AttemptNumber` 不变、`EndpointIndex` 递增（见[可观测性](#可观测性)）。

## 尝试管线

「一次尝试」（单次发送）被抽象成可组合的中间件链，默认链是 **重试 → 备用端点 → 熔断 → 对冲 → 闸门 → 发送**。新增每次尝试的能力（观测、审计）插一个中间件即可，不必再往客户端里加分支。

```csharp
using AxiosNet.Pipeline;

// 插一个「每次尝试」的探针：Append 落在最内层（闸门之后、发送之前），重试 N 次就调用 N+1 次
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
- **顺序是契约**：重试在最外层，于是每次尝试都会重过一遍备用端点、熔断、对冲与闸门；备用端点在熔断之外，于是每个端点各自过熔断；熔断在对冲之外，于是整组分支只记一笔；熔断排在闸门之前，被拒的请求不占槽位。
- **重排与摘除**：`AxiosAttemptPipeline.Create(...)` 显式列出全部中间件（第一个最外层），`Remove(Attempt.Gate)` 摘掉某项；不可变，每次编辑返回新实例。内置项重复出现会报错。
- **不提供「放到最外层」的入口**：那个位置每次请求只调用一次，与响应拦截器完全同构。`InsertBefore(Attempt.Retry, ...)` 会明确拒绝并指向 `client.Interceptors.Response`——它还有 `OnRejected`、运行时增删、覆盖短路三个额外好处。
- **并发槽位不用你管**：闸门自己兜底归还 —— 中间件不调 `next`（自己给一份响应）或自己抛异常，槽位也不会漏。唯一不让闸门归还的情况是**库内**的流式响应：槽位随正文交给 `StreamOwner`，由流的 `Dispose` 释放，正文下载期间该请求一直占着额度。中间件自建 `StreamOwner` 不算这一例外：闸门照常归还自己的槽位，自建凭证不占并发额度。
- **谁创建谁释放**：中间件自己 clone 的 `HttpRequestMessage` 由自己释放（模板仍归管线）。替换响应时必须处置旧的 `AxiosAttemptResult.StreamOwner`，或把它带进新结果（`new AxiosAttemptResult(newResponse, old.StreamOwner)`）—— 它持有连接租约与并发槽位，丢弃即泄漏。
- **中间件自身的耗时不计入 `Timeout`**：超时预算在终端内部起算，闸门排队与中间件前置逻辑都在它之外。
- 响应侧读请求头用 `response.GetRequestHeader(name)`（先查请求头、再查内容头）；短路响应没有底层请求消息，返回 `null`。

与另外四个扩展点的分工：

| 想做的事 | 用哪个 |
| --- | --- |
| 改 URL / 头 / 请求体、注入短路（一次请求一次） | 请求拦截器 |
| 在序列化前加工要发的 `Data` | `transformRequest` |
| 在反序列化后加工回来的 `Data`（每次尝试） | `transformResponse` |
| 加工最终响应、把异常兜成响应 | 响应拦截器 |
| 观察或干预**每一次尝试**（含审计） | 尝试中间件 |
| 改「重试几次 / 退避多久 / 什么时候熔断 / 要不要对冲 / 换哪些端点」 | 对应的 `Policy` 或子配置，不换中间件 |

## 短路响应

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

## 连接池

`AxiosClient` 默认自建一条按 `scheme://host:port` 隔离的连接池（`HttpConnectionPool`）：每个 origin 一个调优过的 `SocketsHttpHandler`，请求共享 TCP 长连接。

> **不要按请求建客户端**：`Axios.Create` / `new AxiosClient` 各自建一条池，按请求新建 = 每请求一套 Handler，长连接不再复用（就是 `new HttpClient()` 那个经典坑）。一个站点一个实例；多个客户端要共享连接，就显式传同一条池（下面的用法 2），DI 里用 `AddAxiosClient` 注册单例。
>
> **丢弃实例前必须 `Dispose`**：未释放的池由后台驱逐定时器持有，GC 收不走，它的连接要等到 `IdleHandlerLifetime`（默认 5 分钟）之后才被回收。

```csharp
using AxiosNet.Pooling;

// 1) 自建池（构造时配置）
var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = true
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
| `PooledConnectionLifetime` | 10 分钟 | 连接最长复用时长，到期后下次取用换新（不打断在途） |
| `PooledConnectionIdleTimeout` | 2 分钟 | 空闲连接关闭 |
| `ConnectTimeout` | 15 秒 | 建连超时 |
| `IdleHandlerLifetime` | 5 分钟 | origin 节点空闲多久被回收 |
| `EvictionInterval` | 30 秒 | 后台清扫间隔；`<= 0` 不启动定时器 |
| `AllowAutoRedirect` / `MaxAutomaticRedirections` | `true` / 5 | 自动重定向；`MaxRedirects = 0` 关闭 |
| `AutomaticDecompression` | `true` | gzip / deflate / br |
| `EnableMultipleHttp2Connections` | `true` | h2 下允许按 `MaxConnectionsPerServer` 继续加连接 |
| `KeepAlivePingDelay` / `KeepAlivePingTimeout` | `InfiniteTimeSpan`（不发）/ 20 秒 | h2 keep-alive ping 的间隔与等 ACK 的上限；默认与 `SocketsHttpHandler` 一致（关闭），设正值即打开。h1 无影响 |
| `KeepAlivePingPolicy` | `WithActiveRequests` | 何时 ping：`WithActiveRequests`（有在途请求时）/ `Always`（空闲也维持）|
| `MaxEndpointCount` | 1024 | origin 节点上限，满了先驱逐最久未用的空闲节点；若全部在途（无可驱逐），新建节点抛 `ERR_POOL_EXHAUSTED`（请求未发出，可观测、默认不自动重发，降扇出 / 调大上限 / `Clear()` 回收后再重试） |
| `UseCookies` | `false` | 打开后整个池共用一份 `CookieContainer` |
| `Proxy` / `Credentials` | `null` | 不设代理时**不使用**系统 / 环境代理；要走系统代理请设 `HttpClient.DefaultProxy` |
| `ServerCertificateCustomValidationCallback` / `EnabledSslProtocols` / `ConfigureSslOptions` | `null` | 证书校验、TLS 版本、客户端证书 |
| `HandlerFactory` | `null` | 每个 origin 创建 Handler 时调用，见[自定义 Handler](#自定义-handler) |

- `Dispose()` 是优雅关闭：空闲节点立即释放，有在途请求的节点等租约归还后回收；`Clear()` 立即释放全部节点（会打断在途）。
- DNS 变更的收敛窗口由 `PooledConnectionLifetime` 决定；服务发现用 `HandlerFactory` + `ctx.CreateDefaultHandler()` 设 `ConnectCallback`。
- **h2 keep-alive 默认关闭**（与 `SocketsHttpHandler` 一致，不悄悄改变线上行为）：把 `KeepAlivePingDelay` 设成正值（常见 30 秒）即打开，只在有在途请求时发。它对付的是「静默掐断」——AWS ALB 默认 60 秒空闲断连、NAT 表 30–120 秒，客户端以为连接还在池里，下一个请求撞上已关闭的连接，要么多付一次 TLS 握手，要么在 h2 上收到 `GOAWAY` / 流重置（表现为偶发 `ERR_NETWORK`）。代价是一个 17 字节的帧，比一次重连便宜得多；低频高价值调用（webhook 回调、定时任务）可以配 `Always` 连空闲期也维持。反过来，链路上若有**不回应 `PING`** 的代理 / 中间盒，打开后连接会被判死重连（`ERR_NETWORK` 变多）——这也是它默认关闭的原因。
- **`UseCookies` 是池级共享，不按业务身份隔离**：整个池共用一份 `CookieContainer`，隔离维度只有域 / 路径。多租户、爬虫、按用户切身份的进程打开它会让不同身份串 cookie —— 请按租户各建一条池，或用请求头自己管 `Cookie`。
- **Trailers 目前不读取**：chunked 响应的尾部头（校验和、配额）停留在 `HttpResponseMessage.TrailingHeaders`，没有暴露到 `AxiosResponse`。需要时用 `HandlerFactory` 包一层在正文读完后取，或直接注入自定义 Handler。
- **代理是池级的，同一个 origin 只能有一条代理**：`Proxy` 落在 Handler 实例上，按请求改代理会抛 `ERR_BAD_OPTION_VALUE`。要多出口 IP 就按代理各建一条池（`HttpConnectionPool.GetOrCreate("egress-us", new ConnectionPoolOptions { Proxy = ... })`），每个出口一条池、客户端之间不共享 —— 隔离维度是「池」，不是「请求」。
- **命名共享池**（用法 4）解决「多客户端想复用连接又不方便传池对象」：key 由调用方给，**不做配置指纹**——`HandlerFactory` / `Proxy` / `Credentials` / 证书回调这类引用成员没有可靠的结构等价，两份「长得一样」的配置宁可报错也不共用一条池。规则只有三条：同名取同一条；`options` 只在首次创建时生效（重复传一份**不同**的配置抛 `ERR_BAD_OPTION_VALUE`，不静默忽略）；表里的池不归任何客户端所有，客户端 `Dispose` 不会关掉它，收尾用 `Release(key)`。
- 想知道「池是不是被建太多了」看指标 `axiosnet.client.pool.active_pools`（存活池数）与 `.shared_pools`（命名池条目数）。

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

- 4xx / 5xx 即使被 `validateStatus` 接受也标 `Error`；失败另记一条 `exception` 事件；每次决定重试加一条 `retry` 事件（带 `axiosnet.retry.delay_ms` / `axiosnet.retry.reason`）。
- `ResponseType.Stream` 的 span 在拿到响应头时结束，耗时等于首包时间。

**Metrics**（源名 `AxiosNet`）：

| 指标 | 类型 | 单位 | 标签 |
| --- | --- | --- | --- |
| `axiosnet.client.request.duration` | `Histogram<double>` | `s` | method / server.address / server.port / status_code / error.type |
| `axiosnet.client.active_requests` | `ObservableGauge<long>` | `{request}` | 无 |
| `axiosnet.client.request.retries` | `Counter<long>` | `{retry}` | method / server.address / server.port / error.type / `axiosnet.retry.reason` |
| `axiosnet.client.gate.queue_depth` | `ObservableGauge<long>` | `{request}` | 无 |
| `axiosnet.client.circuit.state` | `ObservableGauge<long>` | `{circuit}` | `axiosnet.circuit.state`（`closed` / `open` / `half_open`） |
| `axiosnet.client.pool.leases` | `Counter<long>` | `{lease}` | `axiosnet.pool.endpoint.reused` |
| `axiosnet.client.pool.endpoints.created` | `Counter<long>` | `{endpoint}` | 无 |
| `axiosnet.client.pool.endpoints.evicted` | `Counter<long>` | `{endpoint}` | `axiosnet.pool.eviction.reason` |
| `axiosnet.client.pool.active_leases` | `ObservableGauge<long>` | `{lease}` | 无 |
| `axiosnet.client.pool.active_pools` | `ObservableGauge<long>` | `{pool}` | 无 |
| `axiosnet.client.pool.shared_pools` | `ObservableGauge<long>` | `{pool}` | 无 |

- 指标只用低基数标签；URL 路径只进 span。**池指标不带 host / port**：网关 / 爬虫 / 多租户场景下一个 host 一条时间序列会把后端打爆；要定位到具体 origin 看 span 的 `server.address` / `server.port` 与 `axiosnet.pool.endpoint.reused`。
- `axiosnet.retry.reason` 取值有界：有状态码统一报 `status`（具体码在 `error.type`），否则是库错误码（`ECONNABORTED` / `ERR_NETWORK` …）——「该调策略（429）」和「该修下游（5xx）」在面板上直接分得开。
- `gate.queue_depth` 是此刻正在等槽位的请求数（进程级），`circuit.state` 是「多少个熔断维度处于该状态」（熔断器实例 × key），两者都不带 host。
- `request.duration` 是逻辑请求耗时（含重试与退避）；`active_requests` 请求结束即减（流式到响应头），`active_leases` 要等正文流释放——「长下载占了多少并发额度」看后者。
- 池域指标描述池这个资源，不受 `Telemetry.Enabled` 影响。
- `pool.active_pools` 是存活池数（建 +1 / `Dispose` -1），`pool.shared_pools` 是命名池表的条目数：前者持续上涨 = 有代码在反复建池（典型是「按请求 new 客户端」）。

**Events**（`DiagnosticListener` 名 `AxiosNet.DiagnosticListener`）：`AxiosNet.RequestStart` / `RequestStop` / `RequestFailed` / `Retry`，载荷是公开类型（`AxiosRequestStartEvent` / `StopEvent` / `FailedEvent` / `AxiosRetryContext`）。

**尝试审计**（`AxiosNet.AttemptStop` / `AxiosNet.AttemptFailed`，载荷 `AxiosAttemptEvent`）要显式装上 `Attempt.Audit` 才有：

```csharp
var pipeline = AxiosAttemptPipeline.Default.Append(Attempt.Audit);   // 落在闸门之后、发送之前
using var client = Axios.Create(pipeline: pipeline);
```

- 覆盖**真正到达发送的尝试**：熔断拒绝、闸门排队超时、短路响应都不产生尝试事件（审计在它们之内）。
- 事件条数是「**端点 × 分支**」：换端点每换一个一条（`AttemptNumber` 不变，靠 `EndpointIndex` 区分），对冲每条分支一条；被胜出者取消的败者不发失败事件（那是清理，不是下游故障），在胜者出现之前就真实失败的败者照常发。
- 没人订阅时零成本直通：不建状态机、不取时间戳。

**按请求关闭或裁剪**：

```csharp
await client.GetAsync<object>("health", c => c.Telemetry.Enabled = false);      // 完全不产出
await client.GetAsync<object>("api",    c => c.Telemetry.CaptureUrl = false);   // 不写 url.full
await client.GetAsync<object>("api?token=...", c => c.Telemetry.CaptureQuery = true);   // 把查询串也写进 url.full
```

## 声明式 API 客户端

`AxiosNet.WebAPI` 命名空间（与核心同包）：用模型 + 特性描述请求，调用只剩一句 `client.RequestAsync(request)`。

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
| 静态头 | `[Headers]`（类级） | `"Name: Value"`；只写名字表示删除该头 |
| 表单 | `[Multipart]` + `[Part]` | 模型自身即表单；`[Part]` 覆盖字段名 / 文件名 / 部件类型 |
| 请求体 | `[Body]` | 每个模型最多一个；`null` 不发请求体 |
| 排除 | `[ApiIgnore]` | 完全不绑定 |

- **单负载模型可省 `[Body]`**：POST / PUT / PATCH 且模型只有一个未标注的**非标量**属性时默认按 Body 绑定（字典属性除外，始终按查询串）。
- 一个属性可同时标多个绑定特性（`[Path]` + `[Header]`），值只读一次、分别应用。
- 按请求微调：继承 `ApiRequest<T>` 覆写 `Configure(config)`，或 `RequestAsync(request, config => ...)`。
- 模型定义非法（缺路由、占位符未绑定、多个 Body…）首次调用抛 `InvalidOperationException`；启动期体检用 `ApiClient.ValidateModels(typeof(A), ...)` 或 `ValidateModels(assembly)`（返回全部错误消息，不抛）。
- 授权：`[Authorize]` / `[Authorize("Basic")]` 声明需要授权，`[AllowAnonymous]` 跳过整条链路；`IAuthorizationProvider` 是扩展点，内置 `BearerAuthorizationProvider`。
- 强类型错误：`client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()` 后状态码错误抛 `ApiError<TError>`（网络 / 超时 / 取消不经过工厂）。
- 其它：`client.UrlParameterFormatter` 接管 `[Path]` / `[Query]` 文本形态；`client.Axios` 直接进底层 `AxiosClient`（拦截器、池、Handler 全可用）；流式正文的超时口径跟随该客户端配置的 `Timeout` / `Deadline`，没有独立的空闲超时旋钮。

## 接口级声明

不想为每个端点写请求模型时，可直接把接口交给 `client.Create<T>()`：路由写在方法上、绑定特性写在参数上，运行时由 `DispatchProxy` 生成代理。绑定语义与请求模型**同一套内核**（路径模板校验、重名检测、默认 Body 推断、标量护栏、静态头、授权全部一致）。

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

- 每个方法必须标注且只标注一个路由特性；返回类型必须是 `Task<AxiosResponse<T>>`（不支持同步返回 / `ValueTask` / 泛型方法 / `ref`·`out`）。返回 `Task<AxiosResponse<IAsyncEnumerable<T>>>` 即流式方法。
- `CancellationToken` 参数按名字识别（最多一个），不参与绑定；其余参数按 `[Path]` / `[Query]` / `[Header]` / `[HeaderCollection]` / `[Part]` / `[Body]` / `[ApiIgnore]` 或「未标注」规则处理，默认 Body 推断同样生效。
- `[Headers]` / `[Authorize]` / `[AllowAnonymous]` 可标在接口上；继承基接口时，静态头与授权按**当前派生接口作用域**合并（同一基方法在不同派生接口下可以有不同静态头）。
- 接口写在非法（缺路由、多个 Body、`[Part]` 未配 `[Multipart]`、`[Multipart]` 与 `[Body]` 同用、`[Stream]` 用在非流式方法、参数重名…）时，`Create<T>()` 抛 `InvalidOperationException`；启动期体检用 `ApiClient.ValidateInterfaces(typeof(IA), ...)` 或 `ValidateInterfaces(assembly)`（返回全部错误消息，不抛）。
- 授权回调与错误工厂拿到的是 `ApiInterfaceInvocation`（接口类型、方法名、参数快照），不是模型实例；需要按请求分支时用这三项。
- 接口级流式：帧格式用 `[Stream(AxiosStreamFormat.ServerSentEvents)]` / `[Stream(AxiosStreamFormat.JsonArray)]` 声明，省略按 NDJSON；返回流只能枚举一次，超时与错误口径与 `RequestStreamAsync(...)` 完全一致。
- 接口方法不支持方法级 `configure` 钩子，需要逐请求微调时用请求模型。
- 代理基于 `DispatchProxy`（Reflection.Emit），**仅 JIT**：Native AOT 下 `Create<T>()` 抛 `PlatformNotSupportedException`；裁剪时需 root 接口类型及其参数 / 返回类型。

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
| `Data` | `null` | 请求体 |
| `Timeout` | `Zero`（不超时） | 单次尝试的预算（建连 + 响应头 + 正文） |
| `Deadline` | `null`（不设） | 整条请求的总预算：跨重试 / 端点 / 对冲共享，并与 `Timeout` 取小 |
| `ResponseType` | `Json` | `Json` / `Text` / `ArrayBuffer` / `Stream` / `Dynamic` |
| `CancellationToken` | `default` | 外部取消 |
| `TransformRequest` / `TransformResponse` | 空 | 转换链 |
| `Response` | `null` | 响应阶段可读（状态码 / 头 / 媒体类型） |
| `ShortCircuit` | `null` | 短路响应：不发请求直接交付（只对本次请求生效） |

九个子对象（首次访问才创建）：

**`Serialization`** — `JsonSerializerOptions`（Web 风格）/ `ContentSerializer`（`null` = 内置 JSON）/ `RequestEncoding` / `ResponseEncoding` / `ParamsSerializer` / `FormFieldNameSelector` / `CollectionFormat`（`Brackets`）

**`Connection`** — `Auth`（`new AxiosAuth(user, pass)`）/ `MaxRedirects`（5，`0` 不跟随，仅实例级）/ `WithCredentials`（仅实例级）/ `Proxy` / `Credentials` / `PoolOptions`（仅实例级）/ `HttpVersion`（`null` = 2.0）/ `VersionPolicy`（`null` = `OrLower`）

**`Limits`** — `MaxContentLength` / `MaxBodyLength` / `MaxRate`（均 `null` = 不限）/ `MaxConcurrentRequests`（`null`，仅实例级）/ `QueueTimeout`（`null` 一直等）/ `MaxStreamFrameLength`（`null` = 8 MB，1–64 MB）

**`Callbacks`** — `ValidateStatus`（`Func<int,bool>`，默认 2xx）/ `TimeoutMessage` / `OnUploadProgress` / `OnDownloadProgress`

**`Retry`** — `Policy`（`null` = 不重试）/ `OnRetry`

**`Circuit`** — `Policy`（`null` = 不熔断）/ `OnRejected`

**`Hedging`** — `MaxHedges`（`0` = 不对冲，上限 16）/ `Delay`（80ms，第 n 条分支起跑于 `n × Delay`，`Delay × MaxHedges` 不得超过计时器范围）/ `HedgeNonIdempotentMethods`（`false`）

**`Failover`** — `Endpoints`（`null` = 不换端点；有序的绝对 `http`/`https` 地址，可带路径前缀，不能带 query / fragment，不能重复）

**`Telemetry`** — `Enabled`（`null` = 开启）/ `CaptureUrl`（`null` = 记录，不含查询串）/ `CaptureQuery`（`null` = 不记查询串）

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
| —（axios-cache-interceptor 之类） | `c.ShortCircuit`（短路：缓存命中 / mock / 离线回放） |
| —（无对应） | `HttpConnectionPool`、声明式 `ApiClient`、`RequestStreamAsync`、`MaxConcurrentRequests` / `QueueTimeout` |

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

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) 是离线可运行的控制台示例，自带 `TcpListener` 演示服务器，共 46 项：

```bash
dotnet run --project samples/AxiosNet.Simple
```

启动后是交互菜单（反射列出全部示例，输入编号运行，`a` 跑全部，`q` 退出）。

- **核心 32 项**：静态门面、实例创建、JSON / 表单 / multipart、params、Basic Auth、dynamic 请求与响应、text / arraybuffer、拦截器、错误与取消、连接池、文件上传下载、getUri 与大小限制、进度与限速、自定义 Handler、单文件上传、Newtonsoft.Json 替换、DNS 切换、Bearer 令牌、请求签名、XML 序列化、二进制序列化、弹性重试、HTTP/2 与 HTTP/3、可观测性、熔断、短路响应、命名共享池、尝试管线、并发对冲与备用端点。
- **声明式 14 项**：GET 详情 / 列表、POST 创建、授权与 `[AllowAnonymous]`、错误处理、全局日志、签名验签、流式响应（SSE / JSON 数组）、multipart 上传、单帧上限、接口级声明、接口级授权与校验、接口级 multipart、接口级流式。

另有两个示例：

- [`samples/AxiosNet.Simple.DependencyInjection`](samples/AxiosNet.Simple.DependencyInjection)：Generic Host 六个 DI 场景。
- [`samples/AxiosNet.Aot`](samples/AxiosNet.Aot)：Native AOT + 裁剪，自带回环服务。

## 运行时与 AOT

类库单目标 `net6.0`，`net6.0` / `7.0` / `8.0` / `9.0` / `10.0` 及以上直接引用 `lib/net6.0`，无需配置。不支持 .NET Framework 与 .NET Core 3.1 及更早。

已声明 `IsTrimmable`，自身裁剪 0 警告。AOT 发布时消费方需要：

```xml
<ItemGroup>
  <TrimmerRootAssembly Include="MyApp.Models" />
</ItemGroup>
```

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

AOT 下不可用：`dynamic` 响应与运行期才确定的类型、`XmlContentSerializer`、匿名对象作为 params / 表单值、`ValidateModels(Assembly)`（改用 `ValidateModels(typeof(A), ...)`）、接口级声明 `Create<T>()`（`DispatchProxy` 依赖 Reflection.Emit，调用时抛 `PlatformNotSupportedException`；`ValidateInterfaces(Assembly)` 同受裁剪影响，改用 `ValidateInterfaces(typeof(IA), ...)`）。

## 构建与测试

```bash
dotnet build AxiosNet.sln
dotnet test AxiosNet.sln
dotnet build AxiosNet.sln -c Release
```

| 测试项目 | 用例数 | 目标框架 | 执行次数 |
| --- | ---: | --- | ---: |
| `AxiosNet.Tests`（核心 + 声明式） | 1448 | `net8.0` / `net6.0` | 2896 |
| `AxiosNet.DependencyInjection.Tests` | 59 | `net8.0` / `net6.0` | 118 |
| **合计** | **1507** | | **3014** |

测试用自研 `TcpListener` HTTP/1.1 Keep-Alive 服务器跑真实 TCP 往返，覆盖请求编码、拦截器与转换、错误码、超时取消、进度限速、连接池与协议协商、流式响应、重试、熔断、并发对冲、备用端点、短路响应、遥测、声明式绑定、接口级代理与 DI 注册。

行覆盖率 **92.7%**、分支覆盖率 **89.5%**（`AxiosNet.Tests` / net8.0，`--collect:"XPlat Code Coverage"`，排除性能与分配类用例；两次采集有 ±0.1 抖动）。
采集时必须排除两类用例——性能比值与分配护栏在插桩下都会失真（护栏按「每请求分配字节数」断言，插桩本身就在分配）：

```bash
dotnet test test/AxiosNet.Tests/AxiosNet.Tests.csproj -f net8.0 --collect:"XPlat Code Coverage" \
  --filter "FullyQualifiedName!~ConcurrencyPerformanceTests&FullyQualifiedName!~AllocationSweepTests&FullyQualifiedName!~AllocationTests&FullyQualifiedName!~HeadersCopyOnWriteTests"
```

普通 `dotnet test` 仍然全跑（含被排除的这几类）。

### 用例按功能分类

`test/AxiosNet.Tests` 的目录结构就是功能清单——找用例先看目录，再找文件名：

| 目录 | 覆盖范围 |
| --- | --- |
| `Client/` | 静态门面 `Axios`、实例选项与默认值、默认头、配置合并与九个子对象 |
| `Requests/` | URL 与 params、请求体（JSON / 表单 / multipart）、响应解析与缓冲、流式响应、超时取消、Content-Type 解析 |
| `Governance/` | 并发闸门、进度回调与限速 |
| `Resilience/` | 重试策略与计时、总预算（`Deadline`）、熔断状态机、短路响应 |
| `Serialization/` | `IContentSerializer` 契约与注册表、JSON 默认选项 |
| `Pipelines/` | 请求 / 响应拦截器、`transformRequest` / `transformResponse`、尝试管线（内置顺序、插入位置、重排校验）、并发对冲、备用端点 |
| `Connectivity/` | 连接池（含命名共享池）、Handler 注入、HTTP/2·3 协商与降级、重定向、本机 h3 能力探测 |
| `Diagnostics/` | Activity / Meter / DiagnosticListener 三路遥测、尝试审计事件 |
| `Errors/` | 错误模型、错误码、`ToJson` 快照 |
| `Regression/` | 分配护栏（独占集合 + 精确计数，进程级 / 线程级两种口径）、并发吞吐 |
| `WebAPI/` | 声明式模型绑定与校验、授权、流式、接口级代理与启动期校验 |
| `Infrastructure/` | 测试基建：HTTP 服务器、模型、遥测探针、独占集合 |

时序敏感用例（断言上限耗时、靠「占位 vs 预算」区分口径）与分配用例各自独占运行，见 `Infrastructure/TimingCollection.cs`、`Infrastructure/AllocationCollection.cs`。

`AxiosNet.DependencyInjection.Tests` 同规则分两组：`Registration/`（注册扩展与命名注册）、`Lifecycle/`（并发解析与持有者释放）。

### 性能基准

[`test/AxiosNet.Benchmarks`](test/AxiosNet.Benchmarks) 是独立 BenchmarkDotNet 项目（单目标 `net8.0`、`IsPackable=false`、不被 `src/` 引用），把「这次优化有没有效果」变成可读数字。它不参与 `dotnet test`，CI 也不跑（基准对机器状态敏感）。

两者分工：`AxiosNet.Tests/ConcurrencyPerformanceTests` 是**并发正确性**测试（5 轮取最好一轮判 85% 能力线，中位 ≥ 75% 当地板防整体退化），负责拦住塌方；量化改动看基准。判定不用中位：这类噪声只把单轮往下压，取最好一轮才是能力的估计，否则测的是机器而不是代码。

```bash
dotnet run -c Release --project test/AxiosNet.Benchmarks                       # 交互菜单
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*Merge*'  # 参数直传 BDN
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*' --isolated
```

默认**同进程**执行（不生成临时项目、不重建）；要写进提交信息或与历史基线对比时加 `--isolated`（临时项目 + 每用例独立进程），两种方式的绝对耗时不能混比（`Allocated` 两者都可信）。

| 组 | 热点 | 基线 |
| --- | --- | --- |
| `RequestPipelineBenchmarks` | 请求全链路（真实回环 socket） | `HttpClient_GetString` |
| `ConcurrentPipelineBenchmarks` | 8 条在飞时的每请求成本 | `HttpClient_Concurrent` |
| `ConfigMergeBenchmarks` | `MergeConfig` / 请求头合并 | `GetUri_NoMerge` |
| `ConfigPrimitiveBenchmarks` | `AxiosConfig.Clone` / `AxiosHeaders` 原语 | —（看绝对值） |
| `FormEncodingBenchmarks` | 表单展平 + 百分号编码 | `PostForm_FlatAscii` |
| `DynamicDeserializationBenchmarks` | `dynamic` 反序列化 | `Typed` |
| `StreamingFramingBenchmarks` | 流式分帧（NDJSON / SSE / JSON 数组） | `Ndjson` |

读法、前后对比流程与已知噪声见 [`test/AxiosNet.Benchmarks/README.md`](test/AxiosNet.Benchmarks/README.md)。

## 项目结构

```
.
├─ src/
│  ├─ AxiosNet/                       # 类库（单目标 net6.0）
│  │  ├─ Axios.cs                     # 静态门面
│  │  ├─ AxiosClient*.cs              # 实例 + 请求管线 + 快捷方法
│  │  ├─ AxiosConfig*.cs              # 配置（顶层 + 九个子对象）
│  │  ├─ AxiosHeaders.cs              # 请求头
│  │  ├─ AxiosResponse.cs / AxiosException.cs / AxiosShortCircuit.cs
│  │  ├─ Interceptors/                # 请求 / 响应拦截器
│  │  ├─ Pipeline/                    # 尝试中间件链：AxiosAttemptPipeline + 内置重试 / 备用端点 / 熔断 / 对冲 / 闸门 + 可选审计
│  │  ├─ Retry/                       # IAxiosRetryPolicy / RetryPolicy
│  │  ├─ Circuit/                     # IAxiosCircuitPolicy / CircuitBreaker
│  │  ├─ Serialization/               # IContentSerializer + JSON / XML 实现
│  │  ├─ Telemetry/                   # ActivitySource / Meter / DiagnosticListener
│  │  ├─ Pooling/                     # HttpConnectionPool + 池选项 / 统计 + Handler 注入
│  │  ├─ WebAPI/                      # 声明式 API 客户端（模型 / 接口）
│  │  └─ Internal/                    # URL、正文编码、响应读取、流量计量
│  └─ AxiosNet.DependencyInjection/   # DI 扩展（独立包）
├─ samples/                           # Simple（46 项）/ DI / Aot
└─ test/
   ├─ AxiosNet.Tests/                  # 核心 + 声明式（目录 = 功能分类，见「构建与测试」）
   ├─ AxiosNet.DependencyInjection.Tests/
   └─ AxiosNet.Benchmarks/
```

## License

[MIT](LICENSE)
