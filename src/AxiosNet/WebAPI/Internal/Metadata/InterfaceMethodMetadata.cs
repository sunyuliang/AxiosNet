using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Internal.Metadata;

/// <summary>
/// 接口方法的绑定元数据：路由、参数绑定计划、授权、返回类型特化后的调用器；缓存键包含被代理的接口。
/// </summary>
internal sealed partial class InterfaceMethodMetadata
{
    private readonly string? _buildError;
    private readonly Type? _interfaceType;
    private readonly string? _methodName;

    /// <summary>错误消息与 multipart 来源名共用的定位文本（<c>接口名.方法名</c>），构造期拼一次。</summary>
    private readonly string _target = string.Empty;
    private readonly HttpMethod? _method;
    private readonly string? _pathTemplate;
    private readonly BindingPlan? _plan;
    private readonly ApiAuthorization _authorization;
    private readonly int _cancellationTokenIndex;
    private readonly bool _isMultipart;
    private readonly string? _contentType;
    private readonly AxiosCollectionFormat? _queryCollectionFormat;
    private readonly PartBinding[] _partBindings;
    private readonly Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object>? _invoker;
    private readonly Func<Exception, object>? _fault;

    /// <summary>整份正文参数的序号；<c>-1</c> 表示没有正文。</summary>
    private readonly int _rootBodyIndex;

    /// <summary>方法声明的参数个数（含被忽略的与取消令牌），生成期字面量快路退回通用路径时用来还原参数数组。</summary>
    private readonly int _parameterCount;

    /// <summary>声明是否只有「方法 + 字面量路由 + 单整份正文」：是则生成代理可跳过参数数组与绑定计划遍历。</summary>
    private readonly bool _literalRootBody;

    /// <summary>实参数组能否只凭「整份正文 + 取消令牌」还原：生成代理走字面量快路、运行期却判定不是快路形状时用。</summary>
    private readonly bool _argsRebuildable;

    /// <summary>把内核的「构造失败」改判为装配失败；每条声明共用一份，不随请求分配。</summary>
    private readonly Func<AxiosException, AxiosException>? _failureMapper;

    private InterfaceMethodMetadata(string buildError)
    {
        _buildError = buildError;
        _partBindings = [];
        _rootBodyIndex = -1;
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
        string? contentType,
        AxiosCollectionFormat? queryCollectionFormat,
        PartBinding[] partBindings,
        // 校验路径只构建契约，不创建泛型调用器：这两个字段允许为 null。
        Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object>? invoker,
        Func<Exception, object>? fault,
        int rootBodyIndex = -1,
        int parameterCount = 0,
        bool literalRootBody = false,
        bool argsRebuildable = false)
    {
        _interfaceType = interfaceType;
        _methodName = methodName;
        _method = method;
        _pathTemplate = pathTemplate;
        _plan = plan;
        _authorization = authorization;
        _cancellationTokenIndex = cancellationTokenIndex;
        _isMultipart = isMultipart;
        _contentType = contentType;
        _queryCollectionFormat = queryCollectionFormat;
        _partBindings = partBindings;
        _invoker = invoker;
        _fault = fault;
        _rootBodyIndex = rootBodyIndex;
        _parameterCount = parameterCount;
        _literalRootBody = literalRootBody;
        _argsRebuildable = argsRebuildable;
        _target = interfaceType.Name + "." + methodName;
        _failureMapper = MapConstructionFailure;
    }

    /// <summary>把内核的请求构造失败改判成装配失败（<c>ERR_BAD_REQUEST</c>），消息里带接口与方法名。</summary>
    private AxiosException MapConstructionFailure(AxiosException error)
        => RequestBindingErrors.Create(error.Config, request: null, _target, error.Cause ?? error);

    /// <summary>声明里的方法名（生成代理用它校验索引是否与注册声明一致）。</summary>
    internal string? Name => _methodName;

    /// <summary>该声明是否判定为生成期字面量快路形状（运行期还可能因观察者退回通用路径）。</summary>
    internal bool IsLiteralRootBody => _literalRootBody;

