using System.Collections.Concurrent;
using System.Text;
using AxiosNet.Internal;
using AxiosNet.Pooling;

namespace AxiosNet.Compat.Tests;

/// <summary>
/// 兼容资产上的契约细节回归：
/// (1) 参数为空时抛出的必须是标准 <see cref="ArgumentNullException"/>，不能是兼容层的派生类
///     （<c>ArgumentNullExceptionCompat</c> 只用来补 <c>ThrowIfNull</c> 静态方法）；
/// (2) 配置容器克隆时并发字典回退到 Ordinal 比较器——该属性在 ns2.0 契约与 .NET Framework 运行时里都不存在。
/// </summary>
public sealed class CompatContractTests
{
    [Fact]
    public void 参数为空时抛出的异常类型是标准_ArgumentNullException()
    {
        // 走 ThrowIfNull 路径（兼容层提供的静态方法）
        var fromThrowIfNull = Assert.Throws<ArgumentNullException>(
            () => new ConnectionPoolOptions().AddTransport("p", null!));
        Assert.Equal(typeof(ArgumentNullException), fromThrowIfNull.GetType());
        Assert.Equal("transport", fromThrowIfNull.ParamName);

        // 走 throw new 路径（兼容资产上显式写成 System.ArgumentNullException）
        var fromThrowNew = Assert.Throws<ArgumentNullException>(
            () => HttpConnectionPool.GetOrCreate(null!));
        Assert.Equal(typeof(ArgumentNullException), fromThrowNew.GetType());
        Assert.Equal("key", fromThrowNew.ParamName);
    }

    [Fact]
    public void 并发字典克隆回退到_Ordinal_比较器()
    {
        var source = new ConcurrentDictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["Key"] = 1 };
        var config = new AxiosConfig { Params = source };

        var cloned = Assert.IsType<ConcurrentDictionary<string, object?>>(config.Clone().Params);

        // Ordinal 语义：大小写不同的键查不到（net6.0 / net8.0 资产会保留 OrdinalIgnoreCase）
        Assert.True(cloned.ContainsKey("Key"));
        Assert.False(cloned.ContainsKey("key"));
    }

    [Fact]
    public void 非_Unicode_字符集的解析结果随宿主运行时而定()
    {
        // 三份资产共用同一份 EncodingResolver：认不出的 charset 返回 null，调用点回退 BOM / UTF-8。
        // 差别不在资产而在运行时——.NET Framework 的 BCL 自带全部代码页，.NET Core 要先注册
        // CodePagesEncodingProvider（本库零依赖、不注册）。这条用例把差异钉住，别被当成 bug 去「修」。
        var gbk = ResponseTextDecoder.ResolveFromCharset("gbk");

#if NETFRAMEWORK
        Assert.NotNull(gbk);
        Assert.Equal(936, gbk!.CodePage);
#else
        Assert.Null(gbk);
#endif

        // 两个宿主都必须认得的内置集合。
        Assert.Equal(Encoding.UTF8.CodePage, ResponseTextDecoder.ResolveFromCharset("utf-8")!.CodePage);
        Assert.Equal(Encoding.ASCII.CodePage, ResponseTextDecoder.ResolveFromCharset("us-ascii")!.CodePage);
        Assert.Equal(28591, ResponseTextDecoder.ResolveFromCharset("iso-8859-1")!.CodePage);
    }
}
