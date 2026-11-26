using System.Collections.ObjectModel;
using System.Text;

namespace AxiosNet.Cache;

/// <summary>解析后的 <c>Cache-Control</c> 指令（请求头与响应头共用；指令名大小写不敏感）。头不存在时各项都是「未出现」。</summary>
/// <remarks>
/// 只解析不取舍：库按 <see cref="AxiosCacheOptions.FreshnessMode"/> 用其中一部分，其余原样交给自建存储。
/// 数字指令按秒解析；<c>max-age</c> / <c>s-maxage</c> 非法或负数按零，<c>stale-*</c> 非法或负数按「未出现」。
/// </remarks>
public sealed class CacheControlDirectives
{
    /// <summary>没有 <c>Cache-Control</c> 时共用的空实例（不可变）。</summary>
    internal static readonly CacheControlDirectives Empty = new()
    {
        Extensions = new ReadOnlyDictionary<string, string?>(
            new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase))
    };

    /// <summary>原始头值（多条按 <c>", "</c> 拼接）；没有这个头时为 <c>null</c>。</summary>
    public string? Raw { get; internal init; }

    /// <summary><c>no-store</c>：任何缓存都不得保存这条响应。</summary>
    public bool NoStore { get; internal init; }

    /// <summary><c>no-cache</c>：可以保存，但每次复用前须向源验证；本库解析模式下与 <c>no-store</c> 同样不落库。</summary>
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

            // 引号不成对说明头值畸形：切分退回「按所有逗号切开」，落在未闭合引号之后的内容不作为新鲜期来源。
            var quotes = QuoteScan.Of(value);

            foreach (var (directive, trusted) in SplitDirectives(value, quotes))
            {
                var equals = directive.IndexOf('=');
                var name = (equals < 0 ? directive : directive[..equals]).Trim();
                if (name.Length == 0)
                {
                    continue;
                }

                var argument = equals < 0 ? null : Unquote(directive[(equals + 1)..].Trim());
                var lowered = name.ToLowerInvariant();

                // 未闭合引号之后（含其内）的指令只接受收紧缓存的标志：放宽类（新鲜期 / stale-*）来自畸形头值，不采信。
                if (!trusted && ExtendsCacheLifetime(lowered))
                {
                    continue;
                }

                switch (lowered)
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
                    // 无效新鲜期按零；重复出现时非法值不覆盖已有合法值。
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

    /// <summary>按逗号切开指令，合法引号内的逗号不算分隔符（扩展指令的值可能是带逗号的引号串）。</summary>
    /// <param name="value">头值。</param>
    /// <param name="quotes">这条头值的引号扫描结果。</param>
    /// <returns>指令文本，以及它是否可采信（见 <see cref="QuoteScan.IsTrusted"/>）。</returns>
    /// <remarks>
    /// 引号不成对时退回「按所有逗号切分」：后续的 <c>no-store</c> 不会被整段当成引号内容吞掉。
    /// <c>quoted-pair</c>（<c>\"</c> / <c>\\</c>）里的引号不参与状态翻转，也不参与分隔。
    /// </remarks>
    private static IEnumerable<(string Text, bool Trusted)> SplitDirectives(string value, QuoteScan quotes)
    {
        var quoteSensitive = quotes.Balanced;
        var start = 0;
        var quoted = false;

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (quoteSensitive && c == '\\' && quoted && i + 1 < value.Length)
            {
                // 被转义的字符原样留在指令里，不参与分隔与状态判定。
                i++;
                continue;
            }

            if (quoteSensitive && c == '"')
            {
                quoted = !quoted;
            }
            else if (c == ',' && !quoted)
            {
                var part = value.AsSpan(start, i - start).Trim();
                if (!part.IsEmpty)
                {
                    yield return (part.ToString(), quotes.IsTrusted(i));
                }

                start = i + 1;
            }
        }

        var tail = value.AsSpan(start).Trim();
        if (!tail.IsEmpty)
        {
            yield return (tail.ToString(), quotes.IsTrusted(value.Length));
        }
    }

    /// <summary>一条头值的引号扫描结果：是否成对，以及第一个未闭合引号的起点。</summary>
    private readonly struct QuoteScan
    {
        private QuoteScan(int brokenAt) => BrokenAt = brokenAt;

        /// <summary>第一个未闭合引号的起点；引号成对时为 <c>-1</c>。</summary>
        private int BrokenAt { get; }

        /// <summary>引号是否成对；成对时引号内的逗号才享有豁免。</summary>
        internal bool Balanced => BrokenAt < 0;

        /// <summary>扫一遍引号；<c>quoted-pair</c>（<c>\"</c> / <c>\\</c>）里的引号不计入。</summary>
        internal static QuoteScan Of(string value)
        {
            var open = -1;

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];

                if (c == '\\' && open >= 0 && i + 1 < value.Length)
                {
                    // 跳过被转义的字符，它不改变引号状态。
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    open = open < 0 ? i : -1;
                }
            }

            return new QuoteScan(open);
        }

        /// <summary>这一段（终点不越过未闭合引号的起点）是否落在畸形引号之外，因而可以采信。</summary>
        internal bool IsTrusted(int segmentEnd) => BrokenAt < 0 || segmentEnd <= BrokenAt;
    }

    /// <summary>这条指令会不会延长缓存寿命（给出新鲜期，或允许在过期后继续交旧内容）。</summary>
    private static bool ExtendsCacheLifetime(string name)
        => name is "max-age" or "s-maxage" or "stale-while-revalidate" or "stale-if-error";

    /// <summary>去掉指令值两侧的双引号并解开 <c>quoted-pair</c>；空值返回 null。</summary>
    /// <remarks>与 <see cref="SplitDirectives"/> 共用同一套转义规则，避免「切分认转义、取值不认」的不一致。</remarks>
    private static string? Unquote(string value)
    {
        if (value.Length == 0)
        {
            return null;
        }

        if (value.Length < 2 || value[0] != '"' || value[^1] != '"')
        {
            return value;
        }

        var inner = value.AsSpan(1, value.Length - 2);

        // 没有转义就直出，省掉一次分配。
        if (inner.IndexOf('\\') < 0)
        {
            return inner.ToString();
        }

        var builder = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                i++;
            }

            builder.Append(inner[i]);
        }

        return builder.ToString();
    }

    /// <summary>把 <c>max-age</c> 之类的秒数换成时长：非数字或负数返回 null，超出 <see cref="TimeSpan"/> 上限按无限长处理。</summary>
    /// <remarks>与响应侧共用 <see cref="AxiosCache.ParseSeconds"/>：两侧对同一个秒数必须给出同一结论。</remarks>
    private static TimeSpan? ParseSeconds(string? argument) => AxiosCache.ParseSeconds(argument);
}
