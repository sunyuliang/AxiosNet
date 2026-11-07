# AxiosNet 1.5 破坏性变更

本文是 **1.x → 1.5** 的迁移对照，适用于从 `AxiosNet` 1.4.x 及更早版本升级到 1.5.x 的调用方；内容与 1.5.0 起发布的公共面一致。

## 1.5 破坏性变更

| 1.x | 1.5 |
| --- | --- |
| `Callbacks` | `Observability` + 顶层 `ValidateStatus` |
| `Connection` | `Transport` |
| `Limits` | 保持公共分组（真分组）：`Limits` 下三个叶子 `MaxResponseBytes` / `MaxRequestBytes` / `MaxStreamFrameBytes`；`MaxRate` → `Transport.MaxRate`，并发三项 → `Resilience.Concurrency` |
| `Limits.MaxBodyLength` / `MaxContentLength` / `MaxStreamFrameLength` | `Limits.MaxRequestBytes` / `Limits.MaxResponseBytes` / `Limits.MaxStreamFrameBytes` |
| `Limits.MaxRate` | `Transport.MaxRate` |
| `Limits.MaxConcurrentRequests` / `MaxQueueLength` / `QueueTimeout` | `Resilience.Concurrency.MaxInFlight` / `MaxQueue` / `QueueTimeout` |
| `Connection.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `PoolOptions` / `HttpVersion` / `VersionPolicy` / `Auth` / `Transport` | `Transport.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `Pool` / `HttpVersion` / `VersionPolicy` / `Auth` / `Profile` |
| `Transport.BasicAuth`（及旧 `Connection.Auth`） | `Transport.Auth = AxiosCredential.Basic(...)`；同一槽位还支持 `Bearer` / `Header` / `Provider`，一个请求只能取一种形态 |
| 显式 `Authorization` 头与凭据同时设置（旧行为静默取显式头） | 报 `ERR_BAD_OPTION_VALUE`；`CredentialProvider` 与已设置的 `Transport.Auth` 并存同样报错 |
| `[Authorize("Token")]` 与静态槽位 scheme 不一致（旧行为静默以槽位为准） | 报 `ERR_BAD_OPTION_VALUE`；声明与 `Transport.Auth` 的 scheme 必须一致 |
| 401 重放的正文范围（旧行为只有少数可重建形态能重放） | 数据型正文重新装配；白名单内存内容首次发送前快照（受 `Limits.MaxRequestBytes` 约束，超限不重放）；`Stream` / 流式 multipart 不重放 |
| `RequestStreamAsync` / `[Stream]` 遇到显式非 `Stream` 的 `ResponseType`（旧行为静默覆盖） | 发出前报 `ERR_BAD_OPTION_VALUE`；只有未显式设置时才自动切到 `Stream` |
| 流读取消（旧行为：直接对 `Response.Data` 流调用 `ReadAsync` / `CopyToAsync` 并传入自己的令牌时抛原始 `OperationCanceledException`） | 统一抛带 `ERR_CANCELED` 的 `AxiosException`，原始 OCE 留在 `Cause`；超时仍为 `ECONNABORTED` |
| 声明式扩展入口（旧行为每次调用新建门面，丢掉 `CredentialProvider` / `ErrorFactory`） | 按 `AxiosClient` 缓存同一个 `ApiClient`，先配好的状态照常生效 |
| 流式分帧实现位置 | NDJSON / SSE / JSON 数组分帧下沉到内核内部，`AxiosStreamFormat` / `Limits.MaxStreamFrameBytes` / `AxiosException.Stream*` 仍是公共能力；WebAPI 只保留声明式入口 |
| `[Stream]` / `[BodyContentType]` / `[QueryCollectionFormat]` 仅可用于接口方法 | 可用于方法 / 类 / 属性：接口式仍打在方法上，模型式可打在类或属性上；属性 / 方法级覆盖类级，两处都显式声明且值不同报 `ERR_BAD_OPTION_VALUE`；`[Multipart]` 与 `[BodyContentType]` 互斥 |
| `Telemetry.Enabled` / `CaptureUrl` / `CaptureQuery` | `Observability.Capture = TelemetryCapture.Off` / `Path` / `Query` / `NoUrl` |
| 顶层 `Retry` / `Circuit` / `Hedging` / `Failover` / `Idempotency` / `TotalTimeout` | `Resilience.*` |
| `Cache` | `Caching` |
| `Data` | `Body` |
| `RequestCompression` | `Compression` |
| `ValueFormatter` | `Serialization.ValueText` |
| `ContentSerializers` / `client.ContentSerializers` | `Serialization.BodySerializers` |
| `Serialization.ContentSerializer` | 删除；改用 `Serialization.BodySerializers.Default`，或进程级 `AxiosContent.Register` / `AxiosContent.Default` |
| `AxiosContent.Serializer` | `AxiosContent.Default`（只兜底，按媒体类型登记始终优先） |
| `client.BodySerializers` | `client.Defaults.Serialization.BodySerializers` |
| `ContentSerializerRegistry.SetDefault(x)` | `registry.Default = x` |
| `RequestEncoding` / `ResponseEncoding` | `Serialization.TextEncoding` |
| `ParamsSerializer` | `Serialization.QueryEncoder` |
| `FormFieldNameSelector` | `Serialization.FormFieldName` |
| `JsonSerializerOptions` | `Serialization.JsonOptions` |
| `RetryNonIdempotentMethods` / `HedgeNonIdempotentMethods` | `Resilience.AllowUnsafeRetry`（非幂等放行的唯一开关） |
| `RemoveHeaders` | 不再公开 |
| `AxiosConfig.LimitSettings`（公共嵌套类型） | 成为公共真分组 `AxiosConfig.LimitsSettings`（经 `AxiosConfig.Limits` 访问，类型名与访问路径同时更名）；默认值常量仍在 `AxiosConfig.DefaultStreamFrameBytes` / `MinStreamFrameBytes` / `MaxStreamFrameBytesLimit` |
| — | 新增 `Axios.Create(baseAddress)` 与 `client.Create<T>()` |

