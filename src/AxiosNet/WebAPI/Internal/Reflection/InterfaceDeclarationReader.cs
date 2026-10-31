using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Internal.Reflection;

/// <summary>
/// JIT 反射读取器：把一个接口方法上的特性读成 <see cref="InterfaceMethodDeclaration"/>。
/// 读出的声明与源生成器烘焙的声明同形，之后的绑定与校验走同一份构建器；本文件是接口路径唯一依赖反射的地方。
/// </summary>
internal static class InterfaceDeclarationReader
{
    private const DynamicallyAccessedMemberTypes InterfaceMembers =
        DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.Interfaces;

    /// <summary>读出一个接口方法的声明（连同接口级声明：静态头、授权）。</summary>
    public static (InterfaceDeclarationData Owner, InterfaceMethodDeclaration Method) Read(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method)
    {
        var chain = InterfaceDeclaration.CollectInterfaceChain(interfaceType);

        var owner = new InterfaceDeclarationData { InterfaceType = interfaceType };
        CollectStaticHeaders(chain, owner.StaticHeaders);
        ReadOwnerAuthorization(chain, owner);

        var declaration = new InterfaceMethodDeclaration { Name = method.Name };
        ReadRoute(method, declaration);
        declaration.Multipart = method.GetCustomAttribute<MultipartAttribute>(inherit: false) is not null;

        if (method.GetCustomAttribute<HeadersAttribute>(inherit: false) is { } headers)
        {
            declaration.StaticHeaders.AddRange(headers.Headers);
        }

        ReadMethodAuthorization(method, declaration);
        ReadParameters(interfaceType, method, declaration.Parameters);

        declaration.ReturnShape = InterfaceDeclaration.GetReturnShape(method, out var responseType);
        declaration.ResponseType = responseType;
        declaration.ReturnTypeText = ReturnTypeShape.Describe(method.ReturnType);

        var stream = method.GetCustomAttribute<StreamAttribute>(inherit: false);
        declaration.HasStreamAttribute = stream is not null;
        declaration.StreamFormat = stream?.Format ?? AxiosStreamFormat.NewlineDelimitedJson;

        return (owner, declaration);
    }

    /// <summary>路由特性必须恰好一个；缺失或重复时留空，由构建期统一报错（与生成路径同一句文案）。</summary>
    private static void ReadRoute(MethodInfo method, InterfaceMethodDeclaration declaration)
    {
        var routes = method.GetCustomAttributes<HttpApiAttribute>(inherit: false).ToArray();
        if (routes.Length == 1)
        {
            declaration.MethodName = routes[0].Method.Method;
            declaration.Path = routes[0].Path;
        }
    }

    /// <summary>
    /// 读参数绑定：按签名顺序列出全部参数（取消令牌也列出，构建期按类型跳过并校验唯一性）。
    /// 取消令牌唯一性刻意留到构建期判，与路由特性等其余错误的先后顺序保持一致。
    /// </summary>
    private static void ReadParameters(
        Type interfaceType,
        MethodInfo method,
        List<InterfaceParameterDeclaration> into)
    {
        var parameters = method.GetParameters();

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var name = parameter.Name ?? $"arg{index}";
            var declaration = new InterfaceParameterDeclaration
            {
                Index = index,
                Name = name,
                ValueType = parameter.ParameterType,
                Description = $"{interfaceType.Name}.{method.Name}.{name}"
            };

            into.Add(declaration);

            if (parameter.ParameterType == typeof(CancellationToken))
            {
                continue;
            }

            if (parameter.GetCustomAttribute<PathAttribute>() is { } path)
            {
                declaration.Path = true;
                declaration.PathName = path.Name;
            }

            if (parameter.GetCustomAttribute<QueryAttribute>() is { } query)
            {
                declaration.Query = true;
                declaration.QueryName = query.Name;
            }

            if (parameter.GetCustomAttribute<HeaderAttribute>() is { } header)
            {
                declaration.Header = true;
                declaration.HeaderName = header.Name;
            }

            declaration.HeaderCollection = parameter.GetCustomAttribute<HeaderCollectionAttribute>() is not null;
            declaration.Body = parameter.GetCustomAttribute<BodyAttribute>() is not null;
            declaration.Ignore = parameter.GetCustomAttribute<ApiIgnoreAttribute>() is not null;

            if (parameter.GetCustomAttribute<PartAttribute>() is { } part)
            {
                declaration.Part = true;
                declaration.PartName = part.Name;
                declaration.PartFileName = part.FileName;
                declaration.PartContentType = part.ContentType;
            }
        }
    }

    /// <summary>接口级静态头：基接口在前、派生接口在后（同名后者覆盖）。</summary>
    private static void CollectStaticHeaders(List<Type> chain, List<string> into)
    {
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].GetCustomAttribute<HeadersAttribute>(inherit: false) is not { } headers)
            {
                continue;
            }

            foreach (var declaration in headers.Headers)
            {
                if (declaration is not null)
                {
                    into.Add(declaration);
                }
            }
        }
    }

    /// <summary>接口级授权：链上第一个声明了授权的接口胜出（派生接口压过基接口）。</summary>
    private static void ReadOwnerAuthorization(List<Type> chain, InterfaceDeclarationData owner)
    {
        foreach (var type in chain)
        {
            if (type != typeof(object) && AuthorizationDeclaration.TryRead(type, out var allowAnonymous, out var scheme))
            {
                owner.AllowAnonymous = allowAnonymous;
                owner.AuthorizeScheme = scheme;
                return;
            }
        }
    }

    /// <summary>方法级授权：只认方法自己声明的特性（方法级压过任何接口级）。</summary>
    private static void ReadMethodAuthorization(MethodInfo method, InterfaceMethodDeclaration declaration)
    {
        if (AuthorizationDeclaration.TryRead(method, out var allowAnonymous, out var scheme))
        {
            declaration.AllowAnonymous = allowAnonymous;
            declaration.AuthorizeScheme = scheme;
        }
    }
}
