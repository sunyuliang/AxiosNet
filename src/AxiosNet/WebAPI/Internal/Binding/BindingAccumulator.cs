using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using AxiosNet.Internal.Headers;
using AxiosNet.Internal.Spec;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>声明构建器：类模型的属性集与接口模型的参数集都通过它累积绑定。</summary>
internal sealed class BindingAccumulator
{
    private readonly string _owner;
    private readonly string _memberKind;

    private readonly List<(string Token, IValueSource Source)> _paths = [];
    private readonly List<(string Name, IValueSource Source)> _queries = [];
    private readonly List<(string Name, IValueSource Source)> _queryMaps = [];
    private readonly List<(string Name, IValueSource Source)> _headers = [];
    private readonly List<IValueSource> _headerCollections = [];
    // 未标注成员：默认通道由 Build 按 HTTP 方法决定；Order 即声明顺序。
    private readonly List<(int Order, IValueSource Source, string FieldName, bool NameFromAttribute)> _unannotated = [];
    private readonly List<(string Name, string? Value)> _staticHeaders = [];
    // 显式 [Body("名")] 与 body 方法上改道的未标注成员：作为请求体对象的键，可多个；键名重复在构建期报错。
    private readonly List<(int Order, string Name, bool NameFromAttribute, IValueSource Source)> _bodyMembers = [];
    // 无参 [Body]：整份正文，原样发送；与其它正文来源互斥，构建期报错。
    private (int Order, IValueSource Source)? _rootBody;
    // 正文来源的声明序号：三类来源共用一个自增计数，合并后据此还原声明顺序。
    private int _bodyOrder;

    private readonly HashSet<string> _pathNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _queryNames = new(StringComparer.Ordinal);
    private readonly HashSet<string> _headerNames = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="owner">定位前缀，如 <c>请求模型 UserRequest</c> / <c>接口 IUserApi.GetAsync</c>。</param>
    /// <param name="memberKind">成员称谓：类模型为「属性」，接口模型为「参数」。</param>
    internal BindingAccumulator(string owner, string memberKind)
    {
        _owner = owner;
        _memberKind = memberKind;
    }

    /// <summary>是否已绑定请求正文（无参 [Body] 或至少一个正文键）。</summary>
    public bool HasBody => _rootBody is not null || _bodyMembers.Count > 0;

    /// <summary>正文的定位名（错误消息用）。</summary>
    public string? BodyName => _rootBody is { } root
        ? root.Source.Name
        : (_bodyMembers.Count > 0 ? _bodyMembers[0].Source.Name : null);

    /// <summary>路径绑定：占位符名称大小写不敏感去重，值必须是标量（<see cref="Build"/> 时校验）。</summary>
    public void AddPath(IValueSource source, string explicitName)
    {
        EnsureFormat(source, "Path");

        var name = ResolveName(explicitName, source.Name);
        if (!_pathNames.Add(name))
        {
            throw DuplicateBinding(name, "Path");
        }

        _paths.Add((name, source));
    }

    /// <summary>请求头绑定：头名大小写不敏感去重，值必须是标量（<see cref="Build"/> 时校验）。</summary>
    public void AddHeader(IValueSource source, string explicitName)
    {
        EnsureFormat(source, "Header");

        var name = ResolveName(explicitName, source.Name);
        if (!_headerNames.Add(name))
        {
            throw DuplicateBinding(name, "Header");
        }

        _headers.Add((name, source));
    }

    /// <summary>登记一个「头集合」来源：一次写入多个头，键冲突留给构建期校验。</summary>
    public void AddHeaderCollection(IValueSource source)
    {
        if (source.Format is not null)
        {
            throw new InvalidOperationException(
                $"{_owner} 的{_memberKind} {source.Name} 同时标注了 [Format] 与 [HeaderCollection]："
                + "[HeaderCollection] 的值必须是字符串字典，逐项格式化没有意义。");
        }

        if (_headers.Any(h => ReferenceEquals(h.Source, source)))
        {
            throw new InvalidOperationException(
                $"{_owner} 的{_memberKind} {source.Name} 同时标注了 [Header] 与 [HeaderCollection]：单值与逐项是二义性。");
        }

        if (!QueryMap.IsStringMapType(source.ValueType))
        {
            throw new InvalidOperationException(
                $"{_owner} 的{_memberKind} {source.Name} 标注了 [HeaderCollection]，但类型 {source.ValueType.Name} "
                + "不是字符串键值字典：请用 IDictionary<string, string>（Dictionary / SortedDictionary 等实现均可）。");
        }

        _headerCollections.Add(source);
    }

