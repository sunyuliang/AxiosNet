using BenchmarkDotNet.Attributes;

// 全项目统一的默认作业：3 预热 + 8 迭代（BDN 的 DefaultJob 是 6 + 15）。
//
// 为什么刻意比 BDN 默认轻：本套基准完整一轮有 40 多行，DefaultJob 下要跑 50 分钟，
// 而「证明这次优化没退化」要求改动前后各跑一轮 —— 一个半小时的前置成本，最后一定是没人跑。
// 压到 8 次迭代后一轮约 10 分钟，前后对比才落得进日常流程。
//
// 精度上不会因此失守：判定的第一依据是 Allocated（确定性量，与迭代次数无关），
// 第二依据是同一次运行内的 Ratio（同进程同窗口的比），第三才是 Mean ± Error。
// 8 次迭代下每行仍采样几十万次调用，Error 半宽通常在一个百分点量级。
// 需要更窄的置信区间时用 BDN 内置作业覆盖：--job long / --job medium。
[assembly: SimpleJob(warmupCount: 3, iterationCount: 8)]
