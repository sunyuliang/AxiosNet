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
            // 只接受单字符文本，多字符截断会静默改变值。
            if (raw.Length != 1)
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
                var parsed = Enum.Parse(target, raw, ignoreCase: true);

                // Enum.Parse 不校验未定义数值，这里复核；flags 枚举放行只由已定义位构成的组合值。
                if (!IsAcceptableEnumValue(target, parsed))
                {
                    return false;
                }

                value = parsed;
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

    /// <summary>枚举值是否可接受：已定义值，或 flags 枚举「只由已定义位构成」的组合值（<c>"Read,Write"</c>）；目标类型不是枚举时放行。</summary>
    public static bool IsAcceptableEnumValue(Type enumType, object value)
    {
        var target = Nullable.GetUnderlyingType(enumType) ?? enumType;
        if (!target.IsEnum)
        {
            return true;
        }

        return Enum.IsDefined(target, value) || IsFlagsCombination(target, value);
    }

    /// <summary>flags 枚举的组合值是否只由已定义位构成（<c>"Read,Write"</c> 合法、<c>"999"</c> 不合法）；非 flags 枚举返回 <c>false</c>。</summary>
    private static bool IsFlagsCombination(Type enumType, object parsed)
    {
        if (ReflectionCache.GetFlagMask(enumType) is not { } mask)
        {
            return false;
        }

        return mask.UnsignedMask is { } unsignedMask
            ? (Convert.ToUInt64(parsed, CultureInfo.InvariantCulture) & ~unsignedMask) == 0UL
            : (Convert.ToInt64(parsed, CultureInfo.InvariantCulture) & ~mask.SignedMask) == 0L;
    }
}
