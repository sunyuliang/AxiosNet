using System.Runtime.ExceptionServices;
using AxiosNet.WebAPI.DependencyInjection;

namespace AxiosNet.DependencyInjection.Tests;

/// <summary>
/// 属主释放循环的失败容错回归（2026-10-24 审查修复）：
/// 单个 holder 释放失败不能跳过其余站点 —— 第一个抛异常就中断会让后面的连接池永久泄漏。
/// <para>
/// 该行为无法从公开面触发（holder 是内部类型，且连接池 / Handler 的释放路径都刻意吞掉关闭期异常），
/// 经测试 seam 直接用假 <see cref="IDisposable"/> 驱动 <see cref="HolderDisposal"/>；
/// 解析器侧的「全部站点都释放、释放后 Get 抛 ObjectDisposedException」由
/// <see cref="ApiClientResolverConcurrencyTests"/> 锁住。
/// </para>
/// </summary>
public sealed class HolderDisposalTests
{
    [Fact]
    public void DisposeAll_FirstHolderFails_StillDisposesTheRest_AndRethrowsFirstFailure()
    {
        var boom = new InvalidOperationException("boom");
        var disposed = new List<string>();

        var ex = Assert.Throws<InvalidOperationException>(() => HolderDisposal.DisposeAll(
            new IDisposable[]
            {
                new ThrowingDisposable(boom),
                new CountingDisposable("second", disposed),
                new CountingDisposable("third", disposed)
            }));

        // 首个失败原样抛出让调用方看见（ExceptionDispatchInfo 保留原始堆栈）
        Assert.Same(boom, ex);
        Assert.Contains("ThrowingDisposable", ex.StackTrace);

        // 第一个失败没有中断循环：其余 holder 全部释放
        Assert.Equal(new[] { "second", "third" }, disposed);
    }

    [Fact]
    public void DisposeAll_MiddleHolderFails_ContinuesToTheLast()
    {
        var boom = new InvalidOperationException("boom");
        var disposed = new List<string>();

        Assert.Throws<InvalidOperationException>(() => HolderDisposal.DisposeAll(
            new IDisposable[]
            {
                new CountingDisposable("first", disposed),
                new ThrowingDisposable(boom),
                new CountingDisposable("third", disposed)
            }));

        Assert.Equal(new[] { "first", "third" }, disposed);
    }

    [Fact]
    public void DisposeAll_AllSucceed_DoesNotThrow()
    {
        var disposed = new List<string>();

        HolderDisposal.DisposeAll(
            new IDisposable[]
            {
                new CountingDisposable("a", disposed),
                new CountingDisposable("b", disposed)
            });

        Assert.Equal(new[] { "a", "b" }, disposed);
    }

    [Fact]
    public async Task DisposeAllAsync_FirstHolderFails_StillDisposesTheRest_AndRethrowsFirstFailure()
    {
        var boom = new InvalidOperationException("boom");
        var disposed = new List<string>();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => HolderDisposal.DisposeAllAsync(
            new IAsyncDisposable[]
            {
                new ThrowingDisposable(boom),
                new CountingDisposable("second", disposed),
                new CountingDisposable("third", disposed)
            }).AsTask());

        Assert.Same(boom, ex);
        Assert.Contains("ThrowingDisposable", ex.StackTrace);
        Assert.Equal(new[] { "second", "third" }, disposed);
    }

    private sealed class CountingDisposable : IDisposable, IAsyncDisposable
    {
        private readonly List<string> _disposed;

        public CountingDisposable(string name, List<string> disposed)
        {
            Name = name;
            _disposed = disposed;
        }

        private string Name { get; }

        public void Dispose() => _disposed.Add(Name);

        public ValueTask DisposeAsync()
        {
            _disposed.Add(Name);
            return default;
        }
    }

    private sealed class ThrowingDisposable : IDisposable, IAsyncDisposable
    {
        private readonly Exception _boom;

        public ThrowingDisposable(Exception boom) => _boom = boom;

        // 释放失败：不记入「已释放」名单（名单只收成功释放的 holder ——
        // 失败容错要证明的是「其余站点不受它牵连」）。
        public void Dispose() => ExceptionDispatchInfo.Capture(_boom).Throw();

        public ValueTask DisposeAsync()
        {
            ExceptionDispatchInfo.Capture(_boom).Throw();
            return default;
        }
    }
}