    /// <summary>实参数组能否只由整份正文与取消令牌还原；<c>false</c> 时快路无法退回通用路径。</summary>
    internal bool IsArgsRebuildable => _argsRebuildable;

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
        // 配置从实例默认值的副本起步（与快捷方法 / 请求模型同一条起点），本次请求项只覆盖它需要改的部分。
        var formatter = client.Axios.Defaults.Serialization.ValueText;
        var config = client.Axios.NewEffectiveConfig();
        config.Method = _method!;

        // 声明式装配失败统一归 ERR_BAD_REQUEST（路径 / 查询 / 头 / 正文同口径）。
        config.Url = BindingErrors.Run(
            config,
            _target,
            (_plan, _pathTemplate, args, formatter),
            static state => state._plan!.ResolveUrl(state._pathTemplate!, state.args, state.formatter));

        if (BindingErrors.Run(
                config,
                _target,
                (_plan, args, formatter),
                static state => state._plan!.ResolveQuery(state.args, state.formatter)) is { } query)
        {
            config.Params = query;
        }

        if (_isMultipart)
        {
            if (BindingErrors.Run(
                    config,
                    _target,
                    (@this: this, args),
                    static state => state.@this.ResolveMultipartBody(state.args)) is { } resolved)
            {
                config.Body = resolved;
            }

            // 与请求模型一致：multipart 方法默认 content-type，显式配置优先。
            if (!config.ContentTypeSet)
            {
                config.ContentType = AxiosContentType.MultipartFormData;
            }
        }
        else if (BindingErrors.Run(
                     config,
                     _target,
                     (_plan, args, formatter),
                     static state => state._plan!.ResolveBodyValue(state.args, state.formatter)) is { } body)
        {
            config.Body = body;
        }

        // 声明级编码：媒体类型决定正文编码（不只是头），集合格式覆盖实例配置。
        if (_contentType is not null)
        {
            config.ContentType = _contentType;
        }

        if (_queryCollectionFormat is { } collectionFormat)
        {
            config.Serialization.CollectionFormat = collectionFormat;
        }

        // 声明级静态头先写，参数上的 [Header] / [HeaderCollection] 覆盖同名项
        BindingErrors.Run(
            config,
            _target,
            (_plan, config),
            static state => state._plan!.ApplyStaticHeaders(state.config));

        BindingErrors.Run(
            config,
            _target,
            (_plan, args, config, formatter),
            static state => state._plan!.ApplyHeaders(state.args, state.config, state.formatter));

