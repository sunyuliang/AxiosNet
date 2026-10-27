
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Net.Http;
using System.Net;
using System.Text;

namespace AxiosNet.Tests.WebAPI;

/// <summary>
/// 声明式特性：静态头、`HeaderCollection`、`QueryMap`、`CollectionFormat`、`UrlParameterFormatter`。
/// </summary>
public sealed class BindingAttributesTests
{
    // ------------------------------------------------------------------
    // 类级静态头 + 字典头
    // ------------------------------------------------------------------

    [Fact]
    public async Task StaticHeaders_AreSent_PropertyHeaderOverridesSameName()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new StaticHeaderRequest { Trace = "dynamic" });

        var received = Assert.Single(server.Received);
        Assert.Equal("application/json", received.Headers["Accept"]);
        Assert.Equal("2", received.Headers["X-Api-Version"]);
        // 属性上的 [Header] 覆盖类级同名静态头
        Assert.Equal("dynamic", received.Headers["X-Trace"]);
    }

    [Fact]
    public async Task StaticHeaders_NameOnly_RemovesDefaultHeader()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url, defaults => defaults.Headers.Set("X-Remove", "old"));

        await client.RequestAsync(new RemoveHeaderRequest());

        var received = Assert.Single(server.Received);
        Assert.False(received.Headers.ContainsKey("X-Remove"));
    }

    [Fact]
    public async Task HeaderCollection_WritesEachEntry_NullValueSkipped()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new HeaderCollectionRequest
        {
            Extra = new Dictionary<string, string?>
            {
                ["X-A"] = "1",
                ["X-B"] = null
            }
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("1", received.Headers["X-A"]);
        Assert.False(received.Headers.ContainsKey("X-B"));
    }

    [Fact]
    public void HeaderCollection_NonStringDictionary_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new BadHeaderCollectionRequest()));

        Assert.Contains("[HeaderCollection]", ex.Message);
        Assert.Contains("IDictionary<string, string>", ex.Message);
    }

    [Fact]
    public void HeaderAndHeaderCollection_OnSameProperty_IsRejected()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => new ApiClient("http://127.0.0.1:1/").GetUri(new ConflictingHeaderRequest()));

        Assert.Contains("二义性", ex.Message);
    }

    // ------------------------------------------------------------------
    // 查询映射平铺 + CollectionFormat
    // ------------------------------------------------------------------

    [Fact]
    public async Task QueryMap_FlattensDictionaryEntries_IntoTopLevelKeys()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new SearchMapRequest
        {
            Filters = new Dictionary<string, object?> { ["role"] = "admin", ["page"] = 2 }
        });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Contains("role=admin", query);
        Assert.Contains("page=2", query);
        Assert.DoesNotContain("Filters", query);
    }

    [Fact]
    public async Task QueryMap_AllNullValues_SendsNoQueryString()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new SearchMapRequest
        {
            Filters = new Dictionary<string, object?> { ["role"] = null }
        });

        Assert.Null(Assert.Single(server.Received).Query);
    }

    [Fact]
    public async Task QueryMap_ExplicitName_KeepsNestedForm()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new NamedMapRequest
        {
            Filters = new Dictionary<string, object?> { ["role"] = "admin" }
        });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Contains("filters[role]=admin", query);
    }

    [Fact]
    public async Task CollectionFormat_PerRequest_JoinsScalars()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new[] { new Person("alice", 1) }));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(
            new ListUsersRequest { Ids = [1, 2], Role = null },
            config => config.Serialization.CollectionFormat = AxiosCollectionFormat.Csv);

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Contains("Ids=1,2", query);
    }

    [Fact]
    public async Task CollectionFormat_FromInstanceDefaults_RepeatsKey()
    {
        await using var server = TestHttpServer.Start(_ =>
            TestJson.Response(new[] { new Person("alice", 1) }));
        using var client = new ApiClient(server.Url,
            defaults => defaults.Serialization.CollectionFormat = AxiosCollectionFormat.Multi);

        await client.RequestAsync(new ListUsersRequest { Ids = [1, 2], Role = null });

        var query = Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty);
        Assert.Contains("Ids=1", query);
        Assert.Contains("Ids=2", query);
        Assert.DoesNotContain("Ids[]", query);
    }

    [Fact]
    public async Task CollectionFormat_CoreParams_HonorsPipesAndSsv()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new AxiosClient(new AxiosConfig { BaseAddress = server.Url });

        await client.GetAsync<Person>("items", config =>
        {
            config.Params = new { tags = new[] { "a", "b" } };
            config.Serialization.CollectionFormat = AxiosCollectionFormat.Pipes;
        });

        Assert.Equal("tags=a|b", Uri.UnescapeDataString(Assert.Single(server.Received).Query ?? string.Empty));

        await client.GetAsync<Person>("items", config =>
        {
            config.Params = new { tags = new[] { "a", "b" } };
            config.Serialization.CollectionFormat = AxiosCollectionFormat.Ssv;
        });

        Assert.Equal("tags=a b", Uri.UnescapeDataString(server.Received[1].Query ?? string.Empty));
    }

    // ------------------------------------------------------------------
    // URL 参数格式化钩子
    // ------------------------------------------------------------------

    [Fact]
    public async Task UrlParameterFormatter_AppliesToPathAndQuery()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url)
        {
            UrlParameterFormatter = value => value switch
            {
                int id => "p" + id,
                DateTime date => date.ToString("yyyyMMdd"),
                _ => null // 其余值走默认规则
            }
        };

        await client.RequestAsync(new FormattedRequest
        {
            Id = 7,
            When = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc)
        });

        var received = Assert.Single(server.Received);
        Assert.Equal("/fmt/p7", received.Path);
        Assert.Equal("When=20240102", Uri.UnescapeDataString(received.Query ?? string.Empty));
    }

    // ------------------------------------------------------------------
    // B3：基类的「删除声明」不能删掉派生类同名静态头的值
    // ------------------------------------------------------------------

    [Fact]
    public async Task B3_DerivedStaticHeader_WinsOverBaseRemoval()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("alice", 1)));
        using var client = new ApiClient(server.Url);

        await client.RequestAsync(new DerivedStaticHeaderRequest());

        var received = Assert.Single(server.Received);
        Assert.True(received.Headers.TryGetValue("X-Trace", out var trace), "X-Trace 被基类的删除声明误删");
        Assert.Equal("derived", trace);
    }

    // ------------------------------------------------------------------
    // 测试模型
    // ------------------------------------------------------------------

    [Get("static")]
    [Headers("Accept: application/json", "X-Api-Version: 2", "X-Trace: static")]
    private sealed class StaticHeaderRequest : IApiRequest<Person>
    {
        [Header("X-Trace")]
        public string? Trace { get; set; }
    }

    [Get("static")]
    [Headers("X-Remove")]
    private sealed class RemoveHeaderRequest : IApiRequest<Person>
    {
    }

    [Get("headers")]
    private sealed class HeaderCollectionRequest : IApiRequest<Person>
    {
        [HeaderCollection]
        public Dictionary<string, string?> Extra { get; set; } = new();
    }

    [Get("headers")]
    private sealed class BadHeaderCollectionRequest : IApiRequest<Person>
    {
        [HeaderCollection]
        public Dictionary<string, int> Extra { get; set; } = new();
    }

    [Get("headers")]
    private sealed class ConflictingHeaderRequest : IApiRequest<Person>
    {
        [Header("X-A")]
        [HeaderCollection]
        public Dictionary<string, string?> Extra { get; set; } = new();
    }

    [Get("search")]
    private sealed class SearchMapRequest : IApiRequest<Person>
    {
        [Query]
        public Dictionary<string, object?> Filters { get; set; } = new();
    }

    [Get("search")]
    private sealed class NamedMapRequest : IApiRequest<Person>
    {
        [Query("filters")]
        public Dictionary<string, object?> Filters { get; set; } = new();
    }

    [Get("fmt/{id}")]
    private sealed class FormattedRequest : IApiRequest<Person>
    {
        [Path]
        public int Id { get; set; }

        [Query]
        public DateTime When { get; set; }
    }


    // ------------------------------------------------------------------
    // 测试模型
    // ------------------------------------------------------------------


    [Headers("X-Trace")]
    private abstract class BaseWithStaticRemoval : IApiRequest<Person>
    {
    }

    [Get("derived-header")]
    [Headers("X-Trace: derived")]
    private sealed class DerivedStaticHeaderRequest : BaseWithStaticRemoval
    {
    }


    // ------------------------------------------------------------------
    // 测试替身
    // ------------------------------------------------------------------


    /// <summary>预置分块的响应流；可为每个分块指定读取前的延迟，读完后挂起。</summary>
    private sealed class PacedStream : Stream
    {
        private readonly byte[][] _chunks;
        private readonly TimeSpan[] _delays;
        private readonly bool _stallAfterChunks;
        private int _index;
        private int _offset;

        public PacedStream(byte[][] chunks, TimeSpan[] delays, bool stallAfterChunks)
        {
            _chunks = chunks;
            _delays = delays;
            _stallAfterChunks = stallAfterChunks;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_index >= _chunks.Length)
            {
                if (_stallAfterChunks)
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }

                return 0;
            }

            if (_offset == 0 && _index < _delays.Length && _delays[_index] > TimeSpan.Zero)
            {
                await Task.Delay(_delays[_index], cancellationToken).ConfigureAwait(false);
            }

            var chunk = _chunks[_index];
            var count = Math.Min(buffer.Length, chunk.Length - _offset);
            chunk.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            if (_offset >= chunk.Length)
            {
                _index++;
                _offset = 0;
            }

            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(Span<byte> buffer)
        {
            var temp = new byte[buffer.Length];
            var read = ReadAsync(temp.AsMemory(), CancellationToken.None).AsTask().GetAwaiter().GetResult();
            temp.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

}