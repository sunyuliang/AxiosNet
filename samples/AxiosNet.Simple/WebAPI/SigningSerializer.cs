using System.Security.Cryptography;
using System.Text;
using AxiosNet.Serialization;

namespace AxiosNet.Simple.WebAPI;

/// <summary>
/// 签名/验签序列化器：装饰任意 <see cref="IContentSerializer"/>（示例里是内置 JSON），
/// 请求出口对「将要上线的正文文本」算 HMAC-SHA256 写进 X-Signature 头，
/// 响应入口先验签、再交给内层解析，内容被篡改时在反序列化之前就失败。
/// 约定：挂了这个序列化器的客户端，对端必须对所有响应签名（严格模式，
/// 缺 X-Signature 视为验签失败）；二进制格式请改用字节通道做同构装饰。
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

    public string Serialize(object? value, Type type, AxiosConfig config)
    {
        var body = _inner.Serialize(value, type, config);

        // 对「将要上线的字节」签名；序列化器可写请求头，写入的头在正文构造完成后统一应用
        config.Headers.Set("X-Signature", Sign(body));

        return body;
    }

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

    private string Sign(string text)
        => Convert.ToHexString(HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(text)));
}
