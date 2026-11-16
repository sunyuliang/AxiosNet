# Changelog

本项目的所有重要变更记录在此文件。

格式基于 [Keep a Changelog](https://keepachangelog.com/)，版本号遵循 [Semantic Versioning](https://semver.org/)。

> **说明：本文件从 `1.5.0` 起建立，此前版本未追述。** 更早的历史（`1.4.x` 及以前）请以 git 提交历史为准；`1.x → 1.5` 的完整迁移对照见 [`docs/breaking-changes-1.5.md`](docs/breaking-changes-1.5.md)（英文：[`docs/breaking-changes-1.5.en.md`](docs/breaking-changes-1.5.en.md)）。每个条目只记录能从版本 tag 与对应提交中取证的改动。

## [Unreleased]

### Added

- 源生成器新增两条编译期诊断：`AXA016`（路由模板里的 `{name}` 没有任何成员用 `[Path]` 绑定，名字比对大小写不敏感）与 `AXA017`（多个成员显式标了 `[Body]`）。二者此前只在运行期由绑定构建器报错。
- 缓存条目可导出与重建：新增 `AxiosShortCircuit.Restore(...)`，供自定义 `IAxiosCacheStore` 从自己的存储格式重建条目；新增只读视图 `MultiValueHeaderValues` / `CachedAt` / `CachedAge`，用于把多值响应头、落库时刻与落库龄期写进自己的存储格式。此前这三项在包外读写不到，自建 Redis / 共享缓存会静默丢失多条 `Set-Cookie` / `Vary` 的全部值、`ETag` / `Last-Modified` 的优先来源，以及 `Age` 的推进。
- 新增 `AxiosResponse<T>.DataOrThrow()`：正文为空时抛 `AxiosException(ERR_BAD_RESPONSE)`（消息带期望的类型名，异常带 `Config` / `Response`），用于替代 `Data!` —— 后者遇到空正文会退化成不带请求上下文的 `NullReferenceException`。空正文合法时继续用 `Data`。
- 反射缓存（对象形状 / 属性元数据）新增条目上限：条目数达到 `ClearThreshold`（目标上限的 2 倍：形状 4096 条、属性元数据 8192 条）时整体清空。此前这两份缓存以 `Type` / `PropertyInfo` 为强引用键、没有任何上限，插件或可回收 `AssemblyLoadContext` 场景下会把已卸载程序集的类型一直挂在字典里，`Unload()` 永不生效。阈值取 2 倍而不是目标上限本身，是为了避开「工作集略超上限 → 每来一个新条目就清一次表 → 之后每条都要重新反射构建」的稳态重算：一次清空的 O(n) 摊到随后再插入的 n 条上，摊还约为每插入一次。清空后旧类型的强引用消失，程序集随即可回收。

### Changed

- `AxiosException.Message` 对非 2xx 响应追加请求方法与 URL（默认剥 query / userinfo / fragment）与尝试序号，便于开了重试 / 换端点 / 对冲后定位打到哪个端点、第几次尝试；前缀 `Request failed with status code {status}` 保持不变。
- 声明式装配失败的消息补上「哪一步、哪个字段」：`Request could not be built from 'UserRequest' (query parameter 'UserRequest.Page'): ...`。此前路径 / 查询 / 请求头取值或格式化抛错时只有一句底层消息，`targetName` 只到模型名；现在字典映射还带上键名（如 `UserRequest.Extra[boom]`）。错误码仍是 `ERR_BAD_REQUEST`，`Cause` 仍是该字段上真正抛出的原始异常，未多包一层。
- 请求模型**声明非法**改走统一出口：`ApiClient.RequestAsync` / `GetUri` / `RequestStreamAsync` 现在抛 `AxiosException(ERR_BAD_OPTION_VALUE)`，原始异常在 `Cause`。此前是裸 `InvalidOperationException`（且发生在 `BuildConfig` 之前，不受绑定错误那条出口保护），`catch (AxiosException)` 抓不到。`AxiosAotMetadataException` 例外，仍原样抛出（专供「源生成器没启用」，调用方可能专门捕获）。
- 请求体压缩不再多复制一份成品：压缩后直接引用缓冲区有效段（此前是 `MemoryStream.ToArray()`），压缩期间的峰值从「缓冲区 + 成品」两份降到一份。配置与线上字节不变。
- `ApiClient.Create<T>()` / `Create(Type)` 的接口声明校验改走统一出口：接口定义非法（缺路由、多个正文、`[Part]` 未配 `[Multipart]`、`[Stream]` 用在非流式方法、参数重名…）现在抛 `AxiosException(ERR_BAD_OPTION_VALUE)`，原始异常在 `Cause`。此前是裸 `InvalidOperationException` —— 同一类「声明写错」，模型式 `catch (AxiosException)` 抓得住、接口式会漏。`AxiosAotMetadataException` 仍原样抛出。
- 非 2xx 消息的请求上下文补齐到**全部**分支：`MaxRedirects = 0`（`ERR_BAD_REQUEST`）与重定向超限（`ERR_TOO_MANY_REDIRECTS`）此前只有状态码与调参提示，现在同样带方法 / URL / 尝试序号 —— 这两类正是开了换端点 / 对冲后最需要定位「打到哪个端点」的场合。


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
