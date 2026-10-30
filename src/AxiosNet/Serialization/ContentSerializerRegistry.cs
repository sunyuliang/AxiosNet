using AxiosNet.Internal;

namespace AxiosNet.Serialization;

/// <summary>
/// 按媒体类型登记序列化器的表（每个 <see cref="AxiosNet.AxiosClient"/> 一份）。
/// 查表规则与全局 <see cref="AxiosNet.AxiosContent.Register"/> 一致：归一化后精确匹配 → 常见别名（text/xml、text/json）
/// → 结构化后缀（RFC 6839，<c>application/vnd.x+yaml</c> 回落到 <c>application/yaml</c>）。
/// 登记是写时复制，查表不加锁；本表只影响持有它的客户端，不改变其它实例与全局注册表。
/// 表对象随配置走：同一个 <see cref="AxiosNet.AxiosConfig"/> 被多个客户端使用、或被 <c>Clone()</c> 时，彼此共享这一张表。
/// </summary>
public sealed class ContentSerializerRegistry
{
    private static readonly IReadOnlyDictionary<string, IContentSerializer> Empty =
        new Dictionary<string, IContentSerializer>(0, StringComparer.Ordinal);

    private static readonly Dictionary<string, string> AliasTargets = new(StringComparer.Ordinal)
    {
        ["text/xml"] = AxiosContentType.Xml,
        ["text/json"] = AxiosContentType.Json
    };

    private readonly object _gate = new();
    private volatile IReadOnlyDictionary<string, IContentSerializer> _entries = Empty;

    /// <summary>已登记的类型数。</summary>
    public int Count => _entries.Count;

    /// <summary>为某个媒体类型登记序列化器；键忽略大小写与参数，重复登记以最后一次为准。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="serializer"/> 为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException"><paramref name="mediaType"/> 为空。</exception>
    public void Register(string mediaType, IContentSerializer serializer)
    {
        ArgumentNullException.ThrowIfNull(serializer);

        var key = MediaTypes.Normalize(mediaType)
            ?? throw new ArgumentException("媒体类型不能为空。", nameof(mediaType));

        lock (_gate)
        {
            var current = _entries;
            if (current.TryGetValue(key, out var existing) && ReferenceEquals(existing, serializer))
            {
                return;
            }

            var next = new Dictionary<string, IContentSerializer>(current, StringComparer.Ordinal) { [key] = serializer };
            _entries = next;
        }
    }

    /// <summary>取消某个媒体类型的登记（回到「未登记」，由更外层按序回落）。</summary>
    /// <returns>是否确实删掉了一项。</returns>
    public bool Unregister(string mediaType)
    {
        var key = MediaTypes.Normalize(mediaType);
        if (key is null)
        {
            return false;
        }

        lock (_gate)
        {
            var current = _entries;
            if (!current.ContainsKey(key))
            {
                return false;
            }

            var next = new Dictionary<string, IContentSerializer>(current, StringComparer.Ordinal);
            next.Remove(key);
            _entries = next;
            return true;
        }
    }

    /// <summary>清空本表的全部登记。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _entries = Empty;
        }
    }

    /// <summary>按媒体类型查表；未命中返回 <c>null</c>。</summary>
    internal IContentSerializer? Lookup(string? mediaType) => Lookup(_entries, mediaType);

    /// <summary>查表实现，全局注册表与实例注册表共用。</summary>
    internal static IContentSerializer? Lookup(IReadOnlyDictionary<string, IContentSerializer> entries, string? mediaType)
    {
        // 空表不做归一化。
        if (entries.Count == 0)
        {
            return null;
        }

        var main = MediaTypes.Normalize(mediaType);
        if (main is null)
        {
            return null;
        }

        if (entries.TryGetValue(main, out var exact))
        {
            return exact;
        }

        if (AliasTargets.TryGetValue(main, out var canonical) && entries.TryGetValue(canonical, out var aliased))
        {
            return aliased;
        }

        var plus = main.LastIndexOf('+');
        if (plus > 0 && plus < main.Length - 1
            && entries.TryGetValue("application/" + main[(plus + 1)..], out var structured))
        {
            return structured;
        }

        return null;
    }
}
