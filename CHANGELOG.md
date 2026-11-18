# Changelog

本项目的所有重要变更记录在此文件。

格式基于 [Keep a Changelog](https://keepachangelog.com/)。版本号沿用 [Semantic Versioning](https://semver.org/) 的 `主.次.修订` 形式，但 **`1.x` 内允许带破坏性变更**：破坏性项单独写进「Changed (Breaking)」并在 `docs/breaking-changes-*.md` 给出迁移对照，**不发 2.0**——这个库的实际使用面小、迭代快，一次迁移文档比一个没人敢跳的大版本号更有用。

> **说明：本文件从 `1.5.0` 起建立，此前版本未追述。** 更早的历史（`1.4.x` 及以前）请以 git 提交历史为准；`1.x → 1.5` 的完整迁移对照见 [`docs/breaking-changes-1.5.md`](docs/breaking-changes-1.5.md)（英文：[`docs/breaking-changes-1.5.en.md`](docs/breaking-changes-1.5.en.md)）。每个条目只记录能从版本 tag 与对应提交中取证的改动。

## [Unreleased]

### Changed

- `Warmup()` 预热失败后不再永久置「已预热」：静态首用或 `HttpConnectionPool.Prewarm` 抛异常时回滚实例标记，下一次调用重新尝试。此前失败一次之后每次调用都静默返回 `false`，调用方既拿不到重试机会、也看不到失败原因（`WarmupTests` 已固化「容量恢复后能预热成功」）。
- 源生成器的类名与 hint 名换形态：哈希从 32 位改成 64 位（16 位十六进制），请求模型与接口代理的前缀截到 64 字符、对象形状截到 48 字符。32 位哈希在类型名多时有撞车余地，撞了就是 `AddSource` 抛 `ArgumentException`——用户看到的是「生成器崩溃」而不是「我的类型名冲突」。把生成产物提交进版本库的用户会看到一次全量重命名。
- OpenAPI 生成器：字段名里的 BMP 外字母（如 CJK 扩展 B 区）转义成 `_uXXXXX` 再进标识符。C# 标识符只接受 BMP 码位，直接写代理对会让生成的模型编译不过（CS1056）；字段原名仍由 `JsonPropertyName` 保留，序列化行为不变。
- 单飞（`SingleFlight`）的跟随者直接跟随在途的那一轮，不再先创建一个注定落选的领导者；在途登记改为按「键 + 实例」移除，换任期间不会摘掉新上位的领导者。

### Fixed

- `OnSendingRequest` 钩子把正文换成另一份时，头隔离外壳不再无人释放；钩子对内容头的改写也不会叠到下一次尝试或另一条对冲分支上（正文所有权现在是显式契约，由 `SendingRequestHookTests` 守着重试重放）。
- 表单百分号编码按 `Encoding.GetBytes` 实际写入的字节数切片，不再按预算的字节数。
- 增量生成管线的值相等数组：`default` 实例与 `Empty` 现在哈希一致（此前两者相等但哈希不同，等于每次编译都判「变了」）。

## [1.7.0] - 2026-11-17

### Added

- 预热入口，用来摊掉首条请求的固定成本：`HttpConnectionPool.Prewarm(uri, transport?)` 预建指定 origin 的节点（Handler 与 `HttpClient`），`AxiosClient.Warmup()` / `Axios.Warmup()` 额外跑热静态首用（JSON 选项校验与转换器、响应解码器、状态文案与媒体类型判定）并按 `Defaults.BaseAddress` 预建节点。两者都**只建节点、不建连**——TCP 仍然由第一条真正的请求建立。
  - 「预热过」记在**实例**上：`Warmup()` 第二次调用返回 `false` 且不再执行；`Axios.Reset()` 换掉默认实例后标记随之重置。`Prewarm` 幂等（节点已在时只是一次哈希查找），同样受 `MaxEndpointCount` 与空闲驱逐约束。（`Unreleased` 起：预热抛异常会回滚标记，下一次调用重新尝试。）
  - `Warmup()` 的基地址必须是绝对 URI，否则抛 `ERR_BAD_OPTION_VALUE`（不静默跳过）；已释放的实例与请求同一口径抛 `ERR_CANCELED`，池已释放后 `Prewarm` 抛 `ObjectDisposedException`。
- 观测实现可整套替换：新增 `IAxiosTelemetrySink`、`AxiosTelemetrySink`（全空实现的基类）与观测句柄 `AxiosRequestTrace`，赋 `AxiosTelemetry.Sink` 即接管 span / 指标 / 事件三路输出，内置那套**同时停用**（不是叠加）；赋 `AxiosTelemetrySink.Off` 则彻底关掉。此前 `AxiosTelemetry` 是静态类，包外只能按 `ActivitySource` / `Meter` 名字订阅再自行翻译，既接不进公司自己的管道，也关不掉内置的那套。默认行为不变（`Sink` 默认指向内置实现）。
  - 可替换实现是第三方代码，它违约抛异常时**只丢掉那一次观测，不改写请求结果**：派发统一经 `AxiosTelemetry.Dispatch` 的护栏，`finally` 里的收尾异常不再把一条 200 换成观测异常、也不再盖掉真正的失败原因；在途请求计数照常归零（否则 `Dispose()` 永远等不到 0，池与 Handler 一直不释放）——`EnterRequest` 抛了就不补 `ExitRequest`，水位不会变负。
  - `IsEnabled` 的管辖范围写明：只管请求级生命周期（开句柄 / 成功 / 失败上报），缓存、重试、闸门、池与租约这些事件不看它；要整体静音用 `AxiosTelemetrySink.Off`。
- 源生成器新增两条编译期诊断：`AXA016`（路由模板里的 `{name}` 没有任何成员用 `[Path]` 绑定，名字比对大小写不敏感）与 `AXA017`（多个成员显式标了 `[Body]`）。二者此前只在运行期由绑定构建器报错。
- 缓存条目可导出与重建：新增 `AxiosShortCircuit.Restore(...)`，供自定义 `IAxiosCacheStore` 从自己的存储格式重建条目；新增只读视图 `MultiValueHeaderValues` / `CachedAt` / `CachedAge`，用于把多值响应头、落库时刻与落库龄期写进自己的存储格式。此前这三项在包外读写不到，自建 Redis / 共享缓存会静默丢失多条 `Set-Cookie` / `Vary` 的全部值、`ETag` / `Last-Modified` 的优先来源，以及 `Age` 的推进。
- 新增 `AxiosResponse<T>.DataOrThrow()`：正文为空时抛 `AxiosException(ERR_BAD_RESPONSE)`（消息带期望的类型名，异常带 `Config` / `Response`），用于替代 `Data!` —— 后者遇到空正文会退化成不带请求上下文的 `NullReferenceException`。空正文合法时继续用 `Data`。
- 反射缓存（对象形状 / 属性元数据）新增条目上限：条目数达到 `ClearThreshold`（目标上限的 2 倍：形状 4096 条、属性元数据 8192 条）时整体清空。此前这两份缓存以 `Type` / `PropertyInfo` 为强引用键、没有任何上限，插件或可回收 `AssemblyLoadContext` 场景下会把已卸载程序集的类型一直挂在字典里，`Unload()` 永不生效。阈值取 2 倍而不是目标上限本身，是为了避开「工作集略超上限 → 每来一个新条目就清一次表 → 之后每条都要重新反射构建」的稳态重算：一次清空的 O(n) 摊到随后再插入的 n 条上，摊还约为每插入一次。清空后旧类型的强引用消失，程序集随即可回收。

### Changed (Breaking)

- 实例级选项独立成 `AxiosClientOptions`：代理 / 凭据 / Cookie / 重定向次数 / 连接池 / 并发容量这七项决定连接层与闸门的形态，构造实例时定型，所以从 `AxiosConfig` 上移除（`Transport.Proxy` / `Credentials` / `WithCredentials` / `MaxRedirects` / `Pool`、`Resilience.Concurrency.MaxInFlight` / `MaxQueue`），改为构造参数 `new AxiosClient(defaults, options)`；`Axios.Create` 的三个重载同步加 `AxiosClientOptions? options`。写错位置现在是**编译错误**，不再等到发请求时抛 `ERR_BAD_OPTION_VALUE`；带 `*Set` 标记的运行期守卫、闸门容量不一致的报错一并删除。
  - 容量类的非法值（`MaxInFlight` / `MaxQueue` 为 0 或负数）改在**构造期**报错；`MaxRedirects` 为负同样在构造期报错。
  - 新增**只读**快照 `AxiosClient.Options`：已回填连接池的实际生效值（代理 / 凭据 / 重定向 / Cookie / 池选项），读到就是真实行为；构造结束时冻结，之后赋值**抛 `ERR_BAD_OPTION_VALUE`**（此前是「静默改了不生效」——那和本项「写错位置要立刻报错」的立论自相矛盾）。
  - `MaxQueue` 必须与 `MaxInFlight` 同时给：没有闸门就没有队列，只写排队深度现在构造期报错，不再静默忽略。
  - `AxiosClientOptions.Clone()` 转为公开：容器 / 门面是「先登记、后惰性创建」，登记时留一份副本，避免两者之间被改。
  - 闸门容量在**构造期定稿**：闸门仍是首个受限请求才创建，但容量取构造时的 `MaxInFlight` / `MaxQueue`。否则「构造后改这两项」会变成「没发过请求的实例上生效、发过的实例上不生效」——同一次赋值两种结果，最难排查。
  - 按请求可变的另一半保持不变：`Transport.Profile` / `MaxRate` / `HttpVersion` / `VersionPolicy`、`Resilience.Concurrency.QueueTimeout`、`Timeout` 等仍在 `AxiosConfig` 上。
  - 包装入口同步加 `options`，否则这些能力从容器 / 声明式入口根本够不到：`AddAxiosClient(configure, options, pipeline)`、`AddApiClient(baseAddress, configureDefaults, options, configure)`、`AddApiClient(name, baseAddress, configureDefaults, options, configure)`、`new ApiClient(baseAddress, configureDefaults, options)`。`options` 都插在「配置」之后、「管线 / 门面配置」之前，按位置传旧参数会编译失败（类型不同，不会静默错位）。
- 跨终端资源从「一格槽位」改为可登记多个：`AxiosAttemptContext.Slot` → `Lease`，`WithSlot(IDisposable?)` → `WithLease(IDisposable)`（`null` 直接抛 `ArgumentNullException` —— 它是「悄悄丢掉前一个租约」的入口）。登记改为**追加**而不是覆盖，释放走登记顺序的逆序；`AxiosAttemptResult.SlotTransferred` 随之改名 `LeasesTransferred`，含义扩成「上下文里登记的租约全部已随正文转交」。此前一次尝试只能携带一个跨终端资源，闸门槽位与流式正文的所有权凭证只能靠一个布尔区分主人，第二个资源（审计会话、外部租约、批处理句柄）无处安放，抢同一格就会泄漏前一个。
  - 常见路径零分配不变：只有一个租约（绝大多数请求就是闸门槽位）时直接裸放，登记第二个才合成链表节点。
  - 释放时**收集全部失败**再抛：只有一个失败按原样重抛（类型与堆栈保留），多个失败抛 `AggregateException`。此前 `try/finally` 链里后一个异常会盖掉前一个，「谁还漏着」这条线索直接消失。

### Changed

- 响应 `Content-Type` 改为**按需解析**：装配响应时只留原始头值（不复制字符串、不构造 `MediaTypeHeaderValue`），媒体类型与字符集在第一次读 `Config.Response.MediaType` / `CharSet`、或正文转换真正用到它们时才拆一次并缓存；引号、转义、多值等形态回落 BCL 解析，结果与 `MediaTypeHeaderValue.TryParse` 逐字一致。字节（`byte[]` / `arraybuffer`）与流式响应一次都不解析，`Content-Type` 缺失 / 非法的判定口径不变。要读正文的响应仍需主值与字符集两段子串（文本、标量、强类型业务对象都一样：UTF-8 字节判定要看字符集），省下的是每条响应一整个媒体类型对象图（256 B；要读正文时换成 88 B 子串）。对外契约不变。
- 快捷方法的 `data` 位置现在拦住「放错位置的参数」：把 `AxiosConfig` 传进 `data` 抛 `ERR_BAD_OPTION_VALUE`（消息指路 `RequestAsync(config)`），与原有的 `CancellationToken` 护栏同一口径。此前 `object? data` 会把配置对象当正文：静态门面 `Axios.GetAsync(url, config)`（门面没有 `(url, config)` 形态）与表单动词 `PostFormAsync(url, config)` 都能编译，结果要么把配置序列化进正文，要么退化成 `ERR_BAD_REQUEST`「正文序列化失败」——错误码与真实原因无关，而同一行代码在 `AxiosClient` 上走的却是配置形态。**重载数量不变**：把 `(url, config)` 或 `(url, ct)` 删掉会让这两类错用从「明确报错」退化成「静默把配置 / 令牌当正文发出去」，护栏只能加在现有重载内部，不靠删重载来消除歧义。
- `AxiosException.Message` 对非 2xx 响应追加请求方法与 URL（默认剥 query / userinfo / fragment）与尝试序号，便于开了重试 / 换端点 / 对冲后定位打到哪个端点、第几次尝试；前缀 `Request failed with status code {status}` 保持不变。路径段本身不脱敏，凭据放在 path 里的接口需自行脱敏（见 README「错误处理」）。
- 声明式装配失败的消息补上「哪一步、哪个字段」：`Request could not be built from 'UserRequest' (query parameter 'UserRequest.Page'): ...`。此前路径 / 查询 / 请求头取值或格式化抛错时只有一句底层消息，`targetName` 只到模型名；现在字典映射还带上键名（如 `UserRequest.Extra[boom]`）。错误码仍是 `ERR_BAD_REQUEST`，`Cause` 仍是该字段上真正抛出的原始异常，未多包一层。
- 请求模型**声明非法**改走统一出口：`ApiClient.RequestAsync` / `GetUri` / `RequestStreamAsync` 现在抛 `AxiosException(ERR_BAD_OPTION_VALUE)`，原始异常在 `Cause`。此前是裸 `InvalidOperationException`（且发生在 `BuildConfig` 之前，不受绑定错误那条出口保护），`catch (AxiosException)` 抓不到。`AxiosAotMetadataException` 例外，仍原样抛出（专供「源生成器没启用」，调用方可能专门捕获）。
- 请求体压缩不再多复制一份成品：压缩后直接引用缓冲区有效段（此前是 `MemoryStream.ToArray()`），压缩期间的峰值从「缓冲区 + 成品」两份降到一份。配置与线上字节不变。
- `ApiClient.Create<T>()` / `Create(Type)` 的接口声明校验改走统一出口：接口定义非法（缺路由、多个正文、`[Part]` 未配 `[Multipart]`、`[Stream]` 用在非流式方法、参数重名…）现在抛 `AxiosException(ERR_BAD_OPTION_VALUE)`，原始异常在 `Cause`。此前是裸 `InvalidOperationException` —— 同一类「声明写错」，模型式 `catch (AxiosException)` 抓得住、接口式会漏。`AxiosAotMetadataException` 仍原样抛出。
- 非 2xx 消息的请求上下文补齐到**全部**分支：`MaxRedirects = 0`（`ERR_BAD_REQUEST`）与重定向超限（`ERR_TOO_MANY_REDIRECTS`）此前只有状态码与调参提示，现在同样带方法 / URL / 尝试序号 —— 这两类正是开了换端点 / 对冲后最需要定位「打到哪个端点」的场合。
- 仓库源码的语言版本固定为 **C# 12**（每个项目显式 `<LangVersion>12.0</LangVersion>`，此前是 `latest`）：此前源码里有依赖 C# 14 隐式 span 转换与 C# 13 `async` 内 `ref` 局部变量的写法，用 .NET SDK 8 / 9 或另一套 Roslyn 编译会失败。现在 .NET SDK 8.0 及以上都能构建。对外 API 与产物行为不变。


## [1.6.0] - 2026-11-13

### Added

- 原始响应正文捕获：新增 `AxiosResponse.RawText` / `AxiosResponse.RawBody`，以及 `Observability.OnRawResponse` 回调，可在日志、错误或拦截器里拿到服务端返回的原始响应文本 / 字节。
- 支持 .NET Standard 2.0：多目标扩展为 `netstandard2.0;net6.0;net8.0`（`netstandard2.0` 目标不做裁剪 / AOT 标注）。

### Changed

- 强化错误模型：`AxiosException` 携带响应正文等上下文（`ResponseBody` 等价于 `Response.RawText`），并补充错误模型相关测试与 `ResponseReader` 优化。

### Fixed

- 修复 `netstandard2.0` 目标下的兼容性缺陷，以及源生成器模块初始化（`ModuleInitializerShim`）相关问题。

## [1.5.1] - 2026-11-09

### Changed

- 简化授权 / 凭据管理：调整 `ApiCredential` 与凭据提供方适配，移除内部 `AuthorizationHeader`，凭据到 `Authorization` 头的映射统一收敛到内核完成。

### Fixed

- 修复 CI 上因时序调度过长导致的测试用例假失败（仅涉及测试）。

## [1.5.0] - 2026-11-07

### Changed (Breaking)

- 配置面重构：`Callbacks` → `Observability` 加顶层 `ValidateStatus`；`Connection` → `Transport`；`Cache` → `Caching`；`Data` → `Body`；顶层 `Retry` / `Circuit` / `Hedging` / `Failover` / `Idempotency` / `TotalTimeout` 等治理项统一收敛到 `Resilience.*`。
- 凭据模型统一为 `Transport.Auth = AxiosCredential.Basic / Bearer / Header / Provider`，一个请求只取一种形态；显式 `Authorization` 头与凭据并存等冲突改报 `ERR_BAD_OPTION_VALUE`。
- 若干行为收紧：401 重放的正文范围、流式读取消改为 `ERR_CANCELED`、重定向超限改用独立错误码 `ERR_TOO_MANY_REDIRECTS` 等。

### Added

- 新增 `Axios.Create(baseAddress)` 与 `client.Create<T>()` 便捷入口。

> 这是 `1.x → 1.5` 的一次较大破坏性重构，完整的迁移对照见 [`docs/breaking-changes-1.5.md`](docs/breaking-changes-1.5.md)。
