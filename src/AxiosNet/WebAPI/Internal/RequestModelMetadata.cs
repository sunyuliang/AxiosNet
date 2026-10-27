using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.Json;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>请求模型的绑定元数据（按模型类型缓存一份，构造期完成全部校验）。校验失败同样进入缓存，每次调用都抛同一份错误。</summary>
internal sealed partial class RequestModelMetadata
{
    private static readonly ConcurrentDictionary<Type, RequestModelMetadata> Cache = new();

    private readonly string? _buildError;
    private readonly string _pathTemplate;
    private readonly BindingPlan _plan;

    private RequestModelMetadata(HttpMethod method, string pathTemplate, BindingPlan plan)
    {
        Method = method;
        _pathTemplate = pathTemplate;
        _plan = plan;
    }

    private RequestModelMetadata(string buildError)
    {
        _buildError = buildError;
        _pathTemplate = string.Empty;
        _plan = BindingPlan.Empty;
    }

    /// <summary>本次请求的 HTTP 方法（来自路由特性；仅构建错误的缓存实例上为占位值，<see cref="Get"/> 会先抛错）。</summary>
    public HttpMethod Method { get; } = null!;

    /// <summary>授权策略（最近一层声明优先；都没标时为 <see cref="ApiAuthorizationMode.Inherit"/>）。</summary>
    internal ApiAuthorization Authorization { get; init; } = ApiAuthorization.Inherit;

    /// <summary>是否声明了 <see cref="MultipartAttribute"/>（请求体 = 模型自身装配的 multipart 表单）。</summary>
    internal bool IsMultipart { get; init; }

    /// <summary>multipart 部件绑定：未标注参数特性的属性默认参与（名字 = 属性名），[Part] 可覆盖。</summary>
    internal PartBinding[] PartBindings { get; init; } = [];

