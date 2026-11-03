using AxiosNet.Benchmarks.Infrastructure;
using AxiosNet.Comparison.Models;
using RestSharp;

namespace AxiosNet.Comparison.Targets;

/// <summary>
/// RestSharp：单例 <see cref="RestClient"/>，每请求新建 <see cref="RestRequest"/>（RestSharp 的常规用法；
/// RestRequest 不可复用）。Handler 注入与其它实现同一份对齐配置。
/// </summary>
internal sealed class RestSharpTarget : ITarget
{
    private readonly RestClient _client;

    public RestSharpTarget(string baseUrl)
    {
        var options = new RestClientOptions(baseUrl)
        {
            ConfigureMessageHandler = _ => ComparableHttpClientHandler.Create()
        };
        _client = new RestClient(options);
    }

    public string Name => "RestSharp";

    /// <summary>为一个 worker 开一条会话；<see cref="RestClient"/> 由全部 worker 共享。</summary>
    public ITargetSession OpenSession() => new Session(_client);

    /// <summary>释放 <see cref="RestClient"/> 及其 handler。</summary>
    public void Dispose() => _client.Dispose();

    private sealed class Session(RestClient client) : ITargetSession
    {
        /// <summary>发一次 GET；非 2xx 或没有数据都视为错误，并带上状态码与错误消息。</summary>
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            var request = new RestRequest(Payloads.Route);
            var response = await client.ExecuteAsync<User>(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessful || response.Data is null)
            {
                throw new InvalidOperationException(
                    $"RestSharp 响应异常：status={response.StatusCode}, error={response.ErrorMessage}。");
            }
        }

        /// <summary>会话不独占资源，无需释放。</summary>
        public void Dispose()
        {
        }
    }
}
