# AxiosNet.Benchmarks

AxiosNet 的量化性能基线：只度量、不判定——没有门槛、没有断言，只输出可复现的数字、误差条与分配量。
是否退化由使用者或 CI 之外的脚本比对决定。

## 项目定位

[`test/AxiosNet.Tests/Regression/ConcurrencyPerformanceTests.cs`](../AxiosNet.Tests/Regression/ConcurrencyPerformanceTests.cs)
是**并发正确性测试**，不是量化基线。它把「AxiosNet 吞吐 ≥ HttpClient 的 85%」钉成一条断言，
用于拦住「往热路径塞反射 / 同步阻塞 / 每请求大块分配」这类灾难性回退。它不能证明某次优化「没有退化」，原因如下：

- 只采 3 轮 × 1 秒，判定用逐轮配对比值的中位数；
- 绝对值受功耗策略、P/E 核分配、后台干扰影响：同一变体在 15 个连续窗口里能从 35,228 摆到 45,996 req/s，
  逐轮比值在 71%~120% 之间晃（2026-10-21 实测）；
- 输出是「过线 / 没过线」一个布尔值，没有误差条、没有分配量、没有分项成本。

因此 3% 的优化会淹没在 ±15% 的噪声里，优化前后都「过线」，看不出区别；一次真实退化也可能撞上顺风局而通过。

本项目提供：BenchmarkDotNet 的多轮采样 + 误差条 + 每操作分配量，以及按热点拆开的**分项成本**
（合并 / 编码 / 反序列化 / 分帧），用于定位「哪一段变贵了」。

## 约束

| 约束 | 原因 |
| --- | --- |
| 单目标 `net8.0`、`OutputType=Exe` | BDN 需要一个具体宿主运行时；基准不是产品资产 |
| `IsPackable=false`、不被 `src/` 引用 | 不进 NuGet 包，不改动主库「单目标 `net6.0` + 零第三方运行时依赖」的约定 |
| 不引用 `test/AxiosNet.Tests` | 回环服务器在本项目内独立实现（`Infrastructure/LoopbackHttpServer.cs`），避免测试基建重构悄悄改变基准口径 |
| 不进 CI | 基准对机器状态敏感，共享 runner 上的数字没有参考价值；在专用机器或本机运行 |
| 基准类不能 `sealed` | BDN 会派生基准类来拦截调用，`sealed` 直接报校验错误 |
| `BenchmarkDotNet` 不能加 `PrivateAssets="all"` | BDN 生成的临时项目靠传递引用拿到 `BenchmarkDotNet.Engines`，私有化后生成项目编译失败 |

## 快速开始

```bash
cd test/AxiosNet.Benchmarks

# 默认入口：交互菜单（选组 → 跑 → 看摘要 → 回到菜单）
dotnet run -c Release

# 列出全部基准（不运行）
dotnet run -c Release -- --list flat

# 只跑一组（filter 是通配符，匹配 namespace.typeName.methodName）
dotnet run -c Release -- --filter '*Merge*'

# 快速冒烟：3 轮迭代，误差大，只能用来确认「跑得通」，不能当基线
dotnet run -c Release -- --filter '*' --job short

# 完整一轮（默认就会导出 md / csv / html 三份报告）
dotnet run -c Release -- --filter '*'

# 要写进提交信息 / 与历史基线比时：切回 BDN 的隔离执行
dotnet run -c Release -- --filter '*' --isolated
```

不带参数时进入菜单；标准输入被重定向（管道 / CI）时打印用法，避免菜单阻塞在 `ReadLine` 上。
带参数时行为不变，脚本不受影响。

**务必用 `-c Release`**：BDN 按构建配置做校验，Debug 下的数字不能用于任何结论。

产物（生成的临时项目、报告）落在**当前工作目录**的 `BenchmarkDotNet.Artifacts/`，
所以先 `cd` 进项目目录再跑；该目录与 `.baseline/` 都已 gitignore。

### 执行方式：同进程（默认）/ 隔离

BDN 默认的 `CsProjCoreToolchain` 会为作业生成一个临时项目、`dotnet build` 一遍，再为每个用例起一个子进程。
本项目默认改为同进程（`InProcessEmitToolchain`）：不生成项目、不重建、不另起进程，跑的是**当前这个程序集**
（`-c Release` 就是 Release，`-c Debug` 就是 Debug），一次运行的固定开销从几十秒降到亚秒级。

| 方式 | 开关 | 适合 | 代价 |
| --- | --- | --- | --- |
| 同进程（默认） | `--in-process` | 边改边看、冒烟、定位「哪一段变贵了」 | 所有用例共用一个进程：JIT 分层、GC 堆、线程池状态会互相影响，绝对数偏高且随组顺序漂 |
| 隔离 | `--isolated` | 写进提交信息 / PR、与 `.baseline` 前后对比 | 每个作业先编译一次临时项目（几十秒），每个用例一个子进程 |

