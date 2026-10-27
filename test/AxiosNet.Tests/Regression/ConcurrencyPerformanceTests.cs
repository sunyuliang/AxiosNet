
using AxiosNet.Pooling;
using AxiosNet.Tests.Infrastructure;
using System.Diagnostics;
using System.Net;
using Xunit.Abstractions;

namespace AxiosNet.Tests.Regression;

/// <summary>
/// 并发吞吐下限：与 `HttpClient` 的配对比值。
/// </summary>
[Collection(PerformanceCollection.Name)]
public sealed class ConcurrencyPerformanceTests
{
    /// <summary>Debug 也跑，但判定线是 70%，只用来发现灾难性回退。</summary>
    [Fact]
    public async Task AxiosNet_ConcurrentThroughput_ReachesMinimumRatioOfHttpClient()
    {
        await using var server = PerfHttpServer.Start();
        var url = server.Url;

        using var axios = new AxiosClient();
        using var http = new HttpClient(CreateComparableHandler());

        var axiosSend = SendAxiosAsync(axios);
        var httpSend = SendHttpAsync(http);

        // 预热：两侧同时跑一遍，把连接建齐，不计入窗口。
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

        // 通过时也打印每轮的绝对吞吐与比值。
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

    /// <summary>每侧并发请求数（在飞请求是它的两倍）。</summary>
    private const int Concurrency = 8;

    /// <summary>每轮测量时长。</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(1);

    /// <summary>预热时长：只为建连与首次触摸，不计入窗口。</summary>
    private static readonly TimeSpan WarmupWindow = TimeSpan.FromSeconds(1);

    /// <summary>轮数：取逐轮配对比值的中位。</summary>
    private const int Rounds = 3;

    /// <summary>达标线：AxiosNet 吞吐 ≥ HttpClient 的 85%（Release），Debug 放宽到 70%。</summary>
#if DEBUG
    private const double MinimumRatio = 0.70;

#else
    private const double MinimumRatio = 0.85;

#endif

    /// <summary>被测库是否经过优化（Release）；Debug 下只拦灾难性回退。</summary>
#if DEBUG
    private const bool IsOptimizedBuild = false;

#else
    private const bool IsOptimizedBuild = true;

#endif

    private readonly ITestOutputHelper _output;

    public ConcurrencyPerformanceTests(ITestOutputHelper output) => _output = output;

    /// <summary>与 AxiosNet 默认连接池逐项对齐的 Handler：否则测的是两个库的默认值差异。</summary>
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
            // 与 GetAsync<string> 对等的工作量：拿到响应并把正文读成字符串。
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