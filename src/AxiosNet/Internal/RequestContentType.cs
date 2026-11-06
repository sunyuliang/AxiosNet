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
    /// 取媒体类型里显式声明的 charset，供「正文要按它编码 / 要与头判定一致」的调用点使用：
    /// 没有声明 charset 时为 <c>null</c>，声明了但当前平台解析不出来时抛 <c>ERR_BAD_OPTION_VALUE</c>。
    /// </summary>
    /// <remarks>
    /// 解析不出来的名字**必须**是错误而不是 <c>null</c>：库零依赖、不注册 <c>CodePagesEncodingProvider</c>，
    /// <c>charset=gbk</c> / <c>windows-1252</c> 在默认 .NET 上本来就解析不出来；调用点再把它当「没声明」，
    /// 正文就会按 UTF-8 编码、头里却留着 gbk，服务端按头解出乱码且没有任何报错。
    /// </remarks>
    public static Encoding? ResolveCharset(string mediaType, AxiosConfig? config)
    {
        if (ParseCharset(mediaType, out var unresolved) is { } encoding)
        {
            return encoding;
        }

        if (unresolved is null)
        {
            return null;
        }

        throw AxiosException.InvalidOption(
            $"Content-Type 声明的 charset '{unresolved}' 在当前平台上解析不出来：库零依赖、不注册 System.Text.Encoding.CodePages，"
            + "默认只有 UTF-8 / UTF-16 / ASCII / ISO-8859-1。按这个头发送等于把实际上按 UTF-8 编码的正文标成别的编码，"
            + "服务端会解出乱码。请改用内置编码（如 utf-8），或在应用启动时注册 "
            + "Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)（需引用 System.Text.Encoding.CodePages 包）。",
            config);
    }

    /// <summary>解析 charset 参数：返回编码；解析不出来时由 <paramref name="unresolvedName"/> 带回声明的名字（不判定对错）。</summary>
    private static Encoding? ParseCharset(string mediaType, out string? unresolvedName)
    {
        unresolvedName = null;
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

            var resolved = EncodingResolver.TryResolve(name);
            if (resolved is null)
            {
                // 平台不识别的 charset 名不在此判定：要不要报错由调用点（ResolveCharset）决定。
                unresolvedName = name;
            }

            return resolved;
        }

        return null;
    }

    /// <summary>
    /// 从媒体类型参数里取 boundary；没有则返回 <c>null</c>。
    /// 统一走 <see cref="MediaTypeHeaderValue.TryParse"/>：参数顺序、<c>boundary = abc</c> 的空格、引号都按 RFC 规则处理，
    /// 不用字符串查找，避免「声明了边界却按没有处理」的静默替换。
    /// </summary>
    public static string? ExtractBoundary(string mediaType)
    {
        // 无法解析的媒体类型按「没有声明 boundary」处理：是否合法由写头 / 构建内容处决定。
        if (!MediaTypeHeaderValue.TryParse(mediaType, out var parsed))
        {
            return null;
        }

        foreach (var parameter in parsed.Parameters)
        {
            if (!parameter.Name.Equals("boundary", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = parameter.Value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            // 引号包裹的边界：引号本身不进 boundary；未闭合的引号视为没有声明。
            if (value[0] == '"')
            {
                if (value.Length < 2 || value[^1] != '"')
                {
                    return null;
                }

                value = value[1..^1].Trim();
            }

            return value.Length == 0 ? null : value;
        }

        return null;
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
