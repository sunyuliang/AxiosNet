using System.Collections.Concurrent;
using System.Reflection;
using System.Text.RegularExpressions;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 一个路径占位符 / 查询参数 / 请求头绑定的统一描述。
/// </summary>
internal sealed record PropertyBinding(string Name, PropertyInfo Property);

/// <summary>
/// 请求模型的绑定元数据（按模型类型缓存一份，构造期完成全部校验）。
/// <para>
/// 校验失败同样进入缓存：每次调用都抛同一份错误 —— 模型定义错误是编程错误，
/// 报一次和报 N 次的修复动作相同，不值得为「只报一次」引入 TryGet 双路径。
/// </para>
/// </summary>
internal sealed partial class RequestModelMetadata
{
    private static readonly ConcurrentDictionary<Type, RequestModelMetadata> Cache = new();

    private readonly string? _buildError;
    private readonly string _pathTemplate;

    /// <summary>按模板出现顺序排列的路径占位符绑定：Token 是模板中的原文（替换用），Property 提供值。</summary>
    private readonly (string Token, PropertyInfo Property)[] _pathTokens;
    private readonly PropertyBinding[] _queryBindings;

    /// <summary>平铺的字典查询参数（属性值是字符串键字典：<c>a=1&amp;b=2</c> 而不是 <c>name[a]=1</c>）。</summary>
    private readonly PropertyBinding[] _queryMaps;
    private readonly PropertyBinding[] _headerBindings;
    private readonly PropertyInfo[] _headerCollections;

    /// <summary>类级静态头：Value 为 null 表示删除该头。</summary>
    private readonly (string Name, string? Value)[] _staticHeaders;
    private readonly PropertyInfo? _bodyProperty;

    private RequestModelMetadata(
        HttpMethod method,
        string pathTemplate,
        (string Token, PropertyInfo Property)[] pathTokens,
        PropertyBinding[] queryBindings,
        PropertyBinding[] queryMaps,
        PropertyBinding[] headerBindings,
        PropertyInfo[] headerCollections,
        (string Name, string? Value)[] staticHeaders,
        PropertyInfo? bodyProperty)
    {
        Method = method;
        _pathTemplate = pathTemplate;
        _pathTokens = pathTokens;
        _queryBindings = queryBindings;
        _queryMaps = queryMaps;
        _headerBindings = headerBindings;
        _headerCollections = headerCollections;
        _staticHeaders = staticHeaders;
        _bodyProperty = bodyProperty;
    }

    private RequestModelMetadata(string buildError)
    {
        _buildError = buildError;
        _pathTemplate = string.Empty;
        _pathTokens = [];
        _queryBindings = [];
        _queryMaps = [];
        _headerBindings = [];
        _headerCollections = [];
        _staticHeaders = [];
    }

    /// <summary>本次请求的 HTTP 方法（来自路由特性；仅构建错误的缓存实例上为占位值，<see cref="Get"/> 会先抛错）。</summary>
    public HttpMethod Method { get; } = null!;

    /// <summary>授权策略（最近一层声明优先；都没标时为 <see cref="ApiAuthorizationMode.Inherit"/>）。</summary>
    internal ApiAuthorization Authorization { get; init; } = ApiAuthorization.Inherit;

    /// <summary>取（必要时构建并缓存）请求模型的绑定元数据；模型定义非法时抛 <see cref="InvalidOperationException"/>。</summary>
    public static RequestModelMetadata Get(Type requestType)
    {
        var metadata = Cache.GetOrAdd(requestType, static t => Build(t));

        if (metadata._buildError is not null)
        {
            throw new InvalidOperationException(metadata._buildError);
        }

        return metadata;
    }

    /// <summary>把路径模板里的 <c>{name}</c> 替换成属性值（URL 转义），返回最终请求 URL。</summary>
    /// <param name="request">请求模型实例。</param>
    /// <param name="formatter">URL 参数格式化钩子（返回 null 表示用默认规则），可为 null。</param>
    public string ResolveUrl(object request, Func<object, string?>? formatter)
    {
        if (_pathTokens.Length == 0)
        {
            return _pathTemplate;
        }

        var url = _pathTemplate;

        foreach (var (token, property) in _pathTokens)
        {
            var value = property.GetValue(request)
                ?? throw new InvalidOperationException(
                    $"路径参数 {property.DeclaringType!.Name}.{property.Name}（占位符 {{{token}}}）为 null：路径缺段无法成行。");

            url = url.Replace("{" + token + "}", Uri.EscapeDataString(ToText(FormatValue(value, formatter)!)));
        }

        return url;
    }

