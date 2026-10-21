# AxiosNet

[![.NET](https://img.shields.io/badge/.NET-6.0%2B-512BD4?logo=.net)](https://dotnet.microsoft.com/)
[![nuget](https://img.shields.io/nuget/v/AxiosNet?logo=nuget)](https://www.nuget.org/packages/AxiosNet)
[![language](https://img.shields.io/badge/C%23-12-239120)](https://learn.microsoft.com/dotnet/csharp/)
[![tests](https://img.shields.io/badge/tests-443%20passed-success)](#build-and-test)
[![license](https://img.shields.io/badge/license-MIT-blue)](LICENSE)

[简体中文](README.md) | **English**

An **axios-style HTTP client for .NET (C#)** whose API maps one-to-one onto the JavaScript [axios](https://github.com/axios/axios) ecosystem. On top of .NET's native `SocketsHttpHandler` connection reuse, it adds a **per-origin connection pool** (connection reuse, concurrency caps, idle eviction, sharing across instances, observable statistics). It is meant to replace hand-rolled `HttpClient` wrappers and covers API calls, file upload/download, form submission, and similar network scenarios.

- Zero third-party dependencies — .NET BCL only
- Runtime support: **.NET 6 and later** (single `net6.0` target, see [Target framework and runtime support](#target-framework-and-runtime-support))
- Async-first (`Task` / `CancellationToken` / `ConfigureAwait(false)`)

> Repository: **Gitee ([itsm/axios-net](https://gitee.com/itsm/axios-net)) is the primary repository** — issues, PRs, and tags are handled there. [GitHub](https://github.com/sunyuliang/AxiosNet) is a read-only mirror (CI-driven NuGet publishing also runs on the mirror side).

---

## Table of contents

- [Installation](#installation)
- [Quick start](#quick-start)
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
- [Project layout](#project-layout)
- [License](#license)

---

## Installation

### Option 1: NuGet (recommended)

```bash
dotnet add package AxiosNet
```

> Version numbers match Git tags: tagging `v1.2.3` publishes `1.2.3`; tagged pre-releases (e.g. `v1.2.3-rc.1`) are published as prerelease versions.

### Option 2: Project reference (this repository)

```bash
dotnet add your-app.csproj reference src/AxiosNet/AxiosNet.csproj
```

### Option 3: Source inclusion

Copy the `src/AxiosNet` folder into your solution:

```bash
dotnet sln YourApp.sln add src/AxiosNet/AxiosNet.csproj
```

> The solution file `AxiosNet.slnx` uses the new XML format and requires .NET SDK 8.0.400+ or Visual Studio 2022 17.13+. On older tooling, regenerate a classic `.sln` with `dotnet new sln` and add the two projects manually.

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
    JsonSerializer = mySerializer,            // custom IAxiosJsonSerializer (null = global default)
    ParamsSerializer = p => MyQuery(p),       // Func<object?, string>: custom ?a=1&b=2
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

| `ContentType` | Structured data (objects / dictionaries / dynamic / collections) | `string` / `byte[]` / `Memory<byte>` / `ArraySegment<byte>` / `Stream` / `HttpContent` |
| --- | --- | --- |
| `application/json` (default) | JSON text | Sent as-is |
| `application/x-www-form-urlencoded` | Key-value form (nested `a[b]=v`, arrays `a[]=v`, nulls skipped) | Sent as-is (treated as an already-encoded form string) |
| `multipart/form-data` | Fields + file parts (`FormFile` / `Stream` / `byte[]` / memory types / `HttpContent`; arrays expand into repeated same-name parts) | Throws `ERR_BAD_OPTION_VALUE` (header/body contradiction); pass a pre-serialized multipart body as `HttpContent`, or give `Content-Type` a `boundary` parameter |
| Anything else (`text/plain`, `application/xml`, …) | Still written as JSON text; only the header differs | Sent as-is |

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

> The default media type is JSON, so set `c.ContentType = "application/octet-stream"` explicitly when sending a binary payload — the library never guesses from `byte[]`/`Stream`.

Additional notes on multipart:

- **Field names** come from the object's property names or the dictionary's keys (honoring `JsonSerializerOptions.PropertyNamingPolicy` and recognizing `[JsonPropertyName]` / `[JsonIgnore]`); nested objects become `meta[author]`; arrays/collections expand into **repeated same-name parts** (bindable directly by ASP.NET Core `IFormFileCollection`, FastAPI, and PHP).
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

Runnable sample: item 18 of `samples/AxiosNet.Simple` (`dotnet run --project samples/AxiosNet.Simple`) — `DemoServer` exposes `/upload/raw` and `/upload/single`, parses byte-exactly, and reports file name/byte count/SHA256, which the client verifies one by one.

## responseType (including dynamic)

```csharp
// 1) Strongly typed (most common)
var typed = await http.GetAsync<User>("users/1");
User u = typed.Data!;

// 2) Non-generic: Data is a JsonElement (falls back to the raw text for invalid JSON, matching axios transformResponse)
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
- Non-generic: `JsonElement` for valid JSON, the raw text for invalid JSON (matching axios's `transformResponse`).

### Content encoding

- **Request body**: `RequestEncoding` decides the bytes written (UTF-8 by default); when set explicitly, the `charset` of `Content-Type` is written too. `byte[]` / `Stream` / `HttpContent` pass through untouched and are unaffected.
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
- Reports are throttled to every 50 ms; on completion (`loaded == total`) exactly one 100% event is guaranteed, never duplicated.
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
- `MaxBodyLength` checks `Content.Headers.ContentLength` before sending, so it only applies to bodies whose length is known (JSON / forms / `byte[]` / `FormFile`); custom `HttpContent` of unknown length is not intercepted.
- **Negative** values for these four limits throw `ERR_BAD_OPTION_VALUE` (they used to be silently treated as "unlimited", so callers believed a limit was in force). `MaxRate = 0` means "unthrottled" and `Timeout = TimeSpan.Zero` means "no timeout".

## Custom serialization and data transforms

### Custom JSON serialization

JSON behavior has three layers, each overriding the previous one:

| Layer | Entry point | Typical use |
| --- | --- | --- |
| Global | `AxiosJson.Options` / `AxiosJson.Serializer` | One naming strategy and number/date handling for the whole app |
| Instance | `Axios.Create(c => c.JsonSerializerOptions = ...)`, `c.JsonSerializer = ...` | One backend that speaks PascalCase / snake_case |
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

To swap the whole serialization engine (Newtonsoft.Json, source-generated serializers, etc.), implement `IAxiosJsonSerializer`:

```csharp
using AxiosNet.Serialization;

public sealed class NewtonsoftJsonSerializer : IAxiosJsonSerializer
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver()
    };

    public string Serialize(object? value, Type type, AxiosConfig config) =>
        JsonConvert.SerializeObject(value, type, Settings);

    public object? Deserialize(string json, Type type, AxiosConfig config) =>
        JsonConvert.DeserializeObject(json, type, Settings);

    // dynamic expands to JObject/JArray (supports data.user.name, data.items[0].id)
    public object? ParseDynamic(string json, AxiosConfig config) =>
        JsonConvert.DeserializeObject<JToken>(json, Settings);

    // Data of non-generic responses: the document shape is up to you (the default implementation uses JsonElement)
    public object ParseDocument(string json, AxiosConfig config) =>
        JToken.Parse(json);
}

AxiosJson.Serializer = new NewtonsoftJsonSerializer();            // global
// or: new AxiosClient(new AxiosConfig { JsonSerializer = ... })   // instance only
```

Conventions and boundaries:

- The serializer is shared by concurrent requests, so implementations must be stateless or thread-safe; thrown exceptions are wrapped as `ERR_BAD_RESPONSE` / `ERR_BAD_REQUEST`.
- It only affects the **JSON request body**, **JSON response deserialization**, **dynamic expansion**, and **document parsing for non-generic responses**.
- Field naming for forms/multipart still comes from `JsonSerializerOptions`; with a custom serializer the library first serializes the object to JSON and then parses it into a tree to flatten it, so naming stays consistent.

> A complete runnable version lives in [`samples/AxiosNet.Simple/NewtonsoftJsonSerializer.cs`](samples/AxiosNet.Simple/NewtonsoftJsonSerializer.cs) and `Examples/NewtonsoftJsonExample.cs` (sample item 19): the same data is serialized with Newtonsoft and System.Text.Json and the request bodies compared, plus both a PascalCase strongly-typed response path and a `dynamic` (`JObject`) path.

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

> The first parameter is `object?`; cast to your own DTO when you need strong typing. The default serializer already follows axios conventions (arrays `a[]=1&a[]=2`, nested `o[k]=v`, nulls skipped), so most scenarios need no customization.

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
| `ERR_BAD_REQUEST` | Response status is 4xx (default validation failure); request body exceeds `MaxBodyLength`; request body serialization failed (cycles, converter exceptions — see `ex.Cause`) | ✅ |
| `ERR_BAD_RESPONSE` | Response status is 5xx; response body cannot be deserialized to the target type (including `dynamic` receiving non-JSON); response body exceeds `MaxContentLength`; generic argument and `responseType` mismatch (e.g. `GetAsync<Stream>` combined with `text`/`arraybuffer`) | ✅ |
| `ECONNABORTED` | The request exceeded `Timeout` (the message can be replaced via `TimeoutMessage`) | ✅ |
| `ERR_NETWORK` | Connection refused / DNS failure / connection dropped mid-flight / truncated response, i.e. network-layer failures (see `ex.Cause` for the inner exception) | ✅ |
| `ERR_CANCELED` | `CancellationToken` cancellation | ✅ |
| `ERR_INVALID_URL` | Invalid URL (including a relative URL with no `baseURL`, or a scheme other than `http`/`https`) | ✅ |
| `ERR_BAD_OPTION_VALUE` | Instance-only settings set on a single request (`proxy` / `credentials` / `withCredentials` / `maxRedirects` / `poolOptions`); connection-level options set on a shared-pool client's defaults; `pool` and `poolOptions` passed together; invalid pool option values (e.g. `MaxConnectionsPerServer <= 0`); raw content (`string` / `byte[]` / `Memory` / `Stream`) passed as `data` while `Content-Type` is `multipart/form-data` without a `boundary` parameter | ✅ (same code) |

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
- `auth` (HTTP Basic), `withCredentials`, proxy, automatic redirects (`maxRedirects: 0` disables them entirely), automatic gzip/deflate/br decompression
- `axios.getUri(config)` → `client.GetUri(config)`: builds the URL without sending a request (debugging/logging/pre-signing)
- `maxContentLength` / `maxBodyLength`: size caps for response and request bodies; exceeding them throws `ERR_BAD_RESPONSE` / `ERR_BAD_REQUEST` with the actual byte count in the message
- `onUploadProgress` / `onDownloadProgress`: progress events (loaded/total/percentage/average rate/ETA) in both directions
- `maxRate`: one-way upload/download throttling (bytes/second)
- `paramsSerializer` / `formFieldNameSelector`: custom query string and form field naming (the field-name customization of axios's `formSerializer`)
- `transformRequest` / `transformResponse`: pre-request and post-response transform chains (thread-safe, mutable at runtime)
- 🔧 **Pluggable JSON serialization**: System.Text.Json by default, with `JsonSerializerOptions` overridden per "global → instance → request"; implement `IAxiosJsonSerializer` to swap the whole engine (e.g. Newtonsoft.Json)
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
3. For non-generic JSON responses, `Data` is a `JsonElement` (use the generic `GetAsync<dynamic>` when you need JS-style dynamic access);
4. Cancellation uses `CancellationToken` (semantically equivalent to `AbortController`);
5. **The request body encoding is decided by `ContentType`** (JSON by default) and is never inferred from the data type: dictionaries are not automatically turned into forms, so you must set `ContentType = AxiosConfig.FormUrlEncodedContentType` explicitly when you need a form;
6. The connection pool is an extension of this library; axios (browser/XHR, Node http) has no isomorphic implementation;
7. Request/response text encoding can be controlled explicitly via `RequestEncoding` / `ResponseEncoding` (axios has no equivalent setting);
8. `proxy` / `credentials` / `withCredentials` / `maxRedirects` can only be set at the instance level; setting them on a single request **throws immediately** with `ERR_BAD_OPTION_VALUE` (axios silently ignores them — this library chooses fail-fast). Setting them on a shared-pool client's defaults also throws immediately, because connection behavior is decided by the pool and must be configured on `ConnectionPoolOptions`;
9. `maxRate`, `onUploadProgress` / `onDownloadProgress` are implemented by the library at the `HttpContent` and `Stream` layers (`HttpClient` has no native support), so for custom `HttpContent` the "encoded bytes" counted are the bytes actually written to the network stream;
10. `transformRequest` / `transformResponse` take `object?` rather than a generic chain (C# cannot discard a type and narrow it back as freely as JS can); cast inside the delegate when you need strong typing;
11. `config.method` uses the BCL `HttpMethod` directly (there is no bespoke enum here), so any custom verb can be sent; axios's adapter corresponds to `HttpMessageHandler` on the .NET side, with `ConnectionPoolOptions.HandlerFactory` or `new AxiosClient(handler: ...)` as the entry points.
12. **Headers are validated on write**: a header name must be an RFC 7230 token and its value must not contain `CR` / `LF` / `NUL`, otherwise `AxiosHeaders.Set` throws `ArgumentException` (axios/JS does no such check). Browser/Node HTTP stacks split CRLF themselves; .NET's `TryAddWithoutValidation` does not — without this check the library would leave an HTTP request-splitting (CWE-113) door open;
13. **`WithCredentials` cookies are shared per domain** (one container for the whole pool instead of one per origin), so sessions across sub-domains / alternate ports behave like a browser;
14. `Timeout` is capped at about 49.7 days; larger values are clamped to the cap instead of throwing (`Timeout = 0` is the real unlimited timeout);
15. **`params` uses strict RFC 3986 encoding** (`Uri.EscapeDataString`), whose character set differs from axios's default `encode()`: spaces become `%20` instead of `+`, and `!'()*` plus `,:$` are encoded too. That encoding is actually a better fit for OSS/S3-style pre-signing; pass a custom `ParamsSerializer` when you need axios's exact form;
16. **Multipart field names and file names are validated before sending**: `"`, `\`, `CR`/`LF` and control characters would break the quoted-string in `Content-Disposition`, so they throw `ERR_BAD_OPTION_VALUE` (or `ArgumentException` from the `FormFile` constructor) instead of letting the server silently receive a garbled key (browser `FormData` handles these inconsistently, so axios cannot rely on any particular behavior);
17. **`ContentType` is a `string`, not an enum** (a `Content-Type` is media type + `charset`/`boundary` parameters, which an enum cannot express). Common values ship as `AxiosContentType` constants — the same "presets plus arbitrary custom values" idea as `HttpMethod.Get` together with `new HttpMethod("PROPFIND")`.


## Sample project

[`samples/AxiosNet.Simple`](samples/AxiosNet.Simple) is a **ready-to-run, offline** console sample: it embeds a `TcpListener`-based HTTP/1.1 keep-alive demo server and covers 20 categories of integration calls:

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
19. **Custom JSON serialization**: implement `IAxiosJsonSerializer` and replace System.Text.Json with **Newtonsoft.Json** (request body / strongly-typed response / dynamic all routed through the same engine)
20. **DNS changes and connection expiry**: two local servers simulate old and new addresses for the same domain, demonstrating natural convergence when `PooledConnectionLifetime` expires and the immediate invalidation of `pool.Clear()`

```bash
dotnet run --project samples/AxiosNet.Simple
```

> Items 13 and 18 above cover uploads: `DemoServer` uses `MultipartReader` to parse multipart **byte-exactly** and reports each part's field name, file name, content type, byte count, and SHA256; the single-file scenario also provides `/upload/raw` (raw stream) and `/upload/single` (multipart single file).
>
> Item 19 is the only sample that needs an extra NuGet package (`Newtonsoft.Json`), and only to demonstrate **engine replacement** — the library itself still has zero third-party runtime dependencies; if you do not need custom serialization you can remove `Newtonsoft.Json` from the sample project.

## Target framework and runtime support

The library **targets `net6.0` only**; a single assembly covers every runtime from .NET 6 onward:

| Consumer target framework | Result |
| --- | --- |
| `net6.0` / `net7.0` / `net8.0` / `net9.0` / `net10.0` / `net11.0` … | References `lib/net6.0` directly, no configuration needed |

- **Why one target is enough**: .NET only guarantees one direction — "a newer runtime can load an older assembly" — and every API the library uses already exists in 6.0, with no 7/8/9-only calls, so there is no value in recompiling per runtime. Shared-framework assemblies such as `System.Text.Json` automatically resolve to the 8.x version in a .NET 8 app at runtime; 6.x is not carried along.
- **.NET Framework is not supported**, nor is .NET Core 3.1 or earlier: the connection pool is built directly on `SocketsHttpHandler` (`PooledConnectionLifetime` / `PooledConnectionIdleTimeout` / `ConnectTimeout` / `SslOptions` / `HandlerFactory`), a type absent from the `netstandard2.0` BCL contract; 3.1 additionally lacks `DateOnly`/`TimeOnly`, `ReadAsStreamAsync(CancellationToken)`, `ConcurrentDictionary.TryRemove(KeyValuePair)`, and a batch of other APIs, so the compatibility work would not be worth the gain.
- **Why the floor is .NET 6**: `DateOnly` / `TimeOnly` and `HttpContent.SerializeToStreamAsync(Stream, TransportContext, CancellationToken)` only arrived around 6.0; going lower would require maintaining another layer of `#if`, and .NET 5 is out of support. The current source has **zero `#if`**.
- **How it is verified**: the test project still targets both `net8.0` and `net6.0`, running the same shipped assembly's 443 cases on each runtime (886 executions in total, all passing). That exactly covers the most common consumption shape — a .NET 8 app loading the `net6.0` asset.

```xml
<!-- Consumers need no configuration: any target framework >= net6.0 can reference it -->
<PackageReference Include="AxiosNet" Version="1.0.1" />
```

## Build and test

```bash
# Build (Debug): the library produces a single net6.0 asset
dotnet build AxiosNet.slnx

# Run all 443 xUnit tests (the test project targets net8.0 + net6.0, so 886 executions)
dotnet test AxiosNet.slnx

# Release
dotnet build AxiosNet.slnx -c Release
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
- Interceptors: snapshot caching (reused when unchanged) and immediate invalidation on Use/Eject/Clear
- Response reading: close-delimited responses without Content-Length (buffered unknown-length branch), zero-copy return when the length allows exact preallocation
- Generic routing: `GetAsync<Stream>` combined with an incompatible explicit `responseType` is uniformly wrapped as `ERR_BAD_RESPONSE` (no raw `InvalidCastException` leaking out)
- Custom handlers: passthrough of custom `HttpMethod`s such as `PROPFIND`, direct `DelegatingHandler` injection (including `disposeHandler` semantics), `HandlerFactory` called once per origin / wrapping the default handler / throwing `InvalidOperationException` on a null return, plus fail-fast with pool/poolOptions and instance-level options
- Failure and concurrency regressions (`ReviewRegressionTests`): streaming 4xx releasing the connection, truncated responses / error codes for non-JSON dynamic, concurrent mutation of default request headers, the params indexer, `Timeout = 0` override, streaming body timeout, leases not evicted during download
- Pluggable JSON (`JsonCustomizationTests`): a custom `IAxiosJsonSerializer` driving request/response/dynamic/form flattening, instance-level `JsonSerializerOptions`, concurrent add/remove on transform lists
- Security & hardening (`HardeningTests`): header values/names with CR/LF or non-token characters rejected (HTTP request smuggling guard), `poolOptions.MaxAutomaticRedirections` no longer overwritten by an implicit default, truncated streaming body (and `Timeout=0`) reported as `ERR_NETWORK`, oversized `Timeout` clamped to ~49.7 days, negative limits fail fast, params appended before the URL fragment, `PostAsync(url, null)` throwing `ArgumentNullException`, `maxContentLength` enforced for streaming and chunked responses, cookies shared across origins, cancellation keeping its `Cause`, `JsonElement` params, `byte[]` params (base64), pool origin cap and graceful `Dispose` (in-flight requests are not killed by closing the client; dispose races never leak a raw `ObjectDisposedException`)

## Project layout

```
.
├─ AxiosNet.slnx
├─ src/
│  └─ AxiosNet/                  # library (net6.0 only, compatible with all runtimes from .NET 6 onward)
│     ├─ Axios.cs                # static facade (the global axios equivalent)
│     ├─ AxiosClient.cs          # instance + request pipeline + response reading
│     ├─ AxiosClient.Shortcuts.cs# all shortcut overloads (get/post/... + form)
│     ├─ AxiosClient.Config.cs   # config merging and header application
│     ├─ AxiosJson.cs            # global JSON defaults (options / pluggable serializer)
│     ├─ AxiosConfig.cs          # request/instance configuration
│     ├─ AxiosContentType.cs     # common Content-Type presets
│     ├─ AxiosUrl.cs             # path-segment escaping (AxiosUrl.EncodePathSegment)
│     ├─ AxiosHeaders.cs         # case-insensitive headers
│     ├─ AxiosResponse.cs        # response (generic/non-generic)
│     ├─ AxiosException.cs       # aligned with AxiosError
│     ├─ AxiosProgressEvent.cs   # upload/download progress events
│     ├─ FormFile.cs             # multipart file part
│     ├─ ResponseType.cs / AxiosAuth.cs          # response shape / Basic auth
│     ├─ Interceptors/           # request/response interceptor management
│     ├─ Serialization/          # IAxiosJsonSerializer + default System.Text.Json implementation
│     ├─ Internal/               # URL/params, body factories, traffic metering, dynamic JSON, reflection cache, status text
│     └─ Pooling/                # HttpConnectionPool + PooledHandlerContext (handler injection)
├─ samples/
│  └─ AxiosNet.Simple/           # 20 categories of integration calls (embedded demo server + exact multipart parsing + Newtonsoft.Json serializer + DNS refresh)
└─ test/
   └─ AxiosNet.Tests/            # xUnit tests (443 cases × net6.0 / net8.0 = 886 executions)
```

## License

[MIT](LICENSE)
