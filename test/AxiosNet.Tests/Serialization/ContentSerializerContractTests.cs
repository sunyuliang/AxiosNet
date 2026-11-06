using System.Text;
using AxiosNet.Retry;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// 第三方内容序列化器（<see cref="IContentSerializer"/>）的契约：请求体只序列化一次、类型化响应走字节入口、
/// 非 UTF-8 响应先解码再进文本入口、空体不调用序列化器、失败映射到统一错误码，重试 / 对冲分支复用同一实例。
/// </summary>
public sealed class ContentSerializerContractTests
{
    [Fact]
    public async Task Request_SerializesOnce_AndTheBodyIsReplayedOnRetry()
    {
        var calls = 0;
        await using var server = TestHttpServer.Start(_ => Interlocked.Increment(ref calls) == 1
            ? TestJson.Response(new { error = "boom" }, statusCode: 500)
            : new TestResponse(200, Body: null));

        var serializer = new ContractSerializer();
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = { BodySerializers = { Default = serializer } },
            Resilience =
            {
                AllowUnsafeRetry = true,
                Retry = { Policy = new RetryPolicy(2, TimeSpan.Zero) }
            }
            });

        await client.PostAsync(server.Url + "x", new Person("ada", 36));

        Assert.Equal(2, server.Received.Count);
        Assert.Equal("name=ada;age=36", Encoding.UTF8.GetString(server.Received[0].Body));
        Assert.Equal("name=ada;age=36", Encoding.UTF8.GetString(server.Received[1].Body));

        // 重试复用同一条模板正文：序列化只发生一次（不是每次尝试各序列化一次）。
        Assert.Equal(1, serializer.SerializeCalls);

        // 未显式设置 ContentType 时由序列化器自报。
        Assert.Equal("text/x-contract", server.Received[0].Headers["Content-Type"]);
        Assert.All(serializer.SameSerializerSnapshot(), same => Assert.True(same));
    }

    [Fact]
    public async Task TypedResponse_IsDeserializedThroughTheInjectedSerializer()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("name=ada;age=36"),
            ContentType: "text/x-contract; charset=utf-8"));

        var serializer = new ContractSerializer();
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = { BodySerializers = { Default = serializer } }
        });

        var response = await client.GetAsync<Person>(server.Url + "x");

        Assert.Equal("ada", response.Data!.Name);
        Assert.Equal(36, response.Data.Age);
        Assert.Equal(1, serializer.DeserializeCalls);
        Assert.Equal(new[] { "name=ada;age=36" }, serializer.TextSnapshot());

        // 每次调用拿到的配置里都还是同一个序列化器实例，不是按请求新建的。
        Assert.All(serializer.SameSerializerSnapshot(), same => Assert.True(same));
    }

    [Fact]
    public async Task ResponseCharset_IsDecodedBeforeTheTextEntry()
    {
        var payload = Encoding.Latin1.GetBytes("name=José;age=3");
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: payload,
            ContentType: "text/x-contract; charset=iso-8859-1"));

        var serializer = new ContractSerializer();
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = { BodySerializers = { Default = serializer } }
        });

        var response = await client.GetAsync<Person>(server.Url + "x");

        // 非 UTF-8 正文按声明 charset 解码后才进文本入口；序列化器看到的不是原始坏字节。
        Assert.Equal(new[] { "name=José;age=3" }, serializer.TextSnapshot());
        Assert.Equal("José", response.Data!.Name);
    }

    [Fact]
    public async Task RequestCharset_ProducesBytesInTheDeclaredEncoding()
    {
        await using var server = TestHttpServer.Start(request => TestJson.Echo(request));
        var serializer = new ContractSerializer();
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = { BodySerializers = { Default = serializer }, TextEncoding = Encoding.Latin1 }
        });

        await client.PostAsync(
            server.Url + "x",
            new Person("José", 3),
            config => config.ContentType = "text/x-contract");

        var received = Assert.Single(server.Received);
        Assert.Equal(Encoding.Latin1.GetBytes("name=José;age=3"), received.Body);
        Assert.Contains((byte)0xE9, received.Body);

        // 显式 TextEncoding 会把 charset 写进请求头，与实际正文编码一致。
        Assert.Equal("text/x-contract; charset=iso-8859-1", received.Headers["Content-Type"]);
        Assert.Equal(1, serializer.SerializeCalls);
    }

    [Fact]
    public async Task WithHedging_TheSerializerIsSharedByTheBranches()
    {
        var arrivals = 0;
        var bothArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            if (Interlocked.Increment(ref arrivals) == 1)
            {
                // 两条分支都先把请求发出来，再一起回响应，保证能观察到两个分支。
                await Task.WhenAny(bothArrived.Task, Task.Delay(TimeSpan.FromSeconds(2)));
            }
            else
            {
                bothArrived.TrySetResult(true);
            }

            return new TestResponse(
                200,
                Body: Encoding.UTF8.GetBytes("name=ada;age=36"),
                ContentType: "text/x-contract; charset=utf-8");
        });

        var serializer = new ContractSerializer();
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = { BodySerializers = { Default = serializer } },
            Resilience =
            {
                AllowUnsafeRetry = true,
                Hedging = {
                    MaxHedges = 1,
                    Delay = TimeSpan.Zero
                }
            }
            });

        var response = await client
            .PostAsync<Person>(server.Url + "x", new Person("ada", 36))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("ada", response.Data!.Name);

        // 正文只序列化一次；两条分支的配置都是克隆，但里面的序列化器实例是同一个。
        Assert.Equal(1, serializer.SerializeCalls);
        Assert.Equal(2, server.Received.Count);
        Assert.All(serializer.SameSerializerSnapshot(), same => Assert.True(same));
        Assert.True(serializer.DeserializeCalls >= 1);
    }

    [Fact]
    public async Task SerializeFailure_MapsToErrBadRequest()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(200, Body: null));
        var serializer = new ContractSerializer { FailSerialize = true };
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = { BodySerializers = { Default = serializer } }
        });

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.PostAsync(server.Url + "x", new Person("ada", 36)));

        Assert.Equal(AxiosException.ErrBadRequest, error.Code);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task DeserializeFailure_MapsToErrBadResponse()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            200,
            Body: Encoding.UTF8.GetBytes("not-a-person"),
            ContentType: "text/x-contract; charset=utf-8"));

        var serializer = new ContractSerializer { FailDeserialize = true };
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = { BodySerializers = { Default = serializer } }
        });

        var error = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync<Person>(server.Url + "x"));

        Assert.Equal(AxiosException.ErrBadResponse, error.Code);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task EmptyBody_YieldsNullWithoutCallingTheSerializer()
    {
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            204,
            Body: null,
            ContentType: "text/x-contract; charset=utf-8"));

        var serializer = new ContractSerializer();
        using var client = new AxiosClient(new AxiosConfig
        {
            Serialization = { BodySerializers = { Default = serializer } }
        });

        var response = await client.GetAsync<Person>(server.Url + "x");

        // 空体 / 全空白正文由库直接判空为 null，不交给序列化器。
        Assert.Null(response.Data);
        Assert.Equal(0, serializer.DeserializeCalls);
        Assert.Single(server.Received);
    }

    /// <summary>
    /// 计数用的自定义序列化器：<c>name=..;age=..</c> 文本格式，可注入失败，记录每次调用拿到的配置。
    /// </summary>
    private sealed class ContractSerializer : IContentSerializer
    {
        private readonly object _gate = new();
        private readonly List<string> _texts = new();
        private readonly List<bool> _sameSerializer = new();
        private int _serializeCalls;
        private int _deserializeCalls;

        public string? MediaType => "text/x-contract";

        public bool FailSerialize { get; init; }

        public bool FailDeserialize { get; init; }

        public int SerializeCalls => Volatile.Read(ref _serializeCalls);

        public int DeserializeCalls => Volatile.Read(ref _deserializeCalls);

        public string[] TextSnapshot()
        {
            lock (_gate)
            {
                return _texts.ToArray();
            }
        }

        public bool[] SameSerializerSnapshot()
        {
            lock (_gate)
            {
                return _sameSerializer.ToArray();
            }
        }

        public string Serialize(object? value, Type type, AxiosConfig config)
        {
            Interlocked.Increment(ref _serializeCalls);
            RememberConfig(config);

            if (FailSerialize)
            {
                throw new InvalidOperationException("serialize boom");
            }

            var person = (Person)value!;
            return $"name={person.Name};age={person.Age}";
        }

        public object? Deserialize(string text, Type type, AxiosConfig config)
        {
            Interlocked.Increment(ref _deserializeCalls);

            lock (_gate)
            {
                _texts.Add(text);
            }

            RememberConfig(config);

            if (FailDeserialize)
            {
                throw new InvalidOperationException("deserialize boom");
            }

            var name = string.Empty;
            var age = 0;
            foreach (var segment in text.Split(';'))
            {
                var pair = segment.Split('=');
                if (pair.Length != 2)
                {
                    continue;
                }

                if (pair[0] == "name")
                {
                    name = pair[1];
                }
                else if (pair[0] == "age")
                {
                    age = int.Parse(pair[1]);
                }
            }

            return new Person(name, age);
        }

        private void RememberConfig(AxiosConfig config)
        {
            lock (_gate)
            {
                _sameSerializer.Add(ReferenceEquals(config.Serialization.BodySerializers.Default, this));
            }
        }
    }
}
