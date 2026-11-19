# AxiosNet 与主流 .NET HTTP 组件综合评比

对比对象：`HttpClient`（BCL 基线）、Refit、RestSharp、Flurl、AxiosNet。
维度：功能覆盖、性能、易用性、工程化与生态。文末给出加权评分与选型建议，并说明性能口径的边界。

- 参评版本：AxiosNet 1.7.1（仓库源码，net8.0 资产）；Refit 16.4.0；RestSharp 113.0.0；Flurl.Http 4.0.2；HttpClient 取 .NET 8.0.31 BCL。
- 性能数据来自本仓库 `test/AxiosNet.Comparison` 的现场运行：2026-11-19 18:24–18:27 连续三次独立运行（8 并发 × 2s × 6 轮），另用 2026-11-01 与 2026-11-19 16:46 的历史窗口做交叉印证，不是引用旧结果。
- 功能与易用性结论来自各库公开 API 与本仓库源码，属于定性判断，已在文中标注。
- 三次运行的全部实现 CPU 份额为 43%–58%，**均达到工具门槛（42%）**，绝对吞吐与相对比值都可判定；结论取三次运行的中位与区间。

---

## 一、结论先行

**性能：AxiosNet 为「裸 HttpClient」的 91.7%（三次运行 89.7%–92.6%），每请求净分配只多 0.09 KB，与 Refit（88.3%）、Flurl（84.3%）同属轻封装一档；
RestSharp 明显落后（54.1%），每请求净分配约为 HttpClient / AxiosNet 的 7.7 倍。**

| 实现 | RPS | 相对 HttpClient | 净分配/请求 | 净 CPU µs/请求 | p99 | 一句话 |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| HttpClient | 51,154 | 100% | 4.23 KB | 58 | 279 µs | 天花板，但功能最少 |
| Refit | 45,181 | 88.3% | 5.68 KB | 83 | 317 µs | 声明式接口，源生成开销小 |
| **AxiosNet** | **47,372** | **92.6%** | **4.32 KB** | **95** | **294 µs** | **功能最全，成本可控** |
| Flurl | 42,739 | 83.5% | 8.12 KB | 97 | 352 µs | 链式写法，轻封装 |
| RestSharp | 27,684 | 54.1% | 32.69 KB | 172 | 2,587 µs | 功能全面但分配重 |
| RawSocket | 64,931 | 126.9% | 0 B | 0 | 226 µs | 回环服务器上限，不是客户端 |

> 表中为三次运行中第三次（18:27）的 6 轮中位数；三次运行的 AxiosNet 比值分别为 91.7% / 89.7% / 92.6%，
> 全部实现 CPU 份额 43%–58%，均达标。跨会话对照见 §4.3。
> p99 受单轮 GC 影响较大，不作为单独结论；判断实现差异请看吞吐比值与分配量。

**功能：只有 AxiosNet 把重试、熔断、并发对冲、备用端点、响应缓存、在途并发闸门、限流、流式分帧、
声明式 API 与 OpenAPI 导入、内置遥测集中在一个零第三方依赖的包里。** 其余四家在这些能力上
要么完全没有，要么需要叠加 Polly / 自写 Handler / 引入缓存库。

**易用性：单次调用最省代码的是 Flurl（一行链式）；端点多、要类型安全的是 Refit（写接口）；
AxiosNet 与 axios 写法同构，并额外提供「模型式 / 接口式 / OpenAPI 导入」三种声明式入口。**

**综合评分（加权，满分 5）：AxiosNet 4.6、Refit 4.1、Flurl 3.5、HttpClient 3.3、RestSharp 3.2。**
评分口径与权重见 §6，它是判断不是测量，换权重会改变名次。

---

## 二、参评对象与生态位

先澄清一件事：**「HTTP 组件」不是一个层次的概念。** `IHttpClientFactory` 管生命周期，Polly 管弹性，
`System.Net.Http.Json` 管序列化，它们与「客户端库」是组合关系，不是替代关系。

