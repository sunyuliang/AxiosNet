using Microsoft.CodeAnalysis;

namespace AxiosNet.SourceGen
{
    /// <summary>生成期发现的一条诊断：在输出阶段转成 <see cref="Diagnostic"/>（位置来自用户源码）。</summary>
    internal sealed record DiagnosticInfo(
        DiagnosticDescriptor Descriptor,
        EquatableArray<string> MessageArgs,
        Location Location);

    /// <summary>成员绑定：只记录「有哪些绑定、名字是什么」，真正的合法性由运行时共享构建器校验。</summary>
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
    internal sealed record RequestMemberInfo(
        string Name,
        string FieldName,
        string Description,
        string TypeOfExpression,
        string GetterExpression,
        bool Ignore,
        EquatableArray<MemberBindingInfo> Bindings);

    /// <summary>一个请求模型的完整生成数据；<see cref="Emit"/> 为 false 时只携带诊断。</summary>
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
        bool Emit);

    /// <summary>生成器开关（来自 buildTransitive props 暴露的 CompilerVisibleProperty）。</summary>
    internal readonly record struct GeneratorOptions(bool Enabled, bool PublishAot);

    /// <summary>接口方法参数的绑定声明（与请求模型成员同一套绑定规则）。</summary>
    internal sealed record InterfaceParameterInfo(
        int Index,
        string Name,
        string TypeOfExpression,
        string Description,
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
        Response = 0,
        Data = 1,
        Void = 2
    }

    /// <summary>一个接口方法：路由 / 静态头 / 授权 / 参数绑定 / 返回形态；生成代理与注册声明共用。</summary>
    internal sealed record InterfaceMethodInfo(
        int Index,
        string Name,
        string DeclaringInterfaceExpression,
        string RouteMethod,
        string RoutePath,
        EquatableArray<string> StaticHeaders,
        bool Multipart,
        bool AllowAnonymous,
        string? AuthorizeScheme,
        bool IsStreaming,
        int StreamFormat,
        string? ResponseTypeExpression,
        string? StreamFrameTypeExpression,
        InterfaceReturnKind ReturnKind,
        int CancellationTokenIndex,
        EquatableArray<InterfaceParameterInfo> Parameters);

    /// <summary>对象形状的一个成员：展平时的名字与直接取值委托（生成代码里的 lambda 文本）。</summary>
    internal sealed record ShapeMemberInfo(string Name, string FieldName, bool Ignored, string GetterExpression);

    /// <summary>
    /// 一个可注册的对象形状：命名类型用 <c>default(T)</c> 推断类型参数，匿名类型用结构相同的样本推断；
    /// <see cref="MapInterfaceExpression"/> 非 null 时同一注册块里再登记映射访问器；
    /// <see cref="RegisterShape"/> 为 false 时只登记映射访问器（自定义只读字典不是 POCO 展平对象）。
    /// </summary>
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
    internal sealed record ShapeBatchInfo(
        EquatableArray<ShapeInfo> Shapes,
        EquatableArray<DiagnosticInfo> Diagnostics);

    /// <summary>一个接口（可能是闭合泛型）的完整生成数据；<see cref="Emit"/> 为 false 时只携带诊断。</summary>
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
    internal sealed record InterfaceBatchInfo(EquatableArray<InterfaceModelInfo> Models)
    {
        public static readonly InterfaceBatchInfo Empty = new(EquatableArray<InterfaceModelInfo>.Empty);
    }
}
