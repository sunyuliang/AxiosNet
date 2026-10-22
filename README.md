# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/nuget/v/AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![language](https://img.shields.io/badge/C%23-12-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![tests](https://img.shields.io/badge/tests-576%20passed-success)](#构建与测试)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

**简体中文** | [English](README.en.md)

用 .NET (C#) 实现的 **axios 风格 HTTP 客户端**，API 设计与 JS 生态的 [axios](https://github.com/axios/axios) 一一对应；并在 .NET 原生 `SocketsHttpHandler` 连接复用之上，额外提供一层**按 origin 隔离的网络连接池**（连接复用、并发上限、空闲驱逐、多实例共享、可观测统计）。适合替代手写的 `HttpClient` 封装，覆盖 API 调用、文件上传下载、表单提交等网络请求场景。

- 零第三方依赖，仅使用 .NET BCL
- 运行时支持：**.NET 6 及以上**（单目标 `net6.0`，详见[目标框架与运行时支持](#目标框架与运行时支持)）
- 异步优先（`Task` / `CancellationToken` / `ConfigureAwait(false)`）

> 仓库：**Gitee（[itsm/axios-net](https://gitee.com/itsm/axios-net)）是提交主仓**，Issue / PR / tag 以 Gitee 为准；[GitHub](https://github.com/sunyuliang/AxiosNet) 为只读镜像（CI 自动发布 NuGet 也由镜像侧执行）。

---

## 目录

- [安装](#安装)
- [快速开始](#快速开始)
- [配置项](#配置项)
- [请求体 data 与查询参数 params](#请求体-data-与查询参数-params)
- [URL 编码：哪些位置会自动编码](#url-编码哪些位置会自动编码)
- [对接 ASP.NET Core Web API：单文件上传](#对接-aspnet-core-web-api单文件上传)
- [响应类型 responseType（含 dynamic）](#响应类型-responsetype含-dynamic)
- [下载文件](#下载文件)
- [表单快捷方法 postForm / putForm / patchForm](#表单快捷方法-postform--putform--patchform)
- [进度回调、限速与大小限制](#进度回调限速与大小限制)
- [自定义序列化与数据转换](#自定义序列化与数据转换)
- [拦截器](#拦截器)
- [错误处理](#错误处理)
- [超时与取消](#超时与取消)
- [网络连接池](#网络连接池)
- [自定义 Handler 注入](#自定义-handler-注入)
- [特性](#特性)
- [axios ↔ AxiosNet 对照表](#axios--axiosnet-对照表)
- [与 axios 的差异说明](#与-axios-的差异说明)
- [示例项目](#示例项目)
- [目标框架与运行时支持](#目标框架与运行时支持)
- [构建与测试](#构建与测试)
- [整体流程](#整体流程)
- [项目结构](#项目结构)
- [License](#license)

---

## 安装

### 方式一：NuGet（推荐）

```bash
dotnet add package AxiosNet
```

> 版本号与 Git tag 一致：打 `v1.2.3` 即发布 `1.2.3`；带后缀的 tag（如 `v1.2.3-rc.1`）会作为预发布版本发布。

### 方式二：引用项目（当前仓库）

```bash
dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj
```

### 方式三：源码引入

直接把 `src/AxiosNet` 目录复制进你的解决方案：

```bash
dotnet sln YourApp.sln add src/AxiosNet/AxiosNet.csproj
```

> 解决方案文件 `AxiosNet.slnx` 为新的 XML 格式，需要 .NET SDK 8.0.400+ 或 Visual Studio 2022 17.13+；旧版本可自行执行 `dotnet new sln` 重新生成经典 `.sln` 并添加两个项目。

## 快速开始

```csharp
using AxiosNet;

// 等同 axios.get('/users/1').then(res => res.data)
var response = await Axios.GetAsync<User>("https://api.example.com/users/1");

User user = response.Data!;
Console.WriteLine($"{response.Status} {user.Name}");

// 等同 axios.create({ baseURL, timeout, headers })
var http = Axios.Create(c =>
{
    c.BaseAddress = "https://api.example.com/";
    c.Timeout = TimeSpan.FromSeconds(10);
    c.Headers.Set("Authorization", "Bearer xxx");
});

await http.PostAsync("users", new CreateUserRequest("bob", 30));

// 等同 axios.getUri(config)：只拼 URL，不发请求
string url = http.GetUri(c =>
{
    c.Url = "users";
    c.Params = new { page = 2, ids = new[] { 1, 2 } };
});
// -> https://api.example.com/users?page=2&ids[]=1&ids[]=2
```

> `GetUri` 返回的就是**实际发送形态**：查询串里的百分号编码非保留字符（`%41`→`A`、`%7E`→`~`）会被 `System.Uri` 按 RFC 3986 归一化，与请求行上线内容一致；做预签名时请以 `GetUri` 的返回值作为签名输入。

## 配置项

```csharp
var config = new AxiosConfig
{
    BaseAddress = "https://api.example.com/", // 等同 baseURL
    Url = "users",                            // 相对或绝对地址
    Method = HttpMethod.Post,                 // 直接用 BCL HttpMethod，默认 Get
    Headers = new AxiosHeaders(),             // 大小写不敏感
    Params = new { page = 1 },                // -> ?page=1
    Data = new { name = "bob" },              // 请求体（按 ContentType 编码）
    ContentType = "application/json",         // 默认；可设为 application/x-www-form-urlencoded 等
    Timeout = TimeSpan.FromSeconds(10),       // 0/Zero = 不超时
    Auth = new AxiosAuth("user", "pass"),     // Basic
    ResponseType = ResponseType.Json,         // Json/Text/ArrayBuffer/Stream/Dynamic
    ValidateStatus = s => s is >= 200 and < 300,
    CancellationToken = cancellationToken,
    MaxRedirects = 5,
    WithCredentials = false,
    RequestEncoding = null,                   // 请求体文本编码，null = UTF-8
    ResponseEncoding = null,                  // 响应体文本编码，null = 自动识别

    // —— 大小、限速与进度 ——
    MaxContentLength = 10 * 1024 * 1024,      // 响应体上限（字节），null = 不限
    MaxBodyLength = 50 * 1024 * 1024,         // 请求体上限（字节），null = 不限
    MaxRate = 2 * 1024 * 1024,                // 单向限速（字节/秒），null = 不限
    TimeoutMessage = "上游响应超时",           // 自定义超时错误消息
    OnUploadProgress = e => Console.WriteLine($"↑ {e}"),
    OnDownloadProgress = e => Console.WriteLine($"↓ {e}"),

    // —— 序列化与转换 ——
    JsonSerializerOptions = myJsonOptions,    // 本次/本实例的 System.Text.Json 选项
    ContentSerializer = mySerializer,         // 内容序列化器，任意格式（null = 全局默认 JSON）
    ParamsSerializer = p => MyQuery(p),       // Func<object?, string>：自定义 ?a=1&b=2
    FormFieldNameSelector = (prefix, name) => string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}",
    // TransformRequest / TransformResponse：IList<Func<object?, AxiosConfig, object?>>

    // Proxy / Credentials / HttpVersion / JsonSerializerOptions / PoolOptions ...
};
```

> 进度与限速的小节见 [进度回调、限速与大小限制](#进度回调限速与大小限制)。

默认 JSON 选项为 **Web 风格**（输出 camelCase、输入大小写不敏感），与 JS/axios 生态习惯一致；优先级是「单次请求 → 实例 defaults → 全局 `AxiosJson.Options`」，自定义序列化器的接入方式见 [自定义 JSON 序列化](#自定义-json-序列化)。

> `withCredentials` / `proxy` / `credentials` / `maxRedirects` 属于**实例级**配置：连接池 Handler 在构造 `AxiosClient` 时创建，因此这些项只在实例默认值上生效。为避免"设了却没生效"的静默错误，在**单次请求**上写**与实例生效值不同**的值会立即抛出 `AxiosException`（`ERR_BAD_OPTION_VALUE`），提示改为在 `Axios.Create(...)` 的实例 defaults 中设置。判定依据是"值是否与生效值不同"而不是"是否写过"：把 defaults 里的值原样抄到单次请求上属于等价写法，不会报错（`maxRedirects` 的生效值取自池的实际策略——`AllowAutoRedirect = false` 时为 `0`，`poolOptions.MaxAutomaticRedirections` 为多少就是多少）。`proxy` / `credentials` / `poolOptions` 按引用与 defaults 比较，传入不同实例即视为不同值。
>
> 共享连接池的 `AxiosClient(pool: ...)` 同理：这四项与 `PoolOptions` 作用于**池**而非单个实例，在共享池 client 的 defaults 上设置同样立即抛 `ERR_BAD_OPTION_VALUE`，应改为在创建池的 `ConnectionPoolOptions` 上配置（详见[网络连接池](#网络连接池)）。
>
> 需要按请求生效的项（`timeout`、`headers`、`params`、`responseType`、`maxContentLength`、`maxRate`、`onUploadProgress`/`onDownloadProgress`、`transformRequest`/`transformResponse`、`validateStatus`、`auth`、`cancellationToken`、`RequestEncoding`/`ResponseEncoding` 等）不受此限制；其中 `cancellationToken` 既可设在实例默认，也可按请求传入，按请求传入时以后者为准。

## 请求体 data 与查询参数 params

请求体的编码格式**只由 `ContentType` 决定，与 `data` 的运行时类型无关**：字典不会被自动当成表单，对象也不会因为实现了某个接口而改变编码方式。

基础数据类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…）**直接取其文本发送、不经过序列化器**（裸标量没有「格式」可言）；字典与对象走同一套逻辑 —— 都不展平、不进标量分支。

| `ContentType` | 结构化数据（对象 / 字典 / dynamic / 集合） | 基础数据类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…） | `string` / `byte[]` / `Memory<byte>` / `ArraySegment<byte>` / `Stream` / `HttpContent` |
| --- | --- | --- | --- |
| `application/json`（默认） | 当前序列化器输出 JSON 文本 | 原值文本（`42`、`true`、`Paid`），**不经过序列化器** | 原样发送 |
| `application/x-www-form-urlencoded` | 键值对表单（嵌套 `a[b]=v`、数组 `a[]=v`、null 跳过） | 根标量没有键名，忽略（空正文） | 原样发送（视为已编码好的表单串） |
| `multipart/form-data` | 字段 + 文件部件（`FormFile` / `Stream` / `byte[]` / 内存类型 / `HttpContent`，数组展开为同名重复部件） | 根标量没有字段名，抛错（请放进对象/字典字段） | 抛 `ERR_BAD_OPTION_VALUE`（头部与正文矛盾）；预序列化的 multipart 正文请用 `HttpContent`，或 Content-Type 自带 `boundary` 参数 |
| 其他（`application/xml`、`application/yaml` …） | 交给当前序列化器（换 XML 序列化器就产出 XML） | 原值文本，**不经过序列化器** | 原样发送 |

常用值可以直接用 `AxiosContentType` 的预置常量（与 `HttpMethod.Get` 同一取舍：内置常用 + 允许任意自定义，底层仍是 `string`）：

```csharp
c.ContentType = AxiosContentType.Json;                 // application/json（默认）
c.ContentType = AxiosContentType.FormUrlEncoded;       // application/x-www-form-urlencoded
c.ContentType = AxiosContentType.MultipartFormData;    // multipart/form-data
c.ContentType = AxiosContentType.OctetStream;          // application/octet-stream
c.ContentType = AxiosContentType.ProblemJson;          // application/problem+json

c.ContentType = AxiosContentType.MultipartFormData + "; boundary=----axios-net";  // 带参数
c.ContentType = "application/vnd.api+json";                                       // 私有类型照旧
```

> `Content-Type` 是「媒体类型 + 参数」的复合值，`charset` 与 `boundary` 恰恰是库要支持的自定义点（`c.ContentType = "multipart/form-data; boundary=----xxx"`），因此它是 `string` 而不是 `enum`——枚举表达不了参数部分，也会把「读配置字符串」这条路堵死。`AxiosConfig.JsonContentType` / `FormUrlEncodedContentType` 保留不变（值相等）。

```csharp
// 1) 默认 application/json：对象、字典、dynamic、集合一律 JSON
await http.PostAsync("users", new { name = "bob", age = 30 });                            // {"name":"bob","age":30}
await http.PostAsync("users", new Dictionary<string, object?> { ["name"] = "bob" });       // 同一份 JSON
await http.PostAsync("users", expandoPayload);                                             // 同一份 JSON

// 2) 需要表单编码时显式声明（实例 defaults 与按请求设置均可）
await http.PostAsync("login", new Dictionary<string, object?>
{
    ["username"] = "alice",
    ["tags"] = new[] { "a", "b" }        // -> tags[]=a&tags[]=b
}, c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

// 3) multipart/form-data：字段 + 一堆文件
//    3.1 交给库构建：对象/字典里放 FormFile / Stream / byte[] / HttpContent
await http.PostAsync("upload", new
{
    title = "季度报告",
    files = new[]
    {
        new FormFile("a.pdf", File.OpenRead("a.pdf"), "application/pdf"), // 流随请求释放
        FormFile.FromPath("b.zip"),                                      // 库负责打开/释放文件流
        FormFile.FromBytes("c.txt", bytes, "text/plain")
    }
}, c => c.ContentType = "multipart/form-data");

//    3.2 自己拼 MultipartFormDataContent（需要逐部件控制时）
using var multipart = new MultipartFormDataContent();
multipart.Add(new StringContent("alice"), "name");
multipart.Add(new ByteArrayContent(bytes), "file", "a.bin");
await http.PostAsync("upload-up", multipart);                    // 原样透传，保留自带头部与边界

// 4) 二进制直发
await http.PostAsync("bin", bytes, c => c.ContentType = "application/octet-stream");

// 5) 每个方法都能携带任意类型的 body（GET / DELETE / HEAD / OPTIONS 同样支持）
await http.GetAsync("reports/query", new { from = "2024-01-01", to = "2024-02-01" });
await http.DeleteAsync("users/1", new { reason = "duplicated" });

// 6) params：数组/嵌套对象（与 axios 默认 paramsSerializer 对齐）
await http.GetAsync("search", c =>
    c.Params = new
    {
        q = "a b",                       // q=a%20b
        ids = new[] { 1, 2 },            // ids[]=1&ids[]=2
        filter = new { kind = "book" }   // filter[kind]=book
    });

// 6.1) params 也可以直接给「已序列化好的查询串」，或给根集合（用下标作键）
await http.GetAsync("search", c => c.Params = "a=1&b=2");   // ?a=1&b=2
await http.GetAsync("search", c => c.Params = new[] { 1, 2 }); // ?[0]=1&[1]=2
```

> 默认媒体类型是 JSON，因此发送二进制载荷时请显式设置 `c.ContentType = "application/octet-stream"`（库不会根据 `byte[]`/`Stream` 猜测类型）。如果整个应用都固定用某种二进制格式（Protobuf / MessagePack…），更省事的做法是注入实现了 `SerializeToBytes` + `MediaType` 的序列化器（见[自定义序列化](#自定义序列化与数据转换)）——对象直接传，头与正文自动对齐。

multipart 补充说明：

- **字段名**：取所在对象的属性名或字典的键（遵循 `JsonSerializerOptions.PropertyNamingPolicy`，并识别 `[JsonPropertyName]` / `[JsonIgnore]`）；嵌套对象写成 `meta[author]`；数组/集合展开为**同名重复部件**（ASP.NET Core `IFormFileCollection`、FastAPI、PHP 都能直接绑定）。
- **根节点是文件数组**时没有键名，用 `FormFile.FieldName` 指定部件名：`new[] { new FormFile(...) { FieldName = "files" } }`。
- **文件名**：`FormFile.FileName` 决定 `filename`；直接用 `Stream`/`byte[]`/`Memory<byte>` 也会作为文件部件发送（零拷贝，文件名回退为字段名）。
- **自带 `HttpContent` 部件**：部件已带 `Content-Disposition` 时原样保留（含 `filename`），只补缺失的 `name`——否则服务端无法绑定该字段。
- **边界**：默认自动生成；`c.ContentType = "multipart/form-data; boundary=----xxx"` 可指定固定边界。
- **生命周期**：部件内容随请求释放（成功/异常/取消都会 `Dispose`），`File.OpenRead(...)` 的流不要在请求之外复用；需要复用时先读成 `byte[]`。

### URL 编码：哪些位置会自动编码

只有「结构化输入」会被自动编码；手写在 `Url` 里的文本按原样参与拼接，不会再被语义编码。

| 位置 | 自动编码 | 说明 |
| --- | --- | --- |
| `c.Params` 的键与值（对象 / 字典 / 集合） | ✅ | `Uri.EscapeDataString`：空格 → `%20`、`&` → `%26`、`=` → `%3D`、`+` → `%2B`、`%` → `%25`、中文 → UTF-8 百分号编码；键里的 `[]` 保留（`a[b]`） |
| `c.Params` 传已序列化的查询串（`string`） | ❌ | 原样透传（只去掉前导 `?` / `&`）；值里的 `&`、`+` 会被服务端当结构字符，需要调用方自己编码 |
| `c.Url` 里手写的查询串 | ❌ | 只有 `System.Uri` 的归一化：空格 → `%20`、中文 → UTF-8、裸 `%` → `%25`、非保留字符还原（`%41` → `A`） |
| `c.Url` 里的路径段 | ❌ | 值里的 `/` 会直接拆段、`#` 会变成 fragment；用 `AxiosUrl.EncodePathSegment(...)` 转义 |
| `application/x-www-form-urlencoded` 的键与值 | ✅ | 空格 → `+`（与 `FormUrlEncodedContent` 一致）、`+` → `%2B`、`&` → `%26`，键里的 `[]` 保留 |
| `multipart/form-data` 的字段名 / 文件名 / 正文 | 不需要 | 靠 boundary 分段定位，名字与正文按 UTF-8 原样传输；非 ASCII 文件名由传输层生成 RFC 5987 `filename*=utf-8''...`，非 ASCII 字段名生成 RFC 2047 encoded-word。字段名/文件名里的 `"`、`\`、CR/LF 与控制字符会破坏 `Content-Disposition`，**在发出前直接报错**（`ERR_BAD_OPTION_VALUE` / `ArgumentException`） |
| 请求头 | ❌ | 不做任何编码；非 ASCII 头值被传输层拒绝（`ERR_NETWORK`，`Cause` 为 `Request headers must contain only ASCII characters`），需要自己转 base64 等 |

```csharp
// 路径参数：值里可能有 / ? # 或空格
var id = "a/b c";
await http.GetAsync("items/" + AxiosUrl.EncodePathSegment(id));    // items/a%2Fb%20c

// 手写查询串：值必须自己编码（更好的做法是交给 Params，由库自动编码）
await http.GetAsync("search?q=" + Uri.EscapeDataString("a&b"));     // search?q=a%26b

// 已编码过的串别再塞进 Params 对象：会二次编码（%20 → %2520）
```

> 预签名时以 `GetUri` 的返回值为准：`Uri` 会把非保留字符的百分号编码还原（`%41`→`A`、`%7E`→`~`），该返回值与请求行完全一致。

### 对接 ASP.NET Core Web API：单文件上传

「单文件上传」在 HTTP 层有两种形态，客户端与服务端必须对上：

| 形态 | Content-Type | 客户端 | 服务端 |
| --- | --- | --- | --- |
| **裸流直传**（整个请求体就是文件） | 文件类型，如 `application/octet-stream` | `PostAsync(url, stream, c => c.ContentType = ...)` | 读 `HttpRequest.Body` 流式落盘 |
| **multipart 单文件**（文件 + 其它字段） | `multipart/form-data` | `PostFormAsync(url, new { file = FormFile.FromPath(...) })` | `IFormFile file` |

#### 1) 裸流直传（大文件首选）

```csharp
// 服务端（Minimal API）：请求体直接就是文件内容，边收边落盘，不占内存
app.MapPost("/api/files/raw", async (HttpRequest request) =>
{
    var name = Path.GetFileName(request.Headers["X-File-Name"].ToString());
    var path = Path.Combine(uploadRoot, string.IsNullOrEmpty(name) ? $"{Guid.NewGuid():N}.bin" : name);

    await using (var target = File.Create(path))
    {
        await request.Body.CopyToAsync(target);
    }

    return Results.Ok(new { bytes = new FileInfo(path).Length, contentType = request.ContentType });
});

// 客户端：data 直接给 Stream（或 byte[]），Content-Type 声明为文件类型
await using var stream = File.OpenRead(@"D:\data\report.bin");
var res = await http.PostAsync<UploadResult>("api/files/raw", stream, c =>
{
    c.ContentType = "application/octet-stream";
    c.Headers.Set("X-File-Name", "report.bin");   // 裸流没有文件名，用自定义头/query 带过去
});

// 小文件也可以直接给 byte[]
await http.PostAsync("api/files/raw", bytes, c => c.ContentType = "image/png");
```

> **别让 `ContentType` 留默认值**：`Stream`/`byte[]` 若不显式设置，会按默认的 `application/json` 发送（字节仍原样，但网关、服务端判断与日志都会错）。

#### 2) multipart 单文件（需要一起提交其它字段时）

```csharp
// 服务端：IFormFile 的参数名 = 表单字段名
app.MapPost("/api/files/form", async (IFormFile file, string? note) =>
{
    var path = Path.Combine(uploadRoot, Path.GetFileName(file.FileName));
    await using (var target = File.Create(path))
    {
        await file.CopyToAsync(target);
    }

    return Results.Ok(new { file.FileName, bytes = new FileInfo(path).Length, note });
}).DisableAntiforgery();   // .NET 8 Minimal API 绑定 IFormFile 需显式关闭防伪校验

// 客户端：PostFormAsync 默认就是 multipart，字段名与参数名一致即可
await http.PostFormAsync("api/files/form", new
{
    file = FormFile.FromPath(@"D:\data\report.bin"),   // 字段名 file
    note = "季度报告"
});
```

MVC Controller 等价写法：

```csharp
[HttpPost("upload")]
[RequestSizeLimit(50 * 1024 * 1024)]
public async Task<IActionResult> Upload(IFormFile file, [FromForm] string? note) { ... }
```

> 在 ASP.NET Core 项目里 `FormFile` 会出现二义性（`AxiosNet.FormFile` vs `Microsoft.AspNetCore.Http.FormFile`），用别名区分：`using ClientFile = AxiosNet.FormFile;`。

#### 3) 大文件三件套

```csharp
await http.PostAsync("api/files/raw", File.OpenRead(path), c =>
{
    c.ContentType = "application/octet-stream";
    c.MaxBodyLength = 200 * 1024 * 1024;   // 客户端上限：长度可算时在发送前就拦截
    c.MaxRate = 8 * 1024 * 1024;           // 限速 8 MB/s（可选）
    c.OnUploadProgress = e => Console.WriteLine($"{e.Progress:P0} {e.Rate / 1024:F0} KB/s");
});
```

- 服务端 Kestrel 默认请求体上限 **30 MB**，超出直接 413：`builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 50 * 1024 * 1024)`，或端点级 `.DisableRequestSizeLimit()`。
- 服务端用 `request.Body.CopyToAsync(fileStream)` 边收边写；**避免** `[FromBody] byte[]`（整包进内存）。
- `FileStream` 可 Seek，`Content-Length` 确定，因此进度回调的 `Total` 与 `MaxBodyLength` 都准确。

可运行示例：`samples/AxiosNet.Simple` 的第 18 项（`dotnet run --project samples/AxiosNet.Simple`）—— `DemoServer` 提供 `/upload/raw` 与 `/upload/single`，按字节精确解析并回报文件名/字节数/SHA256，客户端逐个校验。

## 响应类型 responseType（含 dynamic）

```csharp
// 1) 强类型（最常用）
var typed = await http.GetAsync<User>("users/1");
User u = typed.Data!;

// 2) 非泛型：Data 为 JsonElement（非法 JSON 回退原文、二进制响应给 byte[]，对齐 axios transformResponse）
var raw = await http.GetAsync("users/1");
JsonElement element = (JsonElement)raw.Data!;

// 3) Text / ArrayBuffer / Stream
var text = await http.GetAsync("ping", c => c.ResponseType = ResponseType.Text);
var bytes = await http.GetAsync("file", c => c.ResponseType = ResponseType.ArrayBuffer);
var stream = await http.GetAsync("file", c => c.ResponseType = ResponseType.Stream);

// 4) dynamic：直接使用泛型 <dynamic>，无需 DTO，像 JS 一样访问（ExpandoObject 动态图）
var dyn = await http.GetAsync<dynamic>("users/1");
dynamic data = dyn.Data!;
Console.WriteLine($"{data.name}, {data.address.city}, {data.roles[0]}");
```

### 下载文件

按文件大小选形态：

```csharp
// 1) 大文件：流式落盘，内存占用恒定（推荐）
var response = await http.GetAsync<Stream>("files/report.zip");   // <Stream> 自动切到 ResponseType.Stream
await using (var source = response.Data!)
await using (var target = File.Create(@"D:\downloads\report.zip"))
{
    await source.CopyToAsync(target, cancellationToken);
}

// 2) 小文件：一次性拿 byte[]
var bytes = (await http.GetAsync<byte[]>("files/icon.png")).Data!;
await File.WriteAllBytesAsync(@"D:\downloads\icon.png", bytes);

// 3) 用服务端给的文件名（Content-Disposition: attachment; filename=...）
var disposition = ContentDispositionHeaderValue.Parse(response.GetHeader("Content-Disposition")!);
var fileName = (disposition.FileNameStar ?? disposition.FileName)?.Trim('"') ?? "download.bin";
```

要点：

- `ResponseType.Stream`（或 `GetAsync<Stream>`）走 `HttpCompletionOption.ResponseHeadersRead`：响应头一到就返回，正文边读边给，不会把整包读进内存。
- **必须释放流**（`using` / `await using`）：流读完或释放后连接才回到连接池。只读一部分就释放，该 TCP 连接会被丢弃而不是复用。
- 下载超时/取消与普通请求一致：`c.Timeout` 覆盖到**正文读取结束**（超时抛 `ECONNABORTED`，外部取消抛 `ERR_CANCELED`）；并发下载量大时注意连接池 `MaxConnectionsPerServer`。
- 流式请求遇到非 2xx 时，库会**立即丢弃响应体并归还连接**（`ex.Response.Data` 为 `null`，`Status` / `Headers` 仍可用）——否则一个没人读的错误响应流会长期占住连接。
- 下载期间该 origin 的租约保持占用（`GetStatistics()` 的 `ActiveLeases` 会反映正在下载的数量），因此空闲驱逐不会中断长下载；流释放后租约才归还。
- 下载进度用 `c.OnDownloadProgress`，下载限速用 `c.MaxRate`，大小上限用 `c.MaxContentLength`（详见下一节）。
- 需要把响应流直接存盘的场景，`OnDownloadProgress` 的报告节流为 50 ms，不会因为回调过频拖慢拷贝。
- `ResponseType.Stream` 时响应体不做任何解码，`response.Status/Headers` 照常可用（先校验状态码再落盘）。

所有快捷方法都支持 `<dynamic>`：`GetAsync<dynamic>` / `PostAsync<dynamic>` / `PutAsync<dynamic>` / `PatchAsync<dynamic>` / `DeleteAsync<dynamic>`（含 `RequestAsync<dynamic>` 与静态门面 `Axios.GetAsync<dynamic>`）；也可以在非泛型请求上显式设置 `c.ResponseType = ResponseType.Dynamic`。显式指定 `ResponseType.Text/ArrayBuffer/Stream` 时优先于 `<dynamic>` 的默认动态展开。

`<Stream>` / `<byte[]>` 也会自动匹配响应形态：`GetAsync<Stream>` 等价于 `ResponseType.Stream`，`GetAsync<byte[]>` 等价于 `ResponseType.ArrayBuffer`，无需再手写 `c.ResponseType = ...`。

### 原始内容优先与基础数据类型

响应统一**先读取原始字节，再解码为文本，最后按返回类型转换**，因此可以直接返回基础数据类型：

```csharp
var s = (await http.GetAsync<string>("ping")).Data;        // 纯文本原样返回（body 即使是 JSON 字符串也保留引号）
var n = (await http.GetAsync<int>("count")).Data;          // 裸数字 "42" -> 42
var d = (await http.GetAsync<DateTime>("today")).Data;     // 裸日期 "2024-01-02" -> DateTime
var raw = (await http.GetAsync("file", c => c.ResponseType = ResponseType.ArrayBuffer)).Data; // byte[]
```

- `GetAsync<string>` / `ResponseType.Text`：返回原始文本，不做 JSON 解析；
- 数值 / 日期 / 布尔 / GUID / 枚举 / `TimeSpan` 等：body 是 JSON 标量时直接反序列化，是裸标量文本时按类型解析；
- 复杂类型：按 JSON 反序列化；
- 非泛型：文本响应下合法 JSON 为 `JsonElement`、非法 JSON 原样返回文本（对齐 axios `transformResponse`）；二进制响应（`Content-Type` 非文本）直接给 `byte[]`；
- 二进制响应的业务对象（`GetAsync<T>`）：字节直达序列化器的字节入口，不做文本解码。

### 内容编码

- **请求体**：`RequestEncoding` 决定写入的字节（默认 UTF-8），显式设置时同时写入 `Content-Type` 的 `charset`；`byte[]` / `Stream` / `HttpContent` 原样透传，不受该项影响。
- **响应体**：默认自动识别 —— `Content-Type` 的 `charset` → BOM → UTF-8；`ResponseEncoding` 可强制指定，用于服务端 charset 缺失或错误的情况。

```csharp
// 请求按 GBK 发送
await http.PostAsync("legacy", "中文", c => c.RequestEncoding = Encoding.GetEncoding("gbk"));

// 响应强制按 GBK 解码
await http.GetAsync("legacy", c => c.ResponseEncoding = Encoding.GetEncoding("gbk"));
```

> **GBK / GB2312 / Big5 等代码页编码必须先注册提供程序**（`utf-8` / `utf-16` / `ascii` / `iso-8859-1` 之外都算）。
> .NET 6+ 内置编码表里没有它们，未注册时 `Encoding.GetEncoding("gbk")` 会抛 `ArgumentException`。
>
> ```csharp
> // 启动期执行一次；需要 NuGet 包 System.Text.Encoding.CodePages（库本身零依赖，不替你引）
> Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
>
> // 之后两种用法都有效
> await http.GetAsync("legacy", c => c.ResponseEncoding = Encoding.GetEncoding("gbk"));  // 强制按 GBK 解码
> // 或什么都不配：服务端 Content-Type: text/html; charset=gbk 时自动识别就能解对
> ```
>
> ⚠️ **没注册时是静默乱码**：`charset=gbk` 会被当成未知编码，回落到 BOM/UTF-8 解码，
> 中文变成 `����` 且**不抛任何异常**。如果对端是国内老系统，先确认这一行加过。

JSON 到 CLR 的映射规则：

| JSON | .NET 类型 |
| --- | --- |
| object | `ExpandoObject`（成员访问 / 转 `IDictionary<string,object?>` 索引） |
| array | `List<object?>`（支持动态下标） |
| string | `string` |
| true/false | `bool` |
| 整数 | `long` |
| 小数 | `double` |
| null | `null` |

`dynamic` 也可作为**请求体/参数**（`ExpandoObject`）：

```csharp
dynamic payload = new ExpandoObject();
payload.name = "charlie";
payload.age = 25;
await http.PostAsync<dynamic>("users", payload); // 序列化为 {"name":"charlie","age":25}

dynamic query = new ExpandoObject();
query.page = 3;
query.filter = new ExpandoObject();
query.filter.kind = "music";                      // ?page=3&filter[kind]=music
await http.GetAsync<dynamic>("search", c => c.Params = query);
```

> 注意：C# 语言的动态绑定器不支持 `expando["key"]` 字符串索引语法；需要键索引时请显式转换：
> `((IDictionary<string, object?>)dyn.Data!)["key"]`。

## 表单快捷方法 postForm / putForm / patchForm

`PostFormAsync` / `PutFormAsync` / `PatchFormAsync` 对应 axios v1.5+ 的 `axios.postForm(...)`：**未显式设置 `ContentType` 时默认按 `multipart/form-data` 发送**，对象里的 `FormFile` / `Stream` / `byte[]` 自动展开成文件部件。

```csharp
// 一行发 multipart：字段 + 文件（无需手写 ContentType）
await http.PostFormAsync("upload", new
{
    title = "季度报告",
    files = new[] { FormFile.FromPath("a.pdf") }
});

// 与 axios 一致：想发 urlencoded 就自己指定 ContentType
await http.PostFormAsync("login", new { username = "alice", password = "***" },
    c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

// 泛型与静态门面同样支持
var res = await http.PostFormAsync<UploadResult>("upload", payload);
var res2 = await Axios.PostFormAsync<UploadResult>("https://api.example.com/upload", payload);
```

> 三者与 `PostAsync(..., c => c.ContentType = "multipart/form-data")` 完全等价，只是把「默认 multipart」这层约定收进方法名，省掉每次手写 `ContentType`。

## 进度回调、限速与大小限制

三项能力既可放在实例 defaults，也可按请求设置。

### 进度回调

```csharp
await http.PostAsync("upload", new { files }, c =>
{
    c.OnUploadProgress = e =>
        Console.WriteLine($"↑ {e.Loaded}/{e.Total} ({e.Progress:P0}) {e.Rate / 1024:F1} KB/s 剩余 {e.Estimated}");
    c.OnDownloadProgress = e =>
        Console.WriteLine($"↓ {e.Loaded}/{e.Total} ({e.Progress:P0}) {e.Rate / 1024:F1} KB/s");
});
```

`AxiosProgressEvent` 成员：

| 成员 | 含义 |
| --- | --- |
| `Loaded` | 已传输字节 |
| `Total` | 总字节；服务端未给 `Content-Length`（分块响应）时为 `null` |
| `Progress` | 0~1 的小数；`Total` 未知时为 `null` |
| `Rate` | 累计平均速率（字节/秒） |
| `Estimated` | 按当前速率估算的剩余时间；已传完或速率未知时为 `null` |
| `Upload` | `true` 表示上传事件 |
| `ToString()` | 已格式化，如 `"download 1024/2048 bytes (50.0%)"`，打日志可直接用 |

- 回调是**同步**执行的：耗时逻辑请自行转到后台，否则会直接拖慢传输。
- 报告节流 50 ms 一次；结束时（`loaded == total`）保证有且仅有一次 100% 事件，不会重复上报。
- 下载进度在 `ResponseType.Stream` 下同样生效（数据边读边计数），不影响流式落盘的低内存占用。
- 契约：同一响应的响应流须**单线程顺序读取**（进度计量与限速都不是线程安全的）。

### 限速 maxRate

```csharp
// 每个请求限制到 1 MB/s（字节/秒），上传和下载各自受限
await http.GetAsync("big", c => c.MaxRate = 1024 * 1024);
```

实现按已传字节数反算"应耗时"，超前就 `Delay` 补差，限的是**平均速率**（允许小段突发），不是严格令牌桶。

### 大小限制

```csharp
// 响应体超过 1 MB 直接失败
try
{
    await http.GetAsync("big", c => c.MaxContentLength = 1024 * 1024);
}
catch (AxiosException ex)
{
    Console.WriteLine(ex.Code);    // ERR_BAD_RESPONSE
    Console.WriteLine(ex.Message); // maxContentLength size of 1048576 exceeded (got 8388608 bytes)
}

// 请求体超过 5 MB 在发送前就拒绝
await http.PostAsync("upload", bytes, c => c.MaxBodyLength = 5 * 1024 * 1024);
```

- `MaxContentLength` 对普通（缓冲）响应是**边读边累计**：越界立即中止并释放连接，不会先把 8 MB 全读进内存再判断；对 `ResponseType.Stream` 则是"声明长度先预检 + 读取时边读边计数"两层：声明超限时连响应头都不读正文就失败，chunked / 未声明长度的响应也会在累计越界的那一刻让 `Read`/`ReadAsync` 抛出 `ERR_BAD_RESPONSE`。
- `MaxBodyLength` 在发送前按 `Content.Headers.ContentLength` 判断，只对能算出长度的请求体生效（JSON / 表单 / `byte[]` / `FormFile`）；自定义的未知长度 `HttpContent` 不做拦截。
- 这四个限制项的**负值会抛 `ERR_BAD_OPTION_VALUE`**（此前的实现把负值静默当作"不限制"，调用方会以为限制生效了）。`MaxRate = 0` 等价于不限速，`Timeout = TimeSpan.Zero` 表示不超时。

## 自定义序列化与数据转换

### 内容序列化（默认 JSON，任意格式可注入）

请求体 / 响应体的编解码由 `IContentSerializer` 决定，默认实现是 System.Text.Json。接口是 **2 + 2** 个方法：文本格式实现前两个；二进制格式（Protobuf / MessagePack / CBOR…）改用一对字节通道——请求出口 `SerializeToBytes` + 响应入口 `Deserialize(ReadOnlyMemory<byte>)`（都自带默认实现，文本序列化器不用管）。另有一个**可选**属性 `MediaType`（同样带默认实现）：序列化器自报「我用哪个媒体类型」，调用方没设 `ContentType` 时库就拿它写请求头（文本与字节两条通道共用这次决策）：

```csharp
public interface IContentSerializer
{
    // 可选：本格式的媒体类型。null（默认）= 不自报，沿用 config.ContentType。
    // 只影响请求头，且只在调用方没显式设置 ContentType 时生效（文本 / 字节两条通道都算）。
    string? MediaType => null;

    string Serialize(object? value, Type type, AxiosConfig config);

    // 文本响应（含无 Content-Type）
    object? Deserialize(string text, Type type, AxiosConfig config);

    // ── 以下两个是二进制格式的字节通道，默认实现 = 「本格式没有字节形态」，文本序列化器不用管 ──

    // 请求出口：返回 null 走 Serialize 的文本通道；返回字节则原样发送（不编码、不补 charset）。
    // 与 MediaType 配对：注入一个 protobuf 序列化器，请求头与正文自动对齐。
    ReadOnlyMemory<byte>? SerializeToBytes(object? value, Type type, AxiosConfig config) => null;

    // 响应入口（Content-Type 声明为非文本）：默认按 UTF-8 解码转调文本入口，二进制格式覆写它
    object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config)
        => Deserialize(Encoding.UTF8.GetString(data.Span), type, config);
}
```

入库的只有「业务对象」，其余全由框架分派 —— 库不解释响应内容，也不要求任何通用数据树：

| 方向 | 谁处理 |
| --- | --- |
| 请求 `string` / `byte[]` / `Stream` / `HttpContent` | 原样发送，只套 `ContentType` |
| 请求 表单 / multipart | 库按 `ContentType` 组装（**不经过序列化器**） |
| 请求 基础类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…） | 框架直接取文本写入（**不经过序列化器**） |
| 请求 对象 / 字典 / dynamic / 集合（文本格式） | `Serialize`（字典与对象是同一套逻辑，不会被展平或当成标量） |
| 请求 对象 / 字典 / dynamic / 集合（二进制格式） | `SerializeToBytes`，字节原样发送；返回 `null` 才走文本 |
| 响应 `Stream` / `byte[]` | 框架直接给 |
| 响应 `string` | 框架直接给文本 |
| 响应 基础类型（数值 / 布尔 / 枚举 / Guid / 日期时间 / Uri…） | 框架直接解析文本 |
| 响应 `dynamic` | 框架按 JSON 展开（JSON 便利能力） |
| 响应 非泛型 / `JsonElement` | 文本 → 按 JSON 文档形态（失败给原文）；二进制 → 原始 `byte[]` |
| 响应 其它业务对象（文本响应） | `Deserialize(string)` |
| 响应 其它业务对象（二进制响应） | `Deserialize(ReadOnlyMemory<byte>)`，全程不解码 |

#### 序列化器可以写请求头

`Serialize` 拿到的 `config` 是**本次请求独占的副本**（`Defaults.Clone()`，Headers 也是独立副本——写时复制，任一方写入才真正分离），写进去的头会在请求体构造完成后统一应用——这就是 axios `transformRequest(data, headers)` 里 `headers.setContentType(...)` 的等价能力：

```csharp
public string Serialize(object? value, Type type, AxiosConfig config)
{
    config.Headers.Set("X-Checksum", Checksum(value));   // 与格式强绑定的头
    return ToCsv(value);
}
```

三条规则：

- **能覆盖 `Content-Type`**：应用时点在工厂设置之后，所以 `Headers.Set("Content-Type", ...)` 会赢；multipart 的 boundary 除外（库会跳过覆盖，否则正文没法解析）。
- **不会外溢**：per-request 副本，实例 `Defaults` 与并发请求都不受影响（有用例守着）。
- ⚠️ **给 `config.ContentType` 赋值对本次请求无效**：正文已按赋值前的值构造完，改它是静默空操作。声明格式用 `MediaType`，或直接写 `Headers`。

与格式无关的头（追踪 ID、租户）放 `TransformRequest` 更合适：时机更早、语义更清楚，也不会绑死在某个序列化器上。响应方向（`Deserialize`）写头无意义——请求早已发出。

反过来的方向也通：**反序列化时可以读响应头**，走 `config.Response`（`int Status` / `Headers` / `MediaType` / `CharSet` / `GetHeader(name)`）：

```csharp
public object? Deserialize(string text, Type type, AxiosConfig config)
    => config.Response?.GetHeader("X-Schema-Version") switch   // 响应方向才有值，请求方向是 null
    {
        "2" => ParseV2(text, type),
        _   => ParseV1(text, type)
    };
```

它有两个边界：**只在响应阶段有值**（`Serialize` 里读到 `null`）；**不随配置克隆**（`Clone()` 会清空，免得上一次响应的头漏进下一个请求）。`Headers` 与 `AxiosResponse.Headers` 是同一份字典，零拷贝。`TransformResponse` 同样能读它——把 `X-Total-Count` 这类元数据贴到业务对象上正合适。

#### 用哪个序列化器：4 层

| 顺序 | 来源 | 说明 |
| --- | --- | --- |
| 1 | `AxiosConfig.ContentSerializer`（单次请求 / 实例 defaults） | **显式注入，最高优先级** |
| 2 | `AxiosContent.Serializer`（全局显式注入） | **`null`（默认）= 未注入** → 进自动模式 |
| 3 | 自动模式：按媒体类型查注册表（`AxiosContent.Register`） | 请求看 `ContentType`，响应看响应 `Content-Type`；**未注册查表得到 `null`** |
| 4 | 内置 JSON（`SystemTextJsonSerializer.Instance`） | 兜底 |

库**内置两个**实现：`SystemTextJsonSerializer`（JSON）与 `XmlContentSerializer`（XML，`System.Xml.Serialization`，零第三方依赖）。要 XML 有两条路：

```csharp
using AxiosNet.Serialization;

// 方式 A：显式注入 —— 一个 client（或整个应用）只说一种格式
AxiosContent.Serializer = XmlContentSerializer.Instance;                        // 全局
// 或 new AxiosClient(new AxiosConfig { ContentSerializer = XmlContentSerializer.Instance })   // 仅实例

// 方式 B：自动模式 + 注册 —— 同一个应用按 Content-Type 混用多种格式
AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
AxiosContent.Register(AxiosContentType.Json, new MyJsonEngine());   // 顺带换掉 JSON 引擎
// AxiosContent.Unregister(AxiosContentType.Xml);                   // 注销

await http.PostAsync("orders", order);                                             // 方式 A：头自动 = 序列化器自报的 MediaType
await http.PostAsync("orders", order, c => c.ContentType = AxiosContentType.Xml);   // 方式 B：显式指定，触发自动匹配
var user = (await http.GetAsync<User>("users/1")).Data;                            // application/json → 自动 → JSON
```

注册表匹配规则：只看媒体类型主值（忽略大小写、忽略 `; charset=` 等参数）精确匹配；`application/vnd.api+xml` 这类**结构化后缀**会回落到 `application/xml`（`+json` / `+yaml` 同理），因此给一种格式注册一次就覆盖它的厂商变体——只有显式为某个厂商类型注册过才精确优先。

> 头体配对是**双向**的：自动模式「按头选格式」，序列化器又能「自报头」。
> ① **注入路径**（方式 A）：`ContentSerializer = XmlContentSerializer.Instance` 一处改动即可，请求头自动变 `application/xml`，不用再设 `ContentType`；
> ② **注册路径**（方式 B）：注册表的键就是媒体类型，所以 `ContentType` 必须自己设，否则查不到表、回落内置 JSON；
> ③ 一旦显式设置过 `ContentType`（单次或实例 defaults），序列化器的 `MediaType` 一律不覆盖它——那是调用方对服务端的判断；
> ④ `ContentType = Xml` 但既没注入也没注册 XML 时，正文仍是 JSON（头体不一致不会被自动纠正）。

内置 XML 实现的细节与约束：

- 按目标类型缓存 `XmlSerializer`（构造开销大），线程安全可共享；
- 输出声明 `encoding="utf-8"`（与写 socket 的编码一致），根元素不带 `xmlns:xsi` / `xmlns:xsd` 噪音，`[XmlRoot(Namespace = ...)]` 照常生效；
- 反序列化**禁用 DTD**（防 XXE / 十亿笑声），失败统一是 `ERR_BAD_RESPONSE`；
- XmlSerializer 本身的限制照旧：目标类型要 **public + 公开无参构造**、只序列化公开可读写属性/字段；匿名类型、接口、`Dictionary<,>` 不支持 —— 请用 DTO。

其它文本格式（YAML / CSV …）自己实现两个方法即可：

```csharp
using AxiosNet.Serialization;

public sealed class YamlSerializer : IContentSerializer
{
    // 自报媒体类型：注入后不设 ContentType，请求头也自动是 application/yaml
    public string? MediaType => "application/yaml";

    public string Serialize(object? value, Type type, AxiosConfig config)
        => YamlConvert.Serialize(value, type);

    public object? Deserialize(string text, Type type, AxiosConfig config)
        => YamlConvert.Deserialize(text, type);
}

AxiosContent.Serializer = new YamlSerializer();          // 全局；也可按实例/单次请求设置
```

边界说明：

- **请求头可以由序列化器自报**：`MediaType` 非 `null` 且调用方没显式设 `ContentType` 时，库把它写进请求头（对应 axios 默认 `transformRequest` 里的 `headers.setContentType(..., false)`），文本与字节两条通道都生效。显式设过就绝不覆盖。
- **文本 / 二进制由响应 `Content-Type` 判定**（只看媒体类型主值，忽略 `charset` / `boundary` 等参数）：`text/*`、`application/json`、`application/xml`、`+json` / `+xml` / `+yaml`、YAML、CSV 等算文本，其余（`application/octet-stream`、`image/png`、`application/x-protobuf`…）算二进制；**没有 Content-Type 视为文本**。响应方向不看 `MediaType`，头是权威。
- **二进制响应不解码**：业务对象走 `Deserialize(ReadOnlyMemory<byte>)` 拿到原始字节（Protobuf / MessagePack / CBOR 零拷贝接入）；非泛型调用直接给 `byte[]`。字节入口的默认实现按 UTF-8 解码转调文本入口，因此文本序列化器不用实现它（也兼容把 JSON 错标成 `octet-stream` 的服务端）。
- **二进制格式的请求出口**：覆写 `SerializeToBytes` 返回线格式字节，库原样发送（不编码、不补 charset），头取自报的 `MediaType`——与响应字节入口配对后，注入一个序列化器就完成整条链路换格式。返回 `null` 回落文本通道；字节的内存必须存活到整条请求结束（不要返回 ArrayPool 租约）。**文本通道无法承载任意字节**，二进制格式的 `Serialize` 实现成抛 `NotSupportedException` 即可。
- **请求方向的基础类型不进序列化器**：`PostAsync("url", 42)` 发的是 `42`、`true` 发 `true`、枚举发名字。裸标量没有「格式」可言（XML / YAML 无法表达无名的根标量），交给序列化器只会让每个实现都重复一遍标量特判。因此它也不参与 `MediaType` 回写：头保持 `ContentType`（默认 `application/json`，而裸 `42` 恰好是合法 JSON）。日期时间用往返格式（保留 Kind 与 tick），不像表单那样折成毫秒级 UTC。
- **表单 / multipart 不经过序列化器**：字段名只看 `FormFieldNameSelector` 与属性上的 `[JsonPropertyName]`（忽略用 `[JsonIgnore]`）。
- **`dynamic` 依赖 JSON**：非 JSON 响应下 `GetAsync<dynamic>` 会抛 `ERR_BAD_RESPONSE`；要原文请用 `ResponseType.Text` 或非泛型 `GetAsync()`。
- **非泛型响应**：文本按 JSON 文档形态给 `JsonElement`（解析失败给原文），二进制给原始 `byte[]`，与用哪个序列化器无关。
- **`GetAsync<JsonDocument>` 也能用，但它算「业务对象」**：不做框架的文档形态，而是交给当前序列化器（默认 STJ 原生支持）。因此它 **需要调用方 `Dispose`**（背后是池化缓冲），换成 XML 序列化器会直接失败 —— 只想读 JSON 树时优先用 `GetAsync<JsonElement>`（框架给、免释放）或非泛型 `GetAsync()`。
- 性能注记：文本响应仍要先解码成字符串（约 2× payload 的额外拷贝，只影响大响应）；二进制响应完全没有这一步，字节直达序列化器。

#### JSON 专项设置

JSON 行为分三层，后者覆盖前者：

| 层级 | 入口 | 典型用途 |
| --- | --- | --- |
| 全局 | `AxiosJson.Options` | 整个应用统一命名策略、数字/日期处理 |
| 实例 | `Axios.Create(c => c.JsonSerializerOptions = ...)` | 某个后端对接 PascalCase / snake_case |
| 单次请求 | `c => c.JsonSerializerOptions = ...` | 个别接口的特殊契约 |

```csharp
// 1) 全局：不再 camelCase（例如后端字段就是 PascalCase），并允许字符串化的数字
AxiosJson.Options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = null,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
};

// 2) 实例：只影响这个 client
var http = Axios.Create(c =>
{
    c.BaseAddress = "https://api.example.com/";
    c.JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
});

// 3) 单次请求：优先级最高
await http.PostAsync("users", user, c => c.JsonSerializerOptions = specialOptions);
```

> 替换 `AxiosJson.Options` 时请给一个**新实例**；System.Text.Json 会冻结已使用过的选项实例，之后再修改会抛 `InvalidOperationException`。建议在启动期设置一次。

需要换掉整个 JSON 引擎（Newtonsoft.Json、源生成序列化器等）时，同样是实现 `IContentSerializer` —— 它只影响「业务对象」的编解码，`dynamic` 与非泛型响应仍由库自己处理：

```csharp
using AxiosNet.Serialization;

public sealed class NewtonsoftJsonSerializer : IContentSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public string Serialize(object? value, Type type, AxiosConfig config) =>
        JsonConvert.SerializeObject(value, type, Settings);

    public object? Deserialize(string json, Type type, AxiosConfig config) =>
        JsonConvert.DeserializeObject(json, type, Settings);
}

AxiosContent.Serializer = new NewtonsoftJsonSerializer();         // 全局
// 或：new AxiosClient(new AxiosConfig { JsonSerializer = ... })   // 仅实例
```

约定与边界：

- 序列化器会被并发请求共享，实现需无状态或线程安全；抛出的异常会被统一包装成 `ERR_BAD_RESPONSE` / `ERR_BAD_REQUEST`。
- 影响范围：**业务对象的请求体**与**业务对象的响应反序列化**；`dynamic`、非泛型响应、基础类型、表单 / multipart 都不经过它。
- 只有 `IContentSerializer` 一个抽象、一个注入点；单次请求高于实例，实例高于全局（`AxiosContent.Serializer`）。

### paramsSerializer

`ParamsSerializer` 收到**整个 `Params` 对象**，返回编码好的查询串（前导 `?` / `&` 会被自动去掉）：

```csharp
// 逗号分隔的数组：ids=1,2,3&q=a%20b
await http.GetAsync("items", c =>
{
    c.Params = new ItemQuery { Ids = new[] { 1, 2, 3 }, Q = "a b" };
    c.ParamsSerializer = p =>
    {
        var q = (ItemQuery)p!;
        return $"ids={string.Join(",", q.Ids)}&q={Uri.EscapeDataString(q.Q)}";
    };
});
```

> 第一个参数是 `object?`，需要强类型时先转成自己的 DTO。默认序列化器已是 axios 兼容风格（数组 `a[]=1&a[]=2`、嵌套 `o[k]=v`、null 跳过），大多数场景不需要自定义。

### FormFieldNameSelector

决定 multipart / urlencoded 表单中**字段名如何拼接**，签名 `Func<string?, string, string>`（父级名, 当前名）：

```csharp
// 默认是 prefix[name]；改成 dot 风格：user.name
await http.PostFormAsync("submit", new { user = new { name = "bob" } }, c =>
{
    c.FormFieldNameSelector = (prefix, name) =>
        string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
});
```

### transformRequest / transformResponse

与 axios 的转换链同构：**请求方向在拦截器之后、编码之前**依次执行（可改 `Data`、加字段、整体换类型）；**响应方向在反序列化之后**依次执行（可清洗、归一化 `Data`）。

```csharp
// 实例默认链
var http = Axios.Create(c =>
{
    c.TransformRequest.Add((data, cfg) =>
    {
        // 给字典型 body 打时间戳（其他类型原样放行）
        if (data is IDictionary<string, object?> map)
        {
            map["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        return data;
    });

    // 响应侧：统一剥掉服务端的 { code, data } 外壳
    c.TransformResponse.Add((data, cfg) =>
        data is JsonElement { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty("data", out var inner)
            ? inner
            : data);
});

// 单次请求追加（与实例链组合，顺序稳定：先实例再本次）
await http.GetAsync("users/1", c =>
{
    c.TransformResponse.Add((data, cfg) => data is JsonElement e ? Normalize(e) : data);
});
```

- 链是 `IList<Func<object?, AxiosConfig, object?>>`，返回 `null` 是合法的（会继续往后传）。
- `TransformRequest` 的返回值仍由 `ContentType` 决定编码方式——转换链不改变"格式由 ContentType 决定"这条铁律。
- `TransformResponse` 在 `ResponseType.Json` / `Text` / `ArrayBuffer` / `Dynamic` 下都会执行；`Stream` 不执行（数据尚未读取）。
- `TransformResponse` 拿到的 `data` 已经是**目标类型**：非泛型请求是 `JsonElement`（或文本），`GetAsync<User>` 就是 `User`。想在 JSON 层统一加工（如剥 `{code,data}` 外壳），用非泛型请求 + `TransformResponse`，或改用 `JsonSerializerOptions` 里的自定义转换器。

## 拦截器

```csharp
// 请求拦截器：与 axios 一致，按注册顺序【逆序】执行
client.Interceptors.Request.Use(config =>
{
    config.Headers.Set("X-Trace-Id", TraceId.New());
    return config;
});

// 异步拦截器
client.Interceptors.Request.Use(async config =>
{
    var token = await tokenService.GetTokenAsync();
    config.Headers.Set("Authorization", $"Bearer {token}");
    return config;
});

// 响应拦截器：按注册【顺序】执行，rejected 分支可吞错并返回兜底响应
client.Interceptors.Response.Use(
    onFulfilled: response => response,
    onRejected: ex =>
    {
        var error = (AxiosException)ex;
        if (error.Code == "ERR_BAD_REQUEST" && error.Response!.Status == 401)
        {
            // 刷新 token 后重放，或直接返回兜底数据
        }
        throw ex;
    });

// 移除拦截器
var id = client.Interceptors.Request.Use(cfg => cfg);
client.Interceptors.Request.Eject(id);
```

## 错误处理

所有错误统一抛出 `AxiosException`，携带 `IsAxiosError / Code / Config / Request / Response`：

```csharp
try
{
    await client.GetAsync("missing");
}
catch (AxiosException ex)
{
    Console.WriteLine(ex.Code);              // ERR_BAD_REQUEST
    Console.WriteLine(ex.Status);            // 404（= Response?.Status 的快捷访问）
    Console.WriteLine(ex.Message);           // Request failed with status code 404
    Console.WriteLine(ex.Cause);             // 底层异常（DNS/连接失败等场景），无则为 null
}

// 与 axios.isAxiosError / axios.isCancel 对齐
if (Axios.IsAxiosError(ex) && !Axios.IsCancel(ex)) { /* ... */ }
```

`ex.ToJson()` 返回与 axios `AxiosError.toJSON()` 同形的只读字典（`name` / `message` / `code` / `status` / `method` / `url` / `isAxiosError` / `stack`），可直接丢进结构化日志。

| Code | 触发场景 | 对应 axios |
| --- | --- | --- |
| `ERR_BAD_REQUEST` | 响应状态码为 4xx（默认 validate 失败）；请求体超过 `MaxBodyLength`；请求体序列化失败（循环引用、转换器抛错等，底层异常见 `ex.Cause`） | ✅ |
| `ERR_BAD_RESPONSE` | 响应状态码为 5xx；响应体无法反序列化为目标类型（含 `dynamic` 拿到非 JSON）；响应体超过 `MaxContentLength`；泛型参数与 `responseType` 不匹配（如 `GetAsync<Stream>` 却配 `text`/`arraybuffer`） | ✅ |
| `ECONNABORTED` | 请求超过 `Timeout`（消息可用 `TimeoutMessage` 覆盖） | ✅ |
| `ERR_NETWORK` | 连接被拒绝/DNS 失败/连接中途断开/响应体被截断等网络层异常（底层异常见 `ex.Cause`） | ✅ |
| `ERR_CANCELED` | `CancellationToken` 取消 | ✅ |
| `ERR_INVALID_URL` | URL 非法（含相对 URL 且未提供 `baseURL`、非 `http`/`https` 的 scheme） | ✅ |
| `ERR_BAD_OPTION_VALUE` | 在单次请求上写了**与实例生效值不同**的仅实例级选项（`proxy` / `credentials` / `withCredentials` / `maxRedirects` / `poolOptions`）；共享池 client 的 defaults 上设置连接级选项；同时传 `pool` 与 `poolOptions`；连接池选项取值非法（如 `MaxConnectionsPerServer <= 0`）；`Content-Type` 为 `multipart/form-data` 而 `data` 是原始内容（`string` / `byte[]` / `Memory` / `Stream`，且 Content-Type 未带 `boundary`）；`Params` 是标量（`int` / `bool` / `DateTime` …，无法表达键名）；拦截器返回 `null` | ✅（同码） |

## 超时与取消

```csharp
// 单次请求超时
await client.GetAsync("slow", c => c.Timeout = TimeSpan.FromMilliseconds(500));

// 关闭实例级默认超时（显式 Timeout = 0 即覆盖；未赋值才沿用实例默认）
await client.GetAsync("slow-but-wanted", c => c.Timeout = TimeSpan.Zero);

// 外部取消（等同 AbortController）
using var cts = new CancellationTokenSource();
var task = client.GetAsync("slow", cancellationToken: cts.Token);
cts.CancelAfter(TimeSpan.FromSeconds(1));
```

> 流式响应（`ResponseType.Stream`）的超时覆盖到正文读取结束：请求返回后，在流上继续读超过 `Timeout` 同样抛 `ECONNABORTED`。注意这是**全程总预算**（建连 + 等响应头 + 读正文一起算），不是单次读写的空闲超时——响应头阶段耗时也会从正文剩余预算里扣除；需要"长连接式"的空闲超时请自行用 `CancellationToken` 控制。令牌是交给每一次底层读写的，同步 `Read`/`Write`/`Flush` 同样生效（内部实现为对异步核心的阻塞等待，读取过程中触发超时/取消会立刻打断），只是它会占住当前线程，能用 `ReadAsync` 的场景优先用 `ReadAsync`。
>
> 超时值上限约 **49.7 天**（底层计时器是 `uint.MaxValue-1` 毫秒）：超过这个值会**收敛到上限**而不是抛异常，因此 `TimeSpan.MaxValue` 也是安全的；真正的无限超时用 `Timeout = TimeSpan.Zero`。负值抛 `ERR_BAD_OPTION_VALUE`。取消/超时异常会保留底层 `OperationCanceledException` 作为 `Cause`，`AxiosException.ToJson()` 与日志都能看到触发点。

## 网络连接池

### 为什么需要

.NET 的 `SocketsHttpHandler` 本身已经能复用 TCP 连接，但错误地“每次请求 new HttpClient/Handler”会导致 socket 耗尽，而全局唯一 `HttpClient` 又无法按目标站点隔离调优、无法及时回收。`HttpConnectionPool` 在此之上提供：

1. **按 origin 复用**：每个 `scheme://host:port` 对应一个调优过的 `SocketsHttpHandler`，全部请求共享，TCP 长连接复用（Keep-Alive）。主机名按 IDNA/punycode 归一——`münchen.de` 与 `xn--mnchen-3ya.de` 是同一域名的两种书写（HTTP Host 头只能是 ASCII，发送前会规范化），视为同一站点共享节点；IPv6 保留方括号；
2. **连接数限流**：`MaxConnectionsPerServer` 控制单站点最大 TCP 连接数，超出的请求在连接层排队，高并发下吞吐更稳定；
3. **生命周期治理**：池化连接最大存活时间（平滑 DNS 变更）、空闲超时、建连超时；
4. **空闲 origin 驱逐**：后台定时器自动释放长期无流量站点的 Handler；
5. **多实例共享**：多个 `AxiosClient`（例如对应不同业务模块）可以共享同一个池；
6. **可观测**：`GetStatistics()` 输出 origin 节点数、累计租约、每节点活跃租约等。

### 使用方式

```csharp
using System.Security.Authentication;
using AxiosNet.Pooling;

// 1) 每个 AxiosClient 默认自建池，也可显式配置
var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,           // 单站点最大连接数，默认 100
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    ConnectTimeout = TimeSpan.FromSeconds(15),
    IdleHandlerLifetime = TimeSpan.FromMinutes(5),
    EvictionInterval = TimeSpan.FromSeconds(30),
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5,
    AutomaticDecompression = true,          // gzip/deflate/br
    MaxEndpointCount = 1024,                // origin 节点数上限（0/负数 = 不限制）
    UseCookies = false,                     // 打开后整个池共用一份 CookieContainer
    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,   // 可选：限制 TLS 版本
    ConfigureSslOptions = ssl =>            // 可选：客户端证书 / ALPN / 加密套件等 SslClientAuthenticationOptions 扩展
    {
        // ssl.ClientCertificates.Add(myCert);
    }
});

// 2) 推荐：全局共享一个池给多个客户端
await using var sharedPool = new HttpConnectionPool(new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 64
});
using var orderClient  = new AxiosClient(pool: sharedPool);
using var payClient    = new AxiosClient(pool: sharedPool);
using var payClient2   = Axios.Create(c => c.BaseAddress = "https://pay.example.com/", sharedPool);  // 静态门面同样支持共享池

// 3) 观察池状态
ConnectionPoolStatistics stats = sharedPool.GetStatistics();
// stats.EndpointCount / TotalLeases / Endpoints[*].ActiveLeases ...

// 手动驱逐空闲 origin（也会由后台定时器自动执行）
sharedPool.EvictIdleEndpoints();
```

> fail-fast：共享池的连接行为（重定向 / 代理 / 凭据 / Cookie / 池选项）在创建池时已固定。在共享池 client 的 defaults 上设置 `MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials` / `PoolOptions`，或给 `AxiosClient` 同时传 `pool` 与 `poolOptions`，都会抛 `ERR_BAD_OPTION_VALUE` 而不是静默忽略——这些项请在创建池的 `ConnectionPoolOptions` 上配置，或让该 client 自建池（不传 `pool`）。
>
> 两个默认行为：未显式设置 `Proxy` 时池**不使用系统/环境代理**（避免环境变量带来的意外路由），需要显式走系统代理时设 `Proxy = HttpClient.DefaultProxy`；`EvictionInterval ≤ 0` 时不启动后台驱逐定时器（只能手动 `EvictIdleEndpoints()`）。池构造时会克隆传入的 options，`pool.Options` 也返回副本——事后修改自己持有的对象不影响已建成的池。
>
> `MaxEndpointCount` 是资源面护栏：目标主机若来自用户输入（短链、webhook、多租户自定义域名），origin 节点数就是可被放大的面。达到上限时先驱逐最久未使用的空闲节点；一个都驱逐不掉（全部在途）才抛 `InvalidOperationException`，不会悄悄丢请求。
>
> `UseCookies = true` 时整个池**共用一份** `CookieContainer`（不再每个 origin 一份）：Cookie 的隔离维度是域/路径而不是端口，共用之后 `auth.example.com` 种下的会话才能被 `api.example.com` 带上，与浏览器/axios 的 `withCredentials` 语义一致，不同站点之间仍由域匹配隔离。
>
> `Dispose()` 是**优雅关闭**：空闲节点立即释放，仍有请求在途的节点等最后一条租约归还时自行回收——关闭客户端不会把在途请求打成 `ObjectDisposedException`。需要立刻掐断全部连接用 `Clear()`。
>
> `AxiosClient.Dispose()` 同样不打断在途请求：仍有请求在飞时，注入的 `HttpClient`/`Handler` 或自建池的释放会推迟到最后一个请求结束（由它代为执行），但此后不再接受新请求（抛 `ERR_CANCELED`，不再漏裸 `ObjectDisposedException`）。

> 性能要点：顺序/并发请求都会复用已建立的 TCP 连接（仓库测试与示例实测：5 个顺序请求只建立 1 条 TCP；6 个并发慢请求在 `MaxConnectionsPerServer=2` 下峰值并发恰好为 2）。应用关闭时 `Dispose()` 池即可释放全部连接；共享池不会因为单个 client 释放而被销毁（仅池拥有者负责释放）。

### DNS 变更与连接过期

连接池里存的是 Handler，不是 IP；IP 绑在 Handler 内部的 TCP 连接上。所以 DNS 切换后的收敛窗口由「连接还能复用多久」决定：

| 控制项 | 作用 |
| --- | --- |
| `PooledConnectionLifetime` | 一条连接最多可被复用的时长。到期后**下一次取用**时才换新连接（不打断在途请求），新连接会重新解析 DNS。默认 10 分钟，快速切流场景可调到 30–60 秒；设为 `Timeout.InfiniteTimeSpan` 表示永不轮换 |
| `PooledConnectionIdleTimeout` | 空闲连接直接关闭（默认 2 分钟），低流量 origin 因此更快收敛 |
| `IdleHandlerLifetime` | 整个 origin 的 Handler 空闲后驱逐，等于彻底重置该站点的连接状态 |
| `HttpConnectionPool.Clear()` | 立即释放所有 origin 节点，**会打断在途请求**，只适合运维手动触发或进程关闭 |

现网经验：连接健康时连接池不会因为 DNS 变了就主动断开——这是 keep-alive 的收益，也意味着必须接受「最多一个 `PooledConnectionLifetime` 的不新鲜窗口」。需要服务发现（Consul / Nacos / SRV / 权重）时，用 `HandlerFactory` + `ctx.CreateDefaultHandler()` 拿到 Handler 后设置 `ConnectCallback` 自行决定目标地址；它按「每条新 TCP 连接」调用一次，天然跟着生命周期刷新。

完整可运行演示见示例第 20 项（`Examples/DnsRefreshExample.cs`）：两个本地服务器模拟同一域名的旧/新地址，几秒内就能看到「切 DNS 后请求仍命中旧地址 → 生命周期到期后落到新地址」，以及 `Clear()` 的立即收敛。

## 自定义 Handler 注入

连接层有两个可插拔点，按「是否保留连接池」选择：

| 需求 | 用法 | 走连接池 |
| --- | --- | --- |
| 插入日志/签名/重试等 `DelegatingHandler`，同时保留按 origin 复用 | `ConnectionPoolOptions.HandlerFactory` | ✅ |
| 完全接管连接层（测试桩、自定义传输、外部给的 Handler） | `new AxiosClient(handler: ...)` | ❌ |

### 方式一：HandlerFactory（推荐）

每个 origin 创建 Handler 时调用一次。`PooledHandlerContext` 提供 `Origin` / `BaseAddress` / `Options`，以及 `CreateDefaultHandler()` —— 拿到的是**按池选项配好的** `SocketsHttpHandler`（连接上限、生命周期、重定向、解压、代理、证书校验都已就绪），直接当内层即可：

```csharp
using AxiosNet.Pooling;

using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,
    HandlerFactory = ctx => new TraceHandler               // 你的 DelegatingHandler
    {
        InnerHandler = ctx.CreateDefaultHandler()          // 内层：调优过的 SocketsHttpHandler
    }
});
```

约定：

- 每个 origin 必须返回**独立**实例（同一个 Handler 不能同时挂在多条管线/多个 origin 上）；
- 返回的 Handler 由连接池负责释放（origin 被驱逐、池 `Clear()` / `Dispose()` 时）；
- 返回 `null` 会在首次请求该 origin 时抛 `InvalidOperationException`；
- 工厂只在「首次访问某 origin」时调用，之后复用同一个 Handler —— 连接复用、`MaxConnectionsPerServer`、空闲驱逐等池能力全部保留。

### 方式二：直接注入 Handler（无池）

```csharp
// 1) 完全自定义传输（示例：内存桩，不发网络请求）
var stub = new StubHandler();
using var client = new AxiosClient(handler: stub);

// 2) 自己拼 Handler 链
var handler = new TraceHandler { InnerHandler = new SocketsHttpHandler() };
using var client2 = new AxiosClient(
    defaults: new AxiosConfig { BaseAddress = "https://api.example.com/" },
    handler: handler,
    disposeHandler: true);   // 释放 AxiosClient 时是否一并释放该 Handler，默认 true

// 3) 静态门面
using var client3 = Axios.Create(
    c => c.BaseAddress = "https://api.example.com/",
    handler: new TraceHandler { InnerHandler = new SocketsHttpHandler() });
```

> 一个 Handler 实例只能属于一条管线：不要把它同时注入多个 client。

要点：

- 注入后**不创建连接池**：`client.Pool` 为 `null`、`client.UsesConnectionPool` 为 `false`，所有 origin 共用这一个 Handler（`SocketsHttpHandler` 自身仍会按 host 复用 TCP）。
- 连接层行为完全由 Handler 决定，因此在 defaults 上设置 `MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials` 会抛 `ERR_BAD_OPTION_VALUE`（避免"设了没生效"）。
- 与 `pool` / `poolOptions` 互斥，同时传入同样抛 `ERR_BAD_OPTION_VALUE`。
- 超时仍由库按请求控制（`HttpClient.Timeout` 内部设为 Infinite，`c.Timeout` 照常生效）；`OnUploadProgress` / `maxRate` / `maxContentLength` 等数据层能力不受影响。

## 特性

**与 axios 对齐**

- `axios.create(config)` → `Axios.Create(config)`，静态门面 `Axios.GetAsync(...)`
- 快捷方法：`Get / Post / Put / Patch / Delete / Head / Options`；`Method` 直接用 BCL `HttpMethod`，自定义谓词（`new HttpMethod("PROPFIND")`）照发不误
- `baseURL + url` 合并、`params` 默认序列化（数组 `a[]=1&a[]=2`、嵌套 `o[k]=v`、null 跳过）
- `data` 编码由 `config.ContentType` 决定（默认 `application/json`，与数据类型无关）：对象/字典/dynamic/集合统一 JSON；设为 `application/x-www-form-urlencoded` 转键值对表单；设为 `multipart/form-data` 自动把 `FormFile`/`Stream`/`byte[]` 拼成文件部件；`string/byte[]/Stream/HttpContent` 原样透传
- 所有快捷方法（`Get/Post/Put/Patch/Delete/Head/Options`）都可携带任意类型的 `data`
- 请求/响应拦截器（同步 & 异步、`Eject`、请求拦截器逆序执行、响应拦截器顺序执行、错误分支可恢复）
- `validateStatus` 自定义合法状态码
- `timeout`（`ECONNABORTED`）、外部取消（`ERR_CANCELED`）、网络错误（`ERR_NETWORK`）、`timeoutMessage` 自定义超时消息
- 错误码与 axios 对齐：`ERR_BAD_REQUEST`(4xx) / `ERR_BAD_RESPONSE`(5xx) / `ERR_INVALID_URL` / `ERR_BAD_OPTION_VALUE`，`Axios.IsAxiosError` / `Axios.IsCancel` 判定
- `auth`（HTTP Basic）、`withCredentials`、代理、自动重定向（`maxRedirects: 0` 可完全关闭）、gzip/deflate/br 自动解压
- `axios.getUri(config)` → `client.GetUri(config)`：只拼 URL 不发请求（调试/日志/预签名）
- `maxContentLength` / `maxBodyLength`：响应体与请求体大小上限，超限分别抛 `ERR_BAD_RESPONSE` / `ERR_BAD_REQUEST`，消息里带实际字节数
- `onUploadProgress` / `onDownloadProgress`：进度事件（已传/总量/百分比/平均速率/预估剩余），上传与下载双向
- `maxRate`：上传、下载单向限速（字节/秒）
- `paramsSerializer` / `formFieldNameSelector`：自定义查询串与表单字段名（对应 axios `formSerializer` 的字段名定制）
- `transformRequest` / `transformResponse`：请求前、响应后数据变换链（线程安全，可在运行期增删）
- 🔧 **可插拔内容序列化**：默认 JSON（System.Text.Json），**内置 XML**（`XmlContentSerializer`）；`JsonSerializerOptions` 按「全局 → 实例 → 单次请求」覆盖；实现 `IContentSerializer`（文本格式两个方法 + 二进制格式一对字节通道 `SerializeToBytes` / `Deserialize(bytes)` + 可选的 `MediaType` 自报媒体类型）即可换成 YAML / Protobuf / 换掉 JSON 引擎（`AxiosContent.Serializer` 全局注入），请求头由序列化器自动对齐
- `postForm` / `putForm` / `patchForm`：一行发 `multipart/form-data` 或表单
- 完整响应结构：`Data / Status / StatusText / Headers / Config / Request`

**额外能力**

- 🔌 **网络连接池** `HttpConnectionPool`：按 `scheme://host:port` 池化并复用 `SocketsHttpHandler`，`MaxConnectionsPerServer` 限流、连接生命周期与空闲回收、后台空闲 origin 驱逐、多客户端共享同一池、租约统计
- 🧩 **dynamic 支持**：`GetAsync<dynamic>` / `ResponseType.Dynamic` 直接 `data.address.city`、`data.items[0].id`，无需定义 DTO（对标 JS 里直接访问 `response.data.xxx`）
- 📦 泛型强类型响应 `GetAsync<T>` 与非泛型 `JsonElement` 响应并存
- ⬇️ **文件下载**：`GetAsync<Stream>` 流式落盘（内存占用恒定）、`GetAsync<byte[]>` 一次性字节；流读完自动归还连接（可复验连接复用）
- ⬆️ **文件上传**：裸流直传（`Stream`/`byte[]` + 文件类型 `ContentType`）与 `multipart/form-data`（`FormFile`、同名重复部件）；示例服务器按字节精确解析并回报字段名/文件名/大小/SHA256
- 📊 **流量治理**：进度回调 + 单向限速 + 大小上限，均为纯 BCL 实现（包装 `HttpContent.SerializeToStreamAsync` 与 `Stream`），不依赖 `HttpClient` 原生能力
- 🧵 **自定义 Handler 注入**：`ConnectionPoolOptions.HandlerFactory` 在每个 origin 的默认 `SocketsHttpHandler` 外套你的 `DelegatingHandler`（日志/签名/重试/埋点），保留连接复用；`new AxiosClient(handler: ...)` 则完全接管连接层（不走池）

## axios ↔ AxiosNet 对照表

| axios (JS) | AxiosNet (.NET) |
| --- | --- |
| `axios(config)` | `client.RequestAsync(new AxiosConfig { ... })` |
| `axios(url, config)` | `client.GetAsync(url, c => { ... })` |
| `config.method` | `c.Method`（`System.Net.Http.HttpMethod`，默认 `HttpMethod.Get`） |
| `axios.get/post/put/patch/delete/head/options` | `GetAsync / PostAsync / PutAsync / PatchAsync / DeleteAsync / HeadAsync / OptionsAsync` |
| `axios.create(config)` | `Axios.Create(config)` / `Axios.Create(config, sharedPool)`（第二参数可传共享池） |
| `axios.getUri(config)` | `client.GetUri(config)` / `client.GetUri(c => ...)` / `Axios.GetUri(...)` |
| `axios.postForm / putForm / patchForm` | `PostFormAsync / PutFormAsync / PatchFormAsync`（默认 multipart） |
| `axios.defaults` | `Axios.Defaults` / `client.Defaults` |
| `res.data / status / statusText / headers / config` | `Data / Status / StatusText / Headers / Config` |
| `interceptors.request.use(ok, err)` | `Interceptors.Request.Use(...)`（同步/异步重载） |
| `interceptors.response.use(...)` | `Interceptors.Response.Use(...)` |
| `interceptors.x.eject(id)` | `Eject(id)` |
| `config.params` | `c.Params`（对象/字典/数组/嵌套；也接受已序列化好的查询串 `string`，标量会抛 `ERR_BAD_OPTION_VALUE`） |
| `config.data`（对象→JSON） | `c.Data`（任意类型；编码方式由 `c.ContentType` 决定，默认 JSON） |
| `config.ContentType` | 请求体媒体类型（默认 `AxiosContentType.Json`，可传含 charset 的完整值；决定 data 的编码格式） |
| —（无对应） | `AxiosContentType` 预置常量：`Json` / `FormUrlEncoded` / `MultipartFormData` / `OctetStream` / `Xml` / `JsonPatch` / `ProblemJson` / `TextPlain` / `TextHtml` |
| `responseType: 'json'/'text'/'arraybuffer'/'stream'` | `ResponseType.Json/Text/ArrayBuffer/Stream` |
| —（JS 天然动态） | `GetAsync<dynamic>` / `ResponseType.Dynamic` |
| —（无对应配置） | `RequestEncoding` / `ResponseEncoding`（请求/响应文本编码） |
| `validateStatus` | `c.ValidateStatus = status => ...` |
| `timeout` / `AbortController` | `c.Timeout` / `c.CancellationToken` |
| `auth: {username,password}` | `c.Auth = new AxiosAuth(user, pass)` |
| `withCredentials` / `proxy` / `maxRedirects` | 同名属性（**仅实例级**生效：按请求写与实例不同的值会抛 `ERR_BAD_OPTION_VALUE`，见下） |
| `maxContentLength` / `maxBodyLength` | 同名属性（`long?`，字节；超限分别抛 `ERR_BAD_RESPONSE` / `ERR_BAD_REQUEST`） |
| `maxRate` | `c.MaxRate`（字节/秒，上传下载共用） |
| `onUploadProgress` / `onDownloadProgress` | 同名属性（`Action<AxiosProgressEvent>`） |
| `timeoutMessage` | `c.TimeoutMessage` |
| `paramsSerializer` | `c.ParamsSerializer` 或实例级 `Defaults.ParamsSerializer`（返回已编码查询串） |
| `formSerializer`（字段名/嵌套风格） | `c.FormFieldNameSelector`（`Func<string?, string, string>`，`(父级名, 当前名) => 最终名`；默认 `prefix[name]`） |
| `transformRequest` / `transformResponse` | 同名属性（`IList<Func<object?, AxiosConfig, object?>>`，顺序执行、可追加） |
| `AxiosError.code` | `AxiosException.Code`（同名字符串），另有 `Status` / `Cause` / `ToJson()` |
| `axios.isAxiosError(e)` / `axios.isCancel(e)` | `Axios.IsAxiosError(e)` / `Axios.IsCancel(e)` |
| （无内置） | `HttpConnectionPool` / `ConnectionPoolOptions` |

## 与 axios 的差异说明

受 .NET 平台特性影响的有意差异：

1. 方法命名为 .NET 习惯的 `XxxAsync`（`GetAsync` 对应 `axios.get`）；
2. `timeout` 使用 `TimeSpan` 而非毫秒数；
3. 非泛型文本响应的 `Data` 是 `JsonElement`，二进制响应是 `byte[]`（需要 JS 式动态访问时使用泛型 `GetAsync<dynamic>`）；
4. 取消使用 `CancellationToken`（语义等同 `AbortController`）；
5. **请求体编码由 `ContentType` 决定**（默认 JSON），不按 data 类型推断：字典不会自动转表单，需要表单时必须显式设 `ContentType = AxiosConfig.FormUrlEncodedContentType`；
6. 连接池是本库的扩展能力，axios（浏览器/XHR、Node http）没有同构实现；
7. 请求/响应文本编码可通过 `RequestEncoding` / `ResponseEncoding` 显式控制（axios 无对应配置项）；
8. `proxy` / `credentials` / `withCredentials` / `maxRedirects` / `poolOptions` 只能在实例级设置；在单次请求上写**与实例生效值不同**的值会**立即抛** `ERR_BAD_OPTION_VALUE`（axios 里是静默不生效，这里选择 fail-fast），写相同的值则等价于不写、正常放行；共享池 client 的 defaults 上设置同样立即抛——连接行为由池决定，应配置在 `ConnectionPoolOptions` 上。单次请求要摘掉某个实例默认头用 `config.RemoveHeaders.Add("X-Foo")`；
9. `maxRate`、`onUploadProgress` / `onDownloadProgress` 由库在 `HttpContent` 与 `Stream` 层自行实现（`HttpClient` 无原生支持），因此对自定义 `HttpContent` 的"已编码字节"统计以实际写入网络流的字节为准；
10. `transformRequest` / `transformResponse` 的类型是 `object?` 而非泛型链（C# 无法像 JS 那样丢失类型后再自由收窄），需要强类型时在委托内自行转换；
11. `config.method` 直接用 BCL 的 `HttpMethod`（本库没有自研枚举），因此任意自定义谓词都能发；axios 的 adapter 在 .NET 侧对应 `HttpMessageHandler`，接入点是 `ConnectionPoolOptions.HandlerFactory` 或 `new AxiosClient(handler: ...)`；
12. **请求头写入即校验**：头部名必须是 RFC 7230 token，取值不允许出现 C0 控制字符（`HTAB` 除外）与 `DEL`，否则 `AxiosHeaders.Set`（以及 `AxiosConfig.ContentType`）抛 `ArgumentException`（axios/JS 侧不做这层校验）。浏览器/Node 的 HTTP 栈会自行拆分 CRLF，.NET 的 `TryAddWithoutValidation` 不会——不校验就等于把请求拆分（CWE-113）的口子开着；
13. **`WithCredentials` 的 Cookie 容器按域共享**（整个池一份，不再每个 origin 一份），这样跨子域/换端口的会话与浏览器行为一致；
14. `Timeout` 上限约 49.7 天，超出会收敛到上限而不是抛异常（`Timeout = 0` 才是真正的无限超时）；
15. **`params` 用 RFC 3986 严格编码**（`Uri.EscapeDataString`），与 axios 默认 `encode()` 的字符集不同：空格是 `%20` 而不是 `+`，`!'()*` 与 `,:$` 也会被编码。这套编码对 OSS/S3 一类预签名反而更合适；需要 axios 的原始形态时传自定义 `ParamsSerializer`；
16. **multipart 的字段名/文件名发送前就校验**：`"`、`\`、`CR`/`LF` 与控制字符会破坏 `Content-Disposition` 的 quoted-string，直接抛 `ERR_BAD_OPTION_VALUE`（`FormFile` 构造时抛 `ArgumentException`），而不是让服务端静默收到错位的键名（浏览器 `FormData` 对这些字符的处理各不相同，axios 侧无法依赖）；
17. **`ContentType` 是 `string` 而非枚举**（`Content-Type` 由媒体类型 + `charset`/`boundary` 参数组成，枚举表达不了），常用值以 `AxiosContentType` 常量提供——与 BCL 的 `HttpMethod.Get` + `new HttpMethod("PROPFIND")` 是同一套「预置常量 + 任意自定义」思路。


## 示例项目

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) 是一个**开箱即用、离线可运行**的控制台示例：内置一个基于 `TcpListener` 的 HTTP/1.1 Keep-Alive 演示服务器，覆盖 24 类集成调用：

1. 全局静态门面 `Axios.GetAsync`
2. `Axios.Create`（baseURL/默认头/强类型响应）
3. POST 对象 JSON 往返
4. POST 字典表单
5. params 数组/嵌套序列化
6. HTTP Basic Auth
7. **dynamic 响应**：`GetAsync<dynamic>`（成员/嵌套/根数组，另支持 `ResponseType.Dynamic`）
8. **dynamic 请求**（ExpandoObject 作 body 与 params）
9. text / arraybuffer
10. 请求/响应拦截器（含 401/404 错误恢复）
11. 错误码、超时、取消、validateStatus
12. 连接池（连接复用、并发上限、多客户端共享）
13. **multipart/form-data**：字段 + 一堆文件（内存文件与本地文件，服务端逐部件精确解析）
14. **文件下载**：`GetAsync<Stream>` 流式落盘（1 MB 演示）、`GetAsync<byte[]>`、`Content-Disposition` 取文件名
15. **getUri 预览 / maxContentLength 拦截 / maxRedirects: 0**
16. **上传下载进度回调 + maxRate 限速**
17. **HttpMethod 直用 + 自定义 Handler 注入**（`PROPFIND` 自定义谓词、`DelegatingHandler` 直接注入、连接池 `HandlerFactory`）
18. **单文件上传**：裸流直传（`/upload/raw`）与 multipart 单文件（`/upload/single`），服务端精确回报字节数 + SHA256
19. **自定义 JSON 序列化**：实现 `IContentSerializer`，用 **Newtonsoft.Json** 替换 System.Text.Json（业务对象的请求体与强类型响应走同一引擎）
20. **DNS 变更与连接过期**：两个本地服务器模拟同一域名的旧/新地址，演示 `PooledConnectionLifetime` 到期的自然收敛，以及 `pool.Clear()` 的立即失效
21. **Bearer Token 认证**：登录签发、请求拦截器自动携带 `Authorization`、服务端验签端点，以及 401 后「刷新令牌 → 重试一次」的完整闭环（含登出吊销后刷新失败向上抛错）
22. **请求签名**：HMAC-SHA256(token + 时间戳 + nonce + 方法 + 路径 + 请求体哈希)，演示防篡改（改请求体）与防重放（nonce 一次性 + 时间戳窗口）
23. **XML 序列化（内置）**：换上 `XmlContentSerializer.Instance` —— 请求体、强类型响应、非泛型原文三条路径，标量请求体不经过序列化器，`AxiosContent.Register` 的自动模式（按 Content-Type 选序列化器），以及表单/multipart 与序列化器解耦
24. **二进制格式（字节通道双向）**：序列化器覆写 `SerializeToBytes` + 字节入口 —— 请求对象自动变字节正文并自报 `application/octet-stream`，响应零解码直达字节入口；对比非泛型 / `byte[]` 路径，并演示调用方显式 `ContentType` 覆盖自报头

```bash
dotnet run --project samples/AxiosNet.Simple
```

> 上面的第 13 项与第 18 项覆盖了上传：`DemoServer` 用 `MultipartReader` **按字节精确解析** multipart，回报每个部件的字段名、文件名、Content-Type、字节数与 SHA256；单文件场景还提供 `/upload/raw`（裸流）与 `/upload/single`（multipart 单文件）两个端点。
>
> 第 19 项是唯一需要额外 NuGet 包（`Newtonsoft.Json`）的示例，仅用于演示**引擎替换**——类库本身仍然零第三方运行时依赖；不需要自定义序列化时可以把 `Newtonsoft.Json` 从示例项目里删掉。

## 目标框架与运行时支持

类库是**单目标 `net6.0`**，一份程序集覆盖 .NET 6 及以上的全部运行时：

| 消费方目标框架 | 结果 |
| --- | --- |
| `net6.0` / `net7.0` / `net8.0` / `net9.0` / `net10.0` / `net11.0` … | 直接引用 `lib/net6.0`，无需任何配置 |

- **为什么一份就够**：.NET 只保证“高版本运行时能加载低版本程序集”这一个方向，而库用到的 API 在 6.0 已全部存在，没有任何 7/8/9 专属调用，所以没必要为每个运行时重复编译。像 `System.Text.Json` 这类共享框架程序集，在 .NET 8 应用里运行时自动使用 8.x 版本，不是把 6.x 一起带过去。
- **不支持 .NET Framework**，也不支持 .NET Core 3.1 及更早：连接池直接建立在 `SocketsHttpHandler` 上（`PooledConnectionLifetime` / `PooledConnectionIdleTimeout` / `ConnectTimeout` / `SslOptions` / `HandlerFactory`），`netstandard2.0` 的 BCL 契约里没有这个类型；3.1 又缺 `DateOnly`/`TimeOnly`、`ReadAsStreamAsync(CancellationToken)`、`ConcurrentDictionary.TryRemove(KeyValuePair)` 等一批 API，补兼容层的改动量与收益不成比例。
- **为什么下限是 .NET 6**：`DateOnly` / `TimeOnly`、`HttpContent.SerializeToStreamAsync(Stream, TransportContext, CancellationToken)` 都是 6.0 前后才补齐的；再往下要多维护一层 `#if`，而 .NET 5 已停止支持。当前源码**零 `#if`**。
- **验证方式**：测试项目仍是 `net8.0` + `net6.0` 双目标，同一份出厂程序集在两种运行时上各跑 576 个用例（共 1152 次执行，全部通过）。这样恰好覆盖最常见的消费形态——.NET 8 应用加载 `net6.0` 资产。

```xml
<!-- 消费方无需任何配置：项目目标框架 >= net6.0 即可引用 -->
<PackageReference Include="AxiosNet" Version="1.0.1" />
```

## 构建与测试

```bash
# 构建（Debug）：类库产出单份 net6.0 资产
dotnet build AxiosNet.slnx

# 运行全部 576 个 xUnit 单元测试（测试项目为 net8.0 + net6.0 双目标，实际执行 1152 次）
dotnet test AxiosNet.slnx

# Release
dotnet build AxiosNet.slnx -c Release
```

测试项目使用自研的轻量 HTTP/1.1 Keep-Alive 测试服务器（`TcpListener`，零 NuGet 依赖），可直接观测真实 TCP 连接复用数与峰值并发，覆盖：

- 基础请求/全方法/重定向/HEAD/BaseAddress/静态门面
- params 序列化、请求体编码（`ContentType` 决定 JSON/表单/multipart/透传）、全方法携带 body、Basic Auth、默认头
- `ContentType` 与数据类型解耦的边界用例（字典默认 JSON、显式表单、multipart 文件/流/字节、根数组文件、固定边界）
- 请求/响应拦截器（顺序、Eject、异步、错误恢复）
- 4xx/5xx/超时/取消/网络错误/validateStatus
- `GetUri`（相对/绝对、数组与嵌套 params、中文与空格转义、自定义 serializer）
- `maxContentLength` / `maxBodyLength` / `maxRedirects: 0` / 错误模型（`Status` / `Cause` / `ToJson` / `IsAxiosError` / `IsCancel`）
- 进度回调（上传/下载、100% 唯一性）与 `maxRate` 限速（上/下行）
- `paramsSerializer` / `formFieldNameSelector` / `transformRequest` / `transformResponse` / `PostForm*` 快捷方法
- 文件下载：`GetAsync<Stream>` 落盘、`ResponseType.ArrayBuffer`、流释放后连接复用、Content-Disposition 文件名、下载超时
- dynamic 响应图与 dynamic 请求（9 个专项测试，覆盖 GetAsync&lt;dynamic&gt; / PostAsync&lt;dynamic&gt; / 静态门面 / 显式 responseType 优先）
- 连接池：连接复用、最大连接数限流、跨实例共享、空闲驱逐、多 origin 隔离、Dispose 语义、共享池 fail-fast（连接级 defaults 与 `pool`/`poolOptions` 互斥）、origin key IDNA 归一（unicode/punycode 同节点、IPv6 括号保留）、`pool.Options` 选项隔离
- 拦截器：快照缓存（无变更复用）与 Use/Eject/Clear 即时失效
- 响应读取：无 Content-Length 的关闭 delimiting 响应（缓冲读取未知长度分支）、精确预分配时零拷贝返回
- 泛型路由：`GetAsync<Stream>` 与显式 `responseType` 不匹配时统一包装为 `ERR_BAD_RESPONSE`（不漏裸 `InvalidCastException`）
- 自定义 Handler：`PROPFIND` 等自定义 `HttpMethod` 透传、`DelegatingHandler` 直接注入（含 disposeHandler 语义）、`HandlerFactory` 按 origin 调用一次 / 包装默认 Handler / 返回 null 时抛 `InvalidOperationException`、与 pool/poolOptions 及实例级选项的 fail-fast
- 故障与并发回归（`ReviewRegressionTests`）：流式 4xx 释放连接、截断响应 / 非 JSON dynamic 的错误码、并发修改默认请求头、params 索引器、`Timeout = 0` 覆盖、流式正文超时、下载期间租约不被驱逐
- 序列化可插拔（`JsonCustomizationTests` / `ContentSerializerTests`）：自定义 `IContentSerializer` 驱动请求/响应、实例级 `JsonSerializerOptions`、transform 列表并发增删；`IContentSerializer` 的 XML 端到端、基础类型不经过序列化器、非泛型 `JsonElement`/原文、表单与序列化器解耦、XML 下 dynamic 明确失败、二进制响应走字节入口（非 UTF-8 尾字节原样到达、零解码）与文本序列化器在 `octet-stream` 下的默认兜底、二进制请求出口（对象 → 字节正文 + 自报 `MediaType`、不补 charset、显式 `ContentType` 优先、表单/标量不进字节通道）、内置 XML 序列化器（UTF-8 声明 / 无命名空间噪音 / `[XmlRoot]` 命名空间 / 拒绝 DTD）、`JsonDocument` 目标与序列化器绑定的边界、标量请求体与字典归属、序列化器自报 `MediaType` 回填请求头（显式/实例默认 ContentType 优先、默认 null 零破坏）
- 安全与加固（`HardeningTests`）：请求头 CRLF/控制字符与非法名称拦截（防 HTTP 请求拆分）、`poolOptions.MaxAutomaticRedirections` 不被未显式设置的默认值覆盖、流式正文截断与 `Timeout=0` 下的统一 `ERR_NETWORK`、超大 `Timeout` 收敛到 49.7 天、负值限制项 fail-fast、params 落在 fragment 之前、`PostAsync(url, null)` 的 `ArgumentNullException`、流式与 chunked 的 `maxContentLength`、跨 origin 共享 Cookie、取消异常保留 `Cause`、params 传 `JsonElement`、params 传 `byte[]`（base64）、池 origin 上限与优雅 `Dispose`（在途请求不被关闭打断、关闭竞态不漏裸 `ObjectDisposedException`）

## 整体流程

一次请求从调用到拿到 `AxiosResponse`，在库里按顺序经过 9 个阶段。贯穿管线的是**同一个生效配置**（实例 `Defaults` 与按请求项合并而来）——拦截器改写的 `Headers`、`transformRequest` 替换的 `Data`、序列化器写回的请求头，作用的都是它，这也是它们能对最终请求生效的原因。

```text
调用方
                                │  Axios.GetAsync / client.GetAsync<T> / RequestAsync
                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ① 入口与配置合并                                             │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ 快捷方法组装 AxiosConfig（url / method / data）              │
│ ⊕ 合并实例 Defaults → 生效配置                               │
│ 选项校验：连接级 / 负值 fail-fast（ERR_BAD_OPTION_VALUE）    │
└──────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ② 请求拦截器 Interceptors.Request                            │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ 逆序执行 · 可异步 · 可改 Headers                             │
└──────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ③ TransformRequest 链                                        │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ 拦截器之后、编码之前变换 Data                                │
└──────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ④ URL 组装 + 请求体构造                                      │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ UrlBuilder：baseURL ⊕ url ⊕ params 序列化                    │
│ RequestBodyFactory 按 ContentType 分派：                     │
│   对象/字典/dynamic/集合 → SerializeToBytes 或 Serialize     │
│     （字节通道优先，null 回落文本；头取 MediaType 自报）     │
│   标量 → 直取文本（不经过序列化器）                          │
│   表单/multipart → 库内组装（不经过序列化器）                │
│   string/byte[]/Stream/HttpContent → 原样透传                │
│ 上传进度/限速 → ProgressHttpContent 包装                     │
└──────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ⑤ 应用请求头 + 获取连接                                      │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ 默认头 ⊕ RemoveHeaders ⊕ 序列化器自报 MediaType              │
│ 默认：pool.LeaseAsync 按 origin 租用                         │
│   （TCP 复用 · 并发上限 · 空闲驱逐）                         │
│ 注入 handler：固定 HttpClient（不走池）                      │
└──────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ⑥ 发送                                                       │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ HttpClient.SendAsync(ResponseHeadersRead)                    │
│ SocketsHttpHandler：Keep-Alive / 重定向 / 自动解压           │
└──────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ⑦ 响应读取 ResponseReader                                    │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ validateStatus 判定一次（决定宽松解析 / 抛错）               │
│ 响应头 → config.Response（序列化器/transform 可读）          │
│ Stream：包装流直接交出，租约随流转移                         │
│ 缓冲：读全量字节 → 按需解码 → ConvertData 分派               │
│   直给 Stream/byte[]/string · 标量解析 · dynamic 展开        │
│   JsonElement · 业务对象 → IContentSerializer.Deserialize    │
└──────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ⑧ TransformResponse 链 → 响应拦截器                          │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ 反序列化之后加工 Data                                        │
│ 响应拦截器按注册顺序执行；rejected 分支可恢复错误            │
└──────────────────────────────────────────────────────────────┘
                                │
                                ▼
┌──────────────────────────────────────────────────────────────┐
│ ⑨ 收尾                                                       │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ 非流式：释放响应消息 + 租约归还连接池                        │
│ 流式：TimeoutStream 持有租约，流释放时归还                   │
│ 泛型：DataCast 校验 Data 与 T 兼容                           │
└──────────────────────────────────────────────────────────────┘
                                │
                                ▼
                                AxiosResponse / AxiosResponse<T>

任意阶段失败 → AxiosException（统一错误模型：ERR_* 错误码 + Cause 底层异常）
              → 进入响应拦截器 rejected 链：可刷新令牌重放，或返回兜底响应
```

各阶段与源码文件的对应关系（配合下方[项目结构](#项目结构)一起看）：

| 阶段 | 关键源码 |
| --- | --- |
| ① 入口与配置合并 | `AxiosClient.Shortcuts.cs`（组装 config）、`AxiosClient.Config.cs`（`MergeConfig` / `ApplyHeaders`） |
| ② 请求拦截器 | `Interceptors/RequestInterceptorManager.cs` |
| ③ TransformRequest | `AxiosClient.cs` 请求管线 |
| ④ URL + 请求体 | `Internal/UrlBuilder.cs`、`Internal/RequestBodyFactory.cs`（分派）、`Internal/FormBodyEncoder.cs`（表单）、`Internal/MultipartBodyBuilder.cs`（multipart）、`Internal/ProgressHttpContent.cs` |
| ⑤ 请求头 + 连接 | `AxiosClient.Config.cs`、`Pooling/HttpConnectionPool.cs`（`LeaseAsync`）、`Pooling/PooledEndpoint.cs` |
| ⑥ 发送 | `AxiosClient.cs`（`SendAsync`）→ 每 origin 的 `HttpClient` |
| ⑦ 响应读取 | `Internal/ResponseReader.cs`、`Internal/ResponseTextDecoder.cs`、`Internal/ScalarParser.cs`、`Internal/DynamicJson.cs`、`Serialization/` |
| ⑧ TransformResponse + 响应拦截器 | `Internal/ResponseReader.cs`、`Interceptors/ResponseInterceptorManager.cs` |
| ⑨ 收尾 | `Internal/ResponseStreams.cs`（`TimeoutStream` / `ResponseStreamOwner`）、`Internal/DataCast.cs` |

> 失败路径与成功路径同样经过这条链：任何阶段抛出的异常都会归一到 `AxiosException`（`ERR_*` 错误码 + `Cause` 底层异常），并进入响应拦截器的 rejected 链——可以在那里刷新令牌后重放，或返回兜底响应（见[拦截器](#拦截器)、[错误处理](#错误处理)）。

## 项目结构

```
.
├─ AxiosNet.slnx
├─ src/
│  └─ AxiosNet/                  # 类库（单目标 net6.0，兼容 .NET 6 及以上全部运行时）
│     ├─ Axios.cs                # 静态门面（对应全局 axios）
│     ├─ AxiosClient.cs          # 实例 + 请求管线
│     ├─ AxiosClient.Shortcuts.cs# 全部快捷方法重载（get/post/... + form）
│     ├─ AxiosClient.Config.cs   # 配置合并与请求头应用
│     ├─ AxiosJson.cs            # JSON 全局默认（JsonSerializerOptions）
│     ├─ AxiosContent.cs         # 内容序列化全局注入点（AxiosContent.Serializer）
│     ├─ AxiosConfig.cs          # 请求/实例配置
│     ├─ AxiosContentType.cs     # 常用 Content-Type 预置常量
│     ├─ AxiosUrl.cs             # 路径段转义（AxiosUrl.EncodePathSegment）
│     ├─ AxiosHeaders.cs         # 大小写不敏感请求头
│     ├─ AxiosResponse.cs        # 响应（泛型/非泛型）
│     ├─ AxiosResponseInfo.cs    # 反序列化阶段的只读响应上下文（状态/头/媒体类型）
│     ├─ AxiosException.cs       # AxiosError 对齐
│     ├─ AxiosProgressEvent.cs   # 上传/下载进度事件
│     ├─ FormFile.cs             # multipart 文件部件
│     ├─ ResponseType.cs / AxiosAuth.cs          # 响应形态 / Basic 认证
│     ├─ Interceptors/           # 请求/响应拦截器管理
│     ├─ Serialization/          # IContentSerializer（2 个文本方法 + 一对字节通道 + MediaType）+ 内置 JSON / XML 实现
│     ├─ Internal/               # URL/params、请求体分派 + 表单/multipart 编码、响应读取、流量计量、动态 JSON、反射缓存、媒体类型判定、状态码文本
│     └─ Pooling/                # HttpConnectionPool 连接池 + PooledHandlerContext（Handler 注入）
├─ samples/
│  └─ AxiosNet.Simple/           # 24 类集成调用示例（内置演示服务器 + multipart 精确解析 + Newtonsoft.Json / XML / 二进制序列化器 + DNS 刷新 + Bearer 令牌 + 请求签名）
└─ test/
   └─ AxiosNet.Tests/            # xUnit 单元测试（576 个用例 × net6.0 / net8.0 双目标 = 1152 次执行）
```

## License

[MIT](LICENSE)
