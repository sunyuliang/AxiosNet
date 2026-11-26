namespace AxiosNet.Cli.Diagnostics;

/// <summary>一处导入缺口或降级：写进控制台摘要与 <c>--report</c>。</summary>
/// <param name="Code">缺口编号，用于去重与分类统计。</param>
/// <param name="Location">出现位置（如 <c>操作.状态码</c>），报告里靠它定位。</param>
/// <param name="Message">给人看的说明。</param>
internal sealed record ApiWarning(string Code, string Location, string Message);

/// <summary>警告收集器：去重并设置上限。</summary>
internal sealed class WarningCollector
{
    /// <summary>单次运行的警告上限；超出后静默丢弃，报告与摘要都不会无限膨胀。</summary>
    private const int MaxWarnings = 500;

    /// <summary>已收集的警告（按加入顺序）。</summary>
    private readonly List<ApiWarning> _items = new();

    /// <summary>去重键集合（码 + 位置 + 消息），保证同一缺口只报一次。</summary>
    private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

    /// <summary>对外只读视图，最终会拷进 <c>ApiSpec.Warnings</c>。</summary>
    public IReadOnlyList<ApiWarning> Items => _items;

    /// <summary>追加一条警告；达到上限或已存在同码同位置同消息时忽略。</summary>
    /// <param name="code">缺口编号。</param>
    /// <param name="location">出现位置。</param>
    /// <param name="message">给人看的说明。</param>
    public void Add(string code, string location, string message)
    {
        if (_items.Count >= MaxWarnings || !_keys.Add(code + "\0" + location + "\0" + message))
        {
            return;
        }

        _items.Add(new ApiWarning(code, location, message));
    }
}
