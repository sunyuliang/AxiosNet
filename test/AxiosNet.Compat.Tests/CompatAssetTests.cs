using System.Reflection;
using System.Runtime.Versioning;

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
}
