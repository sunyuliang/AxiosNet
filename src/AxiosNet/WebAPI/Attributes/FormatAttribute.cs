using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Attributes;

/// <summary>
/// 成员级值格式化：作用于路径 / 查询 / 请求头 / 表单 / multipart 文本部件 / 裸标量正文。
/// 优先级高于实例级 <see cref="AxiosConfig.SerializationSettings.ValueText"/>，是两层声明里最具体的一层；结构化正文内部的格式化归序列化器。
/// </summary>
/// <remarks>
/// 两种形态互斥：格式串走 <see cref="IFormattable"/>（区域固定为不变文化），格式化函数走静态方法
/// <c>public static string? M(object? value, ValueTextChannel channel)</c>（返回 <c>null</c> 回落默认规则）。
/// 函数按通道分叉（如请求头用 RFC 1123、查询串用 ISO 8601），与实例钩子拿到的通道口径一致。
/// 集合 / 字典上标注时逐元素（逐值）应用；元素不是标量、与结构化 <c>[Body]</c> 同用、用在文件部件上都在构建期报错。
/// </remarks>
/// <example>
/// <code>
/// [Format("yyyy-MM-dd")]
/// public DateTime? Since { get; init; }
///
/// [Format(typeof(Formats), nameof(Formats.Money))]
/// public decimal Amount { get; init; }
///
/// public static class Formats
/// {
///     public static string? Money(object? value, ValueTextChannel channel)
///         => value is decimal amount ? amount.ToString("0.00", CultureInfo.InvariantCulture) : null;
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class FormatAttribute : Attribute
{
    /// <summary>格式串；值须实现 <see cref="IFormattable"/>。</summary>
    public string? Format { get; }

    /// <summary>格式化函数所在类型；与 <see cref="FormatterMethod"/> 同时为 <c>null</c> 时表示使用格式串。</summary>
    public Type? FormatterType { get; }

    /// <summary>格式化函数的静态方法名。</summary>
    public string? FormatterMethod { get; }

    /// <summary>按格式串格式化。</summary>
    /// <param name="format">复合格式串（如 <c>yyyy-MM-dd</c>）。</param>
    public FormatAttribute(string format)
    {
        if (string.IsNullOrWhiteSpace(format))
        {
            throw new ArgumentException("格式串不能为空白：不指定格式请直接省略 [Format]。", nameof(format));
        }

        Format = format.Trim();
    }

    /// <summary>按静态格式化函数取文本。</summary>
    /// <param name="formatterType">声明格式化函数的类型。</param>
    /// <param name="formatterMethod">静态方法名；签名须为 <c>public static string? M(object? value, ValueTextChannel channel)</c>。请写 <c>nameof(...)</c>：拼错由编译器报错，签名不符由源生成器报 <c>AXA015</c>。</param>
    public FormatAttribute(Type formatterType, string formatterMethod)
    {
        ArgumentNullException.ThrowIfNull(formatterType);

        if (string.IsNullOrWhiteSpace(formatterMethod))
        {
            throw new ArgumentException("格式化函数名不能为空白。", nameof(formatterMethod));
        }

        FormatterType = formatterType;
        FormatterMethod = formatterMethod.Trim();
    }

}
