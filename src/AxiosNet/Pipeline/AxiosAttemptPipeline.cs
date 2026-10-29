using System.Collections.ObjectModel;

namespace AxiosNet.Pipeline;

/// <summary>
/// 「一次尝试」的中间件链：<b>不可变</b>，每个编辑方法都返回新实例。
/// 默认链是 <c>[Retry, Failover, Circuit, Hedging, Gate]</c>，索引 0 最外层、末位最内层；
/// 真正的发送（终端）恒在最内层，不在列表里，也换不掉。
/// </summary>
/// <remarks>
/// 调用次数由位置决定：落在 <see cref="Attempt.Retry"/> 之内 = 每次尝试一次（重试 N 次则 N+1 次）；
/// 落在它之外 = 每次请求一次，该位置请改用响应拦截器（本类型不提供把中间件放到最外层的方法）。
/// 链在客户端构造时编译一次并固化，中间件里按请求分支请读 <see cref="AxiosAttemptContext.Config"/>。
/// </remarks>
public sealed class AxiosAttemptPipeline
{
    private readonly AxiosAttemptMiddleware[] _middleware;

    // 对外只给只读视图：直接交出数组会被强转回数组改元素，改坏全局共享的 Default。
    private readonly ReadOnlyCollection<AxiosAttemptMiddleware> _view;

    private AxiosAttemptPipeline(AxiosAttemptMiddleware[] middleware)
    {
        _middleware = middleware;
        _view = new ReadOnlyCollection<AxiosAttemptMiddleware>(middleware);
    }

    /// <summary>
    /// 默认链：<c>[Retry, Failover, Circuit, Hedging, Gate]</c>。顺序是契约：重试在最外层，每次尝试都会重过一遍
    /// 备用端点、熔断、对冲与闸门；备用端点与熔断都在对冲之外，闸门在对冲之内。
    /// 换成别的顺序会换掉这些语义。
    /// </summary>
    public static AxiosAttemptPipeline Default { get; } =
        new(new[] { Attempt.Retry, Attempt.Failover, Attempt.Circuit, Attempt.Hedging, Attempt.Gate });

    /// <summary>链上的中间件，索引 0 最外层（只读视图，改不了）。</summary>
    public IReadOnlyList<AxiosAttemptMiddleware> Middleware => _view;

    /// <summary>
    /// 整体重排：按给出的顺序建链（第一个 = 最外层）。
    /// 把自定义中间件放在 <see cref="Attempt.Retry"/> 之前会让它变成「每次请求一次」，该场景请改用响应拦截器。
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="middleware"/> 或其元素为 <c>null</c>。</exception>
    /// <exception cref="ArgumentException">同一个内置中间件出现了多次（嵌套重试 / 双闸门几乎都是写错）。</exception>
    public static AxiosAttemptPipeline Create(params AxiosAttemptMiddleware[] middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return new AxiosAttemptPipeline(Copy(middleware));
    }

    /// <summary>
    /// 追加到最内层（并发闸门之后、真正的发送之前）：<b>每次尝试各调用一次</b>，重试 N 次就是 N+1 次。
    /// 这是绝大多数自定义中间件（每次尝试的观测、审计、指标）该待的位置。
    /// </summary>
    public AxiosAttemptPipeline Append(AxiosAttemptMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        return new AxiosAttemptPipeline(Append(_middleware, middleware));
    }

    /// <summary>
    /// 插到某个中间件之前。<paramref name="anchor"/> 必须是链上已有的成员。
    /// </summary>
    /// <exception cref="ArgumentException">
    /// 锚点是链上第一项 —— 插到它前面等于放最外层，请改用响应拦截器。
    /// </exception>
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

    /// <summary>
    /// 替换链上的某个中间件（可用它把内置项换成自己的实现，或用 <see cref="Remove"/> 直接摘掉）。
    /// </summary>
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

    /// <summary>
    /// 把链编译成单个委托：索引 0 成为最外层。<b>只在客户端构造时调用一次</b>。
    /// </summary>
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

    private int IndexOf(AxiosAttemptMiddleware anchor, string paramName)
    {
        if (anchor is null)
        {
            // 用调用方的公共参数名（anchor / target），不固定成 nameof(anchor)。
            throw new ArgumentNullException(paramName);
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

    private AxiosAttemptPipeline Insert(int index, AxiosAttemptMiddleware middleware)
    {
        var copy = new AxiosAttemptMiddleware[_middleware.Length + 1];
        Array.Copy(_middleware, 0, copy, 0, index);
        copy[index] = middleware;
        Array.Copy(_middleware, index, copy, index + 1, _middleware.Length - index);
        RejectDuplicateBuiltIns(copy);
        return new AxiosAttemptPipeline(copy);
    }

    private static AxiosAttemptMiddleware[] Copy(AxiosAttemptMiddleware[] middleware)
    {
        var copy = new AxiosAttemptMiddleware[middleware.Length];
        for (var i = 0; i < middleware.Length; i++)
        {
            copy[i] = middleware[i] ?? throw new ArgumentNullException(nameof(middleware), $"第 {i} 个中间件是 null。");
        }

        RejectDuplicateBuiltIns(copy);
        return copy;
    }

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

    private static bool IsBuiltIn(AxiosAttemptMiddleware middleware)
        => ReferenceEquals(middleware, Attempt.Retry)
           || ReferenceEquals(middleware, Attempt.Failover)
           || ReferenceEquals(middleware, Attempt.Hedging)
           || ReferenceEquals(middleware, Attempt.Circuit)
           || ReferenceEquals(middleware, Attempt.Gate)
           || ReferenceEquals(middleware, Attempt.Audit);
}
