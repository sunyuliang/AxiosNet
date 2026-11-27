#if !NETSTANDARD2_0 && !NET8_0_OR_GREATER
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AxiosNet.Serialization;

/// <summary>
/// net6.0 目标的 <c>DateOnly</c> / <c>TimeOnly</c> 转换器：该 TFM 用的是框架自带的 System.Text.Json 6.x，
/// 它没有这两个类型的转换器，反射重载会抛 <see cref="NotSupportedException"/>。
/// </summary>
/// <remarks>
/// 写出形态与 net8.0 逐字节一致（<c>DateOnly</c> 写 <c>yyyy-MM-dd</c>、<c>TimeOnly</c> 写 <c>HH:mm:ss</c> 或 7 位往返格式）；
/// 读入也照 net8.0 的口径收紧（只认这两种形态），避免同一份 JSON 在两张资产上一成一败。只在本 TFM 上编译。
/// </remarks>
internal sealed class DateOnlyConverter : JsonConverter<DateOnly>
{
    /// <summary>读 <c>yyyy-MM-dd</c>（严格按该形态，不做区域性宽松解析）；其它形态即 <see cref="JsonException"/>。</summary>
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && DateOnly.TryParseExact(reader.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return value;
        }

        throw new JsonException("不是合法的 DateOnly（期望 yyyy-MM-dd）。");
    }

    /// <summary>写 <c>yyyy-MM-dd</c>。</summary>
    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>net6.0 目标的 <c>TimeOnly</c> 转换器（形态见 <see cref="DateOnlyConverter"/>）。</summary>
internal sealed class TimeOnlyConverter : JsonConverter<TimeOnly>
{
    /// <summary>读 <c>HH:mm:ss</c> 或带 1-7 位小数秒的同形文本；其它形态即 <see cref="JsonException"/>。</summary>
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && TryParse(reader.GetString(), out var value))
        {
            return value;
        }

        throw new JsonException("不是合法的 TimeOnly（期望 HH:mm:ss 或带小数秒的同形文本）。");
    }

    /// <summary>按 <see cref="TimeFormat"/> 选定的形态写出。</summary>
    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(TimeFormat(value), CultureInfo.InvariantCulture));

    /// <summary>
    /// 与 net8.0 的 System.Text.Json 输出逐字节一致：小数秒全零时整段省略（<c>01:02:03</c>），
    /// 否则按往返格式写满 7 位（<c>01:02:03.4560000</c>）。
    /// </summary>
    /// <remarks>net8.0 对毫秒不做 3 位截断（<c>456</c> 毫秒写 <c>.4560000</c>），所以只有「全零省略」与「O」两种形态。</remarks>
    private static string TimeFormat(TimeOnly value)
        => value.Ticks % TimeSpan.TicksPerSecond == 0
            ? "HH:mm:ss"
            : "O";

    /// <summary>
    /// 按 net8.0 的接受集合解析：整串至少 8 字符（<c>hh:mm:ss</c> 的最短形态），
    /// 三段用 <c>:</c> 分隔且都是数字，可选 1-7 位小数秒。
    /// </summary>
    /// <remarks>组件位数不限（<c>000:02:03</c> 也接受），但整串长度与小数秒位数都要合规矩。</remarks>
    private static bool TryParse(string? text, out TimeOnly value)
    {
        value = default;

        if (text is null || text.Length < 8)
        {
            return false;
        }

        var dot = text.IndexOf('.');
        var head = dot < 0 ? text : text[..dot];
        var fraction = dot < 0 ? null : text[(dot + 1)..];

        if (fraction is { Length: 0 or > 7 } || (fraction is not null && !IsDigits(fraction)))
        {
            return false;
        }

        var parts = head.Split(':');
        if (parts.Length != 3 || !IsDigits(parts[0]) || !IsDigits(parts[1]) || !IsDigits(parts[2]))
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var second)
            || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        var ticks = fraction is null ? 0 : long.Parse(fraction.PadRight(7, '0'), CultureInfo.InvariantCulture);
        value = new TimeOnly(hour, minute, second).Add(TimeSpan.FromTicks(ticks));
        return true;
    }

    /// <summary>整段都是 ASCII 数字。</summary>
    private static bool IsDigits(string text)
    {
        foreach (var character in text)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
#endif
