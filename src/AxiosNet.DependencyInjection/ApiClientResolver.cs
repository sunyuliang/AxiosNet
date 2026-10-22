using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.WebAPI.DependencyInjection;

/// <summary>
/// 多站点解析器：按站点名取用 <see cref="ApiClient"/> 单例。
/// 站点由 AddApiClient 的命名重载注册，每个站点自带 BaseAddress 与底层 <see cref="AxiosClient"/>，
/// 客户端首次被取用时创建，之后每次解析同一实例；容器退出时经本解析器统一释放。
/// </summary>
public interface IApiClientResolver
{
    /// <summary>已注册的站点名，按注册顺序排列（比较区分大小写）。</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>取指定站点的 <see cref="ApiClient"/>（首次访问时创建，之后同一实例）。</summary>
    /// <param name="name">站点名（区分大小写）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">站点名未注册（消息中列出全部已注册站点）。</exception>
    ApiClient Get(string name);

    /// <summary>尝试取指定站点的 <see cref="ApiClient"/>；站点名未注册时返回 false。</summary>
    /// <param name="name">站点名（区分大小写），null 时返回 false。</param>
    /// <param name="client">取到的站点客户端；未注册时为 null。</param>
    bool TryGet(string name, [MaybeNullWhen(false)] out ApiClient client);
}

/// <summary>命名站点的注册数据（内部）：站点名 + 底层客户端默认值 + 对 ApiClient 自身的配置。</summary>
internal sealed class ApiClientDefinition
{
    public ApiClientDefinition(string name, AxiosConfig defaults, Action<ApiClient>? configure)
    {
        Name = name;
        Defaults = defaults;
        Configure = configure;
    }

    public string Name { get; }

    public AxiosConfig Defaults { get; }

    public Action<ApiClient>? Configure { get; }
}

/// <summary>
/// <see cref="IApiClientResolver"/> 的默认实现：按站点名惰性创建客户端（每站点自建底层
/// <see cref="AxiosClient"/>），并作为这些客户端的属主在容器退出时释放。
/// </summary>
internal sealed class ApiClientResolver : IApiClientResolver, IDisposable, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly ApiClientDefinition[] _definitions;
    private readonly string[] _names;
    private readonly Dictionary<string, ApiClient> _clients = new(StringComparer.Ordinal);
    private readonly List<ApiClientOwnedAxiosClient> _owned = new();
    private readonly object _gate = new();

    public ApiClientResolver(IServiceProvider services, IEnumerable<ApiClientDefinition> definitions)
    {
        _services = services;
        _definitions = definitions.ToArray();
        _names = _definitions.Select(definition => definition.Name).ToArray();
    }

    public IReadOnlyList<string> Names => _names;

    public ApiClient Get(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (TryGetCore(name, out var client))
        {
            return client;
        }

        var available = _names.Length == 0 ? "(无)" : string.Join(", ", _names);
        throw new InvalidOperationException($"未注册名为 \"{name}\" 的 ApiClient 站点。已注册的站点: {available}");
    }

    public bool TryGet(string name, [MaybeNullWhen(false)] out ApiClient client)
    {
        if (name is null)
        {
            client = null;
            return false;
        }

        return TryGetCore(name, out client);
    }

    private bool TryGetCore(string name, [MaybeNullWhen(false)] out ApiClient client)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(name, out client))
            {
                return true;
            }

            var definition = Array.Find(_definitions, d => string.Equals(d.Name, name, StringComparison.Ordinal));
            if (definition is null)
            {
                client = null;
                return false;
            }

            // 每站点自建底层客户端（与容器里的 AxiosClient 注册互不相干），由本解析器持有并在容器退出时释放
            var holder = new ApiClientOwnedAxiosClient(definition.Defaults);
            _owned.Add(holder);
            client = ApiClientFactory.Create(_services, holder.Client, definition.Configure);
            _clients[name] = client;
            return true;
        }
    }

    public void Dispose()
    {
        ApiClientOwnedAxiosClient[] holders;
        lock (_gate)
        {
            holders = _owned.ToArray();
            _owned.Clear();
        }

        foreach (var holder in holders)
        {
            holder.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        ApiClientOwnedAxiosClient[] holders;
        lock (_gate)
        {
            holders = _owned.ToArray();
            _owned.Clear();
        }

        foreach (var holder in holders)
        {
            await holder.DisposeAsync();
        }
    }
}

/// <summary>创建 <see cref="ApiClient"/> 并应用「显式 Authorization 优先、否则取容器 IAuthorizationProvider」规则。</summary>
internal static class ApiClientFactory
{
    public static ApiClient Create(IServiceProvider services, AxiosClient axiosClient, Action<ApiClient>? configure)
    {
        var client = new ApiClient(axiosClient);
        configure?.Invoke(client);

        // 显式设置的 Authorization 优先；未设置时才从容器取 IAuthorizationProvider 单例
        client.Authorization ??= services.GetService<IAuthorizationProvider>();
        return client;
    }
}
