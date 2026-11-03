using AxiosNet.Comparison.Models;

namespace AxiosNet.Comparison.Targets;

/// <summary>
/// 被测对象：AxiosNet 默认实例（连接池 + 默认配置，不做任何调优）。
/// 与其它实现的差别正是「这一层管线」——同一回环服务器、同一负载、同一返回模型。
/// </summary>
internal sealed class AxiosNetTarget : ITarget
{
    private readonly AxiosClient _client;

    public AxiosNetTarget(string baseUrl)
    {
        _client = new AxiosClient(new AxiosConfig { BaseAddress = baseUrl });
    }

    public string Name => "AxiosNet";

    /// <summary>为一个 worker 开一条会话；底层客户端与连接池都在实例上，各 worker 共用。</summary>
    public ITargetSession OpenSession() => new Session(_client);

    /// <summary>释放客户端与它的连接池。</summary>
    public void Dispose() => _client.Dispose();

    private sealed class Session(AxiosClient client) : ITargetSession
    {
        /// <summary>发一次 GET 并读成 <c>User</c>；数据为空就抛出，让窗口统计记成错误。</summary>
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            var response = await client.GetAsync<User>(Payloads.Route, cancellationToken).ConfigureAwait(false);
            if (response.Data is null)
            {
                throw new InvalidOperationException($"AxiosNet 返回空数据：status={response.Status}。");
            }
        }

        /// <summary>会话不独占资源（连接池在共享客户端上），无需释放。</summary>
        public void Dispose()
        {
        }
    }
}
