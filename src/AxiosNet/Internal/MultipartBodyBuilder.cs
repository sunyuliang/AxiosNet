using System.Collections;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// <c>multipart/form-data</c> 通道：标量 / 嵌套对象 → 普通字段，
/// <see cref="FormFile"/> / Stream / byte[] / HttpContent → 文件部件；
/// 数组 / 集合展开为同名重复部件。
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
        var boundary = RequestContentType.ExtractBoundary(mediaType);
        var content = boundary is null
            ? new MultipartFormDataContent()
            : new MultipartFormDataContent(boundary);

        try
        {
            CollectParts(null, data, content, encoding, config.FormFieldNameSelector, visited: null);
            return content;
        }
        catch (Exception ex)
        {
            // 构建中途失败：已加入的部件（含已打开的 FileStream）必须释放，避免句柄泄漏。
            content.Dispose();

            // 字段名非法属于调用方选项错误：换成带配置上下文的 ERR_BAD_OPTION_VALUE。
            if (ex is InvalidFieldNameException invalid)
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

    private static void CollectParts(
        string? name,
        object? value,
        MultipartFormDataContent content,
        Encoding encoding,
        Func<string?, string, string>? nameSelector,
        HashSet<object>? visited)
    {
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
                    CollectParts(name, item, content, encoding, nameSelector, visited);
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
            throw new InvalidFieldNameException(
                "multipart 数据项缺少字段名：根节点不能是标量，请把内容放进对象/字典字段，或对 FormFile 设置 FieldName。");
        }

        // 字段名最终写进 Content-Disposition 的 name="..."（quoted-string）。
        // 反斜杠与控制字符不会让 .NET 报错，而是让服务端拿到错位或带引号的键名；引号与换行直接破坏语法。
        if (DispositionTokens.FindInvalidCharacter(name) is { } invalid)
        {
            throw new InvalidFieldNameException(
                $"multipart 字段名包含非法字符（{invalid}）：'{name}'。"
                + "Content-Disposition 的 name 是 quoted-string：双引号、反斜杠、CR/LF 与其它控制字符"
                + "会破坏语法或让服务端拿到的键名错位。中文可以正常使用（按 RFC 2047 编码），"
                + "请只去掉这些字符。");
        }

        return name;
    }
}
