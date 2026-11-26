using AxiosNet.Cache;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Caching;

/// <summary>
/// 单飞领导者「已登记在途、还没发出请求」就失败时的收尾：这条键不能被永久占住，跟随者也不能挂在原地。
/// </summary>
/// <remarks>
/// 领导者在这一段里抛异常只可能来自缓存读的总预算用完或调用方取消（存储自身的报错在 <see cref="AxiosCache.TryGetAsync"/> 里
/// 已经按未命中吞掉了）。此处用「存储的旧条目查询永远不完成 + 领导者自己的令牌取消」构造这一时机，
/// 断言两件事：跟随者自己补发而不是等下去，以及同键的下一次请求仍能正常完成。
/// 断言依赖各请求自己的总预算差异，故整类进 <see cref="TimingCollection"/> 独占运行。
/// </remarks>
[Collection(TimingCollection.Name)]
public sealed class CacheSingleFlightFailureTests
{
    [Fact]
    public async Task LeaderCancelledBeforeSending_UnblocksFollower_AndReleasesTheKey()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));

        var store = new StalledRevalidationStore();
        using var client = new AxiosClient(new AxiosConfig
        {
            // 跟随者与后续请求的预算足够宽：它们只可能因为「等不到领导者」而失败。
            Caching = new AxiosCache(new AxiosCacheOptions
            {
                SingleFlight = true,
                ConditionalRequests = true,
                Store = store,
            }),
            Resilience = { TotalTimeout = TimeSpan.FromSeconds(5) },
        });

        using var leaderCts = new CancellationTokenSource();

        // 领导者：卡在旧条目查询里，随后被自己的令牌取消。
        var leader = client.GetAsync(server.Url + "x", config => config.CancellationToken = leaderCts.Token);
        await store.Stalled.Task;

        // 跟随者：同一键，跟领导者等同一份结果。
        var follower = client.GetAsync(server.Url + "x");
        await Task.Delay(100);

        leaderCts.Cancel();

        var canceled = await Assert.ThrowsAsync<AxiosException>(() => leader);
        Assert.Equal(AxiosException.ErrCanceled, canceled.Code);

        // 领导者失败已广播：跟随者自己发一次，拿到正文。
        var response = await follower;
        Assert.Equal(200, response.Status);

        // 在途登记已摘除：同键再来一次不能挂在那个永远不完成的登记上。
        var again = await client.GetAsync(server.Url + "x");
        Assert.Equal(200, again.Status);

        // 三次请求里只有跟随者与最后一次真的发出去过（领导者死在缓存阶段）。
        Assert.Equal(2, server.Received.Count);
    }

    /// <summary>
    /// 存储替身：普通查询永远未命中；旧条目查询第一次卡住不返回，之后直接返回 <c>null</c>。
    /// </summary>
    private sealed class StalledRevalidationStore : IAxiosCacheStore, IAxiosCacheRevalidationStore
    {
        private readonly TaskCompletionSource<AxiosShortCircuit?> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _revalidationCalls;

        /// <summary>领导者已经进到旧条目查询里（此时它已登记在途）。</summary>
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => new((AxiosShortCircuit?)null);

        public ValueTask<AxiosShortCircuit?> GetRevalidationAsync(string key, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _revalidationCalls) > 1)
            {
                return new ValueTask<AxiosShortCircuit?>((AxiosShortCircuit?)null);
            }

            Stalled.TrySetResult();
            return new ValueTask<AxiosShortCircuit?>(StallAsync(cancellationToken));
        }

        // 读取认调用方的令牌：取消时抛 OperationCanceledException（与真实存储一致）。
        private async Task<AxiosShortCircuit?> StallAsync(CancellationToken cancellationToken)
        {
            await _never.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
            => default;

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => new(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken)
            => default;
    }
}
