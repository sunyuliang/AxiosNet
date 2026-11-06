namespace AxiosNet.Metadata;

/// <summary>
/// 已定型的线上文本：调用方已经完成「值 → 文本」的转换，内核不再套用格式化钩子或通道默认规则。
/// </summary>
/// <remarks>
/// 两个用途共用一个类型：查询参数值（内核直接拼进查询串，并让自定义 <c>ParamsSerializer</c> 只看到纯文本）
/// 与裸标量正文（内核按标量正文的媒体类型交付，不再当成调用方传入的字符串）。
/// 它是调用方与内核之间的中性约定，不携带任何绑定 / 声明语义。
/// </remarks>
public sealed class PreformattedValue
{
    /// <summary>用已定型文本创建标记。</summary>
    /// <param name="text">已经转换好的线上文本，原样使用。</param>
    public PreformattedValue(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    /// <summary>已定型文本，直接拼进 URL 或作为正文，不会再被格式化钩子改写。</summary>
    public string Text { get; }

    /// <summary>返回 <see cref="Text"/>，便于诊断输出。</summary>
    public override string ToString() => Text;
}