    /// <summary>
    /// 汇总全部查询参数（显式 <see cref="QueryAttribute"/> + 未标注属性的默认绑定 + 平铺的字典映射）；
    /// 没有任何查询参数时返回 <c>null</c>（省掉空字典）。
    /// <para>
    /// 「有绑定但值全为 null」同样返回 <c>null</c>：此时与「没有任何绑定」一样没有参数要发，
    /// 两种模型形态必须行为一致。合并层对非 null <c>Params</c> 做整体替换，
    /// 返回空字典会静默清掉实例默认参数（如 DI 场景配在 defaults 上的 api_key）。
    /// </para>
    /// </summary>
    /// <param name="request">请求模型实例。</param>
    /// <param name="formatter">URL 参数格式化钩子（返回 null 表示用默认规则），可为 null。</param>
    public Dictionary<string, object?>? ResolveQuery(object request, Func<object, string?>? formatter)
    {
        if (_queryBindings.Length == 0 && _queryMaps.Length == 0)
        {
            return null;
        }

        var query = new Dictionary<string, object?>();

        foreach (var binding in _queryBindings)
        {
            var value = FormatValue(binding.Property.GetValue(request), formatter);

            // 与 axios params 一致：null 参数跳过
            if (value is not null)
            {
                query.Add(binding.Name, value);
            }
        }

        // 字典映射平铺：值仍保持原始对象，集合 / 嵌套对象继续走 UrlBuilder 的既有规则
        foreach (var map in _queryMaps)
        {
            if (map.Property.GetValue(request) is not { } dictionary)
            {
                continue;
            }

            foreach (var entry in QueryMap.Enumerate(dictionary))
            {
                var value = FormatValue(entry.Value, formatter);
                if (value is not null)
                {
                    // 与具名参数同名时后者覆盖：映射来自显式数据，优先于属性
                    query[entry.Key] = value;
                }
            }
        }

        return query.Count > 0 ? query : null;
    }

    /// <summary>请求体的原始值；没有 <see cref="BodyAttribute"/> 属性或值为 <c>null</c> 时返回 <c>null</c>（不发请求体）。</summary>
    public object? ResolveBody(object request) => _bodyProperty?.GetValue(request);

    /// <summary>把全部 <see cref="HeaderAttribute"/> 属性写进配置；没有任何头绑定时不动配置（不建集合）。</summary>
    /// <remarks>
    /// 头名 / 头值的校验沿用 <see cref="AxiosHeaders.Set(string, string?)"/> 的公开契约（抛 <see cref="ArgumentException"/>），
    /// 不在模型层改写异常类型：调用方无论是直接写头还是经模型绑定写头，看到的是同一类错误。
    /// 校验发生在配置构建期，请求一个字节都不会发出。
    /// </remarks>
    public void ApplyHeaders(object request, AxiosConfig config)
    {
        foreach (var binding in _headerBindings)
        {
            var value = binding.Property.GetValue(request);

            // 与 query 一致：null 头跳过
            if (value is not null)
            {
                config.Headers.Set(binding.Name, ToText(value));
            }
        }

        foreach (var property in _headerCollections)
        {
            if (property.GetValue(request) is not { } dictionary)
            {
                continue;
            }

            foreach (var entry in QueryMap.Enumerate(dictionary))
            {
                // 值为 null 的项跳过（字典头没有「删除」语义，删除请用类级 [Headers("X-Remove")]）
                if (entry.Value is string text)
                {
                    config.Headers.Set(entry.Key, text);
                }
            }
        }
    }

    /// <summary>应用类级 <see cref="HeadersAttribute"/> 声明：值写入头，只有名字的声明摘除同名头。</summary>
    public void ApplyStaticHeaders(AxiosConfig config)
    {
        foreach (var (name, value) in _staticHeaders)
        {
            if (value is null)
            {
                config.RemoveHeaders.Add(name);
                continue;
            }

            config.Headers.Set(name, value);
        }
    }

    // ------------------------------------------------------------------
    // 构建：反射只发生在这里，之后全部是数组遍历
    // ------------------------------------------------------------------

