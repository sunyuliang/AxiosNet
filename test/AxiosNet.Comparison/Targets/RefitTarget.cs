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

    /// <summary>为一个 worker 开一条会话；Refit 生成的接口只借用共享的 <see cref="HttpClient"/>。</summary>
    public ITargetSession OpenSession() => new Session(_api);

    /// <summary>释放底层 <see cref="HttpClient"/>。</summary>
    public void Dispose() => _http.Dispose();

    private sealed class Session(IUserApi api) : ITargetSession
    {
        /// <summary>调生成的接口方法发一次 GET；返回 null 视为错误。</summary>
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            var user = await api.GetUserAsync(cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                throw new InvalidOperationException("Refit 返回 null（端点应返回 User JSON）。");
            }
        }

        /// <summary>会话不独占资源，无需释放。</summary>
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
