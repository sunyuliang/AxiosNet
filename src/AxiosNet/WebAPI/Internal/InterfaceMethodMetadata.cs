using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>接口方法的绑定元数据：路由、参数绑定计划、授权、返回类型特化后的调用器；缓存键包含被代理的接口。</summary>
internal sealed class InterfaceMethodMetadata
{
    private const DynamicallyAccessedMemberTypes InterfaceMembers =
        DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.Interfaces;

    private static readonly ConcurrentDictionary<(Type Interface, MethodInfo Method), InterfaceMethodMetadata> Cache = new();

    private readonly string? _buildError;
    private readonly Type? _interfaceType;
    private readonly string? _methodName;
    private readonly HttpMethod? _method;
    private readonly string? _pathTemplate;
    private readonly BindingPlan? _plan;
    private readonly ApiAuthorization _authorization;
    private readonly int _cancellationTokenIndex;
    private readonly bool _isMultipart;
    private readonly PartBinding[] _partBindings;
    private readonly Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object>? _invoker;
    private readonly Func<Exception, object>? _fault;

    private InterfaceMethodMetadata(string buildError)
    {
        _buildError = buildError;
        _partBindings = [];
    }

    private InterfaceMethodMetadata(
        Type interfaceType,
        string methodName,
        HttpMethod method,
        string pathTemplate,
        BindingPlan plan,
        ApiAuthorization authorization,
        int cancellationTokenIndex,
        bool isMultipart,
        PartBinding[] partBindings,
        // 校验路径只构建契约，不创建泛型调用器：这两个字段允许为 null。
        Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object>? invoker,
        Func<Exception, object>? fault)
    {
        _interfaceType = interfaceType;
        _methodName = methodName;
        _method = method;
        _pathTemplate = pathTemplate;
        _plan = plan;
        _authorization = authorization;
        _cancellationTokenIndex = cancellationTokenIndex;
        _isMultipart = isMultipart;
        _partBindings = partBindings;
        _invoker = invoker;
        _fault = fault;
    }

    /// <summary>声明里的方法名（生成代理用它校验索引是否与注册声明一致）。</summary>
    internal string? Name => _methodName;

