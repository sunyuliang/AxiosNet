namespace AxiosNet.Comparison.Targets;

/// <summary>
/// 一个被测实现。只回答「怎么用本库的惯用写法发一次 POST 并读回正文」，负载、计时、采样都由 <c>LoadRunner</c> 统一做，
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
    /// <summary>
    /// 发一次 POST：把 <c>Payloads.Request</c> 序列化上送，再把响应正文反序列化成 <c>User</c>。
    /// 正文不合预期时抛异常，由窗口统计记成错误。
    /// </summary>
    Task SendAsync(CancellationToken cancellationToken);
}
