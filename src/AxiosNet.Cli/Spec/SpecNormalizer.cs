using AxiosNet.Cli.Model;

namespace AxiosNet.Cli.Spec;

/// <summary>
/// 把 Swagger 2.0 文档就地归一化成 OpenAPI 3 形状（只做读取器需要的最小转换），
/// 3.x 文档原样返回。这样读取与生成只需要应对一套结构。
/// </summary>
internal static class SpecNormalizer
{
    /// <summary>校验文档版本并把 Swagger 2.0 转成 OpenAPI 3 形状；版本不支持时抛 <see cref="GenerationException"/>。</summary>
    public static SpecNode Normalize(SpecNode root, string sourceName, WarningCollector warnings)
    {
        if (!root.IsObject)
        {
            throw new GenerationException($"{sourceName} 不是 OpenAPI / Swagger 文档（顶层不是对象）。");
        }

        var swagger = root["swagger"]?.AsString();
        if (!string.IsNullOrEmpty(swagger))
        {
            if (swagger != "2.0")
            {
                throw new GenerationException($"不支持的 Swagger 版本 {swagger}：仅支持 2.0。");
            }

            return Swagger2Converter.ToOpenApi3(root, warnings);
        }

        var openapi = root["openapi"]?.AsString();
        if (string.IsNullOrEmpty(openapi))
        {
            throw new GenerationException($"{sourceName} 不是 OpenAPI / Swagger 文档：缺少 openapi 或 swagger 字段。");
        }

        if (!openapi.StartsWith("3.", StringComparison.Ordinal))
        {
            throw new GenerationException($"不支持的 OpenAPI 版本 {openapi}：支持 3.0 / 3.1 与 Swagger 2.0。");
        }

        if (root["webhooks"] is { IsObject: true } webhooks && webhooks.Object.Count > 0)
        {
            warnings.Add("WEBHOOKS_IGNORED", "webhooks", "OpenAPI webhooks 不会被生成为客户端接口方法。");
        }

        return root;
    }
}

/// <summary>Swagger 2.0 → OpenAPI 3.0 的最小转换：definitions / parameters / responses / securityDefinitions 与全局 consumes / produces。</summary>
internal static class Swagger2Converter
{
    /// <summary>Swagger 2.0 path item 下的 HTTP 方法键（小写）。</summary>
    private static readonly string[] Verbs = { "get", "put", "post", "delete", "options", "head", "patch", "trace" };

