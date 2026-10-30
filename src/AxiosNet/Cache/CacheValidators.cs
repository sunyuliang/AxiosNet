namespace AxiosNet.Cache;

/// <summary>条件请求用的校验器：<c>ETag</c> 优先，其次 <c>Last-Modified</c>；都取自缓存条目的响应头。</summary>
internal static class CacheValidators
{
    internal const string ETag = "ETag";

    internal const string LastModified = "Last-Modified";

    /// <summary>条目里有没有可用于条件请求的校验器。</summary>
    internal static bool HasAny(AxiosShortCircuit entry)
        => Find(entry, ETag) is not null || Find(entry, LastModified) is not null;

    /// <summary>取头值（大小写不敏感、跳过空白值）；没有就返回 <c>null</c>。</summary>
    internal static string? Find(AxiosShortCircuit entry, string name)
    {
        if (entry.Headers is not { Count: > 0 } headers)
        {
            return null;
        }

        foreach (var header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(header.Value))
            {
                return header.Value;
            }
        }

        return null;
    }
}
