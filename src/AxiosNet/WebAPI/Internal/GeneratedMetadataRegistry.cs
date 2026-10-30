using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 生成元数据注册表：写时复制 + <c>Volatile.Read</c>，读路径无锁、无分配。
/// 注册只发生在模块初始化 / 启动期；注册后不再修改字典本身（替换整个实例）。
/// 同一类型重复注册属编程错误，直接抛出（生成器与手工注册不应同时声明同一类型）。
/// </summary>
internal static class GeneratedMetadataRegistry
{
    private static readonly object Gate = new();

    private static Dictionary<Type, RequestModelDeclaration> _requestModels = new();
    private static Dictionary<Type, InterfaceEntry> _interfaces = new();
    private static Dictionary<Type, ObjectShape> _shapes = new();
    private static Dictionary<Type, MapAccessor> _maps = new();

    internal static bool TryGetRequestModel(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out RequestModelDeclaration? declaration)
        => Volatile.Read(ref _requestModels).TryGetValue(type, out declaration);

    internal static bool TryGetInterface(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out InterfaceEntry? entry)
        => Volatile.Read(ref _interfaces).TryGetValue(type, out entry);

    internal static bool TryGetShape(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ObjectShape? shape)
        => Volatile.Read(ref _shapes).TryGetValue(type, out shape);

    internal static bool TryGetMap(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MapAccessor? accessor)
        => Volatile.Read(ref _maps).TryGetValue(type, out accessor);

    internal static void RegisterRequestModel(RequestModelDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        lock (Gate)
        {
            var current = _requestModels;
            if (current.ContainsKey(declaration.ModelType))
            {
                throw new InvalidOperationException(
                    $"请求模型 {Describe(declaration.ModelType)} 的元数据已注册：源生成器与手工 AxiosMetadata.RegisterRequestModel 不能同时声明同一类型。");
            }

            var next = new Dictionary<Type, RequestModelDeclaration>(current) { [declaration.ModelType] = declaration };
            Volatile.Write(ref _requestModels, next);

            // 该类型可能已被 JIT 反射构建并缓存：显式注册必须压过旧缓存。
            RequestModelMetadata.Invalidate(declaration.ModelType);
        }
    }

    internal static void RegisterInterface(InterfaceDeclarationData declaration, Func<ApiClient, object> factory)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(factory);
        declaration.Factory = factory;

        lock (Gate)
        {
            var current = _interfaces;
            if (current.ContainsKey(declaration.InterfaceType))
            {
                throw new InvalidOperationException(
                    $"接口 {Describe(declaration.InterfaceType)} 的元数据已注册：源生成器与手工 AxiosMetadata.RegisterInterface 不能同时声明同一类型。");
            }

            var next = new Dictionary<Type, InterfaceEntry>(current)
            {
                [declaration.InterfaceType] = new InterfaceEntry(declaration, factory)
            };

            Volatile.Write(ref _interfaces, next);
        }
    }

    internal static void RegisterShape(ObjectShape shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        lock (Gate)
        {
            var current = _shapes;
            if (current.ContainsKey(shape.Type))
            {
                throw new InvalidOperationException(
                    $"类型 {Describe(shape.Type)} 的对象形状已注册：[AxiosShape] 与手工 AxiosMetadata.RegisterShape 不能同时声明同一类型。");
            }

            var next = new Dictionary<Type, ObjectShape>(current) { [shape.Type] = shape };
            Volatile.Write(ref _shapes, next);
        }
    }

    internal static void RegisterMap(MapAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        lock (Gate)
        {
            var current = _maps;
            if (current.ContainsKey(accessor.Type))
            {
                throw new InvalidOperationException(
                    $"类型 {Describe(accessor.Type)} 的映射访问器已注册：源生成器与手工 AxiosMetadata.RegisterMap 不能同时声明同一类型。");
            }

            var next = new Dictionary<Type, MapAccessor>(current) { [accessor.Type] = accessor };
            Volatile.Write(ref _maps, next);
        }
    }

    private static string Describe(Type type) => type.FullName ?? type.Name;
}

/// <summary>接口注册项：声明 + 代理工厂；方法元数据在首次创建代理时按索引构建并缓存。</summary>
internal sealed class InterfaceEntry
{
    private InterfaceMethodMetadata[]? _methods;

    public InterfaceEntry(InterfaceDeclarationData declaration, Func<ApiClient, object> factory)
    {
        Declaration = declaration;
        Factory = factory;
    }

    public InterfaceDeclarationData Declaration { get; }

    public Func<ApiClient, object> Factory { get; }

    /// <summary>按声明顺序构建（并缓存）全部方法的绑定元数据；构建错误照旧在首次使用该夹具时抛出。</summary>
    public InterfaceMethodMetadata[] GetMethods()
    {
        var methods = Volatile.Read(ref _methods);
        if (methods is not null)
        {
            return methods;
        }

        methods = new InterfaceMethodMetadata[Declaration.Methods.Count];
        for (var i = 0; i < methods.Length; i++)
        {
            methods[i] = InterfaceMethodMetadata.FromDeclaration(Declaration, Declaration.Methods[i]);
        }

        return Interlocked.CompareExchange(ref _methods, methods, null) ?? methods;
    }

    /// <summary>构建全部方法契约（不创建泛型调用器）：Create 时一次性暴露全部声明错误。</summary>
    public void Validate()
    {
        // 接口级错误（configure 在声明任何方法前抛错）没有方法可挂：先单独抛出，避免被空方法表吞掉。
        if (Declaration.Error is { } error)
        {
            throw new InvalidOperationException(error);
        }

        foreach (var method in GetMethods())
        {
            method.EnsureValid();
        }
    }
}
