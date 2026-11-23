using System.Globalization;
using System.Text.Json;
using AxiosNet.Metadata;

namespace AxiosNet.Internal.Serialization;

/// <summary>值 → 线上文本的唯一出口：路径 / 查询 / 请求头 / 表单 / multipart 文本部件 / 裸标量正文共用；只产原始文本，不做转义。</summary>
/// <remarks>优先级：成员级 <see cref="ValueFormat"/> &gt; 实例级格式化钩子 &gt; 通道默认。</remarks>
internal static class ValueText
{
    /// <summary>取值的线上文本；<c>null</c> 值返回 <c>null</c>，由调用方决定跳过还是报错。</summary>
    public static string? Format(
        object? value,
        ValueTextChannel channel,
        ValueFormat? memberFormat = null,
        Func<object?, ValueTextChannel, string?>? formatter = null)
    {
        if (value is null)
        {
            return null;
        }

        if (TryCustomFormat(value, channel, memberFormat, formatter) is { } custom)
        {
            return custom;
        }

        return Default(value, channel);
    }

    /// <summary>只应用两层声明（成员级 <see cref="ValueFormat"/> &gt; 实例钩子），不计算通道默认。</summary>
    public static string? TryCustomFormat(
        object? value,
        ValueTextChannel channel,
        ValueFormat? memberFormat,
        Func<object?, ValueTextChannel, string?>? formatter)
    {
        if (value is null)
        {
            return null;
        }

        // 成员级声明压过实例级钩子。
        if (memberFormat?.TryFormat(value, channel) is { } declared)
        {
            return declared;
        }

        return formatter?.Invoke(value, channel);
    }

    /// <summary>JSON 树叶子节点的文本（数值保留原始书写形式，不经过 double 往返）。</summary>
    public static string JsonScalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => element.GetRawText()
    };

    /// <summary>JSON 树叶子节点的文本：先让实例钩子按 CLR 标量尝试一次，未命中回落到原始书写形式。</summary>
    public static string JsonScalar(JsonElement element, ValueTextChannel channel, Func<object?, ValueTextChannel, string?>? formatter)
    {
        if (formatter is not null)
        {
            object? scalar = element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var number) ? number : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };

            if (scalar is not null && TryCustomFormat(scalar, channel, memberFormat: null, formatter) is { } custom)
            {
                return custom;
            }
        }

        return JsonScalar(element);
    }

    /// <summary>通道默认形态：只有日期时间族分两档（正文保真用往返格式，其余折 UTC 毫秒）。</summary>
    private static string Default(object value, ValueTextChannel channel) => value switch
    {
        // 查询串 / 表单没有二进制表示，用 base64。
        byte[] bytes => Convert.ToBase64String(bytes),
        ReadOnlyMemory<byte> memory => PlatformCompat.ToBase64String(memory.Span),
        Memory<byte> memory => PlatformCompat.ToBase64String(memory.Span),
        ArraySegment<byte> segment => PlatformCompat.ToBase64String(segment.AsSpan()),
        string text => text,
        bool flag => flag ? "true" : "false",
        char character => character.ToString(),
        DateTime dateTime => channel == ValueTextChannel.ScalarBody
            ? dateTime.ToString("O", CultureInfo.InvariantCulture)
            : ToUtcMillis(dateTime),
        DateTimeOffset dateTimeOffset => channel == ValueTextChannel.ScalarBody
            ? dateTimeOffset.ToString("O", CultureInfo.InvariantCulture)
            : dateTimeOffset.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
#if !NETSTANDARD2_0
        // DateOnly / TimeOnly 是 .NET 6 类型，netstandard2.0 没有。
        DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly time => time.ToString("O", CultureInfo.InvariantCulture),
#endif
        Enum enumValue => enumValue.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };

    /// <summary><see cref="DateTimeKind.Unspecified"/> 按 UTC 解释，折成毫秒精度。</summary>
    private static string ToUtcMillis(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value;
        return utc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
