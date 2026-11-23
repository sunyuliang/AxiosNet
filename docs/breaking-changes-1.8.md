# AxiosNet 1.8 破坏性变更

本文是 **1.7 → 1.8** 的迁移对照，适用于从 `AxiosNet` 1.7.x 升级到 1.8.x 的调用方；内容与 1.8.0 起发布的公共面一致。

两处变化都朝「少写一层」的方向走：声明式默认交付正文，快捷方法的第二个参数由动词决定。

## 1. 声明式默认返回正文

`RequestAsync` 现在返回 `Task<TResponse>`；要状态码 / 响应头 / 请求消息，改用新增的 `RequestFullAsync`（同名两个重载，`ApiClient` 与 `AxiosClient` 扩展方法都有）。

| 1.7 | 1.8 |
| --- | --- |
| `Task<AxiosResponse<T>> ApiClient.RequestAsync<T>(IApiRequest<T>, …)` | `Task<T> ApiClient.RequestAsync<T>(IApiRequest<T>, …)` |
| — | `Task<AxiosResponse<T>> ApiClient.RequestFullAsync<T>(IApiRequest<T>, …)`（新增） |
| `Task<AxiosResponse<T>> AxiosClientDeclarativeExtensions.RequestAsync<T>(…)` | `Task<T> …RequestAsync<T>(…)` + `…RequestFullAsync<T>(…)`（新增） |
| `var user = (await client.RequestAsync(new GetUserRequest { Id = 5 })).Data!;` | `var user = await client.RequestAsync(new GetUserRequest { Id = 5 });` |
| `var rsp = await client.RequestAsync(request);`（读 `rsp.Status` / `rsp.Headers` / `rsp.Request`） | `var rsp = await client.RequestFullAsync(request);` |

- 正文为空时返回 `default(TResponse)`，与接口式 `Task<T>` 形态一致；要区分「空正文」用 `RequestFullAsync`（`Data` 为 `null`）或 `DataOrThrow()`。
- `RequestStreamAsync` 不变：流式必须交出响应（帧的释放归属在响应上）。
- 接口式 `Create<T>()` 代理不受影响（四种返回形态本来就由方法签名决定）。

## 2. 读动词的第二个参数是查询参数

`GetAsync` / `DeleteAsync` / `HeadAsync` / `OptionsAsync` 的第二个参数从「请求体」改为「查询参数」，参数名由 `data` 改为 `query`，值写入 `Params`（展平与转义规则与 `c.Params` 完全一致）。写动词（`PostAsync` / `PutAsync` / `PatchAsync` / `*FormAsync`）不变。

| 1.7 | 1.8 |
| --- | --- |
| `GetAsync(url, new { page = 1 })` → 发 GET 正文 | `GetAsync(url, new { page = 1 })` → `?page=1` |
| `DeleteAsync(url, new { reason = "x" })` → 发 DELETE 正文 | `DeleteAsync(url, new { reason = "x" })` → `?reason=x` |
| — | `GetAsync(url, c => c.Body = new { page = 1 })`（读动词带正文） |
| `DeleteAsync("orders/1", data: null)`（摘掉实例默认正文） | `DeleteAsync("orders/1", c => c.Body = null)` |
| `GetAsync(url, stream)`（当正文发） | 抛 `ERR_BAD_OPTION_VALUE`；正文写 `c => c.Body = stream` |

- `AxiosConfig` / `CancellationToken` 落到第二个参数仍立即抛 `ERR_BAD_OPTION_VALUE` 并指路（读动词的提示改成「查询参数」）。
- 命名参数调用要跟着改：`GetAsync(url, data: x)` → `GetAsync(url, query: x)`。

## 3. 示例与文档

接口式示例里的复合参数统一标 `[RootBody]`，正文不再多包一层参数名（`{ "payload": { … } }` → `{ … }`），与模型式一致。这是示例层面的对齐，绑定规则本身没变：POST / PUT / PATCH 上未标注的成员仍按键名聚合成一个对象正文。
