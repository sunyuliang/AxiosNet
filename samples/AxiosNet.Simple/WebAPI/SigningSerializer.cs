using System.Security.Cryptography;
using System.Text;
using AxiosNet.Serialization;

namespace AxiosNet.Simple.WebAPI;

/// <summary>
/// 签名 / 验签序列化器：装饰任意 <c>IContentSerializer</c>（示例里是内置 JSON），请求出口对将要上线的正文算 HMAC-SHA256 写入 <c>X-Signature</c>，响应入口先验签再交内层解析。
/// </summary>
public sealed class SigningSerializer : IContentSerializer
{
    private readonly IContentSerializer _inner;
    private readonly byte[] _key;

    public SigningSerializer(IContentSerializer inner, string key)
    {
        _inner = inner;
        _key = Encoding.UTF8.GetBytes(key);
    }

    /// <summary>先让内层序列化出正文，再对这段字节签名写进 <c>X-Signature</c> 头。</summary>
    public string Serialize(object? value, Type type, AxiosConfig config)
    {
        var body = _inner.Serialize(value, type, config);

        // 对「将要上线的字节」签名；序列化器可写请求头，写入的头在正文构造完成后统一应用
        config.Headers.Set("X-Signature", Sign(body));

        return body;
    }

    /// <summary>先用响应头里的 <c>X-Signature</c> 验签，通过后才交给内层解析。</summary>
    public object? Deserialize(string text, Type type, AxiosConfig config)
    {
        // config.Response 只在响应方向有值（状态码 / 响应头 / 字符集）
        var expected = config.Response?.GetHeader("X-Signature");
        var actual = Sign(text);

        if (string.IsNullOrEmpty(expected) ||
            !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"响应验签失败：X-Signature 缺失或不匹配（预期 {actual[..16]}…，实际 {expected ?? "null"}）。");
        }

        return _inner.Deserialize(text, type, config); // 验签过了才解析
    }

    /// <summary>正文的 HMAC-SHA256，输出大写十六进制。</summary>
    private string Sign(string text)
        => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(text)));
}
