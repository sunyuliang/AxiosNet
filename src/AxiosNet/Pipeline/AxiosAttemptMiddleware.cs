namespace AxiosNet.Pipeline;

/// <summary>一次尝试的处理器：拿到本次尝试要发的请求与上下文，返回本次尝试的结果。</summary>
/// <param name="request">本次尝试的请求消息；重试时除首次外都是按模板复制的新实例，中间件自建的消息由它自己释放。</param>
/// <param name="context">本次尝试的上下文（按值传递，只能用 <c>WithXxx</c> 派生副本）。</param>
/// <returns>本次尝试的结果；只能消费一次，要多次观察请先 <c>AsTask()</c>。</returns>
public delegate ValueTask<AxiosAttemptResult> AxiosAttemptHandler(
    HttpRequestMessage request,
    AxiosAttemptContext context);

/// <summary>尝试中间件：接收链上的下一环，返回自己的处理器。</summary>
/// <remarks>链在客户端构造时编译一次；中间件只能读 <see cref="AxiosAttemptContext.Config"/> 决定本次是否生效。</remarks>
public delegate AxiosAttemptHandler AxiosAttemptMiddleware(AxiosAttemptHandler next);
