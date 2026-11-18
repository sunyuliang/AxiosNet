using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using AxiosNet.Metadata;
using AxiosNet.Reflection;

namespace AxiosNet.Internal.Serialization;

/// <summary>
/// 对象形状入口：params / 表单 / multipart 展平所需的成员清单。
/// 注册表（源生成或手工注册）优先；JIT 下回退反射构建并缓存；AOT 下缺少形状直接失败，不猜测基类布局。
/// </summary>
internal static class ShapeCache
{
    /// <summary>
    /// 反射形状缓存的目标条目上限：到这条线只是「记满」，继续吸收到 <see cref="ClearThreshold"/> 才清空。
    /// </summary>
    /// <remarks>
    /// 键是 <see cref="Type"/>，强引用会让插件 / 可回收 <c>AssemblyLoadContext</c> 的 <c>Unload()</c> 永不生效。
    /// 用「上限 + 清空」而不是弱引用：清空后旧类型的强引用消失、程序集随即可回收，而代价只是一次重算
    /// （只有 JIT 反射路径会走到这里，AOT 下这段填充逻辑被裁掉）。
    /// </remarks>
    private const int MaxEntries = 2048;

    /// <summary>
    /// 触发整体清空的条目数（<see cref="MaxEntries"/> 的 2 倍）。
    /// </summary>
    /// <remarks>
    /// 为什么不在 <see cref="MaxEntries"/> 那条线就清：工作集**略超**上限时（例如 2100 个类型），
    /// 「每来一个新类型就清一次」会让之后每个类型都重新反射构建 —— 稳态退化成持续重算，比不清还慢。
    /// 攒到 2 倍再清，一次清空的 O(n) 成本摊到「随后再插入 n 条」上，摊还回到每插入一次；
    /// 代价只是字典最多多装一倍条目（4096 个指针级槽位，可忽略）。
    /// </remarks>
    private const int ClearThreshold = MaxEntries * 2;

    /// <summary>反射构建的形状（AOT 下从不写入：填充它的分支被裁掉了）。</summary>
    private static readonly ConcurrentDictionary<Type, ObjectShape> Cache = new();

    /// <summary>
    /// 取类型形状；<paramref name="fieldPath"/> 是展平到当前位置的字段名（只用于 AOT 缺失时的错误定位）。
    /// 形状声明损坏（注册期 configure 抛错）时抛出 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static ObjectShape Get(Type type, string? fieldPath = null)
    {
        if (ShapeRegistry.TryGetShape(type, out var registered))
        {
            return Validate(registered);
        }

        // 条件里必须是 RuntimeFeature.IsDynamicCodeSupported 本身：ILC 折成常量 false 后，这段反射实现才不可达并被裁掉。
        if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
        {
            return Validate(GetOrAdd(type));
        }

        throw AxiosAotMetadataException.MissingShape(type, fieldPath);
    }

    /// <summary>
    /// 尽力取形状（回收已交出所有权的流 / 内容时使用）：AOT 下没有形状就停止下钻，不抛错。
    /// 注册期损坏的形状同样静默跳过——回收路径不应成为第二个报错点。
    /// </summary>
    public static bool TryGet(Type type, [NotNullWhen(true)] out ObjectShape? shape)
    {
        if (ShapeRegistry.TryGetShape(type, out var registered))
        {
            shape = registered.Error is null ? registered : null;
            return shape is not null;
        }

        if (!RuntimeSupport.ForceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported)
        {
            shape = GetOrAdd(type);
            return true;
        }

        shape = null;
        return false;
    }

    /// <summary>显式注册压过旧的反射缓存（同一类型的热路径第一次读取前完成）。</summary>
    public static void Invalidate(Type type) => Cache.TryRemove(type, out _);

    /// <summary>清空反射构建的形状缓存（能力切换时调用，避免 AOT 与 JIT 的构建结论混用）。</summary>
    public static void InvalidateAll() => Cache.Clear();

    /// <summary>取类型形状，没有就用反射建一份并缓存（同一类型只建一次）。</summary>
    /// <param name="type">要展平的类型。</param>
    /// <returns>构建好的形状（可能带 <c>Error</c>，由调用方决定抛还是跳过）。</returns>
    private static ObjectShape GetOrAdd(Type type)
    {
        if (Cache.TryGetValue(type, out var cached))
        {
            return cached;
        }

        var shape = ObjectShapeReflection.Create(type);

        // Count 会取全部锁，只在未命中时调一次；命中路径完全不碰它。
        if (Cache.Count >= ClearThreshold)
        {
            Cache.Clear();
        }

        return Cache.GetOrAdd(type, shape);
    }

    /// <summary>构建失败（<c>Error</c> 非 null）就抛出去，成功则原样返回。</summary>
    private static ObjectShape Validate(ObjectShape shape)
        => shape.Error is null ? shape : throw new InvalidOperationException(shape.Error);
}
