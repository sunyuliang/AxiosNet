using AxiosNet.Metadata;

namespace AxiosNet.Internal.Content;

/// <summary>
/// 声明式 API 的默认请求体：把正文成员按键名聚合成一个对象（成员顺序＝声明顺序）。
/// 由 <c>BindingPlan</c> 在运行期装配，<see cref="RequestBodyFactory"/> 按 Content-Type 选择 JSON / 表单编码。
/// </summary>
internal sealed class AggregateBody
{
    public AggregateBody(AggregateBodyMember[] members)
    {
        Members = members;
    }

    /// <summary>按声明顺序排列的成员。</summary>
    public AggregateBodyMember[] Members { get; }

    /// <summary>序列化失败时用的来源描述（正文错误消息里的「from ...」）：首个成员名 + 成员数，不暴露内部类型名。</summary>
    public string SourceName => Members.Length == 0
        ? "默认正文"
        : (Members.Length == 1
            ? $"默认正文（{Members[0].Name}）"
            : $"默认正文（{Members[0].Name} 等 {Members.Length} 个键）");

    /// <summary>正文不再产出时回收成员里的流 / 文件内容（与 <see cref="FormData.DisposeOwned"/> 同一契约）。</summary>
    public void DisposeOwned()
    {
        foreach (var member in Members)
        {
            MultipartOwnership.DisposeOwned(member.Value);
        }
    }
}

/// <summary>聚合正文的一个成员：键名 + 值 + 声明类型（序列化时按声明类型取元数据）。</summary>
internal readonly struct AggregateBodyMember
{
    public AggregateBodyMember(
        string name,
        object? value,
        Type valueType,
        bool nameFromAttribute,
        ValueFormat? format,
        bool spread = false)
    {
        Name = name;
        Value = value;
        ValueType = valueType;
        NameFromAttribute = nameFromAttribute;
        Format = format;
        Spread = spread;
    }

    /// <summary>JSON / 表单里的键名（<c>[Body("名")]</c> &gt; <c>[JsonPropertyName]</c> &gt; 成员名）。</summary>
    public string Name { get; }

    /// <summary>成员的原始值；<c>null</c> 在 JSON 里照样输出 <c>null</c>。</summary>
    public object? Value { get; }

    /// <summary>成员的声明类型：AOT 下按它解析 <c>JsonTypeInfo</c>，避免合成字典缺元数据。</summary>
    public Type ValueType { get; }

    /// <summary>键名是否已显式指定（<c>[Body("名")]</c> / <c>[JsonPropertyName]</c>）；是则不套 <c>PropertyNamingPolicy</c>。</summary>
    public bool NameFromAttribute { get; }

    /// <summary>成员级 <c>[Format]</c>；<c>null</c> 表示走通道默认。</summary>
    public ValueFormat? Format { get; }

    /// <summary>是否把结构化值的属性展开到当前层（多个 <c>[RootBody]</c> 合并）；false 表示按 <see cref="Name"/> 作为键。</summary>
    public bool Spread { get; }
}
