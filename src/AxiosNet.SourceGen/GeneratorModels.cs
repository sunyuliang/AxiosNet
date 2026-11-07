using Microsoft.CodeAnalysis;

namespace AxiosNet.SourceGen
{
    /// <summary>生成期发现的一条诊断：在输出阶段转成 <see cref="Diagnostic"/>（位置来自用户源码）。</summary>
    /// <param name="Descriptor">命中的规则（ID、标题与严重级别）。</param>
    /// <param name="MessageArgs">消息模板的实参，报告时按顺序代入。</param>
    /// <param name="Location">用户源码中的位置，用来把诊断指到出问题的那一行。</param>
    internal sealed record DiagnosticInfo(
        DiagnosticDescriptor Descriptor,
        EquatableArray<string> MessageArgs,
        Location Location);

    /// <summary>成员绑定：只记录「有哪些绑定、名字是什么」，真正的合法性由运行时共享构建器校验。</summary>
    /// <param name="Path">该成员是否绑定到路由模板里的占位符。</param>
    /// <param name="PathName">路径参数名；null 表示沿用成员名。</param>
    /// <param name="Query">是否作为查询参数写出。</param>
    /// <param name="QueryName">查询参数名；null 表示沿用字段名。</param>
    /// <param name="Header">是否作为请求头写出。</param>
    /// <param name="HeaderName">请求头名；null 表示沿用字段名。</param>
    /// <param name="HeaderCollection">整个成员当成一个多值头（集合元素逐项写入同一头）。</param>
    /// <param name="Body">是否作为请求体内容（每次请求只能有一个）。</param>
    /// <param name="Part">是否作为 <c>multipart/form-data</c> 的一个部件。</param>
    /// <param name="PartName">部件名；null 表示沿用字段名。</param>
    /// <param name="PartFileName">文件部件的文件名，仅文件流 / 字节内容有意义。</param>
    /// <param name="PartContentType">部件的 <c>Content-Type</c>，缺省按内容推断。</param>
    internal sealed record MemberBindingInfo(
        bool Path,
        string? PathName,
        bool Query,
        string? QueryName,
        bool Header,
        string? HeaderName,
        bool HeaderCollection,
        bool Body,
        bool Part,
        string? PartName,
        string? PartFileName,
        string? PartContentType);

    /// <summary>请求模型的一个属性（已按反射语义解析：override 继承、[ApiIgnore]、[JsonPropertyName]）。</summary>
    /// <param name="Name">CLR 属性名，元数据里的成员键。</param>
    /// <param name="FieldName">展平后使用的字段名（<c>[JsonPropertyName]</c> 优先，否则属性名）。</param>
    /// <param name="Description">错误消息里的定位文本（<c>声明类型.成员名</c>），与反射侧 <c>DeclaringType.Name</c> 口径一致。</param>
    /// <param name="TypeOfExpression"><c>typeof(…)</c> 的源码文本，直接拼进生成代码。</param>
    /// <param name="GetterExpression">直接取值的静态 lambda 文本；运行期不碰 <c>PropertyInfo</c>。</param>
    /// <param name="Ignore">被 <c>[ApiIgnore]</c> 标记，展平时跳过。</param>
    /// <param name="FormatExpression">值格式化 lambda 文本（来自 <c>[Format]</c>）；null 表示原样输出。</param>
    /// <param name="Bindings">命中的绑定目标；多个目标同时成立时留给构建期报错。</param>
    internal sealed record RequestMemberInfo(
        string Name,
        string FieldName,
        string Description,
        string TypeOfExpression,
        string GetterExpression,
        bool Ignore,
        string? FormatExpression,
        EquatableArray<MemberBindingInfo> Bindings,
        string? ContentType = null,
        int? QueryCollectionFormat = null,
        bool HasStreamAttribute = false,
        int StreamFormat = 0);

