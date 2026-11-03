using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AxiosNet.Cli.Generation;
using AxiosNet.Cli.Spec;

namespace AxiosNet.Cli.Model;

/// <summary>把（已归一化的）OpenAPI 3 文档读成生成器使用的 IR：按 tag 分组、参数/正文/响应/授权全部落成声明式语义。</summary>
internal sealed class ApiReader
{
    /// <summary>OpenAPI path item 下的 HTTP 方法键（小写），同时决定遍历顺序。</summary>
    private static readonly string[] Verbs = { "get", "put", "post", "delete", "options", "head", "patch", "trace" };

    /// <summary>路径模板占位符：与运行时的识别规则完全一致（字母 / 下划线开头，字母 / 数字 / 下划线组成）。</summary>
    private static readonly Regex PathPlaceholderPattern = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);

    /// <summary>由客户端 / 框架托管的请求头：声明成参数会破坏请求构造，读取时直接跳过。</summary>
    private static readonly HashSet<string> ManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Accept", "Content-Type", "Authorization", "Host", "Content-Length", "Transfer-Encoding", "Connection"
    };

    /// <summary>归一化后的文档根节点，所有指针解析都以它为基准。</summary>
    private readonly SpecNode _root;

    /// <summary>命令行选项，决定生成形态、单接口模式等读取策略。</summary>
    private readonly CliOptions _options;

    /// <summary>读取阶段产生的警告收集器（最终并入 <see cref="ApiSpec.Warnings"/>）。</summary>
    private readonly WarningCollector _warnings;

    /// <summary>本地 $ref 解析器（path item / operation / parameter / requestBody 都会解引用）。</summary>
    private readonly RefResolver _refs;

    /// <summary>正在构建的 IR 结果。</summary>
    private readonly ApiSpec _spec = new();

    /// <summary>tag → 分组，按 tag 首次出现的顺序在外层维护。</summary>
    private readonly Dictionary<string, ApiGroup> _groups = new(StringComparer.Ordinal);

    /// <summary>分组键出现顺序，保证最终 <see cref="ApiSpec.Groups"/> 的排列稳定。</summary>
    private readonly List<string> _groupOrder = new();

    /// <summary>根级 tags[] 里声明的分组描述（tag 名 → description）。</summary>
    private readonly Dictionary<string, string?> _tagDescriptions = new(StringComparer.Ordinal);

    /// <summary>类型映射器，在分组与操作命名确定后创建。</summary>
    private SchemaMapper? _mapper;

    /// <summary>构造读取器；root 必须是已归一化为 OpenAPI 3 的文档。</summary>
    public ApiReader(SpecNode root, CliOptions options, WarningCollector warnings)
    {
        _root = root;
        _options = options;
        _warnings = warnings;
        _refs = new RefResolver(root, warnings);
    }

    /// <summary>读取整份文档并返回 IR；没有任何 paths / 可生成操作时抛 <see cref="GenerationException"/>。</summary>
    public ApiSpec Read()
    {
        ReadInfo();
        ReadTags();

        // paths 是必填结构：缺失或类型错误视为整份文档不可生成。
        var paths = _root["paths"];
        if (paths is not { IsObject: true })
        {
            throw new GenerationException("文档里缺少 paths 对象。");
        }

        // 先把「路径 + 方法 + 操作」摊平，后续分组 / 命名 / 处理都以这个列表为入口。
        var entries = BuildEntries(paths);
        if (entries.Count == 0)
        {
            throw new GenerationException("文档里没有任何可生成的操作（paths 下没有 HTTP 方法）。");
        }

        // 命名全部确定后再建映射器，模型名才能避开请求类 / 接口名。
        CreateGroups(entries);
        AssignNames(entries);
        _mapper = new SchemaMapper(_root, ReservedTypeNames(entries), _options, _warnings);

        // 根级 security 作为操作未显式声明 security 时的默认值。
        var rootSecurity = ParseSecurity(_root["security"], "security");
        foreach (var entry in entries)
        {
            Process(entry, rootSecurity);
        }

        FinalizeGroups();
        _spec.Types.AddRange(_mapper.Types);
        _spec.Warnings.AddRange(_warnings.Items);

        // 全部操作都被跳过（例如参数结构无法表达）时同样视为失败；警告已由调用方在异常前打印出来。
        if (_spec.OperationCount == 0)
        {
            throw new GenerationException("没有任何操作可以生成（全部操作都无法用声明式接口表达，原因见上方警告）。");
        }

        return _spec;
    }

    /// <summary>读 <c>info</c> 与 <c>servers</c> 的展示信息（标题、版本、首个 server URL）。</summary>
    private void ReadInfo()
    {
        _spec.Style = _options.Style;
        _spec.OpenApiVersion = _root["openapi"]?.AsString() ?? string.Empty;
        var info = _root["info"];
        _spec.Title = info?["title"]?.AsString() ?? "OpenAPI";
        _spec.SpecVersion = info?["version"]?.AsString() ?? string.Empty;
        _spec.ServerUrl = _root["servers"]?.Items.FirstOrDefault()?["url"]?.AsString();
    }

    /// <summary>收集根级 <c>tags[]</c> 的 name → description，供接口注释使用。</summary>
    private void ReadTags()
    {
        if (_root["tags"] is not { IsArray: true } tags)
        {
            return;
        }

        foreach (var tag in tags.Items)
        {
            var name = tag["name"]?.AsString()?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                _tagDescriptions[name] = tag["description"]?.AsString();
            }
        }
    }

    /// <summary>把 paths 下的每个 HTTP 方法摊平成操作条目：解析 path item / operation 的 $ref，并确定分组键。</summary>
    private List<OperationEntry> BuildEntries(SpecNode paths)
    {
        // 摊平后的操作列表，顺序为路径声明顺序 × Verbs 顺序。
        var entries = new List<OperationEntry>();
        foreach (var (path, rawPathItem) in paths.Properties)
        {
            var pathItem = _refs.Resolve(rawPathItem, $"path {path}");
            if (pathItem is not { IsObject: true })
            {
                continue;
            }

            foreach (var verb in Verbs)
            {
                var rawOperation = pathItem[verb];
                if (rawOperation is not { IsObject: true })
                {
                    continue;
                }

                var method = verb.ToUpperInvariant();
                var operation = _refs.Resolve(rawOperation, $"{method} {path}");
                if (operation is not { IsObject: true })
                {
                    continue;
                }

                // 单接口模式全部塞进一个空键分组；否则按第一个 tag 分组，无 tag 归入 Default。
                var groupKey = _options.SingleInterface ? string.Empty : FirstTag(operation) ?? "Default";
                entries.Add(new OperationEntry(path, pathItem, method, operation, groupKey));
            }
        }

        return entries;
    }

    /// <summary>取 operation 的第一个非空 tag；没有 tags 或全是空白串时返回 null。</summary>
    private static string? FirstTag(SpecNode operation)
    {
        if (operation["tags"] is not { IsArray: true } tags)
        {
            return null;
        }

        foreach (var tag in tags.Items)
        {
            var name = tag.AsString()?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>按分组键创建 <see cref="ApiGroup"/>：确定接口名、请求模型文件名与分组描述（均需去重）。</summary>
    private void CreateGroups(List<OperationEntry> entries)
    {
        // 接口名与请求模型文件名各自独立去重，避免不同 tag 规范化后撞名。
        var usedInterfaceNames = new HashSet<string>(StringComparer.Ordinal);
        // 请求模型文件名最终落成磁盘上的文件：Windows 文件系统不区分大小写，
        // 因此这里也按忽略大小写去重，否则 AB / Ab 两个 tag 会生成同名文件互相覆盖。
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (_groups.ContainsKey(entry.GroupKey))
            {
                continue;
            }

            string interfaceName;
            string? description;
            if (_options.SingleInterface)
            {
                interfaceName = _options.InterfaceName;
                description = _root["info"]?["description"]?.AsString() ?? _spec.Title;
            }
            else
            {
                interfaceName = Naming.Unique(Naming.InterfaceNameForTag(entry.GroupKey), usedInterfaceNames);
                description = _tagDescriptions.GetValueOrDefault(entry.GroupKey);
            }

            usedInterfaceNames.Add(interfaceName);
            _groups[entry.GroupKey] = new ApiGroup(entry.GroupKey, interfaceName, description)
            {
                RequestFileName = Naming.Unique(Naming.Pascal(entry.GroupKey) + "Requests", usedFileNames)
            };
            _groupOrder.Add(entry.GroupKey);
        }
    }

    /// <summary>先给所有操作定名：方法名按分组去重，请求类名在整份文档内唯一（同一个 C# 命名空间）。</summary>
    private void AssignNames(List<OperationEntry> entries)
    {
        var usedMethodNamesByGroup = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        // 请求类与运行库同名（如 operationId 为 api → ApiRequest）会遮蔽 ApiRequest<T>，
        // 因此先把保留名放进已占用集合，冲突时自动加序号。
        var usedClassNames = new HashSet<string>(Naming.ReservedTypeNames, StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!usedMethodNamesByGroup.TryGetValue(entry.GroupKey, out var usedMethodNames))
            {
                usedMethodNames = new HashSet<string>(StringComparer.Ordinal);
                usedMethodNamesByGroup[entry.GroupKey] = usedMethodNames;
            }

            var operationId = entry.Operation["operationId"]?.AsString() ?? string.Empty;
            entry.Name = Naming.Unique(MethodName(operationId, entry.Method, entry.Path), usedMethodNames);

            if (_options.Style == ApiStyle.Model)
            {
                entry.ClassName = Naming.Unique(RequestClassName(entry.Name), usedClassNames);
            }
        }
    }

    /// <summary>需要在模型命名时避开的类型名：模型式避请求类名，接口式避接口名。</summary>
    private IEnumerable<string> ReservedTypeNames(List<OperationEntry> entries)
        => _options.Style == ApiStyle.Model
            ? entries.Select(entry => entry.ClassName).Where(name => name.Length > 0)
            : _groups.Values.Select(group => group.InterfaceName);

    /// <summary>方法名 → 请求类名：去掉 Async 后缀再加 Request（如 <c>ListPetsAsync</c> → <c>ListPetsRequest</c>）。</summary>
    private static string RequestClassName(string methodName)
    {
        var baseName = methodName.EndsWith("Async", StringComparison.Ordinal)
            ? methodName[..^"Async".Length]
            : methodName;
        return (baseName.Length == 0 ? "Api" : baseName) + "Request";
    }

    /// <summary>把一个操作条目读成 <see cref="ApiOperation"/>：授权、参数、请求体、响应依次解析，结构不可表达时整条跳过。</summary>
    private void Process(OperationEntry entry, SecurityInfo rootSecurity)
    {
        var operation = entry.Operation;
        var location = $"{entry.Method} {entry.Path}";
        var group = _groups[entry.GroupKey];

        var model = new ApiOperation
        {
            Method = entry.Method,
            Path = entry.Path,
            OperationId = operation["operationId"]?.AsString() ?? string.Empty,
            Name = entry.Name,
            ClassName = entry.ClassName,
            Summary = operation["summary"]?.AsString(),
            Description = operation["description"]?.AsString(),
            Deprecated = operation["deprecated"]?.AsBool() == true
        };

        ApplySecurity(model, operation, rootSecurity, location);

        // 路径模板先于参数读取校验：占位符不合规时运行时会直接抛异常，整条操作必须先跳过。
        if (!TryExtractPlaceholders(model.Path, out var placeholders, out var templateProblem))
        {
            _warnings.Add("INVALID_PATH_TEMPLATE", location, templateProblem!);
            _warnings.Add("OPERATION_SKIPPED", location, "路径模板含无法识别的占位符，已跳过该操作。");
            return;
        }

        // 已占用的 C# 成员名（取消令牌固定占一个），参数 / 部件 / body 命名都要经过它去重。
        var usedNames = new HashSet<string>(StringComparer.Ordinal) { "cancellationToken" };

        // 本操作所有数组型查询参数声明的集合格式，最后取第一个并提示冲突。
        var queryFormats = new List<string>();

        // 路径模板里的占位符集合，用于校验路径参数是否齐全。
        var placeholderSet = new HashSet<string>(placeholders, StringComparer.Ordinal);

        // 出现无法表达的 path / header 参数时置位，整个操作随之跳过。
        var unsupportedParameter = false;

        foreach (var parameter in MergeParameters(entry.PathItem, operation, location))
        {
            var wireName = parameter["name"]?.AsString();
            if (string.IsNullOrEmpty(wireName))
            {
                _warnings.Add("PARAMETER_WITHOUT_NAME", location, "参数缺少 name，已跳过。");
                continue;
            }

            switch (parameter["in"]?.AsString())
            {
                case "path":
                    unsupportedParameter |= !ReadPathParameter(model, parameter, wireName, usedNames, location);
                    break;

                case "query":
                    ReadQueryParameter(model, parameter, wireName, usedNames, queryFormats, location);
                    break;

                case "header":
                    unsupportedParameter |= !ReadHeaderParameter(model, parameter, wireName, usedNames, location);
                    break;

                case "cookie":
                    _warnings.Add("COOKIE_PARAMETER", location, $"cookie 参数 {wireName} 无法在 AxiosNet 的接口声明中表达，已跳过。");
                    break;

                default:
                    _warnings.Add(
                        "UNKNOWN_PARAMETER_LOCATION",
                        location,
                        $"参数 {wireName} 的 in={parameter["in"]?.AsString()} 不受支持，已跳过。");
                    break;
            }
        }

        if (unsupportedParameter)
        {
            _warnings.Add("OPERATION_SKIPPED", location, "路径 / 请求头参数无法用标量绑定表达，已跳过该操作。");
            return;
        }

        if (!ValidatePathParameters(model, placeholderSet))
        {
            _warnings.Add("OPERATION_SKIPPED", location, "路径模板与路径参数不一致，已跳过该操作。");
            return;
        }

        var requestBodyNode = operation["requestBody"];
        if (requestBodyNode is not null)
        {
            var requestBody = _refs.Resolve(requestBodyNode, location);
            if (requestBody is not { IsObject: true })
            {
                _warnings.Add("BROKEN_REQUEST_BODY", location, "requestBody 无法解析，已跳过该操作。");
                return;
            }

            var body = ReadRequestBody(requestBody, model, usedNames, location, out var unsupported);
            if (unsupported)
            {
                _warnings.Add("OPERATION_SKIPPED", location, "请求体结构无法用声明式接口表达，已跳过该操作。");
                return;
            }

            model.RequestBody = body;
        }

        ReadResponse(model, operation["responses"], location);

        if (queryFormats.Count > 0)
        {
            var distinct = queryFormats.Distinct(StringComparer.Ordinal).ToList();
            model.CollectionFormat = distinct[0];
            if (distinct.Count > 1)
            {
                _warnings.Add(
                    "MIXED_COLLECTION_FORMATS",
                    location,
                    $"查询集合格式不一致（{string.Join(", ", distinct)}），统一按 {distinct[0]} 生成。");
            }
        }

        group.Operations.Add(model);
    }

    /// <summary>应用授权：操作级 security 优先，其次根级；只把可表达的 scheme 落成 <c>[Authorize]</c> / <c>[AllowAnonymous]</c>。</summary>
    private void ApplySecurity(ApiOperation model, SpecNode operation, SecurityInfo rootSecurity, string location)
    {
        // 只有 security 显式为数组时才覆盖根级配置，否则沿用根级默认。
        var security = operation["security"] is { IsArray: true }
            ? ParseSecurity(operation["security"], location)
            : rootSecurity;

        if (security.Required && security.SchemeName is not null)
        {
            // 只有能用 [Authorize] 表达的 http bearer / basic 才生成授权声明；
            // apiKey / oauth2 等其余方案无法由特性表达，记警告并由调用方在客户端设置凭据。
            if (ResolveScheme(security.SchemeName, location) is { } scheme)
            {
                model.RequiresAuth = true;
                model.AuthScheme = scheme;
            }
        }
        else if (security.ExplicitlyEmpty)
        {
            model.Anonymous = true;
        }
    }

    /// <summary>解析 security 节点：空数组表示显式匿名，非空对象里取第一个 scheme；多 scheme 的 AND 只取其一并告警。</summary>
    private SecurityInfo ParseSecurity(SpecNode? node, string location)
    {
        if (node is not { IsArray: true })
        {
            return default;
        }

        if (node.Items.Count == 0)
        {
            return new SecurityInfo(true, true, false, null);
        }

        // anonymous 表示存在空 object 替代方案（security 允许匿名访问）；scheme 取第一个出现的方案名。
        var anonymous = false;
        string? scheme = null;

        // 单个替代方案里同时列了多个 scheme（AND 语义）时只能取一个，其余会静默丢失，这里汇总提示。
        string? andSchemes = null;
        foreach (var alternative in node.Items)
        {
            if (!alternative.IsObject)
            {
                continue;
            }

            if (alternative.Object.Count == 0)
            {
                anonymous = true;
                continue;
            }

            if (alternative.Object.Count > 1)
            {
                andSchemes ??= string.Join(" + ", alternative.Object.Keys);
            }

            scheme ??= alternative.Properties.First().Key;
        }

        if (andSchemes is not null && scheme is not null && !anonymous)
        {
            _warnings.Add(
                "MULTI_SCHEME_SECURITY",
                location,
                $"security 要求同时满足多个方案（{andSchemes}），[Authorize] 只能表达一个，按 {scheme} 生成；"
                + "其余方案请在客户端补充凭据。");
        }

        return new SecurityInfo(true, false, scheme is not null && !anonymous, scheme);
    }

    /// <summary>
    /// 安全方案 → <c>[Authorize]</c> 的 scheme 名；只有 http bearer / basic 可表达，其余返回 <c>null</c> 并记警告。
    /// </summary>
    private string? ResolveScheme(string name, string location)
    {
        var definition = _root["components"]?["securitySchemes"]?[name];
        var type = definition?["type"]?.AsString();
        var scheme = definition?["scheme"]?.AsString();

        switch (type)
        {
            case "http":
                if (string.IsNullOrEmpty(scheme) || scheme.Equals("bearer", StringComparison.OrdinalIgnoreCase))
                {
                    return "Bearer";
                }

                if (scheme.Equals("basic", StringComparison.OrdinalIgnoreCase))
                {
                    return "Basic";
                }

                _warnings.Add(
                    "UNSUPPORTED_HTTP_SECURITY",
                    location,
                    $"安全方案 {name} 是 http {scheme}：AxiosNet 的 [Authorize] 只覆盖 bearer / basic，"
                    + "未生成授权声明，请在客户端自行设置 Authorization 头。");
                return null;

            case "apiKey":
                _warnings.Add(
                    "APIKEY_SECURITY",
                    location,
                    $"安全方案 {name} 是 apiKey（{definition?["in"]?.AsString()} {definition?["name"]?.AsString()}）："
                    + "[Authorize] 只写 Authorization 头，无法表达 apiKey，未生成授权声明；"
                    + "请在客户端按文档要求自行补充该密钥（头 / 查询参数 / Cookie）。");
                return null;

            case "oauth2":
            case "openIdConnect":
                _warnings.Add(
                    "OAUTH_SECURITY",
                    location,
                    $"安全方案 {name} 是 {type}：[Authorize] 只覆盖 http bearer / basic，未生成授权声明；"
                    + "请在客户端设置 Authorization（Bearer 令牌）。");
                return null;

            default:
                _warnings.Add(
                    "UNKNOWN_SECURITY",
                    location,
                    $"安全方案 {name} 的类型 {type} 无法用 [Authorize] 表达，未生成授权声明，请在客户端自行设置凭据。");
                return null;
        }
    }

    /// <summary>合并 path item 级与 operation 级参数：同 (in, name) 时操作级覆盖路径级，顺序保留首次声明。</summary>
    private List<SpecNode> MergeParameters(SpecNode pathItem, SpecNode operation, string location)
    {
        // 合并结果与 (in, name) → 下标索引，用于原位覆盖。
        var result = new List<SpecNode>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        // 追加一组参数：解引用后按 key 覆盖或新增。
        void Add(SpecNode? list)
        {
            if (list is not { IsArray: true })
            {
                return;
            }

            foreach (var raw in list.Items)
            {
                var parameter = _refs.Resolve(raw, location);
                if (parameter is not { IsObject: true })
                {
                    continue;
                }

                var key = (parameter["in"]?.AsString() ?? string.Empty)
                          + "\0"
                          + (parameter["name"]?.AsString() ?? string.Empty);

                if (index.TryGetValue(key, out var existing))
                {
                    result[existing] = parameter;
                }
                else
                {
                    index[key] = result.Count;
                    result.Add(parameter);
                }
            }
        }

        Add(pathItem["parameters"]);
        Add(operation["parameters"]);
        return result;
    }

    /// <summary>读取路径参数；无法用 <c>[Path]</c> 的标量绑定表达时记警告并返回 <c>false</c>（调用方跳过整个操作）。</summary>
    private bool ReadPathParameter(
        ApiOperation model,
        SpecNode parameter,
        string wireName,
        HashSet<string> usedNames,
        string location)
    {
        // label / matrix 会把 . / ; 等分隔符写进 URL，AxiosNet 的 [Path] 只做原样替换。
        if (parameter["style"]?.AsString() is { } style
            && !style.Equals("simple", StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add(
                "UNSUPPORTED_PATH_STYLE",
                location,
                $"路径参数 {wireName} 使用 style={style}，AxiosNet 的 [Path] 只支持 simple 风格，已跳过该操作。");
            return false;
        }

        // 先按 schema 形态判定能否绑定，再调用映射：非标量参数若先 Map 会注册出永远不会被引用的死模型。
        var schema = ParameterSchema(parameter);
        var (scalar, isArray) = ClassifyParameterSchema(schema, $"{location} path {wireName}");
        if (!scalar)
        {
            _warnings.Add(
                isArray ? "ARRAY_PATH_PARAMETER" : "NON_SCALAR_PATH_PARAMETER",
                location,
                $"路径参数 {wireName} 的 schema 是数组或对象：路径段在线上就是一段文本，"
                + "AxiosNet 的 [Path] 只接受标量，已跳过该操作。");
            return false;
        }

        var type = _mapper!.Map(
            schema, model.Name + Naming.Pascal(wireName), $"{location} path {wireName}");

        if (parameter["required"]?.AsBool() == false)
        {
            _warnings.Add("OPTIONAL_PATH_PARAMETER", location, $"路径参数 {wireName} 声明为可选，仍按必填生成。");
        }

        model.Parameters.Add(new ApiParameter
        {
            Location = ApiParameterLocation.Path,
            WireName = wireName,
            Name = Naming.Unique(Naming.Camel(wireName), usedNames),
            Type = type with { Nullable = false },
            Required = true,
            Description = parameter["description"]?.AsString()
        });

        return true;
    }

    /// <summary>读取查询参数：数组类型额外记录集合格式，其余按 <c>[Query]</c> 生成属性 / 形参。</summary>
    private void ReadQueryParameter(
        ApiOperation model,
        SpecNode parameter,
        string wireName,
        HashSet<string> usedNames,
        List<string> queryFormats,
        string location)
    {
        var type = _mapper!.Map(
            ParameterSchema(parameter), model.Name + Naming.Pascal(wireName), $"{location} query {wireName}");

        var style = parameter["style"]?.AsString();
        if (IsArray(type))
        {
            queryFormats.Add(CollectionFormat(parameter, location, wireName));
        }
        else if (style is not null && style.Equals("deepObject", StringComparison.OrdinalIgnoreCase))
        {
            _warnings.Add(
                "UNSUPPORTED_QUERY_STYLE",
                location,
                $"查询参数 {wireName} 使用 deepObject 编码（<名字>[prop]=value 形式的多个键），"
                + "AxiosNet 的 [Query] 只按单个键序列化对象，服务端可能无法解析。");
        }
        else if (!IsScalarQueryType(type))
        {
            _warnings.Add(
                "UNSUPPORTED_QUERY_STYLE",
                location,
                $"查询参数 {wireName} 的类型 {type.Name} 是复杂对象，AxiosNet 的 [Query] 不会展开成多个键，"
                + "按整体值序列化。");
        }

        model.Parameters.Add(new ApiParameter
        {
            Location = ApiParameterLocation.Query,
            WireName = wireName,
            Name = Naming.Unique(Naming.Camel(wireName), usedNames),
            Type = type,
            Required = parameter["required"]?.AsBool() == true,
            Description = parameter["description"]?.AsString()
        });
    }

    /// <summary>读取请求头参数；无法用 <c>[Header]</c> 的标量绑定表达时记警告并返回 <c>false</c>（调用方跳过整个操作）。</summary>
    private bool ReadHeaderParameter(
        ApiOperation model,
        SpecNode parameter,
        string wireName,
        HashSet<string> usedNames,
        string location)
    {
        if (ManagedHeaders.Contains(wireName))
        {
            _warnings.Add("MANAGED_HEADER_PARAMETER", location, $"请求头参数 {wireName} 由框架管理，已跳过。");
            return true;
        }

        // 与路径参数同理：先判形态再映射，避免为被跳过的操作注册死模型。
        var schema = ParameterSchema(parameter);
        var (scalar, isArray) = ClassifyParameterSchema(schema, $"{location} header {wireName}");
        if (!scalar)
        {
            _warnings.Add(
                isArray ? "ARRAY_HEADER_PARAMETER" : "NON_SCALAR_HEADER_PARAMETER",
                location,
                $"请求头参数 {wireName} 的 schema 是数组或对象：请求头在线上就是一段文本，"
                + "AxiosNet 的 [Header] 只接受标量，已跳过该操作。");
            return false;
        }

        var type = _mapper!.Map(
            schema, model.Name + Naming.Pascal(wireName), $"{location} header {wireName}");

        model.Parameters.Add(new ApiParameter
        {
            Location = ApiParameterLocation.Header,
            WireName = wireName,
            Name = Naming.Unique(Naming.Camel(wireName), usedNames),
            Type = type,
            Required = parameter["required"]?.AsBool() == true,
            Description = parameter["description"]?.AsString()
        });

        return true;
    }

    /// <summary>取参数的 schema：优先 <c>content.*.schema</c>（参数用 JSON 编码时），否则直接取 <c>schema</c>。</summary>
    private static SpecNode ParameterSchema(SpecNode parameter)
    {
        if (parameter["content"] is { IsObject: true } content && content.Object.Count > 0)
        {
            var media = content.Properties.First().Value;
            if (media?["schema"] is { IsObject: true } schema)
            {
                return schema;
            }
        }

        return parameter["schema"] is { IsObject: true } direct ? direct : parameter;
    }

    /// <summary>判断是否为数组类型（<c>byte[]</c> 视为二进制标量，不算数组）。</summary>
    private static bool IsArray(TypeRef type)
        => type.Name.EndsWith("[]", StringComparison.Ordinal) && type.Name != "byte[]";

    /// <summary>查询参数能否按单键标量序列化：string 与值类型（含枚举）算标量，其余对象 / 字典不可展开。</summary>
    private static bool IsScalarQueryType(TypeRef type)
        => type.Name == "string" || (type.IsValueType && type.Name != "JsonElement");

    /// <summary>
    /// 在类型映射之前按 schema 形态判断参数能否落成标量：与 <see cref="SchemaMapper"/> 的分支顺序保持一致
    /// （$ref → enum → oneOf/anyOf → 对象 → 数组 / 原始类型）。返回的第二项区分数组，用于选警告码。
    /// </summary>
    private (bool Scalar, bool Array) ClassifyParameterSchema(SpecNode? schema, string location)
    {
        var resolved = _refs.Resolve(schema, location);
        if (resolved is not { IsObject: true })
        {
            return (false, false);
        }

        // enum 在映射里先于对象 / 数组分支，结果一定是枚举或基础类型（都是标量）。
        if (resolved["enum"] is { IsArray: true } enumNode && enumNode.Items.Count > 0)
        {
            return (true, false);
        }

        // oneOf / anyOf 一律降级为 JsonElement，运行时不算标量。
        if (resolved["oneOf"] is { IsArray: true } oneOf && oneOf.Items.Count > 0)
        {
            return (false, false);
        }

        if (resolved["anyOf"] is { IsArray: true } anyOf && anyOf.Items.Count > 0)
        {
            return (false, false);
        }

        // 有 properties / allOf 的 schema 会生成类，不是标量。
        if (IsClassLike(resolved))
        {
            return (false, false);
        }

        switch (EffectiveType(resolved))
        {
            case "string":
                // format=byte / binary 映射成 byte[]，运行时同样不算标量。
                var format = resolved["format"]?.AsString();
                return (format is not ("byte" or "binary"), false);

            case "integer":
            case "number":
            case "boolean":
                return (true, false);

            case "array":
                return (false, true);

            default:
                // object / file / null / 未知 / 多类型都降级成 JsonElement 或 byte[]，不是标量。
                return (false, false);
        }
    }

    /// <summary>取 schema 的有效 <c>type</c>：3.1 的数组形式剔除 null 后只剩一种才算明确，否则视为未知。</summary>
    private static string? EffectiveType(SpecNode schema)
    {
        var node = schema["type"];
        if (node is null)
        {
            return null;
        }

        if (!node.IsArray)
        {
            return node.AsString();
        }

        var types = node.Items
            .Select(item => item.AsString())
            .Where(type => !string.IsNullOrEmpty(type) && type != "null")
            .ToList();
        return types.Count == 1 ? types[0] : null;
    }

    /// <summary>判断 schema 是否会生成命名类：有非空 properties 或非空 allOf（与映射器口径一致）。</summary>
    private static bool IsClassLike(SpecNode schema)
        => (schema["properties"] is { IsObject: true } properties && properties.Object.Count > 0)
           || (schema["allOf"] is { IsArray: true } allOf && allOf.Items.Count > 0);

    /// <summary>决定数组型查询参数的序列化格式：优先扩展字段 x-axios-collection-format，再按 OpenAPI style/explode 推导。</summary>
    private string CollectionFormat(SpecNode parameter, string location, string wireName)
    {
        if (parameter["x-axios-collection-format"]?.AsString() is { } explicitFormat)
        {
            switch (explicitFormat.ToLowerInvariant())
            {
                case "multi":
                    return "Multi";
                case "csv":
                    return "Csv";
                case "ssv":
                    return "Ssv";
                case "tsv":
                    return "Tsv";
                case "pipes":
                    return "Pipes";
            }

            _warnings.Add(
                "UNKNOWN_COLLECTION_FORMAT",
                location,
                $"查询参数 {wireName} 的 collectionFormat={explicitFormat} 未知，按 Multi 生成。");
            return "Multi";
        }

        var style = parameter["style"]?.AsString();
        if (style is null)
        {
            // OpenAPI 3 查询参数的默认风格是 form + explode=true，即重复键。
            return "Multi";
        }

        var explode = parameter["explode"]?.AsBool() ?? style == "form";
        switch (style)
        {
            case "form":
                return explode ? "Multi" : "Csv";
            case "spaceDelimited":
                return "Ssv";
            case "pipeDelimited":
                return "Pipes";
            case "deepObject":
                _warnings.Add(
                    "DEEP_OBJECT_ARRAY",
                    location,
                    $"查询参数 {wireName} 对数组使用 deepObject，AxiosNet 无对应格式，按 Multi 生成。");
                return "Multi";
            case "simple":
                _warnings.Add("SIMPLE_QUERY_STYLE", location, $"查询参数 {wireName} 使用 simple 风格，按 Csv 生成。");
                return "Csv";
            default:
                _warnings.Add(
                    "UNKNOWN_QUERY_STYLE",
                    location,
                    $"查询参数 {wireName} 的 style={style} 未知，按 Multi 生成。");
                return "Multi";
        }
    }

    /// <summary>校验路径模板与路径参数：占位符缺参数则跳过操作，多余参数记警告后移除。</summary>
    private bool ValidatePathParameters(ApiOperation model, HashSet<string> placeholders)
    {
        // 已声明的路径参数线上名集合。
        var declared = model.Parameters
            .Where(parameter => parameter.Location == ApiParameterLocation.Path)
            .Select(parameter => parameter.WireName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = placeholders.Except(declared).ToList();
        if (missing.Count > 0)
        {
            _warnings.Add(
                "MISSING_PATH_PARAMETER",
                model.Location,
                $"路径模板占位符 {string.Join(", ", missing)} 没有对应参数。");
            return false;
        }

        var extra = declared.Except(placeholders).ToList();
        if (extra.Count > 0)
        {
            _warnings.Add(
                "UNUSED_PATH_PARAMETER",
                model.Location,
                $"路径参数 {string.Join(", ", extra)} 不在路径模板中，已移除。");
            model.Parameters.RemoveAll(parameter =>
                parameter.Location == ApiParameterLocation.Path && extra.Contains(parameter.WireName));
        }

        return true;
    }

    /// <summary>读请求体：按媒体类型选 JSON / form / multipart / text / binary 的绑定形态，无法表达时置 unsupported。</summary>
    private ApiRequestBody? ReadRequestBody(
        SpecNode requestBody,
        ApiOperation model,
        HashSet<string> usedNames,
        string location,
        out bool unsupported)
    {
        unsupported = false;
        var content = requestBody["content"];
        if (content is not { IsObject: true } || content.Object.Count == 0)
        {
            return null;
        }

        // 多个媒体类型时按固定优先级选一个（JSON 优先），并提示其余被忽略。
        var media = PickMediaType(content.Object, forRequest: true);
        if (content.Object.Count > 1)
        {
            _warnings.Add(
                "MULTIPLE_REQUEST_CONTENT",
                location,
                $"请求体声明了多个媒体类型（{string.Join(", ", content.Object.Keys)}），取 {media}。");
        }

        var mediaNode = content[media];
        var schema = mediaNode?["schema"];
        var result = new ApiRequestBody
        {
            MediaType = BaseMediaType(media),
            Required = requestBody["required"]?.AsBool() == true,
            Description = requestBody["description"]?.AsString()
        };

        switch (ClassifyMedia(media))
        {
            case MediaKind.Multipart:
                result.MediaType = "multipart/form-data";
                if (!_mapper!.TryDescribeObject(schema, $"{location} multipart", out var properties))
                {
                    _warnings.Add("MULTIPART_UNSUPPORTED", location, "multipart 请求体不是对象 schema，无法展开为部件。");
                    unsupported = true;
                    return null;
                }

                result.IsMultipart = true;
                foreach (var property in properties)
                {
                    var type = _mapper.Map(
                        property.Schema,
                        model.Name + Naming.Pascal(property.WireName),
                        $"{location} part {property.WireName}");

                    if (IsComplexPart(type))
                    {
                        _warnings.Add(
                            "MULTIPART_OBJECT_PART",
                            location,
                            $"部件 {property.WireName} 是复杂对象，表单编码结果可能与服务端预期不一致。");
                    }

                    result.Parts.Add(new ApiPart
                    {
                        WireName = property.WireName,
                        Name = Naming.Unique(Naming.Camel(property.WireName), usedNames),
                        Type = type,
                        Required = property.Required,
                        IsFile = type.Name == "byte[]",
                        Description = property.Description
                    });
                }

                if (result.Parts.Count == 0)
                {
                    _warnings.Add("MULTIPART_UNSUPPORTED", location, "multipart 请求体没有任何属性。");
                    unsupported = true;
                    return null;
                }

                return result;

            case MediaKind.Form:
                result.MediaType = "application/x-www-form-urlencoded";
                result.Type = _mapper!.Map(schema, model.Name + "Form", $"{location} request");
                result.BodyName = Naming.Unique("body", usedNames);
                return result;

            case MediaKind.Text:
                // 媒体类型保持文档声明（text/csv、text/markdown 等不能一律改写成 text/plain），
                // 否则服务端收到的 Content-Type 与契约不一致。
                result.Type = TypeRef.String with { Nullable = result.Required ? false : true };
                result.BodyName = Naming.Unique("body", usedNames);
                return result;

            case MediaKind.Binary:
                result.MediaType = "application/octet-stream";
                result.Type = new TypeRef("byte[]", false, result.Required ? false : true);
                result.BodyName = Naming.Unique("body", usedNames);
                return result;

            case MediaKind.Json:
                // 保留文档声明的媒体类型（如 application/vnd.api+json）：运行时未登记该类型时仍回落到 JSON 序列化，
                // 但 Content-Type 必须按声明发送，不能一律写成 application/json。
                result.Type = schema is null
                    ? TypeRef.JsonElement
                    : _mapper!.Map(schema, BodyTypeName(model), $"{location} request");
                result.BodyName = Naming.Unique("body", usedNames);
                return result;

            default:
                result.Type = schema is null
                    ? TypeRef.JsonElement
                    : _mapper!.Map(schema, BodyTypeName(model), $"{location} request");
                result.BodyName = Naming.Unique("body", usedNames);
                return result;
        }
    }

    /// <summary>内联请求体类型的建议名：模型式下与请求类名（<c>XxxRequest</c>）错开，避免命名后缀。</summary>
    private string BodyTypeName(ApiOperation model)
        => model.Name + (_options.Style == ApiStyle.Model ? "Body" : "Request");

    /// <summary>判断 multipart 部件是否是复杂结构（引用类型且非 string / byte[] / 数组），用于提示编码风险。</summary>
    private static bool IsComplexPart(TypeRef type)
        => !type.IsValueType
           && type.Name is not ("string" or "byte[]")
           && !type.Name.EndsWith("[]", StringComparison.Ordinal);

    /// <summary>响应节点（可能还是组件 <c>$ref</c>）是否声明了非空 <c>content</c>。</summary>
    private bool HasResponseContent(SpecNode? node, string location)
    {
        var response = _refs.Resolve(node, location);
        var content = response?["content"];
        return content is { IsObject: true } && content.Object.Count > 0;
    }

    /// <summary>读成功响应：优先带正文的最小 2xx，其次 2XX 区间，最后 default；再判定流式 / JSON / 文本 / 二进制。</summary>
    private void ReadResponse(ApiOperation model, SpecNode? responses, string location)
    {
        if (responses is not { IsObject: true } || responses.Object.Count == 0)
        {
            model.ResponseVoid = true;
            _warnings.Add("MISSING_RESPONSES", location, "操作缺少 responses，按无返回值生成。");
            return;
        }

        // 具体 2xx 状态码 / 2XX 区间 / default 兜底；优先级从高到低。
        var exact = new List<(int Code, string Key, bool HasContent)>();
        string? range = null;
        string? fallback = null;

        foreach (var key in responses.Object.Keys)
        {
            if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
                && code is >= 200 and <= 299)
            {
                exact.Add((code, key, HasResponseContent(responses[key], $"{location} {key}")));
            }
            else if (key.Equals("2XX", StringComparison.OrdinalIgnoreCase))
            {
                range = key;
            }
            else if (key.Equals("default", StringComparison.OrdinalIgnoreCase))
            {
                fallback = key;
            }
        }

        // 多个 2xx 时优先选带正文的，其次状态码最小的：只按状态码排会把 200（无 content）当成功，
        // 丢掉 201（带 content）声明的强类型返回值。
        exact.Sort((left, right) =>
        {
            if (left.HasContent != right.HasContent)
            {
                return left.HasContent ? -1 : 1;
            }

            return left.Code.CompareTo(right.Code);
        });

        var chosen = exact.Count > 0 ? exact[0].Key : range ?? fallback;
        if (chosen is null)
        {
            model.ResponseVoid = true;
            _warnings.Add("NO_SUCCESS_RESPONSE", location, "没有 2xx / 2XX / default 响应，按无返回值生成。");
            return;
        }

        if (exact.Count > 1)
        {
            _warnings.Add(
                "MULTIPLE_SUCCESS_RESPONSES",
                location,
                $"存在多个 2xx 响应（{string.Join(", ", exact.Select(item => item.Key))}），取 {chosen}。");
        }

        if (chosen == fallback)
        {
            _warnings.Add("DEFAULT_RESPONSE", location, "没有 2xx 响应，按 default 生成。");
        }

        // 响应本身可能也是组件 $ref（如 #/components/responses/Xxx）。
        var response = _refs.Resolve(responses[chosen], $"{location} {chosen}");
        if (response is not { IsObject: true })
        {
            model.ResponseVoid = true;
            return;
        }

        var content = response["content"];
        if (content is not { IsObject: true } || content.Object.Count == 0)
        {
            model.ResponseVoid = true;
            return;
        }

        // 响应侧只按 JSON → +json → */* → text → octet-stream 的固定顺序挑一个媒体类型。
        var media = PickMediaType(content.Object, forRequest: false);
        if (content.Object.Count > 1)
        {
            _warnings.Add(
                "MULTIPLE_RESPONSE_CONTENT",
                location,
                $"响应 {chosen} 声明了多个媒体类型（{string.Join(", ", content.Object.Keys)}），取 {media}。");
        }

        var mediaNode = content[media];
        var schema = mediaNode?["schema"];

        // 流式判定优先于普通媒体分类；explicitArrayStream 表示文档显式声明了数组流。
        var streamFormat = DetectStreamFormat(media, mediaNode, response, schema, out var explicitArrayStream);
        if (streamFormat is not null)
        {
            model.ResponseKind = ApiResponseKind.Stream;
            model.StreamFormat = streamFormat;

            if (!_options.UseResponseWrapper)
            {
                _warnings.Add(
                    "STREAM_REQUIRES_WRAPPER",
                    location,
                    "流式响应只支持 Task<AxiosResponse<IAsyncEnumerable<T>>>，--response-wrapper none 对该方法不生效。");
            }

            if (schema?["type"]?.AsString() == "array" && schema["items"] is { IsObject: true } itemSchema)
            {
                // 显式声明 x-axios-stream: array 时按数组就是契约，不再提示。
                if (!explicitArrayStream)
                {
                    _warnings.Add("STREAM_ARRAY_FRAME", location, "流式响应的 schema 是数组，按元素类型逐帧生成。");
                }

                schema = itemSchema;
            }

            model.ReturnType = schema is null
                ? TypeRef.JsonElement
                : _mapper!.Map(schema, model.Name + "Item", $"{location} response");
            return;
        }

        switch (ClassifyResponseMedia(media))
        {
            case ResponseMedia.Json:
                model.ResponseKind = ApiResponseKind.Json;
                if (schema is null)
                {
                    _warnings.Add(
                        "RESPONSE_WITHOUT_SCHEMA",
                        location,
                        $"响应 {chosen} 的 {media} 没有 schema，按 JsonElement 生成。");
                    model.ReturnType = TypeRef.JsonElement;
                }
                else
                {
                    model.ReturnType = _mapper!.Map(schema, model.Name + "Response", $"{location} response");
                }

                break;

            case ResponseMedia.Text:
                model.ResponseKind = ApiResponseKind.Text;
                model.ReturnType = TypeRef.String with { Nullable = false };
                break;

            default:
                model.ResponseKind = ApiResponseKind.Binary;
                model.ReturnType = new TypeRef("byte[]", false, false);
                break;
        }
    }

    /// <summary>识别流式响应：先看 x-axios-stream 扩展，再按 text/event-stream、NDJSON 等媒体类型推断。</summary>
    private static string? DetectStreamFormat(
        string media,
        SpecNode? mediaNode,
        SpecNode response,
        SpecNode? schema,
        out bool explicitArray)
    {
        explicitArray = false;
        // 扩展字段允许写在 media type / response / schema 三处，按从具体到抽象的顺序取值。
        var declared = mediaNode?["x-axios-stream"]?.AsString()
                       ?? response["x-axios-stream"]?.AsString()
                       ?? schema?["x-axios-stream"]?.AsString();

        if (declared is not null)
        {
            switch (declared.ToLowerInvariant())
            {
                case "sse":
                case "eventsource":
                    return "ServerSentEvents";
                case "ndjson":
                    return "NewlineDelimitedJson";
                case "array":
                case "jsonarray":
                    explicitArray = true;
                    return "JsonArray";
            }
        }

        // 没有扩展字段时按标准流式媒体类型兜底识别。
        var value = BaseMediaType(media).ToLowerInvariant();
        if (value == "text/event-stream")
        {
            return "ServerSentEvents";
        }

        if (value is "application/x-ndjson" or "application/ndjson" or "application/jsonl" or "application/x-jsonlines")
        {
            return "NewlineDelimitedJson";
        }

        return null;
    }

    /// <summary>响应媒体类型归类：JSON（含 +json 与 */*）、text/* 与其他（二进制）。</summary>
    private static ResponseMedia ClassifyResponseMedia(string media)
    {
        var value = BaseMediaType(media);
        if (value.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || value == "*/*")
        {
            return ResponseMedia.Json;
        }

        return value.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            ? ResponseMedia.Text
            : ResponseMedia.Binary;
    }

    /// <summary>请求媒体类型归类：form / multipart / JSON / text / 二进制 / 其他。</summary>
    private static MediaKind ClassifyMedia(string media)
    {
        var value = BaseMediaType(media);
        if (value.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Form;
        }

        if (value.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Multipart;
        }

        if (value.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || value == "*/*")
        {
            return MediaKind.Json;
        }

        if (value.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Text;
        }

        return value.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
            ? MediaKind.Binary
            : MediaKind.Other;
    }

    /// <summary>从多个 content 媒体类型中按固定优先级挑一个：JSON → +json → */* →（请求侧 form / multipart）→ text → 二进制。</summary>
    private static string PickMediaType(IReadOnlyDictionary<string, SpecNode> content, bool forRequest)
    {
        // keys 顺序即文档声明顺序，FirstOrDefault 保证同优先级时取最先声明的。
        var keys = content.Keys.ToList();
        string? pick = keys.FirstOrDefault(key =>
                         BaseMediaType(key).Equals("application/json", StringComparison.OrdinalIgnoreCase))
                     ?? keys.FirstOrDefault(key =>
                         BaseMediaType(key).EndsWith("+json", StringComparison.OrdinalIgnoreCase))
                     ?? keys.FirstOrDefault(key => BaseMediaType(key) == "*/*");

        if (forRequest)
        {
            pick ??= keys.FirstOrDefault(key =>
                BaseMediaType(key).Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase));
            pick ??= keys.FirstOrDefault(key =>
                BaseMediaType(key).StartsWith("multipart/", StringComparison.OrdinalIgnoreCase));
        }

        pick ??= keys.FirstOrDefault(key => BaseMediaType(key).StartsWith("text/", StringComparison.OrdinalIgnoreCase));
        pick ??= keys.FirstOrDefault(key =>
            BaseMediaType(key).Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase));
        return pick ?? keys[0];
    }

    /// <summary>去掉媒体类型的参数（如 <c>;charset=utf-8</c>）并去除首尾空白。</summary>
    private static string BaseMediaType(string media)
    {
        var semicolon = media.IndexOf(';');
        return (semicolon >= 0 ? media[..semicolon] : media).Trim();
    }

    /// <summary>收尾每个分组：模型式逐操作决定是否落 <c>[Authorize]</c>，接口式判断能否把授权提到接口上。</summary>
    private void FinalizeGroups()
    {
        foreach (var key in _groupOrder)
        {
            var group = _groups[key];
            if (group.Operations.Count == 0)
            {
                continue;
            }

            if (_options.Style == ApiStyle.Model)
            {
                // 模型式没有接口级授权，每个请求类按自己的安全要求声明。
                foreach (var operation in group.Operations)
                {
                    operation.EmitAuthorize = operation.RequiresAuth;
                }
            }
            else
            {
                var first = group.Operations.FirstOrDefault(operation => operation.RequiresAuth);
                group.InterfaceAuthorize = first is not null
                                           && group.Operations.All(operation =>
                                               operation.RequiresAuth && operation.AuthScheme == first.AuthScheme);

                if (group.InterfaceAuthorize)
                {
                    group.AuthScheme = first!.AuthScheme ?? "Bearer";
                }

                foreach (var operation in group.Operations)
                {
                    operation.EmitAuthorize = operation.RequiresAuth && !group.InterfaceAuthorize;
                }
            }

            _spec.Groups.Add(group);
        }
    }

    /// <summary>
    /// 提取并校验路径模板占位符：识别规则与运行时一致，且同一占位符只能出现一次、模板里不能残留花括号。
    /// 不合规时返回 <c>false</c> 并给出原因（调用方记警告后跳过整个操作）。
    /// </summary>
    private static bool TryExtractPlaceholders(string path, out List<string> placeholders, out string? problem)
    {
        placeholders = new List<string>();
        problem = null;

        // 已出现的占位符名，重复使用会让运行时的替换表无法建立（一个占位符只能出现一次）。
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in PathPlaceholderPattern.Matches(path))
        {
            var name = match.Groups[1].Value;
            if (!seen.Add(name))
            {
                problem = $"路径模板 \"{path}\" 重复使用占位符 {{{name}}}：一个占位符只能出现一次。";
                return false;
            }

            placeholders.Add(name);
        }

        // 去掉全部合法占位符后仍有花括号：说明存在 { id }、{a,b} 这类运行时无法识别的写法。
        var leftover = PathPlaceholderPattern.Replace(path, string.Empty);
        if (leftover.Contains('{') || leftover.Contains('}'))
        {
            problem = $"路径模板 \"{path}\" 含无法识别的花括号：占位符只支持 {{name}} 形式的标识符"
                      + "（字母 / 下划线开头，字母 / 数字 / 下划线组成）。";
            return false;
        }

        return true;
    }

    /// <summary>确定方法名：有 operationId 时规范化使用，否则由 HTTP 动词 + 路径片段拼接，最后统一补 Async。</summary>
    private static string MethodName(string operationId, string method, string path)
    {
        if (!string.IsNullOrWhiteSpace(operationId))
        {
            return EnsureAsync(Naming.Pascal(operationId));
        }

        // HTTP 动词在 internal 模型里大写保存；拼方法名时先转小写，避免出现 GETMulti 这类缩写。
        var builder = new StringBuilder(Naming.Pascal(method.ToLowerInvariant()));
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            builder.Append(Naming.Pascal(segment.Trim('{', '}')));
        }

        return EnsureAsync(builder.ToString());
    }

    /// <summary>给方法名补 Async 后缀（已是 Async 结尾则不动）。</summary>
    private static string EnsureAsync(string name)
        => name.EndsWith("Async", StringComparison.Ordinal) ? name : name + "Async";

    /// <summary>摊平后的一个操作：路径、path item、方法、operation 与分组键，以及后续定好的方法名 / 请求类名。</summary>
    private sealed class OperationEntry
    {
        /// <summary>用原始文档节点构造条目；Name / ClassName 在命名阶段才赋值。</summary>
        public OperationEntry(string path, SpecNode pathItem, string method, SpecNode operation, string groupKey)
        {
            Path = path;
            PathItem = pathItem;
            Method = method;
            Operation = operation;
            GroupKey = groupKey;
        }

        /// <summary>路径模板原文。</summary>
        public string Path { get; }

        /// <summary>path item 节点（已解引用），用于合并路径级参数。</summary>
        public SpecNode PathItem { get; }

        /// <summary>大写 HTTP 方法。</summary>
        public string Method { get; }

        /// <summary>operation 节点（已解引用）。</summary>
        public SpecNode Operation { get; }

        /// <summary>分组键：tag 名、Default 或单接口模式的空串。</summary>
        public string GroupKey { get; }

        /// <summary>接口式下的方法名（<see cref="AssignNames"/> 赋值）。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>模型式下的请求类名（<see cref="AssignNames"/> 赋值）。</summary>
        public string ClassName { get; set; } = string.Empty;
    }

    /// <summary>security 节点的解析结果：是否存在、是否显式匿名、是否要求授权、第一个方案名。</summary>
    /// <param name="Present">文档里是否出现过 <c>security</c> 节点（空数组也算出现）。</param>
    /// <param name="ExplicitlyEmpty">是否显式写成空数组，即声明「本操作匿名」。</param>
    /// <param name="Required">是否要求授权（继承了全局 security 也算）。</param>
    /// <param name="SchemeName">第一个方案名；匿名或没有方案时为 null。</param>
    private readonly record struct SecurityInfo(
        bool Present,
        bool ExplicitlyEmpty,
        bool Required,
        string? SchemeName);

    /// <summary>请求体的媒体类型大类，决定生成哪种绑定形态。</summary>
    private enum MediaKind
    {
        /// <summary>JSON 请求体（<c>[Body]</c>）。</summary>
        Json,

        /// <summary>application/x-www-form-urlencoded 表单（仍按 <c>[Body]</c>）。</summary>
        Form,

        /// <summary>multipart/form-data，展开成多个 <c>[Part]</c>。</summary>
        Multipart,

        /// <summary>text/* 正文，映射成 string。</summary>
        Text,

        /// <summary>二进制正文，映射成 byte[]。</summary>
        Binary,

        /// <summary>其他媒体类型，按 schema 尽力映射。</summary>
        Other
    }

    /// <summary>响应体的媒体类型大类。</summary>
    private enum ResponseMedia
    {
        /// <summary>JSON 正文，按 schema 映射。</summary>
        Json,

        /// <summary>text/* 正文，映射成 string。</summary>
        Text,

        /// <summary>二进制正文，映射成 byte[]。</summary>
        Binary
    }
}
