using AxiosNet.Benchmarks.Infrastructure;
using AxiosNet.Comparison.Models;
using Flurl.Http;

namespace AxiosNet.Comparison.Targets;

/// <summary>
/// Flurl：单例 <see cref="FlurlClient"/>（Flurl 的连接复用挂在它持有的 HttpClient 上），
/// 每请求 <c>Request(url).GetJsonAsync&lt;User&gt;()</c>。
/// </summary>
internal sealed class FlurlTarget : ITarget
{
    private readonly FlurlClient _client;
    private readonly string _url;

    public FlurlTarget(string baseUrl)
    {
        _client = new FlurlClient(new HttpClient(ComparableHttpClientHandler.Create()));
        _url = baseUrl.TrimEnd('/') + "/" + Payloads.Route;
    }

    public string Name => "Flurl";

    public ITargetSession OpenSession() => new Session(_client, _url);

    public void Dispose() => _client.Dispose();

    private sealed class Session(FlurlClient client, string url) : ITargetSession
    {
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            var user = await client.Request(url)
                .GetJsonAsync<User>(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (user is null)
            {
                throw new InvalidOperationException("Flurl 返回 null（端点应返回 User JSON）。");
            }
        }

        public void Dispose()
        {
        }
    }
}