        // 与快捷方法同一顺序：所有头都写完（含声明级删头的撤销）再执行删头。
        AxiosClient.ApplyRemoveHeaders(config);
        config.ConstructionFailureMap = _failureMapper;
        return config;
    }

    /// <summary>接口方法的 multipart 请求体：参数即表单，全部参数为 null 时不发送请求体（与请求模型一致）。</summary>
    private object? ResolveMultipartBody(object?[] args)
        => MultipartFormAssembler.Build(_target, _partBindings, args);

    /// <summary>调用前准备：写入调用方取消令牌、按需组装调用上下文并跑授权（同步完成，不包 Task）。</summary>
    /// <remarks>
    /// 只有凭据提供方 / 错误工厂会读实参时才建调用对象；两者都不在时授权拿不到载荷，但授予路径
    /// （免鉴权清空 / 静态凭据方案校验）都不读它。
    /// </remarks>
    private ApiInterfaceInvocation? Prepare(
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

        var payload = client.RequiresInvocationPayload
            ? new ApiInterfaceInvocation(_interfaceType!, _methodName!, args)
            : null;

        var auth = client.ApplyAuthorization(_authorization, payload, config);
        if (payload is not null)
        {
            payload.Auth = auth;
        }

        return payload;
    }

    /// <summary><c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c>：交付完整响应（生成代理经 <c>InterfaceMethodRuntime</c> 直接调用）。</summary>
    internal Task<AxiosResponse<T>> SendResponseAsync<T>(ApiClient client, object?[] args, CancellationToken cancellationToken)
    {
        // 装配期异常按异步方法的时序交给返回的 Task：调用方 await 时才看到，不在这里同步抛出。
        try
        {
            var config = BuildConfig(client, args);
            var invocation = Prepare(client, args, config, cancellationToken);

            return client.SendAsync<T>(invocation, config, invocation?.Auth, cancellationToken, _target);
        }
        catch (Exception ex)
        {
            return Task.FromException<AxiosResponse<T>>(ex);
        }
    }

    /// <summary><c>Task&lt;AxiosResponse&gt;</c>：交付响应，正文按 JsonElement 原样给。</summary>
    internal Task<AxiosResponse> SendUntypedAsync(ApiClient client, object?[] args, CancellationToken cancellationToken)
    {
        try
        {
            var config = BuildConfig(client, args);
            var invocation = Prepare(client, args, config, cancellationToken);

            return client.SendUntypedAsync(invocation, config, invocation?.Auth, cancellationToken, _target);
        }
        catch (Exception ex)
        {
            return Task.FromException<AxiosResponse>(ex);
        }
    }

    /// <summary><c>Task&lt;T&gt;</c>：只交付正文。</summary>
    internal async Task<T?> SendDataAsync<T>(ApiClient client, object?[] args, CancellationToken cancellationToken)
    {
        var response = await SendResponseAsync<T>(client, args, cancellationToken).ConfigureAwait(false);
        return response.Data;
    }

    /// <summary><c>Task</c>：只完成请求，正文丢弃。</summary>
    internal Task SendVoidAsync(ApiClient client, object?[] args, CancellationToken cancellationToken)
    {
        try
        {
            var config = BuildConfig(client, args);
            var invocation = Prepare(client, args, config, cancellationToken);

            return client.SendWithoutBodyAsync(invocation, config, invocation?.Auth, cancellationToken, _target);
        }
        catch (Exception ex)
        {
            return Task.FromException(ex);
        }
    }

    // 生成期字面量快路：声明只有「方法 + 字面量路由 + 单整份正文（或无正文）」，没有别的绑定、
    // 声明级头与媒体类型声明。生成代理改调这几个成员，跳过参数数组与绑定计划遍历。

    /// <summary><c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c>：字面量路由 + 整份正文。</summary>
    internal Task<AxiosResponse<T>> SendRootBodyResponseAsync<T>(
        ApiClient client,
        object? rootBody,
        CancellationToken cancellationToken)
    {
        // 与通用路径同一时序：装配期异常交给返回的 Task，不在调用方同步抛出。
        try
        {
            return CanUseRootBodyFastPath(client)
                ? client.SendPreparedDeclarativeAsync<T>(BuildLiteralConfig(client, rootBody, cancellationToken))
                : SendResponseAsync<T>(client, BuildArguments(rootBody, cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException<AxiosResponse<T>>(ex);
        }
    }

    /// <summary><c>Task&lt;AxiosResponse&gt;</c>：字面量路由 + 整份正文，正文按 JsonElement 原样给。</summary>
    internal Task<AxiosResponse> SendRootBodyUntypedAsync(
        ApiClient client,
        object? rootBody,
        CancellationToken cancellationToken)
    {
        try
        {
            return CanUseRootBodyFastPath(client)
                ? client.SendPreparedDeclarativeAsync(BuildLiteralConfig(client, rootBody, cancellationToken))
                : SendUntypedAsync(client, BuildArguments(rootBody, cancellationToken), cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException<AxiosResponse>(ex);
        }
    }

    /// <summary><c>Task&lt;T&gt;</c>：字面量路由 + 整份正文，只交付正文。</summary>
    internal async Task<T?> SendRootBodyDataAsync<T>(
        ApiClient client,
        object? rootBody,
        CancellationToken cancellationToken)
    {
        var response = await SendRootBodyResponseAsync<T>(client, rootBody, cancellationToken).ConfigureAwait(false);
        return response.Data;
    }

    /// <summary><c>Task</c>：字面量路由 + 整份正文，丢弃响应正文。</summary>
    internal async Task SendRootBodyVoidAsync(
        ApiClient client,
        object? rootBody,
        CancellationToken cancellationToken)
    {
        // 通用路径自己负责正文释放，这里只处理快路拿到的响应。
        if (!CanUseRootBodyFastPath(client))
        {
            await SendVoidAsync(client, BuildArguments(rootBody, cancellationToken), cancellationToken).ConfigureAwait(false);
            return;
        }

        await client
            .SendPreparedDeclarativeVoidAsync(BuildLiteralConfig(client, rootBody, cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>字面量快路是否可用：声明形状在生成期已确认，且本次没有需要读实参的观察者。</summary>
    private bool CanUseRootBodyFastPath(ApiClient client)
        => _literalRootBody && !client.RequiresInvocationPayload;

    /// <summary>退回通用路径时还原参数数组：只有整份正文与取消令牌两个位置有值。</summary>
    /// <exception cref="InvalidOperationException">声明还有别的绑定通道：实参无法还原，说明生成代理与运行库版本不一致。</exception>
    private object?[] BuildArguments(object? rootBody, CancellationToken cancellationToken)
    {
        if (!_argsRebuildable)
        {
            throw new InvalidOperationException(
                $"{_target} 的生成代理按「整份正文」快路调用，但运行期元数据判定该声明还有其它绑定（路径 / 查询 / 头 / 部件 / 聚合正文）："
                + "生成器与运行库版本的判据不一致。请重新编译该接口所在的程序集，或把 AxiosNet 与生成器升级到同一版本。");
        }

        var args = new object?[Math.Max(_parameterCount, 1)];
        if (_rootBodyIndex >= 0)
        {
            args[_rootBodyIndex] = rootBody;
        }

        if (_cancellationTokenIndex >= 0 && _cancellationTokenIndex < args.Length)
        {
            args[_cancellationTokenIndex] = cancellationToken;
        }

        return args;
    }

    /// <summary>字面量快路的配置装配：在实例默认值的副本上只填本次请求项，不走绑定计划。</summary>
    private AxiosConfig BuildLiteralConfig(ApiClient client, object? rootBody, CancellationToken cancellationToken)
    {
        var formatter = client.Axios.Defaults.Serialization.ValueText;
        var config = client.Axios.NewEffectiveConfig();
        config.Method = _method!;
        config.Url = _pathTemplate!;

        // 正文与通用路径同口径：格式化失败归装配失败（ERR_BAD_REQUEST），不把用户钩子的异常原样抛出。
        config.Body = BindingErrors.Run(
            config,
            _target,
            (rootBody, formatter),
            static state => BindingPlan.ResolveRootValue(state.rootBody, format: null, state.formatter));

        config.ConstructionFailureMap = _failureMapper;

        // 与通用路径同一顺序：删头在取消令牌与授权之前。快路形状没有静态头，不需要写头。
        AxiosClient.ApplyRemoveHeaders(config);

        if (cancellationToken.CanBeCanceled)
        {
            config.CancellationToken = cancellationToken;
        }

        // 快路只在没有凭据提供方 / 错误工厂时走到这里：授权不会去读调用载荷。
        client.ApplyAuthorization(_authorization, request: null, config);
        return config;
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
        return await client.SendStreamAsync<TItem>(invocation, _authorization, config, cancellationToken, format, _target).ConfigureAwait(false);
    }

    // 下面两个成员只由 JIT 反射路径调用（DispatchProxy 入口 / 挂泛型调用器）。

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
            _contentType,
            _queryCollectionFormat,
            _partBindings,
            invoker,
            fault,
            _rootBodyIndex,
            _parameterCount,
            _literalRootBody,
            _argsRebuildable);

    /// <summary>同名 Part 绑定的错误：说明同名参数要合并成一项。</summary>
    private static InvalidOperationException DuplicatePart(string owner, string name)
        => new($"{owner} 有多个 Part 绑定使用了同名 \"{name}\"：同名参数请合并为一项（集合值走 AxiosNet 的数组规则）。");
}