    /// <summary>一个请求模型的完整生成数据；<see cref="Emit"/> 为 false 时只携带诊断。</summary>
    /// <param name="FullTypeName">模型类型的完整名（含命名空间），输出阶段按它去重。</param>
    /// <param name="ClassName">生成的元数据类名。</param>
    /// <param name="HintName">生成的 <c>.g.cs</c> 文件名，也是 <c>AddSource</c> 的去重键。</param>
    /// <param name="RouteMethod">路由特性上声明的 HTTP 方法文本（如 <c>GET</c>）。</param>
    /// <param name="RoutePath">路由模板原文；占位符在运行期按路径绑定替换。</param>
    /// <param name="StaticHeaders">特性上写死的头（<c>Name: Value</c> 形式），生成期已解析完。</param>
    /// <param name="Multipart">是否按 <c>multipart/form-data</c> 组装正文。</param>
    /// <param name="AllowAnonymous">是否声明了免鉴权。</param>
    /// <param name="AuthorizeScheme">要求的鉴权方案；null 表示用默认方案。</param>
    /// <param name="ImplementsApiRequest">模型是否实现 <c>IApiRequest&lt;T&gt;</c>（启动期校验用，免得运行期反射接口）。</param>
    /// <param name="Members">待注册的成员及其绑定。</param>
    /// <param name="Diagnostics">这一模型相关的诊断，输出阶段统一上报。</param>
    /// <param name="Emit">是否可以出代码；false 表示这个模型只用来携带诊断。</param>
    internal sealed record RequestModelInfo(
        string FullTypeName,
        string ClassName,
        string HintName,
        string RouteMethod,
        string RoutePath,
        EquatableArray<string> StaticHeaders,
        bool Multipart,
        bool AllowAnonymous,
        string? AuthorizeScheme,
        bool ImplementsApiRequest,
        EquatableArray<RequestMemberInfo> Members,
        EquatableArray<DiagnosticInfo> Diagnostics,
        bool Emit,
        string? ContentType = null,
        int? QueryCollectionFormat = null,
        bool HasStreamAttribute = false,
        int StreamFormat = 0);

    /// <summary>生成器开关（来自 buildTransitive props 暴露的 CompilerVisibleProperty）。</summary>
    /// <param name="Enabled">总开关（<c>AxiosNetSourceGen</c>，缺省开启，只有显式写 <c>false</c> 才关）。</param>
    /// <param name="PublishAot">项目是否按 Native AOT 发布：关掉生成时要在 AOT 下报 AXA012。</param>
    internal readonly record struct GeneratorOptions(bool Enabled, bool PublishAot);

    /// <summary>接口方法参数的绑定声明（与请求模型成员同一套绑定规则）。</summary>
    /// <param name="Index">参数在方法签名里的序号（从 0 起）。</param>
    /// <param name="Name">参数名，生成代码里的形参名。</param>
    /// <param name="TypeOfExpression"><c>typeof(…)</c> 的源码文本。</param>
    /// <param name="Description">错误消息里的定位文本（<c>接口.方法.参数</c>）。</param>
    /// <param name="FormatExpression">值格式化 lambda 文本；null 表示原样输出。</param>
    /// <param name="Ignore">该参数被忽略，不参与任何绑定。</param>
    /// <param name="Path">是否绑定到路由模板里的占位符。</param>
    /// <param name="PathName">路径参数名；null 表示沿用参数名。</param>
    /// <param name="Query">是否作为查询参数写出。</param>
    /// <param name="QueryName">查询参数名；null 表示沿用参数名。</param>
    /// <param name="Header">是否作为请求头写出。</param>
    /// <param name="HeaderName">请求头名；null 表示沿用参数名。</param>
    /// <param name="HeaderCollection">整个参数当成一个多值头。</param>
    /// <param name="Body">是否作为请求体内容。</param>
    /// <param name="Part">是否作为 <c>multipart/form-data</c> 的一个部件。</param>
    /// <param name="PartName">部件名；null 表示沿用参数名。</param>
    /// <param name="PartFileName">文件部件的文件名。</param>
    /// <param name="PartContentType">部件的 <c>Content-Type</c>，缺省按内容推断。</param>
    internal sealed record InterfaceParameterInfo(
        int Index,
        string Name,
        string TypeOfExpression,
        string Description,
        string? FormatExpression,
        bool Ignore,
        bool Path,
        string? PathName,
        bool Query,
        string? QueryName,
        bool Header,
        string? HeaderName,
        bool HeaderCollection,
        bool Body,
        bool Part,
        string? PartName,
        string? PartFileName,
        string? PartContentType);