同进程仍走 BDN 的 emit 桩，`InvocationCount` / `UnrollFactor` / `MemoryDiagnoser` 都保留，
所以**分配量（`Allocated`）在同进程下依旧可信**——它不依赖进程隔离。不可信的是绝对耗时。
两种方式的数字**不要混着比**：同一台机器上 `Headers_Clone` 同进程 7.3 ns、隔离 5.8 ns，
差的是测量环境而不是代码。

Debug 宿主下 BDN 会拒绝运行（被测库是未优化 IL），同进程模式关闭了这条校验；Debug 数字只能确认跑得通，
启动时会有明确提示。

默认作业是 **3 预热 + 8 迭代**（见 [`BenchmarkConfig.cs`](BenchmarkConfig.cs)），
比 BDN 的 `DefaultJob`（6 + 15）轻一档：不含大正文的 45 个基准在本机实测 **8 分 41 秒**（隔离模式；
大正文三组（上传 / 下载 / JSON）是后加的 54 个用例，单独一轮约十几分钟，按需用 `--filter '*LargeBody*'` 跑）。
代价是置信区间更宽，所以判定的第一依据是 `Allocated`（确定性量），第二依据是同运行内的比值；
要更窄的区间就用作业覆盖：`--job long`。

`--job` 是**真覆盖**：作业只从 `BenchmarkConfig` 来，命令行给的那个会替换掉默认作业。
BDN 的 `--job` 默认往配置里**追加**作业，实测 `--job short` 会让 6 个基准跑 12 次，同一张表里混进两套迭代次数；
本项目改为单一来源，避免口径混用。可用作业名：`default / dry / short / medium / long / verylong`，拼错会提示后回退默认作业。

## 菜单与结果摘要

菜单承担两件事：把「选哪一组」和「这组怎么读」放在一起，以及跑完把 BDN 的报告重新排成一张可直接读的表。
编号 / 标题 / 读法来自各基准类上的特性（`[Description]` / `[BenchmarkGroup]` / `[BenchmarkHint]`），
菜单启动时反射发现（判据只有一条：类里有 `[Benchmark]` 方法），**没有需要维护的清单**：
新增基准类只改它自己的文件，编译进程序集就会出现在菜单里。分组按「数字里含不含网络往返」切，
分组先后由 `[BenchmarkGroup(..., Order = n)]` 定：

- 输入编号跑一组，跑完回到菜单；
- `a` = 顺序跑全部（默认作业；不含大正文的 37 个基准约 7~9 分钟——时间取自「45 个基准时本机实测 8 分 41 秒」那一档，加大正文三组会更久）；
- `s` = 冒烟跑全部（`--job short`，3 轮，只确认跑得通）；
- `m` = 在同进程 / 隔离之间切换（当前方式印在标题下方，每组摘要末尾也会标一次）；
- `q` = 退出。

跑完打印的摘要（与 BDN 自己那张表并存，口径不同、不冲突）：

| 处理 | 原因 |
| --- | --- |
| 按参数组合分组、组内**基线排第一** | BDN 的行序是全局 FastestToSlowest，多参数组里各组合会交错，「这行是拿谁比出来的」无法一眼看出 |
| **比值自己重算**（同参数组合 + `[Benchmark(Baseline = true)]`） | BDN 的 `Ratio` 列在多参数组里会把非基线行当成时间基线（见「怎么读数字」第 2 点）；摘要不抄它 |
| 时间列整张表一个单位 | 一行 ns 一行 µs 的表格无法横向比较 |
| `Allocated` 按量级显示 B / KB / MB | 绝对值从 0 B 到几十 KB，统一用字节读不出量级 |
| 误差列 = 99.9% 置信区间半宽 | 与 BDN 的 `Error` 列同口径，`GetConfidenceInterval` 算，不手拼 |

摘要只**呈现**不判定：没有阈值、没有通过 / 不通过。判定口径见下面几节。

## 十三组基准

