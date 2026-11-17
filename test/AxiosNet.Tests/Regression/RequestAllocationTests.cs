using System.Net;
using System.Reflection;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;
using Xunit.Abstractions;

namespace AxiosNet.Tests.Regression;

/// <summary>
/// 请求方向的分配护栏：JSON 正文只编码一次（UTF-8 字节出口），以及一个小 GET 的每请求总量上界。
/// </summary>
/// <remarks>
/// 只测最小口径（stub handler 同步完成、空响应体）：分配量与「正文怎么产出」「每请求造了几个对象」直接挂钩，
/// 不掺网络与反序列化噪声。Debug 下 async 状态机以类分配，数字没有参考价值，断言只在优化构建里生效。
/// </remarks>
[Collection(AllocationCollection.Name)]
public sealed class RequestAllocationTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>
    /// 每请求分配上界（`GetAsync&lt;string&gt;`，stub handler，空正文）。门槛只用来抓「又多了几个每请求对象」，
    /// 改完热路径要跟着下调，不要放宽；两档差异来自 BCL 自身（net6 的请求 / 响应对象比 net8 多约 170 B）。
    /// </summary>
#if NET6_0
    private const long GetStringBudgetBytes = 1860;
#else
    private const long GetStringBudgetBytes = 1680;
#endif

    /// <summary>
    /// 带 2 个 params 的每请求分配上界。查询串按「先算准容量、再写进池化缓冲」产出，门槛只用来抓
    /// 「又回到攒中间集合 / 中间字符串」的写法；改完热路径要跟着下调，不要放宽。
    /// </summary>
#if NET6_0
    private const long GetWithParamsBudgetBytes = 2150;
#else
    private const long GetWithParamsBudgetBytes = 1980;
#endif

    /// <summary>
    /// 当前构建是否执行分配断言：Debug 构建的 async 状态机以类分配，每请求数字比优化构建高一个量级，
    /// 只有优化构建的测量值可比。
    /// </summary>
    private static readonly bool AssertsAllocations = DetectOptimizedBuild();

    public RequestAllocationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void JsonBody_IsEncodedOnceAsUtf8()
    {
        // 正文约 10 KB：字符串中转会立刻多出整整一份 UTF-16（≈2× 载荷）+ 一份 UTF-8。
        var body = new JsonRequestDto
        {
            Name = new string('n', 200),
            Items = Enumerable.Range(0, 1000).ToArray(),
            Text = new string('t', 8000)
        };
        var utf8Size = JsonSerializer.SerializeToUtf8Bytes(body).Length;

        using var client = new AxiosClient(handler: new EmptyOkHandler(), disposeHandler: true);

        // 测量窗口内必须同步推进：await 会引入测试自己的分配。
#pragma warning disable xUnit1031
        void Post() => _ = client.PostAsync<string>("http://units.test/x", body).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        var allocated = MinimumBytes(50, Post);
        _output.WriteLine($"JSON 正文 {utf8Size:N0} B，每请求分配 {allocated:N0} B（{allocated / (double)utf8Size:0.00}× 载荷）");

        if (!AssertsAllocations)
        {
            GC.KeepAlive(allocated);
            return;
        }

        // 1.25× 是通过字节出口时的实测量级（正文 1× + 固定开销）；3.2× 是退回「先串后字节」的实测量级。
        // 门槛 2× 卡在两者之间，给运行时差异与固定开销留足余量。
        Assert.True(
            allocated < utf8Size * 2,
            $"JSON 上送分配了 {allocated:N0} B（{allocated / (double)utf8Size:0.00}× 载荷，正文 {utf8Size:N0} B）"
            + "——超出 2× 护栏：正文又被序列化成字符串再转字节，`SerializeToBytes` 的字节出口没生效。");
    }

    [Fact]
    public void GetString_StaysWithinPerRequestBudget()
    {
        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = "http://units.test/" },
            handler: new EmptyOkHandler(),
            disposeHandler: true);

#pragma warning disable xUnit1031
        void Get() => _ = client.GetAsync<string>("x").GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        var allocated = MinimumBytes(100, Get);
        _output.WriteLine($"GetAsync<string> 每请求分配 {allocated:N0} B");

        if (!AssertsAllocations)
        {
            GC.KeepAlive(allocated);
            return;
        }

        Assert.True(
            allocated < GetStringBudgetBytes,
            $"单个 GET 分配了 {allocated:N0} B，超出 {GetStringBudgetBytes:N0} B 上界"
            + "——热路径上新增了每请求对象；若是有意优化，请连这条上界一起下调。");
    }

    /// <summary>程序集上的 <see cref="System.Diagnostics.DebuggableAttribute"/> 带「禁用 JIT 优化」标记即为 Debug 构建。</summary>
    private static bool DetectOptimizedBuild() =>
        typeof(RequestAllocationTests).Assembly.GetCustomAttribute<System.Diagnostics.DebuggableAttribute>()
            is not { IsJITOptimizerDisabled: true };

    [Fact]
    public void GetWithParams_StaysWithinPerRequestBudget()
    {
        using var client = new AxiosClient(
            new AxiosConfig { BaseAddress = "http://units.test/" },
            handler: new EmptyOkHandler(),
            disposeHandler: true);

        // 两个参数：一个数字、一个需要转义的值——查询串要算准容量再写一遍。
        var query = new { page = 3, filter = "a b" };

#pragma warning disable xUnit1031
        void Get() => _ = client.GetAsync<string>("x", c => c.Params = query).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        var allocated = MinimumBytes(100, Get);
        _output.WriteLine($"GetAsync<string>（2 个 params）每请求分配 {allocated:N0} B");

        if (!AssertsAllocations)
        {
            GC.KeepAlive(allocated);
            return;
        }

        Assert.True(
            allocated < GetWithParamsBudgetBytes,
            $"带 2 个 params 的 GET 分配了 {allocated:N0} B，超出 {GetWithParamsBudgetBytes:N0} B 上界"
            + "——参数序列化又在攒中间集合 / 中间字符串；若是有意优化，请连这条上界一起下调。");
    }

    /// <summary>取多个测量窗口里的最小值：只看最干净的一轮，压掉并发与 GC 抖动。</summary>
    private static long MinimumBytes(int iterations, Action action, int windows = 3)
    {
        action();
        var best = long.MaxValue;
        for (var window = 0; window < windows; window++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < iterations; i++)
            {
                action();
            }

            best = Math.Min(best, (GC.GetAllocatedBytesForCurrentThread() - before) / iterations);
        }

        return best;
    }

    /// <summary>JSON 上送的载荷形状：长字符串 + 数字数组 + 长文本，量级落在 10 KB 附近。</summary>
    public sealed class JsonRequestDto
    {
        public string Name { get; set; } = string.Empty;

        public int[] Items { get; set; } = [];

        public string Text { get; set; } = string.Empty;
    }

    private sealed class EmptyOkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>())
            });
    }
}
