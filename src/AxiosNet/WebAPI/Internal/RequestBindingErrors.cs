namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 请求装配失败（<see cref="AxiosException.ErrBadRequest"/>）：路径 / 查询 / 请求头 / 正文取值或格式化失败时使用。
/// 这是 WebAPI 侧的语义与错误码，内核只提供中性的「构造期失败」分类。
/// </summary>
internal static class RequestBindingErrors
{
    /// <summary>构造装配失败异常，原始异常进 <c>Cause</c>。</summary>
    /// <remarks>
    /// <paramref name="inner"/> 是 <see cref="BindingMemberException"/> 时，消息额外带上通道与成员定位文本
    /// （<c>Request could not be built from 'UserRequest' (query parameter 'UserRequest.Page'): ...</c>），
    /// <c>Cause</c> 仍是那个成员上真正抛出的原始异常 —— 不多包一层。
    /// </remarks>
    /// <param name="config">本次请求配置。</param>
    /// <param name="request">请求对象；装配阶段通常还没有。</param>
    /// <param name="targetName">请求模型的类型名（错误消息里的定位主体）。</param>
    /// <param name="inner">触发失败的原始异常。</param>
    /// <returns>统一的 <see cref="AxiosException.ErrBadRequest"/>。</returns>
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

    /// <summary>绑定通道 → 错误消息里的英文标签。</summary>
    /// <param name="channel">失败的通道。</param>
    /// <returns>形如 <c>query parameter</c> 的标签。</returns>
    /// <remarks>
    /// 每个已知成员显式列名，兜底臂按枚举自身名字输出，不要改成固定标签：新增通道时会指错地方。
    /// 也不能去掉兜底臂（会变成 CS8524 永久警告，与本仓 0 条 CS 警告的基线冲突）。
    /// </remarks>
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
