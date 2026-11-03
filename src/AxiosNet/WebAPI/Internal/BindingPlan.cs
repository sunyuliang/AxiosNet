using System.Collections;
using AxiosNet.Internal;
using AxiosNet.Metadata;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 一份声明（类模型 / 接口方法）的绑定计划：构建期完成全部校验，运行期只做数组遍历。
/// 取值的来源由 <see cref="IValueSource"/> 抽象，类模型传模型实例，接口模型传参数数组。
/// 值 → 文本统一走 <see cref="ValueText"/>，本类只决定通道。
/// </summary>
internal sealed class BindingPlan
{
    // 空计划：没有参数要绑的方法共用这一份，省一次分配。
    internal static readonly BindingPlan Empty = new(
        [],
        [],
        [],
        [],
        [],
        [],
        null);

    private readonly (string Token, IValueSource Source)[] _pathTokens;
    private readonly (string Name, IValueSource Source)[] _queryBindings;
    private readonly (string Name, IValueSource Source)[] _queryMaps;
    private readonly (string Name, IValueSource Source)[] _headerBindings;
    private readonly IValueSource[] _headerCollections;

    /// <summary>声明级静态头：Value 为 null 表示删除该头。</summary>
    private readonly (string Name, string? Value)[] _staticHeaders;

    private readonly IValueSource? _body;

    internal BindingPlan(
        (string Token, IValueSource Source)[] pathTokens,
        (string Name, IValueSource Source)[] queryBindings,
        (string Name, IValueSource Source)[] queryMaps,
        (string Name, IValueSource Source)[] headerBindings,
        IValueSource[] headerCollections,
        (string Name, string? Value)[] staticHeaders,
        IValueSource? body)
    {
        _pathTokens = pathTokens;
        _queryBindings = queryBindings;
        _queryMaps = queryMaps;
        _headerBindings = headerBindings;
        _headerCollections = headerCollections;
        _staticHeaders = staticHeaders;
        _body = body;
    }

    /// <summary>是否已绑定请求正文（正文只能有一个）。</summary>
    public bool HasBody => _body is not null;

    /// <summary>正文的定位名（错误消息用）。</summary>
    public string? BodyName => _body?.Name;

    /// <summary>
    /// 请求体的原始值（普通声明取 <see cref="BodyAttribute"/> 绑定的来源）。
    /// 标量 + 成员级格式化 / 实例钩子时返回 <see cref="FormattedScalarBody"/>，让正文工厂按标量正文处理而不是当字符串透传；
    /// 钩子返回 <c>null</c>（本次不处理）时封存通道默认文本，正文工厂便不会再问它第二遍。
    /// 二进制正文只在真的产出文本时封存，否则保持原始字节。
    /// </summary>
    /// <param name="target">请求模型实例或方法参数数组。</param>
    /// <param name="formatter">实例级格式化钩子；<c>null</c> 表示不处理。</param>
    public object? ResolveBodyValue(object target, Func<object?, ValueTextChannel, string?>? formatter)
    {
        var value = _body?.GetValue(target);
        if (value is null || !ScalarTypes.IsScalarBodyType(value.GetType()))
        {
            return value;
        }

        // 只有两层声明真的产出文本时才改成文本正文：二进制正文的默认形态是原始字节，
        // 不能因为配置了「对其它类型生效、对 byte[] 返回 null」的实例钩子就退化成 base64 文本。
        if (ValueText.TryCustomFormat(value, ValueTextChannel.ScalarBody, _body!.Format, formatter) is { } text)
        {
            return new FormattedScalarBody(text);
        }

        // 钩子在绑定期已经看过这个值（返回 null = 不处理）：把默认文本一并封存，
        // 正文工厂对裸标量还会按「直接调用」那条规则再问一次钩子，封存后同一个值不会被问第二遍。
        // 二进制正文除外——它的默认形态是原始字节，封存成文本就等于 base64 化。
        return formatter is not null && !ScalarTypes.IsBinaryType(value.GetType())
            ? new FormattedScalarBody(ValueText.Format(value, ValueTextChannel.ScalarBody)!)
            : value;
    }

