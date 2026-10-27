using System.Collections;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Requests;

/// <summary>
/// 查询参数的边界形态：标量文本化、嵌套字典 / 数组、JsonElement 根、空值语义。
/// 与 <see cref="ParamsTests"/> 的分工是「这里只压展平与编码的细节」。
/// </summary>
public sealed class ParamsEdgeCaseTests
{
    [Fact]
    public void Params_ScalarValues_UseInvariantText()
    {
        var uri = BuildUri(new
        {
            span = TimeSpan.FromSeconds(3723),
            day = new DateOnly(2024, 1, 2),
            letter = 'x',
            time = new TimeOnly(1, 2, 3),
            flag = true
        });

        Assert.Contains("span=01%3A02%3A03", uri);
        // DateOnly 没有独立分支，落到 IFormattable 的不变文化短日期（斜杠被百分号编码），
        // 与 ModelBindingTests 记录的既有约定一致。
        Assert.Contains("day=01%2F02%2F2024", uri);
        Assert.Contains("letter=x", uri);
        Assert.Contains("time=01%3A02%3A03", uri);
        Assert.Contains("flag=true", uri);
    }

    [Fact]
    public void Params_UriValue_IsFullyEscaped()
    {
        var uri = BuildUri(new { next = new Uri("https://example.com/a?b=1") });

        Assert.Contains("next=https%3A%2F%2Fexample.com%2Fa%3Fb%3D1", uri);
    }

    [Fact]
    public void Params_NestedDictionaries_UseBracketNotation()
    {
        var uri = BuildUri(new
        {
            filter = new Dictionary<string, object?>
            {
                ["range"] = new Dictionary<string, object?> { ["from"] = 1, ["to"] = 9 }
            }
        });

        Assert.Contains("filter[range][from]=1", uri);
        Assert.Contains("filter[range][to]=9", uri);
    }

    [Fact]
    public void Params_ArrayOfObjects_UsesIndexedBrackets()
    {
        var uri = BuildUri(new
        {
            items = new[] { new { id = 1, name = "a" }, new { id = 2, name = "b" } }
        });

        Assert.Contains("items[][id]=1", uri);
        Assert.Contains("items[][name]=a", uri);
        Assert.Contains("items[][id]=2", uri);
    }

    [Fact]
    public void Params_NullValues_AreSkippedButEmptyStringsKept()
    {
        var uri = BuildUri(new { keep = "", gone = (string?)null });

        Assert.Contains("keep=", uri);
        Assert.DoesNotContain("gone", uri);
    }

    [Fact]
    public void Params_EmptyObject_AddsNoQuery()
    {
        var uri = BuildUri(new { });

        Assert.DoesNotContain("?", uri);
    }

    [Fact]
    public void Build_WithoutUrlOrBaseAddress_ThrowsInvalidUrl()
    {
        using var client = new AxiosClient(new AxiosConfig());

        var exception = Assert.Throws<AxiosException>(() => client.GetUri(new AxiosConfig()));

        Assert.Equal(AxiosException.ErrInvalidUrl, exception.Code);
    }

    [Fact]
    public void Params_ReadOnlyObjectDictionary_IsFlattenedWithoutReflectingProperties()
    {
        var uri = BuildUri(new ReadOnlyObjectDictionary
        {
            ["page"] = 2,
            ["tag"] = "news"
        });

        Assert.Contains("page=2", uri);
        Assert.Contains("tag=news", uri);
    }

    [Fact]
    public void Params_NestedReadOnlyObjectDictionary_UsesBracketNotation()
    {
        var uri = BuildUri(new
        {
            filter = new ReadOnlyObjectDictionary { ["kind"] = "book" }
        });

        Assert.Contains("filter[kind]=book", uri);
    }

    [Fact]
    public void Params_NonGenericDictionary_RootAndNestedAreFlattened()
    {
        var root = new Hashtable { ["page"] = 2 };

        Assert.Contains("page=2", BuildUri(root));

        var nested = new Hashtable { ["kind"] = "book", [42] = "number-key" };
        var uri = BuildUri(new { filter = nested });

        Assert.Contains("filter[kind]=book", uri);
        // 键统一 ToString()，非字符串键不会丢
        Assert.Contains("filter[42]=number-key", uri);
    }

    [Fact]
    public void Params_JsonElementValueInsideObject_IsExpandedWithoutReflection()
    {
        using var document = JsonDocument.Parse("""{"kind":"book","tags":["a"]}""");

        var uri = BuildUri(new { filter = document.RootElement.Clone() });

        Assert.Contains("filter[kind]=book", uri);
        Assert.Contains("filter[tags][]=a", uri);
    }

    [Fact]
    public void Params_CollectionFormatMulti_RepeatsTheSameKey()
    {
        var uri = BuildUri(new { tags = new[] { "a", "b" } }, c => c.Serialization.CollectionFormat = AxiosCollectionFormat.Multi);

        Assert.Contains("tags=a", uri);
        Assert.Contains("tags=b", uri);
        Assert.DoesNotContain("tags[]", uri);
    }

