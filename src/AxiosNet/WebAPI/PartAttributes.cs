using System.Reflection;
using System.Text.Json.Serialization;

namespace AxiosNet.WebAPI;

/// <summary>
/// 声明请求模型的请求体是 <c>multipart/form-data</c>：<b>模型自身即表单</b>——
/// 未标注参数特性的属性默认按部件绑定（字段名 = 属性名），标了 <see cref="PartAttribute"/> 的属性
/// 可覆盖字段名、文件名与部件 Content-Type；标了 <c>[Query]</c> / <c>[Header]</c> / <c>[Path]</c> 的照常走各自绑定。
/// <para>与 <c>[Body]</c> 互斥（一个模型两种请求体形态，构建期报错）。</para>
/// </summary>
/// <example>
/// <code>
/// [Post("upload")]
/// [Multipart]
/// public sealed class UploadRequest : IApiRequest&lt;UploadResult&gt;
/// {
///     [Part] public string Title { get; set; } = "";
///     [Part(FileName = "a.bin", ContentType = "application/octet-stream")] public byte[] Data { get; set; } = [];
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class MultipartAttribute : Attribute
{
}

/// <summary>
/// multipart 模型上的部件声明：覆盖部件字段名，并把 byte[] / Stream 类属性声明成
/// 带文件名 / Content-Type 的文件部件。
/// <para>
/// 名字优先级：<see cref="Name"/> &gt; <see cref="FormFile.FieldName"/> &gt;
/// 默认名（<see cref="JsonPropertyNameAttribute"/> 或属性名，再经 <c>FormFieldNameSelector</c>）。
/// </para>
/// <para>
/// 值规则：<see cref="FormFile"/> 的 <see cref="FormFile.FileName"/> / <see cref="FormFile.ContentType"/>
/// 可被特性声明覆盖；byte[] / Stream 未声明文件名时 filename = 字段名、类型 = <c>application/octet-stream</c>（builder 默认）；
/// <b>字符串 / 标量永远是普通字段</b>——要发文本文件请用 <see cref="FormFile"/> 或 byte[]；
/// 集合逐项应用（同名重复部件）；嵌套对象 / 字典整体透传（由 builder 展平为 <c>a[b]</c>）。
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = true, AllowMultiple = false)]
public sealed class PartAttribute : Attribute
{
    /// <summary>声明一个部件（字段名回退到 [JsonPropertyName] / 属性名）。</summary>
    public PartAttribute()
    {
    }

    /// <summary>声明一个部件并指定字段名（Content-Disposition 的 <c>name</c>）。</summary>
    /// <param name="name">部件字段名；不能为空白。</param>
    public PartAttribute(string name)
    {
        Name = name;
    }

    /// <summary>部件字段名（Content-Disposition 的 <c>name</c>）；null 表示用 [JsonPropertyName] / 属性名。</summary>
    public string? Name { get; }

    /// <summary>文件部件的文件名（Content-Disposition 的 <c>filename</c>）；null 表示用字段名（builder 默认）。</summary>
    /// <remarks>只对文件类值（FormFile / Stream / byte[] / Memory）有意义；字符串 / 标量不受影响。</remarks>
    public string? FileName { get; set; }

    /// <summary>部件的 Content-Type；null 表示 <c>application/octet-stream</c>（builder 默认）。</summary>
    /// <remarks>只对文件类值有意义；字符串 / 标量不受影响。</remarks>
    public string? ContentType { get; set; }
}

/// <summary>解析后的部件绑定（<c>RequestModelMetadata.ResolveBody</c> 装配表单用）。</summary>
/// <param name="Name">部件字段名（已解析：显式声明或默认名）。</param>
/// <param name="Property">读取部件值的属性。</param>
/// <param name="HasExplicitName">名字是否由 <see cref="PartAttribute.Name"/> 显式指定（决定它是否压过 FormFile.FieldName）。</param>
/// <param name="FileName">声明的文件名；null 表示沿用值 / builder 默认。</param>
/// <param name="ContentType">声明的部件 Content-Type；null 表示沿用值 / builder 默认。</param>
internal readonly record struct PartBinding(
    string Name,
    PropertyInfo Property,
    bool HasExplicitName,
    string? FileName,
    string? ContentType);
