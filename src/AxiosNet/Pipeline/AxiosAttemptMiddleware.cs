namespace AxiosNet.Pipeline;

/// <summary>
/// 一次尝试的处理器：拿到本次尝试要发的请求消息与上下文，返回本次尝试的结果。
/// </summary>
/// <param name="request">
/// 本次尝试的请求消息。重试时每一跳都是按模板复制出来的新实例，只有第一次是模板本身；
/// 中间件若自己新建了消息再往下传，就由它自己负责释放。
/// </param>
/// <param name="context">本次尝试的上下文（按值传递，中间件只能用 <c>WithXxx</c> 派生副本）。</param>
/// <returns>本次尝试的结果；不调用 <c>next</c> 就等于跳过后续所有环节（含真正的发送）。</returns>
public delegate ValueTask<AxiosAttemptResult> AxiosAttemptHandler(
    HttpRequestMessage request,
    AxiosAttemptContext context);

/// <summary>
/// 尝试中间件：接收链上的下一环，返回自己的处理器。典型形态是
/// <c>next =&gt; async (request, ctx) =&gt; { 前置; var r = await next(request, ctx); 后置; return r; }</c>。
/// </summary>
/// <remarks>
/// 链在客户端构造时编译一次：中间件只能读 <see cref="AxiosAttemptContext.Config"/> 决定本次是否生效，
/// 不能按配置增删链成员。按请求定制请用请求 / 响应拦截器。
/// </remarks>
public delegate AxiosAttemptHandler AxiosAttemptMiddleware(AxiosAttemptHandler next);
