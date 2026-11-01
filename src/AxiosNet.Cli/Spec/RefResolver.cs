using System.Globalization;
using AxiosNet.Cli.Model;

namespace AxiosNet.Cli.Spec;

/// <summary>解析文档内部 JSON Pointer（<c>#/a/b</c>）。外部引用不下载，按缺口记录后交给调用方降级处理。</summary>
internal sealed class RefResolver
{
    /// <summary>解析基准节点（通常是文档根）。</summary>
    private readonly SpecNode _root;

    /// <summary>断链 / 环 / 外部引用的警告输出。</summary>
    private readonly WarningCollector _warnings;

    /// <summary>以 <paramref name="root"/> 为基准构造解析器。</summary>
    public RefResolver(SpecNode root, WarningCollector warnings)
    {
        _root = root;
        _warnings = warnings;
    }

    /// <summary>按 JSON Pointer 取节点；引用不存在或不是内部引用时返回 null。</summary>
    public SpecNode? ResolvePointer(string pointer)
    {
        if (!pointer.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }

        // 从根开始逐段下钻；JSON Pointer 的 ~1 / ~0 是 / 与 ~ 的转义。
        var node = _root;
        foreach (var raw in pointer[2..].Split('/'))
        {
            var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

            // 对象按 key 下钻，数组按十进制下标下钻（如 #/paths/~1x/get/parameters/0）；
            // 下标越界或不是数字时视为断链。
            if (node.IsObject)
            {
                if (!node.Object.TryGetValue(segment, out var next))
                {
                    return null;
                }

                node = next;
                continue;
            }

            if (node.IsArray)
            {
                if (!int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                    || index < 0
                    || index >= node.Items.Count)
                {
                    return null;
                }

                node = node.Items[index];
                continue;
            }

            return null;
        }

        return node;
    }

    /// <summary>把 <c>$ref</c> 链解析到实体；环、断链、外部引用都记警告并返回 null。</summary>
    public SpecNode? Resolve(SpecNode? node, string location, HashSet<string>? visited = null, int depth = 0)
    {
        if (node is null)
        {
            return null;
        }

        var reference = node["$ref"]?.AsString();
        if (reference is null)
        {
            return node;
        }

        if (!reference.StartsWith("#/", StringComparison.Ordinal))
        {
            _warnings.Add("EXTERNAL_REF", location, $"不支持外部引用 {reference}，按缺失处理。");
            return null;
        }

        // visited 记录本次解析链上出现过的指针；深度上限 32 兜底防止异常文档无限展开。
        visited ??= new HashSet<string>(StringComparer.Ordinal);
        if (depth > 32 || !visited.Add(reference))
        {
            _warnings.Add("REF_CYCLE", location, $"引用链存在循环：{reference}。");
            return null;
        }

        var target = ResolvePointer(reference);
        if (target is null)
        {
            _warnings.Add("BROKEN_REF", location, $"引用不存在：{reference}。");
            return null;
        }

        return Resolve(target, location, visited, depth + 1);
    }

    /// <summary>取引用路径的最后一段作为类型名（解码 <c>~1</c> / <c>~0</c>）。</summary>
    public static string LastSegment(string pointer)
    {
        var index = pointer.LastIndexOf('/');
        var segment = index >= 0 ? pointer[(index + 1)..] : pointer;
        return segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
    }
}
