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
            // 只接受单字符文本：多字符取首字符是静默截断（"abc" → 'a'），
            // 调用方以为拿到的是完整值，比直接报错更难排查。
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

                // Enum.Parse 不校验未定义数值（"999" 会静默产出 (E)999）：不复核的话
                // GetAsync<MyEnum> 会把服务端的非法枚举值交作 Data，而不是按 ERR_BAD_RESPONSE 失败。
                // flags 枚举放行组合值（"Read,Write"），但要求组合里没有已定义位之外的位。
                if (!Enum.IsDefined(target, parsed) && !IsFlagsCombination(target, parsed))
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

    /// <summary>
    /// flags 枚举的组合值是否只由已定义位构成（<c>"Read,Write"</c> 合法、<c>"999"</c> 不合法）。
    /// <para>非 flags 枚举直接 false；按底层类型分流（decimal 没有位运算）：ulong 底层按无符号汇总，
    /// 其余按 <see cref="long"/>（负值 flags 同样正确）。</para>
    /// </summary>
    private static bool IsFlagsCombination(Type enumType, object parsed)
    {
        if (!enumType.IsDefined(typeof(FlagsAttribute), inherit: false))
        {
            return false;
        }

        if (Enum.GetUnderlyingType(enumType) == typeof(ulong))
        {
            var unsignedMask = ulong.MinValue;
            foreach (var defined in Enum.GetValues(enumType))
            {
                unsignedMask |= Convert.ToUInt64(defined, CultureInfo.InvariantCulture);
            }

            return (Convert.ToUInt64(parsed, CultureInfo.InvariantCulture) & ~unsignedMask) == ulong.MinValue;
        }

        var mask = 0L;
        foreach (var defined in Enum.GetValues(enumType))
        {
            mask |= Convert.ToInt64(defined, CultureInfo.InvariantCulture);
        }

        return (Convert.ToInt64(parsed, CultureInfo.InvariantCulture) & ~mask) == 0L;
    }
}