    /// <summary>接口方法的返回形态（生成器侧；写入 <c>InterfaceBuilder.Returns*</c>）。</summary>
    internal enum InterfaceReturnKind
    {
        /// <summary>返回完整 <c>AxiosResponse</c>（状态码、头与正文都要）。</summary>
        Response = 0,

        /// <summary>只返回反序列化后的数据。</summary>
        Data = 1,

        /// <summary>无返回值，只关心是否成功。</summary>
        Void = 2
    }

    /// <summary>一个接口方法：路由 / 静态头 / 授权 / 参数绑定 / 返回形态；生成代理与注册声明共用。</summary>
    /// <param name="Index">方法在接口里的序号，生成委托字段与缓存槽位时按它定位。</param>
    /// <param name="Name">方法名。</param>
    /// <param name="DeclaringInterfaceExpression">声明该方法的接口类型表达式；调用默认实现时用它限定，避免落到派生接口。</param>
    /// <param name="RouteMethod">路由特性上声明的 HTTP 方法文本。</param>
    /// <param name="RoutePath">路由模板原文。</param>
    /// <param name="StaticHeaders">接口 / 方法上写死的头。</param>
    /// <param name="Multipart">是否按 <c>multipart/form-data</c> 组装正文。</param>
    /// <param name="ContentType">正文内容类型（<c>[BodyContentType]</c>）；null 表示按默认 JSON。</param>
    /// <param name="QueryCollectionFormat">集合查询参数的分隔风格（<c>AxiosCollectionFormat</c> 的整数值）；null 表示未声明。</param>
    /// <param name="AllowAnonymous">是否声明了免鉴权。</param>
    /// <param name="AuthorizeScheme">要求的鉴权方案；null 表示用默认方案。</param>
    /// <param name="IsStreaming">返回流式响应（<c>IAsyncEnumerable</c> 逐帧读取）。</param>
    /// <param name="StreamFormat">帧格式（<c>AxiosStreamFormat</c> 的整数值）。</param>
    /// <param name="ResponseTypeExpression">要反序列化的返回类型表达式；无返回形态为 null。</param>
    /// <param name="StreamFrameTypeExpression">流式响应的元素类型表达式；非流式为 null。</param>
    /// <param name="ReturnKind">返回形态，决定生成方法的签名。</param>
    /// <param name="CancellationTokenIndex"><c>CancellationToken</c> 参数的序号；-1 表示方法没有该参数。</param>
    /// <param name="Parameters">参数绑定，顺序与签名一致。</param>
    internal sealed record InterfaceMethodInfo(
        int Index,
        string Name,
        string DeclaringInterfaceExpression,
        string RouteMethod,
        string RoutePath,
        EquatableArray<string> StaticHeaders,
        bool Multipart,
        string? ContentType,
        int? QueryCollectionFormat,
        bool AllowAnonymous,
        string? AuthorizeScheme,
        bool IsStreaming,
        int StreamFormat,
        string? ResponseTypeExpression,
        string? StreamFrameTypeExpression,
        InterfaceReturnKind ReturnKind,
        int CancellationTokenIndex,
        EquatableArray<InterfaceParameterInfo> Parameters);

    /// <summary>对象形状的一个成员：展平时的名字、直接取值委托（生成代码里的 lambda 文本）与成员级格式化。</summary>
    /// <param name="Name">成员名。</param>
    /// <param name="FieldName">展平后的字段名（<c>[JsonPropertyName]</c> 生效后的名字）。</param>
    /// <param name="Ignored">被 <c>[ApiIgnore]</c> 标记，展平时跳过。</param>
    /// <param name="GetterExpression">直接取值的静态 lambda 文本。</param>
    /// <param name="FormatExpression">值格式化 lambda 文本；null 表示原样输出。</param>
    internal sealed record ShapeMemberInfo(
        string Name,
        string FieldName,
        bool Ignored,
        string GetterExpression,
        string? FormatExpression);

