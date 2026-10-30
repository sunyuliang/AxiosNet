using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace AxiosNet.Cache;

/// <summary>
/// 解析后的 <c>Cache-Control</c> 响应指令（指令名大小写不敏感）。响应没带这个头时各项都是「未出现」。
/// </summary>
/// <remarks>
/// 只做解析，不做取舍：库自己按 <see cref="AxiosCacheOptions.FreshnessMode"/> 使用其中一部分，
/// 其余（<c>private</c> / <c>s-maxage</c> / <c>stale-*</c> / 扩展指令）原样交给 <see cref="IAxiosCacheStore"/>，自建存储可以按自己的策略用。
/// 数字指令按秒解析，其中 <c>max-age</c> / <c>s-maxage</c> 非法或负数按零（RFC 9111 §4.2.1：无效的新鲜期信息按已过期处理），
/// <c>stale-*</c> 非法或负数按「未出现」处理（<c>null</c>）。
/// </remarks>
public sealed class CacheControlDirectives
{
    /// <summary>响应没有 <c>Cache-Control</c> 时共用的空实例（不可变，避免每次解析都新建）。</summary>
    internal static readonly CacheControlDirectives Empty = new()
    {
        Extensions = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase))
    };

    /// <summary>响应的原始头值（多条按 <c>", "</c> 拼接）；没有这个头时为 <c>null</c>。</summary>
    public string? Raw { get; internal init; }

    /// <summary><c>no-store</c>：任何缓存都不得保存这条响应。</summary>
    public bool NoStore { get; internal init; }

    /// <summary><c>no-cache</c>：可以保存，但每次复用前都必须向源服务器验证（库只在「查不到新鲜条目」时走条件请求，命中即直接交付，故解析模式下与 <c>no-store</c> 同样不落库）。</summary>
    public bool NoCache { get; internal init; }

    /// <summary><c>private</c>：只允许私有缓存保存。进程内存储天然是私有的；接共享存储时可据此拒绝落库。</summary>
    public bool Private { get; internal init; }

    /// <summary><c>public</c>：即使带凭据等条件也允许共享缓存保存。</summary>
    public bool Public { get; internal init; }

    /// <summary><c>must-revalidate</c>：过期后不得再交出旧内容（库到点即弃，故无额外动作）。</summary>
    public bool MustRevalidate { get; internal init; }

    /// <summary><c>immutable</c>：在新鲜期内内容保证不变。</summary>
    public bool Immutable { get; internal init; }

    /// <summary><c>no-transform</c>：中间层不得改写正文。</summary>
    public bool NoTransform { get; internal init; }

    /// <summary><c>max-age</c>：新鲜期（秒）；出现但值非法或为负时为零。</summary>
    public TimeSpan? MaxAge { get; internal init; }

    /// <summary><c>s-maxage</c>：共享缓存的新鲜期。库按私有缓存解释，不采用它；出现但值非法或为负时为零。</summary>
    public TimeSpan? SharedMaxAge { get; internal init; }

    /// <summary><c>stale-while-revalidate</c>：过期后仍可先交旧内容、同时后台验证的窗口。</summary>
    public TimeSpan? StaleWhileRevalidate { get; internal init; }

    /// <summary><c>stale-if-error</c>：源服务器出错时仍可交旧内容的窗口。</summary>
    public TimeSpan? StaleIfError { get; internal init; }

    /// <summary>不认识的指令名 → 参数（无参数时为 <c>null</c>），如 <c>community="UCI"</c>。</summary>
    public IReadOnlyDictionary<string, string?> Extensions { get; internal init; }
        = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    private CacheControlDirectives()
    {
    }

    /// <summary>解析单个头值（请求侧只有一个 <c>Cache-Control</c>）；<c>null</c> / 空白视为没带这个头。</summary>
    internal static CacheControlDirectives ParseValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? Empty : Parse(new[] { value });

    /// <summary>解析一组 <c>Cache-Control</c> 头值；<c>null</c> / 空白视为没带这个头。</summary>
    internal static CacheControlDirectives Parse(IEnumerable<string>? values)
    {
        if (values is null)
        {
            return Empty;
        }

        bool noStore = false, noCache = false, isPrivate = false, isPublic = false;
        bool mustRevalidate = false, immutable = false, noTransform = false;
        TimeSpan? maxAge = null, sharedMaxAge = null, staleWhileRevalidate = null, staleIfError = null;
        Dictionary<string, string?>? extensions = null;
        StringBuilder? raw = null;

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            raw ??= new StringBuilder();
            if (raw.Length > 0)
            {
                raw.Append(", ");
            }

            raw.Append(value);

            foreach (var directive in SplitDirectives(value))
            {
                var equals = directive.IndexOf('=');
                var name = (equals < 0 ? directive : directive[..equals]).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                var argument = equals < 0 ? null : Unquote(directive[(equals + 1)..].Trim());

                switch (name.ToLowerInvariant())
                {
                    case "no-store":
                        noStore = true;
                        break;
                    case "no-cache":
                        noCache = true;
                        break;
                    case "private":
                        isPrivate = true;
                        break;
                    case "public":
                        isPublic = true;
                        break;
                    case "must-revalidate":
                        mustRevalidate = true;
                        break;
                    case "immutable":
                        immutable = true;
                        break;
                    case "no-transform":
                        noTransform = true;
                        break;
                    // 无效的新鲜期按零（RFC 9111 §4.2.1）：请求侧演变成绕开缓存，响应侧演变成不落库。
                    // 重复出现时非法值不覆盖前面解析出的合法值（有合法新鲜期信息就不算无效）。
                    case "max-age":
                        maxAge = ParseSeconds(argument) ?? maxAge ?? TimeSpan.Zero;
                        break;
                    case "s-maxage":
                        sharedMaxAge = ParseSeconds(argument) ?? sharedMaxAge ?? TimeSpan.Zero;
                        break;
                    case "stale-while-revalidate":
                        staleWhileRevalidate = ParseSeconds(argument) ?? staleWhileRevalidate;
                        break;
                    case "stale-if-error":
                        staleIfError = ParseSeconds(argument) ?? staleIfError;
                        break;
                    default:
                        extensions ??= new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                        extensions[name] = argument;
                        break;
                }
            }
        }

        if (raw is null)
        {
            return Empty;
        }

        return new CacheControlDirectives
        {
            Raw = raw.ToString(),
            NoStore = noStore,
            NoCache = noCache,
            Private = isPrivate,
            Public = isPublic,
            MustRevalidate = mustRevalidate,
            Immutable = immutable,
            NoTransform = noTransform,
            MaxAge = maxAge,
            SharedMaxAge = sharedMaxAge,
            StaleWhileRevalidate = staleWhileRevalidate,
            StaleIfError = staleIfError,
            Extensions = extensions ?? new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        };
    }

    /// <summary>按逗号切开指令，引号内的逗号不算分隔符（扩展指令的值可能是带逗号的引号串）。</summary>
    private static IEnumerable<string> SplitDirectives(string value)
    {
        var start = 0;
        var quoted = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                var part = value.AsSpan(start, i - start).Trim();
                if (!part.IsEmpty)
                {
                    yield return part.ToString();
                }

                start = i + 1;
            }
        }

        var tail = value.AsSpan(start).Trim();
        if (!tail.IsEmpty)
        {
            yield return tail.ToString();
        }
    }

    private static string? Unquote(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            return value[1..^1];
        }

        return value;
    }

    private static TimeSpan? ParseSeconds(string? argument)
    {
        if (argument is null
            || !long.TryParse(argument, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 0)
        {
            return null;
        }

        // 直接按秒换算可能超出 TimeSpan 上限：超出就按「无限长」处理。
        return seconds > long.MaxValue / TimeSpan.TicksPerSecond
            ? TimeSpan.MaxValue
            : TimeSpan.FromTicks(seconds * TimeSpan.TicksPerSecond);
    }
}
