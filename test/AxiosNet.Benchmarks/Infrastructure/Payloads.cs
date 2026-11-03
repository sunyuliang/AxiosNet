using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Benchmarks.Infrastructure;

// 基准共用的模型与载荷。所有响应体都在静态构造里编码成 UTF-8 字节：
// 基准方法里不做字符串编码，测到的才是被测路径本身。

/// <summary>典型的小对象响应（5 个字段 + 嵌套对象），用于「全链路」与「强类型反序列化」。</summary>
public sealed class UserDto
{
    /// <summary>用户 ID。</summary>
    public int Id { get; set; }

    /// <summary>用户名：覆盖字符串解码与转义。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>布尔字段：覆盖 <c>true</c> / <c>false</c> 的字面量解析。</summary>
    public bool Active { get; set; }

    /// <summary>字符串数组：覆盖数组节点与元素分配。</summary>
    public string[] Tags { get; set; } = [];

    /// <summary>嵌套对象：覆盖一层对象图构建。</summary>
    public AddressDto? Address { get; set; }
}

/// <summary><see cref="UserDto"/> 的嵌套对象。</summary>
public sealed class AddressDto
{
    /// <summary>嵌套对象的城市字段。</summary>
    public string City { get; set; } = string.Empty;

    /// <summary>嵌套对象的邮编字段。</summary>
    public string Zip { get; set; } = string.Empty;
}

/// <summary>大正文 JSON 数组的元素：数字 / 字符串 / 浮点三种字段，覆盖常见对象图成本。</summary>
public sealed class JsonItemDto
{
    /// <summary>大数组元素里的整数列。</summary>
    public int Id { get; set; }

    /// <summary>大数组元素里的字符串列。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>大数组元素里的浮点列。</summary>
    public double Value { get; set; }
}

/// <summary>流式响应的单帧（模拟 LLM token 流的一行）。</summary>
public sealed class StreamFrame
{
    /// <summary>帧序号。</summary>
    public int Id { get; set; }

    /// <summary>本帧正文（模拟 token 片段）。</summary>
    public string Delta { get; set; } = string.Empty;

    /// <summary>是否最后一帧。</summary>
    public bool Done { get; set; }
}

/// <summary>流式基准用的请求模型：路由只决定打哪个地址，帧格式由调用处指定。</summary>
[Get("stream")]
public sealed class StreamRequest : IApiRequest<StreamFrame>
{
}

/// <summary>全部基准共用的预编码载荷：字符串在这里编码成 UTF-8 字节一次，基准方法内不再做编码。</summary>
internal static class Payloads
{
    /// <summary>大正文用的确定性内容（避免全零被中间层特殊对待，也让内容校验有意义）。</summary>
    public const byte LargeBodyFill = 0x5A;

    /// <summary>构造指定大小的正文载荷；只在 <c>GlobalSetup</c> 里调用一次，基准方法内不再分配。</summary>
    public static byte[] Large(int bytes)
    {
        var payload = new byte[bytes];
        Array.Fill(payload, LargeBodyFill);
        return payload;
    }

    /// <summary>
    /// 构造不小于 <paramref name="bytes"/> 的 JSON 数组正文（同一对象重复），
    /// 用于「大正文 JSON 响应的分配量尺」。只在 <c>GlobalSetup</c> 里调用一次。
    /// </summary>
    public static byte[] LargeJson(int bytes)
    {
        const string item = """{"id":123,"name":"abcdefghij","value":1234.5}""";
        var count = (bytes / item.Length) + 1;
        var builder = new StringBuilder(2 + (count * (item.Length + 1)));
        builder.Append('[');
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(item);
        }

        builder.Append(']');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>小对象响应：与 <see cref="UserDto"/> 同形。</summary>
    public const string UserJson =
        """{"id":1,"name":"alice","active":true,"tags":["admin","user"],"address":{"city":"Shanghai","zip":"200000"}}""";

