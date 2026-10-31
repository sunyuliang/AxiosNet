using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI.Reflection;

/// <summary>
/// 接口级声明的反射读取与静态校验：接口形状、路由特性、返回类型、参数绑定特性。
/// <see cref="ApiClient.Create{T}"/> 与 <see cref="ApiClient.ValidateInterfaces(Type[])"/> 共用这一份规则。
/// </summary>
internal static class InterfaceDeclaration
{
    /// <summary>接口级校验要读属性 / 事件，按最宽的要求标注。</summary>
    private const DynamicallyAccessedMemberTypes InterfaceMembers =
        DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.PublicProperties
        | DynamicallyAccessedMemberTypes.PublicEvents
        | DynamicallyAccessedMemberTypes.Interfaces;

    /// <summary>只读方法与方法特性（含基接口图）时的要求。</summary>
    private const DynamicallyAccessedMemberTypes InterfaceMethods =
        DynamicallyAccessedMemberTypes.PublicMethods
        | DynamicallyAccessedMemberTypes.Interfaces;

    /// <summary>校验一个接口声明的全部方法，返回全部错误（按方法顺序聚合）。</summary>
    public static IReadOnlyList<string> Validate([DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType)
    {
        var errors = new List<string>();

        if (!interfaceType.IsInterface)
        {
            errors.Add($"{interfaceType.FullName} 不是接口：接口级声明只能用于 interface 类型。");
            return errors;
        }

        if (interfaceType.IsGenericTypeDefinition)
        {
            errors.Add($"接口 {interfaceType.Name} 是开放泛型：请使用闭合的接口类型（如 IUserApi / IRepo<User>）。");
            return errors;
        }

        if (!interfaceType.IsVisible)
        {
            errors.Add($"接口 {interfaceType.Name} 不是 public 接口：DispatchProxy 无法为其生成实现，请把接口（及参数 / 返回类型）改为 public。");
        }

        if (interfaceType.GetProperties().Length > 0 || interfaceType.GetEvents().Length > 0)
        {
            errors.Add($"接口 {interfaceType.Name} 不能声明属性或事件：接口级声明只支持方法。");
        }

        var methods = CollectMethods(interfaceType);
        if (methods.Count == 0)
        {
            errors.Add($"接口 {interfaceType.Name} 没有可声明的方法。");
            return errors;
        }

        foreach (var method in methods)
        {
            errors.AddRange(ValidateMethod(interfaceType, method));
        }

        return errors;
    }

    /// <summary>校验通过则返回；否则把全部错误拼成一条 <see cref="InvalidOperationException"/>。</summary>
    public static void EnsureValid([DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType)
    {
        var errors = Validate(interfaceType);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    /// <summary>接口自身 + 全部基接口的方法，按 <see cref="MethodInfo"/> 去重。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "基接口类型来自已标注的接口图；调用方负责 root 接口及其基接口。")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "基接口类型来自已标注的接口图；调用方负责 root 接口及其基接口。")]
    public static IReadOnlyList<MethodInfo> CollectMethods([DynamicallyAccessedMembers(InterfaceMethods)] Type interfaceType)
    {
        var seen = new HashSet<MethodInfo>();
        var result = new List<MethodInfo>();
        var pending = new Stack<Type>();
        pending.Push(interfaceType);

        while (pending.Count > 0)
        {
            var type = pending.Pop();

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (seen.Add(method))
                {
                    result.Add(method);
                }
            }

            foreach (var baseInterface in type.GetInterfaces())
            {
                pending.Push(baseInterface);
            }
        }

        return result;
    }

    /// <summary>
    /// 接口链：自身在前，其余按「依赖的接口数量」降序（派生接口依赖更多，必然排在它继承的接口之前）；
    /// 同层再按全限定名排序，使顺序不再依赖 <see cref="Type.GetInterfaces"/> 的未定义返回顺序——
    /// 生成器侧用同一条规则，两条路径才能选中同一层。
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "基接口类型来自已标注的接口图；调用方负责 root 接口及其基接口。")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2072",
        Justification = "基接口类型来自已标注的接口图；调用方负责 root 接口及其基接口。")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2075",
        Justification = "基接口类型来自已标注的接口图；调用方负责 root 接口及其基接口。")]
    public static List<Type> CollectInterfaceChain([DynamicallyAccessedMembers(InterfaceMethods)] Type interfaceType)
    {
        var inherited = interfaceType.GetInterfaces();
        var chain = new List<Type>(inherited.Length + 1) { interfaceType };
        foreach (var baseInterface in inherited
            .OrderByDescending(static type => type.GetInterfaces().Length)
            .ThenBy(static type => GetChainSortKey(type), StringComparer.Ordinal))
        {
            chain.Add(baseInterface);
        }

        return chain;
    }

    /// <summary>接口是否至少有一个方法标注了路由特性（用于程序集扫描时跳过普通接口）。</summary>
    public static bool HasRoute([DynamicallyAccessedMembers(InterfaceMethods)] Type interfaceType)
    {
        foreach (var method in CollectMethods(interfaceType))
        {
            if (method.GetCustomAttributes<HttpApiAttribute>(inherit: false).Any())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>解析返回形态；<paramref name="responseType"/> 是正文类型（<c>Void</c> 与非泛型响应均为 <c>null</c>）。</summary>
    public static InterfaceReturnShape GetReturnShape(MethodInfo method, out Type? responseType)
    {
        var returnType = method.ReturnType;
        responseType = null;

        if (!returnType.IsGenericType)
        {
            return returnType == typeof(Task) ? InterfaceReturnShape.Void : InterfaceReturnShape.Unsupported;
        }

        if (returnType.GetGenericTypeDefinition() != typeof(Task<>))
        {
            return InterfaceReturnShape.Unsupported;
        }

        var inner = returnType.GetGenericArguments()[0];

        // 非泛型 AxiosResponse：正文按 JsonElement 原样交付。
        if (inner == typeof(AxiosResponse))
        {
            return InterfaceReturnShape.Response;
        }

        if (inner.IsGenericType && inner.GetGenericTypeDefinition() == typeof(AxiosResponse<>))
        {
            responseType = inner.GetGenericArguments()[0];
            return InterfaceReturnShape.Response;
        }

        responseType = inner;
        return InterfaceReturnShape.Data;
    }

    /// <summary>DispatchProxy 交回的 <see cref="MethodInfo"/> 可能声明在代理类型上（继承接口场景），解析回接口方法。</summary>
    public static MethodInfo ResolveMethod(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method)
    {
        if (method.DeclaringType is { IsInterface: true })
        {
            return method;
        }

        foreach (var candidate in CollectMethods(interfaceType))
        {
            if (candidate.Name == method.Name && SameSignature(candidate, method))
            {
                return candidate;
            }
        }

        return method;
    }

    /// <summary>校验单个接口方法：静态 / 泛型 / 路由 / 返回形态 / [Stream] / 参数形态，最后把绑定层错误一并聚合。</summary>
    private static IReadOnlyList<string> ValidateMethod(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method)
    {
        var errors = new List<string>();
        var display = $"接口 {interfaceType.Name} 的 {method.Name}";

        if (method.IsStatic)
        {
            errors.Add($"{display} 是静态方法：接口级声明只支持实例方法。");
        }

        if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
        {
            errors.Add($"{display} 是泛型方法：接口级声明暂不支持泛型方法。");
        }

        var routes = method.GetCustomAttributes<HttpApiAttribute>(inherit: false).ToArray();
        if (routes.Length == 0)
        {
            errors.Add($"{display} 缺少路由特性：请在方法上标注 [Get(\"...\")] / [Post(\"...\")] 等 HttpApiAttribute 子类。");
        }
        else if (routes.Length > 1)
        {
            var names = string.Join(" 与 ", routes.Select(r => "[" + TrimAttributeSuffix(r.GetType().Name) + "]"));
            errors.Add($"{display} 有多个路由特性（{names}）：一个方法只能声明一个。");
        }

        var shape = GetReturnShape(method, out var responseType);
        if (shape == InterfaceReturnShape.Unsupported)
        {
            errors.Add($"{display} 的返回类型是 {ReturnTypeShape.Describe(method.ReturnType)}："
                + "接口方法必须返回 Task<AxiosResponse<T>> / Task<T> / Task<AxiosResponse> / Task（不支持同步返回 / ValueTask）。");
        }
        else if (shape == InterfaceReturnShape.Data && ReturnTypeShape.IsStreaming(responseType!))
        {
            errors.Add($"{display} 返回 {ReturnTypeShape.Describe(method.ReturnType)}："
                + "流式正文按帧交付需要响应元数据，请声明 Task<AxiosResponse<IAsyncEnumerable<T>>>。");
        }

        // [Stream] 只用于流式方法；帧格式值必须落在枚举内。
        var stream = method.GetCustomAttribute<StreamAttribute>(inherit: false);
        if (stream is not null)
        {
            if (shape != InterfaceReturnShape.Response || responseType is null || !ReturnTypeShape.IsStreaming(responseType))
            {
                errors.Add($"{display} 标注了 [Stream]，但返回类型是 {ReturnTypeShape.Describe(method.ReturnType)}："
                    + "[Stream] 只用于返回 Task<AxiosResponse<IAsyncEnumerable<T>>> 的方法。");
            }
            else if (!Enum.IsDefined(typeof(AxiosStreamFormat), stream.Format))
            {
                errors.Add($"{display} 的 [Stream] 帧格式值 {(int)stream.Format} 无效：请使用 AxiosStreamFormat 的已定义值。");
            }
        }

        var parameters = method.GetParameters();
        var cancellationTokenCount = 0;

        foreach (var parameter in parameters)
        {
            var name = parameter.Name ?? "arg";
            var parameterDisplay = $"参数 {interfaceType.Name}.{method.Name}.{name}";

            if (parameter.ParameterType.IsByRef || parameter.IsOut)
            {
                errors.Add($"{parameterDisplay} 是 ref / out / in 参数：接口级声明不支持按引用传递。");
            }

            if (parameter.ParameterType.IsByRefLike)
            {
                errors.Add($"{parameterDisplay} 的类型 {parameter.ParameterType.Name} 是 ref struct：无法跨越接口调用绑定。");
            }

            var hasBinding = !(parameter.GetCustomAttribute<PathAttribute>() is null
                && parameter.GetCustomAttribute<QueryAttribute>() is null
                && parameter.GetCustomAttribute<HeaderAttribute>() is null
                && parameter.GetCustomAttribute<HeaderCollectionAttribute>() is null
                && parameter.GetCustomAttribute<PartAttribute>() is null
                && parameter.GetCustomAttribute<BodyAttribute>() is null);

            if (parameter.ParameterType == typeof(CancellationToken))
            {
                cancellationTokenCount++;
                if (hasBinding)
                {
                    errors.Add($"{parameterDisplay} 是 CancellationToken，不能同时标注 Path / Query / Header / HeaderCollection / Part / Body。");
                }

                continue;
            }

            var isIgnored = parameter.GetCustomAttribute<ApiIgnoreAttribute>() is not null;
            if (isIgnored && hasBinding)
            {
                errors.Add($"{parameterDisplay} 同时标注了 [ApiIgnore] 与参数特性：两者同用是二义性。");
            }
        }

        if (cancellationTokenCount > 1)
        {
            errors.Add($"{display} 声明了多个 CancellationToken 参数：最多一个。");
        }

        // 形状检查通过后再交给元数据构建，把绑定层错误（重名、模板、标量等）一并聚合。
        if (errors.Count == 0)
        {
            try
            {
                // 只构建契约，不创建泛型调用器。
                InterfaceMethodMetadataJit.Validate(interfaceType, method);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
            {
                errors.Add(ex.Message);
            }
        }

        return errors;
    }

    /// <summary>接口链同层排序键：定义元数据名 + 各类型实参的元数据名（与生成器的 <c>GetChainSortKey</c> 同规则）。</summary>
    private static string GetChainSortKey(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var definition = type.GetGenericTypeDefinition();
        return (definition.FullName ?? definition.Name) + "|" + string.Join("|", type.GetGenericArguments().Select(GetChainArgumentName));
    }

    private static string GetChainArgumentName(Type type)
    {
        if (type.IsArray)
        {
            return GetChainArgumentName(type.GetElementType()!) + "[]";
        }

        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        return definition.FullName ?? definition.Name;
    }

    private static bool SameSignature(MethodInfo left, MethodInfo right)
    {
        var leftParameters = left.GetParameters();
        var rightParameters = right.GetParameters();
        if (leftParameters.Length != rightParameters.Length)
        {
            return false;
        }

        for (var i = 0; i < leftParameters.Length; i++)
        {
            if (leftParameters[i].ParameterType != rightParameters[i].ParameterType)
            {
                return false;
            }
        }

        return true;
    }

    private static string TrimAttributeSuffix(string name)
        => name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^"Attribute".Length] : name;
}
