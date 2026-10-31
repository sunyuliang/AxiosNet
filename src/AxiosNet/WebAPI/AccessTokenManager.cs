namespace AxiosNet.WebAPI;

/// <summary>
/// 访问令牌管理器：缓存令牌、到期前刷新、并发刷新只发一次（单飞）。
/// 实现 <see cref="IAccessTokenProvider"/>，直接挂到 <see cref="ApiClient.AccessTokenProvider"/> 即可。
/// </summary>
/// <remarks>
/// <para>刷新任务不跟随调用方的取消令牌：一个调用方断开不会让同批等待者一起失败，也不会白刷一次。</para>
/// <para>刷新回调抛的异常原样抛给全部等待者；返回 <c>null</c> / 空白令牌转成 <see cref="InvalidOperationException"/>。失败不缓存 —— 下一次取令牌会重新尝试。</para>
/// <para>过期时间取自 <see cref="AccessToken.ExpiresAt"/>；提前量 = min(<see cref="AccessTokenOptions.RefreshAhead"/>, 令牌寿命的一半)。</para>
/// </remarks>
/// <example>
/// <code>
/// var tokens = new AccessTokenManager(new AccessTokenOptions(AcquireAsync)
/// {
///     RefreshAsync = RefreshAsync,
///     RefreshAhead = TimeSpan.FromSeconds(30)
/// });
///
/// using var client = new ApiClient(baseAddress) { AccessTokenProvider = tokens };
/// </code>
/// </example>
public sealed class AccessTokenManager : IAccessTokenProvider, IAccessTokenRefresher, IDisposable
{
    private readonly AccessTokenOptions _options;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _gate = new();

    private AccessToken? _current;
    private bool _hasToken;
    private TimeSpan _refreshAhead;
    private Task<AccessToken>? _refreshing;
    private CancellationTokenSource? _refreshCts;
    private bool _disposed;

    /// <summary>按配置创建管理器。</summary>
    public AccessTokenManager(AccessTokenOptions options)
        : this(options, clock: null)
    {
    }

    /// <summary>测试用：注入时钟以控制过期判定。</summary>
    internal AccessTokenManager(AccessTokenOptions options, Func<DateTimeOffset>? clock)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _clock = clock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>取当前可用令牌：缓存有效时直接返回，否则触发（或等待正在进行的）刷新。</summary>
    /// <exception cref="ObjectDisposedException">管理器已释放。</exception>
    /// <exception cref="OperationCanceledException">等待期间 <paramref name="cancellationToken"/> 被取消；刷新本身继续，结果仍会缓存。</exception>
    public async ValueTask<string?> GetTokenAsync(ApiAuthContext context, CancellationToken cancellationToken)
    {
        Task<AccessToken> refreshing;

        lock (_gate)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(AccessTokenManager));
            }

            if (_current is { } cached && !NeedsRefresh(cached))
            {
                return cached.Value;
            }

            // 不能用 `??=`：它在 StartRefresh 返回之后才赋值，会把同步完成时 finally 清掉的槽位重新粘上。
            refreshing = _refreshing ?? StartRefresh();
        }

        var token = await refreshing.WaitAsync(cancellationToken).ConfigureAwait(false);
        return token.Value;
    }

    /// <summary>作废当前令牌：下一次取令牌会重新获取（已有刷新在途时等它完成）。401 之后调用。</summary>
    /// <param name="staleToken">只作废这个令牌值（并发下避免把别人刚刷新的新令牌也作废）；<c>null</c> 表示无条件作废。</param>
    public void Invalidate(string? staleToken = null)
    {
        lock (_gate)
        {
            if (staleToken is null || string.Equals(_current?.Value, staleToken, StringComparison.Ordinal))
            {
                _current = null;
            }
        }
    }

    /// <summary>释放：取消正在进行的刷新。之后再取令牌抛 <see cref="ObjectDisposedException"/>。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _refreshCts?.Cancel();
            _current = null;
        }
    }

    /// <summary>当前令牌是否需要刷新（已过期或进入提前量窗口）。</summary>
    private bool NeedsRefresh(AccessToken token)
    {
        if (token.ExpiresAt is not { } expiresAt)
        {
            return false;
        }

        return _clock() >= expiresAt - _refreshAhead;
    }

    /// <summary>
    /// 在 <see cref="_gate"/> 内启动一次刷新：内部令牌源独立于调用方，超时由配置控制。
    /// 先占位再启动 —— 回调可能同步完成，若等 <c>async</c> 方法返回再赋值，<c>finally</c> 会先清掉槽位，
    /// 随后这个已完成的 Task 又粘回字段，下一次取令牌就会拿到旧结果。
    /// </summary>
    private Task<AccessToken> StartRefresh()
    {
        var tcs = new TaskCompletionSource<AccessToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshing = tcs.Task;

        var cts = new CancellationTokenSource();
        if (_options.RefreshTimeout is { } timeout)
        {
            cts.CancelAfter(timeout);
        }

        _refreshCts = cts;

        // 从未成功取过令牌才走 Acquire；之后一律走 Refresh（Invalidate 不重置这个状态）。
        var acquire = !_hasToken;

        // 异常全部进 tcs，不留在无人等待的 Task 上。
        _ = RunRefreshAsync(tcs, cts, acquire);
        return tcs.Task;
    }

    /// <summary>刷新本体：成功即替换缓存，失败清掉单飞槽位让下一次重试。</summary>
    private async Task RunRefreshAsync(TaskCompletionSource<AccessToken> tcs, CancellationTokenSource cts, bool acquire)
    {
        try
        {
            var token = acquire || _options.RefreshAsync is not { } refresh
                ? await _options.AcquireAsync(cts.Token).ConfigureAwait(false)
                : await refresh(cts.Token).ConfigureAwait(false);

            if (token is null || string.IsNullOrWhiteSpace(token.Value))
            {
                throw new InvalidOperationException("令牌回调返回了 null 或空白令牌：无法写入 Authorization 头。");
            }

            lock (_gate)
            {
                _current = token;
                _hasToken = true;
                _refreshAhead = ResolveRefreshAhead(token, _clock());
            }

            tcs.TrySetResult(token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // 释放导致的取消不是超时：分开报，否则调用方会以为 token 端点太慢。
            tcs.TrySetException(Volatile.Read(ref _disposed)
                ? new ObjectDisposedException(nameof(AccessTokenManager))
                : new TimeoutException($"刷新访问令牌超过 {_options.RefreshTimeout?.TotalSeconds:0.###} 秒。", new OperationCanceledException()));
        }
        catch (Exception ex)
        {
            tcs.TrySetException(ex);
        }
        finally
        {
            lock (_gate)
            {
                // 异常路径同样清掉：下一次取令牌重新刷新，而不是永远等一个已失败的任务。
                _refreshing = null;
                _refreshCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>提前量取「配置值与寿命一半的较小者」：短寿命令牌不会一拿到就进入刷新窗口。</summary>
    private TimeSpan ResolveRefreshAhead(AccessToken token, DateTimeOffset fetchedAt)
    {
        if (token.ExpiresAt is not { } expiresAt)
        {
            return TimeSpan.Zero;
        }

        var lifetime = expiresAt - fetchedAt;
        if (lifetime <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var half = lifetime / 2;
        return _options.RefreshAhead < half ? _options.RefreshAhead : half;
    }
}
