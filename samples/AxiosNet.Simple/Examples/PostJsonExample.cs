using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>3. POST 对象 -> application/json，响应强类型回读。</summary>
public sealed class PostJsonExample : IExample
{
    public int Number => 3;

    public string Title => "POST 对象自动 JSON 序列化（/echo 原样回显）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var payload = new User(0, "bob", 30, true, 99.5, new Address("Beijing", "100000"), new[] { "dev" });

        var response = await Axios.PostAsync<User>(context.BaseUrl + "echo", payload);
        Console.WriteLine($"  {response.Status} -> {SampleConsole.Pretty(response.Data)}");
    }
}
