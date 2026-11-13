using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using AxiosNet.Internal;
using AxiosNet.Pooling;

namespace AxiosNet.Compat.Tests;

/// <summary>兼容资产的行为正确性：这些类型 / 分支在 net6.0 / net8.0 资产上根本不存在。</summary>
public sealed class CompatBehaviorTests
{
    [Fact]
    public async Task WaitAsync_取消抛_TaskCanceledException_与_BCL_一致()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();

        var wait = NetStandard20Extensions.WaitAsync(pending.Task, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<TaskCanceledException>(() => wait);
    }

    [Fact]
    public async Task WaitAsync_超时抛_TimeoutException()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await Assert.ThrowsAsync<TimeoutException>(
            () => NetStandard20Extensions.WaitAsync(pending.Task, TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task WaitAsync_透传结果与原始异常()
    {
        Assert.Equal(42, await NetStandard20Extensions.WaitAsync(Task.FromResult(42), TimeSpan.FromSeconds(5)));

        var faulted = Task.FromException(new InvalidOperationException("boom"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => NetStandard20Extensions.WaitAsync(faulted, TimeSpan.FromSeconds(5)));
        Assert.Equal("boom", ex.Message);
    }

    [Fact]
    public async Task 带令牌的_HttpContent_重载_在进入边界就生效()
    {
        using var content = new StringContent("hello", Encoding.UTF8, "text/plain");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // 测试工程本身按 net8.0 编译，BCL 类型走 net8 参考程序集（这些重载已存在），
        // 所以必须显式调 shim 才能验证兼容资产上的实现。
        // 对照基线：上一版 ns2.0 shim 的写法（丢弃令牌），取消后照样完整执行。
        Assert.Equal("hello", Encoding.UTF8.GetString(await OldReadAsByteArrayIgnoringToken(content)));

        // 现实现：进入边界就抛取消。
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NetStandard20Extensions.ReadAsByteArrayAsync(content, cts.Token));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NetStandard20Extensions.ReadAsStreamAsync(content, cts.Token));

        using var target = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => NetStandard20Extensions.CopyToAsync(content, target, cts.Token));
        Assert.Equal(0, target.Length);

        // 未取消时三条路径行为不变。
        Assert.Equal("hello", Encoding.UTF8.GetString(await NetStandard20Extensions.ReadAsByteArrayAsync(content, default)));
        using var stream = await NetStandard20Extensions.ReadAsStreamAsync(content, default);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Assert.Equal("hello", await reader.ReadToEndAsync());
    }

    [Fact]
    public void IsByRefLike_正确识别_span_与普通类型()
    {
        Assert.True(PlatformCompat.IsByRefLike(typeof(Span<int>)));
        Assert.True(PlatformCompat.IsByRefLike(typeof(ReadOnlySpan<byte>)));
        Assert.False(PlatformCompat.IsByRefLike(typeof(string)));
        Assert.False(PlatformCompat.IsByRefLike(typeof(AxiosClient)));
    }

