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

        // 字典不可能是单个文件：整体透传给 builder 按 a[b] 展平（否则会落进下面的集合分支被拆成 Key/Value 对象）
        if (value is IDictionary)
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
    /// 这些部件必须在这里释放，否则异常路径会永久泄漏 FileStream 句柄。
    /// </summary>
    public static void DisposeOwned(IEnumerable<object?> values)
    {
        foreach (var value in values)
        {
            switch (value)
            {
                case FormFile file:
                    file.Content.Dispose();
                    break;

                case HttpContent content:
                    content.Dispose();
                    break;

                // 未声明 FileName/ContentType 的裸 Stream 由 Normalize 原样透传（尚未被 builder 包成 StreamContent），
                // 容器的 Dispose 同样覆盖不到：模型里的流从装配开始就归库，失败路径必须在这里关掉。
                case Stream stream:
                    stream.Dispose();
                    break;

                // Normalize 的集合产物：逐项回收
                case IEnumerable<object?> items:
                    DisposeOwned(items);
                    break;
            }
        }
    }
}
