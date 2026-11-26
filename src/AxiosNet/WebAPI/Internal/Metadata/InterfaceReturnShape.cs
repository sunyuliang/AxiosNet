namespace AxiosNet.WebAPI.Internal.Metadata;

/// <summary>接口方法的返回形态：决定调用产物与异常封装。</summary>
/// <remarks>
/// <c>0</c> 是 <see cref="Unsupported"/>（非法签名的兜底）。生成器侧的 <c>InterfaceReturnKind</c> 以 <c>Response</c> 为 <c>0</c>，
/// 两侧的默认值含义相反，不能按序号互转，只能显式映射。
/// </remarks>
internal enum InterfaceReturnShape
{
    /// <summary>不支持（同步返回 / <c>ValueTask</c> / 其它泛型）。</summary>
    Unsupported = 0,

    /// <summary><c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c>（<c>Task&lt;AxiosResponse&gt;</c> 即正文类型为 <c>null</c>）。</summary>
    Response,

    /// <summary><c>Task&lt;T&gt;</c>：只交付正文。</summary>
    Data,

    /// <summary><c>Task</c>：只完成请求，正文丢弃。</summary>
    Void
}
