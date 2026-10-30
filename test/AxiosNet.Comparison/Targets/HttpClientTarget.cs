using System.Net.Http.Json;
using AxiosNet.Benchmarks.Infrastructure;
using AxiosNet.Comparison.Models;

namespace AxiosNet.Comparison.Targets;

/// <summary>
/// 基线：裸 <see cref="HttpClient"/> + <c>GetFromJsonAsync&lt;User&gt;</c>。
/// Handler 用与 AxiosNet 默认连接池逐项对齐的那份（Cookies / Proxy / 连接数 / 超时全对齐），
/// 否则测的是两个库的默认值差异而不是实现差异。
/// </summary>
internal sealed class HttpClientTarget : ITarget
{
    private readonly HttpClient _client;
    private readonly string _url;

    public HttpClientTarget(string baseUrl)
    {
        _client = new HttpClient(ComparableHttpClientHandler.Create());
        _url = baseUrl.TrimEnd('/') + "/" + Payloads.Route;
    }

    public string Name => "HttpClient";

    public ITargetSession OpenSession() => new Session(_client, _url);

    public void Dispose() => _client.Dispose();

    private sealed class Session(HttpClient client, string url) : ITargetSession
    {
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            var user = await client.GetFromJsonAsync<User>(url, cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                throw new InvalidOperationException("HttpClient 返回 null（端点应返回 User JSON）。");
            }
        }

        public void Dispose()
        {
        }
    }
}
