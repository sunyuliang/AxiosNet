using System.ComponentModel;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 配置合并的两个地基原语：<c>AxiosConfig.Clone</c> 与 <c>AxiosHeaders</c> 的读 / 写 / 克隆。没有基线，只看绝对 ns 与 Allocated：<c>Headers_Clone</c> 应为一次 24 B，<c>Headers_Set_Same</c> 应为 0。
/// </summary>
[Description("合并原语：AxiosConfig.Clone / AxiosHeaders 读、幂等写、克隆")]
[BenchmarkGroup("纯 CPU（FakeHttpHandler，无网络）", "没有网络噪声，比值与 Allocated 都稳", Order = 2)]
[BenchmarkHint("没有基线，看绝对值与 Allocated：Headers_Clone 应恒为 24 B，Headers_Set_Same 应为 0")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class ConfigPrimitiveBenchmarks
{
    // 被测配置：10 个默认头 + 1 个幂等头，克隆成本随这个规模走。
    private AxiosConfig _config = null!;
    // 指向 _config.Headers：读 / 写 / 克隆三条原语都打在它身上。
    private AxiosHeaders _headers = null!;
    // 两次写入之间来回切取值：同值写会命中幂等快路径，就测不到发布新版本了。
    private bool _toggle;

    /// <summary>造一份「有内容」的配置：头集合非空，Clone 才会走到复制分支。</summary>
    [GlobalSetup]
    public void Setup()
    {
        _config = new AxiosConfig
        {
            BaseAddress = "http://bench.local/api/",
            Params = new { page = 1, size = 20 }
        };

        _config.Headers.Set("X-Idempotent", "v");
        for (var i = 0; i < 10; i++)
        {
            _config.Headers.Set($"X-Default-{i}", "value");
        }

        _headers = _config.Headers;
    }

    /// <summary>每请求一次的深拷贝（<c>MemberwiseClone</c> + 有内容的集合按需复制）。</summary>
    [Benchmark]
    public AxiosConfig Config_Clone() => _config.Clone();

    /// <summary>头集合克隆：只共享不可变数组引用，成本应与头数无关。</summary>
    [Benchmark]
    public AxiosHeaders Headers_Clone() => _headers.Clone();

    /// <summary>命中查找（判别位 + 字符串比较）。</summary>
    [Benchmark]
    public string? Headers_Get_Hit() => _headers.Get("X-Default-9");

    /// <summary>未命中查找：走判别位过滤后还要 <c>Trim</c> 重试一次。</summary>
    [Benchmark]
    public bool Headers_Has_Miss() => _headers.Has("X-Absent");

    /// <summary>幂等写：取值与当前值相同，应 0 分配、不发布新版本。</summary>
    [Benchmark]
    public AxiosHeaders Headers_Set_Same() => _headers.Set("X-Idempotent", "v");

    /// <summary>覆盖写（不同取值）：发布一版新条目数组，成本随头数线性增长。</summary>
    [Benchmark]
    public AxiosHeaders Headers_Set_New()
        // 两个取值来回切：写同一个值会命中幂等快速路径，测到的就不是「发布新版本」了
        => _headers.Set("X-Idempotent", (_toggle = !_toggle) ? "changed" : "value");
}
