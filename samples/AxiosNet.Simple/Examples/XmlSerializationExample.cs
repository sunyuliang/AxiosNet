using AxiosNet;
using AxiosNet.Serialization;

namespace AxiosNet.Simple.Examples;

/// <summary>23. XML 序列化：换上系统内置的 <see cref="XmlContentSerializer"/>，请求体与强类型响应走同一个引擎。</summary>
public sealed class XmlSerializationExample : IExample
{
    /// <summary>本示例专用客户端（XML 引擎）：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _xml;

    /// <summary>本示例专用客户端（自动模式，按 Content-Type 选序列化器）：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _auto;

    public int Number => 23;

    public string Title => "XML 序列化：内置 XmlContentSerializer";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 实例级注入：只影响这个 client，不污染全局默认（静态复用：首次运行创建，之后复用）
        var xml = _xml ??= Axios.Create(c =>
        {
            c.BaseAddress = context.BaseUrl;
            c.Serialization.ContentSerializer = XmlContentSerializer.Instance;
        });

        var user = new XmlUser { Id = 1, Name = "alice", Age = 30 };

        // 1) 请求方向：不设 ContentType，头与正文都由序列化器产出（/echo 原样回显请求体）
        var echoed = await xml.PostAsync<string>("echo", user, c => c.ResponseType = ResponseType.Text);

        Console.WriteLine($"  请求体(头自动 application/xml): {echoed.Data}");

        // 2) 响应方向：application/xml 的强类型响应，直接反序列化成 XmlUser
        var typed = await xml.GetAsync<XmlUser>("users/1/xml");
        Console.WriteLine($"  强类型响应: Id={typed.Data!.Id}, Name={typed.Data.Name}, Age={typed.Data.Age}");

        // 3) 非泛型响应：XML 解析不了，原样返回文本
        var raw = await xml.GetAsync("users/1/xml");
        Console.WriteLine($"  非泛型响应(原文): {raw.Data}");

        // 4) 基础数据类型不经过序列化器，直接取文本发送，媒体类型仍由 ContentType 决定
        var scalar = await xml.PostAsync<string>(
            "echo",
            42,
            c =>
            {
                c.ContentType = AxiosContentType.Xml;
                c.ResponseType = ResponseType.Text;
            });

        Console.WriteLine($"  标量请求体(application/xml, 不经过序列化器): {scalar.Data}");

        // 5) 自动模式：只按媒体类型注册，同一个 client 可以混用格式
        AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
        try
        {
            // 注册表是全局的，与用哪个客户端无关
            var auto = _auto ??= new AxiosClient(new AxiosConfig { BaseAddress = context.BaseUrl });

            var byRegistry = await auto.GetAsync<XmlUser>("users/1/xml");
            Console.WriteLine($"  自动模式(按 Content-Type 注册): Id={byRegistry.Data!.Id}, Name={byRegistry.Data.Name}, Age={byRegistry.Data.Age}");
        }
        finally
        {
            // 示例内注册的键随手清掉，避免影响后面的示例
            AxiosContent.Unregister(AxiosContentType.Xml);
        }

        Console.WriteLine("  提示：基础类型（<Age>30</Age>）由库直接解析、不经过序列化器；dynamic 是 JSON 便利能力，");
        Console.WriteLine("        XML 响应下会用 JSON 解析并失败（需要原文请用 ResponseType.Text 或非泛型 GetAsync）");
    }
}
