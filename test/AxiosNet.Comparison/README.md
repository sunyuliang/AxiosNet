# AxiosNet.Comparison

跨实现横向基准：**同一台回环服务器、同一份负载、同一个返回模型**，对比

| 实现 | 用法 |
| --- | --- |
| 裸 `HttpClient` | `GetFromJsonAsync<User>`（基线） |
| Refit | `RestService.ForGenerated<IUserApi>`（source-generated，不走反射回退） |
| RestSharp | 单例 `RestClient` + 每请求 `RestRequest` + `ExecuteAsync<User>` |
| Flurl | 单例 `FlurlClient` + `Request(url).GetJsonAsync<User>()` |
| AxiosNet | 默认 `AxiosClient`（连接池 + 默认配置，不做任何调优） |
| RawSocket | 裸 TCP 发预编码请求（**服务端基线**，不是被对比的客户端） |

每个实现都走各自**惯用 API** 完成「发 GET → 把 JSON 读成 `User`」，负载、计时、采样全部由同一个
`LoadRunner` 做，保证除被测库本身外没有任何一侧多干活。

它回答三个问题：

- **RPS**：同一窗口内谁跑得快、相对 `HttpClient` 是多少；
- **p50 / p99 / max**：客户端观察延迟（含服务端处理、回环 RTT 与反序列化）；
- **分配**：进程级每请求分配，以及减掉服务端基线后的**客户端净分配**。

输出控制台表，同时把 Markdown + CSV 写到 `--out` 目录，便于归档、写进提交信息或对外宣传。

## 为什么单独一个工程

- **不引用 `test/AxiosNet.Tests`**：这是量化工具，不是断言用例。回环服务器
  `test/AxiosNet.Tests/Infrastructure/PerfHttpServer.cs` 与 Handler 对齐
  `test/AxiosNet.Benchmarks/Infrastructure/ComparableHttpClientHandler.cs` 都以 `<Compile Include>`
  **链接源码**（不复制）—— 口径与 `ConcurrencyPerformanceTests` 必须能互相印证，任何一边改了基建都会同时反映到这里。
- **不进 CI、不被 `dotnet test` 收集**（`IsTestProject=false`）：数字不可断言，共享 runner 上更没意义。
  它在 `AxiosNet.sln` 里只为 IDE 能直接打开运行，因此 `dotnet build AxiosNet.sln` 会一并还原
  Refit / RestSharp / Flurl；CI 只按 `TEST_PROJECTS` 构建测试工程，不受影响。
  需要门禁时在本地/专用机器上用 `--min-ratio` 跑（见下）。
- 与 `test/AxiosNet.Benchmarks`（BenchmarkDotNet）分工不同：那边是**单实现的分项成本**（合并 / 编码 /
  反序列化 / 分帧），这里是**跨实现的端到端对照**。两边的绝对值不可互换。

## 测量口径

| 项 | 口径 |
| --- | --- |
| 服务器 | 与 `ConcurrencyPerformanceTests` 同一份 `PerfHttpServer`：HTTP/1.1、Keep-Alive、响应字节启动时预编码一次 |
| 负载 | 每实现 `--concurrency` 个 worker 常驻，窗口内不停发请求；默认 8 并发 × 1s × 5 轮 |
| 轮转 | 每轮把实现顺序整体旋转一位：机器负载漂移均摊到所有实现，而不是永远压在第一位的实现上 |
| 预热 | 每实现一个 `--warmup` 窗口；随后 `--cooldown` 秒等分层编译在后台落地 |
| 校准 | 预热后用 RawSocket 连续量窗口，最近 3 窗 RPS 极差 ≤ 中位 15% 才开测（最多 10 窗）；不收敛照常开测并提示 |
| 停窗 | 置停止位 + 等在途请求自然收尾；停窗后完成的请求不计入吞吐，也不计入延迟样本 |
| 延迟 | `Stopwatch.GetTimestamp` 包住一次完整调用（发请求 + 读正文 + 反序列化）；每 worker 最多记录 65,536 个样本（并发高时按 8 MB 总预算缩减），超出只截断延迟统计、不影响吞吐与分配 |
| 分配 | `GC.GetTotalAllocatedBytes(precise: true)` 在窗口前后取样 ÷ 完成请求数；**含同进程回环服务端的固定开销**，`净分配` 已减掉 RawSocket 行。排序 / 建数组等测量自身的分配不计入 |
| CPU | 本进程窗口内 `TotalProcessorTime` ÷ 墙钟 ÷ 核数（含同进程服务端） |
| 汇总 | 每列取**轮中位数**（抗单轮抖动） |

对齐细节：

- `HttpClient` / `Refit` / `RestSharp` / `Flurl` 全部注入与 AxiosNet 默认连接池逐项对齐的
  `SocketsHttpHandler`（Cookies / Proxy / 连接数 / 超时 / 解压一致），否则测的是默认值差异。
- 返回模型显式标 `[JsonPropertyName]`，避免各库默认 `JsonSerializerOptions` 的大小写策略不同导致「某库解析失败」
  被当成性能差异。
- Refit 只走 source-generated 的 `ForGenerated`；`RestService.For<T>` 的反射回退路径是另一档，不混谈。

## 用法