    /// <summary>取（必要时构建并缓存）请求模型的绑定元数据；模型定义非法时抛 <see cref="InvalidOperationException"/>。</summary>
    /// <param name="requestType">请求模型类型；标注要求裁剪器保留其公开属性（绑定与取值都靠反射）。</param>
    public static RequestModelMetadata Get(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type requestType)
    {
        // 用捕获的 requestType（带标注）而不是工厂参数：缓存字典的 Type 键本身不携带裁剪标注。
        var metadata = Cache.GetOrAdd(requestType, _ => Build(requestType));

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
        => _plan.ResolveUrl(_pathTemplate, request, formatter);

    /// <summary>汇总全部查询参数（显式 <see cref="QueryAttribute"/> + 未标注属性的默认绑定 + 平铺的字典映射）；没有参数或值全为 <c>null</c> 时返回 <c>null</c>。
    /// 调用方拿到 <c>null</c> 时不要写进配置：合并层把显式 null 当作摘掉实例默认值。</summary>
    public Dictionary<string, object?>? ResolveQuery(object request, Func<object, string?>? formatter)
        => _plan.ResolveQuery(request, formatter);

    /// <summary>请求体的原始值：普通模型取 <see cref="BodyAttribute"/> 属性；multipart 模型返回装配好的声明式表单（全部属性为 <c>null</c> 时返回 <c>null</c>）。</summary>
    public object? ResolveBody(object request)
    {
        if (!IsMultipart)
        {
            return _plan.ResolveBodyValue(request);
        }

        // 模型自身即表单：null 属性跳过（与 query / headers 的 null 规则一致）
        var form = new DeclaredForm { SourceName = request.GetType().Name };

        try
        {
            foreach (var binding in PartBindings)
            {
                var value = binding.Source.GetValue(request);
                if (value is null)
                {
                    continue;
                }

                // 声明覆盖与值分开登记，覆盖在装配时由 MultipartBodyBuilder 应用。
                var partOverride = new PartOverride(binding.FileName, binding.ContentType, binding.HasExplicitName);
                if (partOverride.Any)
                {
                    form.Overrides[binding.Name] = partOverride;
                    form.Add(binding.Name, MultipartValues.MaterializeDeclaredCollection(value));
                }
                else
                {
                    form.Add(binding.Name, value);
                }
            }
        }
        catch
        {
            // 还没交给 MultipartBodyBuilder：已取得的流 / 内容必须就地释放，否则会随异常泄漏。
            MultipartOwnership.DisposeOwned(form.Values);
            throw;
        }

        // 全部属性为 null：没有可发的部件，按「无请求体」处理（与 [Body] 为 null 的行为一致）
        return form.Count > 0 ? form : null;
    }

    /// <summary>把全部 <see cref="HeaderAttribute"/> 属性写进配置；没有任何头绑定时不动配置。头名 / 头值的校验沿用 <see cref="AxiosHeaders.Set(string, string?)"/> 的公开契约，发生在配置构建期。</summary>
    public void ApplyHeaders(object request, AxiosConfig config) => _plan.ApplyHeaders(request, config);

    /// <summary>应用类级 <see cref="HeadersAttribute"/> 声明：值写入头，只有名字的声明摘除同名头。</summary>
    public void ApplyStaticHeaders(AxiosConfig config) => _plan.ApplyStaticHeaders(config);

    // 构建：反射只发生在这里，之后全部是数组遍历

    private static RequestModelMetadata Build(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type requestType)
    {
        try
        {
            return BuildCore(requestType);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            // 特性构造函数抛出的 ArgumentException / FormatException 连同校验错误一起缓存，统一从 Get() 抛出。
            return new RequestModelMetadata(ex.Message);
        }
    }

    private static RequestModelMetadata BuildCore(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type requestType)
    {
        var route = requestType.GetCustomAttribute<HttpApiAttribute>(inherit: false) ?? throw new InvalidOperationException($"请求模型 {requestType.Name} 缺少路由特性：请在类上标注 [Get(\"...\")] / [Post(\"...\")] 等 HttpApiAttribute 子类。");

        var accumulator = new BindingAccumulator($"请求模型 {requestType.Name}", "属性");

        // multipart 部件绑定：模型声明 [Multipart] 时，模型自身即表单
        var parts = new List<PartBinding>();
        var seenPartNames = new HashSet<string>(StringComparer.Ordinal);

        var isMultipart = requestType.GetCustomAttribute<MultipartAttribute>() is not null;

        accumulator.AddStaticHeaders(EnumerateStaticHeaderAttributes(requestType));

        foreach (var property in EnumerateBindableProperties(requestType))
        {
            var path = property.GetCustomAttribute<PathAttribute>();
            var query = property.GetCustomAttribute<QueryAttribute>();
            var header = property.GetCustomAttribute<HeaderAttribute>();
            var isHeaderCollection = property.GetCustomAttribute<HeaderCollectionAttribute>() is not null;
            var part = property.GetCustomAttribute<PartAttribute>();
            var isBody = property.GetCustomAttribute<BodyAttribute>() is not null;
            var ignore = property.GetCustomAttribute<ApiIgnoreAttribute>() is not null;

            var kinds = (path is not null ? 1 : 0) + (query is not null ? 1 : 0)
                + (header is not null ? 1 : 0) + (isHeaderCollection ? 1 : 0)
                + (part is not null ? 1 : 0) + (isBody ? 1 : 0);

            if (ignore)
            {
                // [ApiIgnore] 在 override 属性上会继承基类的参数特性，只有本属性自己声明的才算冲突。
                var ownKinds = CountOwnParameterAttributes(property);
                if (ownKinds > 0)
                {
                    throw new InvalidOperationException(
                        $"属性 {requestType.Name}.{property.Name} 同时标注了 [ApiIgnore] 与参数特性（Path / Query / Header / HeaderCollection / Part / Body）："
                        + "[ApiIgnore] 的语义是「完全不绑定」，两者同用是二义性。");
                }

                // 完全排除：不进任何绑定，也不算「未标注」（不阻断默认 Body 判定）
                continue;
            }

            // 同一属性可同时绑定多处（路径 + 头 + 查询 + 请求体），按各处的 null 规则分别应用。
            var source = new PropertyValueSource(property);

            if (path is not null)
            {
                accumulator.AddPath(source, path.Name);
            }

            if (header is not null)
            {
                accumulator.AddHeader(source, header.Name);
            }

            if (isHeaderCollection)
            {
                accumulator.AddHeaderCollection(source);
            }

            if (part is not null)
            {
                if (!isMultipart)
                {
                    throw new InvalidOperationException($"属性 {requestType.Name}.{property.Name} 标注了 [Part]，但模型没有 [Multipart]：请在类上声明 [Multipart]。");
                }

                var explicitName = CleanPartValue(part.Name, requestType, property, nameof(PartAttribute.Name));
                // 默认名走 [JsonPropertyName] / 属性名，与 builder 展平嵌套对象时的命名规则一致
                var name = explicitName ?? BodyFields.ResolveFieldName(property);
                if (!seenPartNames.Add(name))
                {
                    throw DuplicateBinding(requestType, name, "Part");
                }

                parts.Add(new PartBinding(name, source, explicitName is not null, CleanPartValue(part.FileName, requestType, property, nameof(PartAttribute.FileName)), CleanPartValue(part.ContentType, requestType, property, nameof(PartAttribute.ContentType))));
            }

            if (isBody)
            {
                accumulator.AddBody(source);
            }

            if (kinds == 0)
            {
                if (isMultipart)
                {
                    // multipart 模型：无标注属性默认按部件绑定（名字 = [JsonPropertyName] 或属性名），不参与默认 Body / Query 判定
                    var partName = BodyFields.ResolveFieldName(property);
                    if (!seenPartNames.Add(partName))
                    {
                        throw DuplicateBinding(requestType, partName, "Part");
                    }

                    parts.Add(new PartBinding(partName, source, false, null, null));
                    continue;
                }

                // 无标注 → 默认 Query（名字回退属性名）
                accumulator.AddUnannotated(source);
            }
            else if (query is not null)
            {
                // 已有其他绑定的属性再显式 [Query]：追加一处查询绑定
                accumulator.AddQuery(source, query.Name);
            }
        }

        // multipart 与 [Body] 互斥：一个模型两种请求体形态属二义性
        if (isMultipart && accumulator.HasBody)
        {
            throw new InvalidOperationException(
                $"请求模型 {requestType.Name} 同时声明了 [Multipart] 与 [Body]（{accumulator.BodyName}）："
                + "multipart 模型的请求体由全部属性装配，两者互斥。");
        }

        var plan = accumulator.Build(route.Method, route.Path, allowDefaultBody: !isMultipart);

        return new RequestModelMetadata(route.Method, route.Path, plan)
        {
            Authorization = BuildAuthorization(requestType),
            IsMultipart = isMultipart,
            PartBindings = [.. parts]
        };
    }

    /// <summary>清理 [Part] 的可选字符串声明；显式空白（而非省略）视为构建错误。</summary>
    private static string? CleanPartValue(string? value, Type requestType, PropertyInfo property, string member)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            throw new InvalidOperationException($"请求模型 {requestType.Name}.{property.Name} 的 [Part].{member} 不能为空白：要省略请直接不设置。");
        }

