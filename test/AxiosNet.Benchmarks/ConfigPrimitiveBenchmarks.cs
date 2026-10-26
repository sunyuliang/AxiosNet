using System.ComponentModel;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;

namespace AxiosNet.Benchmarks;

/// <summary>
/// 配置合并的两个地基原语：<see cref="AxiosConfig.Clone"/> 与 <see cref="AxiosHeaders"/> 的读 / 写 / 克隆。
/// <para>
/// <b>为什么单独一个类</b>：它们与「头数」之类的参数无关，放在 <see cref="ConfigMergeBenchmarks"/>
/// 里会被参数组合重复测多遍，白花时间还让表格难读。这里没有基线 —— 原语看的是绝对 ns 与
/// <c>Allocated</c>，比值没有意义。
/// </para>
/// <para>
/// <b>两条护栏值得盯</b>：
/// <list type="bullet">
/// <item><c>Headers_Clone</c> 应该只有一次 <c>AxiosHeaders</c> 对象分配（24 B）—— 克隆只共享不可变数组；
/// 一旦谁给 Clone 加了「复制条目数组」，这里的 Allocated 会随头数线性上涨；</item>
/// <item><c>Headers_Set_Same</c>（幂等写）应该 <b>0 分配</b> —— 相同取值只做一次扫描就返回。
/// 这条塌了意味着每个请求写默认头都在产垃圾。</item>
/// </list>
/// </para>
/// </summary>
[Description("合并原语：AxiosConfig.Clone / AxiosHeaders 读、幂等写、克隆")]
[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[HideColumns("Job")]
// 不能 sealed：BDN 会派生基准类来拦截调用（0.15 起对 sealed 直接报校验错误）。
public class ConfigPrimitiveBenchmarks
{
    private AxiosConfig _config = null!;
    private AxiosHeaders _headers = null!;
    private bool _toggle;

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
