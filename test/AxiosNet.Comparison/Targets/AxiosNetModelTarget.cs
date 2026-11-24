using AxiosNet.Comparison.Models;
using AxiosNet.WebAPI;

namespace AxiosNet.Comparison.Targets;

/// <summary>
/// 被测对象：AxiosNet 模型式入口。请求模型实例在构造期建好、全部 worker 复用同一份，
/// 与强类型入口共享同一个 <c>User</c> 负载，测量的差别只来自「模型绑定」这一层。
/// </summary>
internal sealed class AxiosNetModelTarget : IAxiosNetTarget
{
    private readonly AxiosClient _client;
    private readonly SaveUserRequest _request;

    public AxiosNetModelTarget(string baseUrl)
    {
        _client = new AxiosClient(new AxiosConfig { BaseAddress = baseUrl });
        _request = new SaveUserRequest { Body = Payloads.Request };
    }

    public string Name => "AxiosNet";

    public ApiStyle? Style => ApiStyle.Model;

    /// <summary>为一个 worker 开一条会话；客户端与请求模型实例都由全部 worker 共用。</summary>
    public ITargetSession OpenSession() => new Session(_client, _request);

    /// <summary>释放客户端与它的连接池。</summary>
    public void Dispose() => _client.Dispose();

    private sealed class Session(AxiosClient client, SaveUserRequest request) : ITargetSession
    {
        /// <summary>发一次 <c>RequestAsync(请求模型)</c>；正文由请求模型的 <c>[Body]</c> 成员提供，读回 <c>User</c>。</summary>
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            var user = await client.RequestAsync(request, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                throw new InvalidOperationException("AxiosNet 模型式返回空数据。");
            }
        }

        /// <summary>会话不独占资源（连接池在共享客户端上），无需释放。</summary>
        public void Dispose()
        {
        }
    }
}