    /// <summary>转换整份文档，返回新的 OpenAPI 3 根节点（不修改入参）。</summary>
    public static SpecNode ToOpenApi3(SpecNode root, WarningCollector warnings)
    {
        // 解析旧文档里的 $ref（swagger 2 的参数 / 响应也可能引用组件）。
        var resolver = new RefResolver(root, warnings);

        // 全局默认的请求 / 响应媒体类型，供未声明 consumes / produces 的操作继承。
        var globalConsumes = Strings(root["consumes"]);
        var globalProduces = Strings(root["produces"]);

        // 新文档的根对象，key 依次加入。
        var result = new Dictionary<string, SpecNode>(StringComparer.Ordinal)
        {
            ["openapi"] = SpecNode.Str("3.0.0")
        };

        if (root["info"] is { IsObject: true } info)
        {
            result["info"] = info;
        }

        if (root["tags"] is { IsArray: true } tags)
        {
            result["tags"] = tags;
        }

        if (root["security"] is { IsArray: true } security)
        {
            result["security"] = security;
        }

        var servers = BuildServers(root);
        if (servers is not null)
        {
            result["servers"] = servers;
        }

        // paths 逐个方法转换：路径级参数统一转一次，操作级参数在 ConvertOperation 内处理。
        var paths = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        if (root["paths"] is { IsObject: true } pathsNode)
        {
            foreach (var (path, rawItem) in pathsNode.Properties)
            {
                var item = resolver.Resolve(rawItem, $"path {path}");
                if (item is not { IsObject: true })
                {
                    continue;
                }

                var newItem = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
                foreach (var (key, value) in item.Properties)
                {
                    if (key != "parameters" && !Verbs.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        newItem[key] = value;
                    }
                }

                var pathParameters = ConvertParameters(item["parameters"], resolver, warnings, $"path {path}");
                if (pathParameters.Count > 0)
                {
                    newItem["parameters"] = SpecNode.Arr(pathParameters);
                }

                foreach (var verb in Verbs)
                {
                    var rawOperation = item[verb];
                    if (rawOperation is not { IsObject: true })
                    {
                        continue;
                    }

                    var operation = resolver.Resolve(rawOperation, $"{verb.ToUpperInvariant()} {path}");
                    if (operation is not { IsObject: true })
                    {
                        continue;
                    }

                    newItem[verb] = ConvertOperation(
                        operation, resolver, warnings, verb.ToUpperInvariant(), path, globalConsumes, globalProduces);
                }

                paths[path] = SpecNode.Obj(newItem);
            }
        }

        result["paths"] = SpecNode.Obj(paths);

        // Swagger 2 的 definitions / securityDefinitions 对应 OpenAPI 3 的 components.schemas / securitySchemes。
        var components = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        if (root["definitions"] is { IsObject: true } definitions)
        {
            components["schemas"] = definitions;
        }

        if (root["securityDefinitions"] is { IsObject: true } securityDefinitions)
        {
            components["securitySchemes"] = ConvertSecurityDefinitions(securityDefinitions);
        }

        if (components.Count > 0)
        {
            result["components"] = SpecNode.Obj(components);
        }

        return RewriteDefinitionRefs(SpecNode.Obj(result));
    }

    /// <summary>
    /// Swagger 2 的 <c>#/definitions/X</c> 归一化后位于 <c>#/components/schemas/X</c>；
    /// 复制进 paths / requestBody / responses 的 schema 仍带旧指针，必须整体改写，否则读取期全部 BROKEN_REF。
    /// </summary>
    private static SpecNode RewriteDefinitionRefs(SpecNode node)
    {
        if (node.IsObject)
        {
            // 只有发生改写时才重建节点，其余情况原样返回（保持引用相等，便于上层短路）。
            var changed = false;
            var copy = new Dictionary<string, SpecNode>(node.Object.Count, StringComparer.Ordinal);
            foreach (var (key, value) in node.Properties)
            {
                var rewritten = key == "$ref"
                    && value.AsString() is { } reference
                    && reference.StartsWith("#/definitions/", StringComparison.Ordinal)
                    ? SpecNode.Str("#/components/schemas/" + reference["#/definitions/".Length..])
                    : RewriteDefinitionRefs(value);

                changed |= !ReferenceEquals(rewritten, value);
                copy[key] = rewritten;
            }

            return changed ? SpecNode.Obj(copy) : node;
        }

        if (node.IsArray)
        {
            var changed = false;
            var items = new List<SpecNode>(node.Items.Count);
            foreach (var item in node.Items)
            {
                var rewritten = RewriteDefinitionRefs(item);
                changed |= !ReferenceEquals(rewritten, item);
                items.Add(rewritten);
            }

            return changed ? SpecNode.Arr(items) : node;
        }

        return node;
    }

    /// <summary>由 Swagger 2 的 host / basePath / schemes 拼出 OpenAPI 3 的 servers 数组；信息不全时返回 null。</summary>
    private static SpecNode? BuildServers(SpecNode root)
    {
        var host = root["host"]?.AsString();
        var basePath = root["basePath"]?.AsString() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(host))
        {
            var scheme = Strings(root["schemes"]).FirstOrDefault() ?? "https";
            var server = new Dictionary<string, SpecNode>(StringComparer.Ordinal)
            {
                ["url"] = SpecNode.Str($"{scheme}://{host}{basePath}")
            };
            return SpecNode.Arr(new[] { SpecNode.Obj(server) });
        }

