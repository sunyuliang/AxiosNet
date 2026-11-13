using System.Reflection;
using System.Runtime.Versioning;
using AxiosNet.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.Compat.Tests;

/// <summary>
/// AxiosNet.DependencyInjection 的 netstandard2.0 资产：不只要编译通过，还要在真实容器里
/// 完成注册 / 解析，否则兼容资产对 .NET Framework 用户仍然是「装上但用不了」。
/// </summary>
public sealed class DependencyInjectionCompatTests
{
    [Fact]
    public void DI_兼容资产的目标框架是_netstandard2_0()
    {
        var framework = typeof(AxiosClientServiceCollectionExtensions).Assembly
            .GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName;

        Assert.Equal(".NETStandard,Version=v2.0", framework);
    }

    [Fact]
    public void 兼容资产上的_AddAxiosClient_注册为单例()
    {
        var services = new ServiceCollection();
        services.AddAxiosClient();

        // TryAdd 语义：重复注册不应产生第二个描述符。
        services.AddAxiosClient();
        Assert.Single(services, d => d.ServiceType == typeof(AxiosClient));

        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<AxiosClient>();
        Assert.Same(first, provider.GetRequiredService<AxiosClient>());
    }
}
