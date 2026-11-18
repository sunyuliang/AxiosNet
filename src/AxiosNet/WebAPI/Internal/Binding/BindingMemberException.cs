namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>
/// 绑定<b>某一个成员</b>时取值 / 格式化失败：携带通道与成员的定位文本，
/// 让 <see cref="RequestBindingErrors"/> 能报出「哪一步、哪个字段」而不是只丢一句底层消息。
/// </summary>
/// <remarks>
/// 只在包内流转：绑定循环捕获任意异常后包成这个类型再抛，外层统一翻译成
/// <see cref="AxiosException.ErrBadRequest"/>。原始异常<b>原样</b>进 <see cref="Exception.InnerException"/>，
/// 最终成为错误模型的 <c>Cause</c>，不丢、不多包一层。
/// </remarks>
internal sealed class BindingMemberException : Exception
{
    /// <summary>构造一条成员级绑定失败。</summary>
    /// <param name="channel">失败所在的绑定通道。</param>
    /// <param name="member">成员的定位文本（如 <c>UserRequest.Page</c>）。</param>
    /// <param name="inner">原始异常。</param>
    public BindingMemberException(BindingChannel channel, string member, Exception inner)
        : base(inner.Message, inner)
    {
        Channel = channel;
        Member = member;
    }

    /// <summary>失败所在的绑定通道（路径 / 查询 / 请求头 / 正文）。</summary>
    public BindingChannel Channel { get; }

    /// <summary>成员的定位文本：模型名 + 属性名，接口模型为方法名 + 参数名。</summary>
    public string Member { get; }
}

/// <summary>绑定通道：错误消息里用来说明「哪一步」失败。</summary>
internal enum BindingChannel
{
    /// <summary>路径占位符替换。</summary>
    Path,

    /// <summary>查询参数（含查询字典映射）。</summary>
    Query,

    /// <summary>请求头（含头字典映射）。</summary>
    Header
}
