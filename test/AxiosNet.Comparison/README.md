# AxiosNet.Comparison

跨实现横向基准：同一台回环服务器、同一份负载、同一个返回模型，对比以下实现。

| 实现 | 模式 | 用法 |
| --- | --- | --- |
| 裸 `HttpClient` | 强类型 | `PostAsJsonAsync` + `ReadFromJsonAsync<User>`（基线） |
| Refit | 接口式 | `RestService.ForGenerated<IUserApi>` + `[Body] User`（source-generated，不走反射回退） |
| RestSharp | 强类型 | 单例 `RestClient` + 每请求 `RestRequest` + `AddJsonBody` + `ExecuteAsync<User>` |
| Flurl | 强类型 | 单例 `FlurlClient` + `Request(url).PostJsonAsync(...).GetJsonAsync<User>()` |
| AxiosNet | 强类型 / 模型式 / 接口式 | 三种入口都支持，按 `--api-style` 选用（见下） |
| RawSocket | — | 裸 TCP 发预编码 POST（**服务端基线**，不是被对比的客户端） |

每个实现都用各自**惯用 API** 完成「把同一个 `User` 负载序列化上送 → 把响应 JSON 读成 `User`」。负载、计时、采样全部由同一个
`LoadRunner` 执行，除被测库本身外，任何一侧都不额外承担工作。

## 三种 API 形态

AxiosNet 的三种入口都能表达「POST 一个 `User` 并读回 `User`」，三者共用同一个 `User` 模型、同一条路由与同一个回环服务器，
差别只在调用层：

| 形态 | AxiosNet 写法 | 同一形态的对照实现 |
| --- | --- | --- |
| 强类型（`typed`） | `client.PostAsync<User>(Payloads.Route, model)` | HttpClient / RestSharp / Flurl |
| 模型式（`model`） | `client.RequestAsync(new SaveUserRequest { Body = model })`（`[Post]` + `[RootBody]`） | —（其余四家没有请求模型这一层） |
| 接口式（`interface`） | `client.Create<ISaveUserApi>().SaveUserAsync(model)`（`[Post]` + `[RootBody]` 参数） | Refit |

其它实现只有一种入口：Refit 只有接口式，HttpClient / RestSharp / Flurl 只有强类型。所选形态它们不支持时**按自身支持的形态参与**，
表里「模式」列标注每行实际使用的形态，报告口径行会列出哪些实现是「按自身形态参与」。

请求模型与接口和发布包一样走编译期烘焙（工程引了同一份源生成器分析器）；模型实例、接口代理都在构造期建好、全部 worker 复用，
不把「每请求构造模型 / 建代理」的分配算进测量。

未传 `--api-style` 时，程序启动后打印 1 / 2 / 3 菜单，可多选（空格 / 逗号分隔，`all` 全选；回车默认强类型）；
标准输入被重定向（管道、CI）时不等待输入，直接按默认继续。

多选时按输入顺序**逐个形态跑完整流程**（各自预热、校准、轮转测量、导出报告），跑完再打一张跨形态小结表
（每个形态的 AxiosNet 吞吐 / 比值 / p99 / 净分配 / 失败数）。跨形态的绝对值来自不同测量窗口，只作趋势参考。

输出四项指标：

- **RPS**：同一窗口内各实现的吞吐，以及相对 `HttpClient` 的比例；
- **p50 / p99 / max**：客户端观察延迟（含服务端处理、回环 RTT 与反序列化）；
- **分配**：进程级每请求分配，以及减掉服务端基线后的**客户端净分配**；
- **CPU 成本**：每请求分摊的进程 CPU 时间（µs/请求），以及减掉服务端基线后的客户端净成本。**是成本口径，不是吞吐**。

控制台打印表格，同时把 Markdown + CSV 写到 `--out` 目录，用于归档、写入提交信息或对外发布。

## 项目定位

- **不引用 `test/AxiosNet.Tests`**：这是量化工具，不是断言用例。回环服务器
  `test/AxiosNet.Tests/Infrastructure/PerfHttpServer.cs` 与 Handler 对齐
  `test/AxiosNet.Benchmarks/Infrastructure/ComparableHttpClientHandler.cs` 都以 `<Compile Include>`
  **链接源码**（不复制），保证与 `ConcurrencyPerformanceTests` 使用同一份口径，任一侧修改基建都会同步反映到这里。
