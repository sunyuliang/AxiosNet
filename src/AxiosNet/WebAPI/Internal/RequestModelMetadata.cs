using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AxiosNet.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.WebAPI.Internal;

/// <summary>请求模型的绑定元数据（按模型类型缓存一份，构造期完成全部校验）。校验失败同样进入缓存，每次调用都抛同一份错误。</summary>
internal sealed class RequestModelMetadata
{
    private const DynamicallyAccessedMemberTypes ModelMembers =
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.Interfaces;

    private static readonly ConcurrentDictionary<Type, RequestModelMetadata> Cache = new();

    private readonly string? _buildError;
    private readonly Type? _missingMetadataType;
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

    private RequestModelMetadata(Type missingMetadataType)
    {
        _missingMetadataType = missingMetadataType;
        _pathTemplate = string.Empty;
        _plan = BindingPlan.Empty;
    }

    /// <summary>本次请求的 HTTP 方法（来自路由特性；仅构建错误的缓存实例上为占位值，<see cref="Get"/> 会先抛错）。</summary>
    public HttpMethod Method { get; } = null!;

    /// <summary>授权策略（最近一层声明优先；都没标时为 <see cref="ApiAuthorizationMode.Inherit"/>）。</summary>
    internal ApiAuthorization Authorization { get; init; } = ApiAuthorization.Inherit;

    /// <summary>是否声明了 <see cref="MultipartAttribute"/>（请求体 = 模型自身装配的 multipart 表单）。</summary>
    internal bool IsMultipart { get; init; }

    /// <summary>multipart 部件绑定：未标注参数特性的属性默认参与（名字 = [JsonPropertyName] / 属性名），[Part] 可覆盖。</summary>
    internal PartBinding[] PartBindings { get; init; } = [];

    /// <summary>取（必要时构建并缓存）请求模型的绑定元数据；模型定义非法时抛 <see cref="InvalidOperationException"/>。</summary>
    /// <param name="requestType">请求模型类型；标注要求 JIT 回退路径保留公开属性（生成路径不依赖反射）。</param>
    public static RequestModelMetadata Get(
        [DynamicallyAccessedMembers(ModelMembers)] Type requestType)
    {
        // 用捕获的 requestType（带标注）而不是工厂参数：缓存字典的 Type 键本身不携带裁剪标注。
        var metadata = Cache.GetOrAdd(requestType, _ => Build(requestType));

        if (metadata._missingMetadataType is { } missingType)
        {
            // 每次新建异常：同一条缓存可被并发请求共享，异常实例不能跨线程同时抛出。
            throw AxiosAotMetadataException.MissingRequestModel(missingType);
        }

        if (metadata._buildError is not null)
        {
            throw new InvalidOperationException(metadata._buildError);
        }

        return metadata;
    }

    /// <summary>注册新声明后摘掉该类型的旧缓存：反射构建的结果不能压过之后的显式注册。</summary>
    /// <param name="requestType">被注册的请求模型类型。</param>
    internal static void Invalidate(Type requestType) => Cache.TryRemove(requestType, out _);

    /// <summary>测试钩子：切换动态代码能力后清空全部缓存（JIT 构建的元数据与 AOT 结论不能混用）。</summary>
    internal static void InvalidateAll() => Cache.Clear();

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

    /// <summary>把全部 <see cref="HeaderAttribute"/> 属性写进配置；没有任何头绑定时不动配置。头名 / 头值在配置构建期按 <see cref="AxiosHeaders.Set(string, string?)"/> 校验。</summary>
    public void ApplyHeaders(object request, AxiosConfig config) => _plan.ApplyHeaders(request, config);

    /// <summary>应用类级 <see cref="HeadersAttribute"/> 声明：值写入头，只有名字的声明摘除同名头。</summary>
    public void ApplyStaticHeaders(AxiosConfig config) => _plan.ApplyStaticHeaders(config);

    // 构建：声明优先（生成器 / 手工注册）→ JIT 反射 → AOT 显式失败

    private static RequestModelMetadata Build(
        [DynamicallyAccessedMembers(ModelMembers)] Type requestType)
    {
        try
        {
            if (GeneratedMetadataRegistry.TryGetRequestModel(requestType, out var declaration))
            {
                return BuildCore(declaration);
            }

            if (!RuntimeSupport.IsDynamicCodeSupported)
            {
                return new RequestModelMetadata(requestType);
            }

            return BuildCore(RequestModelDeclarationReader.Read(requestType));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            // 特性构造函数抛出的 ArgumentException / FormatException 连同校验错误一起缓存，统一从 Get() 抛出。
            return new RequestModelMetadata(ex.Message);
        }
    }

