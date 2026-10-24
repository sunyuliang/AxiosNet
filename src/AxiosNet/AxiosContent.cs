using System.Collections.Concurrent;
using AxiosNet.Internal;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>
/// 请求体 / 响应体的<b>内容格式</b>入口（对应「用哪种格式说话」）。内置 JSON（默认）与 XML 两个实现。
/// <para>选择规则按优先级：</para>
/// <list type="number">
/// <item>单次请求 / 实例 defaults 的 <see cref="AxiosConfig.SerializationSettings.ContentSerializer"/>（最强）；</item>
/// <item><see cref="Serializer"/>（全局显式注入；<c>null</c> = 未注入，即自动模式）；</item>
/// <item>自动模式：按媒体类型查注册表（<see cref="Register"/>）；</item>
/// <item>仍未命中 → 内置 JSON。</item>
/// </list>
/// <para>
/// 查表键：<b>请求方向</b>是 <see cref="AxiosConfig.ContentType"/>，<b>响应方向</b>是响应 <c>Content-Type</c>
/// —— 同一个应用可以「请求 JSON、响应 XML」。精确匹配不中时按结构化后缀再试一次
/// （<c>application/vnd.api+xml</c> → <c>application/xml</c>），因此注册一次即可覆盖厂商变体。
/// 媒体类型参数与大小写不参与匹配。
/// </para>
/// <para>
/// 与自报头是互逆的：自动模式按头选格式，而序列化器可以反过来自报头（见
/// <see cref="IContentSerializer.MediaType"/>），所以 <c>AxiosContent.Serializer = XmlContentSerializer.Instance</c>
/// 一处改动即可让请求头变成 <c>application/xml</c>。
/// </para>
/// </summary>
/// <example>
/// <code>
/// // 全局统一用 XML（显式注入，覆盖一切）
/// AxiosContent.Serializer = XmlContentSerializer.Instance;
///
/// // 或者：自动模式（注入保持 null），按媒体类型注册各自的格式
/// AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
/// AxiosContent.Register(AxiosContentType.Json, new MyJsonSerializer());
/// </code>
/// </example>
public static class AxiosContent
{
    private static readonly ConcurrentDictionary<string, IContentSerializer> Registry =
        new(StringComparer.Ordinal);

    /// <summary>
    /// 注册表条目数，只用于 <see cref="Lookup"/> 的「空表」快路径判定：
    /// <see cref="ConcurrentDictionary{TKey,TValue}.IsEmpty"/> 会取全部桶锁，比一次字符串归一化还贵。
    /// 注册/注销是启动期动作，这里的计数允许与并发写入有极短的可见性滞后（最多错过一次查找）。
    /// </summary>
    private static int _registeredCount;

    private static IContentSerializer? _serializer;

    /// <summary>
    /// 全局内容序列化器。<b><c>null</c>（默认）= 未注入</b>，此时走自动模式：按媒体类型查注册表，
    /// 查不到再用内置 JSON。赋值为 <c>null</c> 可随时回到自动模式。
    /// </summary>
    public static IContentSerializer? Serializer
    {
        get => Volatile.Read(ref _serializer);
        set => Volatile.Write(ref _serializer, value);
    }

    /// <summary>
    /// 为某个媒体类型注册序列化器（自动模式使用）。
    /// <para>
    /// 键是媒体类型主值：参数与大小写会被忽略；重复注册以最后一次为准；
    /// 厂商类型（<c>application/vnd.api+xml</c>）会回落到 <c>application/xml</c>，除非它自己也注册过。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException"><paramref name="mediaType"/> 为空。</exception>
    public static void Register(string mediaType, IContentSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        var key = MediaTypes.Normalize(mediaType)
            ?? throw new ArgumentException("媒体类型不能为空。", nameof(mediaType));

        if (!Registry.TryAdd(key, serializer))
        {
            // 重复注册以最后一次为准：覆盖已有项，计数不变。
            Registry[key] = serializer;
            return;
        }

        Interlocked.Increment(ref _registeredCount);
    }

    /// <summary>取消某个媒体类型的注册（回到「未注册」，即自动模式回落内置 JSON）。</summary>
    /// <returns>是否确实删掉了一项。</returns>
    public static bool Unregister(string mediaType)
    {
        var key = MediaTypes.Normalize(mediaType);
        if (key is null || !Registry.TryRemove(key, out _))
        {
            return false;
        }

        Interlocked.Decrement(ref _registeredCount);
        return true;
    }

    /// <summary>
    /// 取本次请求实际生效的序列化器：单次/实例注入 → 全局注入 → 按媒体类型自动匹配 → 内置 JSON。
    /// </summary>
    /// <param name="config">请求配置（读取单次/实例注入）。</param>
    /// <param name="mediaType">请求的 <see cref="AxiosConfig.ContentType"/> 或响应的 <c>Content-Type</c>。</param>
    internal static IContentSerializer Get(AxiosConfig config, string? mediaType)
        => config.SerializationOrNull?.ContentSerializer
            ?? Volatile.Read(ref _serializer)
            ?? Lookup(mediaType)
            ?? SystemTextJsonSerializer.Instance;

    /// <summary>
    /// 同一格式的常见别名：注册一次 canonical，别名头也要命中。
    /// <para>
    /// 现实中大量老服务端把 XML 标成 <c>text/xml</c>、把 JSON 标成 <c>text/json</c>；
    /// 只做精确匹配会让「注册了 <c>application/xml</c>」在响应方向完全落空（转而用内置 JSON 解析 XML → 报错）。
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["text/xml"] = AxiosContentType.Xml,
        ["text/json"] = AxiosContentType.Json
    };

    /// <summary>
    /// 自动模式的查表：精确匹配 → 别名 → 结构化后缀回落到 <c>application/&lt;suffix&gt;</c>。
    /// 未注册返回 <c>null</c>，由调用方回落内置 JSON。
    /// </summary>
    private static IContentSerializer? Lookup(string? mediaType)
    {
        // 注册表为空时任何媒体类型都查不到：先于归一化返回，省掉每请求一次子串 + 大小写转换
        // （零注册是绝大多数应用的形态，这条快路径让「无注入」时查找本身零分配）。
        // 用计数而不是 ConcurrentDictionary.IsEmpty：后者每次都要取全部桶锁。
        if (Volatile.Read(ref _registeredCount) == 0)
        {
            return null;
        }

        var main = MediaTypes.Normalize(mediaType);
        if (main is null)
        {
            return null;
        }

        if (Registry.TryGetValue(main, out var exact))
        {
            return exact;
        }

        if (Aliases.TryGetValue(main, out var canonical) && Registry.TryGetValue(canonical, out var aliased))
        {
            return aliased;
        }

        var plus = main.LastIndexOf('+');
        if (plus > 0 && plus < main.Length - 1
            && Registry.TryGetValue("application/" + main[(plus + 1)..], out var structured))
        {
            return structured;
        }

        return null;
    }
}
