namespace AxiosNet.Internal.Serialization;

/// <summary>生成元数据注册表：对象形状、字典映射，以及进程级的冻结开关。</summary>
/// <remarks>写时复制 + <c>Volatile.Read</c>，读路径无锁；注册只发生在启动期。</remarks>
internal static class ShapeRegistry
{
    // 写入锁；读路径不加锁（写时复制 + Volatile.Read）。
    private static readonly object Gate = new();

    // 冻结标志：冻结后任何注册都抛，读路径不受影响。
    private static volatile bool _frozen;

    // 类型 → 对象形状（写时复制，整体替换）。
    private static Dictionary<Type, ObjectShape> _shapes = new();
    // 类型 → 字符串键字典访问器（写时复制，整体替换）。
    private static Dictionary<Type, MapAccessor> _maps = new();

    /// <summary>注册表是否已冻结（冻结只影响写入）。</summary>
    internal static bool IsFrozen => _frozen;

    /// <summary>冻结注册表：此后任何注册都抛 <see cref="InvalidOperationException"/>。可重复调用。</summary>
    internal static void Freeze()
    {
        lock (Gate)
        {
            _frozen = true;
        }
    }

    /// <summary>测试钩子：复位冻结状态。</summary>
    internal static void UnfreezeForTests()
    {
        lock (Gate)
        {
            _frozen = false;
        }
    }

    /// <summary>取已注册的形状；查不到返回 <c>false</c>，不触发反射构建。</summary>
    internal static bool TryGetShape(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ObjectShape? shape)
        => Volatile.Read(ref _shapes).TryGetValue(type, out shape);

    /// <summary>取已注册的字典访问器；查不到返回 <c>false</c>。</summary>
    internal static bool TryGetMap(Type type, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MapAccessor? accessor)
        => Volatile.Read(ref _maps).TryGetValue(type, out accessor);

    /// <summary>手工注册对象形状：同一类型重复注册、或注册表已冻结时抛 <see cref="InvalidOperationException"/>。</summary>
    internal static void RegisterShape(ObjectShape shape)
        => AddShape(shape, allowExisting: false);

    /// <summary>生成代码的形状注册：重复注册保留先注册的一份。</summary>
    internal static void RegisterGeneratedShape(ObjectShape shape)
        => AddShape(shape, allowExisting: true);

    /// <summary>手工注册字典访问器：同一类型重复注册、或注册表已冻结时抛 <see cref="InvalidOperationException"/>。</summary>
    internal static void RegisterMap(MapAccessor accessor)
        => AddMap(accessor, allowExisting: false);

    /// <summary>生成代码的映射注册：重复注册保留先注册的一份。</summary>
    internal static void RegisterGeneratedMap(MapAccessor accessor)
        => AddMap(accessor, allowExisting: true);

    /// <summary>冻结后拒绝写入；消息带上类型与程序集以便定位。</summary>
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

    /// <summary>异常 / 日志里的类型名：全名优先，无名类型退回短名。</summary>
    internal static string Describe(Type type) => type.FullName ?? type.Name;

    /// <summary>登记对象形状：写时复制换表并失效反射缓存；<paramref name="allowExisting"/> 为 true 时重复注册忽略（先注册者胜）。</summary>
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

            // 显式注册必须压过反射缓存。
            ShapeCache.Invalidate(shape.Type);
        }
    }

    /// <summary>登记映射访问器：写时复制换表并清掉 <see cref="QueryMap"/> 的按类型缓存。</summary>
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

            // 丢掉 QueryMap 按类型缓存的「没有访问器」结果。
            QueryMap.Invalidate();
        }
    }
}