| 组件 | 版本 / 形态 | 在技术栈中的位置 | 是否进入性能对比 |
| --- | --- | --- | --- |
| HttpClient | BCL（.NET 8.0.31） | 传输与请求模型底座 | 是（基线） |
| IHttpClientFactory | Microsoft.Extensions.Http | Handler 生命周期 / DI 工厂 | 否（与客户端正交，可套在任一方） |
| Polly v8 / Microsoft.Extensions.Http.Resilience | 弹性库 | 重试 / 熔断 / 超时的策略层 | 否（AxiosNet 内置同类能力） |
| Refit | 16.4.0 | 声明式 REST 客户端（源生成） | 是 |
| RestSharp | 113.0.0 | 传统请求对象模型的客户端 | 是 |
| Flurl | Flurl.Http 4.0.2 | 链式 URL 构造 + HTTP 客户端 | 是 |
| AxiosNet | 1.7.1 | axios 风格客户端 + 声明式 API + 治理 | 是 |
| WebClient / HttpWebRequest | BCL 遗留 API | 旧代码兼容 | 否（.NET 6+ 已标记过时，不建议新项目使用） |
| gRPC / GraphQL 客户端 | 协议栈 | 与 REST 客户端不是替代关系 | 否 |

AxiosNet 的定位不是「又一个 HttpClient 包装」：它同时占据**客户端库 + 弹性策略 + 声明式客户端 +
OpenAPI 代码生成 + 遥测**五层，这也是功能表里差距的来源。

### 2.1 版本与用法（性能对比中的调用方式）

| 实现 | 在本仓库基准里的用法 |
| --- | --- |
| HttpClient | `PostAsJsonAsync` + `ReadFromJsonAsync<User>`（基线） |
| Refit | `RestService.ForGenerated<IUserApi>` + `[Body] User`（只走源生成，不走反射回退） |
| RestSharp | 单例 `RestClient` + 每请求新建 `RestRequest` + `AddJsonBody` + `ExecuteAsync<User>` |
| Flurl | 单例 `FlurlClient` + `Request(url).PostJsonAsync(...).GetJsonAsync<User>()` |
| AxiosNet | 默认 `AxiosClient` + `PostAsync<User>(route, model)`（连接池 + 默认配置，不做任何调优） |
| RawSocket | 裸 TCP 发预编码 POST（**服务端基线**，不是被对比的客户端） |

> 四个第三方实现与 AxiosNet 都注入了同一份「与 AxiosNet 默认连接池逐项对齐」的
> `SocketsHttpHandler`，因此测到的是实现差异，不是默认值差异。

---

## 三、功能对比

### 3.1 请求能力

| 能力 | HttpClient | Refit | RestSharp | Flurl | AxiosNet |
| --- | --- | --- | --- | --- | --- |
| 强类型反序列化 | `GetFromJsonAsync<T>` | 接口返回类型 | `ExecuteAsync<T>` | `GetJsonAsync<T>` | `GetAsync<T>` |
| 原文 / `dynamic` / `JsonElement` | 需自己处理 | 需自己处理 | 需自己处理 | 需自己处理 | 四种形态内置 |
| 声明式端点 | 无 | **接口 + 特性**（源生成/反射两档） | 无 | 无 | **模型式 + 接口式** |
| OpenAPI 导入生成客户端 | 无 | 无（需第三方 Refitter） | 无 | 无 | **内置 `axiosnet-gen`** |
| 只拼 URL（不发请求） | 手写 | 无 | 无 | URL 构建器 | `GetUri(...)` |
| 查询参数对象化 | 手写 | `[Query]` / 参数展开 | `AddQueryParameter` | `SetQueryParams` | `Params`（对象/字典/集合） |
| multipart 表单 + 文件 | 手写 | 支持 | 支持 | 支持 | `PostFormAsync` / `FormFile` |
| 流式请求体（边算边发） | 需自定义 `HttpContent` | 有限 | 有限 | 有限 | `IAsyncEnumerable<byte[]>` / 内存块 |
| 流式响应分帧（SSE / NDJSON / JSON 数组） | 需自己读流 | 返回 `Stream` 自行处理 | 返回流自行处理 | 返回流自行处理 | `RequestStreamAsync<T>` 内置三种格式 |
| 上传/下载进度 | 需自定义 | 无 | 有进度事件 | 无 | 回调内置 |
| 字节限速 / 体积上限 | 无 | 无 | 无 | 无 | `Limits.MaxRate` / 大小上限 |
| 请求体压缩（gzip/deflate/br） | 手写 | 无 | 无 | 无 | `RequestCompression` |
| 统一凭据扩展点 + 令牌缓存 / 提前刷新 / 401 重放 | 无 | 部分（授权头） | 部分（OAuth 助手） | 无 | `IApiCredentialProvider` / `AccessTokenManager` |
| 声明式鉴权（`[Authorize]` / `[AllowAnonymous]` + 401 自动重放） | 无 | 无 | 无 | 无 | 模型 / 接口级特性 + `ApiClient.CredentialProvider` |