| 类 | 热点 | 基线 | 读什么 |
| --- | --- | --- | --- |
| `RequestPipelineBenchmarks` | 请求全链路（真实回环 socket） | `HttpClient_GetString` | `Ratio` = 单请求时间比，**吞吐比 = 1 / Ratio** |
| `ConcurrentPipelineBenchmarks` | 8 条在飞时的每请求成本 | `HttpClient_Concurrent` | 同上；`Mean` 已经是每请求成本（`OperationsPerInvoke=8`） |
| `ConfigMergeBenchmarks` | `MergeConfig` + 头合并 | `GetUri_NoMerge` | `Ratio - 1` = 把按请求补丁合到默认值上的增量 |
| `ConfigPrimitiveBenchmarks` | `AxiosConfig.Clone` / `AxiosHeaders` 读写克隆 | 无（看绝对值） | `Allocated`：`Headers_Clone` 应恒为 24 B，`Headers_Set_Same` 应为 0 |
| `TelemetryDispatchBenchmarks` | 观测派发的一次调用（池 / 租约每次事件） | 无（看绝对值与差值） | `ViaGuard − Direct` = 护栏那一跳；应恒为 0 分配、个位数 ns |
| `ResponseContentTypeBenchmarks` | 响应 `Content-Type` 的解析（类型化解析 vs 原始值 + 按需拆分） | `Eager_MediaTypeHeaderValue` | `Allocated`：`Lazy_RawOnly` 应比基线少一整个媒体类型对象图（流式 / 字节响应不再付费） |
| `FormEncodingBenchmarks` | 表单展平 + 百分号编码 | `PostForm_FlatAscii` | 倍数 = 「编码成本 / 基础成本」 |
| `DynamicDeserializationBenchmarks` | `dynamic` 对象图构建 | `Typed` | `Ratio` = 比强类型贵几倍；`Dynamic_Wide` 看每节点成本 |
| `StreamingFramingBenchmarks` | 流式分帧（NDJSON / SSE / JSON 数组） | `Ndjson` | `Ratio` = 格式差；`Frames` 三档的**斜率** = 每帧成本 |
| `JsonUploadBenchmarks` | JSON 对象上送（100 / 2000 元素） | `HttpClient_PostJsonUtf8` | `Allocated / 载荷`：对象正文 ≈ 1.2× = 走字节出口；≈ 3× = 退回「先序列化成字符串再转字节」 |
| `LargeBodyUploadBenchmarks` | 大正文上传（1 / 8 / 32MB：`byte[]` / `ReadOnlyMemory` / 流 / 异步序列） | `HttpClient_Upload_ByteArray` | `Allocated` 不随 `Megabytes` 涨 = 没有正文拷贝；三种 chunked 形态互相对照 |
| `LargeBodyDownloadBenchmarks` | 大正文下载（1 / 8 / 32MB：缓冲成 `byte[]` vs 流式读完） | `HttpClient_Download_Buffered` | 缓冲下载 `Allocated` ≈ 1× 载荷（8MB 是预分配边界）；流式下载只有读缓冲 |
| `LargeBodyJsonBenchmarks` | 大正文 JSON 响应（1 / 8 / 32MB：`byte[]` / 强类型 / `JsonElement`） | `HttpClient_GetBytes` | `Allocated / 载荷`：`byte[]` ≈ 1×；强类型 ≈ 3.4×；`JsonElement` ≈ 4.1×（文档 + 克隆 + token 元数据），三档线性 = 没有额外整串复制 |

六组回环 socket 的（`RequestPipeline` / `ConcurrentPipeline` / `JsonUpload` / `LargeBodyUpload` / `LargeBodyDownload` / `LargeBodyJson`）
与七组纯 CPU 的（`ConfigMerge` / `ConfigPrimitive` / `FormEncoding` / `DynamicDeserialization` / `StreamingFraming` / `TelemetryDispatch` / `ResponseContentType`）
不要混在一个结论里用：前者的数字里有 TCP 往返与线程池调度，后者没有。大正文三组另有自己的口径：
`Allocated` 是主轴（确定性量），耗时只用于「同量级 / 谁更快」的判断。

## 怎么读数字

**1. 绝对 `Mean` 只在本机、本次运行内有意义；能拿来下结论的是 `Allocated` 与按正确基线算出的比值。**
跨机器、跨进程、跨天的 ns 数不可比。BDN 的 `Ratio` 也不是「同时对照」：**每个变体一个独立进程、顺序测**，
功耗策略 / 后台干扰的漂移仍会进入比值（`ConcurrencyPerformanceTests` 坚持「同一窗口内两侧并行跑」正是为此）。因此：

- 先看 `Allocated`（确定性量，同样的代码路径就同样的字节数）；
- 再看 `Ratio`，并且和 `RatioSD` 比：差值小于 `RatioSD` 就是噪声；
- 要更强的结论就跑两遍，看两次的 `Ratio` 是否一致。单次运行里出现个别离群行是正常的 ——
  同一组跑两遍时，`AxiosNet_GetString` 从 146.6 µs 变成 102.1 µs，而 `Allocated` 两次都是 4.73 KB：
  时间会漂，分配不会。

**2. `Ratio` 列不能盲抄 —— 先确认它到底以谁为基线。**
BDN 在多参数组里会把「不是声明基线的那一行」当成时间基线。本机实测
`StreamingFramingBenchmarks` 的 `Frames = 1000` 块：`JsonArray` 拿到 `Ratio = 1.00`、`Ndjson` 变成 `1.02`，
而**同一块**的 `Alloc Ratio` 仍以 `Ndjson` 为 1.00（198.16 / 190.13 KB = 1.04）。
也就是说同一份报告里，时间比值和分配比值的基线可以不是同一个。读多参数组时二选一：
自己用 `Mean` 除声明基线行的 `Mean`，或者只信「基线行确实是 1.00」的那些块。
跑完打印的摘要表已经按这个口径重算过（同参数组合 + `[Benchmark(Baseline = true)]`），可以直接用；
BDN 报告里的 `Ratio` 列仍需按上面说的自行核对。

**3. 跨运行对比时，`Allocated` 比 `Mean` 稳得多。**
判断「有没有退化」时先看分配量有没有变，再看 `Mean` 的差值有没有超出 `Error`
（BDN 的 99.9% 置信区间半宽）。

