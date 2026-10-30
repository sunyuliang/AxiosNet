namespace AxiosNet.WebAPI;

/// <summary>访问令牌：令牌值与过期时间（<see cref="ExpiresAt"/> 为 <c>null</c> 表示不会过期）。</summary>
public sealed class AccessToken
{
    /// <summary>创建一个访问令牌。</summary>
    /// <param name="value">令牌文本（不能为空白）。</param>
    /// <param name="expiresAt">过期时间；<c>null</c> 表示不会过期（此时只在 <see cref="AccessTokenManager.Invalidate"/> 后刷新）。</param>
    public AccessToken(string value, DateTimeOffset? expiresAt = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("令牌不能为空白。", nameof(value));
        }

        Value = value;
        ExpiresAt = expiresAt;
    }

    /// <summary>令牌文本。</summary>
    public string Value { get; }

    /// <summary>过期时间；<c>null</c> 表示不会过期。</summary>
    public DateTimeOffset? ExpiresAt { get; }
}

/// <summary>令牌管理器的配置：怎么取、怎么刷、提前多久刷。</summary>
public sealed class AccessTokenOptions
{
    private TimeSpan _refreshAhead = TimeSpan.FromMinutes(1);
    private TimeSpan? _refreshTimeout = TimeSpan.FromSeconds(30);

    /// <summary>创建配置。</summary>
    /// <param name="acquireAsync">首次取令牌（授权码 / 密码 / 客户端凭据换 token）。</param>
    public AccessTokenOptions(Func<CancellationToken, ValueTask<AccessToken>> acquireAsync)
    {
        ArgumentNullException.ThrowIfNull(acquireAsync);
        AcquireAsync = acquireAsync;
    }

    /// <summary>取令牌的回调：首次成功前每次取令牌都会调用；成功之后配置了 <see cref="RefreshAsync"/> 就改走它（刷新失败也不回退），未配置时继续用本回调。</summary>
    public Func<CancellationToken, ValueTask<AccessToken>> AcquireAsync { get; }

    /// <summary>刷新令牌的回调（通常是 <c>grant_type=refresh_token</c>）；<c>null</c> 时回落到 <see cref="AcquireAsync"/>。</summary>
    public Func<CancellationToken, ValueTask<AccessToken>>? RefreshAsync { get; init; }

    /// <summary>提前多久刷新，默认 1 分钟；实际提前量不超过令牌寿命的一半（短寿命令牌不会「刚拿到就该刷」）。</summary>
    public TimeSpan RefreshAhead
    {
        get => _refreshAhead;
        init
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(RefreshAhead), value, "提前刷新量不能为负。");
            }

            _refreshAhead = value;
        }
    }

    /// <summary>刷新本身的超时，默认 30 秒；<c>null</c> 表示不限（由回调自己控制）。超时抛 <see cref="TimeoutException"/>。</summary>
    public TimeSpan? RefreshTimeout
    {
        get => _refreshTimeout;
        init
        {
            if (value is { } timeout && timeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(RefreshTimeout), value, "刷新超时必须为正。");
            }

            _refreshTimeout = value;
        }
    }
}
