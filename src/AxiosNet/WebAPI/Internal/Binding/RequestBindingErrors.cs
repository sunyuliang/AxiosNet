namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>
/// 请求装配失败（<see cref="AxiosException.ErrBadRequest"/>）：路径 / 查询 / 请求头 / 正文取值或格式化失败时使用。
/// </summary>
internal static class RequestBindingErrors
{
    /// <summary>构造装配失败异常，原始异常进 <c>Cause</c>。</summary>
    /// <remarks>
    /// <paramref name="inner"/> 是 <see cref="BindingMemberException"/> 时，消息额外带上通道与成员定位文本；<c>Cause</c> 仍是成员上真正抛出的原始异常。
    /// </remarks>
    public static AxiosException Create(
        AxiosConfig? config,
        HttpRequestMessage? request,
        string targetName,
        Exception inner)
    {
        if (inner is BindingMemberException member)
        {
            return new BindingException(
                $"Request could not be built from '{targetName}' ({ChannelLabel(member.Channel)} '{member.Member}'): {member.Message}",
                config,
                request,
                member.InnerException ?? member);
        }

        return new BindingException(
            $"Request could not be built from '{targetName}': {inner.Message}",
            config,
            request,
            inner);
    }

    /// <summary>绑定通道 → 错误消息里的英文标签；未知通道按枚举名输出。</summary>
    private static string ChannelLabel(BindingChannel channel) => channel switch
    {
        BindingChannel.Path => "path parameter",
        BindingChannel.Query => "query parameter",
        BindingChannel.Header => "header",
        _ => channel.ToString()
    };

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
