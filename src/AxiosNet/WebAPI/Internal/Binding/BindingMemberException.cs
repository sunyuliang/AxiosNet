namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>
/// 绑定某一个成员时取值 / 格式化失败：携带通道与成员的定位文本，交给 <see cref="RequestBindingErrors"/> 报出「哪一步、哪个字段」。
/// </summary>
/// <remarks>原始异常原样进 <see cref="Exception.InnerException"/>，最终成为错误模型的 <c>Cause</c>。</remarks>
internal sealed class BindingMemberException : Exception
{
    /// <summary>构造一条成员级绑定失败；原始异常原样作为 <c>InnerException</c>。</summary>
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
