using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>
/// 成员级绑定声明：请求模型的属性与接口方法的参数共用同一份形状，绑定规则由
/// <see cref="BindingDeclarationWalker"/> 统一执行。只承载特性事实，校验不在本类型内。
/// </summary>
internal abstract class BindingMemberDeclaration
{
    /// <summary>成员名（属性名 / 参数名），也是 <see cref="DefaultName"/> 的兜底来源。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>成员声明的类型；Path / Query / Header 的文本化与正文序列化都按它选规则。</summary>
    public Type ValueType { get; set; } = typeof(object);

    /// <summary>错误消息里的定位文本（如 <c>UserRequest.Id</c>）；<c>null</c> 表示由构建方按属主与成员名合成。</summary>
    public string? Description { get; set; }

    /// <summary>成员级格式化（<c>[Format]</c>）；<c>null</c> 表示走通道默认规则。</summary>
    public ValueFormat? Format { get; set; }

    /// <summary>是否 <c>[ApiIgnore]</c>：完全不绑定，也不参与默认通道判定。</summary>
    public bool Ignore { get; set; }

    /// <summary>是否标注了 <c>[Path]</c>：值填进路由模板的同名占位符。</summary>
    public bool Path { get; set; }

    /// <summary><c>[Path]</c> 的占位符名；<c>null</c> 表示用 <see cref="DefaultName"/>。</summary>
    public string? PathName { get; set; }

    /// <summary>是否标注了 <c>[Query]</c>：值进查询串（集合按 <c>QueryCollectionFormat</c> 展开）。</summary>
    public bool Query { get; set; }

    /// <summary><c>[Query]</c> 的键名；<c>null</c> 表示用 <see cref="DefaultName"/>。</summary>
    public string? QueryName { get; set; }

    /// <summary>是否标注了 <c>[Header]</c>：值进单个请求头。</summary>
    public bool Header { get; set; }

    /// <summary><c>[Header]</c> 的头名；<c>null</c> 表示用 <see cref="DefaultName"/>。</summary>
    public string? HeaderName { get; set; }

    /// <summary>是否标注了 <c>[HeaderCollection]</c>：整个字典逐项展开成头，不再按单值处理。</summary>
    public bool HeaderCollection { get; set; }

    /// <summary>是否标注了 <c>[Body]</c>；键名与形态看 <see cref="BodyName"/>。</summary>
    public bool Body { get; set; }

    /// <summary><c>[Body]</c> 的键名；<c>null</c> 表示无参构造（整份正文），非 <c>null</c>（含空串）表示正文里的一个键。</summary>
    public string? BodyName { get; set; }

    /// <summary>是否标注了 <c>[Part]</c>：值作为一个 <c>multipart</c> 部件（要求请求是 multipart）。</summary>
    public bool Part { get; set; }

    /// <summary>部件名；<c>null</c> 表示用 <see cref="DefaultName"/>。</summary>
    public string? PartName { get; set; }

    /// <summary>上传文件的 <c>filename</c>；只对文件类形态（流 / 字节）有意义，其余形态忽略。</summary>
    public string? PartFileName { get; set; }

    /// <summary>该部件自己的 <c>Content-Type</c>；<c>null</c> 表示按值形态推断。</summary>
    public string? PartContentType { get; set; }

    /// <summary>是否至少命中一个绑定目标；一个都没有说明是误标，构建期报错。</summary>
    public bool HasAnyBinding => Path || Query || Header || HeaderCollection || Body || Part;

    /// <summary>默认键名 / 部件名：属性取 <c>[JsonPropertyName]</c> 或属性名，参数取参数名。</summary>
    public virtual string DefaultName => Name;

    /// <summary><see cref="DefaultName"/> 是否来自 <c>[JsonPropertyName]</c>（是则不套 JSON 命名策略）。</summary>
    public virtual bool DefaultNameFromAttribute => false;

    /// <summary>构造取值来源；<paramref name="fallbackDescription"/> 在 <see cref="Description"/> 为 <c>null</c> 时用作定位文本。</summary>
    internal abstract IValueSource CreateSource(string fallbackDescription);

    /// <summary>取值来源的前置校验；默认无校验。</summary>
    /// <param name="memberPath">成员的定位文本，如 <c>UserRequest.Id</c>。</param>
    internal virtual void EnsureSourceAvailable(string memberPath)
    {
    }
}
