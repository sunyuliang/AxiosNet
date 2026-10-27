
using AxiosNet.Pooling;
using AxiosNet.Serialization;
using AxiosNet.Tests.Infrastructure;
using AxiosNet.WebAPI;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Dynamic;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net.Security;
using System.Net;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;

namespace AxiosNet.Tests.Requests;

// 本目录专用测试替身（请求模型 / 流 / Handler），供同目录多个用例类共用；各类型自带一句 summary。

/// <summary>
/// 只读、非 seekable 的流：模拟网络流 / 管道流（<see cref="Stream.Length"/> 不可知），
/// 用来覆盖「内容长度未知时手写 Content-Length」的逃生口。
/// </summary>
internal sealed class NonSeekableStream : Stream
{
    private readonly MemoryStream _inner;

    public NonSeekableStream(byte[] payload) => _inner = new MemoryStream(payload, writable: false);

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>XML 序列化用的公开 DTO（XmlSerializer 要求公开无参构造 + 公开可读写属性）。</summary>
public sealed class XmlProbeDto
{
    public string Name { get; set; } = string.Empty;
}

[HttpApi("GE(T", "x")]
public sealed class BadMethodRequest : IApiRequest<Person>
{
}
