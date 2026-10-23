# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/nuget/v/AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![language](https://img.shields.io/badge/C%23-12-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![tests](https://img.shields.io/badge/tests-792%20passed-success)](#build-and-test)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[简体中文](README.md) | **English**

An **axios-style HTTP client for .NET (C#)** whose API maps one-to-one onto the JavaScript [axios](https://github.com/axios/axios) ecosystem. On top of .NET's native `SocketsHttpHandler` connection reuse, it adds a **per-origin connection pool** (connection reuse, concurrency caps, idle eviction, sharing across instances, observable statistics). It is meant to replace hand-rolled `HttpClient` wrappers and covers API calls, file upload/download, form submission, and similar network scenarios.

- Zero third-party dependencies — .NET BCL only
- Runtime support: **.NET 6 and later** (single `net6.0` target, see [Target framework and runtime support](#target-framework-and-runtime-support))
- Async-first (`Task` / `CancellationToken` / `ConfigureAwait(false)`)

> Repository: **Gitee ([itsm/axios-net](https://gitee.com/itsm/axios-net)) is the primary repository** —  PRs and tags are handled there. [GitHub](https://github.com/sunyuliang/AxiosNet) is a read-only mirror (CI-driven NuGet publishing also runs on the mirror side).

---

## Table of contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [Declarative API client: AxiosNet.WebAPI (Refit-style)](#declarative-api-client-axiosnetwebapi-refit-style)
- [Dependency injection (singleton registration)](#dependency-injection-singleton-registration)
- [Configuration](#configuration)
- [Request body data and query params](#request-body-data-and-query-params)
- [URL encoding: what gets encoded automatically](#url-encoding-what-gets-encoded-automatically)
- [ASP.NET Core Web API: single-file upload](#aspnet-core-web-api-single-file-upload)
- [responseType (including dynamic)](#responsetype-including-dynamic)
- [Downloading files](#downloading-files)
- [Form shortcuts: postForm / putForm / patchForm](#form-shortcuts-postform--putform--patchform)
- [Progress callbacks, rate limiting, and size limits](#progress-callbacks-rate-limiting-and-size-limits)
- [Custom serialization and data transforms](#custom-serialization-and-data-transforms)
- [Interceptors](#interceptors)
- [Error handling](#error-handling)
- [Timeouts and cancellation](#timeouts-and-cancellation)
- [Connection pool](#connection-pool)
- [Injecting a custom handler](#injecting-a-custom-handler)
- [Features](#features)
- [axios ↔ AxiosNet mapping](#axios--axiosnet-mapping)
- [Differences from axios](#differences-from-axios)
- [Sample project](#sample-project)
- [Target framework and runtime support](#target-framework-and-runtime-support)
- [Build and test](#build-and-test)
- [Overall pipeline](#overall-pipeline)
- [Project layout](#project-layout)
- [License](#license)

---

## Installation

### Option 1: NuGet (recommended)

```bash
dotnet add package AxiosNet                          # axios-style HTTP client + built-in declarative API client
dotnet add package AxiosNet.DependencyInjection      # optional: DI singleton registration extensions (depends on the main package)
```

> Version numbers match Git tags: tagging `v1.2.3` publishes `AxiosNet 1.2.3` and `AxiosNet.DependencyInjection 1.2.3` together; tagged pre-releases (e.g. `v1.2.3-rc.1`) are published as prerelease versions.

### Option 2: Project reference (this repository)

```bash
dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj
```

### Option 3: Source inclusion

Copy the `src/AxiosNet` folder into your solution:

```bash
dotnet sln YourApp.sln add src/AxiosNet/AxiosNet.csproj
```

## Quick start

```csharp
using AxiosNet;

// Equivalent to axios.get('/users/1').then(res => res.data)
var response = await Axios.GetAsync<User>("https://api.example.com/users/1");

User user = response.Data!;
Console.WriteLine($"{response.Status} {user.Name}");

// Equivalent to axios.create({ baseURL, timeout, headers })
var http = Axios.Create(c =>
{
    c.BaseAddress = "https://api.example.com/";
    c.Timeout = TimeSpan.FromSeconds(10);
    c.Headers.Set("Authorization", "Bearer xxx");
});

await http.PostAsync("users", new CreateUserRequest("bob", 30));

// Equivalent to axios.getUri(config): build the URL without sending a request
string url = http.GetUri(c =>
{
    c.Url = "users";
    c.Params = new { page = 2, ids = new[] { 1, 2 } };
});
// -> https://api.example.com/users?page=2&ids[]=1&ids[]=2
```

> `GetUri` returns the **actual wire form**: percent-encoded unreserved characters in the query (`%41`→`A`, `%7E`→`~`) are normalized by `System.Uri` per RFC 3986, exactly like the request line that goes out; use the `GetUri` result as the signing input for pre-signed URLs.

## Declarative API client: AxiosNet.WebAPI (Refit-style)

When you would rather not hand-write URLs and config, use the declarative API client built into AxiosNet (namespace `AxiosNet.WebAPI`, shipped in the same package): **define request models + plug in authorization, and the call site is a single `client.RequestAsync(request)`** — the response type is inferred from the request model's generic argument (the RestSharp / Flurl / Refit niche, while keeping the full axios-style response shape and every low-level capability such as the connection pool).

```csharp
using AxiosNet.WebAPI;

// 1) Request model: route on the class, parameter bindings on properties;
//    the IApiRequest<TResponse> generic IS the response type
[Get("users/{id}")]
public sealed class GetUserRequest : IApiRequest<User>
{
    [Path] public int Id { get; set; }                        // fills {id} (URL-escaped, case-insensitive token match)
    [Query("verbose")] public string? Verbose { get; set; }   // query string
    [Header("X-Trace")] public string? Trace { get; set; }    // request header
}

[Post("users")]
public sealed class CreateUserRequest : IApiRequest<User>
{
    [Body] public UserPayload Payload { get; set; } = null!;  // body (JSON / form / multipart / raw — same encoding rules as the core package's data)
}

// 2) Client + authorization: IAuthorizationProvider is the extension point (built-in Bearer; implement your own for token caching / refresh / signing)
var client = new ApiClient("https://api.example.com/")
{
    Authorization = new BearerAuthorizationProvider(() => GetTokenAsync())
};

// 3) Call: response.Data is already a User
var response = await client.RequestAsync(new GetUserRequest { Id = 5, Trace = "t-1" });
Console.WriteLine($"{response.Status} {response.Data!.Name}");
await client.RequestAsync(new CreateUserRequest { Payload = payload });   // returns AxiosResponse<User>
```

Binding rules (parameter semantics aligned with Refit, but applied to **properties**):

| Binding | Attribute | Notes |
| --- | --- | --- |
| Path segment | `[Path]` | substitutes template `{name}`, URL-escaped; `null` throws |
| Query | `[Query]` / unannotated property | unannotated properties bind as query by default (**exception: default body below**); arrays `a[]=v`, nested `a[b]=v` follow the core package's params rules |
| Query map | `[Query]` + dictionary property | **flattened** to `?a=1&b=2` when no explicit name is given; an explicit name (`[Query("filters")]`) keeps the nested form `?filters[a]=1` |
| Header | `[Header]` | `null` is skipped |
| Header map | `[HeaderCollection]` | `IDictionary<string, string>` written entry by entry; entries with a `null` value are skipped |
| Static headers | `[Headers]` (class level) | fixed `"Name: Value"` headers; a name alone (`"X-Internal"`) removes that header; property headers override same-named static ones, base-class declarations are inherited |
| Body | `[Body]` | at most one per model; `null` sends no body |
| Exclusion | `[ApiIgnore]` | the property is excluded from binding entirely (no path/query/header/body), for internal state or sensitive data; combining with a parameter attribute throws |

```csharp
[Get("users/{id}")]
[Headers("Accept: application/json", "X-Api-Version: 2")]   // class-level static headers
public sealed class GetUserRequest : IApiRequest<User>
{
    [Path] public int Id { get; set; }
    [Query] public Dictionary<string, object?> Filters { get; set; } = new();  // -> ?role=admin&page=2
    [HeaderCollection] public Dictionary<string, string?> Extra { get; set; } = new();  // one header per entry
}
```

- **`[Body]` is optional for single-payload models**: on POST/PUT/PATCH, if the model has exactly one unannotated **non-scalar** property (excluding primitives, enums, string, decimal, DateTime/DateTimeOffset, TimeSpan, Guid and their nullable forms), that property binds as the body by default; the scalar guard keeps "POST with only a query parameter" from silently becoming a request body, and multiple unannotated properties all stay query — ambiguous cases keep the status quo, and `[Body]` can always be stated explicitly
- **One property can bind to several places (multi-binding)**: e.g. `[Path("id")]` + `[Header("X-Id")]` + `[Body]` on the same property — the value is read once and applied to each binding. Guard: `[Path]` / `[Header]` properties must be scalars (path segments and headers are plain text on the wire; a complex object would only produce a garbage `ToString()` — rejected at build time); `[Query]` / `[Body]` are unrestricted
- **Properties you don't want to send**: `[ApiIgnore]` excludes the property from binding entirely (it also does not count toward the default body's "exactly one unannotated" check); note this only affects binding — serialization of properties **inside** the request body is the body type's own business (that is STJ's `[JsonIgnore]`)
- Per-request tweaks (timeout / Content-Type / serializer / responseType): derive from `ApiRequest<T>` and override `Configure(config)`, or call `RequestAsync(request, config => ...)` (runs after model binding, so it can override headers written by the model)
- Invalid model definitions (missing route attribute, unbound placeholder, multiple bodies, duplicate parameter names) throw `InvalidOperationException` on first use, naming the offending property
- **Startup check**: `ApiClient.ValidateModels(typeof(GetUserRequest), ...)` or `ApiClient.ValidateModels(assembly)` returns **all** error messages (empty = all valid) without throwing; it shares the same metadata build as the first call, so "validated but still failing" cannot happen
- Authorization runs **after** binding and `Configure`: signing implementations see the final URL and final headers
- **Typed errors**: with `client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()`, status-code errors throw `ApiError<TError>` (`Error` / `Status` / `Response` / `Original`, with the original `AxiosException` as `InnerException`); if the factory returns `null` or is not set, `AxiosException` is thrown as before. Network errors / timeouts / cancellation never go through the factory
- **Reading error bodies**: `ApiErrorContext.Body` exposes the raw error response text (regardless of whether the declared response type could parse it), and `TryReadAsync<TError>()` deserializes it with the serializer selected by the response `Content-Type`; a parse failure only yields `default` and never hides the original HTTP error
- **Authorization declarations**: `[Authorize]` (or `[Authorize("Basic")]`) marks the model as requiring authorization — the scheme decides the `Authorization: {scheme} {token}` prefix; `[AllowAnonymous]` skips the whole authorization chain (login, health checks). `client.AccessTokenProvider` only obtains the token, the library writes the header; an `Authorization` header already written by the model or the instance defaults is never overwritten, and `Authorization` (`IAuthorizationProvider`) still runs afterwards and may override it
- **URL parameter formatting**: `client.UrlParameterFormatter = value => ...` takes over the text form of `[Path]` / `[Query]` values (enum spellings, date formats, ...); returning `null` falls back to the default rules
- `client.GetUri(request)` builds the URL without sending; `client.Axios` exposes the underlying `AxiosClient` (interceptors / connection pool / handler injection all available)

### Streaming responses (SSE / NDJSON)

`RequestStreamAsync` turns the response stream into a frame-by-frame `IAsyncEnumerable<T>`; **the status code and headers are available at `await` time** (same shape as a normal response), frames are read during enumeration:

```csharp
var response = await client.RequestStreamAsync(
    new ChatStreamRequest { Prompt = "hi" },
    AxiosStreamFormat.ServerSentEvents);          // RequestStreamAsync(request) uses NDJSON by default

await foreach (var chunk in response.Data!)
{
    Console.Write(chunk.Text);
}
```

- **Timeout semantics**: for streaming requests `Timeout` **only covers the response headers**; the body is governed by `client.StreamIdleTimeout` — the default `Zero` means no limit (only the cancellation token applies), while a positive value fails when no data arrives for that long (`ECONNABORTED`, with "idle" in the message)
- **Framing happens at the byte level**: split on `\n` first, then decode — a multi-byte character split across chunks survives (decoding first and splitting lines afterwards produces `U+FFFD`)
- **SSE**: multi-line `data:` joined with newlines, `:` comment lines treated as heartbeats and ignored, `event:` / `id:` / `retry:` ignored, `data: [DONE]` ends the stream; **NDJSON**: one JSON value per line, empty lines skipped
- **Ownership**: a stream can be enumerated once; ending or `break`ing an `await foreach` disposes the enumerator and returns the connection, while manual `MoveNextAsync` callers must call `DisposeAsync` themselves or the connection stays occupied
- **Errors**: status-code errors throw at `await` (honoring `ErrorFactory`; up to 64 KB of the failing response body is preserved), frame parse failures are `ERR_BAD_RESPONSE`, mid-stream disconnects are `ERR_NETWORK`, cancellation is `ERR_CANCELED`

## Dependency injection (singleton registration)

The DI extensions live in the separate package [`AxiosNet.DependencyInjection`](https://www.nuget.org/packages/AxiosNet.DependencyInjection) (the main library itself stays free of external dependencies): in ASP.NET Core / Generic Host, register the clients as singletons with the extension methods so the connection pool, interceptors, and instance defaults are shared for the container's lifetime.

```bash
dotnet add package AxiosNet.DependencyInjection
```

```csharp
using AxiosNet.DependencyInjection;        // AddAxiosClient (DI extension package)
using AxiosNet.WebAPI.DependencyInjection; // AddApiClient (DI extension package)

// Option 1: the core client only
builder.Services.AddAxiosClient(config =>
{
    config.BaseAddress = "https://api.example.com";
    config.Timeout = TimeSpan.FromSeconds(10);
});

// Option 2: the declarative client with its own underlying client (BaseAddress is the parameter)
builder.Services.AddApiClient("https://api.example.com",
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.Authorization = new BearerAuthorizationProvider(token));

// Option 3: the declarative client as a facade over the registered AxiosClient singleton (pool / interceptors shared)
builder.Services.AddApiClient(configure: client =>
    client.Authorization = new BearerAuthorizationProvider(token));

// Option 4: multiple sites (a/b/c) — one named registration per site, resolved via IApiClientResolver
builder.Services.AddApiClient("a", "https://a.example.com",
    configure: client => client.Authorization = new BearerAuthorizationProvider(tokenA));
builder.Services.AddApiClient("b", "https://b.example.com",
    configure: client => client.Authorization = new BearerAuthorizationProvider(tokenB));

// Then inject and use
public sealed class UserService(ApiClient client)
{
    public Task<User> GetAsync(int id) => client.RequestAsync(new GetUserRequest { Id = id });
}

// Multi-site consumer: when the site is only known at runtime (config / database), inject the resolver
public sealed class Dispatcher(IApiClientResolver resolver)
{
    public Task<AxiosResponse<T>> SendAsync<T>(string site, IApiRequest<T> request)
        => resolver.Get(site).RequestAsync(request);
}
```

| Extension | Registers | Notes |
| --- | --- | --- |
| `AddAxiosClient(configure)` | singleton `AxiosClient` | `configure` sets instance defaults (BaseAddress, timeout, headers, serializer, pool options) |
| `AddApiClient(baseAddress, configureDefaults, configure)` | singleton `ApiClient` (own client) | carries its own BaseAddress; independent of any registered `AxiosClient` — use the facade overload to share |
| `AddApiClient(configure)` | singleton `ApiClient` (facade) | reuses the registered `AxiosClient`; throws `InvalidOperationException` on resolve when none is registered |
| `AddApiClient(name, baseAddress, configureDefaults, configure)` | singleton `IApiClientResolver` + named sites | multi-site: each site carries its own BaseAddress and underlying client, reachable only through `IApiClientResolver` by name; a duplicate name throws at registration time |

- **TryAdd semantics**: both extensions skip registration when one already exists (first wins), so repeated calls never create a second connection pool; remove the old registration to replace it
- **Authorization and error factory from the container**: registered `IAuthorizationProvider` / `IAccessTokenProvider` / `IApiErrorFactory` singletons are attached to the `ApiClient` automatically; anything set explicitly in `configure` wins
- **The container owns the lifetime**: disposing an injected `ApiClient` does not release the shared underlying client; singletons are released when the container shuts down (self-built clients through an internal holder, named sites through the resolver)
- **Multi-site resolver**: `Get(name)` throws `InvalidOperationException` when the name is not registered (the message lists all registered sites), `TryGet` never throws, and `Names` lists the site names in registration order; site names are case-sensitive
- **.NET 8+ alternative**: the official keyed services (`AddKeyedSingleton<ApiClient>("a", ...)` + `[FromKeyedServices("a")]` / `GetRequiredKeyedService`, enums work as keys too) are semantically equivalent to the named overload — scenario 6 of the sample demonstrates them, and both approaches can coexist
- Runnable sample: `samples/AxiosNet.Simple.DependencyInjection` (Generic Host, six scenarios over real HTTP round-trips; the startup menu lists every scenario by reflection — type a number to run, and you are back at the menu when it finishes); besides the AxiosNet main package, the extension package only depends on the first-party `Microsoft.Extensions.DependencyInjection.Abstractions`

## Configuration

```csharp
var config = new AxiosConfig
{
    BaseAddress = "https://api.example.com/", // same as baseURL
    Url = "users",                            // relative or absolute
    Method = HttpMethod.Post,                 // BCL HttpMethod directly, defaults to Get
    Headers = new AxiosHeaders(),             // case-insensitive
    Params = new { page = 1 },                // -> ?page=1
    Data = new { name = "bob" },              // request body (encoded per ContentType)
    ContentType = "application/json",         // default; can be set to application/x-www-form-urlencoded etc.
    Timeout = TimeSpan.FromSeconds(10),       // 0/Zero = no timeout
    StreamIdleTimeout = null,                 // streaming body idle timeout: null = share Timeout; Zero = unlimited
    Auth = new AxiosAuth("user", "pass"),     // Basic
    ResponseType = ResponseType.Json,         // Json/Text/ArrayBuffer/Stream/Dynamic
    ValidateStatus = s => s is >= 200 and < 300,
    CancellationToken = cancellationToken,
    MaxRedirects = 5,
    WithCredentials = false,
    RequestEncoding = null,                   // request body text encoding, null = UTF-8
    ResponseEncoding = null,                  // response body text encoding, null = auto-detect

    // —— Size, rate limiting, and progress ——
    MaxContentLength = 10 * 1024 * 1024,      // response body cap (bytes), null = unlimited
    MaxBodyLength = 50 * 1024 * 1024,         // request body cap (bytes), null = unlimited
    MaxRate = 2 * 1024 * 1024,                // one-way rate limit (bytes/second), null = unlimited
    TimeoutMessage = "Upstream response timed out", // custom timeout error message
    OnUploadProgress = e => Console.WriteLine($"↑ {e}"),
    OnDownloadProgress = e => Console.WriteLine($"↓ {e}"),

    // —— Serialization and transforms ——
    JsonSerializerOptions = myJsonOptions,    // System.Text.Json options for this request/instance
    ContentSerializer = mySerializer,         // content serializer for any format (null = default JSON)
    ParamsSerializer = p => MyQuery(p),       // Func<object?, string>: custom ?a=1&b=2
    CollectionFormat = AxiosCollectionFormat.Csv, // collection form: Brackets(default)/Multi/Csv/Ssv/Tsv/Pipes
    FormFieldNameSelector = (prefix, name) => string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}",
    // TransformRequest / TransformResponse: IList<Func<object?, AxiosConfig, object?>>

    // Proxy / Credentials / HttpVersion / JsonSerializerOptions / PoolOptions ...
};
```

> See [Progress callbacks, rate limiting, and size limits](#progress-callbacks-rate-limiting-and-size-limits) for the progress/rate-limit details.

The default JSON options are **web-style** (camelCase output, case-insensitive input), matching JS/axios conventions. Precedence is "per request → instance defaults → global `AxiosJson.Options`"; see [Custom JSON serialization](#custom-json-serialization) for wiring up a custom serializer.

> `withCredentials` / `proxy` / `credentials` / `maxRedirects` / `poolOptions` are **instance-level** settings: the pool handler is created when the `AxiosClient` is constructed, so these only take effect on instance defaults. To avoid silent misconfiguration, setting any of them on a **single request** throws `AxiosException` (`ERR_BAD_OPTION_VALUE`) immediately, telling you to move it into the instance defaults passed to `Axios.Create(...)`. To drop one of the instance default headers on a single request, use `config.RemoveHeaders.Add("X-Foo")`.
>
> The same applies to an `AxiosClient(pool: ...)` sharing a pool: these four settings and `PoolOptions` apply to the **pool**, not to an individual instance, so setting them on a shared-pool client's defaults also throws `ERR_BAD_OPTION_VALUE` immediately. Configure them on the `ConnectionPoolOptions` used to create the pool instead (see [Connection pool](#connection-pool)).
>
> Settings that are meant to be per-request (`timeout`, `headers`, `params`, `responseType`, `maxContentLength`, `maxRate`, `onUploadProgress`/`onDownloadProgress`, `transformRequest`/`transformResponse`, `validateStatus`, `auth`, `cancellationToken`, `RequestEncoding`/`ResponseEncoding`, and so on) are not affected. `cancellationToken` may be set either as an instance default or per request; the per-request value wins when both are present.

## Request body data and query params

The body encoding is decided **solely by `ContentType`, independent of the runtime type of `data`**: a dictionary is not automatically treated as a form, and an object does not change encoding just because it implements a particular interface.

Primitives (numbers / bool / enum / Guid / date-time / Uri…) are **written as their own text and never reach the serializer** (a bare scalar has no "format"); dictionaries follow exactly the same logic as objects — neither is flattened, and neither falls into the scalar branch.

| `ContentType` | Structured data (objects / dictionaries / dynamic / collections) | Primitives (numbers / bool / enum / Guid / date-time / Uri…) | `string` / `byte[]` / `Memory<byte>` / `ArraySegment<byte>` / `Stream` / `HttpContent` |
| --- | --- | --- | --- |
| `application/json` (default) | JSON text from the current serializer | The value's own text (`42`, `true`, `Paid`), **never through the serializer** | Sent as-is |
| `application/x-www-form-urlencoded` | Key-value form (nested `a[b]=v`, arrays `a[]=v`, nulls skipped) | A root scalar has no key: ignored (empty body) | Sent as-is (treated as an already-encoded form string) |
| `multipart/form-data` | Fields + file parts (`FormFile` / `Stream` / `byte[]` / memory types / `HttpContent`; arrays expand into repeated same-name parts) | A root scalar has no field name: throws `ERR_BAD_OPTION_VALUE` (put it inside an object/dictionary field) | Throws `ERR_BAD_OPTION_VALUE` (header/body contradiction); pass a pre-serialized multipart body as `HttpContent`, or give `Content-Type` a `boundary` parameter |
| Anything else (`application/xml`, `application/yaml`, …) | Handed to the current serializer (an XML serializer produces XML) | The value's own text, **never through the serializer** | Sent as-is |

For the common values, use the `AxiosContentType` constants (the same trade-off as `HttpMethod.Get`: built-in common values plus arbitrary custom ones, still a `string` underneath):

```csharp
c.ContentType = AxiosContentType.Json;                 // application/json (default)
c.ContentType = AxiosContentType.FormUrlEncoded;       // application/x-www-form-urlencoded
c.ContentType = AxiosContentType.MultipartFormData;    // multipart/form-data
c.ContentType = AxiosContentType.OctetStream;          // application/octet-stream
c.ContentType = AxiosContentType.ProblemJson;          // application/problem+json

c.ContentType = AxiosContentType.MultipartFormData + "; boundary=----axios-net";  // with parameters
c.ContentType = "application/vnd.api+json";                                       // custom types still work
```

> `Content-Type` is a composite of *media type + parameters*, and `charset` / `boundary` are exactly the customization points the library supports (`c.ContentType = "multipart/form-data; boundary=----xxx"`). That is why it is a `string` and not an `enum`: an enum cannot express parameters and would also block reading the value from configuration. `AxiosConfig.JsonContentType` / `FormUrlEncodedContentType` are unchanged (same values).

```csharp
// 1) Default application/json: objects, dictionaries, dynamic, and collections all become JSON
await http.PostAsync("users", new { name = "bob", age = 30 });                            // {"name":"bob","age":30}
await http.PostAsync("users", new Dictionary<string, object?> { ["name"] = "bob" });       // the same JSON
await http.PostAsync("users", expandoPayload);                                             // the same JSON

// 2) Request form encoding explicitly (instance defaults or per request both work)
await http.PostAsync("login", new Dictionary<string, object?>
{
    ["username"] = "alice",
    ["tags"] = new[] { "a", "b" }        // -> tags[]=a&tags[]=b
}, c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

// 3) multipart/form-data: fields plus a batch of files
//    3.1 Let the library build it: put FormFile / Stream / byte[] / HttpContent into an object/dictionary
await http.PostAsync("upload", new
{
    title = "Quarterly report",
    files = new[]
    {
        new FormFile("a.pdf", File.OpenRead("a.pdf"), "application/pdf"), // the stream is disposed with the request
        FormFile.FromPath("b.zip"),                                      // the library opens and disposes the file stream
        FormFile.FromBytes("c.txt", bytes, "text/plain")
    }
}, c => c.ContentType = "multipart/form-data");

//    3.2 Build MultipartFormDataContent yourself (when you need per-part control)
using var multipart = new MultipartFormDataContent();
multipart.Add(new StringContent("alice"), "name");
multipart.Add(new ByteArrayContent(bytes), "file", "a.bin");
await http.PostAsync("upload-up", multipart);                    // passed through as-is, keeping its own headers and boundary

// 4) Send binary directly
await http.PostAsync("bin", bytes, c => c.ContentType = "application/octet-stream");

// 5) Every method can carry a body of any type (GET / DELETE / HEAD / OPTIONS included)
await http.GetAsync("reports/query", new { from = "2024-01-01", to = "2024-02-01" });
await http.DeleteAsync("users/1", new { reason = "duplicated" });

// 6) params: arrays and nested objects (matching axios's default paramsSerializer)
await http.GetAsync("search", c =>
    c.Params = new
    {
        q = "a b",                       // q=a%20b
        ids = new[] { 1, 2 },            // ids[]=1&ids[]=2
        filter = new { kind = "book" }   // filter[kind]=book
    });
```

> The default media type is JSON, so set `c.ContentType = "application/octet-stream"` explicitly when sending a binary payload — the library never guesses from `byte[]`/`Stream`. When the whole app speaks one binary format (Protobuf / MessagePack…), the easier route is injecting a serializer with `SerializeToBytes` + `MediaType` (see [Custom serialization](#custom-serialization-and-data-transforms)) — pass objects directly and the header + body align automatically.

Additional notes on multipart:

- **Field names** come from the object's property names or the dictionary's keys (recognizing `[JsonPropertyName]` / `[JsonIgnore]`; `JsonSerializerOptions.PropertyNamingPolicy` is **not** consulted — forms are decoupled from the serializer, and `FormFieldNameSelector` is the only naming entry point); nested objects become `meta[author]`; arrays/collections expand into **repeated same-name parts** (bindable directly by ASP.NET Core `IFormFileCollection`, FastAPI, and PHP).
- **When the root is an array of files** there is no key name; use `FormFile.FieldName` to name the part: `new[] { new FormFile(...) { FieldName = "files" } }`.
- **File names**: `FormFile.FileName` determines `filename`; a bare `Stream`/`byte[]`/`Memory<byte>` is also sent as a file part (zero-copy, file name falls back to the field name).
- **Your own `HttpContent` part**: when the part already carries a `Content-Disposition` it is preserved as-is (including `filename`) and only a missing `name` is filled in — otherwise the server cannot bind the field.
- **Boundary**: generated automatically by default; `c.ContentType = "multipart/form-data; boundary=----xxx"` pins a fixed boundary.
- **Lifetime**: part contents are disposed with the request (on success, failure, and cancellation), so do not reuse a `File.OpenRead(...)` stream outside the request; read it into a `byte[]` first if you need to reuse it.

### URL encoding: what gets encoded automatically

Only *structured input* is encoded automatically. Text you write into `Url` is concatenated as-is and never semantically encoded.

| Location | Encoded? | Details |
| --- | --- | --- |
| Keys and values of `c.Params` (object / dictionary / collection) | ✅ | `Uri.EscapeDataString`: space → `%20`, `&` → `%26`, `=` → `%3D`, `+` → `%2B`, `%` → `%25`, non-ASCII → UTF-8 percent-encoding; `[]` in keys is preserved (`a[b]`) |
| `c.Params` given a pre-serialized query string (`string`) | ❌ | Passed through (only a leading `?` / `&` is stripped); `&` and `+` inside values act as structure characters, so encode them yourself |
| A query string written directly into `c.Url` | ❌ | Only `System.Uri` normalization applies: space → `%20`, non-ASCII → UTF-8, bare `%` → `%25`, unreserved escapes decoded (`%41` → `A`) |
| Path segments written into `c.Url` | ❌ | A `/` in the value splits the path and a `#` starts a fragment; escape with `AxiosUrl.EncodePathSegment(...)` |
| Keys and values of `application/x-www-form-urlencoded` | ✅ | Space → `+` (identical to `FormUrlEncodedContent`), `+` → `%2B`, `&` → `%26`; `[]` in keys is preserved |
| Field names / file names / body of `multipart/form-data` | Not needed | Parts are delimited by the boundary, so names and body travel as-is in UTF-8; non-ASCII file names become RFC 5987 `filename*=utf-8''...` and non-ASCII field names become RFC 2047 encoded-words. `"`, `\`, CR/LF and control characters in either name would break `Content-Disposition`, so they **fail before anything is sent** (`ERR_BAD_OPTION_VALUE` / `ArgumentException`) |
| Request headers | ❌ | No encoding at all; non-ASCII header values are rejected by the transport (`ERR_NETWORK` with `Cause` = `Request headers must contain only ASCII characters`) — base64-encode them yourself |

```csharp
// Path parameter: the value may contain / ? # or spaces
var id = "a/b c";
await http.GetAsync("items/" + AxiosUrl.EncodePathSegment(id));    // items/a%2Fb%20c

// Hand-written query string: encode the value yourself (better: let Params do it)
await http.GetAsync("search?q=" + Uri.EscapeDataString("a&b"));     // search?q=a%26b

// Never pass an already-encoded string into a Params object: it gets encoded twice (%20 → %2520)
```

> For pre-signing, use the value returned by `GetUri`: `Uri` decodes percent-escapes of unreserved characters (`%41`→`A`, `%7E`→`~`), and that value matches the request line exactly.
>
> `url` is a required argument: shortcut methods reject `null` / a blank string with `ERR_INVALID_URL` instead of interpreting it as "request baseAddress itself" — a forgotten variable assignment never turns into an unexpected request to the root path (a `POST` may have side effects). Pass `"/"` explicitly to request the base address.

### ASP.NET Core Web API: single-file upload

"Single-file upload" has two shapes at the HTTP level, and client and server must agree:

| Shape | Content-Type | Client | Server |
| --- | --- | --- | --- |
| **Raw stream** (the whole body is the file) | A file media type, e.g. `application/octet-stream` | `PostAsync(url, stream, c => c.ContentType = ...)` | Read `HttpRequest.Body` and stream to disk |
| **multipart single file** (file + other fields) | `multipart/form-data` | `PostFormAsync(url, new { file = FormFile.FromPath(...) })` | `IFormFile file` |

#### 1) Raw stream (preferred for large files)

```csharp
// Server (Minimal API): the request body is the file content itself, written to disk as it arrives, no buffering
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

// Client: pass a Stream (or byte[]) as data and declare a file Content-Type
await using var stream = File.OpenRead(@"D:\data\report.bin");
var res = await http.PostAsync<UploadResult>("api/files/raw", stream, c =>
{
    c.ContentType = "application/octet-stream";
    c.Headers.Set("X-File-Name", "report.bin");   // raw streams have no file name, so pass it via a custom header or query
});

// Small files can also be sent as byte[]
await http.PostAsync("api/files/raw", bytes, c => c.ContentType = "image/png");
```

> **Never leave `ContentType` at its default**: without an explicit value, `Stream`/`byte[]` is sent as the default `application/json` (the bytes are still intact, but gateways, server-side logic, and logs will all be wrong).

#### 2) multipart single file (when other fields must be submitted together)

```csharp
// Server: the IFormFile parameter name must match the form field name
app.MapPost("/api/files/form", async (IFormFile file, string? note) =>
{
    var path = Path.Combine(uploadRoot, Path.GetFileName(file.FileName));
    await using (var target = File.Create(path))
    {
        await file.CopyToAsync(target);
    }

    return Results.Ok(new { file.FileName, bytes = new FileInfo(path).Length, note });
}).DisableAntiforgery();   // .NET 8 Minimal API requires antiforgery to be disabled explicitly for IFormFile binding

// Client: PostFormAsync is multipart by default; just match the field name to the parameter name
await http.PostFormAsync("api/files/form", new
{
    file = FormFile.FromPath(@"D:\data\report.bin"),   // field name: file
    note = "Quarterly report"
});
```

Equivalent MVC controller:

```csharp
[HttpPost("upload")]
[RequestSizeLimit(50 * 1024 * 1024)]
public async Task<IActionResult> Upload(IFormFile file, [FromForm] string? note) { ... }
```

> In ASP.NET Core projects `FormFile` is ambiguous (`AxiosNet.FormFile` vs `Microsoft.AspNetCore.Http.FormFile`); disambiguate with an alias: `using ClientFile = AxiosNet.FormFile;`.

#### 3) The large-file trio

```csharp
await http.PostAsync("api/files/raw", File.OpenRead(path), c =>
{
    c.ContentType = "application/octet-stream";
    c.MaxBodyLength = 200 * 1024 * 1024;   // client-side cap: rejected before sending when the length is known
    c.MaxRate = 8 * 1024 * 1024;           // throttle to 8 MB/s (optional)
    c.OnUploadProgress = e => Console.WriteLine($"{e.Progress:P0} {e.Rate / 1024:F0} KB/s");
});
```

- Kestrel's default request body limit is **30 MB** and returns 413 beyond that: `builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 50 * 1024 * 1024)`, or per-endpoint `.DisableRequestSizeLimit()`.
- Stream to disk with `request.Body.CopyToAsync(fileStream)`; **avoid** `[FromBody] byte[]` (it loads the whole payload into memory).
- `FileStream` is seekable and `Content-Length` is known, so both the progress `Total` and `MaxBodyLength` are exact.

Runnable sample: item 18 of `samples/AxiosNet.Simple` (`dotnet run --project samples/AxiosNet.Simple`, then type 18 at the menu) — `DemoServer` exposes `/upload/raw` and `/upload/single`, parses byte-exactly, and reports file name/byte count/SHA256, which the client verifies one by one.

## responseType (including dynamic)

```csharp
// 1) Strongly typed (most common)
var typed = await http.GetAsync<User>("users/1");
User u = typed.Data!;

// 2) Non-generic: Data is a JsonElement (falls back to raw text for invalid JSON; binary responses give byte[])
var raw = await http.GetAsync("users/1");
JsonElement element = (JsonElement)raw.Data!;

// 3) Text / ArrayBuffer / Stream
var text = await http.GetAsync("ping", c => c.ResponseType = ResponseType.Text);
var bytes = await http.GetAsync("file", c => c.ResponseType = ResponseType.ArrayBuffer);
var stream = await http.GetAsync("file", c => c.ResponseType = ResponseType.Stream);

// 4) dynamic: use the <dynamic> generic, no DTO needed, accessed like JS (ExpandoObject graph)
var dyn = await http.GetAsync<dynamic>("users/1");
dynamic data = dyn.Data!;
Console.WriteLine($"{data.name}, {data.address.city}, {data.roles[0]}");
```

### Downloading files

Pick the shape based on file size:

```csharp
// 1) Large files: stream to disk with constant memory (recommended)
var response = await http.GetAsync<Stream>("files/report.zip");   // <Stream> switches to ResponseType.Stream automatically
await using (var source = response.Data!)
await using (var target = File.Create(@"D:\downloads\report.zip"))
{
    await source.CopyToAsync(target, cancellationToken);
}

// 2) Small files: get a byte[] in one shot
var bytes = (await http.GetAsync<byte[]>("files/icon.png")).Data!;
await File.WriteAllBytesAsync(@"D:\downloads\icon.png", bytes);

// 3) Use the file name supplied by the server (Content-Disposition: attachment; filename=...)
var disposition = ContentDispositionHeaderValue.Parse(response.GetHeader("Content-Disposition")!);
var fileName = (disposition.FileNameStar ?? disposition.FileName)?.Trim('"') ?? "download.bin";
```

Key points:

- `ResponseType.Stream` (or `GetAsync<Stream>`) uses `HttpCompletionOption.ResponseHeadersRead`: it returns as soon as the headers arrive and streams the body while reading, so the whole payload never lands in memory.
- **You must dispose the stream** (`using` / `await using`): the connection returns to the pool only after the stream is fully read or disposed. Disposing after reading only part of it discards that TCP connection instead of reusing it.
- Download timeouts/cancellation behave like any other request: `c.Timeout` covers **through the end of body reading** (timeout throws `ECONNABORTED`, external cancellation throws `ERR_CANCELED`); with many concurrent downloads, watch the pool's `MaxConnectionsPerServer`.
- On a non-2xx response to a streaming request, the library **discards the body immediately and returns the connection** (`ex.Response.Data` is `null`, `Status`/`Headers` remain usable) — otherwise an unread error stream would occupy the connection for a long time.
- The lease for that origin is held for the whole download (`ActiveLeases` in `GetStatistics()` reflects in-flight downloads), so idle eviction cannot interrupt long downloads; the lease is released when the stream is disposed.
- Use `c.OnDownloadProgress` for download progress, `c.MaxRate` for throttling, and `c.MaxContentLength` for the size cap (details in the next section).
- When writing the response stream straight to disk, `OnDownloadProgress` reports are throttled to every 50 ms so frequent callbacks cannot slow the copy down.
- With `ResponseType.Stream` the body is not decoded at all, while `response.Status/Headers` remain available (validate the status code before writing to disk).

Every shortcut supports `<dynamic>`: `GetAsync<dynamic>` / `PostAsync<dynamic>` / `PutAsync<dynamic>` / `PatchAsync<dynamic>` / `DeleteAsync<dynamic>` (including `RequestAsync<dynamic>` and the static facade `Axios.GetAsync<dynamic>`). You can also set `c.ResponseType = ResponseType.Dynamic` explicitly on a non-generic request. An explicitly specified `ResponseType.Text/ArrayBuffer/Stream` takes precedence over the default dynamic expansion for `<dynamic>`.

`<Stream>` / `<byte[]>` also match the response shape automatically: `GetAsync<Stream>` is equivalent to `ResponseType.Stream` and `GetAsync<byte[]>` to `ResponseType.ArrayBuffer`, so you never need to write `c.ResponseType = ...` by hand.

### Raw content first, and primitive types

Responses uniformly **read the raw bytes first, then decode them to text, then convert them to the return type**, so primitive types can be returned directly:

```csharp
var s = (await http.GetAsync<string>("ping")).Data;        // plain text returned as-is (quotes are kept even if the body is a JSON string)
var n = (await http.GetAsync<int>("count")).Data;          // bare number "42" -> 42
var d = (await http.GetAsync<DateTime>("today")).Data;     // bare date "2024-01-02" -> DateTime
var raw = (await http.GetAsync("file", c => c.ResponseType = ResponseType.ArrayBuffer)).Data; // byte[]
```

- `GetAsync<string>` / `ResponseType.Text`: returns the raw text without JSON parsing;
- Numbers / dates / booleans / GUIDs / enums / `TimeSpan`, etc.: deserialized directly when the body is a JSON scalar, parsed by type when it is bare scalar text;
- Complex types: deserialized as JSON;
- Non-generic: with a textual body, `JsonElement` for valid JSON and the raw text for invalid JSON (matching axios's `transformResponse`); with a binary body (`Content-Type` non-textual) you get `byte[]`;
- Business objects over binary responses (`GetAsync<T>`): bytes go straight to the serializer's byte entry, with no text decoding.

### Content encoding

- **Request body**: `RequestEncoding` decides the bytes written (UTF-8 by default); when set explicitly, the `charset` of `Content-Type` is written too. A `charset` declared explicitly in `Content-Type` takes precedence over `RequestEncoding` (the body is encoded with the declared charset, so header and body always agree); writing both with contradicting values throws `ERR_BAD_OPTION_VALUE`. `byte[]` / `Stream` / `HttpContent` pass through untouched and are unaffected.
- **Response body**: auto-detected by default — the `Content-Type` `charset` → BOM → UTF-8; `ResponseEncoding` forces a specific encoding for servers with a missing or wrong charset.

```csharp
// Send the request in GBK
await http.PostAsync("legacy", "中文", c => c.RequestEncoding = Encoding.GetEncoding("gbk"));

// Force GBK decoding for the response
await http.GetAsync("legacy", c => c.ResponseEncoding = Encoding.GetEncoding("gbk"));
```

> On .NET Core, code pages such as GBK require registering `CodePagesEncodingProvider` first, otherwise `Encoding.GetEncoding("gbk")` throws; in that case auto-detection silently falls back to BOM/UTF-8.

JSON-to-CLR mapping rules:

| JSON | .NET type |
| --- | --- |
| object | `ExpandoObject` (member access / cast to `IDictionary<string,object?>` for indexing) |
| array | `List<object?>` (supports dynamic indexing) |
| string | `string` |
| true/false | `bool` |
| integer | `long` |
| decimal | `double` |
| null | `null` |

`dynamic` also works as a **request body/params** value (`ExpandoObject`):

```csharp
dynamic payload = new ExpandoObject();
payload.name = "charlie";
payload.age = 25;
await http.PostAsync<dynamic>("users", payload); // serialized as {"name":"charlie","age":25}

dynamic query = new ExpandoObject();
query.page = 3;
query.filter = new ExpandoObject();
query.filter.kind = "music";                      // ?page=3&filter[kind]=music
await http.GetAsync<dynamic>("search", c => c.Params = query);
```

> Note: the C# dynamic binder does not support the `expando["key"]` string-indexer syntax; for key-based indexing cast explicitly:
> `((IDictionary<string, object?>)dyn.Data!)["key"]`.

## Form shortcuts: postForm / putForm / patchForm

`PostFormAsync` / `PutFormAsync` / `PatchFormAsync` correspond to `axios.postForm(...)` from axios v1.5+: **they send `multipart/form-data` by default when `ContentType` is not set explicitly**, and `FormFile` / `Stream` / `byte[]` inside the object expand into file parts automatically.

```csharp
// Send multipart in one line: fields + files (no manual ContentType)
await http.PostFormAsync("upload", new
{
    title = "Quarterly report",
    files = new[] { FormFile.FromPath("a.pdf") }
});

// As with axios: specify ContentType yourself if you want urlencoded
await http.PostFormAsync("login", new { username = "alice", password = "***" },
    c => c.ContentType = AxiosConfig.FormUrlEncodedContentType);

// Generics and the static facade work too
var res = await http.PostFormAsync<UploadResult>("upload", payload);
var res2 = await Axios.PostFormAsync<UploadResult>("https://api.example.com/upload", payload);
```

> All three are exactly equivalent to `PostAsync(..., c => c.ContentType = "multipart/form-data")`; they merely bake the "multipart by default" convention into the method name so you don't have to write `ContentType` every time.

## Progress callbacks, rate limiting, and size limits

All three capabilities can be set either in instance defaults or per request.

### Progress callbacks

```csharp
await http.PostAsync("upload", new { files }, c =>
{
    c.OnUploadProgress = e =>
        Console.WriteLine($"↑ {e.Loaded}/{e.Total} ({e.Progress:P0}) {e.Rate / 1024:F1} KB/s ETA {e.Estimated}");
    c.OnDownloadProgress = e =>
        Console.WriteLine($"↓ {e.Loaded}/{e.Total} ({e.Progress:P0}) {e.Rate / 1024:F1} KB/s");
});
```

`AxiosProgressEvent` members:

| Member | Meaning |
| --- | --- |
| `Loaded` | Bytes transferred so far |
| `Total` | Total bytes; `null` when the server sends no `Content-Length` (chunked responses) |
| `Progress` | Fraction from 0 to 1; `null` when `Total` is unknown |
| `Rate` | Cumulative average rate (bytes/second) |
| `Estimated` | Remaining time estimated from the current rate; `null` when finished or when the rate is unknown |
| `Upload` | `true` for upload events |
| `ToString()` | Preformatted, e.g. `"download 1024/2048 bytes (50.0%)"`, ready for logging |

- Callbacks run **synchronously**: move expensive work to the background yourself, otherwise it directly slows the transfer.
- Reports are throttled to every 50 ms; each actual transfer round reports one 100% event on completion (`loaded == total`), never duplicated within that round. A 307/308 redirect that resends the body keeps accumulating (progress never jumps back), so one more event may follow (with `Progress` capped at 100%).
- A throwing callback aborts the request through the unified error model: `ERR_BAD_REQUEST` during upload, `ERR_BAD_RESPONSE` during download, with the original exception in `Cause` (previously the upload side was wrapped into `HttpRequestException` and misreported as `ERR_NETWORK`).
- Download progress also works under `ResponseType.Stream` (bytes are counted as they are read) without affecting the low memory footprint of streaming to disk.
- Contract: the response stream of a single response must be **read by one thread sequentially** (neither progress accounting nor rate limiting is thread-safe).

### Rate limiting with maxRate

```csharp
// Limit each request to 1 MB/s (bytes/second); upload and download are limited independently
await http.GetAsync("big", c => c.MaxRate = 1024 * 1024);
```

The implementation derives the "expected elapsed time" from the bytes transferred and `Delay`s to make up any shortfall, so it limits the **average rate** (small bursts allowed) rather than being a strict token bucket.

### Size limits

```csharp
// Fail immediately when the response body exceeds 1 MB
try
{
    await http.GetAsync("big", c => c.MaxContentLength = 1024 * 1024);
}
catch (AxiosException ex)
{
    Console.WriteLine(ex.Code);    // ERR_BAD_RESPONSE
    Console.WriteLine(ex.Message); // maxContentLength size of 1048576 exceeded (got 8388608 bytes)
}

// Reject a request body over 5 MB before sending
await http.PostAsync("upload", bytes, c => c.MaxBodyLength = 5 * 1024 * 1024);
```

- `MaxContentLength` accumulates **while reading** for ordinary (buffered) responses: it aborts and releases the connection as soon as the limit is crossed, rather than reading all 8 MB into memory first. For `ResponseType.Stream` it works in two layers: the declared `Content-Length` is pre-checked when the headers arrive, and the bytes read are counted as they arrive — so a chunked / length-less response also fails on the read that crosses the limit.
- `MaxBodyLength` checks `Content.Headers.ContentLength` before sending, so it only applies to bodies whose length is known (JSON / forms / `byte[]` / a seekable raw `Stream`); `multipart/form-data` (with `FormFile`) and custom `HttpContent` of unknown length are not intercepted.
- **Negative** values for these four limits throw `ERR_BAD_OPTION_VALUE` (they used to be silently treated as "unlimited", so callers believed a limit was in force). `MaxRate = 0` means "unthrottled" and `Timeout = TimeSpan.Zero` means "no timeout".

## Custom serialization and data transforms

### Content serialization (JSON by default, any format pluggable)

Request/response encoding is owned by `IContentSerializer`; the default implementation is System.Text.Json. The interface is **2 + 2** methods: text formats implement the first two; binary formats (Protobuf / MessagePack / CBOR…) use a pair of byte-channel hooks instead — the request outlet `SerializeToBytes` plus the response inlet `Deserialize(ReadOnlyMemory<byte>)` (both ship default bodies, so text serializers can ignore them). There is also an **optional** `MediaType` property (defaulted too): the serializer declares which media type it speaks, and the library writes it into the request header when the caller did not set `ContentType` (one decision shared by the text and byte channels):

```csharp
public interface IContentSerializer
{
    // optional: this format's media type. null (default) = do not self-declare.
    // Affects the request header only, and only when the caller never set ContentType
    // (applies to both the text and byte channels).
    string? MediaType => null;

    string Serialize(object? value, Type type, AxiosConfig config);

    // textual responses (no Content-Type counts as textual)
    object? Deserialize(string text, Type type, AxiosConfig config);

    // ── the byte channel for binary formats; both defaults mean "this format has no
//    byte form", so text serializers can ignore them ──

    // request outlet: return null to fall back to the text channel (Serialize);
    // return bytes and they are sent as-is (no text encoding, no charset appended).
    // Pairs with MediaType: inject one protobuf serializer and header + body align automatically.
    ReadOnlyMemory<byte>? SerializeToBytes(object? value, Type type, AxiosConfig config) => null;

    // response inlet (Content-Type declared non-textual): the library ships a default that
    // decodes with the response charset (ResponseEncoding -> charset -> BOM -> UTF-8) and
    // delegates to the text entry; binary formats override it
    object? Deserialize(ReadOnlyMemory<byte> data, Type type, AxiosConfig config);
}
```

Only **business objects** enter the serializer; everything else is dispatched by the framework — the library never interprets response content and needs no generic data tree:

| Direction | Handled by |
| --- | --- |
| Request `string` / `byte[]` / `Stream` / `HttpContent` | Sent as-is, only the `ContentType` header is applied |
| Request form / multipart | Assembled by the library from `ContentType` (**never through the serializer**) |
| Request primitives (numbers / bool / enum / Guid / date-time / Uri…) | The framework writes the value's text (**never through the serializer**) |
| Request objects / dictionaries / dynamic / collections (text format) | `Serialize` (a dictionary follows the same logic as an object; it is neither flattened nor treated as a scalar) |
| Request objects / dictionaries / dynamic / collections (binary format) | `SerializeToBytes`, bytes sent as-is; text only when it returns `null` |
| Response `Stream` / `byte[]` | Given directly by the framework |
| Response `string` | Raw text from the framework |
| Response primitives (numbers / bool / enum / Guid / date-time / Uri…) | Parsed from text by the framework |
| Response `dynamic` | Expanded from JSON by the framework (JSON convenience) |
| Response non-generic / `JsonElement` | Textual → JSON document shape (raw text on failure); binary → raw `byte[]` |
| Response any other object (textual response) | `Deserialize(string)` |
| Response any other object (binary response) | `Deserialize(ReadOnlyMemory<byte>)`, never decoded |

#### Serializers can write request headers

The `config` passed to `Serialize` is a **per-request copy** (`Defaults.Clone()`, headers cloned too). Headers written there are applied after the body is built — the equivalent of `headers.setContentType(...)` inside axios' `transformRequest(data, headers)`:

```csharp
public string Serialize(object? value, Type type, AxiosConfig config)
{
    config.Headers.Set("X-Checksum", Checksum(value));   // a header bound to the format
    return ToCsv(value);
}
```

Three rules:

- **It can override `Content-Type`**: headers are applied after the factory sets its default, so `Headers.Set("Content-Type", ...)` wins. The one exception is a multipart body, where the boundary must be preserved (the library skips that override).
- **Nothing leaks**: per-request copy, so instance `Defaults` and concurrent requests are untouched (covered by a test).
- ⚠️ **Assigning `config.ContentType` has no effect on the current request**: the body was already built from the previous value, so it is a silent no-op. Use `MediaType` to declare the format, or write `Headers` directly.

Format-agnostic headers (trace IDs, tenants) belong in `TransformRequest` — earlier, clearer, and not tied to one serializer. Writing headers from `Deserialize` is pointless: the request is long gone.

The other direction works too: **deserialization can read response headers** through `config.Response` (`int Status` / `Headers` / `MediaType` / `CharSet` / `GetHeader(name)`):

```csharp
public object? Deserialize(string text, Type type, AxiosConfig config)
    => config.Response?.GetHeader("X-Schema-Version") switch   // only set on the response path; null while serializing
    {
        "2" => ParseV2(text, type),
        _   => ParseV1(text, type)
    };
```

Two boundaries: it is **only populated during the response phase** (`Serialize` sees `null`), and it is **dropped by `Clone()`** so a previous response's headers cannot leak into the next request. `Headers` is the same dictionary instance as `AxiosResponse.Headers` — zero copy. `TransformResponse` can read it as well, which is the natural place to attach `X-Total-Count`-style metadata to the business object.

#### Which serializer is used: four layers

| Order | Source | Notes |
| --- | --- | --- |
| 1 | `AxiosConfig.ContentSerializer` (per request / instance defaults) | **explicit injection, highest priority** |
| 2 | `AxiosContent.Serializer` (global explicit injection) | **`null` (default) = not injected** → auto mode |
| 3 | Auto mode: registry lookup by media type (`AxiosContent.Register`) | request uses `ContentType`, response uses its `Content-Type`; **a miss returns `null`** |
| 4 | Built-in JSON (`SystemTextJsonSerializer.Instance`) | final fallback |

Two implementations ship **built in**: `SystemTextJsonSerializer` (JSON) and `XmlContentSerializer` (XML via `System.Xml.Serialization`, no third-party dependencies). There are two ways to get XML:

```csharp
using AxiosNet.Serialization;

// A: explicit injection — one client (or the whole app) speaks a single format
AxiosContent.Serializer = XmlContentSerializer.Instance;                        // global
// or new AxiosClient(new AxiosConfig { ContentSerializer = XmlContentSerializer.Instance })   // instance only

// B: auto mode + registry — one app mixes formats by Content-Type
AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
AxiosContent.Register(AxiosContentType.Json, new MyJsonEngine());   // also replaces the JSON engine
// AxiosContent.Unregister(AxiosContentType.Xml);

await http.PostAsync("orders", order);                                             // A: header auto = the serializer's MediaType
await http.PostAsync("orders", order, c => c.ContentType = AxiosContentType.Xml);   // B: explicit, triggers auto matching
var user = (await http.GetAsync<User>("users/1")).Data;                            // application/json → auto → JSON
```

Registry matching: only the media-type main value is compared (case-insensitive, `; charset=...` and friends ignored); **structured suffixes** such as `application/vnd.api+xml` fall back to `application/xml` (same for `+json` / `+yaml`), so one registration covers its vendor variants — an exact registration for a vendor type still wins.

> Header/body pairing works **both ways**: auto mode *picks the format from the header*, and a serializer can *declare the header*.
> 1. **Injection path** (A): `ContentSerializer = XmlContentSerializer.Instance` is the only change needed — the request header becomes `application/xml` automatically, no `ContentType` required;
> 2. **Registry path** (B): the registry key *is* the media type, so you must set `ContentType`, otherwise the lookup misses and JSON is used;
> 3. once `ContentType` was set explicitly (per request or on instance defaults), the serializer's `MediaType` never overrides it — that value is the caller's judgement about its server;
> 4. `ContentType = Xml` with neither injection nor registration leaves the body as JSON (the mismatch is not auto-corrected).

Details and limits of the built-in XML implementation:

- `XmlSerializer` instances are cached per target type (construction is expensive) and shared safely across threads;
- the declared `encoding` **always matches** the bytes actually written: the priority is the `charset` declared in `ContentType` → `RequestEncoding` → UTF-8 (the same resolution the body encoder uses, so you never get the triangle of "declared utf-8, body gb2312, header declaring a third thing"); the root element drops the `xmlns:xsi` / `xmlns:xsd` noise, while `[XmlRoot(Namespace = ...)]` is honoured;
- deserialization **prohibits DTDs** (XXE / billion laughs), and failures surface as `ERR_BAD_RESPONSE`;
- XmlSerializer's own limits still apply: the target type needs a **public parameterless constructor** and only public read/write properties/fields are serialized; anonymous types, interfaces and `Dictionary<,>` are unsupported — use a DTO.

For other textual formats (YAML / CSV …) implement the two methods yourself:

```csharp
using AxiosNet.Serialization;

public sealed class YamlSerializer : IContentSerializer
{
    // self-declared media type: inject it and the request header follows without setting ContentType
    public string? MediaType => "application/yaml";

    public string Serialize(object? value, Type type, AxiosConfig config)
        => YamlConvert.Serialize(value, type);

    public object? Deserialize(string text, Type type, AxiosConfig config)
        => YamlConvert.Deserialize(text, type);
}

AxiosContent.Serializer = new YamlSerializer();          // global; or per instance / per request
```

Boundaries:

- **The request header can be declared by the serializer**: when `MediaType` is non-null and the caller never set `ContentType`, the library writes it into the request (mirroring axios' default `headers.setContentType(..., false)`); it applies to both the text and byte channels. An explicit value always wins. The response direction ignores `MediaType` — the response header is authoritative.
- **Text vs binary comes from the response `Content-Type`** (only the media type main value; `charset` / `boundary` parameters are ignored): `text/*`, `application/json`, `application/xml`, `+json` / `+xml` / `+yaml`, YAML and CSV count as textual; everything else (`application/octet-stream`, `image/png`, `application/x-protobuf`…) is binary; **a missing Content-Type counts as textual**.
- **Binary responses are never decoded**: business objects receive the raw bytes via `Deserialize(ReadOnlyMemory<byte>)` (zero-copy access for Protobuf / MessagePack / CBOR); non-generic calls get `byte[]` directly. The byte inlet's default decodes with the **response charset** and delegates to the text entry (same priority as textual responses: `ResponseEncoding` → charset → BOM → UTF-8), so text serializers need not implement it (and servers mislabeling JSON as `octet-stream` keep working, including non-UTF-8 variants declared via charset or BOM).
- **Binary formats get a request outlet**: override `SerializeToBytes` to return the wire-format bytes — the library sends them as-is (no text encoding, no charset appended) and takes the header from the declared `MediaType`. Paired with the response byte inlet, injecting one serializer swaps the whole pipeline's format. Returning `null` falls back to the text channel; the returned memory must stay alive until the whole request completes (never hand back an ArrayPool lease). **The text channel cannot carry arbitrary bytes**, so a binary format's `Serialize` may just throw `NotSupportedException`.
- **Primitive requests never reach the serializer**: `PostAsync("url", 42)` sends `42`, `true` sends `true`, enums send their name. A bare scalar has no "format" (XML / YAML cannot express an unnamed root scalar), so routing it through the serializer would only force every implementation to special-case scalars again. It therefore does not take part in the `MediaType` header fill either: the header stays at `ContentType` (default `application/json` — and a bare `42` is valid JSON). Date-times use the round-trip format (Kind and ticks preserved) rather than the millisecond UTC form used by forms.
- **Forms / multipart never go through the serializer**: field names come from `FormFieldNameSelector` and `[JsonPropertyName]` (ignore with `[JsonIgnore]`).
- **`dynamic` depends on JSON**: with a non-JSON response `GetAsync<dynamic>` throws `ERR_BAD_RESPONSE`; use `ResponseType.Text` or the non-generic `GetAsync()` for raw text.
- **Non-generic responses**: textual bodies become a `JsonElement` (raw text on failure), binary bodies come back as `byte[]`, independent of the serializer.
- **`GetAsync<JsonDocument>` works, but it counts as a business object**: it is not the framework's document shape — it goes through the active serializer (STJ supports it natively). So the caller **must `Dispose` it** (pooled buffers underneath), and it fails outright under the XML serializer. For read-only JSON tree access prefer `GetAsync<JsonElement>` (framework-provided, nothing to dispose) or the non-generic `GetAsync()`.
- Performance note: textual responses still have to be decoded to a string (one extra ≈2× payload copy, large responses only); binary responses skip that step entirely — bytes go straight to the serializer.

#### JSON-specific settings

JSON behavior has three layers, each overriding the previous one:

| Layer | Entry point | Typical use |
| --- | --- | --- |
| Global | `AxiosJson.Options` | One naming strategy and number/date handling for the whole app |
| Instance | `Axios.Create(c => c.JsonSerializerOptions = ...)` | One backend that speaks PascalCase / snake_case |
| Per request | `c => c.JsonSerializerOptions = ...` | Special contracts for individual endpoints |

```csharp
// 1) Global: stop using camelCase (e.g. the backend fields really are PascalCase) and allow stringified numbers
AxiosJson.Options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
{
    PropertyNamingPolicy = null,
    NumberHandling = JsonNumberHandling.AllowReadingFromString
};

// 2) Instance: affects only this client
var http = Axios.Create(c =>
{
    c.BaseAddress = "https://api.example.com/";
    c.JsonSerializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };
});

// 3) Per request: highest precedence
await http.PostAsync("users", user, c => c.JsonSerializerOptions = specialOptions);
```

> When replacing `AxiosJson.Options`, always assign a **new instance**; System.Text.Json freezes an options instance once it has been used, and mutating it afterwards throws `InvalidOperationException`. Setting it once at startup is recommended.

To swap the whole JSON engine (Newtonsoft.Json, source-generated serializers, etc.), implement `IContentSerializer` as well — it only affects business objects; `dynamic` and non-generic responses stay in the framework:

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

AxiosContent.Serializer = new NewtonsoftJsonSerializer();         // global
// or: new AxiosClient(new AxiosConfig { JsonSerializer = ... })   // instance only
```

Conventions and boundaries:

- The serializer is shared by concurrent requests, so implementations must be stateless or thread-safe; thrown exceptions are wrapped as `ERR_BAD_RESPONSE` / `ERR_BAD_REQUEST`.
- Scope: **request bodies and response deserialization of business objects**. `dynamic`, non-generic responses, primitives, forms and multipart never go through it.
- One abstraction, one injection point: per request > instance > global (`AxiosContent.Serializer`).

### paramsSerializer

`ParamsSerializer` receives **the entire `Params` object** and returns the encoded query string (leading `?` / `&` are stripped automatically):

```csharp
// Comma-separated arrays: ids=1,2,3&q=a%20b
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

> The first parameter is `object?`; cast to your own DTO when you need strong typing. The default serializer already follows axios conventions (arrays `a[]=1&a[]=2`, nested `o[k]=v`, nulls skipped), so most scenarios need no customization. To change the collection shape (`a=1&a=2`, `a=1,2`, space / tab / pipe separated) use `CollectionFormat`: it works per request, and a custom `ParamsSerializer` overrides it.

### FormFieldNameSelector

Decides **how field names are joined** in multipart/urlencoded forms. Signature: `Func<string?, string, string>` (parent name, current name):

```csharp
// The default is prefix[name]; switch to dot style: user.name
await http.PostFormAsync("submit", new { user = new { name = "bob" } }, c =>
{
    c.FormFieldNameSelector = (prefix, name) =>
        string.IsNullOrEmpty(prefix) ? name : $"{prefix}.{name}";
});
```

### transformRequest / transformResponse

Structurally identical to axios transform chains: **request-side transforms run after the interceptors and before encoding** (they may change `Data`, add fields, or replace the type entirely); **response-side transforms run after deserialization** (they may clean up or normalize `Data`).

```csharp
// Instance default chains
var http = Axios.Create(c =>
{
    c.TransformRequest.Add((data, cfg) =>
    {
        // Stamp a timestamp onto dictionary bodies (everything else passes through)
        if (data is IDictionary<string, object?> map)
        {
            map["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }

        return data;
    });

    // Response side: uniformly unwrap the server's { code, data } envelope
    c.TransformResponse.Add((data, cfg) =>
        data is JsonElement { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty("data", out var inner)
            ? inner
            : data);
});

// Append per request (composed with the instance chain, deterministic order: instance first, then this request)
await http.GetAsync("users/1", c =>
{
    c.TransformResponse.Add((data, cfg) => data is JsonElement e ? Normalize(e) : data);
});
```

- The chain is an `IList<Func<object?, AxiosConfig, object?>>`; returning `null` is legal (it simply flows on).
- The return value of `TransformRequest` is still encoded according to `ContentType` — the transform chain does not change the iron rule that "the format is decided by ContentType".
- `TransformResponse` runs for `ResponseType.Json` / `Text` / `ArrayBuffer` / `Dynamic`; it does not run for `Stream` (the data has not been read yet).
- The `data` handed to `TransformResponse` is already the **target type**: for non-generic requests it is a `JsonElement` (or text), while `GetAsync<User>` yields a `User`. To process uniformly at the JSON level (e.g. unwrapping a `{code,data}` envelope), use a non-generic request plus `TransformResponse`, or use a custom converter in `JsonSerializerOptions`.

## Interceptors

```csharp
// Request interceptors: as with axios, they run in **reverse** registration order
client.Interceptors.Request.Use(config =>
{
    config.Headers.Set("X-Trace-Id", TraceId.New());
    return config;
});

// Async interceptor
client.Interceptors.Request.Use(async config =>
{
    var token = await tokenService.GetTokenAsync();
    config.Headers.Set("Authorization", $"Bearer {token}");
    return config;
});

// Response interceptors: run in registration **order**; the rejected branch may swallow the error and return a fallback response
client.Interceptors.Response.Use(
    onFulfilled: response => response,
    onRejected: ex =>
    {
        var error = (AxiosException)ex;
        if (error.Code == "ERR_BAD_REQUEST" && error.Response!.Status == 401)
        {
            // Refresh the token and replay, or return fallback data directly
        }
        throw ex;
    });

// Remove an interceptor
var id = client.Interceptors.Request.Use(cfg => cfg);
client.Interceptors.Request.Eject(id);
```

## Error handling

All failures surface as `AxiosException`, carrying `IsAxiosError / Code / Config / Request / Response`:

```csharp
try
{
    await client.GetAsync("missing");
}
catch (AxiosException ex)
{
    Console.WriteLine(ex.Code);              // ERR_BAD_REQUEST
    Console.WriteLine(ex.Status);            // 404 (= shortcut for Response?.Status)
    Console.WriteLine(ex.Message);           // Request failed with status code 404
    Console.WriteLine(ex.Cause);             // underlying exception (DNS/connection failures etc.), null if none
}

// Aligned with axios.isAxiosError / axios.isCancel
if (Axios.IsAxiosError(ex) && !Axios.IsCancel(ex)) { /* ... */ }
```

`ex.ToJson()` returns a read-only dictionary shaped like axios `AxiosError.toJSON()` (`name` / `message` / `code` / `status` / `method` / `url` / `isAxiosError` / `stack`), ready for structured logging.

| Code | Trigger | axios equivalent |
| --- | --- | --- |
| `ERR_BAD_REQUEST` | Response status is 4xx (default validation failure); request body exceeds `MaxBodyLength`; request body serialization failed (cycles, converter exceptions — see `ex.Cause`); the upload progress callback (`OnUploadProgress`) threw | ✅ |
| `ERR_BAD_RESPONSE` | Response status is 5xx; response body cannot be deserialized to the target type (including `dynamic` receiving non-JSON); response body exceeds `MaxContentLength`; generic argument and `responseType` mismatch (e.g. `GetAsync<Stream>` combined with `text`/`arraybuffer`); the download progress callback (`OnDownloadProgress`) or `transformResponse` threw | ✅ |
| `ECONNABORTED` | The request exceeded `Timeout` (the message can be replaced via `TimeoutMessage`) | ✅ |
| `ERR_NETWORK` | Connection refused / DNS failure / connection dropped mid-flight / truncated response, i.e. network-layer failures (see `ex.Cause` for the inner exception) | ✅ |
| `ERR_CANCELED` | `CancellationToken` cancellation | ✅ |
| `ERR_INVALID_URL` | Invalid URL (including a relative URL with no `baseURL`, a scheme other than `http`/`https`, or a `null` / blank `url` passed to a shortcut method) | ✅ |
| `ERR_BAD_OPTION_VALUE` | Instance-only settings set on a single request (`proxy` / `credentials` / `withCredentials` / `maxRedirects` / `poolOptions`); connection-level options set on a shared-pool client's defaults; `pool` and `poolOptions` passed together; invalid pool option values (e.g. `MaxConnectionsPerServer <= 0`); raw content (`string` / `byte[]` / `Memory` / `Stream`) passed as `data` while `Content-Type` is `multipart/form-data` without a `boundary` parameter; a custom `paramsSerializer` threw | ✅ (same code) |

## Timeouts and cancellation

```csharp
// Per-request timeout
await client.GetAsync("slow", c => c.Timeout = TimeSpan.FromMilliseconds(500));

// Disable the instance-level default timeout (an explicit Timeout = 0 overrides it; only an unassigned value inherits the default)
await client.GetAsync("slow-but-wanted", c => c.Timeout = TimeSpan.Zero);

// External cancellation (equivalent to AbortController)
using var cts = new CancellationTokenSource();
var task = client.GetAsync("slow", cancellationToken: cts.Token);
cts.CancelAfter(TimeSpan.FromSeconds(1));
```

> For streaming responses (`ResponseType.Stream`) the timeout covers through the end of body reading: after the request returns, reading from the stream for longer than `Timeout` also throws `ECONNABORTED`. Note that this is a **total budget for the whole operation** (connect + wait for headers + read body combined), not a per-read idle timeout — time spent waiting for headers is deducted from the body's remaining budget. For a long-connection style idle timeout, control it yourself with a `CancellationToken`. The token is passed to every underlying read/write, so synchronous `Read`/`Write`/`Flush` honor it too (implemented as a blocking wait over the async core; a timeout/cancellation during the read interrupts it immediately), but that occupies the calling thread — prefer `ReadAsync` where possible.
>
> The timeout value is capped at about **49.7 days** (the underlying timer is `uint.MaxValue-1` milliseconds): anything larger is **clamped to that cap** instead of throwing, so `TimeSpan.MaxValue` is safe too; use `Timeout = TimeSpan.Zero` for a genuinely unlimited timeout. Negative values throw `ERR_BAD_OPTION_VALUE`. Cancellation/timeout exceptions keep the underlying `OperationCanceledException` as `Cause`, so `AxiosException.ToJson()` and logs show the trigger point.

## Connection pool

### Why it exists

.NET's `SocketsHttpHandler` already reuses TCP connections, but wrongly creating a new `HttpClient`/handler per request exhausts sockets, while a single global `HttpClient` cannot be tuned per destination site or reclaimed in time. `HttpConnectionPool` adds:

1. **Per-origin reuse**: each `scheme://host:port` gets a tuned `SocketsHttpHandler` shared by all requests, reusing TCP keep-alive connections. Host names are normalized via IDNA/punycode — `münchen.de` and `xn--mnchen-3ya.de` are two spellings of the same domain (the HTTP Host header must be ASCII, so it is normalized before sending) and share a node; IPv6 keeps its brackets;
2. **Connection throttling**: `MaxConnectionsPerServer` caps the TCP connections to a single site; surplus requests queue at the connection layer, giving steadier throughput under high concurrency;
3. **Lifetime governance**: max pooled connection lifetime (to smooth DNS changes), idle timeout, connect timeout;
4. **Idle origin eviction**: a background timer releases handlers for sites with no traffic for a while;
5. **Multi-instance sharing**: several `AxiosClient`s (e.g. for different business modules) can share one pool;
6. **Observability**: `GetStatistics()` reports the number of origin nodes, cumulative leases, per-node active leases, and so on.

### Usage

```csharp
using System.Security.Authentication;
using AxiosNet.Pooling;

// 1) Every AxiosClient creates its own pool by default, or you can configure it explicitly
var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,           // max connections per site, default 100
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    ConnectTimeout = TimeSpan.FromSeconds(15),
    IdleHandlerLifetime = TimeSpan.FromMinutes(5),
    EvictionInterval = TimeSpan.FromSeconds(30),
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5,
    AutomaticDecompression = true,          // gzip/deflate/br
    MaxEndpointCount = 1024,                // cap on origin nodes (0 / negative = unlimited)
    UseCookies = false,                     // when on, one shared CookieContainer for the whole pool
    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,   // optional: restrict TLS versions
    ConfigureSslOptions = ssl =>            // optional: client certs / ALPN / cipher suites and other SslClientAuthenticationOptions
    {
        // ssl.ClientCertificates.Add(myCert);
    }
});

// 2) Recommended: share one global pool across multiple clients
await using var sharedPool = new HttpConnectionPool(new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 64
});
using var orderClient  = new AxiosClient(pool: sharedPool);
using var payClient    = new AxiosClient(pool: sharedPool);
using var payClient2   = Axios.Create(c => c.BaseAddress = "https://pay.example.com/", sharedPool);  // the static facade takes a shared pool too

// 3) Observe pool state
ConnectionPoolStatistics stats = sharedPool.GetStatistics();
// stats.EndpointCount / TotalLeases / Endpoints[*].ActiveLeases ...

// Manually evict idle origins (also done automatically by the background timer)
sharedPool.EvictIdleEndpoints();
```

> Fail-fast: a shared pool's connection behavior (redirects / proxy / credentials / cookies / pool options) is fixed when the pool is created. Setting `MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials` / `PoolOptions` on a shared-pool client's defaults, or passing both `pool` and `poolOptions` to `AxiosClient`, throws `ERR_BAD_OPTION_VALUE` instead of being silently ignored — configure these on the `ConnectionPoolOptions` used to create the pool, or let that client build its own pool (do not pass `pool`).
>
> Two defaults: when `Proxy` is not set explicitly the pool **does not use system/environment proxies** (avoiding surprising routing from environment variables); set `Proxy = HttpClient.DefaultProxy` to opt into the system proxy. When `EvictionInterval ≤ 0` the background eviction timer is not started (only manual `EvictIdleEndpoints()` then). The pool clones the options passed in, and `pool.Options` returns a copy too — mutating the object you hold afterwards does not affect an already-created pool.
>
> `MaxEndpointCount` is a resource-surface guard: when destination hosts come from user input (short links, webhooks, per-tenant domains), the number of origin nodes is exactly what can be amplified. At the cap the least-recently-used idle node is evicted first; only when every node is busy does the pool refuse with `ERR_BAD_OPTION_VALUE` — requests are never dropped silently.
>
> With `UseCookies = true` the whole pool shares **one** `CookieContainer` (no longer one per origin): cookies are scoped by domain/path rather than by port, so a session set by `auth.example.com` is sent to `api.example.com`, matching browser / axios `withCredentials` semantics. Cookies of unrelated domains are still isolated by domain matching.
>
> `Dispose()` is a **graceful shutdown**: idle nodes are released immediately, while nodes with in-flight requests wait until their last lease is returned — closing a client never turns an in-flight request into `ObjectDisposedException`. Use `Clear()` when you want to cut every connection right now.
>
> `AxiosClient.Dispose()` does not interrupt in-flight requests either: while requests are still in flight, disposal of the injected `HttpClient`/`Handler` or the client-owned pool is deferred until the last request finishes (that request performs it), after which no new requests are accepted (`ERR_CANCELED` — never a raw `ObjectDisposedException`).

> Performance notes: sequential and concurrent requests both reuse established TCP connections (measured in the repository tests and samples: 5 sequential requests open only 1 TCP connection; 6 concurrent slow requests with `MaxConnectionsPerServer=2` peak at exactly 2 concurrent). Disposing the pool at shutdown releases all connections; a shared pool is not destroyed when one client is disposed (only the owner disposes it).

### DNS changes and connection expiry

The pool stores handlers, not IPs; the IP lives on the TCP connections inside the handler. So the convergence window after a DNS switch is determined by "how much longer can this connection be reused":

| Control | Effect |
| --- | --- |
| `PooledConnectionLifetime` | How long one connection may be reused. After expiry a fresh connection is used at **next acquisition** (in-flight requests are not interrupted), and the new connection resolves DNS again. Default 10 minutes; 30–60 seconds for fast failover; `Timeout.InfiniteTimeSpan` disables rotation entirely |
| `PooledConnectionIdleTimeout` | Idle connections are closed outright (default 2 minutes), so low-traffic origins converge sooner |
| `IdleHandlerLifetime` | The handler for a whole origin is evicted once idle, fully resetting that site's connection state |
| `HttpConnectionPool.Clear()` | Releases all origin nodes immediately, **interrupting in-flight requests** — only suitable for manual ops intervention or process shutdown |

Field experience: while connections are healthy the pool will not proactively disconnect just because DNS changed — that is the payoff of keep-alive, and it also means accepting a staleness window of at most one `PooledConnectionLifetime`. When you need service discovery (Consul / Nacos / SRV / weights), use `HandlerFactory` + `ctx.CreateDefaultHandler()` to obtain the handler and set `ConnectCallback` to choose the target address yourself; it is invoked once **per new TCP connection**, so it naturally refreshes with the lifecycle.

A complete runnable demo is sample item 20 (`Examples/DnsRefreshExample.cs`): two local servers simulate old and new addresses for the same domain, showing within seconds that "requests still hit the old address after the DNS switch → the new address is used once the lifetime expires", plus the immediate convergence of `Clear()`.

## Injecting a custom handler

The connection layer has two pluggable points; choose based on whether you want to keep the pool:

| Need | Usage | Uses pool |
| --- | --- | --- |
| Insert `DelegatingHandler`s (logging/signing/retries) while keeping per-origin reuse | `ConnectionPoolOptions.HandlerFactory` | ✅ |
| Take over the connection layer entirely (test stubs, custom transports, an externally supplied handler) | `new AxiosClient(handler: ...)` | ❌ |

### Option 1: HandlerFactory (recommended)

Called once for each origin when its handler is created. `PooledHandlerContext` provides `Origin` / `BaseAddress` / `Options`, plus `CreateDefaultHandler()` — which returns a `SocketsHttpHandler` **already configured per the pool options** (connection cap, lifetime, redirects, decompression, proxy, certificate validation), ready to serve as the inner handler:

```csharp
using AxiosNet.Pooling;

using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,
    HandlerFactory = ctx => new TraceHandler               // your DelegatingHandler
    {
        InnerHandler = ctx.CreateDefaultHandler()          // inner: the tuned SocketsHttpHandler
    }
});
```

Conventions:

- Each origin must get its **own** instance (the same handler cannot be attached to multiple pipelines/origins at once);
- The returned handler is disposed by the pool (when the origin is evicted, on `Clear()` / `Dispose()`);
- Returning `null` throws `InvalidOperationException` on the first request to that origin;
- The factory is called only on "first access to an origin" and the same handler is reused afterwards — connection reuse, `MaxConnectionsPerServer`, idle eviction, and the rest of the pool's capabilities are all preserved.

### Option 2: Inject the handler directly (no pool)

```csharp
// 1) Fully custom transport (example: an in-memory stub that sends no network request)
var stub = new StubHandler();
using var client = new AxiosClient(handler: stub);

// 2) Build the handler chain yourself
var handler = new TraceHandler { InnerHandler = new SocketsHttpHandler() };
using var client2 = new AxiosClient(
    defaults: new AxiosConfig { BaseAddress = "https://api.example.com/" },
    handler: handler,
    disposeHandler: true);   // whether to dispose that handler along with the AxiosClient, default true

// 3) Static facade
using var client3 = Axios.Create(
    c => c.BaseAddress = "https://api.example.com/",
    handler: new TraceHandler { InnerHandler = new SocketsHttpHandler() });
```

> A handler instance can belong to only one pipeline: do not inject it into multiple clients.

Key points:

- Once a handler is injected **no connection pool is created**: `client.Pool` is `null`, `client.UsesConnectionPool` is `false`, and all origins share that one handler (`SocketsHttpHandler` still reuses TCP per host on its own).
- Connection behavior is entirely up to the handler, so setting `MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials` on the defaults throws `ERR_BAD_OPTION_VALUE` (to prevent "configured but not applied").
- It is mutually exclusive with `pool` / `poolOptions`; passing both also throws `ERR_BAD_OPTION_VALUE`.
- Timeouts are still managed per request by the library (`HttpClient.Timeout` is set to Infinite internally, so `c.Timeout` works as usual); data-layer features such as `OnUploadProgress` / `maxRate` / `maxContentLength` are unaffected.

## Features

**Aligned with axios**

- `axios.create(config)` → `Axios.Create(config)`, plus the static facade `Axios.GetAsync(...)`
- Shortcut methods: `Get / Post / Put / Patch / Delete / Head / Options`; `Method` takes the BCL `HttpMethod` directly, so custom verbs (`new HttpMethod("PROPFIND")`) work out of the box
- `baseURL + url` merging, default `params` serialization (arrays as `a[]=1&a[]=2`, nested objects as `o[k]=v`, nulls skipped)
- `data` encoding is decided by `config.ContentType` (default `application/json`, independent of the data type): objects/dictionaries/dynamic/collections all become JSON; set `application/x-www-form-urlencoded` to send a key-value form; set `multipart/form-data` to have `FormFile`/`Stream`/`byte[]` assembled into file parts; `string/byte[]/Stream/HttpContent` pass through untouched
- Every shortcut method (`Get/Post/Put/Patch/Delete/Head/Options`) accepts a `data` payload of any type
- Request/response interceptors (sync & async, `Eject`, request interceptors run in reverse registration order, response interceptors in registration order, rejected branches can recover)
- `validateStatus` for custom acceptable status codes
- `timeout` (`ECONNABORTED`), external cancellation (`ERR_CANCELED`), network errors (`ERR_NETWORK`), custom `timeoutMessage`
- Error codes aligned with axios: `ERR_BAD_REQUEST` (4xx) / `ERR_BAD_RESPONSE` (5xx) / `ERR_INVALID_URL` / `ERR_BAD_OPTION_VALUE`, with `Axios.IsAxiosError` / `Axios.IsCancel` predicates
- `auth` (HTTP Basic; the username may not contain a colon — RFC 7617 splits on the first one — while the password may), `withCredentials`, proxy, automatic redirects (`maxRedirects: 0` disables them entirely), automatic gzip/deflate/br decompression
- `axios.getUri(config)` → `client.GetUri(config)`: builds the URL without sending a request (debugging/logging/pre-signing)
- `maxContentLength` / `maxBodyLength`: size caps for response and request bodies; exceeding them throws `ERR_BAD_RESPONSE` / `ERR_BAD_REQUEST` with the actual byte count in the message. With no cap set, a body larger than a single array can hold (about 2GB, `Array.MaxLength`) also throws `ERR_BAD_RESPONSE` and points you at `ResponseType.Stream`, instead of a bare `OutOfMemoryException`
- `onUploadProgress` / `onDownloadProgress`: progress events (loaded/total/percentage/average rate/ETA) in both directions; on a 307/308 redirect or a retry that resends the body the counts **accumulate across attempts**, so the progress never jumps back (percentage capped at 100%)
- `maxRate`: one-way upload/download throttling (bytes/second)
- `paramsSerializer` / `formFieldNameSelector`: custom query string and form field naming (the field-name customization of axios's `formSerializer`)
- `transformRequest` / `transformResponse`: pre-request and post-response transform chains (thread-safe, mutable at runtime; assigning any `IList` wraps it into the thread-safe implementation, so enumeration always sees a snapshot)
- 🔧 **Pluggable content serialization**: JSON (System.Text.Json) by default with **XML built in** (`XmlContentSerializer`); `JsonSerializerOptions` overridden per "global → instance → request"; implement `IContentSerializer` (two text methods + a pair of byte-channel hooks `SerializeToBytes` / `Deserialize(bytes)` for binary formats + an optional `MediaType` self-declaration) to switch to YAML / Protobuf or replace the JSON engine via `AxiosContent.Serializer`, with the request header aligned automatically
- `postForm` / `putForm` / `patchForm`: one-liners for `multipart/form-data` or forms
- Full response shape: `Data / Status / StatusText / Headers / Config / Request`

**Additional capabilities**

- 🔌 **Connection pool** `HttpConnectionPool`: pools and reuses `SocketsHttpHandler` per `scheme://host:port`, with `MaxConnectionsPerServer` throttling, connection lifetime and idle recycling, background eviction of idle origins, one pool shared by multiple clients, and lease statistics
- 🧩 **dynamic support**: `GetAsync<dynamic>` / `ResponseType.Dynamic` lets you write `data.address.city` or `data.items[0].id` without declaring a DTO (mirroring `response.data.xxx` in JS)
- 📦 Generic strongly-typed responses via `GetAsync<T>` alongside non-generic `JsonElement` responses
- ⬇️ **File download**: `GetAsync<Stream>` streams to disk with constant memory, `GetAsync<byte[]>` grabs all bytes at once; connections return to the pool when the stream is disposed (connection reuse is verifiable)
- ⬆️ **File upload**: raw stream passthrough (`Stream`/`byte[]` plus a file `ContentType`) and `multipart/form-data` (`FormFile`, repeated same-name parts); the sample server parses byte-exactly and reports field name/file name/size/SHA256
- 📊 **Traffic governance**: progress callbacks + one-way rate limiting + size caps, all in pure BCL (wrapping `HttpContent.SerializeToStreamAsync` and `Stream`), with no reliance on native `HttpClient` features
- 🧵 **Custom handler injection**: `ConnectionPoolOptions.HandlerFactory` wraps your `DelegatingHandler` around the default `SocketsHttpHandler` per origin (logging/signing/retries/telemetry) while keeping connection reuse; `new AxiosClient(handler: ...)` takes over the connection layer entirely (bypassing the pool)
- 📝 **Declarative API client** (built into the main package, namespace `AxiosNet.WebAPI`): request models + route/parameter attributes (`[Get("users/{id}")]`, `[Path]` / `[Query]` / `[Header]` / `[Body]`) + the `IAuthorizationProvider` authorization extension point (built-in Bearer), with `client.RequestAsync(request)` inferring its return type from `IApiRequest<TResponse>` — the Refit-style declarative experience on top of the full AxiosNet pipeline
- 💉 **Singleton DI registration** (separate package [`AxiosNet.DependencyInjection`](https://www.nuget.org/packages/AxiosNet.DependencyInjection)): `AddAxiosClient` / `AddApiClient` (self-built / facade / named multi-site modes) register the clients as singletons in one line, with TryAdd semantics preventing duplicate pools; a registered `IAuthorizationProvider` singleton is picked up as the authorization provider automatically; multiple sites are resolved by name through `IApiClientResolver`

## axios ↔ AxiosNet mapping

| axios (JS) | AxiosNet (.NET) |
| --- | --- |
| `axios(config)` | `client.RequestAsync(new AxiosConfig { ... })` |
| `axios(url, config)` | `client.GetAsync(url, c => { ... })` |
| `config.method` | `c.Method` (`System.Net.Http.HttpMethod`, defaults to `HttpMethod.Get`) |
| `axios.get/post/put/patch/delete/head/options` | `GetAsync / PostAsync / PutAsync / PatchAsync / DeleteAsync / HeadAsync / OptionsAsync` |
| `axios.create(config)` | `Axios.Create(config)` / `Axios.Create(config, sharedPool)` (the second argument accepts a shared pool) |
| `axios.getUri(config)` | `client.GetUri(config)` / `client.GetUri(c => ...)` / `Axios.GetUri(...)` |
| `axios.postForm / putForm / patchForm` | `PostFormAsync / PutFormAsync / PatchFormAsync` (multipart by default) |
| `axios.defaults` | `Axios.Defaults` / `client.Defaults` |
| `res.data / status / statusText / headers / config` | `Data / Status / StatusText / Headers / Config` |
| `interceptors.request.use(ok, err)` | `Interceptors.Request.Use(...)` (sync/async overloads) |
| `interceptors.response.use(...)` | `Interceptors.Response.Use(...)` |
| `interceptors.x.eject(id)` | `Eject(id)` |
| `config.params` | `c.Params` (objects/dictionaries/arrays/nested) |
| `config.data` (object → JSON) | `c.Data` (any type; encoding decided by `c.ContentType`, JSON by default) |
| `config.ContentType` | Request body media type (default `AxiosContentType.Json`, a full value including charset is accepted; decides how `data` is encoded) |
| — (no equivalent) | `AxiosContentType` presets: `Json` / `FormUrlEncoded` / `MultipartFormData` / `OctetStream` / `Xml` / `JsonPatch` / `ProblemJson` / `TextPlain` / `TextHtml` |
| `responseType: 'json'/'text'/'arraybuffer'/'stream'` | `ResponseType.Json/Text/ArrayBuffer/Stream` |
| — (natively dynamic in JS) | `GetAsync<dynamic>` / `ResponseType.Dynamic` |
| — (no equivalent setting) | `RequestEncoding` / `ResponseEncoding` (request/response text encoding) |
| `validateStatus` | `c.ValidateStatus = status => ...` |
| `timeout` / `AbortController` | `c.Timeout` / `c.CancellationToken` |
| `auth: {username,password}` | `c.Auth = new AxiosAuth(user, pass)` |
| `withCredentials` / `proxy` / `maxRedirects` | Same-named properties (**instance-level only**, see below) |
| `maxContentLength` / `maxBodyLength` | Same-named properties (`long?`, bytes; exceeding them throws `ERR_BAD_RESPONSE` / `ERR_BAD_REQUEST`) |
| `maxRate` | `c.MaxRate` (bytes/second, shared by upload and download) |
| `onUploadProgress` / `onDownloadProgress` | Same-named properties (`Action<AxiosProgressEvent>`) |
| `timeoutMessage` | `c.TimeoutMessage` |
| `paramsSerializer` | `c.ParamsSerializer` or the instance-level `Defaults.ParamsSerializer` (returns an already-encoded query string) |
| `formSerializer` (field naming/nesting style) | `c.FormFieldNameSelector` (`Func<string?, string, string>`, `(parentName, currentName) => finalName`; defaults to `prefix[name]`) |
| `transformRequest` / `transformResponse` | Same-named properties (`IList<Func<object?, AxiosConfig, object?>>`, executed in order, appendable) |
| `AxiosError.code` | `AxiosException.Code` (same string values), plus `Status` / `Cause` / `ToJson()` |
| `axios.isAxiosError(e)` / `axios.isCancel(e)` | `Axios.IsAxiosError(e)` / `Axios.IsCancel(e)` |
| (no built-in equivalent) | `HttpConnectionPool` / `ConnectionPoolOptions` |

## Differences from axios

Intentional differences driven by the .NET platform:

1. Methods use the .NET naming convention `XxxAsync` (`GetAsync` corresponds to `axios.get`);
2. `timeout` uses `TimeSpan` rather than a millisecond count;
3. For non-generic responses, `Data` is a `JsonElement` for textual bodies and `byte[]` for binary ones (use the generic `GetAsync<dynamic>` when you need JS-style dynamic access);
4. Cancellation uses `CancellationToken` (semantically equivalent to `AbortController`);
5. **The request body encoding is decided by `ContentType`** (JSON by default) and is never inferred from the data type: dictionaries are not automatically turned into forms, so you must set `ContentType = AxiosConfig.FormUrlEncodedContentType` explicitly when you need a form;
6. The connection pool is an extension of this library; axios (browser/XHR, Node http) has no isomorphic implementation;
7. Request/response text encoding can be controlled explicitly via `RequestEncoding` / `ResponseEncoding` (axios has no equivalent setting);
8. `proxy` / `credentials` / `withCredentials` / `maxRedirects` can only be set at the instance level; setting them on a single request **throws immediately** with `ERR_BAD_OPTION_VALUE` (axios silently ignores them — this library chooses fail-fast). **Repeating the instance's own equivalent setting** is allowed and means "write nothing" (`withCredentials` / `maxRedirects` compare by **value**; `proxy` / `credentials` / `poolOptions` have no dependable equality, so they compare by **reference** — only the exact object from the instance defaults is accepted). Setting them on a shared-pool client's defaults also throws immediately, because connection behavior is decided by the pool and must be configured on `ConnectionPoolOptions`;
9. `maxRate`, `onUploadProgress` / `onDownloadProgress` are implemented by the library at the `HttpContent` and `Stream` layers (`HttpClient` has no native support), so for custom `HttpContent` the "encoded bytes" counted are the bytes actually written to the network stream;
10. `transformRequest` / `transformResponse` take `object?` rather than a generic chain (C# cannot discard a type and narrow it back as freely as JS can); cast inside the delegate when you need strong typing;
11. `config.method` uses the BCL `HttpMethod` directly (there is no bespoke enum here), so any custom verb can be sent; axios's adapter corresponds to `HttpMessageHandler` on the .NET side, with `ConnectionPoolOptions.HandlerFactory` or `new AxiosClient(handler: ...)` as the entry points.
12. **Headers are validated on write**: a header name must be an RFC 7230 token and its value must not contain `CR` / `LF` / `NUL`, otherwise `AxiosHeaders.Set` throws `ArgumentException` (axios/JS does no such check). Browser/Node HTTP stacks split CRLF themselves; .NET's `TryAddWithoutValidation` does not — without this check the library would leave an HTTP request-splitting (CWE-113) door open;
13. **`WithCredentials` cookies are shared per domain** (one container for the whole pool instead of one per origin), so sessions across sub-domains / alternate ports behave like a browser;
14. `Timeout` is capped at about 49.7 days; larger values are clamped to the cap instead of throwing (`Timeout = 0` is the real unlimited timeout);
15. **`params` uses strict RFC 3986 encoding** (`Uri.EscapeDataString`), whose character set differs from axios's default `encode()`: spaces become `%20` instead of `+`, and `!'()*` plus `,:$` are encoded too. That encoding is actually a better fit for OSS/S3-style pre-signing; pass a custom `ParamsSerializer` when you need axios's exact form;
16. **Multipart field names and file names are validated before sending**: `"`, `\`, `CR`/`LF` and control characters would break the quoted-string in `Content-Disposition`, so they throw `ERR_BAD_OPTION_VALUE` (both `FormFile.FileName` and `FormFile.FieldName` throw `ArgumentException` **at construction time**, not when the request is issued) instead of letting the server silently receive a garbled key (browser `FormData` handles these inconsistently, so axios cannot rely on any particular behavior); assembly failures (including a root node with no field name) also report `ERR_BAD_OPTION_VALUE` and **dispose any `Stream` already handed in** (`FormFile.Content` / a bare `Stream` part) — an already-open handle is never pushed back onto the caller;
17. **`ContentType` is a `string`, not an enum** (a `Content-Type` is media type + `charset`/`boundary` parameters, which an enum cannot express). Common values ship as `AxiosContentType` constants — the same "presets plus arbitrary custom values" idea as `HttpMethod.Get` together with `new HttpMethod("PROPFIND")`.


## Sample project

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) is a **ready-to-run, offline** console sample: it embeds a `TcpListener`-based HTTP/1.1 keep-alive demo server and covers 24 categories of integration calls:

1. The global static facade `Axios.GetAsync`
2. `Axios.Create` (baseURL / default headers / strongly-typed response)
3. POST an object as JSON round-trip
4. POST a dictionary as a form
5. `params` array/nested serialization
6. HTTP Basic Auth
7. **dynamic response**: `GetAsync<dynamic>` (members/nesting/root array, plus `ResponseType.Dynamic`)
8. **dynamic request** (`ExpandoObject` as body and as params)
9. text / arraybuffer
10. Request/response interceptors (including 401/404 error recovery)
11. Error codes, timeouts, cancellation, validateStatus
12. Connection pool (connection reuse, concurrency cap, multi-client sharing)
13. **multipart/form-data**: fields + a batch of files (in-memory and local files, parsed part-by-part byte-exactly by the server)
14. **File download**: `GetAsync<Stream>` streaming to disk (1 MB demo), `GetAsync<byte[]>`, file name from `Content-Disposition`
15. **getUri preview / maxContentLength interception / maxRedirects: 0**
16. **Upload and download progress callbacks + maxRate throttling**
17. **Using `HttpMethod` directly + custom handler injection** (`PROPFIND` custom verb, direct `DelegatingHandler` injection, pool `HandlerFactory`)
18. **Single-file upload**: raw stream (`/upload/raw`) and multipart single file (`/upload/single`), with the server reporting exact byte count + SHA256
19. **Custom JSON serialization**: implement `IContentSerializer` and replace System.Text.Json with **Newtonsoft.Json** (business objects on both the request and response side go through the same engine)
20. **DNS changes and connection expiry**: two local servers simulate old and new addresses for the same domain, demonstrating natural convergence when `PooledConnectionLifetime` expires and the immediate invalidation of `pool.Clear()`
21. **Bearer token auth**: login issues tokens, an request interceptor attaches them automatically, the server verifies them, and a 401 triggers the refresh-and-retry loop
22. **Request signing**: HMAC-SHA256(token + timestamp + nonce + method + path + body hash), demonstrating tamper protection and replay protection
23. **XML serialization (built in)**: switch to `XmlContentSerializer.Instance` — request body, strongly-typed response, non-generic raw text, scalar request bodies bypassing the serializer, the `AxiosContent.Register` auto mode (serializer picked by Content-Type), and the decoupling of forms/multipart from the serializer
24. **Binary formats (byte channel both ways)**: the serializer overrides `SerializeToBytes` + the byte inlet — request objects become byte bodies with a self-declared `application/octet-stream`, and responses reach the byte inlet with zero decoding; compared against the non-generic / `byte[]` paths, plus the caller's explicit `ContentType` overriding the self-declared header

```bash
dotnet run --project samples/AxiosNet.Simple
```

The app starts into an **interactive menu**: reflection lists every sample's class name and title (24 core + 8 declarative = 32 items; sample classes are never registered by hand — add one and it appears), type a number to run a single sample, `a` to run them all in order, `q` to quit; after each sample finishes you are back at the menu. The numbering above matches menu items 1-24.

> Items 13 and 18 above cover uploads: `DemoServer` uses `MultipartReader` to parse multipart **byte-exactly** and reports each part's field name, file name, content type, byte count, and SHA256; the single-file scenario also provides `/upload/raw` (raw stream) and `/upload/single` (multipart single file).
>
> Item 19 is the only sample that needs an extra NuGet package (`Newtonsoft.Json`), and only to demonstrate **engine replacement** — the library itself still has zero third-party runtime dependencies; if you do not need custom serialization you can remove `Newtonsoft.Json` from the sample project.

The `WebAPI/` folder of [`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) is the companion sample for the declarative client (namespace [`AxiosNet.WebAPI`](#declarative-api-client-axiosnetwebapi-refit-style)) (also ready-to-run and offline): request and response models live in `WebAPI/ApiModels.cs`, and 8 examples make real HTTP round-trips:

1. **GET detail**: `[Path]` binding + the response generic inferred from the request model
2. **GET list**: unannotated properties bind as query by default, `ids[]=` array binding, `client.GetUri(request)` preview
3. **POST create**: `[Body]` request body + the model's `Configure` hook (the server echoes X-Trace back to prove the header arrived)
4. **Authorization**: `[AllowAnonymous]` (login skips the whole authorization chain — the provider is never called) + `[Authorize]` (automatic `Bearer` header) + `IAccessTokenProvider` (token only), compared against a layered `IAuthorizationProvider` (runs last, may override) and the built-in `BearerAuthorizationProvider` fixed-token one-liner
5. **DELETE + error handling**: the method comes from the route attribute; a 404 throws `AxiosException` (`ERR_BAD_REQUEST`) with the `ToJson` structured snapshot
6. **Global logging**: `client.Axios.Interceptors` with request/response/error branches (plus a global header)
7. **Signing/verification**: the `SigningSerializer` decorator around the built-in JSON — request body HMAC-SHA256 into `X-Signature`, response verified before parsing; the demo server verifies symmetrically and rejects unsigned requests with 401
8. **Streaming (SSE)**: `RequestStreamAsync` returns `AxiosResponse<IAsyncEnumerable<T>>` — `await` gives you the status code and headers, `await foreach` consumes frames one by one; the demo server streams with `chunked` encoding (terminated by `[DONE]`)

These 8 items live in the same startup menu as the 24 above (menu items 25-32, with their own demo server started lazily on first use):

```bash
dotnet run --project samples/AxiosNet.Simple
```

## Target framework and runtime support

The library **targets `net6.0` only**; a single assembly covers every runtime from .NET 6 onward:

| Consumer target framework | Result |
| --- | --- |
| `net6.0` / `net7.0` / `net8.0` / `net9.0` / `net10.0` / `net11.0` … | References `lib/net6.0` directly, no configuration needed |

- **Why one target is enough**: .NET only guarantees one direction — "a newer runtime can load an older assembly" — and every API the library uses already exists in 6.0, with no 7/8/9-only calls, so there is no value in recompiling per runtime. Shared-framework assemblies such as `System.Text.Json` automatically resolve to the 8.x version in a .NET 8 app at runtime; 6.x is not carried along.
- **.NET Framework is not supported**, nor is .NET Core 3.1 or earlier: the connection pool is built directly on `SocketsHttpHandler` (`PooledConnectionLifetime` / `PooledConnectionIdleTimeout` / `ConnectTimeout` / `SslOptions` / `HandlerFactory`), a type absent from the `netstandard2.0` BCL contract; 3.1 additionally lacks `DateOnly`/`TimeOnly`, `ReadAsStreamAsync(CancellationToken)`, `ConcurrentDictionary.TryRemove(KeyValuePair)`, and a batch of other APIs, so the compatibility work would not be worth the gain.
- **Why the floor is .NET 6**: `DateOnly` / `TimeOnly` and `HttpContent.SerializeToStreamAsync(Stream, TransportContext, CancellationToken)` only arrived around 6.0; going lower would require maintaining another layer of `#if`, and .NET 5 is out of support. The current source has **zero `#if`**.
- **How it is verified**: the test project still targets both `net8.0` and `net6.0`, running the same shipped assembly's 792 cases on each runtime (1584 executions in total, all passing). That exactly covers the most common consumption shape — a .NET 8 app loading the `net6.0` asset.

```xml
<!-- Consumers need no configuration: any target framework >= net6.0 can reference it -->
<PackageReference Include="AxiosNet" Version="1.0.1" />
```

## Build and test

```bash
# Build (Debug): the library produces a single net6.0 asset
dotnet build AxiosNet.sln

# Run all 792 xUnit tests (both test projects target net8.0 + net6.0, so 1584 executions)
dotnet test AxiosNet.sln

# Release
dotnet build AxiosNet.sln -c Release
```

The test project uses a self-contained lightweight HTTP/1.1 keep-alive test server (`TcpListener`, zero NuGet dependencies) that directly observes real TCP connection reuse counts and peak concurrency. Coverage includes:

- Basic requests / all methods / redirects / HEAD / BaseAddress / static facade
- `params` serialization, request body encoding (`ContentType` deciding JSON/form/multipart/passthrough), bodies on all methods, Basic Auth, default headers
- Boundary cases where `ContentType` is decoupled from the data type (dictionaries as JSON by default, explicit forms, multipart files/streams/bytes, root-array files, fixed boundary)
- Request/response interceptors (order, Eject, async, error recovery)
- 4xx/5xx/timeout/cancellation/network errors/validateStatus
- `GetUri` (relative/absolute, array and nested params, Chinese characters and spaces escaped, custom serializer)
- `maxContentLength` / `maxBodyLength` / `maxRedirects: 0` / error model (`Status` / `Cause` / `ToJson` / `IsAxiosError` / `IsCancel`)
- Progress callbacks (upload/download, 100% uniqueness) and `maxRate` throttling (up/down)
- `paramsSerializer` / `formFieldNameSelector` / `transformRequest` / `transformResponse` / `PostForm*` shortcuts
- File download: `GetAsync<Stream>` to disk, `ResponseType.ArrayBuffer`, connection reuse after stream disposal, Content-Disposition file name, download timeout
- dynamic response graphs and dynamic requests (9 dedicated tests covering GetAsync&lt;dynamic&gt; / PostAsync&lt;dynamic&gt; / static facade / explicit responseType precedence)
- Connection pool: connection reuse, max-connection throttling, sharing across instances, idle eviction, multi-origin isolation, dispose semantics, shared-pool fail-fast (connection-level defaults and `pool`/`poolOptions` exclusivity), origin key IDNA normalization (unicode/punycode as the same node, IPv6 brackets preserved), `pool.Options` option isolation
- Interceptors: snapshot caching (reused when unchanged) and immediate invalidation on Use/Eject/Clear; range validation of limit-style options targets the config **after** the interceptors (an interceptor writing `Timeout = -1` is rejected, while an interceptor repairing a caller's invalid value is allowed through)
- Response reading: close-delimited responses without Content-Length (buffered unknown-length branch), zero-copy return when the length allows exact preallocation
- Generic routing: `GetAsync<Stream>` combined with an incompatible explicit `responseType` is uniformly wrapped as `ERR_BAD_RESPONSE` (no raw `InvalidCastException` leaking out)
- Custom handlers: passthrough of custom `HttpMethod`s such as `PROPFIND`, direct `DelegatingHandler` injection (including `disposeHandler` semantics), `HandlerFactory` called once per origin / wrapping the default handler / throwing `InvalidOperationException` on a null return, plus fail-fast with pool/poolOptions and instance-level options
- Failure and concurrency regressions (`ReviewRegressionTests`): streaming 4xx releasing the connection, truncated responses / error codes for non-JSON dynamic, concurrent mutation of default request headers, the params indexer, `Timeout = 0` override, streaming body timeout, leases not evicted during download
- Pluggable serialization (`JsonCustomizationTests` / `ContentSerializerTests`): a custom `IContentSerializer` driving request/response, instance-level `JsonSerializerOptions`, concurrent add/remove on transform lists; end-to-end XML via `IContentSerializer`, primitives bypassing the serializer, non-generic `JsonElement`/raw text, forms decoupled from the serializer, dynamic failing loudly on XML bodies, binary responses going through the byte inlet (non-UTF-8 trailing byte delivered untouched, no decoding), text serializers falling back gracefully on `octet-stream`, the binary request outlet (object → byte body + self-declared `MediaType`, no charset appended, explicit `ContentType` wins, forms/scalars never enter the byte channel), the built-in XML serializer (declaration encoding follows the declared `ContentType` charset / `RequestEncoding`, no namespace noise, `[XmlRoot]` namespace, DTD rejected), releasing the caller's `Stream` when multipart assembly fails (both the invalid-field-name and root-without-name paths report `ERR_BAD_OPTION_VALUE`), the boundary around `JsonDocument` targets, scalar request bodies and dictionary routing, self-declared `MediaType` filling the request header (explicit / instance-default `ContentType` wins, `null` is non-breaking)
- Security & hardening (`HardeningTests`): header values/names with CR/LF or non-token characters rejected (HTTP request smuggling guard), `poolOptions.MaxAutomaticRedirections` no longer overwritten by an implicit default, truncated streaming body (and `Timeout=0`) reported as `ERR_NETWORK`, oversized `Timeout` clamped to ~49.7 days, negative limits fail fast, params appended before the URL fragment, `PostAsync(url, null)` throwing `ArgumentNullException`, `maxContentLength` enforced for streaming and chunked responses, cookies shared across origins, cancellation keeping its `Cause`, `JsonElement` params, `byte[]` params (base64), pool origin cap and graceful `Dispose` (in-flight requests are not killed by closing the client; dispose races never leak a raw `ObjectDisposedException`)
- Global-audit regressions (`Audit20261022GlobalAuditFixTests`, 25 cases): per-request `MaxRedirects` validated against the pool's effective value (no more short-circuit against the property default 5), the built-in JSON byte inlet strips the UTF-8 BOM, all-null query bindings no longer wipe instance-default `params`, protocol-relative URLs (`//host`) resolve against the baseURL scheme, a serializer throwing `OperationCanceledException` surfaces as `ERR_CANCELED` instead of `ERR_BAD_RESPONSE`, an invalid HTTP method token is normalized to `InvalidOperationException` and cached, `DateOnly` / `TimeOnly` / `Uri` scalar classification (one shared list with params serialization, so default-body and Path/Header checks no longer drift), `Defaults.RemoveHeaders` behaves identically on the shortcut and `RequestAsync` paths, a request interceptor returning a null Task fails with a clear error, `GetStatistics` taken from a single snapshot, capacity eviction picks the least-recently-used endpoint, blank `ContentType` fails fast, a declared `ContentType` charset wins the encoding (fail-fast when it contradicts `RequestEncoding`), and duplicated connection-level options on a self-built pool fail fast
- Second global-audit round (`Audit20261022MultipartXmlFixTests`, 12 cases): multipart failures release the caller's `Stream` (invalid field name and root-without-name paths, both reporting `ERR_BAD_OPTION_VALUE`), `FormFile.FieldName` fails fast at assignment like `FileName`, the success path keeps the same stream ownership, and the XML declaration encoding follows the declared `ContentType` charset / `RequestEncoding` (utf-8 by default); `InterceptorTests` adds two more: range validation of limit-style options targets the config after the interceptors (an interceptor writing `Timeout = -1` is rejected, repairing a caller's invalid value is allowed)
- P2 fixes (`Audit20261022P2FixTests`, 5 cases): upload progress accumulates across a 307 redirect and stays monotonic with the percentage capped at 100%; assigning any `IList` to a transform chain wraps it into the thread-safe implementation (concurrent add/remove plus enumeration never throws) and an already-wrapped instance is not double-wrapped; a Basic username containing a colon is rejected at construction while the password may contain one and the header is encoded correctly
- Third global-audit round (`Audit20261022Round3FixTests`, 10 cases): a `null` / blank `url` on shortcut methods now fails fast with `ERR_INVALID_URL` (it used to be silently interpreted as "request baseAddress", so a `POST` would land on the root path); exceptions thrown by upload/download progress callbacks and by a custom `paramsSerializer` are normalized to `AxiosException` (the upload side is no longer wrapped by HttpClient into `HttpRequestException` and misreported as `ERR_NETWORK`; download reports `ERR_BAD_RESPONSE`, config errors report `ERR_BAD_OPTION_VALUE`, and the original exception goes into `Cause`); replaying `response.Config` onto another instance now uses the target instance's `BaseAddress` (same origin as connection-level options, so requests no longer go back to the old server); a scalar request body takes its `Content-Type` from the effective serializer (injecting XML no longer yields an `application/json` header with a bare scalar body); `AxiosHeaders` copy-on-write matches a plain-dictionary model instance-by-instance across 3000 random operations (no cross-writes between copies, no missed copies from reference counting); built-in Bearer scheme handling (a `Bearer` prefix is added when there is no space, an explicit scheme is kept as-is)
- Third-round pending-item fixes (`Audit20261022Round3PendingFixTests`, 2 cases): `AxiosHeaders`' "take an exclusive store" now retries when the old store was already replaced by another thread instead of handing out a store that `Clone` may just have counted — a concurrent write plus clone no longer silently pollutes the snapshot (the interleaving is pinned down with reflection plus a held lock, not left to chance, so it belongs in the regular suite); download progress no longer locks itself after the first report when the declared `Content-Length` is 0 while the body keeps coming (or an injected handler does not truncate at the declared length) — previously 512 KB produced a single `loaded=first chunk / total=0` report, now it keeps reporting through EOF
- Declarative client (`AxiosNet.Tests/WebAPI/`, 66 cases): route/Path/Query/Header/Body binding end-to-end (path escaping, absolute URL overriding BaseAddress, model- and call-site Configure hooks, inherited property bindings), default body (POST/PUT/PATCH + exactly one unannotated non-scalar property; scalar guard, ambiguity with multiple properties stays query, explicit `[Query]` does not block, DELETE excluded), multi-binding (Path+Header, Query+Header, scalar Path+Body+Header in all three places, complex object Query+Body; non-scalar Path/Header rejected at build time), `[ApiIgnore]` (query exclusion, does not block default body, combining with a parameter attribute throws), edge cases (case-variant placeholders, default-body null/scalar-array semantics, Guid and record models, PROPFIND custom verb, direct [HttpApi("delete", …)] normalization, CRLF injection fail-fast, raw byte[]/Stream responses, connection reuse, facade guards), built-in Bearer (static/factory/scheme-prefixed) and custom authorization (skipping by request type, seeing the final config, no half-sent request on auth failure), model definition validation (missing route, unbound/extra placeholders, malformed braces, duplicate placeholder, multiple bodies, duplicate names, null path value) and error caching
- Singleton DI registration (`AxiosNet.DependencyInjection.Tests`, 40 cases): `AddAxiosClient` registration shape and configure application, TryAdd first-wins (with configure side effects skipped too), end-to-end requests from a resolved client; `AddApiClient` in both modes (BaseAddress and configureDefaults applied, facade reusing the registered `AxiosClient`, resolve-time error when none is registered, shared client surviving facade disposal, self-built and facade registrations independent), container `IAuthorizationProvider` auto-wiring with explicit `configure` taking precedence, repeated registration first-wins, argument guards; named multi-site (independent client and BaseAddress per site, `Names` in registration order, Get/TryGet and case sensitivity, unknown-name error listing available sites, duplicate name rejected at registration, per-site defaults and authorization, container provider as fallback, named sites exposed only through the resolver, coexistence with the facade overload, end-to-end requests, a throwing site `configure` not corrupting other sites, `Get` after resolver disposal throwing `ObjectDisposedException` (resolved and never-resolved sites alike, ruling out owner-less pool leaks))

## Overall pipeline

A single request passes through 9 stages from call to `AxiosResponse`. What threads the pipeline together is **one effective config** (instance `Defaults` merged with the per-request items) — the `Headers` that interceptors mutate, the `Data` that `transformRequest` replaces, and the request headers that serializers write back all act on it, which is why they take effect on the final request.

```text
caller
                                     │  Axios.GetAsync / client.GetAsync<T> / RequestAsync
                                     ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 1 Entry & config merge                                                 │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ Shortcut methods assemble an AxiosConfig (url / method / data)         │
│ Merge instance Defaults -> effective config                            │
│ Validation: connection-level / negative values fail fast               │
└────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 2 Request interceptors (Interceptors.Request)                          │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ Reverse registration order - async-capable - may mutate Headers        │
└────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 3 TransformRequest chain                                               │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ Runs after interceptors, before encoding; transforms Data              │
└────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 4 URL assembly + request body                                          │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ UrlBuilder: baseURL + url + serialized params                          │
│ RequestBodyFactory dispatches on ContentType:                          │
│   object/dictionary/dynamic/collection -> SerializeToBytes             │
│     or Serialize (byte channel first, null falls back to text;         │
│     header from the declared MediaType)                                │
│   scalars -> text as-is (bypasses the serializer)                      │
│   form/multipart -> assembled in-library (bypasses it too)             │
│   string/byte[]/Stream/HttpContent -> passed through untouched         │
│ Upload progress/throttling -> wrapped in ProgressHttpContent           │
└────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 5 Apply headers + acquire connection                                   │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ Default headers + RemoveHeaders + serializer-declared MediaType        │
│ Default: pool.LeaseAsync leases per origin                             │
│   (TCP reuse - concurrency cap - idle eviction)                        │
│ Injected handler: fixed HttpClient (no pool)                           │
└────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 6 Send                                                                 │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ HttpClient.SendAsync(ResponseHeadersRead)                              │
│ SocketsHttpHandler: keep-alive / redirects / auto-decompression        │
└────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 7 Response read (ResponseReader)                                       │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ validateStatus evaluated once (lenient parse vs. throw)                │
│ Response headers -> config.Response (readable in deserialization)      │
│ Stream: wrapped stream handed over; lease travels with the stream      │
│ Buffered: read all bytes -> decode as needed -> ConvertData dispatch   │
│   direct Stream/byte[]/string - scalar parsing - dynamic expansion     │
│   JsonElement - business objects -> IContentSerializer.Deserialize     │
└────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 8 TransformResponse chain -> response interceptors                     │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ Post-deserialization shaping of Data                                   │
│ Response interceptors run in registration order; rejected can recover  │
└────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
┌────────────────────────────────────────────────────────────────────────┐
│ 9 Wrap-up                                                              │
│ ┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈┈│
│ Buffered: dispose the response message + return the lease to the pool  │
│ Streaming: TimeoutStream holds the lease; returned on stream dispose   │
│ Generic: DataCast verifies Data against T                              │
└────────────────────────────────────────────────────────────────────────┘
                                     │
                                     ▼
                                     AxiosResponse / AxiosResponse<T>

Failure at any stage -> AxiosException (unified model: ERR_* code + Cause)
                        -> response interceptor rejected chain: refresh-and-retry or fallback
```

How the stages map onto the source files (read together with the [project layout](#project-layout) below):

| Stage | Key source files |
| --- | --- |
| 1 Entry & config merge | `AxiosClient.Shortcuts.cs` (assembly), `AxiosClient.Config.cs` (`MergeConfig` / `ApplyHeaders`) |
| 2 Request interceptors | `Interceptors/RequestInterceptorManager.cs` |
| 3 TransformRequest | `AxiosClient.cs` request pipeline |
| 4 URL + request body | `Internal/UrlBuilder.cs`, `Internal/RequestBodyFactory.cs` (dispatch), `Internal/FormBodyEncoder.cs` (form), `Internal/MultipartBodyBuilder.cs` (multipart), `Internal/ProgressHttpContent.cs` |
| 5 Headers + connection | `AxiosClient.Config.cs`, `Pooling/HttpConnectionPool.cs` (`LeaseAsync`), `Pooling/PooledEndpoint.cs` |
| 6 Send | `AxiosClient.cs` (`SendAsync`) → the per-origin `HttpClient` |
| 7 Response read | `Internal/ResponseReader.cs`, `Internal/ResponseTextDecoder.cs`, `Internal/ScalarParser.cs`, `Internal/DynamicJson.cs`, `Serialization/` |
| 8 TransformResponse + response interceptors | `Internal/ResponseReader.cs`, `Interceptors/ResponseInterceptorManager.cs` |
| 9 Wrap-up | `Internal/ResponseStreams.cs` (`TimeoutStream` / `ResponseStreamOwner`), `Internal/DataCast.cs` |

> The failure path travels the same chain: an exception thrown at any stage is normalized into an `AxiosException` (`ERR_*` code + `Cause` for the underlying exception) and enters the response interceptors' rejected chain — where you can refresh a token and replay, or return a fallback response (see [Interceptors](#interceptors) and [Error handling](#error-handling)).

## Project layout

```
.
├─ AxiosNet.sln
├─ src/
│  ├─ AxiosNet/                  # library (net6.0 only, compatible with all runtimes from .NET 6 onward)
│  │  ├─ Axios.cs                # static facade (the global axios equivalent)
│  │  ├─ AxiosClient.cs          # instance + request pipeline
│  │  ├─ AxiosClient.Shortcuts.cs# all shortcut overloads (get/post/... + form)
│  │  ├─ AxiosClient.Config.cs   # config merging and header application
│  │  ├─ AxiosJson.cs            # global JSON defaults (JsonSerializerOptions)
│  │  ├─ AxiosContent.cs         # global injection point for content serialization
│  │  ├─ AxiosConfig.cs          # request/instance configuration
│  │  ├─ AxiosContentType.cs     # common Content-Type presets
│  │  ├─ AxiosUrl.cs             # path-segment escaping (AxiosUrl.EncodePathSegment)
│  │  ├─ AxiosHeaders.cs         # case-insensitive headers
│  │  ├─ AxiosResponse.cs        # response (generic/non-generic)
│  │  ├─ AxiosResponseInfo.cs    # read-only response context for deserialization (status/headers/media type)
│  │  ├─ AxiosException.cs       # aligned with AxiosError
│  │  ├─ AxiosProgressEvent.cs   # upload/download progress events
│  │  ├─ FormFile.cs             # multipart file part
│  │  ├─ ResponseType.cs / AxiosAuth.cs          # response shape / Basic auth
│  │  ├─ Interceptors/           # request/response interceptor management
│  │  ├─ Serialization/          # IContentSerializer (2 text methods + a pair of byte-channel hooks + MediaType) + built-in JSON / XML implementations
│  │  ├─ Internal/               # URL/params, request-body dispatch + form/multipart encoding, response reading, traffic metering, dynamic JSON, reflection cache, media-type and scalar classification, status text
│  │  └─ Pooling/                # HttpConnectionPool + PooledHandlerContext (handler injection)
│  │  └─ WebAPI/                 # declarative API client (built into the main package, namespace AxiosNet.WebAPI)
│  │     ├─ ApiClient.cs            # facade: RequestAsync(request) + GetUri(request), a thin wrapper over AxiosClient
│  │     ├─ IApiRequest.cs          # request-model contract (IApiRequest<T> determines the response type) + Configure hook
│  │     ├─ HttpApiAttribute.cs     # route attributes (Get/Post/Put/Patch/Delete/Head/Options)
│  │     ├─ ApiParameterAttributes.cs # parameter-binding attributes (Path / Query / Header / Body)
│  │     ├─ IAuthorizationProvider.cs # authorization extension point + ApiAuthContext
│  │     ├─ BearerAuthorizationProvider.cs # built-in Bearer authorization
│  │     └─ Internal/RequestModelMetadata.cs # binding metadata (cached per model type, fully validated at build time)
│  └─ AxiosNet.DependencyInjection/      # DI singleton registration extensions (separate NuGet package, depends on the AxiosNet main package)
│     ├─ AxiosClientServiceCollectionExtensions.cs # AddAxiosClient: registers a singleton AxiosClient
│     ├─ ApiClientServiceCollectionExtensions.cs   # AddApiClient: registers a singleton ApiClient (self-built / facade / named multi-site)
│     ├─ ApiClientResolver.cs            # IApiClientResolver + site definitions/creation (owner of the named sites' clients)
│     └─ ApiClientOwnedAxiosClient.cs     # internal holder for the self-built and named-site clients (released when the container shuts down)
├─ samples/
│  ├─ AxiosNet.Simple/           # 24 categories of integration calls (embedded demo server + exact multipart parsing + Newtonsoft.Json / XML / binary serializers + DNS refresh + bearer tokens + request signing) + 8 declarative samples under WebAPI/; the startup menu lists all 32 by reflection — type a number to run, a = all, q = quit
│  │  ├─ Examples/               # core integration samples (24 items; IExample classes need no registration to appear in the menu; SampleMenu.cs is the shared interactive menu used by both sample apps)
│  │  └─ WebAPI/                 # declarative client sample (own demo server: 8 examples over real HTTP round-trips, incl. authorization declarations, global logging, HMAC signing/verification and SSE streaming; menu items 25-32)
│  └─ AxiosNet.Simple.DependencyInjection/ # DI extension sample (Generic Host, six scenarios: core singleton / self-built / facade / container authorization / named multi-site / keyed services; IScenario classes under Scenarios/ need no registration to appear in the menu)
└─ test/
   ├─ AxiosNet.Tests/            # xUnit tests (714 cases × net6.0 / net8.0 = 1428 executions)
   │  └─ WebAPI/                 # declarative client tests (66 cases, same project and assembly as the main tests)
   └─ AxiosNet.DependencyInjection.Tests/ # DI extension tests (40 cases × net6.0 / net8.0, reuses the main/WebAPI test infrastructure)
```

## License

[MIT](LICENSE)
