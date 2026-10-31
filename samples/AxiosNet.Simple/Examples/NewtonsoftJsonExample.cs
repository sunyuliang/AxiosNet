using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>19. 自定义 JSON 序列化：实现 IContentSerializer，把默认的 System.Text.Json 换成 Newtonsoft.Json。</summary>
public sealed class NewtonsoftJsonExample : IExample
{
    /// <summary>本示例专用客户端（Newtonsoft 引擎）：配置固定，首次运行创建后跨运行复用。</summary>
    private static AxiosClient? _newtonsoft;

    public int Number => 19;

    public string Title => "自定义 JSON 序列化：用 Newtonsoft.Json 替换 System.Text.Json";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var profile = new ProfileDto
        {
            UserName = "alice",
            Age = 20,
            Address = new Address("Shanghai", "200000"),
            Roles = ["admin", "user"]
        };

        // 实例级替换：只影响这个 client，不污染全局（静态复用：首次运行创建，之后复用）
        var newtonsoft = _newtonsoft ??= Axios.Create(c =>
        {
            c.BaseAddress = context.BaseUrl;
            c.Serialization.ContentSerializer = new NewtonsoftJsonSerializer();
        });

        // 对比基线：System.Text.Json 是默认引擎，直接用 Axios 静态门面，不必单独建实例

        // 1) 请求方向：同一份对象，两种引擎写出不同的属性名（/echo 原样回显请求体）
        var newtonsoftBody = await newtonsoft.PostAsync<string>("echo", profile, c => c.ResponseType = ResponseType.Text);
        var systemTextJsonBody = await Axios.PostAsync<string>(context.BaseUrl + "echo", profile, c => c.ResponseType = ResponseType.Text);

        Console.WriteLine($"  Newtonsoft 请求体     : {newtonsoftBody.Data}");
        Console.WriteLine($"  System.Text.Json 请求体: {systemTextJsonBody.Data}");

        // 2) 响应方向：PascalCase 契约反序列化为强类型
        var typed = await newtonsoft.GetAsync<ProfileDto>("users/1/pascal");
        Console.WriteLine(
            $"  强类型响应: UserName={typed.Data!.UserName}, Age={typed.Data.Age}, "
            + $"City={typed.Data.Address?.City}, Roles={string.Join("/", typed.Data.Roles ?? [])}");

        // 3) dynamic：由库展开成 AxiosDynamic（JSON 便利能力，与序列化器无关）
        dynamic data = (await newtonsoft.GetAsync<dynamic>("users/1/pascal")).Data!;
        Console.WriteLine($"  dynamic(AxiosDynamic): UserName={data.UserName}, Age={data.Age}, Roles[1]={data.Roles[1]}");

        Console.WriteLine("  提示：库本身仍零第三方依赖，Newtonsoft 只在示例项目中引用");
    }
}
