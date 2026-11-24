using System.Collections;
using AxiosNet.Internal.Content;
using AxiosNet.Metadata;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.WebAPI.Internal.Binding;

/// <summary>
/// 一份声明（类模型 / 接口方法）的绑定计划：构建期完成全部校验，运行期只做数组遍历；值 → 文本统一走 <see cref="ValueText"/>，本类只决定通道。
/// </summary>
internal sealed class BindingPlan
{
    /// <summary>空计划：无任何绑定通道。</summary>
    internal static readonly BindingPlan Empty = new(
        [],
        [],
        [],
        [],
        [],
        [],
        []);

    private readonly (string Token, IValueSource Source)[] _pathTokens;
    private readonly (string Name, IValueSource Source)[] _queryBindings;
    private readonly (string Name, IValueSource Source)[] _queryMaps;
    private readonly (string Name, IValueSource Source)[] _headerBindings;
    private readonly IValueSource[] _headerCollections;

    /// <summary>声明级静态头：Value 为 null 表示删除该头。</summary>
    private readonly (string Name, string? Value)[] _staticHeaders;

    /// <summary>正文来源：按声明顺序排列（空数组表示没有请求体）。</summary>
    private readonly BodyEntry[] _bodyEntries;

    internal BindingPlan(
        (string Token, IValueSource Source)[] pathTokens,
        (string Name, IValueSource Source)[] queryBindings,
        (string Name, IValueSource Source)[] queryMaps,
        (string Name, IValueSource Source)[] headerBindings,
        IValueSource[] headerCollections,
        (string Name, string? Value)[] staticHeaders,
        BodyEntry[] bodyEntries)
    {
        _pathTokens = pathTokens;
        _queryBindings = queryBindings;
        _queryMaps = queryMaps;
        _headerBindings = headerBindings;
        _headerCollections = headerCollections;
        _staticHeaders = staticHeaders;
        _bodyEntries = bodyEntries;
    }

    /// <summary>请求体原始值：无参 <see cref="BodyAttribute"/> 时原样返回，否则把正文键聚合成一个对象。</summary>
    public object? ResolveBodyValue(object target, Func<object?, ValueTextChannel, string?>? formatter)
    {
        if (_bodyEntries.Length == 0)
        {
            return null;
        }

        // 单条正文键也要加壳：{ "键": 值 }。
        if (!_bodyEntries[0].IsRootBody)
        {
            var aggregate = new AggregateBodyMember[_bodyEntries.Length];
            for (var index = 0; index < _bodyEntries.Length; index++)
            {
                var entry = _bodyEntries[index];
                aggregate[index] = new AggregateBodyMember(
                    entry.Name,
                    entry.Source.GetValue(target),
                    entry.Source.ValueType,
                    entry.NameFromAttribute,
                    entry.Source.Format);
            }

            return new AggregateBody(aggregate);
        }

        var source = _bodyEntries[0].Source;
        return ResolveRootValue(source.GetValue(target), source.Format, formatter);
    }

    /// <summary>整份正文的取值规则（无参 <see cref="BodyAttribute"/> 与生成期字面量快路共用）：标量正文走格式化钩子，其余原样。</summary>
    /// <param name="value">正文原始值。</param>
    /// <param name="format">成员级 <c>[Format]</c>；生成期快路的参数绑定没有成员级格式化，传 <c>null</c>。</param>
    /// <param name="formatter">实例级格式化钩子。</param>
    public static object? ResolveRootValue(
        object? value,
        ValueFormat? format,
        Func<object?, ValueTextChannel, string?>? formatter)
    {
        if (value is null || !ScalarTypes.IsScalarBodyType(value.GetType()))
        {
            return value;
        }

        // 只有真的产出文本时才改成文本正文：二进制正文的默认形态是原始字节。
        if (ValueText.TryCustomFormat(value, ValueTextChannel.ScalarBody, format, formatter) is { } text)
        {
            return new PreformattedValue(text);
        }

        // 钩子已看过该值：封存默认文本；二进制正文除外。
        return formatter is not null && !ScalarTypes.IsBinaryType(value.GetType())
            ? new PreformattedValue(ValueText.Format(value, ValueTextChannel.ScalarBody)!)
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

            var text = FormatMember(
                    value,
                    ValueTextChannel.Path,
                    source.Format,
                    formatter,
                    BindingChannel.Path,
                    source.Description)
                ?? throw new InvalidOperationException(
                    $"路径参数 {source.Description}（占位符 {{{token}}}）无法转成文本：格式化函数返回了 null，且值本身没有默认形态。");

            url = url.Replace("{" + token + "}", Uri.EscapeDataString(text));
        }

