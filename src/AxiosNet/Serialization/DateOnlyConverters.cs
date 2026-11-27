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
/// 形态与 net8.0 的默认输出一致（<c>DateOnly</c> 写 <c>yyyy-MM-dd</c>、<c>TimeOnly</c> 写往返格式），
/// 保证同一份模型在两张现代资产上的线上字节相同。只在本 TFM 上编译。
/// </remarks>
internal sealed class DateOnlyConverter : JsonConverter<DateOnly>
{
    public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return value;
        }

        throw new JsonException($"'{text}' 不是合法的 DateOnly（期望 yyyy-MM-dd）。");
    }

    public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
}

/// <summary>net6.0 目标的 <c>TimeOnly</c> 转换器（形态见 <see cref="DateOnlyConverter"/>）。</summary>
internal sealed class TimeOnlyConverter : JsonConverter<TimeOnly>
{
    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        if (TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
        {
            return value;
        }

        throw new JsonException($"'{text}' 不是合法的 TimeOnly。");
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(TimeFormat(value), CultureInfo.InvariantCulture));

    /// <summary>
    /// 与 net8.0 的 System.Text.Json 输出逐字节一致：小数秒按 3 / 7 位输出，且全零时整段省略
    /// （<c>01:02:03</c>、<c>01:02:03.4560000</c>）。用往返格式会多出 <c>.0000000</c>，两张资产就不一致了。
    /// </summary>
    private static string TimeFormat(TimeOnly value)
        => value.Ticks % TimeSpan.TicksPerSecond == 0
            ? "HH:mm:ss"
            : value.Ticks % TimeSpan.TicksPerMillisecond == 0
                ? "HH:mm:ss.fff"
                : "O";
}
#endif
