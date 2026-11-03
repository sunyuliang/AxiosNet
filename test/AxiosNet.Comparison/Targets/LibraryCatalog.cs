using System.Diagnostics.CodeAnalysis;

namespace AxiosNet.Comparison.Targets;

/// <summary>可选实现清单：命令行名字 → 构造器。名字保持与 <see cref="ITarget.Name"/> 一致，报告和参数共用。</summary>
internal static class LibraryCatalog
{
    // 名字 → 构造器；键同时是报告里显示的名字，新增实现只改这一处。
    private static readonly Dictionary<string, Func<string, ITarget>> Factories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["HttpClient"] = url => new HttpClientTarget(url),
            ["Refit"] = url => new RefitTarget(url),
            ["RestSharp"] = url => new RestSharpTarget(url),
            ["Flurl"] = url => new FlurlTarget(url),
            ["AxiosNet"] = url => new AxiosNetTarget(url),
            ["RawSocket"] = url => new RawSocketTarget(url)
        };

    /// <summary>默认参与对比的客户端实现（RawSocket 是服务端基线，由 <see cref="Create"/> 自动追加）。</summary>
    public static readonly string[] DefaultLibraries = ["HttpClient", "Refit", "RestSharp", "Flurl", "AxiosNet"];

    /// <summary>全部可选实现名，报错信息与 <c>--libs</c> 校验都读这里，不会和构造器清单脱节。</summary>
    public static IEnumerable<string> Names => Factories.Keys;

    /// <summary>把用户输入的大小写形态归一成清单里的标准名；不认识的返回 false，由调用方列出可选实现。</summary>
    public static bool TryCanonicalize(string name, [NotNullWhen(true)] out string? canonical)
    {
        foreach (var key in Factories.Keys)
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

    /// <summary>按命令行顺序构造实现；RawSocket 不在清单里时补在最后（分配口径必须减掉服务端成本）。</summary>
    public static IReadOnlyList<ITarget> Create(IReadOnlyList<string> names, string baseUrl)
    {
        var targets = new List<ITarget>(names.Count + 1);
        foreach (var name in names)
        {
            targets.Add(Factories[name](baseUrl));
        }

        if (!targets.Exists(t => t is RawSocketTarget))
        {
            targets.Add(new RawSocketTarget(baseUrl));
        }

        return targets;
    }
}
