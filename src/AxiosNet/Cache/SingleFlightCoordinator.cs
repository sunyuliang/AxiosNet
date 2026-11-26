using System.Collections.Concurrent;

namespace AxiosNet.Cache;

/// <summary>同键并发只发一次的登记簿：键 → 领导者那一轮。领导者负责发送与落库，跟随者只等它的结果。</summary>
internal sealed class SingleFlightCoordinator
{
    private readonly ConcurrentDictionary<string, AxiosCacheFlight> _flights = new(StringComparer.Ordinal);

    /// <summary>登记一次发送：返回的实例要么是领导者（负责发送并落库），要么是等待同一结果的跟随者。</summary>
    internal AxiosCacheFlight Join(string key)
    {
        if (_flights.TryGetValue(key, out var running))
        {
            return new AxiosCacheFlight(running.Completion);
        }

        var leader = new AxiosCacheFlight();
        var existing = _flights.GetOrAdd(key, leader);

        return ReferenceEquals(existing, leader) ? leader : new AxiosCacheFlight(existing.Completion);
    }

    /// <summary>领导者成功：落库已完成（或这次不可缓存），放行跟随者。</summary>
    internal void Complete(string key, AxiosCacheFlight flight, AxiosShortCircuit? reply)
    {
        Remove(key, flight);
        flight.Succeed(reply);
    }

    /// <summary>领导者失败：跟随者原样拿到这个错误，不要挂在原地等。</summary>
    internal void Fail(string key, AxiosCacheFlight flight, Exception error)
    {
        Remove(key, flight);
        flight.Fail(error);
    }

    // 先摘除再放行等待者，按「键 + 实例」移除。
    private void Remove(string key, AxiosCacheFlight flight)
        => ((ICollection<KeyValuePair<string, AxiosCacheFlight>>)_flights)
            .Remove(new KeyValuePair<string, AxiosCacheFlight>(key, flight));
}
