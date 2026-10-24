using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// <c>multipart/form-data</c> 通道：标量 / 嵌套对象 → 普通字段，
/// <see cref="FormFile"/> / Stream / byte[] / HttpContent → 文件部件；
/// 数组 / 集合展开为同名重复部件。
/// <para>
/// 部件声明覆盖（字段名 / 文件名 / Content-Type，由声明式层随值传入）在文件部件装配处
/// 一次性应用：包装、校验与所有权登记都只有这一处。
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

            // 只释放容器覆盖不到「还没轮到装配」的那些：第 N 个部件失败时，第 N+1 个裸 Stream
            // 还没被包成 StreamContent，但它同样已经交过所有权（README：装配失败即释放已交进来的流）。
            MultipartOwnership.DisposeOwned(data);

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
        switch (value)
        {
            case null:
                return;

            case FormFile file:
                // 值的自带声明（FieldName / FileName / ContentType）是默认值，声明覆盖在装配处压过它；
                // 值的流所有权随即转给 StreamContent，失败路径由 AddFilePart 就地释放
                AddFilePart(
                    content,
                    name,
                    new StreamContent(file.Content),
                    partOverride,
                    file.FileName,
                    file.ContentType,
                    file.FieldName);
                return;

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
                AddFilePart(content, name, new ByteArrayContent(bytes), partOverride);
                return;

            case ReadOnlyMemory<byte> memory:
                AddFilePart(content, name, new ReadOnlyMemoryContent(memory), partOverride);
                return;

            case Memory<byte> memory:
                AddFilePart(content, name, new ReadOnlyMemoryContent(memory), partOverride);
                return;

            case ArraySegment<byte> segment:
                AddFilePart(content, name, new ReadOnlyMemoryContent(segment.AsMemory()), partOverride);
                return;

            case Stream stream:
                AddFilePart(content, name, new StreamContent(stream), partOverride);
                return;

            case IDictionary<string, object?> genericDictionary:
            {
                // 含 ExpandoObject（dynamic）；声明式表单的覆盖按字段名登记，在这里随值一起下传
                var overrides = (genericDictionary as DeclaredForm)?.Overrides;
                visited = ObjectGraph.Enter(visited, genericDictionary);
                try
                {
                    foreach (var entry in genericDictionary)
                    {
                        var entryOverride = overrides is not null && overrides.TryGetValue(entry.Key, out var declared)
                            ? declared
                            : default;

                        CollectParts(
                            BodyFields.CombineName(nameSelector, name, entry.Key),
                            entry.Value,
                            content,
                            encoding,
                            nameSelector,
                            visited,
                            entryOverride);
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
    /// 把一个文件类值加入容器：字段名 / 文件名 / Content-Type 在这里统一解析，
    /// 声明覆盖压过值自带的声明（<paramref name="valueFileName"/> 等），后者再压过默认值。
    /// <para>
    /// 二进制内容默认「字段名即文件名 + octet-stream」（与 byte[] 既有行为一致）；
    /// 流 / <see cref="FormFile"/> 的默认值由调用方所给的值决定。
    /// </para>
    /// </summary>
    /// <param name="content">目标表单（部件加入后由它持有释放责任）。</param>
    /// <param name="name">字段名（[JsonPropertyName] / 属性名，再经 FormFieldNameSelector）。</param>
    /// <param name="part">已构造好的部件内容（失败路径由本方法就地释放）。</param>
    /// <param name="partOverride">声明式层传入的覆盖项（没有声明时为 default）。</param>
    /// <param name="valueFileName">值自带的文件名（如 <see cref="FormFile.FileName"/>）。</param>
    /// <param name="valueContentType">值自带的 Content-Type。</param>
    /// <param name="valueFieldName">值自带的字段名（如 <see cref="FormFile.FieldName"/>）。</param>
    /// <remarks>
    /// 名字校验必须在「部件交出所有权」之前完成：<paramref name="part"/> 已经构造好（Stream 形态下
    /// 它持有调用方的流），校验失败时它还没加入容器，容器的 Dispose 覆盖不到它 —— 就地释放是唯一机会。
    /// </remarks>
    private static void AddFilePart(
        MultipartFormDataContent content,
        string? name,
        HttpContent part,
        PartOverride partOverride,
        string? valueFileName = null,
        string? valueContentType = null,
        string? valueFieldName = null)
    {
        try
        {
            // 名字优先级：显式声明 > 值自带 > 字段名（[JsonPropertyName] / 属性名，再经 FormFieldNameSelector）
            var field = RequireName(partOverride.HasExplicitName ? name : valueFieldName ?? name);
            var fileName = partOverride.FileName ?? valueFileName ?? field;
            var contentType = partOverride.ContentType ?? valueContentType ?? OctetStreamMediaType;

            ValidateDeclaredValue(partOverride);

            // contentType 三条来源（声明覆盖 / 值自带 / 默认 octet-stream）都已在各自入口校验过
            // 控制字符，这里不携带 config：真出现非法值时会以 ArgumentException 冒到 Create 的 catch。
            RequestContentType.Set(part, contentType);
            content.Add(part, field, fileName);
        }
        catch
        {
            part.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 校验声明式层传入的文件名 / Content-Type。
    /// <para>
    /// 与 <see cref="FormFile"/> 的构造契约一致（文件名进 quoted-string、Content-Type 是头值）；
    /// 抛出的 <see cref="ArgumentException"/> 属于「配置写错了」而不是「请求体序列化失败」，
    /// 在这里统一换成 <see cref="InvalidMultipartOptionException"/>
    /// （由 <see cref="Create"/> 转成 <c>ERR_BAD_OPTION_VALUE</c>）。
    /// </para>
    /// </summary>
    private static void ValidateDeclaredValue(PartOverride partOverride)
    {
        try
        {
            if (partOverride.FileName is { } fileName
                && DispositionTokens.FindInvalidCharacter(fileName) is { } invalid)
            {
                throw new ArgumentException(
                    $"文件名包含不能出现在 Content-Disposition 里的字符（{invalid}）：'{fileName}'。"
                    + "双引号、反斜杠、CR/LF 与其它控制字符会破坏头部语法或让服务端收到的文件名错位；"
                    + "中文可以正常使用（会按 RFC 5987 编码）。",
                    nameof(fileName));
            }

            if (partOverride.ContentType is { } contentType)
            {
                AxiosHeaders.ValidateHeaderValue("Content-Type", contentType);
            }
        }
        catch (ArgumentException ex)
        {
            throw DeclaredValueError(ex);
        }
    }

    /// <summary>把声明值的 <see cref="ArgumentException"/> 归成调用方配置错误。</summary>
    private static InvalidMultipartOptionException DeclaredValueError(ArgumentException ex) =>
        new($"部件声明的字段名 / 文件名 / Content-Type 非法：{ex.Message}", ex);

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