### 3.2 弹性与治理（本表是差距最大的一块）

| 能力 | HttpClient | Refit | RestSharp | Flurl | AxiosNet |
| --- | --- | --- | --- | --- | --- |
| 重试（退避/抖动/`Retry-After`） | 需 Polly | 需 Polly | 无内置 | 无内置 | **内置策略** |
| 全局重试预算 | 需自写 | 需自写 | 无 | 无 | **`RetryBudgetPolicy`** |
| 熔断（Closed/Open/HalfOpen） | 需 Polly | 需 Polly | 无 | 无 | **内置状态机** |
| 并发对冲（Hedging） | 无 | 无 | 无 | 无 | **内置** |
| 备用端点 / 端点健康记忆 | 自写 | 自写 | 自写 | 自写 | **内置** |
| 幂等键（重试/换端共用） | 自写 | 自写 | 自写 | 自写 | **内置** |
| 在途并发闸门 + 排队上限 | 自写 | 自写 | 自写 | 自写 | **内置** |
| 响应缓存 + 单飞 + 条件复用 | 需缓存库 | 需缓存库 | 需缓存库 | 需缓存库 | **内置** |
| 短路响应（mock / 回放） | 自写 | 自写 | 自写 | 自写 | **内置** |
| 单次预算 vs 整条请求预算分离 | 只有 `Timeout` | 继承 HttpClient | 有超时 | 有超时 | `Timeout` + `TotalTimeout` |

> 「需 Polly」指需要额外引入第三方弹性库并把它装进 `DelegatingHandler` 链；这本身是可行方案，
> 但它是**另一个包 + 另一套配置模型**，与本表里「同一次配置调用里就能写」不是一回事。

### 3.3 连接、观测与工程化

| 能力 | HttpClient | Refit | RestSharp | Flurl | AxiosNet |
| --- | --- | --- | --- | --- | --- |
| 连接池复用 | `SocketsHttpHandler` | 继承 | 继承 | 继承 | 按 origin 自建池，可命名共享 |
| 多出口（代理/凭据/证书/Cookie 会话分档） | 多 Handler 手写 | 多 Handler 手写 | 有限 | 有限 | **传输档 `Transport`** |
| HTTP/2 / HTTP/3 | 支持 | 支持 | 通过底层 | 通过底层 | 支持，可降级 |
| DI 注册 | `IHttpClientFactory` | `Refit.HttpClientFactory` | 手写 | 手写 | `AddAxiosClient` / `AddApiClient` |
| 遥测 | 内置 `HttpClient` 事件 | 继承 | 继承 | 继承 | **`ActivitySource` + `Meter` + `DiagnosticListener`，尝试级事件** |
| 统一错误模型 | `HttpRequestException` 等 | `ApiException` | `RestResponse` 状态 | `FlurlHttpException` | `AxiosException` + 稳定 `Code` |
| Native AOT / 裁剪 | 原生支持 | 源生成支持 | 反射较重，需配置 | 反射较重，需配置 | `IsTrimmable` / `IsAotCompatible`，生成器烘焙 |
| 第三方运行时依赖 | 0（BCL） | 1 个包 | 1 个包 | 2 个包 | **0（net6/net8 资产只用 BCL）** |

> AxiosNet 的 `netstandard2.0` 兼容资产为 .NET Framework 4.7.2+ / .NET Core 2.0+ 兜底，
> 该资产额外引用 9 个微软第一方 BCL 兼容包；`net6.0` / `net8.0` 资产为零第三方运行时依赖。
> Refit/Flurl 还各自带源生成器或子包，未重复计。

---

## 四、性能对比

### 4.1 口径（不要跳过这一节）

| 项 | 值 |
| --- | --- |
| 时间 / 环境 | 2026-11-19 18:24–18:27 +08:00（三次独立运行）；Windows 10.0.19045；18 逻辑核；.NET 8.0.31；Server GC=False；Release |
| 负载 | 每次运行 8 并发常驻 × 2 s 窗口 × 6 轮，轮间整体轮转实现顺序，预热 3 s + 冷却 2 s |
| 请求 | HTTP/1.1 明文回环 **POST**，正文 51 B JSON（`User`），反序列化回同一模型 |
| 延迟 | `Stopwatch.GetTimestamp` 包住「序列化 + 发请求 + 读正文 + 反序列化」，含服务端处理与回环 RTT |
| 分配 | 进程级 `GC.GetTotalAllocatedBytes` ÷ 完成请求数；`净分配` 已减掉 RawSocket 基线 |
| CPU | 进程级 `TotalProcessorTime` ÷ 完成请求数；`净 CPU` 已减掉 RawSocket 行 |
| 汇总 | 每列取 6 轮的**中位数**；正文列第三次运行的原始值，并用三次运行的比值区间交叉核对 |
| CPU 份额 | 三次运行的全部实现为 43%–58%，均高于 42% 门槛，无需「不可判定」标记 |