    private static RequestModelMetadata Build(Type requestType)
    {
        try
        {
            return BuildCore(requestType);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            // GetCustomAttribute 遇到特性构造函数抛错会原样透出 ArgumentException / FormatException
            // （如 [HttpApi("GE(T", ...)] 的 HttpMethod 构造）；连同校验错误一起缓存，
            // 让 Get() 每次抛同一份消息，不把裸 FormatException 漏出统一错误模型
            return new RequestModelMetadata(ex.Message);
        }
    }

    private static RequestModelMetadata BuildCore(Type requestType)
    {
        var route = requestType.GetCustomAttribute<HttpApiAttribute>(inherit: false)
            ?? throw new InvalidOperationException(
                $"请求模型 {requestType.Name} 缺少路由特性：请在类上标注 [Get(\"...\")] / [Post(\"...\")] 等 HttpApiAttribute 子类。");

        var paths = new List<PropertyBinding>();
        var queries = new List<PropertyBinding>();
        var headers = new List<PropertyBinding>();
        var headerCollections = new List<PropertyInfo>();
        var unannotated = new List<PropertyInfo>();

        // 平铺的字典映射：属性是字符串键字典且没有显式命名时，键直接进查询串（Refit 的动态查询）。
        // 显式命名（[Query("filters")]）保留既有嵌套语义 ?filters[k]=v，作为逃生口。
        var mapProperties = new List<PropertyInfo>();
        var staticHeaders = new List<(string Name, string? Value)>();
        PropertyInfo? body = null;
        // Path 占位符是本库自己的模板语言：与模板 token 的大小写不敏感匹配（属性名回退如 Id ↔ {id}）；
        // Header 名按 RFC 3986 语义大小写不敏感，同名不同写法同样算重复。
        // Query 键名发到服务端是区分大小写的，保持 Ordinal。
        var seenPathNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenQueryNames = new HashSet<string>(StringComparer.Ordinal);
        var seenHeaderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ParseStaticHeaders(requestType, staticHeaders);

        foreach (var property in EnumerateBindableProperties(requestType))
        {
            var path = property.GetCustomAttribute<PathAttribute>();
            var query = property.GetCustomAttribute<QueryAttribute>();
            var header = property.GetCustomAttribute<HeaderAttribute>();
            var isHeaderCollection = property.GetCustomAttribute<HeaderCollectionAttribute>() is not null;
            var isBody = property.GetCustomAttribute<BodyAttribute>() is not null;
            var ignore = property.GetCustomAttribute<ApiIgnoreAttribute>() is not null;

            var kinds = (path is not null ? 1 : 0) + (query is not null ? 1 : 0)
                + (header is not null ? 1 : 0) + (isHeaderCollection ? 1 : 0) + (isBody ? 1 : 0);

            if (ignore)
            {
                // [ApiIgnore] 加在 override 属性上时，基类的参数特性会被 GetCustomAttribute 继承进来
                // （这些特性都是 Inherited = true）—— 那正是调用方想「解绑」的绑定，不能算二义性。
                // 只有本属性<b>自己</b>声明的参数特性才算真冲突。
                var ownKinds = CountOwnParameterAttributes(property);
                if (ownKinds > 0)
                {
                    throw new InvalidOperationException(
                        $"属性 {requestType.Name}.{property.Name} 同时标注了 [ApiIgnore] 与参数特性（Path / Query / Header / HeaderCollection / Body）："
                        + "[ApiIgnore] 的语义是「完全不绑定」，两者同用是二义性。");
                }

                // 完全排除：不进任何绑定，也不算「未标注」（不阻断默认 Body 判定）
                continue;
            }

            // 多特性并存：同一属性可同时绑定多处（路径 + 头 + 查询 + 请求体），
            // 按各处的 null 规则分别应用。注意每个目标各读一次属性（getter 有副作用时
            // 各处拿到的值可能不一致）—— 绑定是构建期的，请求期按目标分发
            if (path is not null)
            {
                var name = ResolveBindingName(path.Name, property, "Path");
                if (!seenPathNames.Add(name))
                {
                    throw DuplicateBinding(requestType, name, "Path");
                }

                paths.Add(new PropertyBinding(name, property));
            }

            if (header is not null)
            {
                var name = ResolveBindingName(header.Name, property, "Header");
                if (!seenHeaderNames.Add(name))
                {
                    throw DuplicateBinding(requestType, name, "Header");
                }

                headers.Add(new PropertyBinding(name, property));
            }

            if (isHeaderCollection)
            {
                if (header is not null)
                {
                    throw new InvalidOperationException(
                        $"属性 {requestType.Name}.{property.Name} 同时标注了 [Header] 与 [HeaderCollection]：单值与逐项是二义性。");
                }

                if (!QueryMap.IsStringMapType(property.PropertyType))
                {
                    throw new InvalidOperationException(
                        $"属性 {requestType.Name}.{property.Name} 标注了 [HeaderCollection]，但类型 {property.PropertyType.Name} "
                        + "不是字符串键值字典：请用 IDictionary<string, string>（Dictionary / SortedDictionary 等实现均可）。");
                }

                headerCollections.Add(property);
            }

            if (isBody)
            {
                if (body is not null)
                {
                    throw new InvalidOperationException(
                        $"请求模型 {requestType.Name} 有多个 [Body] 属性（{body.Name} 与 {property.Name}）：一个请求最多一个请求体。");
                }

                body = property;
            }

            if (kinds == 0)
            {
                // 无标注 → 默认 Query（ kinds == 0 时没有任何参数特性，名字只能回退属性名）
                var name = ResolveBindingName(string.Empty, property, "Query");
                if (!seenQueryNames.Add(name))
                {
                    throw DuplicateBinding(requestType, name, "Query");
                }

                queries.Add(new PropertyBinding(name, property));

                // 「未标注」与「显式 [Query]」要区分：默认 Body 只认前者
                unannotated.Add(property);

                if (QueryMap.IsMapType(property.PropertyType))
                {
                    mapProperties.Add(property);
                }
            }
            else if (query is not null)
            {
                // 已有其他绑定的属性再显式 [Query]：追加一处查询绑定
                var name = ResolveBindingName(query.Name, property, "Query");
                if (!seenQueryNames.Add(name))
                {
                    throw DuplicateBinding(requestType, name, "Query");
                }

                queries.Add(new PropertyBinding(name, property));

                // 字典 + 未显式命名 → 平铺；显式命名保留嵌套（?name[key]=v）
                if (string.IsNullOrWhiteSpace(query.Name) && QueryMap.IsMapType(property.PropertyType))
                {
                    mapProperties.Add(property);
                }
            }
        }

        // 默认 Body：body 方法 + 恰好一个未标注属性 + 非标量类型 → 该属性即请求体。
        // 标量护栏防止「POST 只想带一个查询参数」被静默变成 JSON 请求体；
        // 多个未标注属性时维持 Query（歧义时宁可保持现状，[Body] 永远可以显式表态）。
        if (body is null
            && unannotated.Count == 1
            && IsBodyMethod(route.Method)
            && !IsScalarType(unannotated[0].PropertyType))
        {
            body = unannotated[0];
            queries.RemoveAll(q => ReferenceEquals(q.Property, body));
            mapProperties.Remove(body);
        }

        // 平铺映射不再走具名查询绑定：同一属性只能有一种形态
        if (mapProperties.Count > 0)
        {
            queries.RemoveAll(q => mapProperties.Contains(q.Property));
        }

        var pathTokens = ValidatePathTemplate(requestType, route.Path, paths);
        ValidateScalarBindings(requestType, paths, headers);

        return new RequestModelMetadata(
            route.Method,
            route.Path,
            pathTokens,
            [.. queries],
            [.. mapProperties.Select(p => new PropertyBinding(p.Name, p))],
            [.. headers],
            [.. headerCollections],
            [.. staticHeaders],
            body)
        {
            Authorization = BuildAuthorization(requestType)
        };
    }

