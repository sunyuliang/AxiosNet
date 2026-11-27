# AxiosNet 1.8 破坏性变更

本文是 **1.7 → 1.8** 的迁移对照，适用于从 `AxiosNet` 1.7.x 升级到 1.8.x 的调用方；内容与 1.8.0 起发布的公共面一致。

三处变化都朝「少写一层」的方向走：声明式默认交付正文、快捷方法的第二个参数由动词决定、正文位置改由 `[Body]` 的构造形态表达。

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

## 3. 正文绑定：`[RootBody]` 删除，`[Body]` 分两种形态

正文位置不再由两个特性区分，也不再由「正文来源个数」推断——`[Body]` 的构造形态就是答案：

| 写法 | 含义 |
| --- | --- |
| `[Body]` | 该成员的值就是整份正文：原样发送、不加键壳，且必须独占 |
| `[Body("名")]` | 聚合正文里的一个键（`{ "名": 值, … }`），可多个 |
| `[Body("")]` / 未标注成员 | 键名回退 `[JsonPropertyName]`，再回退属性名 / 参数名 |

| 1.7 的写法 | 1.8 的写法 |
| --- | --- |
| `[RootBody] x`（1.7.2）/ `[Body] x`（1.7.0–1.7.1）：整份正文 | `[Body] x` |
| `[Body] x`（1.7.2）：聚合正文里的一个键 | `[Body("名")] x`；键名沿用原规则时写 `[Body("")] x` |
| `RootBody()` builder 方法 | `Body()`（无参 = 整份正文）、`Body("名")` |
| `BodyAttribute(string name = "")`，`Name` 非空 | `BodyAttribute()` / `BodyAttribute(string name)`，`Name` 为 `string?`（`null` = 整份正文） |

- 从 1.7.2 升级时裸 `[Body]` 一律要加名字（1.7.2 已把「整份正文」搬到 `[RootBody]`）；从 1.7.0 / 1.7.1 升级时裸 `[Body]` 不用动，把 `[RootBody]` 换成 `[Body]` 即可。
- 「未标注成员在 POST / PUT / PATCH 上进正文」这条规则没变；变的是它们只**按键聚合**，不与整份正文混用。
- 无参 `[Body]` 与 `[Body("名")]`、body 方法上的未标注成员、另一个无参 `[Body]`、`[Multipart]` 同用是构建期错误：请求模型由源生成器在编译期报 `AXA017`；接口式由绑定构建器在 `Create<T>()` 时抛 `ERR_BAD_OPTION_VALUE`，未启用生成器的模型式在首次请求时抛同一个码。两边消息一致，都给出「给别的成员换通道」与「把整份正文改成 `[Body("名")]`」两条出路。
- 整份正文是数组 / 标量 / 流时不必再为它造子类：直接把 `[Body]` 标在该成员上。
- 接口式示例里的复合参数随之从 `[RootBody]` 改为 `[Body]`，正文不再多包一层参数名（`{ "payload": { … } }` → `{ … }`），与模型式一致。
