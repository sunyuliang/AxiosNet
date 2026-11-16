using BenchmarkDotNet.Jobs;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 全项目统一的 BDN 作业定义：3 次预热 + 8 次迭代。判定依次看 Allocated、同一次运行内的比值，最后才是 Mean ± Error。
/// </summary>
internal static class BenchmarkConfig
{
    /// <summary>默认作业：3 预热 + 8 迭代。</summary>
    public static Job DefaultJob { get; } = Job.Default
        .WithWarmupCount(3)
        .WithIterationCount(8)
        .WithId("Default3x8");

    /// <summary>
    /// 作业名 → 作业；<c>null</c>（命令行没给 <c>--job</c>）取默认作业。
    /// 未知名字返回 null 由调用方提示，不静默回退 —— 拼错作业名会让整轮数字失去可比性。
    /// </summary>
    public static Job? Resolve(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        null or "" or "default" => DefaultJob,
        "dry" => Job.Dry,
        "short" => Job.ShortRun,
        "medium" => Job.MediumRun,
        "long" => Job.LongRun,
        "verylong" => Job.VeryLongRun,
        _ => null,
    };

    /// <summary>可用的作业名（提示与文档用）。</summary>
    public const string KnownNames = "default / dry / short / medium / long / verylong";
}
