using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 部件声明（声明式层传入的字段名 / 文件名 / Content-Type）解析后的覆盖项；包装与所有权登记由
/// <see cref="MultipartBodyBuilder"/> 在装配时完成。
/// </summary>
/// <param name="FileName">声明的文件名；null 表示沿用值自带 / builder 默认。</param>
/// <param name="ContentType">声明的部件 Content-Type；null 表示沿用值自带 / builder 默认。</param>
/// <param name="HasExplicitName">字段名是否由声明显式指定（决定它是否压过 <see cref="FormFile.FieldName"/>）。</param>
internal readonly record struct PartOverride(
    string? FileName,
    string? ContentType,
    bool HasExplicitName)
{
    /// <summary>是否声明了任何覆盖项。</summary>
    public bool Any => HasExplicitName || FileName is not null || ContentType is not null;
}

/// <summary>
/// 声明式 multipart 表单：值保持调用方原样（<see cref="FormFile"/> / 流 / 字节 / 原值），
/// 声明覆盖按字段名单独登记，装配时由 <see cref="MultipartBodyBuilder"/> 逐项取出应用。
/// </summary>
internal sealed class DeclaredForm : Dictionary<string, object?>
{
    /// <summary>字段名 → 声明覆盖（只登记确实声明了覆盖的项）。</summary>
    public Dictionary<string, PartOverride> Overrides { get; } = new(StringComparer.Ordinal);

    /// <summary>数据来源的请求模型名：装配期错误用它做上下文（普通字典没有这个名字）。</summary>
    public string? SourceName { get; init; }
}

/// <summary>
/// multipart 部件的所有权回收：装配中途失败时释放已取得所有权的流 / 内容（含嵌套在字典、集合、对象里的）。
/// 单项释放异常只放弃该项，不中断其余部件，也不遮蔽装配的原始异常。
/// </summary>
internal static class MultipartOwnership
{
    /// <summary>回收一组表单值（装配前的表单字典）。</summary>
    public static void DisposeOwned(IEnumerable<object?> values)
    {
        // 按引用记录已访问，避免自引用字典无限递归。
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var owned = new List<IDisposable>();

        foreach (var value in values)
        {
            Collect(value, visited, owned);
        }

        Release(owned);
    }

    /// <summary>回收一个根对象（装配入口只拿得到 <c>data</c> 本身，见 <see cref="MultipartBodyBuilder.Create"/>）。</summary>
    public static void DisposeOwned(object? value)
    {
        var owned = new List<IDisposable>();

        Collect(value, new HashSet<object>(ReferenceEqualityComparer.Instance), owned);
        Release(owned);
    }

    /// <summary>
    /// 把 <paramref name="value"/> 里的部件收进 <paramref name="owned"/>（只读遍历，不释放）。
    /// </summary>
    private static void Collect(object? value, HashSet<object> visited, List<IDisposable> owned)
    {
        try
        {
            Walk(value, visited, stream => owned.Add(stream), content => owned.Add(content));
        }
        catch
        {
            // 遍历本身失败（属性 getter 抛等）：已经收集到的部件仍然要释放
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
                // 单个部件释放失败不连累其余部件。
            }
        }
    }

    /// <summary>
    /// 遍历一个根对象里「随正文一起发出去」的流（<see cref="FormFile.Content"/> 与裸 <see cref="Stream"/>）
    /// 以及调用方自带的 <see cref="HttpContent"/> 部件；<paramref name="visitContent"/> 传 <c>null</c> 表示只关心裸流。
    /// </summary>
    public static void VisitStreams(object? value, Action<Stream> visit, Action<HttpContent>? visitContent = null)
        => Walk(value, new HashSet<object>(ReferenceEqualityComparer.Instance), visit, visitContent);

    /// <summary>
    /// 对象图遍历本体：叶子上分别回调「流」与「<see cref="HttpContent"/>」，容器继续下钻。
    /// </summary>
    // 反射读属性：运行时类型无法静态标注。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "multipart 值的运行时类型无法静态标注；AOT 下需由消费方 root 模型程序集（README「Native AOT / 裁剪」）。")]
    private static void Walk(
        object? value,
        HashSet<object> visited,
        Action<Stream> onStream,
        Action<HttpContent>? onContent)
    {
        switch (value)
        {
            case null:
            // string 也是 IEnumerable<char>；byte[] 按二进制内容处理
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

            // 字典：声明覆盖整体透传，值里的 FormFile / 裸流仍然是库的所有权
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

            // 只实现 IReadOnlyDictionary 的字典：builder 同样按 a[b] 展平
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

            // 自定义只读字典 / 仅泛型字典：能安全枚举的才按键值对下钻（拿不准的形态留给下面的集合通道）。
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

            // 集合 / 迭代器（含非泛型 ArrayList 等）：逐项回收
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

        // 嵌套对象：按对象形状下钻回收；AOT 下没有形状就停止（回收路径尽力而为，不抛错）。
        var type = value.GetType();
        if (BodyFields.IsScalar(type) || !visited.Add(value) || !ShapeCache.TryGet(type, out var shape))
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
