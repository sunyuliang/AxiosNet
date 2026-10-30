# AxiosNet.Benchmarks

AxiosNet 的**量化性能基线**。这里只度量、不判定：没有门槛、没有断言，只有可复现的数字、
误差条与分配量。判定（「这次改动是不是退化」）由人来做，或由 CI 之外的脚本比对。

## 为什么单独一个项目

[`test/AxiosNet.Tests/ConcurrencyPerformanceTests.cs`](../AxiosNet.Tests/ConcurrencyPerformanceTests.cs)
是**并发正确性测试**，不是量化基线。它把「AxiosNet 吞吐 ≥ HttpClient 的 85%」钉成一条断言，
作用是拦住「往热路径塞反射 / 同步阻塞 / 每请求大块分配」这类灾难性回退 —— 这没问题，而且很有价值。
但它的数字无法用来证明一次优化「没有退化」，原因写在它自己的类注释里：

- 只采 3 轮 × 1 秒，判定用逐轮配对比值的中位数；
- 绝对值受功耗策略、P/E 核分配、后台干扰影响：同一变体在 15 个连续窗口里能从 35,228 摆到 45,996 req/s，
  逐轮比值在 71%~120% 之间晃（2026-10-21 实测）；
- 输出是「过线 / 没过线」一个布尔值，没有误差条、没有分配量、没有分项成本。

于是一个 3% 的优化必然淹没在 ±15% 的噪声里 —— 优化前后都「过线」，看不出任何区别；
反过来，一次真实退化也可能撞上顺风局而通过。

本项目补的就是这一层：BenchmarkDotNet 的多轮采样 + 误差条 + 每操作分配量，
以及按热点拆开的**分项成本**（合并 / 编码 / 反序列化 / 分帧），让「哪一段变贵了」可以被定位。

## 边界（刻意为之，别改）

| 约束 | 原因 |
| --- | --- |
| 单目标 `net8.0`、`OutputType=Exe` | BDN 需要一个具体宿主运行时；基准不是产品资产 |
| `IsPackable=false`、不被 `src/` 引用 | 不进 NuGet 包，不改动主库「单目标 `net6.0` + 零第三方运行时依赖」的约定 |
| 不引用 `test/AxiosNet.Tests` | 回环服务器在本项目内独立实现（`Infrastructure/LoopbackHttpServer.cs`），避免测试基建重构悄悄改变基准口径 |
| 不进 CI | 基准对机器状态敏感，共享 runner 上的数字没有意义。要跑请在本机，且跑的时候别干别的 |
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

不带参数时进菜单；标准输入被重定向（管道 / CI）时退回打印用法 —— 菜单会阻塞在 `ReadLine` 上，
在非交互环境里那就是挂住。带参数时行为与以前完全一致，脚本不受影响。

**务必用 `-c Release`**：BDN 按构建配置做校验，Debug 下的数字不该进任何结论。

产物（生成的临时项目、报告）落在**当前工作目录**的 `BenchmarkDotNet.Artifacts/`，
所以先 `cd` 进项目目录再跑，产物才会在项目下；该目录与 `.baseline/` 都已 gitignore。

### 执行方式：同进程（默认）/ 隔离

BDN 默认的 `CsProjCoreToolchain` 会为作业**生成一个临时项目、`dotnet build` 一遍，再为每个用例起一个子进程**——
这就是「跑个基准怎么还在编译」的来源。本项目默认改成同进程（`InProcessEmitToolchain`）：
不生成项目、不重建、不另起进程，跑的就是**当前这个程序集**（`-c Release` 就是 Release，`-c Debug` 就是 Debug），
一次运行的固定开销从几十秒降到亚秒级。

| 方式 | 开关 | 适合 | 代价 |
| --- | --- | --- | --- |
| 同进程（默认） | `--in-process` | 边改边看、冒烟、定位「哪一段变贵了」 | 所有用例共用一个进程：JIT 分层、GC 堆、线程池状态会互相影响，绝对数会偏高且随组顺序漂 |
| 隔离 | `--isolated` | 写进提交信息 / PR、与 `.baseline` 前后对比 | 每个作业先编译一次临时项目（几十秒），每个用例一个子进程 |

同进程仍然走 BDN 的 emit 桩，`InvocationCount` / `UnrollFactor` / `MemoryDiagnoser` 都保留，
所以**分配量（`Allocated`）在同进程下依旧可信**——它不依赖进程隔离。不可信的是绝对耗时。
两种方式的数字**不要混着比**：同一台机器上 `Headers_Clone` 同进程 7.3 ns、隔离 5.8 ns，
差的是测量环境而不是代码。

