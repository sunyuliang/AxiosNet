using System.Diagnostics.CodeAnalysis;

namespace AxiosNet.Comparison.Targets;

/// <summary>可选实现清单：命令行名字 → 构造器。名字保持与 <see cref="ITarget.Name"/> 一致，报告和参数共用。</summary>
internal static class LibraryCatalog
{
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

    public static IEnumerable<string> Names => Factories.Keys;

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
