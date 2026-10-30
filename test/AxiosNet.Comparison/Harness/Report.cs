using System.Globalization;
using System.Text;
using AxiosNet.Comparison.Targets;

namespace AxiosNet.Comparison.Harness;

/// <summary>一个实现在全部轮次上的汇总（每项取轮中位数，抗单轮抖动）。</summary>
internal sealed class TargetSummary
{
    public TargetSummary(ITarget target, IReadOnlyList<WindowResult> windows)
    {
        Target = target;
        Windows = windows;
        Rps = Statistics.Median(windows.Select(w => w.Rps));
        P50Microseconds = Statistics.Median(windows.Select(w => w.P50Microseconds));
        P99Microseconds = Statistics.Median(windows.Select(w => w.P99Microseconds));
        MaxMicroseconds = Statistics.Median(windows.Select(w => w.MaxMicroseconds));
        AllocatedBytesPerRequest = Statistics.Median(windows.Select(w => w.AllocatedBytesPerRequest));
        CpuShare = Statistics.Median(windows.Select(w => w.CpuShare));
        Completed = windows.Sum(w => w.Completed);
        Errors = windows.Sum(w => w.Errors);
        SamplesDropped = windows.Sum(w => w.SamplesDropped);
    }

    public ITarget Target { get; }

    public IReadOnlyList<WindowResult> Windows { get; }

    public double Rps { get; }

    public double P50Microseconds { get; }

    public double P99Microseconds { get; }

    public double MaxMicroseconds { get; }

    /// <summary>进程总分配 / 请求：含同进程回环服务端的固定开销。</summary>
    public double AllocatedBytesPerRequest { get; }

    /// <summary>减掉 <see cref="RawSocket"/> 基线后的客户端净分配 / 请求（可能因噪声略偏负，按 0 显示）。</summary>
    public double NetAllocatedBytesPerRequest { get; set; }

    public double CpuShare { get; }

    public long Completed { get; }

    public int Errors { get; }

    public int SamplesDropped { get; }
}

/// <summary>全部实现 + 服务端基线。</summary>
internal sealed class SummarySet
{
    public SummarySet(IReadOnlyList<TargetSummary> targets)
    {
        Baseline = targets.Single(t => t.Target is RawSocketTarget);

        // 用户可以用 --libs 把 RawSocket 排在任意位置；展示顺序固定为「客户端在前、基线在最后」。
        Targets = targets.Where(t => !ReferenceEquals(t, Baseline)).Append(Baseline).ToArray();
        HttpClient = Targets.SingleOrDefault(t => t.Target is HttpClientTarget);
        AxiosNet = Targets.SingleOrDefault(t => t.Target is AxiosNetTarget);

        foreach (var target in Targets)
        {
            target.NetAllocatedBytesPerRequest =
                Math.Max(0, target.AllocatedBytesPerRequest - Baseline.AllocatedBytesPerRequest);
        }
    }

    /// <summary>展示顺序：客户端实现在前、服务端基线在最后。</summary>
    public IReadOnlyList<TargetSummary> Targets { get; }

    public TargetSummary Baseline { get; }

    public TargetSummary? HttpClient { get; }

    public TargetSummary? AxiosNet { get; }
}

internal static class Report
{
    public static string ConsoleTable(SummarySet set)
    {
        string[] headers =
            ["实现", "RPS", "p50 (µs)", "p99 (µs)", "max (µs)", "分配/请求", "净分配/请求", "CPU", "vs HttpClient"];

        var rows = new List<string[]>();
        foreach (var summary in set.Targets)
        {
            var ratio = set.HttpClient is null || ReferenceEquals(summary, set.HttpClient)
                ? "—"
                : FormatRatio(summary.Rps / set.HttpClient.Rps);

            rows.Add(
            [
                summary.Target.Name,
                summary.Rps.ToString("N0", CultureInfo.InvariantCulture),
                summary.P50Microseconds.ToString("N0", CultureInfo.InvariantCulture),
                summary.P99Microseconds.ToString("N0", CultureInfo.InvariantCulture),
                summary.MaxMicroseconds.ToString("N0", CultureInfo.InvariantCulture),
                FormatBytes(summary.AllocatedBytesPerRequest),
                FormatBytes(summary.NetAllocatedBytesPerRequest),
                FormatPercent(summary.CpuShare),
                ratio
            ]);
        }

        return FormatTable(headers, rows);
    }

