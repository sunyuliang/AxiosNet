using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Internal;

/// <summary>生成路径的取值来源：直接调用生成期烘焙的 getter / 委托，不读 <see cref="System.Reflection.PropertyInfo"/>。</summary>
internal sealed class DelegateValueSource : IValueSource
{
    private readonly Func<object, object?> _getValue;

    internal DelegateValueSource(
        string name,
        Type valueType,
        Func<object, object?> getValue,
        string description,
        ValueFormat? format = null)
    {
        Name = name;
        ValueType = valueType;
        _getValue = getValue;
        Description = description;
        Format = format;
    }

    public string Name { get; }

    public string Description { get; }

    public Type ValueType { get; }

    public ValueFormat? Format { get; }

    /// <summary>直接调用生成期写死的取值委托，取值不经过反射。</summary>
    public object? GetValue(object target) => _getValue(target);
}
