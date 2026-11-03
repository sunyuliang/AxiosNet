namespace AxiosNet.Cli.Model;

/// <summary>映射到 C# 的类型引用：名字不含可空标注，可空由 <see cref="Nullable"/> 或调用方的 required 语义决定。</summary>
/// <param name="Name">类型名（如 <c>int</c>、<c>List&lt;string&gt;</c>），不含 <c>?</c> 标注。</param>
/// <param name="IsValueType">是否值类型，决定可空要不要写成 <c>Nullable&lt;T&gt;</c>。</param>
/// <param name="Nullable">是否需要可空标注（引用类型补 <c>?</c>）。</param>
internal readonly record struct TypeRef(string Name, bool IsValueType, bool Nullable)
{
    /// <summary>兜底类型：无法表达的结构一律降级成 <c>JsonElement</c>（值类型、默认非空）。</summary>
    public static readonly TypeRef JsonElement = new("JsonElement", true, false);

    /// <summary>字符串默认可空：OpenAPI 未标 required 的字符串在 C# 侧映射成 <c>string?</c>。</summary>
    public static readonly TypeRef String = new("string", false, true);

    /// <summary>按调用方的 required 语义与类型自身可空性拼出最终 C# 类型名（需要时追加 <c>?</c>）。</summary>
    public string Format(bool nullable) => nullable || Nullable ? Name + "?" : Name;
}

/// <summary>参数在线上的位置，决定生成 <c>[Path]</c> / <c>[Query]</c> / <c>[Header]</c> 中的哪一个。</summary>
internal enum ApiParameterLocation
{
    /// <summary>路径模板占位符。</summary>
    Path,

    /// <summary>查询串参数。</summary>
    Query,

    /// <summary>请求头参数。</summary>
    Header
}

/// <summary>响应交付形态，决定生成代码里的返回类型与流式常量 / 特性。</summary>
internal enum ApiResponseKind
{
    /// <summary>没有响应正文（204 / 无 content）。</summary>
    Void,

    /// <summary>JSON 正文，按 schema 映射成强类型。</summary>
    Json,

    /// <summary><c>text/*</c> 正文，映射成 <c>string</c>。</summary>
    Text,

    /// <summary>二进制正文，映射成 <c>byte[]</c>。</summary>
    Binary,

    /// <summary>流式正文（SSE / NDJSON / JSON 数组），按帧类型逐帧读取。</summary>
    Stream
}

/// <summary>生成出来的模型类别：类、枚举或常量类（枚举值不能作为成员名时的降级形态）。</summary>
internal enum ApiTypeKind
{
    /// <summary>普通对象模型。</summary>
    Class,

    /// <summary>字符串枚举。</summary>
    Enum,

    /// <summary>常量类：枚举值不全是合法标识符时用来保留线上字面量。</summary>
    Constants
}

/// <summary>一处导入缺口或降级：写进控制台摘要与 <c>--report</c>。</summary>
/// <param name="Code">缺口编号，用于去重与分类统计。</param>
/// <param name="Location">出现位置（如 <c>操作.状态码</c>），报告里靠它定位。</param>
/// <param name="Message">给人看的说明。</param>
internal sealed record ApiWarning(string Code, string Location, string Message);

/// <summary>警告收集器：去重并设置上限，避免病态文档刷屏。</summary>
internal sealed class WarningCollector
{
    /// <summary>单次运行的警告上限；超出后静默丢弃，报告与摘要都不会无限膨胀。</summary>
    private const int MaxWarnings = 500;

    /// <summary>已收集的警告（按加入顺序）。</summary>
    private readonly List<ApiWarning> _items = new();

    /// <summary>去重键集合（码 + 位置 + 消息），保证同一缺口只报一次。</summary>
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>对外只读视图，最终会拷进 <see cref="ApiSpec.Warnings"/>。</summary>
    public IReadOnlyList<ApiWarning> Items => _items;

    /// <summary>追加一条警告；达到上限或已存在同码同位置同消息时忽略。</summary>
    public void Add(string code, string location, string message)
    {
        if (_items.Count >= MaxWarnings || !_keys.Add(code + "\0" + location + "\0" + message))
        {
            return;
        }

        _items.Add(new ApiWarning(code, location, message));
    }
}

/// <summary>归一化后的整份 API：分组后的接口、模型与全部警告。</summary>
internal sealed class ApiSpec
{
    /// <summary>生成形态，来自命令行选项。</summary>
    public ApiStyle Style { get; set; } = ApiStyle.Model;

    /// <summary>文档标题（<c>info.title</c>），缺省为 <c>OpenAPI</c>。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>文档版本（<c>info.version</c>），仅用于注释与报告。</summary>
    public string SpecVersion { get; set; } = string.Empty;

    /// <summary>OpenAPI 版本原文（<c>openapi</c>），Swagger 2.0 归一化后固定为 <c>3.0.0</c>。</summary>
    public string OpenApiVersion { get; set; } = string.Empty;

    /// <summary>第一份 server 的 URL（报告展示用，不写进生成代码）。</summary>
    public string? ServerUrl { get; set; }

