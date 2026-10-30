namespace AxiosNet.WebAPI;

/// <summary>接口级调用上下文：授权钩子与错误工厂拿到的 <c>request</c> 载荷（哪个接口、哪个方法、实参是什么）。</summary>
public sealed class ApiInterfaceInvocation
{
    internal ApiInterfaceInvocation(Type interfaceType, string methodName, object?[] arguments)
    {
        InterfaceType = interfaceType;
        MethodName = methodName;
        Arguments = Array.AsReadOnly(arguments);
    }

    /// <summary>被调用的接口类型（<c>Create&lt;T&gt;()</c> 的 <c>T</c>，含继承场景下的派生接口）。</summary>
    public Type InterfaceType { get; }

    /// <summary>接口方法名（重载方法请结合 <see cref="Arguments"/> 区分）。</summary>
    public string MethodName { get; }

    /// <summary>调用实参（含 <see cref="CancellationToken"/> 位置上的值），只读视图。</summary>
    public IReadOnlyList<object?> Arguments { get; }

    /// <summary>本次请求的 <c>Authorization</c> 头是否由 <see cref="ApiClient.AccessTokenProvider"/> 写入（401 重放的判定依据）。</summary>
    internal bool TokenManaged { get; set; }
}
