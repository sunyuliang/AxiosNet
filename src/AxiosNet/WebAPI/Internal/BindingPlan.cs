using System.Collections;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 一份声明（类模型 / 接口方法）的绑定计划：构建期完成全部校验，运行期只做数组遍历。
/// 取值的来源由 <see cref="IValueSource"/> 抽象，类模型传模型实例，接口模型传参数数组。
/// </summary>
internal sealed class BindingPlan
{
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

    public bool HasBody => _body is not null;

    public string? BodyName => _body?.Name;

    /// <summary>请求体的原始值（普通声明取 <see cref="BodyAttribute"/> 绑定的来源）。</summary>
    public object? ResolveBodyValue(object target) => _body?.GetValue(target);

    /// <summary>把路径模板里的 <c>{name}</c> 替换成值（URL 转义），返回最终请求 URL。</summary>
    public string ResolveUrl(string pathTemplate, object target, Func<object, string?>? formatter)
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

            url = url.Replace("{" + token + "}", Uri.EscapeDataString(ToText(FormatValue(value, formatter)!)));
        }

        return url;
    }

    /// <summary>
    /// 汇总全部查询参数；没有参数或值全为 <c>null</c> 时返回 <c>null</c>。
    /// 调用方拿到 <c>null</c> 时不要写进配置：合并层把显式 null 当作摘掉实例默认值。
    /// </summary>
    public Dictionary<string, object?>? ResolveQuery(object target, Func<object, string?>? formatter)
    {
        if (_queryBindings.Length == 0 && _queryMaps.Length == 0)
        {
            return null;
        }

        var query = new Dictionary<string, object?>();

        foreach (var binding in _queryBindings)
        {
            var value = FormatValue(binding.Source.GetValue(target), formatter);

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

    /// <summary>把全部请求头绑定写进配置；没有任何头绑定时不动配置。</summary>
    public void ApplyHeaders(object target, AxiosConfig config)
    {
        foreach (var binding in _headerBindings)
        {
            var value = binding.Source.GetValue(target);

            // 与 query 一致：null 头跳过
            if (value is not null)
            {
                config.Headers.Set(binding.Name, ToText(value));
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
                if (entry.Value is string text)
                {
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

    /// <summary>从待删除集合里摘掉匹配的名字（头名大小写不敏感）；没有匹配项时不动集合。</summary>
    private static void ClearPendingRemoval(AxiosConfig config, string name)
    {
        if (!config.HasRemoveHeaders)
        {
            return;
        }

        string? matched = null;
        foreach (var pending in config.RemoveHeaders)
        {
            if (string.Equals(pending, name, StringComparison.OrdinalIgnoreCase))
            {
                matched = pending;
                break;
            }
        }

        if (matched is not null)
        {
            config.RemoveHeaders.Remove(matched);
        }
    }

    /// <summary>基础类型的线上文本：转调查询串 / 表单的同一份规则。</summary>
    private static string ToText(object value) => ParamsSerializer.ScalarToString(value);

    /// <summary>URL 参数格式化钩子：<paramref name="formatter"/> 返回 null 时回落到默认规则。</summary>
    private static object? FormatValue(object? value, Func<object, string?>? formatter)
        => value is null || formatter is null ? value : formatter(value) ?? value;
}
