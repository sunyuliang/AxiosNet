using System.Net;
using System.Net.Http.Headers;

namespace AxiosNet.Benchmarks.Infrastructure;

/// <summary>
/// 无 socket 的响应源：每次请求返回同一份预编码字节，把网络从测量里摘掉，只留客户端 CPU。每次请求固定多出约两次分配，看差值时被抵消。
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

    /// <summary>直接回固定正文的 200 响应，不走网络，让基准只测库自身开销。</summary>
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