    /// <summary>
    /// 取最近一层的授权声明（派生类优先于基类）；两个特性同标一层属二义性。
    /// </summary>
    private static ApiAuthorization BuildAuthorization(Type requestType)
    {
        for (var type = requestType; type is not null && type != typeof(object); type = type.BaseType)
        {
            var anonymous = type.GetCustomAttribute<AllowAnonymousAttribute>(inherit: false) is not null;
            var authorize = type.GetCustomAttribute<AuthorizeAttribute>(inherit: false);

            if (anonymous && authorize is not null)
            {
                throw new InvalidOperationException(
                    $"请求模型 {type.Name} 同时标注了 [Authorize] 与 [AllowAnonymous]：授权与匿名互斥。");
            }

            if (anonymous)
            {
                return new ApiAuthorization(ApiAuthorizationMode.Anonymous, "Bearer");
            }

            if (authorize is not null)
            {
                return new ApiAuthorization(ApiAuthorizationMode.Required, authorize.Scheme);
            }
        }

        return ApiAuthorization.Inherit;
    }

    /// <summary>
    /// 解析类级静态头声明（基类在前、派生类在后，同名后者覆盖）。
    /// <para>无冒号 = 摘除同名头；有冒号 = 固定值（冒号后允许为空 = 空值头）。</para>
    /// </summary>
    private static void ParseStaticHeaders(Type requestType, List<(string Name, string? Value)> target)
    {
        var chain = new List<Type>();
        for (var type = requestType; type is not null && type != typeof(object); type = type.BaseType)
        {
            chain.Add(type);
        }

        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var attribute = chain[i].GetCustomAttribute<HeadersAttribute>(inherit: false);
            if (attribute is null)
            {
                continue;
            }

            foreach (var declaration in attribute.Headers)
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
                        throw new InvalidOperationException(
                            $"请求模型 {requestType.Name} 的 [Headers] 含空白声明：无法作为「删除头」的名字。");
                    }

