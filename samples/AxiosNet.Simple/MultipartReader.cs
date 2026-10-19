using System.Text;

namespace AxiosNet.Simple;

/// <summary>
/// multipart/form-data 的一个部件（字段或文件）。
/// </summary>
/// <param name="Name">表单字段名（Content-Disposition 的 name）。</param>
/// <param name="FileName">文件名；普通字段为 null。</param>
/// <param name="ContentType">部件自己的 Content-Type；未声明为 null。</param>
/// <param name="Content">部件的精确字节（不含边界与结尾 CRLF）。</param>
internal sealed record MultipartPart(string Name, string? FileName, string? ContentType, byte[] Content)
{
    /// <summary>按 UTF-8 取文本（仅对普通字段有意义）。</summary>
    public string Text => Encoding.UTF8.GetString(Content);

    /// <summary>是不是文件部件。</summary>
    public bool IsFile => FileName is not null;
}

/// <summary>
/// 极简但**按字节精确**的 multipart/form-data 解析器（示例用，零依赖）。
/// <para>
/// 只做三件事：按 boundary 切分部件、解析 Content-Disposition/Content-Type、截出内容字节。
/// 关键是内容边界靠字节查找定位，因此二进制文件（含 CRLF、0x00）不会被截断或污染。
/// </para>
/// </summary>
internal static class MultipartReader
{
    private static readonly byte[] HeaderTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>解析 multipart 请求体。</summary>
    public static bool TryParse(
        string? contentType,
        byte[] body,
        out List<MultipartPart> parts,
        out string? error)
    {
        parts = new List<MultipartPart>();
        error = null;

        if (contentType is null
            || !contentType.Contains("multipart/form-data", StringComparison.OrdinalIgnoreCase))
        {
            error = "Content-Type 不是 multipart/form-data";
            return false;
        }

        var boundary = ExtractBoundary(contentType);
        if (string.IsNullOrEmpty(boundary))
        {
            error = "Content-Type 缺少 boundary";
            return false;
        }

        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var span = body.AsSpan();

        var index = span.IndexOf(delimiter);
        if (index < 0)
        {
            error = "请求体里找不到 boundary";
            return false;
        }

        while (true)
        {
            var pos = index + delimiter.Length;

            // 结束标记：--boundary--
            if (pos + 1 < span.Length && span[pos] == (byte)'-' && span[pos + 1] == (byte)'-')
            {
                break;
            }

            if (pos + 1 >= span.Length || span[pos] != (byte)'\r' || span[pos + 1] != (byte)'\n')
            {
                error = "boundary 后缺少 CRLF";
                return false;
            }

            pos += 2;

            // 下一个 boundary 的位置即当前部件内容的终点
            // 下一个 boundary 相对 pos 的偏移，就是当前部件的字节长度
            var next = span[pos..].IndexOf(delimiter);
            if (next < 0)
            {
                error = "缺少结束 boundary";
                return false;
            }

            var part = span.Slice(pos, next);

            // 内容末尾的 CRLF 属于 boundary 分隔符，不属于内容
            if (part.Length >= 2 && part[^2] == (byte)'\r' && part[^1] == (byte)'\n')
            {
                part = part[..^2];
            }

            var headerEnd = part.IndexOf(HeaderTerminator);
            if (headerEnd < 0)
            {
                error = "部件缺少头部结束标记（CRLFCRLF）";
                return false;
            }

            var headerText = Encoding.UTF8.GetString(part[..headerEnd]);
            var content = part[(headerEnd + HeaderTerminator.Length)..].ToArray();

            string? name = null;
            string? fileName = null;
            string? partContentType = null;

            foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                var colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                var headerName = line[..colon].Trim();
                var value = line[(colon + 1)..].Trim();

                if (headerName.Equals("Content-Disposition", StringComparison.OrdinalIgnoreCase))
                {
                    name = GetDispositionParameter(value, "name");
                    fileName = GetDispositionParameter(value, "filename");
                }
                else if (headerName.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    partContentType = value;
                }
            }

            if (name is null)
            {
                error = "部件缺少 Content-Disposition 的 name";
                return false;
            }

            parts.Add(new MultipartPart(name, fileName, partContentType, content));

            index = pos + next;
        }

        return true;
    }

    private static string? ExtractBoundary(string contentType)
    {
        foreach (var segment in contentType.Split(';'))
        {
            var trimmed = segment.Trim();
            if (!trimmed.StartsWith("boundary=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed["boundary=".Length..].Trim();

            // 允许带引号：boundary="----abc"
            return value.Length >= 2 && value[0] == '"' && value[^1] == '"'
                ? value[1..^1]
                : value;
        }

        return null;
    }

    /// <summary>从 Content-Disposition 里取参数（按 ';' 分段，避免 filename 被 name 误匹配）。</summary>
    private static string? GetDispositionParameter(string disposition, string key)
    {
        foreach (var segment in disposition.Split(';'))
        {
            var trimmed = segment.Trim();
            if (!trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed[(key.Length + 1)..].Trim();

            if (value.Length >= 2 && value[0] == '"')
            {
                var end = value.IndexOf('"', 1);
                return end < 0 ? value[1..] : value[1..end];
            }

            return value;
        }

        return null;
    }
}
