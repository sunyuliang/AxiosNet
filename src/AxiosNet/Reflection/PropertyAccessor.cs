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
    private static readonly ConcurrentDictionary<PropertyInfo, Func<object, object?>> Getters = new();

    public static object? GetValue(PropertyInfo property, object target)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return property.GetValue(target);
        }

        return Getters.GetOrAdd(property, static p => CreateGetter(p))(target);
    }

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
        public static Func<object, object?> Create(PropertyInfo property)
        {
            var getter = property.GetGetMethod(nonPublic: true)
                ?? throw new InvalidOperationException($"属性 {property.Name} 没有可读的 getter。");
            var compiled = (Func<TTarget, TValue>)getter.CreateDelegate(typeof(Func<TTarget, TValue>));
            return target => compiled((TTarget)target);
        }
    }
}
