using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 接口方法的绑定元数据：路由、参数绑定计划、授权、返回类型特化后的调用器；缓存键包含被代理的接口。
/// 本文件是两条路径共用的部分（契约字段 + 绑定成配置 + 发送）：契约构建见 <c>InterfaceMethodMetadata.Contract.cs</c>，
/// 反射路径见 <c>WebAPI/Reflection/InterfaceMethodMetadataJit.cs</c>。
/// </summary>
internal sealed partial class InterfaceMethodMetadata
{
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

    /// <summary>声明错误（构建失败时的可读消息）；合法契约为 <c>null</c>。</summary>
    internal string? BuildError => _buildError;

    /// <summary>构建失败时的占位元数据：只携带错误消息，首次使用（<see cref="EnsureValid"/> / <see cref="Invoke"/>）时抛出。</summary>
    internal static InterfaceMethodMetadata FromError(string message) => new(message);

    /// <summary>契约已构建但存在声明错误时抛出（启动期校验 / 首次创建代理用）。</summary>
    public void EnsureValid()
    {
        if (_buildError is not null)
        {
            throw new InvalidOperationException(_buildError);
        }
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

    // 下面两个成员只由 JIT 反射路径调用（DispatchProxy 入口 / 挂泛型调用器）：
    // 反射实现本身在 <c>WebAPI/Reflection/InterfaceMethodMetadataJit.cs</c>，这里只保留需要访问私有状态的薄壳。

    /// <summary>DispatchProxy 的调用入口：绑定 / 发送阶段的异常一律包装成 faulted Task，保持异步方法的异常时序。</summary>
    internal object Invoke(ApiClient client, object?[]? args)
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

    /// <summary>把泛型调用器挂到已构建的契约上（契约本身不变，只是多两个委托）。</summary>
    internal InterfaceMethodMetadata WithRuntime(
        Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> invoker,
        Func<Exception, object> fault)
        => new(
            _interfaceType!,
            _methodName!,
            _method!,
            _pathTemplate!,
            _plan!,
            _authorization,
            _cancellationTokenIndex,
            _isMultipart,
            _partBindings,
            invoker,
            fault);

    private static InvalidOperationException DuplicatePart(string owner, string name)
        => new($"{owner} 有多个 Part 绑定使用了同名 \"{name}\"：同名参数请合并为一项（集合值走 AxiosNet 的数组规则）。");
}
