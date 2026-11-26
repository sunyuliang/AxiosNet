
using System.Text;
using AxiosNet.Tests.Infrastructure;

namespace AxiosNet.Tests.Caching;

/// <summary>同键并发：单飞共享、跟随者失败与取消、总预算约束。</summary>
public sealed partial class CacheTests
{
    [Fact]
    public async Task ConcurrentRequests_SendOnce()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var tasks = new List<Task<AxiosResponse<Person>>>();
        for (var i = 0; i < 10; i++)
        {
            tasks.Add(client.GetAsync<Person>(server.Url + "x"));
        }

        // 等第一次发送真的到了服务端，再放行。
        await WaitUntilAsync(() => server.Received.Count == 1);
        await Task.Delay(100);
        release.SetResult(true);

        var responses = await Task.WhenAll(tasks);

        Assert.Single(server.Received);
        Assert.All(responses, response => Assert.Equal("ada", response.Data?.Name));

        // 只有真正发送的那一个是「有底层请求」的，其余 9 个走缓存路径。
        Assert.Equal(1, responses.Count(response => response.Request is not null));
    }

    [Fact]
    public async Task SingleFlight_SharesHttpStatusFailure_FollowerUsesItsOwnValidateStatus()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        await using var server = TestHttpServer.Start(async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return TestJson.Response(new { error = "boom" }, statusCode: 500);
        });

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        // 领导者用默认 ValidateStatus：这次 500 会变成 ERR_BAD_RESPONSE。
        var leader = client.GetAsync<Person>(server.Url + "x", c => c.Headers.Set("X-Viewer", "leader"));

        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);

        // 跟随者共享领导者的 500 响应，但按自己的 ValidateStatus 判成成功；服务端不再发第二次。
        var follower = client.GetAsync<Person>(server.Url + "x", c =>
        {
            c.Headers.Set("X-Viewer", "follower");
            c.ValidateStatus = _ => true;
        });

        await Task.Delay(100);
        release.SetResult(true);

        var error = await Assert.ThrowsAsync<AxiosException>(() => leader);
        Assert.NotNull(error.Response); // 确实是 HTTP 状态类失败，而不是传输 / 取消类
        Assert.Equal("leader", error.Config?.Headers.Get("X-Viewer"));

        var response = await follower;
        Assert.Equal(500, response.Status);
        Assert.Equal("follower", response.Config?.Headers.Get("X-Viewer")); // 响应对象属于跟随者自己
        Assert.Null(response.Request); // 跟随者没有发请求
        Assert.NotSame(error.Response, response); // 每人一份独立响应对象
        Assert.Equal(1, calls); // 核心验收：整条链路服务端只收到 1 次
    }

