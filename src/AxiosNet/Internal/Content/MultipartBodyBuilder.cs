using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AxiosNet.Metadata;

namespace AxiosNet.Internal.Content;

/// <summary><c>multipart/form-data</c> 通道：标量 / 嵌套对象 → 普通字段，<see cref="FormFile"/> / Stream / byte[] / HttpContent → 文件部件，数组展开为同名重复部件。</summary>
internal static class MultipartBodyBuilder
{
    // 文件部件的默认媒体类型（无显式 Content-Type 时用它）。
    private const string OctetStreamMediaType = AxiosContentType.OctetStream;

    /// <summary>把数据装配成 multipart 正文；显式 boundary 沿用。</summary>
    /// <remarks>失败时先释放已装部件；非法选项 / 环归一为 <c>ERR_BAD_OPTION_VALUE</c>，其余异常原样上抛。</remarks>
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

        var path = new ObjectPath();
        try
        {
            // claimed：本次装配已用掉的流。
            CollectParts(null, data, content, encoding, config, ref path, claimed: new HashSet<Stream>(ReferenceEqualityComparer.Instance));
            return content;
        }
        catch (Exception ex)
        {
            // 构建中途失败：已加入的部件必须释放。
            content.Dispose();

            // 再兜底释放数据里的流 / 自定义内容。
            MultipartOwnership.DisposeOwned(data);

            // 字段名 / 声明值非法：换成带配置上下文的 ERR_BAD_OPTION_VALUE。
            if (ex is InvalidMultipartOptionException invalid)
            {
                throw AxiosException.InvalidOption(invalid.Message, config, invalid);
            }

            if (ex is DataCycleException cycle)
            {
                throw AxiosException.InvalidOption(cycle.Message, config, cycle);
            }

            throw;
        }
        finally
        {
            path.Dispose();
        }
    }

    /// <summary>按值形态把数据项装配进容器：文件类 → 文件部件，标量 → 文本字段，字典 / 集合 / POCO 递归展开。</summary>
    private static void CollectParts(
        string? name,
        object? value,
        MultipartFormDataContent content,
        Encoding encoding,
        AxiosConfig config,
        ref ObjectPath path,
        HashSet<Stream> claimed,
        FormPartOverride partOverride = default,
        ValueFormat? memberFormat = null)
    {
        // 字段名选择器从 config 读取。
        var nameSelector = config.SerializationOrNull?.FormFieldName;
        switch (value)
        {
            case null:
                return;

            case FormFile file:
                // 值自带的声明是默认值，声明覆盖压过它。
                ClaimStream(file.Content, claimed);
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
                // 调用方自带部件：只补缺失的 Name，其余原样保留。
                string field;
                try
                {
                    field = RequireName(name);
                }
                catch
                {
                    // 装配失败也要释放。
                    part.Dispose();
                    throw;
                }

                AddUserPart(content, part, field);
                return;
            }

            case JsonElement element:
            {
                var sink = new MultipartJsonSink(content, encoding, config.SerializationOrNull?.ValueText);
                JsonFlattener.Flatten(name, element, nameSelector, ref sink);
                return;
            }

            case string text:
                content.Add(
                    new StringContent(
                        ValueText.Format(text, ValueTextChannel.Multipart, memberFormat, config.SerializationOrNull?.ValueText)!,
                        encoding),
                    RequireName(name));
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
                ClaimStream(stream, claimed);
                AddFilePart(content, name, new StreamContent(stream), config, partOverride);
                return;

            case IDictionary<string, object?> genericDictionary:
            {
                // 含 ExpandoObject（dynamic）；覆盖与格式化按字段名下传。
                var overrides = (genericDictionary as FormData)?.Overrides;
                var formats = (genericDictionary as FormData)?.Formats;
                path.Enter(genericDictionary);
                try
                {
                    foreach (var entry in genericDictionary)
                    {
                        var entryOverride = overrides is not null && overrides.TryGetValue(entry.Key, out var declared)
                            ? declared
                            : default;
                        var entryFormat = formats is not null && formats.TryGetValue(entry.Key, out var declaredFormat)
                            ? declaredFormat
                            : memberFormat;

                        CollectParts(
                            BodyFields.CombineName(nameSelector, name, entry.Key),
                            entry.Value,
                            content,
                            encoding,
                            config,
                            ref path,
                            claimed,
                            entryOverride,
                            entryFormat);
                    }
                }
                finally
                {
                    path.Exit(genericDictionary);
                }

                return;
            }

            // 只实现 IReadOnlyDictionary 的字典单独处理，不走 POCO 反射。
            case IReadOnlyDictionary<string, object?> readOnlyDictionary:
            {
                path.Enter(readOnlyDictionary);
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
                            ref path,
                            claimed,
                            memberFormat: memberFormat);
                    }
                }
                finally
                {
                    path.Exit(readOnlyDictionary);
                }

                return;
            }

            case IDictionary dictionary:
            {
                path.Enter(dictionary);
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
                            ref path,
                            claimed,
                            memberFormat: memberFormat);
                    }
                }
                finally
                {
                    path.Exit(dictionary);
                }

                return;
            }
        }

        var type = value.GetType();
        if (ScalarTypes.IsScalarType(type))
        {
            content.Add(
                new StringContent(ValueText.Format(value, ValueTextChannel.Multipart, memberFormat, config.SerializationOrNull?.ValueText)!, encoding),
                RequireName(name));
            return;
        }

        // 只实现 IDictionary<string,T> 的自定义字典。
        if (value is IEnumerable mapValue && QueryMap.CanEnumerate(mapValue))
        {
            path.Enter(mapValue);
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
                        ref path,
                        claimed,
                        memberFormat: memberFormat);
                }
            }
            finally
            {
                path.Exit(mapValue);
            }

            return;
        }

        if (value is IEnumerable enumerable)
        {
            path.Enter(value);
            try
            {
                foreach (var item in enumerable)
                {
                    // 集合逐项应用同一份声明。
                    CollectParts(name, item, content, encoding, config, ref path, claimed, partOverride, memberFormat);
                }
            }
            finally
            {
                path.Exit(value);
            }

            return;
        }

        // 嵌套 POCO：成员清单来自对象形状。
        path.Enter(value);
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
                    ref path,
                    claimed,
                    memberFormat: member.Format);
            }
        }
        finally
        {
            path.Exit(value);
        }
    }

    /// <summary>JSON 树按树语义展开：对象按键名、数组合并成同名重复部件、null / undefined 跳过。</summary>
    private readonly struct MultipartJsonSink : IJsonFlattenSink
    {
        private readonly MultipartFormDataContent _content;
        private readonly Encoding _encoding;
        private readonly Func<object?, ValueTextChannel, string?>? _formatter;

        public MultipartJsonSink(
            MultipartFormDataContent content,
            Encoding encoding,
            Func<object?, ValueTextChannel, string?>? formatter)
        {
            _content = content;
            _encoding = encoding;
            _formatter = formatter;
        }

        // 数组沿用父名：同名重复部件。
        public string? ArrayItemName(string? prefix, int index) => prefix;

        /// <summary>标量叶子加成同名文本部件。</summary>
        public void Leaf(string? name, JsonElement value)
            => _content.Add(
                new StringContent(
                    ValueText.JsonScalar(value, ValueTextChannel.Multipart, _formatter),
                    _encoding),
                RequireName(name));
    }

    /// <summary>登记「这个流已归某个部件所有」：同一份流挂到两个部件上时第二个只能发出空内容。</summary>
    /// <remarks>常见来源：<c>new[] { file, file }</c>，或同一个 <see cref="FormFile"/> 挂到两个字段上。</remarks>
    private static void ClaimStream(Stream stream, HashSet<Stream> claimed)
    {
        if (!claimed.Add(stream))
        {
            // 与「字段名非法」同属配置错误。
            throw new InvalidMultipartOptionException(
                "同一个流（FormFile.Content 或裸 Stream）被多个 multipart 部件引用：流的读取是单向的，"
                + "第一个部件读完后游标已在末尾，第二个部件只会发出空内容。"
                + "请为每个部件准备独立的流（同一个文件要发两次请开两个 FileStream），或改用 byte[]。");
        }
    }

    /// <summary>把一个文件类值加入容器：字段名 / 文件名 / Content-Type 按「声明覆盖 &gt; 值自带 &gt; 默认值」解析。</summary>
    private static void AddFilePart(
        MultipartFormDataContent content,
        string? name,
        HttpContent part,
        AxiosConfig config,
        FormPartOverride partOverride,
        string? valueFileName = null,
        string? valueContentType = null,
        string? valueFieldName = null)
    {
        try
        {
            // 名字优先级：显式声明 > 值自带 > 字段名。
            var field = RequireName(partOverride.HasExplicitName ? name : valueFieldName ?? name);
            var fileName = partOverride.FileName ?? valueFileName ?? field;
            var contentType = partOverride.ContentType ?? valueContentType ?? OctetStreamMediaType;

            ValidateDeclaredValue(partOverride);

            // 统一入口再校验一次控制字符。
            RequestContentType.Set(part, contentType, config);
            content.Add(part, field, fileName);
        }
        catch
        {
            part.Dispose();
            throw;
        }
    }

    /// <summary>校验调用方传入的文件名 / Content-Type，非法时换成 <see cref="InvalidMultipartOptionException"/>。</summary>
    private static void ValidateDeclaredValue(FormPartOverride partOverride)
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

    /// <summary>加入调用方自带的部件：已有 Content-Disposition 时只补缺失的 Name。</summary>
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
            // 与「名字含非法字符」同属配置错误。
            throw new InvalidMultipartOptionException("multipart 数据项缺少字段名：根节点不能是标量，请把内容放进对象/字典字段，或对 FormFile 设置 FieldName。");
        }

        // 字段名会写进 Content-Disposition 的 quoted-string，先拦非法字符。
        if (DispositionTokens.FindInvalidCharacter(name) is { } invalid)
        {
            throw new InvalidMultipartOptionException(
                $"multipart 字段名包含非法字符（{invalid}）：'{name}'。"
                + "Content-Disposition 的 name 是 quoted-string：双引号、反斜杠、CR/LF 与其它控制字符"
                + "会破坏语法或让服务端拿到的键名错位。中文可以正常使用（按 UTF-8 写进 quoted-string），"
                + "请只去掉这些字符。");
        }

        return name;
    }
}