## 1.5.x 行为收紧（相对 1.5.0）

| 1.5.0 | 1.5.x |
| --- | --- |
| `BaseAddress` 带 query / fragment 时按字符串拼接，语义未定义 | 按 RFC 3986 §5.2.2 组件解析：base query 仅在引用路径为空时继承；base fragment 一律丢弃；相对引用自带的 fragment 保留 |
| 请求拦截器返回全新 `AxiosConfig` 时缺少实例默认值（`BaseAddress` 等） | 自动补一次合并；拦截器显式写入的项不被实例默认值覆盖；已合并配置 / 克隆不二次合并 |
| `Transport.HttpVersion` / `VersionPolicy` 显式 `null` 在两条入口上语义不一致 | 统一为「显式回到库默认」（HTTP/2 / `RequestVersionOrLower`），并压过实例默认值 |
| 重定向超限与此前所有重定向错误都报 `ERR_BAD_REQUEST` | 超限报独立错误码 `ERR_TOO_MANY_REDIRECTS`；`MaxRedirects = 0` 保留 `ERR_BAD_REQUEST` 但使用独立的「禁止跟随」文案 |
| 缓存单飞把 HTTP 状态类失败也广播给跟随者 | 非 2xx 改为「共享响应、各自判定」：跟随者用它自己的 `ValidateStatus` / 拦截器 / 错误工厂判定，服务端只发一次；流式响应与超限正文退回各自补发 |
| `Resilience.TotalTimeout` 预算耗尽后仍可能换端点 / 开对冲分支 | 预算耗尽立即抛出 `ECONNABORTED`（`IsTotalTimeoutExceeded = true`），不再发第二次 |
| 缓存命中时 `Age` 头保持原值；重复响应头只回放首值 | 命中时按 `receivedAt` 重算 `Age`；重复头回放全部值 |
| 囊括所有并发回调的对冲判定只排除下载进度 | 上传 / 下载进度回调与 `MaxRate` 同时排除对冲；`TrafficMeter` 计数改原子操作 |
| 按请求显式把 `Observability` 回调写成 `null`（旧行为：不表态，保留实例值） | 表示清除实例默认值；省略才表示继承。六个叶子：`Observability.Capture` / `OnUploadProgress` / `OnDownloadProgress` / `OnRawResponse` / `OnSendingRequest` / `TimeoutMessage` |

`AxiosNet.WebAPI` 仍是同一个包：没有独立包，声明式扩展方法只需 `using AxiosNet.WebAPI;`；需要时可用 `AxiosNetOmitWebApi` 排除构建。