**4. 带 `[Params]` 的组要看斜率，不要只看单点。**
`Frames`（10 / 100 / 1000）与 `Fields`（5 / 50）就是为此存在的：
10 → 1000 的增量是每帧成本，`Frames = 10` 那一行剩下的固定部分才是取流 / 建连开销。
只看一档数字无法区分「每帧变贵」和「取流变贵」。

## 用它证明「这次优化没有退化」

```bash
cd test/AxiosNet.Benchmarks

# 0) 先把上一轮的产物清掉：BDN 不会删旧文件，混轮对比是这套流程最容易踩的坑
rm -rf BenchmarkDotNet.Artifacts

# 1) 改动前，在干净的工作区跑一轮（前后两轮必须同一执行方式）
git stash
dotnet run -c Release -- --filter '*' --isolated
mv BenchmarkDotNet.Artifacts .baseline

# 2) 改完再跑一轮
git stash pop
dotnet run -c Release -- --filter '*' --isolated
```

两轮都用 `--isolated`：同进程模式的绝对数受组顺序、JIT 分层与共享堆影响，前后两轮之间会漂，
差值里混的是测量环境而不是代码。`--job` 同理，两轮必须一致。

不用 `--exporters json`：BDN 默认就导出 `-report-github.md`（紧凑表格）、`-report.csv`（脚本友好）
和 `-report.html`，三份都带 `Mean` / `Error` / `StdDev` / `Allocated`，有基线的组还有 `Ratio`。
最省事的对比是直接 diff 两份 md（用 `git diff --no-index`，Windows 上不用额外装 `diff`）：

```bash
git diff --no-index \
  .baseline/results/AxiosNet.Benchmarks.ConfigMergeBenchmarks-report-github.md \
  BenchmarkDotNet.Artifacts/results/AxiosNet.Benchmarks.ConfigMergeBenchmarks-report-github.md
```

（`.baseline` 已存在时 `mv` 会把目录挪进去变成 `.baseline/BenchmarkDotNet.Artifacts`，不要重复执行这一步。）

要脚本化就取 CSV：先看表头定位列，再按 `Method` + `Mean` / `Error` / `Allocated` 挑列对比。
列数随 `[Params]` 个数变化，不要硬编码列号：

```bash
head -1 BenchmarkDotNet.Artifacts/results/AxiosNet.Benchmarks.ConfigMergeBenchmarks-report.csv
```

使用 JSON 时需自己加 `--exporters json`，并注意两件事：顶层是对象、数组在 `Benchmarks` 下；
无参数的用例 `Parameters` 是**空串**而不是 `null`，`//` 兜不住，需显式判空：

```bash
jq -r '.Benchmarks[] | [.Method, (.Parameters | if .=="" then "-" else . end),
        (.Statistics.Mean|tostring), (.Memory.BytesAllocatedPerOperation|tostring)] | @tsv' \
  BenchmarkDotNet.Artifacts/results/*.json
```

判定建议（按信号强度排序，越靠前越可信）：

1. **`Allocated` 变化**：任何一行的每操作分配量上升都值得解释；只有被测路径变了才接受；
2. **`Mean` 超出 `Error`**：差值小于 `Error` 就是噪声，不要下结论；
3. **同一类的相对次序**：`GetUri_Merge / GetUri_NoMerge` 这类比值在两次运行间应当稳定，
   它的漂移比绝对数更能说明「合并路径变重了」。这里的比值请用 `Mean` 相除，不要抄 `Ratio` 列。

## 已知噪声与陷阱

- **同进程与隔离的数字不能混比**：同一台机器 `Headers_Clone` 同进程 7.3 ns、隔离 5.8 ns，
  差的是测量环境（共享 GC 堆 / JIT 分层 / 线程池）而不是代码。同进程适合判断「这次改动方向对不对」，
  隔离适合判断「有没有退化」；`Allocated` 两者都可信。
- **回环 socket 组**：单请求 ~100 µs 里含 TCP 往返与线程池调度，绝对值会随机器负载摆；
  比值稳，绝对值不稳。测量时避免同时运行其他高 CPU 进程。
- **`FakeHttpHandler` 的恒定开销**：每次请求多一次 `HttpResponseMessage` + `ByteArrayContent` 分配
  （约 2 次、共 ~200 B）。它对同一类里的各个变体是恒定的，看差值 / 比值时被抵消；看绝对值时会计入其中。
- **`--job short` 只能冒烟**：3 轮迭代下 `RatioSD` 能到 0.2，`Mean` 的 `Error` 能超过 `Mean` 本身。
  任何写进提交信息、PR 或本文档的数字都必须来自默认作业。（它是覆盖而不是追加，见「快速开始」。）
- **旧产物不会自动清掉**：BDN 只写不删。重跑时若只加了部分 `--exporters`，会出现
  「`-report.csv` 是新的、`-report-full-compressed.json` 是上一轮的」这种混轮目录。
  2026-11-17 的复查中，同一目录下 JSON 里 `AxiosNet_GetString` 是 146.6 µs，CSV 里是 102.1 µs。
  对比前先 `rm -rf BenchmarkDotNet.Artifacts`。
