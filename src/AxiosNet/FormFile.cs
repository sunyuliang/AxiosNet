using AxiosNet.Internal;

namespace AxiosNet;

/// <summary>
/// multipart/form-data 中的一个文件部件。
/// <para>
/// 用法：把 <see cref="FormFile"/>（或其集合）放进 data 的对象/字典字段里，
/// 并把 <see cref="AxiosConfig.ContentType"/> 设为 <c>multipart/form-data</c>：
/// <code>
/// await http.PostAsync("upload", new
/// {
///     title = "年报",
///     files = new[]
///     {
///         new FormFile("a.pdf", File.OpenRead("a.pdf"), "application/pdf"),
///         FormFile.FromPath("b.pdf")
///     }
/// }, c => c.ContentType = "multipart/form-data");
/// </code>
/// </para>
/// <para>
/// 生命周期：部件内容随请求释放（结束、异常或取消时都会 Dispose），
/// 因此传入的流不要在请求之外复用；需要复用请自己读成 byte[]。
/// </para>
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

        // 文件名写进 Content-Disposition 的 filename="..."（quoted-string）：引号与换行会让
        // MultipartFormDataContent 抛异常（且错误信息里看不出是文件名），反斜杠与控制字符则静默错乱。
        if (DispositionTokens.FindInvalidCharacter(fileName) is { } invalid)
        {
            throw new ArgumentException(
                $"文件名包含不能出现在 Content-Disposition 里的字符（{invalid}）：'{fileName}'。"
                + "双引号、反斜杠、CR/LF 与其它控制字符会破坏头部语法或让服务端收到的文件名错位；"
                + "中文可以正常使用（会按 RFC 5987 编码）。",
                nameof(fileName));
        }

        // 这个值最终写进部件的 Content-Type 头（经 TryAddWithoutValidation，不拦控制字符）。
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

    /// <summary>
    /// 覆盖部件字段名（Content-Disposition 的 name）。
    /// 默认取所在字段/键名；同一集合里需要不同字段名时使用。
    /// </summary>
    public string? FieldName { get; init; }

    /// <summary>
    /// 打开本地文件；发送给服务端的文件名取 <see cref="Path.GetFileName(string)"/>，
    /// 第二个参数是 <b>Content-Type</b>。
    /// <para>
    /// 注意：与 <see cref="FromPath(string, string, string?)"/> 在「传两个字符串」时无法区分，
    /// 重载决议固定落到这里 —— <c>FromPath(path, "report.txt")</c> 会把 <c>report.txt</c> 当成 Content-Type。
    /// </para>
    /// </summary>
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
            // 文件名校验在构造函数里；失败时异常从这里抛出，已打开的流必须在这里关掉，
            // 否则文件句柄永久泄漏（Windows 上连删除都会被拒）。
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
