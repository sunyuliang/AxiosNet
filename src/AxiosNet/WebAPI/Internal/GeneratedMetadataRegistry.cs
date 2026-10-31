using AxiosNet.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 生成元数据注册表：写时复制 + <c>Volatile.Read</c>，读路径无锁、无分配。
/// 注册只发生在模块初始化 / 启动期；注册后不再修改字典本身（替换整个实例）。
/// 同一类型重复注册属编程错误，直接抛出（生成器与手工注册不应同时声明同一类型）。
/// 可选冻结：<see cref="Freeze"/> 之后任何注册都抛错，把「启动后才冒出来的元数据」变成可定位的失败，
/// 而不是运行到某次请求才出现的行为差异。
/// </summary>
internal static class GeneratedMetadataRegistry
{
    private static readonly object Gate = new();

    private static volatile bool _frozen;

    private static Dictionary<Type, RequestModelDeclaration> _requestModels = new();
    private static Dictionary<Type, InterfaceEntry> _interfaces = new();
    private static Dictionary<Type, ObjectShape> _shapes = new();
    private static Dictionary<Type, MapAccessor> _maps = new();

    /// <summary>注册表是否已冻结（冻结只影响写入，读路径不变）。</summary>
    internal static bool IsFrozen => _frozen;

    /// <summary>冻结注册表：此后任何注册都抛 <see cref="InvalidOperationException"/>。可重复调用。</summary>
    internal static void Freeze()
    {
        lock (Gate)
        {
            _frozen = true;
        }
    }

    /// <summary>测试钩子：冻结是进程级状态，用例之间必须复位，否则后续注册会全部失败。</summary>
    internal static void UnfreezeForTests()
    {
        lock (Gate)
        {
            _frozen = false;
        }
    }

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

            ThrowIfFrozen(declaration.ModelType, "请求模型");

            var next = new Dictionary<Type, RequestModelDeclaration>(current) { [declaration.ModelType] = declaration };
            Volatile.Write(ref _requestModels, next);

            // 该类型可能已被 JIT 反射构建并缓存：显式注册必须压过旧缓存。
            RequestModelMetadata.Invalidate(declaration.ModelType);
        }
    }

    internal static void RegisterInterface(InterfaceDeclarationData declaration, Func<ApiClient, object> factory)
        => AddInterface(declaration, factory, allowExisting: false);

    /// <summary>
    /// 生成代码的注册入口：两个互不引用的程序集引用同一份契约程序集时，会各自为同一接口生成代理并注册；
    /// 生成结果等价，保留先注册的一份即可（否则第二个模块初始化器会抛错）。
    /// </summary>
    internal static void RegisterGeneratedInterface(InterfaceDeclarationData declaration, Func<ApiClient, object> factory)
        => AddInterface(declaration, factory, allowExisting: true);

    private static void AddInterface(InterfaceDeclarationData declaration, Func<ApiClient, object> factory, bool allowExisting)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        ArgumentNullException.ThrowIfNull(factory);
        declaration.Factory = factory;

        lock (Gate)
        {
            var current = _interfaces;
            if (current.ContainsKey(declaration.InterfaceType))
            {
                if (allowExisting)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"接口 {Describe(declaration.InterfaceType)} 的元数据已注册：源生成器与手工 AxiosMetadata.RegisterInterface 不能同时声明同一类型。");
            }

            // 冻结只禁止真正的写入：上面的等价重复注册是空操作，放行。
            ThrowIfFrozen(declaration.InterfaceType, "接口");

            var next = new Dictionary<Type, InterfaceEntry>(current)
            {
                [declaration.InterfaceType] = new InterfaceEntry(declaration, factory)
            };

            Volatile.Write(ref _interfaces, next);
        }
    }

    internal static void RegisterShape(ObjectShape shape)
        => AddShape(shape, allowExisting: false);

    /// <summary>生成代码的形状注册：同一被引用类型可能被多个程序集各生成一份，结果等价，保留先注册的一份。</summary>
    internal static void RegisterGeneratedShape(ObjectShape shape)
        => AddShape(shape, allowExisting: true);

    private static void AddShape(ObjectShape shape, bool allowExisting)
    {
        ArgumentNullException.ThrowIfNull(shape);
        lock (Gate)
        {
            var current = _shapes;
            if (current.ContainsKey(shape.Type))
            {
                if (allowExisting)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"类型 {Describe(shape.Type)} 的对象形状已注册：[AxiosShape] 与手工 AxiosMetadata.RegisterShape 不能同时声明同一类型。");
            }

            ThrowIfFrozen(shape.Type, "对象形状");

            var next = new Dictionary<Type, ObjectShape>(current) { [shape.Type] = shape };
            Volatile.Write(ref _shapes, next);

            // 与请求模型同理：显式注册必须压过 JIT 反射构建的旧缓存。
            ShapeCache.Invalidate(shape.Type);
        }
    }

    internal static void RegisterMap(MapAccessor accessor)
        => AddMap(accessor, allowExisting: false);

    /// <summary>生成代码的映射注册：重复注册保留先注册的一份（与 <see cref="RegisterGeneratedShape"/> 同理）。</summary>
    internal static void RegisterGeneratedMap(MapAccessor accessor)
        => AddMap(accessor, allowExisting: true);

    private static void AddMap(MapAccessor accessor, bool allowExisting)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        lock (Gate)
        {
            var current = _maps;
            if (current.ContainsKey(accessor.Type))
            {
                if (allowExisting)
                {
                    return;
                }

                throw new InvalidOperationException(
                    $"类型 {Describe(accessor.Type)} 的映射访问器已注册：源生成器与手工 AxiosMetadata.RegisterMap 不能同时声明同一类型。");
            }

            ThrowIfFrozen(accessor.Type, "映射访问器");

            var next = new Dictionary<Type, MapAccessor>(current) { [accessor.Type] = accessor };
            Volatile.Write(ref _maps, next);

            // 手工注册可能晚于首次展平：丢掉 QueryMap 按类型缓存的「没有访问器」结果。
            QueryMap.Invalidate();
        }
    }

    private static string Describe(Type type) => type.FullName ?? type.Name;

    /// <summary>
    /// 冻结后拒绝写入。消息必须能自证现场：说清楚是哪个类型、哪个程序集在冻结之后才来注册——
    /// 最常见的成因是「启动时冻结，之后才加载带生成元数据的程序集」。
    /// </summary>
    private static void ThrowIfFrozen(Type target, string kind)
    {
        if (!_frozen)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{kind} {Describe(target)} 的元数据在注册表冻结后才注册（程序集 {target.Assembly.GetName().Name}）："
            + "AxiosMetadata.Freeze() 之后不允许再写入元数据。"
            + "请把 Freeze() 移到所有程序集加载与手工注册完成之后，或去掉 Freeze()——它是可选加固，不调用也完全可用。");
    }
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
        var errors = ValidateAll();
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }
    }

    /// <summary>返回全部声明错误（按方法顺序，保留重载各自的错误）；空列表 = 合法。启动期校验与 Create 共用。</summary>
    public IReadOnlyList<string> ValidateAll()
    {
        // 接口级错误（configure 在声明任何方法前抛错）没有方法可挂：直接返回，避免被空方法表吞掉。
        if (Declaration.Error is { } error)
        {
            return [error];
        }

        var errors = new List<string>();
        foreach (var method in GetMethods())
        {
            if (method.BuildError is { } buildError)
            {
                errors.Add(buildError);
            }
        }

        return errors;
    }
}