### 4.2 本次结果

| 实现 | RPS | p50 (µs) | p99 (µs) | max (µs) | 分配/请求 | 净分配/请求 | CPU | CPU µs/请求 | 净 CPU µs/请求 | RPS vs HttpClient |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| HttpClient | 51,154 | 144 | 279 | 15,950 | 4.48 KB | 4.23 KB | 53% | 187 | 58 | 100% |
| Refit | 45,181 | 161 | 317 | 17,322 | 5.93 KB | 5.68 KB | 53% | 212 | 83 | 88.3% |
| RestSharp | 27,684 | 210 | 2,587 | 17,895 | 32.94 KB | 32.69 KB | 45% | 300 | 172 | 54.1% |
| Flurl | 42,739 | 168 | 352 | 16,721 | 8.38 KB | 8.12 KB | 53% | 225 | 97 | 83.5% |
| AxiosNet | 47,372 | 156 | 294 | 16,096 | 4.58 KB | 4.32 KB | 58% | 223 | 95 | 92.6% |
| RawSocket | 64,931 | 116 | 226 | 18,304 | 262 B | 0 B | 46% | 128 | 0 | 126.9% |

全部行 CPU 份额 ≥ 42%，数值可直接比较。三次独立运行的相对值与离散度：

| 运行 | HttpClient | Refit | RestSharp | Flurl | AxiosNet | RawSocket | AxiosNet 轮间波动 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 18:24 #1 | 100% | 90.5% | 57.6% | 89.3% | 91.7% | 133.6% | ±13.9% |
| 18:26 #2 | 100% | 84.5% | 53.9% | 84.3% | 89.7% | 125.6% | ±5.1% |
| 18:27 #3 | 100% | 88.3% | 54.1% | 83.5% | 92.6% | 126.9% | ±4.2% |
| 中位 | 100% | 88.3% | 54.1% | 84.3% | 91.7% | 126.9% | — |

> 三次运行的 AxiosNet 比值都落在 89.7%–92.6%，净分配三次都是 4.31–4.32 KB；
> 排序稳定为 HttpClient > AxiosNet > Refit > Flurl > RestSharp（RawSocket 为服务端上限）。
> 单轮原始 RPS 与各轮离散度见 `test/AxiosNet.Comparison/results/` 下三次运行的 Markdown 报告。

### 4.3 跨会话印证

单次噪声窗口不足以下结论。下表列出三个独立会话的相对值（都以同窗口 HttpClient 为 100%）：

| 会话 | 口径 | Refit | RestSharp | Flurl | AxiosNet | CPU 份额 |
| --- | --- | ---: | ---: | ---: | ---: | --- |
| 2026-11-01 10:13 | GET，8c × 1s × 5 轮 | 92.8% | 58.1% | 89.5% | **91.5%** | 44%–49%（达标） |
| 2026-11-19 16:46 | POST，8c × 2s × 5 轮 | 96.2% | 53.7% | 88.9% | **92.3%** | 32%–41%（未达标，仅存档） |
| 2026-11-19 18:24–18:27 | POST，8c × 2s × 6 轮 × 3 次 | 88.3% | 54.1% | 84.3% | **91.7%** | 43%–58%（达标） |

三个会话的结论一致：**AxiosNet 稳定在 91%–92%，RestSharp 在 54%–58%，Flurl 在 84%–90%，Refit 在 84%–96%。**
2026-11-19 另做了 8c × 7 轮与 32c × 5 轮两次复跑，受外部负载抬升影响，AxiosNet 相对值分别落到 85.2% 与 53.9%
（后者 RawSocket 甚至低于 HttpClient，说明窗口已失效）。这两次原始报告保留在 `results/`，不作为结论依据。

### 4.4 分配与 CPU 成本

分配比吞吐更稳（本会话各轮波动通常在 1% 以内）；下面是本次口径（POST）的每请求净分配。
历史 GET 口径的绝对值整体略低，但实现之间的量级关系不变，因此不与本表逐字节对比。

