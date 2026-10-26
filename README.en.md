# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/nuget/v/AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![tests](https://img.shields.io/badge/tests-1085%20passed-success)](#build-and-test)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[简体中文](README.md) | **English**

An axios-style HTTP client for .NET: the request API mirrors JS [axios](https://github.com/axios/axios) one-to-one, plus an origin-scoped connection pool, resilient retries, streaming responses, a declarative API client and OpenTelemetry instrumentation.

- Single target `net6.0`: one assembly runs on .NET 6 / 7 / 8 / 9 / 10 and later
- Zero third-party dependencies (BCL only)
- Async first: `Task` + `CancellationToken`
- Traces / metrics / events are on by default and cost nothing when nobody listens

> Repository: [Gitee (itsm/axios-net)](https://gitee.com/itsm/axios-net) is the main repo for PRs and tags; [GitHub](https://github.com/sunyuliang/AxiosNet) is a read-only mirror.

---

## Table of contents

- [Install](#install)
- [Quick start](#quick-start)
- [Clients](#clients)
- [Requests](#requests)
  - [Methods and URLs](#methods-and-urls)
  - [Query parameters](#query-parameters)
  - [Body and ContentType](#body-and-contenttype)
  - [Forms and uploads](#forms-and-uploads)
  - [Response types and downloads](#response-types-and-downloads)
  - [Streaming responses](#streaming-responses)
  - [Timeouts and cancellation](#timeouts-and-cancellation)
  - [Progress, rate limiting and size limits](#progress-rate-limiting-and-size-limits)
- [Errors](#errors)
- [Interceptors](#interceptors)
- [Data transforms](#data-transforms)
- [Serialization](#serialization)
- [Retries](#retries)
- [Connection pool](#connection-pool)
  - [HTTP versions (HTTP/2 by default, HTTP/3 opt-in)](#http-versions-http2-by-default-http3-opt-in)
  - [Custom handlers](#custom-handlers)
- [Observability](#observability)
- [Declarative API client](#declarative-api-client)
- [Dependency injection](#dependency-injection)
- [Configuration reference](#configuration-reference)
- [axios comparison](#axios-comparison)
- [Samples](#samples)
- [Runtime support and AOT](#runtime-support-and-aot)
- [Build and test](#build-and-test)
- [Benchmarks](#benchmarks)
- [Project layout](#project-layout)
- [License](#license)

---

## Install

```bash
dotnet add package AxiosNet                       # core client + declarative API client
dotnet add package AxiosNet.DependencyInjection   # optional: DI singleton registration
```

Using the sources in this repo:

```bash
dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj
```

## Quick start

Start with the simplest form and add typing as needed:

```csharp
using AxiosNet;

// 1) String: the raw response body, no parsing
var text = await Axios.GetAsync<string>("https://api.example.com/ping");
Console.WriteLine(text.Data);          // "pong"

// 2) dynamic: no DTO, access fields like in JS
var dyn = await Axios.GetAsync<dynamic>("https://api.example.com/users/1");
Console.WriteLine($"{dyn.Data.name}, {dyn.Data.address.city}");   // dyn.Data["name"] works too

// 3) Strongly typed: with a DTO, Data is a User
var response = await Axios.GetAsync<User>("https://api.example.com/users/1");
User user = response.Data!;
Console.WriteLine($"{response.Status} {user.Name}");
```

Pick by whether you want to define a DTO:

| Call | Type of `Data` | Use it for |
| --- | --- | --- |
| `GetAsync<string>` | `string` (raw, not parsed) | text endpoints, debugging, custom parsing |
| `GetAsync<dynamic>` | `AxiosDynamic` (members / indexer / missing members return `null`) | unstable shapes, quick checks |
| `GetAsync<T>` | `T` | production code |
| `GetAsync()` (non-generic) | `JsonElement` (invalid JSON falls back to text, binary gives `byte[]`) | inspecting the shape without a type |

Create an instance with baseURL / timeout / default headers:

```csharp
// Equivalent to axios.create({ baseURL, timeout, headers })
var http = Axios.Create(c =>
{
    c.BaseAddress = "https://api.example.com/";
    c.Timeout = TimeSpan.FromSeconds(10);
    c.Headers.Set("Authorization", "Bearer xxx");
});

await http.PostAsync("users", new { name = "bob", age = 30 });   // POST https://api.example.com/users
```

Build the URL without sending (equivalent to `axios.getUri(config)`):

```csharp
string url = http.GetUri(c =>
{
    c.Url = "users";
    c.Params = new { page = 2, ids = new[] { 1, 2 } };
});
// -> https://api.example.com/users?page=2&ids[]=1&ids[]=2
```

## Clients

Three entry points, identical capabilities:

| Entry | Creation | Typical use |
| --- | --- | --- |
| Static facade | `Axios.GetAsync(...)`, `Axios.Defaults`, `Axios.Interceptors` | scripts, small tools, single-site apps |
| Instance | `Axios.Create(c => ...)` or `new AxiosClient(config)` | the normal choice: one set of defaults + one pool |
| Declarative | `new ApiClient(baseAddress)` | many endpoints described by request models (see [Declarative API client](#declarative-api-client)) |

**Instance defaults vs per-request**: `Axios.Create(c => ...)` sets instance defaults; the `configure` parameter of any shortcut applies to that call only. `configure` receives a copy of the instance defaults, so `c.Headers.Delete("X-Foo")` really removes a default header.

```csharp
await http.GetAsync("slow", c => c.Timeout = TimeSpan.FromSeconds(30));   // this call only
```

**Instance-only options**: `Connection.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `PoolOptions` are baked into the pool at construction; `Limits.MaxConcurrentRequests` is fixed when the first limited request runs. Writing a **different** value per request throws `ERR_BAD_OPTION_VALUE` (repeating the effective value is fine), so "configured but silently ignored" cannot happen.

Dispose an instance when done (`using`) — the pool and handlers go with it. In-flight requests are not aborted by `Dispose`; the client just stops accepting new ones.

## Requests

### Methods and URLs

| Method | Notes |
| --- | --- |
| `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` | each has a non-generic form (`Data` is `JsonElement`) and a generic form (`Data` is `T`); all accept `data`, `configure` and a `CancellationToken` |
| `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` | send `multipart/form-data` by default |
| `RequestAsync(config)` / `RequestAsync<T>(config)` | full `AxiosConfig` |
| `GetUri(config)` / `GetUri(c => ...)` | build the URL without sending |
| `Interceptors` / `Defaults` / `Pool` / `UsesConnectionPool` | instance members |

- `url` can be relative (joined with `BaseAddress`) or absolute. `null`/blank throws `ERR_INVALID_URL` — pass `"/"` explicitly to hit the base address.
- `Method` is BCL `HttpMethod`, so custom verbs work: `new HttpMethod("PROPFIND")`.
- Escape path segments with `AxiosUrl.EncodePathSegment(value)`.
- For pre-signing or logging, use the string returned by `GetUri`: it is exactly what this call would send (replaying `response.Config` recomputes it against the same instance).

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

- Collection shape comes from `Serialization.CollectionFormat`: `Brackets` (default, `a[]=1`) / `Multi` (`a=1&a=2`) / `Csv` / `Ssv` / `Tsv` / `Pipes`.
- Keys and values are encoded with `Uri.EscapeDataString` (space → `%20`, non-ASCII → UTF-8 percent-encoding). Query strings written directly into `Url` are not re-encoded.
- Passing a scalar as `Params` (`int` / `bool` / `DateTime`…) throws `ERR_BAD_OPTION_VALUE` (there is no key to express).
- Full control: `c.Serialization.ParamsSerializer = p => "a=1&b=2"` (receives the whole `Params` object, returns an encoded string).
- Scalar text form (shared by `Params` / `[Path]` / `[Header]` / forms): `bool` → `true`/`false`, enums → name, `DateTime` / `DateTimeOffset` → UTC millisecond ISO (`Kind = Unspecified` is treated as UTC), everything else via `IFormattable` + `InvariantCulture`.

### Body and ContentType

**The encoding is decided by `ContentType` alone** (default `application/json`), not by the runtime type of `data`.

| `ContentType` | Objects / dictionaries / dynamic / collections | Scalars (numbers / bool / enum / Guid / dates…) | `string` / `byte[]` / `Memory<byte>` / `Stream` / `HttpContent` |
| --- | --- | --- | --- |
| `application/json` (default) | serialized as JSON | raw text (`42`, `true`, `Paid`) | sent as-is |
| `application/x-www-form-urlencoded` | key/value form (`a[b]=v`, `a[]=v`, null skipped) | ignored (empty body) | sent as-is |
| `multipart/form-data` | fields + file parts | throws `ERR_BAD_OPTION_VALUE` (wrap it in a field) | throws `ERR_BAD_OPTION_VALUE` (`HttpContent` passes through as-is) |
| anything else (`application/xml`, YAML…) | passed to the current serializer | raw text | sent as-is |

```csharp
// JSON (default)
await http.PostAsync("users", new { name = "bob" });

// Form
await http.PostAsync("login", new Dictionary<string, object?>
{
    ["username"] = "alice",
    ["tags"] = new[] { "a", "b" }        // -> tags[]=a&tags[]=b
}, c => c.ContentType = AxiosContentType.FormUrlEncoded);

// Binary: declare the type explicitly, the library never guesses
await http.PostAsync("bin", bytes, c => c.ContentType = AxiosContentType.OctetStream);

// Any verb can carry a body
await http.GetAsync("reports/query", new { from = "2024-01-01" });
await http.DeleteAsync("users/1", new { reason = "duplicated" });
```

Built-in constants: `AxiosContentType.Json` / `FormUrlEncoded` / `MultipartFormData` / `OctetStream` / `Xml` / `JsonPatch` / `ProblemJson` / `TextPlain` / `TextHtml`. Any string works too, including parameters: `"multipart/form-data; boundary=----axios-net"`.

### Forms and uploads

```csharp
// 1) multipart: fields + files (FormFile / Stream / byte[] / HttpContent)
await http.PostFormAsync("upload", new
{
    title = "Q3 report",
    files = new[]
    {
        new FormFile("a.pdf", File.OpenRead("a.pdf"), "application/pdf"),  // stream is disposed with the request
        FormFile.FromPath("b.zip"),                                        // library opens/disposes it
        FormFile.FromBytes("c.txt", bytes, "text/plain")
    }
});

// 2) Raw stream upload (best for large files, constant memory)
await using var source = File.OpenRead(@"D:\data\report.bin");
await http.PostAsync<UploadResult>("api/files/raw", source, c =>
{
    c.ContentType = "application/octet-stream";
    c.Headers.Set("X-File-Name", "report.bin");     // a raw stream has no file name
});

// 3) Your own MultipartFormDataContent
using var multipart = new MultipartFormDataContent();
multipart.Add(new StringContent("alice"), "name");
await http.PostAsync("upload", multipart);          // passed through with its own headers and boundary
```

- Field names come from property names / dictionary keys (honoring `[JsonPropertyName]` / `[JsonIgnore]`); nested objects become `meta[author]`, collections become repeated parts. Customize with `Serialization.FormFieldNameSelector`.
- A root-level file array has no field name: set `FormFile.FieldName`.
- Part contents are disposed with the request (success, failure and cancellation alike). Copy to `byte[]` if you need to reuse them.
- Field names / file names containing `"`, `\`, CR/LF or control characters would break `Content-Disposition` and are rejected before sending.
- In ASP.NET Core projects alias `FormFile` to avoid ambiguity: `using ClientFile = AxiosNet.FormFile;`.

### Response types and downloads

```csharp
// Strongly typed
var typed = await http.GetAsync<User>("users/1");
User u = typed.Data!;

// Non-generic: Data is JsonElement (invalid JSON falls back to text, binary gives byte[])
var raw = await http.GetAsync("users/1");

// Text / ArrayBuffer / Stream / Dynamic
var text   = await http.GetAsync("ping", c => c.ResponseType = ResponseType.Text);
var bytes  = await http.GetAsync("file", c => c.ResponseType = ResponseType.ArrayBuffer);
var stream = await http.GetAsync("file", c => c.ResponseType = ResponseType.Stream);

// dynamic: no DTOs
var dyn = await http.GetAsync<dynamic>("users/1");
Console.WriteLine($"{dyn.Data.name}, {dyn.Data.address.city}, {dyn.Data.roles[0]}");
```

`<Stream>`, `<byte[]>` and `<dynamic>` switch the response type automatically.

Downloads:

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

// File name from the server
var disposition = ContentDispositionHeaderValue.Parse(response.GetHeader("Content-Disposition")!);
var fileName = (disposition.FileNameStar ?? disposition.FileName)?.Trim('"') ?? "download.bin";
```

- The stream **must be disposed** (`using` / `await using`): the connection returns to the pool when the body is consumed or released. Releasing it half-read discards that TCP connection.
- The origin lease stays held during the download, so idle eviction will not cut it off.
- A non-2xx streaming response is drained and released immediately (`ex.Response.Data` is `null`, `Status` / `Headers` remain).

### Streaming responses

`RequestStreamAsync` turns the body into an `IAsyncEnumerable<T>`: `await` yields status code and headers, frames are read during enumeration.

```csharp
var response = await client.RequestStreamAsync(
    new ChatStreamRequest { Prompt = "hi" },
    AxiosStreamFormat.ServerSentEvents);          // no format argument = NDJSON

await foreach (var chunk in response.Data!)
{
    Console.Write(chunk.Text);
}
```

| Format | Frame boundary | Typical source |
| --- | --- | --- |
| `NewlineDelimitedJson` (default) | one JSON value per line, blank lines skipped | log exports, row-by-row results |
| `ServerSentEvents` | blank line ends a frame, `data: [DONE]` ends the stream | LLM token streams, market feeds |
| `JsonArray` | each element of a top-level array `[{...},{...}]` | paged / batch lists |

```csharp
// Paged list: elements are delivered as they complete, no need to wait for the closing ]
var pages = await client.RequestStreamAsync(new ListOrdersRequest(), AxiosStreamFormat.JsonArray);
await foreach (var order in pages.Data!)
{
    Console.WriteLine(order.Id);
}
```

- **Timeouts**: `Timeout` only covers the response headers for streaming calls; the body is governed by `Limits.StreamIdleTimeout` (`null` = body shares the `Timeout` budget; `Zero` = unlimited; `> 0` = idle timeout measured during actual reads).
- **Frame limit**: 8 MB by default, valid range 1–64 MB, adjustable via `Limits.MaxStreamFrameLength`; out-of-range throws `ERR_BAD_OPTION_VALUE` before sending. There is no "unlimited" mode (a whole frame must fit in memory), and peak memory is roughly 2–4× the limit.
- **Encoding**: NDJSON / SSE require an ASCII-compatible charset (UTF-8, GBK…); wide charsets fail with `ERR_BAD_RESPONSE` when the stream is opened. `JsonArray` requires UTF-8. A UTF-8 BOM is skipped.
- **Ownership**: the stream can be enumerated once; `await foreach` disposes it on completion or `break`. Manual `MoveNextAsync` callers must `DisposeAsync` themselves.
- **Errors**: bad status codes throw at `await` (an `ErrorFactory` also applies); frame parsing errors are `ERR_BAD_RESPONSE`, mid-stream disconnects `ERR_NETWORK`, cancellation `ERR_CANCELED`.

### Timeouts and cancellation

```csharp
// Per-request timeout
await client.GetAsync("slow", c => c.Timeout = TimeSpan.FromMilliseconds(500));

// Turn off an instance default timeout for one call
await client.GetAsync("slow-but-wanted", c => c.Timeout = TimeSpan.Zero);

// External cancellation (the AbortController equivalent)
using var cts = new CancellationTokenSource();
var task = client.GetAsync("slow", cancellationToken: cts.Token);
cts.CancelAfter(TimeSpan.FromSeconds(1));
```

- `Timeout` is a **total budget** (connect + headers + body). Default is `TimeSpan.Zero` (no timeout). Values above ~49.7 days are clamped to the timer limit instead of throwing.
- Timeouts throw `ECONNABORTED` (message customizable via `Callbacks.TimeoutMessage`), cancellation throws `ERR_CANCELED`; both keep the underlying `OperationCanceledException` in `Cause`.

### Progress, rate limiting and size limits

```csharp
await http.PostAsync("upload", payload, c =>
{
    c.Callbacks.OnUploadProgress   = e => Console.WriteLine($"↑ {e.Progress:P0} {e.Rate / 1024:F1} KB/s ETA {e.Estimated}");
    c.Callbacks.OnDownloadProgress = e => Console.WriteLine($"↓ {e.Progress:P0} {e.Rate / 1024:F1} KB/s");
    c.Limits.MaxRate = 1024 * 1024;                 // one-way throttle, 1 MB/s
    c.Limits.MaxBodyLength = 50 * 1024 * 1024;      // request body limit, checked before sending
    c.Limits.MaxContentLength = 10 * 1024 * 1024;   // response body limit, counted while reading
});
```

`AxiosProgressEvent`: `Loaded` / `Total` (`null` when unknown) / `Progress` (0–1, capped at 100%, `null` when `Total` is unknown) / `Rate` (average bytes per second) / `Estimated` (remaining time) / `Upload`.

- Callbacks run **synchronously** — move heavy work elsewhere. Reports are throttled to one per 50 ms.
- A throwing callback aborts the request: upload → `ERR_BAD_REQUEST`, download → `ERR_BAD_RESPONSE`, original exception in `Cause`.
- `MaxContentLength` aborts as soon as the count is exceeded for buffered responses; for `ResponseType.Stream` it pre-checks the declared length and keeps counting during reads.
- `MaxBodyLength` only applies when the length is known (JSON / forms / `byte[]` / seekable streams); multipart is not checked.
- Negative values throw `ERR_BAD_OPTION_VALUE`; `MaxRate = 0` means unlimited.

### In-flight concurrency cap

`MaxRate` limits **bytes per second**; it does nothing about "hundreds of tiny requests per second" — that pressure comes from how many requests are in flight at once. The concurrency gate covers that dimension:

```csharp
// Instance-wide: at most 4 requests in flight, the rest queue up
var client = Axios.Create(c =>
{
    c.Limits.MaxConcurrentRequests = 4;
    c.Limits.QueueTimeout = TimeSpan.FromSeconds(2);   // wait at most 2 s for a slot
});

// Per-request patience (affects only this wait, never the gate itself)
await client.GetAsync("fast", c => c.Limits.QueueTimeout = TimeSpan.Zero);   // fail fast, no queueing
```

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxConcurrentRequests` | `null` (unlimited) | max requests in flight for this instance; positive integer |
| `QueueTimeout` | `null` (wait forever) | wait cap for a slot: `null` / `Zero` (no queueing) / `>0` (fail with `ERR_QUEUE_TIMEOUT`) |

- Queueing out throws **`ERR_QUEUE_TIMEOUT`** (not `ECONNABORTED`): the request was never sent, so the fix is back-pressure / more capacity / backoff — not a bigger `Timeout`.
- Queue time is **not** counted against `Timeout` (which covers only the part that was actually sent); `QueueTimeout` bounds the wait on its own (once per attempt), and `CancellationToken` cancels it (`ERR_CANCELED`).
- The slot covers the whole in-flight span: every retry attempt re-acquires (backoff does not hold a slot), and a **streaming response holds it until the body stream is disposed**.
- The gate is shared per instance, so `MaxConcurrentRequests` is instance-level: a different value per request (or changing `Defaults` after the gate exists) throws `ERR_BAD_OPTION_VALUE`; create a new instance to change the cap. Configure it before the first limited request.
- **Self-deadlock risk**: issuing a sub-request through the **same instance** from an interceptor or progress callback makes the inner request compete for the same slots — with a small cap (especially 1) both wait forever. Set `QueueTimeout` for that pattern, or send sub-requests through another instance.
- The default retry policy does not retry `ERR_QUEUE_TIMEOUT` (it is not in the network / timeout / 408 / 429 / 5xx set); opt in with a custom policy and make sure the backoff gives slots time to free up.
- Not the same knob as the pool's `MaxConnectionsPerServer`: that one counts **connections**, and HTTP/2 multiplexing lets one connection carry many requests; this one counts **requests**.

## Errors

Every failure surfaces as `AxiosException`:

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
    Console.WriteLine(ex.Cause);     // underlying exception (DNS / connect failures), may be null
    Console.WriteLine(ex.ToJson());  // structured snapshot for logging
}

if (Axios.IsAxiosError(ex) && !Axios.IsCancel(ex)) { /* ... */ }
```

| Code | Raised when |
| --- | --- |
| `ERR_BAD_REQUEST` | 4xx; request body exceeds `Limits.MaxBodyLength`; body serialization fails; upload progress callback throws |
| `ERR_BAD_RESPONSE` | 5xx; response cannot be deserialized into the target type; body exceeds `Limits.MaxContentLength`; generic argument and `responseType` disagree; download callback or `transformResponse` throws |
| `ECONNABORTED` | `Timeout` exceeded (or streaming idle timeout) |
| `ERR_NETWORK` | connection refused / DNS failure / connection dropped / truncated body |
| `ERR_CANCELED` | `CancellationToken` cancelled, or the client was disposed |
| `ERR_QUEUE_TIMEOUT` | every concurrency slot stayed busy past `Limits.QueueTimeout` (request never sent) |
| `ERR_INVALID_URL` | invalid URL, relative URL without `BaseAddress`, non-http(s) scheme |
| `ERR_BAD_OPTION_VALUE` | invalid option value (per-request instance-only option, multipart header/body mismatch, scalar `Params`, out-of-range retry settings…) |

Custom success range (2xx by default):

```csharp
await client.GetAsync("maybe-404", c => c.Callbacks.ValidateStatus = s => s is >= 200 and < 500);
```

## Interceptors

```csharp
// Request interceptors: executed in reverse registration order; sync and async overloads
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

// Response interceptors: executed in registration order; the rejected branch can recover
client.Interceptors.Response.Use(
    onFulfilled: response => response,
    onRejected: ex =>
    {
        var error = (AxiosException)ex;
        if (error.Code == AxiosException.ErrBadRequest && error.Status == 401)
        {
            // refresh the token and replay, or return a fallback response
        }
        throw ex;
    });

// Remove one
var id = client.Interceptors.Request.Use(cfg => cfg);
client.Interceptors.Request.Eject(id);
```

## Data transforms

Same two chains as axios: the request direction runs after interceptors and before encoding; the response direction runs after deserialization.

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

    // Strip a { code, data } envelope
    c.TransformResponse.Add((data, cfg) =>
        data is JsonElement { ValueKind: JsonValueKind.Object } e && e.TryGetProperty("data", out var inner)
            ? inner
            : data);
});

// Per-request append (instance chain first, then this one)
await http.GetAsync("users/1", c => c.TransformResponse.Add((d, _) => Normalize(d)));
```

- The chain is `IList<Func<object?, AxiosConfig, object?>>`; returning `null` is allowed and passes through.
- The value returned by `TransformRequest` is still encoded according to `ContentType`.
- `TransformResponse` runs for `Json` / `Text` / `ArrayBuffer` / `Dynamic`, not for `Stream` (the body has not been read).

## Serialization

Bodies are encoded and decoded by System.Text.Json by default and can be replaced entirely.

**JSON options, three levels** (later wins):

| Level | Entry point |
| --- | --- |
| Global | `AxiosJson.Options` |
| Instance | `Axios.Create(c => c.Serialization.JsonSerializerOptions = ...)` |
| Per request | `c => c.Serialization.JsonSerializerOptions = ...` |

Defaults are Web-style (camelCase output, case-insensitive input). Assign a **new** instance when replacing `AxiosJson.Options` (STJ freezes used options); do it once at startup.

**Built-in XML**:

```csharp
using AxiosNet.Serialization;

// Option A: inject globally / per instance; the request header becomes application/xml
AxiosContent.Serializer = XmlContentSerializer.Instance;
await http.PostAsync("orders", order);

// Option B: register and let Content-Type pick (multiple formats in one app)
AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
await http.PostAsync("orders", order, c => c.ContentType = AxiosContentType.Xml);
```

**Custom formats**: implement `IContentSerializer` (two text methods; for binary formats also the byte pair `SerializeToBytes` / `Deserialize(ReadOnlyMemory<byte>)`; optional `MediaType` to advertise the media type).

```csharp
public sealed class YamlSerializer : IContentSerializer
{
    public string? MediaType => "application/yaml";

    public string Serialize(object? value, Type type, AxiosConfig config) => YamlConvert.Serialize(value, type);

    public object? Deserialize(string text, Type type, AxiosConfig config) => YamlConvert.Deserialize(text, type);
}

AxiosContent.Serializer = new YamlSerializer();
```

- A serializer may write request headers (`config.Headers.Set(...)`); the `config` it receives is a per-request copy, so instance defaults are unaffected.
- Forms / multipart, scalars, `string` / `byte[]` / `Stream` never go through the serializer.
- Implementations must be thread-safe (shared across concurrent requests); exceptions become `ERR_BAD_REQUEST` / `ERR_BAD_RESPONSE`.
- Text encodings: `Serialization.RequestEncoding` / `Serialization.ResponseEncoding` (default UTF-8 / auto-detect). GBK and other code pages require `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` first, otherwise you get silent mojibake.

## Retries

No retries by default; attach a policy to enable them:

```csharp
using AxiosNet.Retry;

var client = Axios.Create(c => c.Retry.Policy = new RetryPolicy { MaxRetries = 3 });
// or
config.Retry.Policy = RetryPolicy.Constant(3, TimeSpan.FromSeconds(1));                                     // fixed delay
config.Retry.Policy = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));  // exponential backoff

// Disable the instance policy for one call (explicit null differs from "not set")
await client.GetAsync("orders", c => c.Retry.Policy = null);

// Observe retries
client.Defaults.Retry.OnRetry = ctx => logger.LogWarning(
    "attempt {Attempt}/{Max} failed ({Code}), retrying in {Delay}ms",
    ctx.Attempt, ctx.MaxRetries, ctx.ErrorCode, ctx.Delay.TotalMilliseconds);
```

`RetryPolicy` defaults (all overridable):

| Case | Default |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED` | retry |
| 408 / 429 / 5xx | retry (`RetryStatusCodes` to change) |
| other 4xx, config / URL errors | no retry |
| `ERR_CANCELED` | no retry |
| POST / PATCH and other non-idempotent methods | no retry (`RetryNonIdempotentMethods = true` to enable) |

Other members: `Delay` / `BackoffFactor` / `MaxDelay` / `Jitter` (default `true`, waits land in 50%–100%) / `HonorRetryAfter` (default `true`: if the server sends `Retry-After`, wait that long, still capped by `MaxDelay`) / `RetryWhen` (extra condition).

Semantics:

- `Timeout` is the budget **per attempt** (3 retries × 10 s = up to 40 s); use `CancellationToken` for a total budget.
- Interceptors, `transformRequest` and body serialization run once; the response interceptor sees the final outcome.
- The body must be replayable: seekable streams are rewound before each attempt; non-seekable streams and `StreamContent` (root or multipart part) throw `ERR_BAD_OPTION_VALUE` as soon as retries are enabled.
- Custom policies implement `IAxiosRetryPolicy` (`MaxRetries` / `ShouldRetry(ctx)` / `GetDelay(ctx)`); `ctx` exposes `Attempt` / `MaxRetries` / `Exception` / `Response` / `StatusCode` / `ErrorCode` / `Delay` / `Config` / `Method`. Instances are shared concurrently — keep them stateless.

## Connection pool

Each `AxiosClient` owns a pool keyed by `scheme://host:port` (`HttpConnectionPool`): one tuned `SocketsHttpHandler` per origin, with TCP keep-alive reuse.

```csharp
using AxiosNet.Pooling;

// 1) Own pool (configured at construction)
var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = true
});

// 2) Share one pool between clients
await using var sharedPool = new HttpConnectionPool(new ConnectionPoolOptions { MaxConnectionsPerServer = 64 });
using var orderClient = new AxiosClient(pool: sharedPool);
using var payClient   = new AxiosClient(pool: sharedPool);
using var payClient2  = Axios.Create(c => c.BaseAddress = "https://pay.example.com/", sharedPool);

// 3) Inspect / evict manually
ConnectionPoolStatistics stats = sharedPool.GetStatistics();
sharedPool.EvictIdleEndpoints();
```

| Option | Default | Meaning |
| --- | --- | --- |
| `MaxConnectionsPerServer` | 100 | max TCP connections per origin; extra requests queue at the connection layer |
| `PooledConnectionLifetime` | 10 min | max reuse time per connection; replaced on next checkout (in-flight requests are not interrupted) |
| `PooledConnectionIdleTimeout` | 2 min | idle connection close |
| `ConnectTimeout` | 15 s | connect timeout |
| `IdleHandlerLifetime` | 5 min | how long an idle origin node is kept |
| `EvictionInterval` | 30 s | background sweep interval; `<= 0` disables it |
| `AllowAutoRedirect` / `MaxAutomaticRedirections` | `true` / 5 | redirects; `MaxRedirects = 0` disables |
| `AutomaticDecompression` | `true` | gzip / deflate / br |
| `EnableMultipleHttp2Connections` | `true` | allow extra connections when h2 stream limits are hit |
| `MaxEndpointCount` | 1024 | origin node cap; the least-recently-used idle node is evicted first |
| `UseCookies` | `false` | one `CookieContainer` shared by the whole pool |
| `Proxy` / `Credentials` | `null` | no proxy by default (system/env proxy is **not** used); pass `HttpClient.DefaultProxy` to opt in |
| `ServerCertificateCustomValidationCallback` / `EnabledSslProtocols` / `ConfigureSslOptions` | `null` | certificate validation, TLS versions, client certificates |
| `HandlerFactory` | `null` | invoked once per origin, see [Custom handlers](#custom-handlers) |

- `Dispose()` is graceful: idle nodes go immediately, nodes with in-flight leases are reclaimed when the last lease returns.
- `Clear()` releases every node immediately and does interrupt in-flight requests.
- DNS convergence is bounded by `PooledConnectionLifetime`. For service discovery, use `HandlerFactory` + `ctx.CreateDefaultHandler()` and set `ConnectCallback`.

### HTTP versions (HTTP/2 by default, HTTP/3 opt-in)

The default is `HttpVersion = Version20` with `VersionPolicy = RequestVersionOrLower`: on https the client negotiates h2 and falls back to 1.1 inside the same TLS handshake; cleartext `http://` always uses 1.1.

```csharp
// Defaults, nothing to configure
var client = new AxiosClient();

// Back to HTTP/1.1 (legacy gateways, NTLM, connection-level headers)
await client.GetAsync(url, c => c.Connection.HttpVersion = HttpVersion.Version11);

// Require h2, fail if it cannot be negotiated
await client.GetAsync(url, c =>
{
    c.Connection.HttpVersion = HttpVersion.Version20;
    c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
});

// Cleartext h2c: only Exact does h2
await client.GetAsync("http://h2c.example.com/", c =>
{
    c.Connection.HttpVersion = HttpVersion.Version20;
    c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
});

// HTTP/3: silently downgrades when the environment or server cannot do it
await client.GetAsync(url, c => c.Connection.HttpVersion = HttpVersion.Version30);
```

| Configuration | What goes on the wire |
| --- | --- |
| default (`Version20` + `OrLower`) | h2 when negotiated on https, otherwise 1.1; cleartext always 1.1 |
| `Version20` + `Exact` | h2 only, error if negotiation fails (also the h2c switch on cleartext) |
| `Version11` (default policy) | HTTP/1.1 |
| `Version30` (default policy) | h3 when QUIC and the server support it, otherwise downgrade |
| `Version30` + `Exact` | h3 only; impossible environments (cleartext / no QUIC) throw `ERR_BAD_OPTION_VALUE` before sending, server refusal is `ERR_NETWORK` |

- Read the negotiated version from `response.ProtocolVersion`; `Config.Connection.HttpVersion` is only the intent.
- Check local capability with `Http3Support.IsSupported` / `UnsupportedReason` (.NET 6 has h3 off by default: set `System.Net.SocketsHttpHandler.Http3Support=true` in runtimeconfig or `DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=1`, and MsQuic must be available).
- `HttpVersion` / `VersionPolicy` are per-request; retries keep the same pair.
- `WithCredentials = true` uses `CredentialCache.DefaultCredentials`, and NTLM / Negotiate are connection-level authentication that h2 cannot carry — use `Version11` for IIS / domain-integrated auth.

### Custom handlers

| Need | Usage | Pooled |
| --- | --- | --- |
| Wrap a logging / signing / tracing `DelegatingHandler` | `ConnectionPoolOptions.HandlerFactory` | yes |
| Take over the transport completely (test stubs) | `new AxiosClient(handler: ...)` | no |

```csharp
using AxiosNet.Pooling;

// Option 1: created once per origin, inner handler configured by the pool
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

- `HandlerFactory` runs once per origin, must return a fresh instance, and the pool disposes it; returning `null` throws `InvalidOperationException` on first use of that origin.
- With an injected handler `client.Pool` is `null` and `UsesConnectionPool` is `false`; connection-level options (`MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials`) belong to the handler and throw `ERR_BAD_OPTION_VALUE` if set on defaults.
- Timeouts, progress, rate limiting and size limits are unaffected.

## Observability

All three signals are on by default and share the name `AxiosNet`.

```csharp
builder.Services.AddOpenTelemetry()
    .WithTracing(t => t.AddSource(AxiosTelemetry.ActivitySourceName))   // "AxiosNet"
    .WithMetrics(m => m
        .AddMeter(AxiosTelemetry.MeterName)                             // "AxiosNet"
        .AddView(AxiosTelemetry.RequestDurationInstrumentName,
            new ExplicitBucketHistogramConfiguration
            {
                // durations are in seconds; OTel default boundaries are millisecond-scaled
                Boundaries = new[] { 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10 }
            }));
```

**Traces**: one span per logical request (retries do not create new spans), named after the HTTP method.

| Tag | Meaning |
| --- | --- |
| `http.request.method` / `url.scheme` / `server.address` / `server.port` / `url.path` | target and method |
| `url.full` | full URL without userinfo; omitted when `Telemetry.CaptureUrl = false` |
| `http.response.status_code` | when a response exists |
| `network.protocol.version` | the version actually negotiated (`1.0` / `1.1` / `2` / `3`) |
| `http.request.body.size` / `http.response.body.size` | byte counts (omitted when unknown) |
| `http.request.resend_count` | retries performed |
| `error.type` | status code → library error code → exception type name |
| `axiosnet.error.code` | library error code (e.g. `ERR_NETWORK`), alongside `error.type` |
| `axiosnet.pool.endpoint.reused` | whether an existing pool node was reused (`false` = a node was created, usually slower) |

- 4xx / 5xx are marked `Error` even when `validateStatus` accepts them; failures also add an `exception` event.
- Every retry decision adds a `retry` event.
- For `ResponseType.Stream` the span ends when headers arrive, so its duration is time-to-first-byte.

**Metrics** (source `AxiosNet`):

| Instrument | Type | Unit | Tags |
| --- | --- | --- | --- |
| `axiosnet.client.request.duration` | `Histogram<double>` | `s` | method / server.address / server.port / status_code / error.type |
| `axiosnet.client.active_requests` | `ObservableGauge<long>` | `{request}` | none |
| `axiosnet.client.request.retries` | `Counter<long>` | `{retry}` | method / server.address / server.port / error.type |
| `axiosnet.client.pool.leases` | `Counter<long>` | `{lease}` | server.address / server.port / `axiosnet.pool.endpoint.reused` |
| `axiosnet.client.pool.endpoints.created` | `Counter<long>` | `{endpoint}` | server.address / server.port |
| `axiosnet.client.pool.endpoints.evicted` | `Counter<long>` | `{endpoint}` | server.address / server.port / `axiosnet.pool.eviction.reason` (`idle` / `capacity` / `manual` / `clear` / `dispose`) |
| `axiosnet.client.pool.active_leases` | `ObservableGauge<long>` | `{lease}` | none |

- Metrics use low-cardinality tags only (method / host / port / status code / error type); URL paths live on spans.
- `request.duration` covers the logical request (including retries and backoff) and shares its window with the span.
- `active_requests` drops when the request finishes (as soon as headers arrive for streaming responses); `active_leases` drops when the body stream is released — use it to see how much concurrency long downloads hold.
- Pool metrics describe the pool as a resource and ignore `Telemetry.Enabled`; compute the node hit ratio on the backend from the `reused` series (it measures node hits, not TCP connection reuse).

**Events** (`DiagnosticListener` named `AxiosNet.DiagnosticListener`): `AxiosNet.RequestStart` / `RequestStop` / `RequestFailed` / `Retry`, with public payload types (`AxiosRequestStartEvent` / `StopEvent` / `FailedEvent` / `AxiosRetryContext`).

**Turning it off** (both are per-request):

```csharp
await client.GetAsync<object>("health", configure: c => c.Telemetry.Enabled = false);   // no telemetry at all
await client.GetAsync<object>("api",    configure: c => c.Telemetry.CaptureUrl = false); // no url.full
```

## Declarative API client

Namespace `AxiosNet.WebAPI` (shipped in the same package): describe the request with a model, configure authorization, then call `client.RequestAsync(request)`.

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
Console.WriteLine($"{response.Status} {response.Data!.Name}");

string url = client.GetUri(new GetUserRequest { Id = 5 });   // build the URL only
```

| Binding | Attribute | Notes |
| --- | --- | --- |
| Path segment | `[Path]` | replaces `{name}`, URL-escaped, `null` is an error |
| Query | `[Query]` / unannotated properties | query by default; arrays `a[]=v`, nesting `a[b]=v` |
| Dictionary query | `[Query]` + dictionary | flattened `?a=1&b=2` when unnamed, `?filters[a]=1` when named |
| Headers | `[Header]` / `[HeaderCollection]` | `null` values skipped |
| Static headers | `[Headers]` (class level) | `"Name: Value"`; a bare name removes that header |
| Form | `[Multipart]` + `[Part]` | the model itself is the form; `[Part]` overrides name / file name / part content type |
| Body | `[Body]` | at most one per model; `null` sends no body |
| Exclude | `[ApiIgnore]` | not bound at all |

- **Single-payload models can omit `[Body]`**: for POST / PUT / PATCH with exactly one unannotated **non-scalar** property, that property becomes the body (dictionary properties are excluded — they stay query parameters).
- One property can carry several binding attributes (`[Path]` + `[Header]`); the value is read once and applied to each.
- Per-request tweaks: derive from `ApiRequest<T>` and override `Configure(config)`, or call `RequestAsync(request, config => ...)`.
- Invalid models (missing route, unbound placeholder, multiple bodies…) throw `InvalidOperationException` on first use. Validate at startup with `ApiClient.ValidateModels(typeof(A), ...)` or `ValidateModels(assembly)` — it returns every error message instead of throwing.
- Authorization: `[Authorize]` / `[Authorize("Basic")]` mark a model as protected, `[AllowAnonymous]` skips the whole chain; `IAuthorizationProvider` is the extension point (built-in `BearerAuthorizationProvider`); `AccessTokenProvider` only supplies tokens.
- Typed errors: set `client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()` to get `ApiError<TError>` for status-code failures (network / timeout / cancellation bypass the factory).
- Also available: `client.UrlParameterFormatter` for `[Path]` / `[Query]` formatting, `client.Axios` to reach the underlying `AxiosClient` (interceptors, pool, handler injection), and `client.StreamIdleTimeout` for streaming bodies.

## Dependency injection

```csharp
using AxiosNet.DependencyInjection;
using AxiosNet.WebAPI.DependencyInjection;

// Core client only
builder.Services.AddAxiosClient(config =>
{
    config.BaseAddress = "https://api.example.com";
    config.Timeout = TimeSpan.FromSeconds(10);
});

// Declarative client with its own underlying client
builder.Services.AddApiClient("https://api.example.com",
    configureDefaults: config => config.Timeout = TimeSpan.FromSeconds(10),
    configure: client => client.Authorization = new BearerAuthorizationProvider(token));

// Declarative client as a facade over the registered AxiosClient singleton
builder.Services.AddApiClient(configure: client => client.Authorization = new BearerAuthorizationProvider(token));

// Multiple sites: one named registration per site
builder.Services.AddApiClient("a", "https://a.example.com", configure: c => c.Authorization = providerA);
builder.Services.AddApiClient("b", "https://b.example.com", configure: c => c.Authorization = providerB);
```

| Extension method | Registers |
| --- | --- |
| `AddAxiosClient(configure)` | singleton `AxiosClient` |
| `AddApiClient(baseAddress, configureDefaults, configure)` | singleton `ApiClient` with its own client |
| `AddApiClient(configure)` | singleton `ApiClient` reusing the container's `AxiosClient` |
| `AddApiClient(name, baseAddress, ...)` | singleton `IApiClientResolver` + named sites |

- TryAdd semantics: existing registrations win, so no second pool is created.
- `IAuthorizationProvider` / `IAccessTokenProvider` / `IApiErrorFactory` singletons in the container are attached automatically; explicit `configure` settings win.
- The container owns the lifetime: disposing an injected `ApiClient` does not dispose the shared client.
- Resolve named sites via `IApiClientResolver.Get(name)` / `TryGet` / `Names`; .NET 8+ can use keyed services (`AddKeyedSingleton` + `[FromKeyedServices]`) instead.

## Configuration reference

Top-level `AxiosConfig` members:

| Member | Type | Default | Notes |
| --- | --- | --- | --- |
| `BaseAddress` | `string?` | `null` | axios `baseURL` |
| `Url` | `string?` | `null` | relative or absolute |
| `Method` | `HttpMethod` | `Get` | BCL type, custom verbs allowed |
| `Headers` | `AxiosHeaders` | empty | case-insensitive, validated on write |
| `RemoveHeaders` | `ICollection<string>` | empty | default headers to strip |
| `Params` | `object?` | `null` | object / dictionary / collection / encoded string |
| `ContentType` | `string` | `application/json` | decides how `Data` is encoded |
| `Data` | `object?` | `null` | request body |
| `Timeout` | `TimeSpan` | `Zero` (no timeout) | total budget |
| `ResponseType` | `ResponseType` | `Json` | `Json` / `Text` / `ArrayBuffer` / `Stream` / `Dynamic` |
| `CancellationToken` | `CancellationToken` | `default` | external cancellation |
| `TransformRequest` / `TransformResponse` | `IList<Func<object?, AxiosConfig, object?>>` | empty | transform chains |
| `Response` | `AxiosResponseInfo?` | — | readable during the response phase (status / headers / media type) for serializers and `transformResponse` |

Six grouped sub-objects:

**`Serialization`**

| Member | Default | Notes |
| --- | --- | --- |
| `JsonSerializerOptions` | Web defaults | STJ options for this request |
| `ContentSerializer` | `null` (built-in JSON) | pluggable serializer |
| `RequestEncoding` / `ResponseEncoding` | `null` | body encoding / forced response decoding |
| `ParamsSerializer` | `null` | `Func<object?, string>` |
| `FormFieldNameSelector` | `prefix[name]` | form field naming |
| `CollectionFormat` | `Brackets` | `Brackets` / `Multi` / `Csv` / `Ssv` / `Tsv` / `Pipes` |

**`Connection`**

| Member | Default | Notes |
| --- | --- | --- |
| `Auth` | `null` | HTTP Basic (`new AxiosAuth(user, pass)`) |
| `MaxRedirects` | 5 | `0` = do not follow (instance-only) |
| `WithCredentials` | `false` | system credentials / cookies (instance-only) |
| `Proxy` / `Credentials` / `PoolOptions` | `null` | instance-only |
| `HttpVersion` | `null` (= 2.0) | per-request |
| `VersionPolicy` | `null` (= `OrLower`) | per-request |

**`Limits`**

| Member | Default | Notes |
| --- | --- | --- |
| `MaxContentLength` | `null` (unlimited) | response body cap, bytes |
| `MaxBodyLength` | `null` (unlimited) | request body cap, bytes |
| `MaxRate` | `null` (unlimited) | one-way throttle, bytes/s |
| `MaxConcurrentRequests` | `null` (unlimited) | in-flight request cap (instance-only, fixed at the first limited request) |
| `QueueTimeout` | `null` (wait forever) | wait cap for a slot: `null` / `Zero` (no queueing) / `>0` (`ERR_QUEUE_TIMEOUT`) |
| `StreamIdleTimeout` | `null` | streaming body: `null` shares `Timeout` / `Zero` unlimited / `> 0` idle timeout |
| `MaxStreamFrameLength` | `null` (= 8 MB) | streaming frame cap, 1–64 MB; constants `DefaultStreamFrameLength` / `MinStreamFrameLength` / `MaxStreamFrameLengthLimit` |

**`Callbacks`**

| Member | Notes |
| --- | --- |
| `ValidateStatus` | `Func<int, bool>`, 2xx by default |
| `TimeoutMessage` | custom timeout message |
| `OnUploadProgress` / `OnDownloadProgress` | `Action<AxiosProgressEvent>` |

**`Retry`**

| Member | Default | Notes |
| --- | --- | --- |
| `Policy` | `null` (no retries) | `IAxiosRetryPolicy`; explicit `null` per request disables the instance policy |
| `OnRetry` | `null` | `Action<AxiosRetryContext>` |

**`Telemetry`**

| Member | Default | Notes |
| --- | --- | --- |
| `Enabled` | `null` (= on) | `false` disables every signal for this request |
| `CaptureUrl` | `null` (= record) | `false` omits `url.full` |

> Sub-objects are created on first access, so configs that never touch them allocate nothing extra.

## axios comparison

| axios (JS) | AxiosNet (.NET) |
| --- | --- |
| `axios(config)` | `client.RequestAsync(new AxiosConfig { ... })` |
| `axios(url, config)` | `client.GetAsync(url, c => { ... })` |
| `axios.get/post/put/patch/delete/head/options` | `GetAsync` / `PostAsync` / `PutAsync` / `PatchAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` |
| `axios.create(config)` | `Axios.Create(config)` / `Axios.Create(config, sharedPool)` |
| `axios.getUri(config)` | `client.GetUri(config)` / `client.GetUri(c => ...)` |
| `axios.postForm / putForm / patchForm` | `PostFormAsync` / `PutFormAsync` / `PatchFormAsync` |
| `axios.defaults` | `Axios.Defaults` / `client.Defaults` |
| `res.data / status / statusText / headers / config` | `Data` / `Status` / `StatusText` / `Headers` / `Config` (plus `ProtocolVersion` / `Request`) |
| `interceptors.request.use` / `response.use` / `eject` | `Interceptors.Request.Use` / `Interceptors.Response.Use` / `Eject` |
| `config.params` | `c.Params` |
| `config.data` | `c.Data` (encoding decided by `c.ContentType`) |
| `responseType: 'json' / 'text' / 'arraybuffer' / 'stream'` | `ResponseType.Json` / `Text` / `ArrayBuffer` / `Stream` |
| `validateStatus` | `c.Callbacks.ValidateStatus` |
| `timeout` / `AbortController` | `c.Timeout` / `c.CancellationToken` |
| `auth: { username, password }` | `c.Connection.Auth = new AxiosAuth(user, pass)` |
| `withCredentials` / `proxy` / `maxRedirects` | same names on `Connection` (instance-only) |
| `maxContentLength` / `maxBodyLength` / `maxRate` | same names on `Limits` |
| `onUploadProgress` / `onDownloadProgress` / `timeoutMessage` | same names on `Callbacks` |
| `paramsSerializer` / `formSerializer` | `Serialization.ParamsSerializer` / `Serialization.FormFieldNameSelector` |
| `transformRequest` / `transformResponse` | same names |
| `AxiosError.code` | `AxiosException.Code` (plus `Status` / `Cause` / `ToJson()`) |
| `axios.isAxiosError` / `isCancel` | `Axios.IsAxiosError` / `Axios.IsCancel` |
| `axios-retry` plugin | `c.Retry.Policy` / `c.Retry.OnRetry` |
| — (needs the OTel JS SDK) | built-in `ActivitySource` / `Meter` / `DiagnosticListener` |
| — (no equivalent) | `HttpConnectionPool`, declarative `ApiClient`, `RequestStreamAsync`, `Limits.MaxConcurrentRequests` / `QueueTimeout` (in-flight gate + `ERR_QUEUE_TIMEOUT`) |

Intentional differences:

1. Methods carry an `Async` suffix; `timeout` is a `TimeSpan`; cancellation uses `CancellationToken`.
2. Body encoding comes from `ContentType` (JSON by default), never inferred from the data type — a dictionary does not become a form automatically.
3. For non-generic text responses `Data` is a `JsonElement` (invalid JSON falls back to text); binary responses give `byte[]`. Use `GetAsync<dynamic>` for JS-style access.
4. Writing a **different** instance-only value per request throws `ERR_BAD_OPTION_VALUE` immediately (axios ignores it silently).
5. `params` uses strict `Uri.EscapeDataString` encoding (space is `%20`, not `+`); customize with `ParamsSerializer` if you need the axios form.
6. `transformRequest` / `transformResponse` elements are typed `object?` (C# cannot drop and re-narrow types like JS).
7. Request headers are validated on write (RFC 7230 token, no CR/LF) and throw `ArgumentException` when invalid.
8. `MaxRate` and progress callbacks are implemented in the library on top of `HttpContent` / `Stream` (the BCL has no equivalent).
9. `Timeout` is clamped at ~49.7 days; use `Timeout = Zero` for a truly infinite timeout.

## Samples

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) is an offline console sample with a built-in `TcpListener` demo server and 37 scenarios:

```bash
dotnet run --project samples/AxiosNet.Simple
```

It starts an interactive menu (reflection lists every sample; type a number to run, `a` for all, `q` to quit). The 27 core scenarios cover: static facade, instance creation, JSON / form / multipart, params, Basic auth, dynamic request and response, text / arraybuffer, interceptors, errors and cancellation, connection pool, upload and download, getUri and size limits, progress and rate limiting, custom handlers, single-file upload, Newtonsoft.Json replacement, DNS refresh, Bearer tokens, request signing, XML serialization, binary serialization, retries, HTTP/2 and HTTP/3, and observability.

The 10 declarative scenarios (menu items 28–37) cover: GET detail / list, POST create, authorization with `[AllowAnonymous]`, error handling, global logging, signing and verification, streaming (SSE / JSON array), multipart upload, and the streaming frame limit.

Two more samples:

- [`samples/AxiosNet.Simple.DependencyInjection`](samples/AxiosNet.Simple.DependencyInjection): six Generic Host scenarios.
- [`samples/AxiosNet.Aot`](samples/AxiosNet.Aot): Native AOT + trimming with a loopback server.

## Runtime support and AOT

The library targets `net6.0` only; `net6.0` / `net7.0` / `net8.0` / `net9.0` / `net10.0` and later consume the `lib/net6.0` asset with no configuration. .NET Framework and .NET Core 3.1 or earlier are not supported.

`IsTrimmable` is declared and the library produces 0 trim warnings. AOT publishing needs two things from the consumer:

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

Not available under AOT: `dynamic` responses and runtime-determined types, `XmlContentSerializer`, anonymous objects as params / form values, and `ValidateModels(Assembly)` (use `ValidateModels(typeof(A), ...)`).

## Build and test

```bash
dotnet build AxiosNet.sln
dotnet test AxiosNet.sln
dotnet build AxiosNet.sln -c Release
```

| Test project | Cases | Target frameworks | Executions |
| --- | ---: | --- | ---: |
| `AxiosNet.Tests` (core + declarative) | 1038 | `net8.0` / `net6.0` | 2076 |
| `AxiosNet.DependencyInjection.Tests` | 47 | `net8.0` / `net6.0` | 94 |
| **Total** | **1085** | | **2170** |

Tests run against a hand-rolled `TcpListener` HTTP/1.1 keep-alive server over real TCP, covering request encoding, interceptors and transforms, error codes, timeouts and cancellation, progress / rate / size limits, the connection pool and protocol negotiation, streaming, retries, telemetry, declarative binding and DI registration.

The solution also contains `test/AxiosNet.Benchmarks` (single target `net8.0`), which no test runs:
`dotnet build AxiosNet.sln` builds it (one BenchmarkDotNet restore), `dotnet test` ignores it, and CI
never runs it — benchmark numbers are meaningless on a shared runner.

## Benchmarks

[`test/AxiosNet.Benchmarks`](test/AxiosNet.Benchmarks) is a standalone BenchmarkDotNet project that turns
"did this change help, and did it regress anything else?" into readable numbers. It targets `net8.0` only,
sets `IsPackable=false` and is not referenced by `src/`, so the library keeps its
"single `net6.0` target, zero third-party runtime dependencies" contract.

Keep the two jobs apart: `AxiosNet.Tests/ConcurrencyPerformanceTests` is a **concurrency correctness** test —
85% threshold, 3 × 1s rounds, median of paired ratios, ±15% across processes. It catches collapses, it cannot
resolve 3%. Use the benchmarks to quantify a change, keep that test as the floor.

```bash
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --list flat
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*Merge*'
dotnet run -c Release --project test/AxiosNet.Benchmarks -- --filter '*'
```

| Group | Hot spot | Baseline |
| --- | --- | --- |
| `RequestPipelineBenchmarks` | Full request path (real loopback socket) | `HttpClient_GetString` |
| `ConcurrentPipelineBenchmarks` | Per-request cost with 8 in flight | `HttpClient_Concurrent` |
| `ConfigMergeBenchmarks` | `MergeConfig` / header merge | `GetUri_NoMerge` |
| `ConfigPrimitiveBenchmarks` | `AxiosConfig.Clone` / `AxiosHeaders` primitives | — (absolute values) |
| `FormEncodingBenchmarks` | Form flattening + percent-encoding | `PostForm_FlatAscii` |
| `DynamicDeserializationBenchmarks` | `dynamic` deserialization | `Typed` |
| `StreamingFramingBenchmarks` | Stream framing (NDJSON / SSE / JSON array) | `Ndjson` |

`Ratio` is an in-run ratio against the baseline (more portable than absolute ns, still machine-specific);
`Allocated` is bytes per operation (stable across runs). Reading guide, before/after workflow and known
noise: [`test/AxiosNet.Benchmarks/README.md`](test/AxiosNet.Benchmarks/README.md).

## Project layout

```
.
├─ src/
│  ├─ AxiosNet/                       # library (single target net6.0)
│  │  ├─ Axios.cs                     # static facade
│  │  ├─ AxiosClient*.cs              # instance + request pipeline + shortcuts
│  │  ├─ AxiosConfig*.cs              # config (top level + six groups)
│  │  ├─ AxiosHeaders.cs              # request headers
│  │  ├─ AxiosResponse.cs / AxiosException.cs
│  │  ├─ Interceptors/                # request / response interceptors
│  │  ├─ Retry/                       # IAxiosRetryPolicy / RetryPolicy
│  │  ├─ Serialization/               # IContentSerializer + JSON / XML implementations
│  │  ├─ Telemetry/                   # ActivitySource / Meter / DiagnosticListener
│  │  ├─ Pooling/                     # HttpConnectionPool + options / stats + handler injection
│  │  ├─ WebAPI/                      # declarative API client
│  │  └─ Internal/                    # URL building, body encoding, response reading, traffic metering
│  └─ AxiosNet.DependencyInjection/   # DI extensions (separate package)
├─ samples/                           # Simple (37 scenarios) / DI / Aot
└─ test/                              # AxiosNet.Tests / DependencyInjection.Tests / Benchmarks
```

## License

[MIT](LICENSE)
