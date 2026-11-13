using System.Reflection;
using System.Runtime.Versioning;

namespace AxiosNet.Compat.Tests;

public sealed class CompatAssetTests
{
    [Fact]
    public void 本工程运行的是_netstandard2_0_兼容资产()
    {
        var attribute = typeof(AxiosClient).Assembly.GetCustomAttribute<TargetFrameworkAttribute>();

        Assert.Equal(".NETStandard,Version=v2.0", attribute?.FrameworkName);
    }
}
