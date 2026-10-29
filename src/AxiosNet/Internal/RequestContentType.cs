using System.Net.Http.Headers;
using System.Text;
using AxiosNet.Serialization;

namespace AxiosNet.Internal;

/// <summary>
/// 请求侧 <c>Content-Type</c> 的组装与解析：写头、按需补 charset、读取 boundary。
/// <see cref="Set"/> 是库内写 Content-Type 的唯一入口，统一在这里拦控制字符。
/// </summary>
internal static class RequestContentType
{
    /// <summary>写 Content-Type（库内唯一入口）；非法值按配置错误抛 <c>ERR_BAD_OPTION_VALUE</c>。</summary>
    public static void Set(HttpContent content, string mediaType, AxiosConfig? config = null)
    {
        try
        {
            AxiosHeaders.ValidateHeaderValue("Content-Type", mediaType);
        }
        catch (ArgumentException ex)
        {
            throw AxiosException.InvalidOption($"Content-Type 非法：{ex.Message}", config, ex);
        }

        content.Headers.Remove("Content-Type");
        content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
    }

    /// <summary>
    /// 组合实际写入的 Content-Type：已自带 charset 时保留用户设置，文本类型在显式设置 RequestEncoding 时补 charset。
    /// </summary>
    public static string Compose(string mediaType, Encoding encoding, bool appendCharset)
    {
        if (!appendCharset
            || HasCharsetParameter(mediaType)
            || !MediaTypes.IsTextual(mediaType))
        {
            return mediaType;
        }

        return mediaType + "; charset=" + encoding.WebName;
    }

    /// <summary>媒体类型是否已声明 charset（按标准解析，容忍 <c>charset = utf-8</c> / 带引号等写法）。</summary>
    private static bool HasCharsetParameter(string mediaType)
    {
        // 快速排除：没有 "charset" 字样时直接返回。
        if (!mediaType.Contains("charset", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (MediaTypeHeaderValue.TryParse(mediaType, out var parsed))
        {
            foreach (var parameter in parsed.Parameters)
            {
                if (parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        // 解析不出来时退回朴素查找，避免写出重复 charset。
        return mediaType.Contains("charset=", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析媒体类型参数里显式声明的 charset 对应的 <see cref="Encoding"/>；未声明或平台无法识别时为 <c>null</c>。
    /// </summary>
    public static Encoding? GetCharsetEncoding(string mediaType)
    {
        if (!MediaTypeHeaderValue.TryParse(mediaType, out var parsed))
        {
            return null;
        }

        foreach (var parameter in parsed.Parameters)
        {
            if (!parameter.Name.Equals("charset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 参数值可能带引号（charset="utf-8"）；charset 名本身不含引号，直接剥
            var name = parameter.Value?.Trim().Trim('"');
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            try
            {
                return Encoding.GetEncoding(name);
            }
            catch (ArgumentException)
            {
                // 平台不识别的 charset 名不在此判定。
                return null;
            }
        }

        return null;
    }

    /// <summary>从媒体类型参数里取 boundary（支持带引号的形式）；没有则返回 <c>null</c>。</summary>
    public static string? ExtractBoundary(string mediaType)
    {
        const string marker = "boundary=";
        var index = mediaType.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = mediaType[(index + marker.Length)..].Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            return end > 1 ? value[1..end] : null;
        }

        var separator = value.IndexOf(';');
        if (separator >= 0)
        {
            value = value[..separator];
        }

        value = value.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>内容自身是否已带 multipart boundary。</summary>
    public static bool HasMultipartBoundary(MediaTypeHeaderValue? contentType)
    {
        if (contentType?.MediaType is not { } mediaType
            || !mediaType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var parameter in contentType.Parameters)
        {
            if (parameter.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(parameter.Value))
            {
                return true;
            }
        }

        return false;
    }
}
