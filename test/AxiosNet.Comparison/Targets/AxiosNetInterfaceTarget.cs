using AxiosNet.Comparison.Models;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Comparison.Targets;

/// <summary>
/// 被测对象：AxiosNet 接口式入口。接口代理在构造期建一次，全部 worker 复用；
/// 与强类型入口共享同一个 <c>User</c> 负载，测量的差别只来自「接口参数绑定」这一层。
/// </summary>
internal sealed class AxiosNetInterfaceTarget : IAxiosNetTarget
{
    private readonly AxiosClient _client;
    private readonly ISaveUserApi _api;

    public AxiosNetInterfaceTarget(string baseUrl)
    {
        _client = new AxiosClient(new AxiosConfig { BaseAddress = baseUrl });
        _api = _client.Create<ISaveUserApi>();
    }

    public string Name => "AxiosNet";

    public ApiStyle? Style => ApiStyle.Interface;

    /// <summary>为一个 worker 开一条会话；接口代理与客户端都由全部 worker 共用。</summary>
    public ITargetSession OpenSession() => new Session(_api);

    /// <summary>释放客户端与它的连接池。</summary>
    public void Dispose() => _client.Dispose();

    private sealed class Session(ISaveUserApi api) : ITargetSession
    {
        /// <summary>调代理方法发一次 POST；正文来自 <c>[RootBody]</c> 参数，读回 <c>User</c>。</summary>
        public async Task SendAsync(CancellationToken cancellationToken)
        {
            var response = await api.SaveUserAsync(Payloads.Request, cancellationToken).ConfigureAwait(false);
            if (response.Data is null)
            {
                throw new InvalidOperationException($"AxiosNet 接口式返回空数据：status={response.Status}。");
            }
        }

        /// <summary>会话不独占资源（连接池在共享客户端上），无需释放。</summary>
        public void Dispose()
        {
        }
    }
}

/// <summary>接口式入口的声明：路由与方法绑定写在方法 / 参数上，返回形状是 <c>Task&lt;AxiosResponse&lt;T&gt;&gt;</c>。</summary>
internal interface ISaveUserApi
{
    /// <summary>POST 一个 <see cref="User"/> 负载并读回 <see cref="User"/>（<c>[RootBody]</c> 表示该参数独占正文）。</summary>
    [Post("/" + Payloads.Route)]
    Task<AxiosResponse<User>> SaveUserAsync([RootBody] User payload, CancellationToken cancellationToken = default);
}
