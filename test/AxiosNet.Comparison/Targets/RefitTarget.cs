using AxiosNet.Benchmarks.Infrastructure;
using AxiosNet.Comparison.Models;
using Refit;

namespace AxiosNet.Comparison.Targets;

/// <summary>
/// Refit：只走 source-generated 的 <c>RestService.ForGenerated</c>。
/// <c>RestService.For&lt;T&gt;</c> 的反射回退路径是另一档，不在同一个数字里混谈。
/// </summary>
internal sealed class RefitTarget : ITarget
{
    private readonly HttpClient _http;
    private readonly IUserApi _api;

    public RefitTarget(string baseUrl)
    {
        _http = new HttpClient(ComparableHttpClientHandler.Create())
        {
            BaseAddress = new Uri(baseUrl)
        };
        _api = RestService.ForGenerated<IUserApi>(_http);
    }

    public string Name => "Refit";

    public ITargetSession OpenSession() => new Session(_api);

    public void Dispose() => _http.Dispose();

    private sealed class Session(IUserApi api) : ITargetSession
    {
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            var user = await api.GetUserAsync(cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                throw new InvalidOperationException("Refit 返回 null（端点应返回 User JSON）。");
            }
        }

        public void Dispose()
        {
        }
    }
}

/// <summary>Refit 的服务接口（source generator 在编译期实现）。</summary>
internal interface IUserApi
{
    [Get("/" + Payloads.Route)]
    Task<User> GetUserAsync(CancellationToken cancellationToken);
}