                    target.Add((removalName, null));
                    continue;
                }

                var name = declaration[..separator].Trim();
                if (name.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"请求模型 {requestType.Name} 的 [Headers] 声明 \"{declaration}\" 缺少头名。");
                }

                target.Add((name, declaration[(separator + 1)..].Trim()));
            }
        }
    }

    /// <summary>公共可读实例属性（跳过索引器），按声明顺序遍历。</summary>
    private static IEnumerable<PropertyInfo> EnumerateBindableProperties(Type requestType)
        => requestType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

    /// <summary>特性上没写名字就用属性名；显式给了空白串同样回退到属性名（与「省略参数」语义一致）。</summary>
    private static string ResolveBindingName(string explicitName, PropertyInfo property, string kind)
        => string.IsNullOrWhiteSpace(explicitName)
            ? property.Name
            : explicitName.Trim();

    /// <summary>本属性自身（不含基类继承）声明了几个参数特性。</summary>
    private static int CountOwnParameterAttributes(PropertyInfo property)
        => (property.GetCustomAttribute<PathAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<QueryAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<HeaderAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<HeaderCollectionAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<BodyAttribute>(inherit: false) is not null ? 1 : 0);

    /// <summary>
    /// 校验路径模板与 <see cref="PathAttribute"/> 绑定一一对应（占位符与绑定的名字大小写不敏感匹配），
    /// 返回按模板出现顺序排列的（占位符原文, 属性）序列，供 <see cref="ResolveUrl"/> 逐个替换。
    /// </summary>
    private static (string Token, PropertyInfo Property)[] ValidatePathTemplate(
        Type requestType,
        string template,
        List<PropertyBinding> pathBindings)
    {
        var matches = PathTokenRegex.Matches(template);
        var tokenSpellings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bindingsByName = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var match in matches.Cast<Match>())
        {
            var name = match.Groups[1].Value;
            if (!tokenSpellings.TryAdd(name, name))
            {
                throw new InvalidOperationException(
                    $"请求模型 {requestType.Name} 的路径模板重复使用占位符 {{{name}}}：一个占位符只能出现一次。");
            }
        }

        // 模板里识别不了的花括号（空占位符、非法字符、未闭合）单独指出，避免用户对着静默的原始模板排查；
        // 放在绑定核对之前：模板本身写坏了时，报「模板非法」比报「绑定对不上」更接近根因
        var leftover = template;
        foreach (var match in matches.Cast<Match>())
        {
            leftover = leftover.Replace(match.Value, string.Empty);
        }

        if (leftover.Contains('{') || leftover.Contains('}'))
        {
            throw new InvalidOperationException(
                $"请求模型 {requestType.Name} 的路径模板 \"{template}\" 含无法识别的花括号："
                + "占位符只支持 {{name}} 形式的标识符（字母/下划线开头，字母/数字/下划线组成）。");
        }

        foreach (var binding in pathBindings)
        {
            if (!bindingsByName.TryAdd(binding.Name, binding.Property))
            {
                // 上面 seenPathNames 已拦同名绑定，这里实际到不了
                throw DuplicateBinding(requestType, binding.Name, "Path");
            }
        }

        foreach (var name in tokenSpellings.Keys)
        {
            if (!bindingsByName.ContainsKey(name))
            {
                throw new InvalidOperationException(
                    $"请求模型 {requestType.Name} 的路径模板 \"{template}\" 含占位符 {{{name}}}，"
                    + "但没有属性标注 [Path] 绑定它。");
            }
        }

        foreach (var binding in pathBindings)
        {
            if (!tokenSpellings.ContainsKey(binding.Name))
            {
                throw new InvalidOperationException(
                    $"请求模型 {requestType.Name} 的属性 {binding.Property.Name} 标注了 [Path(\"{binding.Name}\")]，"
                    + $"但路径模板 \"{template}\" 中没有 {{{binding.Name}}} 占位符（多余绑定多为改名后的拼写遗漏）。");
            }
        }

        return [.. matches.Cast<Match>().Select(m => (m.Groups[1].Value, bindingsByName[m.Groups[1].Value]))];
    }

    private static InvalidOperationException DuplicateBinding(Type requestType, string name, string kind)
        => new($"请求模型 {requestType.Name} 有多个 {kind} 绑定使用了同名 \"{name}\"：同名参数请合并为一个属性（集合值走 AxiosNet 的数组规则）。");

    /// <summary>
    /// 路径占位符与请求头的值必须是标量：两者在线上的形态就是一段文本，
    /// 复杂对象只能得到 ToString() 垃圾值（多特性并存时尤其容易误标），构建期直接拒绝。
    /// 查询参数不受限（复杂对象走 a[b]=v 嵌套规则），请求体不受限（交给序列化器）。
    /// </summary>
    private static void ValidateScalarBindings(Type requestType, List<PropertyBinding> paths, List<PropertyBinding> headers)
    {
        foreach (var binding in paths.Concat(headers))
        {
            if (!IsScalarType(binding.Property.PropertyType))
            {
                throw new InvalidOperationException(
                    $"请求模型 {requestType.Name} 的属性 {binding.Property.Name} 绑定到了 {nameof(PathAttribute)} / {nameof(HeaderAttribute)}，"
                    + $"但类型 {binding.Property.PropertyType.Name} 不是标量：路径段与请求头在线上就是文本，"
                    + "复杂对象只会得到 ToString() 垃圾值。请绑定标量属性（或把复杂对象放进 [Body] / [Query]）。");
            }
        }
    }

    /// <summary>约定上携带请求体的方法（默认 Body 只在这些方法上生效；其余方法未标注属性一律 Query）。</summary>
    private static bool IsBodyMethod(HttpMethod method)
        => method == HttpMethod.Post || method == HttpMethod.Put || method == HttpMethod.Patch;

    /// <summary>
    /// 标量判定（默认 Body 的护栏）：见 <see cref="ScalarTypes.IsScalarType"/>。
    /// 这些值作为「整个请求体」没有合理语义，保持 Query。
    /// </summary>
    private static bool IsScalarType(Type type) => ScalarTypes.IsScalarType(type);

    /// <summary>
    /// 基础类型的线上文本：<b>转调查询串 / 表单的同一份规则</b>（<see cref="ParamsSerializer.ScalarToString"/>）——
    /// 三处各写一份时 <c>bool</c> 会退化成 <c>True</c>（Boolean 不实现 IFormattable）、
    /// <c>DateTime</c> 会变成带空格的短日期，同一个值在 <c>[Query]</c> 与 <c>[Path]</c> 上发出两种形态。
    /// </summary>
    private static string ToText(object value) => ParamsSerializer.ScalarToString(value);

    /// <summary>
    /// URL 参数格式化钩子：<paramref name="formatter"/> 返回 null 时回落到默认规则
    /// （null 值原样保留，由调用方按各处规则跳过）。
    /// </summary>
    private static object? FormatValue(object? value, Func<object, string?>? formatter)
        => value is null || formatter is null ? value : formatter(value) ?? value;

    /// <summary>路径占位符：字母/下划线开头，字母/数字/下划线组成。</summary>
    /// <remarks>GeneratedRegexAttribute 是 .NET 7+ API（net6.0 目标禁用），用编译正则：构建每类型只跑一次。</remarks>
    private static readonly Regex PathTokenRegex = new(@"\{([A-Za-z_][A-Za-z0-9_]*)\}", RegexOptions.Compiled);
}
