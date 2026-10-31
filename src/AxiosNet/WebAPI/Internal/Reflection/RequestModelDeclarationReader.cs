using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.Internal;
using AxiosNet.Internal.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Internal.Reflection;

/// <summary>
/// JIT 反射读取器：把请求模型上的特性读成 <see cref="RequestModelDeclaration"/>。
/// 只在 <see cref="RuntimeSupport.IsDynamicCodeSupported"/> 为 true 时调用；AOT 下必须由生成器提供同一份声明。
/// </summary>
internal static class RequestModelDeclarationReader
{
    private const DynamicallyAccessedMemberTypes ModelMembers =
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces;

    /// <summary>读出一份请求模型声明（反射只发生在这里；后续绑定与生成路径共用同一份构建器）。</summary>
    public static RequestModelDeclaration Read(
        [DynamicallyAccessedMembers(ModelMembers)] Type requestType)
    {
        var route = requestType.GetCustomAttribute<HttpApiAttribute>(inherit: false)
            ?? throw new InvalidOperationException(
                $"请求模型 {requestType.Name} 缺少路由特性：请在类上标注 [Get(\"...\")] / [Post(\"...\")] 等 HttpApiAttribute 子类。");

        var declaration = new RequestModelDeclaration
        {
            ModelType = requestType,
            MethodName = route.Method.Method,
            Path = route.Path,
            ImplementsApiRequest = ImplementsApiRequest(requestType),
            Multipart = requestType.GetCustomAttribute<MultipartAttribute>() is not null
        };

        // 类级静态头：基类在前、派生类在后（同名后者覆盖）
        var chain = BuildInheritanceChain(requestType);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].GetCustomAttribute<HeadersAttribute>(inherit: false) is { } headers)
            {
                foreach (var header in headers.Headers)
                {
                    if (header is not null)
                    {
                        declaration.StaticHeaders.Add(header);
                    }
                }
            }
        }

        foreach (var property in EnumerateBindableProperties(requestType))
        {
            var member = new RequestModelMemberDeclaration
            {
                Name = property.Name,
                FieldName = ReflectionCache.GetFieldName(property),
                ValueType = property.PropertyType,
                GetValue = target => PropertyAccessor.GetValue(property, target),
                Description = $"{property.DeclaringType?.Name ?? requestType.Name}.{property.Name}"
            };

            if (property.GetCustomAttribute<ApiIgnoreAttribute>() is not null)
            {
                // [ApiIgnore] 在 override 属性上会继承基类的参数特性：只有本属性自己声明的才算冲突。
                member.Ignore = true;
                if (CountOwnParameterAttributes(property) > 0)
                {
                    ApplyAttributes(member, property, inherit: false);
                }

                declaration.Members.Add(member);
                continue;
            }

            ApplyAttributes(member, property, inherit: true);
            declaration.Members.Add(member);
        }

        // 类级授权：链上第一个声明了授权的类型胜出（派生类压过基类）。
        foreach (var type in chain)
        {
            if (type != typeof(object) && AuthorizationDeclaration.TryRead(type, out var allowAnonymous, out var scheme))
            {
                declaration.AllowAnonymous = allowAnonymous;
                declaration.AuthorizeScheme = scheme;
                break;
            }
        }

        return declaration;
    }

    /// <summary>把参数特性写进声明；<paramref name="inherit"/> 为 true 时沿 override 链继承基类特性。</summary>
    private static void ApplyAttributes(RequestModelMemberDeclaration member, PropertyInfo property, bool inherit)
    {
        if (property.GetCustomAttribute<PathAttribute>(inherit) is { } path)
        {
            member.Path = true;
            member.PathName = path.Name;
        }

        if (property.GetCustomAttribute<QueryAttribute>(inherit) is { } query)
        {
            member.Query = true;
            member.QueryName = query.Name;
        }

        if (property.GetCustomAttribute<HeaderAttribute>(inherit) is { } header)
        {
            member.Header = true;
            member.HeaderName = header.Name;
        }

        if (property.GetCustomAttribute<HeaderCollectionAttribute>(inherit) is not null)
        {
            member.HeaderCollection = true;
        }

        if (property.GetCustomAttribute<PartAttribute>(inherit) is { } part)
        {
            member.Part = true;
            member.PartName = part.Name;
            member.PartFileName = part.FileName;
            member.PartContentType = part.ContentType;
        }

        if (property.GetCustomAttribute<BodyAttribute>(inherit) is not null)
        {
            member.Body = true;
        }
    }

    /// <summary>本属性自身（不含基类继承）声明了几个参数特性。</summary>
    private static int CountOwnParameterAttributes(PropertyInfo property)
        => (property.GetCustomAttribute<PathAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<QueryAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<HeaderAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<HeaderCollectionAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<PartAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<BodyAttribute>(inherit: false) is not null ? 1 : 0);

    /// <summary>类型自身在前的继承链（不包含 <see cref="object"/>）。</summary>
    private static List<Type> BuildInheritanceChain(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type requestType)
    {
        var chain = new List<Type>();
        for (var type = requestType; type is not null && type != typeof(object); type = type.BaseType)
        {
            chain.Add(type);
        }

        return chain;
    }

    /// <summary>公共可读实例属性（跳过索引器），按声明顺序遍历。</summary>
    private static IEnumerable<PropertyInfo> EnumerateBindableProperties(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type requestType)
        => requestType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "仅 JIT 反射路径可达；AOT 下由源生成器在声明里写入 ImplementsApiRequest 标志。")]
    private static bool ImplementsApiRequest(Type type)
        => type.GetInterfaces().Any(candidate
            => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IApiRequest<>));
}
