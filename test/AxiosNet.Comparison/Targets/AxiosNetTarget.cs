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

    public ITargetSession OpenSession() => new Session(_client);

    public void Dispose() => _client.Dispose();

    private sealed class Session(AxiosClient client) : ITargetSession
    {
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            var response = await client.GetAsync<User>(Payloads.Route, cancellationToken).ConfigureAwait(false);
            if (response.Data is null)
            {
                throw new InvalidOperationException($"AxiosNet 返回空数据：status={response.Status}。");
            }
        }

        public void Dispose()
        {
        }
    }
}
