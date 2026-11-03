using System.Globalization;

namespace AxiosNet.Metadata;

/// <summary>成员级值格式化：格式串（<see cref="IFormattable"/>）或格式化函数。</summary>
/// <remarks>
/// 只作用于库自己把值转成线上文本的通道（路径 / 查询 / 请求头 / 表单 / multipart 文本部件 / 裸标量正文），
/// 不进入结构化正文内部——那里由序列化器负责（要让 JSON 文档内部也生效，用可选的
/// <c>AxiosNet.WebAPI.Serialization.AxiosJsonFormat.ApplyTo</c> 桥接）。
/// </remarks>
public sealed class ValueFormat
{
    private readonly string? _format;
    private readonly Func<object?, string?>? _formatter;

    private ValueFormat(string? format, Func<object?, string?>? formatter)
    {
        _format = format;
        _formatter = formatter;
    }

    /// <summary>按格式串格式化：值须实现 <see cref="IFormattable"/>，区域固定为不变文化。</summary>
    /// <param name="format">复合格式串（如 <c>yyyy-MM-dd</c>、<c>N2</c>）。</param>
    public static ValueFormat FromFormat(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            throw new ArgumentException("格式串不能为空白：不指定格式请直接省略 [Format]。", nameof(format));
        }

        return new ValueFormat(format.Trim(), null);
    }

    /// <summary>按格式化函数取文本；函数返回 <c>null</c> 表示本次不处理，回落到默认规则。</summary>
    /// <param name="formatter">接收成员值、返回线上文本的委托。</param>
    public static ValueFormat FromDelegate(Func<object?, string?> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        return new ValueFormat(null, formatter);
    }

    /// <summary>是否由格式化函数实现（构建期校验据此区分「必须实现 IFormattable」与「函数自理」）。</summary>
    internal bool HasDelegate => _formatter is not null;

    /// <summary>取值的线上文本；无法格式化时返回 <c>null</c>（调用方回落默认规则）。</summary>
    internal string? TryFormat(object value)
        => _formatter is not null
            ? _formatter(value)
            : value is IFormattable formattable ? formattable.ToString(_format, CultureInfo.InvariantCulture) : null;
}
