# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/badge/nuget-AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![tests](https://img.shields.io/badge/tests-1162%20passed-success)](#build-and-test)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[简体中文](README.md) | **English**

An axios-style HTTP client for .NET. The request API mirrors JS [axios](https://github.com/axios/axios), and on top of that it adds what server-side code actually needs: an origin-scoped connection pool, retries, a circuit breaker, streaming responses, an in-flight concurrency gate, a declarative API client, and OpenTelemetry instrumentation.

| Item | Value |
| --- | --- |
| Packages | `AxiosNet` (core + declarative client in one package), `AxiosNet.DependencyInjection` (optional) |
| Target | Single target `net6.0`, runs on .NET 6 / 7 / 8 / 9 / 10 and later |
| Dependencies | Zero third-party, BCL only |
| Style | Async first: `Task` + `CancellationToken` |
| Telemetry | Trace / metrics / events on by default, zero cost when nobody listens |

> Repository: [Gitee (itsm/axios-net)](https://gitee.com/itsm/axios-net) is the main repo for PRs and tags; [GitHub](https://github.com/sunyuliang/AxiosNet) is a read-only mirror.

---

## Table of contents

| Group | Sections |
| --- | --- |
| Getting started | [Install](#install) · [Quick start](#quick-start) · [What it supports](#what-it-supports) · [Clients](#clients) |
| Requests | [Methods and URLs](#methods-and-urls) · [Query parameters](#query-parameters) · [Body and ContentType](#body-and-contenttype) · [Forms and uploads](#forms-and-uploads) · [Response types and downloads](#response-types-and-downloads) · [Streaming responses](#streaming-responses) · [Timeouts and cancellation](#timeouts-and-cancellation) · [Progress, rate and size limits](#progress-rate-and-size-limits) · [In-flight concurrency cap](#in-flight-concurrency-cap) |
| Data and errors | [Errors](#errors) · [Interceptors](#interceptors) · [Data transforms](#data-transforms) · [Serialization](#serialization) |
| Resilience | [Retries](#retries) · [Circuit breaker](#circuit-breaker) · [Short circuit](#short-circuit) |
| Infrastructure | [Connection pool](#connection-pool) · [HTTP versions](#http-versions) · [Custom handlers](#custom-handlers) · [Observability](#observability) |
| Engineering | [Declarative API client](#declarative-api-client) · [Dependency injection](#dependency-injection) · [Configuration reference](#configuration-reference) · [axios comparison](#axios-comparison) · [Samples](#samples) · [Runtime and AOT](#runtime-and-aot) · [Build and test](#build-and-test) · [Benchmarks](#benchmarks) · [Project layout](#project-layout) · [License](#license) |

---

## Install

```bash
dotnet add package AxiosNet                       # core client + declarative API client (one package)
dotnet add package AxiosNet.DependencyInjection   # optional: DI registration
```

Referencing the source tree: `dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj`

## Quick start

```csharp
using AxiosNet;

// 1) Raw text: no parsing
var text = await Axios.GetAsync<string>("https://api.example.com/ping");
Console.WriteLine(text.Data);          // "pong"

// 2) dynamic: no DTO, read fields like JS
var dyn = await Axios.GetAsync<dynamic>("https://api.example.com/users/1");
Console.WriteLine($"{dyn.Data.name}, {dyn.Data.address.city}");   // dyn.Data["name"] works too

// 3) Strongly typed: Data is a User
var response = await Axios.GetAsync<User>("https://api.example.com/users/1");
Console.WriteLine($"{response.Status} {response.Data!.Name}");
```

The generic argument decides what `Data` is:

| Call | `Data` type | Use for |
| --- | --- | --- |
| `GetAsync<string>` | `string` (raw, not parsed) | Plain-text APIs, debugging, custom parsing |
| `GetAsync<dynamic>` | `AxiosDynamic` (member / index access, missing members return `null`) | Unstable or unknown schemas, quick checks |
| `GetAsync<T>` | `T` | Production code |
| `GetAsync()` (non-generic) | `JsonElement` (falls back to raw text on invalid JSON, `byte[]` for binary) | Inspecting shape, no type needed |

Add a baseURL, timeout and default headers:

```csharp
// Same as axios.create({ baseURL, timeout, headers })
var http = Axios.Create(c =>
{
    c.BaseAddress = "https://api.example.com/";
    c.Timeout = TimeSpan.FromSeconds(10);
    c.Headers.Set("Authorization", "Bearer xxx");
});

await http.PostAsync("users", new { name = "bob", age = 30 });   // POST https://api.example.com/users
```

Build a URL without sending (same as `axios.getUri`):

```csharp
string url = http.GetUri(c =>
{
    c.Url = "users";
    c.Params = new { page = 2, ids = new[] { 1, 2 } };
});
// -> https://api.example.com/users?page=2&ids[]=1&ids[]=2
```

## What it supports

Four groups of capabilities; details in the linked sections.

**Requests and responses**

| Capability | What you get | How |
| --- | --- | --- |
| Four response shapes | Raw text / `JsonElement` / `dynamic` / strong type | `GetAsync<string>` / `GetAsync()` / `GetAsync<dynamic>` / `GetAsync<T>` |
| Client instance | One baseURL + timeout + default headers | `Axios.Create(c => ...)` / `new AxiosClient(config)` |
| URL only | Build the final URL without sending | `client.GetUri(c => ...)` |
| Query parameters | Object / dictionary / collection / pre-encoded string | `c.Params = new { page = 2 }` |
| Request body | Encoding decided by `ContentType` alone, never guessed | `c.ContentType = AxiosContentType.FormUrlEncoded` |
| Forms and uploads | Multipart fields + files, or a raw stream | `PostFormAsync(...)` / `FormFile` / `Stream` |
| Five response types | JSON / text / bytes / stream / dynamic | `c.ResponseType = ResponseType.Stream` |
| Streaming | NDJSON / SSE / JSON array → `IAsyncEnumerable<T>` | `client.RequestStreamAsync<T>(req, AxiosStreamFormat.ServerSentEvents)` |
| Timeouts and cancellation | Total budget plus `CancellationToken` | `c.Timeout = ...` / `cancellationToken:` |

**Traffic control**

| Capability | What you get | How |
| --- | --- | --- |
| Progress and throttling | Upload/download progress, byte rate cap, size caps | `c.Callbacks.OnDownloadProgress` / `c.Limits.MaxRate` |
| In-flight gate | Cap concurrent requests and queue the rest | `c.Limits.MaxConcurrentRequests` + `QueueTimeout` |
| Retries | Exponential backoff / jitter / `Retry-After` / idempotency gate | `c.Retry.Policy = RetryPolicy.Exponential(...)` |
| Circuit breaker | Per-host Closed/Open/HalfOpen state machine | `c.Circuit.Policy = new CircuitBreaker()` |
| Short circuit | Deliver a response without sending (cache / mock / replay) | `c.ShortCircuit = new AxiosShortCircuit { ... }` |

**Data and errors**

| Capability | What you get | How |
| --- | --- | --- |
| Unified errors | Every failure is an `AxiosException` with a stable `Code` | `catch (AxiosException ex) { ex.Code }` |
| Interceptors | Request and response, sync or async | `client.Interceptors.Request.Use(...)` |
| Data transforms | The two axios-style transform chains | `c.TransformRequest.Add(...)` |
| Swappable serialization | JSON (default) + XML built in, any format pluggable | `AxiosContent.Register(...)` |
| Observability | `ActivitySource` + `Meter` + `DiagnosticListener` | `AddSource("AxiosNet")` / `AddMeter("AxiosNet")` |

**Transport and engineering**

| Capability | What you get | How |
| --- | --- | --- |
| Connection pool | One tuned handler per origin, shared keep-alive connections | Built in; share via `new HttpConnectionPool(...)` |
| HTTP versions | HTTP/2 by default with downgrade, HTTP/3 supported | `c.Connection.HttpVersion` |
| Custom handlers | Wrap a `DelegatingHandler`, or take over the transport | `ConnectionPoolOptions.HandlerFactory` |
| Declarative client | Describe requests with models and attributes | `[Get("users/{id}")] class X : IApiRequest<User>` |
| Dependency injection | Singleton registration, named multi-site resolution | `AddAxiosClient(...)` / `AddApiClient(...)` |
| Native AOT | Declared `IsTrimmable`, trims with 0 warnings | Source-generated `JsonSerializerContext` |

## Clients

Three entry points, identical capabilities:

| Entry point | How to create | Use for |
| --- | --- | --- |
| Static facade | `Axios.GetAsync(...)` / `Axios.Defaults` / `Axios.Interceptors` | Scripts, single-endpoint apps |
| Instance | `Axios.Create(c => ...)` or `new AxiosClient(config)` | The normal case: one set of defaults + one pool |
| Declarative | `new ApiClient(baseAddress)` | Many endpoints, describe requests with models ([details](#declarative-api-client)) |

**Instance defaults vs per-request**: what you set in `Axios.Create` are instance defaults; the trailing `configure` argument configures a single request. `configure` receives a **copy** of the instance defaults, so you can really remove a default header:

```csharp
await http.GetAsync("slow", c => c.Timeout = TimeSpan.FromSeconds(30));   // this request only
await http.GetAsync("x", c => c.Headers.Delete("X-Foo"));                 // default header removed
```

- **Instance-level only**: `Connection.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `PoolOptions` (fixed when the pool is built) and `Limits.MaxConcurrentRequests` (fixed on the first gated request). Writing a **different** value per request throws `ERR_BAD_OPTION_VALUE`; repeating the effective value is allowed. This turns "silently ignored" into a loud error.
- **`Params` / `Data` merge on "was it assigned", not on "is it `null`"**: leaving them untouched inherits the instance default; writing `c.Params = null` / `c.Data = null` explicitly drops the instance default (same idea as using `Headers.Delete` instead of `= null` for headers). So `PostAsync(url)` — which passes no `data` — still sends `Defaults.Data`; write `Data = null` to send an empty body.
- Dispose a client when done (`using`); pool and handler go with it. In-flight requests are not aborted, the client just stops accepting new ones.

## Requests

### Methods and URLs

| Method | Notes |
| --- | --- |
| `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` | Each comes as non-generic → `JsonElement` and generic → `T`; all accept `data`, `configure`, `CancellationToken` |
| `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` | Send `multipart/form-data` by default |
| `RequestAsync(config)` / `RequestAsync<T>(config)` | Pass a full `AxiosConfig` |
| `GetUri(config)` / `GetUri(c => ...)` | Build the URL only |
| `Interceptors` / `Defaults` / `Pool` / `UsesConnectionPool` | Instance members |

All seven verbs also have an overload that takes a full `AxiosConfig` (`GetAsync(url, config)` / `PostAsync(url, config)` …): `config.Url` / `config.Method` are overridden by the `url` / verb, everything else merges into the instance defaults as usual. That is why `PostAsync(url, null)` means "no config to pass" and behaves like `PostAsync(url)` (empty body) instead of throwing `ArgumentNullException`.

- `url` may be relative (resolved against `BaseAddress`) or absolute; `null` / blank throws `ERR_INVALID_URL` — to hit the base address itself pass `"/"` explicitly.
- `Method` is BCL's `HttpMethod`, so custom verbs work: `new HttpMethod("PROPFIND")`.
- Escape path segments with `AxiosUrl.EncodePathSegment(value)`.

### Query parameters

```csharp
await http.GetAsync("search", c => c.Params = new
{
    q = "a b",                        // q=a%20b
    ids = new[] { 1, 2 },             // ids[]=1&ids[]=2
    filter = new { kind = "book" }    // filter[kind]=book
});

c.Params = "a=1&b=2";                 // pre-encoded query string, passed through
c.Params = new[] { 1, 2 };            // root collection: [0]=1&[1]=2
```

- Collection shape comes from `Serialization.CollectionFormat`: `Brackets` (default) / `Multi` / `Csv` / `Ssv` / `Tsv` / `Pipes`.
- Keys and values go through `Uri.EscapeDataString` (space → `%20`); a query string written into `Url` is not re-encoded.
- Passing a scalar as `Params` throws `ERR_BAD_OPTION_VALUE` (no key to express). Full control: `c.Serialization.ParamsSerializer = p => "a=1&b=2"`.
- Scalar text form (shared by `Params` / `[Path]` / `[Header]` / forms): `bool` → `true`/`false`, enums → name, `DateTime`/`DateTimeOffset` → UTC ISO with milliseconds (`Unspecified` treated as UTC), everything else `IFormattable` + `InvariantCulture`.

### Body and ContentType

**Encoding is decided by `ContentType` alone, never by the runtime type of `data`** (default `application/json`).

| `ContentType` | Object / dictionary / dynamic / collection | Primitives (number / bool / enum / Guid / date…) | `string` / `byte[]` / `Memory<byte>` / `Stream` / `HttpContent` |
| --- | --- | --- | --- |
| `application/json` (default) | Current serializer emits JSON | Raw text (`42`, `true`, `Paid`) | Sent as-is |
| `application/x-www-form-urlencoded` | Key-value form (`a[b]=v`, `a[]=v`, nulls skipped) | Ignored (empty body) | Sent as-is |
| `multipart/form-data` | Fields + file parts | Throws `ERR_BAD_OPTION_VALUE` (put it in an object field) | Throws `ERR_BAD_OPTION_VALUE` (`HttpContent` passes through) |
| Anything else (`application/xml` / `yaml`…) | Current serializer | Raw text | Sent as-is |

```csharp
await http.PostAsync("users", new { name = "bob" });                    // JSON

await http.PostAsync("login", new Dictionary<string, object?>          // form
{
    ["username"] = "alice",
    ["tags"] = new[] { "a", "b" }                                       // -> tags[]=a&tags[]=b
}, c => c.ContentType = AxiosContentType.FormUrlEncoded);

await http.PostAsync("bin", bytes, c => c.ContentType = AxiosContentType.OctetStream);   // binary: say so

await http.GetAsync("reports/query", new { from = "2024-01-01" });      // any verb may carry a body
await http.DeleteAsync("users/1", new { reason = "duplicated" });
```

Constants: `AxiosContentType.Json` / `FormUrlEncoded` / `MultipartFormData` / `OctetStream` / `Xml` / `JsonPatch` / `ProblemJson` / `TextPlain` / `TextHtml`; a parameterized string works too: `"multipart/form-data; boundary=----axios-net"`.

### Forms and uploads

```csharp
// 1) Multipart: fields + files (FormFile / Stream / byte[] / HttpContent all fine)
await http.PostFormAsync("upload", new
{
    title = "quarterly report",
    files = new[]
    {
        new FormFile("a.pdf", File.OpenRead("a.pdf"), "application/pdf"),  // disposed with the request
        FormFile.FromPath("b.zip"),                                        // library opens and disposes it
        FormFile.FromBytes("c.txt", bytes, "text/plain")
    }
});

// 2) Raw stream (best for large files, constant memory)
await using var source = File.OpenRead(@"D:\data\report.bin");
await http.PostAsync<UploadResult>("api/files/raw", source, c =>
{
    c.ContentType = "application/octet-stream";
    c.Headers.Set("X-File-Name", "report.bin");     // a raw stream has no filename, carry it in a header
});

// 3) Build MultipartFormDataContent yourself
using var multipart = new MultipartFormDataContent();
multipart.Add(new StringContent("alice"), "name");
await http.PostAsync("upload", multipart);          // passed through with its own headers and boundary
```

- Field names come from property names / dictionary keys (respects `[JsonPropertyName]` / `[JsonIgnore]`); nesting becomes `meta[author]`, collections expand into repeated parts of the same name. Override the rule with `Serialization.FormFieldNameSelector`.
- A root-level file array has no key: set `FormFile.FieldName`.
- Parts are disposed with the request (success / error / cancellation); read into `byte[]` first if you need to reuse them.
- Field names or filenames containing `"`, `\`, CR/LF or control characters would break `Content-Disposition` and fail before sending.
- In ASP.NET Core `FormFile` is ambiguous, alias it: `using ClientFile = AxiosNet.FormFile;`.

### Response types and downloads

```csharp
var typed  = await http.GetAsync<User>("users/1");                        // strong type
var raw    = await http.GetAsync("users/1");                              // JsonElement
var text   = await http.GetAsync("ping", c => c.ResponseType = ResponseType.Text);
var bytes  = await http.GetAsync("file", c => c.ResponseType = ResponseType.ArrayBuffer);
var stream = await http.GetAsync("file", c => c.ResponseType = ResponseType.Stream);
var dyn    = await http.GetAsync<dynamic>("users/1");                     // no DTO
```

`<Stream>` / `<byte[]>` / `<dynamic>` switch `ResponseType` automatically — you rarely set it by hand.

Downloading:

```csharp
// Large files: stream to disk, constant memory
var response = await http.GetAsync<Stream>("files/report.zip");
await using (var source = response.Data!)
await using (var target = File.Create(@"D:\downloads\report.zip"))
{
    await source.CopyToAsync(target);
}

// Small files
var bytes = (await http.GetAsync<byte[]>("files/icon.png")).Data!;
await File.WriteAllBytesAsync(@"D:\downloads\icon.png", bytes);
```

- The stream **must be disposed**: the connection returns to the pool only after it is fully read or disposed. Disposing half-read drops that TCP connection.
- While downloading, the lease on that origin stays held; idle eviction will not kill a long download.
- A streaming request that gets a non-2xx status discards the body immediately and returns the connection (`ex.Response.Data` is `null`, `Status` / `Headers` still available).

### Streaming responses

`RequestStreamAsync` turns a response into `IAsyncEnumerable<T>` frame by frame: `await` gives you status and headers, frames are read during enumeration.

```csharp
var response = await client.RequestStreamAsync(
    new ChatStreamRequest { Prompt = "hi" },
    AxiosStreamFormat.ServerSentEvents);          // omit the format for NDJSON

await foreach (var chunk in response.Data!)
{
    Console.Write(chunk.Text);
}
```

| Format | Frame boundary | Typical source |
| --- | --- | --- |
| `NewlineDelimitedJson` (default) | One JSON value per line, blank lines skipped | Log export, line-by-line feeds |
| `ServerSentEvents` | Blank line ends a frame, `data: [DONE]` ends the stream | LLM token streams, market pushes |
| `JsonArray` | Each element of a top-level array | Bulk / paged lists |

```csharp
var pages = await client.RequestStreamAsync(new ListOrdersRequest(), AxiosStreamFormat.JsonArray);
await foreach (var order in pages.Data!)
{
    Console.WriteLine(order.Id);                   // delivered as each element completes
}
```

- **Timeout**: for streaming, `Timeout` covers only up to the response headers; the body is governed by `Limits.StreamIdleTimeout` (`null` = body still counts against `Timeout`; `Zero` = unlimited; `>0` = idle timeout, ticking only during actual reads/writes).
- **Frame cap**: 8 MB default, valid range 1–64 MB (`Limits.MaxStreamFrameLength`), out-of-range throws `ERR_BAD_OPTION_VALUE` before sending. There is no "unlimited" mode — a whole frame must fit in memory; peak usage is roughly 2–4× the cap.
- **Encoding**: NDJSON / SSE require an ASCII-compatible charset, wide-char encodings throw `ERR_BAD_RESPONSE` when taking the stream; `JsonArray` must be UTF-8. A UTF-8 BOM is skipped.
- **Ownership**: the stream can be enumerated once; `await foreach` (including `break`) disposes it, manual `MoveNextAsync` callers must `DisposeAsync` themselves.
- **Errors**: status errors throw at `await`; frame parse failures are `ERR_BAD_RESPONSE`, mid-stream disconnects `ERR_NETWORK`, cancellation `ERR_CANCELED`.

### Timeouts and cancellation

```csharp
await client.GetAsync("slow", c => c.Timeout = TimeSpan.FromMilliseconds(500));   // one request
await client.GetAsync("forever", c => c.Timeout = TimeSpan.Zero);                 // override instance timeout

using var cts = new CancellationTokenSource();                                    // same as AbortController
var task = client.GetAsync("slow", cancellationToken: cts.Token);
cts.CancelAfter(TimeSpan.FromSeconds(1));
```

- `Timeout` is a **total budget** (connect + headers + body), default `Zero` meaning no timeout; capped at ~49.7 days, larger values clamp instead of throwing.
- Timeouts throw `ECONNABORTED` (message overridable via `Callbacks.TimeoutMessage`), cancellation throws `ERR_CANCELED`; the original `OperationCanceledException` is kept in `Cause`.

### Progress, rate and size limits

```csharp
await http.PostAsync("upload", payload, c =>
{
    c.Callbacks.OnUploadProgress   = e => Console.WriteLine($"↑ {e.Progress:P0} {e.Rate / 1024:F1} KB/s ETA {e.Estimated}");
    c.Callbacks.OnDownloadProgress = e => Console.WriteLine($"↓ {e.Progress:P0} {e.Rate / 1024:F1} KB/s");
    c.Limits.MaxRate = 1024 * 1024;                 // 1 MB/s per direction
    c.Limits.MaxBodyLength = 50 * 1024 * 1024;      // request body cap, checked before sending
    c.Limits.MaxContentLength = 10 * 1024 * 1024;   // response body cap, counted while reading
});
```

`AxiosProgressEvent`: `Loaded` / `Total` (`null` when unknown) / `Progress` (0–1, capped at 100%, `null` when `Total` is unknown) / `Rate` (average bytes per second) / `Estimated` (remaining time) / `Upload`.

- Callbacks run **synchronously**; offload expensive work yourself. Reports are throttled to one per 50 ms.
- A throwing callback aborts the request: upload phase → `ERR_BAD_REQUEST`, download phase → `ERR_BAD_RESPONSE`, original exception in `Cause`.
- `MaxContentLength`: plain responses are counted while reading and aborted on overflow; `ResponseType.Stream` pre-checks the declared length and keeps counting while reading.
- `MaxBodyLength` only applies when the body length is computable (JSON / form / `byte[]` / seekable stream); multipart is not intercepted.
- Negative values throw `ERR_BAD_OPTION_VALUE`; `MaxRate = 0` means unlimited.

### In-flight concurrency cap

`MaxRate` limits **bytes per second** and cannot stop "hundreds of tiny requests per second" — that pressure comes from the number of in-flight requests:

```csharp
var client = Axios.Create(c =>
{
    c.Limits.MaxConcurrentRequests = 4;                 // at most 4 in flight, the rest queue
    c.Limits.QueueTimeout = TimeSpan.FromSeconds(2);    // wait at most 2s for a slot
});

await client.GetAsync("fast", c => c.Limits.QueueTimeout = TimeSpan.Zero);   // fail immediately when full
```

| Item | Default | Meaning |
| --- | --- | --- |
| `MaxConcurrentRequests` | `null` (unlimited) | Max in-flight requests for this instance; positive integer |
| `QueueTimeout` | `null` (wait forever) | Slot wait limit: `null` wait / `Zero` don't queue / `>0` fail on timeout |

- Queue timeout throws **`ERR_QUEUE_TIMEOUT`** (not `ECONNABORTED`): nothing was sent, so react by slowing down / scaling out / backing off.
- Queueing **does not** count against `Timeout` (each retry attempt gets its own), and can be cancelled with a `CancellationToken`.
- A slot covers the whole in-flight span: each retry attempt takes one (backoff does not hold it), and **a streaming response holds it until the body stream is disposed**.
- The gate is shared per instance and is instance-level: different per-request values, or mutating `Defaults` after construction, throw `ERR_BAD_OPTION_VALUE`.
- **Self-deadlock risk**: sending a child request on the same instance from inside an interceptor or progress callback competes for the same slots. Set a `QueueTimeout` or use a separate instance.
- Different from `MaxConnectionsPerServer`: that one bounds **connections** (HTTP/2 multiplexes many requests over one), this one bounds **requests**.

## Errors

Every failure throws `AxiosException`:

```csharp
try
{
    await client.GetAsync("missing");
}
catch (AxiosException ex)
{
    Console.WriteLine(ex.Code);      // ERR_BAD_REQUEST
    Console.WriteLine(ex.Status);    // 404
    Console.WriteLine(ex.Cause);     // inner exception, may be null
    Console.WriteLine(ex.ToJson());  // structured snapshot, safe to log
}

if (Axios.IsAxiosError(ex) && !Axios.IsCancel(ex)) { /* ... */ }
```

| Code | Triggered by |
| --- | --- |
| `ERR_BAD_REQUEST` | 4xx; request body larger than `MaxBodyLength`; request body serialization failure; upload progress callback threw |
| `ERR_BAD_RESPONSE` | 5xx; response cannot be deserialized into the target type; body larger than `MaxContentLength`; generic argument vs `ResponseType` mismatch; download progress callback or `transformResponse` threw |
| `ECONNABORTED` | `Timeout` exceeded (or streaming idle timeout) |
| `ERR_NETWORK` | Connection refused / DNS failure / connection dropped / truncated body |
| `ERR_CANCELED` | `CancellationToken` cancelled, or the client was disposed |
| `ERR_QUEUE_TIMEOUT` | Concurrency slots stayed full past `QueueTimeout` (request never sent) |
| `ERR_CIRCUIT_OPEN` | Circuit is open, request **not sent** (`RetryAfter` suggests how long to wait) |
| `ERR_INVALID_URL` | Invalid URL, relative URL without `BaseAddress`, non-http(s) scheme |
| `ERR_BAD_OPTION_VALUE` | Invalid option value (instance-level option per request, multipart header/body conflict, scalar `Params`, out-of-range retry / circuit settings…) |

Custom accepted status codes (2xx by default):

```csharp
await client.GetAsync("maybe-404", c => c.Callbacks.ValidateStatus = s => s is >= 200 and < 500);
```

## Interceptors

```csharp
// Request interceptors run in REVERSE registration order; sync and async overloads
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

// Response interceptors run in registration order; the rejected branch may swallow and return a fallback
client.Interceptors.Response.Use(
    onFulfilled: response => response,
    onRejected: ex =>
    {
        var error = (AxiosException)ex;
        if (error.Code == AxiosException.ErrBadRequest && error.Status == 401)
        {
            // refresh the token and replay, or return fallback data
        }
        throw ex;
    });

// Removal
var id = client.Interceptors.Request.Use(cfg => cfg);
client.Interceptors.Request.Eject(id);
```

## Data transforms

Two chains, same shape as axios: the request chain runs after interceptors and before encoding; the response chain runs after deserialization.

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

    // Strip a { code, data } envelope everywhere
    c.TransformResponse.Add((data, cfg) =>
        data is JsonElement { ValueKind: JsonValueKind.Object } e && e.TryGetProperty("data", out var inner)
            ? inner
            : data);
});

await http.GetAsync("users/1", c => c.TransformResponse.Add((d, _) => Normalize(d)));   // instance chain first
```

- The chains are `IList<Func<object?, AxiosConfig, object?>>`; returning `null` is legal and keeps flowing.
- Whatever `TransformRequest` returns is still encoded according to `ContentType`.
- `TransformResponse` runs for `Json` / `Text` / `ArrayBuffer` / `Dynamic`; not for `Stream` (the data isn't read yet).

## Serialization

Request and response bodies go through System.Text.Json by default, and the whole thing is replaceable.

**Three levels of JSON settings** (later wins): global `AxiosJson.Options` → instance `c.Serialization.JsonSerializerOptions` → per-request same property. The default is web style (camelCase out, case-insensitive in). Replacing `AxiosJson.Options` needs a fresh instance (STJ freezes options once used) — do it once at startup.

**XML built in**:

```csharp
using AxiosNet.Serialization;

// A: global / instance injection, the request header becomes application/xml
AxiosContent.Serializer = XmlContentSerializer.Instance;
await http.PostAsync("orders", order);

// B: select by Content-Type (mixing formats in one app)
AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
await http.PostAsync("orders", order, c => c.ContentType = AxiosContentType.Xml);
```

**Custom formats**: implement `IContentSerializer` (two methods for text formats; binary formats add the byte channel `SerializeToBytes` / `Deserialize(ReadOnlyMemory<byte>)`; optional `MediaType` to advertise one).

```csharp
public sealed class YamlSerializer : IContentSerializer
{
    public string? MediaType => "application/yaml";

    public string Serialize(object? value, Type type, AxiosConfig config) => YamlConvert.Serialize(value, type);

    public object? Deserialize(string text, Type type, AxiosConfig config) => YamlConvert.Deserialize(text, type);
}

AxiosContent.Serializer = new YamlSerializer();
```

- A serializer may write headers (`config.Headers.Set(...)`); the `config` it sees is a per-request copy, so instance defaults stay clean.
- Forms / multipart, primitives, `string` / `byte[]` / `Stream` never go through the serializer.
- Implementations must be thread-safe (shared across concurrent requests); exceptions are wrapped as `ERR_BAD_REQUEST` / `ERR_BAD_RESPONSE`.
- Text encodings: `Serialization.RequestEncoding` / `ResponseEncoding` (UTF-8 / auto-detect by default). Code pages like GBK need `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` first, otherwise you get silent mojibake.

## Retries

No retries by default; attach a policy to enable:

```csharp
using AxiosNet.Retry;

var client = Axios.Create(c => c.Retry.Policy = new RetryPolicy { MaxRetries = 3 });

config.Retry.Policy = RetryPolicy.Constant(3, TimeSpan.FromSeconds(1));                                      // fixed interval
config.Retry.Policy = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));    // exponential

await client.GetAsync("orders", c => c.Retry.Policy = null);   // disable the instance policy for one call

client.Defaults.Retry.OnRetry = ctx => logger.LogWarning(
    "attempt {Attempt}/{Max} failed ({Code}), retrying in {Delay}ms",
    ctx.Attempt, ctx.MaxRetries, ctx.ErrorCode, ctx.Delay.TotalMilliseconds);
```

`RetryPolicy` defaults (all configurable):

| Situation | Default |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED` | Retry |
| 408 / 429 / 5xx | Retry (`RetryStatusCodes` overrides) |
| Other 4xx, config / URL errors | No retry |
| `ERR_CANCELED` | No retry |
| POST / PATCH and other non-idempotent verbs | No retry (`RetryNonIdempotentMethods = true` opts in) |

Other members: `Delay` / `BackoffFactor` / `MaxDelay` / `Jitter` (`true` by default, waits land in 50%–100%) / `HonorRetryAfter` (`true` by default, follows server `Retry-After` but still capped by `MaxDelay`) / `RetryWhen`.

- `Timeout` is **per attempt** (3 retries × 10s = 40s worst case); use a `CancellationToken` for an overall budget.
- Interceptors, `transformRequest` and body serialization run once; response interceptors see the final result.
- The body must be replayable: seekable streams are rewound before each attempt; non-seekable streams and `StreamContent` (including multipart parts) throw `ERR_BAD_OPTION_VALUE` as soon as retries are enabled.
- Custom policies: implement `IAxiosRetryPolicy` (`MaxRetries` / `ShouldRetry(ctx)` / `GetDelay(ctx)`); `ctx` exposes `Attempt` / `MaxRetries` / `Exception` / `Response` / `StatusCode` / `ErrorCode` / `Delay` / `Config` / `Method`. Policies are shared concurrently, so keep them stateless.

## Circuit breaker

Retries answer "should I try again?"; a breaker answers "is the downstream down at all?". Off by default:

```csharp
using AxiosNet.Circuit;

var breaker = new CircuitBreaker
{
    FailureThreshold = 5,                       // open after 5 consecutive downstream failures
    BreakDuration = TimeSpan.FromSeconds(30),   // cool down 30s, then let probes through
    HalfOpenAttempts = 1                        // one probe at a time while half-open
};

breaker.StateChanged += change => logger.LogWarning(
    "{Host} circuit: {From} -> {To}", change.Host, change.Previous, change.Current);

var client = Axios.Create(c => c.Circuit.Policy = breaker);

await client.GetAsync("health", c => c.Circuit.Policy = null);   // probes must not be blocked
breaker.Reset();                                                 // manual recovery
```

State machine: `Closed` → (failures reach `FailureThreshold`) → `Open` → (`BreakDuration` elapses) → `HalfOpen` → (probe succeeds) → `Closed`; a failed probe returns to `Open` **immediately** and restarts the timer.

Failure accounting matches the retry gate (**cancels and 4xx never count**):

| Situation | Counts as failure? |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED` (timeout) | Yes |
| 5xx | Yes |
| 4xx (including 408 / 429) | No (and doesn't reset) |
| `ERR_CANCELED` | No |
| Config / URL / serialization / deserialization errors | No |

- **Counted per host**: the default key is `host:port` (`uri.Authority`); `KeySelector` can switch to `ctx => ctx.Uri.Host` or isolate tenants by route prefix.
- Rejected requests are **never sent**: no connection, no pool lease, just `ERR_CIRCUIT_OPEN` with `AxiosException.RetryAfter` describing the suggested wait.
- With retries enabled: the gate sits **before each attempt**, so once open the next attempt in the retry loop is cut immediately; `ERR_CIRCUIT_OPEN` itself is never retried.
- Each attempt is recorded: 3 retries + the first failure = 4 entries — hitting four 5xx in a row really is a down downstream.
- The state table has a soft cap (`MaxTrackedHosts`, 512 by default): when full it only evicts entries whose loss changes no decision (closed, or open with an expired cooldown); **open / half-open entries are never dropped**.
- Custom policies: implement `IAxiosCircuitPolicy` (`ShouldAllow(ctx)` / `GetRetryAfter(ctx)` / `Record(ctx)`); `ctx` exposes `Host` / `Uri` / `Config` / `Exception` / `StatusCode` / `ErrorCode` / `Method` / `IsSuccess` / `IsFailure`. Policies **are stateful** and shared concurrently, so you own thread safety.

## Short circuit

Sometimes the request should not be sent but a response is still needed — cache hits, mocks, offline replay, contract tests. Set `c.ShortCircuit` and the request is **not sent**; the result is delivered as this call's response.

```csharp
// Cache hit: the request interceptor is the natural place — it can only mutate config,
// and ShortCircuit is the "here is the response" exit it otherwise lacks.
client.Interceptors.Request.Use(config =>
{
    if (config.Url is { } url && cache.TryGetValue(url, out var entry))
    {
        config.ShortCircuit = new AxiosShortCircuit
        {
            Status = entry.Status,
            ContentType = entry.ContentType,                              // keep the charset
            Headers = new Dictionary<string, string> { ["X-Cache"] = "HIT" },
            Body = entry.Body                                            // bytes: parsed per call
        };
    }

    return config;
});

// Mock / fixture: the data is already materialised
var response = await client.GetAsync<User>("users/1",
    c => c.ShortCircuit = AxiosShortCircuit.From(new User { Id = 1, Name = "stub" }));
```

What is skipped is the HTTP operation itself; what is kept is the response-side semantics:

| Stage | On a short circuit |
| --- | --- |
| Concurrency gate / queueing, pool lease, sending, retries, circuit breaker | not involved |
| Telemetry span and in-flight metrics | not produced (count hits in your own cache) |
| Range validation (`Timeout` / size limits) | as usual (a config error does not vanish on a hit) |
| Deserialization by `ResponseType` / generic argument, `transformResponse` | as usual |
| `ValidateStatus` (non-2xx throws `AxiosException`), response interceptors | as usual |

- **Two payloads, pick one**: `Body` is raw bytes (what a cache stores — the same bytes must yield different shapes for `GetAsync<User>` and `GetAsync<string>`); `Data` is an already-materialised result (for mocks: not parsed, `transformResponse` not run, ownership stays with you).
- **`Body` + `Stream` / `byte[]`**: a fresh `MemoryStream` per call, and the byte array is copied — two calls never share an object, so a cache entry cannot be mutated by one of them.
- **Scoped to one request**: `AxiosConfig.Clone` clears it, so setting it on `Defaults` does nothing and replaying `response.Config` does not replay the previous short circuit.
- **Cancellation still wins**: an already-cancelled token throws `ERR_CANCELED` instead of returning a hit.
- **Headers are validated too**: names must be valid tokens and values must be free of control characters (the same gate as request headers); a violation throws `ERR_BAD_OPTION_VALUE` — dirty headers would otherwise reach logs and `transformResponse`.
- The only visible trace is `response.Request == null` (no request was made); `ProtocolVersion` stays `null` unless you set it.
- A cache entry needs at least `Status` / `ContentType` / `Body`; the `Content-Type` header drives text decoding, so dropping the charset garbles non-UTF-8 bodies on hits.

## Connection pool

`AxiosClient` builds its own pool (`HttpConnectionPool`) keyed by `scheme://host:port`: one tuned `SocketsHttpHandler` per origin, requests sharing keep-alive TCP connections.

```csharp
using AxiosNet.Pooling;

// 1) Own pool (configured at construction)
var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = true
});

// 2) Share one pool across clients
await using var sharedPool = new HttpConnectionPool(new ConnectionPoolOptions { MaxConnectionsPerServer = 64 });
using var orderClient = new AxiosClient(pool: sharedPool);
using var payClient   = new AxiosClient(pool: sharedPool);
using var payClient2  = Axios.Create(c => c.BaseAddress = "https://pay.example.com/", sharedPool);

// 3) Inspect / evict
ConnectionPoolStatistics stats = sharedPool.GetStatistics();
sharedPool.EvictIdleEndpoints();
```

| Option | Default | Notes |
| --- | --- | --- |
| `MaxConnectionsPerServer` | 100 | Max TCP connections per site; excess requests queue at the connection layer |
| `PooledConnectionLifetime` | 10 min | Max reuse age; replaced on next use (in-flight not interrupted) |
| `PooledConnectionIdleTimeout` | 2 min | Idle connections close |
| `ConnectTimeout` | 15 s | Connect timeout |
| `IdleHandlerLifetime` | 5 min | How long an idle origin node is kept |
| `EvictionInterval` | 30 s | Background sweep interval; `<= 0` disables the timer |
| `AllowAutoRedirect` / `MaxAutomaticRedirections` | `true` / 5 | Auto-redirect; `MaxRedirects = 0` disables |
| `AutomaticDecompression` | `true` | gzip / deflate / br |
| `EnableMultipleHttp2Connections` | `true` | Under h2, allow more connections up to `MaxConnectionsPerServer` |
| `MaxEndpointCount` | 1024 | Origin node cap; evicts the least recently used idle node |
| `UseCookies` | `false` | When on, the whole pool shares one `CookieContainer` |
| `Proxy` / `Credentials` | `null` | With no proxy set, system / env proxies are **not** used; set `HttpClient.DefaultProxy` if you want them |
| `ServerCertificateCustomValidationCallback` / `EnabledSslProtocols` / `ConfigureSslOptions` | `null` | Certificate validation, TLS versions, client certificates |
| `HandlerFactory` | `null` | Called when creating a per-origin handler, see [Custom handlers](#custom-handlers) |

- `Dispose()` is a graceful shutdown: idle nodes go immediately, nodes with in-flight requests are reclaimed once leases return. `Clear()` drops every node now (interrupting in-flight requests).
- How fast DNS changes converge is governed by `PooledConnectionLifetime`; for service discovery use `HandlerFactory` + `ctx.CreateDefaultHandler()` with a `ConnectCallback`.

### HTTP versions

Default is `HttpVersion = Version20` + `VersionPolicy = RequestVersionOrLower`: over https it uses h2 when negotiated and downgrades to 1.1 in the same TLS handshake; plaintext `http://` is always 1.1.

```csharp
var client = new AxiosClient();                                       // defaults are fine

await client.GetAsync(url, c => c.Connection.HttpVersion = HttpVersion.Version11);   // back to 1.1

await client.GetAsync(url, c =>                                        // require h2
{
    c.Connection.HttpVersion = HttpVersion.Version20;
    c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
});

await client.GetAsync(url, c => c.Connection.HttpVersion = HttpVersion.Version30);   // h3, downgrade if unsupported
```

| Configuration | What actually goes out |
| --- | --- |
| Default (`Version20` + `OrLower`) | h2 when negotiated over https, otherwise 1.1; plaintext always 1.1 |
| `Version20` + `Exact` | h2 only, error if not negotiable (on plaintext this means prior knowledge h2c) |
| `Version11` (policy default) | HTTP/1.1 |
| `Version30` (policy default) | h3 when QUIC and the server support it, otherwise downgrade |
| `Version30` + `Exact` | h3 only; impossible setups (plaintext / no QUIC) throw `ERR_BAD_OPTION_VALUE` before sending, unsupported servers throw `ERR_NETWORK` |

- The version actually used is `response.ProtocolVersion`; the intent is `Config.Connection.HttpVersion`.
- Check h3 availability with `Http3Support.IsSupported` / `UnsupportedReason` (.NET 6 disables h3 by default: set `System.Net.SocketsHttpHandler.Http3Support=true` in runtimeconfig or `DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=1`, and MsQuic must be present).
- `HttpVersion` / `VersionPolicy` are per-request: settable on instance defaults and per call; retries keep them.
- `WithCredentials = true` uses `CredentialCache.DefaultCredentials`; connection-level auth such as NTLM / Negotiate does not work over h2 — set `Version11` explicitly for IIS / domain services.

### Custom handlers

| Need | How | Uses the pool |
| --- | --- | --- |
| Wrap a logging / signing / tracing `DelegatingHandler` | `ConnectionPoolOptions.HandlerFactory` | Yes |
| Take over the transport (test doubles, custom protocol) | `new AxiosClient(handler: ...)` | No |

```csharp
using AxiosNet.Pooling;

// Option 1: created once per origin, inner handler comes from the pool configuration
using var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    HandlerFactory = ctx => new TraceHandler { InnerHandler = ctx.CreateDefaultHandler() }
});

// Option 2: inject directly, no pool
using var client2 = new AxiosClient(
    defaults: new AxiosConfig { BaseAddress = "https://api.example.com/" },
    handler: new TraceHandler { InnerHandler = new SocketsHttpHandler() },
    disposeHandler: true);
```

- `HandlerFactory` is called once per origin and must return an independent instance; the pool disposes it. Returning `null` throws `InvalidOperationException` on the first request to that origin.
- With an injected handler, `client.Pool` is `null` and `UsesConnectionPool` is `false`; connection-level options (`MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials`) become the handler's business, and setting them on defaults throws `ERR_BAD_OPTION_VALUE`.
- Timeouts, progress, rate limiting and size caps are unaffected.

## Observability

Three signal types, on by default, all named `AxiosNet`.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(AxiosTelemetry.ActivitySourceName))   // "AxiosNet"
    .WithMetrics(m => m
        .AddMeter(AxiosTelemetry.MeterName)                             // "AxiosNet"
        .AddView(AxiosTelemetry.RequestDurationInstrumentName,
            new ExplicitBucketHistogramConfiguration
            {
                // Durations are in seconds; the OTel defaults assume milliseconds, so override them
                Boundaries = new[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
            }));
```

**Trace**: one span per logical request (retries do not create new spans), named after the HTTP method.

| Tag | Notes |
| --- | --- |
| `http.request.method` / `url.scheme` / `server.address` / `server.port` / `url.path` | Target and method |
| `url.full` | Full URL (userinfo stripped, query string omitted by default); omitted when `Telemetry.CaptureUrl = false`, include the query with `Telemetry.CaptureQuery = true` |
| `http.response.status_code` / `network.protocol.version` | Status code / protocol actually used |
| `http.request.body.size` / `http.response.body.size` | Byte counts (omitted when unknown) |
| `http.request.resend_count` | Number of retries so far |
| `error.type` / `axiosnet.error.code` | Status code → library error code → exception type name; plus the library code |
| `axiosnet.pool.endpoint.reused` | Whether an existing origin node was reused (`false` = created on the fly, usually slower) |

- 4xx / 5xx are marked `Error` even when accepted by `validateStatus`; failures also record an `exception` event; every retry decision adds a `retry` event.
- For `ResponseType.Stream` the span ends when headers arrive, so its duration is time-to-first-byte.

**Metrics** (source `AxiosNet`):

| Metric | Type | Unit | Tags |
| --- | --- | --- | --- |
| `axiosnet.client.request.duration` | `Histogram<double>` | `s` | method / server.address / server.port / status_code / error.type |
| `axiosnet.client.active_requests` | `ObservableGauge<long>` | `{request}` | none |
| `axiosnet.client.request.retries` | `Counter<long>` | `{retry}` | method / server.address / server.port / error.type |
| `axiosnet.client.pool.leases` | `Counter<long>` | `{lease}` | server.address / server.port / `axiosnet.pool.endpoint.reused` |
| `axiosnet.client.pool.endpoints.created` | `Counter<long>` | `{endpoint}` | server.address / server.port |
| `axiosnet.client.pool.endpoints.evicted` | `Counter<long>` | `{endpoint}` | server.address / server.port / `axiosnet.pool.eviction.reason` |
| `axiosnet.client.pool.active_leases` | `ObservableGauge<long>` | `{lease}` | none |

- Metrics only carry low-cardinality tags; URL paths live in spans only.
- `request.duration` is the logical request duration (including retries and backoff); `active_requests` drops when the request ends (headers for streaming), while `active_leases` waits for the body stream — use the latter to see how much concurrency a long download holds.
- Pool metrics describe the pool as a resource and are unaffected by `Telemetry.Enabled`.

**Events** (`DiagnosticListener` named `AxiosNet.DiagnosticListener`): `AxiosNet.RequestStart` / `RequestStop` / `RequestFailed` / `Retry`, with public payload types (`AxiosRequestStartEvent` / `StopEvent` / `FailedEvent` / `AxiosRetryContext`).

**Per-request opt-out**:

```csharp
await client.GetAsync<object>("health", c => c.Telemetry.Enabled = false);      // produce nothing
await client.GetAsync<object>("api",    c => c.Telemetry.CaptureUrl = false);   // omit url.full
await client.GetAsync<object>("api?token=...", c => c.Telemetry.CaptureQuery = true);   // include the query in url.full
```

## Declarative API client

Namespace `AxiosNet.WebAPI` (same package as the core): describe the request with a model plus attributes, then call `client.RequestAsync(request)`.

```csharp
using AxiosNet.WebAPI;

[Get("users/{id}")]
[Headers("Accept: application/json")]                    // class-level static headers
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
string url = client.GetUri(new GetUserRequest { Id = 5 });   // URL only
```

| Binding | Attribute | Notes |
| --- | --- | --- |
| Path segment | `[Path]` | Replaces `{name}`; value URL-escaped; `null` is an error |
| Query parameter | `[Query]` / undecorated | Bound to the query by default; arrays `a[]=v`, nesting `a[b]=v` |
| Dictionary query | `[Query]` + dictionary | Unnamed flattens to `?a=1&b=2`, explicit name keeps `?filters[a]=1` |
| Header | `[Header]` / `[HeaderCollection]` | `null` is skipped |
| Static headers | `[Headers]` (class level) | `"Name: Value"`; name only means remove that header |
| Form | `[Multipart]` + `[Part]` | The model itself is the form; `[Part]` overrides field name / filename / part type |
| Body | `[Body]` | At most one per model; `null` sends no body |
| Skip | `[ApiIgnore]` | Not bound at all |

- **Single-payload models can omit `[Body]`**: for POST / PUT / PATCH with exactly one undecorated **non-scalar** property, it binds as the body (dictionary properties excepted — always query).
- One property may carry several binding attributes (`[Path]` + `[Header]`); the value is read once and applied to each.
- Per-request tweaks: derive from `ApiRequest<T>` and override `Configure(config)`, or `RequestAsync(request, config => ...)`.
- Invalid models (missing route, unbound placeholder, multiple bodies…) throw `InvalidOperationException` on first call; validate at startup with `ApiClient.ValidateModels(typeof(A), ...)` or `ValidateModels(assembly)` (returns all messages, never throws).
- Authorization: `[Authorize]` / `[Authorize("Basic")]` require it, `[AllowAnonymous]` skips the whole chain; `IAuthorizationProvider` is the extension point, with `BearerAuthorizationProvider` built in.
- Typed errors: after `client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()`, status-code failures throw `ApiError<TError>` (network / timeout / cancellation bypass the factory).
- Also: `client.UrlParameterFormatter` overrides `[Path]` / `[Query]` text form; `client.Axios` reaches the underlying `AxiosClient` (interceptors, pool, handler injection all work); `client.StreamIdleTimeout` controls streaming body idle timeout.

## Dependency injection

```csharp
using AxiosNet.DependencyInjection;
using AxiosNet.WebAPI.DependencyInjection;

builder.Services.AddAxiosClient(config =>                    // core client only
{
    config.BaseAddress = "https://api.example.com";
    config.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddApiClient("https://api.example.com",     // declarative client with its own transport
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.Authorization = new BearerAuthorizationProvider(token));

builder.Services.AddApiClient(                               // declarative facade over the AxiosClient singleton
    configure: client => client.Authorization = new BearerAuthorizationProvider(token));

builder.Services.AddApiClient("a", "https://a.example.com", configure: c => c.Authorization = providerA);   // multi-site
builder.Services.AddApiClient("b", "https://b.example.com", configure: c => c.Authorization = providerB);
```

| Extension | Registers |
| --- | --- |
| `AddAxiosClient(configure)` | Singleton `AxiosClient` |
| `AddApiClient(baseAddress, configureDefaults, configure)` | Singleton `ApiClient` (owns its transport) |
| `AddApiClient(configure)` | Singleton `ApiClient` (reuses the container's `AxiosClient`) |
| `AddApiClient(name, baseAddress, ...)` | Singleton `IApiClientResolver` + named site |

- TryAdd semantics: already registered means not registered twice, so no second pool is created.
- `IAuthorizationProvider` / `IAccessTokenProvider` / `IApiErrorFactory` from the container attach automatically; explicit values in `configure` win.
- Lifetime belongs to the container: disposing an injected `ApiClient` does not dispose the shared underlying client.
- Multi-site via `IApiClientResolver.Get(name)` / `TryGet` / `Names`; on .NET 8+ keyed services (`AddKeyedSingleton` + `[FromKeyedServices]`) work too.

## Configuration reference

`AxiosConfig` top level (the request itself):

| Member | Default | Notes |
| --- | --- | --- |
| `BaseAddress` | `null` | The axios `baseURL` equivalent |
| `Url` | `null` | Relative or absolute |
| `Method` | `Get` | BCL `HttpMethod`, custom verbs allowed |
| `Headers` | empty | `AxiosHeaders`, case-insensitive, validated on write |
| `RemoveHeaders` | empty | Instance default headers to strip |
| `Params` | `null` | Object / dictionary / collection / pre-encoded string |
| `ContentType` | `application/json` | Decides how `Data` is encoded |
| `Data` | `null` | Request body |
| `Timeout` | `Zero` (no timeout) | Total budget |
| `ResponseType` | `Json` | `Json` / `Text` / `ArrayBuffer` / `Stream` / `Dynamic` |
| `CancellationToken` | `default` | External cancellation |
| `TransformRequest` / `TransformResponse` | empty | Transform chains |
| `Response` | `null` | Readable during the response phase (status / headers / media type) |
| `ShortCircuit` | `null` | Deliver a response without sending (this request only) |

Seven sub-objects (created lazily on first access):

**`Serialization`** — `JsonSerializerOptions` (web style) / `ContentSerializer` (`null` = built-in JSON) / `RequestEncoding` / `ResponseEncoding` / `ParamsSerializer` / `FormFieldNameSelector` / `CollectionFormat` (`Brackets`)

**`Connection`** — `Auth` (`new AxiosAuth(user, pass)`) / `MaxRedirects` (5, `0` disables, instance-level) / `WithCredentials` (instance-level) / `Proxy` / `Credentials` / `PoolOptions` (instance-level) / `HttpVersion` (`null` = 2.0) / `VersionPolicy` (`null` = `OrLower`)

**`Limits`** — `MaxContentLength` / `MaxBodyLength` / `MaxRate` (all `null` = unlimited) / `MaxConcurrentRequests` (`null`, instance-level) / `QueueTimeout` (`null` = wait) / `StreamIdleTimeout` (`null` = counts against `Timeout`) / `MaxStreamFrameLength` (`null` = 8 MB, 1–64 MB)

**`Callbacks`** — `ValidateStatus` (`Func<int,bool>`, 2xx by default) / `TimeoutMessage` / `OnUploadProgress` / `OnDownloadProgress`

**`Retry`** — `Policy` (`null` = no retries) / `OnRetry`

**`Circuit`** — `Policy` (`null` = no breaker) / `OnRejected`

**`Telemetry`** — `Enabled` (`null` = on) / `CaptureUrl` (`null` = capture, without the query string) / `CaptureQuery` (`null` = omit the query string)

> Assigning `null` per request disables the instance's `Retry.Policy` / `Circuit.Policy` — that is a different state from "not set".

## axios comparison

| axios (JS) | AxiosNet (.NET) |
| --- | --- |
| `axios(config)` / `axios(url, config)` | `client.RequestAsync(new AxiosConfig { ... })` / `client.GetAsync(url, c => ...)` |
| `axios.get/post/put/patch/delete/head/options` | `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` |
| `axios.create(config)` | `Axios.Create(config)` / `Axios.Create(config, sharedPool)` |
| `axios.getUri(config)` | `client.GetUri(config)` / `client.GetUri(c => ...)` |
| `axios.postForm / putForm / patchForm` | `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` |
| `axios.defaults` | `Axios.Defaults` / `client.Defaults` |
| `res.data / status / statusText / headers / config` | `Data` / `Status` / `StatusText` / `Headers` / `Config` (plus `ProtocolVersion` / `Request`) |
| `interceptors.request/response.use` / `eject` | `Interceptors.Request/Response.Use` / `Eject` |
| `config.params` / `config.data` | `c.Params` / `c.Data` (encoding via `c.ContentType`) |
| `responseType: 'json' / 'text' / 'arraybuffer' / 'stream'` | `ResponseType.Json` / `Text` / `ArrayBuffer` / `Stream` |
| `validateStatus` / `timeout` / `AbortController` | `c.Callbacks.ValidateStatus` / `c.Timeout` / `c.CancellationToken` |
| `auth: { username, password }` | `c.Connection.Auth = new AxiosAuth(user, pass)` |
| `withCredentials` / `proxy` / `maxRedirects` | Same-named properties on `Connection` (instance-level) |
| `maxContentLength` / `maxBodyLength` / `maxRate` | Same-named properties on `Limits` |
| `onUploadProgress` / `onDownloadProgress` / `timeoutMessage` | Same-named properties on `Callbacks` |
| `paramsSerializer` / `formSerializer` | `Serialization.ParamsSerializer` / `FormFieldNameSelector` |
| `transformRequest` / `transformResponse` | Same-named properties |
| `AxiosError.code` / `isAxiosError` / `isCancel` | `AxiosException.Code` / `Axios.IsAxiosError` / `Axios.IsCancel` |
| `axios-retry` plugin | `c.Retry.Policy` / `c.Retry.OnRetry` |
| — (needs opossum or similar) | `c.Circuit.Policy` / `c.Circuit.OnRejected` |
| — (needs the OTel JS SDK) | Built-in `ActivitySource` / `Meter` / `DiagnosticListener` |
| — (axios-cache-interceptor and friends) | `c.ShortCircuit` (cache hit / mock / offline replay) |
| — (no equivalent) | `HttpConnectionPool`, declarative `ApiClient`, `RequestStreamAsync`, `MaxConcurrentRequests` / `QueueTimeout` |

Intentional differences from axios:

1. Method names carry the `Async` suffix; `timeout` is a `TimeSpan`; cancellation uses `CancellationToken`.
2. Body encoding is decided by `ContentType` (JSON by default), never inferred from the data type — a dictionary does not become a form on its own.
3. Non-generic text responses give `Data` as `JsonElement` (falling back to raw text on invalid JSON), binary as `byte[]`; use `GetAsync<dynamic>` for JS-style access.
4. Writing a **different** value for an instance-level option per request throws `ERR_BAD_OPTION_VALUE` (axios silently ignores it).
5. `params` uses strict `Uri.EscapeDataString` (space becomes `%20`, not `+`).
6. `transformRequest` / `transformResponse` elements are `object?` (C# cannot lose and then narrow types the way JS does).
7. Header writes are validated immediately (RFC 7230 token, no CR/LF) and throw `ArgumentException`.
8. `MaxRate` and progress callbacks are implemented at the `HttpContent` / `Stream` layer (BCL has no native support).
9. `Timeout` is capped at ~49.7 days, larger values clamp; a truly unbounded request uses `Timeout = Zero`.

## Samples

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) is an offline console sample with a built-in `TcpListener` demo server — 39 items:

```bash
dotnet run --project samples/AxiosNet.Simple
```

It opens an interactive menu (reflection lists every sample; type a number to run, `a` for all, `q` to quit).

- **29 core items**: static facade, instance creation, JSON / form / multipart, params, Basic auth, dynamic requests and responses, text / arraybuffer, interceptors, errors and cancellation, connection pool, upload and download, getUri and size limits, progress and rate limiting, custom handler, single-file upload, Newtonsoft.Json replacement, DNS refresh, bearer token, request signing, XML serialization, binary serialization, retries, HTTP/2 and HTTP/3, observability, circuit breaker, short circuit.
- **10 declarative items**: GET detail / list, POST create, authorization with `[AllowAnonymous]`, error handling, global logging, signing, streaming (SSE / JSON array), multipart upload, frame cap.

Two more samples:

- [`samples/AxiosNet.Simple.DependencyInjection`](samples/AxiosNet.Simple.DependencyInjection): six DI scenarios on the Generic Host.
- [`samples/AxiosNet.Aot`](samples/AxiosNet.Aot): Native AOT + trimming with a built-in loopback server.

## Runtime and AOT

The library targets `net6.0` only; `net6.0` / `7.0` / `8.0` / `9.0` / `10.0` and later consume the `lib/net6.0` asset with no configuration. .NET Framework and .NET Core 3.1 or earlier are not supported.

`IsTrimmable` is declared and the library trims with 0 warnings. For AOT publishing, consumers need:

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

Not available under AOT: `dynamic` responses and runtime-determined types, `XmlContentSerializer`, anonymous objects as params / form values, `ValidateModels(Assembly)` (use `ValidateModels(typeof(A), ...)`).

## Build and test

```bash
dotnet build AxiosNet.sln
dotnet test AxiosNet.sln
dotnet build AxiosNet.sln -c Release
```

| Test project | Cases | Frameworks | Executions |
| --- | ---: | --- | ---: |
| `AxiosNet.Tests` (core + declarative) | 1115 | `net8.0` / `net6.0` | 2230 |
| `AxiosNet.DependencyInjection.Tests` | 47 | `net8.0` / `net6.0` | 94 |
| **Total** | **1162** | | **2324** |

Tests run real TCP round-trips against a home-grown `TcpListener` HTTP/1.1 keep-alive server, covering request encoding, interceptors and transforms, error codes, timeouts and cancellation, progress and rate limiting, pooling and protocol negotiation, streaming, retries, circuit breaking, short circuits, telemetry, declarative binding and DI registration.

### Benchmarks

[`test/AxiosNet.Benchmarks`](test/AxiosNet.Benchmarks) is a standalone BenchmarkDotNet project (single target `net8.0`, `IsPackable=false`, not referenced by `src/`) that turns "did this change help?" into numbers. It is not part of `dotnet test` and CI does not run it (benchmarks are machine-sensitive).

Division of labour: `AxiosNet.Tests/ConcurrencyPerformanceTests` is a **concurrency correctness** test (85% threshold, 3 rounds, median) that stops collapses; use the benchmarks to quantify changes.

```bash
dotnet run -c Release --project test/AxiosNet.Benchmarks                        # interactive menu
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*Merge*'  # args go to BDN
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*' --isolated
```

By default it runs **in-process** (no temp project, no rebuild). Add `--isolated` when writing commit notes or comparing against a historical baseline (BDN temp project + one process per case); absolute timings from the two modes are not comparable (`Allocated` is trustworthy in both).

| Group | Hot spot | Baseline |
| --- | --- | --- |
| `RequestPipelineBenchmarks` | Full request pipeline (real loopback socket) | `HttpClient_GetString` |
| `ConcurrentPipelineBenchmarks` | Per-request cost with 8 in flight | `HttpClient_Concurrent` |
| `ConfigMergeBenchmarks` | `MergeConfig` / header merging | `GetUri_NoMerge` |
| `ConfigPrimitiveBenchmarks` | `AxiosConfig.Clone` / `AxiosHeaders` primitives | — (absolute values) |
| `FormEncodingBenchmarks` | Form flattening + percent-encoding | `PostForm_FlatAscii` |
| `DynamicDeserializationBenchmarks` | `dynamic` deserialization | `Typed` |
| `StreamingFramingBenchmarks` | Streaming framing (NDJSON / SSE / JSON array) | `Ndjson` |

Readings, before/after workflow and known noise: [`test/AxiosNet.Benchmarks/README.md`](test/AxiosNet.Benchmarks/README.md).

## Project layout

```
.
├─ src/
│  ├─ AxiosNet/                       # library (single target net6.0)
│  │  ├─ Axios.cs                     # static facade
│  │  ├─ AxiosClient*.cs              # instance + request pipeline + shortcuts
│  │  ├─ AxiosConfig*.cs              # configuration (top level + seven sub-objects)
│  │  ├─ AxiosHeaders.cs              # request headers
│  │  ├─ AxiosResponse.cs / AxiosException.cs / AxiosShortCircuit.cs
│  │  ├─ Interceptors/                # request / response interceptors
│  │  ├─ Retry/                       # IAxiosRetryPolicy / RetryPolicy
│  │  ├─ Circuit/                     # IAxiosCircuitPolicy / CircuitBreaker
│  │  ├─ Serialization/               # IContentSerializer + JSON / XML implementations
│  │  ├─ Telemetry/                   # ActivitySource / Meter / DiagnosticListener
│  │  ├─ Pooling/                     # HttpConnectionPool + options / statistics + handler injection
│  │  ├─ WebAPI/                      # declarative API client
│  │  └─ Internal/                    # URL, body encoding, response reading, traffic metering
│  └─ AxiosNet.DependencyInjection/   # DI extensions (separate package)
├─ samples/                           # Simple (39 items) / DI / Aot
└─ test/                              # AxiosNet.Tests / DependencyInjection.Tests / Benchmarks
```

## License

[MIT](LICENSE)
