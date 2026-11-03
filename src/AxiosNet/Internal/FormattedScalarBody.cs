namespace AxiosNet.Internal;

/// <summary>
/// 已格式化的裸标量请求体：声明成员是标量且带 <c>[Format]</c> 时由绑定层产出，
/// 让正文工厂仍按「标量正文」处理（媒体类型取序列化器自报值），而不是把它当成调用方传入的字符串。
/// </summary>
internal sealed class FormattedScalarBody
{
    public FormattedScalarBody(string text)
    {
        Text = text;
    }

    /// <summary>格式化后的正文文本。</summary>
    public string Text { get; }
}
