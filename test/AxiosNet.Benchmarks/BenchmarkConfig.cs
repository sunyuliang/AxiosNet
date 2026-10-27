using BenchmarkDotNet.Jobs;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 全项目统一的作业定义（3 预热 + 8 迭代，BDN 的 <c>DefaultJob</c> 是 6 + 15）。
/// <para>
/// 为什么刻意比 BDN 默认轻：本套基准完整一轮有 40 多行，DefaultJob 下要跑 50 分钟，
/// 而「证明这次优化没退化」要求改动前后各跑一轮 —— 一个半小时的前置成本，最后一定是没人跑。
/// 压到 8 次迭代后一轮约 10 分钟，前后对比才落得进日常流程。
/// </para>
/// <para>
/// 精度上不会因此失守：判定的第一依据是 <c>Allocated</c>（确定性量，与迭代次数无关），
/// 第二依据是同一次运行内的比值（同进程同窗口的比），第三才是 <c>Mean ± Error</c>。
/// 8 次迭代下每行仍采样几十万次调用，<c>Error</c> 半宽通常在一个百分点量级。
/// </para>
/// <para>作业由 <see cref="Harness.BenchmarkLauncher"/> 独占：BDN 的 <c>--job</c> 是追加而非覆盖，</para>
/// <para>放进程序集特性会让同一组基准跑两遍、两套迭代次数混进一张表。</para>
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
