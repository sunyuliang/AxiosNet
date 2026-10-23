using AxiosNet;
using AxiosNet.Serialization;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 23. XML 序列化：换上<b>系统内置</b>的 <see cref="XmlContentSerializer"/>（零第三方依赖），
/// 请求体、强类型响应统一走同一个引擎 —— 库本身不认识 XML，也不知道 Data 里装的是什么。
/// </summary>
public sealed class XmlSerializationExample : IExample
{
    public int Number => 23;

    public string Title => "XML 序列化：内置 XmlContentSerializer";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // 实例级注入：只影响这个 client，不污染全局默认（全局入口是 AxiosContent.Serializer）
        using var xml = Axios.Create(c =>
        {
            c.BaseAddress = context.BaseUrl;
            c.Serialization.ContentSerializer = XmlContentSerializer.Instance;
        });

        var user = new XmlUser { Id = 1, Name = "alice", Age = 30 };

        // 1) 请求方向：这里<b>没有</b>设置 ContentType —— 头由序列化器自报（MediaType = application/xml），
        //    正文由它产出（/echo 原样回显请求体）。等价于 axios 默认 transformRequest 里顺手
        //    headers.setContentType(...)：换格式只需换序列化器一处，头与正文不会脱节。
        var echoed = await xml.PostAsync<string>(
            "echo",
            user,
            c => c.ResponseType = ResponseType.Text);

        Console.WriteLine($"  请求体(头自动 application/xml): {echoed.Data}");

        // 2) 响应方向：application/xml 的强类型响应，直接反序列化成 XmlUser
        var typed = await xml.GetAsync<XmlUser>("users/1/xml");
        Console.WriteLine($"  强类型响应: Id={typed.Data!.Id}, Name={typed.Data.Name}, Age={typed.Data.Age}");

        // 3) 非泛型响应：框架按 JSON 文档形态解析（XML 解析不了）→ 原样返回文本，不经过序列化器
        var raw = await xml.GetAsync("users/1/xml");
        Console.WriteLine($"  非泛型响应(原文): {raw.Data}");

        // 4) 请求方向的基础数据类型：不经过序列化器（XmlSerializer 对无名的根标量无从下手），
        //    框架直接取文本发送 —— 媒体类型仍然由 ContentType 决定
        var scalar = await xml.PostAsync<string>(
            "echo",
            42,
            c =>
            {
                c.ContentType = AxiosContentType.Xml;
                c.ResponseType = ResponseType.Text;
            });

        Console.WriteLine($"  标量请求体(application/xml, 不经过序列化器): {scalar.Data}");

        // 5) 自动模式：不注入任何序列化器（AxiosContent.Serializer 保持 null），只按媒体类型注册 ——
        //    同一个 client 因此可以混用格式：Content-Type 是 XML 走 XML，其余回落内置 JSON
        AxiosContent.Register(AxiosContentType.Xml, XmlContentSerializer.Instance);
        try
        {
            using var auto = Axios.Create(c => c.BaseAddress = context.BaseUrl);

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
