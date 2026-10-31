using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Reflection;

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
        Justification = "接口类型来自 ApiClient.Create 的泛型参数，已由 DynamicallyAccessedMembers 标注接口成员；具体类型由调用方负责 root。")]
    internal static T Create<T>(ApiClient client) where T : class
        => (T)Create(typeof(T), client);

    // category 必须是 "AOT"（IL3050 的实际类别）；写成别的名字时 Roslyn 放行、ILC 阶段仍会重新报，
    // 包消费方（NuGet 引用）发布时就会看到这条不属于它代码的警告。
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "DispatchProxy 依赖 Reflection.Emit：只有 ApiClient.Create 在动态代码可用（JIT）时才会调到这里，Native AOT 下不可达。")]
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
        => InterfaceMethodMetadataJit
            .Get(_interfaceType!, InterfaceDeclaration.ResolveMethod(_interfaceType!, targetMethod!))
            .Invoke(_client!, args);
}
