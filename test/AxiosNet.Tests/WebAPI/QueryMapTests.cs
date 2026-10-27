using System.Collections;
using AxiosNet.WebAPI.Internal;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 字符串键字典的识别与枚举：<see cref="QueryMap"/> 是 <c>[Query]</c> 平铺与 <c>[HeaderCollection]</c> 的共用通道。
/// 四条枚举通道（<c>IDictionary&lt;string,object&gt;</c> / <c>IReadOnlyDictionary&lt;string,object&gt;</c> /
/// 非泛型 <c>IDictionary</c> / 自定义只读字典）覆盖的是「运行时实现类型五花八门」这件事，只能逐个构造。
/// </summary>
public sealed class QueryMapTests
{
    [Fact]
    public void IsMapType_AcceptsEveryStringKeyedDictionaryShape()
    {
        Assert.True(QueryMap.IsMapType(typeof(Dictionary<string, object?>)));
        Assert.True(QueryMap.IsMapType(typeof(Dictionary<string, string>)));
        Assert.True(QueryMap.IsMapType(typeof(Dictionary<string, int>)));
        Assert.True(QueryMap.IsMapType(typeof(IReadOnlyDictionary<string, object?>)));
        Assert.True(QueryMap.IsMapType(typeof(Hashtable)));
        Assert.True(QueryMap.IsMapType(typeof(ReadOnlyObjectMap)));
        Assert.True(QueryMap.IsMapType(typeof(ReadOnlyIntMap)));
    }

    [Fact]
    public void IsMapType_RejectsTypesThatAreNotDictionaries()
    {
        Assert.False(QueryMap.IsMapType(typeof(string[])));
        Assert.False(QueryMap.IsMapType(typeof(string)));
        Assert.False(QueryMap.IsMapType(typeof(int)));
        Assert.False(QueryMap.IsMapType(typeof(List<string>)));
        Assert.False(QueryMap.IsMapType(typeof(IntSequenceMap)));

        // 非泛型 IDictionary 通道不约束键类型：Dictionary<int,string> 仍算字典（枚举时键统一 ToString()），
        // 但它是「值不是 string」的字典，所以不能用于 [HeaderCollection]。
        Assert.True(QueryMap.IsMapType(typeof(Dictionary<int, string>)));
        Assert.False(QueryMap.IsStringMapType(typeof(Dictionary<int, string>)));
    }

    [Fact]
    public void IsStringMapType_RequiresStringValues()
    {
        Assert.True(QueryMap.IsStringMapType(typeof(Dictionary<string, string>)));
        Assert.True(QueryMap.IsStringMapType(typeof(ReadOnlyStringMap)));

        // 值类型不是 string 的字典不能当 [HeaderCollection] 用
        Assert.False(QueryMap.IsStringMapType(typeof(Dictionary<string, int>)));
        Assert.False(QueryMap.IsStringMapType(typeof(Dictionary<string, object?>)));

        // 只有非泛型 IDictionary：拿不到键值对类型，按「不满足」处理
        Assert.False(QueryMap.IsStringMapType(typeof(Hashtable)));
    }

    [Fact]
    public void Enumerate_GenericObjectDictionary_KeepsValuesVerbatim()
    {
        var marker = new object();
        var map = new Dictionary<string, object?> { ["a"] = 1, ["b"] = marker, ["c"] = null };

        var entries = QueryMap.Enumerate(map).ToArray();

        Assert.Equal(3, entries.Length);
        Assert.Equal(1, entries.Single(e => e.Key == "a").Value);
        Assert.Same(marker, entries.Single(e => e.Key == "b").Value);
        Assert.Null(entries.Single(e => e.Key == "c").Value);
    }

    [Fact]
    public void Enumerate_ReadOnlyObjectDictionary_IsEnumeratedAsIs()
    {
        var entries = QueryMap.Enumerate(new ReadOnlyObjectMap()).ToArray();

        Assert.Single(entries);
        Assert.Equal("only", entries[0].Key);
        Assert.Equal(7, entries[0].Value);
    }

