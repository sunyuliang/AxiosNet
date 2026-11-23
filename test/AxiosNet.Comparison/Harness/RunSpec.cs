using AxiosNet.Comparison.Targets;

namespace AxiosNet.Comparison.Harness;

/// <summary>一次运行的口径：并发、测量窗口、轮数、预热与 API 形态；控制台与报告共用同一份。</summary>
/// <param name="Concurrency">每实现的并发 worker 数。</param>
/// <param name="DurationSeconds">每轮测量窗口的秒数。</param>
/// <param name="Rounds">测量轮数（每轮轮转实现顺序）。</param>
/// <param name="WarmupSeconds">每实现的预热秒数。</param>
/// <param name="ApiStyle">本次对比使用的 API 形态。</param>
internal readonly record struct RunSpec(
    int Concurrency,
    double DurationSeconds,
    int Rounds,
    double WarmupSeconds,
    ApiStyle ApiStyle);
