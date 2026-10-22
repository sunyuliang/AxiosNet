# AxiosNet.DependencyInjection

[简体中文](#依赖注入扩展) | [English](#dependency-injection-extensions)

**.NET 依赖注入扩展 for [AxiosNet](https://www.nuget.org/packages/AxiosNet)（含包内声明式客户端，命名空间 `AxiosNet.WebAPI`）** — `AddAxiosClient` / `AddApiClient` 把客户端注册为单例，连接池、拦截器与实例默认值随容器生命周期共享。除 AxiosNet 主包外只依赖微软第一方 `Microsoft.Extensions.DependencyInjection.Abstractions`。

## 依赖注入扩展

```bash
dotnet add package AxiosNet.DependencyInjection
```

### AddAxiosClient：核心客户端单例

```csharp
using AxiosNet.DependencyInjection;

builder.Services.AddAxiosClient(config =>
{
    config.BaseAddress = "https://api.example.com";
    config.Timeout = TimeSpan.FromSeconds(10);
});

// 之后直接注入使用
public sealed class UserService(AxiosClient client)
{
    public Task<User> GetAsync(int id) => client.GetAsync<User>($"users/{id}");
}
```

### AddApiClient：声明式客户端单例（三种模式）

```csharp
using AxiosNet.WebAPI.DependencyInjection;

// 自建模式：自带 BaseAddress 自建底层客户端（与容器里的 AxiosClient 注册互不相干）
builder.Services.AddApiClient("https://api.example.com",
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.Authorization = new BearerAuthorizationProvider(token));

// 门面模式：复用容器中已注册的 AxiosClient 单例（需先 AddAxiosClient）
builder.Services.AddApiClient(configure: client =>
    client.Authorization = new BearerAuthorizationProvider(token));

// 命名模式：多站点（a/b/c）各注册一次，经 IApiClientResolver 按站点名取用（详见下节）
builder.Services.AddApiClient("a", "https://a.example.com");
builder.Services.AddApiClient("b", "https://b.example.com");
```

### 多站点：命名注册 + IApiClientResolver

同时对接多个站点时，每个站点调一次 `AddApiClient(name, baseAddress, ...)`（站点名区分大小写、必须唯一），消费方注入 `IApiClientResolver` 按键取用：

```csharp
using AxiosNet.WebAPI.DependencyInjection;

// 注册：每站点自带 BaseAddress、默认值（超时/头/序列化器/连接池）与授权，互不干扰
builder.Services.AddApiClient("a", "https://a.example.com",
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.Authorization = new BearerAuthorizationProvider(tokenA));

builder.Services.AddApiClient("b", "https://b.example.com",
    configure: client => client.Authorization = new BearerAuthorizationProvider(tokenB));

// 消费：站点名运行时才知道（来自配置 / 数据库）时用解析器
public sealed class Dispatcher(IApiClientResolver resolver)
{
    public Task<AxiosResponse<T>> SendAsync<T>(string site, IApiRequest<T> request)
        => resolver.Get(site).RequestAsync(request);
}

// 消费：站点编译期已知时也可以直接取（每次同一实例）
var a = resolver.Get("a");
if (resolver.TryGet("b", out var b)) { /* b 站点已注册 */ }
```

- `Get(name)`：未注册时抛 `InvalidOperationException`，消息列出全部已注册站点；`TryGet` 不抛
- `Names`：已注册站点名，按注册顺序排列（只读副本，外部无法改写解析器状态）
- 命名站点**只经解析器暴露**，不注册未命名的 `ApiClient`（单站点场景用不带 name 的重载）
- 站点名重复在**注册期**即抛 `InvalidOperationException`；各站点的底层客户端由解析器持有，随容器退出统一释放
- 解析器**释放后**调 `Get` / `TryGet` 抛 `ObjectDisposedException`（已解析与未解析过的站点一致）；站点创建失败（`configure` 抛错、授权提供方解析失败）当场释放已建的连接池并原样透出异常
- .NET 8+ 消费方也可完全用官方键名服务（`AddKeyedSingleton<ApiClient>("a", ...)` + `[FromKeyedServices("a")]` / `GetRequiredKeyedService`），示例场景 6 有完整演示；两种方案可共存

### 容器授权：IAuthorizationProvider 单例自动装配

```csharp
// 授权提供方本身也走 DI（真实系统里可从其它服务取令牌 / 刷新）
builder.Services.AddSingleton<IAuthorizationProvider>(
    new BearerAuthorizationProvider(() => TokenService.GetAsync()));

// 未显式设置 Authorization 时自动采用它；configure 里显式设置的优先
builder.Services.AddApiClient("https://api.example.com");
```

### 语义要点

- **TryAdd 语义**：两者都是「已注册则不重复注册」（保留首次），重复调用不会建出第二个连接池，`configure` / `configureDefaults` 的回调也不再执行（不会留下「副作用生效、配置被丢弃」的半生效状态）；要替换请先移除原注册
- **多站点（a/b/c）**：命名重载 `AddApiClient(name, baseAddress, ...)` + `IApiClientResolver`（见上节，全 TFM 可用）；.NET 8+ 亦可用官方键名服务 `AddKeyedSingleton<ApiClient>("a", ...)` + `[FromKeyedServices("a")]`（示例场景 6）
- **生命周期归容器**：`using` 掉注入的客户端不会释放共享实例；单例随容器退出统一释放（自建模式与命名站点经内部持有项/解析器释放连接池）
- **门面模式缺注册**：容器中没有 `AxiosClient` 时解析 `ApiClient` 抛 `InvalidOperationException`（与注册顺序无关）
- 完整可运行示例见仓库 `samples/AxiosNet.Simple.DependencyInjection`（Generic Host，六个场景跑真实 HTTP 往返：核心单例 / 自建 / 门面 / 容器授权 / 命名多站点 / 键名服务）

## Dependency injection extensions

```bash
dotnet add package AxiosNet.DependencyInjection
```

### AddAxiosClient: core client singleton

```csharp
using AxiosNet.DependencyInjection;

builder.Services.AddAxiosClient(config =>
{
    config.BaseAddress = "https://api.example.com";
    config.Timeout = TimeSpan.FromSeconds(10);
});

public sealed class UserService(AxiosClient client)
{
    public Task<User> GetAsync(int id) => client.GetAsync<User>($"users/{id}");
}
```

### AddApiClient: declarative client singleton (three modes)

```csharp
using AxiosNet.WebAPI.DependencyInjection;

// Self-built mode: carries its own BaseAddress, independent of any registered AxiosClient
builder.Services.AddApiClient("https://api.example.com",
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.Authorization = new BearerAuthorizationProvider(token));

// Facade mode: reuses the registered AxiosClient singleton (call AddAxiosClient first)
builder.Services.AddApiClient(configure: client =>
    client.Authorization = new BearerAuthorizationProvider(token));

// Named mode: register each site (a/b/c) once and resolve by name via IApiClientResolver (see below)
builder.Services.AddApiClient("a", "https://a.example.com");
builder.Services.AddApiClient("b", "https://b.example.com");
```

### Multiple sites: named registration + IApiClientResolver

When talking to several sites at once, call `AddApiClient(name, baseAddress, ...)` once per site (names are case-sensitive and must be unique), and inject `IApiClientResolver` in consumers:

```csharp
using AxiosNet.WebAPI.DependencyInjection;

// Registration: each site carries its own BaseAddress, defaults (timeout/headers/serializer/pool) and authorization
builder.Services.AddApiClient("a", "https://a.example.com",
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.Authorization = new BearerAuthorizationProvider(tokenA));

builder.Services.AddApiClient("b", "https://b.example.com",
    configure: client => client.Authorization = new BearerAuthorizationProvider(tokenB));

// Consumption: when the site is only known at runtime (config / database), use the resolver
public sealed class Dispatcher(IApiClientResolver resolver)
{
    public Task<AxiosResponse<T>> SendAsync<T>(string site, IApiRequest<T> request)
        => resolver.Get(site).RequestAsync(request);
}

// Consumption: when the site is known at compile time, take it directly (same instance every time)
var a = resolver.Get("a");
if (resolver.TryGet("b", out var b)) { /* site b is registered */ }
```

- `Get(name)`: throws `InvalidOperationException` when the name is not registered — the message lists all registered sites; `TryGet` never throws
- `Names`: registered site names, in registration order
- Named sites are exposed **only through the resolver** — no unnamed `ApiClient` is registered (use the nameless overload for single-site scenarios)
- A duplicate name throws `InvalidOperationException` **at registration time**; each site's underlying client is owned by the resolver and released when the container shuts down
- On .NET 8+ you can also use the official keyed services end-to-end (`AddKeyedSingleton<ApiClient>("a", ...)` + `[FromKeyedServices("a")]` / `GetRequiredKeyedService`) — see scenario 6 of the sample; both approaches can coexist

### Authorization from the container

```csharp
builder.Services.AddSingleton<IAuthorizationProvider>(
    new BearerAuthorizationProvider(() => TokenService.GetAsync()));

builder.Services.AddApiClient("https://api.example.com");
```

### Semantics

- **TryAdd semantics**: registration is skipped when one already exists (first wins), so repeated calls never create a second connection pool; remove the old registration to replace it
- **Multiple sites (a/b/c)**: the named overload `AddApiClient(name, baseAddress, ...)` + `IApiClientResolver` (see above, works on every TFM); on .NET 8+ the official keyed services (`AddKeyedSingleton<ApiClient>("a", ...)` + `[FromKeyedServices("a")]`, scenario 6 of the sample) work as well
- **The container owns the lifetime**: disposing an injected client does not release the shared instance; singletons are released when the container shuts down (self-built and named sites release their pools through an internal holder / the resolver)
- **Facade mode without a registration**: resolving `ApiClient` throws `InvalidOperationException` when no `AxiosClient` is registered (order-independent)
- Runnable sample: `samples/AxiosNet.Simple.DependencyInjection` in this repository (Generic Host, six scenarios over real HTTP round-trips: core singleton / self-built / facade / container authorization / named multi-site / keyed services)

## Runtime support

Single `net6.0` assembly — runs on .NET 6 / 7 / 8 / 9 / 10 / 11. No third-party runtime dependencies.
