using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AxiosNet.Circuit;
using AxiosNet.Internal;
using AxiosNet.Pipeline;
using AxiosNet.Pooling;
using AxiosNet.Retry;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.Tests.Resilience;

namespace AxiosNet.Tests.Pipelines;

/// <summary>
/// 备用端点：当前端点用不了时（网络错误 / 超时 / 连接池耗尽 / 熔断打开 / 5xx）按顺序换端点重发，全部失败才交给重试；
/// 4xx 等换镜像解决不了的故障不换；换端点必须连连接租约 / 熔断维度 / 遥测标签一起换。
/// </summary>
public sealed class FailoverTests
{
    [Fact]
    public async Task Failover_NoEndpoints_SendsOnce()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = server.Url,
            Failover = new() { Endpoints = Array.Empty<Uri>() }
        });

        var response = await client.GetAsync("x");

        Assert.Equal(200, response.Status);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task Failover_SwitchesToMirror_OnNetworkError_AndKeepsPathAndQuery()
    {
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        using var client = new AxiosClient();

        // 127.0.0.1:1 上的连接会被立刻拒绝（ERR_NETWORK），正好覆盖「当前端点建连都失败」。
        var response = await client.GetAsync(
            "http://127.0.0.1:1/api/users?page=2",
            c => c.Failover.Endpoints = new[] { new Uri(mirror.Url) });

        Assert.Equal(200, response.Status);
        Assert.True(Assert.IsType<System.Text.Json.JsonElement>(response.Data).GetProperty("mirror").GetBoolean());

        var received = Assert.Single(mirror.Received);
        Assert.Equal("/api/users", received.Path);
        Assert.Equal("page=2", received.Query);

        // 响应上的请求必须是真正发出去的那一条（镜像地址），而不是原始模板。
        Assert.Equal(mirror.Url + "api/users?page=2", response.Request!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Failover_SwitchesOn5xx_AndEndpointPrefixIsKept()
    {
        var seen = new ConcurrentQueue<Uri>();

        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(500, Body: Array.Empty<byte>()));
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        // 记录每条尝试真正拿到的 URL：换端点必须走 WithUri，否则租约 / 熔断维度 / 遥测标签会留在旧端点上。
        AxiosAttemptMiddleware recorder = next => (request, ctx) =>
        {
            seen.Enqueue(ctx.Uri);
            return next(request, ctx);
        };

        using var client = new AxiosClient(
            new AxiosConfig
            {
                BaseAddress = primary.Url,
                Failover = new() { Endpoints = new[] { new Uri(mirror.Url + "api") } }
            },
            pipeline: AxiosAttemptPipeline.Default.Append(recorder));

        var response = await client.GetAsync("v1/thing");

        Assert.Equal(200, response.Status);
        Assert.Single(primary.Received);
        Assert.Equal("/api/v1/thing", Assert.Single(mirror.Received).Path);

        var urls = seen.ToArray();
        Assert.Equal(2, urls.Length);
        Assert.Equal(primary.Url + "v1/thing", urls[0].AbsoluteUri);
        Assert.Equal(mirror.Url + "api/v1/thing", urls[1].AbsoluteUri);
    }

    [Fact]
    public async Task Failover_DoesNotSwitchOn4xx()
    {
        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(404, Body: Array.Empty<byte>()));
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        // 4xx 是请求本身的问题：换镜像改变不了，不该把同一份请求再打到一个端点上。
        Assert.Equal(AxiosException.ErrBadRequest, exception.Code);
        Assert.Single(primary.Received);
        Assert.Empty(mirror.Received);
    }

    [Fact]
    public async Task Failover_AllEndpointsFail_IsHandedToRetry()
    {
        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));
        await using var mirror = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Retry = new() { Policy = RetryPolicy.Constant(1, TimeSpan.Zero) },
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        // 每次尝试 = 原始端点 + 1 个备用端点；重试 1 次 → 两端点各 2 条。
        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Equal(2, primary.Received.Count);
        Assert.Equal(2, mirror.Received.Count);
    }

    [Fact]
    public async Task Failover_PerRequestNullEndpoints_DisablesInstanceFailover()
    {
        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(
            () => client.GetAsync("x", c => c.Failover.Endpoints = null));

        // 显式赋 null 是「本次请求不换端点」，不是「没表态」。
        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Single(primary.Received);
        Assert.Empty(mirror.Received);
    }

    [Fact]
    public async Task Failover_RejectsEndpointThatIsNotAbsoluteHttp()
    {
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "http://127.0.0.1:1/",
            Failover = new() { Endpoints = new[] { new Uri("backup", UriKind.Relative) } }
        });

        var relative = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));
        Assert.Equal(AxiosException.ErrBadOptionValue, relative.Code);

        using var ftpClient = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "http://127.0.0.1:1/",
            Failover = new() { Endpoints = new[] { new Uri("ftp://backup.example.com/") } }
        });

        var ftp = await Assert.ThrowsAsync<AxiosException>(() => ftpClient.GetAsync("x"));
        Assert.Equal(AxiosException.ErrBadOptionValue, ftp.Code);
    }

    [Fact]
    public async Task Failover_RejectsEndpointWithQueryOrFragment()
    {
        using var queryClient = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "http://127.0.0.1:1/",
            Failover = new() { Endpoints = new[] { new Uri("https://backup.example.com/api?v=2") } }
        });

        // 端点只提供 origin + 路径前缀：自带的 query 没有安放位置，静默丢掉就是「配置没生效」。
        var query = await Assert.ThrowsAsync<AxiosException>(() => queryClient.GetAsync("x"));
        Assert.Equal(AxiosException.ErrBadOptionValue, query.Code);

        using var fragmentClient = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "http://127.0.0.1:1/",
            Failover = new() { Endpoints = new[] { new Uri("https://backup.example.com/api#frag") } }
        });

        var fragment = await Assert.ThrowsAsync<AxiosException>(() => fragmentClient.GetAsync("x"));
        Assert.Equal(AxiosException.ErrBadOptionValue, fragment.Code);
    }

    [Fact]
    public async Task Failover_RequiresReplayableBody()
    {
        await using var primary = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
        });

        // 换端点要按顺序重发同一份正文：与重试同一份要求，不可 seek 的流在第一次发送之前就该拦下。
        await using var payload = new NonSeekableStream(Encoding.UTF8.GetBytes("{\"name\":\"a\"}"));
        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync("x", payload));

        Assert.Equal(AxiosException.ErrBadOptionValue, exception.Code);
        Assert.Empty(primary.Received);
        Assert.Empty(mirror.Received);
    }

    [Fact]
    public async Task Failover_SwitchesWhenPrimaryCircuitIsOpen()
    {
        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Circuit = { Policy = new CircuitBreaker(failureThreshold: 1, breakDuration: TimeSpan.FromMinutes(1)) },
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
        });

        // 第 1 次：主端点 503 → 熔断打开 → 换到镜像。
        var first = await client.GetAsync("x");
        Assert.True(Assert.IsType<JsonElement>(first.Data).GetProperty("mirror").GetBoolean());

        // 第 2 次：主端点的熔断器已打开，请求根本没发出去 —— 熔断按端点独立成账，仍要换到镜像。
        var second = await client.GetAsync("x");
        Assert.True(Assert.IsType<JsonElement>(second.Data).GetProperty("mirror").GetBoolean());

        Assert.Single(primary.Received);
        Assert.Equal(2, mirror.Received.Count);
    }

    [Fact]
    public async Task Failover_SwitchesOnTimeout()
    {
        // 主端点把响应头压到超时之后：单次尝试的时间预算耗尽 → ECONNABORTED → 换端点。
        await using var primary = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600));
            return TestJson.Response(new { primary = true });
        });
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Timeout = TimeSpan.FromMilliseconds(150),
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
        });

        var response = await client.GetAsync("x");

        // 每个端点各有自己的 Timeout 预算：主端点耗光之后，镜像仍拿到完整的一份。
        Assert.Equal(200, response.Status);
        Assert.True(Assert.IsType<JsonElement>(response.Data).GetProperty("mirror").GetBoolean());
        Assert.Equal(mirror.Url + "x", response.Request!.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Failover_DoesNotSwitchWhenBudgetExpiredBeforeSend()
    {
        // 预算在「发出之前」就耗尽（本例被并发闸门挡住，排到预算用完）：
        // 这类 ECONNABORTED 换端点解决不了 —— Deadline 管的是整条逻辑请求的墙钟时间，
        // 换个下游不会让它变长，只会在备用端点再走一遍管线、占一次熔断名额与闸门入口，然后原地再失败一次。
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var primary = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new { primary = true });
        });
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        var policy = new HostCountingPolicy();

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Limits = { MaxConcurrentRequests = 1 },
            Circuit = { Policy = policy },
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
        });

        // 占住唯一槽位（到本用例结束才放）：后面的请求只能在闸门里等到预算耗尽
        var holder = client.GetAsync("hold");
        await WaitUntilAsync(() => primary.Received.Count >= 1);

        try
        {
            var expired = await Assert.ThrowsAsync<AxiosException>(
                () => client.GetAsync("queued", c => c.Deadline = TimeSpan.FromMilliseconds(80)));

            Assert.Equal(AxiosException.ErrConnAborted, expired.Code);

            // 只碰过主端点（holder 一条 + 排队那条各判一次）：备用端点连一次熔断判定都没做过
            Assert.All(policy.Allowed, host => Assert.Equal($"127.0.0.1:{primary.Port}", host));
            Assert.Empty(mirror.Received);
        }
        finally
        {
            // 断言失败也要放开占位请求：否则它挂在服务器的 await 上，用例失败会连带把测试进程拖住。
            release.SetResult(true);
        }

        await holder;
    }

    [Fact]
    public async Task Failover_SwitchesWhenThePoolCannotTakeTheOrigin()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 镜像先把池里唯一的节点占住；主端点是「池里装不下」的新 origin → ERR_POOL_EXHAUSTED。
        await using var mirror = TestHttpServer.Start(async request =>
        {
            if (request.Path == "/hold")
            {
                await release.Task;
            }

            return TestJson.Response(new { mirror = true });
        });

        using var pool = new HttpConnectionPool(new ConnectionPoolOptions
        {
            MaxEndpointCount = 1,
            EvictionInterval = TimeSpan.Zero
        });
        using var client = new AxiosClient(pool: pool);

        var holding = client.GetAsync(mirror.Url + "hold");
        await WaitUntilAsync(() => mirror.Received.Count >= 1);

        var response = await client.GetAsync(
            "http://127.0.0.1:1/x",
            c => c.Failover.Endpoints = new[] { new Uri(mirror.Url) });

        // 本机连接池耗尽换个下游就能解决：请求真的发到了镜像上。
        Assert.Equal(200, response.Status);
        Assert.True(Assert.IsType<JsonElement>(response.Data).GetProperty("mirror").GetBoolean());

        release.SetResult(true);
        await holding;
    }

    [Fact]
    public async Task Failover_DoesNotSwitchOnQueueTimeout()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var primary = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new { ok = true });
        });
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Limits = { MaxConcurrentRequests = 1, QueueTimeout = TimeSpan.Zero },
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
        });

        var occupying = client.GetAsync("hold");
        await WaitUntilAsync(() => primary.Received.Count >= 1);

        // 闸门排队超时说明本机额度已满，换端点只会把同一个瓶颈带到镜像上。
        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrQueueTimeout, exception.Code);
        Assert.Empty(mirror.Received);

        release.SetResult(true);
        await occupying;
    }

    [Fact]
    public async Task Failover_GateRejection_DoesNotMaskThePrimaryFailure()
    {
        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        // 换到备用端点时在闸门前被拒（排队超时 / 队列满）：闸门是客户端级的，换端点也过不去，
        // 抛给调用方的必须是主端点那条真实失败，而不是「没排上队」。
        AxiosAttemptMiddleware rejectMirror = next => async (request, ctx) =>
        {
            if (ctx.EndpointIndex > 0)
            {
                throw AxiosException.QueueFull(ctx.Config, request, maxQueueLength: 1, maxConcurrency: 1);
            }

            return await next(request, ctx);
        };

        using var client = new AxiosClient(
            new AxiosConfig
            {
                BaseAddress = primary.Url,
                Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
            },
            pipeline: AxiosAttemptPipeline.Default.Append(rejectMirror));

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Equal(503, exception.Status);

        // 备用端点连请求都没拿出来。
        Assert.Single(primary.Received);
        Assert.Empty(mirror.Received);
    }

    [Fact]
    public async Task Failover_WithHedging_EachEndpointGetsItsOwnBranches()
    {
        // 两个端点都压一点延迟：两条分支必须真的都发出去，否则先成功的那条会在对手还在路上时就把它取消，
        // 「每个端点各得一组分支」就测不到了。
        await using var primary = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            return new TestResponse(503, Body: Array.Empty<byte>());
        });
        await using var mirror = TestHttpServer.Start(async _ =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
            return TestJson.Response(new { mirror = true });
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } },
            Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
        });

        var response = await client.GetAsync("x");

        // 备用端点在对冲之外：主端点先跑完自己那组分支（2 条），全失败才换端点，镜像再拿一组。
        Assert.Equal(200, response.Status);
        Assert.True(Assert.IsType<JsonElement>(response.Data).GetProperty("mirror").GetBoolean());
        Assert.Equal(2, primary.Received.Count);
        Assert.Equal(2, mirror.Received.Count);
    }

    [Fact]
    public async Task Failover_SkipsEndpointThatCannotRunTheRequestedHttpVersion()
    {
        // 「跳过明文端点之后 https 镜像仍拿到请求」必须本机有 QUIC 才构造得出来：严格 h3 下所有 https 端点
        // 都要过 Http3Support 这道判据，无 QUIC 的机器上它们会被全部跳过，只剩「全被跳过」一种结果。
        // 所以本机空跑；跳过逻辑本身由 Failover_SkipsPlaintextEndpoint_UnderStrictHttp3 覆盖（不依赖 QUIC）。
        if (!Http3Support.IsSupported)
        {
            return;
        }

        var sent = new ConcurrentQueue<Uri>();
        using var client = new AxiosClient(handler: new PrimaryFailingHandler(sent));

        var response = await client.GetAsync("https://primary.test/x", c =>
        {
            c.Connection.HttpVersion = HttpVersion.Version30;
            c.Connection.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            c.Failover.Endpoints = new[] { new Uri("http://plain.test/"), new Uri("https://mirror.test/") };
        });

        // 明文端点 + 严格 h3 是不可能成立的配置：跳过它，第 2 个备用端点仍拿到这次请求。
        Assert.Equal(200, response.Status);
        Assert.Equal(
            new[] { "https://primary.test/x", "https://mirror.test/x" },
            sent.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public void Failover_EndpointsAreSnapshottedOnAssignment()
    {
        var endpoints = new List<Uri> { new("https://backup-a.example.com") };

        var config = new AxiosConfig { Failover = { Endpoints = endpoints } };

        // 赋值即快照：之后改调用方自己的列表不影响已生效的配置。
        endpoints.Add(new Uri("https://backup-b.example.com"));
        Assert.Single(config.Failover.Endpoints!);

        // 克隆共享同一版不可变快照（不必每请求复制）。
        Assert.Same(config.Failover.Endpoints, config.Clone().Failover.Endpoints);
    }

    [Fact]
    public async Task Failover_RejectsDuplicateEndpoints()
    {
        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = "http://127.0.0.1:1/",
            Failover = new()
            {
                // 写法不同但规范化后是同一个地址：同一份请求对同一个地址打两遍只会放大下游压力。
                Endpoints = new[] { new Uri("https://backup.example.com/api"), new Uri("https://BACKUP.example.com/api/") }
            }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrBadOptionValue, exception.Code);
    }

    [Fact]
    public async Task Failover_DoesNotCarryTheOriginalHostHeader()
    {
        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));
        await using var mirror = TestHttpServer.Start(_ => TestJson.Response(new { mirror = true }));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } }
        });

        // 调用方显式指定虚拟主机（反代 / 多站点同 IP 的常见做法）。
        var response = await client.GetAsync("x", c => c.Headers.Set("Host", "primary.example.com"));

        Assert.Equal(200, response.Status);
        Assert.Equal("primary.example.com", Assert.Single(primary.Received).Headers["Host"]);

        // HttpClient 只认 Headers.Host（它优先于 URI authority）：搬过去会让镜像按原端点的虚拟主机路由。
        Assert.Equal($"127.0.0.1:{mirror.Port}", Assert.Single(mirror.Received).Headers["Host"]);
    }

    [Fact]
    public void Failover_CloneRequest_CarriesOptionsAndKeepsHostOnlyWhenNotRetargeted()
    {
        using var template = new HttpRequestMessage(HttpMethod.Get, "https://primary.example.com/v1/users");
        template.Headers.Host = "virtual.example.com";
        template.Options.Set(new HttpRequestOptionsKey<bool>("probe"), true);

        // 同地址重发（重试）：请求头原样搬走，包括调用方指定的 Host。
        using var same = Attempt.CloneRequest(template);
        Assert.Equal("virtual.example.com", same.Headers.Host);
        Assert.True(same.Options.TryGetValue(new HttpRequestOptionsKey<bool>("probe"), out var kept) && kept);

        // 换目标地址（备用端点）：Host 交给 HttpClient 按新地址重算，其余选项照搬。
        using var retargeted = Attempt.CloneRequest(template, new Uri("https://mirror.example.com/v1/users"));
        Assert.Null(retargeted.Headers.Host);
        Assert.True(retargeted.Options.TryGetValue(new HttpRequestOptionsKey<bool>("probe"), out var copied) && copied);
    }

    [Fact]
    public async Task Failover_SkipsEndpointThatResolvesToTheSameUrl()
    {
        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,

            // 照抄 BaseAddress 当备用端点：拼出来的就是当前地址，换过去等于原地重发。
            Failover = new() { Endpoints = new[] { new Uri(primary.Url) } }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Single(primary.Received);
    }

    [Fact]
    public async Task Failover_DoesNotSkipEndpointThatAddsAPathPrefix()
    {
        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url + "api/",

            // 端点带路径前缀：请求 path 会整体接在后面（拼出 /api/api/x），与请求地址不同 —— 照打，不跳过。
            Failover = new() { Endpoints = new[] { new Uri(primary.Url + "api") } }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Equal(new[] { "/api/x", "/api/api/x" }, primary.Received.Select(request => request.Path).ToArray());
    }

    [Fact]
    public async Task Failover_SkipsPlaintextEndpoint_UnderStrictHttp3()
    {
        // 严格 h3 + 明文端点是不可能成立的组合，护栏在发请求前就拒 —— 与 QUIC 能力无关，所以这条在任何机器上都真跑。
        // 直接测中间件：走整个客户端的话，无 QUIC 的机器上「主端点 https + 严格 h3」会先被同一套护栏拦掉，只能空跑。
        var config = new AxiosConfig
        {
            Failover = new()
            {
                Endpoints = new[] { new Uri("http://plain-a.test/"), new Uri("http://plain-b.test/") }
            }
        };

        var sent = new List<Uri>();
        AxiosAttemptHandler next = (request, _) =>
        {
            sent.Add(request.RequestUri!);
            throw AxiosException.Network(config, request, new HttpRequestException("主端点建连失败"));
        };

        using var template = new HttpRequestMessage(HttpMethod.Get, "https://primary.test/x")
        {
            Version = HttpVersion.Version30,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };

        var handler = Attempt.Failover(next);
        var ctx = AxiosAttemptContext.From(config, template.RequestUri!, dataType: null, target: null, gate: null, deadlineTimestamp: DeadlineBudget.None);

        var exception = await Assert.ThrowsAsync<AxiosException>(async () => await handler(template, ctx));

        // 两个端点都被跳过（一个字节都没发），最终抛的是主端点那条真实失败，而不是配置错误。
        Assert.Equal(AxiosException.ErrNetwork, exception.Code);
        Assert.Equal(new[] { "https://primary.test/x" }, sent.Select(uri => uri.AbsoluteUri));
    }

    [Fact]
    public async Task Failover_WithRetryAndHedging_SendsAttemptsTimesEndpointsTimesBranches()
    {
        await using var primary = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));
        await using var mirror = TestHttpServer.Start(_ =>
            new TestResponse(503, Body: Array.Empty<byte>()));

        using var client = new AxiosClient(new AxiosConfig
        {
            BaseAddress = primary.Url,
            Retry = new() { Policy = RetryPolicy.Constant(1, TimeSpan.Zero) },
            Failover = new() { Endpoints = new[] { new Uri(mirror.Url) } },
            Hedging = new() { MaxHedges = 1, Delay = TimeSpan.Zero }
        });

        var exception = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync("x"));

        // 顺序契约：重试最外 × 备用端点居中 × 对冲最内，次数就是三者相乘。
        // 全失败没有胜者，所以每条分支都会真的跑完，计数确定（不会因取消少几条）。
        Assert.Equal(AxiosException.ErrBadResponse, exception.Code);
        Assert.Equal(4, primary.Received.Count);
        Assert.Equal(4, mirror.Received.Count);
    }

    /// <summary>主端点回 5xx、其余端点回 200，并记下每条真正发出去的地址（用来证明被跳过的端点一个字节都没发）。</summary>
    private sealed class PrimaryFailingHandler : HttpMessageHandler
    {
        private readonly ConcurrentQueue<Uri> _sent;

        public PrimaryFailingHandler(ConcurrentQueue<Uri> sent) => _sent = sent;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _sent.Enqueue(request.RequestUri!);

            var status = request.RequestUri!.Host == "primary.test"
                ? HttpStatusCode.InternalServerError
                : HttpStatusCode.OK;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                RequestMessage = request,
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            });
        }
    }

    /// <summary>记录每次 <see cref="IAxiosCircuitPolicy.ShouldAllow"/> 的维度：证明某条端点根本没进过管线。</summary>
    private sealed class HostCountingPolicy : IAxiosCircuitPolicy
    {
        private readonly List<string> _allowed = new();

        public IReadOnlyList<string> Allowed
        {
            get
            {
                lock (_allowed)
                {
                    return _allowed.ToArray();
                }
            }
        }

        public bool ShouldAllow(AxiosCircuitContext context)
        {
            lock (_allowed)
            {
                _allowed.Add(context.Host);
            }

            return true;
        }

        public TimeSpan GetRetryAfter(AxiosCircuitContext context) => TimeSpan.Zero;

        public void Record(AxiosCircuitContext context)
        {
            // 本策略只统计放行次数，不判故障。
        }
    }

    /// <summary>等测试服务器真的收到请求（异步启动的并发请求无法靠固定等待断言）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("等待测试服务器收到请求超时。");
            }

            await Task.Delay(10);
        }
    }
}