    /// <summary><see cref="BodyAttribute"/>：无参（<paramref name="explicitName"/> 为 null）登记整份正文，否则登记为聚合正文的一个键；键名重复在 <see cref="Build"/> 报错。</summary>
    public void AddBody(IValueSource source, string fieldName, string? explicitName, bool nameFromAttribute)
    {
        if (source.Format is not null)
        {
            if (!ScalarTypes.IsScalarBodyType(source.ValueType))
            {
                throw new InvalidOperationException(
                    $"{_owner} 的{_memberKind} {source.Name} 同时标注了 [Format] 与 [Body]，但类型 {source.ValueType.Name} 是结构化对象："
                    + "结构化正文内部的格式化归序列化器（JsonConverter / XmlElement 等），库不进入文档内部改写。"
                    + "要让 JSON 文档内部也按 [Format] 输出，请额外用 AxiosJsonFormat.ApplyTo 包装 JsonSerializerOptions。");
            }

            EnsureFormat(source, "Body");
        }

        if (explicitName is null)
        {
            if (_rootBody is { } existing)
            {
                throw new InvalidOperationException(
                    $"{_owner} 的{_memberKind} {existing.Source.Name} 与 {source.Name} 都用无参 [Body] 声明了整份正文：只能有一个。"
                    + "要合并进同一层，请改用 [Body(\"名\")] 把其中一个作为键。");
            }

            _rootBody = (_bodyOrder++, source);
            return;
        }

        _bodyMembers.Add((_bodyOrder++, ResolveName(explicitName, string.IsNullOrWhiteSpace(fieldName) ? source.Name : fieldName), nameFromAttribute, source));
    }

    /// <summary>未标注任何参数特性的成员：先按 Query 登记；Build 时 body 方法会整体改道成聚合正文（与无参 [Body] 共存即构建期报错）。</summary>
    public void AddUnannotated(IValueSource source, string fieldName, bool nameFromAttribute)
    {
        EnsureFormat(source, "Query");
        _unannotated.Add((_bodyOrder++, source, string.IsNullOrWhiteSpace(fieldName) ? source.Name : fieldName, nameFromAttribute));

        if (QueryMap.IsMapType(source.ValueType))
        {
            // 平铺映射只产出字典里的键，不占用具名查询绑定的名字。
            _queryMaps.Add((source.Name, source));
            return;
        }

        // 同名冲突留到 Build 再判：这些成员可能整体改道去聚合正文，提前报错会误伤合法声明。
        _queries.Add((ResolveName(string.Empty, source.Name), source));
    }

    /// <summary>显式 <see cref="QueryAttribute"/> 绑定：字典 + 未显式命名 → 平铺，显式命名保留嵌套（?name[key]=v）。</summary>
    public void AddQuery(IValueSource source, string explicitName)
    {
        EnsureFormat(source, "Query");

        if (string.IsNullOrWhiteSpace(explicitName) && QueryMap.IsMapType(source.ValueType))
        {
            _queryMaps.Add((source.Name, source));
            return;
        }

        var name = ResolveName(explicitName, source.Name);
        if (!_queryNames.Add(name))
        {
            throw DuplicateBinding(name, "Query");
        }

        _queries.Add((name, source));
    }

    /// <summary>按声明顺序解析静态头：无冒号 = 摘除同名头，有冒号 = 固定值。</summary>
    public void AddStaticHeaders(IEnumerable<string> declarations)
    {
        foreach (var declaration in declarations)
        {
            if (declaration is null)
            {
                continue;
            }

            var separator = declaration.IndexOf(':');
            if (separator < 0)
            {
                var removalName = declaration.Trim();
                if (removalName.Length == 0)
                {
                    throw new InvalidOperationException($"{_owner} 的 [Headers] 含空白声明：无法作为「删除头」的名字。");
                }

                _staticHeaders.Add((removalName, null));
                continue;
            }

            var name = declaration[..separator].Trim();
            if (name.Length == 0)
            {
                throw new InvalidOperationException($"{_owner} 的 [Headers] 声明 \"{declaration}\" 缺少头名。");
            }

            _staticHeaders.Add((name, declaration[(separator + 1)..].Trim()));
        }
    }

