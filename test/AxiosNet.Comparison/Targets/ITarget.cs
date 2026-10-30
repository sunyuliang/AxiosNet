namespace AxiosNet.Comparison.Targets;

/// <summary>
/// 一个被测实现。只回答「怎么发一次 GET 并把 JSON 读成模型」，负载、计时、采样都由 <c>LoadRunner</c> 统一做，
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
    Task SendAsync(CancellationToken cancellationToken);
}