Debug 宿主下 BDN 会拒绝运行（被测库是未优化 IL），同进程模式关掉了这条校验 ——
「Debug 就 Debug」是你的选择，但那种数字只能确认跑得通，启动时会有明确提示。

默认作业是 **3 预热 + 8 迭代**（见 [`BenchmarkConfig.cs`](BenchmarkConfig.cs)），
比 BDN 的 `DefaultJob`（6 + 15）轻一档：不含大正文的 45 个基准在本机实测 **8 分 41 秒**（隔离模式；
大正文三组（上传 / 下载 / JSON）是后加的 54 个用例，单独一轮约十几分钟，按需用 `--filter '*LargeBody*'` 跑）——
「前后各跑一轮」才落得进日常流程。代价是置信区间更宽，所以判定的第一依据是 `Allocated`（确定性量），
第二依据是同运行内的比值；要更窄的区间就用作业覆盖：`--job long`。

`--job` 是**真覆盖**：作业只从 `BenchmarkConfig` 来，命令行给的那个会替换掉默认作业。
（早先的写法是程序集上的 `[assembly: SimpleJob]`，而 BDN 的 `--job` 是往配置里**追加**作业 ——
实测 `--job short` 会让 6 个基准跑 12 次，同一张表里混进两套迭代次数。这正是本项目最忌的「口径混用」，
所以改成单一来源。）可用作业名：`default / dry / short / medium / long / verylong`，拼错会提示后回退默认作业。

## 菜单与结果摘要

菜单只做两件事：把「选哪一组」和「这组怎么读」放在一起，以及跑完把 BDN 的报告重新排成一张能直接读的表。
编号 / 标题 / 读法全部来自各基准类自己身上的特性（`[Description]` / `[BenchmarkGroup]` / `[BenchmarkHint]`），
菜单启动时反射发现（判据只有一条：类里有 `[Benchmark]` 方法），**没有任何清单需要维护**：
新加一个基准类只改它自己的文件，编译进程序集就在菜单里了。分组按「数字里含不含网络往返」切，
分组的先后由 `[BenchmarkGroup(..., Order = n)]` 定：

- 输入编号跑一组，跑完回到菜单；
- `a` = 顺序跑全部（默认作业；不含大正文的 45 个基准约 8~9 分钟，加大正文三组会更久）；
- `s` = 冒烟跑全部（`--job short`，3 轮，只确认跑得通）；
- `m` = 在同进程 / 隔离之间切换（当前方式印在标题下方，每组摘要末尾也会标一次）；
- `q` = 退出。

跑完打印的摘要（与 BDN 自己那张表并存，口径不同、不冲突）：

| 处理 | 原因 |
| --- | --- |
| 按参数组合分组、组内**基线排第一** | BDN 的行序是全局 FastestToSlowest，多参数组里各组合会交错，「这行是拿谁比出来的」就看不出来了 |
| **比值自己重算**（同参数组合 + `[Benchmark(Baseline = true)]`） | BDN 的 `Ratio` 列在多参数组里会把非基线行当成时间基线（见「怎么读数字」第 2 点）；摘要不抄它 |
| 时间列整张表一个单位 | 一行 ns 一行 µs 的表格没法横向比大小 |
| `Allocated` 按量级显示 B / KB / MB | 绝对值从 0 B 到几十 KB，统一用字节读不出量级 |
| 误差列 = 99.9% 置信区间半宽 | 与 BDN 的 `Error` 列同口径，`GetConfidenceInterval` 算，不手拼 |

摘要只**呈现**不判定：没有阈值、没有通过 / 不通过。判定口径仍是下面几节写的那些。


## 十组基准

