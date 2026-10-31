using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using AxiosNet.WebAPI.Internal.Reflection;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 接口方法的反射路径：读接口方法特性（<see cref="InterfaceDeclarationReader"/>）、按返回类型特化泛型调用器。
/// 只在 <see cref="RuntimeSupport.IsDynamicCodeSupported"/> 为 true 时可达；生成代理不经过这里。
/// </summary>
internal sealed partial class InterfaceMethodMetadata
{
    private const DynamicallyAccessedMemberTypes InterfaceMembers =
        DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.Interfaces;

    private static readonly ConcurrentDictionary<(Type Interface, MethodInfo Method), InterfaceMethodMetadata> Cache = new();

    /// <summary>取（必要时构建并缓存）方法元数据；构建期错误在首次访问时抛 <see cref="InvalidOperationException"/>。</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2077",
        Justification = "缓存键 ValueTuple 无法携带裁剪标注；接口类型已由 ApiClient.Create 的 DynamicallyAccessedMembers 标注覆盖，具体类型由调用方负责 root。")]
    public static InterfaceMethodMetadata Get(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method)
    {
        var metadata = Cache.GetOrAdd((interfaceType, method), key => BuildReflection(key.Interface, key.Method, createRuntime: true));

        if (metadata._buildError is not null)
        {
            throw new InvalidOperationException(metadata._buildError);
        }

        return metadata;
    }

    /// <summary>只构建契约（不创建泛型调用器）：供启动期校验使用，Native AOT 下也可执行。</summary>
    public static void Validate(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method)
        => BuildReflection(interfaceType, method, createRuntime: false).EnsureValid();

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

    /// <summary>反射构建：读声明 → 走与生成路径同一份契约构建 → 需要时特化泛型调用器。</summary>
    private static InterfaceMethodMetadata BuildReflection(
        [DynamicallyAccessedMembers(InterfaceMembers)] Type interfaceType,
        MethodInfo method,
        bool createRuntime)
    {
        try
        {
            var (owner, declaration) = InterfaceDeclarationReader.Read(interfaceType, method);
            var metadata = BuildCore(owner, declaration);
            if (!createRuntime)
            {
                return metadata;
            }

            var shape = declaration.ReturnShape;
            var responseType = declaration.ResponseType;
            var isStreaming = shape == InterfaceReturnShape.Response
                && responseType is not null
                && ReturnTypeShape.IsStreaming(responseType);

            return metadata.WithRuntime(
                CreateInvoker(shape, responseType, isStreaming, declaration.StreamFormat),
                CreateFault(shape, responseType));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            return new InterfaceMethodMetadata(ex.Message);
        }
    }

    /// <summary>把泛型调用器挂到已构建的契约上（契约本身不变，只是多两个委托）。</summary>
    private InterfaceMethodMetadata WithRuntime(
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

    /// <summary>按返回形态选调用器：非泛型响应与 <c>Task</c> 直接闭包，其余泛型实例按正文类型特化。</summary>
    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateInvoker(
        InterfaceReturnShape shape,
        Type? responseType,
        bool isStreaming,
        AxiosStreamFormat streamFormat)
        => shape switch
        {
            InterfaceReturnShape.Response when responseType is null
                => static (client, metadata, args, cancellationToken) => metadata.SendUntypedAsync(client, args, cancellationToken),
            InterfaceReturnShape.Response when isStreaming
                => CreateStreamInvoker(responseType!.GetGenericArguments()[0], streamFormat),
            InterfaceReturnShape.Response
                => CreateResponseInvoker(responseType!),
            InterfaceReturnShape.Data
                => CreateDataInvoker(responseType!),
            _ => static (client, metadata, args, cancellationToken) => metadata.SendVoidAsync(client, args, cancellationToken),
        };

    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateResponseInvokerCore<T>()
        => static (client, metadata, args, cancellationToken) => metadata.SendResponseAsync<T>(client, args, cancellationToken);

    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateDataInvokerCore<T>()
        => static (client, metadata, args, cancellationToken) => metadata.SendDataAsync<T>(client, args, cancellationToken);

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "只在反射路径创建泛型调用器：BuildReflection 仅由 Get / Validate 调用，两者都在 RuntimeFeature.IsDynamicCodeSupported 为 true 时可达，Native AOT 下不可达。生成代理不经过这里。")]
    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateResponseInvoker(Type responseType)
        => (Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object>)typeof(InterfaceMethodMetadata)
            .GetMethod(nameof(CreateResponseInvokerCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(responseType)
            .Invoke(null, null)!;

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "只在反射路径创建泛型调用器：BuildReflection 仅由 Get / Validate 调用，两者都在 RuntimeFeature.IsDynamicCodeSupported 为 true 时可达，Native AOT 下不可达。生成代理不经过这里。")]
    private static Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object> CreateDataInvoker(Type responseType)
        => (Func<ApiClient, InterfaceMethodMetadata, object?[], CancellationToken, object>)typeof(InterfaceMethodMetadata)
            .GetMethod(nameof(CreateDataInvokerCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(responseType)
            .Invoke(null, null)!;

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "只在反射路径创建泛型调用器：BuildReflection 仅由 Get / Validate 调用，两者都在 RuntimeFeature.IsDynamicCodeSupported 为 true 时可达，Native AOT 下不可达。生成代理不经过这里。")]
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
    private static Func<Exception, object> CreateFault(InterfaceReturnShape shape, Type? responseType)
        => shape switch
        {
            InterfaceReturnShape.Response when responseType is null
                => static error => Task.FromException<AxiosResponse>(error),
            InterfaceReturnShape.Void
                => static error => Task.FromException(error),
            InterfaceReturnShape.Data
                => CreateDataFault(responseType!),
            _ => CreateResponseFault(responseType!),
        };

    private static Func<Exception, object> CreateResponseFaultCore<T>()
        => static error => Task.FromException<AxiosResponse<T>>(error);

    private static Func<Exception, object> CreateDataFaultCore<T>()
        => static error => Task.FromException<T>(error);

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "只在反射路径创建泛型调用器：BuildReflection 仅由 Get / Validate 调用，两者都在 RuntimeFeature.IsDynamicCodeSupported 为 true 时可达，Native AOT 下不可达。生成代理不经过这里。")]
    private static Func<Exception, object> CreateResponseFault(Type responseType)
        => (Func<Exception, object>)typeof(InterfaceMethodMetadata)
            .GetMethod(nameof(CreateResponseFaultCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(responseType)
            .Invoke(null, null)!;

    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "只在反射路径创建泛型调用器：BuildReflection 仅由 Get / Validate 调用，两者都在 RuntimeFeature.IsDynamicCodeSupported 为 true 时可达，Native AOT 下不可达。生成代理不经过这里。")]
    private static Func<Exception, object> CreateDataFault(Type responseType)
        => (Func<Exception, object>)typeof(InterfaceMethodMetadata)
            .GetMethod(nameof(CreateDataFaultCore), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(responseType)
            .Invoke(null, null)!;
}