| 实现 | 本次净分配/请求 | 相对 HttpClient | 说明 |
| --- | ---: | ---: | --- |
| HttpClient | 4.23 KB | 1.00× | 基线 |
| AxiosNet | 4.32 KB | 1.02× | 只多 0.09 KB：配置合并/管线/错误模型的固定成本 |
| Refit | 5.68 KB | 1.34× | 接口代理与参数绑定 |
| Flurl | 8.12 KB | 1.92× | 每次链式调用创建请求对象 |
| RestSharp | 32.69 KB | 7.73× | 每请求 `RestRequest` + 自身序列化路径 |

`净 CPU µs/请求` 是成本列：HttpClient 58、AxiosNet 95、Refit 83、Flurl 97、RestSharp 172。
它由两个大数相减得出（同进程回环服务端占去大部分 CPU），相对误差比分配大，看量级不看小数点。

### 4.5 怎么读

- **AxiosNet 的约 8% 吞吐差，换来的是「一个包替代 Polly + 缓存库 + 自写 Handler 链」。**
  三次中位为 91.7%，同一窗口里它的 CPU 份额（58%）与 HttpClient（53%）接近，p50 只差 12 µs，
  说明差的不是传输层，而是每次请求上多走的配置合并、拦截器/转换链、统一错误模型与遥测判定。
- **AxiosNet 与 Refit 同档（中位 91.7% vs 88.3%），但分配明显更低（4.32 KB vs 5.68 KB）。**
  两者的比值差小于轮间波动，不应据此宣称谁「更快」；AxiosNet 的优势在功能与分配，不在原始吞吐。
- **RestSharp 的 p99（2,587 µs）约为其它客户端的 7–9 倍，与它 32.69 KB/请求的净分配是同一件事的两个侧面**：
  每请求新建 `RestRequest` + 自身序列化路径带来更高 Gen0/Gen2 压力，窗口内 GC 暂停直接体现在尾延迟上。
  用不经过 socket 的伪 Handler 单独复测，`ExecuteAsync<User>` 单次约 29.4 KB，同量级。
- **RawSocket 不是客户端**：它是回环服务器的上限，HttpClient 已到它的 79%，说明在这套负载下
  瓶颈开始向服务端转移。
- **只看比值，不看绝对值**：同一台机器上两次运行绝对 RPS 可以差一倍（功耗策略、P/E 核、后台负载）。
  本次三次运行的 AxiosNet 比值为 91.7% / 89.7% / 92.6%，跨会话（GET 91.5%、POST 92.3%）也落在同一区间；
  跨版本比较必须同机、同 Tag、同机器状态。
- **CPU 份额门槛不是装饰**：低于 42% 时，机器的吞吐上限不由被测实现决定，绝对值不可判定。
  本次三次运行的全部实现为 43%–58%，因此上表数值可直接比较。

### 4.6 这套性能数据不覆盖什么

- 只测 HTTP/1.1 明文回环「POST 一个 51 B 的 JSON 负载并读回同一模型」。TLS、HTTP/2、HTTP/3、
  大正文上传、流式响应都不在范围内（大正文与分帧看 `test/AxiosNet.Benchmarks`）。
- 服务端与客户端同进程，所以分配指标必须减基线，CPU 指标含服务端。
- 每个实现按各自文档的惯用写法，不做「为跑分而调优」（例如 RestSharp 每请求新建 `RestRequest`
  就是它的正常用法）。
- Refit 只测源生成模式；其反射回退路径是另一档性能，不在本表内。
- AxiosNet 只测默认配置；连接池/缓存/重试等治理选项开启后会改变成本，需另测。

---

## 五、易用性对比

以「GET 一个 JSON 成强类型对象」为例，各库的惯用写法：

```csharp
// HttpClient
var http = new HttpClient { BaseAddress = new Uri("https://api.example.com/") };
var user = await http.GetFromJsonAsync<User>("users/1");

// Refit
public interface IUserApi
{
    [Get("/users/{id}")]
    Task<User> GetUserAsync(int id, CancellationToken ct = default);
}
var api = RestService.For<IUserApi>("https://api.example.com");
var user = await api.GetUserAsync(1);

// RestSharp
var client = new RestClient("https://api.example.com");
var res = await client.ExecuteAsync<User>(new RestRequest("users/1"));
if (!res.IsSuccessful) throw new InvalidOperationException(res.ErrorMessage);
var user = res.Data;

// Flurl
var user = await "https://api.example.com/users/1".GetJsonAsync<User>();

// AxiosNet
var http = Axios.Create(c =>
{
    c.BaseAddress = "https://api.example.com/";
    c.Timeout = TimeSpan.FromSeconds(10);
});
var user = (await http.GetAsync<User>("users/1")).Data;
```