| 类 | 热点 | 基线 | 读什么 |
| --- | --- | --- | --- |
| `RequestPipelineBenchmarks` | 请求全链路（真实回环 socket） | `HttpClient_GetString` | `Ratio` = 单请求时间比，**吞吐比 = 1 / Ratio** |
| `ConcurrentPipelineBenchmarks` | 8 条在飞时的每请求成本 | `HttpClient_Concurrent` | 同上；`Mean` 已经是每请求成本（`OperationsPerInvoke=8`） |
| `ConfigMergeBenchmarks` | `MergeConfig` + 头合并 | `GetUri_NoMerge` | `Ratio - 1` = 把按请求补丁合到默认值上的增量 |
| `ConfigPrimitiveBenchmarks` | `AxiosConfig.Clone` / `AxiosHeaders` 读写克隆 | 无（看绝对值） | `Allocated`：`Headers_Clone` 应恒为 24 B，`Headers_Set_Same` 应为 0 |
| `FormEncodingBenchmarks` | 表单展平 + 百分号编码 | `PostForm_FlatAscii` | 倍数 = 「编码成本 / 基础成本」 |
| `DynamicDeserializationBenchmarks` | `dynamic` 对象图构建 | `Typed` | `Ratio` = 比强类型贵几倍；`Dynamic_Wide` 看每节点成本 |
| `StreamingFramingBenchmarks` | 流式分帧（NDJSON / SSE / JSON 数组） | `Ndjson` | `Ratio` = 格式差；`Frames` 三档的**斜率** = 每帧成本 |
| `LargeBodyUploadBenchmarks` | 大正文上传（1 / 8 / 32MB：`byte[]` / `ReadOnlyMemory` / 流 / 异步序列） | `HttpClient_Upload_ByteArray` | `Allocated` 不随 `Megabytes` 涨 = 没有正文拷贝；三种 chunked 形态互相对照 |
| `LargeBodyDownloadBenchmarks` | 大正文下载（1 / 8 / 32MB：缓冲成 `byte[]` vs 流式读完） | `HttpClient_Download_Buffered` | 缓冲下载 `Allocated` ≈ 1× 载荷（8MB 是预分配边界）；流式下载只有读缓冲 |
| `LargeBodyJsonBenchmarks` | 大正文 JSON 响应（1 / 8 / 32MB：`byte[]` / 强类型 / `JsonElement`） | `HttpClient_GetBytes` | `Allocated / 载荷`：`byte[]` ≈ 1×；强类型 ≈ 3.4×；`JsonElement` ≈ 4.1×（文档 + 克隆 + token 元数据），三档线性 = 没有额外整串复制 |

五组回环 socket 的（`RequestPipeline` / `ConcurrentPipeline` / `LargeBodyUpload` / `LargeBodyDownload` / `LargeBodyJson`）
与五组纯 CPU 的（`ConfigMerge` / `ConfigPrimitive` / `FormEncoding` / `DynamicDeserialization` / `StreamingFraming`）
不要混在一个结论里用：前者的数字里有 TCP 往返与线程池调度，后者没有。大正文三组另有自己的口径：
`Allocated` 是主轴（确定性量），耗时只用于「同量级 / 谁更快」的判断。

## 怎么读数字

**1. 绝对 `Mean` 只在本机、本次运行内有意义；能拿来下结论的是 `Allocated` 与按正确基线算出的比值。**
跨机器、跨进程、跨天的 ns 数不可比 —— 这与 `ConcurrencyPerformanceTests` 的实测结论是同一件事。
但也要清楚 BDN 的 `Ratio` 并不是「同时对照」：**每个变体一个独立进程、顺序测**，
所以功耗策略 / 后台干扰的漂移仍然进得到比值里（这正是 `ConcurrencyPerformanceTests` 坚持
「同一窗口内两侧并行跑」的原因 —— BDN 做不到这件事）。因此：

- 先看 `Allocated`（确定性量，同样的代码路径就同样的字节数）；
- 再看 `Ratio`，并且和 `RatioSD` 比：差值小于 `RatioSD` 就是噪声；
- 要更强的结论就跑两遍，看两次的 `Ratio` 是否一致。单次运行里出现个别离群行是正常的 ——
  同一组我们跑了两遍，`AxiosNet_GetString` 从 146.6 µs 变成 102.1 µs，而 `Allocated` 两次都是 4.73 KB：
  时间会漂，分配不会。

**2. `Ratio` 列不能盲抄 —— 先确认它到底以谁为基线。**
BDN 在多参数组里会把「不是声明基线的那一行」当成时间基线。本机实测
`StreamingFramingBenchmarks` 的 `Frames = 1000` 块：`JsonArray` 拿到 `Ratio = 1.00`、`Ndjson` 变成 `1.02`，
而**同一块**的 `Alloc Ratio` 仍老实以 `Ndjson` 为 1.00（198.16 / 190.13 KB = 1.04）。
也就是说同一份报告里，时间比值和分配比值的基线可以不是同一个。读多参数组时二选一：
自己用 `Mean` 除声明基线行的 `Mean`，或者只信「基线行确实是 1.00」的那些块。
跑完打印的摘要表已经按这个口径重算过（同参数组合 + `[Benchmark(Baseline = true)]`），可以直接用；
BDN 报告里的 `Ratio` 列仍要按上面说的自己核对。

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