    /// <summary>把路径模板里的 <c>{name}</c> 替换成值（URL 转义），返回最终请求 URL。</summary>
    public string ResolveUrl(string pathTemplate, object target, Func<object?, ValueTextChannel, string?>? formatter)
    {
        if (_pathTokens.Length == 0)
        {
            return pathTemplate;
        }

        var url = pathTemplate;

        foreach (var (token, source) in _pathTokens)
        {
            var value = source.GetValue(target)
                ?? throw new InvalidOperationException(
                    $"路径参数 {source.Description}（占位符 {{{token}}}）为 null：路径缺段无法成行。");

            var text = ValueText.Format(value, ValueTextChannel.Path, source.Format, formatter)
                ?? throw new InvalidOperationException(
                    $"路径参数 {source.Description}（占位符 {{{token}}}）无法转成文本：格式化函数返回了 null，且值本身没有默认形态。");

            url = url.Replace("{" + token + "}", Uri.EscapeDataString(text));
        }

        return url;
    }

    /// <summary>
    /// 汇总全部查询参数；没有参数或值全为 <c>null</c> 时返回 <c>null</c>。
    /// 调用方拿到 <c>null</c> 时不要写进配置：合并层把显式 null 当作摘掉实例默认值。
    /// </summary>
    public Dictionary<string, object?>? ResolveQuery(object target, Func<object?, ValueTextChannel, string?>? formatter)
    {
        if (_queryBindings.Length == 0 && _queryMaps.Length == 0)
        {
            return null;
        }

        var query = new Dictionary<string, object?>();

        foreach (var binding in _queryBindings)
        {
            var value = FormatQueryValue(binding.Source.GetValue(target), binding.Source.Format, formatter);

            // 与 axios params 一致：null 参数跳过
            if (value is not null)
            {
                query.Add(binding.Name, value);
            }
        }

        // 字典映射平铺：值仍保持原始对象，集合 / 嵌套对象继续走 UrlBuilder 的既有规则
        foreach (var map in _queryMaps)
        {
            if (map.Source.GetValue(target) is not { } dictionary)
            {
                continue;
            }

            foreach (var entry in QueryMap.Enumerate(dictionary))
            {
                var value = FormatQueryValue(entry.Value, map.Source.Format, formatter);
                if (value is not null)
                {
                    // 与具名参数同名时映射值覆盖。
                    query[entry.Key] = value;
                }
            }
        }

        return query.Count > 0 ? query : null;
    }

    /// <summary>
    /// 把全部请求头绑定写进配置；没有任何头绑定时不动配置。
    /// 属性 / 参数上的头压过声明级同名项（含「只写名字 = 删除」的声明）：写值时撤销待删除项。
    /// </summary>
    public void ApplyHeaders(object target, AxiosConfig config, Func<object?, ValueTextChannel, string?>? formatter)
    {
        foreach (var binding in _headerBindings)
        {
            var value = binding.Source.GetValue(target);

            // 与 query 一致：null 头跳过
            if (value is not null)
            {
                // 静态头先写、参数头后写，所以这里必须撤销声明级的待删除项：
                // 否则合并层最后统一 Delete，会把刚写进去的签名 / 鉴权头静默摘掉。
                ClearPendingRemoval(config, binding.Name);
                config.Headers.Set(
                    binding.Name,
                    ValueText.Format(value, ValueTextChannel.Header, binding.Source.Format, formatter) ?? string.Empty);
            }
        }

        foreach (var source in _headerCollections)
        {
            if (source.GetValue(target) is not { } dictionary)
            {
                continue;
            }

            foreach (var entry in QueryMap.Enumerate(dictionary))
            {
                // 值为 null 的项跳过（字典头没有「删除」语义，删除请用声明级 [Headers("X-Remove")]）
                if (entry.Value is null)
                {
                    continue;
                }

                // 字典值与单值头同一口径：都走「值 → 请求头文本」这一步，实例钩子对两者一视同仁
                // （成员级 [Format] 在构建期已被拒绝，所以这里没有成员声明可应用）。
                if (ValueText.Format(entry.Value, ValueTextChannel.Header, memberFormat: null, formatter) is { } text)
                {
                    ClearPendingRemoval(config, entry.Key);
                    config.Headers.Set(entry.Key, text);
                }
            }
        }
    }

