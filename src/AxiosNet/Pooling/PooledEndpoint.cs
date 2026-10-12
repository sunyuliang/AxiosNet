using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AxiosNet.Pooling;

/// <summary>
/// 单个 origin（scheme://host:port）对应的池化节点：
/// 持有一个调优过的 SocketsHttpHandler 与其上的 HttpClient，
/// Handler 内部负责 TCP 连接复用、排队与连接数上限。
/// </summary>
internal sealed class PooledEndpoint : IDisposable
{
    public string Origin { get; }

    public SocketsHttpHandler Handler { get; }

    public HttpClient Client { get; }

    public int ActiveLeases { get; private set; }

    public DateTime LastUsedUtc { get; private set; } = DateTime.UtcNow;

    public bool IsDisposed { get; private set; }

    /// <summary>租约计数与释放共用的锁。</summary>
    public object SyncRoot { get; } = new();

    public PooledEndpoint(string origin, ConnectionPoolOptions options)
    {
        Origin = origin;
        Handler = CreateHandler(options);
        Client = new HttpClient(Handler, disposeHandler: false)
        {
            // 超时由 AxiosClient 按请求统一控制，避免 Handler 级超时干扰
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static SocketsHttpHandler CreateHandler(ConnectionPoolOptions o)
    {
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = o.MaxConnectionsPerServer,
            PooledConnectionLifetime = o.PooledConnectionLifetime,
            PooledConnectionIdleTimeout = o.PooledConnectionIdleTimeout,
            ConnectTimeout = o.ConnectTimeout,
            AllowAutoRedirect = o.AllowAutoRedirect,
            MaxAutomaticRedirections = o.MaxAutomaticRedirections,
            AutomaticDecompression = o.AutomaticDecompression
                ? DecompressionMethods.All
                : DecompressionMethods.None,
            UseCookies = o.UseCookies,
            UseProxy = o.Proxy is not null,
            Proxy = o.Proxy,
            Credentials = o.Credentials
        };

        if (o.UseCookies)
        {
            handler.CookieContainer = new CookieContainer();
        }

        if (o.ServerCertificateCustomValidationCallback is not null || o.EnabledSslProtocols is not null)
        {
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = o.ServerCertificateCustomValidationCallback,
                EnabledSslProtocols = o.EnabledSslProtocols ?? SslProtocols.None
            };
        }

        return handler;
    }

    /// <summary>在 SyncRoot 下调用；返回 false 表示节点已被驱逐。</summary>
    public bool TryAcquire()
    {
        lock (SyncRoot)
        {
            if (IsDisposed)
            {
                return false;
            }

            ActiveLeases++;
            LastUsedUtc = DateTime.UtcNow;
            return true;
        }
    }

    public void Release()
    {
        lock (SyncRoot)
        {
            if (ActiveLeases > 0)
            {
                ActiveLeases--;
            }

            LastUsedUtc = DateTime.UtcNow;
        }
    }

    public void Dispose()
    {
        lock (SyncRoot)
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
        }

        try
        {
            Handler.Dispose();
        }
        catch
        {
            // 连接关闭异常不影响池关闭
        }
    }
}
