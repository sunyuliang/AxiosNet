using System.Text;
using System.Text.Json;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Tests.Serialization;

/// <summary>
/// <c>DateOnly</c> / <c>TimeOnly</c> 在聚合正文里的写出形态：两张现代资产必须给出同一份字节。
/// </summary>
/// <remarks>
/// net6.0 用的是框架自带的 System.Text.Json 6.x，它没有这两个类型的转换器。库在
/// <see cref="AxiosJson"/> 的默认选项里显式注册转换器，因此结论不取决于消费方把
/// System.Text.Json 包升到了哪个版本——升到 7/8 也好、留在 6.x 也好，线上字节都一样。
/// </remarks>
public sealed class DateOnlyOnNet6Tests
{
    /// <summary>默认正文里的单个 <c>DateOnly</c> 成员写成 ISO 日期，两张资产一致。</summary>
    [Fact]
    public async Task AggregateBody_WithDateOnly_WritesIsoDateOnEveryTargetFramework()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new DateOnlyRequest { When = new DateOnly(2026, 10, 22) });

        var received = Assert.Single(server.Received);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("2026-10-22", json.GetProperty("when").GetString());
    }

    /// <summary>默认正文里的单个 <c>TimeOnly</c> 成员与 net8.0 逐字节一致（全零小数秒整段省略）。</summary>
    [Fact]
    public async Task AggregateBody_WithTimeOnly_WritesRoundTripOnEveryTargetFramework()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new TimeOnlyRequest { At = new TimeOnly(1, 2, 3) });

        var received = Assert.Single(server.Received);
        var json = JsonSerializer.Deserialize<JsonElement>(received.Body);
        Assert.Equal("01:02:03", json.GetProperty("at").GetString());
    }

    /// <summary>成员显式标了 <c>[Format]</c> 时按用户给的格式写出——文本形态由调用方选。</summary>
    [Fact]
    public async Task AggregateBody_WithFormattedDateOnly_WritesFormattedText()
    {
        await using var server = TestHttpServer.Start(_ => TestJson.Response(new { ok = true }));
        using var client = new ApiClient(server.Url);

        await client.RequestFullAsync(new FormattedDateOnlyRequest { When = new DateOnly(2026, 10, 22) });

        var received = Assert.Single(server.Received);
        Assert.Equal("20261022", JsonSerializer.Deserialize<JsonElement>(received.Body).GetProperty("when").GetString());
    }

    /// <summary>响应方向同样能读回 <c>DateOnly</c>：写与读用同一对转换器。</summary>
    [Fact]
    public async Task AggregateBody_DateOnly_RoundTripsThroughResponse()
    {
        // 固定字节而不是 TestJson.Response：后者用测试工程自己的 System.Text.Json 序列化，
        // net6.0 的测试宿主同样没有 DateOnly 转换器，会掩盖被测的那条路径。
        await using var server = TestHttpServer.Start(_ => new TestResponse(
            Body: Encoding.UTF8.GetBytes("""{"when":"2026-10-22"}"""),
            ContentType: "application/json; charset=utf-8"));
        using var client = new ApiClient(server.Url);

        var response = await client.RequestFullAsync(new DateOnlyRequest { When = new DateOnly(2026, 10, 22) });

        Assert.Equal(new DateOnly(2026, 10, 22), response.Data!.When);
    }
}

/// <summary>带 <c>DateOnly</c> 成员的响应模型。</summary>
public sealed class DateOnlyResponse
{
    /// <summary>日期。</summary>
    public DateOnly When { get; set; }
}

/// <summary>带 <c>DateOnly</c> 成员的请求模型。</summary>
[Post("report")]
public sealed class DateOnlyRequest : IApiRequest<DateOnlyResponse>
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
