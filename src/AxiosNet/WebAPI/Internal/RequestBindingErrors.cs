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
    /// 每个已知成员都显式列名，兜底臂<b>不返回固定标签</b>：写成 <c>_ => "header"</c> 时，
    /// 将来给 <see cref="BindingChannel"/> 添第四个成员（例如 <c>Body</c>）编译器一声不响，
    /// 错误消息会把新通道标成「header」—— 指错地方比没有信息更坏，而消息文本没有测试盯住就不会有人发现。
    /// 兜底用枚举自身名字，新成员的措辞最多是不够漂亮（<c>Body</c> 而不是「request body」），不会指错。
    /// <para>
    /// 这里不能用「列全三臂、去掉兜底」来换取 CS8509：枚举的 switch 表达式去掉 <c>_</c> 后，
    /// 编译器会改报 CS8524（未处理 <c>(BindingChannel)3</c> 这类未命名取值），是一条<b>永久</b>警告，
    /// 与本仓「0 条 CS 警告」的构建基线冲突。
    /// </para>
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
