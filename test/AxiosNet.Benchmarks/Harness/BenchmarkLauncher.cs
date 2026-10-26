using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace AxiosNet.Benchmarks.Harness;

/// <summary>
/// 跑一轮基准的唯一入口：解析作业与执行方式、把其余参数原样交给 BDN、跑完打印摘要。
/// <para>
/// 为什么统一走 <see cref="BenchmarkSwitcher"/> 而不是 <c>BenchmarkRunner.Run(type)</c>：只有前者认
/// <c>--filter</c> / <c>--exporters</c> 这些命令行参数，菜单通道与脚本通道才能共用同一条路径。
/// </para>
/// <para>
/// <b><c>--job</c> 为什么要自己摘出来</b>：BDN 的 <c>--job</c> 是往配置里<b>追加</b>作业，不会替换
/// 已有作业；再叠加程序集 / 类上的作业特性，同一组基准就会被跑两遍、两张口径混进一张表
/// （实测 <c>--job short</c> 让 6 个基准执行了 12 次）。所以这里把 <c>--job</c> 从参数里摘掉、
/// 换成配置里唯一的一个作业：<c>--job X</c> 变成真正的覆盖，一次运行只有一个作业。
/// </para>
/// </summary>
internal static class BenchmarkLauncher
{
    /// <summary>按 BDN 参数跑一轮，并打印结果摘要。</summary>
    /// <param name="args">BDN 参数（<c>--job</c> / <c>--in-process</c> / <c>--isolated</c> 由这里消费）。</param>
    /// <param name="announce">是否先把执行方式打到 stderr（菜单自己会在标题里说明，就不用重复）。</param>
    public static IReadOnlyList<Summary> Run(string[] args, bool announce = true)
    {
        var options = RunOptions.Parse(args);
        var job = BenchmarkConfig.Resolve(options.Job);

        if (job is null)
        {
            Console.WriteLine($"[提醒] 未知作业「{options.Job}」，本次改用默认作业。可用：{BenchmarkConfig.KnownNames}");
            job = BenchmarkConfig.DefaultJob;
        }

        job = ApplyToolchain(job, options.InProcess);

        if (announce)
        {
            Console.Error.WriteLine(DescribeToolchain(options.InProcess));
        }

        var config = ManualConfig.Create(DefaultConfig.Instance).AddJob(job);

        if (options.InProcess)
        {
            // 同进程 = 跑当前这个程序集：校验器要拦的是「BDN 生成的临时项目没开优化」，这里不存在。
            // 宿主是 Debug 时被测库（AxiosNet）确实是未优化 IL —— 那由 Program 的构建提示负责说明，
            // 不在这里拦：拦掉就等于否认了「Debug 就 Debug」这个前提。
            config = (ManualConfig)config.WithOptions(ConfigOptions.DisableOptimizationsValidator);
        }

        var summaries = BenchmarkSwitcher
            .FromAssembly(typeof(BenchmarkLauncher).Assembly)
            .Run(options.BenchmarkArgs, config)
            .ToArray();

        BenchmarkDigest.Print(summaries, options.InProcess);
        return summaries;
    }

    /// <summary>菜单选择 → BDN 参数：一个基准类 = 一条 <c>--filter</c>。</summary>
    public static IReadOnlyList<Summary> RunClass(string className, string? job, bool inProcess)
    {
        var args = new List<string> { "--filter", $"*{className}*" };

        if (job is not null)
        {
            args.Add("--job");
            args.Add(job);
        }

        args.Add(inProcess ? "--in-process" : "--isolated");

        // 菜单标题里已经写了执行方式，不再每组重复一行
        return Run(args.ToArray(), announce: false);
    }

    /// <summary>BDN 把校验错误挂在 Summary 上、退出码恒为 0，所以要显式问一句。</summary>
    public static bool HasFailures(IReadOnlyList<Summary> summaries)
        => summaries.Count == 0 || summaries.Any(summary => summary.HasCriticalValidationErrors);

    /// <summary>执行方式的说明，CLI 打一次、菜单标题用同一句，避免两处口径漂移。</summary>
    public static string DescribeToolchain(bool inProcess) => inProcess
        ? "[执行方式] 同进程：不生成临时项目、不重建、不另起进程；跑的就是当前这个程序集（Release 就是 Release，Debug 就是 Debug）"
        : "[执行方式] 隔离：为作业生成并编译临时项目，每个用例一个独立子进程（数字更可比，但慢）";

    /// <summary>
    /// 同进程走 <see cref="InProcessEmitToolchain"/>：BDN 仍在内存里发出调用桩（InvocationCount / UnrollFactor
    /// 这些特性保留），但不再生成项目、不再 <c>dotnet build</c>、不再起子进程。
    /// </summary>
    private static Job ApplyToolchain(Job job, bool inProcess)
        => inProcess ? job.WithToolchain(InProcessEmitToolchain.Instance) : job;

    /// <summary>解析出来的运行参数。</summary>
    private sealed class RunOptions
    {
        public required string[] BenchmarkArgs { get; init; }

        public string? Job { get; init; }

        public bool InProcess { get; init; }

        public static RunOptions Parse(string[] args)
        {
            var remaining = new List<string>(args.Length);
            string? job = null;

            // 默认同进程：BDN 默认的「生成临时项目 + 每个用例一个子进程」在一次会话里要重建、要等，
            // 而交互菜单本来就是「边改边看」。要写进提交信息的数字用 --isolated。
            var inProcess = true;

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                if (arg is "--job" or "-j")
                {
                    if (i + 1 < args.Length)
                    {
                        job = args[++i];
                    }

                    continue;
                }

                if (arg.StartsWith("--job=", StringComparison.Ordinal))
                {
                    job = arg["--job=".Length..];
                    continue;
                }

                if (arg.StartsWith("-j=", StringComparison.Ordinal))
                {
                    job = arg["-j=".Length..];
                    continue;
                }

                switch (arg.TrimStart('-').ToLowerInvariant())
                {
                    case "inprocess" or "in-process" or "i":
                        inProcess = true;
                        continue;
                    case "isolated" or "outofprocess" or "out-of-process":
                        inProcess = false;
                        continue;
                }

                remaining.Add(arg);
            }

            return new RunOptions { BenchmarkArgs = remaining.ToArray(), Job = job, InProcess = inProcess };
        }
    }
}
