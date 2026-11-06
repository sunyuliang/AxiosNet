namespace AxiosNet.WebAPI;

/// <summary>接口级调用上下文：凭据提供方与错误工厂拿到的 <c>request</c> 载荷（哪个接口、哪个方法、实参是什么）。</summary>
public sealed class ApiInterfaceInvocation
{
    private readonly object?[] _arguments;
    // 实参数组的只读视图（首次访问时才建，稳态调用不分配）。
    private IReadOnlyList<object?>? _argumentsView;

    internal ApiInterfaceInvocation(Type interfaceType, string methodName, object?[] arguments)
    {
        InterfaceType = interfaceType;
        MethodName = methodName;
        _arguments = arguments;
    }

    /// <summary>被调用的接口类型（<c>Create&lt;T&gt;()</c> 的 <c>T</c>，含继承场景下的派生接口）。</summary>
    public Type InterfaceType { get; }

    /// <summary>接口方法名（重载方法请结合 <see cref="Arguments"/> 区分）。</summary>
    public string MethodName { get; }

    /// <summary>
    /// 调用实参（含 <see cref="CancellationToken"/> 位置上的值），只读视图。
    /// </summary>
    /// <remarks>
    /// 每次接口调用都会创建一个本对象，因此只读视图按需创建：不读 <see cref="Arguments"/> 的调用方
    /// （绝大多数请求，凭据提供方不关心实参时）不为这层包装付一次分配。
    /// </remarks>
    public IReadOnlyList<object?> Arguments => Volatile.Read(ref _argumentsView) ?? CreateArgumentsView();

    /// <summary>本次请求由库实际写入的凭据形态（<see cref="ApiCredentialKind.None"/> 表示没写头，401 重放的判定依据）。</summary>
    internal ApiCredentialKind CredentialKind { get; set; }

    /// <summary>参数只读视图按需创建，并发首次访问只发布一份（避免把可变数组直接交出去）。</summary>
    private IReadOnlyList<object?> CreateArgumentsView()
    {
        var view = Array.AsReadOnly(_arguments);
        return Interlocked.CompareExchange(ref _argumentsView, view, null) ?? view;
    }
}
