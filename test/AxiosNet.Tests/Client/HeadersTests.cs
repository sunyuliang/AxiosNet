
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text;
using Xunit;

namespace AxiosNet.Tests.Client;

/// <summary>
/// 请求头语义：大小写不敏感、写时复制、控制字符拒绝、默认头删除。
/// </summary>
public sealed class HeadersTests
{
    [Theory]
    [InlineData("a\u000bb")]  // VT
    [InlineData("a\fb")]      // FF
    [InlineData("a\u0001b")]  // SOH
    [InlineData("a\u007fb")]  // DEL
    public void Headers_ControlCharsInValue_Throw(string value)
    {
        var headers = new AxiosHeaders();
        Assert.Throws<ArgumentException>(() => headers.Set("X-Test", value));
    }

    [Fact]
    public void Headers_TabInValue_IsAllowed()
    {
        // HTAB 是合法的 field-content。
        var headers = new AxiosHeaders();
        headers.Set("X-Test", "a\tb");
        Assert.Equal("a\tb", headers.Get("X-Test"));
    }

    // ===============================================================
    // Bug 8：Defaults.RemoveHeaders 只在快捷路径生效
    // ===============================================================

    [Fact]
    public async Task DefaultsRemoveHeaders_AppliesOnRequestAsyncPathToo()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Headers = new AxiosHeaders(new Dictionary<string, string> { ["X-Trace"] = "on" })
        });
        client.Defaults.RemoveHeaders.Add("X-Trace");

        await client.GetAsync("x");
        await client.RequestAsync(new AxiosConfig { Url = "x" });

        // 两条入口对「实例默认要摘除的头」行为一致
        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, r => Assert.False(r.Headers.ContainsKey("X-Trace")));
    }

    // ------------------------------------------------------------------
    // 6) 附加回归：COW 头存储的模型对比 / Bearer 的 scheme 处理
    // ------------------------------------------------------------------

    /// <summary>
    /// 让 <see cref="AxiosHeaders"/> 的实例树与「朴素字典树」并行执行同一串随机操作，每一步校验全部实例。
    /// COW 的缺陷（副本读到别人的写入、引用计数错导致漏复制）会立刻暴露。
    /// </summary>
    [Fact]
    public void CopyOnWrite_RandomOperations_MatchPlainDictionaryModel()
    {
        var random = new Random(20261022);

        var instances = new List<AxiosHeaders> { new() };
        var models = new List<Dictionary<string, string>>
        {
            new(StringComparer.OrdinalIgnoreCase)
        };

        string[] names = ["A", "B", "C", "D", "E"];
        for (var step = 0; step < 3000; step++)
        {
            var index = random.Next(instances.Count);
            switch (random.Next(4))
            {
                case 0:
                {
                    var name = names[random.Next(names.Length)];
                    var value = "v" + random.Next(100);
                    instances[index].Set(name, value);
                    models[index][name] = value;
                    break;
                }

                case 1:
                {
                    var name = names[random.Next(names.Length)];
                    Assert.Equal(models[index].Remove(name), instances[index].Delete(name));
                    break;
                }

                case 2:
                    instances[index].Clear();
                    models[index].Clear();
                    break;

                default:
                    if (instances.Count < 24)
                    {
                        instances.Add(instances[index].Clone());
                        models.Add(new Dictionary<string, string>(models[index], StringComparer.OrdinalIgnoreCase));
                    }

                    break;
            }

            for (var i = 0; i < instances.Count; i++)
            {
                Assert.Equal(models[i].Count, instances[i].Count);

                foreach (var name in names)
                {
                    models[i].TryGetValue(name, out var expected);
                    Assert.Equal(expected, instances[i].Get(name));
                    Assert.Equal(expected is not null, instances[i].Has(name));
                }
            }
        }
    }

    /// <summary>
    /// 克隆共享同一版数组（写时复制）；双方写入互不可见，且各自保留自己的条目。
    /// </summary>
    [Fact]
    public void Clone_SharesSnapshot_ButWritesStayIsolated()
    {
        var source = new AxiosHeaders();
        source.Set("k0", "v0");

        var clone = source.Clone();
        Assert.Equal("v0", clone.Get("k0"));

        // 副本写入：源不受影响
        clone.Set("k1", "v1");
        Assert.Null(source.Get("k1"));

        // 源写入：已经克隆出去的副本也不受影响（对称分离）
        source.Set("k2", "v2");
        Assert.Null(clone.Get("k2"));
        Assert.Equal(2, source.Count);
        Assert.Equal(2, clone.Count);
    }

    /// <summary>
    /// A2：同一实例被多线程并发写<b>不同的头</b>时不能丢更新。
    /// <para>这是「实例默认头可在请求进行中修改（如运行期刷新 Token）」的底层保证：
    /// CAS 失败即基于新版本重算，因此每个线程写的键都必须留下。</para>
    /// </summary>
    [Fact]
    public void ConcurrentWrites_OnSameInstance_DoNotLoseUpdates()
    {
        var headers = new AxiosHeaders();
        const int workers = 8;
        const int perWorker = 200;

        Parallel.For(0, workers, worker =>
        {
            for (var i = 0; i < perWorker; i++)
            {
                headers.Set($"X-W{worker}-{i}", i.ToString());
            }
        });

        Assert.Equal(workers * perWorker, headers.Count);
        for (var worker = 0; worker < workers; worker++)
        {
            for (var i = 0; i < perWorker; i++)
            {
                Assert.Equal(i.ToString(), headers.Get($"X-W{worker}-{i}"));
            }
        }
    }

    [Fact]
    public async Task EmptyHeaderValue_IsSent()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "x", c => c.Headers.Set("X-Empty", string.Empty));

        Assert.True(server.Received[0].Headers.ContainsKey("X-Empty"));
        Assert.Equal(string.Empty, server.Received[0].Headers["X-Empty"]);
    }

    [Fact]
    public async Task RemoveHeaders_RemovesInstanceDefaultHeader()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Headers = new AxiosHeaders(new Dictionary<string, string> { ["X-Trace"] = "on" })
        });

        await client.GetAsync(server.Url + "x", c => c.RemoveHeaders.Add("X-Trace"));

        Assert.False(server.Received[0].Headers.ContainsKey("X-Trace"));
    }

    [Fact]
    public async Task RemoveHeaders_ConcurrentMutation_DoesNotBreakInFlightRequests()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            Headers = new AxiosHeaders(new Dictionary<string, string> { ["X-Trace"] = "on" })
        });

        using var stop = new CancellationTokenSource();
        var mutation = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                // 枚举 + 增删并发进行，快照语义不能抛 InvalidOperationException
                foreach (var _ in client.Defaults.RemoveHeaders)
                {
                }

                client.Defaults.RemoveHeaders.Add("X-Temp" + i++);
                client.Defaults.RemoveHeaders.Remove("X-Temp" + i);
            }
        });

        var requests = Enumerable.Range(0, 16)
            .Select(i => client.GetAsync(server.Url + "x" + i, c => c.RemoveHeaders.Add("X-Trace")))
            .ToArray();

        Assert.All(await Task.WhenAll(requests), r => Assert.Equal(200, r.Status));
        stop.Cancel();
        await mutation;
    }

    // ---------------------------------------------------------------
    // 头集合的增删：configure 拿到的就是生效配置，删除 / 清空必须真的生效
    // ---------------------------------------------------------------

    [Fact]
    public async Task Configure_CanDeleteInstanceDefaultHeader_OnBothEntries()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Headers = new AxiosHeaders(new Dictionary<string, string> { ["Authorization"] = "Bearer secret" })
        });

        // 快捷入口：configure 在实例默认值的副本上改
        await client.GetAsync("a", c => c.Headers.Delete("Authorization"));

        // 主入口：起点必须与快捷入口相同 —— 从空配置起会让 Delete 在空集合上返回 false，
        // 凭据静默残留，两条入口行为正好相反
        await client.RequestAsync(c =>
        {
            c.Url = "b";
            c.Headers.Delete("Authorization");
        });

        Assert.Equal(2, server.Received.Count);
        Assert.All(server.Received, r => Assert.DoesNotContain("Authorization", r.Headers.Keys, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Configure_HeadersClear_DropsInstanceDefaults()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Echo(req));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Headers = new AxiosHeaders(new Dictionary<string, string> { ["X-Api-Key"] = "k" })
        });

        // Clear 同理：空配置上 Clear 什么也清不掉，生效配置上才是「这次一个默认头都不带」
        await client.RequestAsync(c =>
        {
            c.Url = "a";
            c.Headers.Clear();
            c.Headers.Set("X-Only", "1");
        });

        var received = Assert.Single(server.Received);
        Assert.DoesNotContain("X-Api-Key", received.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("X-Only", received.Headers.Keys, StringComparer.OrdinalIgnoreCase);
    }

    // 丢更新。

    [Fact]
    public void Headers_ConcurrentCloneAndSet_NeverLosesUpdate()
    {
        for (var round = 0; round < 20; round++)
        {
            var source = NewHeaders(64);
            var expected = new ConcurrentBag<string>();
            var stop = 0;

            // 克隆方不停共享当前版本，逼写方每次都走「算新数组 + CAS 发布」。
            var cloner = Task.Run(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    _ = source.Clone();
                }
            });

            Parallel.For(0, 4, worker =>
            {
                for (var i = 0; i < 400; i++)
                {
                    var key = $"X-W{worker}-{i}";
                    source.Set(key, "v");
                    expected.Add(key);
                }
            });

            Volatile.Write(ref stop, 1);
            cloner.Wait();

            foreach (var key in expected)
            {
                Assert.NotNull(source.Get(key));
            }
        }
    }

    [Fact]
    public void TransformList_ConcurrentCloneAndAppend_NeverLosesAppend()
    {
        for (var round = 0; round < 20; round++)
        {
            var config = new AxiosConfig();
            config.TransformRequest.Add(static (data, _) => data); // 让链非空，Clone 才会共享

            var stop = 0;
            var cloner = Task.Run(() =>
            {
                while (Volatile.Read(ref stop) == 0)
                {
                    _ = config.Clone();
                }
            });

            const int appends = 1_600;
            Parallel.For(0, 4, _ =>
            {
                for (var i = 0; i < appends / 4; i++)
                {
                    config.TransformRequest.Add(static (data, _) => data);
                }
            });

            Volatile.Write(ref stop, 1);
            cloner.Wait();

            Assert.Equal(appends + 1, config.TransformRequest.Count);
        }
    }

    // 克隆必须看到调用时刻的最新内容。

    [Fact]
    public void Clone_AlwaysSeesLatestStore()
    {
        var source = new AxiosHeaders();
        source.Set("A", "1");
        _ = source.Clone();

        source.Set("B", "2"); // 写入必须发布新数组，不能改共享的那一版
        var clone = source.Clone();

        Assert.Equal("2", clone.Get("B"));
        Assert.Equal(2, clone.Count);
    }

    // 零复制：无操作不该付整份复制的代价。

    [Fact]
    public void Headers_ClearOnSharedEmpty_DoesNotCopy()
    {
        var source = new AxiosHeaders();
        var shared = source.Clone(); // 共享一个空存储

        var cost = AverageBytes(20_000, source.Clear);

        Assert.True(cost < 32, $"共享空存储上的 Clear() 平均分配 {cost} B —— 疑似仍整份复制。");
        Assert.Equal(0, shared.Count);
    }

    [Fact]
    public void Headers_DeleteMissingOnShared_DoesNotCopy()
    {
        var source = NewHeaders(10);
        var shared = source.Clone();

        var cost = AverageBytes(20_000, () => source.Delete("X-Not-There"));

        Assert.True(cost < 32, $"删除不存在的头平均分配 {cost} B —— 疑似仍整份复制。");
        Assert.Equal(10, shared.Count);
    }

    [Fact]
    public void TransformList_RemoveMissingOnShared_DoesNotCopy()
    {
        var config = new AxiosConfig();
        Func<object?, AxiosConfig, object?> transform = static (data, _) => data;
        config.TransformRequest.Add(transform);
        var shared = config.Clone();

        var cost = AverageBytes(20_000, () => config.TransformRequest.Remove(static (_, _) => null));

        Assert.True(cost < 32, $"移除不存在的 transform 平均分配 {cost} B —— 疑似仍整份复制。");
        Assert.Equal(1, shared.TransformRequest.Count);
    }

    [Fact]
    public void Dynamic_ManyDistinctFieldNames_StillCorrect()
    {
        var node = new AxiosDynamic();

        // 2000 个互不重复的字段名，会越过单节点迁移缓存上限。
        for (var i = 0; i < 2000; i++)
        {
            node[$"f{i}"] = i;
        }

        Assert.Equal(2000, node.Count);
        Assert.Equal(1999, (int)node["f1999"]!);

        for (var i = 0; i < 2000; i += 2)
        {
            Assert.True(node.Remove($"f{i}"));
        }

        Assert.Equal(1000, node.Count);
        Assert.Null(node["f0"]);
        Assert.Equal(1, (int)node["f1"]!);
    }

    // 请求头 CRLF 注入

    [Theory]
    [InlineData("ok\r\nX-Injected: pwned")]
    [InlineData("ok\nX-Injected: pwned")]
    [InlineData("ok\rX-Injected: pwned")]
    [InlineData("ok\0pwned")]
    public void HeaderValue_WithControlChars_IsRejected(string value)
    {
        var headers = new AxiosHeaders();

        var ex = Assert.Throws<ArgumentException>(() => headers.Set("X-Test", value));

        Assert.Equal("value", ex.ParamName);
    }

    [Theory]
    [InlineData("X-A: X-B")]
    [InlineData("X A")]
    [InlineData("X-A\r\n")]
    [InlineData("X\tA")]
    public void HeaderName_OutsideTokenCharset_IsRejected(string name)
    {
        var headers = new AxiosHeaders();

        var ex = Assert.Throws<ArgumentException>(() => headers.Set(name, "v"));

        Assert.Equal("name", ex.ParamName);
    }

    [Fact]
    public void HeaderValue_WithoutControlChars_IsAcceptedVerbatim()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-Trace", "a=b; c, d");

        Assert.Equal("a=b; c, d", headers.Get("x-trace"));
    }

    [Fact]
    public async Task HeaderValue_ReachesServerUnchanged()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient();

        await client.GetAsync(server.Url + "ping", c => c.Headers.Set("X-Trace", "a=b; c, d"));

        Assert.Equal("a=b; c, d", server.Received[0].Headers["x-trace"]);
    }

    // AxiosHeaders：集合语义

    [Fact]
    public void Set_SameNameDifferentCase_Overwrites()
    {
        var headers = new AxiosHeaders();

        headers.Set("X-Trace", "first");
        headers.Set("x-trace", "second");

        Assert.Equal("second", headers.Get("X-Trace"));
        Assert.Equal(1, headers.Count);
    }

    [Fact]
    public void Set_NullValue_DeletesEntry()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-Keep", "1");

        headers.Set("X-Keep", null);

        Assert.False(headers.Has("x-keep"));
        Assert.Null(headers.Get("X-Keep"));
    }

    [Fact]
    public void Set_Batch_NullValueDeletes_AndNonNullOverwrites()
    {
        var headers = new AxiosHeaders();
        headers.Set(new[]
        {
            new KeyValuePair<string, string?>("X-A", "1"),
            new KeyValuePair<string, string?>("X-B", "2")
        });

        headers.Set(new[]
        {
            new KeyValuePair<string, string?>("X-A", (string?)null),
            new KeyValuePair<string, string?>("X-B", "22")
        });

        Assert.False(headers.Has("X-A"));
        Assert.Equal("22", headers.Get("X-B"));
    }

    [Fact]
    public void Set_Batch_RejectsControlCharactersInValue()
    {
        var headers = new AxiosHeaders();

        // 批量路径与单条 Set 走同一校验。
        var ex = Assert.Throws<ArgumentException>(() => headers.Set(new[]
        {
            new KeyValuePair<string, string?>("X-Ok", "1"),
            new KeyValuePair<string, string?>("X-Bad", "a\r\nInjected: 1")
        }));

        Assert.Equal("value", ex.ParamName);
        Assert.Equal("1", headers.Get("X-Ok"));
        Assert.False(headers.Has("X-Bad"));
    }

    [Fact]
    public void Indexer_ReadsAndWrites_IncludingNullDelete()
    {
        var headers = new AxiosHeaders();

        headers["X-A"] = "1";
        Assert.Equal("1", headers["x-a"]);

        headers["X-A"] = null;
        Assert.Null(headers["X-A"]);
    }

    [Fact]
    public void Has_And_Delete_ReportPresenceCaseInsensitively()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-Trace", "on");

        Assert.True(headers.Has("x-trace"));
        Assert.True(headers.Delete("X-TRACE"));
        Assert.False(headers.Has("X-Trace"));
        Assert.False(headers.Delete("X-Trace"));
    }

    [Fact]
    public void Get_TrimsLookupName_ButNotStoredValue()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-A", " padded ");

        Assert.Equal(" padded ", headers.Get("X-A"));
        Assert.Equal(" padded ", headers.Get("  X-A  "));
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-A", "1");
        headers.Set("X-B", "2");

        headers.Clear();

        Assert.Equal(0, headers.Count);
        Assert.Empty(headers);
    }

    [Fact]
    public void Set_ReturnsSameInstance_ForChaining()
    {
        var headers = new AxiosHeaders();

        Assert.Same(headers, headers.Set("X-A", "1"));
    }

    [Fact]
    public void Clone_IsIndependent_OfSource()
    {
        var original = new AxiosHeaders();
        original.Set("X-A", "1");

        var clone = original.Clone();
        clone.Set("X-A", "2");
        clone.Set("X-B", "new");

        Assert.Equal("1", original.Get("X-A"));
        Assert.False(original.Has("X-B"));
    }

    [Fact]
    public void Constructor_FromPairs_StoresAllEntries_TrimmingNames()
    {
        var headers = new AxiosHeaders(new[]
        {
            new KeyValuePair<string, string>("  X-A  ", "1"),
            new KeyValuePair<string, string>("X-B", "2")
        });

        Assert.Equal(2, headers.Count);
        Assert.Equal("1", headers.Get("X-A"));
    }

    [Fact]
    public void Enumeration_IsASnapshot_NotALiveView()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-A", "1");

        var seen = new List<string>();
        foreach (var pair in headers)
        {
            seen.Add(pair.Key);
            headers.Set("X-B", "2");
        }

        Assert.Equal(new[] { "X-A" }, seen);
    }

    [Fact]
    public void HeaderLookups_WithNullName_ThrowArgumentNullException()
    {
        var headers = new AxiosHeaders();
        headers.Set("X-Trace", "abc");

        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => headers.Get(null!)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => headers.Has(null!)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => headers.Delete(null!)).ParamName);

        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => headers[null!]).ParamName);

        Assert.Equal("abc", headers.Get("X-Trace"));
    }

    [Fact]
    public void HeadersProperty_NullAssignment_ThrowsAtAssignmentPoint()
    {
        var config = new AxiosConfig();
        var original = config.Headers;

        var ex = Assert.Throws<ArgumentNullException>(() => config.Headers = null!);

        Assert.Equal("value", ex.ParamName);

        Assert.Same(original, config.Headers);
        config.Headers.Set("X-Trace", "abc");
        Assert.Equal("abc", config.Headers.Get("X-Trace"));
    }

    [Fact]
    public async Task DefaultHeaders_AreSent_AndPerRequestHeadersOverrideThem()
    {
        await using var server = TestHttpServer.Start(req => TestJson.Response(new
        {
            def = req.Headers.GetValueOrDefault("x-default"),
            version = req.Headers.GetValueOrDefault("x-version"),
            perRequest = req.Headers.GetValueOrDefault("x-request")
        }));

        var defaults = new AxiosConfig();
        defaults.Headers.Set("X-Default", "yes");
        defaults.Headers.Set("X-Version", "v1");

        using var client = new AxiosClient(defaults);

        var response = await client.GetAsync<JsonElement>(server.Url, c =>
        {
            c.Headers.Set("X-Version", "v2");
            c.Headers.Set("X-Request", "1");
        });

        Assert.Equal("yes", response.Data.GetProperty("def").GetString());
        Assert.Equal("v2", response.Data.GetProperty("version").GetString());
        Assert.Equal("1", response.Data.GetProperty("perRequest").GetString());
    }

    // ---------------------------------------------------------------
    // P0-2：运行期改默认请求头（Token 刷新）不能打死并发请求
    // ---------------------------------------------------------------

    [Fact]
    public async Task ConcurrentDefaultsHeaderMutation_DoesNotBreakInFlightRequests()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient();
        using var stop = new CancellationTokenSource();

        var writer = Task.Run(() =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                client.Defaults.Headers.Set("X-Token", "v" + i++);
                client.Defaults.Headers.Delete("X-Token");
            }
        });

        var failures = new List<Exception>();
        for (var i = 0; i < 300; i++)
        {
            try
            {
                await client.GetAsync(server.Url + "ping");
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        stop.Cancel();
        await writer;

        Assert.Empty(failures);
    }

    private static TestResponse Json(string body = "{\"ok\":true}") => new(Body: Encoding.UTF8.GetBytes(body), ContentType: "application/json; charset=utf-8");

    private static string Body(TestRequest request) => Encoding.UTF8.GetString(request.Body);

    private static string ContentType(TestRequest request)
        => request.Headers.GetValueOrDefault("content-type") ?? string.Empty;

    // ===============================================================
    // 测试用请求模型 / 序列化器
    // ===============================================================


    /// <summary>声明任意 <c>Content-Length</c>，但始终写出完整正文的 Handler（模拟声明不可信）。</summary>
    private sealed class FixedLengthHandler : HttpMessageHandler
    {
        private readonly byte[] _body;
        private readonly long _declared;

        public FixedLengthHandler(byte[] body, long declaredLength)
        {
            _body = body;
            _declared = declaredLength;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new ByteArrayContent(_body);
            content.Headers.ContentLength = _declared;

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content,
                RequestMessage = request
            });
        }
    }

    private sealed class Node
    {
        public Node? Self { get; set; }
    }


    private static AxiosHeaders NewHeaders(int count)
    {
        var headers = new AxiosHeaders();
        for (var i = 0; i < count; i++)
        {
            headers.Set($"X-H{i}", $"value-{i}");
        }

        return headers;
    }

    private static long AverageBytes(int iterations, Action action)
    {
        action();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
    }


    // ---------------------------------------------------------------
    // 辅助
    // ---------------------------------------------------------------

    /// <summary>
    /// 正文读取阶段的超时预算。必须同时满足两头：
    /// <list type="bullet">
    /// <item>远大于「已预热连接的一次本地往返」——否则预算会先被响应头阶段吃掉，用例根本进不到正文阶段；</item>
    /// <item>远小于正文延迟（<see cref="SlowBodyDelay"/>）——否则读会自己等到正文，区分不出「超时生效」。</item>
    /// </list>
    /// <para>与 <c>StreamCancellationTests</c> 同一口径：预算与自然完成时间差一个数量级。</para>
    /// </summary>
    private static readonly TimeSpan BodyReadTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 服务端「先发响应头、再延迟正文」的延迟：必须远大于 <see cref="BodyReadTimeout"/>，
    /// 这样「被超时打断」与「自己等到正文」才不会混淆，调度抖动晚到几秒也仍有充足余量。
    /// </summary>
    private static readonly TimeSpan SlowBodyDelay = TimeSpan.FromSeconds(20);


}