    [Fact]
    public void 兼容_handler_映射连接数上限与_TLS_协议版本()
    {
        var options = new ConnectionPoolOptions
        {
            MaxConnectionsPerServer = 7,
            EnabledSslProtocols = SslProtocols.Tls12
        };

        using var handler = (HttpClientHandler)DefaultHandlerFactory.Create(options, options.ProjectDefaultTransport());

        Assert.Equal(7, handler.MaxConnectionsPerServer);
        Assert.Equal(SslProtocols.Tls12, handler.SslProtocols);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate, handler.AutomaticDecompression);
    }

    [Fact]
    public void 兼容_handler_转发证书校验回调()
    {
        var calls = 0;
        var options = new ConnectionPoolOptions
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) =>
            {
                calls++;
                return true;
            }
        };

        using var handler = (HttpClientHandler)DefaultHandlerFactory.Create(options, options.ProjectDefaultTransport());
        var accepted = handler.ServerCertificateCustomValidationCallback!(
            new HttpRequestMessage(),
            null,
            null,
            SslPolicyErrors.RemoteCertificateNameMismatch);

        Assert.True(accepted);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task gzip_请求体压缩可解回原文()
    {
        var payload = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("压缩载荷-", 512)));
        var config = new AxiosConfig { Compression = AxiosCompression.Gzip };

        using var content = Compression.WrapBytes(payload, config);
        Assert.Contains("gzip", content.Headers.ContentEncoding);

        var compressed = await content.ReadAsByteArrayAsync();
        Assert.True(compressed.Length < payload.Length, $"压缩后 {compressed.Length} 应小于原文 {payload.Length}");

        using var gzip = new GZipStream(new MemoryStream(compressed), CompressionMode.Decompress);
        using var output = new MemoryStream();
        await gzip.CopyToAsync(output);
        Assert.Equal(payload, output.ToArray());
    }

    [Fact]
    public async Task 兼容资产上_deflate_在选项校验阶段被拒绝()
    {
        var client = new AxiosClient();

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.PostAsync<string>(
            "http://localhost:1/",
            new { value = 1 },
            config => config.Compression = AxiosCompression.Deflate));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("gzip", ex.Message);
    }

    [Fact]
    public void Clone_保留兼容资产上的全部公开选项()
    {
        IWebProxy proxy = new WebProxy("http://127.0.0.1:3128");
        var credentials = new NetworkCredential("user", "pass");
        var factory = new Func<PooledHandlerContext, HttpMessageHandler>(_ => new NullHandler());
        var certificateValidation = new RemoteCertificateValidationCallback((_, _, _, _) => true);

        var source = new ConnectionPoolOptions
        {
            MaxConnectionsPerServer = 9,
            PooledConnectionLifetime = TimeSpan.FromMinutes(3),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(4),
            MaxResponseHeadersLength = 128,
            AllowAutoRedirect = false,
            MaxAutomaticRedirections = 3,
            UseCookies = false,
            AutomaticDecompression = false,
            Decompression = DecompressionMethods.None,
            MaxEndpointCount = 12,
            Proxy = proxy,
            Credentials = credentials,
            ServerCertificateCustomValidationCallback = certificateValidation,
            EnabledSslProtocols = SslProtocols.Tls12,
            HandlerFactory = factory
        };

        var copy = source.Clone();

        Assert.Equal(9, copy.MaxConnectionsPerServer);
        Assert.Equal(TimeSpan.FromMinutes(3), copy.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(2), copy.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(4), copy.ConnectTimeout);
        Assert.Equal(128, copy.MaxResponseHeadersLength);
        Assert.False(copy.AllowAutoRedirect);
        Assert.Equal(3, copy.MaxAutomaticRedirections);
        Assert.False(copy.UseCookies);
        Assert.False(copy.AutomaticDecompression);
        Assert.Equal(DecompressionMethods.None, copy.Decompression);
        Assert.Equal(12, copy.MaxEndpointCount);
        Assert.Same(proxy, copy.Proxy);
        Assert.Same(credentials, copy.Credentials);
        Assert.Same(certificateValidation, copy.ServerCertificateCustomValidationCallback);
        Assert.Equal(SslProtocols.Tls12, copy.EnabledSslProtocols);
        Assert.Same(factory, copy.HandlerFactory);
    }

    [Fact]
    public void 兼容资产上不可映射的连接级选项_在建池阶段被拒绝()
    {
        // 默认值放行：HttpClientHandler 只能承载这些默认行为。
        using var defaults = new HttpConnectionPool(new ConnectionPoolOptions());

        var connectTimeout = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { ConnectTimeout = TimeSpan.FromSeconds(4) }));
        Assert.Equal(AxiosException.ErrBadOptionValue, connectTimeout.Code);
        Assert.Contains("ConnectTimeout", connectTimeout.Message);

        var ping = Assert.Throws<AxiosException>(() =>
            new HttpConnectionPool(new ConnectionPoolOptions { KeepAlivePingDelay = TimeSpan.FromSeconds(30) }));
        Assert.Equal(AxiosException.ErrBadOptionValue, ping.Code);
        Assert.Contains("KeepAlivePingDelay", ping.Message);

        AssertRejected("PooledConnectionLifetime", new ConnectionPoolOptions { PooledConnectionLifetime = TimeSpan.FromMinutes(3) });
        AssertRejected("PooledConnectionIdleTimeout", new ConnectionPoolOptions { PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1) });
        AssertRejected("EnableMultipleHttp2Connections", new ConnectionPoolOptions { EnableMultipleHttp2Connections = false });
        AssertRejected("KeepAlivePingDelay / KeepAlivePingTimeout", new ConnectionPoolOptions { KeepAlivePingTimeout = TimeSpan.FromSeconds(5) });

        // 自定义 HandlerFactory 自行决定这些选项如何落地：构造期不拒绝。
        using var custom = new HttpConnectionPool(new ConnectionPoolOptions
        {
            ConnectTimeout = TimeSpan.FromSeconds(4),
            HandlerFactory = _ => new HttpClientHandler()
        });

        static void AssertRejected(string optionName, ConnectionPoolOptions options)
        {
            var ex = Assert.Throws<AxiosException>(() => new HttpConnectionPool(options));
            Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
            Assert.Contains(optionName, ex.Message);
        }
    }

    [Fact]
    public void 命名传输档的工厂不能豁免默认档的连接级选项()
    {
        // 默认档只会用 options.HandlerFactory（见 PooledEndpoint.CreateHandler），
        // 命名档的工厂只作用于该档；因此默认档上 ConnectTimeout 依然无处落地，必须拒绝。
        var options = new ConnectionPoolOptions { ConnectTimeout = TimeSpan.FromSeconds(4) };
        options.AddTransport("alt", new ConnectionTransportOptions { HandlerFactory = _ => new HttpClientHandler() });

        var ex = Assert.Throws<AxiosException>(() => new HttpConnectionPool(options));

        Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
        Assert.Contains("ConnectTimeout", ex.Message);
    }

    [Fact]
    public async Task 兼容资产上显式要求_HTTP2_在发送前被拒绝_不看策略()
    {
        var client = new AxiosClient();
        var policies = new[]
        {
            HttpVersionPolicy.RequestVersionOrLower,
            HttpVersionPolicy.RequestVersionExact,
            HttpVersionPolicy.RequestVersionOrHigher
        };

        foreach (var policy in policies)
        {
            var ex = await Assert.ThrowsAsync<AxiosException>(() => client.GetAsync<string>(
                "http://localhost:1/",
                config =>
                {
                    config.Transport.HttpVersion = new Version(2, 0);
                    config.Transport.VersionPolicy = policy;
                }));

            Assert.Equal(AxiosException.ErrBadOptionValue, ex.Code);
            Assert.Contains("HTTP/1.1", ex.Message);
        }
    }

    [Fact]
    public void Span_拼接的兼容实现与_string_Concat_一致()
    {
        Assert.Equal("ab", SpanStringConcat.Concat("a".AsSpan(), "b".AsSpan()));
        Assert.Equal("abc", SpanStringConcat.Concat("a".AsSpan(), "b".AsSpan(), "c".AsSpan()));
        Assert.Equal("abcd", SpanStringConcat.Concat("a".AsSpan(), "b".AsSpan(), "c".AsSpan(), "d".AsSpan()));
        Assert.Equal("中文😀x", SpanStringConcat.Concat("中文".AsSpan(), "😀".AsSpan(), default, "x".AsSpan()));
        Assert.Equal(string.Empty, SpanStringConcat.Concat(default, default));
    }

    /// <summary>上一版 ns2.0 shim 的写法（对照基线）：令牌被丢弃。</summary>
    private static Task<byte[]> OldReadAsByteArrayIgnoringToken(HttpContent content) => content.ReadAsByteArrayAsync();

    private sealed class NullHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