    /// <summary>完成校验并生成计划：正文来源互斥 → 正文归并 → 平铺映射去重 → 路径模板核对 → 标量护栏。</summary>
    public BindingPlan Build(HttpMethod method, string pathTemplate, bool allowDefaultBody, string? contentType)
    {
        ValidateBodySources(method, allowDefaultBody);

        // 显式 [Body("名")] 在任何方法上都进正文；body 方法上的未标注成员按方法并入正文，默认通道只由 HTTP 方法决定。
        var entries = new List<BodyEntry>(_bodyMembers.Count + _unannotated.Count + (_rootBody is null ? 0 : 1));

        foreach (var member in _bodyMembers)
        {
            entries.Add(new BodyEntry(member.Order, member.Name, member.NameFromAttribute, member.Source, isRootBody: false));
        }

        if (allowDefaultBody && _unannotated.Count > 0 && IsBodyMethod(method))
        {
            foreach (var member in _unannotated)
            {
                entries.Add(new BodyEntry(member.Order, member.FieldName, member.NameFromAttribute, member.Source, isRootBody: false));
            }

            // 改道去正文的成员不再出现在查询串里。
            _queries.RemoveAll(q => _unannotated.Any(m => ReferenceEquals(m.Source, q.Source)));
            _queryMaps.RemoveAll(m => _unannotated.Any(u => ReferenceEquals(u.Source, m.Source)));
        }

        // 无参 [Body]：整份正文，独占（ValidateBodySources 已保证此时没有其它正文来源）。
        if (_rootBody is { } root)
        {
            entries.Add(new BodyEntry(root.Order, root.Source.Name, nameFromAttribute: false, root.Source, isRootBody: true));
        }

        // 声明顺序即输出顺序：正文键按声明序号排列。
        entries.Sort(static (left, right) => left.Order.CompareTo(right.Order));
        ValidateBodyEntries(entries, guardCaseInsensitive: MediaTypes.IsJson(contentType));

        // 平铺映射不再走具名查询绑定：同一成员只能有一种形态
        if (_queryMaps.Count > 0)
        {
            _queries.RemoveAll(q => _queryMaps.Any(m => ReferenceEquals(m.Source, q.Source)));
        }

        ValidateQueryNames();
        var pathTokens = ValidatePathTemplate(pathTemplate);
        ValidateScalarBindings();

        return new BindingPlan(
            pathTokens,
            [.. _queries],
            [.. _queryMaps],
            [.. _headers],
            [.. _headerCollections],
            [.. _staticHeaders],
            [.. entries]);
    }

    /// <summary>无参 <c>[Body]</c> 是整份正文：不能与其它正文来源共存。</summary>
    private void ValidateBodySources(HttpMethod method, bool allowDefaultBody)
    {
        if (_rootBody is not { } root)
        {
            return;
        }

        if (_bodyMembers.Count > 0)
        {
            throw RootBodyConflict(root.Source.Name, _bodyMembers[0].Source.Name);
        }

        // body 方法上未标注成员会改道进正文；非 body 方法上它们走查询串，不冲突。
        if (allowDefaultBody && IsBodyMethod(method) && _unannotated.Count > 0)
        {
            throw RootBodyConflict(root.Source.Name, _unannotated[0].Source.Name);
        }
    }

    /// <summary>整份正文与另一个正文来源共存的错误：给出换通道与改成键两条出路。</summary>
    private InvalidOperationException RootBodyConflict(string rootName, string otherName)
        => new($"{_owner} 的{_memberKind} {rootName} 用无参 [Body] 声明为整份正文，但{_memberKind} {otherName} 也要进正文：整份正文必须独占。"
            + $"请给 {otherName} 标 [Query] / [Header] 换通道或 [ApiIgnore] 排除，"
            + $"或把 {rootName} 改成 [Body(\"名\")] 作为正文里的一个键。");

    /// <summary>正文合并的构建期校验：同层键名唯一、聚合正文装不下文件部件。</summary>
    private void ValidateBodyEntries(List<BodyEntry> entries, bool guardCaseInsensitive)
    {
        // 整份正文独占时不按键聚合，键名唯一性与文件部件都由序列化器直接处理。
        if (_rootBody is not null)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var caseInsensitiveNames = guardCaseInsensitive ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) : null;
        foreach (var entry in entries)
        {
            if (!names.Add(entry.Name))
            {
                throw new InvalidOperationException(
                    $"{_owner} 有多个{_memberKind}的正文键名都是 \"{entry.Name}\"：聚合到同一层后不能有重复键。"
                    + "请改名，或用无参 [Body] 把其中一个作为整份正文单独发送。");
            }

            if (caseInsensitiveNames is not null && !caseInsensitiveNames.Add(entry.Name))
            {
                throw new InvalidOperationException(
                    $"{_owner} 的正文键名 \"{entry.Name}\" 与另一个键仅大小写不同："
                    + "多数 JSON 反序列化默认大小写不敏感，两个键会落到同一个成员上、后者覆盖前者。请改名。");
            }
        }