        return url;
    }

    /// <summary>汇总全部查询参数；没有参数或值全为 <c>null</c> 时返回 <c>null</c>（调用方不要写进配置：显式 null 会摘掉实例默认值）。</summary>
    public Dictionary<string, object?>? ResolveQuery(object target, Func<object?, ValueTextChannel, string?>? formatter)
    {
        if (_queryBindings.Length == 0 && _queryMaps.Length == 0)
        {
            return null;
        }

        var query = new Dictionary<string, object?>();

        foreach (var binding in _queryBindings)
        {
            var value = FormatQueryMember(
                binding.Source.GetValue(target),
                binding.Source.Format,
                formatter,
                binding.Source.Description);

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
                // 成员文本只在失败时拼（含字典键）：成功路径不为消息分配字符串。
                object? value;
                try
                {
                    value = FormatQueryValue(entry.Value, map.Source.Format, formatter);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not AxiosException)
                {
                    throw new BindingMemberException(
                        BindingChannel.Query,
                        $"{map.Source.Description}[{entry.Key}]",
                        ex);
                }

                if (value is not null)
                {
                    // 与具名参数同名时映射值覆盖。
                    query[entry.Key] = value;
                }
            }
        }

        return query.Count > 0 ? query : null;
    }

    /// <summary>把全部请求头绑定写进配置；属性 / 参数上的头压过声明级同名项（含「只写名字 = 删除」的声明）。</summary>
    public void ApplyHeaders(object target, AxiosConfig config, Func<object?, ValueTextChannel, string?>? formatter)
    {
        foreach (var binding in _headerBindings)
        {
            var value = binding.Source.GetValue(target);

            // 与 query 一致：null 头跳过
            if (value is not null)
            {
                // 写值时撤销声明级的待删除项。
                ClearPendingRemoval(config, binding.Name);
                config.Headers.Set(
                    binding.Name,
                    FormatMember(
                        value,
                        ValueTextChannel.Header,
                        binding.Source.Format,
                        formatter,
                        BindingChannel.Header,
                        binding.Source.Description) ?? string.Empty);
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

                // 字典值与单值头同口径：都走「值 → 请求头文本」（成员级 [Format] 在构建期已被拒绝）。
                string? text;
                try
                {
                    text = ValueText.Format(entry.Value, ValueTextChannel.Header, memberFormat: null, formatter);
                }
                catch (Exception ex) when (ex is not OperationCanceledException and not AxiosException)
                {
                    throw new BindingMemberException(
                        BindingChannel.Header,
                        $"{source.Description}[{entry.Key}]",
                        ex);
                }

                if (text is not null)
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

    /// <summary>把一个值转成本通道的线上文本；失败时补上「哪条通道、哪个成员」再抛。</summary>
    private static string? FormatMember(
        object? value,
        ValueTextChannel channel,
        ValueFormat? format,
        Func<object?, ValueTextChannel, string?>? formatter,
        BindingChannel bindingChannel,
        string member)
    {
        try
        {
            return ValueText.Format(value, channel, format, formatter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AxiosException)
        {
            // 自定义格式化可抛任意异常：只补「哪个字段」，原始异常保持为 Cause。
            throw new BindingMemberException(bindingChannel, member, ex);
        }
    }

    /// <summary>查询参数版本的 <see cref="FormatMember"/>：走查询通道的集合折叠规则。</summary>
    private static object? FormatQueryMember(
        object? value,
        ValueFormat? format,
        Func<object?, ValueTextChannel, string?>? formatter,
        string member)
    {
        try
        {
            return FormatQueryValue(value, format, formatter);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not AxiosException)
        {
            throw new BindingMemberException(BindingChannel.Query, member, ex);
        }
    }

    /// <summary>查询参数的文本化：标量取文本、集合逐元素取文本、嵌套对象原样交给 <see cref="ParamsSerializer"/> 展开。</summary>
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

            // 有实例钩子时封存结果：ParamsSerializer 不能再套第二遍钩子。
            return formatter is null || text is null ? text : new PreformattedValue(text);
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
            // 有实例钩子时只文本化标量叶子，结构化元素原样交给 ParamsSerializer 展开。
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
                            mixed.Add(new PreformattedValue(text));
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

    /// <summary>从待删除集合里摘掉所有匹配的名字（头名大小写不敏感）。</summary>
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

    /// <summary>删掉集合里第一个同名项；删除后立即退出枚举。</summary>
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

/// <summary>
/// 正文的一个来源：正文键（<c>[Body("名")]</c> / body 方法上的未标注成员，按键写入），或无参 <c>[Body]</c>（整份正文、原样发送）；顺序即输出顺序。
/// </summary>
internal readonly struct BodyEntry
{
    public BodyEntry(int order, string name, bool nameFromAttribute, IValueSource source, bool isRootBody)
    {
        Order = order;
        Name = name;
        NameFromAttribute = nameFromAttribute;
        Source = source;
        IsRootBody = isRootBody;
    }

    /// <summary>声明序号（正文键与整份正文共用一个自增计数）。</summary>
    public int Order { get; }

    /// <summary>键名（<c>[Body("名")]</c> &gt; <c>[JsonPropertyName]</c> &gt; 成员名）。</summary>
    public string Name { get; }

    /// <summary>键名是否已显式指定（<c>[Body("名")]</c> / <c>[JsonPropertyName]</c>）；是则不套 JSON 命名策略。</summary>
    public bool NameFromAttribute { get; }

    /// <summary>取值来源。</summary>
    public IValueSource Source { get; }

    /// <summary>是否来自无参 <c>[Body]</c>：整份正文，原样发送、不加壳。</summary>
    public bool IsRootBody { get; }
}
