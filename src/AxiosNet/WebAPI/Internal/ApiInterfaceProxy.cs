using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 接口代理：DispatchProxy 拦截全部接口方法，转交给 <see cref="InterfaceMethodMetadata"/>。
/// 仅支持 JIT（Reflection.Emit）；Native AOT 下由 <see cref="ApiClient.Create{T}"/> 提前拒绝。
/// </summary>
internal class ApiInterfaceProxy : DispatchProxy
{
    private static readonly MethodInfo CreateMethod = typeof(DispatchProxy)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(method => method.Name == nameof(DispatchProxy.Create) && method.IsGenericMethodDefinition);

    private const DynamicallyAccessedMemberTypes InterfaceMembers =
        DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.PublicEvents
        | DynamicallyAccessedMemberTypes.Interfaces;

    private ApiClient? _client;

    [DynamicallyAccessedMembers(InterfaceMembers)]
    private Type? _interfaceType;

    /// <summary>DispatchProxy 要求代理类型有可用的无参构造。</summary>
    public ApiInterfaceProxy()
    {
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2087",
        Justification = "公共入口 ApiClient.Create<T>() 已标注 RequiresUnreferencedCode；接口类型由调用方负责 root。")]
    internal static T Create<T>(ApiClient client) where T : class
        => (T)Create(typeof(T), client);

    [UnconditionalSuppressMessage(
        "AotAnalysis",
        "IL3050",
        Justification = "接口级声明在 Create 时已要求 RuntimeFeature.IsDynamicCodeSupported（JIT）；Native AOT 下直接拒绝。")]
    internal static object Create(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        ApiClient client)
    {
        var proxy = CreateMethod.MakeGenericMethod(interfaceType, typeof(ApiInterfaceProxy)).Invoke(null, null)!;
        var self = (ApiInterfaceProxy)proxy;
        self._client = client;
        self._interfaceType = interfaceType;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        => InterfaceMethodMetadata
            .Get(_interfaceType!, InterfaceDeclaration.ResolveMethod(_interfaceType!, targetMethod!))
            .Invoke(_client!, args);
}
