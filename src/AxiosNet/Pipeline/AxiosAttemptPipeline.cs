using System.Collections.ObjectModel;

namespace AxiosNet.Pipeline;

/// <summary>「一次尝试」的中间件链：<b>不可变</b>，每个编辑方法都返回新实例；默认链为 <c>[Retry, Failover, Circuit, Hedging, Gate, Audit]</c>，索引 0 最外层。</summary>
/// <remarks>
/// 真正的发送（终端）恒在最内层，不在列表里，也换不掉。调用次数由位置决定：落在 <see cref="Attempt.Retry"/> 之内为每次尝试一次，
/// 再落在 <see cref="Attempt.Failover"/> / <see cref="Attempt.Hedging"/> 之内则按端点与分支相乘；最外层为每次请求一次，该位置请改用响应拦截器。
/// 没有 <see cref="InsertBefore"/> 能插到链首（会抛 <see cref="ArgumentException"/>）；链在客户端构造时编译一次并固化。
/// </remarks>
public sealed class AxiosAttemptPipeline
{
    private readonly AxiosAttemptMiddleware[] _middleware;

    // 只读视图，防止外部改写共享的 Default。
    private readonly ReadOnlyCollection<AxiosAttemptMiddleware> _view;

    private AxiosAttemptPipeline(AxiosAttemptMiddleware[] middleware)
    {
        _middleware = middleware;
        _view = new ReadOnlyCollection<AxiosAttemptMiddleware>(middleware);
    }

    /// <summary>默认链：<c>[Retry, Failover, Circuit, Hedging, Gate, Audit]</c>，顺序即语义，换序会改变行为。</summary>
    /// <remarks>审计已在默认链上，再 <c>Append(Attempt.Audit)</c> 会作为重复内置项被拒绝。</remarks>
    public static AxiosAttemptPipeline Default { get; } =
        new(new[] { Attempt.Retry, Attempt.Failover, Attempt.Circuit, Attempt.Hedging, Attempt.Gate, Attempt.Audit });

    /// <summary>链上的中间件，索引 0 最外层（只读视图，改不了）。</summary>
    public IReadOnlyList<AxiosAttemptMiddleware> Middleware => _view;

    /// <summary>整体重排：按给出的顺序建链（第一个 = 最外层）；把自定义中间件放在 <see cref="Attempt.Retry"/> 之前会变成每次请求一次。</summary>
    /// <exception cref="ArgumentNullException"><paramref name="middleware"/> 或其元素为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException">同一个内置中间件出现了多次（嵌套重试 / 双闸门几乎都是写错）。</exception>
    public static AxiosAttemptPipeline Create(params AxiosAttemptMiddleware[] middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return new AxiosAttemptPipeline(Copy(middleware));
    }

    /// <summary>追加到链尾（闸门与审计之后、发送之前）：每次尝试各调用一次，必须能并发执行、不共享可变状态。</summary>
    public AxiosAttemptPipeline Append(AxiosAttemptMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return new AxiosAttemptPipeline(Append(_middleware, middleware));
    }

    /// <summary>插到某个中间件之前。<paramref name="anchor"/> 必须是链上已有的成员。</summary>
    /// <exception cref="ArgumentException">锚点是链上第一项 —— 该位置请改用响应拦截器。</exception>
    public AxiosAttemptPipeline InsertBefore(AxiosAttemptMiddleware anchor, AxiosAttemptMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        var index = IndexOf(anchor, nameof(anchor));
        if (index == 0)
        {
            throw new ArgumentException(
                "不能把中间件插到链的最外层：那个位置每次请求只调用一次，与响应拦截器完全同构，"
                + "请改用 client.Interceptors.Response（它还额外支持 OnRejected、运行时增删与短路响应）；"
                + "确实需要整体重排请用 AxiosAttemptPipeline.Create 显式列出全部中间件。",
                nameof(anchor));
        }

        return Insert(index, middleware);
    }

    /// <summary>插到某个中间件之后。<paramref name="anchor"/> 必须是链上已有的成员。</summary>
    public AxiosAttemptPipeline InsertAfter(AxiosAttemptMiddleware anchor, AxiosAttemptMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return Insert(IndexOf(anchor, nameof(anchor)) + 1, middleware);
    }

    /// <summary>替换链上的某个中间件（也可用 <see cref="Remove"/> 直接摘掉）。</summary>
    public AxiosAttemptPipeline Replace(AxiosAttemptMiddleware target, AxiosAttemptMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);

