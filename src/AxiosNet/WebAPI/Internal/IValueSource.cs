using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Internal;

/// <summary>绑定取值来源：类模型读属性，接口模型读方法参数。</summary>
internal interface IValueSource
{
    /// <summary>绑定名缺省值（属性名 / 参数名）。</summary>
    string Name { get; }

    /// <summary>错误消息中的定位文本（如 <c>UserRequest.Id</c>）。</summary>
    string Description { get; }

    Type ValueType { get; }

    /// <summary>成员级格式化（<c>[Format]</c>）；<c>null</c> 表示走通道默认规则。</summary>
    ValueFormat? Format { get; }

    /// <summary>取值；<paramref name="target"/> 为请求模型实例或方法参数数组。</summary>
    object? GetValue(object target);
}

/// <summary>接口模型：从方法参数数组按位置取值（无反射）。</summary>
internal sealed class ArgumentValueSource : IValueSource
{
    private readonly int _index;

    internal ArgumentValueSource(int index, string name, Type valueType, string description, ValueFormat? format = null)
    {
        _index = index;
        Name = name;
        ValueType = valueType;
        Description = description;
        Format = format;
    }

    public string Name { get; }

    public string Description { get; }

    public Type ValueType { get; }

    public ValueFormat? Format { get; }

    public object? GetValue(object target) => ((object?[])target)[_index];
}
