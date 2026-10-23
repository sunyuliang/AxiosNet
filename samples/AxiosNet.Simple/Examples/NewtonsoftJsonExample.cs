using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 19. 自定义 JSON 序列化：实现 IContentSerializer，把默认的 System.Text.Json 换成 Newtonsoft.Json，
/// 业务对象的请求体与强类型响应走同一个引擎（dynamic 由库自己展开，与引擎无关）。
/// </summary>
public sealed class NewtonsoftJsonExample : IExample
{
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

        // 实例级替换：只影响这个 client，不污染全局
        using var newtonsoft = Axios.Create(c =>
        {
            c.BaseAddress = context.BaseUrl;
            c.Serialization.ContentSerializer = new NewtonsoftJsonSerializer();
        });

        // 对比用的默认实例（System.Text.Json，Web 默认 camelCase）
        using var systemTextJson = Axios.Create(c => c.BaseAddress = context.BaseUrl);

        // 1) 请求方向：同一份对象，两种引擎写出不同的属性名（/echo 原样回显请求体）
        var newtonsoftBody = await newtonsoft.PostAsync<string>(
            "echo", profile, c => c.ResponseType = ResponseType.Text);
        var systemTextJsonBody = await systemTextJson.PostAsync<string>(
            "echo", profile, c => c.ResponseType = ResponseType.Text);

        Console.WriteLine($"  Newtonsoft 请求体     : {newtonsoftBody.Data}");
        Console.WriteLine($"  System.Text.Json 请求体: {systemTextJsonBody.Data}");

        // 2) 响应方向：PascalCase 契约反序列化为强类型
        var typed = await newtonsoft.GetAsync<ProfileDto>("users/1/pascal");
        Console.WriteLine(
            $"  强类型响应: UserName={typed.Data!.UserName}, Age={typed.Data.Age}, "
            + $"City={typed.Data.Address?.City}, Roles={string.Join("/", typed.Data.Roles ?? [])}");

        // 3) dynamic：由库展开成 ExpandoObject（JSON 便利能力，与序列化器无关）
        dynamic data = (await newtonsoft.GetAsync<dynamic>("users/1/pascal")).Data!;
        Console.WriteLine($"  dynamic(ExpandoObject): UserName={data.UserName}, Age={data.Age}, Roles[1]={data.Roles[1]}");

        Console.WriteLine("  提示：库本身仍零第三方依赖，Newtonsoft 只在示例项目中引用");
    }
}
