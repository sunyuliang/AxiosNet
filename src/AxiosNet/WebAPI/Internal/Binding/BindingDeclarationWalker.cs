using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>绑定规则执行的上下文：属主文本、成员称谓，以及两处 multipart 相关措辞。</summary>
internal sealed class BindingWalkContext
{
    private readonly string _memberPathPrefix;

    /// <param name="owner">属主文本，如 <c>请求模型 UserRequest</c> / <c>接口 IUserApi.GetAsync</c>。</param>
    /// <param name="memberKind">成员称谓：类模型为「属性」，接口模型为「参数」。</param>
    /// <param name="memberPathPrefix">成员的定位前缀，如 <c>UserRequest.</c> / <c>IUserApi.GetAsync.</c>。</param>
    /// <param name="multipartMissingHint">[Part] 缺少 [Multipart] 时的提示语，如「模型没有 [Multipart]：请在类上声明 [Multipart]。」</param>
    /// <param name="multipartBodySubject">multipart 与正文互斥时说明装配主体，如「模型的请求体由全部属性装配」。</param>
    internal BindingWalkContext(
        string owner,
        string memberKind,
        string memberPathPrefix,
        string multipartMissingHint,
        string multipartBodySubject)
    {
        Owner = owner;
        MemberKind = memberKind;
        _memberPathPrefix = memberPathPrefix;
        MultipartMissingHint = multipartMissingHint;
        MultipartBodySubject = multipartBodySubject;
    }

    /// <summary>属主文本（<see cref="BindingAccumulator"/> 的同名构造参数）。</summary>
    internal string Owner { get; }

    /// <summary>成员称谓（<see cref="BindingAccumulator"/> 的同名构造参数）。</summary>
    internal string MemberKind { get; }

    /// <summary>[Part] 缺少 [Multipart] 的提示语；拼在「…标注了 [Part]，但」之后。</summary>
    internal string MultipartMissingHint { get; }

    /// <summary>multipart 与正文互斥时的装配主体说明；作为「两者互斥」的前一分句。</summary>
    internal string MultipartBodySubject { get; }

    /// <summary>成员的定位文本，如 <c>UserRequest.Id</c>。</summary>
    internal string MemberPath(string name) => string.Concat(_memberPathPrefix, name);

    /// <summary>带称谓的成员定位文本，如 <c>属性 UserRequest.Id</c>。</summary>
    internal string Describe(string name) => $"{MemberKind} {MemberPath(name)}";
}

