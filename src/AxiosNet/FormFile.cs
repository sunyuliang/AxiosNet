using AxiosNet.Internal;

namespace AxiosNet;

/// <summary>
/// multipart/form-data 中的一个文件部件：把 <see cref="FormFile"/>（或其集合）放进 data 的对象/字典字段里，
/// 并把 <see cref="AxiosConfig.ContentType"/> 设为 <c>multipart/form-data</c>。
/// 部件内容随请求释放（结束、异常或取消时都会 Dispose），传入的流不要在请求之外复用。
/// </summary>
public sealed class FormFile
{
    /// <summary>用流构造文件部件。</summary>
    /// <param name="fileName">发送给服务端的文件名（含扩展名）。</param>
    /// <param name="content">文件内容流，请求结束时会被释放。</param>
    /// <param name="contentType">部件的 Content-Type，默认 <c>application/octet-stream</c>。</param>
    public FormFile(string fileName, Stream content, string? contentType = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", nameof(fileName));
        }

        ArgumentNullException.ThrowIfNull(content);

        // 文件名写进 Content-Disposition 的 quoted-string，先校验非法字符。
        if (DispositionTokens.FindInvalidCharacter(fileName) is { } invalid)
        {
            throw new ArgumentException(
                $"文件名包含不能出现在 Content-Disposition 里的字符（{invalid}）：'{fileName}'。"
                + "双引号、反斜杠、CR/LF 与其它控制字符会破坏头部语法或让服务端收到的文件名错位；"
                + "中文可以正常使用（会按 RFC 5987 编码）。",
                nameof(fileName));
        }

        // 这个值最终写进部件的 Content-Type 头，先校验。
        if (contentType is not null)
        {
            AxiosHeaders.ValidateHeaderValue("Content-Type", contentType);
        }

        FileName = fileName;
        Content = content;
        ContentType = contentType;
    }

    /// <summary>用内存字节构造文件部件。</summary>
    public FormFile(string fileName, byte[] content, string? contentType = null)
        : this(fileName, ToReadOnlyStream(content), contentType)
    {
    }

    private static MemoryStream ToReadOnlyStream(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return new MemoryStream(content, writable: false);
    }

    /// <summary>文件名（含扩展名），写入 Content-Disposition 的 filename。</summary>
    public string FileName { get; }

    /// <summary>文件内容流。</summary>
    public Stream Content { get; }

    /// <summary>部件 Content-Type，null 表示 <c>application/octet-stream</c>。</summary>
    public string? ContentType { get; }

    private readonly string? _fieldName;

    /// <summary>覆盖部件字段名（Content-Disposition 的 name）；默认取所在字段/键名。</summary>
    /// <exception cref="ArgumentException">字段名含不能出现在 Content-Disposition 里的字符。</exception>
    public string? FieldName
    {
        get => _fieldName;
        init
        {
            if (value is not null && DispositionTokens.FindInvalidCharacter(value) is { } invalid)
            {
                throw new ArgumentException(
                    $"字段名包含不能出现在 Content-Disposition 里的字符（{invalid}）：'{value}'。"
                    + "双引号、反斜杠、CR/LF 与其它控制字符会破坏头部语法或让服务端收到的字段名错位；"
                    + "中文可以正常使用（会按 RFC 2047 编码）。",
                    nameof(FieldName));
            }

            _fieldName = value;
        }
    }

    /// <summary>打开本地文件，文件名取 <see cref="Path.GetFileName(string)"/>，第二个参数是 Content-Type。</summary>
    public static FormFile FromPath(string path, string? contentType = null)
        => FromPath(path, Path.GetFileName(path), contentType);

    /// <summary>打开本地文件并指定发送给服务端的文件名。文件流随请求一起释放。</summary>
    public static FormFile FromPath(string path, string fileName, string? contentType = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("The value cannot be an empty string or composed entirely of whitespace.", nameof(path));
        }

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);

        try
        {
            // 构造失败时关掉已打开的流。
            return new FormFile(fileName, stream, contentType);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>用内存字节构造文件部件（<see cref="FormFile(string, byte[], string?)"/> 的语义化写法）。</summary>
    public static FormFile FromBytes(string fileName, byte[] content, string? contentType = null)
        => new(fileName, content, contentType);
}
