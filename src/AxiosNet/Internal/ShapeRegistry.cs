namespace AxiosNet.Internal;

/// <summary>
/// 生成元数据注册表的核心部分：对象形状与字典映射（params / 表单 / multipart 展平要用），
/// 以及进程级的「冻结」开关（声明式 API 那半张表也读同一个开关）。
/// 写时复制 + <c>Volatile.Read</c>，读路径无锁、无分配；注册只发生在模块初始化 / 启动期。
/// </summary>
internal static class ShapeRegistry
{
    private static readonly object Gate = new();

    private static volatile bool _frozen;

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

    internal static bool TryGetShape(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ObjectShape? shape)
        => Volatile.Read(ref _shapes).TryGetValue(type, out shape);

    internal static bool TryGetMap(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MapAccessor? accessor)
        => Volatile.Read(ref _maps).TryGetValue(type, out accessor);

    internal static void RegisterShape(ObjectShape shape)
        => AddShape(shape, allowExisting: false);

    /// <summary>生成代码的形状注册：同一被引用类型可能被多个程序集各生成一份，结果等价，保留先注册的一份。</summary>
    internal static void RegisterGeneratedShape(ObjectShape shape)
        => AddShape(shape, allowExisting: true);

    internal static void RegisterMap(MapAccessor accessor)
        => AddMap(accessor, allowExisting: false);

    /// <summary>生成代码的映射注册：重复注册保留先注册的一份（与 <see cref="RegisterGeneratedShape"/> 同理）。</summary>
    internal static void RegisterGeneratedMap(MapAccessor accessor)
        => AddMap(accessor, allowExisting: true);

    /// <summary>
    /// 冻结后拒绝写入。消息必须能自证现场：说清楚是哪个类型、哪个程序集在冻结之后才来注册——
    /// 最常见的成因是「启动时冻结，之后才加载带生成元数据的程序集」。
    /// </summary>
    internal static void ThrowIfFrozen(Type target, string kind)
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

    internal static string Describe(Type type) => type.FullName ?? type.Name;

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
}
