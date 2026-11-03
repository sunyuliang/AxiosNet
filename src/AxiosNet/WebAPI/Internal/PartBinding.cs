namespace AxiosNet.WebAPI.Internal;

/// <summary>解析后的部件绑定（请求模型 / 接口方法装配表单共用）。</summary>
/// <param name="Name">部件字段名（已解析：显式声明或默认名）。</param>
/// <param name="Source">读取部件值的绑定来源（属性或方法参数）。</param>
/// <param name="HasExplicitName">名字是否由 <see cref="Attributes.PartAttribute.Name"/> 显式指定（决定它是否压过 FormFile.FieldName）。</param>
/// <param name="FileName">声明的文件名；null 表示沿用值 / builder 默认。</param>
/// <param name="ContentType">声明的部件 Content-Type；null 表示沿用值 / builder 默认。</param>
internal readonly record struct PartBinding(
    string Name,
    IValueSource Source,
    bool HasExplicitName,
    string? FileName,
    string? ContentType);
