using System.Collections;
using System.Text.Json;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// <see cref="PartAttribute"/> 的值规范化：把「声明了文件名 / Content-Type 的文件类值」
/// 统一包装成 <see cref="FormFile"/>，其余值原样交给 <see cref="AxiosNet.Internal.MultipartBodyBuilder"/> 的既有规则
/// （标量 → 字段、集合 → 同名重复部件、嵌套对象 → <c>a[b]</c>）。
/// <para>
/// 名字优先级：[Part(Name)] &gt; FormFile.FieldName &gt; 默认名（[JsonPropertyName] / 属性名，再经 FormFieldNameSelector）。
/// 因此绑定名只在显式声明时写进 FormFile.FieldName，否则留给值自带的名字或 builder 的字段名。
/// </para>
/// <para>规范化不递归进嵌套对象 / 字典：部件名由 builder 的展平规则处理，特性声明只作用于绑定的值本身。</para>
/// </summary>
internal static class MultipartPartValues
{
    public static object? Normalize(
        object? value,
        string bindingName,
        bool hasExplicitName,
        string? fileName,
        string? contentType)
    {
        if (value is null)
        {
            return null;
        }

        // FormFile 的 FileName / ContentType / FieldName 是值层默认，绑定上的 [Part] 是声明层，后者优先。
        // 新旧 FormFile 共享同一个流：所有权只交给请求一次（旧实例随即被丢弃，不会二次释放）。
        if (value is FormFile file)
        {
            var mergedFileName = fileName ?? file.FileName;
            var mergedContentType = contentType ?? file.ContentType;
            var mergedFieldName = hasExplicitName ? bindingName : file.FieldName;

            var unchanged = mergedFileName == file.FileName
                && mergedContentType == file.ContentType
                && mergedFieldName == file.FieldName;

            if (unchanged)
            {
                return file;
            }

            try
            {
                return new FormFile(mergedFileName, file.Content, mergedContentType) { FieldName = mergedFieldName };
            }
            catch
            {
                // 声明的文件名 / 类型 / 字段名非法：包装失败时流的所有权已经收不回去，就地释放
                file.Content.Dispose();
                throw;
            }
        }

        // 未声明文件名 / 类型时原样透传：Stream / byte[] 的默认形态（filename = 字段名、octet-stream）由 builder 决定
        if (fileName is null && contentType is null)
        {
            return value;
        }

        // 字典不可能是单个文件：整体透传给 builder 按 a[b] 展平（否则会落进下面的集合分支被拆成 Key/Value 对象）。
        // 只实现 IReadOnlyDictionary 的自研只读视图同样命中 builder 的字典分支，不能当集合拆。
        if (value is IDictionary || value is IReadOnlyDictionary<string, object?>)
        {
            return value;
        }

        switch (value)
        {
            case Stream stream:
                try
                {
                    return new FormFile(fileName ?? bindingName, stream, contentType)
                    {
                        FieldName = hasExplicitName ? bindingName : null
                    };
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }

            case byte[] bytes:
                return new FormFile(fileName ?? bindingName, bytes, contentType)
                {
                    FieldName = hasExplicitName ? bindingName : null
                };

            case ReadOnlyMemory<byte> memory:
                return new FormFile(fileName ?? bindingName, memory.ToArray(), contentType)
                {
                    FieldName = hasExplicitName ? bindingName : null
                };

            case Memory<byte> memory:
                return new FormFile(fileName ?? bindingName, memory.ToArray(), contentType)
                {
                    FieldName = hasExplicitName ? bindingName : null
                };

            case ArraySegment<byte> segment:
                return new FormFile(fileName ?? bindingName, segment.ToArray(), contentType)
                {
                    FieldName = hasExplicitName ? bindingName : null
                };
        }

        // 字符串 / 标量永远是普通字段（文本文件的文本形态由调用方用 FormFile / byte[] 表达）
        if (value is string || value is JsonElement)
        {
            return value;
        }

        if (value is IEnumerable items)
        {
            // 集合逐项规范化 → 同名重复部件
            var normalized = new List<object?>();
            try
            {
                foreach (var item in items)
                {
                    normalized.Add(Normalize(item, bindingName, hasExplicitName, fileName, contentType));
                }
            }
            catch
            {
                // 枚举 / 后续项规范化失败：前面已包装的流必须回收（这个列表还没交给 builder）
                DisposeOwned(normalized);
                throw;
            }

            return normalized;
        }

        return value;
    }

    /// <summary>
    /// 装配中途失败时的回滚：<see cref="Normalize"/> 已经把调用方的流包进 FormFile（尚未交给 builder），
    /// 或者把裸 Stream / 字典 / 集合原样透传给了表单，这些部件必须在这里释放，
    /// 否则异常路径会永久泄漏 FileStream 句柄。
    /// <para>
    /// 遍历范围与 <c>MultipartBodyBuilder</c> 对齐：字典、只读字典、集合都要递归进去。
    /// 只看 <see cref="IEnumerable{T}"/> 会整本跳过字典 —— 它的元素是 KeyValuePair 值类型，
    /// 泛型协变不成立（<c>Dictionary&lt;string, FormFile&gt;</c> 并不是 <c>IEnumerable&lt;object?&gt;</c>）。
    /// </para>
    /// <para>
    /// 清理自身不允许失败：单项释放异常只放弃该项，既不中断其余部件，也不遮蔽装配的原始异常。
    /// </para>
    /// </summary>
    public static void DisposeOwned(IEnumerable<object?> values)
    {
        // 字典可以自引用（d["self"] = d）：按引用记录已访问，避免清理阶段无限递归。
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var value in values)
        {
            DisposeValue(value, visited);
        }
    }

    private static void DisposeValue(object? value, HashSet<object> visited)
    {
        try
        {
            switch (value)
            {
                case null:
                // string 也是 IEnumerable<char>；byte[] 是二进制内容，逐字节枚举纯属浪费
                case string _:
                case byte[] _:
                    return;

                case FormFile file:
                    file.Content.Dispose();
                    return;

                case HttpContent content:
                    content.Dispose();
                    return;

                case Stream stream:
                    stream.Dispose();
                    return;

                // 字典：Normalize 整体透传，值里的 FormFile / 裸流仍然是库的所有权
                case IDictionary dictionary:
                    if (!visited.Add(dictionary))
                    {
                        return;
                    }

                    foreach (DictionaryEntry entry in dictionary)
                    {
                        DisposeValue(entry.Value, visited);
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
                        DisposeValue(entry.Value, visited);
                    }

                    return;

                // 集合 / 迭代器（含非泛型 ArrayList 等）：Normalize 的集合产物与裸透传都在这里逐项回收
                case IEnumerable items:
                    if (!visited.Add(value))
                    {
                        return;
                    }

                    foreach (var item in items)
                    {
                        DisposeValue(item, visited);
                    }

                    return;
            }
        }
        catch
        {
            // 清理阶段吞掉异常：调用方看到的是装配失败的原始异常，且其余部件继续释放
        }
    }
}
