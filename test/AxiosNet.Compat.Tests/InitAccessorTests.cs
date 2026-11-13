using AxiosNet.Cache;
using AxiosNet.Metadata;
using AxiosNet.WebAPI;

namespace AxiosNet.Compat.Tests;

/// <summary>
/// 回归护栏：兼容资产的公开配置属性必须能用对象初始化器写入。
/// 跨程序集的 <c>init</c> 在 .NET Framework 消费方不可写（编译器只在 corlib 里解析 <c>IsExternalInit</c>，
/// 库内定义无论 internal 还是 public 都救不了），因此这几个属性在 netstandard2.0 上是普通 <c>set</c>。
/// 一旦有人改回 <c>init</c>，net472 宿主编译会直接 CS0200，这条用例连带整个工程一起失败。
/// </summary>
public sealed class InitAccessorTests
{
    [Fact]
    public void 公开配置属性可用对象初始化器写入()
    {
        var store = new MemoryCacheStore();
        var cacheOptions = new AxiosCacheOptions { Store = store, MaxEntries = 16 };
        Assert.Same(store, cacheOptions.Store);
        Assert.Equal(16, cacheOptions.MaxEntries);

        var form = new FormData { SourceName = "表单" };
        Assert.Equal("表单", form.SourceName);

        static ValueTask<AccessToken> Acquire(CancellationToken _) => new(new AccessToken("t"));
        Func<CancellationToken, ValueTask<AccessToken>> refresh = Acquire;
        var tokenOptions = new AccessTokenOptions(Acquire) { RefreshAsync = refresh };
        Assert.Same(refresh, tokenOptions.RefreshAsync);
    }
}
