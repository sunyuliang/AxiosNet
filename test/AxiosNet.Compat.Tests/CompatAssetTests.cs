using System.Reflection;
using System.Runtime.Versioning;
using AxiosNet.Metadata;

namespace AxiosNet.Compat.Tests;

/// <summary>确认本工程链接到的是 netstandard2.0 兼容资产。</summary>
public sealed class CompatAssetTests
{
    [Fact]
    public void 本工程运行的是_netstandard2_0_兼容资产()
    {
        var attribute = typeof(AxiosClient).Assembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.Equal(".NETStandard,Version=v2.0", attribute?.FrameworkName);
    }

    /// <summary>
    /// 缺元数据的提示不能在本资产上建议 net6.0 的动作：本资产虽引用 STJ 8.x（有 <c>TypeInfoResolver</c>），
    /// 但序列化路径只编了反射分支，文案要按本资产自己的处境说话，否则调用方照做也接不进去。
    /// </summary>
    [Fact]
    public void 缺元数据提示不把兼容资产说成_net6_0()
    {
        var message = AxiosAotMetadataException.MissingJsonPropertyInfo(typeof(CompatAssetTests)).Message;

        Assert.Contains("netstandard2.0", message, StringComparison.Ordinal);
        Assert.DoesNotContain("net6.0", message, StringComparison.Ordinal);
    }
}