        if (basePath.Length > 0)
        {
            var server = new Dictionary<string, SpecNode>(StringComparer.Ordinal)
            {
                ["url"] = SpecNode.Str(basePath)
            };
            return SpecNode.Arr(new[] { SpecNode.Obj(server) });
        }

        return null;
    }

    /// <summary>转换单个操作：拆出 body / formData 参数生成 requestBody，responses 的 schema 移到 content 下。</summary>
    private static SpecNode ConvertOperation(
        SpecNode operation,
        RefResolver resolver,
        WarningCollector warnings,
        string httpMethod,
        string path,
        string[] globalConsumes,
        string[] globalProduces)
    {
        // 操作级 consumes / produces 优先，缺省时继承全局默认。
        var location = $"{httpMethod} {path}";
        var consumes = Strings(operation["consumes"]);
        if (consumes.Length == 0)
        {
            consumes = globalConsumes;
        }

        var produces = Strings(operation["produces"]);
        if (produces.Length == 0)
        {
            produces = globalProduces;
        }

        var result = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        foreach (var (key, value) in operation.Properties)
        {
            if (key is not ("parameters" or "responses" or "consumes" or "produces"))
            {
                result[key] = value;
            }
        }

        // 普通参数 / body 参数 / formData 参数三类分开收集：后两类会合并成 requestBody。
        var parameters = new List<SpecNode>();
        SpecNode? body = null;
        var form = new List<SpecNode>();

        if (operation["parameters"] is { IsArray: true } parameterList)
        {
            foreach (var raw in parameterList.Items)
            {
                var parameter = resolver.Resolve(raw, location);
                if (parameter is not { IsObject: true })
                {
                    continue;
                }

                switch (parameter["in"]?.AsString())
                {
                    case "body":
                        body = parameter;
                        break;

                    case "formData":
                        form.Add(parameter);
                        break;

                    default:
                        parameters.Add(ConvertParameter(parameter));
                        break;
                }
            }
        }

        if (parameters.Count > 0)
        {
            result["parameters"] = SpecNode.Arr(parameters);
        }

        if (body is not null)
        {
            result["requestBody"] = ConvertBodyParameter(body, consumes, warnings, location);
        }
        else if (form.Count > 0)
        {
            result["requestBody"] = ConvertFormParameters(form, consumes, warnings, location);
        }

        result["responses"] = ConvertResponses(operation["responses"], produces, resolver, warnings, location);
        return SpecNode.Obj(result);
    }

    /// <summary>转换路径级参数；body / formData 出现在路径级属于非法结构，记警告后忽略。</summary>
    private static List<SpecNode> ConvertParameters(
        SpecNode? list,
        RefResolver resolver,
        WarningCollector warnings,
        string location)
    {
        var result = new List<SpecNode>();
        if (list is not { IsArray: true })
        {
            return result;
        }

        foreach (var raw in list.Items)
        {
            var parameter = resolver.Resolve(raw, location);
            if (parameter is not { IsObject: true })
            {
                continue;
            }

            if (parameter["in"]?.AsString() is "body" or "formData")
            {
                warnings.Add("PATH_BODY_PARAMETER", location, "路径级 body / formData 参数不受支持，已忽略。");
                continue;
            }

            result.Add(ConvertParameter(parameter));
        }

        return result;
    }

    /// <summary>单个非 body 参数转换：collectionFormat → x-axios-collection-format，x-nullable → nullable，file → binary string。</summary>
    private static SpecNode ConvertParameter(SpecNode parameter)
    {
        var result = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        foreach (var (key, value) in parameter.Properties)
        {
            result[key] = value;
        }

        if (result.TryGetValue("collectionFormat", out var collectionFormat))
        {
            result.Remove("collectionFormat");
            if (collectionFormat.AsString() is { } format)
            {
                result["x-axios-collection-format"] = SpecNode.Str(format);
            }
        }

        if (result.TryGetValue("x-nullable", out var nullable))
        {
            result["nullable"] = nullable;
        }

        if (result.TryGetValue("type", out var type) && type.AsString() == "file")
        {
            result["type"] = SpecNode.Str("string");
            result["format"] = SpecNode.Str("binary");
        }

        return SpecNode.Obj(result);
    }

    /// <summary>把 Swagger 2 的 body 参数转换成 OpenAPI 3 的 requestBody（content + schema）。</summary>
    private static SpecNode ConvertBodyParameter(
        SpecNode parameter,
        string[] consumes,
        WarningCollector warnings,
        string location)
    {
        var schema = parameter["schema"] is { IsObject: true } value
            ? value
            : SpecNode.Obj(new Dictionary<string, SpecNode>(StringComparer.Ordinal));

        // 请求体固定挑一个媒体类型（优先含 json 的 consumes）。
        var mediaType = ChooseJsonMedia(consumes);
        var content = new Dictionary<string, SpecNode>(StringComparer.Ordinal)
        {
            [mediaType] = SpecNode.Obj(new Dictionary<string, SpecNode>(StringComparer.Ordinal)
            {
                ["schema"] = schema
            })
        };

        var result = new Dictionary<string, SpecNode>(StringComparer.Ordinal)
        {
            ["content"] = SpecNode.Obj(content)
        };

        if (parameter["required"]?.AsBool() == true)
        {
            result["required"] = SpecNode.Bool(true);
        }

        if (parameter["description"]?.AsString() is { Length: > 0 } description)
        {
            result["description"] = SpecNode.Str(description);
        }

        if (consumes.Length > 1)
        {
            warnings.Add("MULTIPLE_REQUEST_CONTENT", location, $"body 参数声明了多个 consumes，取 {mediaType}。");
        }

        return SpecNode.Obj(result);
    }

    /// <summary>把一组 formData 参数合成一个对象 schema 的 requestBody（multipart 或 urlencoded）。</summary>
    private static SpecNode ConvertFormParameters(
        List<SpecNode> form,
        string[] consumes,
        WarningCollector warnings,
        string location)
    {
        // 文档常省略 consumes；有 file 参数时按 multipart 处理，否则文件会被编进 urlencoded 表单。
        var isMultipart = consumes.Any(media =>
                              media.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                          || form.Any(parameter => parameter["type"]?.AsString() == "file");
        var mediaType = isMultipart ? "multipart/form-data" : "application/x-www-form-urlencoded";

        // 合成对象的属性 / 必填名单，以及从 formData 参数搬进 schema 的字段白名单。
        var properties = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        var required = new List<SpecNode>();
        var schemaKeys = new[]
        {
            "type", "format", "items", "enum", "default", "minimum", "maximum",
            "minLength", "maxLength", "pattern", "uniqueItems", "x-nullable"
        };

        foreach (var parameter in form)
        {
            var name = parameter["name"]?.AsString();
            if (string.IsNullOrWhiteSpace(name))
            {
                warnings.Add("FORM_PARAMETER_WITHOUT_NAME", location, "formData 参数缺少 name，已忽略。");
                continue;
            }

            var schema = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
            foreach (var key in schemaKeys)
            {
                if (parameter[key] is { } value)
                {
                    schema[key] = value;
                }
            }

            if (schema.TryGetValue("type", out var type) && type.AsString() == "file")
            {
                schema["type"] = SpecNode.Str("string");
                schema["format"] = SpecNode.Str("binary");
            }

            properties[name] = SpecNode.Obj(schema);

            if (parameter["required"]?.AsBool() == true)
            {
                required.Add(SpecNode.Str(name));
            }
        }

        // 合成成一个 object schema，再塞进对应媒体类型的 content 下。
        var objectSchema = new Dictionary<string, SpecNode>(StringComparer.Ordinal)
        {
            ["type"] = SpecNode.Str("object"),
            ["properties"] = SpecNode.Obj(properties)
        };

        if (required.Count > 0)
        {
            objectSchema["required"] = SpecNode.Arr(required);
        }

        var content = new Dictionary<string, SpecNode>(StringComparer.Ordinal)
        {
            [mediaType] = SpecNode.Obj(new Dictionary<string, SpecNode>(StringComparer.Ordinal)
            {
                ["schema"] = SpecNode.Obj(objectSchema)
            })
        };

        var result = new Dictionary<string, SpecNode>(StringComparer.Ordinal)
        {
            ["content"] = SpecNode.Obj(content)
        };

        if (required.Count > 0)
        {
            result["required"] = SpecNode.Bool(true);
        }

        if (consumes.Length > 1)
        {
            warnings.Add("MULTIPLE_REQUEST_CONTENT", location, $"formData 声明了多个 consumes，取 {mediaType}。");
        }

        return SpecNode.Obj(result);
    }

    /// <summary>把 Swagger 2 响应的 <c>schema</c> 包进 <c>content</c>；没有 produces 时默认 application/json。</summary>
    private static SpecNode ConvertResponses(
        SpecNode? responses,
        string[] produces,
        RefResolver resolver,
        WarningCollector warnings,
        string location)
    {
        var result = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        if (responses is not { IsObject: true })
        {
            return SpecNode.Obj(result);
        }

        foreach (var (code, raw) in responses.Properties)
        {
            var response = resolver.Resolve(raw, $"{location} {code}");
            if (response is not { IsObject: true })
            {
                result[code] = SpecNode.Obj(new Dictionary<string, SpecNode>(StringComparer.Ordinal));
                continue;
            }

            // 除 schema 外的字段（description / headers 等）原样保留。
            var converted = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
            foreach (var (key, value) in response.Properties)
            {
                if (key != "schema")
                {
                    converted[key] = value;
                }
            }

            if (response["schema"] is { IsObject: true } schema)
            {
                // 同一个 schema 复制到每个声明的 produces 媒体类型下（去重）。
                var content = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
                var mediaTypes = produces.Length > 0 ? produces : new[] { "application/json" };
                foreach (var mediaType in mediaTypes)
                {
                    if (!content.ContainsKey(mediaType))
                    {
                        content[mediaType] = SpecNode.Obj(new Dictionary<string, SpecNode>(StringComparer.Ordinal)
                        {
                            ["schema"] = schema
                        });
                    }
                }

                converted["content"] = SpecNode.Obj(content);
            }

            result[code] = SpecNode.Obj(converted);
        }

        if (result.Count == 0)
        {
            warnings.Add("MISSING_RESPONSES", location, "响应没有声明任何状态码。");
        }

        return SpecNode.Obj(result);
    }

    /// <summary>转换安全定义：Swagger 2 的 <c>type: basic</c> 改成 OpenAPI 3 的 <c>http + scheme: basic</c>。</summary>
    private static SpecNode ConvertSecurityDefinitions(SpecNode securityDefinitions)
    {
        var result = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
        foreach (var (name, raw) in securityDefinitions.Properties)
        {
            var definition = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
            if (raw is { IsObject: true })
            {
                foreach (var (key, value) in raw.Properties)
                {
                    definition[key] = value;
                }
            }

            if (definition.TryGetValue("type", out var type) && type.AsString() == "basic")
            {
                definition["type"] = SpecNode.Str("http");
                definition["scheme"] = SpecNode.Str("basic");
            }

            result[name] = SpecNode.Obj(definition);
        }

        return SpecNode.Obj(result);
    }

    /// <summary>从 consumes 里挑一个含 json 的媒体类型；没有则取第一个，空列表退回 application/json。</summary>
    private static string ChooseJsonMedia(string[] consumes)
    {
        foreach (var media in consumes)
        {
            if (media.Contains("json", StringComparison.OrdinalIgnoreCase))
            {
                return media;
            }
        }

        return consumes.FirstOrDefault() ?? "application/json";
    }

    /// <summary>把数组节点读成去空白的字符串数组；非数组返回空数组。</summary>
    private static string[] Strings(SpecNode? node)
        => node is { IsArray: true }
            ? node.Items
                .Select(item => item.AsString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)
                .ToArray()
            : Array.Empty<string>();
}
