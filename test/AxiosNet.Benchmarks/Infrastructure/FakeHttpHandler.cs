using System.Net;
using System.Net.Http.Headers;

namespace AxiosNet.Benchmarks.Infrastructure;

/// <summary>
/// 无 socket 的响应源：每次请求返回同一份预编码字节。
/// <para>
/// 用途是把「网络」从测量里摘掉，只留客户端 CPU（配置合并、URL 组装、编码、反序列化、分帧）。
/// 代价是每次请求都要 new 一个 <see cref="HttpResponseMessage"/> + <see cref="ByteArrayContent"/>
/// （约 2 次分配），这份开销对同一类里的各个变体是<b>恒定</b>的，看差值 / 比值时会被抵消；
/// 看绝对值时要知道它已经含在里面。
/// </para>
/// <para>
/// 需要真实往返（含建连、TCP、框架收发）的场景请用 <see cref="LoopbackHttpServer"/>，
/// 两者不要混在一个结论里用。
/// </para>
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly byte[] _body;
    private readonly MediaTypeHeaderValue _contentType;

    public FakeHttpHandler(byte[] body, string contentType)
    {
        _body = body;
        _contentType = MediaTypeHeaderValue.Parse(contentType);
    }

    public FakeHttpHandler(string body, string contentType = "application/json; charset=utf-8")
        : this(Encoding.UTF8.GetBytes(body), contentType)
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var content = new ByteArrayContent(_body);
        content.Headers.ContentType = _contentType;

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content,
            RequestMessage = request
        });
    }
}