        var copy = (AxiosAttemptMiddleware[])_middleware.Clone();
        copy[IndexOf(target, nameof(target))] = middleware;
        RejectDuplicateBuiltIns(copy);
        return new AxiosAttemptPipeline(copy);
    }

    /// <summary>摘掉链上的某个中间件（例如 <c>Remove(Attempt.Gate)</c> 关掉并发闸门）。允许摘空。</summary>
    public AxiosAttemptPipeline Remove(AxiosAttemptMiddleware target)
    {
        var index = IndexOf(target, nameof(target));
        var copy = new AxiosAttemptMiddleware[_middleware.Length - 1];
        Array.Copy(_middleware, 0, copy, 0, index);
        Array.Copy(_middleware, index + 1, copy, index, _middleware.Length - index - 1);
        return new AxiosAttemptPipeline(copy);
    }

    /// <summary>把链编译成单个委托：索引 0 成为最外层。<b>只在客户端构造时调用一次</b>。</summary>
    internal AxiosAttemptHandler Compile(AxiosAttemptHandler terminal, AxiosConfig? config = null)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        var handler = terminal;
        for (var i = _middleware.Length - 1; i >= 0; i--)
        {
            handler = _middleware[i](handler)
                ?? throw AxiosException.InvalidOption(
                    $"管线的第 {i} 个中间件（索引 0 为最外层）返回了 null："
                    + "AxiosAttemptMiddleware 必须返回一个 AxiosAttemptHandler。",
                    config);
        }

        return handler;
    }

    // 定位锚点；找不到时按参数名报错。
    private int IndexOf(AxiosAttemptMiddleware anchor, string paramName)
    {
        if (anchor is null)
        {
            throw new System.ArgumentNullException(paramName);
        }

        var index = Array.IndexOf(_middleware, anchor);
        if (index < 0)
        {
            throw new ArgumentException(
                "锚点中间件不在当前链上：请先确认它没被 Replace / Remove 掉；"
                + "内置项请用 Attempt.Retry / Attempt.Failover / Attempt.Hedging / Attempt.Circuit / Attempt.Gate 引用。",
                paramName);
        }

        return index;
    }

    // 插入并返回新实例。
    private AxiosAttemptPipeline Insert(int index, AxiosAttemptMiddleware middleware)
    {
        var copy = new AxiosAttemptMiddleware[_middleware.Length + 1];
        Array.Copy(_middleware, 0, copy, 0, index);
        copy[index] = middleware;
        Array.Copy(_middleware, index, copy, index + 1, _middleware.Length - index);
        RejectDuplicateBuiltIns(copy);
        return new AxiosAttemptPipeline(copy);
    }

    // 逐项复制并校验，返回新数组。
    private static AxiosAttemptMiddleware[] Copy(AxiosAttemptMiddleware[] middleware)
    {
        var copy = new AxiosAttemptMiddleware[middleware.Length];
        for (var i = 0; i < middleware.Length; i++)
        {
            copy[i] = middleware[i] ?? throw new System.ArgumentNullException(nameof(middleware), $"第 {i} 个中间件是 null。");
        }

        RejectDuplicateBuiltIns(copy);
        return copy;
    }

    // 追加到末尾并返回新实例。
    private static AxiosAttemptMiddleware[] Append(AxiosAttemptMiddleware[] source, AxiosAttemptMiddleware middleware)
    {
        var copy = new AxiosAttemptMiddleware[source.Length + 1];
        Array.Copy(source, copy, source.Length);
        copy[source.Length] = middleware;
        RejectDuplicateBuiltIns(copy);
        return copy;
    }

    /// <summary>内置项重复出现视为错误；自定义中间件重复不做限制。</summary>
    private static void RejectDuplicateBuiltIns(AxiosAttemptMiddleware[] middleware)
    {
        for (var i = 0; i < middleware.Length; i++)
        {
            if (!IsBuiltIn(middleware[i]))
            {
                continue;
            }

            for (var j = i + 1; j < middleware.Length; j++)
            {
                if (ReferenceEquals(middleware[i], middleware[j]))
                {
                    throw new ArgumentException(
                        $"同一个内置中间件在链上出现了多次（位置 {i} 与 {j}）："
                        + "嵌套重试 / 双闸门 / 双审计 / 双对冲几乎一定不是你想要的，请检查编排。",
                        nameof(middleware));
                }
            }
        }
    }

    // 是否内置中间件。
    private static bool IsBuiltIn(AxiosAttemptMiddleware middleware)
        => ReferenceEquals(middleware, Attempt.Retry)
           || ReferenceEquals(middleware, Attempt.Failover)
           || ReferenceEquals(middleware, Attempt.Hedging)
           || ReferenceEquals(middleware, Attempt.Circuit)
           || ReferenceEquals(middleware, Attempt.Gate)
           || ReferenceEquals(middleware, Attempt.Audit);
}
