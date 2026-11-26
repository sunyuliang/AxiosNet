using System.Text.Json;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// net6.0 上 <c>DateOnly</c> / <c>TimeOnly</c> 的聚合正文行为：默认正文包含这类成员时必须在序列化阶段报错。
/// 不能依赖 System.Text.Json 自己的结局——消费方把 System.Text.Json 包升到 7/8 后它会静默写出
/// <c>2026-10-22</c> 一类的字符串，调用方以为发对了。
/// </summary>
public sealed class DateOnlyOnNet6Tests
{
#if !NET8_0_OR_GREATER
    /// <summary>net6.0：单个 <c>DateOnly</c> 成员必须报错，消息由本库给出，且请求不发出。</summary>
    /// <remarks>断言消息文本而不是只断言类型名：System.Text.Json 自己的报错同样含 <c>DateOnly</c>，只查类型名的话守卫被删掉也照样绿。</remarks>
    [Fact]
    public async Task AggregateBody_WithDateOnly_OnNet6_ThrowsLibraryError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new ApiClient(server.Url);

        var request = new DateOnlyRequest { When = new DateOnly(2026, 10, 22) };

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestFullAsync(request));

        Assert.Contains("net6.0 的 System.Text.Json 不支持 DateOnly", ex.ToString());
        Assert.Empty(server.Received);
    }

    /// <summary>net6.0：单个 <c>TimeOnly</c> 成员同样必须由本库报错。</summary>
    [Fact]
    public async Task AggregateBody_WithTimeOnly_OnNet6_ThrowsLibraryError()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new ApiClient(server.Url);

        var request = new TimeOnlyRequest { At = new TimeOnly(1, 2, 3) };

        var ex = await Assert.ThrowsAsync<AxiosException>(() => client.RequestFullAsync(request));

        Assert.Contains("net6.0 的 System.Text.Json 不支持 TimeOnly", ex.ToString());
        Assert.Empty(server.Received);
    }

    /// <summary>net6.0：成员显式标了 <c>[Format]</c> 时按用户给的格式写出——文本形态是调用方选的，不走「不支持」拒绝。</summary>
    [Fact]
    public async Task AggregateBody_WithFormattedDateOnly_OnNet6_WritesFormattedText()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new FormattedDateOnlyRequest { When = new DateOnly(2026, 10, 22) });

        var received = Assert.Single(server.Received);
        Assert.Equal("20261022", JsonSerializer.Deserialize<JsonElement>(received.Body).GetProperty("when").GetString());
    }
#endif

#if NET8_0_OR_GREATER
    /// <summary>net8.0：同类请求正常写出 ISO 格式，证明上面的拒绝只属于 net6.0 的能力差异。</summary>
    [Fact]
    public async Task AggregateBody_WithDateOnly_OnNet8_WritesIsoDate()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new DateOnlyRequest { When = new DateOnly(2026, 10, 22) });

        var received = Assert.Single(server.Received);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("2026-10-22", json.GetProperty("when").GetString());
    }
#endif
}

/// <summary>带 <c>DateOnly</c> 成员的请求模型。</summary>
[Post("report")]
public sealed class DateOnlyRequest : IApiRequest<Person>
{
    /// <summary>日期。</summary>
    public DateOnly When { get; set; }
}

/// <summary>带 <c>TimeOnly</c> 成员的请求模型。</summary>
[Post("report-time")]
public sealed class TimeOnlyRequest : IApiRequest<Person>
{
    /// <summary>时刻。</summary>
    public TimeOnly At { get; set; }
}

/// <summary>带显式格式的 <c>DateOnly</c> 成员的请求模型。</summary>
[Post("report-formatted")]
public sealed class FormattedDateOnlyRequest : IApiRequest<Person>
{
    /// <summary>日期（显式格式）。</summary>
    [Format("yyyyMMdd")]
    public DateOnly When { get; set; }
}