        return trimmed;
    }

    /// <summary>取最近一层的授权声明（派生类优先于基类）；两个特性同标一层属二义性。</summary>
    private static ApiAuthorization BuildAuthorization(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type requestType)
        => ApiAuthorizationBuilder.Build(BuildInheritanceChain(requestType), "请求模型");

    /// <summary>类级静态头：基类在前、派生类在后（同名后者覆盖）。</summary>
    private static IEnumerable<HeadersAttribute> EnumerateStaticHeaderAttributes(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type requestType)
    {
        var chain = BuildInheritanceChain(requestType);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            var attribute = chain[i].GetCustomAttribute<HeadersAttribute>(inherit: false);
            if (attribute is not null)
            {
                yield return attribute;
            }
        }
    }

    /// <summary>类型自身在前的继承链（不包含 <see cref="object"/>）。</summary>
    private static List<Type> BuildInheritanceChain(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type requestType)
    {
        var chain = new List<Type>();
        for (var type = requestType; type is not null && type != typeof(object); type = type.BaseType)
        {
            chain.Add(type);
        }

        return chain;
    }

    /// <summary>公共可读实例属性（跳过索引器），按声明顺序遍历。</summary>
    private static IEnumerable<PropertyInfo> EnumerateBindableProperties(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type requestType)
        => requestType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0);

    /// <summary>本属性自身（不含基类继承）声明了几个参数特性。</summary>
    private static int CountOwnParameterAttributes(PropertyInfo property)
        => (property.GetCustomAttribute<PathAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<QueryAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<HeaderAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<HeaderCollectionAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<PartAttribute>(inherit: false) is not null ? 1 : 0)
            + (property.GetCustomAttribute<BodyAttribute>(inherit: false) is not null ? 1 : 0);

    private static InvalidOperationException DuplicateBinding(Type requestType, string name, string kind)
        => new($"请求模型 {requestType.Name} 有多个 {kind} 绑定使用了同名 \"{name}\"：同名参数请合并为一个属性（集合值走 AxiosNet 的数组规则）。");
}
