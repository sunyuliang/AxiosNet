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
}
