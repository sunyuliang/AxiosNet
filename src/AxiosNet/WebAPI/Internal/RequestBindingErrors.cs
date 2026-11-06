namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 请求装配失败（<see cref="AxiosException.ErrBadRequest"/>）：路径 / 查询 / 请求头 / 正文取值或格式化失败时使用。
/// 这是 WebAPI 侧的语义与错误码，内核只提供中性的「构造期失败」分类。
/// </summary>
internal static class RequestBindingErrors
{
    /// <summary>构造装配失败异常，原始异常进 <c>Cause</c>。</summary>
    public static AxiosException Create(
        AxiosConfig? config,
        HttpRequestMessage? request,
        string targetName,
        Exception inner)
    {
        return new BindingException(
            $"Request could not be built from '{targetName}': {inner.Message}",
            config,
            request,
            inner);
    }

    /// <summary>只为拿到受保护的构造函数：异常类型本身仍是 <see cref="AxiosException"/> 派生类。</summary>
    private sealed class BindingException : AxiosException
    {
        public BindingException(
            string message,
            AxiosConfig? config,
            HttpRequestMessage? request,
            Exception inner)
            : base(message, ErrBadRequest, config, request, response: null, innerException: inner)
        {
        }
    }
}
