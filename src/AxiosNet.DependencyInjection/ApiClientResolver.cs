using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;

namespace AxiosNet.WebAPI.DependencyInjection;

/// <summary>
/// 多站点解析器：按站点名取用 <see cref="ApiClient"/> 单例，首次访问时创建、之后复用；容器退出时统一释放。
/// </summary>
public interface IApiClientResolver
{
    /// <summary>已注册的站点名，按注册顺序排列（比较区分大小写）。</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>取指定站点的 <see cref="ApiClient"/>（首次访问时创建，之后同一实例）。</summary>
    /// <param name="name">站点名（区分大小写）。</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> 为 null。</exception>
    /// <exception cref="InvalidOperationException">站点名未注册（消息中列出全部已注册站点）。</exception>
    /// <exception cref="ObjectDisposedException">解析器已释放。</exception>
    ApiClient Get(string name);

    /// <summary>尝试取指定站点的 <see cref="ApiClient"/>；站点名未注册时返回 false。</summary>
    /// <param name="name">站点名（区分大小写），null 或解析器已释放时返回 false。</param>
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

    /// <summary>站点名：解析器里的键，注入时按它取对应客户端。</summary>
    public string Name { get; }

    /// <summary>该站点的默认配置：每站点一份，注册之间的改动不会互相污染。</summary>
    public AxiosConfig Defaults { get; }

    /// <summary>客户端创建后的最后一步配置（拦截器、默认头等）；null 表示不再额外设置。</summary>
    public Action<ApiClient>? Configure { get; }
}

/// <summary>
/// <see cref="IApiClientResolver"/> 的默认实现：按站点名惰性创建客户端（每站点自建底层 <see cref="AxiosClient"/>），
/// 并作为这些客户端的属主在容器退出时释放。
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
    /// 每站点一把创建锁：首次解析在站点锁内完成（含用户 configure 与容器解析）；全局 <see cref="_gate"/> 只保护
    /// <c>_clients</c> / <c>_owned</c> / <c>_disposed</c> 的读写，不执行用户代码。configure 里做跨站点同步等待可能互锁。
    /// </summary>
    private readonly ConcurrentDictionary<string, object> _siteGates = new(StringComparer.Ordinal);
    // 0/1 释放标志（解析器与它创建的客户端一起释放）。
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

    /// <summary>取指定站点的客户端（首次访问时才创建）；未注册时抛出并列出全部已注册站点名。</summary>
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

    /// <summary>尝试取指定站点的客户端：站点名为 null 或解析器已释放时返回 false，不抛异常。</summary>
    public bool TryGet(string name, [MaybeNullWhen(false)] out ApiClient client)
    {
        if (name is null)
        {
            client = null;
            return false;
        }

        return TryGetCore(name, out client, throwIfDisposed: false);
    }

    /// <summary>已释放时返回 <c>true</c>：Get 语义直接抛 <see cref="ObjectDisposedException"/>，TryGet 语义由调用方返回 <c>false</c>。</summary>
    private bool TryHandleDisposed(bool throwIfDisposed, out ApiClient client)
    {
        client = null!;

        if (Volatile.Read(ref _disposed) == 0)
        {
            return false;
        }

        if (throwIfDisposed)
        {
            throw new ObjectDisposedException(nameof(ApiClientResolver));
        }

        return true;
    }

    /// <summary>解析主流程：全局锁内查缓存与定义，站点锁内建客户端，创建结果再回填缓存。</summary>
    private bool TryGetCore(string name, [MaybeNullWhen(false)] out ApiClient client, bool throwIfDisposed = true)
    {
        // 第一层（全局锁）：只做字典查询与定义查找，不执行任何用户代码。
        object siteGate;
        ApiClientDefinition? definition;
        lock (_gate)
        {
            if (TryHandleDisposed(throwIfDisposed, out client))
            {
                return false;
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

        // 第二层（站点锁）：创建跑在这里，configure 与容器解析都是调用方代码。
        lock (siteGate)
        {
            lock (_gate)
            {
                if (TryHandleDisposed(throwIfDisposed, out client))
                {
                    return false;
                }

                if (_clients.TryGetValue(name, out client))
                {
                    return true;
                }
            }

            // 每站点自建底层客户端，由本解析器持有并在容器退出时释放；创建失败时 holder 当场释放，不登记。
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

                    if (throwIfDisposed)
                    {
                        throw new ObjectDisposedException(nameof(ApiClientResolver));
                    }

                    client = null!;
                    return false;
                }

                _owned.Add(holder);
                _clients[name] = created;
            }

            client = created;
            return true;
        }
    }

    /// <summary>先置释放标志并快照已建客户端，再逐个同步释放（释放逻辑不在锁内跑）。</summary>
    public void Dispose()
    {
        ApiClientOwnedAxiosClient[] holders;
        lock (_gate)
        {
            Volatile.Write(ref _disposed, 1);
            // 清空已创建的客户端：释放后不再返回底层客户端已释放的实例。
            _clients.Clear();
            holders = _owned.ToArray();
            _owned.Clear();
        }

        HolderDisposal.DisposeAll(holders);
    }

    /// <summary>异步版本：标志与快照同 <see cref="Dispose"/>，释放走异步路径以免阻塞底层连接关闭。</summary>
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

        await HolderDisposal.DisposeAllAsync(holders).ConfigureAwait(false);
    }
}

/// <summary>
/// 属主释放的失败容错循环：单个 holder 释放失败不跳过其余站点，收集首个失败后统一抛出（保留原始堆栈）。
/// </summary>
internal static class HolderDisposal
{
    /// <summary>逆序释放一组持有者：收集首个异常，其余照常释放，最后重抛。</summary>
    public static void DisposeAll(IEnumerable<IDisposable> holders)
    {
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

    /// <summary>同上，异步释放版。</summary>
    public static async ValueTask DisposeAllAsync(IEnumerable<IAsyncDisposable> holders)
    {
        ExceptionDispatchInfo? failure = null;
        foreach (var holder in holders)
        {
            try
            {
                await holder.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failure ??= ExceptionDispatchInfo.Capture(ex);
            }
        }

        failure?.Throw();
    }
}

/// <summary>创建 <see cref="ApiClient"/>：显式设置的 Authorization / AccessTokenProvider / ErrorFactory 优先，未设置时才取容器里的单例。</summary>
internal static class ApiClientFactory
{
    /// <summary>按注册数据造一个站点的客户端（底层 <see cref="AxiosClient"/> 由调用方持有）。</summary>
    public static ApiClient Create(IServiceProvider services, AxiosClient axiosClient, Action<ApiClient>? configure)
    {
        var client = new ApiClient(axiosClient);
        configure?.Invoke(client);

        // 回填发生在 configure 之后，所以显式设置天然优先；未设置的才取容器里的单例。
        client.Authorization ??= services.GetService<IAuthorizationProvider>();
        client.AccessTokenProvider ??= services.GetService<IAccessTokenProvider>();
        client.ErrorFactory ??= services.GetService<IApiErrorFactory>();
        return client;
    }
}
