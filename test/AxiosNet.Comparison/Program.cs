using System.Globalization;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using AxiosNet.Comparison;
using AxiosNet.Comparison.Harness;
using AxiosNet.Comparison.Models;
using AxiosNet.Comparison.Targets;
using AxiosNet.Tests.Infrastructure;

return await ComparisonProgram.RunAsync(args);

internal static class ComparisonProgram
{
    public static async Task<int> RunAsync(string[] args)
    {
        Options options;
        try
        {
            options = Options.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            Options.PrintHelp();
            return 3;
        }

        if (options.ShowHelp)
        {
            Options.PrintHelp();
            return 0;
        }

        var usesGate = options.MinRatio is not null || options.MaxAllocRatio is not null;
        if (usesGate
            && (!options.Libraries.Contains("HttpClient", StringComparer.OrdinalIgnoreCase)
                || !options.Libraries.Contains("AxiosNet", StringComparer.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("门禁参数需要同时包含 HttpClient（基线）与 AxiosNet（被测对象）。");
            return 3;
        }

        // 报告文件要能跨机器对比：数字统一按不变文化输出（千分位、小数点不受系统区域设置影响）。
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

        var started = DateTimeOffset.Now;

        // 服务器与 ConcurrencyPerformanceTests 同一份 PerfHttpServer：两端口径能互相印证。
        await using var server = PerfHttpServer.Start(Payloads.UserJson.ToArray());
        var targets = LibraryCatalog.Create(options.Libraries, server.Url);
        var runner = new LoadRunner(options.Concurrency);

        try
        {
            Console.WriteLine($"回环服务器：{server.Url}（正文 {Payloads.UserJson.Length} B，HTTP/1.1 keep-alive）");
            Console.WriteLine(
                $"负载：{options.Concurrency} 并发 × {options.DurationSeconds:0.#}s × {options.Rounds} 轮，"
                + $"每实现预热 {options.WarmupSeconds:0.#}s；每轮轮转实现顺序");
            Console.WriteLine();

            foreach (var target in targets)
            {
                var warmup = await runner.RunAsync(target, TimeSpan.FromSeconds(options.WarmupSeconds));
                Console.WriteLine($"  预热 {target.Name,-12} {warmup.Completed,10:N0} 请求{DescribeWindow(warmup)}");
            }

            if (options.CooldownSeconds > 0)
            {
                Console.WriteLine($"  等待 {options.CooldownSeconds:0.#}s 让分层编译在后台落地……");
                await Task.Delay(TimeSpan.FromSeconds(options.CooldownSeconds));
            }

            // 机器刚跑热时频率/调度还在爬坡（实测前两轮吞吐可以只有稳态的一半），
            // 先用服务端基线连续量几窗，稳定了才开测，否则「谁排在第一轮谁吃亏」。
            await CalibrateAsync(runner, targets.OfType<RawSocketTarget>().First(), options.DurationSeconds);

            Console.WriteLine();

            var windows = targets.ToDictionary(t => t, _ => new List<WindowResult>());
            var serverBefore = server.Requests;
            for (var round = 0; round < options.Rounds; round++)
            {
                var order = Rotate(targets, round);
                var line = new StringBuilder($"第 {round + 1} 轮：");

                foreach (var target in order)
                {
                    var result = await runner.RunAsync(target, TimeSpan.FromSeconds(options.DurationSeconds));
                    windows[target].Add(result);
                    line.Append($"  {target.Name} {result.Rps:N0} req/s / p99 {result.P99Microseconds:N0} µs");
                    if (result.Errors > 0)
                    {
                        line.Append($"（{result.Errors} 个失败）");
                    }
                }

                Console.WriteLine(line.ToString());
            }

            var summaries = targets.Select(t => new TargetSummary(t, windows[t])).ToArray();
            var set = new SummarySet(summaries);

            var measured = set.Targets.Sum(t => t.Completed);
            var served = server.Requests - serverBefore;

            Console.WriteLine();
            Console.WriteLine("结果（各列取轮中位数）：");
            Console.WriteLine(Report.ConsoleTable(set));
            Console.WriteLine($"  测量窗口内服务器收到 {served:N0} 个请求 / 客户端完成 {measured:N0} 个；{DescribeEnvironment()}");

            if (served < measured)
            {
                Console.Error.WriteLine(
                    "  警告：服务器收到的请求少于客户端完成数 —— 有请求没有真正走网络，RPS 与延迟口径不可信。");
            }

            if (set.Targets.Any(t => t.SamplesDropped > 0))
            {
                Console.WriteLine("  注意：有 worker 的延迟样本超过记录上限，p99 由截断样本估计（吞吐与分配不受影响）。");
            }

            Console.WriteLine();
            foreach (var path in Export(set, options, started, server.Requests))
            {
                Console.WriteLine($"报告：{path}");
            }

            return Evaluate(set, options);
        }
        finally
        {
            foreach (var target in targets)
            {
                target.Dispose();
            }
        }
    }

    /// <summary>
    /// 用 RawSocket 基线做机器状态校准：连续 3 个窗口的 RPS 极差 ≤ 中位的 15% 就开测；
    /// 10 窗仍不稳说明外部负载持续存在，此时照常开测（比值仍同窗可比），但把状态打在屏幕上。
    /// </summary>
    private static async Task CalibrateAsync(LoadRunner runner, ITarget baseline, double windowSeconds)
    {
        var recent = new Queue<double>(3);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var window = await runner.RunAsync(baseline, TimeSpan.FromSeconds(windowSeconds));
            Console.WriteLine($"  校准 {attempt,2}：RawSocket {window.Rps,9:N0} req/s{DescribeWindow(window)}");

            if (window.Errors > 0)
            {
                recent.Clear();
                continue;
            }

            recent.Enqueue(window.Rps);
            if (recent.Count > 3)
            {
                recent.Dequeue();
            }

            if (recent.Count == 3)
            {
                var values = recent.ToArray();
                var median = Statistics.Median(values);
                if (values.Max() - values.Min() <= median * 0.15)
                {
                    return;
                }
            }
        }

        Console.WriteLine("  校准未收敛（机器负载持续不稳），按当前状态开测：同一轮内的比值仍可比，绝对值会偏低。");
    }

    /// <summary>每轮把实现顺序整体旋转一位：机器负载的漂移会均摊到所有实现上，而不是永远压在第一位的实现。</summary>
    private static IReadOnlyList<ITarget> Rotate(IReadOnlyList<ITarget> targets, int round)
    {
        var offset = round % targets.Count;
        return targets.Skip(offset).Concat(targets.Take(offset)).ToArray();
    }

    private static string DescribeWindow(WindowResult window)
        => window.Errors > 0
            ? $"（{window.Errors} 个失败：{window.FirstError?.GetType().Name}）"
            : string.Empty;

    private static IEnumerable<string> Export(
        SummarySet set,
        Options options,
        DateTimeOffset started,
        long serverRequests)
    {
        var directory = Path.GetFullPath(options.OutputDirectory, Environment.CurrentDirectory);
        Directory.CreateDirectory(directory);

        var stamp = started.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var tag = SanitizeTag(options.Tag);
        var name = tag is null
            ? $"comparison-{stamp}"
            : $"comparison-{tag}-{stamp}";

        var markdownPath = Path.Combine(directory, name + ".md");
        var csvPath = Path.Combine(directory, name + ".csv");

        File.WriteAllText(
            markdownPath,
            Report.BuildMarkdown(
                set,
                options.Concurrency,
                options.DurationSeconds,
                options.Rounds,
                options.WarmupSeconds,
                started,
                DescribeEnvironment(),
                serverRequests));

        File.WriteAllText(csvPath, Report.BuildCsv(set));
        return [markdownPath, csvPath];
    }

    /// <summary>tag 会拼进文件名：把非法字符（含路径分隔符）换成 '-'，避免 `--tag 15:00` 这种时间戳直接抛 IO 异常。</summary>
    private static string? SanitizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            tag = tag.Replace(invalid, '-');
        }

