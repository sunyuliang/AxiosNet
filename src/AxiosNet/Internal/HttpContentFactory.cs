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
        switch (data)
        {
            case null:
                return null;

            case HttpContent existing:
                // 已构造好的内容（含 MultipartFormDataContent）直接透传
                return existing;

            case string text:
                // axios 对字符串 body 不做转换；默认方法头是 application/json
                return new StringContent(text, Encoding.UTF8, "application/json");

            case byte[] bytes:
                return new ByteArrayContent(bytes);

            case ReadOnlyMemory<byte> memory:
                return new ByteArrayContent(memory.ToArray());

            case Stream stream:
                return new StreamContent(stream);

            case IDictionary dictionary:
                return CreateFormContent(dictionary);

            default:
                // 与 axios transformRequest 一致：对象先序列化为 JSON 字符串。
                // 直接构造 StringContent 可保证携带 Content-Length，避免分块传输带来的兼容性问题。
                var options = config.JsonSerializerOptions ?? JsonOptions.Web;
                var json = JsonSerializer.Serialize(data, data.GetType(), options);
                return new StringContent(json, Encoding.UTF8, "application/json");
        }
    }

    private static HttpContent CreateFormContent(IDictionary dictionary)
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

        return new FormUrlEncodedContent(pairs);
    }

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
