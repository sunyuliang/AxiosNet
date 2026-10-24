using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 部件声明（声明式层传入的字段名 / 文件名 / Content-Type）解析后的覆盖项。
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
    /// <summary>是否声明了任何覆盖项：决定要不要在装配前校验、要不要把它登记到 <see cref="DeclaredForm.Overrides"/>。</summary>
    public bool Any => HasExplicitName || FileName is not null || ContentType is not null;
}

/// <summary>
/// 声明式 multipart 表单：值保持调用方原样（<see cref="FormFile"/> / 流 / 字节 / 原值），
/// 声明覆盖按字段名单独登记，装配时由 <see cref="MultipartBodyBuilder"/> 逐项取出应用。
/// <para>
/// 用字典子类型而不是包装值：公开的 <c>config.Data</c> 里看到的仍是原始值
/// （<c>Configure</c> / 拦截器 / <c>transformRequest</c> 都按普通表单字典处理）。
/// </para>
/// </summary>
internal sealed class DeclaredForm : Dictionary<string, object?>
{
    /// <summary>字段名 → 声明覆盖（只登记确实声明了覆盖的项）。</summary>
    public Dictionary<string, PartOverride> Overrides { get; } = new(StringComparer.Ordinal);

    /// <summary>数据来源的请求模型名：装配期错误用它做上下文（普通字典没有这个名字）。</summary>
    public string? SourceName { get; init; }
}

/// <summary>
/// multipart 部件的所有权回收。
/// <para>
/// 装配中途失败时，已经取得所有权的流 / 内容（含字典、集合、迭代器、嵌套对象里的）必须释放，
/// 否则异常路径会永久泄漏 <see cref="FileStream"/> 句柄。这里是<b>唯一</b>实现：模型装配
/// （<c>RequestModelMetadata.ResolveBody</c> 的失败回滚）与 <see cref="MultipartBodyBuilder"/>
/// 的装配回滚共用。
/// </para>
/// <para>
/// 遍历范围与 <see cref="MultipartBodyBuilder"/> 的展平规则对齐：字典（含 <see cref="DeclaredForm"/>）、
/// 只读字典、集合、嵌套对象都要递归进去。只看 <see cref="IEnumerable{T}"/> 会整本跳过字典 ——
/// 它的元素是 KeyValuePair 值类型，泛型协变不成立
/// （<c>Dictionary&lt;string, FormFile&gt;</c> 并不是 <c>IEnumerable&lt;object?&gt;</c>）。
/// </para>
/// <para>
/// 清理自身不允许失败：单项释放异常只放弃该项，既不中断其余部件，也不遮蔽装配的原始异常。
/// </para>
/// </summary>
internal static class MultipartOwnership
{
    /// <summary>回收一组表单值（装配前的表单字典）。</summary>
    public static void DisposeOwned(IEnumerable<object?> values)
    {
        // 字典可以自引用（d["self"] = d）：按引用记录已访问，避免清理阶段无限递归。
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        foreach (var value in values)
        {
            DisposeValue(value, visited);
        }
    }

    /// <summary>
    /// 回收一个根对象（装配入口只拿得到 <c>data</c> 本身，见 <see cref="MultipartBodyBuilder.Create"/>）：
    /// 装配在中间某个部件失败时，后面还没轮到装配的流同样已经交过所有权。
    /// </summary>
    public static void DisposeOwned(object? value) =>
        DisposeValue(value, new HashSet<object>(ReferenceEqualityComparer.Instance));

    // 反射读属性：根对象/嵌套对象的运行时类型无法静态标注（理由同 MultipartBodyBuilder.CollectParts）。
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "multipart 值的运行时类型无法静态标注；AOT 下需由消费方 root 模型程序集（README「Native AOT / 裁剪」）。")]
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
                case JsonElement:
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

            // 嵌套对象：装配按 public 可读属性展平成部件，回收也得进到属性里，
            // 否则「根对象是匿名类型/POCO」时属性上的流整片漏掉。
            var type = value.GetType();
            if (BodyFields.IsScalar(type) || !visited.Add(value))
            {
                return;
            }

            foreach (var property in ReflectionCache.GetReadableProperties(type))
            {
                if (!BodyFields.IsIgnored(property))
                {
                    DisposeValue(property.GetValue(value), visited);
                }
            }
        }
        catch
        {
            // 清理阶段吞掉异常：调用方看到的是装配失败的原始异常，且其余部件继续释放
        }
    }
}
