using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// <c>multipart/form-data</c> 通道：标量 / 嵌套对象 → 普通字段，
/// <see cref="FormFile"/> / Stream / byte[] / HttpContent → 文件部件；
/// 数组 / 集合展开为同名重复部件。
/// <para>
/// <see cref="PartValue"/> 携带的声明覆盖（<c>[Part]</c> 的字段名 / 文件名 / Content-Type）
/// 在装配时由本类应用：包装、校验与所有权登记都只有这一处。
/// </para>
/// </summary>
internal static class MultipartBodyBuilder
{
    private const string OctetStreamMediaType = AxiosContentType.OctetStream;

    public static HttpContent Create(
        object data,
        string mediaType,
        Encoding encoding,
        AxiosConfig config)
    {
        // 用户显式声明 boundary 时沿用（部分服务端会校验），否则由 MultipartFormDataContent 生成随机边界。
        // 构造放进 try：BCL 对非法 boundary（超长 / 含限定字符集之外的字符）抛 ArgumentException，
        // 那与「字段名非法」同属调用方选项错误，统一成带配置上下文的 ERR_BAD_OPTION_VALUE
        // （放在 try 外会被 AxiosClient 收成 ERR_BAD_REQUEST「请求体序列化失败」，分类错位）。
        MultipartFormDataContent content;
        try
        {
            var boundary = RequestContentType.ExtractBoundary(mediaType);
            content = boundary is null
                ? new MultipartFormDataContent()
                : new MultipartFormDataContent(boundary);
        }
        catch (ArgumentException ex)
        {
            throw AxiosException.InvalidOption(
                $"multipart boundary 非法：{ex.Message}"
                + "（RFC 2046：boundary 不超过 70 字符，且只含限定字符集；不指定时由库自动生成）。",
                config,
                ex);
        }

        try
        {
            CollectParts(null, data, content, encoding, config.SerializationOrNull?.FormFieldNameSelector, visited: null);
            return content;
        }
        catch (Exception ex)
        {
            // 构建中途失败：已加入的部件（含已打开的 FileStream）必须释放，避免句柄泄漏。
            content.Dispose();

            // 字段名 / 声明值非法属于调用方选项错误：换成带配置上下文的 ERR_BAD_OPTION_VALUE。
            if (ex is InvalidMultipartOptionException invalid)
            {
                throw AxiosException.InvalidOption(invalid.Message, config, invalid);
            }

            // 环检测：无界递归会栈溢出（不可捕获），转成带配置的选项错误。
            if (ex is DataCycleException cycle)
            {
                throw AxiosException.InvalidOption(cycle.Message, config, cycle);
            }

            throw;
        }
    }

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "multipart 值的运行时类型无法静态标注；AOT 下需由消费方 root 模型程序集（README「Native AOT / 裁剪」）。")]
    private static void CollectParts(
        string? name,
        object? value,
        MultipartFormDataContent content,
        Encoding encoding,
        Func<string?, string, string>? nameSelector,
        HashSet<object>? visited,
        PartOverride partOverride = default)
    {
        // 声明覆盖（[Part]）：包装与所有权都在这里完成，组装前不预先改写值
        if (value is PartValue declared)
        {
            CollectParts(name, declared.Value, content, encoding, nameSelector, visited, declared.Override);
            return;
        }

        value = ApplyPartOverride(name, value, partOverride);

        switch (value)
        {
            case null:
                return;

            case FormFile file:
            {
                // 先取所有权再校验字段名：FormFile.Content 的契约是「随请求释放」，
                // 失败路径也一样 —— 否则名字写错就等于把一个已打开的 FileStream 永久留在调用方手里。
                var part = new StreamContent(file.Content);
                try
                {
                    var field = RequireName(file.FieldName ?? name);
                    RequestContentType.Set(part, file.ContentType ?? OctetStreamMediaType);
                    content.Add(part, field, file.FileName);
                }
                catch
                {
                    // 部件还没加入容器，容器的 Dispose 覆盖不到它：就地释放才是唯一机会
                    part.Dispose();
                    throw;
                }

                return;
            }

            case HttpContent part:
            {
                // 调用方自带部件内容：Add 在部件已带 Content-Disposition 时完全不碰它，
                // 因此这里只补缺失的 Name，其余（尤其 filename）原样保留。
                string field;
                try
                {
                    field = RequireName(name);
                }
                catch
                {
                    // 同上：交进来的内容按「随请求释放」处理，装配失败不能把释放责任退回调用方
                    part.Dispose();
                    throw;
                }

                AddUserPart(content, part, field);
                return;
            }

            case JsonElement element:
                CollectJsonElementParts(name, element, content, encoding, nameSelector);
                return;

            case string text:
                content.Add(new StringContent(text, encoding), RequireName(name));
                return;

            case byte[] bytes:
                AddFilePart(content, name, new ByteArrayContent(bytes));
                return;

            case ReadOnlyMemory<byte> memory:
                AddFilePart(content, name, new ReadOnlyMemoryContent(memory));
                return;

            case Memory<byte> memory:
                AddFilePart(content, name, new ReadOnlyMemoryContent(memory));
                return;

            case ArraySegment<byte> segment:
                AddFilePart(content, name, new ReadOnlyMemoryContent(segment.AsMemory()));
                return;

            case Stream stream:
                AddFilePart(content, name, new StreamContent(stream));
                return;

            case IDictionary<string, object?> genericDictionary:
            {
                // 含 ExpandoObject（dynamic）
                visited = ObjectGraph.Enter(visited, genericDictionary);
                try
                {
                    foreach (var entry in genericDictionary)
                    {
                        CollectParts(
                            BodyFields.CombineName(nameSelector, name, entry.Key),
                            entry.Value,
                            content,
                            encoding,
                            nameSelector,
                            visited);
                    }
                }
                finally
                {
                    ObjectGraph.Exit(visited, genericDictionary);
                }

                return;
            }

            // 只实现 IReadOnlyDictionary 的字典（如自研只读视图）不命中上面的泛型字典分支，
            // 落进 IEnumerable 会把每个 KeyValuePair 当 POCO 反射成 Key/Value 两个字段。
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
            {
                visited = ObjectGraph.Enter(visited, readOnlyDictionary);
                try
                {
                    foreach (var entry in readOnlyDictionary)
                    {
                        CollectParts(
                            BodyFields.CombineName(nameSelector, name, entry.Key),
                            entry.Value,
                            content,
                            encoding,
                            nameSelector,
                            visited);
                    }
                }
                finally
                {
                    ObjectGraph.Exit(visited, readOnlyDictionary);
                }

                return;
            }

            case IDictionary dictionary:
            {
                visited = ObjectGraph.Enter(visited, dictionary);
                try
                {
                    foreach (DictionaryEntry entry in dictionary)
                    {
                        if (entry.Key is null)
                        {
                            continue;
                        }

                        CollectParts(
                            BodyFields.CombineName(nameSelector, name, entry.Key.ToString() ?? string.Empty),
                            entry.Value,
                            content,
                            encoding,
                            nameSelector,
                            visited);
                    }
                }
                finally
                {
                    ObjectGraph.Exit(visited, dictionary);
                }

                return;
            }
        }

        var type = value.GetType();
        if (BodyFields.IsScalar(type))
        {
            content.Add(new StringContent(BodyFields.ScalarToString(value), encoding), RequireName(name));
            return;
        }

        if (value is IEnumerable enumerable)
        {
            visited = ObjectGraph.Enter(visited, value);
            try
            {
                foreach (var item in enumerable)
                {
                    // 集合逐项应用同一份声明（同名重复部件）
                    CollectParts(name, item, content, encoding, nameSelector, visited, partOverride);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, value);
            }

            return;
        }

        // 嵌套 POCO：public 可读属性；命名只看 [JsonPropertyName] 与属性名，忽略看 [JsonIgnore]。
        // 刻意不读 JsonSerializerOptions.PropertyNamingPolicy：表单与序列化器解耦。
        visited = ObjectGraph.Enter(visited, value);
        try
        {
            foreach (var property in ReflectionCache.GetReadableProperties(type))
            {
                if (BodyFields.IsIgnored(property))
                {
                    continue;
                }

                CollectParts(
                    BodyFields.CombineName(nameSelector, name, BodyFields.ResolveFieldName(property)),
                    property.GetValue(value),
                    content,
                    encoding,
                    nameSelector,
                    visited);
            }
        }
        finally
        {
            ObjectGraph.Exit(visited, value);
        }
    }

    /// <summary>JSON 树按树语义展开：对象按键名、数组合并成同名重复部件、null / undefined 跳过。</summary>
    private static void CollectJsonElementParts(
        string? name,
        JsonElement element,
        MultipartFormDataContent content,
        Encoding encoding,
        Func<string?, string, string>? nameSelector)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    CollectJsonElementParts(
                        BodyFields.CombineName(nameSelector, name, property.Name),
                        property.Value,
                        content,
                        encoding,
                        nameSelector);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectJsonElementParts(name, item, content, encoding, nameSelector);
                }

                break;

            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                content.Add(new StringContent(BodyFields.ScalarText(element), encoding), RequireName(name));
                break;

            default:
                // null / undefined：跳过
                break;
        }
    }

    /// <summary>
    /// 应用声明层覆盖（<c>[Part]</c>）：<see cref="FormFile"/> 合并文件名 / 类型 / 字段名；
    /// 裸文件类值（Stream / byte[] / Memory / ArraySegment）按声明包成 <see cref="FormFile"/>；
    /// 集合容器本身不包装，逐项在递归里应用同一份声明；字典 / 嵌套对象 / 字符串 / <see cref="HttpContent"/>
    /// 不受影响（由各自的展平规则处理）。
    /// </summary>
    /// <remarks>
    /// 名字优先级：显式声明 &gt; <see cref="FormFile.FieldName"/> &gt; 字段名（[JsonPropertyName] / 属性名，
    /// 再经 <c>FormFieldNameSelector</c>）。声明的值由 <see cref="FormFile"/> 在构造期校验，
    /// 包装失败时就地释放已取得所有权的流 —— 与 builder 其余失败路径一致。
    /// </remarks>
    private static object? ApplyPartOverride(string? name, object? value, PartOverride partOverride)
    {
        if (value is null)
        {
            return null;
        }

        if (value is FormFile file)
        {
            // 值自带声明（FileName / ContentType / FieldName）是默认值，[Part] 是声明层，后者优先。
            // 新旧 FormFile 共享同一个流：所有权只交给请求一次（旧实例随即被丢弃，不会二次释放）。
            var mergedFileName = partOverride.FileName ?? file.FileName;
            var mergedContentType = partOverride.ContentType ?? file.ContentType;
            var mergedFieldName = partOverride.HasExplicitName ? name : file.FieldName;

            if (mergedFileName == file.FileName
                && mergedContentType == file.ContentType
                && mergedFieldName == file.FieldName)
            {
                return file;
            }

            try
            {
                return new FormFile(mergedFileName, file.Content, mergedContentType) { FieldName = mergedFieldName };
            }
            catch (ArgumentException ex)
            {
                // 声明的文件名 / 类型 / 字段名非法：包装失败时流的所有权已经收不回去，就地释放
                file.Content.Dispose();
                throw DeclaredValueError(ex);
            }
        }

        // 未声明文件名 / 类型时原样透传：Stream / byte[] 的默认形态（filename = 字段名、octet-stream）
        // 由各自的文件部件分支决定（也保留「裸流不预先包装」的现有行为）。
        if (partOverride.FileName is null && partOverride.ContentType is null)
        {
            return value;
        }

        // 字典不可能是单个文件：整体透传给 a[b] 展平（否则会落进集合分支被拆成 Key/Value 对象）。
        // 只实现 IReadOnlyDictionary 的自研只读视图同样命中 builder 的字典分支，不能当集合拆。
        if (value is IDictionary || value is IReadOnlyDictionary<string, object?>)
        {
            return value;
        }

        // 字符串 / 标量 / JSON 树永远是普通字段（文本文件的文本形态由调用方用 FormFile / byte[] 表达）
        if (value is string || value is JsonElement)
        {
            return value;
        }

        // 集合 / 迭代器 / 嵌套对象不包装：声明只作用于绑定的文件类值本身
        switch (value)
        {
            case Stream stream:
                try
                {
                    return new FormFile(
                        partOverride.FileName ?? RequireName(name),
                        stream,
                        partOverride.ContentType)
                    {
                        FieldName = partOverride.HasExplicitName ? name : null
                    };
                }
                catch (ArgumentException ex)
                {
                    // 声明的文件名 / 类型 / 字段名非法（FormFile 在构造期校验）：流的调用方已经交不回来，就地释放
                    stream.Dispose();
                    throw DeclaredValueError(ex);
                }
                catch
                {
                    // 字段名缺失 / 含非法字符：已是选项错误，释放后原样交给上层翻译成 ERR_BAD_OPTION_VALUE
                    stream.Dispose();
                    throw;
                }

            case byte[] bytes:
                return DeclaredFileFromBytes(
                    partOverride.FileName ?? RequireName(name),
                    bytes,
                    partOverride.ContentType,
                    partOverride.HasExplicitName ? name : null);

            case ReadOnlyMemory<byte> memory:
                return DeclaredFileFromBytes(
                    partOverride.FileName ?? RequireName(name),
                    memory.ToArray(),
                    partOverride.ContentType,
                    partOverride.HasExplicitName ? name : null);

            case Memory<byte> memory:
                return DeclaredFileFromBytes(
                    partOverride.FileName ?? RequireName(name),
                    memory.ToArray(),
                    partOverride.ContentType,
                    partOverride.HasExplicitName ? name : null);

            case ArraySegment<byte> segment:
                return DeclaredFileFromBytes(
                    partOverride.FileName ?? RequireName(name),
                    segment.ToArray(),
                    partOverride.ContentType,
                    partOverride.HasExplicitName ? name : null);
        }

        return value;
    }

    /// <summary>
    /// 按声明把二进制内容包成文件部件。
    /// <para>
    /// 声明的文件名 / Content-Type 由 <see cref="FormFile"/> 在构造期校验，抛的是 <see cref="ArgumentException"/>；
    /// 那是「配置写错了」而不是「请求体序列化失败」，所以在这里统一换成
    /// <see cref="InvalidMultipartOptionException"/>（由 <see cref="Create"/> 转成 <c>ERR_BAD_OPTION_VALUE</c>）。
    /// </para>
    /// </summary>
    private static FormFile DeclaredFileFromBytes(string fileName, byte[] content, string? contentType, string? fieldName)
    {
        try
        {
            return new FormFile(fileName, content, contentType) { FieldName = fieldName };
        }
        catch (ArgumentException ex)
        {
            // 内存内容没有所有权要回收（FormFile 内部的 MemoryStream 只持有托管数组）
            throw DeclaredValueError(ex);
        }
    }

    /// <summary>把 <see cref="FormFile"/> 构造期的 <see cref="ArgumentException"/> 归成调用方配置错误。</summary>
    private static InvalidMultipartOptionException DeclaredValueError(ArgumentException ex) =>
        new($"[Part] 声明的字段名 / 文件名 / Content-Type 非法：{ex.Message}", ex);

    /// <summary>二进制内容一律作为文件部件：字段名同时用作文件名（与 byte[] 既有行为一致）。</summary>
    /// <remarks>
    /// 名字校验必须在「部件交出所有权」之前完成：<paramref name="part"/> 已经构造好（Stream 形态下
    /// 它持有调用方的流），校验失败时它还没加入容器，容器的 Dispose 覆盖不到它 —— 就地释放是唯一机会。
    /// </remarks>
    private static void AddFilePart(MultipartFormDataContent content, string? name, HttpContent part)
    {
        try
        {
            var field = RequireName(name);
            RequestContentType.Set(part, OctetStreamMediaType);
            content.Add(part, field, field);
        }
        catch
        {
            part.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 加入调用方自带的部件：已有 Content-Disposition 时只补缺失的 Name
    /// （保留 filename / 类型等一切用户设置），走基类 Add 避免二次改写；
    /// 没有 disposition 时交给 <see cref="MultipartFormDataContent"/> 按 name 生成。
    /// </summary>
    private static void AddUserPart(MultipartFormDataContent content, HttpContent part, string name)
    {
        var disposition = part.Headers.ContentDisposition;
        if (disposition is null)
        {
            content.Add(part, name);
            return;
        }

        if (string.IsNullOrEmpty(disposition.Name))
        {
            disposition.Name = name;
        }

        ((MultipartContent)content).Add(part);
    }

    private static string RequireName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            // 与「名字含非法字符」同属调用方的配置错误，用同一个异常类型：
            // 上层据此统一转成 ERR_BAD_OPTION_VALUE，而不是误报成 ERR_BAD_REQUEST（请求体序列化失败）。
            throw new InvalidMultipartOptionException(
                "multipart 数据项缺少字段名：根节点不能是标量，请把内容放进对象/字典字段，或对 FormFile 设置 FieldName。");
        }

        // 字段名最终写进 Content-Disposition 的 name="..."（quoted-string）。
        // 反斜杠与控制字符不会让 .NET 报错，而是让服务端拿到错位或带引号的键名；引号与换行直接破坏语法。
        if (DispositionTokens.FindInvalidCharacter(name) is { } invalid)
        {
            throw new InvalidMultipartOptionException(
                $"multipart 字段名包含非法字符（{invalid}）：'{name}'。"
                + "Content-Disposition 的 name 是 quoted-string：双引号、反斜杠、CR/LF 与其它控制字符"
                + "会破坏语法或让服务端拿到的键名错位。中文可以正常使用（按 RFC 2047 编码），"
                + "请只去掉这些字符。");
        }

        return name;
    }
}
