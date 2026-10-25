using System.Net;
using System.Reflection;

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
    /// A：<see cref="AxiosHeaders"/> 进入独占写临界区时，若「旧存储已被别的线程换掉」必须重来一轮。
    /// <para>
    /// 缺陷版本会直接把换上的新存储交出去 —— 而那个新存储可能刚被 <c>Clone</c> 加过计数，
    /// 于是写入落进了共享字典，克隆出来的快照静默看到了这次写入（隔离性被破坏）。
    /// </para>
    /// <para>
    /// 时序（本用例钉死的交错）：主线程先持住旧存储的锁 → 后台线程进入写入并读到旧存储后阻塞在那里 →
    /// 主线程把 <c>_store</c> 换成新存储并克隆一次（Owners = 2）→ 释放锁 → 后台线程发现旧存储已过期。
    /// 修复前的实现此时直接写新存储（共享），修复后重试并复制出独占存储。
    /// </para>
    /// </summary>
    [Fact]
    public async Task Exclusive_WhenStoreWasReplaced_DoesNotWriteIntoSharedStore()
    {
        var headers = new AxiosHeaders();
        headers.Set("k0", "v0");

        var type = typeof(AxiosHeaders);
        var storeField = type.GetField("_store", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var oldStore = storeField.GetValue(headers)!;
        var storeType = oldStore.GetType();
        var ownersField = storeType.GetField("Owners", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var mapField = storeType.GetField("Map", BindingFlags.NonPublic | BindingFlags.Instance)!;

        // 锁就是存储自己（不再有独立的 Sync 对象）
        var oldSync = oldStore;

        // 让旧存储变成共享（Owners = 2），后台线程的写入才会走「分离」路径
        _ = headers.Clone();
        Assert.Equal(2L, ownersField.GetValue(oldStore));

        var released = false;
        Monitor.Enter(oldSync);
        try
        {
            var writer = Task.Run(() => headers.Set("k1", "v1"));

            // 同步等待，不能在这里 await：Monitor 是线程亲和的，await 后可能换线程退出锁
            Thread.Sleep(150);

            // 模拟「另一个线程先完成了分离」：把 _store 换成新存储
            var replacement = Activator.CreateInstance(storeType, nonPublic: true)!;
            mapField.SetValue(
                replacement,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["k0"] = "v0" });
            storeField.SetValue(headers, replacement);

            // 再克隆一次让新存储成为共享存储（Owners = 2）：snapshot 与 headers 指向同一份字典
            var snapshot = headers.Clone();
            Assert.Equal(2L, ownersField.GetValue(replacement));

            Monitor.Exit(oldSync);
            released = true;

            await writer.WaitAsync(TimeSpan.FromSeconds(5));

            // 关键断言：克隆快照不该看到后台线程对 headers 的写入
            Assert.Null(snapshot.Get("k1"));
        }
        finally
        {
            if (!released)
            {
                Monitor.Exit(oldSync);
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