- **首次运行会编译**：BDN 会为每组基准生成一个临时项目并编译，第一次跑要几十秒；
  之后同一作业会复用产物。
- **杀毒软件 / 进程创建**：BDN 每个基准用例起一个子进程，安全软件拦截会表现为
  「Detected error exit code from one of the benchmarks」，与代码无关。

## 实测样例（2026-10-26）

本机（Intel Core Ultra 5 125H，Windows 10 22H2，.NET 8.0.31，Release，默认作业）
2026-10-26 的实测摘录：除「全链路」一组有两轮数据外，其余来自同一次完整运行（45 个基准、8 分 41 秒）。
**它只是形态示例，不是门槛** —— 换台机器这些绝对值就会变，能复现的是量级与比值。

`Ratio` 的口径：单参数组（全链路 / 并发 / 表单 / dynamic）直接取 BDN 报告；
「合并」组 BDN 比的是**同参数组合的 `NoMerge`**，所以表里成对列出，否则读者拿 1,117 ÷ 937 会算出 1.19；
「分帧（1000 帧）」组 BDN 把基线认成了 `JsonArray`（见上一节），表中已按 `Ndjson` 重算。

| 组 | 基准（参数） | Mean | Allocated | Ratio |
| --- | --- | ---: | ---: | ---: |
| 全链路（第一轮） | `HttpClient_GetString` | 107.2 µs | 3.72 KB | 1.00 |
| 全链路（第一轮） | `AxiosNet_GetString` | 146.6 µs | 4.73 KB | 1.37 |
| 全链路（复跑） | `HttpClient_GetString` | 115.1 µs | 3.72 KB | 1.00 |
| 全链路（复跑） | `AxiosNet_GetString` | 102.1 µs | 4.73 KB | 0.89 |
| 全链路（复跑） | `AxiosNet_GetTyped` | 109.1 µs | 5.57 KB | 0.95 |
| 全链路（复跑） | `AxiosNet_PostJson` | 113.1 µs | 6.71 KB | 0.99 |
| 并发 8 在飞 | `HttpClient_Concurrent` | 50.7 µs | 3.61 KB | 1.00 |
| 并发 8 在飞 | `AxiosNet_Concurrent` | 58.5 µs | 4.63 KB | 1.16 |
| 合并 | `GetUri_NoMerge`（0 / 0） | 937 ns | 1.09 KB | 1.00 |
| 合并 | `GetUri_Merge`（0 / 0） | 996 ns | 1.09 KB | 1.06 |
| 合并 | `GetUri_NoMerge`（0 / 2） | 1,160 ns | 1.09 KB | 1.00 |
| 合并 | `GetUri_Merge`（0 / 2） | 1,117 ns | 1.29 KB | 0.97 |
| 合并 | `GetUri_NoMerge`（10 / 2） | 898 ns | 1.09 KB | 1.00 |
| 合并 | `GetUri_Merge`（10 / 2） | 1,021 ns | 1.76 KB | 1.14 |
| 原语 | `Headers_Clone` | 8.2 ns | 24 B | — |
| 原语 | `Headers_Set_Same`（幂等写） | 52.0 ns | 0 B | — |
| 原语 | `Config_Clone` | 55.4 ns | 216 B | — |
| 原语 | `Headers_Set_New`（覆盖写） | 140.8 ns | 288 B | — |
| 表单（50 字段） | `PostForm_FlatAscii` | 25.5 µs | 37.45 KB | 1.00 |
| 表单（50 字段） | `PostForm_Nested` | 27.2 µs | 41.00 KB | 1.07 |
| 表单（50 字段） | `PostForm_FlatUnicode` | 35.7 µs | 52.61 KB | 1.40 |
| dynamic | `Typed` | 2.55 µs | 3.06 KB | 1.00 |
| dynamic | `JsonElement` | 2.25 µs | 2.73 KB | 0.88 |
| dynamic | `Dynamic` | 3.36 µs | 3.09 KB | 1.32 |
| dynamic | `Dynamic_Wide`（宽图） | 5.56 µs | 5.16 KB | 2.19 |
| 分帧（100 帧） | `Ndjson` | 60.7 µs | 28.41 KB | 1.00 |
| 分帧（100 帧） | `JsonArray` | 64.1 µs | 28.41 KB | 1.06 |
| 分帧（100 帧） | `Sse` | 71.1 µs | 64.69 KB | 1.17 |
| 分帧（1000 帧） | `Ndjson` | 599.9 µs | 190.13 KB | 1.00 |
| 分帧（1000 帧） | `JsonArray` | 586.7 µs | 198.16 KB | 0.98 |
| 分帧（1000 帧） | `Sse` | 686.6 µs | 556.88 KB | 1.14 |

读数：

- **全链路**：同一份代码、同一台机器，两轮的单请求比值一个 1.37、一个 0.89 —— 绝对时间不能当门槛；
  `Allocated` 两轮一致（4.73 KB vs 3.72 KB，多出的 1 KB 是解析 / 封装成本）。
  这一组只能得出「与裸 `HttpClient` 同量级」的结论。真正可比的是并发组：8 条在途时每请求
  58.5 µs vs 50.7 µs，折算吞吐比 ≈ 0.87，与 `ConcurrencyPerformanceTests` 的 85% 门槛一致
  （略慢、但过线），区别是这里带误差条，且能看出多出来的 ~1 KB 分配花在哪。
