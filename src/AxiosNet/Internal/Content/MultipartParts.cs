using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AxiosNet.Internal.Content;

/// <summary>multipart 部件的所有权回收：装配中途失败时释放已取得所有权的流 / 内容（含嵌套在容器里的）。</summary>
/// <remarks>单项释放异常只放弃该项，不中断其余部件，也不遮蔽装配的原始异常。</remarks>
internal static class MultipartOwnership
{
    /// <summary>回收一组表单值（装配前的表单字典）。</summary>
    public static void DisposeOwned(IEnumerable<object?> values)
    {
        // 按引用记录已访问。
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var owned = new List<IDisposable>();

        foreach (var value in values)
        {
            Collect(value, visited, owned);
        }

        Release(owned);
    }

    /// <summary>回收一个根对象。</summary>
    public static void DisposeOwned(object? value)
    {
        var owned = new List<IDisposable>();

        Collect(value, new HashSet<object>(ReferenceEqualityComparer.Instance), owned);
        Release(owned);
    }

    /// <summary>把 <paramref name="value"/> 里的部件收进 <paramref name="owned"/>（只读遍历，不释放）。</summary>
    private static void Collect(object? value, HashSet<object> visited, List<IDisposable> owned)
    {
        try
        {
            Walk(value, visited, stream => owned.Add(stream), content => owned.Add(content));
        }
        catch
        {
            // 遍历失败时已收集的部件仍然要释放。
        }
    }

    /// <summary>逐个释放，每个部件独立兜异常。</summary>
    private static void Release(List<IDisposable> owned)
    {
        foreach (var item in owned)
        {
            try
            {
                item.Dispose();
            }
            catch
            {
                // 单个部件释放失败不连累其余。
            }
        }
    }

    /// <summary>遍历根对象里随正文发出的流与调用方自带的 <see cref="HttpContent"/> 部件；<paramref name="visitContent"/> 为 <c>null</c> 表示只关心裸流。</summary>
    public static void VisitStreams(object? value, Action<Stream> visit, Action<HttpContent>? visitContent = null)
        => Walk(value, new HashSet<object>(ReferenceEqualityComparer.Instance), visit, visitContent);

    /// <summary>对象图遍历本体：叶子上分别回调流与 <see cref="HttpContent"/>，容器继续下钻。</summary>
    // 反射读属性：运行时类型无法静态标注。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "multipart 值的运行时类型无法静态标注；AOT 下需由消费方 root 模型程序集（README「运行时与 AOT」）。")]
    private static void Walk(
        object? value,
        HashSet<object> visited,
        Action<Stream> onStream,
        Action<HttpContent>? onContent)
    {
        switch (value)
        {
            case null:
            // string 也是 IEnumerable<char>；byte[] 按二进制内容处理。
            case string _:
            case byte[] _:
            case JsonElement:
                return;

            case FormFile file:
                onStream(file.Content);
                return;

            case HttpContent content:
                onContent?.Invoke(content);
                return;

            case Stream stream:
                onStream(stream);
                return;

            // 字典：值里的 FormFile / 裸流仍归库所有。
            case IDictionary dictionary:
                if (!visited.Add(dictionary))
                {
                    return;
                }

                foreach (DictionaryEntry entry in dictionary)
                {
                    Walk(entry.Value, visited, onStream, onContent);
                }

                return;

            // 只实现 IReadOnlyDictionary 的字典：同样按 a[b] 展平。
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
                if (!visited.Add(readOnlyDictionary))
                {
                    return;
                }

                foreach (var entry in readOnlyDictionary)
                {
                    Walk(entry.Value, visited, onStream, onContent);
                }

                return;

            // 自定义只读字典 / 仅泛型字典：能安全枚举的才下钻。
            case IEnumerable map when QueryMap.CanEnumerateBestEffort(map):
                if (!visited.Add(map))
                {
                    return;
                }

                foreach (var entry in QueryMap.Enumerate(map))
                {
                    Walk(entry.Value, visited, onStream, onContent);
                }

                return;

            // 集合 / 迭代器：逐项回收。
            case IEnumerable items:
                if (!visited.Add(value))
                {
                    return;
                }

                foreach (var item in items)
                {
                    Walk(item, visited, onStream, onContent);
                }

                return;
        }

        // 嵌套对象：按对象形状下钻；AOT 下没有形状就停止。
        var type = value.GetType();
        if (ScalarTypes.IsScalarType(type) || !visited.Add(value) || !ShapeCache.TryGet(type, out var shape))
        {
            return;
        }

        foreach (var member in shape.Members)
        {
            if (!member.Ignored)
            {
                Walk(member.GetValue(value), visited, onStream, onContent);
            }
        }
    }
}