        // 按键聚合的正文装不下文件部件：它们没有按键名写入的线上形态。
        foreach (var entry in entries)
        {
            if (IsFileBodyType(entry.Source.ValueType))
            {
                throw new InvalidOperationException(
                    $"{_owner} 的{_memberKind} {entry.Source.Name} 是 {entry.Source.ValueType.Name}："
                    + "按键聚合的正文装不下文件部件，请改用 [Multipart] 由库组装表单，"
                    + "或用无参 [Body] 把它作为整份正文单独发送。");
            }
        }
    }

    /// <summary>聚合正文承载不了的正文形态：流 / 文件部件 / 预构造内容（序列化器写不出它们）。</summary>
    private static bool IsFileBodyType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return typeof(Stream).IsAssignableFrom(type)
            || type == typeof(FormFile)
            || typeof(HttpContent).IsAssignableFrom(type);
    }

    /// <summary>延迟的查询重名校验：未标注成员可能与显式 [Query] 撞名，改道结论确定后才能判。</summary>
    private void ValidateQueryNames()
    {
        if (_queries.Count < 2)
        {
            return;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, _) in _queries)
        {
            if (!names.Add(name))
            {
                throw DuplicateBinding(name, "Query");
            }
        }
    }

    /// <summary>路径模板与 [Path] 绑定一一核对：模板占位符必须有绑定、绑定也必须出现在模板里；返回模板顺序的替换表。</summary>
    private (string Token, IValueSource Source)[] ValidatePathTemplate(string template)
    {
        var matches = PathTokenRegex.Matches(template);
        var tokenSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bindingsByName = new Dictionary<string, IValueSource>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches.Cast<Match>())
        {
            var name = match.Groups[1].Value;
            if (!tokenSpellings.TryAdd(name, name))
            {
                throw new InvalidOperationException(
                    $"{_owner} 的路径模板重复使用占位符 {{{name}}}：一个占位符只能出现一次。");
            }
        }

        // 模板里识别不了的花括号先于绑定核对单独指出。
        var leftover = template;
        foreach (var match in matches.Cast<Match>())
        {
            leftover = leftover.Replace(match.Value, string.Empty);
        }

        if (leftover.Contains('{') || leftover.Contains('}'))
        {
            throw new InvalidOperationException(
                $"{_owner} 的路径模板 \"{template}\" 含无法识别的花括号："
                + "占位符只支持 {{name}} 形式的标识符（字母/下划线开头，字母/数字/下划线组成）。");
        }

        foreach (var binding in _paths)
        {
            if (!bindingsByName.TryAdd(binding.Token, binding.Source))
            {
                // 上面 _pathNames 已拦同名绑定，这里实际到不了
                throw DuplicateBinding(binding.Token, "Path");
            }
        }

        foreach (var name in tokenSpellings.Keys)
        {
            if (!bindingsByName.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"{_owner} 的路径模板 \"{template}\" 含占位符 {{{name}}}，"
                    + $"但没有{_memberKind}标注 [Path] 绑定它。");
            }
        }

        foreach (var binding in _paths)
        {
            if (!tokenSpellings.ContainsKey(binding.Token))
            {
                throw new InvalidOperationException(
                    $"{_owner} 的{_memberKind} {binding.Source.Name} 标注了 [Path(\"{binding.Token}\")]，"
                    + $"但路径模板 \"{template}\" 中没有 {{{binding.Token}}} 占位符（多余绑定多为改名后的拼写遗漏）。");
            }
        }

        return [.. matches.Cast<Match>().Select(m => (m.Groups[1].Value, bindingsByName[m.Groups[1].Value]))];
    }

    /// <summary>路径占位符与请求头的值必须是标量；查询参数与请求体不受限。</summary>
    private void ValidateScalarBindings()
    {
        foreach (var binding in _paths.Concat(_headers))
        {
            if (!ScalarTypes.IsScalarType(binding.Source.ValueType))
            {
                throw new InvalidOperationException(
                    $"{_owner} 的{_memberKind} {binding.Source.Name} 绑定到了 {nameof(PathAttribute)} / {nameof(HeaderAttribute)}，"
                    + $"但类型 {binding.Source.ValueType.Name} 不是标量：路径段与请求头在线上就是文本，"
                    + "复杂对象只会得到 ToString() 垃圾值。请绑定标量成员（或把复杂对象放进 [Body] / [Query]）。");
            }
        }
    }

    /// <summary>校验部件上的成员级格式化：文件类部件按字节发送，没有文本格式。</summary>
    public void ValidatePartFormat(IValueSource source)
    {
        if (source.Format is null)
        {
            return;
        }

        var type = Nullable.GetUnderlyingType(source.ValueType) ?? source.ValueType;

        // 集合 / 字典部件都可能装配成文件（同名重复部件）：文件形态没有文本格式。
        var fileType = FindFilePartType(type, depth: 0);

        if (fileType is not null)
        {
            throw new InvalidOperationException(
                $"{_owner} 的{_memberKind} {source.Name} 标注了 [Format]，但 {fileType.Name} 是文件部件"
                + "（Stream / byte[] / Memory<byte> / ArraySegment<byte> / FormFile / HttpContent）：文件按字节发送，没有文本格式。");
        }

        EnsureFormat(source, "Part");
    }

    /// <summary>在集合元素 / 字典值里递归找文件部件类型（深度上限防自引用类型图）。</summary>
    private static Type? FindFilePartType(Type type, int depth)
    {
        if (IsFilePartType(type))
        {
            return type;
        }

        if (depth >= 8)
        {
            return null;
        }

        if (FormatRules.TryGetElementType(type, out var element)
            && FindFilePartType(element, depth + 1) is { } fromElement)
        {
            return fromElement;
        }

        if (TryGetStringMapValueType(type, out var valueType)
            && valueType is not null
            && FindFilePartType(valueType, depth + 1) is { } fromValue)
        {
            return fromValue;
        }

        return null;
    }

    /// <summary>字符串键字典的值类型（<c>IDictionary&lt;string,T&gt;</c> / <c>IReadOnlyDictionary&lt;string,T&gt;</c>）。</summary>
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage(
        "Trimming",
        "IL2070",
        Justification = "与 FormatRules.TryGetElementType 同口径：只依赖已保留的接口元数据，判不出来就放行。")]
    private static bool TryGetStringMapValueType(Type type, out Type? valueType)
    {
        foreach (var contract in type.GetInterfaces())
        {
            if (!contract.IsGenericType)
            {
                continue;
            }

            var definition = contract.GetGenericTypeDefinition();
            if ((definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>))
                && contract.GetGenericArguments()[0] == typeof(string))
            {
                valueType = contract.GetGenericArguments()[1];
                return true;
            }
        }

        valueType = null;
        return false;
    }

    /// <summary>是否为文件型部件（<c>byte[]</c> / <c>Memory&lt;byte&gt;</c> / 流 / <see cref="FormFile"/> 等；可空类型先剥掉外层）。</summary>
    private static bool IsFilePartType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return type == typeof(byte[])
            || type == typeof(Memory<byte>)
            || type == typeof(ReadOnlyMemory<byte>)
            || type == typeof(ArraySegment<byte>)
            || typeof(Stream).IsAssignableFrom(type)
            || type == typeof(FormFile)
            || typeof(HttpContent).IsAssignableFrom(type);
    }

    /// <summary>成员级格式化与通道的兼容性校验（<see cref="FormatRules"/> 是唯一权威）。</summary>
    private void EnsureFormat(IValueSource source, string target)
        => FormatRules.EnsureApplicable(source.Format, source.ValueType, _owner, source.Name, target);

    /// <summary>同名绑定冲突的错误：说明同名参数要合并成同一个成员。</summary>
    private InvalidOperationException DuplicateBinding(string name, string kind)
        => new($"{_owner} 有多个 {kind} 绑定使用了同名 \"{name}\"：同名参数请合并为一个{_memberKind}（集合值走 AxiosNet 的数组规则）。");

    /// <summary>约定上携带请求体的方法（默认 Body 只在这些方法上生效；其余方法未标注成员一律 Query）。</summary>
    private static bool IsBodyMethod(HttpMethod method)
        => method == HttpMethod.Post || method == HttpMethod.Put || method == PlatformCompat.PatchMethod;

    /// <summary>特性上没写名字就用成员名；显式给了空白串同样回退（与「省略参数」语义一致）。</summary>
    private static string ResolveName(string explicitName, string fallback)
        => string.IsNullOrWhiteSpace(explicitName) ? fallback : explicitName.Trim();

    /// <summary>路径占位符：字母/下划线开头，字母/数字/下划线组成。</summary>
    private static readonly Regex PathTokenRegex = new(PathGrammar.Placeholder, RegexOptions.CultureInvariant);
}