/// <summary>
/// 成员级绑定规则的唯一实现：<see cref="BindMember"/> 按固定顺序处理一个成员的全部绑定目标，
/// 请求模型与接口方法只负责提供 <see cref="BindingWalkContext"/> 与各自的领域差异。
/// </summary>
internal static class BindingDeclarationWalker
{
    /// <summary>
    /// 执行单个成员的全部绑定规则；返回 <c>false</c> 表示被 <see cref="BindingMemberDeclaration.Ignore"/> 排除（不做任何绑定）。
    /// 顺序：二义性 → 取值校验 → 路径 / 头 / 头集合 → 部件 → 正文 → 默认通道。
    /// </summary>
    internal static bool BindMember(
        BindingWalkContext context,
        BindingAccumulator accumulator,
        BindingMemberDeclaration member,
        List<PartBinding> parts,
        HashSet<string> seenPartNames,
        bool isMultipart)
    {
        if (member.Ignore)
        {
            if (member.HasAnyBinding)
            {
                throw new InvalidOperationException(
                    $"{context.Describe(member.Name)} 同时标注了 [ApiIgnore] 与参数特性（Path / Query / Header / HeaderCollection / Part / Body）："
                    + "[ApiIgnore] 的语义是「完全不绑定」，两者同用是二义性。");
            }

            return false;
        }

        var memberPath = context.MemberPath(member.Name);
        member.EnsureSourceAvailable(memberPath);

        // 同一成员可同时绑定多处（路径 + 头 + 查询 + 请求体），按各处的 null 规则分别应用。
        var source = member.CreateSource(member.Description ?? memberPath);

        if (member.Path)
        {
            accumulator.AddPath(source, member.PathName ?? string.Empty);
        }

        if (member.Header)
        {
            accumulator.AddHeader(source, member.HeaderName ?? string.Empty);
        }

        if (member.HeaderCollection)
        {
            accumulator.AddHeaderCollection(source);
        }

        if (member.Part)
        {
            if (!isMultipart)
            {
                throw new InvalidOperationException(
                    $"{context.Describe(member.Name)} 标注了 [Part]，但{context.MultipartMissingHint}");
            }

            accumulator.ValidatePartFormat(source);
            parts.Add(CreatePart(context, member, source, seenPartNames));
        }

        if (member.Body)
        {
            // 键名优先级：[Body("名")] > [JsonPropertyName] > 成员名；无参 [Body] 的 BodyName 为 null，表示整份正文。
            accumulator.AddBody(
                source,
                member.DefaultName,
                member.BodyName,
                nameFromAttribute: !string.IsNullOrWhiteSpace(member.BodyName) || member.DefaultNameFromAttribute);
        }

        if (!member.HasAnyBinding)
        {
            if (isMultipart)
            {
                // multipart：无标注成员默认按部件绑定，不参与默认通道判定。
                if (!seenPartNames.Add(member.DefaultName))
                {
                    throw DuplicatePart(context, member.DefaultName);
                }

                parts.Add(new PartBinding(member.DefaultName, source, false, null, null));
                return true;
            }

            // 无标注：默认通道由 HTTP 方法决定（body 方法 → 按键名聚合；其余 → Query）。
            accumulator.AddUnannotated(source, member.DefaultName, member.DefaultNameFromAttribute);
        }
        else if (member.Query)
        {
            // 已有其他绑定的成员再显式 [Query]：追加一处查询绑定
            accumulator.AddQuery(source, member.QueryName ?? string.Empty);
        }

        return true;
    }

    /// <summary>[Multipart] 与 [Body] 互斥：multipart 的正文由全部成员装配，不能再声明正文目标。</summary>
    internal static void ValidateMultipartBody(
        BindingWalkContext context,
        BindingAccumulator accumulator,
        bool isMultipart)
    {
        if (isMultipart && accumulator.HasBody)
        {
            throw new InvalidOperationException(
                $"{context.Owner} 同时声明了 [Multipart] 与 [Body]（{accumulator.BodyName}）："
                + $"{context.MultipartBodySubject}，两者互斥。");
        }
    }

    /// <summary>解析部件名（显式声明 &gt; 默认名）并登记；同名冲突报错。文件名与部件媒体类型同批归一化。</summary>
    private static PartBinding CreatePart(
        BindingWalkContext context,
        BindingMemberDeclaration member,
        IValueSource source,
        HashSet<string> seenPartNames)
    {
        var display = $"{context.MemberKind} {source.Description}";
        var explicitName = DeclarationNormalization.OptionalPartValue(member.PartName, display, nameof(PartAttribute.Name));

        var name = explicitName ?? member.DefaultName;
        if (!seenPartNames.Add(name))
        {
            throw DuplicatePart(context, name);
        }

        return new PartBinding(
            name,
            source,
            explicitName is not null,
            DeclarationNormalization.OptionalPartValue(member.PartFileName, display, nameof(PartAttribute.FileName)),
            DeclarationNormalization.OptionalPartValue(member.PartContentType, display, nameof(PartAttribute.ContentType)));
    }

    /// <summary>同名部件冲突的错误：说明同名成员要合并成一个。</summary>
    private static InvalidOperationException DuplicatePart(BindingWalkContext context, string name)
        => new($"{context.Owner} 有多个 Part 绑定使用了同名 \"{name}\"：同名参数请合并为一个{context.MemberKind}（集合值走 AxiosNet 的数组规则）。");
}