    public static string BuildMarkdown(
        SummarySet set,
        int concurrency,
        double durationSeconds,
        int rounds,
        double warmupSeconds,
        DateTimeOffset started,
        string environment,
        long serverRequests)
    {
        var md = new StringBuilder();
        md.AppendLine("# 跨实现横向基准");
        md.AppendLine();
        md.AppendLine($"- 时间：{started:yyyy-MM-dd HH:mm:ss zzz}");
        md.AppendLine($"- 环境：{environment}");
        md.AppendLine($"- 负载：每实现 {concurrency} 并发 × {durationSeconds:0.#}s × {rounds} 轮（每轮轮转实现顺序），预热 {warmupSeconds:0.#}s；同一回环服务器、同一 `User` JSON。");
        md.AppendLine("- 对齐：HttpClient / Refit / RestSharp / Flurl 注入与 AxiosNet 默认连接池逐项对齐的 `SocketsHttpHandler`；Refit 只走 source-generated 模式。");
        md.AppendLine($"- 分配是进程级口径（含同进程回环服务端；本次运行服务器共回复 {serverRequests:N0} 个请求）：`净分配` = 总分配 − RawSocket 行。");
        md.AppendLine("- p50 / p99 为客户端观察值，含服务端处理、回环 RTT 与 JSON 反序列化；各列为轮中位数。");
        md.AppendLine();
        md.AppendLine("| 实现 | RPS | p50 (µs) | p99 (µs) | max (µs) | 分配/请求 | 净分配/请求 | CPU | RPS vs HttpClient |");
        md.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (var summary in set.Targets)
        {
            var ratio = set.HttpClient is null || ReferenceEquals(summary, set.HttpClient)
                ? "—"
                : FormatRatio(summary.Rps / set.HttpClient.Rps);

            md.AppendLine(
                $"| {summary.Target.Name} | {summary.Rps:N0} | {summary.P50Microseconds:N0} | "
                + $"{summary.P99Microseconds:N0} | {summary.MaxMicroseconds:N0} | "
                + $"{FormatBytes(summary.AllocatedBytesPerRequest)} | "
                + $"{FormatBytes(summary.NetAllocatedBytesPerRequest)} | {FormatPercent(summary.CpuShare)} | {ratio} |");
        }

        md.AppendLine();
        md.AppendLine("## 每轮原始 RPS");
        md.AppendLine();
        md.Append("| 实现 |");
        for (var round = 1; round <= rounds; round++)
        {
            md.Append($" 第 {round} 轮 |");
        }

        md.AppendLine();
        md.Append("| --- |");
        for (var round = 1; round <= rounds; round++)
        {
            md.Append(" ---: |");
        }

        md.AppendLine();

        foreach (var summary in set.Targets)
        {
            md.Append($"| {summary.Target.Name} |");
            foreach (var window in summary.Windows)
            {
                md.Append($" {window.Rps:N0} |");
            }

            md.AppendLine();
        }

        if (set.Targets.Any(t => t.Errors > 0))
        {
            md.AppendLine();
            md.AppendLine("> 警告：存在失败请求，数字不可信。首个异常见控制台输出。");
        }

        return md.ToString();
    }

    public static string BuildCsv(SummarySet set)
    {
        var csv = new StringBuilder();
        csv.AppendLine("target,round,seconds,completed,errors,rps,p50_us,p99_us,max_us,allocated_bytes_per_request,cpu_share");

        foreach (var summary in set.Targets)
        {
            for (var i = 0; i < summary.Windows.Count; i++)
            {
                var window = summary.Windows[i];
                csv.Append(summary.Target.Name).Append(',')
                    .Append(i + 1).Append(',')
                    .Append(window.Seconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(',')
                    .Append(window.Completed).Append(',')
                    .Append(window.Errors).Append(',')
                    .Append(window.Rps.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                    .Append(window.P50Microseconds.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                    .Append(window.P99Microseconds.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                    .Append(window.MaxMicroseconds.ToString("0.##", CultureInfo.InvariantCulture)).Append(',')
                    .Append(window.AllocatedBytesPerRequest.ToString("0.#", CultureInfo.InvariantCulture)).Append(',')
                    .Append(window.CpuShare.ToString("0.####", CultureInfo.InvariantCulture))
                    .AppendLine();
            }
        }

        return csv.ToString();
    }

    public static string FormatBytes(double bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes:N0} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024:N1} KB";
        }

        return $"{bytes / (1024 * 1024):N2} MB";
    }

    public static string FormatRatio(double ratio) => FormatPercent(ratio, decimals: 1);

    public static string FormatPercent(double ratio, int decimals = 0)
        => (ratio * 100).ToString("F" + decimals, CultureInfo.InvariantCulture) + "%";

    /// <summary>
    /// 按显示宽度对齐（中文占两列），列内右对齐。表头也用同一套宽度，
    /// 否则中文表头与 ASCII 数据行会错位。
    /// </summary>
    private static string FormatTable(string[] headers, IReadOnlyList<string[]> rows)
    {
        var widths = new int[headers.Length];
        for (var column = 0; column < headers.Length; column++)
        {
            widths[column] = DisplayWidth(headers[column]);
        }

        foreach (var row in rows)
        {
            for (var column = 0; column < row.Length; column++)
            {
                widths[column] = Math.Max(widths[column], DisplayWidth(row[column]));
            }
        }

        var builder = new StringBuilder();
        AppendRow(builder, headers, widths);
        builder.AppendLine(new string('-', widths.Sum() + (2 * (widths.Length - 1))));
        foreach (var row in rows)
        {
            AppendRow(builder, row, widths);
        }

        return builder.ToString();
    }

    private static void AppendRow(StringBuilder builder, string[] cells, int[] widths)
    {
        for (var column = 0; column < cells.Length; column++)
        {
            var padding = widths[column] - DisplayWidth(cells[column]);
            if (column == 0)
            {
                builder.Append(cells[column]).Append(' ', Math.Max(0, padding));
            }
            else
            {
                builder.Append(' ', Math.Max(0, padding)).Append(cells[column]);
            }

            if (column < cells.Length - 1)
            {
                builder.Append("  ");
            }
        }

        builder.AppendLine();
    }

    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            width += rune.Value >= 0x2E80 ? 2 : 1;
        }

        return width;
    }
}

internal static class Statistics
{
    public static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(x => x).ToArray();
        if (sorted.Length == 0)
        {
            return 0;
        }

        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
