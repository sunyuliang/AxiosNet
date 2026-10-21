using System.Globalization;

namespace AxiosNet.Internal;

/// <summary>
/// 响应体不是合法 JSON、但语义上是「裸标量文本」时的兜底解析（<c>42</c>、<c>true</c>、GUID、日期等），
/// 使泛型请求可以直接返回基础类型。
/// </summary>
internal static class ScalarParser
{
    public static bool TryParse(string text, Type type, out object? value)
    {
        value = null;

        var target = Nullable.GetUnderlyingType(type) ?? type;
        var raw = text.Trim();

        if (target == typeof(string))
        {
            value = text;
            return true;
        }

        if (target == typeof(char))
        {
            if (raw.Length == 0)
            {
                return false;
            }

            value = raw[0];
            return true;
        }

        if (target == typeof(bool))
        {
            if (!bool.TryParse(raw, out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        if (target.IsEnum)
        {
            try
            {
                value = Enum.Parse(target, raw, ignoreCase: true);
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or OverflowException)
            {
                return false;
            }
        }

        if (target == typeof(Guid))
        {
            if (!Guid.TryParse(raw, out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        if (target == typeof(DateTime))
        {
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        if (target == typeof(DateTimeOffset))
        {
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        if (target == typeof(TimeSpan))
        {
            if (!TimeSpan.TryParse(raw, CultureInfo.InvariantCulture, out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        if (target == typeof(Uri))
        {
            if (!Uri.TryCreate(raw, UriKind.RelativeOrAbsolute, out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        if (target == typeof(DateOnly))
        {
            if (!DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        if (target == typeof(TimeOnly))
        {
            if (!TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                return false;
            }

            value = parsed;
            return true;
        }

        if (typeof(IConvertible).IsAssignableFrom(target))
        {
            // 覆盖其余数值类型（byte/sbyte/short/ushort/int/uint/long/ulong/float/double/decimal）。
            try
            {
                value = Convert.ChangeType(raw, target, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                return false;
            }
        }

        return false;
    }
}