两轮都用 `--isolated`：同进程模式的绝对数受组顺序、JIT 分层与共享堆影响，前后两轮之间会漂 ——
用它做前后对比，差值里混的是测量环境而不是代码。`--job` 同理，两轮必须一致。

不用 `--exporters json`：BDN 默认就导出 `-report-github.md`（紧凑表格）、`-report.csv`（脚本友好）
和 `-report.html`，三份都带 `Mean` / `Error` / `StdDev` / `Allocated`，有基线的组还有 `Ratio`。
最省事的对比是直接 diff 两份 md（用 `git diff --no-index`，Windows 上不用额外装 `diff`）：

```bash
git diff --no-index \
  .baseline/results/AxiosNet.Benchmarks.ConfigMergeBenchmarks-report-github.md \
  BenchmarkDotNet.Artifacts/results/AxiosNet.Benchmarks.ConfigMergeBenchmarks-report-github.md
```

（`.baseline` 已存在时 `mv` 会把目录挪进去变成 `.baseline/BenchmarkDotNet.Artifacts`，别重复执行这一步。）

要脚本化就取 CSV：先看表头定位列，再按 `Method` + `Mean` / `Error` / `Allocated` 挑列对比。
列数随 `[Params]` 个数变化，别硬编码列号：

```bash
head -1 BenchmarkDotNet.Artifacts/results/AxiosNet.Benchmarks.ConfigMergeBenchmarks-report.csv
```

坚持用 JSON 的话得自己加 `--exporters json`，且注意两件事：顶层是对象、数组在 `Benchmarks` 下；
无参数的用例 `Parameters` 是**空串**而不是 `null`，`//` 兜不住，得显式判空：

```bash
jq -r '.Benchmarks[] | [.Method, (.Parameters | if .=="" then "-" else . end),
        (.Statistics.Mean|tostring), (.Memory.BytesAllocatedPerOperation|tostring)] | @tsv' \
  BenchmarkDotNet.Artifacts/results/*.json
```

判定建议（按信号强度排序，越靠前越可信）：

1. **`Allocated` 变化**：任何一行的每操作分配量上升都值得解释；只有被测路径变了才接受；
2. **`Mean` 超出 `Error`**：差值小于 `Error` 就是噪声，别下结论；
3. **同一类的相对次序**：`GetUri_Merge / GetUri_NoMerge` 这类比值在两次运行间应当稳定，
   它的漂移比绝对数更能说明「合并路径变重了」。这里的比值请自己用 `Mean` 除，别抄 `Ratio` 列 —— 理由见上一节。

## 已知噪声与陷阱

- **同进程与隔离的数字不能混比**：同一台机器 `Headers_Clone` 同进程 7.3 ns、隔离 5.8 ns，
  差的是测量环境（共享 GC 堆 / JIT 分层 / 线程池）而不是代码。同进程适合「这次改动方向对不对」，
  隔离适合「有没有退化」；`Allocated` 两者都可信。
- **回环 socket 组**：单请求 ~100 µs 里含 TCP 往返与线程池调度，绝对值会随机器负载摆；
  比值稳，绝对值不稳。测的时候别开其他吃 CPU 的进程。
- **`FakeHttpHandler` 的恒定开销**：每次请求多一次 `HttpResponseMessage` + `ByteArrayContent` 分配
  （约 2 次、共 ~200 B）。它对同一类里的各个变体是恒定的，看差值 / 比值时被抵消；看绝对值时要记得它在里面。
- **`--job short` 只能冒烟**：3 轮迭代下 `RatioSD` 能到 0.2，`Mean` 的 `Error` 能超过 `Mean` 本身。
  任何写进提交信息、PR 或本文档的数字都必须来自默认作业。（它是覆盖而不是追加，见「快速开始」。）
- **旧产物不会自动清掉**：BDN 只写不删。重跑时若只加了部分 `--exporters`，就会出现
  「`-report.csv` 是新的、`-report-full-compressed.json` 是上一轮的」这种混轮目录 ——
  我们复查时就撞上过一次（同一目录下 JSON 里 `AxiosNet_GetString` 是 146.6 µs，
  CSV 里是 102.1 µs）。对比前先 `rm -rf BenchmarkDotNet.Artifacts`。
