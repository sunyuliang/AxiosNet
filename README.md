# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![language](https://img.shields.io/badge/C%23-12-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![tests](https://img.shields.io/badge/tests-56%20passed-success)](#构建与测试)
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
- [响应类型 responseType（含 dynamic）](#响应类型-responsetype含-dynamic)
- [拦截器](#拦截器)
- [错误处理](#错误处理)
- [超时与取消](#超时与取消)
- [网络连接池](#网络连接池)
- [示例项目](#示例项目)
- [构建与测试](#构建与测试)
- [项目结构](#项目结构)
- [与 axios 的差异说明](#与-axios-的差异说明)
- [发布到 GitHub](#发布到-github)
- [License](#license)

---

## 特性

**与 axios 对齐**

- `axios.create(config)` → `Axios.Create(config)`，静态门面 `Axios.GetAsync(...)`
- 快捷方法：`Get / Post / Put / Patch / Delete / Head / Options`
- `baseURL + url` 合并、`params` 默认序列化（数组 `a[]=1&a[]=2`、嵌套 `o[k]=v`、null 跳过）
- `data` 自动转换：对象 → JSON，字典 → `x-www-form-urlencoded`，`string/byte[]/Stream/MultipartFormDataContent` 透传
- 请求/响应拦截器（同步 & 异步、`Eject`、请求拦截器逆序执行、响应拦截器顺序执行、错误分支可恢复）
- `validateStatus` 自定义合法状态码
- `timeout`（`ECONNABORTED`）、外部取消（`ERR_CANCELED`）、网络错误（`ERR_NETWORK`）
- 错误码与 axios 对齐：`ERR_BAD_REQUEST`(4xx) / `ERR_BAD_RESPONSE`(5xx)
- `auth`（HTTP Basic）、`withCredentials`、代理、自动重定向、gzip/deflate/br 自动解压
- 完整响应结构：`Data / Status / StatusText / Headers / Config / Request`

**额外能力**

- 🔌 **网络连接池** `HttpConnectionPool`：按 `scheme://host:port` 池化并复用 `SocketsHttpHandler`，`MaxConnectionsPerServer` 限流、连接生命周期与空闲回收、后台空闲 origin 驱逐、多客户端共享同一池、租约统计
- 🧩 **dynamic 支持**：`GetAsync<dynamic>` / `ResponseType.Dynamic` 直接 `data.address.city`、`data.items[0].id`，无需定义 DTO（对标 JS 里直接访问 `response.data.xxx`）
- 📦 泛型强类型响应 `GetAsync<T>` 与非泛型 `JsonElement` 响应并存

## 安装

### 方式一：引用项目（当前仓库）

```bash
dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj
```

### 方式二：源码引入

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
```

## axios ↔ AxiosNet 对照表

| axios (JS) | AxiosNet (.NET) |
| --- | --- |
| `axios(config)` | `client.RequestAsync(new AxiosConfig { ... })` |
| `axios(url, config)` | `client.GetAsync(url, c => { ... })` |
| `axios.get/post/put/patch/delete/head/options` | `GetAsync / PostAsync / PutAsync / PatchAsync / DeleteAsync / HeadAsync / OptionsAsync` |
| `axios.create(config)` | `Axios.Create(config)` |
| `axios.defaults` | `Axios.Defaults` / `client.Defaults` |
| `res.data / status / statusText / headers / config` | `Data / Status / StatusText / Headers / Config` |
| `interceptors.request.use(ok, err)` | `Interceptors.Request.Use(...)`（同步/异步重载） |
| `interceptors.response.use(...)` | `Interceptors.Response.Use(...)` |
| `interceptors.x.eject(id)` | `Eject(id)` |
| `config.params` | `c.Params`（对象/字典/数组/嵌套） |
| `config.data`（对象→JSON） | `c.Data`（对象→JSON，字典→form） |
| `responseType: 'json'/'text'/'arraybuffer'/'stream'` | `ResponseType.Json/Text/ArrayBuffer/Stream` |
| —（JS 天然动态） | `GetAsync<dynamic>` / `ResponseType.Dynamic` |
| `validateStatus` | `c.ValidateStatus = status => ...` |
| `timeout` / `AbortController` | `c.Timeout` / `c.CancellationToken` |
| `auth: {username,password}` | `c.Auth = new AxiosAuth(user, pass)` |
| `withCredentials` / `proxy` / `maxRedirects` | 同名属性 |
| `AxiosError.code` | `AxiosException.Code`（同名字符串） |
| （无内置） | `HttpConnectionPool` / `ConnectionPoolOptions` |

## 配置项

```csharp
var config = new AxiosConfig
{
    BaseAddress = "https://api.example.com/", // 等同 baseURL
    Url = "users",                            // 相对或绝对地址
    Method = AxiosMethod.Post,                // 默认 Get
    Headers = new AxiosHeaders(),             // 大小写不敏感
    Params = new { page = 1 },                // -> ?page=1
    Data = new { name = "bob" },              // 对象自动 JSON
    Timeout = TimeSpan.FromSeconds(10),       // 0/Zero = 不超时
    Auth = new AxiosAuth("user", "pass"),     // Basic
    ResponseType = ResponseType.Json,         // Json/Text/ArrayBuffer/Stream/Dynamic
    ValidateStatus = s => s is >= 200 and < 300,
    CancellationToken = cancellationToken,
    MaxRedirects = 5,
    WithCredentials = false,
    // Proxy / Credentials / HttpVersion / JsonSerializerOptions / PoolOptions ...
};
```

默认 JSON 选项为 **Web 风格**（输出 camelCase、输入大小写不敏感），与 JS/axios 生态习惯一致；可用 `c.JsonSerializerOptions` 覆盖。

## 请求体 data 与查询参数 params

```csharp
// 1) 对象 -> application/json
await http.PostAsync("users", new { name = "bob", age = 30 });

// 2) 字典 -> application/x-www-form-urlencoded
await http.PostAsync("login", new Dictionary<string, object?>
{
    ["username"] = "alice",
    ["tags"] = new[] { "a", "b" }   // tags=a&tags=b
});

// 3) 透传：string / byte[] / Stream / MultipartFormDataContent
using var multipart = new MultipartFormDataContent("boundary");
multipart.Add(new StringContent("alice"), "name");
await http.PostAsync("upload", multipart);

// 4) params：数组/嵌套对象（与 axios 默认 paramsSerializer 对齐）
await http.GetAsync("search", c =>
    c.Params = new
    {
        q = "a b",                       // q=a%20b
        ids = new[] { 1, 2 },            // ids[]=1&ids[]=2
        filter = new { kind = "book" }   // filter[kind]=book
    });
```

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

所有快捷方法都支持 `<dynamic>`：`GetAsync<dynamic>` / `PostAsync<dynamic>` / `PutAsync<dynamic>` / `PatchAsync<dynamic>` / `DeleteAsync<dynamic>`（含 `RequestAsync<dynamic>` 与静态门面 `Axios.GetAsync<dynamic>`）；也可以在非泛型请求上显式设置 `c.ResponseType = ResponseType.Dynamic`。显式指定 `ResponseType.Text/ArrayBuffer/Stream` 时优先于 `<dynamic>` 的默认动态展开。

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
    Console.WriteLine(ex.Code);          // ERR_BAD_REQUEST
    Console.WriteLine(ex.Response.Status);   // 404
    Console.WriteLine(ex.Message);       // Request failed with status code 404
}
```

| Code | 触发场景 | 对应 axios |
| --- | --- | --- |
| `ERR_BAD_REQUEST` | 响应状态码为 4xx（默认 validate 失败） | ✅ |
| `ERR_BAD_RESPONSE` | 响应状态码为 5xx | ✅ |
| `ECONNABORTED` | 请求超过 `Timeout` | ✅ |
| `ERR_NETWORK` | 连接被拒绝/DNS 失败等网络层异常 | ✅ |
| `ERR_CANCELED` | `CancellationToken` 取消 | ✅ |
| `ERR_INVALID_URL` | URL 非法 | ✅ |

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

## 示例项目

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) 是一个**开箱即用、零外部依赖**的控制台示例：内置一个基于 `TcpListener` 的 HTTP/1.1 Keep-Alive 演示服务器，离线即可运行，覆盖 12 类集成调用：

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

```bash
dotnet run --project samples/AxiosNet.Simple
```

## 构建与测试

```bash
# 构建（Debug）
dotnet build AxiosNet.slnx

# 运行全部 56 个 xUnit 单元测试
dotnet test AxiosNet.slnx

# Release
dotnet build AxiosNet.slnx -c Release
```

测试项目使用自研的轻量 HTTP/1.1 Keep-Alive 测试服务器（`TcpListener`，零 NuGet 依赖），可直接观测真实 TCP 连接复用数与峰值并发，覆盖：

- 基础请求/全方法/重定向/HEAD/BaseAddress/静态门面
- params 与 data 转换、multipart、Basic Auth、默认头
- 请求/响应拦截器（顺序、Eject、异步、错误恢复）
- 4xx/5xx/超时/取消/网络错误/validateStatus
- dynamic 响应图与 dynamic 请求（9 个专项测试，覆盖 GetAsync&lt;dynamic&gt; / PostAsync&lt;dynamic&gt; / 静态门面 / 显式 responseType 优先）
- 连接池：连接复用、最大连接数限流、跨实例共享、空闲驱逐、多 origin 隔离、Dispose 语义

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
│     ├─ AxiosMethod.cs / ResponseType.cs / AxiosAuth.cs
│     ├─ Interceptors/           # 请求/响应拦截器管理
│     ├─ Internal/               # URL/params、Content 工厂、dynamic JSON、状态码文本
│     └─ Pooling/                # HttpConnectionPool 连接池
├─ samples/
│  └─ AxiosNet.Simple/           # 12 类集成调用示例（内置演示服务器）
└─ test/
   └─ AxiosNet.Tests/            # xUnit 单元测试（56 个）
```

## 与 axios 的差异说明

受 .NET 平台特性影响的有意差异：

1. 方法命名为 .NET 习惯的 `XxxAsync`（`GetAsync` 对应 `axios.get`）；
2. `timeout` 使用 `TimeSpan` 而非毫秒数；
3. 非泛型 JSON 响应的 `Data` 是 `JsonElement`（需要 JS 式动态访问时使用泛型 `GetAsync<dynamic>`）；
4. 取消使用 `CancellationToken`（语义等同 `AbortController`）；
5. `IDictionary` 请求体默认按表单编码（对齐 `URLSearchParams`），普通对象/`ExpandoObject` 按 JSON 编码；
6. 连接池是本库的扩展能力，axios（浏览器/XHR、Node http）没有同构实现。

## 发布到 GitHub

```bash
git init
git add .
git commit -m "feat: AxiosNet - axios-style HTTP client with connection pooling"
git branch -M main
git remote add origin https://github.com/<你的用户名>/AxiosNet.git
git push -u origin main
```

建议后续补充 GitHub Actions（`dotnet test`）、NuGet 打包（`dotnet pack src/AxiosNet -c Release`）与版本标签。

## License

[MIT](LICENSE)