- **引用同一份源生成器**：`src/AxiosNet.SourceGen` 以分析器方式引入（与 NuGet 包自动分发的分析器同一份），
  模型式 / 接口式两种入口与发布包一样在编译期烘焙元数据。
- **不进 CI、不被 `dotnet test` 收集**（`IsTestProject=false`）：数字不可断言，共享 runner 上没有参考价值。
  项目保留在 `AxiosNet.sln` 中，便于 IDE 直接打开运行；`dotnet build AxiosNet.sln` 会一并还原
  Refit / RestSharp / Flurl，CI 只按 `TEST_PROJECTS` 构建测试工程。需要门禁时在本地或专用机器上用 `--min-ratio` 运行（见下）。
- **与 `test/AxiosNet.Benchmarks` 分工不同**：那边是**单实现的分项成本**（合并 / 编码 /
  反序列化 / 分帧），这里是**跨实现的端到端对照**。两边的绝对值不可互换。

## 测量口径

| 项 | 口径 |
| --- | --- |
| API 形态 | 由 `--api-style` 选定（`typed` / `model` / `interface`，可多选、按顺序逐个跑）；每个形态内 AxiosNet 按该入口，其余实现按自身支持的形态参与，表内「模式」列标注实际形态 |
| 服务器 | 与 `ConcurrencyPerformanceTests` 同一份 `PerfHttpServer`：HTTP/1.1、Keep-Alive、响应字节启动时预编码一次；请求正文按 `Content-Length` 或 `Transfer-Encoding: chunked` 读满（`PostAsJsonAsync` 走的是后者） |
| 负载 | 每实现 `--concurrency` 个 worker 常驻，窗口内不停发请求；默认 8 并发 × 2s × 6 轮 |
| 轮转 | 每轮把实现顺序整体旋转一位：机器负载漂移均摊到所有实现，而不是永远压在第一位的实现上 |
| 预热 | 每实现一个 `--warmup` 窗口；随后 `--cooldown` 秒等分层编译在后台落地 |
| 校准 | 预热后用 RawSocket 连续量窗口，最近 3 窗 RPS 极差 ≤ 中位 15% 才开测（最多 10 窗）；不收敛照常开测并提示 |
| 停窗 | 置停止位 + 等在途请求自然收尾；停窗后完成的请求不计入吞吐，也不计入延迟样本 |
| 延迟 | `Stopwatch.GetTimestamp` 包住一次完整调用（序列化请求体 + 发请求 + 读正文 + 反序列化）；每 worker 最多记录 65,536 个样本（并发高时按 8 MB 总预算缩减），超出只截断延迟统计、不影响吞吐与分配 |
| 分配 | `GC.GetTotalAllocatedBytes(precise: true)` 在窗口前后取样 ÷ 完成请求数；**含同进程回环服务端的固定开销**，`净分配` 已减掉 RawSocket 行。排序 / 建数组等测量自身的分配不计入 |
| CPU | 本进程窗口内 `TotalProcessorTime` ÷ 墙钟 ÷ 核数（含同进程服务端） |
| CPU 成本 | 同一份 `TotalProcessorTime` 增量 ÷ 完成请求数，单位 µs/请求；`净 CPU` 已减掉 RawSocket 行（与 `净分配` 同法） |
| 汇总 | 每列取**轮中位数**（抗单轮抖动） |

对齐细节：

- `HttpClient` / `Refit` / `RestSharp` / `Flurl` 全部注入与 AxiosNet 默认连接池逐项对齐的
  `SocketsHttpHandler`（Cookies / Proxy / 连接数 / 超时 / 解压一致），避免把默认值差异当成实现差异。
- 三种形态发送的请求正文逐字节相同：都把同一个 `Payloads.Request` 实例交给序列化器，模型式用 `[RootBody]`、接口式用 `[RootBody]` 参数。
- 返回模型显式标 `[JsonPropertyName]`，避免各库默认 `JsonSerializerOptions` 的大小写策略不同导致「某库解析失败」
  被当成性能差异。
- Refit 只走 source-generated 的 `ForGenerated`；`RestService.For<T>` 的反射回退路径是另一档，不混谈。

## 用法

