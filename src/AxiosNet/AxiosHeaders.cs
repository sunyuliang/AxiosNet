using System.Collections;

namespace AxiosNet;

/// <summary>
/// 类似 axios 的 AxiosHeaders：大小写不敏感、支持多值、支持链式调用。
/// <para>
/// 所有读写都在内部锁内完成，因此实例默认值（<see cref="Axios.Defaults"/> 等）可以在
/// 请求进行中安全地增删请求头（例如运行期刷新 Token），不会让并发的配置合并看到半成品集合。
/// 枚举与 <see cref="Clone"/> 返回快照，不持有实时视图。
/// </para>
/// <para>
/// <b>写入即校验</b>：头部名称必须是 RFC 7230 的 token（不含空格、冒号、控制字符），
/// 取值不允许出现 <c>CR</c> / <c>LF</c>。请求头最终经由
/// <see cref="HttpRequestMessage.Headers"/> 的 <c>TryAddWithoutValidation</c> 上送，
/// 那一层不做任何检查——若把外部输入直接拼进头部，<c>\r\n</c> 会被当成头部分隔符，
/// 等于给调用方开了一个 HTTP 请求拆分（CWE-113）的口子。这里提前拒绝。
/// </para>
/// </summary>
public sealed class AxiosHeaders : IEnumerable<KeyValuePair<string, string>>
{
    private readonly Dictionary<string, string> _store
        = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _sync = new();

    public AxiosHeaders()
    {
    }

    public AxiosHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var kv in headers)
        {
            Set(kv.Key, kv.Value);
        }
    }

    /// <summary>设置/覆盖请求头，返回当前对象以支持链式调用。</summary>
    /// <exception cref="ArgumentException">名称不是合法 HTTP token，或取值含 CR/LF。</exception>
    public AxiosHeaders Set(string name, string? value)
    {
        var key = ValidateName(name);

        // null 表示删除，不校验取值
        if (value is not null)
        {
            ValidateValue(key, value);
        }

        lock (_sync)
        {
            if (value is null)
            {
                _store.Remove(key);
            }
            else
            {
                _store[key] = value;
            }
        }

        return this;
    }

    /// <summary>批量合并请求头。</summary>
    public AxiosHeaders Set(IEnumerable<KeyValuePair<string, string?>> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        foreach (var kv in headers)
        {
            Set(kv.Key, kv.Value);
        }

        return this;
    }

    public string? Get(string name)
    {
        lock (_sync)
        {
            return _store.TryGetValue(NormalizeName(name), out var value) ? value : null;
        }
    }

    public bool Has(string name)
    {
        lock (_sync)
        {
            return _store.ContainsKey(NormalizeName(name));
        }
    }

    public bool Delete(string name)
    {
        lock (_sync)
        {
            return _store.Remove(NormalizeName(name));
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _store.Clear();
        }
    }

    public string? this[string name]
    {
        get => Get(name);
        set => Set(name, value);
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _store.Count;
            }
        }
    }

    public AxiosHeaders Clone() => new(Snapshot());

    /// <summary>用另一个 headers 覆盖合并当前对象（另一个对象优先）。</summary>
    internal void MergeFrom(AxiosHeaders? other)
    {
        if (other is null)
        {
            return;
        }

        // 先取快照，避免两个实例互相合并时持锁顺序相反造成死锁。
        var snapshot = other.Snapshot();
        lock (_sync)
        {
            foreach (var kv in snapshot)
            {
                _store[kv.Key] = kv.Value;
            }
        }
    }

    private static string NormalizeName(string name) => name.Trim();

    /// <summary>写入前的名称校验：RFC 7230 token（RFC 9110 §5.1 同）。</summary>
    private static string ValidateName(string name)
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace 是 .NET 8 才有的 API，这里手写以兼容 net6.0 目标。
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", nameof(name));
        }

        // 先按原始串拒绝控制字符（含 CR/LF）：名字尾部带 \r\n 会被 Trim 悄悄吃掉，
        // 那正是注入尝试留下的痕迹，不该被「修剪」掉再照常发出去。
        foreach (var c in name)
        {
            if (c < 0x20 || c == 0x7F)
            {
                throw new ArgumentException(
                    $"非法 HTTP 请求头名称（含控制字符）：'{Escape(name)}'。",
                    nameof(name));
            }
        }

        var trimmed = name.Trim();
        foreach (var c in trimmed)
        {
            if (!IsTokenChar(c))
            {
                throw new ArgumentException(
                    $"非法 HTTP 请求头名称 '{name}'：只允许 RFC 7230 token 字符（字母/数字与 !#$%&'*+-.^_`|~），"
                    + "不允许空格、冒号或控制字符。",
                    nameof(name));
            }
        }

        return trimmed;
    }

    /// <summary>把控制字符转义成可见形式，便于错误信息与日志排查。</summary>
    private static string Escape(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7F)
            {
                builder.Append("\\u").Append(((int)c).ToString("X4"));
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// 写入前的取值校验：拒绝 CR/LF。
    /// 这两个字符一旦进入头部值，会被 HTTP 序列化层当作头部分隔符，形成请求拆分。
    /// </summary>
    private static void ValidateValue(string name, string value)
    {
        foreach (var c in value)
        {
            // 不只看 \r\n：单独的 CR 或 LF 同样能截断头部行。
            if (c is '\r' or '\n' or '\0')
            {
                throw new ArgumentException(
                    $"非法 HTTP 请求头取值（头 '{name}'）：不允许包含 CR / LF / NUL 字符。"
                    + "如果这是来自外部的数据，请先剔除控制字符——否则可能被用于注入额外的请求头或请求体。",
                    "value");
            }
        }
    }

    private static bool IsTokenChar(char c)
        => (c >= 'a' && c <= 'z')
            || (c >= 'A' && c <= 'Z')
            || (c >= '0' && c <= '9')
            || c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';

    private List<KeyValuePair<string, string>> Snapshot()
    {
        lock (_sync)
        {
            return new List<KeyValuePair<string, string>>(_store);
        }
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Snapshot().GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