        return tag.Trim();
    }

    private static int Evaluate(SummarySet set, Options options)
    {
        var hasErrors = set.Targets.Any(t => t.Errors > 0);
        if (hasErrors)
        {
            // 有失败请求时吞吐与分配都不是完整样本，再打「门禁通过」只会误导；直接按「数字不可信」退出。
            Console.Error.WriteLine("存在失败请求，本轮数字不可信，跳过硬门禁判定：");
            foreach (var summary in set.Targets.Where(t => t.Errors > 0))
            {
                var first = summary.Windows.Select(w => w.FirstError).FirstOrDefault(e => e is not null);
                Console.Error.WriteLine(
                    $"  {summary.Target.Name}: {summary.Errors} 个失败，首个异常 {first?.GetType().Name}: {first?.Message}");
            }

            return 2;
        }

        var gateFailed = false;

        if (options.MinRatio is { } minRatio)
        {
            // 与分配门禁对称：基数为 0 时比值不是「通过」而是「没有比较对象」。
            if (set.HttpClient!.Rps <= 0)
            {
                Console.WriteLine("门禁 RPS：HttpClient 没有完成任何请求（基线不可用），跳过该门禁。");
            }
            else
            {
                var ratio = set.AxiosNet!.Rps / set.HttpClient.Rps;
                var passed = ratio >= minRatio;
                Console.WriteLine(
                    $"门禁 RPS：AxiosNet / HttpClient = {Report.FormatRatio(ratio)}"
                    + $"（门槛 ≥ {Report.FormatPercent(minRatio)}）→ {(passed ? "通过" : "未通过")}");
                gateFailed |= !passed;
            }
        }

        if (options.MaxAllocRatio is { } maxAllocRatio)
        {
            var baseline = set.HttpClient!.NetAllocatedBytesPerRequest;
            if (baseline <= 0)
            {
                Console.WriteLine("门禁分配：HttpClient 的净分配为 0（基线不可用），跳过该门禁。");
            }
            else
            {
                var ratio = set.AxiosNet!.NetAllocatedBytesPerRequest / baseline;
                var passed = ratio <= maxAllocRatio;
                Console.WriteLine(
                    $"门禁分配：AxiosNet / HttpClient = {ratio:0.00}×（上限 ≤ {maxAllocRatio:0.##}×）→ {(passed ? "通过" : "未通过")}");
                gateFailed |= !passed;
            }
        }

        return gateFailed ? 1 : 0;
    }

    private static string DescribeEnvironment()
    {
#if DEBUG
        const string build = "Debug";
#else
        const string build = "Release";
#endif

        return $"{RuntimeInformation.OSDescription.Trim()} · {Environment.ProcessorCount} 核 · "
            + $"{RuntimeInformation.FrameworkDescription} · Server GC={GCSettings.IsServerGC} · {build} 构建";
    }
}
