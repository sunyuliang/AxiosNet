using AxiosNet.Metadata;

namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>
/// 成员级绑定声明：请求模型的属性与接口方法的参数共用同一份形状，绑定规则由
/// <see cref="BindingDeclarationWalker"/> 统一执行。只承载特性事实，校验不在本类型内。
/// </summary>
internal abstract class BindingMemberDeclaration
{
    public string Name { get; set; } = string.Empty;

    public Type ValueType { get; set; } = typeof(object);

    /// <summary>错误消息里的定位文本（如 <c>UserRequest.Id</c>）；<c>null</c> 表示由构建方按属主与成员名合成。</summary>
    public string? Description { get; set; }

    /// <summary>成员级格式化（<c>[Format]</c>）；<c>null</c> 表示走通道默认规则。</summary>
    public ValueFormat? Format { get; set; }

    /// <summary>是否 <c>[ApiIgnore]</c>：完全不绑定，也不参与默认通道判定。</summary>
    public bool Ignore { get; set; }

    public bool Path { get; set; }

    public string? PathName { get; set; }

    public bool Query { get; set; }

    public string? QueryName { get; set; }

    public bool Header { get; set; }

    public string? HeaderName { get; set; }

    public bool HeaderCollection { get; set; }

    /// <summary>是否标注了 <c>[Body]</c>；键名与形态看 <see cref="BodyName"/>。</summary>
    public bool Body { get; set; }

    /// <summary><c>[Body]</c> 的键名；<c>null</c> 表示无参构造（整份正文），非 <c>null</c>（含空串）表示正文里的一个键。</summary>
    public string? BodyName { get; set; }

    public bool Part { get; set; }

    public string? PartName { get; set; }

    public string? PartFileName { get; set; }

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