```bash
cd test/AxiosNet.Comparison

# 默认：全量对比，只出数字，不设门禁
dotnet run -c Release

# 看参数
dotnet run -c Release -- --help

# 只比 AxiosNet 与 HttpClient，多跑几轮
dotnet run -c Release -- --libs HttpClient,AxiosNet --rounds 7 --duration 2

# 当回归门禁：吞吐不低于 HttpClient 的 85%，净分配不超过其 2 倍
dotnet run -c Release -- --min-ratio 0.85 --max-alloc-ratio 2 --tag nightly
```

参数：

| 参数 | 默认 | 说明 |
| --- | --- | --- |
| `--concurrency <n>` | 8 | 每实现的并发 worker 数 |
| `--duration <秒>` | 1 | 每轮测量窗口 |
| `--rounds <n>` | 5 | 测量轮数（每轮轮转顺序） |
| `--warmup <秒>` | 3 | 每实现预热窗口 |
| `--cooldown <秒>` | 2 | 预热后等待分层编译 |
| `--libs <a,b,...>` | 全部客户端 | 可选 `HttpClient` / `Refit` / `RestSharp` / `Flurl` / `AxiosNet` / `RawSocket`；RawSocket 不在清单里时自动追加为基线 |
| `--min-ratio <比值>` | 不设 | RPS 门禁：AxiosNet / HttpClient 低于该值退出码 1（需同时包含两个实现；0.85 = 85%） |
| `--max-alloc-ratio <x>` | 不设 | 分配门禁：AxiosNet / HttpClient 净分配高于该值退出码 1 |
| `--out <目录>` | `results` | 报告输出目录（相对当前工作目录） |
| `--tag <名字>` | 无 | 报告文件名后缀，便于归档 |

退出码：`0` 正常；`1` 门禁未通过；`2` 存在失败请求（数字不可信）；`3` 参数错误。

**务必 `-c Release`**：工程本身 `Optimize=true`（Debug 也是优化 IL），但 Release 才是要归档的口径。

## 输出

控制台：

```
实现           RPS  p50 (μs)  p99 (μs)  max (μs)  分配/请求  净分配/请求  CPU  vs HttpClient
--------------------------------------------------------------------------------------------
HttpClient  58,173       125       239    15,946     3.7 KB       3.6 KB  52%              —
Refit       54,424       131       326    16,289     4.6 KB       4.4 KB  54%          93.6%
RestSharp   34,569       165     2,321    16,264    31.5 KB      31.4 KB  43%          59.4%
Flurl       49,451       140       434    18,118     6.0 KB       5.9 KB  47%          85.0%
AxiosNet    54,267       128       313    21,177     5.1 KB       5.0 KB  53%          93.3%
RawSocket   73,859        98       222    16,316      131 B          0 B  49%         127.0%
```

（2026-10-30，18 核笔记本、后台有 IDE 负载时的一次运行：这一次校准未收敛，绝对值偏低，看同一窗口内的比值。）

文件：

- `results/comparison-<时间戳>.md`：完整口径 + 汇总表 + 每轮原始 RPS；
- `results/comparison-<时间戳>.csv`：一行一个 `(实现, 轮次)`，便于画图或做趋势对比。

## 怎么读这些数字

- **看比值，不看绝对值**：同一台机先后两次运行，绝对 RPS 可以差一倍（功耗策略、P/E 核、后台负载）；
  同一窗口内的相对关系才是可比的。跨版本比较请用同一个 `--tag` 口径、同样的机器状态。
- **p99 对 GC 极敏感**：每请求分配越高的实现，窗口内 Gen2 越多，p99 与 max 会被 GC 暂停拉长
  （表中 RestSharp 的高分配与毫秒级 p99 是同一件事的两个侧面。用不经过 socket 的伪 Handler 单独复测，
  `ExecuteAsync<User>` 单次约 29.4 KB（29,402 B），与表格里的净分配（31.4 KB）同一量级 ——
  高分配来自 RestSharp 自身，不是服务端或适配器带进来的）。
- **分配是进程级口径**：「净分配」减掉了 RawSocket 基线（服务端 + 裸 socket 客户端的成本），
  仍含各实现自己的 HttpClient 栈开销；不要把这里的数字与 BenchmarkDotNet 的单操作分配直接对比。
- **CPU 是整进程**（含同进程服务端）：它只用来判断「这一窗测得可信不可信」，不是实现自身的 CPU 成本。
- **RawSocket 行不是客户端**：它是这台回环服务器的上限与固定成本；若某个客户端接近它，说明瓶颈已转移到服务器，
  该实现的 RPS 被低估。

## 已知边界（刻意不改）

- 只测 HTTP/1.1 明文回环 GET + 一个 51 B 的 JSON；TLS、HTTP/2、上传、大正文、流式响应都不在范围内
  （大正文看 `test/AxiosNet.Benchmarks`）。
- 服务端与客户端同进程：分配指标必须减基线，CPU 指标含服务端。
- 不切 Server GC：与 `ConcurrencyPerformanceTests` 同用运行时默认的 Workstation GC（报告环境行会打印实际模式），
  换 GC 模式会让两端口径不再一致。
- 每个实现按各自文档里的惯用写法使用，不做「为了跑分」的调优（例如 RestSharp 每请求新建 `RestRequest` 是它的正常用法）。
- Refit 的反射模式、Flurl 的全局默认配置改动、AxiosNet 的非默认选项都不在对比口径内。
