using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// <c>multipart/form-data</c> 通道：标量 / 嵌套对象 → 普通字段，<see cref="FormFile"/> / Stream / byte[] / HttpContent → 文件部件，
/// 数组 / 集合展开为同名重复部件；部件声明覆盖在文件部件装配处一次性应用。
/// </summary>
internal static class MultipartBodyBuilder
{
    private const string OctetStreamMediaType = AxiosContentType.OctetStream;

    /// <summary>
    /// 把数据装配成 multipart 正文；显式 boundary 沿用。失败时先释放已装部件：
    /// 非法选项 / 环归一为 <c>ERR_BAD_OPTION_VALUE</c>，其余异常原样上抛。
    /// </summary>
    public static HttpContent Create(
        object data,
        string mediaType,
        Encoding encoding,
        AxiosConfig config)
    {
        // 显式声明 boundary 时沿用，否则由 MultipartFormDataContent 生成；非法 boundary 归成 ERR_BAD_OPTION_VALUE。
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
                + "（RFC 2046：boundary 不超过 70 字符，且只含限定字符集；不指定时由库自动生成）。", config, ex);
        }

        try
        {
            CollectParts(null, data, content, encoding, config, visited: null);
            return content;
        }
        catch (Exception ex)
        {
            // 构建中途失败：已加入的部件（含已打开的 FileStream）必须释放。
            content.Dispose();

            // 再兜底释放数据里的流 / 自定义内容（可能含容器已覆盖的项，Dispose 可重复调用）。
            MultipartOwnership.DisposeOwned(data);

            // 字段名 / 声明值非法属于调用方选项错误，换成带配置上下文的 ERR_BAD_OPTION_VALUE。
            if (ex is InvalidMultipartOptionException invalid)
            {
                throw AxiosException.InvalidOption(invalid.Message, config, invalid);
            }

            // 环检测：转成带配置的选项错误。
            if (ex is DataCycleException cycle)
            {
                throw AxiosException.InvalidOption(cycle.Message, config, cycle);
            }

            throw;
        }
    }

    /// <summary>按值形态把数据项装配进容器：文件类 → 文件部件，标量 → 文本字段，字典 / 集合 / POCO 递归展开。</summary>
    private static void CollectParts(
        string? name,
        object? value,
        MultipartFormDataContent content,
        Encoding encoding,
        AxiosConfig config,
        HashSet<object>? visited,
        PartOverride partOverride = default)
    {
        // 字段名选择器从 config 读取（含实例默认值）。
        var nameSelector = config.SerializationOrNull?.FormFieldNameSelector;
        switch (value)
        {
            case null:
                return;

            case FormFile file:
                // 值自带的声明是默认值，声明覆盖压过它；流所有权随即转给 StreamContent。
                AddFilePart(
                    content,
                    name,
                    new StreamContent(file.Content),
                    config,
                    partOverride,
                    file.FileName,
                    file.ContentType,
                    file.FieldName);
                return;

            case HttpContent part:
            {
                // 调用方自带部件：只补缺失的 Name，其余（尤其 filename）原样保留。
                string field;
                try
                {
                    field = RequireName(name);
                }
                catch
                {
                    // 内容按「随请求释放」处理，装配失败也要释放。
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
                AddFilePart(content, name, new ByteArrayContent(bytes), config, partOverride);
                return;

            case ReadOnlyMemory<byte> memory:
                AddFilePart(content, name, new ReadOnlyMemoryContent(memory), config, partOverride);
                return;

            case Memory<byte> memory:
                AddFilePart(content, name, new ReadOnlyMemoryContent(memory), config, partOverride);
                return;

            case ArraySegment<byte> segment:
                AddFilePart(content, name, new ReadOnlyMemoryContent(segment.AsMemory()), config, partOverride);
                return;

            case Stream stream:
                AddFilePart(content, name, new StreamContent(stream), config, partOverride);
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
                            config,
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

            // 只实现 IReadOnlyDictionary 的字典要单独处理，不走 POCO 反射。
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
                            config,
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
                            config,
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

        // 只实现 IDictionary<string,T> 的自定义字典（T 不是 object）或值类型字典：按键值对展开。
        if (value is IEnumerable mapValue && QueryMap.CanEnumerate(mapValue))
        {
            visited = ObjectGraph.Enter(visited, mapValue);
            try
            {
                foreach (var entry in QueryMap.Enumerate(mapValue))
                {
                    CollectParts(
                        BodyFields.CombineName(nameSelector, name, entry.Key),
                        entry.Value,
                        content,
                        encoding,
                        config,
                        visited);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, mapValue);
            }

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
                    CollectParts(name, item, content, encoding, config, visited, partOverride);
                }
            }
            finally
            {
                ObjectGraph.Exit(visited, value);
            }

            return;
        }

        // 嵌套 POCO：成员清单来自对象形状，命名只看 [JsonPropertyName] 与属性名，忽略看 [JsonIgnore]。
        visited = ObjectGraph.Enter(visited, value);
        try
        {
            var shape = ShapeCache.Get(type, name);
            foreach (var member in shape.Members)
            {
                if (member.Ignored)
                {
                    continue;
                }

                CollectParts(
                    BodyFields.CombineName(nameSelector, name, member.FieldName),
                    member.GetValue(value),
                    content,
                    encoding,
                    config,
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
                    CollectJsonElementParts(BodyFields.CombineName(nameSelector, name, property.Name), property.Value, content, encoding, nameSelector);
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
    /// 把一个文件类值加入容器：字段名 / 文件名 / Content-Type 按「声明覆盖 &gt; 值自带 &gt; 默认值」解析。
    /// </summary>
    /// <remarks>名字校验必须在部件交出所有权之前完成，否则容器的 Dispose 覆盖不到它。</remarks>
    private static void AddFilePart(
        MultipartFormDataContent content,
        string? name,
        HttpContent part,
        AxiosConfig config,
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

            // 统一入口再校验一次控制字符（第三方头值不可信）。
            RequestContentType.Set(part, contentType, config);
            content.Add(part, field, fileName);
        }
        catch
        {
            part.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 校验声明式层传入的文件名 / Content-Type，非法时换成 <see cref="InvalidMultipartOptionException"/>。
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
                    + "中文可以正常使用（会按 RFC 5987 编码）。", nameof(fileName));
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
    /// 加入调用方自带的部件：已有 Content-Disposition 时只补缺失的 Name，否则交给
    /// <see cref="MultipartFormDataContent"/> 按 name 生成。
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

    /// <summary>字段名必须存在且能安全写进 Content-Disposition 的 quoted-string；不合法抛配置错误。</summary>
    private static string RequireName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            // 与「名字含非法字符」同属配置错误，上层统一转成 ERR_BAD_OPTION_VALUE。
            throw new InvalidMultipartOptionException("multipart 数据项缺少字段名：根节点不能是标量，请把内容放进对象/字典字段，或对 FormFile 设置 FieldName。");
        }

        // 字段名会写进 Content-Disposition 的 quoted-string，先拦非法字符。
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