- **合并**：`Merge / NoMerge` 六组比值是 1.06 / 0.97 / 0.89 / 1.67 / 0.91 / 1.14，而且 `Merge`（5 / 2）
  比 `Merge`（10 / 2）还慢（1,524 ns vs 1,021 ns）—— 头更多不可能更快，说明时间列这一档全是噪声。
  两条 `NoMerge` 行干的活完全一样（分配都是 1.09 KB），`Mean` 却差 24%（937 vs 1,160 ns），
  说明这是本组的时间分辨率下限。可读的只有 `Allocated`：`Merge` 相对同参数 `NoMerge` 多出
  0 B（0 / 0）→ +200 B（0 / 2）→ +656 B（10 / 2），每多一批按请求头就多复制一版条目数组。
  时间列没有结论，分配列有结论。
- **原语**：`Headers_Clone` 恒 24 B、`Headers_Set_Same` 恒 0 B —— 两条设计承诺（克隆只共享不可变数组、
  幂等写不发布新版本）在基准里是可直接盯住的护栏；`Headers_Set_New` 288 B 是「发布一版新数组」的代价。
- **表单**：50 字段全部需要转义时比全 ASCII 贵 40%、多 15 KB（每字节一次映射 + 写出 `%XX`）。
- **dynamic**：小图 1.32×，宽图 2.19× —— 每节点成本（`AxiosDynamic` 构造 + 索引器写入）才是主要项，
  图越大差距越明显；用 dynamic 处理大响应有真实成本。
- **分帧**：NDJSON 与 JSON 数组几乎同价（100 帧 1.06×、1000 帧 0.98×，差异都在 `RatioSD` 的量级上），
  SSE 在 1000 帧下慢 14%，但**分配 2.93 倍**（556.88 KB vs 190.13 KB；每帧先攒进 `StringBuilder` 再 `ToString`）
  —— 三处里最值得优化的一处，只看时间列会漏掉。

## 大正文组实测（2026-10-29）

同一台机器（Intel Core Ultra 5 125H，Windows 10 22H2，.NET 8.0.31，Release，默认作业、同进程），
`--filter '*LargeBody*'` 该轮共 36 个用例（JSON 组是后加的，现在共 54 个）。上传 / 下载各自有独立基线，行内 `Ratio` 只与同组基线比。

**下载**（基线 `HttpClient_Download_Buffered`）：

| 用例（MB） | Mean | Allocated | Ratio |
| --- | ---: | ---: | ---: |
| `HttpClient_Download_Buffered`（8） | 16,456.7 µs | 8,195.37 KB | 1.00 |
| `AxiosNet_Download_Buffered`（8） | 16,358.0 µs | 8,197.08 KB | 1.00 |
| `HttpClient_Download_Buffered`（32） | 39,603.2 µs | 32,771.30 KB | 1.00 |
| `AxiosNet_Download_Buffered`（32） | 34,255.0 µs | 32,773.31 KB | 0.88 |
| `HttpClient_Download_Stream`（32） | 28,146.8 µs | 3.04 KB | 0.72 |
| `AxiosNet_Download_Stream`（32） | 26,481.4 µs | 5.60 KB | 0.68 |

**上传**（基线 `HttpClient_Upload_ByteArray`；32MB 档）：

| 用例（32MB） | Mean | Allocated | Ratio |
| --- | ---: | ---: | ---: |
| `HttpClient_Upload_ByteArray` | 43,855.5 µs | 7.66 KB | 1.00 |
| `AxiosNet_Upload_ByteArray` | 35,252.6 µs | 27.85 KB | 0.81 |
| `AxiosNet_Upload_ReadOnlyMemory` | 39,382.3 µs | 9.59 KB | 0.90 |
| `AxiosNet_Upload_StreamSeekable` | 53,443.4 µs | 9.65 KB | 1.23 |
| `HttpClient_Upload_StreamChunked` | 32,155.4 µs | 1,108.37 KB | 0.74 |
| `AxiosNet_Upload_StreamChunked` | 94,746.8 µs | 1,110.25 KB | 2.18 |
| `AxiosNet_Upload_AsyncEnumerableMemory` | 41,699.0 µs | 2,217.65 KB | 0.96 |
| `AxiosNet_Upload_AsyncEnumerableBytes` | 28,741.6 µs | 2,216.86 KB | 0.66 |

读数：

