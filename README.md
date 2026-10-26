# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/nuget/v/AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![tests](https://img.shields.io/badge/tests-1124%20passed-success)](#构建与测试)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**简体中文** | [English](README.en.md)

axios 风格的 .NET HTTP 客户端：请求写法与 JS 的 [axios](https://github.com/axios/axios) 一一对应，另外自带按 origin 复用的连接池、弹性重试、熔断、流式响应、声明式 API 客户端和 OpenTelemetry 观测点。

- 单目标 `net6.0`：一份程序集跑在 .NET 6 / 7 / 8 / 9 / 10 及以上全部运行时
- 零第三方依赖，只用 BCL
- 全异步：`Task` + `CancellationToken`
- 三路观测点（trace / metrics / events）默认开启，没人订阅时零产出

> 仓库：[Gitee（itsm/axios-net）](https://gitee.com/itsm/axios-net) 是提交主仓，PR / tag 以 Gitee 为准；[GitHub](https://github.com/sunyuliang/AxiosNet) 为只读镜像。

---

## 目录

- [安装](#安装)
- [快速开始](#快速开始)
- [客户端](#客户端)
- [请求](#请求)
  - [方法与 URL](#方法与-url)
  - [查询参数 params](#查询参数-params)
  - [请求体 data 与 ContentType](#请求体-data-与-contenttype)
  - [表单与文件上传](#表单与文件上传)
  - [响应类型与下载](#响应类型与下载)
  - [流式响应](#流式响应)
  - [超时与取消](#超时与取消)
  - [进度、限速与大小上限](#进度限速与大小上限)
- [错误处理](#错误处理)
- [拦截器](#拦截器)
- [数据转换](#数据转换)
- [序列化](#序列化)
- [弹性重试](#弹性重试)
- [熔断](#熔断)
- [连接池](#连接池)
  - [HTTP 版本（默认 HTTP/2，可开 HTTP/3）](#http-版本默认-http2可开-http3)
  - [自定义 Handler](#自定义-handler)
- [可观测性](#可观测性)
- [声明式 API 客户端](#声明式-api-客户端)
- [依赖注入](#依赖注入)
- [配置项参考](#配置项参考)
- [与 axios 对照](#与-axios-对照)
- [示例项目](#示例项目)
- [运行时支持与 AOT](#运行时支持与-aot)
- [构建与测试](#构建与测试)
- [性能基准](#性能基准)
- [项目结构](#项目结构)
- [License](#license)

---

## 安装

```bash
dotnet add package AxiosNet                       # 核心客户端 + 声明式 API 客户端（同一包）
dotnet add package AxiosNet.DependencyInjection   # 可选：DI 单例注册扩展
```

引用本仓库源码时：

```bash
dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj
```

## 快速开始

从最简单的开始，逐步加强：

```csharp
using AxiosNet;

// 1) 字符串：拿到响应原文，不做任何解析
var text = await Axios.GetAsync<string>("https://api.example.com/ping");
Console.WriteLine(text.Data);          // "pong"

// 2) dynamic：不定义 DTO，像 JS 一样按字段取值
var dyn = await Axios.GetAsync<dynamic>("https://api.example.com/users/1");
Console.WriteLine($"{dyn.Data.name}, {dyn.Data.address.city}");   // dyn.Data["name"] 同样可用

// 3) 强类型：定义 DTO，Data 直接是 User
var response = await Axios.GetAsync<User>("https://api.example.com/users/1");
User user = response.Data!;
Console.WriteLine($"{response.Status} {user.Name}");
```

三种形态按「要不要写 DTO」选：

| 写法 | `Data` 的类型 | 适合 |
| --- | --- | --- |
| `GetAsync<string>` | `string`（原文，不解析） | 纯文本接口、调试、自己解析 |
| `GetAsync<dynamic>` | `AxiosDynamic`（成员 / 索引 / 缺省字段返回 `null`） | 字段不固定、快速验证 |
| `GetAsync<T>` | `T` | 生产代码 |
| `GetAsync()`（非泛型） | `JsonElement`（非法 JSON 回退原文，二进制给 `byte[]`） | 只想看结构、不建类型 |

拿到一个实例，带上 baseURL / 超时 / 默认头：

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

只拼 URL 不发请求（等价 `axios.getUri(config)`）：

```csharp
string url = http.GetUri(c =>
{
    c.Url = "users";
    c.Params = new { page = 2, ids = new[] { 1, 2 } };
});
// -> https://api.example.com/users?page=2&ids[]=1&ids[]=2
```

## 客户端

三种入口，能力完全一致：

| 入口 | 创建方式 | 适用场景 |
| --- | --- | --- |
| 静态门面 | `Axios.GetAsync(...)`、`Axios.Defaults`、`Axios.Interceptors` | 小工具、脚本、单站点应用 |
| 实例 | `Axios.Create(c => ...)` 或 `new AxiosClient(config)` | 常规用法：一个实例一套默认值 + 一条连接池 |
| 声明式 | `new ApiClient(baseAddress)` | 接口多、想用模型描述请求（见[声明式 API 客户端](#声明式-api-客户端)） |

**实例默认值 vs 单次请求**：`Axios.Create(c => ...)` 里配的是实例默认值；快捷方法最后一个 `configure` 参数配的是本次请求。`configure` 拿到的 `AxiosConfig` 是实例默认值的副本，所以 `c.Headers.Delete("X-Foo")` 能真的摘掉默认头。

```csharp
await http.GetAsync("slow", c => c.Timeout = TimeSpan.FromSeconds(30));   // 只影响这次
```

**只能实例级生效的选项**：`Connection.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `PoolOptions` 在构造连接池时就固化了；`Limits.MaxConcurrentRequests` 在首个受限请求时固化。单次请求上写**与实例生效值不同**的值会抛 `ERR_BAD_OPTION_VALUE`（复述实例的值则放行），避免「设了却没生效」。

实例用完即弃即可（`using`），连接池与 Handler 随之释放；在途请求不会被 `Dispose` 打断，只是此后不再接受新请求。

## 请求

### 方法与 URL

| 方法 | 说明 |
| --- | --- |
| `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` | 每个都有「非泛型 → `JsonElement`」与「泛型 → `T`」两种；都可带 `data`、`configure`、`CancellationToken` |
| `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` | 默认按 `multipart/form-data` 发送 |
| `RequestAsync(config)` / `RequestAsync<T>(config)` | 传完整 `AxiosConfig` |
| `GetUri(config)` / `GetUri(c => ...)` | 只拼 URL，不发请求 |
| `Interceptors` / `Defaults` / `Pool` / `UsesConnectionPool` | 实例成员 |

- `url` 可以是相对地址（拼 `BaseAddress`）或绝对地址；`null` / 空白串抛 `ERR_INVALID_URL`（不会被当成「请求基地址本身」，要请求根路径请显式传 `"/"`）。
- `Method` 直接用 BCL 的 `HttpMethod`，自定义谓词照发：`new HttpMethod("PROPFIND")`。
- 路径段里的特殊字符用 `AxiosUrl.EncodePathSegment(value)` 转义。
- 预签名 / 日志请以 `GetUri` 的返回值为准：它就是本次请求实际发出的 URL 形态（回放 `response.Config` 时同样按本实例重算）。

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

- 集合形态由 `Serialization.CollectionFormat` 决定：`Brackets`（默认，`a[]=1`）/ `Multi`（`a=1&a=2`）/ `Csv` / `Ssv` / `Tsv` / `Pipes`。
- 键与值用 `Uri.EscapeDataString` 编码（空格 `%20`、中文 UTF-8 百分号）；手写在 `Url` 里的查询串不会被再次编码，需要自己转义。
- `Params` 传标量（`int` / `bool` / `DateTime`…）抛 `ERR_BAD_OPTION_VALUE`（没有键名可表达）。
- 完全自定义查询串：`c.Serialization.ParamsSerializer = p => "a=1&b=2"`（收到整个 `Params` 对象，返回已编码串）。
- 标量文本形态（`Params` / `[Path]` / `[Header]` / 表单共用）：`bool` → `true`/`false`，枚举 → 名字，`DateTime` / `DateTimeOffset` → UTC 毫秒 ISO（`Kind = Unspecified` 按 UTC 解释），其它走 `IFormattable` + `InvariantCulture`。

### 请求体 data 与 ContentType

**编码格式只由 `ContentType` 决定，与 `data` 的运行时类型无关**（默认 `application/json`）。

| `ContentType` | 对象 / 字典 / dynamic / 集合 | 基础类型（数值 / 布尔 / 枚举 / Guid / 日期…） | `string` / `byte[]` / `Memory<byte>` / `Stream` / `HttpContent` |
| --- | --- | --- | --- |
| `application/json`（默认） | 当前序列化器输出 JSON | 原值文本（`42`、`true`、`Paid`） | 原样发送 |
| `application/x-www-form-urlencoded` | 键值对表单（`a[b]=v`、`a[]=v`、null 跳过） | 忽略（空正文） | 原样发送 |
| `multipart/form-data` | 字段 + 文件部件 | 抛 `ERR_BAD_OPTION_VALUE`（请放进对象字段） | 抛 `ERR_BAD_OPTION_VALUE`（`HttpContent` 原样透传） |
| 其他（`application/xml` / `yaml`…） | 交给当前序列化器 | 原值文本 | 原样发送 |

```csharp
// JSON（默认）
await http.PostAsync("users", new { name = "bob" });

// 表单
await http.PostAsync("login", new Dictionary<string, object?>
{
    ["username"] = "alice",
    ["tags"] = new[] { "a", "b" }        // -> tags[]=a&tags[]=b
}, c => c.ContentType = AxiosContentType.FormUrlEncoded);

// 二进制：记得显式声明类型，库不按类型猜
await http.PostAsync("bin", bytes, c => c.ContentType = AxiosContentType.OctetStream);

// 任意方法都能带 body
await http.GetAsync("reports/query", new { from = "2024-01-01" });
await http.DeleteAsync("users/1", new { reason = "duplicated" });
```

常用类型常量：`AxiosContentType.Json` / `FormUrlEncoded` / `MultipartFormData` / `OctetStream` / `Xml` / `JsonPatch` / `ProblemJson` / `TextPlain` / `TextHtml`；也可以直接写带参数的字符串：`"multipart/form-data; boundary=----axios-net"`。

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

- 字段名取属性名 / 字典键（识别 `[JsonPropertyName]` / `[JsonIgnore]`），嵌套写 `meta[author]`，集合展开成同名重复部件；字段名拼接规则可用 `Serialization.FormFieldNameSelector` 改。
- 根节点是文件数组时没有键名，用 `FormFile.FieldName` 指定。
- 部件内容随请求释放（成功 / 异常 / 取消都 `Dispose`）；需要复用请先读成 `byte[]`。
- 字段名 / 文件名里的 `"`、`\`、CR/LF 与控制字符会破坏 `Content-Disposition`，发出前直接报错。
- 在 ASP.NET Core 项目里 `FormFile` 有歧义，用别名：`using ClientFile = AxiosNet.FormFile;`。

### 响应类型与下载

```csharp
// 强类型
var typed = await http.GetAsync<User>("users/1");
User u = typed.Data!;

// 非泛型：Data 是 JsonElement（非法 JSON 回退原文，二进制给 byte[]）
var raw = await http.GetAsync("users/1");

// Text / ArrayBuffer / Stream / Dynamic
var text   = await http.GetAsync("ping", c => c.ResponseType = ResponseType.Text);
var bytes  = await http.GetAsync("file", c => c.ResponseType = ResponseType.ArrayBuffer);
var stream = await http.GetAsync("file", c => c.ResponseType = ResponseType.Stream);

// dynamic：无需 DTO
var dyn = await http.GetAsync<dynamic>("users/1");
Console.WriteLine($"{dyn.Data.name}, {dyn.Data.address.city}, {dyn.Data.roles[0]}");
```

`<Stream>` / `<byte[]>` / `<dynamic>` 会自动切到对应的响应类型，不用再手写 `ResponseType`。

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

// 服务端给的文件名
var disposition = ContentDispositionHeaderValue.Parse(response.GetHeader("Content-Disposition")!);
var fileName = (disposition.FileNameStar ?? disposition.FileName)?.Trim('"') ?? "download.bin";
```

- 流**必须释放**（`using` / `await using`）：流读完或释放后连接才回池；只读一半就释放，这条 TCP 连接会被丢弃。
- 下载期间该 origin 的租约保持占用，空闲驱逐不会打断长下载。
- 流式请求遇到非 2xx 时库会立即丢弃响应体并归还连接（`ex.Response.Data` 为 `null`，`Status` / `Headers` 仍可用）。

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
| `JsonArray` | 顶层数组的每个元素 `[{...},{...}]` | 批量 / 分页列表 |

```csharp
// 分页列表：元素收尾即交付，不必等整个数组下完
var pages = await client.RequestStreamAsync(new ListOrdersRequest(), AxiosStreamFormat.JsonArray);
await foreach (var order in pages.Data!)
{
    Console.WriteLine(order.Id);
}
```

- **超时**：`Timeout` 对流式请求只覆盖到响应头；正文由 `Limits.StreamIdleTimeout` 控制（`null` = 正文仍计入 `Timeout` 总预算；`Zero` = 不限时；大于零 = 空闲超时，只在实际读写期间计时）。
- **单帧上限**：默认 8 MB，合法 1–64 MB，用 `Limits.MaxStreamFrameLength` 调整，越界在发送前抛 `ERR_BAD_OPTION_VALUE`。没有「不限制」语义（整帧必须进内存）；峰值内存约为上限的 2–4 倍。
- **编码**：NDJSON / SSE 要求 ASCII 兼容 charset（UTF-8 / GBK 等），宽字符编码在取流时报 `ERR_BAD_RESPONSE`；`JsonArray` 必须是 UTF-8。UTF-8 BOM 会跳过。
- **所有权**：流只能枚举一次；`await foreach` 结束或 `break` 时自动释放，手动 `MoveNextAsync` 的调用方必须自己 `DisposeAsync`。
- **错误**：状态码错误在 `await` 抛出（`ErrorFactory` 同样接管）；帧解析失败 `ERR_BAD_RESPONSE`，中途断流 `ERR_NETWORK`，取消 `ERR_CANCELED`。

### 超时与取消

```csharp
// 单次请求超时
await client.GetAsync("slow", c => c.Timeout = TimeSpan.FromMilliseconds(500));

// 显式关掉实例默认超时
await client.GetAsync("slow-but-wanted", c => c.Timeout = TimeSpan.Zero);

// 外部取消（等价 AbortController）
using var cts = new CancellationTokenSource();
var task = client.GetAsync("slow", cancellationToken: cts.Token);
cts.CancelAfter(TimeSpan.FromSeconds(1));
```

- `Timeout` 是**全程总预算**（建连 + 等响应头 + 读正文），默认 `TimeSpan.Zero` 表示不超时；上限约 49.7 天，超出会收敛到上限而不是抛异常。
- 超时抛 `ECONNABORTED`（消息可用 `Callbacks.TimeoutMessage` 覆盖），取消抛 `ERR_CANCELED`，两者都把底层 `OperationCanceledException` 留在 `Cause`。

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

`AxiosProgressEvent`：`Loaded`（已传字节）/ `Total`（未知时为 `null`）/ `Progress`（0~1，封顶 100%，`Total` 未知时为 `null`）/ `Rate`（平均字节每秒）/ `Estimated`（预估剩余）/ `Upload`。

- 回调**同步**执行，耗时逻辑请自行转后台；报告节流 50 ms 一次。
- 回调抛异常会中断请求：上传阶段 `ERR_BAD_REQUEST`、下载阶段 `ERR_BAD_RESPONSE`，原始异常在 `Cause`。
- `MaxContentLength` 对普通响应边读边累计，越界立即中止；对 `ResponseType.Stream` 先按声明长度预检，再在读取时累计。
- `MaxBodyLength` 只对能算出长度的正文生效（JSON / 表单 / `byte[]` / 可 seek 的流）；multipart 不拦截。
- 负值一律 `ERR_BAD_OPTION_VALUE`；`MaxRate = 0` 等于不限速。

### 在途并发上限

`MaxRate` 限的是**字节速度**，拦不住「每秒几百个小请求」——那种压力来自同时在途的请求条数。并发闸门补的就是这一维：

```csharp
// 实例级：本实例最多 4 条请求同时在途，其余排队
var client = Axios.Create(c =>
{
    c.Limits.MaxConcurrentRequests = 4;
    c.Limits.QueueTimeout = TimeSpan.FromSeconds(2);   // 排队最多等 2 秒
});

// 按请求调排队耐心（只影响本次等待，不改变闸门本身）
await client.GetAsync("fast", c => c.Limits.QueueTimeout = TimeSpan.Zero);   // 满了立刻失败，不排队
```

| 项 | 默认 | 语义 |
| --- | --- | --- |
| `MaxConcurrentRequests` | `null`（不限） | 本实例同时最多几条请求在途；正整数 |
| `QueueTimeout` | `null`（一直等） | 等槽位的上限：`null` 一直等 / `Zero` 不排队 / `>0` 超时失败 |

- 排队超时抛 **`ERR_QUEUE_TIMEOUT`**（不是 `ECONNABORTED`）：请求根本没发出去，该做的是降速 / 扩容 / 退避，而不是排查网络或调大 `Timeout`。
- 排队**不计入** `Timeout`（`Timeout` 只覆盖真正发出去的那一段），排队时长由 `QueueTimeout` 单独兜底（重试的每次尝试各自计一次）；等待期间可随时用 `CancellationToken` 取消（`ERR_CANCELED`）。
- 槽位覆盖「真正在途」的整段：重试的每次尝试各自取槽（退避等待不占位），**流式响应一直占到正文流被释放**为止。
- 闸门按实例共享，`MaxConcurrentRequests` 属实例级：按请求写不同的值、或构造之后改 `Defaults` 都会抛 `ERR_BAD_OPTION_VALUE`（改上限请新建实例）。首次受限请求之前配好即可。
- **自锁风险**：在拦截器 / 进度回调里用**同一个实例**发子请求时，内外层抢同一批槽位，上限很小时会互等。这种写法请给 `QueueTimeout` 设上限，或让子请求走另一个实例。
- 默认重试策略不会重试 `ERR_QUEUE_TIMEOUT`（它不在「网络错误 / 超时 / 408 / 429 / 5xx」集合里）；要重试请用自定义策略，并确认重试间隔足够让槽位释放。
- 与连接池的 `MaxConnectionsPerServer` 分工不同：那个管**连接**，HTTP/2 下多路复用可以让一条连接跑很多请求；这里管**请求**。

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
    Console.WriteLine(ex.Message);   // Request failed with status code 404
    Console.WriteLine(ex.Cause);     // 底层异常（DNS / 连接失败等），可能为 null
    Console.WriteLine(ex.ToJson());  // 结构化快照，可直接进日志
}

if (Axios.IsAxiosError(ex) && !Axios.IsCancel(ex)) { /* ... */ }
```

| Code | 触发场景 |
| --- | --- |
| `ERR_BAD_REQUEST` | 4xx；请求体超 `Limits.MaxBodyLength`；请求体序列化失败；上传进度回调抛异常 |
| `ERR_BAD_RESPONSE` | 5xx；响应无法反序列化为目标类型；响应体超 `Limits.MaxContentLength`；泛型参数与 `responseType` 不匹配；下载进度回调 / `transformResponse` 抛异常 |
| `ECONNABORTED` | 超过 `Timeout`（或流式空闲超时） |
| `ERR_NETWORK` | 连接被拒 / DNS 失败 / 连接中断 / 正文截断 |
| `ERR_CANCELED` | `CancellationToken` 取消，或客户端已释放 |
| `ERR_QUEUE_TIMEOUT` | 并发槽位一直占满，超过 `Limits.QueueTimeout` 仍未排到（请求未发出） |
| `ERR_CIRCUIT_OPEN` | 熔断打开，请求**没有发出**（`RetryAfter` 给出建议等待） |
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

// 单次请求追加（实例链在前，本次在后）
await http.GetAsync("users/1", c => c.TransformResponse.Add((d, _) => Normalize(d)));
```

- 链是 `IList<Func<object?, AxiosConfig, object?>>`，返回 `null` 合法，会继续往后传。
- `TransformRequest` 的返回值仍按 `ContentType` 编码。
- `TransformResponse` 在 `Json` / `Text` / `ArrayBuffer` / `Dynamic` 下执行，`Stream` 不执行（数据还没读）。

## 序列化

请求体 / 响应体默认由 System.Text.Json 处理，可整体替换。

**JSON 设置三层覆盖**（后者优先）：

| 层级 | 入口 |
| --- | --- |
| 全局 | `AxiosJson.Options` |
| 实例 | `Axios.Create(c => c.Serialization.JsonSerializerOptions = ...)` |
| 单次请求 | `c => c.Serialization.JsonSerializerOptions = ...` |

默认是 Web 风格（输出 camelCase、输入大小写不敏感）。替换 `AxiosJson.Options` 请给新实例（STJ 会冻结用过的选项），建议启动期设一次。

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

**自定义格式**：实现 `IContentSerializer` 即可（文本格式两个方法；二进制格式再加一对字节通道 `SerializeToBytes` / `Deserialize(ReadOnlyMemory<byte>)`；可选 `MediaType` 自报媒体类型）。

```csharp
public sealed class YamlSerializer : IContentSerializer
{
    public string? MediaType => "application/yaml";

    public string Serialize(object? value, Type type, AxiosConfig config) => YamlConvert.Serialize(value, type);

    public object? Deserialize(string text, Type type, AxiosConfig config) => YamlConvert.Deserialize(text, type);
}

AxiosContent.Serializer = new YamlSerializer();
```

- 序列化器可写请求头（`config.Headers.Set(...)`），拿到的 `config` 是本次请求独占的副本，不会污染实例默认值。
- 表单 / multipart、基础类型、`string` / `byte[]` / `Stream` 不经过序列化器。
- 实现需线程安全（会被并发请求共享）；异常统一包装成 `ERR_BAD_REQUEST` / `ERR_BAD_RESPONSE`。
- 请求 / 响应文本编码：`Serialization.RequestEncoding` / `Serialization.ResponseEncoding`（默认 UTF-8 / 自动识别）。GBK 等代码页需先 `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`，否则静默乱码。

## 弹性重试

默认不重试，挂策略即可启用：

```csharp
using AxiosNet.Retry;

var client = Axios.Create(c => c.Retry.Policy = new RetryPolicy { MaxRetries = 3 });
// 或
config.Retry.Policy = RetryPolicy.Constant(3, TimeSpan.FromSeconds(1));                       // 固定间隔
config.Retry.Policy = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5)); // 指数退避

// 单次请求关掉实例默认策略（显式 null 与「没设置」不同）
await client.GetAsync("orders", c => c.Retry.Policy = null);

// 观察每次重试
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

其它成员：`Delay` / `BackoffFactor` / `MaxDelay` / `Jitter`（默认 `true`，等待落在 50%–100%）/ `HonorRetryAfter`（默认 `true`，服务端给了 `Retry-After` 就按它等，仍受 `MaxDelay` 封顶）/ `RetryWhen`（附加条件）。

语义：

- `Timeout` 是**每次尝试**的预算（3 次重试 × 10s 最坏 40s）；需要总预算用 `CancellationToken`。
- 拦截器、`transformRequest`、正文序列化都只跑一次；响应拦截器看到最终结果。
- 正文必须可重放：可 seek 的流每次重试前自动拨回起点；不可 seek 的流与 `StreamContent`（无论根级还是 multipart 部件）在开启重试时直接抛 `ERR_BAD_OPTION_VALUE`。
- 自定义策略：实现 `IAxiosRetryPolicy`（`MaxRetries` / `ShouldRetry(ctx)` / `GetDelay(ctx)`），`ctx` 提供 `Attempt` / `MaxRetries` / `Exception` / `Response` / `StatusCode` / `ErrorCode` / `Delay` / `Config` / `Method`。策略实例会被并发共享，需无状态。

## 熔断

重试解决「这一次失败了，要不要再试」；熔断解决「下游整体不可用时，还要不要试」。默认不熔断，挂策略即可启用：

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

// 探针 / 健康检查不该被熔断挡住：单次请求显式关掉
await client.GetAsync("health", c => c.Circuit.Policy = null);

// 运维手工恢复（清掉所有维度的账，下次请求重新计数）
breaker.Reset();
```

状态机：`Closed` →（连续失败达到 `FailureThreshold`）→ `Open` →（冷却 `BreakDuration`）→ `HalfOpen` →（探测成功）→ `Closed`；探测失败则**立刻**回到 `Open` 并重新计时（不必再攒够阈值）。

失败口径与重试门禁对齐（**取消 / 4xx 不计失败**）：

| 情况 | 计入失败？ |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED`（超时） | 计入 |
| 5xx | 计入 |
| 4xx（含 408 / 429） | 不计入（也不清零） |
| `ERR_CANCELED` | 不计入 |
| 配置 / URL / 正文序列化 / 反序列化错误 | 不计入 |

语义：

- **按 host 计数**：维度默认是 `host:port`（`uri.Authority`），同一 host 的不同端口算两个下游；`KeySelector` 可改成 `ctx => ctx.Uri.Host`（合并端口）或按路由前缀隔离租户。
- 被拒的请求**不发出**：不建连、不占连接池租约，直接抛 `ERR_CIRCUIT_OPEN`，`AxiosException.RetryAfter` 给出建议等待（冷却期剩余，策略说不准时为 `null`）。
- 与重试同时开：闸门在**每次尝试之前**，熔断打开后重试循环里的下一次尝试会被立刻拦下（不再白等一轮退避）；`ERR_CIRCUIT_OPEN` 本身不会被重试。
- 开着重试时**每次尝试各记一笔**：3 次重试 + 首次失败 = 4 笔 —— 一个请求连撞 4 次 5xx，本来就该判定下游不可用。
- 状态表有上限（`MaxTrackedHosts`，默认 512，软上限）：满员时只淘汰「丢了不改变判定」的维度（闭合态、冷却早已过期的打开态），**正在熔断 / 半开探测的条目不会被丢**——宁可短暂超出，也不让刚判定为不可用的下游被悄悄放行。
- 自定义策略：实现 `IAxiosCircuitPolicy`（`ShouldAllow(ctx)` / `GetRetryAfter(ctx)` / `Record(ctx)`）；`ctx` 提供 `Host` / `Uri` / `Config` / `Exception` / `StatusCode` / `ErrorCode` / `Method` / `IsSuccess` / `IsFailure`。策略**有状态**且被并发共享，需自己保证线程安全。

## 连接池

`AxiosClient` 默认自建一条按 `scheme://host:port` 隔离的连接池（`HttpConnectionPool`）：每个 origin 一个调优过的 `SocketsHttpHandler`，请求共享 TCP 长连接。

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
```

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| `MaxConnectionsPerServer` | 100 | 单站点最大 TCP 连接数，超出的请求在连接层排队 |
| `PooledConnectionLifetime` | 10 分钟 | 连接最长复用时长，到期后下一次取用换新（不打断在途请求） |
| `PooledConnectionIdleTimeout` | 2 分钟 | 空闲连接关闭 |
| `ConnectTimeout` | 15 秒 | 建连超时 |
| `IdleHandlerLifetime` | 5 分钟 | origin 节点空闲多久被回收 |
| `EvictionInterval` | 30 秒 | 后台清扫间隔；`<= 0` 不启动定时器 |
| `AllowAutoRedirect` / `MaxAutomaticRedirections` | `true` / 5 | 自动重定向；`MaxRedirects = 0` 关闭 |
| `AutomaticDecompression` | `true` | gzip / deflate / br |
| `EnableMultipleHttp2Connections` | `true` | h2 下允许按 `MaxConnectionsPerServer` 继续加连接 |
| `MaxEndpointCount` | 1024 | origin 节点上限，满了先驱逐最久未用的空闲节点 |
| `UseCookies` | `false` | 打开后整个池共用一份 `CookieContainer` |
| `Proxy` / `Credentials` | `null` | 不设代理时**不使用**系统 / 环境代理；要走系统代理请设 `HttpClient.DefaultProxy` |
| `ServerCertificateCustomValidationCallback` / `EnabledSslProtocols` / `ConfigureSslOptions` | `null` | 证书校验、TLS 版本、客户端证书等 |
| `HandlerFactory` | `null` | 每个 origin 创建 Handler 时调用，见[自定义 Handler](#自定义-handler) |

- `Dispose()` 是优雅关闭：空闲节点立即释放，有在途请求的节点等租约归还后回收。
- `Clear()` 立即释放全部节点，会打断在途请求。
- DNS 变更后的收敛窗口由 `PooledConnectionLifetime` 决定；需要服务发现时用 `HandlerFactory` + `ctx.CreateDefaultHandler()` 设 `ConnectCallback`。

### HTTP 版本（默认 HTTP/2，可开 HTTP/3）

默认 `HttpVersion = Version20` + `VersionPolicy = RequestVersionOrLower`：https 上协商出 h2 就用 h2，服务端只给 1.1 就在同一次 TLS 握手里降级；明文 `http://` 一律 1.1。

```csharp
// 默认即可，无需配置
var client = new AxiosClient();

// 退回 HTTP/1.1（老网关、NTLM 认证、必须发连接级头）
await client.GetAsync(url, c => c.Connection.HttpVersion = HttpVersion.Version11);

// 要求必须 h2，协商不上就报错
await client.GetAsync(url, c =>
{
    c.Connection.HttpVersion = HttpVersion.Version20;
    c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
});

// 明文 h2c：只有 Exact 会走 h2
await client.GetAsync("http://h2c.example.com/", c =>
{
    c.Connection.HttpVersion = HttpVersion.Version20;
    c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
});

// HTTP/3：环境或服务端不支持就静默降级
await client.GetAsync(url, c => c.Connection.HttpVersion = HttpVersion.Version30);
```

| 配置 | 实际发出 |
| --- | --- |
| 默认（`Version20` + `OrLower`） | https 协商 h2，否则 1.1；明文一律 1.1 |
| `Version20` + `Exact` | 只发 h2，协商不上报错（明文上等于 h2c 先验知识） |
| `Version11`（策略默认） | HTTP/1.1 |
| `Version30`（策略默认） | 有 QUIC 且服务端支持就用 h3，否则降级 |
| `Version30` + `Exact` | 只发 h3；环境上不可能（明文 / 无 QUIC）在发请求前报 `ERR_BAD_OPTION_VALUE`，服务端不支持报 `ERR_NETWORK` |

- 实际用到的版本看 `response.ProtocolVersion`（取自响应），请求意图看 `Config.Connection.HttpVersion`。
- 本机能不能跑 h3 查 `Http3Support.IsSupported` / `UnsupportedReason`（.NET 6 运行时默认关闭 h3，需要 runtimeconfig 的 `System.Net.SocketsHttpHandler.Http3Support=true` 或环境变量 `DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=1`，且系统有 MsQuic）。
- `HttpVersion` / `VersionPolicy` 是按请求项，实例默认与单次请求都能设，重试沿用同一套。
- `WithCredentials = true` 走的是 `CredentialCache.DefaultCredentials`，NTLM / Negotiate 这类连接级认证在 h2 上不可用——面向 IIS / 域内服务请显式 `Version11`。

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
- 注入 Handler 后 `client.Pool` 为 `null`、`UsesConnectionPool` 为 `false`；连接级选项（`MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials`）改由 Handler 决定，在 defaults 上设置会抛 `ERR_BAD_OPTION_VALUE`。
- 超时、进度、限速、大小上限等数据层能力不受影响。

## 可观测性

三路观测点默认开启，名字统一是 `AxiosNet`。

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
| `url.full` | 完整 URL（去掉 userinfo）；`Telemetry.CaptureUrl = false` 时不写 |
| `http.response.status_code` | 有响应时写入 |
| `network.protocol.version` | 协商后真正用到的版本（`1.0` / `1.1` / `2` / `3`） |
| `http.request.body.size` / `http.response.body.size` | 字节数（未知时不写） |
| `http.request.resend_count` | 已重试次数 |
| `error.type` | 状态码 → 库错误码 → 异常类型名 |
| `axiosnet.error.code` | 库错误码（如 `ERR_NETWORK`），与 `error.type` 并存 |
| `axiosnet.pool.endpoint.reused` | 本次是否命中池里已有的 origin 节点（`false` = 顺手新建，首个请求通常更慢） |

- 4xx / 5xx 即使被 `validateStatus` 接受也标 `Error`；失败另记一条 `exception` 事件。
- 每次「决定重试」加一条 `retry` 事件。
- 流式响应（`ResponseType.Stream`）的 span 在拿到响应头时结束，耗时等于首包时间。

**Metrics**（源名 `AxiosNet`）：

| 指标 | 类型 | 单位 | 标签 |
| --- | --- | --- | --- |
| `axiosnet.client.request.duration` | `Histogram<double>` | `s` | method / server.address / server.port / status_code / error.type |
| `axiosnet.client.active_requests` | `ObservableGauge<long>` | `{request}` | 无 |
| `axiosnet.client.request.retries` | `Counter<long>` | `{retry}` | method / server.address / server.port / error.type |
| `axiosnet.client.pool.leases` | `Counter<long>` | `{lease}` | server.address / server.port / `axiosnet.pool.endpoint.reused` |
| `axiosnet.client.pool.endpoints.created` | `Counter<long>` | `{endpoint}` | server.address / server.port |
| `axiosnet.client.pool.endpoints.evicted` | `Counter<long>` | `{endpoint}` | server.address / server.port / `axiosnet.pool.eviction.reason`（`idle` / `capacity` / `manual` / `clear` / `dispose`） |
| `axiosnet.client.pool.active_leases` | `ObservableGauge<long>` | `{lease}` | 无 |

- 指标只用低基数标签（方法 / 主机 / 端口 / 状态码 / 错误类型）；URL 路径只进 span。
- `request.duration` 是逻辑请求耗时（含重试与退避），起止点与 span 相同。
- `active_requests` 在请求结束时减（流式响应拿到响应头就减）；`active_leases` 要等正文流释放，所以「长下载占了多少并发额度」看后者。
- 池域指标描述的是池这个资源，不受 `Telemetry.Enabled` 影响；节点复用率请由后端用 `leases` 的 `reused` 两个序列相除（它衡量的是节点命中，不是 TCP 连接复用）。

**Events**（`DiagnosticListener` 名 `AxiosNet.DiagnosticListener`）：`AxiosNet.RequestStart` / `RequestStop` / `RequestFailed` / `Retry`，载荷是公开类型（`AxiosRequestStartEvent` / `StopEvent` / `FailedEvent` / `AxiosRetryContext`）。

**关闭与裁剪**：两项都按请求生效。

```csharp
await client.GetAsync<object>("health", configure: c => c.Telemetry.Enabled = false);   // 完全不产出
await client.GetAsync<object>("api",    configure: c => c.Telemetry.CaptureUrl = false); // 不写 url.full
```

## 声明式 API 客户端

`AxiosNet.WebAPI` 命名空间（与核心同包）：定义请求模型 + 配好授权，调用只剩一句 `client.RequestAsync(request)`。

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
Console.WriteLine($"{response.Status} {response.Data!.Name}");

string url = client.GetUri(new GetUserRequest { Id = 5 });   // 只拼 URL
```

| 绑定 | 特性 | 说明 |
| --- | --- | --- |
| 路径段 | `[Path]` | 替换 `{name}`，值 URL 转义，`null` 报错 |
| 查询参数 | `[Query]` / 无标注属性 | 默认绑定 Query；数组 `a[]=v`、嵌套 `a[b]=v` |
| 字典查询 | `[Query]` + 字典 | 未命名时平铺 `?a=1&b=2`，显式命名保留 `?filters[a]=1` |
| 请求头 | `[Header]` / `[HeaderCollection]` | `null` 跳过 |
| 静态头 | `[Headers]`（类级） | `"Name: Value"`；只有名字表示删除该头 |
| 表单 | `[Multipart]` + `[Part]` | 模型自身即表单；`[Part]` 覆盖字段名 / 文件名 / 部件类型 |
| 请求体 | `[Body]` | 每个模型最多一个；`null` 不发请求体 |
| 排除 | `[ApiIgnore]` | 完全不绑定 |

- **单负载模型可省 `[Body]`**：POST / PUT / PATCH 且模型只有一个未标注的**非标量**属性时，它默认按 Body 绑定（字典属性除外，始终按查询串）。
- 一个属性可同时标多个绑定特性（`[Path]` + `[Header]`），值只读一次、分别应用。
- 按请求微调：继承 `ApiRequest<T>` 覆写 `Configure(config)`，或 `RequestAsync(request, config => ...)`。
- 模型定义非法（缺路由、占位符未绑定、多个 Body…）在首次调用时抛 `InvalidOperationException`；启动期体检用 `ApiClient.ValidateModels(typeof(A), ...)` 或 `ValidateModels(assembly)`（返回全部错误消息，不抛）。
- 授权：`[Authorize]` / `[Authorize("Basic")]` 声明需要授权，`[AllowAnonymous]` 跳过整条链路；`IAuthorizationProvider` 是扩展点，内置 `BearerAuthorizationProvider`；`AccessTokenProvider` 只负责取令牌。
- 强类型错误：`client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()` 后状态码错误抛 `ApiError<TError>`（网络 / 超时 / 取消不经过工厂）。
- 其它：`client.UrlParameterFormatter` 接管 `[Path]` / `[Query]` 的文本形态；`client.Axios` 直接进底层 `AxiosClient`（拦截器、连接池、Handler 注入全可用）；`client.StreamIdleTimeout` 控制流式正文空闲超时。

## 依赖注入

```csharp
using AxiosNet.DependencyInjection;
using AxiosNet.WebAPI.DependencyInjection;

// 只要核心客户端
builder.Services.AddAxiosClient(config =>
{
    config.BaseAddress = "https://api.example.com";
    config.Timeout = TimeSpan.FromSeconds(10);
});

// 声明式客户端，自建底层客户端
builder.Services.AddApiClient("https://api.example.com",
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.Authorization = new BearerAuthorizationProvider(token));

// 声明式客户端做门面，复用上面注册的 AxiosClient 单例
builder.Services.AddApiClient(configure: client => client.Authorization = new BearerAuthorizationProvider(token));

// 多站点：每站点一次命名注册
builder.Services.AddApiClient("a", "https://a.example.com", configure: c => c.Authorization = providerA);
builder.Services.AddApiClient("b", "https://b.example.com", configure: c => c.Authorization = providerB);
```

| 扩展方法 | 注册内容 |
| --- | --- |
| `AddAxiosClient(configure)` | 单例 `AxiosClient` |
| `AddApiClient(baseAddress, configureDefaults, configure)` | 单例 `ApiClient`（自建底层客户端） |
| `AddApiClient(configure)` | 单例 `ApiClient`（复用容器里的 `AxiosClient`） |
| `AddApiClient(name, baseAddress, ...)` | 单例 `IApiClientResolver` + 命名站点 |

- TryAdd 语义：已注册则不重复注册，不会建出第二条池。
- 容器里的 `IAuthorizationProvider` / `IAccessTokenProvider` / `IApiErrorFactory` 会自动挂到 `ApiClient`，`configure` 里显式设置的优先。
- 生命周期归容器：`using` 掉注入的 `ApiClient` 不会释放共享的底层客户端。
- 多站点用 `IApiClientResolver.Get(name)` / `TryGet` / `Names`；.NET 8+ 也可用官方键名服务（`AddKeyedSingleton` + `[FromKeyedServices]`）。

## 配置项参考

`AxiosConfig` 的顶层项（请求本身）：

| 成员 | 类型 | 默认 | 说明 |
| --- | --- | --- | --- |
| `BaseAddress` | `string?` | `null` | 等价 axios `baseURL` |
| `Url` | `string?` | `null` | 相对或绝对地址 |
| `Method` | `HttpMethod` | `Get` | BCL 类型，支持自定义谓词 |
| `Headers` | `AxiosHeaders` | 空 | 大小写不敏感，写入即校验 |
| `RemoveHeaders` | `ICollection<string>` | 空 | 要摘掉的实例默认头 |
| `Params` | `object?` | `null` | 对象 / 字典 / 集合 / 已编码串 |
| `ContentType` | `string` | `application/json` | 决定 `Data` 的编码方式 |
| `Data` | `object?` | `null` | 请求体 |
| `Timeout` | `TimeSpan` | `Zero`（不超时） | 全程总预算 |
| `ResponseType` | `ResponseType` | `Json` | `Json` / `Text` / `ArrayBuffer` / `Stream` / `Dynamic` |
| `CancellationToken` | `CancellationToken` | `default` | 外部取消 |
| `TransformRequest` / `TransformResponse` | `IList<Func<object?, AxiosConfig, object?>>` | 空 | 转换链 |
| `Response` | `AxiosResponseInfo?` | — | 响应阶段可读（状态码 / 头 / 媒体类型），供序列化器与 `transformResponse` 使用 |

六个子对象：

**`Serialization`**

| 成员 | 默认 | 说明 |
| --- | --- | --- |
| `JsonSerializerOptions` | Web 风格 | 本次请求的 STJ 选项 |
| `ContentSerializer` | `null`（内置 JSON） | 内容序列化器，任意格式 |
| `RequestEncoding` / `ResponseEncoding` | `null` | 请求体文本编码 / 强制响应解码编码 |
| `ParamsSerializer` | `null` | `Func<object?, string>` 自定义查询串 |
| `FormFieldNameSelector` | `prefix[name]` | 表单字段名拼接 |
| `CollectionFormat` | `Brackets` | 集合形态：`Brackets` / `Multi` / `Csv` / `Ssv` / `Tsv` / `Pipes` |

**`Connection`**

| 成员 | 默认 | 说明 |
| --- | --- | --- |
| `Auth` | `null` | HTTP Basic（`new AxiosAuth(user, pass)`） |
| `MaxRedirects` | 5 | `0` = 不跟随（仅实例级） |
| `WithCredentials` | `false` | 系统凭据 / Cookie（仅实例级） |
| `Proxy` / `Credentials` / `PoolOptions` | `null` | 仅实例级 |
| `HttpVersion` | `null`（= 2.0） | 按请求项 |
| `VersionPolicy` | `null`（= `OrLower`） | 按请求项 |

**`Limits`**

| 成员 | 默认 | 说明 |
| --- | --- | --- |
| `MaxContentLength` | `null`（不限） | 响应体上限，字节 |
| `MaxBodyLength` | `null`（不限） | 请求体上限，字节 |
| `MaxRate` | `null`（不限） | 单向限速，字节/秒 |
| `MaxConcurrentRequests` | `null`（不限） | 在途请求条数上限（仅实例级，首个受限请求时固化） |
| `QueueTimeout` | `null`（一直等） | 等并发槽位的上限：`null` / `Zero`（不排队） / `>0`（超时报 `ERR_QUEUE_TIMEOUT`） |
| `StreamIdleTimeout` | `null` | 流式正文：`null` 计入 `Timeout` / `Zero` 不限 / `>0` 空闲超时 |
| `MaxStreamFrameLength` | `null`（= 8 MB） | 流式单帧上限，1–64 MB；常量见 `DefaultStreamFrameLength` / `MinStreamFrameLength` / `MaxStreamFrameLengthLimit` |

**`Callbacks`**

| 成员 | 说明 |
| --- | --- |
| `ValidateStatus` | `Func<int, bool>`，默认 2xx |
| `TimeoutMessage` | 自定义超时错误消息 |
| `OnUploadProgress` / `OnDownloadProgress` | `Action<AxiosProgressEvent>` |

**`Retry`**

| 成员 | 默认 | 说明 |
| --- | --- | --- |
| `Policy` | `null`（不重试） | `IAxiosRetryPolicy`；按请求显式赋 `null` 可关掉实例默认策略 |
| `OnRetry` | `null` | `Action<AxiosRetryContext>` |

**`Circuit`**

| 成员 | 默认 | 说明 |
| --- | --- | --- |
| `Policy` | `null`（不熔断） | `IAxiosCircuitPolicy`；按请求显式赋 `null` 可绕过实例默认熔断 |
| `OnRejected` | `null` | `Action<AxiosCircuitContext>`，请求被熔断拒绝（没发出）时触发；`ctx.RetryAfter` 是建议等待 |

**`Telemetry`**

| 成员 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `null`（= 开启） | `false` 时本次请求不产出任何遥测 |
| `CaptureUrl` | `null`（= 记录） | `false` 时不写 `url.full` |

> 子对象首次访问时才创建，不碰它们的配置不会多分配。

## 与 axios 对照

| axios (JS) | AxiosNet (.NET) |
| --- | --- |
| `axios(config)` | `client.RequestAsync(new AxiosConfig { ... })` |
| `axios(url, config)` | `client.GetAsync(url, c => { ... })` |
| `axios.get/post/put/patch/delete/head/options` | `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` |
| `axios.create(config)` | `Axios.Create(config)` / `Axios.Create(config, sharedPool)` |
| `axios.getUri(config)` | `client.GetUri(config)` / `client.GetUri(c => ...)` |
| `axios.postForm / putForm / patchForm` | `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` |
| `axios.defaults` | `Axios.Defaults` / `client.Defaults` |
| `res.data / status / statusText / headers / config` | `Data` / `Status` / `StatusText` / `Headers` / `Config`（另有 `ProtocolVersion` / `Request`） |
| `interceptors.request.use` / `response.use` / `eject` | `Interceptors.Request.Use` / `Interceptors.Response.Use` / `Eject` |
| `config.params` | `c.Params` |
| `config.data` | `c.Data`（编码由 `c.ContentType` 决定） |
| `responseType: 'json' / 'text' / 'arraybuffer' / 'stream'` | `ResponseType.Json` / `Text` / `ArrayBuffer` / `Stream` |
| `validateStatus` | `c.Callbacks.ValidateStatus` |
| `timeout` / `AbortController` | `c.Timeout` / `c.CancellationToken` |
| `auth: { username, password }` | `c.Connection.Auth = new AxiosAuth(user, pass)` |
| `withCredentials` / `proxy` / `maxRedirects` | `Connection` 子对象上的同名属性（仅实例级） |
| `maxContentLength` / `maxBodyLength` / `maxRate` | `Limits` 子对象上的同名属性 |
| `onUploadProgress` / `onDownloadProgress` / `timeoutMessage` | `Callbacks` 子对象上的同名属性 |
| `paramsSerializer` / `formSerializer` | `Serialization.ParamsSerializer` / `Serialization.FormFieldNameSelector` |
| `transformRequest` / `transformResponse` | 同名属性 |
| `AxiosError.code` | `AxiosException.Code`（另有 `Status` / `Cause` / `ToJson()`） |
| `axios.isAxiosError` / `isCancel` | `Axios.IsAxiosError` / `Axios.IsCancel` |
| `axios-retry` 插件 | `c.Retry.Policy` / `c.Retry.OnRetry` |
| —（axios 无内置，需接 opossum 之类） | `c.Circuit.Policy` / `c.Circuit.OnRejected` |
| —（axios 需自己接 OTel JS SDK） | 内置 `ActivitySource` / `Meter` / `DiagnosticListener` |
| —（无对应） | `HttpConnectionPool`、声明式 `ApiClient`、`RequestStreamAsync`、`Limits.MaxConcurrentRequests` / `QueueTimeout`（在途并发闸门 + `ERR_QUEUE_TIMEOUT`） |

与 axios 的有意差异：

1. 方法名带 `Async` 后缀；`timeout` 用 `TimeSpan`；取消用 `CancellationToken`。
2. 请求体编码由 `ContentType` 决定（默认 JSON），不按 data 类型推断——字典不会自动变表单。
3. 非泛型文本响应的 `Data` 是 `JsonElement`（非法 JSON 回退原文），二进制是 `byte[]`；要 JS 式访问用 `GetAsync<dynamic>`。
4. 实例级选项按请求写**不同的值**会立即抛 `ERR_BAD_OPTION_VALUE`（axios 是静默不生效）。
5. `params` 用 `Uri.EscapeDataString` 严格编码（空格 `%20`，不是 `+`）；要 axios 原始形态请自定义 `ParamsSerializer`。
6. `transformRequest` / `transformResponse` 的元素类型是 `object?`（C# 无法像 JS 那样丢失类型再收窄）。
7. 请求头写入即校验（RFC 7230 token、禁止 CR/LF），非法直接抛 `ArgumentException`。
8. `MaxRate` / 进度回调由库在 `HttpContent` 与 `Stream` 层实现（BCL 无原生支持）。
9. `Timeout` 上限约 49.7 天，超出收敛到上限；真正的无限超时用 `Timeout = Zero`。

## 示例项目

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) 是离线可运行的控制台示例，内置 `TcpListener` 演示服务器，共 38 项：

```bash
dotnet run --project samples/AxiosNet.Simple
```

启动后是交互菜单（反射列出全部示例，输入编号运行，`a` 跑全部，`q` 退出）。核心 28 项：静态门面、实例创建、JSON / 表单 / multipart、params、Basic Auth、dynamic 请求与响应、text / arraybuffer、拦截器、错误与取消、连接池、文件上传下载、getUri 与大小限制、进度与限速、自定义 Handler、单文件上传、Newtonsoft.Json 替换、DNS 切换、Bearer 令牌、请求签名、XML 序列化、二进制序列化、弹性重试、HTTP/2 与 HTTP/3、可观测性、熔断。

声明式 10 项（菜单 29–38）：GET 详情 / 列表、POST 创建、授权与 `[AllowAnonymous]`、错误处理、全局日志、签名验签、流式响应（SSE / JSON 数组）、multipart 上传、单帧上限。

另有两个示例：

- [`samples/AxiosNet.Simple.DependencyInjection`](samples/AxiosNet.Simple.DependencyInjection)：Generic Host 六个 DI 场景。
- [`samples/AxiosNet.Aot`](samples/AxiosNet.Aot)：Native AOT + 裁剪，自带回环服务。

## 运行时支持与 AOT

类库单目标 `net6.0`，`net6.0` / `net7.0` / `net8.0` / `net9.0` / `net10.0` 及以上直接引用 `lib/net6.0` 资产，无需配置。不支持 .NET Framework 与 .NET Core 3.1 及更早。

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

AOT 下不可用：`dynamic` 响应与运行期才确定的类型、`XmlContentSerializer`、匿名对象作为 params / 表单值、`ValidateModels(Assembly)`（改用 `ValidateModels(typeof(A), ...)`）。

## 构建与测试

```bash
dotnet build AxiosNet.sln
dotnet test AxiosNet.sln
dotnet build AxiosNet.sln -c Release
```

| 测试项目 | 用例数 | 目标框架 | 执行次数 |
| --- | ---: | --- | ---: |
| `AxiosNet.Tests`（核心 + 声明式） | 1077 | `net8.0` / `net6.0` | 2154 |
| `AxiosNet.DependencyInjection.Tests` | 47 | `net8.0` / `net6.0` | 94 |
| **合计** | **1124** | | **2248** |

测试用自研的 `TcpListener` HTTP/1.1 Keep-Alive 服务器跑真实 TCP 往返，覆盖请求编码、拦截器与转换、错误码、超时取消、进度限速、连接池与协议协商、流式响应、重试、熔断、遥测、声明式绑定与 DI 注册。

解决方案里还有一个不参与测试的 `test/AxiosNet.Benchmarks`（性能基准，单目标 `net8.0`）：
`dotnet build AxiosNet.sln` 会顺带构建它，需要还原一次 BenchmarkDotNet；`dotnet test` 不碰它，
CI 也不跑它（基准对机器状态敏感，共享 runner 上的数字没有意义）。

## 性能基准

[`test/AxiosNet.Benchmarks`](test/AxiosNet.Benchmarks) 是独立的 BenchmarkDotNet 项目，
用来把「这次优化有没有效果、有没有退化」变成可读的数字。它单目标 `net8.0`、`IsPackable=false`、
不被 `src/` 引用，因此主库「单目标 `net6.0` + 零第三方运行时依赖」的约定不受影响。

两者分工要说清楚：`AxiosNet.Tests/ConcurrencyPerformanceTests` 是**并发正确性测试** ——
门槛 85%、3 轮 × 1 秒、判定取中位，跨进程摆动 ±15%，它负责拦住「塌方」，读不出 3% 的差异；
量化改动看基准，守底线看那条用例。

```bash
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --list flat
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*Merge*'
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*'
```

| 组 | 热点 | 基线 |
| --- | --- | --- |
| `RequestPipelineBenchmarks` | 请求全链路（真实回环 socket） | `HttpClient_GetString` |
| `ConcurrentPipelineBenchmarks` | 8 条在飞时的每请求成本 | `HttpClient_Concurrent` |
| `ConfigMergeBenchmarks` | `MergeConfig` / 请求头合并 | `GetUri_NoMerge` |
| `ConfigPrimitiveBenchmarks` | `AxiosConfig.Clone` / `AxiosHeaders` 原语 | —（看绝对值） |
| `FormEncodingBenchmarks` | 表单展平 + 百分号编码 | `PostForm_FlatAscii` |
| `DynamicDeserializationBenchmarks` | `dynamic` 反序列化 | `Typed` |
| `StreamingFramingBenchmarks` | 流式分帧（NDJSON / SSE / JSON 数组） | `Ndjson` |

`Ratio` 是同一次运行内相对基线的比值（比绝对 ns 可移植，但仍带本机特性），`Allocated` 是每操作分配量（跨运行稳定）；
读法、前后对比流程与已知噪声见 [`test/AxiosNet.Benchmarks/README.md`](test/AxiosNet.Benchmarks/README.md)。

## 项目结构

```
.
├─ src/
│  ├─ AxiosNet/                       # 类库（单目标 net6.0）
│  │  ├─ Axios.cs                     # 静态门面
│  │  ├─ AxiosClient*.cs              # 实例 + 请求管线 + 快捷方法
│  │  ├─ AxiosConfig*.cs              # 配置（顶层 + 七个分组）
│  │  ├─ AxiosHeaders.cs              # 请求头
│  │  ├─ AxiosResponse.cs / AxiosException.cs
│  │  ├─ Interceptors/                # 请求 / 响应拦截器
│  │  ├─ Retry/                       # IAxiosRetryPolicy / RetryPolicy
│  │  ├─ Circuit/                     # IAxiosCircuitPolicy / CircuitBreaker
│  │  ├─ Serialization/               # IContentSerializer + JSON / XML 实现
│  │  ├─ Telemetry/                   # ActivitySource / Meter / DiagnosticListener
│  │  ├─ Pooling/                     # HttpConnectionPool + 池选项 / 统计 + Handler 注入
│  │  ├─ WebAPI/                      # 声明式 API 客户端
│  │  └─ Internal/                    # URL、正文编码、响应读取、流量计量
│  └─ AxiosNet.DependencyInjection/   # DI 扩展（独立包）
├─ samples/                           # Simple（38 项）/ DI / Aot
└─ test/                              # AxiosNet.Tests / DependencyInjection.Tests / Benchmarks
```

## License

[MIT](LICENSE)
