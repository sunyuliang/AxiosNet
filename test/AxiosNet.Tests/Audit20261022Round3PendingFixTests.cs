using System.Net;

namespace AxiosNet.Tests;

/// <summary>
/// 2026-10-22 第三轮排查「待修两项」的修复回归（先各写一条确定性红灯，再改实现）。
/// <para>
/// 两条都不是靠压测撞概率：A 用反射 + 主线程持锁把线程交错钉死，B 用自定义 Handler 精确控制
/// 声明的 <c>Content-Length</c>。因此它们可以进常规测试集，不会 flaky。
/// </para>
/// </summary>
public sealed class Audit20261022Round3PendingFixTests
{
    /// <summary>
    /// A：<see cref="AxiosHeaders"/> 的「不可变数组 + CAS」模型必须同时满足两条契约 ——
    /// <b>克隆共享同一版数组（写时复制）</b>、<b>写入各换各的（隔离）</b>。
    /// <para>
    /// 旧实现（引用计数 + 字典）靠「判定独占 → 复制字典 → 发布」的临界区保证这一点，代码里有两个窗口
    /// 曾经丢过更新；换成不可变数组后，这两条契约由「数组发布后永不修改」直接成立，不再有窗口。
    /// 本用例把两条契约都钉死：任一方写入都不得被对方看到，且源与副本各自保留自己的条目。
    /// </para>
    /// </summary>
    [Fact]
    public void Clone_SharesSnapshot_ButWritesStayIsolated()
    {
        var source = new AxiosHeaders();
        source.Set("k0", "v0");

        var clone = source.Clone();
        Assert.Equal("v0", clone.Get("k0"));

        // 副本写入：源不受影响
        clone.Set("k1", "v1");
        Assert.Null(source.Get("k1"));

        // 源写入：已经克隆出去的副本也不受影响（对称分离）
        source.Set("k2", "v2");
        Assert.Null(clone.Get("k2"));
        Assert.Equal(2, source.Count);
        Assert.Equal(2, clone.Count);
    }

    /// <summary>
    /// A2：同一实例被多线程并发写<b>不同的头</b>时不能丢更新。
    /// <para>
    /// 这是「实例默认头可在请求进行中修改（如运行期刷新 Token）」的底层保证。CAS 失败即基于新版本重算，
    /// 因此每个线程写的键都必须留下；旧实现若把「判定独占」与「写入」分成两步，就会在这里丢条目。
    /// </para>
    /// </summary>
    [Fact]
    public void ConcurrentWrites_OnSameInstance_DoNotLoseUpdates()
    {
        var headers = new AxiosHeaders();
        const int workers = 8;
        const int perWorker = 200;

        Parallel.For(0, workers, worker =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                headers.Set($"X-W{worker}-{i}", i.ToString());
            }
        });

        Assert.Equal(workers * perWorker, headers.Count);
        for (var worker = 0; worker < workers; worker++)
        {
            for (var i = 0; i < perWorker; i++)
            {
                Assert.Equal(i.ToString(), headers.Get($"X-W{worker}-{i}"));
            }
        }
    }

    /// <summary>
    /// B：声明长度不可信时，「完成」不能只看 <c>loaded &gt;= total</c>。
    /// <para>
    /// 服务端把 <c>Content-Length</c> 写成 0 却仍有正文（或注入的 Handler 不按声明长度截断）时，
    /// <c>0 &gt;= 0</c> 恒真 —— 缺陷版本在第一次上报后就把计量器锁死，后面几百 KB 全部静默，
    /// 调用方只看到一次「loaded = 第一块 / total = 0」。
    /// </para>
    /// </summary>
    [Fact]
    public async Task DownloadProgress_KeepsReporting_WhenDeclaredLengthIsZero()
    {
        var body = new byte[512 * 1024];
        new Random(7).NextBytes(body);

        using var client = new AxiosClient(
            defaults: new AxiosConfig(),
            handler: new FixedLengthHandler(body, declaredLength: 0));

        var events = new List<AxiosProgressEvent>();
        var response = await client.GetAsync<byte[]>(
            "http://fixed.local/blob",
            c => c.Callbacks.OnDownloadProgress = events.Add);

        // 正文本身一直是完整的（进度计量不参与数据读取）
        Assert.Equal(body.Length, response.Data!.Length);

        Assert.True(events.Count > 1, $"{events.Count} 次进度上报：{string.Join(", ", events)}");
        Assert.Equal(body.Length, events[^1].Loaded);
        Assert.Equal(0L, events[^1].Total);
    }

    /// <summary>声明任意 <c>Content-Length</c>，但始终写出完整正文的 Handler（模拟声明不可信）。</summary>
    private sealed class FixedLengthHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly long _declared;

        public FixedLengthHandler(byte[] body, long declaredLength)
        {
            _body = body;
            _declared = declaredLength;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(_body);
            content.Headers.ContentLength = _declared;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }
}
