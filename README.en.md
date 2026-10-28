# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/badge/nuget-AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![tests](https://img.shields.io/badge/tests-1483%20passed-success)](#build-and-test)
[![coverage](https://img.shields.io/badge/coverage-92.5%25_(branch_89.5%25)-success)](#build-and-test)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[简体中文](README.md) | **English**

An axios-style HTTP client for .NET. The request API mirrors JS [axios](https://github.com/axios/axios), and on top of that it adds what server-side code actually needs: an origin-scoped connection pool, retries, a circuit breaker, concurrent hedging, backup endpoints, streaming responses, an in-flight concurrency gate, a declarative API client (models or interfaces), and OpenTelemetry instrumentation.

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
| Resilience | [Retries](#retries) · [Circuit breaker](#circuit-breaker) · [Concurrent hedging](#concurrent-hedging) · [Backup endpoints](#backup-endpoints) · [Attempt pipeline](#attempt-pipeline) · [Short circuit](#short-circuit) |
| Infrastructure | [Connection pool](#connection-pool) · [HTTP versions](#http-versions) · [Custom handlers](#custom-handlers) · [Observability](#observability) |
| Engineering | [Declarative API client](#declarative-api-client) · [Interface declarations](#interface-declarations) · [Dependency injection](#dependency-injection) · [Configuration reference](#configuration-reference) · [axios comparison](#axios-comparison) · [Samples](#samples) · [Runtime and AOT](#runtime-and-aot) · [Build and test](#build-and-test) · [Benchmarks](#benchmarks) · [Project layout](#project-layout) · [License](#license) |

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
| Concurrent hedging | Fire several copies of one request, first success wins | `c.Hedging.MaxHedges = 1` |
| Backup endpoints | Resend against a mirror when the current endpoint is unusable | `c.Failover.Endpoints = new[] { new Uri("https://mirror") }` |
| Attempt pipeline | One attempt = a composable middleware chain, pluggable and reorderable | `Axios.Create(..., pipeline: AxiosAttemptPipeline.Default.Append(...))` |
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
| Connection pool | One tuned handler per origin, shared keep-alive connections | Built in; share via `new HttpConnectionPool(...)` or `HttpConnectionPool.GetOrCreate("key")` |
| HTTP versions | HTTP/2 by default with downgrade, HTTP/3 supported | `c.Connection.HttpVersion` |
| Custom handlers | Wrap a `DelegatingHandler`, or take over the transport | `ConnectionPoolOptions.HandlerFactory` |
| Declarative client | Describe requests with models and attributes | `[Get("users/{id}")] class X : IApiRequest<User>` |
| Interface declarations | Methods on an interface; `Create<T>()` generates the proxy | `[Get("users/{id}")] Task<AxiosResponse<User>> GetAsync([Path] int id)` |
| Dependency injection | Singleton registration, named multi-site resolution | `AddAxiosClient(...)` / `AddApiClient(...)` |
| Native AOT | Declared `IsTrimmable`, trims with 0 warnings | Source-generated `JsonSerializerContext` |

## Clients

Three entry points, identical capabilities:

| Entry point | How to create | Use for |
| --- | --- | --- |
| Static facade | `Axios.GetAsync(...)` / `Axios.Defaults` / `Axios.Interceptors` | Scripts, single-endpoint apps |
| Instance | `Axios.Create(c => ...)` or `new AxiosClient(config)` | The normal case: one long-lived instance — one set of defaults + one pool |
| Declarative | `new ApiClient(baseAddress)` | Many endpoints, describe requests with models or interfaces ([details](#declarative-api-client)) |

**Instance defaults vs per-request**: what you set in `Axios.Create` are instance defaults; the trailing `configure` argument configures a single request. `configure` receives a **copy** of the instance defaults, so you can really remove a default header:

```csharp
await http.GetAsync("slow", c => c.Timeout = TimeSpan.FromSeconds(30));   // this request only
await http.GetAsync("x", c => c.Headers.Delete("X-Foo"));                 // default header removed
```

- **Instance-level only**: `Connection.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `PoolOptions` (fixed when the pool is built) and `Limits.MaxConcurrentRequests` (fixed on the first gated request). Writing a **different** value per request throws `ERR_BAD_OPTION_VALUE`; repeating the effective value is allowed. This turns "silently ignored" into a loud error.
- **`Params` / `Data` merge on "was it assigned", not on "is it `null`"**: leaving them untouched inherits the instance default; writing `c.Params = null` / `c.Data = null` explicitly drops the instance default (same idea as using `Headers.Delete` instead of `= null` for headers). So `PostAsync(url)` — which passes no `data` — still sends `Defaults.Data`; write `Data = null` to send an empty body.
- **One instance per site — never one per request**: every instance builds its own pool, so `Axios.Create` / `new AxiosClient` each get their own handler set. Creating one per request degrades into the classic `new HttpClient()` pitfall: no keep-alive reuse, connections and handles growing without bound. Change defaults by changing the instance; share connections by sharing a pool (see [Connection pool](#connection-pool)); in DI just register a singleton with `AddAxiosClient`.
- Dispose a client when done (`using`, or let the DI container own it); pool and handler go with it. In-flight requests are not aborted, the client just stops accepting new ones.

## Requests

### Methods and URLs

| Method | Notes |
| --- | --- |
| `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` | Each comes as non-generic → `JsonElement` and generic → `T`; all accept `data`, `configure`, `CancellationToken` |
| `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` | Send `multipart/form-data` by default |
| `RequestAsync(config)` / `RequestAsync<T>(config)` | Pass a full `AxiosConfig` |
| `GetUri(config)` / `GetUri(c => ...)` | Build the URL only |
| `Interceptors` / `Defaults` / `Pool` / `UsesConnectionPool` / `AttemptPipeline` | Instance members |

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
- `Content-Length` is a hint, never a cut-off: when the declared value is shorter than the real body (rewritten by a proxy, a custom `HttpContent`, or a handler that does not truncate), the body is still read to the end — no tail loss and no bypass of `MaxContentLength`.
- `MaxBodyLength` only applies when the body length is computable (JSON / form / `byte[]` / seekable stream); multipart is not intercepted.
- Negative values throw `ERR_BAD_OPTION_VALUE`; `MaxRate = 0` means unlimited.
- `MaxRate` timing starts at the **first transferred byte**: the gap between receiving the response and starting to read (thread pool queueing, doing other work first) is not treated as spent budget, so reading later never loosens the cap.

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
- **With retries enabled**: `ERR_QUEUE_TIMEOUT` itself is never retried — a saturated gate means this instance is already at its in-flight cap, and an immediate resend just hits the same gate. Waiting for concurrency to drop is the caller's job (slow down / raise the cap / use another instance).
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
| `ERR_POOL_EXHAUSTED` | Pool origin cardinality saturated (all in flight, no evictable idle node); request never sent; observable, not retried by default — lower fan-out / raise the cap / `Clear()` then retry |
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

Library-level no-retry (a custom policy cannot override it): `ERR_CANCELED` / `ERR_CIRCUIT_OPEN` / `ERR_QUEUE_TIMEOUT` / `ERR_BAD_OPTION_VALUE` — in these cases the request either never left, an immediate resend would hit the same gate, or the pipeline hit a configuration / programming error.

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

## Concurrent hedging

Retries and breakers answer "what if it fails"; hedging answers "what if it is merely slow". Off by default; `MaxHedges` is the number of **extra** branches:

```csharp
var client = Axios.Create(c =>
{
    c.Hedging.MaxHedges = 1;                          // first send + 1 hedge
    c.Hedging.Delay = TimeSpan.FromMilliseconds(80);  // branch n starts at n × Delay (80ms default)
});

// per request
await client.GetAsync("search", c => c.Hedging.MaxHedges = 2);
await client.GetAsync("orders", c => c.Hedging.MaxHedges = 0);
```

The first send goes out immediately; every `Delay` another branch follows. **The first success wins** (elapsed time is never compared — whoever returns first), the rest are cancelled and cleaned up. If every branch fails, the failure goes to the outer retry.

- **The breaker sits outside the whole group and records once**: one logical attempt = one entry, matching the retry accounting. So "one fails, one succeeds" counts as a success and "all fail" counts as a single failure — extra branches never inflate the failure count.
- **Concurrency budget is `1 + MaxHedges` times**: each branch passes the gate and holds its own slot. Size `Limits.MaxConcurrentRequests` accordingly — too small with `QueueTimeout = Zero` makes branches fail with `ERR_QUEUE_TIMEOUT` at the gate, silently defeating the hedge (it is a latency optimisation, never a failure).
- **Waiting for losers has no upper bound**: after a winner appears the call still waits for the losers to finish (returning earlier would keep leases and slots occupied). Built-in middleware honours `ctx.AttemptToken`; custom middleware that ignores it holds the caller hostage.
- **`transformResponse` and response-side callbacks run once per branch** (losers included): make side-effecting transforms idempotent, or turn hedging off.
- **These requests are sent once**, with no downgrade and no error: streaming responses (a loser's body would have no reader), non-idempotent methods (unless `HedgeNonIdempotentMethods`), bodies that cannot be replayed concurrently (`Stream` / multipart / upload progress wrappers), and requests with shared response-side state (`OnDownloadProgress` or `MaxRate > 0` — progress would be reported twice and the rate cap doubled).
- Typed calls hedge too: each branch fills its own response object and the winner's is handed to the caller — including the status code and headers read from `config.Response`.
- `MaxHedges` is capped at 16: more branches stop buying latency and only saturate the downstream.

## Backup endpoints

When the current endpoint is unusable, resend the same request against the next one; order is priority:

```csharp
var client = Axios.Create(c => c.Failover.Endpoints = new[]
{
    new Uri("https://backup1.example.com"),        // tried first
    new Uri("https://backup2.example.com/api")     // then this one (path prefix allowed)
});

// disable for one request (explicit null / empty = no failover)
await client.GetAsync("health", c => c.Failover.Endpoints = null);
```

An endpoint is a **base URL** only: it replaces the origin and path prefix, while the request's own path and query are kept. Requesting `https://primary/v1/users?page=2` with endpoint `https://mirror/api` gives `https://mirror/api/v1/users?page=2`.

| Situation | Fail over? |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED` (timeout) / `ERR_POOL_EXHAUSTED` | Yes |
| 5xx (`ERR_BAD_RESPONSE` with status ≥ 500) | Yes |
| `ERR_CIRCUIT_OPEN` | Yes — the breaker is per `host:port`, so the primary being open cannot block a backup |
| 4xx, other `ERR_BAD_RESPONSE` (e.g. malformed body on a 2xx) | No (a mirror changes nothing) |
| `ERR_QUEUE_TIMEOUT` | No (local slot starvation, nothing to do with the downstream) |
| Config / programming errors, cancellation | No |

- **Every retry restarts from the first endpoint**: the endpoint list lives inside retries, so one round must exhaust all endpoints before the first failure reaches the retry layer.
- **Failing over recomputes `Host`**: a `Host` header the caller set explicitly is dropped on retarget and recomputed from the destination — keeping it would make the mirror route by the primary's virtual host (`HttpClient` honours `Headers.Host`, which wins over the URI authority). For a per-endpoint `Host`, write an attempt middleware that sets it from `ctx.Uri` (`config.Headers` is only applied to the template).
- **Endpoints must be unique**: two endpoints that resolve to the same base URL (a trailing slash makes no difference) throw `ERR_BAD_OPTION_VALUE` before anything is sent — it would only hit one address twice with the same request.
- **`Timeout` is a per-endpoint, per-send budget**: same rule as retries, so 3 endpoints with 10s can take up to 30s; use a `CancellationToken` for a total budget. Upload progress also accumulates across endpoints (like a retried body — it never jumps back).
- **The body must replay in order**: the same requirement as retries (streams must be seekable); non-seekable streams and `StreamContent` throw `ERR_BAD_OPTION_VALUE` as soon as failover is enabled.
- **Streaming responses can only fail over at the response-head stage**: once the body is handed to the caller, a mid-stream disconnect is neither retried nor failed over (same rule as "the breaker only accounts at the response head").
- **Endpoints must be absolute `http` / `https` URLs without query or fragment**: an endpoint is a base URL, and how an endpoint query would combine with the request query is undefined, so it is rejected with `ERR_BAD_OPTION_VALUE` (to add a parameter only on a backup endpoint, build the URL in a request interceptor).
- **Assigning the list snapshots it**: mutating your own list afterwards does not affect the effective config; what you read back is that internal snapshot and must not be modified.
- **Failing over re-checks the HTTP/3 guard**: an endpoint that cannot run the requested version (e.g. a plaintext mirror with a strict h3 policy) is skipped and the next one is tried; only when none is usable does it throw `ERR_BAD_OPTION_VALUE`.
- Each failover produces one attempt event with the same `AttemptNumber` and an incremented `EndpointIndex` (see [Observability](#observability)).

## Attempt pipeline

One attempt (a single send) is modelled as a composable middleware chain. The default chain is **retry → failover → circuit breaker → hedging → gate → send**, so a per-attempt capability (observation, auditing) is one middleware away instead of another branch inside the client.

```csharp
using AxiosNet.Pipeline;

// A per-attempt probe: Append lands innermost (after the gate, before the send),
// so it runs N+1 times when the request is retried N times.
var pipeline = AxiosAttemptPipeline.Default.Append(next => async (request, ctx) =>
{
    var clock = Stopwatch.StartNew();
    try
    {
        return await next(request, ctx);
    }
    finally
    {
        logger.LogDebug("attempt {Attempt} took {Ms}ms", ctx.AttemptNumber, clock.ElapsedMilliseconds);
    }
});

using var client = Axios.Create(c => c.Retry.Policy = new RetryPolicy(2), pipeline: pipeline);
```

- **`AxiosAttemptContext` is a read-only struct**: `Config` / `Uri` / `AttemptNumber` / `EndpointIndex` / `AttemptToken` / `ExternalToken` / `DataType` / `Target` / `StreamResponse` / `Retry` / `Circuit` / `Hedging` / `Failover` / `Slot`, derivable only through `WithXxx`. It is a struct, not a class, because the chain is compiled once at construction (zero allocation per attempt) and concurrent middleware need to copy the context per branch.
- **Two independent counters**: retries advance `AttemptNumber`, failover advances `EndpointIndex` (`0` = the request's own URL). Failover does not advance the attempt number, so one attempt number can carry several attempt events.
- **Built-ins are always on the chain and decide for themselves** by reading `ctx.Config`: with no retry policy the retry middleware is a straight pass-through. That is what lets the chain be frozen at construction instead of rebuilt per request.
- **Order is the contract**: retry sits outermost, so failover, the breaker, hedging and the gate run again on every attempt; failover sits outside the breaker, so each endpoint gets its own breaker accounting; the breaker sits outside hedging, so a whole branch group records once; the breaker sits before the gate, so a rejected request never occupies a slot.
- **Reorder and remove**: `AxiosAttemptPipeline.Create(...)` lists the whole chain explicitly (first = outermost); `Remove(Attempt.Gate)` drops one. The pipeline is immutable — every edit returns a new instance. Duplicating a built-in throws.
- **There is no "put it outermost" API**: that position runs once per request and is exactly what response interceptors already do. `InsertBefore(Attempt.Retry, ...)` refuses explicitly and points at `client.Interceptors.Response`, which additionally offers `OnRejected`, runtime add/remove, and short-circuit coverage.
- **You do not manage the concurrency slot**: the gate releases it itself — a middleware that skips `next` (returning its own response) or throws cannot leak it. The only case where the gate does not release it is a **library-produced** streamed response: the slot travels with the body into `StreamOwner` and is released when the stream is disposed, so the request keeps occupying its quota while the body downloads. A `StreamOwner` built by a middleware is not that case: the gate still releases its own slot, and a self-built owner gets no concurrency quota.
- **Whoever creates it, disposes it**: a request message cloned by a middleware is that middleware's to dispose (the template still belongs to the pipeline). When replacing a response you must dispose the previous `AxiosAttemptResult.StreamOwner`, or carry it into the new result (`new AxiosAttemptResult(newResponse, old.StreamOwner)`) — it holds the pool lease and the concurrency slot, and dropping it leaks both.
- **Middleware time is not counted against `Timeout`**: the budget starts inside the terminal, and gate queuing plus any middleware prologue happen outside it.
- Read request headers from the response side with `response.GetRequestHeader(name)` (request headers first, then content headers); a short-circuit response has no underlying request message and returns `null`.

How it divides the work with the other four extension points:

| Goal | Use |
| --- | --- |
| Rewrite URL / headers / body, inject a short circuit (once per request) | Request interceptor |
| Shape outgoing `Data` before serialization | `transformRequest` |
| Shape incoming `Data` after deserialization (per attempt) | `transformResponse` |
| Shape the final response, turn an exception into a response | Response interceptor |
| Observe or steer **each attempt** (auditing included) | Attempt middleware |
| Change "how many retries / how long to back off / when to break / whether to hedge / which endpoints" | The matching `Policy` or sub-config, not a middleware swap |

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

> **Do not create a client per request**: `Axios.Create` / `new AxiosClient` each build their own pool, so one-per-request means one handler set per request and no keep-alive reuse — the classic `new HttpClient()` pitfall. Keep one instance per site; to share connections across clients pass the same pool explicitly (usage 2 below), or register a singleton with `AddAxiosClient` in DI.
>
> **Always `Dispose` before dropping an instance**: an undisposed pool is held by its background eviction timer and is never garbage-collected; its connections are only reclaimed once `IdleHandlerLifetime` (5 min by default) elapses.

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

// 4) Named shared pool: the same name always yields the same process-wide pool
//    (options only apply the first time the pool is created)
var named = HttpConnectionPool.GetOrCreate("orders", new ConnectionPoolOptions { MaxConnectionsPerServer = 64 });
using var a = Axios.Create(c => c.BaseAddress = "https://order.example.com/", pool: HttpConnectionPool.GetOrCreate("orders"));
using var b = Axios.Create(c => c.BaseAddress = "https://pay.example.com/",   pool: HttpConnectionPool.GetOrCreate("orders"));

HttpConnectionPool.Release("orders");   // remove and shut down gracefully; the next call builds a new one
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
| `MaxEndpointCount` | 1024 | Origin node cap; evicts the least recently used idle node; if all are in use, creating a new node throws `ERR_POOL_EXHAUSTED` (request never sent; observable, not retried by default — lower fan-out / raise the cap / `Clear()` then retry) |
| `UseCookies` | `false` | When on, the whole pool shares one `CookieContainer` |
| `Proxy` / `Credentials` | `null` | With no proxy set, system / env proxies are **not** used; set `HttpClient.DefaultProxy` if you want them |
| `ServerCertificateCustomValidationCallback` / `EnabledSslProtocols` / `ConfigureSslOptions` | `null` | Certificate validation, TLS versions, client certificates |
| `HandlerFactory` | `null` | Called when creating a per-origin handler, see [Custom handlers](#custom-handlers) |

- `Dispose()` is a graceful shutdown: idle nodes go immediately, nodes with in-flight requests are reclaimed once leases return. `Clear()` drops every node now (interrupting in-flight requests).
- How fast DNS changes converge is governed by `PooledConnectionLifetime`; for service discovery use `HandlerFactory` + `ctx.CreateDefaultHandler()` with a `ConnectCallback`.
- **Named shared pools** (usage 4) cover "several clients want to reuse connections without passing a pool object around". The key is supplied by the caller — there is **no options fingerprint**: reference members such as `HandlerFactory` / `Proxy` / `Credentials` / the certificate callbacks have no reliable structural equality, so two "identical looking" configurations throw instead of silently sharing one pool. Three rules: the same name returns the same pool; `options` only apply on first creation (passing a **different** configuration later throws `ERR_BAD_OPTION_VALUE` rather than being silently ignored); pools in the registry are owned by nobody — disposing a client does not close them, `Release(key)` does.
- To see whether pools are being built too often, watch `axiosnet.client.pool.active_pools` (live pools) and `.shared_pools` (named registry entries).

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
| `axiosnet.client.pool.active_pools` | `ObservableGauge<long>` | `{pool}` | none |
| `axiosnet.client.pool.shared_pools` | `ObservableGauge<long>` | `{pool}` | none |

- Metrics only carry low-cardinality tags; URL paths live in spans only.
- `request.duration` is the logical request duration (including retries and backoff); `active_requests` drops when the request ends (headers for streaming), while `active_leases` waits for the body stream — use the latter to see how much concurrency a long download holds.
- Pool metrics describe the pool as a resource and are unaffected by `Telemetry.Enabled`.
- `pool.active_pools` counts live pools (+1 on creation, -1 on `Dispose`) and `pool.shared_pools` counts entries in the named registry: a rising `active_pools` means some code keeps building pools — typically a client per request.

**Events** (`DiagnosticListener` named `AxiosNet.DiagnosticListener`): `AxiosNet.RequestStart` / `RequestStop` / `RequestFailed` / `Retry`, with public payload types (`AxiosRequestStartEvent` / `StopEvent` / `FailedEvent` / `AxiosRetryContext`).

**Attempt auditing** (`AxiosNet.AttemptStop` / `AxiosNet.AttemptFailed`, payload `AxiosAttemptEvent`) only exists when `Attempt.Audit` is on the chain:

```csharp
var pipeline = AxiosAttemptPipeline.Default.Append(Attempt.Audit);   // after the gate, before the send
using var client = Axios.Create(pipeline: pipeline);
```

- Covers attempts that **actually reach the send**: breaker rejections, gate queue timeouts and short circuits produce no attempt event (the audit sits inside them).
- Event count is **endpoints × branches**: one per failover (`AttemptNumber` unchanged, `EndpointIndex` distinguishes them) and one per hedge branch; losers cancelled by the winner emit no failure event (that is cleanup, not a downstream fault), while losers that genuinely failed before a winner appeared do emit one.
- Zero cost when nobody subscribes: no state machine, no timestamps.

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

## Interface declarations

When writing a request model per endpoint is too much, hand the interface to `client.Create<T>()`: routes live on methods, binding attributes on parameters, and `DispatchProxy` generates the implementation at runtime. Binding semantics are the **same kernel** as request models (path-template validation, duplicate detection, default body inference, scalar guard, static headers and authorization all identical).

```csharp
[Headers("Accept: application/json")]                    // interface-level static headers
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

Multipart and streaming are declared on the interface the same way (semantics match request models one-to-one):

```csharp
[Post("upload")]
[Multipart]                                              // parameters are the form
Task<AxiosResponse<UploadResult>> UploadAsync(
    [Part("Title")] string title,
    [Part(FileName = "data.bin")] byte[] blob,
    CancellationToken cancellationToken = default);

[Get("stream/events")]
[Stream(AxiosStreamFormat.ServerSentEvents)]            // omit for NDJSON
Task<AxiosResponse<IAsyncEnumerable<Chunk>>> EventsAsync(CancellationToken cancellationToken = default);
```

- Every method must carry exactly one route attribute; the return type must be `Task<AxiosResponse<T>>` (no synchronous returns / `ValueTask` / generic methods / `ref`·`out`). Returning `Task<AxiosResponse<IAsyncEnumerable<T>>>` makes it a streaming method.
- `CancellationToken` parameters are recognized by type (at most one) and never bound; every other parameter follows the `[Path]` / `[Query]` / `[Header]` / `[HeaderCollection]` / `[Part]` / `[Body]` / `[ApiIgnore]` or "undecorated" rules, including default body inference.
- `[Headers]` / `[Authorize]` / `[AllowAnonymous]` may sit on the interface; with base interfaces, static headers and authorization merge in the **derived interface's scope** (the same base method can carry different static headers per derived interface).
- Invalid declarations (missing route, multiple bodies, `[Part]` without `[Multipart]`, `[Multipart]` combined with `[Body]`, `[Stream]` on a non-streaming method, duplicate parameter names…) throw `InvalidOperationException` from `Create<T>()`; validate at startup with `ApiClient.ValidateInterfaces(typeof(IA), ...)` or `ValidateInterfaces(assembly)` (returns all messages, never throws).
- Authorization callbacks and the error factory receive an `ApiInterfaceInvocation` (interface type, method name, argument snapshot) instead of a model instance; use those three to branch per request.
- Interface-level streaming: pick the frame format with `[Stream(AxiosStreamFormat.ServerSentEvents)]` / `[Stream(AxiosStreamFormat.JsonArray)]` (NDJSON when omitted); the returned stream can only be enumerated once and shares the exact timeout / error semantics of `RequestStreamAsync(...)`.
- Interface methods have no per-method `configure` hook — switch to a request model when you need per-request tweaks.
- The proxy is built on `DispatchProxy` (Reflection.Emit) and is **JIT-only**: `Create<T>()` throws `PlatformNotSupportedException` under Native AOT, and trimming requires rooting the interface plus its parameter / return types.

## Dependency injection

```csharp
using AxiosNet.DependencyInjection;
using AxiosNet.WebAPI.DependencyInjection;

builder.Services.AddAxiosClient(config =>                    // core client only
{
    config.BaseAddress = "https://api.example.com";
    config.Timeout = TimeSpan.FromSeconds(10);
});

builder.Services.AddAxiosClient(                             // ...and swap the attempt pipeline
    configure: config => config.Timeout = TimeSpan.FromSeconds(10),
    pipeline: AxiosAttemptPipeline.Default.Append(auditAttempt));

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
| `AddAxiosClient(configure, pipeline)` | Singleton `AxiosClient` (optional attempt pipeline) |
| `AddApiClient(baseAddress, configureDefaults, configure)` | Singleton `ApiClient` (owns its transport) |
| `AddApiClient(configure)` | Singleton `ApiClient` (reuses the container's `AxiosClient`) |
| `AddApiClient(name, baseAddress, ...)` | Singleton `IApiClientResolver` + named site |
| `AddApiInterface<T>()` / `AddApiInterface<T>(name)` | Singleton interface proxy (default / named site) |

- TryAdd semantics: already registered means not registered twice, so no second pool is created.
- `IAuthorizationProvider` / `IAccessTokenProvider` / `IApiErrorFactory` from the container attach automatically; explicit values in `configure` win.
- Lifetime belongs to the container: disposing an injected `ApiClient` does not dispose the shared underlying client.
- Multi-site via `IApiClientResolver.Get(name)` / `TryGet` / `Names`; on .NET 8+ keyed services (`AddKeyedSingleton` + `[FromKeyedServices]`) work too.
- Interface declarations register directly: `services.AddApiInterface<IUserApi>()` (unkeyed `ApiClient`) or `AddApiInterface<IUserApi>("a")` (named site); the proxy is reused as a singleton and is JIT-only.

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

Nine sub-objects (created lazily on first access):

**`Serialization`** — `JsonSerializerOptions` (web style) / `ContentSerializer` (`null` = built-in JSON) / `RequestEncoding` / `ResponseEncoding` / `ParamsSerializer` / `FormFieldNameSelector` / `CollectionFormat` (`Brackets`)

**`Connection`** — `Auth` (`new AxiosAuth(user, pass)`) / `MaxRedirects` (5, `0` disables, instance-level) / `WithCredentials` (instance-level) / `Proxy` / `Credentials` / `PoolOptions` (instance-level) / `HttpVersion` (`null` = 2.0) / `VersionPolicy` (`null` = `OrLower`)

**`Limits`** — `MaxContentLength` / `MaxBodyLength` / `MaxRate` (all `null` = unlimited) / `MaxConcurrentRequests` (`null`, instance-level) / `QueueTimeout` (`null` = wait) / `StreamIdleTimeout` (`null` = counts against `Timeout`) / `MaxStreamFrameLength` (`null` = 8 MB, 1–64 MB)

**`Callbacks`** — `ValidateStatus` (`Func<int,bool>`, 2xx by default) / `TimeoutMessage` / `OnUploadProgress` / `OnDownloadProgress`

**`Retry`** — `Policy` (`null` = no retries) / `OnRetry`

**`Circuit`** — `Policy` (`null` = no breaker) / `OnRejected`

**`Hedging`** — `MaxHedges` (`0` = off, max 16) / `Delay` (80ms; branch n starts at `n × Delay`, and `Delay × MaxHedges` must stay within the timer range) / `HedgeNonIdempotentMethods` (`false`)

**`Failover`** — `Endpoints` (`null` = no failover; ordered absolute `http`/`https` URLs, path prefix allowed, no query or fragment, no duplicates)

**`Telemetry`** — `Enabled` (`null` = on) / `CaptureUrl` (`null` = capture, without the query string) / `CaptureQuery` (`null` = omit the query string)

> Assigning `null` per request disables the instance's `Retry.Policy` / `Circuit.Policy` / `Failover.Endpoints` — that is a different state from "not set".

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
| — (needs cockatiel or similar) | `c.Hedging.MaxHedges` (concurrent hedging), `c.Failover.Endpoints` (backup endpoints) |
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

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) is an offline console sample with a built-in `TcpListener` demo server — 46 items:

```bash
dotnet run --project samples/AxiosNet.Simple
```

It opens an interactive menu (reflection lists every sample; type a number to run, `a` for all, `q` to quit).

- **32 core items**: static facade, instance creation, JSON / form / multipart, params, Basic auth, dynamic requests and responses, text / arraybuffer, interceptors, errors and cancellation, connection pool, upload and download, getUri and size limits, progress and rate limiting, custom handler, single-file upload, Newtonsoft.Json replacement, DNS refresh, bearer token, request signing, XML serialization, binary serialization, retries, HTTP/2 and HTTP/3, observability, circuit breaker, short circuit, named shared pool, attempt pipeline, concurrent hedging and backup endpoints.
- **14 declarative items**: GET detail / list, POST create, authorization with `[AllowAnonymous]`, error handling, global logging, signing, streaming (SSE / JSON array), multipart upload, frame cap, interface declarations, interface authorization and validation, interface multipart, interface streaming.

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

Not available under AOT: `dynamic` responses and runtime-determined types, `XmlContentSerializer`, anonymous objects as params / form values, `ValidateModels(Assembly)` (use `ValidateModels(typeof(A), ...)`), and interface declarations via `Create<T>()` (`DispatchProxy` relies on Reflection.Emit and throws `PlatformNotSupportedException` at call time; `ValidateInterfaces(Assembly)` is subject to trimming too — use `ValidateInterfaces(typeof(IA), ...)`).

## Build and test

```bash
dotnet build AxiosNet.sln
dotnet test AxiosNet.sln
dotnet build AxiosNet.sln -c Release
```

| Test project | Cases | Frameworks | Executions |
| --- | ---: | --- | ---: |
| `AxiosNet.Tests` (core + declarative) | 1428 | `net8.0` / `net6.0` | 2856 |
| `AxiosNet.DependencyInjection.Tests` | 59 | `net8.0` / `net6.0` | 118 |
| **Total** | **1487** | | **2974** |

Tests run real TCP round-trips against a home-grown `TcpListener` HTTP/1.1 keep-alive server, covering request encoding, interceptors and transforms, error codes, timeouts and cancellation, progress and rate limiting, pooling and protocol negotiation, streaming, retries, circuit breaking, concurrent hedging, backup endpoints, short circuits, telemetry, declarative binding, interface proxies and DI registration.

Line coverage **92.5%**, branch coverage **89.5%** (`AxiosNet.Tests` / net8.0, via `--collect:"XPlat Code Coverage"`, excluding the perf and allocation suites; repeated runs wobble by ±0.1).
Collection must exclude two kinds of cases — throughput ratios and allocation guards both skew under instrumentation (the guards assert bytes allocated per request, and instrumentation itself allocates):

```bash
dotnet test test/AxiosNet.Tests/AxiosNet.Tests.csproj -f net8.0 --collect:"XPlat Code Coverage" \
  --filter "FullyQualifiedName!~ConcurrencyPerformanceTests&FullyQualifiedName!~AllocationSweepTests&FullyQualifiedName!~AllocationTests&FullyQualifiedName!~HeadersCopyOnWriteTests"
```

A plain `dotnet test` still runs everything, excluded classes included.

### Test suite organised by feature

The folder layout of `test/AxiosNet.Tests` is the feature list — pick the folder first, then the file:

| Folder | What it covers |
| --- | --- |
| `Client/` | Static facade `Axios`, instance options and defaults, default headers, config merging and the nine sub-objects |
| `Requests/` | URLs and params, request bodies (JSON / form / multipart), response parsing and buffering, streaming, timeouts and cancellation, Content-Type parsing |
| `Governance/` | Concurrency gate, progress callbacks and rate limiting |
| `Resilience/` | Retry policies and timing, circuit-breaker state machine, short circuits |
| `Serialization/` | `IContentSerializer` contract and registry, JSON defaults |
| `Pipelines/` | Request / response interceptors, `transformRequest` / `transformResponse`, attempt pipeline (built-in order, insertion position, reorder validation), concurrent hedging, backup endpoints |
| `Connectivity/` | Connection pool (incl. named shared pools), handler injection, HTTP/2·3 negotiation and downgrade, redirects, local h3 capability probe |
| `Diagnostics/` | Activity / Meter / DiagnosticListener telemetry, attempt audit events |
| `Errors/` | Error model, error codes, `ToJson` snapshot |
| `Regression/` | Allocation guards (isolated collection + precise counting, process- or thread-wide), concurrent throughput |
| `WebAPI/` | Declarative model binding and validation, authorization, streaming, interface proxies and startup validation |
| `Infrastructure/` | Test plumbing: HTTP server, models, telemetry probe, isolated collections |

Timing-sensitive cases (upper-bound duration assertions, telling "placeholder vs budget" apart) and allocation cases each run in isolation — see `Infrastructure/TimingCollection.cs` and `Infrastructure/AllocationCollection.cs`.

`AxiosNet.DependencyInjection.Tests` follows the same rule with two groups: `Registration/` (registration extensions and named clients) and `Lifecycle/` (concurrent resolution and holder disposal).

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
│  │  ├─ AxiosConfig*.cs              # configuration (top level + nine sub-objects)
│  │  ├─ AxiosHeaders.cs              # request headers
│  │  ├─ AxiosResponse.cs / AxiosException.cs / AxiosShortCircuit.cs
│  │  ├─ Interceptors/                # request / response interceptors
│  │  ├─ Pipeline/                    # attempt middleware chain: AxiosAttemptPipeline + built-in retry / failover / breaker / hedging / gate + optional audit
│  │  ├─ Retry/                       # IAxiosRetryPolicy / RetryPolicy
│  │  ├─ Circuit/                     # IAxiosCircuitPolicy / CircuitBreaker
│  │  ├─ Serialization/               # IContentSerializer + JSON / XML implementations
│  │  ├─ Telemetry/                   # ActivitySource / Meter / DiagnosticListener
│  │  ├─ Pooling/                     # HttpConnectionPool + options / statistics + handler injection
│  │  ├─ WebAPI/                      # declarative API client (models / interfaces)
│  │  └─ Internal/                    # URL, body encoding, response reading, traffic metering
│  └─ AxiosNet.DependencyInjection/   # DI extensions (separate package)
├─ samples/                           # Simple (46 items) / DI / Aot
└─ test/
   ├─ AxiosNet.Tests/                  # core + declarative (folders = feature groups, see "Build and test")
   ├─ AxiosNet.DependencyInjection.Tests/
   └─ AxiosNet.Benchmarks/
```

## License

[MIT](LICENSE)
