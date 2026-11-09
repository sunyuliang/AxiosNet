# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/badge/nuget-AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![tests net8.0](https://img.shields.io/badge/tests%20net8.0-2262%20passed-success)](#build-and-test)
[![tests net6.0](https://img.shields.io/badge/tests%20net6.0-2251%20passed-success)](#build-and-test)
[![coverage](https://img.shields.io/badge/coverage-91.8%25_(branch_87.3%25)-success)](#build-and-test)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[简体中文](README.md) | **English**

An axios-style HTTP client for .NET. The request API mirrors JS [axios](https://github.com/axios/axios), and on top of that it adds what server-side code actually needs: an origin-scoped connection pool, retries, a circuit breaker, concurrent hedging, backup endpoints, streaming responses, an in-flight concurrency gate, a declarative API client (model-based / interface-based / OpenAPI import), and OpenTelemetry instrumentation.

| Item | Value |
| --- | --- |
| Packages | `AxiosNet` (core + declarative client in one package), `AxiosNet.DependencyInjection` (optional), `AxiosNet.Cli` (optional, design-time dotnet tool) |
| Target | Dual-target `net6.0` / `net8.0`: .NET 8+ consumes `lib/net8.0`, .NET 6 / 7 consumes `lib/net6.0` |
| Dependencies | Zero third-party, BCL only |
| Style | Async first: `Task` + `CancellationToken` |
| Telemetry | Trace / metrics / events on by default, zero cost when nobody listens |

> Repository: [Gitee (itsm/axios-net)](https://gitee.com/itsm/axios-net) is the main repo for PRs and tags; [GitHub](https://github.com/sunyuliang/AxiosNet) is a read-only mirror.

---

## Table of contents

| Group | Sections |
| --- | --- |
| Getting started | [Install](#install) · [Quick start](#quick-start) · [Common tasks](#common-tasks) |
| Requests and responses | [Body and ContentType (JSON default)](#body-and-contenttype-json-default) · [Query parameters](#query-parameters) · [Forms and uploads](#forms-and-uploads) · [Streaming request bodies](#streaming-request-bodies) · [Response types and downloads](#response-types-and-downloads) · [Streaming responses](#streaming-responses) · [Request body compression](#request-body-compression) · [Timeouts and cancellation](#timeouts-and-cancellation) · [Progress, rate and size limits](#progress-rate-and-size-limits) · [Sending-request hook (signing / final request)](#sending-request-hook-signing--final-request) · [Raw response bytes](#raw-response-bytes) · [Methods and URLs](#methods-and-urls) · [In-flight concurrency cap](#in-flight-concurrency-cap) |
| Errors and configuration | [Errors](#errors) · [Configuration](#configuration) ([Configuration reference](#configuration-reference) / [Clients](#clients) / [Interceptors](#interceptors) / [Data transforms and serialization](#data-transforms-and-serialization) / [Connections and transport](#connections-and-transport) / [Observability](#observability)) |
| Resilience and cache | [Resilience](#resilience) ([Retries](#retries) / [Circuit breaker](#circuit-breaker) / [Concurrent hedging](#concurrent-hedging) / [Backup endpoints](#backup-endpoints) / [Idempotency keys](#idempotency-keys) / [Attempt pipeline](#attempt-pipeline) / [Short circuit](#short-circuit)) · [Caching](#caching) |
| Declarative | [Declarative client](#declarative-client) ([Model-based](#model-based-api) / [Interface-based](#interface-based-api) / [Value formatting](#value-formatting) / [OpenAPI import](#openapi-import) / [Credentials and token management](#credentials-and-token-management)) |
| Runtime and reference | [Runtime and AOT](#runtime-and-aot) · [Reference](#reference) ([Capability map](#capability-map) / [axios comparison](#axios-comparison) / [Dependency injection](#dependency-injection) / [Samples](#samples) / [Build and test](#build-and-test) / [Project layout](#project-layout)) |
| Other | [License](#license) |

Pick an entry point:

| Goal | Read |
| --- | --- |
| First use | [Install](#install) → [Quick start](#quick-start) → [Capability map](#capability-map) |
| Migrating from axios | [axios comparison](#axios-comparison) → intentional differences → [Quick start](#quick-start) |
| Look up one option | [Configuration reference](#configuration-reference) |
| Server-side resilience | [Resilience](#resilience) |
| Troubleshoot production | [Errors](#errors) → [Observability](#observability) |

## Install

> Upgrading from 1.x: see [breaking changes in 1.5](docs/breaking-changes-1.5.en.md) for renames and tightened behavior.

```bash
dotnet add package AxiosNet                       # core client + declarative API client (one package)
dotnet add package AxiosNet.DependencyInjection   # optional: DI registration

dotnet tool install -g AxiosNet.Cli               # optional: generate clients from OpenAPI (design-time tool)
```

Referencing the source tree: `dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj`

## Quick start

Direct one-line calls:

```csharp

using AxiosNet;

// string form
var rsp=await Axios.GetAsync<string>("https://api.example.com/users/1");

// dynamic object form
var dyUser=await Axios.GetAsync<dynamic>("https://api.example.com/users/1");

// strongly typed form
var user=await Axios.GetAsync<User>("https://api.example.com/users/1");

```

Once endpoints are grouped, the declarative client offers two parallel ways, and **model-based is the default recommendation**: pass a request model directly, or hand an interface to `Create<T>()`. Both share the same client and the same binding / authorization / validation rules (see [Model-based API](#model-based-api) / [Interface-based API](#interface-based-api)).

**Model-based (default recommendation)**

```csharp
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

[Get("users/{id}")]
public sealed class GetUserRequest : IApiRequest<User>
{
    [Path] public int Id { get; set; }
}

var client = new ApiClient("https://api.example.com/");
var user = (await client.RequestAsync(new GetUserRequest { Id = 5 })).Data!;   // User
```

**Interface-based**

```csharp
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

public interface IUserApi
{
    [Get("users/{id}")]
    Task<AxiosResponse<User>> GetAsync([Path] int id, CancellationToken cancellationToken = default);
}

var api = client.Create<IUserApi>();
var user = (await api.GetAsync(5)).Data!;                                      // User
```

The extension entry points share the same facade as `ApiClient` (cached per `AxiosClient`): construct `new ApiClient(client)`, set `CredentialProvider` / `ErrorFactory`, then use `client.Create<T>()` / `RequestAsync` / `RequestStreamAsync` — that state still applies instead of silently becoming a separate anonymous facade.

Five concepts:

| Concept | Type | Responsibility |
| --- | --- | --- |
| Client | `Axios` / `AxiosClient` | Process-wide default instance + instance clients, and the declarative entry point |
| Request | `AxiosConfig` | One request; the top-level leaves |
| Response | `AxiosResponse<T>` | Status, headers and the parsed body |
| Error | `AxiosException` | Single exit for every failure, with a stable `Code` |
| Endpoint | `IApiRequest<T>` / interfaces | Optional extension: describe an endpoint as a model or interface |

The generic argument decides what `Data` is:

| Call | `Data` type | Use for |
| --- | --- | --- |
| `GetAsync<string>` | `string` (raw, not parsed) | Plain-text APIs, debugging, custom parsing |
| `GetAsync<dynamic>` | `AxiosDynamic` (member / index access, missing members return `null`) | Unstable or unknown schemas, quick checks |
| `GetAsync<T>` | `T` | Production code |
| `GetAsync()` (non-generic) | `JsonElement` (falls back to raw text on invalid JSON, `byte[]` for binary) | Inspecting shape, no type needed |

Next: browse by capability in [Common tasks](#common-tasks) or configure item by item in [Configuration](#configuration); for grouped endpoints see [Declarative client](#declarative-client).

## Common tasks

Browse by capability; each section covers one request shape, and every option lives in [Configuration reference](#configuration-reference).

### Body and ContentType (JSON default)

**Encoding is decided by `ContentType` alone, never by the runtime type of `data`** (default `application/json`).

| `ContentType` | Object / dictionary / dynamic / collection | Primitives (number / bool / enum / Guid / date…) | Raw content (`string` / `byte[]` / `Memory<byte>` / `Stream` / `HttpContent` / async byte chunks) |
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

### Query parameters

`Params` accepts anonymous objects and collections, or an already-encoded query string; the flattening and escaping rules follow.

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
- Passing a scalar as `Params` throws `ERR_BAD_OPTION_VALUE` (no key to express). Full control: `c.Serialization.QueryEncoder = p => "a=1&b=2"` (when `Serialization.ValueText` is set, the callback still sees the final text — internal wrappers are stripped).

"Value → text" is one path shared by `Params` / `[Path]` / `[Header]` / forms / multipart / the raw scalar body:

| Value | Text form |
| --- | --- |
| `bool` | `true` / `false` |
| enum | name |
| `DateTime` / `DateTimeOffset` | UTC ISO with milliseconds (`Unspecified` treated as UTC) |
| the same, as the raw scalar body | round-trip `"O"` form |
| `DateOnly` | `yyyy-MM-dd` |
| `byte[]` | base64 |
| anything else | `IFormattable` + `InvariantCulture` |

Override per member with `[Format]`, or per instance with `AxiosConfig.Serialization.ValueText` (its second argument is the channel the value is written through — see [Value formatting](#value-formatting)).

### Forms and uploads

Three styles: `PostFormAsync` for multipart, a raw stream, or a hand-built `MultipartFormDataContent`.

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

- Field names come from property names / dictionary keys (respects `[JsonPropertyName]` / `[JsonIgnore]`); nesting becomes `meta[author]`, collections expand into repeated parts of the same name. Override the rule with `Serialization.FormFieldName`.
- A root-level file array has no key: set `FormFile.FieldName`.
- Parts are disposed with the request (success / error / cancellation); read into `byte[]` first if you need to reuse them.
- One `FormFile` / `Stream` can feed one part only: a stream reads forward once, so the second part would emit 0 bytes — assembly fails with `ERR_BAD_OPTION_VALUE`.
- Field names or filenames containing `"`, `\`, CR/LF or control characters would break `Content-Disposition` and fail before sending.
- In ASP.NET Core `FormFile` is ambiguous, alias it: `using ClientFile = AxiosNet.FormFile;`.

### Streaming request bodies

The body is produced on the fly and never buffered: pass `IAsyncEnumerable<byte[]>` or `IAsyncEnumerable<ReadOnlyMemory<byte>>` as `data`.

```csharp
// Yield chunks as they are produced (here from a stream; DB cursors, compression pipes, log exports alike)
static async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunks(
    Stream source, [EnumeratorCancellation] CancellationToken token = default)
{
    var buffer = new byte[64 * 1024];
    int read;
    while ((read = await source.ReadAsync(buffer, token)) > 0)
    {
        yield return buffer.AsMemory(0, read);
    }
}

await using var file = File.OpenRead(@"D:\data\export.csv");
await http.PostAsync<UploadResult>("ingest", ReadChunks(file), c =>
{
    c.ContentType = "text/csv";          // like any raw content: only Content-Type is applied
});
```

- **Unknown length**: nothing is pre-read and no total is computed; the transport uses `Transfer-Encoding: chunked` (HTTP/1.1; under the default policy plain `http://` falls back to 1.1) or HTTP/2 DATA frames. Consequently `Limits.MaxRequestBytes` cannot stop it — that cap only applies to bodies with a computable length.
- **Single use**: the sequence cannot be rewound, so enabling retries or backup endpoints throws `ERR_BAD_OPTION_VALUE` **before sending** (turn them off when you do not need resends). Concurrent hedging does not throw: it only kicks in when the body can be replayed concurrently (in-memory buffers), so a streaming body is simply sent once. For replayable bodies use `byte[]` or a seekable stream.
- **Chunk size is yours**: each chunk is written as-is, never split or merged; 64–256 KB is the usual sweet spot, and tiny chunks amplify the chunked-framing overhead.
- **Byte chunks only**: an `IAsyncEnumerable<T>` (say `IAsyncEnumerable<Order>`) is not a body and fails with `ERR_BAD_OPTION_VALUE` plus guidance; write JSON arrays to a `Stream` or `byte[]` first.
- **Cancellation**: the request token reaches your iterator (accept it via `[EnumeratorCancellation]`), and cancellation stops the enumeration.
- **307 / 308 redirects**: redirects are followed by default, and those status codes resend the body — the library re-enumerates the sequence, so the producer must be restartable (the iterator is opened again, or a custom implementation returns a new enumerator each time). For one-shot sources (say relaying an already-consumed network stream) set `Transport.MaxRedirects = 0` on the client options (equivalent to `Transport.Pool.AllowAutoRedirect = false` when the pool is built).
- **`PipeReader`**: net6 / net8 base frameworks do not ship `System.IO.Pipelines`, and the library does not take a dependency for it; wrap it with `pipe.Reader.AsStream()` and pass the `Stream` (same chunk-by-chunk path as a non-seekable stream).

### Response types and downloads

`ResponseType` decides how the body is delivered; the generic argument picks the right one for you.

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

- **Timeout**: headers and body share one budget — streaming body reads count against `Timeout` too (or against the remaining `Resilience.TotalTimeout` when set), so a body that runs past it fails with `ECONNABORTED`. With no timeout configured the body is unbounded; exit via a cancellation token.
- **Frame cap**: 8 MB default, valid range 1–64 MB (`Limits.MaxStreamFrameBytes`), out-of-range throws `ERR_BAD_OPTION_VALUE` before sending. There is no "unlimited" mode — a whole frame must fit in memory; peak usage is roughly 2–4× the cap. The cap counts **raw on-wire bytes** (field prefixes, line endings and delimiters included), so each format's usable payload is smaller: 1 B less for NDJSON and JSON Array, and 7 B (LF) or 8 B (CRLF) per line for SSE.
- **Encoding**: NDJSON / SSE require an ASCII-compatible charset, wide-char encodings throw `ERR_BAD_RESPONSE` when taking the stream; `JsonArray` must be UTF-8. A UTF-8 BOM is skipped.
- **Ownership**: the stream can be enumerated once; `await foreach` (including `break`) disposes it, manual `MoveNextAsync` callers must `DisposeAsync` themselves.
- **Explicit `ResponseType`**: the streaming entry points switch to `Stream` only when `ResponseType` was not explicitly set; an explicit non-`Stream` value (per request or instance default) throws `ERR_BAD_OPTION_VALUE` before sending instead of silently overriding the caller's choice.
- **Errors**: status errors throw at `await`; frame parse failures are `ERR_BAD_RESPONSE`, mid-stream disconnects `ERR_NETWORK`, cancellation `ERR_CANCELED`.

### Request body compression

`Compression` (a per-request / instance-default item, `null` = off) makes the library compress the body before sending and write `Content-Encoding`:

```csharp
await http.PostAsync("users", new { name = "bob" }, c => c.Compression = AxiosCompression.Gzip);

var client = new AxiosClient(new AxiosConfig { Compression = AxiosCompression.Brotli });   // instance default
await client.PostAsync(url, data, c => c.Compression = AxiosCompression.None);             // off for one call
```

- Algorithms: `Gzip` / `Deflate` / `Brotli` (`br`), always at `CompressionLevel.Fastest` — HTTP is usually network-bound, so saving CPU beats squeezing the last byte. `Deflate` emits the RFC 1950 zlib wrapper (which is what RFC 7230 means by `deflate`), not raw DEFLATE — Java / Tomcat and most gateways only accept zlib.
- Only bodies the library buffers as bytes are compressed: objects / primitives / `string` / `byte[]` / `Memory<byte>` / form encoding. `Content-Length` (and the `Limits.MaxRequestBytes` check) then reflect the compressed size.
- Rejected with `ERR_BAD_OPTION_VALUE`: `Stream` and streaming bodies (no full length to compress — compress upstream instead), caller-supplied `HttpContent` (encoding belongs to the content), multipart (parts may stream files), and a hand-written `Content-Encoding` header (mutually exclusive with `Compression`).
- The server must decompress request bodies by `Content-Encoding` itself (ASP.NET Core and most frameworks don't do it by default); don't enable this against a peer that can't.

### Timeouts and cancellation

```csharp
await client.GetAsync("slow", c => c.Timeout = TimeSpan.FromMilliseconds(500));   // one request
await client.GetAsync("forever", c => c.Timeout = TimeSpan.Zero);                 // override instance timeout

using var cts = new CancellationTokenSource();                                    // same as AbortController
var task = client.GetAsync("slow", cancellationToken: cts.Token);
cts.CancelAfter(TimeSpan.FromSeconds(1));
```

- `Timeout` is the budget of a **single attempt** (connect + headers + body), default `Zero` meaning no timeout; capped at ~49.7 days, larger values clamp instead of throwing.
- `Resilience.TotalTimeout` is the budget of the **whole logical request** (from the call, including interceptors, queueing, backoff and hedge branches). `Timeout` is copied per retry / endpoint / hedge; `Resilience.TotalTimeout` is a single shared deadline, and each attempt gets `min(Timeout, remaining)`.
- Timeouts throw `ECONNABORTED` (message overridable via `Observability.TimeoutMessage`), cancellation throws `ERR_CANCELED`; the original `OperationCanceledException` is kept in `Cause`. Buffered and streaming reads share the same exit.
- **`ResponseType.Stream` cancellation also throws `ERR_CANCELED`**: cancelling a stream read — a caller-supplied token passed to `ReadAsync` / `CopyToAsync` / `Read` on `Response.Data`, or the request token (method argument / `config.CancellationToken`) — always surfaces as an `AxiosException` with `ERR_CANCELED` (the original OCE stays in `Cause`); timeouts remain `ECONNABORTED`. The classification order stays fixed at `Caller → Canceled → Timeout → None`; it only decides attribution and the error code, and no path throws a raw OCE anymore. **Do not use `catch (OperationCanceledException)` to detect stream-read cancellation** — check `AxiosException.Code`.

```csharp
// "Give me an answer within 15s" — 5 retries x 10s is no longer the upper bound
await client.GetAsync("orders/scan", c =>
{
    c.Timeout  = TimeSpan.FromSeconds(10);   // one attempt
    c.Resilience.TotalTimeout = TimeSpan.FromSeconds(15);   // the whole request
    c.Resilience.Retry.Policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(200));
});
```

- When the budget is exhausted the next attempt is **never sent**: `ECONNABORTED` with a message naming the attempt (`... before attempt #7 could be sent`). These "never sent" failures do not count toward the circuit breaker, are never retried and never fail over (see the failure table under [Circuit breaker](#circuit-breaker)).
- A timeout that was clamped by the budget says `(remaining Resilience.TotalTimeout budget; Timeout = 10000ms)` so you cannot mistake it for a broken `Timeout`.
- Both cases share the same error code, so use `AxiosException.IsTotalTimeoutExceeded` to tell them apart: `true` = the budget ran out before the send (do not retry, the budget is gone), `false` = a timeout after the send (the downstream is not answering, another endpoint is worth trying).
- **A budget-clamped timeout stops right there**: its limit already came from the remaining budget, so the leftover at the deadline (less than one timer tick) cannot carry a meaningful attempt. The call reports the exhausted budget instead of spending that leftover on another send. Likewise, when a backoff cannot fit in the remaining budget the retry does not wait it out — the token is refunded and the exhausted budget is reported immediately.
- Queueing **counts** against `Resilience.TotalTimeout` (it does not count against `Timeout`): the budget measures what the caller actually waits.
- When the remaining budget is tighter than `Resilience.Concurrency.QueueTimeout`, the **queue wait itself is cut at the budget** and fails with `ECONNABORTED` rather than `ERR_QUEUE_TIMEOUT` (the error names the real constraint). This also holds when `QueueTimeout` is unset (wait forever).
- **Streaming bodies count too**: body reads use the remaining budget (already clamped by `Resilience.TotalTimeout`), so a body that runs past it fails with `ECONNABORTED`. For long-lived streams (SSE, large downloads) leave `Timeout` / `Resilience.TotalTimeout` unset and exit via a cancellation token.
- `Resilience.TotalTimeout = null` means no whole-request budget (default); `Zero` is synonymous with `null`; negative throws `ERR_BAD_OPTION_VALUE`; setting `null` per request clears an instance-level default.

### Progress, rate and size limits

All three live in one `configure` call: progress callbacks, a rate cap, and request / response size limits.

```csharp
await http.PostAsync("upload", payload, c =>
{
    c.Observability.OnUploadProgress   = e => Console.WriteLine($"↑ {e.Progress:P0} {e.Rate / 1024:F1} KB/s ETA {e.Estimated}");
    c.Observability.OnDownloadProgress = e => Console.WriteLine($"↓ {e.Progress:P0} {e.Rate / 1024:F1} KB/s");
    c.Transport.MaxRate = 1024 * 1024;                 // 1 MB/s per direction
    c.Limits.MaxRequestBytes = 50 * 1024 * 1024;      // request body cap, checked before sending
    c.Limits.MaxResponseBytes = 10 * 1024 * 1024;   // response body cap, counted while reading
});
```

`AxiosProgressEvent`: `Loaded` / `Total` (`null` when unknown) / `Progress` (0–1, capped at 100%, `null` when `Total` is unknown) / `Rate` (average bytes per second) / `Estimated` (remaining time) / `Upload`.

- Callbacks run **synchronously**; offload expensive work yourself. Reports are throttled to one per 50 ms.
- A throwing callback aborts the request: upload phase → `ERR_BAD_REQUEST`, download phase → `ERR_BAD_RESPONSE`, original exception in `Cause`.
- `Limits.MaxResponseBytes`: plain responses are counted while reading and aborted on overflow; `ResponseType.Stream` pre-checks the declared length and keeps counting while reading.
- `Limits.MaxResponseBytes` defaults to `null` (unlimited, matching axios's `maxContentLength = -1`) and the library emits no warning or log about it: a buffered response (anything other than `ResponseType.Stream`) holds the whole body in memory, so set an explicit cap in production for untrusted or unknown-size downstreams.
- `Content-Length` is a hint, never a cut-off: when the declared value is shorter than the real body (rewritten by a proxy, a custom `HttpContent`, or a handler that does not truncate), the body is still read to the end — no tail loss and no bypass of `Limits.MaxResponseBytes`.
- `Limits.MaxRequestBytes` only applies when the body length is computable (JSON / form / `byte[]` / seekable stream); multipart and unknown-length bodies (non-seekable streams, `IAsyncEnumerable`) are not intercepted.
- Negative values throw `ERR_BAD_OPTION_VALUE`; `MaxRate = 0` means unlimited.
- `MaxRate` timing starts at the **first transferred byte**: the gap between receiving the response and starting to read (thread pool queueing, doing other work first) is not treated as spent budget, so reading later never loosens the cap.

### Sending-request hook (signing / final request)

When you need to rewrite the **final request** just before it goes out (signatures, timestamps), use `OnSendingRequest`:

```csharp
await client.PostAsync<OrderResult>("orders", order, c =>
{
    c.Observability.OnSendingRequest = async (request, config, ct) =>
    {
        // request.Content already holds the bytes produced from `Body`
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(ct);

        request.Headers.TryAddWithoutValidation(
            "X-Signature", Sign(request.Method.Method, request.RequestUri!.PathAndQuery, body));
    };
});
```

- Fires after the URL is merged, the body is serialized and the headers are written, and **before a connection is leased and the request is sent**; the `HttpRequestMessage` you get is the very object used for that send.
- Fires once per attempt: retries, failover endpoints and hedging branches each fire again, so timestamps and nonces are fresh on every send.
- Keys that must stay **identical across attempts** (idempotency keys) do not belong here — every retry would get a new value, turning one write into two. Use [`Resilience.Idempotency`](#idempotency-keys) instead.
- Rewrites apply to that one send: the library clones the message for the attempt before firing (sharing the body), so headers written on the first attempt are not copied into later attempts.
- Cost of reading the body: buffered bodies (JSON / form / `byte[]` / `string`) hand over their bytes directly; streaming bodies (`IAsyncEnumerable<byte[]>` or a non-seekable `Stream`) get consumed, and a body wrapped for progress / rate limiting is serialized a second time — for signing, stick to buffered bodies or sign the headers only.
- A thrown exception aborts the request (`ERR_BAD_REQUEST`, original exception in `Cause`) without leasing a connection, so nothing is sent; cancellation / timeout thrown by the hook is attributed to the attempt (`ERR_CANCELED` / `ECONNABORTED`).
- A body swapped in by the hook still obeys `Limits.MaxRequestBytes`: if its length is known and over the cap, it is rejected with `ERR_BAD_REQUEST` (bodies of unknown length are not intercepted, as described under size caps above).
- A per-request value replaces the instance default, like every other `Observability` item.

### Raw response bytes

When you need the body **before deserialization** (decryption, signature checks, audit logging), use `OnRawResponse`:

```csharp
await client.GetAsync<User>("users/1", c =>
{
    c.Observability.OnRawResponse = (response, bytes) =>
    {
        Console.WriteLine($"{response.Status} {response.MediaType} {bytes.Length}B");
        audit.Write(response.GetHeader("X-Trace"), bytes.Span);
    };
});
```

- Fires after the body is read and before text decoding, deserialization, and `transformResponse`; the bytes are what `HttpClient` delivered (already decompressed per `AutomaticDecompression` / `Decompression`).
- The context is `AxiosResponseInfo`: `Status` / `StatusText` / `Headers` (the same instance as `response.Headers`) / `MediaType` / `CharSet`, plus `GetHeader` for a single header.
- A rejected status still reads the body before throwing, so 4xx / 5xx responses trigger it too (useful for logging error bodies).
- The `ReadOnlyMemory<byte>` is only valid during the callback; copy it if you need to keep it.
- Only for non-streaming network responses: `ResponseType.Stream` has no full byte array, and short-circuit responses never touch the network read path.
- Fires once per body read, including retries that are abandoned and hedging branches that lose; under hedging several branches fire **concurrently**, so the callback must be thread-safe. It runs synchronously and a thrown exception aborts the request (`ERR_BAD_RESPONSE`, original exception in `Cause`).
- A per-request value replaces the instance default, like every other `Observability` item.

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
- Leaving `config.Url` as `null` also means "request `BaseAddress` itself" and is fully equivalent to passing `"/"`: a trailing slash belongs to the address and is kept as-is.
- `Method` is BCL's `HttpMethod`, so custom verbs work: `new HttpMethod("PROPFIND")`.
- Escape path segments with `AxiosUrl.EncodePathSegment(value)`.
- `BaseAddress` is resolved by RFC 3986 §5.2.2 component rules, not string concatenation:
  - the base query is inherited only when the reference path is empty (a query on the reference itself wins); a non-empty relative path drops the base query instead of silently appending it;
  - the base fragment is **always dropped**; a fragment carried by the relative reference is kept in the final URI (fragments are never sent to the server);
  - `GetUri` and the actual send share this resolution path, so they always agree.

### In-flight concurrency cap

`MaxRate` limits **bytes per second** and cannot stop "hundreds of tiny requests per second" — that pressure comes from the number of in-flight requests:

```csharp
var client = Axios.Create(c =>
{
    c.Resilience.Concurrency.MaxInFlight = 4;                 // at most 4 in flight
    c.Resilience.Concurrency.MaxQueue = 16;                       // at most 16 queued; beyond that reject with ERR_QUEUE_FULL
    c.Resilience.Concurrency.QueueTimeout = TimeSpan.FromSeconds(2);    // once queued, wait at most 2s
});

await client.GetAsync("fast", c => c.Resilience.Concurrency.QueueTimeout = TimeSpan.Zero);   // fail immediately when full
```

| Item | Default | Meaning |
| --- | --- | --- |
| `MaxInFlight` | `null` (unlimited) | Max in-flight requests for this instance; positive integer |
| `MaxQueue` | `null` (unlimited) | Queue depth cap: beyond it the request fails with `ERR_QUEUE_FULL`; positive integer, only meaningful with `MaxInFlight` |
| `QueueTimeout` | `null` (wait forever) | Slot wait limit: `null` wait / `Zero` don't queue / `>0` fail on timeout; the wait is also capped by the remaining `Resilience.TotalTimeout` |

- Queue timeout throws **`ERR_QUEUE_TIMEOUT`**, a full queue throws **`ERR_QUEUE_FULL`** (neither is `ECONNABORTED`): nothing was sent, so react by slowing down / scaling out / backing off.
- Both are "the entrance is crowded", not "the downstream failed": the default policy does not retry and does not fail over — another endpoint cannot fix local slot contention, so the caller should back off and try again.
- When a later attempt is rejected at the gate after an earlier real failure, the real failure is the one surfaced (same rule as hedging): "no queue slot" never hides the fault that explains the request.
- Rejections are counted by `axiosnet.client.gate.rejections` (`axiosnet.gate.reason` = `queue_timeout` / `queue_full`); watch it together with `gate.queue_depth` for backpressure level.
- `MaxQueue` is fixed together with `MaxInFlight` on the first gated request: a different per-request value, or mutating `Defaults` after construction, throws `ERR_BAD_OPTION_VALUE` (a request that never mentions this option is not a statement, so the gate's value stands). For "never queue" use `QueueTimeout = TimeSpan.Zero`.
- Queueing **does not** count against `Timeout` (each retry attempt gets its own) but **does** count against `Resilience.TotalTimeout`; it can be cancelled with a `CancellationToken`.
- A slot covers the whole in-flight span: each retry attempt takes one (backoff does not hold it), and **a streaming response holds it until the body stream is disposed**.
- **Self-deadlock risk**: sending a child request on the same instance from inside an interceptor or progress callback competes for the same slots. Set a `QueueTimeout` or use a separate instance.
- **With retries enabled**: `ERR_QUEUE_TIMEOUT` / `ERR_QUEUE_FULL` are never retried — a saturated gate means this instance is already at its in-flight cap, and an immediate resend just hits the same gate. Waiting for concurrency to drop is the caller's job (slow down / raise the cap / use another instance).
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
| `ERR_BAD_REQUEST` | 4xx; request body larger than `Limits.MaxRequestBytes`; request body serialization failure; upload progress callback threw; declarative request assembly failure (path / query / header / body read or formatting) |
| `ERR_BAD_RESPONSE` | 5xx; response cannot be deserialized into the target type; body larger than `Limits.MaxResponseBytes`; generic argument vs `ResponseType` mismatch; download progress callback, raw response callback or `transformResponse` threw |
| `ECONNABORTED` | `Timeout` exceeded (including the streaming body phase), or the `Resilience.TotalTimeout` budget is exhausted |
| `ERR_NETWORK` | Connection refused / DNS failure / connection dropped / truncated body |
| `ERR_CANCELED` | `CancellationToken` cancelled, or the client was disposed |
| `ERR_QUEUE_TIMEOUT` | Concurrency slots stayed full past `QueueTimeout` (request never sent) |
| `ERR_QUEUE_FULL` | Queue depth already at `MaxQueue` (request never sent, not queued) |
| `ERR_CIRCUIT_OPEN` | Circuit is open, request **not sent** (`RetryAfter` suggests how long to wait) |
| `ERR_POOL_EXHAUSTED` | Pool origin cardinality saturated (all in flight, no evictable idle node); request never sent; observable, not retried by default — lower fan-out / raise the cap / `Clear()` then retry |
| `ERR_INVALID_URL` | Invalid URL, relative URL without `BaseAddress`, non-http(s) scheme, or a URL longer than 65519 characters (the pre-.NET 10 `Uri` limit, now enforced by the library itself) |
| `ERR_TOO_MANY_REDIRECTS` | Redirects followed beyond `Transport.MaxRedirects`. With `MaxRedirects = 0` the code is `ERR_BAD_REQUEST` plus a distinct message ("redirects are disabled") so "disabled" and "over the limit" stay distinguishable |
| `ERR_BAD_OPTION_VALUE` | Invalid option value (instance-level option per request, multipart header/body conflict, scalar `Params`, out-of-range retry / circuit settings…) |

Branch on `ex.ErrorCode` (an enum you can exhaustively `switch`) or `ex.Code` (the axios-aligned string); never parse the message:

```csharp
switch (ex.ErrorCode)
{
    case AxiosErrorCode.ConnAborted: /* timeout: the same request may be worth one more try */ break;
    case AxiosErrorCode.BadRequest:  /* 4xx: the request itself is wrong, resending will not help */ break;
    case AxiosErrorCode.Canceled:    return;
    default: throw;
}
```

`ErrorCode` and `Code` always agree; a `Code` that is not a built-in constant (for example a custom code written by a derived type) maps to `AxiosErrorCode.Unknown`, and then `Code` stays authoritative.

Custom accepted status codes (2xx by default):

```csharp
await client.GetAsync("maybe-404", c => c.ValidateStatus = s => s is >= 200 and < 500);
```

## Configuration

`AxiosConfig` top level is the request itself (13 configurable leaves); everything else is grouped into six groups: `Limits` / `Serialization` / `Resilience` / `Transport` / `Observability` / `Caching`. That is 7 groups and 70 leaves in total (13 + 3 + 9 + 19 + 10 + 6 + 10); each leaf has exactly one storage field, one doc comment and one entry point, and instance defaults and per-request overrides use the same properties.

### Configuration reference

`AxiosConfig` top level (the request itself):

| Member | Default | Notes |
| --- | --- | --- |
| `BaseAddress` | `null` | The axios `baseURL` equivalent |
| `Url` | `null` | Relative or absolute |
| `Method` | `Get` | BCL `HttpMethod`, custom verbs allowed |
| `Headers` | empty | `AxiosHeaders`, case-insensitive, validated on write |
| `Params` | `null` | Object / dictionary / collection / pre-encoded string |
| `ContentType` | `application/json` | Decides how `Body` is encoded |
| `Body` | `null` | Request body |
| `Compression` | `null` (off) | Compress the body: `Gzip` / `Deflate` / `Brotli`; `None` overrides an instance default |
| `Timeout` | `Zero` (no timeout) | Per-attempt budget (connect + headers + body) |
| `ResponseType` | `Json` | `Json` / `Text` / `ArrayBuffer` / `Stream` / `Dynamic` |
| `CancellationToken` | `default` | External cancellation |
| `Response` | `null` | Readable during the response phase (status / headers / media type) |
| `ShortCircuit` | `null` | Deliver a response without sending (this request only) |
| `ValidateStatus` | `null` (2xx) | Returning `false` throws `AxiosException` |
| `Caching` | `null` | Response cache (`AxiosCache`): hits send nothing and are reused across requests (options live in `AxiosCacheOptions`) |

> `Response` is read-only outside the response phase, and `Caching` is a group entry (its `AxiosCacheOptions` hold 10 leaves); neither counts toward the 13 top-level configurable leaves.

`Limits` sub-object (body and frame caps, the single entry point for all three; created lazily on first access):

| Member | Default | Notes |
| --- | --- | --- |
| `Limits.MaxResponseBytes` | `null` (unlimited) | Response body cap, counted while reading (was `Limits.MaxContentLength`) |
| `Limits.MaxRequestBytes` | `null` (unlimited) | Request body cap, checked before sending (was `Limits.MaxBodyLength`) |
| `Limits.MaxStreamFrameBytes` | `null` (= 8 MB) | Streaming frame cap, valid range 1–64 MB (was `Limits.MaxStreamFrameLength`) |

The other four sub-objects (created lazily on first access):

| Sub-object | Key members (default / notes) |
| --- | --- |
| `Serialization` | `JsonOptions` (web style) / `BodySerializers` (instance registry, always present on a client) / `TextEncoding` (`null` = UTF-8 send, auto-detect receive) / `QueryEncoder` / `FormFieldName` / `CollectionFormat` (`Brackets`) / `ValueText` / `TransformRequest` / `TransformResponse` |
| `Resilience` | `Retry` (`Policy` = `null`, no retries / `OnRetry`) / `Circuit` (`Policy` = `null`, no breaker / `OnRejected`) / `Hedging` (`MaxHedges` = `0`, max 16 / `Delay` = 80ms) / `Failover` (`Endpoints` = `null` / `FailureThreshold` = 2 / `Cooldown` = 30s / `MaxCooldown` = 5min) / `Concurrency` (`MaxInFlight` / `MaxQueue` / `QueueTimeout`, all `null`) / `Idempotency` (`Enabled` = `false` / `HeaderName` = `Idempotency-Key` / `Key` / `KeyFactory`) / `TotalTimeout` (`null` = none, shared across retries / endpoints / hedges) / `AllowUnsafeRetry` (`false`, the only switch that lets non-idempotent methods through) |
| `Transport` | `Proxy` / `Credentials` (`null`, instance-level only) / `Pool` (`null`, frozen when the pool is built) / `HttpVersion` (`null` = 2.0) / `VersionPolicy` (`null` = `OrLower`) / `Profile` (`null` = default profile) / `Auth` (`null`; one of static Basic / token / complete header value / provider) / `WithCredentials` (`false`, instance-level only) / `MaxRedirects` (5, instance-level only) / `MaxRate` (`null` = unlimited) |
| `Observability` | `Capture` (`null` = `TelemetryCapture.Path`; `Off` / `Path` / `Query` / `NoUrl`) / `OnUploadProgress` / `OnDownloadProgress` / `OnRawResponse` / `OnSendingRequest` / `TimeoutMessage` |

> Assigning `null` per request explicitly turns off an instance default `Resilience.Retry.Policy` / `Resilience.Circuit.Policy` / `Resilience.Failover.Endpoints`, which is different from "not set".

### Clients

Three client shapes, identical capabilities underneath:

| Shape | How to create | Use for |
| --- | --- | --- |
| Static facade | `Axios.GetAsync(...)` / `Axios.Defaults` / `Axios.Interceptors` | Scripts, single-endpoint apps |
| Instance | `Axios.Create(c => ...)` or `new AxiosClient(config)` | The normal case: one long-lived instance — one set of defaults + one pool |
| Declarative | `client.Create<T>()` or `new ApiClient(baseAddress)` | Many endpoints, describe requests with models or interfaces ([details](#model-based-api)) |

`Axios.Create("https://api.example.com")` is shorthand for `Axios.Create(c => c.BaseAddress = ...)`, with an optional second `Action<AxiosConfig>` argument for defaults.

**Instance defaults vs per-request**: what you set in `Axios.Create` are instance defaults; the trailing `configure` argument configures a single request. `configure` receives a **copy** of the instance defaults, so you can really remove a default header:

```csharp
await http.GetAsync("slow", c => c.Timeout = TimeSpan.FromSeconds(30));   // this request only
await http.GetAsync("x", c => c.Headers.Delete("X-Foo"));                 // default header removed
```

- **Instance-level only**: `Transport.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `Pool` (fixed when the pool is built) and `Resilience.Concurrency.MaxInFlight` / `Resilience.Concurrency.MaxQueue` (fixed on the first gated request). Writing a **different** value per request throws `ERR_BAD_OPTION_VALUE`; repeating the effective value is allowed. This turns "silently ignored" into a loud error.
- **`Params` / `Body` merge on "was it assigned", not on "is it `null`"**: leaving them untouched inherits the instance default; writing `c.Params = null` / `c.Body = null` explicitly drops the instance default (same idea as using `Headers.Delete` instead of `= null` for headers). So `PostAsync(url)` — which passes no `data` — still sends `Defaults.Body`; write `Body = null` to send an empty body.
- **The six `Observability` leaves merge the same way**: leaving one untouched inherits the instance default; writing `null` per request clears the instance default. This covers `Observability.Capture` / `OnUploadProgress` / `OnDownloadProgress` / `OnRawResponse` / `OnSendingRequest` / `TimeoutMessage`, matching `Body` / `ContentType` / `Compression` / `CancellationToken`.
- **One instance per site — never one per request**: every instance builds its own pool, so `Axios.Create` / `new AxiosClient` each get their own handler set. Creating one per request degrades into the classic `new HttpClient()` pitfall: no keep-alive reuse, connections and handles growing without bound. Change defaults by changing the instance; share connections by sharing a pool (see [Connection pool](#connection-pool)); in DI just register a singleton with `AddAxiosClient`.
- Dispose a client when done (`using`, or let the DI container own it); pool and handler go with it. In-flight requests are not aborted, the client just stops accepting new ones.
- **The static facade can be reset**: `Axios.Reset()` disposes the global default instance, and the next access to `Axios.Defaults` / `Axios.Interceptors` or the next request rebuilds it from scratch. In-flight requests are unaffected (disposal does not abort them), and the old defaults and interceptors are dropped — useful to isolate global state between tests, or to recover after the default instance was disposed by mistake (a disposed instance throws `ERR_CANCELED` for every request).

#### Replaying `response.Config`

`AxiosResponse.Config` (and `AxiosException.Config`) is the **effective config of that one request**: instance defaults, the trailing `configure` and request interceptors are already folded in. Handing it back to `RequestAsync` / any overload taking an `AxiosConfig` treats it as per-request items and re-merges them onto the defaults of the **current instance**:

```csharp
var first = await a.GetAsync("items/1");            // a's defaults + this request's items
var again = await b.RequestAsync(first.Config!);    // b's instance-level settings + first's per-request items
```

**Instance-owned items do not travel** — they always come from the current instance, even if you overwrite them on that object:

| Item | Value on replay |
| --- | --- |
| `BaseAddress` | The current instance's default |
| `CancellationToken` | The current instance's default (to swap the token, pass `cancellationToken:` to a shortcut) |
| `Transport.Auth` / `Proxy` / `Credentials` / `WithCredentials` / `Pool` / `MaxRedirects` | The current instance's defaults (the previous call's per-request `Auth` is not carried over) |
| `Resilience.Concurrency.MaxInFlight` / `Resilience.Concurrency.MaxQueue` | The current instance's defaults |
| `Serialization.BodySerializers` | The current instance's registry (media types registered on the source instance do not travel) |
| `Serialization.ValueText` | The current instance's default (the source instance's hook is its own instance-level setting and does not travel with the config; pass a hook per request through a direct call's `configure`) |
| The six `Observability` leaves (`Capture` / `OnUploadProgress` / `OnDownloadProgress` / `OnRawResponse` / `OnSendingRequest` / `TimeoutMessage`) | Callbacks inherited from the source instance do not travel (including the `Config` produced by shortcut methods); callbacks / `null` explicitly written into the config by the caller are kept, because that is request-level intent |
| `ShortCircuit` | Not applied (a short-circuit response belongs to that call only) |

Everything else is copied as per-request items, in three groups:

- the request itself: `Url` / `Method` / `Headers` / `Params` / `Body` / `ContentType` / `Compression` / `ResponseType`;
- timing and policy: `Timeout` / `Resilience.TotalTimeout` / retry / circuit / hedging / failover / telemetry (explicitly assigned items only; callback rules in the table above);
- the rest: the remaining serialization options / the remaining size caps / `Transport.HttpVersion` / `VersionPolicy` / transform chains (appended and de-duplicated by reference against the inherited segment).

**Two boundaries**

- The "instance-sourced" rule applies only to **configs produced by the library**: `response.Config`, its `Clone()`, and the effective configs built inside shortcuts. A config you write yourself (`new AxiosConfig { BaseAddress = ... }`) is used as-is.
- To send the same request to a different host or with a different token: use another instance (`Axios.Create(c => c.BaseAddress = ...)`) or write your own `new AxiosConfig { Url = ..., Method = ... }`. To tweak a few fields on the current instance, use the `RequestAsync(c => ...)` Action overload (it edits a copy of the current instance's defaults).

### Interceptors

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

> **Writing the `Authorization` header from an interceptor only fits clients where the interceptor owns authorization end to end.** When `CredentialProvider` or `Transport.Auth` is also configured, an explicit `Authorization` header plus a credential source counts as a configuration conflict and throws `ERR_BAD_OPTION_VALUE` before sending. To switch identity per request, swap the credential slot instead (`config.Transport.Auth = AxiosCredential.Bearer(token)`); the library re-resolves the credential against the new slot. A 401 replay also re-runs request interceptors, so timestamps and trace ids change on replay — use [`Resilience.Idempotency`](#idempotency-keys) for keys that must stay stable across attempts.

**Merge rule for a brand-new config**: a request interceptor may return its own `new AxiosConfig` (or the caller's raw config). The pipeline then merges it once against the instance defaults, so `BaseAddress`, timeouts and connection-level options still apply. The merge only takes explicitly set values, so anything the interceptor just wrote wins over the instance defaults. Returning an already-merged config (`response.Config`) or its `Clone()` skips the second merge, and mutating the passed `config` then returning it does too.

### Data transforms and serialization

The two transform chains process "within one format", the serializer decides "what format the body is as a whole". Requests run transformRequest first and then encode; responses deserialize first and then run transformResponse.

#### Data transforms

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

#### Serialization

Request and response bodies go through System.Text.Json by default, and the whole thing is replaceable.

**Three levels of JSON settings** (later wins): global `AxiosJson.Options` → instance `c.Serialization.JsonOptions` → per-request same property. The default is web style (camelCase out, case-insensitive in). Replacing `AxiosJson.Options` needs a fresh instance (STJ freezes options once used) — do it once at startup.

**XML built in**:

```csharp
using AxiosNet.Serialization;

// A: process-wide fallback (used only when no media-type registration matches); the request header becomes application/xml
AxiosContent.Default = XmlContentSerializer.Instance;
await http.PostAsync("orders", order);

// B: select by Content-Type (mixing formats in one app)
AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
await http.PostAsync("orders", order, c => c.ContentType = AxiosContentType.Xml);

// C: register on this instance only (recommended: sites and tests stay isolated)
http.Defaults.Serialization.BodySerializers.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
```

**Priority** (highest first): instance / request `Serialization.BodySerializers` (exact registration → table default) → process `AxiosContent.Register` by media type → process `AxiosContent.Default` fallback → built-in JSON. `Default` is a fallback only; it never bypasses media-type registrations.

- The instance registry is a per-client `ContentSerializerRegistry` with the same lookup rules as the global table (exact match → aliases such as `text/xml` → structured suffix `+xml`). Registering there never affects other clients or the global table; in tests, global changes leak into other cases in the same process, so use the instance registry when you need isolation.
- Replace the whole table per request with `c => c.Serialization.BodySerializers = new ContentSerializerRegistry()` — that request sees only the new table, and the instance registry is untouched.
- The table object is shared: the per-request `c.Serialization.BodySerializers` **is** the instance table, so calling `.Register(...)` on it changes the instance. To scope a registration to one request, assign a new table instead. Two clients constructed from the same `AxiosConfig`, or `Defaults.Clone()`, also share the same table.
- The registry is instance-level: replaying `response.Config` uses the target instance's registry, never the source instance's registrations.

**Custom formats**: implement `IContentSerializer` (two methods for text formats; binary formats add the byte channel `SerializeToBytes` / `Deserialize(ReadOnlyMemory<byte>)`; optional `MediaType` to advertise one).

```csharp
public sealed class YamlSerializer : IContentSerializer
{
    public string? MediaType => "application/yaml";

    public string Serialize(object? value, Type type, AxiosConfig config) => YamlConvert.Serialize(value, type);

    public object? Deserialize(string text, Type type, AxiosConfig config) => YamlConvert.Deserialize(text, type);
}

AxiosContent.Default = new YamlSerializer();
```

- A serializer may write headers (`config.Headers.Set(...)`); the `config` it sees is a per-request copy, so instance defaults stay clean.
- Response business objects enter through the byte entry (valid UTF-8 text is parsed as bytes, with no UTF-16 round trip); a body that is not valid UTF-8 goes through the text entry instead, with bad bytes replaced by U+FFFD, matching the old decode-only behavior. Text formats need not implement the byte entry — the default implementation decodes by the response charset and forwards to the text entry.
- Forms / multipart, primitives, `string` / `byte[]` / `Stream` never go through the serializer.
- Implementations must be thread-safe (shared across concurrent requests); exceptions are wrapped as `ERR_BAD_REQUEST` / `ERR_BAD_RESPONSE`.
- Text encodings: `Serialization.TextEncoding` (`null` = UTF-8 send, auto-detect receive; when set, both directions use it).
- Code pages like GBK need `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` first. On the **send** side a `charset` this platform cannot resolve fails with `ERR_BAD_OPTION_VALUE` (UTF-8 bytes are never labelled as gbk); the **receive** side treats an unknown `charset` as absent and falls back to BOM / UTF-8.

### Connections and transport

Per-request transport settings live in `Transport`; pools, handlers and pool-level options are supplied when the client is built.

#### Connection pool

`AxiosClient` builds its own pool (`HttpConnectionPool`) keyed by `scheme://host:port`: one tuned `SocketsHttpHandler` per origin, requests sharing keep-alive TCP connections.

> **Do not create a client per request** (reasons in [Clients](#clients)): keep one instance per site; to share connections across clients pass the same pool explicitly (usage 2 below), or register a singleton with `AddAxiosClient` in DI.
>
> **Always `Dispose` before dropping an instance**: an undisposed pool is held by its background eviction timer and is never garbage-collected; its connections are only reclaimed once `IdleHandlerLifetime` (5 min by default) elapses.

```csharp
using System.Net;
using AxiosNet.Pooling;

// 1) Own pool (configured at construction)
var client = new AxiosClient(poolOptions: new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 32,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = true,                                             // master switch
    Decompression = DecompressionMethods.GZip | DecompressionMethods.Brotli    // only these two (default: All)
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
| `PooledConnectionLifetime` | 10 min | Max reuse age; replaced on next use (in-flight not interrupted); the value applied to the handler is jittered by ±10% so connections do not all drop and reconnect at the same instant |
| `PooledConnectionIdleTimeout` | 2 min | Idle connections close |
| `ConnectTimeout` | 15 s | Connect timeout |
| `ConnectCallback` | `null` | Custom connect: takes over "endpoint → stream" (UDS / in-process transport / custom DNS); TLS is still done by the handler on the returned stream |
| `MaxResponseHeadersLength` | 64 KB | Response header size cap; must be `> 0` (the handler has no "0 = unlimited"); enforced for h1 and h2, overflow fails with `ERR_NETWORK` |
| `IdleHandlerLifetime` | 5 min | How long an idle origin node is kept |
| `EvictionInterval` | 30 s | Background sweep interval; `<= 0` disables the timer |
| `AllowAutoRedirect` / `MaxAutomaticRedirections` | `true` / 5 | Auto-redirect; `MaxRedirects = 0` disables |
| `AutomaticDecompression` | `true` | Master switch for response decompression; off is equivalent to `DecompressionMethods.None` |
| `Decompression` | `All` | Algorithms: any combination of `GZip` / `Deflate` / `Brotli`, or `All` (everything, the default); `None` disables, unknown bits throw `ERR_BAD_OPTION_VALUE` at pool construction |
| `EnableMultipleHttp2Connections` | `true` | Under h2, allow more connections up to `MaxConnectionsPerServer` |
| `KeepAlivePingDelay` / `KeepAlivePingTimeout` | `InfiniteTimeSpan` (off) / 20 s | h2 keep-alive ping interval and ACK timeout; off by default to match `SocketsHttpHandler`, set a positive value to enable. No effect on h1 |
| `KeepAlivePingPolicy` | `WithActiveRequests` | When to ping: `WithActiveRequests` / `Always` (keeps idle connections alive) |
| `MaxEndpointCount` | 1024 | Origin node cap; evicts the least recently used idle node; if all are in use, creating a new node throws `ERR_POOL_EXHAUSTED` (request never sent; observable, not retried by default — lower fan-out / raise the cap / `Clear()` then retry) |
| `UseCookies` | `false` | The default profile gets one `CookieContainer`, readable/writable via `pool.Cookies` (`null` when off); log out with `pool.ClearCookies()`. Named transport profiles each get their own, see [Transport profiles](#transport-profiles) |
| `CookieCapacity` / `CookiePerDomainCapacity` / `CookieMaxCookieSize` | `300` / `20` / `4096` | Cookie container limits (total / per domain / per cookie size); overflow evicts the oldest and oversized cookies are dropped — only active when `UseCookies` is on |
| `Proxy` / `Credentials` | `null` | With no proxy set, system / env proxies are **not** used; set `HttpClient.DefaultProxy` if you want them |
| `ServerCertificateCustomValidationCallback` / `EnabledSslProtocols` / `ConfigureSslOptions` | `null` | Certificate validation, TLS versions, client certificates |
| `HandlerFactory` | `null` | Called when creating a per-origin handler, see [Custom handlers](#custom-handlers) |

- `Dispose()` is a graceful shutdown: idle nodes go immediately, nodes with in-flight requests are reclaimed once leases return. `Clear()` drops every node now (interrupting in-flight requests).
- How fast DNS changes converge is governed by `PooledConnectionLifetime`; for custom connects (UDS / in-process transport / custom DNS) set `ConnectionPoolOptions.ConnectCallback` directly.
- `ConnectCallback` and `MaxResponseHeadersLength` apply to the library's default handler only: building the inner handler with `ctx.CreateDefaultHandler()` inside a `HandlerFactory` carries them over; a fully hand-built handler must map them itself.
- **h2 keep-alive is off by default** (same as `SocketsHttpHandler`, so wire behaviour never changes behind your back): set `KeepAlivePingDelay` to a positive value (30s is common) to turn it on; pings are only sent while requests are in flight.
- It targets *silent* connection reaping: AWS ALB drops idle connections after 60s, NAT tables after 30–120s, and the client still believes the connection is pooled — the next request hits a closed one, costing either a fresh TLS handshake or a `GOAWAY` / stream reset on h2 (seen as sporadic `ERR_NETWORK`). The cost is a 17-byte frame, far cheaper than a reconnect.
- Low-frequency, high-value calls (webhooks, scheduled jobs) can use `Always` to keep the connection alive even when idle. Conversely, behind a proxy or middlebox that does not answer `PING`, enabling it gets connections torn down and re-established (more `ERR_NETWORK`) — which is why it is off by default.
- **`UseCookies` is scoped by domain / path only, not by identity**: the default profile has one container and each named transport profile has its own (see [Transport profiles](#transport-profiles)). Multi-tenant processes, crawlers or anything switching identities should give each identity a profile, otherwise they leak cookies across each other.
- The container is directly accessible via `pool.Cookies` / `pool.GetCookies("profile")` (thread-safe) for seeding cookies at startup or diagnosing why a cookie was not sent.
- Log out with `pool.ClearCookies()`: `CookieContainer` has no delete API, so this swaps in empty containers for **all profiles** and retires existing origin endpoints, which rebuilds connections on the next request; in-flight requests are not interrupted and may still carry the old cookie.
- With the container enabled, a request-level `Cookie` header is **merged** with container contents and cannot override a same-named cookie; neither `Clear()` nor `Dispose()` empties the container. On a named shared pool, `ClearCookies()` affects every client sharing that pool.
- Container limits default to 300 total / 20 per domain / 4096 bytes per cookie, overflowing entries are silently evicted oldest-first — tune with `CookieCapacity` / `CookiePerDomainCapacity` / `CookieMaxCookieSize`.
- **Trailers are not read today**: chunked trailing headers (checksums, quotas) stay on `HttpResponseMessage.TrailingHeaders` and are not exposed on `AxiosResponse`. Wrap the handler with `HandlerFactory` to read them after the body, or inject a custom handler.
- **Proxy / credentials are a single pool-level set by default**: `Proxy` lives on the handler instance, so changing it per request throws `ERR_BAD_OPTION_VALUE`. For several egress IPs or per-tenant sessions, attach several egresses to one pool with [transport profiles](#transport-profiles) instead of building a pool per egress.
- **Named shared pools** (usage 4) cover "several clients want to reuse connections without passing a pool object around". The key is supplied by the caller — there is **no options fingerprint**: reference members such as `HandlerFactory` / `ConnectCallback` / `Proxy` / `Credentials` / the certificate callbacks have no reliable structural equality, so two "identical looking" configurations throw instead of silently sharing one pool.
- Three rules for named pools:
  - the same name returns the same pool;
  - `options` only apply on first creation — passing a **different** configuration later throws `ERR_BAD_OPTION_VALUE` rather than being silently ignored;
  - pools in the registry are owned by nobody — disposing a client does not close them, `Release(key)` does.
- To see whether pools are being built too often, watch `axiosnet.client.pool.active_pools` (live pools) and `.shared_pools` (named registry entries).

#### Transport profiles

Attach several egresses (proxy / credentials / client certificate / cookie session) to one pool and let each request pick one:

```csharp
using AxiosNet.Pooling;

// Default profile: the Proxy / Credentials / TLS fields on ConnectionPoolOptions
var pool = new HttpConnectionPool(new ConnectionPoolOptions
{
    MaxConnectionsPerServer = 64
})
.AddTransport("egress-a", new ConnectionTransportOptions
{
    Proxy = proxyA,
    UseCookies = true                                  // this profile's own cookie session
})
.AddTransport("egress-b", new ConnectionTransportOptions
{
    Proxy = proxyB,
    ConfigureSslOptions = ssl => ssl.ClientCertificates = tenantBCerts   // this profile's own mTLS certificate
});

using var client = new AxiosClient(pool: pool);

await client.GetAsync("orders", c => c.Transport.Profile = "egress-a");
await client.GetAsync("orders", c => c.Transport.Profile = "egress-b");   // same origin, other egress

// Or pin it on the instance defaults; a single request can go back with null
using var tenantClient = new AxiosClient(
    new AxiosConfig { BaseAddress = "https://api.example.com/", Transport = { Profile = "egress-a" } },
    pool: pool);
```

- **A profile does not inherit the default profile's transport fields**: `Proxy` / `Credentials` / certificate callbacks / `ConnectCallback` / `HandlerFactory` / `UseCookies` are one set. To keep using the default proxy on a named profile, assign the same `IWebProxy` instance to it (shared by reference, zero cost).
- **Selecting a named profile disables the default profile's proxy, and the default profile may not set one**: the actual handler only takes that profile's `Proxy` / `Credentials`. If the default profile still sets either, the request throws `ERR_BAD_OPTION_VALUE` before sending (same rule as the other two-layer egress entries) instead of ignoring it silently.
- **Pool-level policy is not split per profile**: connection limit, connection lifetime, idle eviction, decompression, redirects, HTTP/2 and pings still come from `ConnectionPoolOptions` — they describe *how to use a connection*, not *where it comes from*. The limit itself applies **per handler**, and each profile gets its own handler, so N profiles can reach `N × MaxConnectionsPerServer` connections in total (split the budget yourself if you need one shared ceiling).
- **One node per profile per origin**: its own handler, its own TCP connections, its own cookie container (`pool.GetCookies("name")`). So `MaxEndpointCount` counts **origin × profile**, and `GetStatistics().Endpoints[i].Transport` tells you which node belongs to which profile.
- **An unregistered profile fails loudly**: `c.Transport.Profile = "missing"` throws `ERR_BAD_OPTION_VALUE` before anything is sent (the message lists registered profiles). Silently falling back to the default profile would turn "I thought this went through the dedicated line" into a security incident.
- **Profiles are inert with an injected handler**: that path has no pool, so a non-empty profile name also throws `ERR_BAD_OPTION_VALUE`; use a pool with `AddTransport`, or route inside your own handler.
- **A profile's own `HandlerFactory` wins over the pool-level one**: the factory sees `ctx.Profile` / `ctx.Transport` and can return a different transport per profile; `ctx.CreateDefaultHandler()` automatically carries that profile's egress settings.
- **Named shared pools compare the profile table**: a second `GetOrCreate` on the same key with a different name set or different reference members throws `ERR_BAD_OPTION_VALUE` (same rule as the other options).
- Metrics are not tagged by profile (`pool.*` stays low-cardinality): use the span's `server.address` / the endpoint index in attempt events, or `GetStatistics()`.

#### HTTP versions

Default is `HttpVersion = Version20` + `VersionPolicy = RequestVersionOrLower`: over https it uses h2 when negotiated and downgrades to 1.1 in the same TLS handshake; plaintext `http://` is always 1.1.

```csharp
var client = new AxiosClient();                                       // defaults are fine

await client.GetAsync(url, c => c.Transport.HttpVersion = HttpVersion.Version11);   // back to 1.1

await client.GetAsync(url, c =>                                        // require h2
{
    c.Transport.HttpVersion = HttpVersion.Version20;
    c.Transport.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
});

await client.GetAsync(url, c => c.Transport.HttpVersion = HttpVersion.Version30);   // h3, downgrade if unsupported
```

| Configuration | What actually goes out |
| --- | --- |
| Default (`Version20` + `OrLower`) | h2 when negotiated over https, otherwise 1.1; plaintext always 1.1 |
| `Version20` + `Exact` | h2 only, error if not negotiable (on plaintext this means prior knowledge h2c) |
| `Version11` (policy default) | HTTP/1.1 |
| `Version30` (policy default) | h3 when QUIC and the server support it, otherwise downgrade |
| `Version30` + `Exact` | h3 only; impossible setups (plaintext / no QUIC) throw `ERR_BAD_OPTION_VALUE` before sending, unsupported servers throw `ERR_NETWORK` |

- The version actually used is `response.ProtocolVersion`; the intent is `Config.Transport.HttpVersion`.
- Check h3 availability with `Http3Support.IsSupported` / `UnsupportedReason` (.NET 6 disables h3 by default: set `System.Net.SocketsHttpHandler.Http3Support=true` in runtimeconfig or `DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=1`, and MsQuic must be present).
- `HttpVersion` / `VersionPolicy` are per-request: settable on instance defaults and per call; retries keep them.
- `WithCredentials = true` uses `CredentialCache.DefaultCredentials`; connection-level auth such as NTLM / Negotiate does not work over h2 — set `Version11` explicitly for IIS / domain services.

#### Custom handlers

Two entry points, differing in whether they go through the pool:

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
- With an injected handler, `client.Pool` is `null` and `UsesConnectionPool` is `false`; connection-level options (`Transport.MaxRedirects` / `WithCredentials` / `Proxy` / `Credentials`) become the handler's business, and setting them on defaults throws `ERR_BAD_OPTION_VALUE`.
- Timeouts, progress, rate limiting and size caps are unaffected.

### Observability

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
| `url.full` | Full URL (userinfo stripped); path only by default (`Observability.Capture = TelemetryCapture.Path`), `TelemetryCapture.Query` includes the query, `TelemetryCapture.NoUrl` omits the URL, `TelemetryCapture.Off` produces nothing |
| `http.response.status_code` / `network.protocol.version` | Status code / protocol actually used |
| `http.request.body.size` / `http.response.body.size` | Byte counts (omitted when unknown) |
| `http.request.resend_count` | Number of retries so far |
| `error.type` / `axiosnet.error.code` | Status code → library error code → exception type name; plus the library code |
| `axiosnet.pool.endpoint.reused` | Whether an existing origin node was reused (`false` = created on the fly, usually slower) |
| `axiosnet.cache.revalidated` | This response was merged from a conditional request's 304 (the body comes from the cache) |

- 4xx / 5xx are marked `Error` even when accepted by `validateStatus`; failures also record an `exception` event.
- Every retry decision adds a `retry` event, with `axiosnet.retry.delay_ms` / `axiosnet.retry.reason` plus `server.address` / `server.port` — the same logical address as the span and metrics. The event carries it so event-only consumers do not have to join back to span tags.
- The host a failover endpoint or hedge branch actually connected to is not in these tags; look at that branch's attempt event — `Attempt.Audit` is on the default chain, just subscribe to `AxiosNet.Attempt*`.
- A failure that did receive a response (4xx / 5xx) still records `http.response.status_code` / `network.protocol.version` / `http.response.body.size`.
- For `ResponseType.Stream` the span ends when headers arrive, so its duration is time-to-first-byte.

**Metrics** (source `AxiosNet`):

| Metric | Type | Unit | Tags |
| --- | --- | --- | --- |
| `axiosnet.client.request.duration` | `Histogram<double>` | `s` | method / server.address / server.port / status_code / error.type |
| `axiosnet.client.active_requests` | `ObservableGauge<long>` | `{request}` | none |
| `axiosnet.client.request.retries` | `Counter<long>` | `{retry}` | method / server.address / server.port / error.type / `axiosnet.retry.reason` |
| `axiosnet.client.retry.budget_exhausted` | `Counter<long>` | `{retry}` | none |
| `axiosnet.client.gate.queue_depth` | `ObservableGauge<long>` | `{request}` | none |
| `axiosnet.client.gate.rejections` | `Counter<long>` | `{request}` | `axiosnet.gate.reason` (`queue_timeout` / `queue_full`) |
| `axiosnet.client.circuit.state` | `ObservableGauge<long>` | `{circuit}` | `axiosnet.circuit.state` (`closed` / `open` / `half_open`) |
| `axiosnet.client.circuit.untracked_host` | `Counter<long>` | `{host}` | none |
| `axiosnet.client.pool.leases` | `Counter<long>` | `{lease}` | `axiosnet.pool.endpoint.reused` |
| `axiosnet.client.pool.endpoints.created` | `Counter<long>` | `{endpoint}` | none |
| `axiosnet.client.pool.endpoints.evicted` | `Counter<long>` | `{endpoint}` | `axiosnet.pool.eviction.reason` |
| `axiosnet.client.pool.active_leases` | `ObservableGauge<long>` | `{lease}` | none |
| `axiosnet.client.pool.active_pools` | `ObservableGauge<long>` | `{pool}` | none |
| `axiosnet.client.pool.shared_pools` | `ObservableGauge<long>` | `{pool}` | none |
| `axiosnet.client.cache.hits` | `Counter<long>` | `{request}` | none |
| `axiosnet.client.cache.misses` | `Counter<long>` | `{request}` | none |
| `axiosnet.client.cache.stores` | `Counter<long>` | `{entry}` | none |
| `axiosnet.client.cache.store_failures` | `Counter<long>` | `{entry}` | none |
| `axiosnet.client.cache.revalidations` | `Counter<long>` | `{request}` | none |

- Cache hit rate = `cache.hits / (cache.hits + cache.misses)` (a follower that receives the shared reply counts as a hit; requests that skip the fresh lookup because of `no-cache` / `no-store` count as neither — a `no-cache` request that turns into a conditional request only counts as `cache.revalidations`; a conditional request answered with 304 counts as a miss); watch `cache.store_failures` on its own, don't fold it into the hit rate.
- Metrics only carry low-cardinality tags; URL paths live in spans only. **Pool metrics carry no host / port**: for gateways, crawlers and multi-tenant SaaS one series per host blows up the backend; use the span (`server.address` / `server.port` / `axiosnet.pool.endpoint.reused`) to find the origin.
- `axiosnet.retry.reason` has a bounded value set: `status` whenever there is a status code (the exact code is in `error.type`), otherwise the library error code (`ECONNABORTED` / `ERR_NETWORK` …) — so "tune the policy (429)" and "fix the downstream (5xx)" are separable on a dashboard.
- `gate.queue_depth` is the number of requests currently waiting for a slot (process-wide); `gate.rejections` counts entrance rejections (queue timeout / queue full); `circuit.state` is "how many circuit dimensions are in each state" (breaker instance × key); `circuit.untracked_host` counts dimensions that bypassed the full state table in closed state; `retry.budget_exhausted` counts retries denied by the shared budget.
- The five cache counters (`cache.hits` / `misses` / `stores` / `store_failures` / `revalidations`) carry no tags either — none of these carries a host.
- `request.duration` is the logical request duration (including retries, backoff and the cache store write); `active_requests` drops when the request ends (headers for streaming), while `active_leases` waits for the body stream — use the latter to see how much concurrency a long download holds.
- Pool metrics describe the pool as a resource and are unaffected by `Observability.Capture = TelemetryCapture.Off`.
- `pool.active_pools` counts live pools (+1 on creation, -1 on `Dispose`) and `pool.shared_pools` counts entries in the named registry: a rising `active_pools` means some code keeps building pools — typically a client per request.

**Events** (`DiagnosticListener` named `AxiosNet.DiagnosticListener`): `AxiosNet.RequestStart` / `RequestStop` / `RequestFailed` / `Retry`, with public payload types (`AxiosRequestStartEvent` / `StopEvent` / `FailedEvent` / `AxiosRetryContext`).

**Attempt auditing** (`AxiosNet.AttemptStop` / `AxiosNet.AttemptFailed`, payload `AxiosAttemptEvent`) is **on by default** (`Attempt.Audit` is the sixth link of the default chain, after the gate and before the terminal) — just subscribe:

```csharp
// The default chain already contains Attempt.Audit; a custom chain must list it explicitly
var pipeline = AxiosAttemptPipeline.Create(
    Attempt.Retry, Attempt.Failover, Attempt.Circuit, Attempt.Hedging, Attempt.Gate, Attempt.Audit);
using var client = Axios.Create(pipeline: pipeline);
```

- Covers attempts that **actually reach the send**: breaker rejections, gate queue timeouts and short circuits produce no attempt event (the audit sits inside them).
- Event count is **endpoints × branches**: one per failover (`AttemptNumber` unchanged, `EndpointIndex` distinguishes them) and one per hedge branch; losers cancelled by the winner emit no failure event (that is cleanup, not a downstream fault), while losers that genuinely failed before a winner appeared do emit one.
- Zero cost when nobody subscribes: no state machine, no timestamps.

**Per-request opt-out**:

```csharp
await client.GetAsync<object>("health", c => c.Observability.Capture = TelemetryCapture.Off);      // produce nothing
await client.GetAsync<object>("api",    c => c.Observability.Capture = TelemetryCapture.NoUrl);     // omit the URL
await client.GetAsync<object>("api?token=...", c => c.Observability.Capture = TelemetryCapture.Query);   // include the query in url.full
```

## Resilience

`Resilience` holds retries, the circuit breaker, concurrent hedging, backup endpoints, the concurrency gate, idempotency keys and the total budget; everything is off until a policy is attached.

### Retries

No retries by default; attach a policy to enable:

```csharp
using AxiosNet.Retry;

var client = Axios.Create(c => c.Resilience.Retry.Policy = new RetryPolicy { MaxRetries = 3 });

config.Resilience.Retry.Policy = RetryPolicy.Constant(3, TimeSpan.FromSeconds(1));                                      // fixed interval
config.Resilience.Retry.Policy = RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));    // exponential

await client.GetAsync("orders", c => c.Resilience.Retry.Policy = null);   // disable the instance policy for one call

client.Defaults.Resilience.Retry.OnRetry = ctx => logger.LogWarning(
    "attempt {Attempt}/{Max} failed ({Code}), retrying in {Delay}ms",
    ctx.Attempt, ctx.MaxRetries, ctx.ErrorCode, ctx.Delay.TotalMilliseconds);
```

`RetryPolicy` defaults (all configurable):

| Situation | Default |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED` (timeout **after** the send) | Retry |
| `ECONNABORTED` (`Resilience.TotalTimeout` exhausted before the send, `IsTotalTimeoutExceeded`) | No retry (the budget covers the whole request; a resend fails at the same place immediately) |
| `ECONNABORTED` (single-attempt timeout clamped by `Resilience.TotalTimeout`, budget spent when it fired) | No retry (still reported as `IsTotalTimeoutExceeded`) |
| 408 / 429 / 5xx | Retry (`RetryStatusCodes` overrides) |
| Other 4xx, config / URL errors | No retry |
| `ERR_CANCELED` | No retry |
| POST / PATCH and other non-idempotent verbs | No retry (`Resilience.AllowUnsafeRetry = true` opts in) |

Library-level no-retry (a custom policy cannot override it): `ERR_CANCELED` / `ERR_CIRCUIT_OPEN` / `ERR_QUEUE_TIMEOUT` / `ERR_QUEUE_FULL` / `ERR_BAD_OPTION_VALUE` / an exhausted `Resilience.TotalTimeout` (`IsTotalTimeoutExceeded`) — in these cases the request either never left, an immediate resend would hit the same gate, or the pipeline hit a configuration / programming error.

Other members: `Delay` / `BackoffFactor` / `MaxDelay` / `Jitter` (`true` by default, waits land in 50%–100%) / `HonorRetryAfter` (`true` by default, follows server `Retry-After` but still capped by `MaxDelay`) / `RetryWhen`.

- `Timeout` is **per attempt** (3 retries × 10s = 40s worst case); use `Resilience.TotalTimeout` for a whole-request upper bound (or a `CancellationToken` from the caller).
- Interceptors, `transformRequest` and body serialization run once; response interceptors see the final result.
- The body must be replayable: seekable streams are rewound before each attempt; non-seekable streams, `StreamContent` (including multipart parts) and streaming bodies (`IAsyncEnumerable`) throw `ERR_BAD_OPTION_VALUE` as soon as retries or backup endpoints are enabled.
- Caller-supplied `HttpContent` goes through a **whitelist**: `ByteArrayContent` (including `StringContent` / `FormUrlEncodedContent`), `ReadOnlyMemoryContent`, `JsonContent` (re-serialized from its value on every send), and a `MultipartContent` whose parts are all replayable; every other custom content is rejected **before sending**.
- "Length known" does not mean "replayable": a one-shot stream wrapped in a custom content still sends only once.
- Custom policies: implement `IAxiosRetryPolicy` (`MaxRetries` / `ShouldRetry(ctx)` / `GetDelay(ctx)`); `ctx` exposes `Attempt` / `MaxRetries` / `Exception` / `Response` / `StatusCode` / `ErrorCode` / `Delay` / `Config` / `Method`.
- Policies are shared by concurrent requests on the same instance, so they must be thread-safe (stateless is easiest; implementations with shared state, like the built-in `RetryBudgetPolicy`, synchronize themselves).

#### Global retry budget (`RetryBudgetPolicy`)

Per-request retries multiply with concurrency when a downstream is down: 100 in-flight requests × 3 retries = 300 extra sends, enough to turn a blip into an outage. `RetryBudgetPolicy` wraps any policy in an **instance-wide shared** token bucket that throttles retries only, never first attempts:

```csharp
// capacity 20, refills 5/s: 20 retries can burst through, steady state is 5/s
client.Defaults.Resilience.Retry.Policy = new RetryBudgetPolicy(capacity: 20, refillPerSecond: 5);

// the inner policy decides "retry at all / how long to wait"; the bucket only decides "is there budget"
client.Defaults.Resilience.Retry.Policy = new RetryBudgetPolicy(
    RetryPolicy.Exponential(3, TimeSpan.FromMilliseconds(200)), capacity: 50, refillPerSecond: 10);

var budget = (RetryBudgetPolicy)client.Defaults.Resilience.Retry.Policy;
budget.OnBudgetExhausted = ctx => logger.LogWarning("retry budget exhausted, surfacing {Code}", ctx.ErrorCode);
budget.RefusedRetries;   // total retries denied by the budget, readable at any time
```

- **Retries only, never first attempts**: an empty bucket just means "stop retrying"; the caller sees the inner policy's **real failure**, not a budget-specific error.
- **Only retries that can go out consume tokens**:
  - anything the pipeline or the inner policy would not retry (4xx, cancellation, gate rejections, the attempt cap…) never touches the bucket;
  - a retry that cannot be sent because the total budget is already gone is refunded immediately (built-in `RetryBudgetPolicy` only — a custom decorator wrapped around it is not recognized);
  - a retry whose backoff cannot fit in the remaining budget is refunded and reported as exhausted right away, instead of waiting out a sleep that can only end in another failure — a shared bucket is never charged for a send that never happens;
  - a retry that is stopped later (rejected by the gate once it entered the pipeline) still counts: at decision time that was unknowable, and over-counting only tightens the budget — it never amplifies traffic. One cancelled during the backoff wait is refunded instead — that send never happened either.
- **Tokens meter retry rounds, not sends**: with backup endpoints / hedging enabled, one retry round can fan out to at most `(1 + endpoints) × (1 + MaxHedges)` sends (a failure ends the round early and skipped endpoints never send, so the real number is usually lower); divide a send-rate estimate by that factor when sizing `capacity`. First attempts never take a token.
- **The bucket is shared through the policy object**: `Resilience.Retry.Policy` is the same reference across config clones, so every request on a client instance shares one bucket; give a single call its own bucket by assigning `new RetryBudgetPolicy(...)` on that call.
- The bucket starts full; `refillPerSecond = 0` means "initial capacity only, no recovery over time" (handy for batches / tests).
- Thread-safe, allocation-free on the check path; `OnBudgetExhausted` fires outside the lock, so logging / IO in the callback is fine.
- Refusals are counted by `axiosnet.client.retry.budget_exhausted` (no tags, gated by `Observability.Capture`); use the `OnBudgetExhausted` callback when you need per-request context.

### Circuit breaker

Retries answer "should I try again?"; a breaker answers "is the downstream down at all?". Off by default:

```csharp
using AxiosNet.Circuit;

var breaker = new CircuitBreaker
{
    FailureThreshold = 5,                       // open after 5 consecutive downstream failures
    WindowSize = 20,                            // ...or look at the failure rate over the last 20 results
    MinimumSamples = 10,                        // don't judge a rate on fewer than 10 samples
    FailureRateThreshold = 0.5,                 // half the window failing is enough to open
    BreakDuration = TimeSpan.FromSeconds(30),   // cool down 30s, then let probes through
    HalfOpenAttempts = 1,                       // slow-start baseline: one probe in flight
    ProbeTimeout = TimeSpan.FromSeconds(30),    // a probe silent for 30s voids the round and reopens
    RecoverySuccesses = 3                       // three consecutive probe successes to fully recover
};

breaker.StateChanged += change => logger.LogWarning(
    "{Host} circuit: {From} -> {To}", change.Host, change.Previous, change.Current);

var client = Axios.Create(c => c.Resilience.Circuit.Policy = breaker);

await client.GetAsync("health", c => c.Resilience.Circuit.Policy = null);   // probes must not be blocked
breaker.Reset();                                                 // manual recovery
```

State machine: `Closed` → (consecutive failures reach `FailureThreshold`, **or** the window failure rate reaches `FailureRateThreshold`) → `Open` → (`BreakDuration` elapses) → `HalfOpen` → (`RecoverySuccesses` consecutive probe successes) → `Closed`; any failed probe returns to `Open` **immediately** and restarts the timer.

**Why both opening rules exist**: consecutive failures alone can never trip on low-volume traffic — one success in between resets the counter, and a downstream failing half the time is exactly what deserves tripping. A rate alone needs enough samples, which low traffic never produces. So each covers a range: rate once the window is full, consecutive failures until then. The window is **the last N results**, not a time window — no timestamps, no periodic sweep, old entries age out by being overwritten.

Failure accounting matches the retry gate (**cancels and 4xx never count**):

| Situation | Counts as failure? |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED` (timeout after the attempt was sent) | Yes |
| 5xx | Yes |
| 4xx (including 408 / 429) | No (and doesn't reset) |
| `ERR_CANCELED` | No |
| Config / URL / serialization / deserialization errors | No |
| `ECONNABORTED` (`Resilience.TotalTimeout` expired **before** the request was sent) | No (a local constraint expired first; the downstream never saw a byte) |

- **"Never sent" is not a downstream failure**: an `ECONNABORTED` thrown because `Resilience.TotalTimeout` ran out while queueing or before the first byte left the client (`AxiosCircuitContext.TotalTimeoutExpiredBeforeSend`) is not counted. Under overload, queue growth alone is enough for every request to hit it — counting it would mean "the more congested things get, the more likely a healthy downstream gets tripped", turning local queueing into a link-wide outage. Timeouts **after** the request was sent still count; that is a downstream that does not answer.
- **Counted per host**: the default key is `host:port` (`uri.Authority`); `KeySelector` can switch to `ctx => ctx.Uri.Host` or isolate tenants by route prefix.
- **Opening is an OR**: consecutive failures reach `FailureThreshold`, or the window (`WindowSize`) holds at least `ceil(samples × FailureRateThreshold)` failures with at least `MinimumSamples` samples. `WindowSize = 0` disables the rate rule; a `MinimumSamples` larger than `WindowSize` fails with `ERR_BAD_OPTION_VALUE` before anything is sent (that configuration can never trip).
- **Slow-start recovery**: while half-open, the in-flight probe allowance is `HalfOpenAttempts + consecutive successes`, and only `RecoverySuccesses` successes close the circuit. The default ramps 1 → 2 → 3 so a just-recovered downstream is not hit with full load; `RecoverySuccesses = 0` (or 1) degrades to "a single successful probe closes it".
- A failed probe resets the progress and reopens immediately; ignored results (4xx / cancellation / a budget that ran out before the send) only return the probe slot without touching the progress, and only a probe that actually held a slot advances it.
- A late result — admitted before the circuit opened but reported after the cooldown ended — is discarded by generation: it belongs to the previous round and can neither reopen the fresh half-open round nor return another probe's slot.
- **A hung probe never holds the slot forever**: a half-open probe that reports nothing within `ProbeTimeout` (30s by default, `null` disables) is treated as hung — the whole half-open round is voided, the circuit reopens and restarts its cooldown, and late results are discarded by generation. Without a request `Timeout`, a single stuck probe would otherwise leave that dimension half-open forever; raise this value when the probe's own timeout budget is longer (non-positive values fail with `ERR_BAD_OPTION_VALUE`).
- Rejected requests are **never sent**: no connection, no pool lease, just `ERR_CIRCUIT_OPEN` with `AxiosException.RetryAfter` describing the suggested wait.
- With retries enabled: the gate sits **before each attempt**, so once open the next attempt in the retry loop is cut immediately; `ERR_CIRCUIT_OPEN` itself is never retried.
- Each attempt is recorded: 3 retries + the first failure = 4 entries — hitting four 5xx in a row really is a down downstream.
- The state table has a hard cap (`MaxTrackedHosts`, 512 by default): when full it first evicts entries whose loss changes no decision (closed entries, preferring one whose window holds no failure samples — the consecutive-failure count may have been reset by a success, but the failure-rate ledger is still valid); if it still cannot make room, **new dimensions are not tracked at all** and pass through in closed state (no circuit decision, no accounting), while existing dimensions keep working — losing protection beats an unbounded state table. Bypassed dimensions are counted by the `circuit.untracked_host` metric.
- Custom policies: implement `IAxiosCircuitPolicy` (`ShouldAllow(ctx)` / `GetRetryAfter(ctx)` / `Record(ctx)`); `ctx` exposes `Host` / `Uri` / `Config` / `Exception` / `StatusCode` / `ErrorCode` / `Method` / `IsSuccess` / `IsFailure` / `TotalTimeoutExpiredBeforeSend`.
- Policies **are stateful** and shared concurrently, so you own thread safety. Wrapping the built-in `CircuitBreaker` in a decorator loses the admission ticket (the interface carries none), so late results are recorded the old way — the same conservative bias as `RetryBudgetPolicy` losing refunds under a decorator.

### Concurrent hedging

Retries and breakers answer "what if it fails"; hedging answers "what if it is merely slow". Off by default; `MaxHedges` is the number of **extra** branches:

```csharp
var client = Axios.Create(c =>
{
    c.Resilience.Hedging.MaxHedges = 1;                          // first send + 1 hedge
    c.Resilience.Hedging.Delay = TimeSpan.FromMilliseconds(80);  // branch n starts at n × Delay (80ms default)
});

// per request
await client.GetAsync("search", c => c.Resilience.Hedging.MaxHedges = 2);
await client.GetAsync("orders", c => c.Resilience.Hedging.MaxHedges = 0);
```

The first send goes out immediately; every `Delay` another branch follows. **The first success wins** (elapsed time is never compared — whoever returns first), the rest are cancelled and cleaned up. If every branch fails, the failure goes to the outer retry.

- **The breaker sits outside the whole group and records once**: one logical attempt = one entry, matching the retry accounting. So "one fails, one succeeds" counts as a success and "all fail" counts as a single failure — extra branches never inflate the failure count.
- **Concurrency budget is `1 + MaxHedges` times**: each branch passes the gate and holds its own slot. Size `Resilience.Concurrency.MaxInFlight` accordingly — too small with `QueueTimeout = Zero` makes branches fail with `ERR_QUEUE_TIMEOUT` at the gate, silently defeating the hedge (it is a latency optimisation, never a failure).
- **Waiting for losers is capped**: after a winner appears the call waits at most 2 seconds for the losers to finish (returning earlier would keep leases and slots occupied); past that the cleanup continues in the background and releases resources when the branch really ends. Built-in middleware honours `ctx.AttemptToken` and finishes immediately; custom middleware that ignores it can hold the caller for 2 seconds at most. When the caller cancels, the cleanup is not awaited at all (it continues in the background), so cancellation takes effect immediately.
- **`transformResponse` and response-side callbacks run once per branch** (losers included): make side-effecting transforms idempotent, or turn hedging off.
- **These requests are sent once**, with no downgrade and no error: streaming responses (a loser's body would have no reader), non-idempotent methods (unless `Resilience.AllowUnsafeRetry`), bodies that cannot be replayed concurrently (`Stream` / multipart / upload progress wrappers), and requests with shared response-side state (`OnDownloadProgress` or `MaxRate > 0` — progress would be reported twice and the rate cap doubled).
- Typed calls hedge too: each branch fills its own response object and the winner's is handed to the caller — including the status code and headers read from `config.Response`.
- `MaxHedges` is capped at 16: more branches stop buying latency and only saturate the downstream.

### Backup endpoints

When the current endpoint is unusable, resend the same request against the next one; order is priority (cooling endpoints move to the end):

```csharp
var client = Axios.Create(c => c.Resilience.Failover.Endpoints = new[]
{
    new Uri("https://backup1.example.com"),        // tried first
    new Uri("https://backup2.example.com/api")     // then this one (path prefix allowed)
});

// disable for one request (explicit null / empty = no failover)
await client.GetAsync("health", c => c.Resilience.Failover.Endpoints = null);
```

An endpoint supplies the **origin and path prefix**; the request's own path and query are appended as-is. Requesting `https://primary/v1/users?page=2` with endpoint `https://mirror/api` gives `https://mirror/api/v1/users?page=2` (the prefix is **prepended** to the request path, not substituted — do not repeat a prefix the request already carries).

| Situation | Fail over? |
| --- | --- |
| `ERR_NETWORK` / `ECONNABORTED` (timeout **after** the attempt was sent) / `ERR_POOL_EXHAUSTED` | Yes |
| `ECONNABORTED` (`Resilience.TotalTimeout` exhausted **before** the request was sent) | No — the budget is wall-clock time for the whole logical request, and another endpoint cannot make it longer (see below) |
| 5xx (`ERR_BAD_RESPONSE` with status ≥ 500) | Yes |
| `ERR_CIRCUIT_OPEN` | Yes — the breaker is per `host:port`, so the primary being open cannot block a backup |
| 4xx, other `ERR_BAD_RESPONSE` (e.g. malformed body on a 2xx) | No (a mirror changes nothing) |
| `ERR_QUEUE_TIMEOUT` / `ERR_QUEUE_FULL` | No (local slot starvation / no queue slot, nothing to do with the downstream) |
| Config / programming errors, cancellation | No |

- **An exhausted budget does not fail over**: it is the same boundary as [the circuit breaker's failure rules](#circuit-breaker) — when `Resilience.TotalTimeout` runs out before the send, another endpoint cannot extend it and would only walk the whole pipeline once more (burning a breaker decision and a gate entry, then failing again at the exact same place). Timeouts **after** the request was sent still fail over: that is a downstream that might be replaced by a working one.
- **Endpoint health is remembered across requests**:
  - an endpoint that fails `FailureThreshold` times in a row (default 2) cools down (`Cooldown`, default 30s, doubling per extra failure up to `MaxCooldown` 5 min);
  - while cooling it is no longer the first hop, so a dead primary is not paid for on every request;
  - only failures that another downstream could fix count (same list as above); 4xx / cancellation / config errors neither add nor subtract;
  - when the cooldown expires it rejoins automatically, and **when every endpoint is cooling they are still tried in the original order** — the table only decides the order, it never blocks a request;
  - set `FailureThreshold = 0` or `Cooldown = TimeSpan.Zero` to turn it off.
- **Every retry walks the endpoint order again**: the endpoint list lives inside retries, so one round must exhaust all endpoints before the first failure reaches the retry layer; health only changes the order within that round, never the "all must be tried" rule.
- **Failing over recomputes `Host`**: a `Host` header the caller set explicitly is dropped on retarget and recomputed from the destination — keeping it would make the mirror route by the primary's virtual host (`HttpClient` honours `Headers.Host`, which wins over the URI authority). For a per-endpoint `Host`, write an attempt middleware that sets it from `ctx.Uri` (`config.Headers` is only applied to the template).
- **Endpoints must be unique**: two endpoints that resolve to the same base URL (a trailing slash makes no difference) throw `ERR_BAD_OPTION_VALUE` before anything is sent — it would only hit one address twice with the same request.
- **An endpoint that resolves to the current URL is skipped**: when it shares the request's origin and carries **no path prefix** (typically a bare `https://backup.example.com`), failing over would just resend in place, so it is skipped and the next endpoint is tried.
- **`Timeout` is a per-endpoint, per-send budget**: same rule as retries, so 3 endpoints with 10s can take up to 30s; set `Resilience.TotalTimeout` for a whole-request bound (shared across endpoints, see [Timeouts and cancellation](#timeouts-and-cancellation)). Upload progress also accumulates across endpoints (like a retried body — it never jumps back).
- **The body must replay in order**: the same requirement as retries (streams must be seekable); non-seekable streams and `StreamContent` throw `ERR_BAD_OPTION_VALUE` as soon as failover is enabled.
- **Streaming responses can only fail over at the response-head stage**: once the body is handed to the caller, a mid-stream disconnect is neither retried nor failed over (same rule as "the breaker only accounts at the response head").
- **Endpoints must be absolute `http` / `https` URLs without query or fragment**: an endpoint is a base URL, and how an endpoint query would combine with the request query is undefined, so it is rejected with `ERR_BAD_OPTION_VALUE` (to add a parameter only on a backup endpoint, build the URL in a request interceptor).
- **Assigning the list snapshots it**: mutating your own list afterwards does not affect the effective config; what you read back is that internal snapshot and must not be modified.
- **Failing over re-checks the HTTP/3 guard**: an endpoint that cannot run the requested version (e.g. a plaintext mirror with a strict h3 policy) is skipped and the next one is tried; only when none is usable does it throw `ERR_BAD_OPTION_VALUE`.
- Each failover produces one attempt event with the same `AttemptNumber` and an incremented `EndpointIndex` (see [Observability](#observability)).

### Idempotency keys

One key per **logical request**, written into a request header; retries, backup endpoints and hedging branches all carry the same key, so the server treats them as a single write.

```csharp
var client = Axios.Create(c => c.Resilience.Idempotency.Enabled = true);        // off by default

await client.PostAsync("orders", order);                             // Idempotency-Key: <32-char Guid>
await client.PostAsync("orders", order, c => c.Resilience.Idempotency.Key = "upstream-7f3a");  // pass an upstream key through
```

- **Off by default**: attaching a key to non-idempotent methods turns "duplicate write" into "silent dedupe", so enabling it is a decision about your endpoint.
- **Retries must opt in to non-idempotent methods first**: `RetryPolicy` only retries idempotent methods by default, so a key alone will not make a POST resend — set `Resilience.AllowUnsafeRetry` as well (see [Retries](#retries)).
- **The key is written once onto the request template**, and copies of the template carry the header along — retries never change it. `OnSendingRequest` fires **per attempt**, so writing the key there is wrong (every retry gets a new one).
- **One key per logical request**: every `RequestAsync` call builds a new effective config and therefore a new key; a replay after an auth failure must reuse the original key, so put it into `Idempotency.Key` and send again.
- `Key` (explicit) wins over `KeyFactory`; when neither is set, a `Guid` in `N` format is used.
- `HeaderName` defaults to `Idempotency-Key`; a request that already carries that header is left untouched.
- A blank key from the factory or an invalid header name throws `ERR_BAD_OPTION_VALUE` before anything is sent.
- **Hedging + idempotency key**: both branches share one key, so after dedupe the server returns "already processed" to one of them. That is the point of an idempotency key, but it requires the server to actually dedupe by key — otherwise you just sent the same write twice.

### Attempt pipeline

One attempt (a single send) is modelled as a composable middleware chain. The default chain is **retry → failover → circuit breaker → hedging → gate → audit → send**, so a per-attempt capability is one middleware away instead of another branch inside the client.

```csharp
using AxiosNet.Pipeline;

// A per-attempt probe: Append lands at the chain tail (inside the audit, before the send),
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

using var client = Axios.Create(c => c.Resilience.Retry.Policy = new RetryPolicy(2), pipeline: pipeline);
```

- **`AxiosAttemptContext` is a read-only struct**; its fields are `Config` / `Uri` / `AttemptNumber` / `EndpointIndex` / `AttemptToken` / `ExternalToken` / `DataType` / `Target` / `StreamResponse` / `Retry` / `Circuit` / `Hedging` / `Failover` / `Slot`, derivable only through `WithXxx`.
- It is a struct, not a class, because the chain is compiled once at construction (zero allocation per attempt) and concurrent middleware need to copy the context per branch.
- **Two independent counters**: retries advance `AttemptNumber`, failover advances `EndpointIndex` (`0` = the request's own URL). Failover does not advance the attempt number, so one attempt number can carry several attempt events.
- **Built-ins are always on the chain and decide for themselves** by reading `ctx.Config`: with no retry policy the retry middleware is a straight pass-through. That is what lets the chain be frozen at construction instead of rebuilt per request.
- **Auditing is on by default**: `Attempt.Audit` is the sixth link (after the gate, before the terminal), so subscribing to `AxiosNet.Attempt` gives you every attempt's duration / status / error code; with no subscriber it only evaluates the switch and builds no state machine. It is already on the chain — `Append(Attempt.Audit)` is rejected as a duplicate built-in. Links added with `Append` sit inside it (closer to the terminal), so their execution time is counted in the audit event.
- **Order is the contract**: retry sits outermost, so failover, the breaker, hedging and the gate run again on every attempt; failover sits outside the breaker, so each endpoint gets its own breaker accounting; the breaker sits outside hedging, so a whole branch group records once; the breaker sits before the gate, so a rejected request never occupies a slot.
- **Reorder and remove**: `AxiosAttemptPipeline.Create(...)` lists the whole chain explicitly (first = outermost); `Remove(Attempt.Gate)` drops one. The pipeline is immutable — every edit returns a new instance. Duplicating a built-in throws.
- **There is no "put it outermost" API**: that position runs once per request and is exactly what response interceptors already do. `InsertBefore(Attempt.Retry, ...)` refuses explicitly and points at `client.Interceptors.Response`, which additionally offers `OnRejected`, runtime add/remove, and short-circuit coverage.
- **You do not manage the concurrency slot**: the gate releases it itself — a middleware that skips `next` (returning its own response) or throws cannot leak it. The only case where the gate does not release it is a **library-produced** streamed response: the slot travels with the body into `StreamOwner` and is released when the stream is disposed, so the request keeps occupying its quota while the body downloads. A `StreamOwner` built by a middleware is not that case: the gate still releases its own slot, and a self-built owner gets no concurrency quota.
- **Whoever creates it, disposes it**: a request message cloned by a middleware is that middleware's to dispose (the template still belongs to the pipeline). When replacing a response you must dispose the previous `AxiosAttemptResult.StreamOwner`, or carry it into the new result (`new AxiosAttemptResult(newResponse, old.StreamOwner)`) — it holds the pool lease and the concurrency slot, and dropping it leaks both.
- **Middleware time is not counted against `Timeout`**: the budget starts inside the terminal, and gate queuing plus any middleware prologue happen outside it.
- Read request headers from the response side with `response.GetRequestHeader(name)` (request headers first, then content headers); a short-circuit response has no underlying request message and returns `null`.

How it divides the work with the other extension points:

| Goal | Use |
| --- | --- |
| Rewrite URL / headers / body, inject a short circuit (once per request) | Request interceptor |
| Shape outgoing `Body` before serialization | `transformRequest` |
| Rewrite the final request message before sending (signing, timestamps, idempotency keys; per attempt) | `c.Observability.OnSendingRequest` |
| Get the raw response bytes before deserialization (per attempt) | `c.Observability.OnRawResponse` |
| Shape incoming `Data` after deserialization (per attempt) | `transformResponse` |
| Shape the final response, turn an exception into a response | Response interceptor |
| Observe or steer **each attempt** (auditing included) | Attempt middleware |
| Change "how many retries / how long to back off / when to break / whether to hedge / which endpoints" | The matching `Policy` or sub-config, not a middleware swap |

### Short circuit

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

## Declarative client

Once endpoints multiply, URLs, parameters and headers scattered across call sites get hard to manage. The declarative entry point describes each endpoint as a **request model** or an **interface**, leaving calls as one-liners (`client.RequestAsync(request)` / `api.GetAsync(5)`); both forms are carried by the same client: `client.Create<T>()` / `client.RequestAsync(model)` are `AxiosNet.WebAPI` extension methods; switch to `ApiClient` when you need `CredentialProvider` / `ErrorFactory`. Namespace `AxiosNet.WebAPI` (attributes in `AxiosNet.WebAPI.Attributes`), same package as the core.

| Style | What it looks like | Use for | Cost |
| --- | --- | --- | --- |
| Request model (default recommendation) | `class GetUserRequest : IApiRequest<User>` with `[Path]` / `[Query]` / `[Body]` on properties | Endpoints fairly independent; requests passed around or reused as objects | One type per endpoint |
| Interface | `interface IUserApi` with routes and bindings on methods / parameters | Endpoints in groups; calls feel like local methods | Source generator emits real proxies (same path for JIT / Native AOT); falls back to `DispatchProxy` (JIT only) when not generated |

Both forms share one kernel: path-template validation, duplicate detection, default body inference, scalar guards, static headers and authorization semantics are identical. Model-based is the default recommendation — start there.

### Model-based API

Default recommendation: one class per endpoint. The route sits on the class, binding attributes on properties, the URL is built from the declaration, and the call is just `RequestAsync` / `GetUri` / `RequestStreamAsync`; the response type follows the generic argument of `IApiRequest<TResponse>`.

```csharp
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

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
    CredentialProvider = CredentialProviders.Fixed("token")
};

var user = (await client.RequestAsync(new GetUserRequest { Id = 5 })).Data!;   // User
string url = client.GetUri(new GetUserRequest { Id = 5 });                     // URL only
```

**Call and return shapes**

- The `TResponse` of `IApiRequest<TResponse>` is the type of `Data`: `RequestAsync` returns `AxiosResponse<TResponse>`, while `GetUri` only builds the URL and sends nothing.
- `client.RequestAsync(model)` / `RequestStreamAsync` are `AxiosClient` extension methods (`using AxiosNet.WebAPI;`) and share the same facade — `CredentialProvider` / `ErrorFactory` state included — with `new ApiClient(...)`.
- Per-request tweaks: `RequestAsync(request, config => ...)`, or derive from `ApiRequest<TResponse>` and override `Configure(config)`; the callback runs after binding and before authorization.
- `client.Axios` reaches the underlying `AxiosClient` (interceptors, pool, handler injection all work).

**Binding**

| Binding | Attribute | Notes |
| --- | --- | --- |
| Path segment | `[Path]` | Replaces `{name}`; value URL-escaped; `null` is an error |
| Query parameter | `[Query]` / undecorated | Bound to the query by default; arrays `a[]=v`, nesting `a[b]=v` |
| Dictionary query | `[Query]` + dictionary | Unnamed flattens to `?a=1&b=2`, explicit name keeps `?filters[a]=1` |
| Header | `[Header]` / `[HeaderCollection]` | `null` is skipped |
| Static headers | `[Headers]` (class / interface / interface method) | `"Name: Value"`; name only means remove that header (a same-name `[Header]` property / parameter still writes it back); method level overrides interface level |
| Form | `[Multipart]` + `[Part]` | The model itself is the form; `[Part]` overrides field name / filename / part type |
| Body | `[Body]` | At most one per model; `null` sends no body |
| Value formatting | `[Format]` (property / parameter) | Format string or static method; per element for collections, per value for dictionaries (see [Value formatting](#value-formatting)) |
| Encoding | `[BodyContentType]` / `[QueryCollectionFormat]` / `[Stream]` (class / property) | Property level overrides class level; two explicit declarations with different values throw `ERR_BAD_OPTION_VALUE`; `[Multipart]` and `[BodyContentType]` are mutually exclusive |
| Skip | `[ApiIgnore]` | Not bound at all |

- **Single-payload models can omit `[Body]`**: for POST / PUT / PATCH with exactly one undecorated **non-scalar** property, it binds as the body (dictionary properties excepted — always query).
- One property may carry several binding attributes (`[Path]` + `[Header]`): the value is read once and applied to each.
- Value formatting: `[Format]` on the member wins; everything else uses the instance default `AxiosConfig.Serialization.ValueText`. For declarative requests it must be set on the instance defaults — changing it in the per-request `configure` throws `ERR_BAD_OPTION_VALUE` (see [Value formatting](#value-formatting)).

**Encoding and streaming**

Encoding and frame format can be configured per request, or declared directly on the model:

```csharp
[Post("login")]
[BodyContentType("application/x-www-form-urlencoded")]   // class level: this model is form-encoded by default
public sealed class LoginRequest : IApiRequest<Token>
{
    [Body] public LoginForm Form { get; set; } = null!;
}

[Get("users")]
public sealed class ListUsersRequest : IApiRequest<User[]>
{
    [Query]
    [QueryCollectionFormat(AxiosCollectionFormat.Multi)]  // property level: applies to this query property only
    public int[] Ids { get; set; } = new int[0];
}

[Get("stream/events")]
[Stream(AxiosStreamFormat.ServerSentEvents)]              // class-level default frame format; NDJSON when omitted
public sealed class StreamEventsRequest : IApiRequest<Chunk> { }
```

- `[BodyContentType(...)]` may sit on a model class or property and writes `config.ContentType` (it drives body encoding, not just the header); `[QueryCollectionFormat(...)]` likewise writes `config.Serialization.CollectionFormat`.
- Precedence: a single declaration applies as written; property level overrides class level; **two explicit declarations with different values** throw `ERR_BAD_OPTION_VALUE` at build time instead of silently picking one.
- `[Multipart]` together with `[BodyContentType]` is an error (multipart's media type is fixed to `multipart/form-data`).
- Streaming: `[Stream(...)]` may sit on a model class or property as the default frame format, and `RequestStreamAsync(request, format)` still accepts an explicit format (the argument wins); the generic of `IApiRequest<TItem>` is the frame type. OpenAPI-generated models also carry `public const AxiosStreamFormat StreamFormat`, so you can pass `XxxRequest.StreamFormat`.
- Per-request tweaks still work: `RequestAsync(request, c => c.ContentType = ...)`, `c.Serialization.CollectionFormat = AxiosCollectionFormat.Multi`, or derive from `ApiRequest<T>` and pin them in `Configure`.
- Streaming bodies follow the client's `Timeout` / `Resilience.TotalTimeout` — there is no separate idle-timeout knob; frame parse failures map to `ERR_BAD_RESPONSE`, a mid-stream disconnect to `ERR_NETWORK` and cancellation to `ERR_CANCELED`.

**Validation and errors**

- Invalid models (missing route, unbound placeholder, multiple bodies…) throw `InvalidOperationException` on first call.
- Validate at startup with `ApiClient.ValidateModels(typeof(A), ...)` or `ValidateModels(assembly)` — returns all messages, never throws:
  - The type-based overloads work under Native AOT too (generated declarations go straight to the registry).
  - `ValidateModels(assembly)` relies on reflection enumeration — under AOT call the generated `AxiosNet.Generated.AxiosNetValidation_<AssemblyName>.ValidateAll()` instead.
- A declaration that is valid but fails at runtime while reading/formatting values (null path value, throwing formatter, invalid header name/value…) is a request-assembly failure: it throws `AxiosException` (`ERR_BAD_REQUEST`) with the original exception in `Cause`, the same policy as body / multipart assembly.

**Authorization and typed errors**

- Authorization: `[Authorize]` / `[Authorize("Basic")]` require it, `[AllowAnonymous]` skips the whole chain; the single extension point is `ApiClient.CredentialProvider` (`IApiCredentialProvider`), whose `ApiCredential` result decides how the header is written (see [credentials and token management](#credentials-and-token-management)).
- Typed errors: after `client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()`, status-code failures throw `ApiError<TError>` (network / timeout / cancellation bypass the factory). `ApiError<TError>` derives from `AxiosException`, so a plain `catch (AxiosException)` catches it too; `Code` / `ErrorCode` / `Config` / `Request` / `ToJson()` are all inherited.

**Native AOT**

- As long as the assembly declaring the model runs the bundled source generator, member accessors are baked at compile time (internal types included) with no reflection under AOT; without generated / registered metadata `RequestAsync` throws `AxiosAotMetadataException` (with repair guidance).
- Startup validation with `ApiClient.ValidateModels(typeof(A), ...)` works under AOT; assembly scanning does not — call the generated `ValidateAll()` instead.
- The generated path needs no `TrimmerRootAssembly`.

### Interface-based API

When writing a request model per endpoint is too much, hand the interface to `client.Create<T>()`: routes live on methods, binding attributes on parameters. With the bundled source generator the proxy class is emitted at compile time (the same path for JIT and Native AOT); only when nothing was generated does it fall back to `DispatchProxy` (JIT only).

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
var user = (await api.GetAsync(5)).Data!;                 // User
```

**Call and return shapes**

- Every method must carry exactly one route attribute. Four return shapes are supported: `Task<AxiosResponse<T>>` (full response) / `Task<AxiosResponse>` (body delivered as a raw `JsonElement`) / `Task<T>` (only `Data`) / `Task` (request only, body never parsed — a non-JSON body is not a parse error).
- Returning `Task<AxiosResponse<IAsyncEnumerable<T>>>` makes it a streaming method; a bare `Task<IAsyncEnumerable<T>>` has no response metadata or disposal ownership and fails startup validation.
- Synchronous returns / `ValueTask` / generic methods / `ref`·`out` are rejected.
- Failure semantics are identical across shapes: non-2xx throws `AxiosException`, and a configured `ErrorFactory` still replaces/wraps it; with the `Task` shape the factory still receives the response (only the body is discarded).

**Binding**

- `CancellationToken` parameters are recognized by type (at most one) and never bound.
- Every other parameter follows the `[Path]` / `[Query]` / `[Header]` / `[HeaderCollection]` / `[Part]` / `[Body]` / `[ApiIgnore]` or "undecorated" rules, including default body inference.
- `[Multipart]` sits on the method: parameters are the form parts, and `[Part]` overrides the field name / file name / part type.
- `[Format]` may sit on parameters; the rest follows the instance default `AxiosConfig.Serialization.ValueText` (see [Value formatting](#value-formatting)).

**Encoding and streaming**

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

Encoding declarations are method-level too — the media type decides how the body is encoded, and the collection format decides how arrays are written into the URL:

```csharp
[Post("login")]
[BodyContentType("application/x-www-form-urlencoded")]   // object flattened as a form (setting only the Content-Type header does not change encoding)
Task<AxiosResponse<Token>> LoginAsync([Body] LoginForm form, CancellationToken cancellationToken = default);

[Get("users")]
[QueryCollectionFormat(AxiosCollectionFormat.Multi)]    // ids=1&ids=2 (default Brackets: ids[]=1&ids[]=2)
Task<AxiosResponse<User[]>> ListAsync([Query] int[] ids, CancellationToken cancellationToken = default);
```

- Method-level encoding: `[BodyContentType(...)]` writes `config.ContentType` (it drives body encoding, not just the header) for forms, XML or custom media types; it is mutually exclusive with `[Multipart]` (whose media type is fixed).
- `[QueryCollectionFormat(...)]` writes `config.Serialization.CollectionFormat` and overrides instance defaults; OpenAPI `explode: true` maps to `AxiosCollectionFormat.Multi`.
- Interface-level streaming: pick the frame format with `[Stream(AxiosStreamFormat.ServerSentEvents)]` / `[Stream(AxiosStreamFormat.JsonArray)]` (NDJSON when omitted); the returned stream can only be enumerated once and shares the exact timeout / error semantics of `RequestStreamAsync(...)`.
- Interface declarations remain method-level (unchanged); the new class / property scopes for `[BodyContentType]` / `[QueryCollectionFormat]` / `[Stream]` belong to models — see "Encoding and streaming" under [Model-based API](#model-based-api) for precedence and conflicts.
- Interface methods have no per-method `configure` hook — switch to a request model when you need per-request tweaks.

**Validation and errors**

- Invalid declarations (missing route, multiple bodies, `[Part]` without `[Multipart]`, `[Multipart]` combined with `[Body]` / `[BodyContentType]`, `[Stream]` on a non-streaming method, duplicate parameter names…) throw `InvalidOperationException` from `Create<T>()`.
- Validate at startup with `ApiClient.ValidateInterfaces(typeof(IA), ...)` or `ValidateInterfaces(assembly)`: returns all messages, never throws, and aggregates every failing method of the same interface rather than only the first.
  - The type-based overloads work under Native AOT.
  - `ValidateInterfaces(assembly)` throws `PlatformNotSupportedException` under AOT — call the generated `ValidateAll()` instead.
- Declarative assembly failures (path / query / header / body read or formatting threw) are unified as `ERR_BAD_REQUEST`, with the original exception in `Cause`.

**Authorization and typed errors**

- `[Headers]` / `[Authorize]` / `[AllowAnonymous]` may sit on the interface or on an interface method; with base interfaces, static headers and authorization merge in the **derived interface's scope** — the same base method can carry different static headers per derived interface.
- Method-level declarations win over interface level: same-name static headers are overridden, `[AllowAnonymous]` makes a single method anonymous, `[Authorize("Basic")]` switches the scheme for one method.
- Declaring both `[Authorize]` and `[AllowAnonymous]` on one method is ambiguous (`Create<T>()` / `ValidateInterfaces` reports it).
- Typed errors work the same as with request models: with `client.ErrorFactory = new ApiErrorFactory<ProblemDetails>()`, a bad status code throws `ApiError<TError>` (network / timeout / cancellation do not go through the factory).
- The credential provider (via `ApiAuthContext.Request`) and the error factory receive an `ApiInterfaceInvocation` (interface type, method name, argument snapshot) instead of a model instance; use those three to branch per request.

**Native AOT**

- As long as the assembly declaring the interface runs the source generator, a real proxy is emitted (internal interfaces included); closed generic interfaces (e.g. `IRepo<User>`) are generated at the call site — `Create<T>()` / `AddApiInterface<T>()` — and only an open generic declaration itself reports warning `AXA011`.
- Without generated metadata, `Create<T>()` throws `AxiosAotMetadataException` (with repair guidance) under AOT and falls back to `DispatchProxy` (requires a public interface) on JIT.
- The generated path needs no `TrimmerRootAssembly` and no longer roots the interface type.

### Value formatting

A value written into a URL, a header or a form always ends up as text. The library funnels "value → text" through **one** path, with two declaration layers stacked by priority:

1. `[Format]` on the member (model property / interface parameter) — the most specific;
2. `AxiosConfig.Serialization.ValueText` on the client's instance defaults (a per-request config works too, for direct calls) — applies when the member declares nothing; the callback receives a `ValueTextChannel`, so it can target a single channel;
3. Channel defaults (date-time folded to UTC milliseconds, enums by name, `byte[]` as base64, `IFormattable` with the invariant culture…).

```csharp
[Get("users/{id}")]
public sealed class ListRequest : IApiRequest<User[]>
{
    [Path, Format("0000")] public int Id { get; set; }                  // /users/0002
    [Query, Format("yyyy-MM-dd")] public DateTime Since { get; set; }   // since=2024-01-02
    [Query, Format("X2")] public int[] Codes { get; set; } = [];        // codes[]=0B&codes[]=FF

    [Query("amounts"), Format(typeof(Formats), nameof(Formats.Money))]  // amounts[min]=12.50
    public Dictionary<string, decimal> Amounts { get; set; } = new();
}

public static class Formats
{
    // Fixed signature: public static string? M(object? value, ValueTextChannel channel);
    // returning null means "not mine", fall back to the default rule; pass the channel as _ when you do not care.
    public static string? Money(object? value, ValueTextChannel _)
        => value is decimal amount ? amount.ToString("0.00", CultureInfo.InvariantCulture) : null;
}

var client = new ApiClient(url, c =>
{
    // Second argument is the channel the value is written through (AxiosNet.Metadata.ValueTextChannel):
    // RFC 1123 in headers, compact date elsewhere; returning null means "not handled here".
    c.Serialization.ValueText = (value, channel) => value is DateTime date
        ? channel == ValueTextChannel.Header
            ? date.ToString("R", CultureInfo.InvariantCulture)
            : date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
        : null;
});
```

- **Channels covered**: path segment / query string / header / form field / multipart text part / raw scalar body. One rule set — there is no "works in the query but not in the form"; the hook's second argument is exactly one of these six (`Path` / `Query` / `Header` / `Form` / `Multipart` / `ScalarBody`), so it can branch per location.
  - A header collection (`[HeaderCollection]`) applies the hook per dictionary value (channel `Header`), exactly like a single `[Header]`; `[Format]` still cannot be put on that member (the values are already strings — build-time error).
- **Member formatters get the channel too**: the static method behind `[Format(typeof(Formats), nameof(Formats.Money))]` has a fixed signature of `public static string? Money(object? value, ValueTextChannel channel)`; the second argument is the channel that member is written through. Discard it as `_` when you do not care — there is no one-parameter form.
  - The raw scalar body means `data` itself is a scalar, binary forms such as `byte[]` included: a binary body only becomes a text body when the declaration / hook actually produces text, otherwise it is sent as raw bytes, and a hook returning `null` never triggers base64.
  - A raw scalar body on a direct call (`PostAsync(url, data)`) goes through the hook too; but when `data` is a `string` / `byte[]` / `Stream` / byte chunks it is a "verbatim body" — there is no value-to-text step, so the hook does not take part.
  - Nested objects, dictionaries and `JsonElement` trees inside the query apply it per leaf: member declaration first, then the instance hook.
- **Collections and dictionaries**: on a collection it applies per element, on a dictionary per value (keys untouched). Declarations on dictionaries skip the build-time type check — the value type is only known at runtime.
- **Format strings** require `IFormattable` and always use the invariant culture (never `CurrentCulture`); use a formatter method when you need culture-aware output.
- **Structured bodies are out of scope**: `[Format]` on a structured `[Body]` is a build-time error (the serializer owns the document). To apply `[Format]` inside JSON as well, opt into the bridge:

```csharp
using AxiosNet.WebAPI.Serialization;

// net7.0+; unavailable under Native AOT (the bridge builds generic converters per property type)
config.Serialization.JsonOptions =
    AxiosJsonFormat.ApplyTo(new JsonSerializerOptions(JsonSerializerDefaults.Web));
```

- **Build-time errors**, never a silent runtime fallback (model properties and interface parameters share the same validation):
  - `[Format]` combined with a structured `[Body]` / `[HeaderCollection]` / file part (`Stream` / `byte[]` / `Memory<byte>` / `ReadOnlyMemory<byte>` / `ArraySegment<byte>` / `FormFile` / `HttpContent`, with collection elements and dictionary values judged the same way);
  - a format string on a non-`IFormattable` scalar (e.g. `bool`) or on a binary form (`byte[]` / `Memory<byte>` / `ReadOnlyMemory<byte>` / `ArraySegment<byte>`);
  - `[Format]` on a non-scalar (POCO) query parameter.
- **Catching mistakes**, none of the three waits for a request:
  - with `nameof(...)` a typo is an ordinary compile error (`CS0117`);
  - a wrong signature (not `public static` / generic / parameters are not `(object?, ValueTextChannel)` / return is not `string`) is reported at compile time as `AXA015`, with the correct signature spelled out in the message;
  - a value shape that does not fit the channel (a format string on `bool` / a binary form, `[Format]` on a structured `[Body]` or a file part) throws `InvalidOperationException` while the binding plan is built.
- Without the source generator `AXA015` does not run — use `ApiClient.ValidateModels(...)` / `ValidateInterfaces(...)` to surface these at startup instead.
- **Native AOT**: the source generator bakes the formatter delegate into the metadata (an invalid signature reports `AXA015`), so nothing reflects at runtime. Shapes registered by hand via `RegisterShape<T>()` are not baked: `[Format(typeof(...), nameof(...))]` on them fails explicitly under AOT with a pointer to "enable the source generator or use a format string" instead of silently dropping the format.

### OpenAPI import

When the contract already exists, you do not have to write any of the declarations above. `src/AxiosNet.Cli` is a design-time dotnet tool (command `axiosnet-gen`, package `AxiosNet.Cli`) that reads OpenAPI 3.0.x / 3.1.x and Swagger 2.0 documents (JSON / YAML; local file, http(s) URL or stdin via `-`) and emits declarative C#.

| Style | Output | Call site |
| --- | --- | --- |
| Model-based (default) | one `ApiRequest<T>` request model per operation | `client.RequestAsync(new XxxRequest { ... })` |
| Interface-based (`--style interface`) | one interface per tag | `client.Create<T>()` |

Both styles are declarations only — request models and proxies are baked at compile time by the source generator, the same path for JIT and Native AOT. The tool's YAML parser is a design-time dependency — the `AxiosNet` runtime stays free of third-party packages.

```bash
# Install once (global dotnet tool, command axiosnet-gen; ships net6.0 + net8.0, runs on .NET 6 and up)
dotnet tool install -g AxiosNet.Cli

# Generate (model-based by default)
axiosnet-gen demo.openapi.json -o Generated -n MyApp.Api

# Interface-based
axiosnet-gen demo.openapi.json -o Generated -n MyApp.Api --style interface

# Remote document + import report
axiosnet-gen https://api.example.com/openapi.json --namespace MyApp.Api --output Generated --report import.json

# Working on the tool itself: run it straight from the repository
dotnet run --project src/AxiosNet.Cli -f net8.0 -- demo.openapi.json -o Generated -n MyApp.Api
```

Model-based output looks like this (the `users` tag → `UsersRequests.g.cs`, one class per operation; properties are PascalCase, so the wire name is stated explicitly when it differs):

```csharp
[Get("/api/users")]
[Authorize]                                             // root-level / operation-level security
public sealed class ListUsersRequest : ApiRequest<User[]>
{
    [Query("role")] public string? Role { get; set; }
    [Query("ids")]  public long[]? Ids { get; set; }

    protected override void Configure(AxiosConfig config)
    {
        config.Serialization.CollectionFormat = AxiosCollectionFormat.Multi;   // explode: true
    }
}
```

`Configure` is how model style expresses method-level encoding (attributes have no usable method-level target):

- non-JSON request bodies set `config.ContentType` there (forms / XML / custom media types);
- query arrays set `config.Serialization.CollectionFormat`.

Streaming endpoints additionally get `public const AxiosStreamFormat StreamFormat`, read with `client.RequestStreamAsync(new StreamEventsRequest(), StreamEventsRequest.StreamFormat)`. When a response has no body the model derives from `ApiRequest<object>` and `Data` is `null` after the usual `RequestAsync`.

**Default mapping rules** (each has a switch or shows up in the report):

- **Grouping**: operations are grouped by their first tag.
  - Model style writes one file per group (`users` → `UsersRequests.g.cs`) and names request classes from the `operationId` + `Request` (collisions get a numeric suffix and never shadow runtime types such as `ApiRequest`).
  - Interface style writes one interface per tag (`users` → `IUsersApi`; a tag already ending in `Api` just gets the `I` prefix).
  - Untagged operations go to `DefaultRequests.g.cs` / `IDefaultApi`.
  - `--single-file` puts everything into one file; `--single-interface` (with `--interface-name`, default `IApiClient`) is interface-style only.
- **Parameters and bodies**: `path` / `query` / `header` map to `[Path]` / `[Query]` / `[Header]` (on properties in model style, on parameters in interface style).
  - One request media type is chosen, in order JSON > `+json` > `*/*` > form > multipart > text > binary > first.
  - JSON produces a strongly typed `[Body]`; `application/x-www-form-urlencoded` sets `config.ContentType` in the generated `Configure` (model style) or produces `[BodyContentType(...)]` (interface style); `multipart/form-data` expands object properties into `[Part("wire")]` members (`format: binary` → `byte[]`); text bodies become `string`; binary bodies become `byte[]`.
  - Multiple candidates report `MULTIPLE_REQUEST_CONTENT`.
- **Query arrays**: `style: form` + `explode: true` → `Multi` (`ids=1&ids=2`); `explode: false` → `Csv`; `spaceDelimited` / `pipeDelimited` → `Ssv` / `Pipes`; Swagger 2 `collectionFormat` maps the same way. Model style writes it in `Configure`; interface style emits `[QueryCollectionFormat(...)]`.
- **Responses**: the lowest 2xx status wins (then `2XX`, then `default`).
  - Model style derives from `ApiRequest<T>` (a call yields `AxiosResponse<T>`) or `ApiRequest<object>` for a bodyless response.
  - Interface style yields `Task<AxiosResponse<T>>` / `Task<AxiosResponse>`, and `--response-wrapper none` delivers `Task<T>` instead (interface style only).
  - `text/event-stream` or an `x-axios-stream` marker produces a `StreamFormat` constant plus the frame type in model style, or `[Stream(...)]` and `Task<AxiosResponse<IAsyncEnumerable<T>>>` in interface style; an array schema is delivered frame by frame as its element type.
  - Multiple status codes / multiple content entries report `MULTIPLE_SUCCESS_RESPONSES` / `MULTIPLE_RESPONSE_CONTENT`.
- **Authorization**: root-level or operation-level `security` → `[Authorize]`, but only http bearer / basic.
  - `apiKey` / `oauth2` / `openIdConnect` and other http schemes are reported with a warning and get no authorization declaration — set the credential yourself on the `AxiosClient` Authorization chain or request config (for apiKey, add the documented header / query parameter / cookie).
  - `security: []` → `[AllowAnonymous]`.
  - Model style puts it on the request class; interface style moves it up to the interface only when every operation in the group requires the same scheme, otherwise it stays on methods.
- **Models**:
  - `allOf` is flattened into members.
  - OpenAPI 3.0 `nullable`, 3.1 `type: [..., "null"]` and Swagger `x-nullable` all become nullable.
  - `oneOf` / `anyOf` cannot be expressed as a declarative type, so the primary type is `JsonElement` and each member type is generated next to it for the caller to discriminate.
  - An `enum` is generated only when every value is a valid C# identifier (member names are the wire values), otherwise a constants class plus `string` is generated; numeric enums stay numeric.
  - Every generated property carries an explicit `[JsonPropertyName]` matching the defaults of `System.Text.Json`.
- **Gaps are reported, not guessed**: broken `$ref`s, external references, cookie parameters, non-scalar (array / object / `label`·`matrix` style) path and header parameters, arrays without `items`, reference cycles and friends produce stable warning codes and degrade (to `JsonElement`, skipping the whole operation, or `byte[]`) instead of silently dropping fields. `--fail-on warnings` turns them into exit code 1, and the full list is in the report's `warnings`.

**Safe output**:

- Output is deterministic (LF line endings, stable ordering) and every file carries `// <auto-generated />`; a sibling `axiosnet-gen.manifest.json` records the file list from the previous run, including leftover stale names.
- `--check` compares without writing: missing files, out-of-date content or leftover stale files all return 1 — suitable for CI to catch hand edits.
- Files without a generation marker are protected from overwrite unless `--force` is given; deleting leftover stale files also only happens under `--force`, and only for marked files — files you wrote yourself are never deleted automatically.
- `--dry-run` prints the output to stdout; for remote documents use `--header "Authorization: Bearer ..."` and `--timeout <seconds>`.

The full flow is demonstrated by [`OpenApiGeneratedExample`](samples/AxiosNet.Simple/WebAPI/Examples/OpenApiGeneratedExample.cs): it generates [`WebAPI/Generated/`](samples/AxiosNet.Simple/WebAPI/Generated) from [`samples/AxiosNet.Simple/WebAPI/demo.openapi.json`](samples/AxiosNet.Simple/WebAPI/demo.openapi.json) in the default model style and calls the real routes of `DemoApiServer` with `RequestAsync` / `RequestStreamAsync`.

Coverage includes tag-grouped `*Requests.g.cs`, root authorization, form login, multipart, and SSE / JSON-array streaming. Regenerating that output inside the repository:

```bash
dotnet run --project src/AxiosNet.Cli -f net8.0 -- \
  samples/AxiosNet.Simple/WebAPI/demo.openapi.json \
  -o samples/AxiosNet.Simple/WebAPI/Generated \
  -n AxiosNet.Simple.WebAPI.Generated --force
```

### Credentials and token management

A declarative client has exactly one authorization extension point: `ApiClient.CredentialProvider` (`IApiCredentialProvider`). It runs once before every send, and the `ApiCredential` it returns decides how the header is written; `[AllowAnonymous]` skips the whole chain. It ends up as an adapter over the kernel's single credential slot `Transport.Auth` — when using the kernel directly you can put static credentials in that slot:

```csharp
// Configure the kernel credential slot directly (pick one form)
config.Transport.Auth = AxiosCredential.Basic("user", "pass");      // static Basic
config.Transport.Auth = AxiosCredential.Bearer("token");           // static Bearer (optional second argument overrides the scheme)
config.Transport.Auth = AxiosCredential.Header("Signature abc");   // complete header value
config.Transport.Auth = AxiosCredential.Provider(source);          // per-request provider

// Declarative callers keep their existing code (wrapped as the provider form internally)
var client = new ApiClient("https://api.example.com/")
{
    CredentialProvider = CredentialProviders.Fixed("token")
};
```

| Return value | Placement | 401 replay |
| --- | --- | --- |
| `ApiCredential.Token(token)` | Writes `Authorization: {scheme} {token}` using the `[Authorize]` scheme (`Bearer` by default) | Yes |
| `ApiCredential.Header(value)` | Sends `value` verbatim as the full `Authorization` header value, without a scheme prefix (equivalent to `Header("Authorization", value)`) | Yes |
| `ApiCredential.Header(name, value)` | Request header `{name}: {value}` (overwrites a same-named header), e.g. `X-Api-Key`; a `name` of `Authorization` (case-insensitive) is treated as a complete header value | Yes |
| `ApiCredential.Query(name, value)` | Query parameter `?{name}={value}` (overwrites a same-named parameter), e.g. `access_token` | Yes |
| `ApiCredential.Custom()` | The provider already wrote its own config (token in body, several headers at once, body signature); the library touches nothing | No |
| `ApiCredential.None` (or `Token(null)`) | Sends no credential | No |

- **One slot, one source**: the `Authorization`-header placement still lives in the single `Transport.Auth` slot, taking exactly one of four forms (static Basic / static token / complete header value / provider). `CredentialProvider` is an adapter for the provider form; `Transport.BasicAuth` has been folded into this slot. `Header` (non-`Authorization` name) / `Query` placements do not occupy that slot and are written straight into this request's config.
- **Conflicts error, never silently suppressed**: an explicit `Authorization` header together with the credential slot, or `CredentialProvider` together with an already-set `Transport.Auth`, throws `ERR_BAD_OPTION_VALUE` before sending. With only one of them set, that source applies. The credential → header mapping exists once in the kernel; WebAPI only supplies values and the 401 replay policy. `Header` (non-`Authorization` name) / `Query` overwrite same-named keys and take no part in that conflict check.
- **401 replay covers managed placements**: all three library-written shapes (`Token` / `Header` / `Query`) invalidate the stale value, fetch once more and replay once after a 401; `Custom` / `None` never replay. Static Basic / static token / complete header value never participate — use `CredentialProvider` when you need refresh (the kernel exposes `IsRefreshable` for the source).
- **Declared scheme must match the static slot**: when a declaration such as `[Authorize("Token")]` carries a scheme, a static `Transport.Auth` credential with a different scheme throws `ERR_BAD_OPTION_VALUE` before sending instead of silently deferring to the slot. With a static Basic credential the declaration must say `[Authorize("Basic")]`; a token configured as `AxiosCredential.Bearer(token, "Token")` matches `[Authorize("Token")]`.
- **No wasted token fetch**: an explicit header with a provider is rejected first, so the provider is never called.
- **`Token` always prefixes the scheme**: the token form carries the token only; use `ApiCredential.Header(value)` for complete values such as `Basic xxx` / `Signature xxx`.
- **`Header` / `Query` are managed placements**: API-key services use `ApiCredential.Header("X-Api-Key", key)`; query-parameter services (WeCom's `access_token` and friends) use `ApiCredential.Query("access_token", token)`. Both can switch the placement name per request (`ctx.Request` branching), and both feed the cache identity key — different keys never share a cached response. A blank placement name throws `ArgumentException`; a blank value means `None`.
- **`Header` has one rule**: omit the name and it writes the complete `Authorization` value (no scheme prefix); give a name and it writes that header. A name of `Authorization` (case-insensitive) behaves exactly like the omitted form — both go through the kernel credential slot and both conflict with an explicit `Authorization` header.
- **Custom credentials**: when the value depends on the request itself (token in the body, several headers at once, signing a business object rather than raw bytes) return `ApiCredential.Custom()` and write `ctx.Config.Headers` / `ctx.Config.Params` yourself (model bindings have already been merged); branch per request with `ctx.Request` (an `ApiInterfaceInvocation` for interface-based calls). `Custom` is not managed by the library, so it never replays — pass `invalidate:` to `Create` when you want a forced refresh after 401. Signing the **final body / final URL** can also go here — mind the `transformRequest` and 401-replay trade-offs, see [managed token plus body signature](#managed-token-plus-body-signature).
- **Hand-written query parameters use `SetQuery`**: `ctx.Config.SetQuery(name, value)` creates a dictionary when `Params` is empty and writes in place when `Params` is already a mutable `string`-keyed / `object`-valued dictionary; any other shape (object / raw query string / read-only dictionary) throws `ERR_BAD_OPTION_VALUE` instead of silently replacing the existing parameters. For a managed credential use `ApiCredential.Query` instead of writing it yourself.
- **`Fixed` has two entry points**: `Fixed(ApiCredential)` accepts any placement; `Fixed(string)` is the short name for `Fixed(ApiCredential.Token(...))` and additionally rejects a blank token (`Fixed(" ")` throws `ArgumentException`; use `Fixed(ApiCredential.None)` to mean "this request carries no credential").
- **Invalidation on 401**: the library calls `IApiCredentialProvider.Invalidate(staleToken)` after a 401; the default implementation does nothing. The argument is the last placement value (`Header` cannot expose the token itself, so it passes `null` for unconditional invalidation), and passing the value avoids discarding a token another request just refreshed.
- Register `IApiCredentialProvider` in the container and every `ApiClient` picks it up (see [dependency injection](#dependency-injection)).

#### Built-in token manager

`AccessTokenManager` implements the same interface. It decides **when to refresh and keeps concurrent refreshes to one**, while the HTTP call that actually exchanges a token stays yours (the library never calls a token endpoint for you).

```csharp
using var tokens = new AccessTokenManager(new AccessTokenOptions(AcquireAsync)
{
    RefreshAsync = RefreshAsync,                        // falls back to AcquireAsync when omitted
    RefreshAhead = TimeSpan.FromSeconds(30),            // 1 minute by default
    RefreshTimeout = TimeSpan.FromSeconds(10)           // 30 seconds by default
});

using var client = new ApiClient("https://api.example.com/") { CredentialProvider = tokens };

// Both callbacks return the token plus its expiry (ExpiresAt = null means "never expires")
static ValueTask<AccessToken> AcquireAsync(CancellationToken ct) { ... }
static ValueTask<AccessToken> RefreshAsync(CancellationToken ct) { ... }
```

- **Cache hits are free**: a token outside the refresh window is returned without touching a callback.
- **One refresh in flight**: concurrent callers share the same refresh; failures are not cached, so the next call retries.
- **Refreshes ignore caller cancellation**: one client disconnecting neither fails the other waiters nor wastes a refresh (the refresh itself is bounded by `RefreshTimeout`, which throws `TimeoutException`).
- **Refresh-ahead is clamped**: the effective lead is min(`RefreshAhead`, half the token lifetime), so short-lived tokens are not refreshed the moment they arrive.
- **Forced refresh after 401**: `tokens.Invalidate(staleToken)` drops the current token (passing the stale value avoids discarding a token another request just refreshed); the next call must refresh.
- **Fetching a token needs no request context**: `tokens.GetTokenAsync(ct)` returns the token text directly, for providers that decide the placement themselves (query parameter / custom header); `GetTokenAsync(context, ct)` is equivalent and ignores `context`.
- **Automatic replay on 401**: when the response is 401 and the library wrote this request's `Authorization` header, the credential is invalidated, fetched again, and the request is replayed **once** (a second 401 goes through the normal error path; an unchanged credential means no replay). The replay keeps the same idempotency key; non-idempotent methods require `Resilience.AllowUnsafeRetry` (the same switch as kernel retries). Turn it off with `ApiClient.RetryOnUnauthorized = false`.
- **401 is owned by one layer only**: when the credential slot is in provider form, the kernel stops retrying 401 (even if 401 is listed in `RetryStatusCodes`); refresh and replay belong to the declarative layer alone, so there is no double loop.
- **Bodies during replay**: data bodies (`null` / `byte[]` / `string` / business objects / stream-free `FormData`) are rebuilt by the request model; whitelisted in-memory content (`ByteArrayContent` / `StringContent` / `FormUrlEncodedContent` / `ReadOnlyMemoryContent` / `JsonContent` / fully replayable `MultipartContent`) is snapshotted before the first send, bounded by `Limits.MaxRequestBytes` — over the limit no snapshot is taken and this request is not replayed (never a silent second send with an empty body). `Stream`, multipart with stream parts, `StreamContent` and custom content are never replayed; declarations with an explicit `Authorization` header or `[AllowAnonymous]` are never replayed either.

#### Bootstrap the token through the same client

Fetching a token is usually just another endpoint on the same client. Mark the login / token request `[AllowAnonymous]` so it skips the credential chain and cannot recurse into `AccessTokenManager`; the acquire and refresh callbacks can then call that very `ApiClient`:

```csharp
[Post("login")]
[AllowAnonymous]
public sealed class LoginRequest : IApiRequest<TokenResponse>
{
    [Body] public LoginPayload Credentials { get; set; } = null!;
}

ApiClient? api = null;
using var tokens = new AccessTokenManager(new AccessTokenOptions(async ct =>
{
    var r = await api!.RequestAsync(new LoginRequest
    {
        Credentials = new LoginPayload("alice", "wonderland")
    }, cancellationToken: ct);
    return new AccessToken(r.Data!.Token, DateTimeOffset.UtcNow.AddSeconds(r.Data.ExpiresIn));
}));

api = new ApiClient("https://api.example.com/") { CredentialProvider = tokens };
using var client = api;
```

The `/login` request carries no `Authorization` header, and 401 refresh replays go through the same anonymous endpoint. Regression test: `test/AxiosNet.Tests/WebAPI/SelfHostedTokenEndpointTests.cs`.

#### Placement: any request header or query parameter

Not every service puts its token in the `Authorization` header: API-key services use a custom header (`X-Api-Key`), WeCom-style services use a query parameter (`access_token`). Both are managed placements — the library writes the value and replays it after a 401, while `AccessTokenManager` caching and refresh-ahead keep working:

```csharp
// any request header
client.CredentialProvider = CredentialProviders.Fixed(ApiCredential.Header("X-Api-Key", apiKey));

// query parameter (with a token manager, so a 401 gets a fresh value and a replay)
using var tokens = new AccessTokenManager(new AccessTokenOptions(AcquireAsync));
client.CredentialProvider = CredentialProviders.Create(async (_, ct) =>
    ApiCredential.Query("access_token", await tokens.GetTokenAsync(ct)));
```

The placement name can switch per request (`ctx.Request` branching); writing the same placement twice overwrites. Values that depend on the request itself (token in the body, several headers at once, body signature) can all use `ApiCredential.Custom()`; see [managed token plus body signature](#managed-token-plus-body-signature) for the trade-offs.

Services using `Header` / `Query` placements often report an expired token as HTTP 200 plus a business error code (no 401); in that case call `tokens.Invalidate()` from your error handling and the next request fetches a fresh token.

#### Managed token plus body signature

When you need both a managed token (automatic refresh-and-replay on 401) and a signature over the final body, there are two ways to write it:

| Approach | Automatic replay on 401 | Fits when |
| --- | --- | --- |
| Managed placement for the token + `OnSendingRequest` for the signature | Yes | The general case; the hook sees the final bytes and the final URL |
| Do everything inside `ApiCredential.Custom()` | No | There is no `transformRequest`, and you do not need automatic replay on 401 |

The first keeps credentials and signing on their own layers: the token still refreshes and replays as usual, and the hook runs after the body is serialized and the credential header is written, so what it reads is the final bytes.

```csharp
using var tokens = new AccessTokenManager(new AccessTokenOptions(AcquireAsync));

using var api = new ApiClient("https://api.example.com/", defaults =>
{
    defaults.Observability.OnSendingRequest = async (request, config, ct) =>
    {
        // The body is serialized, the URL is built and X-Api-Token is already written
        var body = request.Content is null
            ? Array.Empty<byte>()
            : await request.Content.ReadAsByteArrayAsync(ct);

        var token = request.Headers.TryGetValues("X-Api-Token", out var values) ? values.First() : string.Empty;
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var nonce = Guid.NewGuid().ToString("N");

        request.Headers.TryAddWithoutValidation("X-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Nonce", nonce);
        request.Headers.TryAddWithoutValidation(
            "X-Signature",
            Sign(request.Method.Method, request.RequestUri!.PathAndQuery, timestamp, nonce, token, body)); // your signing function
    };
});

api.CredentialProvider = CredentialProviders.Create(async (_, ct) =>
    ApiCredential.Header("X-Api-Token", await tokens.GetTokenAsync(ct)));
```

- **What the hook sees**: the final absolute URI (path parameters substituted, query merged and escaped), the serialized body bytes, and the **already written credential header**. Each attempt gets its own copy, so header edits never leak into retries or other hedging branches.
- **A 401 replay re-signs with the new token**: the replay runs "resolve credential → build URL → fire hook" again, so the token inside the signature and the token in the header always match.
- **Signature headers never reach the cache key** (the key is built before the hook). With caching on, the token still contributes to the key, but a cache hit sends nothing and therefore signs nothing — disable caching for signed endpoints, or only store responses carrying `Cache-Control: no-store`.
- **The hook fires on every attempt**: retries, backup endpoints and hedges each sign once with a fresh timestamp and nonce, which is exactly what a signature wants. Never write the idempotency key here (a new value per attempt looks like two writes); use [`Resilience.Idempotency`](#idempotency-keys).
- **Reading the body costs**: buffered bodies (JSON / form / `byte[]` / `string`) hand over bytes directly; streaming bodies get consumed. Sign buffered bodies only, or sign headers only.
- **Error mapping**: a throwing hook yields `ERR_BAD_REQUEST` with the request never sent and no connection taken; cancellation or timeout inside the hook is attributed to the current attempt (`ERR_CANCELED` / `ECONNABORTED`).

**`Custom()` can also do it in one pass**: path parameters in `ctx.Config.Url` are already substituted, `ctx.Config.Body` is the business object, and whatever you write back into `Body` is sent as-is — so what you sign and what you send are the same bytes. Resolve options the way the library does (`Serialization.JsonOptions` first, then `AxiosJson.Options`) or the wire format will differ from your normal requests.

```csharp
api.CredentialProvider = CredentialProviders.Create(async (ctx, ct) =>
{
    var token = await tokens.GetTokenAsync(ct);

    // Pin the body to final bytes and write it back: the library sends it as-is, no second serialization
    var body = ctx.Config.Body switch
    {
        null => Array.Empty<byte>(),
        byte[] raw => raw,
        string text => Encoding.UTF8.GetBytes(text),
        var value => JsonSerializer.SerializeToUtf8Bytes(value, ctx.Config.Serialization.JsonOptions ?? AxiosJson.Options),
    };
    if (ctx.Config.Body is not (null or byte[] or string))
    {
        ctx.Config.Body = body;
    }

    // Path parameters are already substituted; this fills in BaseAddress and params without sending
    var uri = api.Axios.GetUri(ctx.Config);

    var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
    ctx.Config.Headers.Set("X-Api-Token", token);
    ctx.Config.Headers.Set("X-Timestamp", timestamp);
    ctx.Config.Headers.Set("X-Signature", Sign(ctx.Config.Method.Method, uri, timestamp, token, body)); // your signing function

    return ApiCredential.Custom();
});
```

The cost is three things you now own:

- **`transformRequest` runs after you**: the transform chain works on `config.Body`, so you sign the pre-transform body and send the post-transform one. Skip this path when the project registers a `transformRequest` (unwrapping / wrapping envelopes) and use the hook form above.
- **No replay on 401**: the library treats `Custom` as non-refreshable, so `invalidate:` only affects the next token fetch and this 401 still surfaces; to replay, catch it at the call site and resend.
- **Identity never reaches the cache key**: `Custom` writes no placement identity, so a new token still hits the same cache entry — configure `Caching.KeySelector` yourself.

## Caching

Both let a request "produce a response without being sent"; the difference is who decides: a short circuit is a response handed in by the caller, the cache is looked up by the library. Manual short circuit first, built-in cache second.

### Response cache

`c.Caching` is the built-in response cache: on a hit the request is **not sent** and the stored response is delivered instead (through the same path as a [short circuit](#short-circuit)). It also does **single flight** — concurrent requests with the same key are sent once and the rest wait for that one result.

```csharp
var cache = new AxiosCache(new AxiosCacheOptions
{
    TimeToLive = TimeSpan.FromSeconds(30),   // 30s by default
    SingleFlight = true,                     // true by default
    MaxEntries = 1024,                       // 1024 by default
    MaxBytes = 64L * 1024 * 1024,            // 64 MB by default, 0 = unlimited
    StoreTimeout = TimeSpan.FromSeconds(2),  // 2s by default, null = no limit (custom stores only)
    FreshnessMode = CacheFreshnessMode.Configured  // default: trust TimeToLive only; Server / Intersection also available
});

client.Defaults.Caching = cache;               // on instance defaults: applies to the whole instance

var user = await client.GetAsync<User>("users/1");    // sent and stored
var again = await client.GetAsync<User>("users/1");   // hit: nothing was sent
```

| Option | Default | Meaning |
| --- | --- | --- |
| `TimeToLive` | 30s | Entry lifetime, must be greater than zero |
| `FreshnessMode` | `Configured` | Where the lifetime comes from: `Configured` trusts your TTL; `Server` trusts the response; `Intersection` takes the smaller of the two |
| `SingleFlight` | `true` | Send once for concurrent requests with the same key (non-2xx responses are shared too, see below) |
| `MaxSharedFailureBodyBytes` | 256 KB | Byte cap for the non-2xx body shared inside a single flight; over the cap (or `0`) followers resend individually. Streaming responses are never shared |
| `ConditionalRequests` | `true` | Revalidate stale entries with `ETag` / `Last-Modified`; a 304 serves the cached body (see below) |
| `Store` | `null` | Entry storage; `null` = the built-in `MemoryCacheStore`. Implement `IAxiosCacheStore` for Redis or a shared cache |
| `MaxEntries` | 1024 | Entry cap of the built-in store: expired entries are purged first, then new entries are refused (ignored when `Store` is set) |
| `MaxBytes` | 64 MB | Approximate byte cap of the built-in store (body + header lengths, `0` = unlimited): same policy as the entry cap — an entry that does not fit is not accepted (ignored when `Store` is set) |
| `StoreTimeout` | 2s | Per-write timeout: a hanging store is treated as "not cached this time" instead of holding the response (`null` / non-positive = no limit; custom stores only; the wait is also capped by the remaining `Resilience.TotalTimeout`) |
| `KeySelector` | `null` | Custom key (gets the effective config and the final URL); defaults to `AxiosCache.CreateKey` |
| `ShouldCache` | built-in rule | Which requests take part; defaults to `AxiosCache.IsCacheable` (GET / HEAD, no short circuit, not streaming, no body, not a range request), and can be composed on top of it |

**Key = method + absolute URL (+ credential digest)**: credentials (an explicit `Authorization` header or the header value resolved from `Transport.Auth`) are part of the key, and only as a 64-bit digest — two users hitting the same endpoint on one instance never share an entry. Use `KeySelector` when identity lives in cookies or a custom header, or when you want per-tenant / per-language partitions.

**Two boundaries**: a query-string token such as `?token=` does not collide (the URL differs), but it enters the cache key **in plain text** (and the audit event URI), whereas `Authorization` enters only as a digest — keep sensitive tokens out of the query string, or have `KeySelector` contribute only a digest. Identity headers written in `OnSendingRequest` are **not part of the key** (the key is computed before that hook runs); write them in the per-request config or a request interceptor instead.

**What is stored is the raw body bytes**, re-parsed per call with the current `ResponseType` and generic argument: the same bytes feed `GetAsync<User>` and `GetAsync<string>` and each gets the shape it asked for.

**An entry shares the body with the response whenever it can**: for JSON / text shapes `Data` is a separate object, so the caller never holds that array and the entry points at the very same memory instead of cloning it.

The entry keeps its own copy when `Data` *is* the raw bytes (`byte[]` / `ArrayBuffer` shapes) or when a `transformResponse` is configured (it may wrap that array in an object of its own, so the library cannot prove the caller never got a reference) — otherwise one write by the caller would poison later hits. Cache hits likewise hand out an independent copy for `byte[]` / `Stream` shapes (an entry must never be mutated by one caller).

An entry is only stored when all of these hold: status is 2xx, the raw bytes were captured (not streaming), and `ShouldCache` passes. An empty body counts as raw bytes too (HEAD / 204 have no body — the headers are what gets cached). Streaming responses, a response interceptor that swapped the response object, and a failing store all just mean "not cached this time" — the request itself is unaffected. Range requests (a `Range` header) do not take part by default, and 206 / `Content-Range` responses are never stored — the key is only method + URL, so a partial body would be delivered as the whole document to a later plain GET.

**Streaming requests stay out of the cache**: a request with `ResponseType = Stream` skips even the lookup by default (the caller asked for a stream; a hit would buffer the whole body into memory). Entries already stored remain reachable from plain calls. Let `ShouldCache` admit streaming requests if that is really the ownership model you want.

**Three ways to pick the lifetime** (`FreshnessMode`):

| Mode | Store decision | Effective TTL |
| --- | --- | --- |
| `Configured` (default) | Client gate only; request and response HTTP directives are ignored; **headers are not parsed before storing** (parsed on demand only when the store reads the policy's HTTP fields) | `TimeToLive` |
| `Server` | `no-store` / `no-cache` / `Vary: *` / past or invalid `Expires` are not stored; **neither is a response that declares no freshness at all** | Server's remaining freshness (request `max-age` caps it), **not** capped by `TimeToLive` |
| `Intersection` | Same as above (both server and request vetoes win) | `min(TimeToLive, remaining freshness, request max-age)`; falls back to `TimeToLive` when no freshness is declared |

Remaining freshness = `max-age` (or `Expires - Date` when absent) minus the current age (the larger of the `Age` header and "received at − `Date`"). A `max-age` that is present but invalid or negative counts as zero (RFC 9111 §4.2.1: invalid freshness information is treated as stale) — `Expires` does not rescue it. Neither mode attempts a `Last-Modified` heuristic.

`Server` / `Intersection` also parse the **request's** `Cache-Control` / `Pragma` and intersect them with the response side:

| Request directive | Lookup | Store |
| --- | --- | --- |
| `no-store` | skipped | not stored |
| `no-cache` (or legacy `Pragma: no-cache`) | skips the fresh lookup but revalidates with the stale entry (a 304 serves the cached body) | still stored |
| `max-age=0` (invalid or negative values count as zero) | skipped | not stored |
| `max-age=N` (positive) | as usual | stored TTL capped at N |

`Configured` means "execute the configuration": neither request nor response HTTP directives take part.

**Conditional requests (`ETag` / 304 reuse)** are on by default (`ConditionalRequests = true`): a fresh hit still sends nothing; only when there is no fresh entry but the store still holds a stale one with validators is the validator written into a conditional header and the request sent once more:

- **`ETag` wins**, sent as `If-None-Match`; only without an `ETag` is `Last-Modified` sent as `If-Modified-Since`; with neither, the request is a full one as usual.
- **What a 304 delivers**: status and body come from the cached entry, headers are merged from the 304 (except `Content-Length` / `Transfer-Encoding` / `Content-Range` — a 304 has no body; repeatable headers such as `Set-Cookie` are exposed as separate values instead of being folded), and the entry is **stored again with a refreshed TTL** (when this request is allowed to store at all), so the next call hits without sending. `response.Request` is not `null` (which tells it apart from "hit, nothing sent").
- **Caller-supplied conditional headers win**: the library neither overrides them nor takes over the resulting 304 (that stays an ordinary non-2xx response for `ValidateStatus`).
- **The store has to cooperate**: `MemoryCacheStore` retains stale entries that carry validators (stale entries without validators are still removed on read and their byte quota refunded). A custom store implements `IAxiosCacheRevalidationStore.GetRevalidationAsync` to join in; a store that only implements `IAxiosCacheStore` cannot hand back the old entry and falls back to full downloads — the request itself is unaffected.
- **Request directives still apply**: `no-store` neither reads nor writes; `no-cache` / `max-age=0` skip the fresh lookup but revalidate with the stale entry (RFC 9111 §5.2.1.4) and still deliver the cached body on a 304.
- **Telemetry**: a 304 reuse increments `cache.revalidations` (untagged) and sets `axiosnet.cache.revalidated=true` on the span; it counts as a miss for the hit rate (the request really went out), and this counter is what shows how much bandwidth was saved.

The library interprets responses as a **private cache**: `private` / `public` do not change the decision, `s-maxage` is not used, `Vary` does not take part in the key (use `KeySelector` for partitioning; `Vary: *` is not stored in the parsing modes); no `stale-*` serving such as `stale-while-revalidate`. None of that information is lost — it is all handed to the store in `AxiosCacheEntryPolicy`.

| | Short circuit | Response cache |
| --- | --- | --- |
| Who decides "do not send" | The caller hands over a response | The library looks the key up |
| Scope | That one request only | Instance-wide, reused across requests |
| On a hit | Deserialization / `transformResponse` / `ValidateStatus` / response interceptors still run | Same |
| Concurrency gate / telemetry span | Not involved | Not involved |

**Hits and storing**

- **A hit sends nothing**: no concurrency slot, no telemetry span, no in-flight gauge, and `response.Request` is `null` (the same visible trace as a short circuit).
- **Headers keep all values**: duplicate response headers (e.g. multiple `WWW-Authenticate` challenges) are stored and replayed verbatim; the `Headers` single-value view keeps the first value for custom stores that read it per contract. `Set-Cookie` / `Set-Cookie2` are never stored — session state belongs to that one response, and replaying it on a hit would hand it to the next request.
- **The key does not include the body**: by default only bodiless GET / HEAD requests are cached; if `ShouldCache` lets a method with a body through, fold a body fingerprint into `KeySelector`, otherwise different bodies share one entry.
- **Policy lives on the instance**: one `AxiosCache` is one policy; create several and attach them separately when endpoints need different lifetimes or keys.

**Single flight and budget**

- **HTTP-status failures share the response**: when the leader gets a non-2xx, the flight hands its status / headers / raw body to every follower; each follower applies its own `ValidateStatus`, response interceptors and error factory (WebAPI `ErrorFactory`). `Response` and `Config` belong to each caller and the server sees exactly one request.
- **Sharing boundaries**: `ResponseType.Stream` (read-once) is never shared; a body over `MaxSharedFailureBodyBytes` (256 KB by default) or setting the option to `0` falls back to individual resends. Transient memory is roughly "error body × in-flight followers". The shared reply lives only in this flight and never enters `IAxiosCacheStore` (the cache contract does not store non-2xx), so store implementers need no changes.
- **Transport / cancellation failures are still broadcast**: waiters get the same `AxiosException`. But a leader cancelled by **its own caller** does not count as a failure — each waiter sends its own request instead.
- **Waiters stay cancellable**: cancelling `CancellationToken` gives up the wait only and never cancels the shared send (`ERR_CANCELED`).
- **Waiting is bounded by the total budget**: a follower waits at most the remaining `Resilience.TotalTimeout` and then fails with `ECONNABORTED` (`IsTotalTimeoutExceeded`) instead of hanging on a slow leader.
- **Lookups count against that budget too**: a cache read is bounded by the remaining `Resilience.TotalTimeout`, so a hanging store (unreachable custom store, network partition) fails with `ECONNABORTED` (`IsTotalTimeoutExceeded`) instead of parking the request; caller cancellation surfaces as `ERR_CANCELED`, and a cancellation thrown by the store itself counts as a miss. Implementations of `IAxiosCacheStore.GetAsync` should honour the token they receive.
- **Writes count against it as well**: a store write is bounded by whichever is shorter, `StoreTimeout` or the remaining `Resilience.TotalTimeout`; running past it means "not cached this time" (followers send their own request) and never holds an already-received response past the budget. If the budget is gone before storing, even the body copy is skipped.

**Storage and operations**

- **Force clearing**: `cache.ClearAsync()` drops every entry and `cache.RemoveAsync(key)` drops one (compute the key with `cache.KeyFor(config, uri)`); requests already in flight are unaffected and still store on completion.
- **Custom stores receive the full policy**: `IAxiosCacheStore` has a new `SetAsync` overload that takes an `AxiosCacheEntryPolicy` (effective / configured TTL, HTTP freshness, age, parsed `Cache-Control`, `Date` / `Expires` / `Vary`).
  - Its default implementation forwards to the old signature, so existing stores need no change; override it to reject `private` responses from a shared cache, partition by `Vary`, or extend the servable window with `stale-while-revalidate`.
  - In `Configured` mode nothing is parsed before storing: those HTTP fields are parsed on demand the first time the store reads them, so a store that only uses the TTL pays nothing.
- **Storage is pluggable**: `AxiosCache` stores nothing itself — every read and write goes through `IAxiosCacheStore` (`GetAsync` / `SetAsync` ×2 / `RemoveAsync` / `ClearAsync`; an `AxiosShortCircuit` is just status + headers + raw bytes, so it serializes to any backend).
  - The default is the in-process `MemoryCacheStore`; implement the interface for Redis or a cache shared across instances, and attach the same store to several clients to share entries.
  - A failing store only means "not cached this time" — requests still go out; a read that hangs past the remaining `Resilience.TotalTimeout` fails with `ECONNABORTED` instead (see above).
- **Namespace is `AxiosNet.Cache`** (same as `Retry/` and `Circuit/`): `AxiosCache` / `AxiosCacheOptions` / `CacheFreshnessMode` / `AxiosCacheEntryPolicy` / `CacheControlDirectives` / `IAxiosCacheStore` / `IAxiosCacheRevalidationStore` / `MemoryCacheStore` live there.
- `AxiosShortCircuit` stays in `AxiosNet` — it is not cache-specific, mocks and replays use it too.
- Replaying `response.Config` carries the same cache object along (it is shared state, not "the result of that request").

## Runtime and AOT

The library targets `net6.0` and `net8.0`: .NET 8 and later consume `lib/net8.0`, .NET 6 / 7 consume `lib/net6.0`, with no configuration. Both assets come from the same sources and behave the same at runtime (each binds to the BCL of the runtime it runs on); the `net8.0` target exists so the library can use net8-era BCL APIs and the net7+ analyzers. .NET Framework and .NET Core 3.1 or earlier are not supported.

`IsTrimmable` is declared on both targets (the library trims with 0 warnings), and the `net8.0` target also sets `IsAotCompatible` — the AOT analyzer reports 0 warnings (the SDK does not offer that analyzer for a `net6.0` target).

### Request models and interfaces: built-in source generator

The package ships a C# source generator (packed under `analyzers/dotnet/cs`, active as soon as the package is referenced). At compile time it bakes a metadata declaration for every request model that carries a route attribute, and emits real proxy classes for interface-based declarations (inherited interfaces, authorization, multipart, streaming, method-level encoding), registering both from a module initializer. It replaces the old "root the model assembly" approach:

- **Zero configuration**: no `TrimmerRootAssembly`, no hand-written registration;
- **One declaration for both paths**: the generator only writes facts (route / members / bindings / authorization); validation and the binding plan still run through the shared runtime builder, so JIT and AOT behave the same and produce the same error text;
- **No runtime reflection over models or interfaces**: request models read members through direct getter delegates; interface proxies only "gather arguments → call the shared binding implementation" and never use `MakeGenericMethod`; under AOT a registry miss throws `AxiosAotMetadataException` (with repair guidance) instead of silently dropping fields;
- **JIT-only reflection is trimmed away**: the reflection readers for interface declarations, request models, custom dictionaries and object shapes, plus the `DispatchProxy` fallback (collected in `Reflection/` and `WebAPI/Reflection/`), live only inside branches guarded by `RuntimeFeature.IsDynamicCodeSupported`; ILC folds that to the constant `false`, so the whole block becomes unreachable and is trimmed together with its types — package consumers do not pay for code that only ever runs on the JIT;
- **Where interface proxies are emitted**: non-generic interfaces at their declaration; closed generic interfaces (`IRepo<User>`) at the `Create<T>()` / `AddApiInterface<T>()` / `ValidateInterfaces(typeof(...))` call site, while an open generic declaration itself (including a nested interface whose outer type is open generic) only reports `AXA011` (suppressed when the same compilation contains a closed call site). When a referenced assembly has already generated a proxy for the same interface, the consumer recognises it by the generated class name and reuses that registration instead of emitting a duplicate (a duplicate registration would throw at startup); only when the referenced assembly did not run the generator does the consumer generate at the call site;
- **Fallback path**: without generated metadata JIT falls back to `DispatchProxy`; Native AOT has no fallback and throws `AxiosAotMetadataException`;
- **Prerequisite**: consumers need C# 9 or later (`[ModuleInitializer]`); the generator itself targets `netstandard2.0` and ships with the package (built against Roslyn 4.0.1, so any compiler from .NET SDK 6.0.100 / VS 2022 17.0 onward can load it);
- **Custom route attributes**: a custom subclass of `HttpApiAttribute` cannot have its constructor executed at compile time → warning `AXA010` and generation is skipped; JIT still uses reflection, and under AOT use `[HttpApi("METHOD", "path")]` or the built-in `[Get]` / `[Post]` etc. instead;
- **Disabling the generator**: `<AxiosNetSourceGen>false</AxiosNetSourceGen>`. JIT falls back to reflection / `DispatchProxy` automatically; when `PublishAot` is true it raises warning `AXA012`.

### Object shapes: flattening for params / forms / multipart

`params`, `form-urlencoded` and `multipart` all need to expand an object into "key → value" pairs. JIT builds the member list through reflection; with the generator enabled the shape is baked at compile time and registered, so AOT needs no reflection:

- **Automatic discovery**: `[AxiosShape]` types, route request models, interface method parameter types, Axios call-site arguments (including `PostFormAsync` data), `Params` / `Data` assignments and object initializers, and anonymous object creations; nested objects, collection elements and dictionary values are expanded recursively;
- **Named types**: registered with `default(T)` as the type-parameter sample, and getters are property accesses baked at compile time; only public getters are included (both the generated and the reflection path skip non-public getters), ref struct / pointer members cannot be boxed and are skipped on both paths, and `[JsonPropertyName]` / `[JsonIgnore]`, base-class `new` hiding and `override` behave exactly like the reflection path, member by member;
- **Anonymous types**: a structurally identical sample is generated from "property name / type / order" (`RegisterGeneratedShapeFromSample`), so the anonymous type argument is inferred; arrays of anonymous elements are written as `new[] { ... }` for the compiler to infer, and structurally different anonymous types never match by accident;
- **Custom read-only dictionaries**: only an enumeration accessor is generated (no POCO shape is registered), and `params` / form / `multipart` all flatten through it; when the declaration is an interface type the accessor is registered under the interface and resolved through the concrete type's interfaces at runtime; `null` keys from the accessor are skipped while flattening, and the ownership / retry traversal stops descending instead of throwing when no accessor exists under AOT;
- **Missing shapes fail explicitly**: under AOT a runtime type without a shape throws `AxiosAotMetadataException` whose message carries the type and field path (e.g. `Payload`), never silently dropping fields. Positions statically typed as `object` / an interface / a generic parameter cannot be inferred; annotate the concrete type with `[AxiosShape]`, or fall back to `AxiosMetadata.RegisterShape<T>()` / `RegisterShapeFromSample(...)` / `RegisterMap(typeof(T), ...)`;
- **Invisible types**: private nested / file-local / cross-assembly internal types cannot be referenced by generated code; they are skipped with warning `AXA013` (using them under AOT fails explicitly).

| Diagnostic | Severity | Scenario |
| --- | --- | --- |
| `AXA001` | Error | Interface member that cannot be proxied: property / event / static member |
| `AXA002` | Error | Unsupported interface method shape: generic method, `ref` / `out` / `in`, ref struct / pointer parameter, non-public abstract member, `CancellationToken` with binding attributes |
| `AXA003` | Error | Exactly one route attribute is required (models and interface methods share it): more than one, or an interface method without a route |
| `AXA004` | Error | Unsupported interface method return shape (synchronous return / `ValueTask` etc.) |
| `AXA005` | Error | Invalid `[Stream]` declaration on an interface method or an undefined frame format value |
| `AXA006` | Error | `[ApiIgnore]` combined with Path / Query / Header / HeaderCollection / Part / Body |
| `AXA007` | Error | Route attribute present but its arguments are blank or not compile-time constant (separate from `AXA003`: different fix) |
| `AXA008` | Error | `[Authorize]` and `[AllowAnonymous]` on the same level (model, interface or method) |
| `AXA009` | Error | Model / interface cannot generate metadata: static class / inaccessible / generic / ref struct / file-local / non-public member getter / invisible type |
| `AXA010` | Warning | Model / interface uses a custom `HttpApiAttribute` subclass (generation skipped) |
| `AXA011` | Warning | Open generic interface (including a nested interface whose outer type is open generic) without a closed call site (`Create` fails explicitly under AOT) |
| `AXA012` | Warning | `AxiosNetSourceGen=false` with `PublishAot` enabled |
| `AXA013` | Warning | Shape type is not accessible to generated code (shape skipped; flattening to it fails explicitly under AOT) |
| `AXA014` | Error | Invalid encoding declaration on an interface method: blank / control-character `[BodyContentType]`, undefined `[QueryCollectionFormat]` value, or combined with `[Multipart]` |
| `AXA015` | Error | Invalid `[Format]` declaration: blank format string, or a formatter that does not exist / is not `public static` / does not take `(object?, ValueTextChannel)` / does not return `string` |

`System.Text.Json` metadata still has to be registered on demand (source-generated `JsonSerializerContext`):

```csharp
[JsonSerializable(typeof(Person))]
internal partial class ApiJsonContext : JsonSerializerContext { }

var client = new AxiosClient(new AxiosConfig
{
    Serialization = new()
    {
        JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            TypeInfoResolver = ApiJsonContext.Default
        }
    }
});
```

`TypeInfoResolver` is a .NET 7+ property: the `net6.0` asset (taken by .NET 6 / 7 apps) has no such property on `JsonSerializerOptions`, so that asset cannot consume source-generated metadata — under AOT use a custom `IContentSerializer`, or move the target framework to `net8.0`. When reflection is switched off both assets throw the same `NotSupportedException`, and the `net6.0` message calls this out explicitly.

Do not drop `JsonSerializerDefaults.Web`: a bare `new JsonSerializerOptions()` switches property names to their declared casing, so request and response fields change shape, and the `JsonSourceGenerationOptions` on your context will not put camelCase back (`AxiosJson.Options` defaults to Web).

The `[Format]` JSON bridge (`AxiosJsonFormat.ApplyTo`) is unavailable for the same reason: it builds generic `JsonConverter` instances per property type and reads property attributes, so it carries `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]`. Under AOT, `[Format]` only affects the channels where the library itself produces text (path / query / header / form / multipart / raw scalar body); for JSON document internals use a custom `JsonConverter` or a dedicated DTO.

Missing metadata under AOT never silently drops fields; it throws an exception with repair guidance:

- request models and interfaces → `AxiosAotMetadataException` (the message names the type and the fixes; the hidden `AxiosApiMetadata.RegisterRequestModel` / `RegisterInterface` APIs can register declarations manually);
- `System.Text.Json` and an unknown type → `NotSupportedException`, telling you to add the type to a `JsonSerializerContext` and set `TypeInfoResolver`; anonymous types cannot be listed in `[JsonSerializable]`, so the message points at named types or params / form instead;
- `XmlContentSerializer` and `AxiosDynamic`'s dynamic member syntax → `PlatformNotSupportedException` (index access and the `IDictionary` interface on `AxiosDynamic` still work).

Not available under AOT:

- dynamic member syntax (`data.name`) and runtime-determined types;
- `XmlContentSerializer`;
- assembly scanning: `ValidateModels(Assembly)` / `ValidateInterfaces(Assembly)` rely on reflection enumeration and throw `PlatformNotSupportedException`. Use the generated `AxiosNet.Generated.AxiosNetValidation_<AssemblyName>.ValidateAll()` (it covers every request model and interface the generator produced metadata for in this assembly), or call `ValidateModels(typeof(A), ...)` / `ValidateInterfaces(typeof(IA), ...)` by type;
- the interface-proxy fallback path: the `DispatchProxy` used when nothing was generated / registered relies on Reflection.Emit and is JIT-only.

The generated class name replaces every non-identifier character of the assembly name with `_` (`AxiosNet.Aot` → `AxiosNetValidation_AxiosNet_Aot`), and the class is emitted only when the assembly has at least one emittable model / interface. Anonymous objects as params / form values need no extra configuration: the generator bakes a sample for their structure (see "Object shapes" above).

Container registration and freezing are both optional hardening.

When `AxiosNet.DependencyInjection` is referenced, the generator additionally emits `AxiosNetGeneratedDependencyInjection.AddAxiosGeneratedApiInterfaces(services[, siteName])`, which registers every routed interface in the assembly as a singleton. It calls the public `AddApiInterface<T>()` one type at a time, so site-conflict detection, named sites and singleton semantics stay the same single implementation — the difference is that types are bound at compile time, so renaming an interface breaks the build instead of quietly leaving a service unregistered at runtime.

To lock metadata down at the end of startup, call the hidden `AxiosMetadata.Freeze()`: any real write afterwards (a later-loaded assembly's module initializer, a manual registration) throws with the type and assembly named, while equivalent duplicate registrations stay no-ops. Not calling it changes nothing.

**Known limitation**: keeping the first registration for equivalent duplicates is designed for "several mutually unreferenced assemblies each generate a proxy for the same interface". Inside a collectible `AssemblyLoadContext` (plugin hot reload), the second registration is silently dropped and the proxy factory still points at the assembly loaded by the first one — do not rely on the registry refreshing itself when a plugin is reloaded; use a separate process, or cross a fresh `AssemblyLoadContext` boundary after unloading.

## Reference

> Configuration renames and behavior tightening when upgrading from 1.x to 1.5: see [1.5 breaking changes](docs/breaking-changes-1.5.en.md).

### Capability map

Four groups; click a capability to jump to its section. Boundaries and edge cases live in the sections themselves.

**Requests and responses**

| Capability | What you get | How |
| --- | --- | --- |
| [Four response shapes](#quick-start) | Raw text / `JsonElement` / `dynamic` / strong type | `GetAsync<string>` / `GetAsync()` / `GetAsync<dynamic>` / `GetAsync<T>` |
| [Declarative API](#declarative-client) | Describe requests with models or interfaces; `ApiClient` makes the call — or generate it from OpenAPI | `[Get("users/{id}")] class X : IApiRequest<User>` / `client.Create<T>()` / `axiosnet-gen` ([OpenAPI import](#openapi-import)) |
| [Client instance](#clients) | One baseURL + timeout + default headers | `Axios.Create(c => ...)` / `new AxiosClient(config)` |
| [URL only](#methods-and-urls) | Build the final URL without sending | `client.GetUri(c => ...)` |
| [Query parameters](#query-parameters) | Object / dictionary / collection / pre-encoded string | `c.Params = new { page = 2 }` |
| [Request body](#body-and-contenttype-json-default) | Encoding decided by `ContentType` alone, never guessed | `c.ContentType = AxiosContentType.FormUrlEncoded` |
| [Forms and uploads](#forms-and-uploads) | Multipart fields + files, or a raw stream | `PostFormAsync(...)` / `FormFile` / `Stream` |
| [Streaming request body](#streaming-request-bodies) | Async sequence produced on the fly, never buffered whole | `data = IAsyncEnumerable<byte[]>` (or `ReadOnlyMemory<byte>` chunks) |
| [Response types](#response-types-and-downloads) | JSON / text / bytes / stream / dynamic | `c.ResponseType = ResponseType.Stream` |
| [Streaming responses](#streaming-responses) | NDJSON / SSE / JSON array → `IAsyncEnumerable<T>` | `client.RequestStreamAsync<T>(req, AxiosStreamFormat.ServerSentEvents)` |
| [Timeouts and cancellation](#timeouts-and-cancellation) | Per-attempt budget, whole-request budget, `CancellationToken` | `c.Timeout = ...` / `c.Resilience.TotalTimeout = ...` / `cancellationToken:` |
| [Credentials and token management](#credentials-and-token-management) | One credential extension point with placements (`Authorization` header / any header / query parameter), token cache, refresh-ahead, single-flight refresh, 401 replay | `ApiClient.CredentialProvider` / `AccessTokenManager` |

**Traffic control**

| Capability | What you get | How |
| --- | --- | --- |
| [Progress and throttling](#progress-rate-and-size-limits) | Upload/download progress, byte rate cap, size caps | `c.Observability.OnDownloadProgress` / `c.Transport.MaxRate` |
| [In-flight gate](#in-flight-concurrency-cap) | Cap concurrent requests; queue depth can be capped | `c.Resilience.Concurrency.MaxInFlight` + `MaxQueue` / `QueueTimeout` |
| [Retries](#retries) | Exponential backoff / jitter / `Retry-After` / idempotency gate / global retry budget | `c.Resilience.Retry.Policy = RetryPolicy.Exponential(...)` |
| [Circuit breaker](#circuit-breaker) | Per-host Closed/Open/HalfOpen state machine: rolling-window failure rate plus a consecutive-failure rule, with slow-start recovery | `c.Resilience.Circuit.Policy = new CircuitBreaker()` |
| [Concurrent hedging](#concurrent-hedging) | Fire several copies of one request, first success wins | `c.Resilience.Hedging.MaxHedges = 1` |
| [Backup endpoints](#backup-endpoints) | Resend against a mirror when the current endpoint is unusable; failing endpoints cool down across requests so a dead primary is not hit first on every request | `c.Resilience.Failover.Endpoints = new[] { new Uri("https://mirror") }` |
| [Idempotency keys](#idempotency-keys) | One key per logical request, shared by retries / endpoints / hedges | `c.Resilience.Idempotency.Enabled = true` |
| [Attempt pipeline](#attempt-pipeline) | One attempt = a composable middleware chain, pluggable and reorderable | `Axios.Create(..., pipeline: AxiosAttemptPipeline.Default.Append(...))` |
| [Short circuit](#short-circuit) | Deliver a response without sending (cache / mock / replay) | `c.ShortCircuit = new AxiosShortCircuit { ... }` |
| [Response cache](#response-cache) | Cache 2xx responses by URL and identity; a hit sends nothing, concurrent requests share one send; stale entries revalidate via `ETag` / 304 | `client.Defaults.Caching = new AxiosCache(...)` |

**Data and errors**

| Capability | What you get | How |
| --- | --- | --- |
| [Unified errors](#errors) | Every failure is an `AxiosException` with a stable `Code` | `catch (AxiosException ex) { ex.Code }` |
| [Interceptors](#interceptors) | Request and response, sync or async | `client.Interceptors.Request.Use(...)` |
| [Data transforms](#data-transforms) | The two axios-style transform chains | `c.TransformRequest.Add(...)` |
| [Swappable serialization](#serialization) | JSON (default) + XML built in, any format pluggable | `AxiosContent.Register(...)` / `Serialization.BodySerializers` |
| [Request body compression](#request-body-compression) | gzip / deflate / br, `Content-Encoding` written for you | `c.Compression = AxiosCompression.Gzip` |
| [Observability](#observability) | `ActivitySource` + `Meter` + `DiagnosticListener` | `AddSource("AxiosNet")` / `AddMeter("AxiosNet")` |

**Transport and engineering**

| Capability | What you get | How |
| --- | --- | --- |
| [Connection pool](#connection-pool) | One tuned handler per origin, shared keep-alive connections | Built in; share via `new HttpConnectionPool(...)` or `HttpConnectionPool.GetOrCreate("key")` |
| [HTTP versions](#http-versions) | HTTP/2 by default with downgrade, HTTP/3 supported | `c.Transport.HttpVersion` |
| [Custom handlers](#custom-handlers) | Wrap a `DelegatingHandler`, or take over the transport | `ConnectionPoolOptions.HandlerFactory` |
| [Dependency injection](#dependency-injection) | Singleton registration, named multi-site resolution | `AddAxiosClient(...)` / `AddApiClient(...)` |
| [Native AOT](#runtime-and-aot) | Declared `IsTrimmable` (trims with 0 warnings); `net8.0` target also sets `IsAotCompatible` (AOT analyzer 0 warnings); built-in request-model / interface-proxy source generator, no `TrimmerRootAssembly` needed | On by reference; configure `JsonSerializerContext` for serialization |

### axios comparison

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
| `config.params` / `config.data` | `c.Params` / `c.Body` (encoding via `c.ContentType`) |
| `responseType: 'json' / 'text' / 'arraybuffer' / 'stream'` | `ResponseType.Json` / `Text` / `ArrayBuffer` / `Stream` |
| `validateStatus` / `timeout` / `AbortController` | `c.ValidateStatus` / `c.Timeout` / `c.CancellationToken` |
| `auth: { username, password }` | `c.Transport.Auth = AxiosCredential.Basic(user, pass)` |
| — (token refresh / 401 replay needs a hand-written interceptor) | Declarative `ApiClient.CredentialProvider` + `AccessTokenManager` (auto refresh / 401 replay) |
| `withCredentials` / `proxy` / `maxRedirects` | Same-named properties on `Transport` (instance-level) |
| `maxContentLength` / `maxBodyLength` / `maxRate` | `Limits.MaxResponseBytes` / `Limits.MaxRequestBytes` / `Transport.MaxRate` |
| `onUploadProgress` / `onDownloadProgress` / `timeoutMessage` | Same-named properties on `Observability` |
| `paramsSerializer` / `formSerializer` | `Serialization.QueryEncoder` / `Serialization.FormFieldName` |
| `transformRequest` / `transformResponse` | Same-named properties |
| `AxiosError.code` / `isAxiosError` / `isCancel` | `AxiosException.Code` / `Axios.IsAxiosError` / `Axios.IsCancel` |
| `axios-retry` plugin | `c.Resilience.Retry.Policy` / `c.Resilience.Retry.OnRetry` |
| — (needs opossum or similar) | `c.Resilience.Circuit.Policy` / `c.Resilience.Circuit.OnRejected` |
| — (needs cockatiel or similar) | `c.Resilience.Hedging.MaxHedges` (concurrent hedging), `c.Resilience.Failover.Endpoints` (backup endpoints) |
| — (needs the OTel JS SDK) | Built-in `ActivitySource` / `Meter` / `DiagnosticListener` |
| — (axios-cache-interceptor and friends) | `c.Caching = new AxiosCache(...)` (built-in cache + single flight), `c.ShortCircuit` (hand-rolled cache hit / mock / offline replay) |
| — (no equivalent) | `HttpConnectionPool`, declarative `ApiClient`, `RequestStreamAsync`, `Resilience.Concurrency.MaxInFlight` / `MaxQueue` / `QueueTimeout`, `IAsyncEnumerable` streaming request bodies |

Intentional differences from axios:

1. Method names carry the `Async` suffix; `timeout` is a `TimeSpan`; cancellation uses `CancellationToken`.
2. Body encoding is decided by `ContentType` (JSON by default), never inferred from the data type — a dictionary does not become a form on its own.
3. Non-generic text responses give `Data` as `JsonElement` (falling back to raw text on invalid JSON), binary as `byte[]`; use `GetAsync<dynamic>` for JS-style access.
4. Writing a **different** value for an instance-level option per request throws `ERR_BAD_OPTION_VALUE` (axios silently ignores it).
5. `params` uses strict `Uri.EscapeDataString` (space becomes `%20`, not `+`).
6. `transformRequest` / `transformResponse` elements are `object?` (C# cannot lose and then narrow types the way JS does).
7. Header writes are validated immediately (RFC 7230 token, no CR/LF) and throw `ArgumentException` (the same error inside declarative binding is wrapped as `ERR_BAD_REQUEST`, with the original exception in `Cause`).
8. `MaxRate` and progress callbacks are implemented at the `HttpContent` / `Stream` layer (BCL has no native support).
9. `Timeout` is capped at ~49.7 days, larger values clamp; a truly unbounded request uses `Timeout = Zero`.

### Dependency injection

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
    configure: client => client.CredentialProvider = CredentialProviders.Fixed(token));

builder.Services.AddApiClient(                               // declarative facade over the AxiosClient singleton
    configure: client => client.CredentialProvider = CredentialProviders.Fixed(token));

builder.Services.AddApiClient("a", "https://a.example.com", configure: c => c.CredentialProvider = providerA);   // multi-site
builder.Services.AddApiClient("b", "https://b.example.com", configure: c => c.CredentialProvider = providerB);
```

| Extension | Registers |
| --- | --- |
| `AddAxiosClient(configure, pipeline)` | Singleton `AxiosClient` (optional attempt pipeline) |
| `AddApiClient(baseAddress, configureDefaults, configure)` | Singleton `ApiClient` (owns its transport) |
| `AddApiClient(configure)` | Singleton `ApiClient` (reuses the container's `AxiosClient`) |
| `AddApiClient(name, baseAddress, ...)` | Singleton `IApiClientResolver` + named site |
| `AddApiInterface<T>()` / `AddApiInterface<T>(name)` | Singleton interface proxy (default / named site) |

- TryAdd semantics: already registered means not registered twice, so no second pool is created.
- `IApiCredentialProvider` / `IApiErrorFactory` from the container attach automatically; explicit values in `configure` win.
- Lifetime belongs to the container: disposing an injected `ApiClient` does not dispose the shared underlying client.
- Multi-site via `IApiClientResolver.Get(name)` / `TryGet` / `Names`; on .NET 8+ keyed services (`AddKeyedSingleton` + `[FromKeyedServices]`) work too.
- Interface declarations register directly: `services.AddApiInterface<IUserApi>()` (unkeyed `ApiClient`) or `AddApiInterface<IUserApi>("a")` (named site); the proxy is reused as a singleton and comes from the source generator (same path for JIT and Native AOT). Without generated metadata it falls back to `DispatchProxy` under JIT, and under AOT resolution fails explicitly.
- One interface binds to one site: registering it again under a different name (including unkeyed ↔ named) throws `InvalidOperationException` at registration time instead of silently dropping the second registration; to share a method set across two sites, derive an interface and register that against the other site.

### Samples

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) is an offline console sample with a built-in `TcpListener` demo server — **54 items**:

```bash
dotnet run --project samples/AxiosNet.Simple
```

It opens an interactive menu (reflection lists every sample; type a number to run, `a` for all, `q` to quit).

| Suite | Items | Coverage |
| --- | ---: | --- |
| Core | 37 | Static facade and `Axios.Reset`, instance creation, JSON / form / multipart, params, Basic auth, dynamic requests and responses, text / arraybuffer, interceptors, errors and cancellation, connection pool, upload and download, getUri and size limits, progress and rate limiting, custom handler, serializer replacement (Newtonsoft / XML / binary), DNS refresh, bearer token, request signing, resilient retries, global retry budget, idempotency keys, HTTP/2·3, observability, circuit breaker, short circuit, response cache, named shared pool, attempt pipeline, concurrent hedging and backup endpoints, queue cap and backpressure, transport profiles |
| Declarative | 17 | GET detail / list, POST create, the unified credential provider with `[AllowAnonymous]` / same-client token bootstrap, error handling, global logging, signing, streaming (SSE / JSON array), multipart upload, frame cap, interface declarations, interface authorization and validation, interface multipart, interface streaming, method-level encoding (form body / query collection format), value formatting (`[Format]` / `Serialization.ValueText` priority), OpenAPI import (`axiosnet-gen` emits request models by default: tag grouping / authorization / form / multipart / streaming) |

Two more samples:

- [`samples/AxiosNet.Simple.DependencyInjection`](samples/AxiosNet.Simple.DependencyInjection): six DI scenarios on the Generic Host.
- [`samples/AxiosNet.Aot`](samples/AxiosNet.Aot): Native AOT + trimming with a built-in loopback server.

<a id="native-aot"></a>

### Build and test

```bash
dotnet build AxiosNet.sln
dotnet test AxiosNet.sln
dotnet build AxiosNet.sln -c Release
```

| Test project | Cases | Frameworks | Executions |
| --- | ---: | --- | ---: |
| `AxiosNet.Tests` (core + declarative) | 2231 / 2220 | `net8.0` / `net6.0` | 4451 |
| `AxiosNet.DependencyInjection.Tests` | 66 | `net8.0` / `net6.0` | 132 |
| `AxiosNet.Cli.Tests` (OpenAPI generation) | 74 | `net8.0` | 74 |
| **Total** | **2371 / 2286** | | **4657** |

Each cell counts the cases run on that TFM from the same sources — the two sides of the slash are not "total / passed".

- `net8.0` has 11 more cases than `net6.0`: the difference comes from cases that only compile on the net8 branch — `AotJsonTests` and the JSON bridge in `FormatAttributeTests` (`JsonSerializerOptions.TypeInfoResolver` / `JsonTypeInfo.Properties` are .NET 7+ APIs).
- `net6.0` alone has the two `JsonSerializerTargetFrameworkParityTests.ReflectionDisabled_*` cases, which assert the error raised when no `TypeInfoResolver` exists.
- The `net6.0` column of the total row excludes `AxiosNet.Cli.Tests` (74 cases), which only runs on net8.
- `AxiosNet.Cli.Tests` runs locally only — CI's `TEST_PROJECTS` lists just the first two projects, so generator snapshots and CLI fixtures are not part of the gate.

Tests run real TCP round-trips against a home-grown `TcpListener` HTTP/1.1 keep-alive server, covering request encoding, interceptors and transforms, error codes, timeouts and cancellation, progress and rate limiting, pooling, transport profiles and protocol negotiation, streaming, retries, circuit breaking, concurrent hedging, backup endpoints and endpoint health memory, short circuits, response cache, telemetry, declarative binding, interface proxies, object shapes (generated vs reflected) and DI registration.

Line coverage **91.8%**, branch coverage **87.3%** (`AxiosNet.Tests` / net8.0, via `--collect:"XPlat Code Coverage"`, excluding the perf and allocation suites). A few rules:

- Now that the test project references the source generator, coverage includes both the `AxiosNet` and `AxiosNet.SourceGen` assemblies; repeated runs wobble by ±0.1.
- Compare history only against the same configuration — the command below is Debug; a Release build emits fewer sequence points, so the line denominator shrinks noticeably and the two are not interchangeable.
- The generator's DI registration extension is only driven by `AxiosNet.DependencyInjection.Tests` (the main test project does not reference the DI package), so those lines fall outside this measurement.
Collection must exclude two kinds of cases — throughput ratios and allocation guards both skew under instrumentation (the guards assert bytes allocated per request, and instrumentation itself allocates):

```bash
dotnet test test/AxiosNet.Tests/AxiosNet.Tests.csproj -f net8.0 --collect:"XPlat Code Coverage" \
  --filter "FullyQualifiedName!~ConcurrencyPerformanceTests&FullyQualifiedName!~AllocationSweepTests&FullyQualifiedName!~AllocationTests&FullyQualifiedName!~HeadersCopyOnWriteTests"
```

A plain `dotnet test` still runs everything, excluded classes included.

#### Runtime matrix

"Compile target" and "runtime" are two different dimensions, and they cover different things:

| Dimension | CI coverage | Why each entry must be exercised |
| --- | --- | --- |
| Compile target | the two `lib/` assets, `net6.0` and `net8.0` | the library source has nine `NET*_OR_GREATER` sites; the other side of each is not even compiled into IL under the other target |
| Runtime | .NET 6.0 and .NET 10.0 | the same `lib/net8.0` asset is loaded by .NET 8 / 9 / 10 / 11, each with a different BCL implementation |

The first dimension is covered by `build-test` (`dotnet test` without `-f`, so each TFM runs once); the second by `test-runtime` — no `TargetFrameworks` change, no extra TFM, no packaging change, just host roll-forward putting the `net8.0` test host on a newer runtime, which is exactly how a consumer on a newer runtime loads the `net8.0` asset:

```bash
DOTNET_ROLL_FORWARD=LatestMajor dotnet test <project> -f net8.0
```

It must be `LatestMajor`: `Major` prefers the latest patch within the same major (8.0.x), and only `LatestMajor` crosses majors. Assert the target runtime is installed first — when roll-forward finds nothing newer it falls back silently to net8, and the run still goes green while covering nothing.

Only .NET 10 is added, not 9 or 11: .NET 8 and 9 both go out of support on 2026-11-10, 10 is LTS (through 2028-11-14) and the only version currently in active support, and 11 has not shipped. The concurrency performance suite does not take part in this pass — it judges the ratio against `HttpClient`, and changing the runtime changes the baseline; performance on a newer runtime needs its own baseline.

#### Native AOT verification

A separate `aot` CI job (`win-x64` / `linux-x64` matrix) treats `IL2026` / `IL3050` / `IL3051` plus the DAM group (`IL2070` / `IL2072` / `IL2075` / `IL2087` / `IL2098`) as errors, so "publish succeeded" means both trimming and AOT analysis are warning-free. The DAM group has to be tightened together with the rest: the library leans heavily on `DynamicallyAccessedMembers` and suppression annotations, and that class of warning is the one most likely to surface only when a package consumer publishes.

```bash
# Sample: the app no longer roots the whole assembly — passing depends entirely on generated metadata
dotnet publish samples/AxiosNet.Aot -c Release -r linux-x64 -p:TrimmerSingleWarn=false \
  -p:WarningsAsErrors=IL2026%3BIL3050%3BIL3051%3BIL2070%3BIL2072%3BIL2075%3BIL2087%3BIL2098
./samples/AxiosNet.Aot/bin/Release/net8.0/linux-x64/publish/AxiosNet.Aot

# Package consumer: pack → throwaway project that only references the package → publish → run
bash eng/aot-package-smoke.sh
```

`eng/aot-package-smoke.sh` first asserts the nupkg carries `analyzers/dotnet/cs/AxiosNet.SourceGen.dll` and `buildTransitive/AxiosNet.props`, then publishes and runs a throwaway project that references nothing but the package.

This covers what the sample cannot: the sample uses a `ProjectReference`, so it never proves the packaged analyzer actually loads or that the packaged dll's AOT annotations hold up. The script packs under a unique version every run — NuGet will not re-extract a package whose version it already has, which would silently keep re-testing the previous build.

Size and startup are part of the budget: both the sample and the package smoke assert the published binary stays under budget — 9 MiB on win-x64 (currently 8.3 MiB for the sample, 8.1 MiB for the package smoke) and 11 MiB on linux-x64 (currently 10.2 MiB). The ~2 MiB platform gap is not a size regression: on Linux the OpenSSL, `System.Native`, compression and globalization shims are linked statically into the executable, while Windows uses system DLLs or pre-built `Aot.lib` files, so the budget is set per platform.

Exceeding it usually means one of two things: a reflection dependency crept back in, or the assembly is being rooted wholesale again (`TrimmerRootAssembly` / the generator is not actually running). Startup plus a full run is gated at 10 s, which only catches order-of-magnitude regressions and is not a performance verdict; use `test/AxiosNet.Benchmarks` for that.

The generated code has a snapshot baseline too (`test/AxiosNet.Tests/WebAPI/Aot/Snapshots/GeneratedSources.snap.txt`): any change in generator output shows up as a diff in that file — regenerate with `AXIOS_UPDATE_SNAPSHOTS=1` and review it line by line.

Known cost: an interface proxy still allocates one argument array per call, because the arguments are handed to the shared binder (`IValueSource.GetValue(object target)` reads them by position); the generator bakes declarations only and never duplicates the binding logic. Truly zero-allocation would require inlining the binding plan per method, which copies the validation and binding rules N times and conflicts with the single-build-entry design, so it is not done. What the proxy layer can avoid — the per-call read-only wrapper — is now created on demand.

#### Test suite organised by feature

The folder layout of `test/AxiosNet.Tests` is the feature list — pick the folder first, then the file:

| Folder | What it covers |
| --- | --- |
| `Client/` | Static facade `Axios`, instance options and defaults, default headers, config merging and the six config groups |
| `Requests/` | URLs and params, request bodies (JSON / form / multipart), response parsing and buffering, streaming, timeouts and cancellation, Content-Type parsing |
| `Governance/` | Concurrency gate, progress callbacks and rate limiting |
| `Resilience/` | Retry policies and timing, whole-request budget (`Resilience.TotalTimeout`), circuit-breaker state machine, short circuits |
| `Caching/` | Response cache and single flight, conditional requests (`ETag` / 304) and `IAxiosCacheRevalidationStore`, the `IAxiosCacheStore` contract and custom stores, key rules and identity isolation, the hit path |
| `Serialization/` | `IContentSerializer` contract and registry, JSON defaults |
| `Pipelines/` | Request / response interceptors, `transformRequest` / `transformResponse`, attempt pipeline (built-in order, insertion position, reorder validation), concurrent hedging, backup endpoints and endpoint health memory |
| `Connectivity/` | Connection pool (incl. named shared pools and named transport profiles), handler injection, HTTP/2·3 negotiation and downgrade, redirects, local h3 capability probe |
| `Diagnostics/` | Activity / Meter / DiagnosticListener telemetry, attempt audit events |
| `Errors/` | Error model, error codes, `ToJson` snapshot |
| `Regression/` | Allocation guards (isolated collection + precise counting; `AllocationTests` uses process-wide averages, `AllocationSweepTests` uses thread-local minima over several windows — when the measured path completes synchronously the true value is a constant and noise only adds), concurrent throughput |
| `WebAPI/` | Declarative model binding and validation, authorization, streaming, interface proxies and startup validation, the request-model source generator (field-by-field parity between generated and reflection paths) |
| `Architecture/` | Architecture guardrails: the core must not reference the `WebAPI/` extension (a source scan fails if "removable as a whole" is broken) |
| `Infrastructure/` | Test plumbing: HTTP server, models, telemetry probe, isolated collections |

Timing-sensitive cases (upper-bound duration assertions, telling "placeholder vs budget" apart) and allocation cases each run in isolation — see `Infrastructure/TimingCollection.cs` and `Infrastructure/AllocationCollection.cs`.

`AxiosNet.DependencyInjection.Tests` follows the same rule with two groups: `Registration/` (registration extensions and named clients) and `Lifecycle/` (concurrent resolution and holder disposal).

`AxiosNet.Cli.Tests` treats generated output as a compilable artifact: fixture snapshots (petstore / Swagger 2 / 3.1 / adversarial documents), compiling generated code together with the AxiosNet source generator and asserting one request model per operation (one proxy per interface under `--style interface`), determinism and `--check`, overwrite protection and manifest tracking, `--dry-run`, option parsing and the import report. The round trip from a generated client to a real server is covered by the `OpenApiGeneratedExample` sample (→ `DemoApiServer`).

#### Benchmarks

[`test/AxiosNet.Benchmarks`](test/AxiosNet.Benchmarks) is a standalone BenchmarkDotNet project (single target `net8.0`, `IsPackable=false`, not referenced by `src/`) that turns "did this change help?" into numbers. It is not part of `dotnet test` and CI does not run it (benchmarks are machine-sensitive).

**Division of labour with the concurrency correctness test**

- `AxiosNet.Tests/ConcurrencyPerformanceTests` judges **concurrency correctness** and stops collapses: the best of 5 rounds must reach the 85% capability line (Debug 70%), and the median must stay above the 75% floor (Debug 60%) to catch overall regressions.
- Use the benchmarks to quantify "how much faster": they give per-item cost; the correctness test only answers "did it fall below usable?".
- It judges a paired ratio, so both sides' compilation state has to be comparable: `HttpClient` ships ReadyToRun and is optimized from the first call, while this library is plain IL that starts in Tier0 and waits for tiering to promote it to Tier1 + PGO. That is why CI gives it a **dedicated process placed before the heavy suite**, which filters it out and then runs per TFM, one after the other.

**Why warmup converges on the ratio**

- The compilation ramp advances by wall clock, so a slower and busier runner gets less of it done inside a fixed warmup — the rounds end up measuring compilation progress, not code.
- The warmup therefore uses no fixed number of seconds: one-second windows, at least 4 and at most 10, and judging starts only once the last 3 windows' ratios span 3 percentage points or less. Measured: the first 4 windows can sit flat at 82–87% and only jump to ~95% at window 5–6; after convergence the judged rounds sit at 93.5–96.2%, whereas the fixed 2-second warmup control run started at 84.1% in round 1.
- Tiered compilation stays on by default (real-world steady state relies on PGO; no special configuration).

**When the ratio misses, look at the CPU share first**

- When the ratio misses the line, first check the **CPU share the process actually got** (`Process.TotalProcessorTime` ÷ wall clock ÷ core count, median over the judged rounds). On one machine with one build, the ratio stayed at 77–87% while the process got 50–56% of the CPU, but swung between 54% and 90% once it was squeezed down to 30–32%. The threshold is therefore 42%; below it the numbers are printed but not asserted — a passing run needs no explanation, so the threshold only matters on a miss.

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
| `LargeBodyUploadBenchmarks` | Large uploads (1 / 8 / 32MB: `byte[]` / `ReadOnlyMemory` / stream / async sequence, real loopback socket) | `HttpClient_Upload_ByteArray` |
| `LargeBodyDownloadBenchmarks` | Large downloads (1 / 8 / 32MB: buffered `byte[]` vs streamed) | `HttpClient_Download_Buffered` |
| `LargeBodyJsonBenchmarks` | Large JSON responses (1 / 8 / 32MB: `byte[]` / typed / `JsonElement`) | `HttpClient_GetBytes` |

Readings, before/after workflow and known noise: [`test/AxiosNet.Benchmarks/README.md`](test/AxiosNet.Benchmarks/README.md).

For a cross-implementation comparison see [`test/AxiosNet.Comparison`](test/AxiosNet.Comparison):

- One loopback server, one load profile (8 concurrent × 1s × 10 rounds by default, with the implementation order rotated every round), comparing plain `HttpClient` / Refit / RestSharp / Flurl / AxiosNet on RPS, p50 / p99 / max latency and per-request allocations, printed as a console table and exported to Markdown / CSV.
- Add `--min-ratio` / `--max-alloc-ratio` to use it as a regression gate; without thresholds it only reports, never judges.
- It is not part of CI and is not collected by `dotnet test` (`IsTestProject=false`; it sits in the solution only so the IDE can open it).
- Methodology, reading guide and one sample run live in that project's README; the write-up with conclusions (features / performance / ergonomics plus selection advice) is [`docs/http-client-comparison.md`](docs/http-client-comparison.md).

### Project layout

```
.
├─ src/
│  ├─ AxiosNet/                       # library (dual target net6.0 / net8.0)
│  │  ├─ Axios.cs                     # static facade
│  │  ├─ AxiosClient*.cs              # instance + request pipeline + shortcuts
│  │  ├─ AxiosConfig*.cs              # configuration (top level + six groups)
│  │  ├─ AxiosHeaders.cs              # request headers
│  │  ├─ AxiosResponse.cs / AxiosException.cs / AxiosErrorCode.cs / AxiosShortCircuit.cs
│  │  ├─ AxiosDynamic.cs / AxiosContent.cs / AxiosJson.cs / FormFile.cs (top-level public types)
│  │  ├─ Cache/                       # AxiosCache (response cache + single flight) / IAxiosCacheStore / MemoryCacheStore
│  │  ├─ Interceptors/                # request / response interceptors
│  │  ├─ Pipeline/                    # attempt middleware chain: AxiosAttemptPipeline + built-in retry / failover / breaker / hedging / gate + optional audit
│  │  ├─ Retry/                       # IAxiosRetryPolicy / RetryPolicy / RetryBudgetPolicy
│  │  ├─ Circuit/                     # IAxiosCircuitPolicy / CircuitBreaker
│  │  ├─ Serialization/               # IContentSerializer + JSON / XML implementations
│  │  ├─ Telemetry/                   # ActivitySource / Meter / DiagnosticListener
│  │  ├─ Pooling/                     # HttpConnectionPool + options / statistics + handler injection
│  │  ├─ Metadata/                    # metadata registration entry points (object shapes / dictionary maps / freeze) + shape builders
│  │  ├─ WebAPI/                      # declarative API client (optional extension, removable as a whole; Attributes/ = public attributes, Metadata/ = public registration entry points, Internal/ = shared contract, Reflection/ = JIT-only)
│  │  ├─ Reflection/                  # object-shape and STJ reflection fallbacks (JIT-only, trimmed away entirely under AOT)
│  │  └─ Internal/                    # URL, body encoding, response reading, traffic metering
│  ├─ AxiosNet.SourceGen/             # source generator (netstandard2.0 analyzer): the build-time mirror of Reflection/
│  ├─ AxiosNet.DependencyInjection/   # DI extensions (separate package)
│  └─ AxiosNet.Cli/                   # design-time tool axiosnet-gen: OpenAPI / Swagger → declarative C# (see "OpenAPI import")
├─ samples/                           # Simple (54 items) / DI / Aot
├─ eng/                               # CI scripts (AOT package-consumer smoke, see "Native AOT verification")
└─ test/
   ├─ AxiosNet.Tests/                  # core + declarative (folders = feature groups, see "Build and test")
   ├─ AxiosNet.DependencyInjection.Tests/
   ├─ AxiosNet.Cli.Tests/              # CLI: fixture snapshots / generated-code compilation / determinism and --check / overwrite protection and manifest tracking / --dry-run
   ├─ AxiosNet.Benchmarks/             # BenchmarkDotNet (per-stage cost of one implementation)
   └─ AxiosNet.Comparison/             # cross-implementation comparison (RPS / p99 / allocations, optional gate)
```

Three folder rules:

- Feature directories (`Metadata/`, `Serialization/`, `Internal/`) split by concern;
- **JIT-only reflection implementations always live under a `Reflection/` folder, of which there are exactly two**: the root `Reflection/` holds the cross-feature building blocks (object-shape reflection, the STJ reflection fallback), while `WebAPI/Reflection/` holds the reflection readers and `DispatchProxy` fallback that belong to the declarative API feature (code outside a `Reflection/` folder does not touch reflection directly);
- **`WebAPI/` is an extension that can be deleted as a whole**: remove it and the core still compiles and plain HTTP still works, so the core must never reference it back (object shapes, dictionary maps, the generated-metadata registry and the AOT guards/exception therefore live in `Metadata/` and `Internal/`).

Public attributes all live under `WebAPI/Attributes/` (namespace `AxiosNet.WebAPI.Attributes`); other public types stay at the `WebAPI/` root.

## License

[MIT](LICENSE)