### 5.1 Flurl 的「省」省在哪：一个带查询参数、鉴权与超时的例子

场景：`GET /users?page=2&tags=dev&tags=ops`，带 Bearer 令牌，10 秒超时，返回 `List<User>`。

```csharp
// HttpClient：请求消息、鉴权头、超时 CTS、读正文、反序列化，五步都自己写
var url = "https://api.example.com/users?page=2&tags=dev&tags=ops";
using var req = new HttpRequestMessage(HttpMethod.Get, url);
req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
using var res = await http.SendAsync(req, cts.Token);
res.EnsureSuccessStatusCode();
var users = await res.Content.ReadFromJsonAsync<List<User>>(cts.Token);

// Refit：接口声明一次，调用一行；但查询数组、鉴权头、超时都要靠特性/HttpClient 配置表达
public interface IUserApi
{
    [Get("/users")]
    Task<List<User>> SearchAsync(
        [Query] int page,
        [Query] string[] tags,
        [Header("Authorization")] string authorization,
        CancellationToken ct = default);
}

// RestSharp：请求对象上逐个加参数与头
var req = new RestRequest("users")
    .AddQueryParameter("page", 2)
    .AddQueryParameter("tags", "dev")
    .AddQueryParameter("tags", "ops")
    .AddHeader("Authorization", $"Bearer {token}");
var res = await client.ExecuteAsync<List<User>>(req, ct);

// Flurl：URL + 查询 + 鉴权 + 超时 + 反序列化，全部链在一行
var users = await "https://api.example.com/users"
    .SetQueryParam("page", 2)
    .SetQueryParam("tags", new[] { "dev", "ops" })
    .WithOAuthBearerToken(token)
    .WithTimeout(TimeSpan.FromSeconds(10))
    .GetJsonAsync<List<User>>(cancellationToken: ct);

// AxiosNet：配置对象集中表达，Params 与 Headers 是一个对象的两个字段
var response = await http.GetAsync<List<User>>("users", c =>
{
    c.Params = new { page = 2, tags = new[] { "dev", "ops" } };
    c.Headers.Set("Authorization", $"Bearer {token}");
}, ct);
```

声明式客户端还能把这一步收进实例：设置 `ApiClient.CredentialProvider`（`IApiCredentialProvider`），
由库按 `[Authorize]` 的 scheme 写 `Authorization` 头（或用 `ApiCredential.Header(name, value)` /
`ApiCredential.Query(name, value)` 落到任意请求头 / 查询参数）；配 `AccessTokenManager` 时自动刷新，
401 后用新凭据重放一次。标了 `[AllowAnonymous]` 的登录接口跳过整条凭据链路，因此取令牌可以直接调同一个 `ApiClient`。

再看上传文件，差距更明显：

```csharp
// HttpClient：MultipartFormDataContent + StreamContent + 手工文件名/内容类型
using var form = new MultipartFormDataContent();
form.Add(new StringContent("avatar"), "name");
using var file = File.OpenRead("photo.jpg");
var fileContent = new StreamContent(file);
fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
form.Add(fileContent, "file", "photo.jpg");
var res = await http.PostAsync("https://api.example.com/upload", form, ct);

// Flurl：同样一行
await "https://api.example.com/upload"
    .WithTimeout(TimeSpan.FromMinutes(5))
    .PostMultipartAsync(mp => mp
        .AddString("name", "avatar")
        .AddFile("file", "photo.jpg"));
```

**Flurl 省在四处**：

1. **字符串就是请求**——不需要 `HttpRequestMessage` / `HttpResponseMessage` / `HttpContent` 三件套，
   请求装饰（头、鉴权、超时）可以就地链式叠加。
2. **URL 与编码交给库**——路径拼接、查询参数、数组→重复键、百分号转义由 `Flurl.Url` 处理，
   不用手写 `Uri.EscapeDataString`，也不容易漏编码。
3. **返回类型直接是 `T`**——`GetJsonAsync<T>` 把「发送 + 读正文 + 反序列化」压成一步；
   `GetStringAsync` / `GetBytesAsync` / `DownloadFileAsync` 是同一套链式写法。
4. **一套 API 覆盖多种请求形态**——JSON、表单、multipart、下载、流都在同一个链上，不用换模型。

**Flurl 不省在三点**（与本报告功能/性能表的结论一致）：