    /// <summary>
    /// 一个可注册的对象形状：命名类型用 <c>default(T)</c> 推断类型参数，匿名类型用结构相同的样本推断；
    /// <see cref="MapInterfaceExpression"/> 非 null 时同一注册块里再登记映射访问器；
    /// <see cref="RegisterShape"/> 为 false 时只登记映射访问器（自定义只读字典不是 POCO 展平对象）。
    /// </summary>
    /// <param name="Key">去重键：<c>named:</c> / <c>map:</c> / <c>anon:</c> 前缀加类型标识。</param>
    /// <param name="ClassName">生成的形状类名，由 <see cref="Key"/> 哈希得到。</param>
    /// <param name="HintName">生成的 <c>.g.cs</c> 文件名。</param>
    /// <param name="TypeExpression">命名类型的表达式；匿名类型没有可写的名字，为 null。</param>
    /// <param name="SampleExpression">匿名类型的样本表达式，靠它推断注册时要用的类型参数。</param>
    /// <param name="Members">参与展平的成员。</param>
    /// <param name="MapInterfaceExpression">只读字典接口表达式；非 null 时额外登记映射访问器。</param>
    /// <param name="RegisterShape">是否注册成 POCO 形状；false 表示只登记映射访问器。</param>
    internal sealed record ShapeInfo(
        string Key,
        string ClassName,
        string HintName,
        string? TypeExpression,
        string? SampleExpression,
        EquatableArray<ShapeMemberInfo> Members,
        string? MapInterfaceExpression,
        bool RegisterShape);

    /// <summary>一次语法候选展开出的形状与诊断（同一类型可能由多个候选各自携带，输出阶段按 <see cref="ShapeInfo.Key"/> 去重）。</summary>
    /// <param name="Shapes">这一批展开出的形状。</param>
    /// <param name="Diagnostics">展开期间发现的问题。</param>
    internal sealed record ShapeBatchInfo(
        EquatableArray<ShapeInfo> Shapes,
        EquatableArray<DiagnosticInfo> Diagnostics);

    /// <summary>一个接口（可能是闭合泛型）的完整生成数据；<see cref="Emit"/> 为 false 时只携带诊断。</summary>
    /// <param name="FullTypeName">接口完整名（闭合泛型带实参），输出阶段按它去重。</param>
    /// <param name="ClassName">生成的代理类名。</param>
    /// <param name="HintName">生成的 <c>.g.cs</c> 文件名。</param>
    /// <param name="InterfaceTypeExpression">接口类型表达式，原样写进生成代码。</param>
    /// <param name="StaticHeaders">接口级写死的头，方法未覆盖时沿用。</param>
    /// <param name="AllowAnonymous">接口级免鉴权声明，方法未覆盖时沿用。</param>
    /// <param name="AuthorizeScheme">接口级鉴权方案；null 表示用默认方案。</param>
    /// <param name="OpenGeneric">是否为未闭合的泛型声明：运行期无法实例化，只报诊断不出代理。</param>
    /// <param name="GenericDefinitionFullName">由闭合泛型调用点展开时记录原泛型定义的全名；直接声明为 null。</param>
    /// <param name="Methods">要生成代理的方法。</param>
    /// <param name="Diagnostics">这一接口相关的诊断。</param>
    /// <param name="Emit">是否可以出代码；false 表示只携带诊断。</param>
    internal sealed record InterfaceModelInfo(
        string FullTypeName,
        string ClassName,
        string HintName,
        string InterfaceTypeExpression,
        EquatableArray<string> StaticHeaders,
        bool AllowAnonymous,
        string? AuthorizeScheme,
        bool OpenGeneric,
        string? GenericDefinitionFullName,
        EquatableArray<InterfaceMethodInfo> Methods,
        EquatableArray<DiagnosticInfo> Diagnostics,
        bool Emit);

    /// <summary>
    /// 一次语法候选展开出的接口模型。<c>ImmutableArray</c> 按底层数组引用比较，
    /// 直接当管线值会让每次编辑都被判成「已修改」，增量缓存失效，所以外面套一层值相等记录。
    /// </summary>
    /// <param name="Models">一次候选展开的全部接口模型（含因诊断而未输出的）。</param>
    internal sealed record InterfaceBatchInfo(EquatableArray<InterfaceModelInfo> Models)
    {
        /// <summary>空批次：管线的初始值，合并时等价于「什么也没有」。</summary>
        public static readonly InterfaceBatchInfo Empty = new(EquatableArray<InterfaceModelInfo>.Empty);
    }
}
