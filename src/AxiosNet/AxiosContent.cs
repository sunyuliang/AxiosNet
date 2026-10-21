using System.Collections.Concurrent;
using AxiosNet.Internal;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>
/// 请求体 / 响应体的<b>内容格式</b>入口（对应「用哪种格式说话」）。
/// <para>
/// 系统内置两个实现：JSON（<see cref="SystemTextJsonSerializer"/>，库的默认）与
/// XML（<see cref="XmlContentSerializer"/>）。选择规则按优先级：
/// </para>
/// <list type="number">
/// <item>单次请求 / 实例 defaults 的 <see cref="AxiosConfig.ContentSerializer"/>（显式注入，最强）；</item>
/// <item><see cref="Serializer"/>（全局显式注入；<c>null</c> = <b>未注入</b>，即自动模式）；</item>
/// <item>自动模式：按媒体类型查注册表（<see cref="Register"/>），未注册得到 <c>null</c>；</item>
/// <item>仍未命中 → 内置默认 JSON（<see cref="SystemTextJsonSerializer.Instance"/>）。</item>
/// </list>
/// <para>
/// 自动模式查表用的键：<b>请求方向</b>是 <see cref="AxiosConfig.ContentType"/>（如
/// <c>application/xml</c>），<b>响应方向</b>是响应的 <c>Content-Type</c> ——
/// 也就是说同一个应用可以「请求 JSON、响应 XML」，只要各自注册好。
/// 精确匹配不中时会按结构化后缀再试一次（<c>application/vnd.api+xml</c> →
/// <c>application/xml</c>），因此给一种格式注册一次就能覆盖它的厂商变体。
/// </para>
/// <para>
/// 媒体类型参数（<c>; charset=...</c>）与大小写都不参与匹配。
/// </para>
/// <para>
/// 两个方向是互逆的：<b>自动模式按头选格式</b>（请求看 <see cref="AxiosConfig.ContentType"/>、
/// 响应看响应头），而序列化器可以<b>反过来自报头</b> —— 调用方没显式设置
/// <see cref="AxiosConfig.ContentType"/> 时，生效序列化器的
/// <see cref="IContentSerializer.MediaType"/> 会写进请求头。于是
/// <c>AxiosContent.Serializer = XmlContentSerializer.Instance</c> 一处改动，
/// 请求头就是 <c>application/xml</c>，不必再手动配对头与正文。
/// </para>
/// </summary>
/// <example>
/// <code>
/// // 全局统一用 XML（显式注入，覆盖一切）
/// AxiosContent.Serializer = XmlContentSerializer.Instance;
///
/// // 或者：按媒体类型自动匹配 —— 注入保持 null（自动模式），注册各自的格式
/// AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
/// AxiosContent.Register(AxiosContentType.Json, new MyJsonSerializer());   // 顺带换掉 JSON 引擎
/// </code>
/// </example>
public static class AxiosContent
{
    private static readonly ConcurrentDictionary<string, IContentSerializer> Registry =
        new(StringComparer.Ordinal);

    private static IContentSerializer? _serializer;

    /// <summary>
    /// 全局内容序列化器。<b><c>null</c>（默认）= 未注入</b>，此时走自动模式：
    /// 按媒体类型查注册表（<see cref="Register"/>），查不到再用内置 JSON。
    /// <para>赋值为 <c>null</c> 可随时回到自动模式。</para>
    /// </summary>
    public static IContentSerializer? Serializer
    {
        get => Volatile.Read(ref _serializer);
        set => Volatile.Write(ref _serializer, value);
    }

    /// <summary>
    /// 为某个媒体类型注册序列化器（自动模式使用：未显式注入时按 <c>Content-Type</c> 匹配）。
    /// <para>
    /// 键是媒体类型主值：<c>; charset=...</c> 等参数、大小写都会被忽略；
    /// 同一个键重复注册以最后一次为准；<c>application/vnd.api+xml</c> 这类厂商类型
    /// 会回落到 <c>application/xml</c>，除非它自己也注册过（精确优先）。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException"><paramref name="mediaType"/> 为空。</exception>
    public static void Register(string mediaType, IContentSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        var key = MediaTypes.Normalize(mediaType)
            ?? throw new ArgumentException("媒体类型不能为空。", nameof(mediaType));

        Registry[key] = serializer;
    }

    /// <summary>取消某个媒体类型的注册（回到「未注册」，即自动模式回落内置 JSON）。</summary>
    /// <returns>是否确实删掉了一项。</returns>
    public static bool Unregister(string mediaType)
    {
        var key = MediaTypes.Normalize(mediaType);
        return key is not null && Registry.TryRemove(key, out _);
    }

    /// <summary>
    /// 取本次请求实际生效的序列化器：单次/实例注入 → 全局注入 → 按媒体类型自动匹配 → 内置 JSON。
    /// </summary>
    /// <param name="config">请求配置（读取单次/实例注入）。</param>
    /// <param name="mediaType">请求的 <see cref="AxiosConfig.ContentType"/> 或响应的 <c>Content-Type</c>。</param>
    internal static IContentSerializer Get(AxiosConfig config, string? mediaType)
        => config.ContentSerializer
            ?? Volatile.Read(ref _serializer)
            ?? Lookup(mediaType)
            ?? SystemTextJsonSerializer.Instance;

    /// <summary>
    /// 自动模式的查表：精确匹配 → 结构化后缀（<c>+xml</c> / <c>+json</c> …）回落到
    /// <c>application/&lt;suffix&gt;</c>。未注册返回 <c>null</c>，由调用方回落内置 JSON。
    /// </summary>
    private static IContentSerializer? Lookup(string? mediaType)
    {
        var main = MediaTypes.Normalize(mediaType);
        if (main is null)
        {
            return null;
        }

        if (Registry.TryGetValue(main, out var exact))
        {
            return exact;
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