```bash
cd test/AxiosNet.Comparison

# 默认：全量对比，只出数字，不设门禁（未传 --api-style 时启动后交互多选形态）
dotnet run -c Release

# 看参数
dotnet run -c Release -- --help

# 指定形态：强类型 / 模型式 / 接口式（也可写 1 / 2 / 3 或 --api-style=model）
dotnet run -c Release -- --api-style model
dotnet run -c Release -- --api-style 接口式

# 多选：按输入顺序逐个形态各跑一遍，每个形态一份报告
dotnet run -c Release -- --api-style typed,model,interface --tag styles
dotnet run -c Release -- --api-style "1 3"

# 只比 AxiosNet 与 HttpClient，多跑几轮
dotnet run -c Release -- --libs HttpClient,AxiosNet --rounds 7 --duration 2

# 当回归门禁：吞吐不低于 HttpClient 的 85%，净分配不超过其 2 倍
dotnet run -c Release -- --min-ratio 0.85 --max-alloc-ratio 2 --tag nightly
```

参数：

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--concurrency <n>` | 8 | 每实现的并发 worker 数 |
| `--duration <秒>` | 2 | 每轮测量窗口 |
| `--rounds <n>` | 6 | 测量轮数（每轮轮转顺序） |
| `--warmup <秒>` | 3 | 每实现预热窗口 |
| `--cooldown <秒>` | 2 | 预热后等待分层编译 |
| `--libs <a,b,...>` | 全部客户端 | 可选 `HttpClient` / `Refit` / `RestSharp` / `Flurl` / `AxiosNet` / `RawSocket`；RawSocket 不在清单里时自动追加为基线 |
| `--api-style <形态,...>` | 启动时交互多选 | `typed` / 强类型、`model` / 模型式、`interface` / 接口式（也认 `1` / `2` / `3`、`all`）；可多选，逗号或空格分隔，按输入顺序逐个跑 |
| `--min-ratio <比值>` | 不设 | RPS 门禁：AxiosNet / HttpClient 低于该值退出码 1（需同时包含两个实现；0.85 = 85%） |
| `--max-alloc-ratio <x>` | 不设 | 分配门禁：AxiosNet / HttpClient 净分配高于该值退出码 1 |
| `--min-cpu-share <比值>` | 0.42 | CPU 份额门槛：任一实现低于该值时，其吞吐/比值标注为不可判定（表内带 `*`），RPS 门禁**跳过而不是判负**；`0` = 关闭判定 |
| `--out <目录>` | `results` | 报告输出目录（相对当前工作目录） |
| `--tag <名字>` | 无 | 报告文件名后缀，便于归档 |

退出码：`0` 正常；`1` 门禁未通过；`2` 存在失败请求（数字不可信）；`3` 参数错误。多选形态时取其中最严重的一档（`2` 优先于 `1`）。

必须用 `-c Release`：工程本身 `Optimize=true`（Debug 也是优化 IL），但 Release 才是要归档的口径。

## 输出

控制台（下表是 2026-11-19 的强类型口径运行，`模式` 列即各行实际形态）：

```
实现          模式     RPS  p50 (µs)  p99 (µs)  max (µs)  分配/请求  净分配/请求  CPU  CPU µs/请求  净 CPU µs/请求  vs HttpClient
---------------------------------------------------------------------------------------------------------------------------------
HttpClient  强类型  51,154       144       279    15,950    4.48 KB      4.23 KB  53%          187              58              —
Refit       接口式  45,181       161       317    17,322    5.93 KB      5.68 KB  53%          212              83          88.3%
RestSharp   强类型  27,684       210     2,587    17,895   32.94 KB     32.69 KB  45%          300             172          54.1%
Flurl       强类型  42,739       168       352    16,721    8.38 KB      8.12 KB  53%          225              97          83.5%
AxiosNet    强类型  47,372       156       294    16,096    4.58 KB      4.32 KB  58%          223              95          92.6%
RawSocket        —  64,931       116       226    18,304      262 B          0 B  46%          128               0         126.9%
```

（2026-11-19 18:27 的第三次运行，口径为 POST。当天连续三次独立运行（每次 8 并发 × 2s × 6 轮）中，
AxiosNet / HttpClient 的 RPS 比分别为 91.7% / 89.7% / 92.6%，净分配稳定在 4.31 ~ 4.32 KB；
三次运行的全部实现 CPU 份额均 ≥ 42%，表中无需「不可判定」标记。单轮数据与离散度见各次运行生成的 Markdown 报告。）

文件：

- `results/comparison-<时间戳>.md`：完整口径（含本次 API 形态）+ 汇总表（带「模式」列）+ CPU 份额门槛判定 + 离散度（各轮最小 ~ 最大）+ 每轮原始 RPS；
- `results/comparison-<时间戳>.csv`：一行一个 `(实现, 形态, 轮次)`（`api_style` 列为 `typed` / `model` / `interface`，基线为 `-`），便于画图或做趋势对比。

多选形态时文件名里多一段形态记号，例如 `comparison-styles-model-20261123-163012.md`（`<tag>-<形态>-<时间戳>`），
一个形态一套报告、互不覆盖；控制台最后另有一张跨形态小结表。

## 如何读数

- **看比值，不看绝对值**：同一台机先后两次运行，绝对 RPS 可以差一倍（功耗策略、P/E 核、后台负载）；
  同一窗口内的相对关系才是可比的。跨版本比较请用同一个 `--tag` 口径、同样的机器状态。
- **p99 对 GC 极敏感**：每请求分配越高的实现，窗口内 Gen2 越多，p99 与 max 会被 GC 暂停拉长
  （表中 RestSharp 的高分配与毫秒级 p99 是同一现象的两个侧面。用不经过 socket 的伪 Handler 单独复测，
  `ExecuteAsync<User>` 单次约 29.4 KB（29,402 B），与表格里的净分配（32.7 KB）同一量级 ——
  高分配来自 RestSharp 自身，不是服务端或适配器带入的）。
- **分配是进程级口径**：「净分配」减掉了 RawSocket 基线（服务端 + 裸 socket 客户端的成本），
  仍含各实现自己的 HttpClient 栈开销；不要与 BenchmarkDotNet 的单操作分配直接对比。
- **CPU 是整进程**（含同进程服务端）：只用于判断「这一窗测得可信不可信」，不是实现自身的 CPU 成本。
  默认门槛 **42%**（`--min-cpu-share`，与 `ConcurrencyPerformanceTests` 的约定一致）：低于门槛的行在表里带 `*`，
  该实现的吞吐与比值**不可判定**，RPS 门禁会跳过而不是判负。
- **`CPU µs/请求` 是成本列，不是吞吐列**：它回答「每请求消耗多少机器时间」，与 `RPS` 回答「每秒出多少请求」是两件事，
  两者都要看。减掉 RawSocket 行剩下的才是客户端自身成本，但它是两个大数相减（回环服务端占去大部分），
  相对误差比 `净分配` 大，看量级即可。
- **不要用 `RPS ÷ CPU` 当主指标**：固定并发下 `RPS ≈ 并发 ÷ p50`，窗口受延迟而非 CPU 限制（CPU 份额常在 50% 上下），
  除以 CPU 等于假设「CPU 线性换吞吐」，在这个区间不成立；分母还含同进程服务端，与实现无关。
  要回答「同等资源下谁性能更高」，把 `--concurrency` 推到 CPU 份额接近 100% 再比 RPS。
- **看中位数，也看离散度**：汇总列是轮中位数；控制台与 Markdown 另附「离散度」表（各轮最小 ~ 最大、RPS 相对波动）。
  波动大（例如 RPS ±10% 以上）时，任何单次比值结论都要打折扣。
- **跨形态的 RPS 不是同一种写法**：选模型式 / 接口式时只有 AxiosNet 用该形态，其余实现按自身形态参与（看「模式」列）。
  同写法对比看两处——强类型对 HttpClient / RestSharp / Flurl，接口式对 Refit；模型式没有同层对照，只与各实现的原生写法并排看。
- **RawSocket 行不是客户端**：它是这台回环服务器的上限与固定成本；若某个客户端接近它，说明瓶颈已转移到服务器，
  该实现的 RPS 被低估。

## 范围与边界

- 只测 HTTP/1.1 明文回环「POST 一个 51 B 的 JSON 负载并读回同一个模型」；TLS、HTTP/2、大正文上传、流式响应都不在范围内
  （大正文看 `test/AxiosNet.Benchmarks`）。
- 服务端与客户端同进程：分配指标必须减基线，CPU 指标含服务端。
- 不切 Server GC：与 `ConcurrencyPerformanceTests` 同用运行时默认的 Workstation GC（报告环境行会打印实际模式），
  换 GC 模式会让两端口径不再一致。
- 每个实现按各自文档里的惯用写法使用，不做「为了跑分」的调优（例如 RestSharp 每请求新建 `RestRequest` 是它的正常用法）。
- 三种形态只切换调用层，客户端配置都是默认值；请求模型与接口代理在构造期建好，不带「每请求构造」的分配。
- Refit 的反射模式、Flurl 的全局默认配置改动、AxiosNet 的非默认选项都不在对比口径内。