1. **治理全在外面**：重试、熔断、并发对冲、缓存、限流都没有内置，要么接 Polly，要么自写循环。
2. **字符串 URL 没有编译期检查**：端点多、路由集中管理时不如 Refit / AxiosNet 的声明式入口。
3. **不是零成本**：每次链式调用仍会创建 `IFlurlRequest` 等对象；基准里净分配 8.12 KB/请求，
   接近 HttpClient 的两倍，也高于 AxiosNet 的 4.32 KB。

**附：JS axios 的同一例（写法参照，不是 .NET 客户端）**

```js
// axios（JS）：配置对象集中表达，和 AxiosNet 的形状几乎一一对应
const { data: users } = await axios.get('https://api.example.com/users', {
  params: { page: 2, tags: ['dev', 'ops'] },   // 默认序列化为 tags[]=dev&tags[]=ops
  headers: { Authorization: `Bearer ${token}` },
  timeout: 10_000,                              // 毫秒
});
```

```csharp
// AxiosNet：同一个配置对象模型的 C# 版本
var response = await http.GetAsync<List<User>>("users", c =>
{
    c.Params = new { page = 2, tags = new[] { "dev", "ops" } };   // 同样是 tags[]=...
    c.Headers.Set("Authorization", $"Bearer {token}");
    c.Timeout = TimeSpan.FromSeconds(10);
}, cancellationToken: ct);
```

两者的「省」是同一种省法：把 URL、参数、头、超时、响应处理收进一个配置对象/实例默认值，
而不是像 Flurl 那样在调用点就地链式装饰。差别在于 C# 侧的类型与命名约定：方法名带 `Async`、
超时用 `TimeSpan`、取消用 `CancellationToken`、请求体编码由 `ContentType` 决定（不按 data 类型猜）。

同一件事加上「失败重试 3 次 + 熔断 + 缓存」的成本：

| 实现 | 需要做什么 |
| --- | --- |
| HttpClient | 引入 Polly（或 `Microsoft.Extensions.Http.Resilience`），组一条 `DelegatingHandler` 链，再单独接缓存库；配置分散在三处 |
| Refit | 同上，Polly 挂在传给 `RestService.For` 的 `HttpClient` 上；Refit 自身不提供 |
| RestSharp | 自己在循环里重试，熔断与缓存全自写 |
| Flurl | 同上；事件钩子（`OnError`/`OnResponse`）能做记录，但不等于弹性策略 |
| AxiosNet | 同一个配置对象里写 `c.Resilience.Retry.Policy`、`c.Resilience.Circuit.Policy`、`client.Defaults.Caching`，一条请求全部生效 |

定性评价（**是判断，不是测量**）：

| 维度 | HttpClient | Refit | RestSharp | Flurl | AxiosNet |
| --- | --- | --- | --- | --- | --- |
| 上手成本 | 低（人人会） | 中（要写接口） | 中（Client/Request 两件套） | **低（一行链式）** | 低（与 axios 同构） |
| 端点多时的可维护性 | 低（模板代码多） | **高（接口集中）** | 中 | 中（URL 字符串散落） | **高（模型/接口 + OpenAPI 生成）** |
| 类型安全 | 中 | **高** | 中 | 中（字符串 URL） | **高** |
| 治理能力的组织方式 | 分散（Handler 链 + 第三方） | 分散 | 分散/自写 | 分散 | **集中（配置对象）** |
| 排障可观测性 | 中（HttpClient 事件） | 中 | 中 | 中 | **高（尝试级事件 + trace/metrics）** |
| 生态与资料 | **最广** | 广 | 广（老项目多） | 中 | 新（本仓库文档即主要资料） |

---

## 六、综合评分

权重与评分都是**定性判断**，目的是把前面的表格压成一个可讨论的结论；换权重会改变名次。

| 维度 | 权重 | 依据 |
| --- | ---: | --- |
| 功能覆盖 | 30% | §3 的三张功能表 |
| 性能效率 | 25% | §4 的 RPS、分配、CPU 成本 |
| 易用性 / 可维护性 | 20% | §5 的写法与端点组织 |
| 工程化 | 15% | DI、遥测、AOT、连接池、错误模型 |
| 生态与成熟度 | 10% | 资料、社区、历史项目、版本稳定性 |