    [Theory]
    [InlineData(AxiosCollectionFormat.Csv, "tags=1%2C2")]
    [InlineData(AxiosCollectionFormat.Ssv, "tags=1%202")]
    [InlineData(AxiosCollectionFormat.Tsv, "tags=1%092")]
    [InlineData(AxiosCollectionFormat.Pipes, "tags=1%7C2")]
    public void Params_CollectionFormatSeparated_JoinsScalarsIntoOneValue(AxiosCollectionFormat format, string expected)
    {
        var uri = BuildUri(new { tags = new[] { 1, 2 } }, c => c.Serialization.CollectionFormat = format);

        Assert.Contains(expected, uri);
    }

    [Fact]
    public void Params_CollectionFormatSeparated_SkipsNullItems()
    {
        var uri = BuildUri(new { tags = new[] { "a", null, "b" } }, c => c.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        Assert.Contains("tags=a%2Cb", uri);
    }

    [Fact]
    public void Params_CollectionFormatSeparated_FallsBackToBracketsForComplexItems()
    {
        var uri = BuildUri(
            new { items = new object[] { 1, new { id = 2 } } },
            c => c.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        Assert.Contains("items[]=1", uri);
        Assert.Contains("items[][id]=2", uri);
    }

    [Fact]
    public void Params_JsonElementArrayWithMultiFormat_RepeatsTheSameKey()
    {
        using var document = JsonDocument.Parse("""{"tags":["a","b"]}""");

        var uri = BuildUri(document.RootElement.Clone(), c => c.Serialization.CollectionFormat = AxiosCollectionFormat.Multi);

        Assert.Contains("tags=a", uri);
        Assert.Contains("tags=b", uri);
    }

    [Fact]
    public void Params_JsonElementArrayWithSeparatedFormat_JoinsScalarsAndSkipsNulls()
    {
        using var document = JsonDocument.Parse("""{"tags":["a",null,"b"],"flags":[false,true]}""");

        var uri = BuildUri(document.RootElement.Clone(), c => c.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        Assert.Contains("tags=a%2Cb", uri);
        Assert.Contains("flags=false%2Ctrue", uri);
    }

    [Fact]
    public void Params_JsonElementArrayWithSeparatedFormat_FallsBackToBracketsForObjects()
    {
        using var document = JsonDocument.Parse("""{"items":[1,{"id":2}]}""");

        var uri = BuildUri(document.RootElement.Clone(), c => c.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        Assert.Contains("items[]=1", uri);
        Assert.Contains("items[][id]=2", uri);
    }

    [Fact]
    public void Params_JsonElementArrayOfNullsWithSeparatedFormat_AddsNothing()
    {
        using var document = JsonDocument.Parse("""{"tags":[null,null]}""");

        var uri = BuildUri(document.RootElement.Clone(), c => c.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        Assert.DoesNotContain("tags", uri);
    }

    [Fact]
    public void Params_JsonRootArrayWithSeparatedFormat_UsesIndexedKeys()
    {
        using var document = JsonDocument.Parse("""[{"id":1},{"id":2}]""");

        var uri = BuildUri(document.RootElement.Clone(), c => c.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        Assert.Contains("[0][id]=1", uri);
        Assert.Contains("[1][id]=2", uri);
    }

    [Fact]
    public void Params_BlankRawQuery_AddsNoQuery()
    {
        var uri = BuildUri("   ");

        Assert.DoesNotContain("?", uri);
    }

    [Fact]
    public void ParamsSerializer_ReturningBlankQuery_AddsNoQuery()
    {
        var uri = BuildUri(new { q = "a" }, c => c.Serialization.ParamsSerializer = _ => "   ");

        Assert.DoesNotContain("?", uri);
    }

    private static string BuildUri(object? parameters, Action<AxiosConfig>? configure = null)
    {
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = "https://example.com/" });

        return client.GetUri(c =>
        {
            c.Url = "search";
            c.Params = parameters;
            configure?.Invoke(c);
        });
    }

    /// <summary>只实现 <c>IReadOnlyDictionary&lt;string,object&gt;</c> 的字典（不是 <c>IDictionary</c>）：走只读字典通道。</summary>
    private sealed class ReadOnlyObjectDictionary : IReadOnlyDictionary<string, object?>
    {
        private readonly Dictionary<string, object?> _inner = new();

        public object? this[string key]
        {
            get => _inner[key];
            set => _inner[key] = value;
        }

        public IEnumerable<string> Keys => _inner.Keys;

        public IEnumerable<object?> Values => _inner.Values;

        public int Count => _inner.Count;

        public bool ContainsKey(string key) => _inner.ContainsKey(key);

        public bool TryGetValue(string key, out object? value) => _inner.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => _inner.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
