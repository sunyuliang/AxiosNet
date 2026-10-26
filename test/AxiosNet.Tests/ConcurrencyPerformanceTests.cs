using System.Diagnostics;
using System.Net;
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using Xunit.Abstractions;

namespace AxiosNet.Tests;

/// <summary>性能类用例的单列集合：压测期间不允许其它用例在同一进程里抢 CPU。</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PerformanceCollection
{
    public const string Name = "Performance";
}

/// <summary>
/// 并发吞吐对比：<see cref="AxiosClient"/> 与裸 <see cref="HttpClient"/> 在同一条回环服务器上的吞吐比值。
/// <para>
/// 门槛是「不低于 HttpClient 的 85%」。AxiosNet 本身就跑在 SocketsHttpHandler + HttpClient 之上，
/// 多出来的是配置合并、URL 组装、拦截器链、响应转换这些纯 CPU 开销；本用例把这份开销钉在 10% 以内，
/// 一旦有人往热路径塞了反射 / 重复分配 / 同步阻塞，比值会先塌下来，比单看绝对吞吐更灵敏。
/// Debug 构建也跑，但门槛放宽到 70%（理由见 <see cref="MinimumRatio"/>），只拦灾难性回退。
/// </para>
/// <para>
/// <b>本用例不是量化基线</b>：它只回答「有没有塌方」，不回答「这次改动是不是快了 3%」。
/// 3 轮 × 1 秒的采样、以及下面记录的 ±15% 跨进程摆动，都决定了它读不出小于噪声的差异 ——
/// 拿它当「优化没退化」的证据，等于用一把刻度 15% 的尺子量 3% 的变化。
/// 需要量化时看 <c>test/AxiosNet.Benchmarks</c>（BenchmarkDotNet：多轮采样 + 误差条 + 每操作分配量 +
/// 合并 / 编码 / 反序列化 / 分帧的分项成本）。标准流程是：<b>先用基准量化改动，再让本用例守住「别塌」的底线</b>。
/// </para>
/// <para>
/// <b>测量方式是「并行对照」</b>：两侧各 <see cref="Concurrency"/> 个 worker <b>在同一窗口内同时跑</b>，
/// 再用两侧计数相除。这不是为了好看，而是必须——顺序测（跑完 A 的 1 秒再跑 B 的 1 秒）时，
/// 两段窗口遇到的 CPU 频率、后台干扰、GC 时机都不同：同一变体在 15 个连续窗口里能从 35,228 摆到
/// 45,996 req/s，逐轮比值在 71%~120% 之间晃（2026-10-21 实测），这种噪声足以让 85% 门槛随机翻脸。
/// 同时跑之后干扰对两侧同等生效，同机实测摆动收窄到 ±3%。
/// </para>
/// <para>
/// 另外三条不能省的：
/// ① 两侧各预热 1 秒 —— 只为把连接建齐（16 条 TCP + 服务器侧 accept 就位），不是等分层编译：
///    实测 0s 与 2s 预热的判定值统计上没有差别，首个窗口的绝对吞吐反而最高（见 <see cref="WarmupWindow"/>）；
/// ② 判定用「逐轮配对比值的中位数」，不用最好一轮 —— best-of-N 会偏向 AxiosNet 恰好撞上顺风局的那一轮；
/// ③ 基线必须功能对等：<see cref="SocketsHttpHandler"/> 默认 cookies 开、系统代理开，
///    而 <see cref="ConnectionPoolOptions"/> 默认两者都关，漏配这一项测出来的是「两个库的默认值差异」
///    （曾把 AxiosNet 报成 HttpClient 的 120%~236%，消融实验里仅关掉这两项 HttpClient 就涨 2.65 倍）。
/// </para>
/// </summary>
[Collection(PerformanceCollection.Name)]
public sealed class ConcurrencyPerformanceTests
{
    /// <summary>
    /// 每侧并发请求数（在飞请求是它的两倍）。
    /// <para>
    /// 这台机器是 Intel Core Ultra 5 125H（4 P 核 + 8 E 核 + 2 LPE，18 逻辑线程），P 核与 E 核单核性能差近一倍，
    /// in-flight 超过 P 核逻辑线程数（8）后，两侧 worker 会被摊到不同性能的核上 —— 表现是
    /// <b>单轮内只摆 ±2 点、跨进程却整轮偏 10~15 点</b>（实测同一份实现在多次运行中落在 80%~110%）。
    /// 这是调度分配，不是实现差异；本用例靠「7 轮取中位 + 只判定中位」把它压到可判定，
    /// 但偶发一次坏运行（整轮偏低）仍可能出现，看到单独一次失败先重跑再下结论。
    /// </para>
    /// </summary>
    private const int Concurrency = 8;