    /// <summary>按 tag 分好组的接口 / 请求模型集合。</summary>
    public List<ApiGroup> Groups { get; } = new();

    /// <summary>去重后的可复用模型（类 / 枚举 / 常量类）。</summary>
    public List<ApiType> Types { get; } = new();

    /// <summary>全部警告（读取阶段 + 落盘阶段）。</summary>
    public List<ApiWarning> Warnings { get; } = new();

    /// <summary>操作总数，跨分组求和。</summary>
    public int OperationCount => Groups.Sum(group => group.Operations.Count);
}

/// <summary>一个接口（默认按第一个 tag 分组）。</summary>
internal sealed class ApiGroup
{
    /// <summary>以 tag 名与已定好的接口名构造分组；模型式下接口名仅用于预留类型冲突。</summary>
    public ApiGroup(string tag, string interfaceName, string? description)
    {
        Tag = tag;
        InterfaceName = interfaceName;
        Description = description;
    }

    /// <summary>原始 tag 名（无 tag 时为 <c>Default</c>；单接口模式为空串）。</summary>
    public string Tag { get; }

    /// <summary>接口式下的接口名（如 <c>IUsersApi</c>）；模型式下只占位、不落盘。</summary>
    public string InterfaceName { get; }

    /// <summary>模型式下该组请求模型的文件名（不含扩展名），如 <c>PetsRequests</c>。</summary>
    public string RequestFileName { get; set; } = string.Empty;

    /// <summary>分组描述：优先取 <c>tags[].description</c>，单接口模式取 <c>info.description</c>。</summary>
    public string? Description { get; }

    /// <summary>该组内的全部操作。</summary>
    public List<ApiOperation> Operations { get; } = new();

    /// <summary>接口式下是否把 <c>[Authorize]</c> 提到接口上（要求全组操作同一 scheme）。</summary>
    public bool InterfaceAuthorize { get; set; }

    /// <summary>提到接口上的授权 scheme（默认 <c>Bearer</c>）。</summary>
    public string AuthScheme { get; set; } = "Bearer";
}

/// <summary>一个 HTTP 操作，是生成请求类 / 接口方法的全部依据。</summary>
internal sealed class ApiOperation
{
    /// <summary>HTTP 方法，大写（如 <c>GET</c>）。</summary>
    public string Method { get; set; } = string.Empty;

    /// <summary>路径模板原文（保留 <c>{name}</c> 占位符）。</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>文档里的 <c>operationId</c>（可能为空）。</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>接口式下的方法名（已确保以 Async 结尾、组内唯一）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>模型式下的请求类名（全局唯一），如 <c>ListPetsRequest</c>；接口式为空。</summary>
    public string ClassName { get; set; } = string.Empty;

    /// <summary>操作摘要（<c>summary</c>），用于生成方法注释。</summary>
    public string? Summary { get; set; }

    /// <summary>操作详细说明（<c>description</c>），与摘要不同时才追加进注释。</summary>
    public string? Description { get; set; }

    /// <summary>文档标记已废弃时为 true，生成 <c>[Obsolete]</c>。</summary>
    public bool Deprecated { get; set; }

    /// <summary>并入请求模型的 path / query / header 参数（已做名字去重）。</summary>
    public List<ApiParameter> Parameters { get; } = new();

    /// <summary>请求体声明；没有请求体时为 null。</summary>
    public ApiRequestBody? RequestBody { get; set; }

    /// <summary>响应无正文时为 true（模型式泛型参数固定 <c>object</c>）。</summary>
    public bool ResponseVoid { get; set; }

    /// <summary>成功响应的类型引用；无正文或未声明 schema 时可能为 null。</summary>
    public TypeRef? ReturnType { get; set; }

    /// <summary>成功响应的交付形态。</summary>
    public ApiResponseKind ResponseKind { get; set; } = ApiResponseKind.Void;

    /// <summary>流式响应帧格式（<c>ServerSentEvents</c> / <c>NewlineDelimitedJson</c> / <c>JsonArray</c>）。</summary>
    public string? StreamFormat { get; set; }

    /// <summary>方法级集合格式（<c>Multi</c> / <c>Csv</c> / <c>Ssv</c> / <c>Tsv</c> / <c>Pipes</c>）。</summary>
    public string? CollectionFormat { get; set; }

    /// <summary>该操作要求授权（已解析出可表达的 scheme）。</summary>
    public bool RequiresAuth { get; set; }

    /// <summary>操作显式声明 <c>security: []</c>，生成 <c>[AllowAnonymous]</c>。</summary>
    public bool Anonymous { get; set; }

    /// <summary>可表达的授权 scheme（<c>Bearer</c> / <c>Basic</c>），未授权时为 null。</summary>
    public string? AuthScheme { get; set; }

    /// <summary>本次最终是否落授权特性：模型式等于 <see cref="RequiresAuth"/>，接口式仅当未提到接口上。</summary>
    public bool EmitAuthorize { get; set; }

    /// <summary>警告与报告里使用的操作定位（<c>METHOD path</c>）。</summary>
    public string Location => $"{Method} {Path}";
}

