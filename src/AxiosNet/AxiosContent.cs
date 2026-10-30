using System.Collections.Concurrent;
using AxiosNet.Internal;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>
/// 请求体 / 响应体的内容格式入口，内置 JSON（默认）与 XML 两个实现。
/// 选择优先级：单次请求 / 实例的 <see cref="AxiosConfig.SerializationSettings.ContentSerializer"/>
/// → 实例注册表（<see cref="AxiosClient.ContentSerializers"/>）→ 全局 <see cref="Serializer"/>
/// → 按媒体类型查 <see cref="Register"/> → 内置 JSON。
/// 请求方向按 <see cref="AxiosConfig.ContentType"/> 查表，响应方向按响应的 <c>Content-Type</c> 查表。
/// </summary>
/// <remarks>
/// 全局注册表是进程级的（测试之间会互相影响）；按实例隔离请用 <see cref="AxiosClient.ContentSerializers"/>。
/// </remarks>
public static class AxiosContent
{
    private static readonly ConcurrentDictionary<string, IContentSerializer> Registry = new(StringComparer.Ordinal);

    /// <summary>注册表条目数，只用于 <see cref="Lookup"/> 的空表快路径判定。</summary>
    private static int _registeredCount;

    private static IContentSerializer? _serializer;

    /// <summary>
    /// 全局内容序列化器。<c>null</c>（默认）= 未注入，此时按媒体类型自动匹配；赋 <c>null</c> 可回到自动模式。
    /// </summary>
    public static IContentSerializer? Serializer
    {
        get => Volatile.Read(ref _serializer);
        set => Volatile.Write(ref _serializer, value);
    }

    /// <summary>为某个媒体类型注册序列化器（自动模式使用）；键忽略大小写与参数，重复注册以最后一次为准。</summary>
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

    /// <summary>取消某个媒体类型的注册（回到「未注册」，即回落内置 JSON）。</summary>
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

    /// <summary>取本次请求实际生效的序列化器：单次/实例注入 → 实例注册表 → 全局注入 → 全局注册表 → 内置 JSON。</summary>
    /// <param name="config">请求配置（读取单次/实例注入与实例注册表）。</param>
    /// <param name="mediaType">请求的 <see cref="AxiosConfig.ContentType"/> 或响应的 <c>Content-Type</c>。</param>
    internal static IContentSerializer Get(AxiosConfig config, string? mediaType)
    {
        var serialization = config.SerializationOrNull;

        return serialization?.ContentSerializer
            // 实例登记比全局兜底更具体：显式登记了某格式就按格式走，不受全局 Serializer 压住。
            ?? serialization?.ContentSerializers?.Lookup(mediaType)
            ?? Volatile.Read(ref _serializer)
            ?? Lookup(mediaType)
            ?? SystemTextJsonSerializer.Instance;
    }

    /// <summary>全局注册表的查表：精确匹配 → 别名 → 结构化后缀回落；未注册返回 <c>null</c>。</summary>
    private static IContentSerializer? Lookup(string? mediaType)
        // 注册表为空时直接返回，不做归一化。
        => Volatile.Read(ref _registeredCount) == 0
            ? null
            : ContentSerializerRegistry.Lookup(Registry, mediaType);
}
