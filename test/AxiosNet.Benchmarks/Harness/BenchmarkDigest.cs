using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Mathematics;
using BenchmarkDotNet.Reports;
using Perfolizer.Mathematics.Common;

namespace AxiosNet.Benchmarks.Harness;

/// <summary>
/// 结果摘要：把 BDN 的 <see cref="Summary"/> 重新排成一张能直接读的表。
/// <para>
/// <b>为什么不直接看 BDN 自己那张表</b>：它是给「全参数全列」用的，行序是 FastestToSlowest ——
/// 多参数组里不同参数组合会交错，而每一行的比值只对<b>同参数组合</b>的基线成立，交错之后
/// 「这一行是拿谁比出来的」就看不出来了。这里做三件事：
/// </para>
/// <list type="number">
/// <item>按参数组合分组、组内按 Mean 升序，基线永远挨着它的变体；</item>
/// <item><b>比值自己重算</b>（同参数组合 + <c>[Benchmark(Baseline = true)]</c>），不抄 BDN 的 Ratio 列 ——
/// 实测 BDN 会把「不是声明基线的那一行」当成时间基线，同一份报告里时间比值与分配比值的基线可以不是同一个；</item>
/// <item>时间列统一单位（整张表一个单位才好横向比），分配量按量级选 B/KB/MB。</item>
/// </list>
/// <para>
/// 这里只<b>呈现</b>，不做判定：没有阈值、没有「通过 / 不通过」，与整个基准项目的定位一致。
/// </para>
/// </summary>
internal static class BenchmarkDigest
{
    /// <summary>打印一轮运行里所有有报告的组；<c>--list</c> 这类不产生报告的命令什么也不打印。</summary>
    public static void Print(IReadOnlyList<Summary> summaries, bool inProcess)
    {
        var printed = 0;

        foreach (var summary in summaries)
        {
            var reports = summary.Reports.ToArray();
            if (reports.Length == 0)
            {
                continue;
            }

            PrintSummary(summary, reports);
            printed++;
        }

        if (printed > 0)
        {
            PrintFooter(inProcess);
        }
    }

    private static void PrintSummary(Summary summary, BenchmarkReport[] reports)
    {
        var type = reports[0].BenchmarkCase.Descriptor.Type;
        var title = type.GetCustomAttribute<DescriptionAttribute>()?.Description;

        Console.WriteLine();
        Console.WriteLine(new string('─', 96));
        Console.WriteLine(string.IsNullOrEmpty(title) ? $"结果摘要：{type.Name}" : $"结果摘要：{type.Name} — {title}");

        if (summary.HasCriticalValidationErrors)
        {
            Console.WriteLine("[!] 本组有校验错误（HasCriticalValidationErrors），下面的数字不可用于任何结论。");
        }

        var rows = BuildRows(reports);
        var unit = ChooseTimeUnit(rows);
        var baselines = rows.Where(row => row.IsBaseline).Select(row => row.Method).Distinct(StringComparer.Ordinal).ToArray();

        Console.WriteLine(baselines.Length > 0
            ? $"基线：{string.Join(" / ", baselines)}（同参数组合内，比值 1.00）"
            : "基线：本组没有 [Benchmark(Baseline = true)]，比值列留空 —— 看绝对值与 Allocated。");
        Console.WriteLine();

        PrintTable(rows, unit);
        Console.WriteLine();
    }

    // ------------------------------------------------------------------
    // 取数
    // ------------------------------------------------------------------

    private sealed class Row
    {
        public required string Method { get; init; }

        /// <summary>参数组合的可读形式；无参数的基准为空串（那一列会被整列省掉）。</summary>
        public required string Parameters { get; init; }

        public double? MeanNs { get; init; }

        public double? ErrorNs { get; init; }

        public long? Allocated { get; init; }

        public bool IsBaseline { get; init; }

        public double? Ratio { get; set; }
    }

