
using System.Security.Cryptography;
using System.Text;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;
using AxiosNet.Cache;

namespace AxiosNet.Tests.Caching;

/// <summary>响应缓存：命中不发请求、原始字节按每次调用重新解析、同键并发只发一次。</summary>
public sealed partial class CacheTests
{
    // 各文件共用的夹具：缓存实例、正文摘要、条目构造与小型存储替身。
    private static AxiosCache NewCache(Action<AxiosCacheOptions>? configure = null)
    {
        var options = new AxiosCacheOptions();
        configure?.Invoke(options);
        return new AxiosCache(options);
    }

    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)).AsSpan(0, 8));

    private static AxiosShortCircuit Entry(string body)
        => new() { Status = 200, ContentType = "text/plain", Body = Encoding.UTF8.GetBytes(body) };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("等待条件超时。");
    }

    [Get("person")]
    private sealed class PersonRequest : IApiRequest<Person>
    {
    }

    /// <summary>记录读写的存储：用来验证管线确实走的是接口，而不是内置字典。</summary>
    private sealed class RecordingStore : IAxiosCacheStore
    {
        private readonly MemoryCacheStore _inner = new();

        public List<string> Gets { get; } = new();

        public List<string> Sets { get; } = new();

        public List<AxiosCacheEntryPolicy> Policies { get; } = new();

        public Func<ValueTask<AxiosShortCircuit?>>? OnGet { get; set; }

        public Func<ValueTask>? OnSet { get; set; }

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
        {
            Gets.Add(key);
            return OnGet is { } onGet ? onGet() : _inner.GetAsync(key, cancellationToken);
        }

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            Sets.Add(key);
            return OnSet is { } onSet ? onSet() : _inner.SetAsync(key, entry, timeToLive, cancellationToken);
        }

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
        {
            Sets.Add(key);
            Policies.Add(policy);
            return OnSet is { } onSet ? onSet() : _inner.SetAsync(key, entry, policy.TimeToLive, cancellationToken);
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => _inner.RemoveAsync(key, cancellationToken);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => _inner.ClearAsync(cancellationToken);
    }

    /// <summary>只实现旧四方法签名的存储：验证新重载的默认实现仍然把写入接住。</summary>
    private sealed class LegacyStore : IAxiosCacheStore
    {
        private readonly MemoryCacheStore _inner = new();

        public TimeSpan LastTimeToLive { get; private set; }

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => _inner.GetAsync(key, cancellationToken);

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
        {
            LastTimeToLive = timeToLive;
            return _inner.SetAsync(key, entry, timeToLive, cancellationToken);
        }

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken)
            => _inner.RemoveAsync(key, cancellationToken);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => _inner.ClearAsync(cancellationToken);
    }

    /// <summary>写入永远不完成的存储：验证 <see cref="AxiosCacheOptions.StoreTimeout"/> 不会拖住响应。</summary>
    private sealed class HangingStore : IAxiosCacheStore
    {
        private readonly TaskCompletionSource<bool> _never = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<AxiosShortCircuit?> GetAsync(string key, CancellationToken cancellationToken)
            => new((AxiosShortCircuit?)null);

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, TimeSpan timeToLive, CancellationToken cancellationToken)
            => new();

        public ValueTask SetAsync(string key, AxiosShortCircuit entry, AxiosCacheEntryPolicy policy, CancellationToken cancellationToken)
            => new(_never.Task);

        public ValueTask<bool> RemoveAsync(string key, CancellationToken cancellationToken) => new(false);

        public ValueTask ClearAsync(CancellationToken cancellationToken) => default;
    }

    private static Dictionary<string, IEnumerable<string>> Headers(params (string Name, string Value)[] pairs)
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in pairs)
        {
            headers[name] = new[] { value };
        }

        return headers;
    }

    /// <summary>一碰就抛的响应头：用来证明某条路径根本没有读取响应头。</summary>
    private sealed class ThrowingHeaders : IReadOnlyDictionary<string, IEnumerable<string>>
    {
        private static InvalidOperationException Touched() => new("响应头被读取了。");

        public IEnumerable<string> this[string key] => throw Touched();

        public IEnumerable<string> Keys => throw Touched();

        public IEnumerable<IEnumerable<string>> Values => throw Touched();

        public int Count => throw Touched();

        public bool ContainsKey(string key) => throw Touched();

        public bool TryGetValue(string key, out IEnumerable<string> value) => throw Touched();

        public IEnumerator<KeyValuePair<string, IEnumerable<string>>> GetEnumerator() => throw Touched();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static Dictionary<string, IEnumerable<string>> HeaderLines(params (string Name, string[] Values)[] pairs)
    {
        var headers = new Dictionary<string, IEnumerable<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in pairs)
        {
            headers[name] = values;
        }

        return headers;
    }
}
