namespace AxiosNet.WebAPI.Internal;

/// <summary>接口方法的返回形态：决定调用产物与异常封装。</summary>
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
