using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using AxiosNet.Internal;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.WebAPI.Internal;

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
    private readonly List<IValueSource> _unannotated = [];
    private readonly List<(string Name, string? Value)> _staticHeaders = [];
    // 正文来源：构建期只允许一个，重复声明直接报错。
    private IValueSource? _body;

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

    /// <summary>是否已绑定请求正文（正文只能有一个）。</summary>
    public bool HasBody => _body is not null;

    /// <summary>正文的定位名（错误消息用）。</summary>
    public string? BodyName => _body?.Name;

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

    /// <summary>请求体唯一：重复的 <see cref="BodyAttribute"/> 绑定在构建期报错。</summary>
    public void AddBody(IValueSource source)
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

        if (_body is not null)
        {
            throw new InvalidOperationException(
                $"{_owner} 有多个 [Body] {_memberKind}（{_body.Name} 与 {source.Name}）：一个请求最多一个请求体。");
        }

        _body = source;
    }

    /// <summary>未标注任何参数特性的成员：默认 Query；字符串键字典未命名时平铺。</summary>
    public void AddUnannotated(IValueSource source)
    {
        EnsureFormat(source, "Query");
        _unannotated.Add(source);

        if (QueryMap.IsMapType(source.ValueType))
        {
            // 平铺映射只产出字典里的键，不占用具名查询绑定的名字。
            _queryMaps.Add((source.Name, source));
            return;
        }

        var name = ResolveName(string.Empty, source.Name);
        if (!_queryNames.Add(name))
        {
            throw DuplicateBinding(name, "Query");
        }

        _queries.Add((name, source));
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

    /// <summary>完成校验并生成计划：默认 Body 推断 → 平铺映射去重 → 路径模板核对 → 标量护栏。</summary>
    public BindingPlan Build(HttpMethod method, string pathTemplate, bool allowDefaultBody)
    {
        // 默认 Body：body 方法 + 恰好一个未标注成员 + 非标量 / 非平铺字典 → 该成员即请求体。
        if (_body is null
            && allowDefaultBody
            && _unannotated.Count == 1
            && IsBodyMethod(method)
            && !ScalarTypes.IsScalarType(_unannotated[0].ValueType)
            && !QueryMap.IsMapType(_unannotated[0].ValueType))
        {
            _body = _unannotated[0];
            _queries.RemoveAll(q => ReferenceEquals(q.Source, _body));
            _queryMaps.RemoveAll(q => ReferenceEquals(q.Source, _body));
        }

        // 平铺映射不再走具名查询绑定：同一成员只能有一种形态
        if (_queryMaps.Count > 0)
        {
            _queries.RemoveAll(q => _queryMaps.Any(m => ReferenceEquals(m.Source, q.Source)));
        }

        var pathTokens = ValidatePathTemplate(pathTemplate);
        ValidateScalarBindings();

        return new BindingPlan(
            pathTokens,
            [.. _queries],
            [.. _queryMaps],
            [.. _headers],
            [.. _headerCollections],
            [.. _staticHeaders],
            _body);
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

        // 集合 / 字典部件都可能逐元素、逐值装配成文件（同名重复部件）：文件形态没有文本格式，
        // 否则 [Format] 在运行期会被文件分支静默跳过。
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
    private static readonly Regex PathTokenRegex = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.CultureInvariant);
}