/// <summary>一个可绑定到 <c>[Path]</c> / <c>[Query]</c> / <c>[Header]</c> 的参数。</summary>
internal sealed class ApiParameter
{
    /// <summary>参数位置。</summary>
    public ApiParameterLocation Location { get; set; }

    /// <summary>线上键名（OpenAPI 的 <c>name</c>）。</summary>
    public string WireName { get; set; } = string.Empty;

    /// <summary>生成的 C# 成员名（已做名字去重与关键字转义）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>参数类型。</summary>
    public TypeRef Type { get; set; }

    /// <summary>是否必填；决定可空标注与接口参数是否带默认值。</summary>
    public bool Required { get; set; }

    /// <summary>参数说明，写进生成代码的 XML 注释。</summary>
    public string? Description { get; set; }
}

/// <summary>请求体声明：媒体类型、绑定形态（单个 <c>[Body]</c> 或多个 <c>[Part]</c>）。</summary>
internal sealed class ApiRequestBody
{
    /// <summary>请求体的媒体类型（已去掉参数，如 <c>application/json</c>）。</summary>
    public string MediaType { get; set; } = "application/json";

    /// <summary>单个正文的类型；multipart 展开时不用此字段。</summary>
    public TypeRef Type { get; set; }

    /// <summary>生成的正文成员名（默认 <c>body</c>，冲突时自动加序号）。</summary>
    public string BodyName { get; set; } = "body";

    /// <summary>请求体是否必填。</summary>
    public bool Required { get; set; }

    /// <summary>是否以 multipart/form-data 发送（此时 <see cref="Parts"/> 才是正文）。</summary>
    public bool IsMultipart { get; set; }

    /// <summary>multipart 展开后的部件列表。</summary>
    public List<ApiPart> Parts { get; } = new();

    /// <summary>请求体说明。</summary>
    public string? Description { get; set; }
}

/// <summary>multipart 表单里的一个部件。</summary>
internal sealed class ApiPart
{
    /// <summary>线上字段名（Content-Disposition 的 <c>name</c>）。</summary>
    public string WireName { get; set; } = string.Empty;

    /// <summary>生成的 C# 成员名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>部件类型；<c>byte[]</c> 视为文件部件。</summary>
    public TypeRef Type { get; set; }

    /// <summary>是否必填。</summary>
    public bool Required { get; set; }

    /// <summary>是否文件部件（<c>byte[]</c>）。</summary>
    public bool IsFile { get; set; }

    /// <summary>部件说明。</summary>
    public string? Description { get; set; }
}

/// <summary>一个展开后的对象属性（模型与 multipart 部件共用）。</summary>
/// <param name="WireName">线上键名，写进 <c>[JsonPropertyName]</c>。</param>
/// <param name="Schema">该属性的 schema 节点，类型解析与嵌套展开都靠它。</param>
/// <param name="Required">是否在 schema 的 required 列表里。</param>
/// <param name="Description">属性说明；null 表示文档里没写。</param>
internal readonly record struct ApiObjectProperty(string WireName, Spec.SpecNode Schema, bool Required, string? Description);

/// <summary>一个可复用的生成类型：对象模型、字符串枚举或常量类。</summary>
internal sealed class ApiType
{
    /// <summary>生成的 C# 类型名（全命名空间唯一、避开运行库保留名）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>类型说明（schema 的 description / title）。</summary>
    public string? Description { get; set; }

    /// <summary>类型类别。</summary>
    public ApiTypeKind Kind { get; set; }

    /// <summary>类属性列表（仅 <see cref="ApiTypeKind.Class"/>）。</summary>
    public List<ApiProperty> Properties { get; } = new();

    /// <summary>枚举成员列表（仅 <see cref="ApiTypeKind.Enum"/>）。</summary>
    public List<ApiEnumMember> EnumMembers { get; } = new();

    /// <summary>常量列表（仅 <see cref="ApiTypeKind.Constants"/>）。</summary>
    public List<ApiConstant> Constants { get; } = new();
}

/// <summary>对象模型的一个属性。</summary>
internal sealed class ApiProperty
{
    /// <summary>生成的 C# 属性名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>线上键名，写进 <c>[JsonPropertyName]</c>。</summary>
    public string WireName { get; set; } = string.Empty;

    /// <summary>属性类型。</summary>
    public TypeRef Type { get; set; }

    /// <summary>是否在 schema 的 required 列表里。</summary>
    public bool Required { get; set; }

    /// <summary>属性说明。</summary>
    public string? Description { get; set; }

    /// <summary>是否为 <c>[JsonExtensionData]</c> 兜底属性（保留未知字段）。</summary>
    public bool IsExtensionData { get; set; }
}

/// <summary>字符串枚举成员：成员名与线上值一致。</summary>
/// <param name="Name">生成的 C# 成员名。</param>
/// <param name="WireValue">线上字面量，写进 <c>[EnumMember]</c>。</param>
internal sealed record ApiEnumMember(string Name, string WireValue);

/// <summary>常量类成员：用于无法作为枚举成员名的线上字面量。</summary>
/// <param name="Name">生成的常量名。</param>
/// <param name="Value">线上字面量的原样文本。</param>
internal sealed record ApiConstant(string Name, string Value);