    [Fact]
    public void Enumerate_NonGenericDictionary_StringifiesNonStringKeys()
    {
        var map = new Hashtable { ["keep"] = "v", [42] = "number-key" };

        var entries = QueryMap.Enumerate(map).ToArray();

        Assert.Equal(2, entries.Length);
        Assert.Contains(entries, e => e.Key == "keep" && (string?)e.Value == "v");
        Assert.Contains(entries, e => e.Key == "42" && (string?)e.Value == "number-key");
    }

    [Fact]
    public void Enumerate_CustomReadOnlyDictionary_ReadsKeyValuePairByReflection()
    {
        var entries = QueryMap.Enumerate(new ReadOnlyIntMap()).ToArray();

        Assert.Equal(2, entries.Length);
        Assert.Equal("one", entries[0].Key);
        Assert.Equal(1, entries[0].Value);
        Assert.Equal("two", entries[1].Key);
        Assert.Equal(2, entries[1].Value);
    }

    [Fact]
    public void Enumerate_NonPairSequence_ThrowsInsteadOfSkipping()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => QueryMap.Enumerate(new IntSequenceMap()).ToArray());

        Assert.Contains("既不是 IDictionary", exception.Message);
        Assert.Contains("键值对序列", exception.Message);
    }

    /// <summary>只实现 <c>IReadOnlyDictionary&lt;string,object&gt;</c>：走「只读对象字典」通道。</summary>
    private sealed class ReadOnlyObjectMap : IReadOnlyDictionary<string, object?>
    {
        private readonly Dictionary<string, object?> _inner = new() { ["only"] = 7 };

        public object? this[string key] => _inner[key];

        public IEnumerable<string> Keys => _inner.Keys;

        public IEnumerable<object?> Values => _inner.Values;

        public int Count => _inner.Count;

        public bool ContainsKey(string key) => _inner.ContainsKey(key);

        public bool TryGetValue(string key, out object? value) => _inner.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>只实现 <c>IReadOnlyDictionary&lt;string,int&gt;</c>：走反射读 Key/Value 的兜底通道。</summary>
    private sealed class ReadOnlyIntMap : IReadOnlyDictionary<string, int>
    {
        private readonly List<KeyValuePair<string, int>> _items = new()
        {
            new("one", 1),
            new("two", 2)
        };

        public int this[string key] => _items.First(pair => pair.Key == key).Value;

        public IEnumerable<string> Keys => _items.Select(pair => pair.Key);

        public IEnumerable<int> Values => _items.Select(pair => pair.Value);

        public int Count => _items.Count;

        public bool ContainsKey(string key) => _items.Any(pair => pair.Key == key);

        public bool TryGetValue(string key, out int value)
        {
            foreach (var pair in _items)
            {
                if (pair.Key == key)
                {
                    value = pair.Value;
                    return true;
                }
            }

            value = 0;
            return false;
        }

        public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>只实现 <c>IReadOnlyDictionary&lt;string,string&gt;</c>：满足 <c>[HeaderCollection]</c> 的值类型要求。</summary>
    private sealed class ReadOnlyStringMap : IReadOnlyDictionary<string, string>
    {
        private readonly Dictionary<string, string> _inner = new() { ["k"] = "v" };

        public string this[string key] => _inner[key];

        public IEnumerable<string> Keys => _inner.Keys;

        public IEnumerable<string> Values => _inner.Values;

        public int Count => _inner.Count;

        public bool ContainsKey(string key) => _inner.ContainsKey(key);

        public bool TryGetValue(string key, out string value) => _inner.TryGetValue(key, out value!);

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => _inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>既不是字典也不是键值对序列：兜底通道必须报错而不是静默跳过。</summary>
    private sealed class IntSequenceMap : IEnumerable<int>
    {
        public IEnumerator<int> GetEnumerator()
        {
            yield return 1;
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