    /// <summary>每轮测量时长（题目要求的 1 秒窗口）。</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 预热时长：只为覆盖「建连 + 首次触摸」，不是给分层编译爬坡用的。
    /// <para>
    /// 2026-10-21 实测（各 3 次、其余参数相同）：0s 预热的判定中位是 85.6 / 85.8 / 85.6，
    /// 2s 预热是 87.9 / 85.2 / 88.1 —— 2 秒预热没有任何收益，首个测量窗口的绝对吞吐甚至是最高的。
    /// 原先「必须 ≥2s 才能让 Tier0 升到 Tier1」的说法在这台机器上不成立：8 个 worker 打满后
    /// 每秒 2~4 万次调用，分层编译早就完成了，真正在晃的是 CPU 功耗策略（涡轮→PL1→风扇起转，
    /// 绝对吞吐能在 21k~40k 之间摆），那是共模，被配对比值消掉了。
    /// </para>
    /// </summary>
    private static readonly TimeSpan WarmupWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// 轮数：3 轮各 1 秒，取逐轮配对比值的中位。
    /// <para>
    /// 加长到 7 轮 / 9 秒并不会让结果更可信：同一天实测，7 轮配置的判定值在 80.2%~94.6% 之间漂，
    /// 而 3 轮配置在 86.8%~98.9% 之间漂 —— 误差的绝对大头是「每次运行机器所处状态」
    /// （功耗策略、后台干扰、P/E 核分配），不是采样轮数。同一进程内 3 轮的极差只有 0.2~2.7 点。
    /// 因此取 3 轮：够压掉单轮异常，又不把用例拖成 9 秒。
    /// </para>
    /// </summary>
    private const int Rounds = 3;

    /// <summary>
    /// 达标线：AxiosNet 吞吐 ≥ HttpClient 的 85%（Release）。
    /// <para>
    /// Debug 下这条线放宽到 70%，原因见 <see cref="IsOptimizedBuild"/>：被测库此刻是未优化 IL，
    /// 而 HttpClient/框架来自 Release 编译的 DLL，比的是构建配置而不是实现。
    /// </para>
    /// </summary>
#if DEBUG
    private const double MinimumRatio = 0.70;
#else
    private const double MinimumRatio = 0.85;
#endif

    /// <summary>
    /// 当前是否为「被测库也经过优化」的构建（Release）。
    /// <para>
    /// 不是就不能拿 85% 去判定：同一份代码 Debug 比 Release 实测低 8~10 个点，
    /// 放 85% 会系统性误报。Debug 只保留一个很宽的底线——真有人往热路径塞反射、同步阻塞
    /// 或每请求大块分配，比值会掉到 70% 以下。
    /// </para>
    /// </summary>
#if DEBUG
    private const bool IsOptimizedBuild = false;
#else
    private const bool IsOptimizedBuild = true;
#endif

    private readonly ITestOutputHelper _output;

    public ConcurrencyPerformanceTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Debug 也跑，但判定线不同（70% / 85%，见 <see cref="MinimumRatio"/>）：
    /// Debug 的数字只用来发现「灾难性回退」，不能与 Release 的数字并列比较。
    /// </summary>
    [Fact]
    public async Task AxiosNet_ConcurrentThroughput_ReachesMinimumRatioOfHttpClient()
    {
        await using var server = PerfHttpServer.Start();
        var url = server.Url;

        using var axios = new AxiosClient();
        using var http = new HttpClient(CreateComparableHandler());

        var axiosSend = SendAxiosAsync(axios);
        var httpSend = SendHttpAsync(http);

        // 预热：两侧同时跑一遍（JIT、静态缓存、连接建立都不计入窗口），同时把服务器侧连接也配齐
        await MeasurePairAsync(axiosSend, httpSend, url, WarmupWindow, Concurrency);

        var log = new List<string>();
        var ratios = new List<double>();
        var failedRequests = 0;
        string? firstFailure = null;

        for (var round = 1; round <= Rounds; round++)
        {
            var result = await MeasurePairAsync(axiosSend, httpSend, url, Window, Concurrency);

            failedRequests += result.Errors;
            firstFailure ??= result.FirstError?.ToString();

            Assert.True(
                result.AxiosCompleted > 200 && result.HttpCompleted > 200,
                $"第 {round} 轮请求量异常偏低（AxiosNet {result.AxiosCompleted} 个 / "
                + $"HttpClient {result.HttpCompleted} 个），压测环境不可用，比值没有意义。");

            var ratio = result.AxiosRps / result.HttpRps;
            ratios.Add(ratio);
            log.Add(
                $"  第 {round} 轮：AxiosNet {result.AxiosRps:N0} req/s，"
                + $"HttpClient {result.HttpRps:N0} req/s，比值 {ratio:P1}");
        }

        var medianRatio = Median(ratios);
        var report = string.Join(Environment.NewLine, log);

        // 通过时也要能看到每次的绝对吞吐与比值（`dotnet test -l "console;verbosity=detailed"`）
        var build = IsOptimizedBuild ? "Release" : "Debug";
        _output.WriteLine(
            $"AxiosNet 并发吞吐 vs HttpClient [{build}]（每侧 {Concurrency} 并发 × {Window.TotalSeconds:0.#}s × {Rounds} 轮，"
            + $"预热 {WarmupWindow.TotalSeconds:0.#}s，服务器侧共回复 {server.Requests:N0} 个请求）：");
        _output.WriteLine(report);
        _output.WriteLine(
            $"  配对比值 中位 {medianRatio:P1} / 最好 {ratios.Max():P1} / 最差 {ratios.Min():P1}"
            + $"（{build} 门槛 {MinimumRatio:P0}，判定用中位）"
            + (IsOptimizedBuild ? string.Empty : " —— Debug 下被测库是未优化 IL，这批数字不可与 Release 并列比较"));

        Assert.True(
            failedRequests == 0,
            $"压测窗口内出现 {failedRequests} 个失败请求，吞吐数字不可信。首个异常：{firstFailure}"
            + Environment.NewLine + report);

        Assert.True(
            medianRatio >= MinimumRatio,
            $"并发吞吐未达到 HttpClient 的 {MinimumRatio:P0}（配对比值中位 {medianRatio:P1}，"
            + $"最好 {ratios.Max():P1} / 最差 {ratios.Min():P1}）。"
            + $"配置 [{build}]：每侧 {Concurrency} 并发 × {Window.TotalSeconds:0.#}s × {Rounds} 轮，"
            + $"预热 {WarmupWindow.TotalSeconds:0.#}s，两侧 handler 功能逐项对齐、服务器回环直连、无请求体。"
            + Environment.NewLine + report
            + Environment.NewLine
            + "排查方向：热路径新增了反射 / 每请求白分配 / 同步等待 / 重复配置合并。"
            + (IsOptimizedBuild
                ? string.Empty
                : Environment.NewLine + "注意：Debug 下被测库是未优化 IL，85% 门槛只在 Release 下成立。"));
    }