    /// <summary>应用声明级 <see cref="HeadersAttribute"/>：值写入头，只有名字的声明摘除同名头。</summary>
    public void ApplyStaticHeaders(AxiosConfig config)
    {
        foreach (var (name, value) in _staticHeaders)
        {
            if (value is null)
            {
                config.RemoveHeaders.Add(name);
                continue;
            }

            // 值声明（更具体的一层）优先于更早的删除声明（基类）：写值的同时撤销待删除项。
            ClearPendingRemoval(config, name);
            config.Headers.Set(name, value);
        }
    }

    /// <summary>
    /// 查询参数的文本化：标量取文本、集合逐元素取文本、其余（嵌套对象）原样交给 <see cref="ParamsSerializer"/> 展开。
    /// 集合在这里就折成文本列表，是因为查询串不需要再区分元素原始类型。
    /// </summary>
    private static object? FormatQueryValue(object? value, ValueFormat? format, Func<object?, ValueTextChannel, string?>? formatter)
    {
        if (value is null)
        {
            return null;
        }

        // 没有任何格式化声明：原样返回，保持既有形态（自定义 ParamsSerializer 仍拿得到原始对象）。
        if (format is null && formatter is null)
        {
            return value;
        }

        if (value is string || ScalarTypes.IsScalarType(value.GetType()) || ScalarTypes.IsBinaryType(value.GetType()))
        {
            var text = ValueText.Format(value, ValueTextChannel.Query, format, formatter);

            // 有实例钩子时把结果封存：ParamsSerializer 不能再对这段文本套第二遍钩子，
            // 否则成员级 [Format] 的结果会被钩子覆盖（优先级链被打破）。
            return formatter is null || text is null ? text : new FormattedQueryValue(text);
        }

        // 字典保持键结构（显式命名的映射要展开成 a[key]=v），只把值文本化。
        if (value is IEnumerable && QueryMap.CanEnumerate(value))
        {
            var map = new Dictionary<string, object?>();
            foreach (var entry in QueryMap.Enumerate(value))
            {
                if (entry.Value is not null && FormatQueryValue(entry.Value, format, formatter) is { } mapped)
                {
                    map[entry.Key] = mapped;
                }
            }

            return map;
        }

        if (value is IEnumerable items)
        {
            // 有实例钩子时只文本化标量叶子：结构化元素原样保留，继续交给 ParamsSerializer 展开
            // （否则列表里的对象会被 ToString() 压成一行，嵌套结构静默丢失）。
            if (formatter is not null)
            {
                var mixed = new List<object?>();
                foreach (var item in items)
                {
                    if (item is null)
                    {
                        continue;
                    }

                    if (item is string || ScalarTypes.IsScalarType(item.GetType()) || ScalarTypes.IsBinaryType(item.GetType()))
                    {
                        if (ValueText.Format(item, ValueTextChannel.Query, format, formatter) is { } text)
                        {
                            mixed.Add(new FormattedQueryValue(text));
                        }
                    }
                    else
                    {
                        mixed.Add(item);
                    }
                }

                return mixed;
            }

            var texts = new List<string>();
            foreach (var item in items)
            {
                if (item is not null && ValueText.Format(item, ValueTextChannel.Query, format, formatter) is { } text)
                {
                    texts.Add(text);
                }
            }

            return texts;
        }

        return value;
    }

    /// <summary>从待删除集合里摘掉匹配的名字（头名大小写不敏感）；没有匹配项时不动集合。</summary>
    /// <remarks>
    /// 同名声明可能重复出现（<see cref="AxiosConfig.RemoveHeaders"/> 只是 <c>ICollection</c>，不去重：
    /// <c>[Headers("X", "X")]</c> 或类 / 接口各声明一次都会留下两条），所以必须**删干净**。
    /// 只删一条的话，剩下那条仍然会在合并层把这个头删掉——表现为「写了签名头却发不出去」。
    /// </remarks>
    private static void ClearPendingRemoval(AxiosConfig config, string name)
    {
        if (!config.HasRemoveHeaders)
        {
            return;
        }

        while (TakeOne(config.RemoveHeaders, name))
        {
        }
    }

    /// <summary>删掉集合里第一个同名项；没有返回 <c>false</c>（删除后立即退出枚举，不在枚举中继续 Remove）。</summary>
    private static bool TakeOne(ICollection<string> pending, string name)
    {
        foreach (var item in pending)
        {
            if (string.Equals(item, name, StringComparison.OrdinalIgnoreCase))
            {
                pending.Remove(item);
                return true;
            }
        }

        return false;
    }
}
