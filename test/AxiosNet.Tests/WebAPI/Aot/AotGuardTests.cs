using System.Dynamic;
using System.Linq.Expressions;
using System.Text;
using AxiosNet;
using AxiosNet.Internal;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Internal;
using AxiosNet.WebAPI.Metadata;

namespace AxiosNet.Tests.WebAPI.Aot;

/// <summary>AOT 守卫：依赖运行时代码生成的入口在 Native AOT 下给出可读的显式失败，而不是随机 NullReferenceException。</summary>
[Collection(GlobalSerializationCollection.Name)]
public sealed class AotGuardTests
{
    [Fact]
    public void XmlSerializer_WhenDynamicCodeIsDisabled_ThrowsPlatformNotSupported()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var error = Assert.Throws<PlatformNotSupportedException>(
                () => XmlContentSerializer.Instance.Serialize(new object(), typeof(object), new AxiosConfig()));

            Assert.Contains("Native AOT", error.Message);
            Assert.Contains("IContentSerializer", error.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void DynamicMemberAccess_WhenDynamicCodeIsDisabled_ThrowsPlatformNotSupported()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var value = new AxiosDynamic { ["name"] = "n" };
            var provider = (IDynamicMetaObjectProvider)value;
            var parameter = Expression.Parameter(typeof(AxiosDynamic), "self");

            var error = Assert.Throws<PlatformNotSupportedException>(() => provider.GetMetaObject(parameter));

            Assert.Contains("Native AOT", error.Message);

            // 索引访问与字典接口不依赖 DLR：守卫生效时仍然可用。
            Assert.Equal("n", value["name"]);
            Assert.True(value.TryGetValue("name", out var read));
            Assert.Equal("n", read);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void MissingRequestModel_ReportsTypeAndRepairOptions()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            using var client = new AxiosNet.WebAPI.ApiClient("http://127.0.0.1:1/");

            var error = Assert.Throws<AxiosAotMetadataException>(
                () => client.GetUri(new GuardUnregisteredRequest()));

            Assert.Contains(nameof(GuardUnregisteredRequest), error.Message);
            Assert.Contains("源生成器", error.Message);
            Assert.Contains("RegisterRequestModel", error.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void MissingParamsShape_PropagatesAotMetadataExceptionInsteadOfSerializerError()
    {
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            using var client = new AxiosClient(new AxiosConfig { BaseAddress = "http://example.com/" });

            // 装箱成 object 的 params 不在生成器数据流里：缺形状应原样抛出，不能包成「paramsSerializer 抛出了异常」。
            var error = Assert.Throws<AxiosAotMetadataException>(() => client.GetUri(c =>
            {
                c.Url = "x";
                c.Params = (object)new GuardMissingShapeParams();
            }));

            Assert.Contains(nameof(GuardMissingShapeParams), error.Message);
            Assert.Contains("缺少对象形状元数据", error.Message);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void StreamVisit_TreatsCustomMapAsBestEffort_WhenNoAccessorExistsUnderAot()
    {
        var map = new GuardUnregisteredMap();
        var visited = 0;

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            // 回收路径：没有映射访问器时停止下钻，不能把 MissingMap 抛给调用方。
            MultipartOwnership.VisitStreams(map, _ => visited++);
            Assert.Equal(0, visited);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }

        // JIT 下按反射枚举键值对（值类型字典走映射通道）：同样不抛错。
        MultipartOwnership.VisitStreams(map, _ => visited++);
        Assert.Equal(0, visited);
    }

    [Fact]
    public void StreamVisit_DescendsIntoGenericObjectDictionaries_WithoutReflection()
    {
        IDictionary<string, object?> map = new ExpandoObject();
        map["file"] = new FormFile("note.txt", Encoding.UTF8.GetBytes("hello"), "text/plain");

        var visited = 0;
        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            // ExpandoObject 只实现泛型字典：AOT 下走字典通道（不走反射），里面的流必须能找到。
            MultipartOwnership.VisitStreams(map, _ => visited++);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }

        Assert.Equal(1, visited);
    }

    [Fact]
    public void MapAccessor_ResolvesThroughBaseTypes()
    {
        // 生成器按「声明类型」注册访问器：声明是基类、运行期拿到派生实例时必须沿基类链命中。
        AxiosMetadata.RegisterMap(
            typeof(GuardBaseBag),
            bag => ((GuardBaseBag)bag).Select(static entry => new KeyValuePair<string, object?>(entry.Key, entry.Value)));

        RuntimeSupport.ForceNoDynamicCode = true;
        try
        {
            var entry = Assert.Single(QueryMap.Enumerate(new GuardDerivedBag()));
            Assert.Equal("k", entry.Key);
            Assert.Equal(1, entry.Value);
        }
        finally
        {
            RuntimeSupport.ForceNoDynamicCode = false;
        }
    }

    [Fact]
    public void MapEntriesWithoutKeys_AreSkippedDuringFlattening()
    {
        AxiosMetadata.RegisterMap(
            typeof(GuardNullKeyMap),
            _ => new[]
            {
                new KeyValuePair<string, object?>(null!, 1),
                new KeyValuePair<string, object?>("k", 2),
            });

        var entry = Assert.Single(QueryMap.Enumerate(new GuardNullKeyMap()));
        Assert.Equal("k", entry.Key);
        Assert.Equal(2, entry.Value);
    }
}

/// <summary>装箱成 object 的参数：生成器看不到类型，AOT 下必须显式报缺形状。</summary>
public sealed class GuardMissingShapeParams
{
    public int A { get; set; }
}

/// <summary>测试项目不跑生成器：没有映射访问器的值类型字典，用于回收路径的尽力而为断言。</summary>
public sealed class GuardUnregisteredMap : IReadOnlyDictionary<string, int>
{
    private readonly Dictionary<string, int> _inner = new() { ["only"] = 1 };

    public int this[string key] => _inner[key];

    public IEnumerable<string> Keys => _inner.Keys;

    public IEnumerable<int> Values => _inner.Values;

    public int Count => _inner.Count;

    public bool ContainsKey(string key) => _inner.ContainsKey(key);

    public bool TryGetValue(string key, out int value) => _inner.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _inner.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>只实现泛型字典的基类：生成器按它注册访问器，运行期用派生实例验证沿基类命中。</summary>
public class GuardBaseBag : IReadOnlyDictionary<string, int>
{
    private readonly Dictionary<string, int> _inner = new() { ["k"] = 1 };

    public int this[string key] => _inner[key];

    public IEnumerable<string> Keys => _inner.Keys;

    public IEnumerable<int> Values => _inner.Values;

    public int Count => _inner.Count;

    public bool ContainsKey(string key) => _inner.ContainsKey(key);

    public bool TryGetValue(string key, out int value) => _inner.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _inner.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed class GuardDerivedBag : GuardBaseBag;

/// <summary>用于验证访问器给出的 null 键被跳过。</summary>
public sealed class GuardNullKeyMap : IReadOnlyDictionary<string, int>
{
    private readonly Dictionary<string, int> _inner = new() { ["k"] = 2 };

    public int this[string key] => _inner[key];

    public IEnumerable<string> Keys => _inner.Keys;

    public IEnumerable<int> Values => _inner.Values;

    public int Count => _inner.Count;

    public bool ContainsKey(string key) => _inner.ContainsKey(key);

    public bool TryGetValue(string key, out int value) => _inner.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, int>> GetEnumerator() => _inner.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

[Get("aot/guard")]
public sealed class GuardUnregisteredRequest : AxiosNet.WebAPI.IApiRequest<object>;
