# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/nuget/v/AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![language](https://img.shields.io/badge/C%23-12-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![tests](https://img.shields.io/badge/tests-153%20passed-success)](#构建与测试)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

用 .NET (C#) 实现的 **axios 风格 HTTP 客户端**，API 设计与 JS 生态的 [axios](https://github.com/axios/axios) 一一对应；并在 .NET 原生 `SocketsHttpHandler` 连接复用之上，额外提供一层**按 origin 隔离的网络连接池**（连接复用、并发上限、空闲驱逐、多实例共享、可观测统计）。

- 零第三方依赖，仅使用 .NET BCL
- 目标框架：`net8.0`
- 异步优先（`Task` / `CancellationToken` / `ConfigureAwait(false)`）

---

## 目录

- [特性](#特性)
- [安装](#安装)
- [快速开始](#快速开始)
- [axios ↔ AxiosNet 对照表](#axios--axiosnet-对照表)
- [配置项](#配置项)
- [请求体 data 与查询参数 params](#请求体-data-与查询参数-params)
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
- [示例项目](#示例项目)
- [构建与测试](#构建与测试)
- [项目结构](#项目结构)
- [与 axios 的差异说明](#与-axios-的差异说明)
- [License](#license)

---

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
- `transformRequest` / `transformResponse`：请求前、响应后数据变换链
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

## axios ↔ AxiosNet 对照表

| axios (JS) | AxiosNet (.NET) |
| --- | --- |
| `axios(config)` | `client.RequestAsync(new AxiosConfig { ... })` |
| `axios(url, config)` | `client.GetAsync(url, c => { ... })` |
| `config.method` | `c.Method`（`System.Net.Http.HttpMethod`，默认 `HttpMethod.Get`） |
| `axios.get/post/put/patch/delete/head/options` | `GetAsync / PostAsync / PutAsync / PatchAsync / DeleteAsync / HeadAsync / OptionsAsync` |
| `axios.create(config)` | `Axios.Create(config)` |
| `axios.getUri(config)` | `client.GetUri(config)` / `client.GetUri(c => ...)` / `Axios.GetUri(...)` |
| `axios.postForm / putForm / patchForm` | `PostFormAsync / PutFormAsync / PatchFormAsync`（默认 multipart） |
| `axios.defaults` | `Axios.Defaults` / `client.Defaults` |
| `res.data / status / statusText / headers / config` | `Data / Status / StatusText / Headers / Config` |
| `interceptors.request.use(ok, err)` | `Interceptors.Request.Use(...)`（同步/异步重载） |
| `interceptors.response.use(...)` | `Interceptors.Response.Use(...)` |
| `interceptors.x.eject(id)` | `Eject(id)` |
| `config.params` | `c.Params`（对象/字典/数组/嵌套） |
| `config.data`（对象→JSON） | `c.Data`（任意类型；编码方式由 `c.ContentType` 决定，默认 JSON） |
| `config.ContentType` | 请求体媒体类型（默认 `application/json`，可传含 charset 的完整值；决定 data 的编码格式） |
| `responseType: 'json'/'text'/'arraybuffer'/'stream'` | `ResponseType.Json/Text/ArrayBuffer/Stream` |
| —（JS 天然动态） | `GetAsync<dynamic>` / `ResponseType.Dynamic` |
| —（无对应配置） | `RequestEncoding` / `ResponseEncoding`（请求/响应文本编码） |
| `validateStatus` | `c.ValidateStatus = status => ...` |
| `timeout` / `AbortController` | `c.Timeout` / `c.CancellationToken` |
| `auth: {username,password}` | `c.Auth = new AxiosAuth(user, pass)` |
| `withCredentials` / `proxy` / `maxRedirects` | 同名属性（**仅实例级**生效，见下） |
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
    ParamsSerializer = p => MyQuery(p),       // Func<object?, string>：自定义 ?a=1&b=2
    FormFieldNameSelector = (prefix, name) => string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}",
    // TransformRequest / TransformResponse：IList<Func<object?, AxiosConfig, object?>>

    // Proxy / Credentials / HttpVersion / JsonSerializerOptions / PoolOptions ...
};
```

> 进度与限速的小节见 [进度回调、限速与大小限制](#进度回调限速与大小限制)。

默认 JSON 选项为 **Web 风格**（输出 camelCase、输入大小写不敏感），与 JS/axios 生态习惯一致；可用 `c.JsonSerializerOptions` 覆盖。

> `withCredentials` / `proxy` / `credentials` / `maxRedirects` 属于**实例级**配置：连接池 Handler 在构造 `AxiosClient` 时创建，因此这些项只在实例默认值上生效。为避免"设了却没生效"的静默错误，在**单次请求**上显式设置这四项会立即抛出 `AxiosException`（`ERR_BAD_OPTION_VALUE`），提示改为在 `Axios.Create(...)` 的实例 defaults 中设置。
>
> 需要按请求生效的项（`timeout`、`headers`、`params`、`responseType`、`maxContentLength`、`maxRate`、`onUploadProgress`/`onDownloadProgress`、`transformRequest`/`transformResponse`、`validateStatus`、`auth`、`cancellationToken`、`RequestEncoding`/`ResponseEncoding` 等）不受此限制；其中 `cancellationToken` 既可设在实例默认，也可按请求传入，按请求传入时以后者为准。

## 请求体 data 与查询参数 params

请求体的编码格式**只由 `ContentType` 决定，与 `data` 的运行时类型无关**：字典不会被自动当成表单，对象也不会因为实现了某个接口而改变编码方式。

| `ContentType` | 结构化数据（对象 / 字典 / dynamic / 集合） | `string` / `byte[]` / `Stream` / `HttpContent` |
| --- | --- | --- |
| `application/json`（默认） | JSON 文本 | 原样发送 |
| `application/x-www-form-urlencoded` | 键值对表单（嵌套 `a[b]=v`、数组 `a[]=v`、null 跳过） | 原样发送（视为已编码好的表单串） |
| `multipart/form-data` | 字段 + 文件部件（`FormFile` / `Stream` / `byte[]` / `HttpContent`，数组展开为同名重复部件） | 原样发送（也可直接传 `MultipartFormDataContent`） |
| 其他（`text/plain`、`application/xml` …） | 仍写 JSON 文本，只有头部不同 | 原样发送 |

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
```

> 默认媒体类型是 JSON，因此发送二进制载荷时请显式设置 `c.ContentType = "application/octet-stream"`（库不会根据 `byte[]`/`Stream` 猜测类型）。

multipart 补充说明：

- **字段名**：取所在对象的属性名或字典的键（遵循 `JsonSerializerOptions.PropertyNamingPolicy`，并识别 `[JsonPropertyName]` / `[JsonIgnore]`）；嵌套对象写成 `meta[author]`；数组/集合展开为**同名重复部件**（ASP.NET Core `IFormFileCollection`、FastAPI、PHP 都能直接绑定）。
- **根节点是文件数组**时没有键名，用 `FormFile.FieldName` 指定部件名：`new[] { new FormFile(...) { FieldName = "files" } }`。
- **文件名**：`FormFile.FileName` 决定 `filename`；直接用 `Stream`/`byte[]` 也会作为文件部件发送（文件名回退为字段名）。
- **边界**：默认自动生成；`c.ContentType = "multipart/form-data; boundary=----xxx"` 可指定固定边界。
- **生命周期**：部件内容随请求释放（成功/异常/取消都会 `Dispose`），`File.OpenRead(...)` 的流不要在请求之外复用；需要复用时先读成 `byte[]`。

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

// 2) 非泛型：Data 为 JsonElement（非法 JSON 时自动回退为原文本，对齐 axios transformResponse）
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
- 下载超时/取消与普通请求一致：`c.Timeout` / `c.CancellationToken`，分别抛 `ECONNABORTED` / `ERR_CANCELED`；并发下载量大时注意连接池 `MaxConnectionsPerServer`。
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
- 非泛型：合法 JSON 为 `JsonElement`，非法 JSON 原样返回文本（对齐 axios `transformResponse`）。

### 内容编码

- **请求体**：`RequestEncoding` 决定写入的字节（默认 UTF-8），显式设置时同时写入 `Content-Type` 的 `charset`；`byte[]` / `Stream` / `HttpContent` 原样透传，不受该项影响。
- **响应体**：默认自动识别 —— `Content-Type` 的 `charset` → BOM → UTF-8；`ResponseEncoding` 可强制指定，用于服务端 charset 缺失或错误的情况。

```csharp
// 请求按 GBK 发送
await http.PostAsync("legacy", "中文", c => c.RequestEncoding = Encoding.GetEncoding("gbk"));

// 响应强制按 GBK 解码
await http.GetAsync("legacy", c => c.ResponseEncoding = Encoding.GetEncoding("gbk"));
```

> .NET Core 上使用 GBK 等代码页编码需先注册 `CodePagesEncodingProvider`，否则 `Encoding.GetEncoding("gbk")` 会抛异常；此时自动识别会静默回退到 BOM/UTF-8。

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

- `MaxContentLength` 对普通（缓冲）响应是**边读边累计**：越界立即中止并释放连接，不会先把 8 MB 全读进内存再判断；对 `ResponseType.Stream` 则在拿到响应头时按声明的 `Content-Length` 判断，声明超限直接失败（未声明长度时不做额外拦截）。
- `MaxBodyLength` 在发送前按 `Content.Headers.ContentLength` 判断，只对能算出长度的请求体生效（JSON / 表单 / `byte[]` / `FormFile`）；自定义的未知长度 `HttpContent` 不做拦截。

## 自定义序列化与数据转换

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
| `ERR_BAD_REQUEST` | 响应状态码为 4xx（默认 validate 失败）；请求体超过 `MaxBodyLength` | ✅ |
| `ERR_BAD_RESPONSE` | 响应状态码为 5xx；响应体无法反序列化为目标类型；响应体超过 `MaxContentLength` | ✅ |
| `ECONNABORTED` | 请求超过 `Timeout`（消息可用 `TimeoutMessage` 覆盖） | ✅ |
| `ERR_NETWORK` | 连接被拒绝/DNS 失败等网络层异常 | ✅ |
| `ERR_CANCELED` | `CancellationToken` 取消 | ✅ |
| `ERR_INVALID_URL` | URL 非法（含相对 URL 且未提供 `baseURL`） | ✅ |
| `ERR_BAD_OPTION_VALUE` | 在单次请求上设置了仅实例级生效的项（`proxy` / `credentials` / `withCredentials` / `maxRedirects`） | ✅（同码） |

## 超时与取消

```csharp
// 单次请求超时
await client.GetAsync("slow", c => c.Timeout = TimeSpan.FromMilliseconds(500));

// 外部取消（等同 AbortController）
using var cts = new CancellationTokenSource();
var task = client.GetAsync("slow", cancellationToken: cts.Token);
cts.CancelAfter(TimeSpan.FromSeconds(1));
```

## 网络连接池

### 为什么需要

.NET 的 `SocketsHttpHandler` 本身已经能复用 TCP 连接，但错误地“每次请求 new HttpClient/Handler”会导致 socket 耗尽，而全局唯一 `HttpClient` 又无法按目标站点隔离调优、无法及时回收。`HttpConnectionPool` 在此之上提供：

1. **按 origin 复用**：每个 `scheme://host:port` 对应一个调优过的 `SocketsHttpHandler`，全部请求共享，TCP 长连接复用（Keep-Alive）；
2. **连接数限流**：`MaxConnectionsPerServer` 控制单站点最大 TCP 连接数，超出的请求在连接层排队，高并发下吞吐更稳定；
3. **生命周期治理**：池化连接最大存活时间（平滑 DNS 变更）、空闲超时、建连超时；
4. **空闲 origin 驱逐**：后台定时器自动释放长期无流量站点的 Handler；
5. **多实例共享**：多个 `AxiosClient`（例如对应不同业务模块）可以共享同一个池；
6. **可观测**：`GetStatistics()` 输出 origin 节点数、累计租约、每节点活跃租约等。

### 使用方式

```csharp
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
});

// 2) 推荐：全局共享一个池给多个客户端
await using var sharedPool = new HttpConnectionPool(new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 64
});
using var orderClient  = new AxiosClient(pool: sharedPool);
using var payClient    = new AxiosClient(pool: sharedPool);

// 3) 观察池状态
ConnectionPoolStatistics stats = sharedPool.GetStatistics();
// stats.EndpointCount / TotalLeases / Endpoints[*].ActiveLeases ...

// 手动驱逐空闲 origin（也会由后台定时器自动执行）
sharedPool.EvictIdleEndpoints();
```

> 性能要点：顺序/并发请求都会复用已建立的 TCP 连接（仓库测试与示例实测：5 个顺序请求只建立 1 条 TCP；6 个并发慢请求在 `MaxConnectionsPerServer=2` 下峰值并发恰好为 2）。应用关闭时 `Dispose()` 池即可释放全部连接；共享池不会因为单个 client 释放而被销毁（仅池拥有者负责释放）。

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

## 示例项目

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) 是一个**开箱即用、零外部依赖**的控制台示例：内置一个基于 `TcpListener` 的 HTTP/1.1 Keep-Alive 演示服务器，离线即可运行，覆盖 18 类集成调用：

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

```bash
dotnet run --project samples/AxiosNet.Simple
```

> 上面的第 13 项与第 18 项覆盖了上传：`DemoServer` 用 `MultipartReader` **按字节精确解析** multipart，回报每个部件的字段名、文件名、Content-Type、字节数与 SHA256；单文件场景还提供 `/upload/raw`（裸流）与 `/upload/single`（multipart 单文件）两个端点。

## 构建与测试

```bash
# 构建（Debug）
dotnet build AxiosNet.slnx

# 运行全部 153 个 xUnit 单元测试
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
- 连接池：连接复用、最大连接数限流、跨实例共享、空闲驱逐、多 origin 隔离、Dispose 语义
- 自定义 Handler：`PROPFIND` 等自定义 `HttpMethod` 透传、`DelegatingHandler` 直接注入（含 disposeHandler 语义）、`HandlerFactory` 按 origin 调用一次 / 包装默认 Handler / 返回 null、与 pool/poolOptions 及实例级选项的 fail-fast

## 项目结构

```
.
├─ AxiosNet.slnx
├─ src/
│  └─ AxiosNet/                  # 类库
│     ├─ Axios.cs                # 静态门面（对应全局 axios）
│     ├─ AxiosClient.cs          # 实例 + 请求管线 + 快捷方法
│     ├─ AxiosConfig.cs          # 请求/实例配置
│     ├─ AxiosHeaders.cs         # 大小写不敏感请求头
│     ├─ AxiosResponse.cs        # 响应（泛型/非泛型）
│     ├─ AxiosException.cs       # AxiosError 对齐
│     ├─ AxiosProgressEvent.cs   # 上传/下载进度事件
│     ├─ FormFile.cs             # multipart 文件部件
│     ├─ ResponseType.cs / AxiosAuth.cs          # 响应形态 / Basic 认证
│     ├─ Interceptors/           # 请求/响应拦截器管理
│     ├─ Internal/               # URL/params、请求体工厂、进度与限速流、dynamic JSON、状态码文本
│     └─ Pooling/                # HttpConnectionPool 连接池 + PooledHandlerContext（Handler 注入）
├─ samples/
│  └─ AxiosNet.Simple/           # 18 类集成调用示例（内置演示服务器 + multipart 精确解析）
└─ test/
   └─ AxiosNet.Tests/            # xUnit 单元测试（153 个）
```

## 与 axios 的差异说明

受 .NET 平台特性影响的有意差异：

1. 方法命名为 .NET 习惯的 `XxxAsync`（`GetAsync` 对应 `axios.get`）；
2. `timeout` 使用 `TimeSpan` 而非毫秒数；
3. 非泛型 JSON 响应的 `Data` 是 `JsonElement`（需要 JS 式动态访问时使用泛型 `GetAsync<dynamic>`）；
4. 取消使用 `CancellationToken`（语义等同 `AbortController`）；
5. **请求体编码由 `ContentType` 决定**（默认 JSON），不按 data 类型推断：字典不会自动转表单，需要表单时必须显式设 `ContentType = AxiosConfig.FormUrlEncodedContentType`；
6. 连接池是本库的扩展能力，axios（浏览器/XHR、Node http）没有同构实现；
7. 请求/响应文本编码可通过 `RequestEncoding` / `ResponseEncoding` 显式控制（axios 无对应配置项）；
8. `proxy` / `credentials` / `withCredentials` / `maxRedirects` 只能在实例级设置；在单次请求上设置会**立即抛** `ERR_BAD_OPTION_VALUE`（axios 里是静默不生效，这里选择 fail-fast）；
9. `maxRate`、`onUploadProgress` / `onDownloadProgress` 由库在 `HttpContent` 与 `Stream` 层自行实现（`HttpClient` 无原生支持），因此对自定义 `HttpContent` 的"已编码字节"统计以实际写入网络流的字节为准；
10. `transformRequest` / `transformResponse` 的类型是 `object?` 而非泛型链（C# 无法像 JS 那样丢失类型后再自由收窄），需要强类型时在委托内自行转换；
11. `config.method` 直接用 BCL 的 `HttpMethod`（本库没有自研枚举），因此任意自定义谓词都能发；axios 的 adapter 在 .NET 侧对应 `HttpMessageHandler`，接入点是 `ConnectionPoolOptions.HandlerFactory` 或 `new AxiosClient(handler: ...)`。


## License

[MIT](LICENSE)
