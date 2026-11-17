namespace AxiosNet.Comparison.Targets;

/// <summary>
/// 测量场景：一次 GET 把响应正文读成什么。两个场景走同一条网络路径，
/// 差别只在正文最终变成字符串还是 <c>User</c> 模型，相减即反序列化成本。
/// </summary>
internal enum ResponseKind
{
    /// <summary>正文只解码成字符串，不做反序列化（各库的「取原始文本」入口）。</summary>
    String,

    /// <summary>正文反序列化成 <c>User</c> 模型（各库的惯用 JSON 绑定入口）。</summary>
    Json
}

/// <summary>
/// 一个被测实现。只回答「怎么按指定场景发一次 GET 并交出正文」，负载、计时、采样都由 <c>LoadRunner</c> 统一做，
/// 保证除被测库本身外没有任何一侧多干活。
/// </summary>
internal interface ITarget : IDisposable
{
    /// <summary>表里显示的名字（保持 ASCII，方便对齐）。</summary>
    string Name { get; }

    /// <summary>
    /// 为一个 worker 开一条会话。HttpClient 系实现共享同一个客户端实例（连接池本来就在实例上），
    /// 只有裸 socket 基线需要「一条会话 = 一条连接」。
    /// </summary>
    ITargetSession OpenSession();
}

/// <summary>单个 worker 的请求入口；<see cref="IDisposable"/> 用于释放该 worker 独占的连接。</summary>
internal interface ITargetSession : IDisposable
{
    /// <summary>按 <paramref name="kind"/> 发一次 GET 并消费正文；正文不合预期时抛异常，由窗口统计记成错误。</summary>
    Task SendAsync(ResponseKind kind, CancellationToken cancellationToken);
}
