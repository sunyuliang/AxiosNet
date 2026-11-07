# AxiosNet 1.5 breaking changes

This is the **1.x → 1.5** migration reference for callers upgrading from `AxiosNet` 1.4.x or earlier to 1.5.x; it matches the public surface shipped since 1.5.0.

## Breaking changes in 1.5

| 1.x | 1.5 |
| --- | --- |
| `Callbacks` | `Observability` + top-level `ValidateStatus` |
| `Connection` | `Transport` |
| `Limits` | stays a public group (a real group): three leaves under `Limits` — `MaxResponseBytes` / `MaxRequestBytes` / `MaxStreamFrameBytes`; `MaxRate` → `Transport.MaxRate`, the three concurrency items → `Resilience.Concurrency` |
| `Limits.MaxBodyLength` / `MaxContentLength` / `MaxStreamFrameLength` | `Limits.MaxRequestBytes` / `Limits.MaxResponseBytes` / `Limits.MaxStreamFrameBytes` |
| `Limits.MaxRate` | `Transport.MaxRate` |
| `Limits.MaxConcurrentRequests` / `MaxQueueLength` / `QueueTimeout` | `Resilience.Concurrency.MaxInFlight` / `MaxQueue` / `QueueTimeout` |
| `Connection.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `PoolOptions` / `HttpVersion` / `VersionPolicy` / `Auth` / `Transport` | `Transport.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `Pool` / `HttpVersion` / `VersionPolicy` / `Auth` / `Profile` |
| `Transport.BasicAuth` (and the legacy `Connection.Auth`) | `Transport.Auth = AxiosCredential.Basic(...)`; the same slot also accepts `Bearer` / `Header` / `Provider`, exactly one form per request |
| Explicit `Authorization` header together with a credential (previously the explicit header silently won) | Throws `ERR_BAD_OPTION_VALUE`; `CredentialProvider` together with an already-set `Transport.Auth` errors too |
| `[Authorize("Token")]` with a static slot of a different scheme (previously the slot silently won) | Throws `ERR_BAD_OPTION_VALUE`; the declared scheme and `Transport.Auth`'s scheme must match |
| 401 replay body scope (previously only a few rebuildable shapes could replay) | Data bodies are rebuilt; whitelisted in-memory content is snapshotted before the first send (bounded by `Limits.MaxRequestBytes`; over the limit no replay); `Stream` / streaming multipart never replay |
| `RequestStreamAsync` / `[Stream]` with an explicit non-`Stream` `ResponseType` (previously silently overridden) | Throws `ERR_BAD_OPTION_VALUE` before sending; `Stream` is only auto-selected when `ResponseType` was not explicitly set |
| Stream-read cancellation (previously a caller-supplied token passed to `ReadAsync` / `CopyToAsync` on the `Response.Data` stream threw a raw `OperationCanceledException`) | Always throws an `AxiosException` with `ERR_CANCELED`, keeping the original OCE in `Cause`; timeouts remain `ECONNABORTED` |
| Declarative extension entry points (previously created a fresh facade per call, dropping `CredentialProvider` / `ErrorFactory`) | Cache one `ApiClient` per `AxiosClient`; state configured beforehand keeps applying |
| Streaming frame implementation location | NDJSON / SSE / JSON array framing moved into the kernel internals; `AxiosStreamFormat` / `Limits.MaxStreamFrameBytes` / `AxiosException.Stream*` remain public capabilities, while WebAPI keeps only the declarative entry points |
| `[Stream]` / `[BodyContentType]` / `[QueryCollectionFormat]` were usable on interface methods only | Usable on methods / classes / properties: interface usage stays method-level, models may declare them on the class or a property; property / method level overrides class level, two explicit declarations with different values throw `ERR_BAD_OPTION_VALUE`, and `[Multipart]` together with `[BodyContentType]` is an error |
| `Telemetry.Enabled` / `CaptureUrl` / `CaptureQuery` | `Observability.Capture = TelemetryCapture.Off` / `Path` / `Query` / `NoUrl` |
| top-level `Retry` / `Circuit` / `Hedging` / `Failover` / `Idempotency` / `TotalTimeout` | `Resilience.*` |
| `Cache` | `Caching` |
| `Data` | `Body` |
| `RequestCompression` | `Compression` |
| `ValueFormatter` | `Serialization.ValueText` |
| `ContentSerializers` / `client.ContentSerializers` | `Serialization.BodySerializers` |
| `Serialization.ContentSerializer` | removed; use `Serialization.BodySerializers.Default`, or process-level `AxiosContent.Register` / `AxiosContent.Default` |
| `AxiosContent.Serializer` | `AxiosContent.Default` (fallback only; media-type registrations always win) |
| `client.BodySerializers` | `client.Defaults.Serialization.BodySerializers` |
| `ContentSerializerRegistry.SetDefault(x)` | `registry.Default = x` |
| `RequestEncoding` / `ResponseEncoding` | `Serialization.TextEncoding` |
| `ParamsSerializer` | `Serialization.QueryEncoder` |
| `FormFieldNameSelector` | `Serialization.FormFieldName` |
| `JsonSerializerOptions` | `Serialization.JsonOptions` |
| `RetryNonIdempotentMethods` / `HedgeNonIdempotentMethods` | `Resilience.AllowUnsafeRetry` (the only switch for non-idempotent methods) |
| `RemoveHeaders` | no longer public |
| `AxiosConfig.LimitSettings` (public nested type) | becomes the public real group `AxiosConfig.LimitsSettings` (reached via `AxiosConfig.Limits`; both the type name and the access path changed); default-value constants stay on `AxiosConfig.DefaultStreamFrameBytes` / `MinStreamFrameBytes` / `MaxStreamFrameBytesLimit` |
| — | new `Axios.Create(baseAddress)` and `client.Create<T>()` |