// 命中的 Age 与多值头

    [Fact]
    public async Task ConcurrentRequests_SendEveryTime_WhenSingleFlightDisabled()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache(o => o.SingleFlight = false) });

        var tasks = new List<Task<AxiosResponse<Person>>>();
        for (var i = 0; i < 5; i++)
        {
            tasks.Add(client.GetAsync<Person>(server.Url + "x"));
        }

        await WaitUntilAsync(() => server.Received.Count == 5);
        release.SetResult(true);
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task SingleFlightDisabled_StillStoresEntries()
    {
        // 关掉单飞只是「不合并并发」，落库照旧：否则这条策略下的缓存永远没有条目、命中率为零。
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new Person("ada", 36)));
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache(o => o.SingleFlight = false) });

        var first = await client.GetAsync<Person>(server.Url + "x");
        var second = await client.GetAsync<Person>(server.Url + "x");

        Assert.Single(server.Received);
        Assert.NotNull(first.Request);
        Assert.Null(second.Request);
        Assert.Equal("ada", second.Data?.Name);
    }

    [Fact]
    public async Task HttpStatusFailure_IsSharedAcrossWaiters_EachUsesItsOwnPolicy()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var server = TestHttpServer.Start(async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return TestJson.Response(new { error = "boom" }, statusCode: 500);
        });

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });
        var entered = 0;
        client.Interceptors.Request.Use(c =>
        {
            Interlocked.Increment(ref entered);
            return Task.FromResult(c);
        });

        var leader = client.GetAsync<Person>(server.Url + "x", c => c.Headers.Set("X-Viewer", "leader"));
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);

        // 4 个并发跟随者：第 1 个自己接受 500，其余 3 个各自按默认口径报错。
        var followers = new List<Task<AxiosResponse<Person>>>();
        for (var i = 0; i < 4; i++)
        {
            var viewer = "f" + i;
            followers.Add(client.GetAsync<Person>(server.Url + "x", c =>
            {
                c.Headers.Set("X-Viewer", viewer);
                if (viewer == "f0")
                {
                    c.ValidateStatus = _ => true;
                }
            }));
        }

        await WaitUntilAsync(() => Volatile.Read(ref entered) == 5);
        await Task.Delay(100);
        release.SetResult(true);

        var leaderError = await Assert.ThrowsAsync<AxiosException>(() => leader);
        Assert.Equal(500, leaderError.Status);
        Assert.Equal("leader", leaderError.Config?.Headers.Get("X-Viewer"));

        var accepted = await followers[0];
        Assert.Equal(500, accepted.Status);
        Assert.Equal("f0", accepted.Config?.Headers.Get("X-Viewer"));
        Assert.Null(accepted.Request);

        for (var i = 1; i < 4; i++)
        {
            var error = await Assert.ThrowsAsync<AxiosException>(() => followers[i]);
            Assert.Equal(AxiosException.ErrBadResponse, error.Code);
            Assert.Equal(500, error.Status);
            Assert.Equal("f" + i, error.Config?.Headers.Get("X-Viewer"));
            Assert.NotNull(error.Response);
            Assert.NotSame(leaderError.Response, error.Response); // 每个跟随者一份独立响应对象
        }

        Assert.Equal(1, calls); // 核心验收：4 个跟随者 + 1 个领导者，服务端只收到 1 次
    }

    [Fact]
    public async Task SingleFlight_StreamResponse_FallsBackToResend()
    {
        // 流式响应只能读一次，不能共享：领导者的流无法复用，跟随者必须各自补发。
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var body = Encoding.UTF8.GetBytes("stream-error");

        await using var server = TestHttpServer.Start(async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return new TestResponse(500, Body: body);
        });

        // 默认 ShouldCache 排除 Stream；这里显式允许，确保确实走到单飞路径。
        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = NewCache(options => options.ShouldCache = _ => true)
        });

        var leader = client.GetAsync<Stream>(server.Url + "s");
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);

        var followers = new List<Task<AxiosResponse<Stream>>>();
        for (var i = 0; i < 3; i++)
        {
            followers.Add(client.GetAsync<Stream>(server.Url + "s"));
        }

        await Task.Delay(100);
        release.SetResult(true);

        await Assert.ThrowsAsync<AxiosException>(() => leader);
        foreach (var follower in followers)
        {
            await Assert.ThrowsAsync<AxiosException>(() => follower);
        }

        Assert.Equal(4, calls); // 领导者 1 次 + 3 个跟随者各自补发
    }

    [Fact]
    public async Task SingleFlight_ErrorBodyOverShareLimit_FallsBackToResend()
    {
        // 共享正文受 MaxSharedFailureBodyBytes 约束；超限时退回各自补发，避免内存放大。
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        await using var server = TestHttpServer.Start(async _ =>
        {
            Interlocked.Increment(ref calls);
            await release.Task;
            return TestJson.Response(new { error = new string('x', 4096) }, statusCode: 500);
        });

        using var client = new AxiosClient(new AxiosConfig
        {
            Caching = NewCache(options => options.MaxSharedFailureBodyBytes = 1024)
        });

        var leader = client.GetAsync<Person>(server.Url + "x", c => c.Headers.Set("X-Viewer", "leader"));
        await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);

        var accepted = client.GetAsync<Person>(server.Url + "x", c =>
        {
            c.Headers.Set("X-Viewer", "accepted");
            c.ValidateStatus = _ => true;
        });
        var rejected = client.GetAsync<Person>(server.Url + "x", c => c.Headers.Set("X-Viewer", "rejected"));

        await Task.Delay(100);
        release.SetResult(true);

        await Assert.ThrowsAsync<AxiosException>(() => leader);
        await Assert.ThrowsAsync<AxiosException>(() => rejected);

        var response = await accepted;
        Assert.Equal(500, response.Status);
        Assert.Equal("accepted", response.Config?.Headers.Get("X-Viewer"));

        Assert.Equal(3, calls); // 超限：领导者 1 次 + 2 个跟随者各自补发
    }

    [Fact]
    public async Task FollowerCancellation_DoesNotCancelTheSharedRequest()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var leader = client.GetAsync<Person>(server.Url + "x");
        await WaitUntilAsync(() => server.Received.Count == 1);

        using var cts = new CancellationTokenSource();
        var follower = client.GetAsync<Person>(server.Url + "x", config => config.CancellationToken = cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        var canceled = await Assert.ThrowsAsync<AxiosException>(() => follower);
        Assert.Equal(AxiosException.ErrCanceled, canceled.Code);

        release.SetResult(true);
        var response = await leader;

        Assert.Equal("ada", response.Data?.Name);
        Assert.Single(server.Received);
    }

    [Fact]
    public async Task LeaderCancellation_DoesNotFailTheFollower()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        using var leaderCts = new CancellationTokenSource();
        var leader = client.GetAsync<Person>(server.Url + "x", config => config.CancellationToken = leaderCts.Token);
        await WaitUntilAsync(() => server.Received.Count == 1);

        var follower = client.GetAsync<Person>(server.Url + "x");
        await Task.Delay(100);

        leaderCts.Cancel();
        await Assert.ThrowsAsync<AxiosException>(() => leader);

        // 领导者被它自己的调用方取消：跟随者自己发一次，而不是跟着失败。
        await WaitUntilAsync(() => server.Received.Count == 2);
        release.SetResult(true);

        var response = await follower;
        Assert.Equal("ada", response.Data?.Name);
    }

    [Fact]
    public async Task FollowerWait_IsBoundedByTotalTimeout()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });

        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var leader = client.GetAsync<Person>(server.Url + "x");
        await WaitUntilAsync(() => server.Received.Count == 1);

        // 领导者慢，跟随者的总预算先到：不能挂在原地无限等，按统一错误模型报「总预算耗尽」。
        var follower = client.GetAsync<Person>(
            server.Url + "x",
            config => config.Resilience.TotalTimeout = TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<AxiosException>(() => follower);
        Assert.True(error.IsTotalTimeoutExceeded);
        Assert.Equal(AxiosException.ErrConnAborted, error.Code);

        release.SetResult(true);
        await leader;
    }

    [Fact]
    public async Task BoundedFollower_SendsItsOwn_WhenLeaderFailsWithCancellation()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = TestHttpServer.Start(async _ =>
        {
            await release.Task;
            return TestJson.Response(new Person("ada", 36));
        });
        using var client = new AxiosClient(new AxiosConfig { Caching = NewCache() });

        var thrown = 0;
        client.Interceptors.Response.Use(response =>
        {
            // 领导者那次响应上抛一个未经翻译的取消（用户拦截器的典型写法）。
            if (Interlocked.Exchange(ref thrown, 1) == 0)
            {
                throw new OperationCanceledException();
            }

            return response;
        });

        var leader = client.GetAsync<Person>(server.Url + "x");
        await WaitUntilAsync(() => server.Received.Count == 1);

        // 跟随者有总预算：等待被领导者失败唤醒时，不能把「领导者取消」误报成本次请求的总预算耗尽。
        var follower = client.GetAsync<Person>(
            server.Url + "x",
            config => config.Resilience.TotalTimeout = TimeSpan.FromSeconds(1));
        await Task.Delay(150);

        release.SetResult(true);
        await Assert.ThrowsAnyAsync<Exception>(() => leader);

        var response = await follower;
        Assert.Equal("ada", response.Data?.Name);
        Assert.Equal(2, server.Received.Count);
    }

}
