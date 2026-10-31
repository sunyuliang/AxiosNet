using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>7. dynamic 响应：无需 DTO，直接 data.xxx / data["xxx"]（对标 JS 用法）。</summary>
public sealed class DynamicResponseExample : IExample
{
    public int Number => 7;

    public string Title => "dynamic 响应：GetAsync<dynamic> 直接 data.name / data[\"name\"] / data.address.city";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var response = await Axios.GetAsync<dynamic>(context.BaseUrl + "users/1");
        dynamic user = response.Data!;
        Console.WriteLine($"  name={user.name}, age={user.age}, active={user.active}, score={user.score}");
        Console.WriteLine($"  address.city={user.address.city}, roles[0]={user.roles[0]}, roles[1]={user.roles[1]}");

        // 索引访问：字段名来自变量时用 data["key"]（缺省字段返回 null，不抛异常）
        string field = "name";
        Console.WriteLine($"  索引访问: user[\"{field}\"]={user[field]}, user[\"notExist\"]={user["notExist"] ?? "<null>"}");

        // 对象节点同时是 IDictionary：可原样回传给 params / 请求体，也能被 System.Text.Json 序列化
        var json = System.Text.Json.JsonSerializer.Serialize<object>((object)user);
        Console.WriteLine($"  序列化回去仍是 JSON 对象: {json[..Math.Min(60, json.Length)]}...");

        // 根数组同样动态可用
        var listResponse = await Axios.GetAsync<dynamic>(context.BaseUrl + "users");
        dynamic users = listResponse.Data!;
        Console.WriteLine($"  根数组: count={users.Count}, 第一个={users[0].name}, 第二个={users[1].name}");

        // 也可以在非泛型请求上显式使用 ResponseType.Dynamic
        var configured = await Axios.GetAsync(context.BaseUrl + "users/1", c => c.ResponseType = ResponseType.Dynamic);
        dynamic viaConfig = configured.Data!;
        Console.WriteLine($"  ResponseType.Dynamic 方式: {viaConfig.name}");
    }
}