    /// <summary>构建契约：路由 → 类级静态头 → 属性绑定（multipart 部件）→ 默认 Body 推断 → 授权。</summary>
    private static RequestModelMetadata BuildCore(RequestModelDeclaration declaration)
    {
        var modelName = declaration.ModelType.Name;

        if (declaration.Error is { } registrationError)
        {
            throw new InvalidOperationException(registrationError);
        }

        if (string.IsNullOrWhiteSpace(declaration.MethodName) || string.IsNullOrWhiteSpace(declaration.Path))
        {
            throw new InvalidOperationException(
                $"请求模型 {modelName} 缺少路由特性：请在类上标注 [Get(\"...\")] / [Post(\"...\")] 等 HttpApiAttribute 子类。");
        }

        var method = DeclarationNormalization.HttpMethodFrom(declaration.MethodName);
        var path = DeclarationNormalization.Path(declaration.Path);
        var owner = $"请求模型 {modelName}";

        var accumulator = new BindingAccumulator(owner, "属性");

        // multipart 部件绑定：模型声明 [Multipart] 时，模型自身即表单
        var parts = new List<PartBinding>();
        var seenPartNames = new HashSet<string>(StringComparer.Ordinal);
        var isMultipart = declaration.Multipart;

        accumulator.AddStaticHeaders(declaration.StaticHeaders);

        foreach (var member in declaration.Members)
        {
            if (member.Ignore)
            {
                if (member.HasAnyBinding)
                {
                    throw new InvalidOperationException(
                        $"属性 {modelName}.{member.Name} 同时标注了 [ApiIgnore] 与参数特性（Path / Query / Header / HeaderCollection / Part / Body）："
                        + "[ApiIgnore] 的语义是「完全不绑定」，两者同用是二义性。");
                }

                // 完全排除：不进任何绑定，也不算「未标注」（不阻断默认 Body 判定）
                continue;
            }

            if (member.GetValue is null)
            {
                throw new InvalidOperationException(
                    $"请求模型 {modelName}.{member.Name} 缺少取值实现：生成元数据不完整，请检查源生成器版本与包是否匹配。");
            }

            // 同一属性可同时绑定多处（路径 + 头 + 查询 + 请求体），按各处的 null 规则分别应用。
            var source = new DelegateValueSource(
                member.Name,
                member.ValueType,
                member.GetValue,
                member.Description ?? $"{modelName}.{member.Name}");

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
                        $"属性 {modelName}.{member.Name} 标注了 [Part]，但模型没有 [Multipart]：请在类上声明 [Multipart]。");
                }

                var display = $"{owner}.{member.Name}";
                var explicitName = DeclarationNormalization.OptionalPartValue(member.PartName, display, nameof(PartAttribute.Name));

                // 默认名走 [JsonPropertyName] / 属性名，与 builder 展平嵌套对象时的命名规则一致
                var name = explicitName ?? member.FieldName;
                if (!seenPartNames.Add(name))
                {
                    throw DuplicateBinding(declaration.ModelType, name, "Part");
                }

                parts.Add(new PartBinding(
                    name,
                    source,
                    explicitName is not null,
                    DeclarationNormalization.OptionalPartValue(member.PartFileName, display, nameof(PartAttribute.FileName)),
                    DeclarationNormalization.OptionalPartValue(member.PartContentType, display, nameof(PartAttribute.ContentType))));
            }

            if (member.Body)
            {
                accumulator.AddBody(source);
            }

            if (!member.HasAnyBinding)
            {
                if (isMultipart)
                {
                    // multipart 模型：无标注属性默认按部件绑定（名字 = [JsonPropertyName] 或属性名），不参与默认 Body / Query 判定
                    var partName = member.FieldName;
                    if (!seenPartNames.Add(partName))
                    {
                        throw DuplicateBinding(declaration.ModelType, partName, "Part");
                    }

                    parts.Add(new PartBinding(partName, source, false, null, null));
                    continue;
                }

                // 无标注 → 默认 Query（名字回退属性名）
                accumulator.AddUnannotated(source);
            }
            else if (member.Query)
            {
                // 已有其他绑定的属性再显式 [Query]：追加一处查询绑定
                accumulator.AddQuery(source, member.QueryName ?? string.Empty);
            }
        }

        // multipart 与 [Body] 互斥：一个模型两种请求体形态属二义性
        if (isMultipart && accumulator.HasBody)
        {
            throw new InvalidOperationException(
                $"请求模型 {modelName} 同时声明了 [Multipart] 与 [Body]（{accumulator.BodyName}）："
                + "multipart 模型的请求体由全部属性装配，两者互斥。");
        }

        var plan = accumulator.Build(method, path, allowDefaultBody: !isMultipart);

        return new RequestModelMetadata(method, path, plan)
        {
            Authorization = DeclarationNormalization.Authorization(declaration.AllowAnonymous, declaration.AuthorizeScheme, owner),
            IsMultipart = isMultipart,
            PartBindings = [.. parts]
        };
    }

    private static InvalidOperationException DuplicateBinding(Type requestType, string name, string kind)
        => new($"请求模型 {requestType.Name} 有多个 {kind} 绑定使用了同名 \"{name}\"：同名参数请合并为一个属性（集合值走 AxiosNet 的数组规则）。");
}
