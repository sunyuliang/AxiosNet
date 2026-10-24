using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
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
    private readonly IReadOnlyList<string> _namesReadOnly;
    private readonly Dictionary<string, ApiClient> _clients = new(StringComparer.Ordinal);
    private readonly List<ApiClientOwnedAxiosClient> _owned = new();
    private readonly object _gate = new();

    /// <summary>
    /// 每站点一把创建锁：首次解析在站点锁内完成（含用户 configure 与容器解析），
    /// 全局 <see cref="_gate"/> 只保护字典读写。这样慢 configure 或「configure 里同步等待
    /// 另一个站点解析」不会阻塞其他站点的首次解析。
    /// <para>
    /// 单向等待（A 的 configure 等 B）不再死锁；<b>A、B 互相等待仍会互锁</b> ——
    /// 两把站点锁之间没有约定锁序。configure 里做跨站点同步等待本身就是调用方要避免的写法，
    /// 这里只保证它不会把无关站点一起拖住。
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _siteGates = new(StringComparer.Ordinal);
    private int _disposed;

    public ApiClientResolver(IServiceProvider services, IEnumerable<ApiClientDefinition> definitions)
    {
        _services = services;
        _definitions = definitions.ToArray();
        _names = _definitions.Select(definition => definition.Name).ToArray();
        // 内部数组不外泄：调用方拿到 IReadOnlyList 后强转 string[] 就能改写解析器状态
        _namesReadOnly = Array.AsReadOnly(_names);
    }

    public IReadOnlyList<string> Names => _namesReadOnly;

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
        // 第一层（全局锁）：只做字典查询与定义查找，不执行任何用户代码。
        object siteGate;
        ApiClientDefinition? definition;
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                throw new ObjectDisposedException(nameof(ApiClientResolver));
            }

            if (_clients.TryGetValue(name, out client))
            {
                return true;
            }

            definition = Array.Find(_definitions, d => string.Equals(d.Name, name, StringComparison.Ordinal));
            if (definition is null)
            {
                client = null;
                return false;
            }

            siteGate = _siteGates.GetOrAdd(name, static _ => new object());
        }

        // 第二层（站点锁）：创建跑在这里 —— configure 与容器解析都是调用方代码，
        // 放在全局锁内会让慢 configure 阻塞全部站点，「configure 里同步等待另一站点解析」
        // 更是直接死锁。站点锁同时保证同一站点只创建一次。
        lock (siteGate)
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    throw new ObjectDisposedException(nameof(ApiClientResolver));
                }

                if (_clients.TryGetValue(name, out client))
                {
                    return true;
                }
            }

            // 每站点自建底层客户端（与容器里的 AxiosClient 注册互不相干），由本解析器持有并在容器退出时释放。
            // 先创建后登记：configure 抛错或 IAuthorizationProvider 解析失败（如注册成 Scoped，
            // 单例解析器从根容器取不到）时，holder 不能留在 _owned —— 每次重试都会新建一个
            // 连接池，直到容器退出才释放；失败路径当场释放。
            var holder = new ApiClientOwnedAxiosClient(definition.Defaults);

            ApiClient created;
            try
            {
                created = ApiClientFactory.Create(_services, holder.Client, definition.Configure);
            }
            catch
            {
                holder.Dispose();
                throw;
            }

            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 1)
                {
                    // 创建期间容器已退出：没人会持有这个 holder，就地释放而不登记
                    holder.Dispose();
                    throw new ObjectDisposedException(nameof(ApiClientResolver));
                }

                _owned.Add(holder);
                _clients[name] = created;
            }

            client = created;
            return true;
        }
    }

    public void Dispose()
    {
        ApiClientOwnedAxiosClient[] holders;
        lock (_gate)
        {
            Volatile.Write(ref _disposed, 1);
            // 释放后 Get 未解析过的站点会新建客户端：没有属主释放它，连接池直接泄漏
            _clients.Clear();
            holders = _owned.ToArray();
            _owned.Clear();
        }

        // 单个 holder 释放失败不能跳过其余站点：第一个抛异常就中断会让后面的连接池永久泄漏。
        // 收集首个失败（保留原始堆栈），全部释放完后再抛。
        ExceptionDispatchInfo? failure = null;
        foreach (var holder in holders)
        {
            try
            {
                holder.Dispose();
            }
            catch (Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure?.Throw();
    }

    public async ValueTask DisposeAsync()
    {
        ApiClientOwnedAxiosClient[] holders;
        lock (_gate)
        {
            Volatile.Write(ref _disposed, 1);
            _clients.Clear();
            holders = _owned.ToArray();
            _owned.Clear();
        }

        ExceptionDispatchInfo? failure = null;
        foreach (var holder in holders)
        {
            try
            {
                await holder.DisposeAsync();
            }
            catch (Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure?.Throw();
    }
}

/// <summary>创建 <see cref="ApiClient"/> 并应用「显式 Authorization 优先、否则取容器 IAuthorizationProvider」规则。</summary>
internal static class ApiClientFactory
{
    public static ApiClient Create(IServiceProvider services, AxiosClient axiosClient, Action<ApiClient>? configure)
    {
        var client = new ApiClient(axiosClient);
        configure?.Invoke(client);

        // 显式设置的 Authorization / AccessTokenProvider / ErrorFactory 优先；未设置时才从容器取单例
        client.Authorization ??= services.GetService<IAuthorizationProvider>();
        client.AccessTokenProvider ??= services.GetService<IAccessTokenProvider>();
        client.ErrorFactory ??= services.GetService<IApiErrorFactory>();
        return client;
    }
}
