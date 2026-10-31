using System.Text.Json.Serialization;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.WebAPI;

/// <summary>声明请求体是 <c>multipart/form-data</c>：请求模型标在类上、接口级声明标在方法上。</summary>
/// <remarks>
/// 模型自身即表单：未标注参数特性的属性 / 参数默认按部件绑定，字段名取 <see cref="JsonPropertyNameAttribute"/> 或属性名 / 参数名。
/// 标了 <see cref="PartAttribute"/> 的成员可覆盖字段名、文件名与部件 Content-Type；与 <c>[Body]</c> 互斥。
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class MultipartAttribute : Attribute
{
}

/// <summary>
/// multipart 的部件声明（请求模型属性 / 接口方法参数）：覆盖部件字段名，并把 <c>byte[]</c> / <c>Stream</c> 类值声明成带文件名 / Content-Type 的文件部件。
/// </summary>
/// <remarks>
/// 名字优先级：<see cref="Name"/> &gt; <see cref="FormFile.FieldName"/> &gt; 默认名（<see cref="JsonPropertyNameAttribute"/> 或属性名 / 参数名）。
/// <c>byte[]</c> / <c>Stream</c> 未声明文件名时 filename = 字段名、类型 = <c>application/octet-stream</c>；字符串 / 标量永远是普通字段，集合逐项应用。
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter, Inherited = true, AllowMultiple = false)]
public sealed class PartAttribute : Attribute
{
    /// <summary>声明一个部件（字段名回退到 [JsonPropertyName] / 属性名 / 参数名）。</summary>
    public PartAttribute()
    {
    }

    /// <summary>声明一个部件并指定字段名（Content-Disposition 的 <c>name</c>）。</summary>
    /// <param name="name">部件字段名；不能为空白。</param>
    public PartAttribute(string name)
    {
        Name = name;
    }

    /// <summary>部件字段名（Content-Disposition 的 <c>name</c>）；null 表示用 [JsonPropertyName] / 属性名（接口方法的参数回退到参数名）。</summary>
    public string? Name { get; }

    /// <summary>文件部件的文件名（Content-Disposition 的 <c>filename</c>）；null 表示用字段名（builder 默认）。</summary>
    /// <remarks>只对文件类值（FormFile / Stream / byte[] / Memory）有意义；字符串 / 标量不受影响。</remarks>
    public string? FileName { get; set; }

    /// <summary>部件的 Content-Type；null 表示 <c>application/octet-stream</c>（builder 默认）。</summary>
    /// <remarks>只对文件类值有意义；字符串 / 标量不受影响。</remarks>
    public string? ContentType { get; set; }
}

/// <summary>解析后的部件绑定（请求模型 / 接口方法装配表单共用）。</summary>
/// <param name="Name">部件字段名（已解析：显式声明或默认名）。</param>
/// <param name="Source">读取部件值的绑定来源（属性或方法参数）。</param>
/// <param name="HasExplicitName">名字是否由 <see cref="PartAttribute.Name"/> 显式指定（决定它是否压过 FormFile.FieldName）。</param>
/// <param name="FileName">声明的文件名；null 表示沿用值 / builder 默认。</param>
/// <param name="ContentType">声明的部件 Content-Type；null 表示沿用值 / builder 默认。</param>
internal readonly record struct PartBinding(
    string Name,
    IValueSource Source,
    bool HasExplicitName,
    string? FileName,
    string? ContentType);
