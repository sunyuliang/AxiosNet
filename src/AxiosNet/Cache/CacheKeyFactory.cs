using System.Text;

namespace AxiosNet.Cache;

/// <summary>缓存键的默认构造：方法 + 绝对 URL，带凭据时再拼一段凭据摘要。键只由本次生效配置与最终 URL 决定。</summary>
internal static class CacheKeyFactory
{
    /// <summary>按默认口径建键；参数为 <c>null</c> 时抛 <see cref="ArgumentNullException"/>。</summary>
    internal static string Create(AxiosConfig config, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(uri);

        var identity = ResolveIdentity(config);
        var absoluteUri = uri.AbsoluteUri;
        var builder = new StringBuilder(config.Method.Method.Length + absoluteUri.Length + 24 + (identity is null ? 0 : 17));

        builder.Append(config.Method.Method).Append(' ').Append(absoluteUri);

        if (identity is not null)
        {
            builder.Append(' ').Append(Digest(identity));
        }

        return builder.ToString();
    }

    /// <summary>身份来源：托管落点身份优先，其次显式 <c>Authorization</c> 头，最后内核凭据槽位解析出的头值；三者都只以摘要进键。</summary>
    private static string? ResolveIdentity(AxiosConfig config)
    {
        if (config.ResolvedCredentialIdentity is { } identity)
        {
            return identity;
        }

        if (config.HasHeaders && config.Headers.Get("Authorization") is { } authorization)
        {
            return authorization;
        }

        return config.ResolvedAuthorization;
    }

    /// <summary>凭据摘要：SHA-256 的前 8 字节转十六进制（16 个字符），只够区分身份、不足以还原原值。</summary>
    private static string Digest(string value)
        => PlatformCompat.Sha256HexPrefix8(value);
}
