using System.Collections;

namespace AxiosNet.Internal;

/// <summary>
/// 部件声明层（<c>[Part]</c>）解析后的覆盖项。
/// <para>
/// 只描述「声明了什么」，不描述「怎么包装」：真正的 FormFile 合并、文件部件包装与所有权登记
/// 全部由 <see cref="MultipartBodyBuilder"/> 在装配时完成 —— 因此失败路径的释放点也只有一处。
/// </para>
/// </summary>
/// <param name="FileName">声明的文件名；null 表示沿用值自带 / builder 默认。</param>
/// <param name="ContentType">声明的部件 Content-Type；null 表示沿用值自带 / builder 默认。</param>
/// <param name="HasExplicitName">字段名是否由声明显式指定（决定它是否压过 <see cref="FormFile.FieldName"/>）。</param>
internal readonly record struct PartOverride(
    string? FileName,
    string? ContentType,
    bool HasExplicitName)
{
    /// <summary>是否声明了任何覆盖项：决定要不要包装成 <see cref="PartValue"/>、要不要在装配前校验。</summary>
    public bool Any => HasExplicitName || FileName is not null || ContentType is not null;
}

/// <summary>
/// 带声明覆盖的部件值：包装由 builder 完成，值本身仍是调用方/模型提供的原始对象。
/// <para>只用于「确实有声明」的绑定；没有声明的值原样放进表单字典，避免多余装箱。</para>
/// </summary>
internal readonly record struct PartValue(object? Value, PartOverride Override);

/// <summary>
/// multipart 部件的所有权回收。
/// <para>
/// 装配中途失败时，已经取得所有权的流 / 内容（含字典、集合、迭代器里的）必须释放，
/// 否则异常路径会永久泄漏 <see cref="FileStream"/> 句柄。这里是<b>唯一</b>实现：
/// 模型装配（<c>RequestModelMetadata.ResolveBody</c> 的失败回滚）与 builder 的既有回滚共用。
/// </para>
/// <para>
/// 遍历范围与 <see cref="MultipartBodyBuilder"/> 的展平规则对齐：<see cref="PartValue"/>、字典、
/// 只读字典、集合都要递归进去。只看 <see cref="IEnumerable{T}"/> 会整本跳过字典 ——
/// 它的元素是 KeyValuePair 值类型，泛型协变不成立
/// （<c>Dictionary&lt;string, FormFile&gt;</c> 并不是 <c>IEnumerable&lt;object?&gt;</c>）。
/// </para>
/// <para>
/// 清理自身不允许失败：单项释放异常只放弃该项，既不中断其余部件，也不遮蔽装配的原始异常。
/// </para>
/// </summary>
internal static class MultipartOwnership
{
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

                // 声明覆盖只是包装前的描述，真正的所有权在包装后的值上
                case PartValue declared:
                    DisposeValue(declared.Value, visited);
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

                // 字典：声明覆盖整体透传，值里的 FormFile / 裸流仍然是库的所有权
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

                // 集合 / 迭代器（含非泛型 ArrayList 等）：逐项回收
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