- **缓冲下载没有额外拷贝**：`Allocated / 载荷` = 1.003（1MB）、1.000（8MB，精确预分配边界）、1.000（32MB，段式装配）——两条路径都是「读满直接交出那个数组」。1MB 档的 1.003 是响应包装对象的固定开销。
- **上传的 `byte[]` / `ReadOnlyMemory` / 可 seek 流不碰正文**：三档尺寸的 `Allocated` 都在 9.6 KB 量级（内容对象 + 请求 / 响应包装），与载荷大小无关；唯一例外是 32MB 档 `AxiosNet_Upload_ByteArray` 的 27.85 KB——这一轮的一次性池化缓冲，仍不是正文拷贝。
- **chunked 传输有框架级的按次分配**：每次写出约 3–4 KB（`HttpClient` 侧同样有）。80 KB 缓冲的 `StreamChunked` 在 32MB 上是 1.1 MB（≈3.4%），64 KB 块的 `AsyncEnumerable` 是 2.2 MB（≈6.7%）——按块数乘出来的，块越大越省；它不是库内的正文缓冲。
- **流式正文的耗时只与流式路径同量级，先后顺序不可当结论**：异步序列少一次「读进 80 KB 缓冲再写出」的搬运，但在 1MB / 8MB 档上这点差异完全埋进噪声。证据是一节数据的复跑（`--filter '*AsyncEnumerable*'`，只改测量时机、不改代码）：1MB 的 `AsyncEnumerableBytes` 从 2,450.2 µs 变成 876.9 µs（2.8 倍），8MB 的 `AsyncEnumerableMemory` 从 5,765.2 µs 变成 8,765.5 µs（1.5 倍）——同一份工作量在两轮里相差数倍，时间列只能读「同量级」。复跑里稳定的仍是分配：1MB 75.7 KB、8MB 559–560 KB、32MB 2.22 MB，两轮一致。

## 大正文 JSON 组实测（2026-10-30）

同机（Intel Core Ultra 5 125H，Windows 10 22H2，.NET 8.0.31，Release，默认作业、同进程），
`--filter '*LargeBodyJson*'` 一轮 18 个用例。表中是 `Allocated / 载荷`（换算自报告的 `Allocated` 列），
基线为 `HttpClient_GetBytes`：

| 用例 | 1MB | 8MB | 32MB |
| --- | ---: | ---: | ---: |
| `HttpClient_GetBytes`（基线，只读字节） | 1.00× | 1.00× | 1.00× |
| `AxiosNet_Bytes` | 1.00× | 1.00× | 1.00× |
| `HttpClient_Typed`（裸 STJ 强类型） | 3.39× | 3.40× | 3.40× |
| `AxiosNet_Typed` | 3.39× | 3.40× | 3.40× |
| `HttpClient_JsonElement`（裸 `Parse + Clone`） | 4.07× | 4.09× | 4.09× |
| `AxiosNet_JsonElement` | 4.08× | 4.09× | 4.09× |

读数：

- **库侧与裸 STJ 的分配逐档一致**（强类型 3.40×、JsonElement 4.09×）：JSON 交付没有多复制一份字节，
  成本全在「对象图」或「文档 + 克隆」本身，库侧零额外分配。
- **强类型 3.4×** = 1× 原始字节 + `List` / 对象 / 每字符串一份 UTF-16；**JsonElement 4.09×** =
  1× 原始字节 + `Parse + RootElement.Clone() + 释放` 3.09×，而 3.09× 与裸
  `JsonSerializer.Deserialize<JsonElement>` 逐字节相同 —— 这是 STJ 交付 `JsonElement` 的地板，
  不是库里的多余复制。实测为 4.09×；要再降只能改交付形态
  （例如改用流式 `IAsyncEnumerable<T>` 不落整图），不是改拷贝次数。
- **`Clone()` 不能省**（2026-10-30 实测，1MB、net6 / net8 一致）：省掉 Clone 会让元素持有未释放的
  `JsonDocument`，文档不释放，元数据数组无法回池，每次解析都要重新租用。
  裸 `Parse`（不 Clone）本身就是 3.91×；放进库的真实路径实测 **5.02×**（`AllocationTests` 改成那种写法时会失败），
  比现在的 `Parse + Clone + Dispose`（3.09× + 1× = 4.09×）多近 1×。护栏因此收到 4.5×。
- **三档线性**：1 / 8 / 32MB 的比例不漂，说明没有隐藏的整串复制；`Bytes` 一档与二进制下载同口径（1×）。
- **时间列只作参考**：1MB 档 `AxiosNet_Typed` 28.3 ms vs `HttpClient_Typed` 11.5 ms，32MB 档反过来
  349 ms vs 410 ms —— 同进程共享 JIT / GC 状态下，先后顺序不可当结论；判定依据仍是 `Allocated`。

回归护栏在 `AxiosNet.Tests/Regression/AllocationTests.cs`：`JsonTypedResponse_PaysObjectGraphNotAnExtraCopy`
（1MB，< 4×）与 `JsonElementResponse_PaysDocumentAndClone`（1MB，< 4.5×），任何一次「整串 UTF-16 往返 /
额外复制」都会把比例推到门槛之上，省掉 `Clone` 的写法同样会撞线。

## 观测派发开销实测（2026-11-16）

同机（Intel Core Ultra 5 125H，Windows 10 22H2，.NET 8.0.31，Release，默认作业、**隔离**），
`--filter '*TelemetryDispatch*'` 一轮 7 个用例。三个变体背后都是同一个内置实现
（`GuardedTelemetrySink` 包着 `BuiltInTelemetrySink`），行间差值是纯派发成本：

