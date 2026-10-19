using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 对应 axios transformRequest 的默认行为，把 config.data 转成 HttpContent：
/// 对象 -&gt; JSON（application/json），字典 -&gt; 表单（application/x-www-form-urlencoded），
/// string/byte[]/Stream/HttpContent 原样透传。
/// </summary>
internal static class HttpContentFactory
{
    public static HttpContent? Create(object? data, AxiosConfig config)
    {
        // 请求体文本编码：默认 UTF-8（axios 行为），可用 config.RequestEncoding 覆盖。
        var encoding = config.RequestEncoding ?? Encoding.UTF8;

        switch (data)
        {
            case null:
                return null;

            case HttpContent existing:
                // 已构造好的内容（含 MultipartFormDataContent）直接透传
                return existing;

            case string text:
                // axios 对字符串 body 不做转换；默认方法头是 application/json
                return new StringContent(text, encoding, "application/json");

            case byte[] bytes:
                return new ByteArrayContent(bytes);

            case ReadOnlyMemory<byte> memory:
                return new ByteArrayContent(memory.ToArray());

            case Stream stream:
                return new StreamContent(stream);

            case IDictionary dictionary:
                return CreateFormContent(dictionary, encoding, config.RequestEncoding is not null);

            default:
                // 与 axios transformRequest 一致：对象先序列化为 JSON 字符串。
                // 直接构造 StringContent 可保证携带 Content-Length，避免分块传输带来的兼容性问题。
                var options = config.JsonSerializerOptions ?? JsonOptions.Web;
                var json = JsonSerializer.Serialize(data, data.GetType(), options);
                return new StringContent(json, encoding, "application/json");
        }
    }

    private static HttpContent CreateFormContent(IDictionary dictionary, Encoding encoding, bool explicitEncoding)
    {
        var pairs = new List<KeyValuePair<string, string>>(dictionary.Count);
        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Key is null || entry.Value is null)
            {
                continue;
            }

            var key = entry.Key.ToString() ?? string.Empty;
            if (entry.Value is not string and IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is not null)
                    {
                        pairs.Add(new KeyValuePair<string, string>(key, ScalarToString(item)));
                    }
                }
            }
            else
            {
                pairs.Add(new KeyValuePair<string, string>(key, ScalarToString(entry.Value)));
            }
        }

        if (pairs.Count == 0)
        {
            return new FormUrlEncodedContent(pairs);
        }

        // 表单体按「已转义的 ASCII 串」写入，因此编码只影响转义映射（默认 UTF-8 百分号编码，与 axios 一致）。
        var body = string.Join(
            '&',
            pairs.Select(pair => $"{PercentEncode(pair.Key, encoding)}={PercentEncode(pair.Value, encoding)}"));

        // 转义结果恒为 ASCII，因此 wire 上的字节一律按 ASCII 写出（charset 仅用于告知服务端转义所用的编码），
        // 避免 RequestEncoding 为 UTF-16/UTF-32 时把 ASCII 串编成多字节而破坏 form-urlencoded 格式。
        var content = new ByteArrayContent(Encoding.ASCII.GetBytes(body));
        var mediaType = explicitEncoding
            ? $"application/x-www-form-urlencoded; charset={encoding.WebName}"
            : "application/x-www-form-urlencoded";
        content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
        return content;
    }

    private static string PercentEncode(string value, Encoding encoding)
    {
        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            // 与 FormUrlEncodedContent 完全一致：空格转义为 '+'。
            return Uri.EscapeDataString(value).Replace("%20", "+");
        }

        var bytes = encoding.GetBytes(value);
        var builder = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            if (b == (byte)' ')
            {
                builder.Append('+');
            }
            else if (IsUnreserved(b))
            {
                builder.Append((char)b);
            }
            else
            {
                builder.Append('%').Append(HexUpper(b >> 4)).Append(HexUpper(b & 0xF));
            }
        }

        return builder.ToString();
    }

    private static bool IsUnreserved(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b is (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

    private static char HexUpper(int value) => (char)(value < 10 ? '0' + value : 'A' + value - 10);

    private static string ScalarToString(object value) => value switch
    {
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
        Enum e => e.ToString(),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty
    };
}