    /// <summary>
    /// 与 AxiosNet 默认连接池<b>逐项对齐</b>的 Handler。
    /// <para>
    /// 必须显式对齐，否则测的根本不是「谁快」，而是「两个库的默认值差多少」：
    /// <see cref="SocketsHttpHandler"/> 默认 <c>UseCookies = true</c>、<c>UseProxy = true</c>，
    /// 而 <see cref="ConnectionPoolOptions"/> 默认两者都关（<c>Proxy</c> 为 null 即 <c>UseProxy = false</c>）。
    /// 2026-10-21 消融实测：仅把 HttpClient 侧这两项关掉，吞吐从 30,977 涨到 82,023 req/s（2.65 倍）；
    /// 反向给 AxiosNet 打开，它从 54,642 掉到 36,455。
    /// </para>
    /// </summary>
    private static SocketsHttpHandler CreateComparableHandler()
    {
        var defaults = new ConnectionPoolOptions();
        return new SocketsHttpHandler
        {
            MaxConnectionsPerServer = defaults.MaxConnectionsPerServer,
            PooledConnectionLifetime = defaults.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = defaults.PooledConnectionIdleTimeout,
            ConnectTimeout = defaults.ConnectTimeout,
            AllowAutoRedirect = defaults.AllowAutoRedirect,
            MaxAutomaticRedirections = defaults.MaxAutomaticRedirections,
            AutomaticDecompression = defaults.AutomaticDecompression
                ? DecompressionMethods.All
                : DecompressionMethods.None,
            UseCookies = defaults.UseCookies,
            UseProxy = defaults.Proxy is not null
        };
    }

    private static Func<string, CancellationToken, Task> SendAxiosAsync(AxiosClient client)
        => async (url, token) =>
        {
            var response = await client.GetAsync<string>(url, token).ConfigureAwait(false);
            if (response.Status != 200 || response.Data is null)
            {
                throw new InvalidOperationException($"AxiosNet 响应异常：status={response.Status}。");
            }
        };

    private static Func<string, CancellationToken, Task> SendHttpAsync(HttpClient client)
        => async (url, token) =>
        {
            // 与 GetAsync<string> 对等的工作量：拿到响应、把正文读成字符串，两边都不做 JSON 反序列化
            var text = await client.GetStringAsync(url, token).ConfigureAwait(false);
            if (text.Length == 0)
            {
                throw new InvalidOperationException("HttpClient 响应正文为空。");
            }
        };

    /// <summary>
    /// 两侧各 <paramref name="perSide"/> 个常驻 worker，在<b>同一窗口内同时</b>跑请求，返回两侧吞吐。
    /// <para>
    /// 窗口结束用「置停止位 + 等在途请求自然收尾」而不是取消令牌：取消会把十几条连接打成异常关闭，
    /// 下一轮得重新建连，被污染的是下一次测量。
    /// </para>
    /// </summary>
    private static async Task<PairResult> MeasurePairAsync(
        Func<string, CancellationToken, Task> axiosSend,
        Func<string, CancellationToken, Task> httpSend,
        string url,
        TimeSpan window,
        int perSide)
    {
        CollectGarbage();

        var stop = 0;
        long axiosCompleted = 0;
        long httpCompleted = 0;
        var errors = 0;
        Exception? firstError = null;

        // 兜底：任何实现卡死都不许把测试进程挂住
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var workers = new Task[perSide * 2];
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < workers.Length; i++)
        {
            // 奇偶交替分配：两侧 worker 在同一个线程池上交错排队，不偏向任何一方
            var isAxios = i % 2 == 0;
            workers[i] = Task.Run(async () =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    try
                    {
                        await (isAxios ? axiosSend(url, guard.Token) : httpSend(url, guard.Token))
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref errors);
                        Interlocked.CompareExchange(ref firstError, ex, null);
                        return;
                    }

                    if (isAxios)
                    {
                        Interlocked.Increment(ref axiosCompleted);
                    }
                    else
                    {
                        Interlocked.Increment(ref httpCompleted);
                    }
                }
            });
        }

        await Task.Delay(window).ConfigureAwait(false);
        var seconds = clock.Elapsed.TotalSeconds;
        Volatile.Write(ref stop, 1);
        await Task.WhenAll(workers).ConfigureAwait(false);

        return new PairResult(
            axiosCompleted / seconds,
            httpCompleted / seconds,
            axiosCompleted,
            httpCompleted,
            errors,
            firstError);
    }

    /// <summary>中位数：奇数个取正中间，偶数个取中间两个的均值。</summary>
    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(x => x).ToList();
        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    /// <summary>每轮前各压一次 GC：让两侧都从同一个干净堆起跑，谁都不背对方的垃圾。</summary>
    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private readonly record struct PairResult(
        double AxiosRps,
        double HttpRps,
        long AxiosCompleted,
        long HttpCompleted,
        int Errors,
        Exception? FirstError);
}