| 变体 | Mean | Allocated |
| --- | ---: | ---: |
| `RecordLease_Direct`（接口直调内置实现） | 1.7 ns | 0 B |
| `RecordLease_ViaProperty`（再读一次 `AxiosTelemetry.Sink`） | 1.7 ns | 0 B |
| `RecordLease_ViaGuard`（再经护栏派发） | 4.9 ns | 0 B |
| `TagPoolReuse_Direct` | 2.4 ns | 0 B |
| `TagPoolReuse_ViaGuard` | 5.1 ns | 0 B |
| `EnterLease_Direct` | 6.6 ns | 0 B |
| `EnterLease_ViaGuard` | 5.1 ns | 0 B |

- **护栏那一跳 ≈ +3 ns、0 分配**（多一次虚调用 + 一个空 `catch` 块）：一次请求里这类事件是个位数，
  合计十几 ns，相对一次回环往返（~100 µs）是 0.01% 量级 —— 不值得为这点成本放掉
  「观测实现抛异常不改写请求结果」。
- **`volatile` 读不构成成本**：`ViaProperty` 与 `Direct` 同价（1.7 vs 1.7 ns）。
- **`EnterLease` 两行是反的**（直调 6.6 > 经护栏 5.1）：每个用例一个独立进程，含一次 `Interlocked`
  的用例跨进程能差 1.5 ns —— 这是本组的时间分辨率下限。护栏只会加、不会减，这一行按噪声处理；
  可读的是分配（恒 0）和另外两组同向的差值。
- **这一组不能拿端到端数据代替**：同机两轮 `RequestPipeline` 的 `AxiosNet_GetString` 比值是
  1.09 / 1.03、`PostJson` 是 1.24 / 2.14（第二轮 `Error` 占 `Mean` 37%），误差比要看的差值大一个量级。
  判断「派发变贵了」只能在这里量。

## 响应 Content-Type 组实测（2026-11-16）

同机（Intel Core Ultra 5 125H，Windows 10 22H2，.NET 8.0.31，Release，`--job medium`、**隔离**），
`--filter '*ResponseContentType*'` 一轮 4 个用例。每条用例都**新建一条响应**、`Content-Type` 以原始字符串种下
（复用同一条消息会命中 BCL 的解析缓存，量出来接近 0）：

| 变体 | Mean | Allocated |
| --- | ---: | ---: |
| `Eager_MediaTypeHeaderValue`（旧：每条响应构造 `MediaTypeHeaderValue`） | 291.1 ns | 544 B |
| `Lazy_RawOnly`（流式 / arraybuffer / 不读 `Config.Response`） | 112.9 ns | 288 B |
| `Lazy_Both`（要读正文：文本 / 标量 / 强类型 JSON 都要主值 + 字符集） | 161.4 ns | 376 B |
| `EndToEnd_TypedJsonGet`（一次强类型 GET 的总量，参照） | 2,767.3 ns | 1.91 KB |

- **288 B 是「造一条响应」的地板**（`HttpResponseMessage` + `ByteArrayContent` + 原始头），与被测逻辑无关：
  看的是同一地板上的差值与 `Allocated` 绝对值。媒体类型对象图 = 544 − 288 = **256 B**；
  按需拆分只留子串，主值 + 字符集共 **88 B**。
- **流式与字节响应现在是 0**：`Lazy_RawOnly` 相对基线少的 256 B 就是旧实现里「每条响应都解析一次」的成本。
- **强类型 JSON 不是「只要主值」**：正文走 UTF-8 字节入口还是先解码，由 `IsUtf8Bytes` 看声明的字符集决定，
  所以主值与字符集两段都要拆，落在 `Lazy_Both` 那行。
- **时间同向**：旧路径 291 ns → 只取原始值 113 ns、拆两项 161 ns。BCL 的类型化解析拿不到这个便宜，
  因为它必须把参数集合与参数对象一并构造出来。
- 端到端那行是**参照**，不要拿它跟上面几行做差：2.7 µs 里含 HTTP 状态机、序列化与对象图，
  Content-Type 只占其中一二十纳秒；它的用处是回答「省的这 200 B 占一次请求多少」。

## 维护约定

- 同一类里的变体必须**只差被测变量**：服务器、载荷、并发度、Handler 对齐口径都要一致，
  否则测的是「两个场景的差异」而不是「一段代码的成本」。
- 新增热点时同步更新：本文件的表格、根 README「构建与测试 › 性能基准」的分组表，以及新类上的
  `[BenchmarkGroup]`（分组 + 口径说明）与 `[BenchmarkHint]`（怎么读）。菜单是运行时反射发现，
  不需要在任何清单里登记；漏写特性不会让类消失 —— 缺分组会落进「未分组」，缺读法会在那一行提示补上。
- 修改 `ConcurrencyPerformanceTests` 里的 `CreateComparableHandler` 对齐口径时，
  必须同步修改 `Infrastructure/ComparableHttpClientHandler.cs`（两处的比值要能互相印证）。