- **首次运行会编译**：BDN 会为每组基准生成一个临时项目并编译，第一次跑要几十秒；
  之后同一作业会复用产物。
- **杀毒软件 / 进程创建**：BDN 每个基准用例起一个子进程，安全软件拦截会表现为
  「Detected error exit code from one of the benchmarks」，与代码无关。

## 一次实测样例

下面是本机（Intel Core Ultra 5 125H，Windows 10 22H2，.NET 8.0.31，Release，默认作业）
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

这张表能读出什么（也是「该怎么读」的示例）：

- **全链路**：同一份代码、同一台机器，两轮的单请求比值一个 1.37、一个 0.89 —— 这就是「绝对时间不能当门槛」
  最直白的证据；`Allocated` 两轮完全一致（4.73 KB vs 3.72 KB，多出的 1 KB 是解析 / 封装成本）。
  所以这一组只能下「与裸 `HttpClient` 同量级」的结论。真正可比的是并发组：8 条在飞时每请求
  58.5 µs vs 50.7 µs，折算吞吐比 ≈ 0.87，与 `ConcurrencyPerformanceTests` 的 85% 门槛是同一个结论
  （略慢、但过线），区别是这里带了误差条，且能看出多出来的 ~1 KB 分配花在哪。
- **合并**：`Merge / NoMerge` 六组比值是 1.06 / 0.97 / 0.89 / 1.67 / 0.91 / 1.14，而且 `Merge`（5 / 2）
  比 `Merge`（10 / 2）还慢（1,524 ns vs 1,021 ns）—— 头更多不可能更快，说明时间列这一档全是噪声。
  两条 `NoMerge` 行干的活完全一样（分配都是 1.09 KB），`Mean` 却差 24%（937 vs 1,160 ns），
  这就是本组的时间分辨率下限。能读的只有 `Allocated`：`Merge` 相对同参数 `NoMerge` 多出
  0 B（0 / 0）→ +200 B（0 / 2）→ +656 B（10 / 2），每多一批按请求头就多复制一版条目数组。
  这就是「先看 `Allocated`」的现场演示：时间列没有结论，分配列有。
- **原语**：`Headers_Clone` 恒 24 B、`Headers_Set_Same` 恒 0 B —— 两条设计承诺（克隆只共享不可变数组、
  幂等写不发布新版本）在基准里是可直接盯住的护栏；`Headers_Set_New` 288 B 是「发布一版新数组」的代价。
- **表单**：50 字段全部需要转义时比全 ASCII 贵 40%、多 15 KB（每字节一次映射 + 写出 `%XX`）。
- **dynamic**：小图 1.32×，宽图 2.19× —— 每节点成本（`AxiosDynamic` 构造 + 索引器写入）才是主要项，
  图越大差距越明显；这也说明「用 dynamic 处理大响应」是真实的成本，不只是风格问题。
- **分帧**：NDJSON 与 JSON 数组几乎同价（100 帧 1.06×、1000 帧 0.98×，差异都在 `RatioSD` 的量级上），
  SSE 在 1000 帧下慢 14%，但**分配 2.93 倍**（556.88 KB vs 190.13 KB；每帧先攒进 `StringBuilder` 再 `ToString`）
  —— 三处里最值得优化的一处，而只看时间列会漏掉它。

## 大正文组实测（2026-10-29）

同一台机器（Intel Core Ultra 5 125H，Windows 10 22H2，.NET 8.0.31，Release，默认作业、同进程），
`--filter '*LargeBody*'` 当时一轮 36 个用例（JSON 组是后加的，现在共 54 个）。上传 / 下载各自有独立基线，行内 `Ratio` 只与同组基线比。

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

这组能读出什么：

