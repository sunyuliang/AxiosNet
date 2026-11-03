using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AxiosNet.Reflection;

/// <summary>
/// 属性取值器：JIT 下把 getter 缓存成强类型委托，AOT 下退回反射取值。
/// 只被反射路径（形状展平、请求模型声明读取）使用。
/// </summary>
internal static class PropertyAccessor
{
    // getter 缓存：反射调用比委托慢一个量级，同一个属性只编译一次。
    private static readonly ConcurrentDictionary<PropertyInfo, Func<object, object?>> Getters = new();

    /// <summary>取属性值：按 <see cref="PropertyInfo"/> 缓存已编译的 getter。</summary>
    public static object? GetValue(PropertyInfo property, object target)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return property.GetValue(target);
        }

        return Getters.GetOrAdd(property, static p => CreateGetter(p))(target);
    }

    /// <summary>借泛型包装类编译出强类型 getter 委托，省掉每次取值的反射开销；AOT 下不会进这里。</summary>
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050",
        Justification = "仅在 RuntimeFeature.IsDynamicCodeSupported 为 true 时调用；AOT 走反射回退分支。")]
    private static Func<object, object?> CreateGetter(PropertyInfo property)
    {
        var wrapperType = typeof(GetterWrapper<,>).MakeGenericType(
            property.DeclaringType!,
            property.PropertyType);
        return (Func<object, object?>)wrapperType
            .GetMethod(nameof(GetterWrapper<object, object?>.Create), BindingFlags.Public | BindingFlags.Static)!
            .Invoke(null, [property])!;
    }

    private static class GetterWrapper<TTarget, TValue>
    {
        /// <summary>把属性 getter 绑成 <c>Func&lt;TTarget, TValue&gt;</c> 再包一层装箱转换；没有可读 getter 时抛出。</summary>
        public static Func<object, object?> Create(PropertyInfo property)
        {
            var getter = property.GetGetMethod(nonPublic: true)
                ?? throw new InvalidOperationException($"属性 {property.Name} 没有可读的 getter。");
            var compiled = (Func<TTarget, TValue>)getter.CreateDelegate(typeof(Func<TTarget, TValue>));
            return target => compiled((TTarget)target);
        }
    }
}