| 实现 | 功能 | 性能 | 易用性 | 工程化 | 生态 | 加权总分 |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| **AxiosNet** | 5.0 | 4.9 | 4.3 | 5.0 | 2.5 | **4.6** |
| Refit | 3.0 | 4.8 | 4.5 | 4.0 | 4.5 | **4.1** |
| Flurl | 3.0 | 3.5 | 4.8 | 3.0 | 3.5 | **3.5** |
| HttpClient | 2.0 | 5.0 | 2.0 | 3.5 | 5.0 | **3.3** |
| RestSharp | 4.0 | 1.5 | 3.5 | 3.0 | 4.5 | **3.2** |

**怎么读这张表**：

- **AxiosNet 的性能分是 4.9，仅次于裸 HttpClient（5.0）**：同窗口吞吐 89.7%–92.6%（中位 91.7%），
  高于 Refit（88.3%）与 Flurl（84.3%）；净分配 4.32 KB，是除 HttpClient 外最低的一档。
  它的总分主要来自功能与工程化，但性能并没有拖后腿。
- **Refit 是「少而精」的最优解**：功能只覆盖声明式调用，性能 4.8（略低于 AxiosNet，主要是吞吐与分配都稍高）、
  易用性、生态都在线，适合不需要治理的场景。
- **Flurl 赢在单次调用的写法，输在治理与分配**；它是脚本与探索式调用的最佳选择之一。
- **HttpClient 的性能满分与生态满分，被功能/易用性的低分拉平**；它是底座，不是开箱即用的客户端方案。
- **RestSharp 的功能分不低，但性能与分配拖累了总分**；已有大量存量代码时仍值得保留。
- 生态分 2.5 是 AxiosNet 的客观短板：新库、资料集中在仓库、社区案例少。选它要接受这个风险。

---

## 七、选型建议

| 场景 | 建议 |
| --- | --- |
| 调用很少、追求零依赖与极致吞吐、团队已熟悉 BCL | `HttpClient`（+ `System.Net.Http.Json`） |
| 端点多、要类型安全、愿意写接口，且不需要内置治理 | Refit |
| 已有大量表单/文件/OAuth 老代码，或需要它成熟的请求模型 | RestSharp（但要接受分配与尾延迟代价） |
| 脚本、探索式调用、URL 拼接多 | Flurl |
| 需要重试/熔断/对冲/备用端点/缓存/限流/观测中的多项，或希望从 OpenAPI 一键生成客户端 | AxiosNet |
| 已经用了 axios（JS）并迁移到 .NET，想保持写法 | AxiosNet |
| 团队小、要求资料丰富、出问题能快速搜到答案 | HttpClient / Refit（生态优先） |

一句话取舍：**要最少的包和最高的吞吐，选 HttpClient；要最少的自写治理代码和统一的错误/观测模型，
选 AxiosNet——代价是同窗口约 8% 的吞吐（三次中位 91.7%，区间 89.7%–92.6%）与每请求多约 0.09 KB 分配（相对 HttpClient）。**

**什么时候不该选 AxiosNet**：

- 只需要发几个 GET/POST，且不想引入任何非 BCL 依赖；
- 团队对 axios 模型不熟，且项目已深度使用 Refit/Polly 组合；
- 需要极成熟的社区答案与第三方扩展生态；
- 对每请求分配极度敏感，且愿意自己维护治理代码。

---

## 八、复现

```bash
cd test/AxiosNet.Comparison

# 本次报告的口径：8 并发 × 2s × 6 轮，连续跑三次
dotnet run -c Release -- --rounds 6 --duration 2 --tag run1
dotnet run -c Release -- --rounds 6 --duration 2 --tag run2
dotnet run -c Release -- --rounds 6 --duration 2 --tag run3

# 当回归门禁：吞吐不低于 HttpClient 的 85%，净分配不超过其 2 倍
dotnet run -c Release -- --min-ratio 0.85 --max-alloc-ratio 2 --tag nightly
```

参数、退出码与报告格式见 `test/AxiosNet.Comparison/README.md`。本次原始报告（相对仓库根目录）：

- [`comparison-run1-20261119-182427.md`](../test/AxiosNet.Comparison/results/run1/comparison-run1-20261119-182427.md)（18:24，8c × 2s × 6 轮）
- [`comparison-run2-20261119-182612.md`](../test/AxiosNet.Comparison/results/run2/comparison-run2-20261119-182612.md)（18:26，8c × 2s × 6 轮）
- [`comparison-run3-20261119-182754.md`](../test/AxiosNet.Comparison/results/run3/comparison-run3-20261119-182754.md)（18:27，8c × 2s × 6 轮）

复现时请尽量关闭后台负载，确认各实现 CPU 份额达到 42% 门槛，再看绝对 RPS；否则只比较同窗口相对值。