- **缓冲下载没有额外拷贝**：`Allocated / 载荷` = 1.003（1MB）、1.000（8MB，精确预分配边界）、1.000（32MB，段式装配）——两条路径都是「读满直接交出那个数组」。1MB 档的 1.003 是响应包装对象的固定开销。
- **上传的 `byte[]` / `ReadOnlyMemory` / 可 seek 流不碰正文**：三档尺寸的 `Allocated` 都在 9.6 KB 量级（内容对象 + 请求 / 响应包装），与载荷大小无关；唯一例外是 32MB 档 `AxiosNet_Upload_ByteArray` 的 27.85 KB——这一轮的一次性池化缓冲，仍不是正文拷贝。
- **chunked 传输有框架级的按次分配**：每次写出约 3–4 KB（`HttpClient` 侧同样有）。80 KB 缓冲的 `StreamChunked` 在 32MB 上是 1.1 MB（≈3.4%），64 KB 块的 `AsyncEnumerable` 是 2.2 MB（≈6.7%）——按块数乘出来的，块越大越省；它不是库内的正文缓冲。
- **流式正文的耗时只与流式路径同量级，先后顺序不可当结论**：异步序列少一次「读进 80 KB 缓冲再写出」的搬运，但在 1MB / 8MB 档上这点差异完全埋进噪声。证据是本节数据的一次复跑（`--filter '*AsyncEnumerable*'`，只改测量时机、不改代码）：1MB 的 `AsyncEnumerableBytes` 从 2,450.2 µs 变成 876.9 µs（2.8 倍），8MB 的 `AsyncEnumerableMemory` 从 5,765.2 µs 变成 8,765.5 µs（1.5 倍）——同一份工作量在两轮里相差数倍，说明时间列只能读「同量级」。复跑里稳定的仍是分配：1MB 75.7 KB、8MB 559–560 KB、32MB 2.22 MB，两轮一致。

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

这组能读出什么：

- **库侧与裸 STJ 的分配逐档一致**（强类型 3.40×、JsonElement 4.09×）：JSON 交付没有多复制一份字节，
  成本全在「对象图」或「文档 + 克隆」本身，库侧零额外分配。
- **强类型 3.4×** = 1× 原始字节 + `List` / 对象 / 每字符串一份 UTF-16；**JsonElement 4.09×** =
  1× 原始字节 + `Parse + RootElement.Clone() + 释放` 3.09×，而 3.09× 与裸
  `JsonSerializer.Deserialize<JsonElement>` 逐字节相同 —— 是 STJ 交付 `JsonElement` 的地板，
  不是库里的多余复制。常说的「3×」不够，实测是 4.09×；想再降只能改交付形态
  （例如改用流式 `IAsyncEnumerable<T>` 不落整图），不是改拷贝次数。
- **`Clone()` 不能省**（2026-10-30 实测，1MB、net6 / net8 一致）：省掉 Clone、让元素持有未释放的
  `JsonDocument`，看似省一次文本复制，实际更贵 —— 文档不释放就没有元数据数组回到池里，每次解析都要重新租：
  裸 `Parse`（不 Clone）本身就 3.91×，放进库的真实路径实测 **5.02×**（`AllocationTests` 里改成那样跑出来是红的），
  比现在的 `Parse + Clone + Dispose`（3.09× + 1× = 4.09×）多近 1×。护栏因此收到 4.5×：
  这个「优化」现在会被判红。
- **三档线性**：1 / 8 / 32MB 的比例不漂，说明没有隐藏的整串复制；`Bytes` 一档与二进制下载同口径（1×）。
- **时间列只作参考**：1MB 档 `AxiosNet_Typed` 28.3 ms vs `HttpClient_Typed` 11.5 ms，32MB 档反过来
  349 ms vs 410 ms —— 同进程共享 JIT / GC 状态下，先后顺序不可当结论；判定依据仍是 `Allocated`。

回归护栏在 `AxiosNet.Tests/Regression/AllocationTests.cs`：`JsonTypedResponse_PaysObjectGraphNotAnExtraCopy`
（1MB，< 4×）与 `JsonElementResponse_PaysDocumentAndClone`（1MB，< 4.5×），任何一次「整串 UTF-16 往返 /
额外复制」都会把比例推到门槛之上，省掉 `Clone` 的改法同样会撞线。

## 维护约定

- 同一类里的变体必须**只差被测变量**：服务器、载荷、并发度、Handler 对齐口径都要一致，
  否则测的是「两个场景的差异」而不是「一段代码的成本」。
- 新增热点时同步更新：本文件的表格、根 README「构建与测试 › 性能基准」的分组表，以及新类上的
  `[BenchmarkGroup]`（分组 + 口径说明）与 `[BenchmarkHint]`（怎么读）。菜单是运行时反射发现，
  不需要在任何清单里登记；漏写特性也不会让类消失 —— 缺分组会落进「未分组」，缺读法会在那一行提示补上。
- 改了 `ConcurrencyPerformanceTests` 里的 `CreateComparableHandler` 对齐口径，
  必须同步改 `Infrastructure/ComparableHttpClientHandler.cs`（两处的比值要能互相印证）。
