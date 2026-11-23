using System.Diagnostics.CodeAnalysis;

namespace AxiosNet.Comparison.Targets;

/// <summary>可选实现清单：命令行名字 → 构造器。名字保持与 <see cref="ITarget.Name"/> 一致，报告和参数共用。</summary>
internal static class LibraryCatalog
{
    // 名字 → 实现定义；键同时是报告里显示的名字，新增实现只改这一处。
    private static readonly Dictionary<string, TargetDefinition> Definitions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["HttpClient"] = new((url, _) => new HttpClientTarget(url), ApiStyle.Typed),
            ["Refit"] = new((url, _) => new RefitTarget(url), ApiStyle.Interface),
            ["RestSharp"] = new((url, _) => new RestSharpTarget(url), ApiStyle.Typed),
            ["Flurl"] = new((url, _) => new FlurlTarget(url), ApiStyle.Typed),
            ["AxiosNet"] = new(
                (url, style) => style switch
                {
                    ApiStyle.Model => new AxiosNetModelTarget(url),
                    ApiStyle.Interface => new AxiosNetInterfaceTarget(url),
                    _ => new AxiosNetTarget(url)
                },
                ApiStyle.Typed,
                ApiStyle.Model,
                ApiStyle.Interface),
            ["RawSocket"] = new((url, _) => new RawSocketTarget(url))
        };

    /// <summary>默认参与对比的客户端实现（RawSocket 是服务端基线，由 <see cref="Create"/> 自动追加）。</summary>
    public static readonly string[] DefaultLibraries = ["HttpClient", "Refit", "RestSharp", "Flurl", "AxiosNet"];

    /// <summary>全部可选实现名，报错信息与 <c>--libs</c> 校验都读这里，不会和构造器清单脱节。</summary>
    public static IEnumerable<string> Names => Definitions.Keys;

    /// <summary>把用户输入的大小写形态归一成清单里的标准名；不认识的返回 false，由调用方列出可选实现。</summary>
    public static bool TryCanonicalize(string name, [NotNullWhen(true)] out string? canonical)
    {
        foreach (var key in Definitions.Keys)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                canonical = key;
                return true;
            }
        }

        canonical = null;
        return false;
    }

    /// <summary>
    /// 按命令行顺序构造实现，每个实现取 <paramref name="style"/> 对应的入口；不支持的退回该实现自身的形态。
    /// RawSocket 不在清单里时补在最后（分配口径必须减掉服务端成本）。
    /// </summary>
    public static IReadOnlyList<ITarget> Create(IReadOnlyList<string> names, string baseUrl, ApiStyle style)
    {
        var targets = new List<ITarget>(names.Count + 1);
        foreach (var name in names)
        {
            targets.Add(Definitions[name].Create(baseUrl, style));
        }

        if (!targets.Exists(t => t is RawSocketTarget))
        {
            targets.Add(new RawSocketTarget(baseUrl));
        }

        return targets;
    }
}

/// <summary>一个实现的可用形态与构造方式；<see cref="Styles"/> 为空表示不区分形态（裸 socket 基线）。</summary>
internal sealed class TargetDefinition
{
    private readonly Func<string, ApiStyle, ITarget> _factory;

    /// <summary>登记一个实现：<paramref name="styles"/> 的首项同时是不支持所选形态时的回退形态。</summary>
    public TargetDefinition(Func<string, ApiStyle, ITarget> factory, params ApiStyle[] styles)
    {
        _factory = factory;
        Styles = styles;
    }

    /// <summary>该实现原生支持的形态，顺序即回退优先级；空表示不区分形态。</summary>
    public IReadOnlyList<ApiStyle> Styles { get; }

    /// <summary>按请求的形态构造实现；不支持时退回 <see cref="Styles"/> 的首项。</summary>
    public ITarget Create(string baseUrl, ApiStyle requested)
        => _factory(baseUrl, Styles.Count == 0 || Styles.Contains(requested) ? requested : Styles[0]);
}
