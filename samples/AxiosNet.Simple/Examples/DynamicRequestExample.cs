using System.Dynamic;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>8. dynamic 请求：AxiosDynamic / ExpandoObject 作为 body 与 params。</summary>
public sealed class DynamicRequestExample : IExample
{
    public int Number => 8;

    public string Title => "dynamic 请求：AxiosDynamic / ExpandoObject 作为 data / params";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        // AxiosDynamic 既是动态对象也是 IDictionary：作 body 时序列化成 JSON 对象
        dynamic payload = new AxiosDynamic();
        payload.name = "charlie";
        payload.age = 25;
        payload.address = new AxiosDynamic();
        payload.address.city = "Shenzhen";

        // (object) 转换：避免整条调用变成动态调用
        var response = await Axios.PostAsync<object>(context.BaseUrl + "echo", (object)payload);
        dynamic echoed = response.Data!;
        Console.WriteLine($"  回显: name={echoed.name}, age={echoed.age}, address.city={echoed.address.city}");

        // ExpandoObject 同样可用：任何 IDictionary<string,object> 都能当 body / params
        dynamic query = new ExpandoObject();
        query.page = 3;
        query.keyword = "dot net";
        query.filter = new AxiosDynamic();
        query.filter.kind = "music";

        var searchResponse = await Axios.GetAsync<object>(context.BaseUrl + "search", c => c.Params = query);
        dynamic data = searchResponse.Data!;
        Console.WriteLine($"  query -> {data.query}");

        // 响应对象零转换直接当 params 复用
        var reuseResponse = await Axios.GetAsync<object>(context.BaseUrl + "search", c => c.Params = (object)payload);
        dynamic reused = reuseResponse.Data!;
        Console.WriteLine($"  复用响应对象作 params -> query={reused.query}");
    }
}
