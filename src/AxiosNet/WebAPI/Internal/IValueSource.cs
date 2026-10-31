using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace AxiosNet.WebAPI.Internal;

/// <summary>绑定取值来源：类模型读属性，接口模型读方法参数。</summary>
internal interface IValueSource
{
    /// <summary>绑定名缺省值（属性名 / 参数名）。</summary>
    string Name { get; }

    /// <summary>错误消息中的定位文本（如 <c>UserRequest.Id</c>）。</summary>
    string Description { get; }

    Type ValueType { get; }

    /// <summary>取值；<paramref name="target"/> 为请求模型实例或方法参数数组。</summary>
    object? GetValue(object target);
}

/// <summary>类模型：从请求模型实例的属性取值。</summary>
internal sealed class PropertyValueSource : IValueSource
{
    private readonly PropertyInfo _property;

    internal PropertyValueSource(PropertyInfo property) => _property = property;

    public string Name => _property.Name;

    public string Description => $"{_property.DeclaringType?.Name ?? _property.Name}.{_property.Name}";

    public Type ValueType => _property.PropertyType;

    public object? GetValue(object target) => PropertyAccessor.GetValue(_property, target);
}

/// <summary>接口模型：从方法参数数组按位置取值（无反射）。</summary>
internal sealed class ArgumentValueSource : IValueSource
{
    private readonly int _index;

    internal ArgumentValueSource(int index, string name, Type valueType, string description)
    {
        _index = index;
        Name = name;
        ValueType = valueType;
        Description = description;
    }

    public string Name { get; }

    public string Description { get; }

    public Type ValueType { get; }

    public object? GetValue(object target) => ((object?[])target)[_index];
}

/// <summary>属性读取器：JIT 下缓存取值委托，AOT 下退回反射。</summary>
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