    /// <summary>取（必要时构建并缓存）方法元数据；构建期错误在首次访问时抛 <see cref="InvalidOperationException"/>。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2077",
        Justification = "缓存键 ValueTuple 无法携带裁剪标注；接口类型已由 ApiClient.Create 的 RequiresUnreferencedCode 契约要求 root。")]
    public static InterfaceMethodMetadata Get(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method)
    {
        var metadata = Cache.GetOrAdd((interfaceType, method), key => Build(key.Interface, key.Method));

        if (metadata._buildError is not null)
        {
            throw new InvalidOperationException(metadata._buildError);
        }

        return metadata;
    }

    /// <summary>DispatchProxy 的调用入口：绑定 / 发送阶段的异常一律包装成 faulted Task，保持异步方法的异常时序。</summary>
    public object Invoke(ApiClient client, object?[]? args)
    {
        if (_buildError is not null)
        {
            // 正常路径不可达；元数据被绕过时直接给出构建错误。
            throw new InvalidOperationException(_buildError);
        }

        var actual = args ?? [];
        try
        {
            var cancellationToken = _cancellationTokenIndex >= 0 && actual.Length > _cancellationTokenIndex
                ? (CancellationToken)actual[_cancellationTokenIndex]!
                : default;

            return _invoker!(client, this, actual, cancellationToken);
        }
        catch (Exception ex)
        {
            return _fault!(ex);
        }
    }

    /// <summary>只构建契约（不创建泛型调用器）：供启动期校验使用，Native AOT 下也可执行。</summary>
    public static void Validate(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method)
        => BuildCore(interfaceType, method, createRuntime: false);

    /// <summary>
    /// 按声明构建方法契约（生成器 / 手工注册的路径）：不反射参数、不创建泛型调用器，
    /// 因此 Native AOT 下可用；调用器由生成代理在 P2 直接调用 <c>Send*Async</c> 桥接方法。
    /// </summary>
    internal static InterfaceMethodMetadata FromDeclaration(
        InterfaceDeclarationData owner,
        InterfaceMethodDeclaration declaration)
    {
        try
        {
            return BuildCore(owner, declaration);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            return new InterfaceMethodMetadata(ex.Message);
        }
    }

    /// <summary>契约已构建但存在声明错误时抛出（启动期校验 / 首次创建代理用）。</summary>
    public void EnsureValid()
    {
        if (_buildError is not null)
        {
            throw new InvalidOperationException(_buildError);
        }
    }

    /// <summary>声明路径的契约构建：只做归一化、绑定与授权，语义与反射路径逐项对齐。</summary>
    private static InterfaceMethodMetadata BuildCore(
        InterfaceDeclarationData owner,
        InterfaceMethodDeclaration declaration)
    {
        if (owner.Error is { } ownerError)
        {
            throw new InvalidOperationException(ownerError);
        }

        if (declaration.Error is { } declarationError)
        {
            throw new InvalidOperationException(declarationError);
        }

        var interfaceType = owner.InterfaceType;
        var methodName = declaration.Name;
        if (string.IsNullOrWhiteSpace(declaration.MethodName) || string.IsNullOrWhiteSpace(declaration.Path))
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} 必须且只能标注一个路由特性（[Get] / [Post] / ...）。");
        }

        var method = DeclarationNormalization.HttpMethodFrom(declaration.MethodName);
        var path = DeclarationNormalization.Path(declaration.Path);
        var ownerText = $"接口 {interfaceType.Name}.{methodName}";
        var accumulator = new BindingAccumulator(ownerText, "参数");

        // 接口级声明在前、方法级在后（同名后者覆盖），与反射路径一致。
        accumulator.AddStaticHeaders(owner.StaticHeaders);
        accumulator.AddStaticHeaders(declaration.StaticHeaders);

        var isMultipart = declaration.Multipart;
        var parts = new List<PartBinding>();
        var seenPartNames = new HashSet<string>(StringComparer.Ordinal);
        var cancellationTokenIndex = declaration.CancellationTokenIndex;

        foreach (var parameter in declaration.Parameters)
        {
            if (parameter.ValueType == typeof(CancellationToken))
            {
                // 反射路径按参数类型跳过取消令牌（其上的绑定特性一律忽略）；声明路径保持同一语义，
                // 生成器可以统一声明全部参数，再由 CancellationToken(index) 标注位置。
                if (cancellationTokenIndex >= 0 && cancellationTokenIndex != parameter.Index)
                {
                    throw new InvalidOperationException($"接口 {interfaceType.Name} 的 {methodName} 声明了多个 CancellationToken 参数：最多一个。");
                }

                cancellationTokenIndex = parameter.Index;
                continue;
            }

            if (parameter.Ignore)
            {
                if (parameter.HasAnyBinding)
                {
                    throw new InvalidOperationException(
                        $"参数 {interfaceType.Name}.{methodName}.{parameter.Name} 同时标注了 [ApiIgnore] 与参数特性（Path / Query / Header / HeaderCollection / Part / Body）："
                        + "[ApiIgnore] 的语义是「完全不绑定」，两者同用是二义性。");
                }

                continue;
            }

            var source = new ArgumentValueSource(
                parameter.Index,
                parameter.Name,
                parameter.ValueType,
                parameter.Description ?? $"{interfaceType.Name}.{methodName}.{parameter.Name}");

            if (parameter.Path)
            {
                accumulator.AddPath(source, parameter.PathName ?? string.Empty);
            }

            if (parameter.Header)
            {
                accumulator.AddHeader(source, parameter.HeaderName ?? string.Empty);
            }

            if (parameter.HeaderCollection)
            {
                accumulator.AddHeaderCollection(source);
            }

            if (parameter.Part)
            {
                if (!isMultipart)
                {
                    throw new InvalidOperationException(
                        $"参数 {interfaceType.Name}.{methodName}.{parameter.Name} 标注了 [Part]，但方法没有 [Multipart]：请在方法上声明 [Multipart]。");
                }

                var explicitName = DeclarationNormalization.OptionalPartValue(parameter.PartName, source.Description!, nameof(PartAttribute.Name));
                var partName = explicitName ?? parameter.Name;
                if (!seenPartNames.Add(partName))
                {
                    throw DuplicatePart(ownerText, partName);
                }

                parts.Add(new PartBinding(
                    partName,
                    source,
                    explicitName is not null,
                    DeclarationNormalization.OptionalPartValue(parameter.PartFileName, source.Description!, nameof(PartAttribute.FileName)),
                    DeclarationNormalization.OptionalPartValue(parameter.PartContentType, source.Description!, nameof(PartAttribute.ContentType))));
            }

            if (parameter.Body)
            {
                accumulator.AddBody(source);
            }

            if (!parameter.HasAnyBinding)
            {
                if (isMultipart)
                {
                    if (!seenPartNames.Add(parameter.Name))
                    {
                        throw DuplicatePart(ownerText, parameter.Name);
                    }

                    parts.Add(new PartBinding(parameter.Name, source, false, null, null));
                    continue;
                }

                accumulator.AddUnannotated(source);
            }
            else if (parameter.Query)
            {
                accumulator.AddQuery(source, parameter.QueryName ?? string.Empty);
            }
        }

        if (isMultipart && accumulator.HasBody)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} 同时声明了 [Multipart] 与 [Body]（{accumulator.BodyName}）："
                + "multipart 方法的请求体由全部参数装配，两者互斥。");
        }

        var plan = accumulator.Build(method, path, allowDefaultBody: !isMultipart);

        var shape = declaration.ReturnShape;
        if (shape == InterfaceReturnShape.Unsupported)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} 返回类型不受支持："
                + "接口方法必须返回 Task<AxiosResponse<T>> / Task<T> / Task<AxiosResponse> / Task。");
        }

        var isStreaming = shape == InterfaceReturnShape.Response
            && declaration.ResponseType is not null
            && InterfaceDeclaration.IsStreamingResponse(declaration.ResponseType);

        if (declaration.HasStreamAttribute && !isStreaming)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} 标注了 [Stream]，但返回类型不是 Task<AxiosResponse<IAsyncEnumerable<T>>>："
                + "[Stream] 只用于返回 Task<AxiosResponse<IAsyncEnumerable<T>>> 的方法。");
        }

        if (isStreaming && !Enum.IsDefined(typeof(AxiosStreamFormat), declaration.StreamFormat))
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {methodName} 的 [Stream] 帧格式值 {(int)declaration.StreamFormat} 无效：请使用 AxiosStreamFormat 的已定义值。");
        }

        // 方法级声明压过接口级；两层都未声明时继承实例默认授权。
        var authorization = declaration.AllowAnonymous || declaration.AuthorizeScheme is not null
            ? DeclarationNormalization.Authorization(declaration.AllowAnonymous, declaration.AuthorizeScheme, ownerText)
            : DeclarationNormalization.Authorization(owner.AllowAnonymous, owner.AuthorizeScheme, $"接口 {interfaceType.Name}");

        return new InterfaceMethodMetadata(
            interfaceType,
            methodName,
            method,
            path,
            plan,
            authorization,
            cancellationTokenIndex,
            isMultipart,
            [.. parts],
            invoker: null,
            fault: null);
    }

    private static InterfaceMethodMetadata Build(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method)
    {
        try
        {
            return BuildCore(interfaceType, method, createRuntime: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            return new InterfaceMethodMetadata(ex.Message);
        }
    }

    /// <summary>构建方法契约：路由 → 接口 / 方法级静态头 → 参数绑定（multipart 部件）→ 返回形态 / [Stream] → 授权；<paramref name="createRuntime"/> 为 false 时只校验、不特化泛型调用器。</summary>
    private static InterfaceMethodMetadata BuildCore(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method,
        bool createRuntime)
    {
        var routes = method.GetCustomAttributes<HttpApiAttribute>(inherit: false).ToArray();
        if (routes.Length != 1)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {method.Name} 必须且只能标注一个路由特性（[Get] / [Post] / ...）。");
        }

        var route = routes[0];
        var owner = $"接口 {interfaceType.Name}.{method.Name}";
        var accumulator = new BindingAccumulator(owner, "参数");
        accumulator.AddStaticHeaders(EnumerateStaticHeaders(interfaceType));

        // 方法级 [Headers] 后加入：同名项覆盖接口级声明。
        if (method.GetCustomAttribute<HeadersAttribute>(inherit: false) is { } methodHeaders)
        {
            accumulator.AddStaticHeaders(methodHeaders.Headers);
        }

        // multipart：方法声明 [Multipart] 时，参数自身即表单
        var isMultipart = method.GetCustomAttribute<MultipartAttribute>(inherit: false) is not null;
        var parts = new List<PartBinding>();
        var seenPartNames = new HashSet<string>(StringComparer.Ordinal);

        var cancellationTokenIndex = -1;
        var parameters = method.GetParameters();

        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var name = parameter.Name ?? $"arg{index}";

            if (parameter.ParameterType == typeof(CancellationToken))
            {
                if (cancellationTokenIndex >= 0)
                {
                    throw new InvalidOperationException($"接口 {interfaceType.Name} 的 {method.Name} 声明了多个 CancellationToken 参数：最多一个。");
                }

                cancellationTokenIndex = index;
                continue;
            }

            var path = parameter.GetCustomAttribute<PathAttribute>();
            var query = parameter.GetCustomAttribute<QueryAttribute>();
            var header = parameter.GetCustomAttribute<HeaderAttribute>();
            var isHeaderCollection = parameter.GetCustomAttribute<HeaderCollectionAttribute>() is not null;
            var part = parameter.GetCustomAttribute<PartAttribute>();
            var isBody = parameter.GetCustomAttribute<BodyAttribute>() is not null;
            var ignore = parameter.GetCustomAttribute<ApiIgnoreAttribute>() is not null;

            var kinds = (path is not null ? 1 : 0) + (query is not null ? 1 : 0)
                + (header is not null ? 1 : 0) + (isHeaderCollection ? 1 : 0)
                + (part is not null ? 1 : 0) + (isBody ? 1 : 0);

            if (ignore)
            {
                if (kinds > 0)
                {
                    throw new InvalidOperationException(
                        $"参数 {interfaceType.Name}.{method.Name}.{name} 同时标注了 [ApiIgnore] 与参数特性（Path / Query / Header / HeaderCollection / Part / Body）："
                        + "[ApiIgnore] 的语义是「完全不绑定」，两者同用是二义性。");
                }

                continue;
            }

            var source = new ArgumentValueSource(index, name, parameter.ParameterType, $"{interfaceType.Name}.{method.Name}.{name}");

            if (path is not null)
            {
                accumulator.AddPath(source, path.Name);
            }

            if (header is not null)
            {
                accumulator.AddHeader(source, header.Name);
            }

            if (isHeaderCollection)
            {
                accumulator.AddHeaderCollection(source);
            }

            if (part is not null)
            {
                if (!isMultipart)
                {
                    throw new InvalidOperationException(
                        $"参数 {interfaceType.Name}.{method.Name}.{name} 标注了 [Part]，但方法没有 [Multipart]：请在方法上声明 [Multipart]。");
                }

                var explicitName = CleanPartValue(part.Name, interfaceType, method, name, nameof(PartAttribute.Name));
                var partName = explicitName ?? name;
                if (!seenPartNames.Add(partName))
                {
                    throw DuplicatePart(owner, partName);
                }

                parts.Add(new PartBinding(
                    partName,
                    source,
                    explicitName is not null,
                    CleanPartValue(part.FileName, interfaceType, method, name, nameof(PartAttribute.FileName)),
                    CleanPartValue(part.ContentType, interfaceType, method, name, nameof(PartAttribute.ContentType))));
            }

            if (isBody)
            {
                accumulator.AddBody(source);
            }

            if (kinds == 0)
            {
                if (isMultipart)
                {
                    // multipart 方法：无标注参数默认按部件绑定（名字 = 参数名），不参与默认 Body / Query 判定
                    if (!seenPartNames.Add(name))
                    {
                        throw DuplicatePart(owner, name);
                    }

                    parts.Add(new PartBinding(name, source, false, null, null));
                    continue;
                }

                accumulator.AddUnannotated(source);
            }
            else if (query is not null)
            {
                accumulator.AddQuery(source, query.Name);
            }
        }

        // multipart 与 [Body] 互斥：一个方法两种请求体形态属二义性
        if (isMultipart && accumulator.HasBody)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {method.Name} 同时声明了 [Multipart] 与 [Body]（{accumulator.BodyName}）："
                + "multipart 方法的请求体由全部参数装配，两者互斥。");
        }

        var plan = accumulator.Build(route.Method, route.Path, allowDefaultBody: !isMultipart);

        var shape = InterfaceDeclaration.GetReturnShape(method, out var responseType);
        if (shape == InterfaceDeclaration.ReturnShape.Unsupported)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {method.Name} 返回类型是 {InterfaceDeclaration.DescribeReturnType(method.ReturnType)}："
                + "接口方法必须返回 Task<AxiosResponse<T>> / Task<T> / Task<AxiosResponse> / Task。");
        }

        var isStreaming = shape == InterfaceDeclaration.ReturnShape.Response
            && responseType is not null
            && InterfaceDeclaration.IsStreamingResponse(responseType);

        var stream = method.GetCustomAttribute<StreamAttribute>(inherit: false);
        if (stream is not null && !isStreaming)
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {method.Name} 标注了 [Stream]，但返回类型是 {InterfaceDeclaration.DescribeReturnType(method.ReturnType)}："
                + "[Stream] 只用于返回 Task<AxiosResponse<IAsyncEnumerable<T>>> 的方法。");
        }

        var streamFormat = stream?.Format ?? AxiosStreamFormat.NewlineDelimitedJson;
        if (isStreaming && !Enum.IsDefined(typeof(AxiosStreamFormat), streamFormat))
        {
            throw new InvalidOperationException(
                $"接口 {interfaceType.Name} 的 {method.Name} 的 [Stream] 帧格式值 {(int)streamFormat} 无效：请使用 AxiosStreamFormat 的已定义值。");
        }

        var authorization = ApiAuthorizationBuilder.Build(GetInterfaceChain(interfaceType), method, "接口");

        return new InterfaceMethodMetadata(
            interfaceType,
            method.Name,
            route.Method,
            route.Path,
            plan,
            authorization,
            cancellationTokenIndex,
            isMultipart,
            [.. parts],
            createRuntime ? CreateInvoker(shape, responseType, isStreaming, streamFormat) : null,
            createRuntime ? CreateFault(shape, responseType) : null);
    }

    /// <summary>把参数绑定成配置：URL / 查询 / 请求体（含 multipart）→ 声明级静态头 → 参数头（后者覆盖同名项）。</summary>
    private AxiosConfig BuildConfig(ApiClient client, object?[] args)
    {
        var config = new AxiosConfig
        {
            Method = _method!,
            Url = _plan!.ResolveUrl(_pathTemplate!, args, client.UrlParameterFormatter)
        };

        if (_plan.ResolveQuery(args, client.UrlParameterFormatter) is { } query)
        {
            config.Params = query;
        }

        if (_isMultipart)
        {
            object? resolved;
            try
            {
                resolved = ResolveMultipartBody(args);
            }
            catch (Exception ex) when (ex is not AxiosException and not OperationCanceledException)
            {
                // 表单装配失败（延迟迭代器抛错等）归 ERR_BAD_REQUEST。
                throw AxiosException.BadRequestPayload(config, request: null, $"{_interfaceType!.Name}.{_methodName}", ex);
            }

            if (resolved is not null)
            {
                config.Data = resolved;
            }

            // 与请求模型一致：multipart 方法默认 content-type，显式配置优先。
            if (!config.ContentTypeSet)
            {
                config.ContentType = AxiosContentType.MultipartFormData;
            }
        }
        else if (_plan.ResolveBodyValue(args) is { } body)
        {
            config.Data = body;
        }

        // 声明级静态头先写，参数上的 [Header] / [HeaderCollection] 覆盖同名项
        _plan.ApplyStaticHeaders(config);
        _plan.ApplyHeaders(args, config);

        return config;
    }

    /// <summary>接口方法的 multipart 请求体：参数即表单，全部参数为 null 时不发送请求体（与请求模型一致）。</summary>
    private object? ResolveMultipartBody(object?[] args)
    {
        var form = new DeclaredForm { SourceName = $"{_interfaceType!.Name}.{_methodName}" };

        try
        {
            foreach (var binding in _partBindings)
            {
                var value = binding.Source.GetValue(args);
                if (value is null)
                {
                    continue;
                }

                var partOverride = new PartOverride(binding.FileName, binding.ContentType, binding.HasExplicitName);
                if (partOverride.Any)
                {
                    form.Overrides[binding.Name] = partOverride;
                    form.Add(binding.Name, MultipartValues.MaterializeDeclaredCollection(value));
                }
                else
                {
                    form.Add(binding.Name, value);
                }
            }
        }
        catch
        {
            // 还没交给 MultipartBodyBuilder：已取得的流 / 内容必须就地释放，否则会随异常泄漏。
            MultipartOwnership.DisposeOwned(form.Values);
            throw;
        }

        return form.Count > 0 ? form : null;
    }

    /// <summary>调用前准备：写入调用方取消令牌、组装调用上下文并跑授权。</summary>
    private async Task<ApiInterfaceInvocation> PrepareAsync(
        ApiClient client,
        object?[] args,
        AxiosConfig config,
        CancellationToken cancellationToken)
    {
        // 仅在调用方传入可取消的令牌时才写入：None 会覆盖实例默认令牌
        if (cancellationToken.CanBeCanceled)
        {
            config.CancellationToken = cancellationToken;
        }

        var invocation = new ApiInterfaceInvocation(_interfaceType!, _methodName!, args);

        invocation.TokenManaged = await client.ApplyAuthorizationAsync(_authorization, invocation, config, cancellationToken).ConfigureAwait(false);

        return invocation;
    }

    /// <summary><c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c>：交付完整响应（生成代理经 <c>InterfaceMethodRuntime</c> 直接调用）。</summary>
    internal async Task<AxiosResponse<T>> SendResponseAsync<T>(ApiClient client, object?[] args, CancellationToken cancellationToken)
    {
        var config = BuildConfig(client, args);
        var invocation = await PrepareAsync(client, args, config, cancellationToken).ConfigureAwait(false);

        return await client.SendAsync<T>(invocation, config, invocation.TokenManaged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>Task&lt;AxiosResponse&gt;</c>：交付响应，正文按 JsonElement 原样给。</summary>
    internal async Task<AxiosResponse> SendUntypedAsync(ApiClient client, object?[] args, CancellationToken cancellationToken)
    {
        var config = BuildConfig(client, args);
        var invocation = await PrepareAsync(client, args, config, cancellationToken).ConfigureAwait(false);

        return await client.SendUntypedAsync(invocation, config, invocation.TokenManaged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>Task&lt;T&gt;</c>：只交付正文。</summary>
    internal async Task<T?> SendDataAsync<T>(ApiClient client, object?[] args, CancellationToken cancellationToken)
    {
        var response = await SendResponseAsync<T>(client, args, cancellationToken).ConfigureAwait(false);
        return response.Data;
    }

    /// <summary><c>Task</c>：只完成请求，正文丢弃。</summary>
    internal async Task SendVoidAsync(ApiClient client, object?[] args, CancellationToken cancellationToken)
    {
        var config = BuildConfig(client, args);
        var invocation = await PrepareAsync(client, args, config, cancellationToken).ConfigureAwait(false);

        await client.SendWithoutBodyAsync(invocation, config, invocation.TokenManaged, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>接口侧流式发送：绑定配置后把授权交给客户端内核统一执行。</summary>
    internal async Task<AxiosResponse<IAsyncEnumerable<TItem>>> SendStreamAsync<TItem>(
        ApiClient client,
        object?[] args,
        CancellationToken cancellationToken,
        AxiosStreamFormat format)
    {
        var config = BuildConfig(client, args);

        if (cancellationToken.CanBeCanceled)
        {
            config.CancellationToken = cancellationToken;
        }

        var invocation = new ApiInterfaceInvocation(_interfaceType!, _methodName!, args);

        // 流式发送内核自己跑授权，这里不再重复。
        return await client.SendStreamAsync<TItem>(invocation, _authorization, config, cancellationToken, format).ConfigureAwait(false);
    }

    /// <summary>
    /// 接口链：自身在前，其余按「依赖的接口数量」降序（派生接口依赖更多，必然排在它继承的接口之前）。
    /// 单纯按反射遍历顺序会随菱形的分支交错而把基接口排到派生接口前面，导致同名静态头 / 最近授权选错层。
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
    private static List<Type> GetInterfaceChain([DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType)
    {
        var inherited = interfaceType.GetInterfaces();
        var chain = new List<Type>(inherited.Length + 1) { interfaceType };
        foreach (var baseInterface in inherited.OrderByDescending(static type => type.GetInterfaces().Length))
        {
            chain.Add(baseInterface);
        }

        return chain;
    }

    /// <summary>声明级静态头：基接口在前、派生接口在后（同名后者覆盖）。</summary>
    private static IEnumerable<string> EnumerateStaticHeaders(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType)
    {
        var chain = GetInterfaceChain(interfaceType);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var attribute = chain[i].GetCustomAttribute<HeadersAttribute>(inherit: false);
            if (attribute is not null)
            {
                foreach (var declaration in attribute.Headers)
                {
                    yield return declaration;
                }
            }
        }
    }

    /// <summary>清理 [Part] 的可选字符串声明；显式空白（而非省略）视为构建错误。</summary>
    private static string? CleanPartValue(string? value, Type interfaceType, MethodInfo method, string parameterName, string member)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException(
                $"参数 {interfaceType.Name}.{method.Name}.{parameterName} 的 [Part].{member} 不能为空白：要省略请直接不设置。");
        }

        return trimmed;
    }

    private static InvalidOperationException DuplicatePart(string owner, string name)
        => new($"{owner} 有多个 Part 绑定使用了同名 \"{name}\"：同名参数请合并为一项（集合值走 AxiosNet 的数组规则）。");

    /// <summary>按返回形态选调用器：非泛型响应与 <c>Task</c> 直接闭包，其余泛型实例按正文类型特化。</summary>
    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateInvoker(
        InterfaceDeclaration.ReturnShape shape,
        Type? responseType,
        bool isStreaming,
        AxiosStreamFormat streamFormat)
        => shape switch
        {
            InterfaceDeclaration.ReturnShape.Response when responseType is null
                => static (client, metadata, args, cancellationToken) => metadata.SendUntypedAsync(client, args, cancellationToken),
            InterfaceDeclaration.ReturnShape.Response when isStreaming
                => CreateStreamInvoker(responseType.GetGenericArguments()[0], streamFormat),
            InterfaceDeclaration.ReturnShape.Response
                => CreateResponseInvoker(responseType!),
            InterfaceDeclaration.ReturnShape.Data
                => CreateDataInvoker(responseType!),
            _ => static (client, metadata, args, cancellationToken) => metadata.SendVoidAsync(client, args, cancellationToken),
        };

    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateResponseInvokerCore<T>()
        => static (client, metadata, args, cancellationToken) => metadata.SendResponseAsync<T>(client, args, cancellationToken);

    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateDataInvokerCore<T>()
        => static (client, metadata, args, cancellationToken) => metadata.SendDataAsync<T>(client, args, cancellationToken);

    [UnconditionalSuppressMessage(
        "AotAnalysis",
        "IL3050",
        Justification = "接口级声明在 Create 时已要求 RuntimeFeature.IsDynamicCodeSupported（JIT）；Native AOT 下直接拒绝。")]
    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateResponseInvoker(Type responseType)
        => (Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object>)typeof(InterfaceMethodMetadata)
            .GetMethod(nameof(CreateResponseInvokerCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(responseType)
            .Invoke(null, null)!;

    [UnconditionalSuppressMessage(
        "AotAnalysis",
        "IL3050",
        Justification = "接口级声明在 Create 时已要求 RuntimeFeature.IsDynamicCodeSupported（JIT）；Native AOT 下直接拒绝。")]
    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateDataInvoker(Type responseType)
        => (Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object>)typeof(InterfaceMethodMetadata)
            .GetMethod(nameof(CreateDataInvokerCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(responseType)
            .Invoke(null, null)!;

    [UnconditionalSuppressMessage(
        "AotAnalysis",
        "IL3050",
        Justification = "接口级声明在 Create 时已要求 RuntimeFeature.IsDynamicCodeSupported（JIT）；Native AOT 下直接拒绝。")]
    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateStreamInvoker(
        Type frameType,
        AxiosStreamFormat format)
        => (Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object>)typeof(InterfaceMethodMetadata)
            .GetMethod(nameof(CreateStreamInvokerCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(frameType)
            .Invoke(null, [format])!;

    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateStreamInvokerCore<TItem>(AxiosStreamFormat format)
        => (client, metadata, args, cancellationToken) => metadata.SendStreamAsync<TItem>(client, args, cancellationToken, format);

    /// <summary>绑定 / 发送阶段的异常封装成对应形态的 faulted Task（保持异步方法的异常时序）。</summary>
    private static Func<Exception, object> CreateFault(InterfaceDeclaration.ReturnShape shape, Type? responseType)
        => shape switch
        {
            InterfaceDeclaration.ReturnShape.Response when responseType is null
                => static error => Task.FromException<AxiosResponse>(error),
            InterfaceDeclaration.ReturnShape.Void
                => static error => Task.FromException(error),
            InterfaceDeclaration.ReturnShape.Data
                => CreateDataFault(responseType!),
            _ => CreateResponseFault(responseType!),
        };

    private static Func<Exception, object> CreateResponseFaultCore<T>()
        => static error => Task.FromException<AxiosResponse<T>>(error);

    private static Func<Exception, object> CreateDataFaultCore<T>()
        => static error => Task.FromException<T>(error);

    [UnconditionalSuppressMessage(
        "AotAnalysis",
        "IL3050",
        Justification = "接口级声明在 Create 时已要求 RuntimeFeature.IsDynamicCodeSupported（JIT）；Native AOT 下直接拒绝。")]
    private static Func<Exception, object> CreateResponseFault(Type responseType)
        => (Func<Exception, object>)typeof(InterfaceMethodMetadata)
            .GetMethod(nameof(CreateResponseFaultCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(responseType)
            .Invoke(null, null)!;

    [UnconditionalSuppressMessage(
        "AotAnalysis",
        "IL3050",
        Justification = "接口级声明在 Create 时已要求 RuntimeFeature.IsDynamicCodeSupported（JIT）；Native AOT 下直接拒绝。")]
    private static Func<Exception, object> CreateDataFault(Type responseType)
        => (Func<Exception, object>)typeof(InterfaceMethodMetadata)
            .GetMethod(nameof(CreateDataFaultCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(responseType)
            .Invoke(null, null)!;
}