## Behavior tightening in 1.5.x (vs 1.5.0)

| 1.5.0 | 1.5.x |
| --- | --- |
| `BaseAddress` with query / fragment was string-concatenated, semantics undefined | Resolved per RFC 3986 §5.2.2: the base query is inherited only when the reference path is empty, the base fragment is always dropped, and a fragment on the relative reference is kept |
| A request interceptor returning a brand-new `AxiosConfig` lost instance defaults (`BaseAddress` etc.) | Merged once automatically; explicitly written values win over instance defaults; already-merged configs / clones are not merged twice |
| Explicit `null` on `Transport.HttpVersion` / `VersionPolicy` behaved differently across entry points | Unified as "explicitly back to the library default" (HTTP/2 / `RequestVersionOrLower`), overriding the instance default |
| Every redirect failure reported `ERR_BAD_REQUEST` | Overrun now reports the dedicated `ERR_TOO_MANY_REDIRECTS`; `MaxRedirects = 0` keeps `ERR_BAD_REQUEST` but with a distinct "redirects disabled" message |
| Cache single-flight broadcast HTTP-status failures to followers | Non-2xx now uses "share the response, decide per follower": each follower applies its own `ValidateStatus` / interceptors / error factory while the server sees one request; streaming responses and over-cap bodies fall back to individual resends |
| A spent `Resilience.TotalTimeout` budget could still fail over / hedge | Exhaustion throws `ECONNABORTED` (`IsTotalTimeoutExceeded = true`) immediately; no second attempt is sent |
| Cached hits kept the original `Age` header; duplicate response headers replayed only the first value | `Age` is recomputed from `receivedAt` on hits; duplicate headers replay all values |
| Hedging exclusion covered download progress only | Upload / download progress callbacks and `MaxRate` now exclude hedging; `TrafficMeter` counting is atomic |
| Explicitly writing `null` into an `Observability` callback per request (previously: no statement, kept the instance value) | Now clears the instance default; omitting the property is what inherits it. Six leaves: `Observability.Capture` / `OnUploadProgress` / `OnDownloadProgress` / `OnRawResponse` / `OnSendingRequest` / `TimeoutMessage` |

`AxiosNet.WebAPI` is still part of the same package: there is no separate package, and the declarative extension methods only need `using AxiosNet.WebAPI;`; use `AxiosNetOmitWebApi` to leave it out of the build when needed.
