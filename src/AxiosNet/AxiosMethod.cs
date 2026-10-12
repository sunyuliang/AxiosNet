namespace AxiosNet;

/// <summary>
/// 与 axios config.method 对齐的 HTTP 方法枚举。
/// </summary>
public enum AxiosMethod
{
    Get,
    Post,
    Put,
    Delete,
    Head,
    Options,
    Patch
}

internal static class AxiosMethodExtensions
{
    public static HttpMethod ToHttpMethod(this AxiosMethod method) => method switch
    {
        AxiosMethod.Get => HttpMethod.Get,
        AxiosMethod.Post => HttpMethod.Post,
        AxiosMethod.Put => HttpMethod.Put,
        AxiosMethod.Delete => HttpMethod.Delete,
        AxiosMethod.Head => HttpMethod.Head,
        AxiosMethod.Options => HttpMethod.Options,
        AxiosMethod.Patch => new HttpMethod("PATCH"),
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    public static string ToLowerName(this AxiosMethod method) => method.ToString().ToLowerInvariant();
}