    private static List<Row> BuildRows(BenchmarkReport[] reports)
    {
        var rows = new List<Row>(reports.Length);

        foreach (var report in reports)
        {
            var method = report.BenchmarkCase.Descriptor.WorkloadMethod;
            var statistics = report.ResultStatistics;

            // PrintInfo 形如 "InstanceHeaders=0&PerCallHeaders=0"：'&' 换成分隔符比 & 好读
            var parameters = report.BenchmarkCase.Parameters.PrintInfo;

            rows.Add(new Row
            {
                Method = method.Name,
                Parameters = parameters.Length == 0 ? string.Empty : parameters.Replace("&", ", "),
                MeanNs = statistics?.Mean,
                // 与 BDN 的 Error 列同口径：99.9% 置信区间半宽。必须走 GetConfidenceInterval ——
                // ConfidenceInterval 的构造函数第三个参数不是样本数，手拼会得到一个与报告完全不同的数。
                ErrorNs = statistics is null ? null : ErrorOf(statistics.GetConfidenceInterval(ConfidenceLevel.L999).Margin),
                Allocated = report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase),
                IsBaseline = method.GetCustomAttribute<BenchmarkAttribute>()?.Baseline == true,
            });
        }

        // 参数组合按首次出现定序：BDN 的行序是全局 FastestToSlowest，直接照抄会让各组合交错
        var order = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!order.ContainsKey(row.Parameters))
            {
                order[row.Parameters] = order.Count;
            }
        }

        // 比值只在同一参数组合内成立：跨组合的 Mean 差的是「头数 / 帧数」而不是「路径贵不贵」
        foreach (var group in rows.GroupBy(row => row.Parameters, StringComparer.Ordinal))
        {
            if (group.FirstOrDefault(row => row.IsBaseline && row.MeanNs.HasValue)?.MeanNs is not { } baselineMean
                || baselineMean <= 0)
            {
                continue;
            }

            foreach (var row in group)
            {
                if (row.MeanNs is { } mean)
                {
                    row.Ratio = mean / baselineMean;
                }
            }
        }

        return rows
            .OrderBy(row => order[row.Parameters])
            // 组内基线永远排第一：这张表是「相对基线贵多少」，先看到参照行才读得下去
            .ThenBy(row => row.IsBaseline ? 0 : 1)
            .ThenBy(row => row.MeanNs ?? double.MaxValue)
            .ToList();
    }

    /// <summary>
    /// 误差半宽 = 99.9% 置信区间半宽，与 BDN 的 <c>Error</c> 列同口径。
    /// 样本只有 1 次时区间未定义（NaN / ∞），返回 null 而不是把这些值印出来。
    /// </summary>
    private static double? ErrorOf(double margin)
        => double.IsNaN(margin) || double.IsInfinity(margin) ? null : margin;

    // ------------------------------------------------------------------
    // 渲染
    // ------------------------------------------------------------------
    private static void PrintTable(List<Row> rows, string unit)
    {
        var showParameters = rows.Exists(row => row.Parameters.Length > 0);

        var headers = new List<string> { "方法" };
        var alignRight = new List<bool> { false };

        if (showParameters)
        {
            headers.Add("参数");
            alignRight.Add(false);
        }

        headers.Add($"Mean ({unit})");
        alignRight.Add(true);
        headers.Add($"Error ({unit})");
        alignRight.Add(true);
        headers.Add("Allocated");
        alignRight.Add(true);
        headers.Add("比值");
        alignRight.Add(true);

        var cells = new List<string[]>(rows.Count);
        foreach (var row in rows)
        {
            var line = new List<string> { row.IsBaseline ? row.Method + " *" : row.Method };
            if (showParameters)
            {
                line.Add(row.Parameters.Length == 0 ? "—" : row.Parameters);
            }

            line.Add(FormatTime(row.MeanNs, unit));
            line.Add(FormatTime(row.ErrorNs, unit));
            line.Add(FormatAllocated(row.Allocated));
            line.Add(row.Ratio?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—");

            cells.Add(line.ToArray());
        }

        // 列宽按显示宽度算（中文表头占两列），否则表头会跟数据错位
        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            widths[i] = DisplayWidth(headers[i]);
            foreach (var cell in cells)
            {
                widths[i] = Math.Max(widths[i], DisplayWidth(cell[i]));
            }
        }

        Console.WriteLine(FormatLine(headers.ToArray(), widths, alignRight));
        Console.WriteLine(new string('─', widths.Sum() + 2 * (widths.Length - 1)));
        foreach (var cell in cells)
        {
            Console.WriteLine(FormatLine(cell, widths, alignRight));
        }
    }

    private static string FormatLine(string[] cells, int[] widths, List<bool> alignRight)
    {
        var parts = new string[cells.Length];
        for (var i = 0; i < cells.Length; i++)
        {
            var padding = widths[i] - DisplayWidth(cells[i]);
            var left = alignRight[i] ? new string(' ', padding) : string.Empty;
            var right = alignRight[i] ? string.Empty : new string(' ', padding);
            parts[i] = left + cells[i] + right;
        }

        return string.Join("  ", parts);
    }

    /// <summary>
    /// 整张表共用一个时间单位：一行 ns 一行 µs 的表格没法横向比大小。
    /// 阈值按「最小档位也要有 3 位有效数字」定，不用严格的数量级边界。
    /// </summary>
    private static string ChooseTimeUnit(List<Row> rows)
    {
        var max = 0d;
        foreach (var row in rows)
        {
            if (row.MeanNs is { } mean && mean > max)
            {
                max = mean;
            }
        }

        return max >= 10_000_000 ? "ms" : max >= 10_000 ? "µs" : "ns";
    }

    private static string FormatTime(double? nanoseconds, string unit)
    {
        if (nanoseconds is not { } value)
        {
            return "—";
        }

        return unit switch
        {
            "ns" => value.ToString("N1", CultureInfo.InvariantCulture),
            "µs" => (value / 1_000d).ToString("N2", CultureInfo.InvariantCulture),
            _ => (value / 1_000_000d).ToString("N3", CultureInfo.InvariantCulture),
        };
    }

    private static string FormatAllocated(long? bytes)
    {
        if (bytes is not { } value)
        {
            return "—";
        }

        if (value == 0)
        {
            return "0 B";
        }

        if (value < 1024)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture) + " B";
        }

        return value < 1024 * 1024
            ? (value / 1024d).ToString("N2", CultureInfo.InvariantCulture) + " KB"
            : (value / 1024d / 1024d).ToString("N2", CultureInfo.InvariantCulture) + " MB";
    }

    private static void PrintFooter(bool inProcess)
    {
        Console.WriteLine("说明：Allocated 是确定性量（跨运行可比）；Mean 只在本机本次运行内可比，差值小于 Error 视为噪声。");
        Console.WriteLine("      比值由本工具按「同参数组合 + [Benchmark(Baseline = true)]」重算，不抄 BDN 的 Ratio 列");
        Console.WriteLine("      （多参数组里 BDN 可能把非基线行当成时间基线）。带 * 的行是同参数组合内的基线。");
        Console.WriteLine(inProcess
            ? "      执行方式：同进程（InProcessEmit）—— 数字受进程内 JIT / GC / 线程池状态影响，"
              + "跨组横向比、或要写进提交信息时请加 --isolated 重跑。"
            : "      执行方式：隔离（每个用例一个独立进程）—— 与历史基线可比的口径。");
        Console.WriteLine($"      完整报告：{Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts", "results")}");
    }

    // ------------------------------------------------------------------
    // 显示宽度
    // ------------------------------------------------------------------

    /// <summary>控制台显示宽度：CJK / 全角按 2 列算，其余 1 列。</summary>
    private static int DisplayWidth(string text)
    {
        var width = 0;
        foreach (var c in text)
        {
            width += IsWide(c) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(char c) =>
        c is >= '\u1100' and <= '\u115F'    // 谚文字母
            or >= '\u2E80' and <= '\uA4CF'  // CJK 部首 / 汉字 / 假名 / 注音
            or >= '\uAC00' and <= '\uD7A3'  // 谚文音节
            or >= '\uF900' and <= '\uFAFF'  // CJK 兼容汉字
            or >= '\uFE30' and <= '\uFE6F'  // CJK 兼容形式
            or >= '\uFF00' and <= '\uFF60'  // 全角 ASCII / 全角标点
            or >= '\uFFE0' and <= '\uFFE6'; // 全角符号
}