    /// <summary>宽对象图（13 个标量 / 3 层嵌套 / 2 个数组 / 1 个 null）：放大 dynamic 图构建的成本。</summary>
    public const string WideJson =
        """
        {"id":1024,"name":"alice","email":"alice@example.com","active":true,"score":95.5,"level":7,
        "createdAt":"2026-10-26T10:04:20Z","nickname":null,
        "address":{"city":"Shanghai","zip":"200000","geo":{"lat":31.23,"lng":121.47}},
        "roles":["admin","user","auditor"],
        "tags":[{"id":1,"label":"vip"},{"id":2,"label":"beta"}]}
        """;

    public static readonly byte[] UserJsonBytes = Encoding.UTF8.GetBytes(UserJson);

    public static readonly byte[] WideJsonBytes = Encoding.UTF8.GetBytes(WideJson);

    public const string NdjsonContentType = "application/x-ndjson; charset=utf-8";

    public const string SseContentType = "text/event-stream; charset=utf-8";

    public const string JsonContentType = "application/json; charset=utf-8";

    /// <summary>NDJSON：每行一个完整 JSON 值。</summary>
    public static byte[] Ndjson(int frames)
    {
        var builder = new StringBuilder(frames * 44);
        for (var i = 0; i < frames; i++)
        {
            builder.Append(FrameJson(i)).Append('\n');
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>SSE：每帧一行 <c>data:</c> + 一个空行（空行结束一帧）。</summary>
    public static byte[] Sse(int frames)
    {
        var builder = new StringBuilder(frames * 50);
        for (var i = 0; i < frames; i++)
        {
            builder.Append("data: ").Append(FrameJson(i)).Append("\n\n");
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>单个 JSON 数组：分帧靠 <c>Utf8JsonReader</c> 增量定位顶层元素。</summary>
    public static byte[] JsonArray(int frames)
    {
        var builder = new StringBuilder(frames * 44);
        builder.Append('[');
        for (var i = 0; i < frames; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(FrameJson(i));
        }

        builder.Append(']');
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    /// <summary>扁平 ASCII 表单：转义映射几乎不命中，是百分号编码的最省路径。</summary>
    public static Dictionary<string, string> FormAscii(int fields)
    {
        var data = new Dictionary<string, string>(fields, StringComparer.Ordinal);
        for (var i = 0; i < fields; i++)
        {
            data[$"field{i}"] = $"value-{i}";
        }

        return data;
    }

    /// <summary>扁平但全部需要转义的表单：空格 / 中文 / <c>&amp;</c> / <c>=</c> / <c>+</c> / <c>%</c> / <c>/</c>。</summary>
    public static Dictionary<string, string> FormUnicode(int fields)
    {
        var data = new Dictionary<string, string>(fields, StringComparer.Ordinal);
        for (var i = 0; i < fields; i++)
        {
            data[$"字段{i}"] = $"值 {i} 含空格 & 特殊=符号+斜杠/%";
        }

        return data;
    }

    /// <summary>嵌套表单：键名带 <c>[a][b]</c>（<c>EncodeKey</c> 的括号回填路径）+ 数组 <c>a[]=v</c>。</summary>
    public static Dictionary<string, object?> FormNested(int fields)
    {
        var data = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["user"] = new Dictionary<string, object?>
            {
                ["name"] = "alice",
                ["email"] = "alice@example.com",
                ["profile"] = new Dictionary<string, object?>
                {
                    ["city"] = "Shanghai",
                    ["zip"] = "200000"
                }
            },
            ["tags"] = new[] { "admin", "user" },
            ["count"] = fields
        };

        for (var i = 0; i < fields; i++)
        {
            data[$"extra{i}"] = $"值 {i}";
        }

        return data;
    }

    /// <summary>生成一帧 NDJSON 文本（id / delta / done 三个字段）。</summary>
    private static string FrameJson(int index)
        => $$"""{"id":{{index}},"delta":"token {{index}}","done":false}""";
}
