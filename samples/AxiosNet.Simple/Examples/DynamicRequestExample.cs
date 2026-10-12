using System.Dynamic;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 8. dynamic 请求：ExpandoObject 作为 body 与 params。
/// </summary>
public sealed class DynamicRequestExample : IExample
{
    public int Number => 8;

    public string Title => "dynamic 请求：ExpandoObject 作为 data / params";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        using var http = new AxiosClient();

        dynamic payload = new ExpandoObject();
        payload.name = "charlie";
        payload.age = 25;
        dynamic nested = new ExpandoObject();
        nested.city = "Shenzhen";
        payload.address = nested;

        var response = await http.PostAsync<dynamic>(context.BaseUrl + "echo", payload);
        dynamic echoed = response.Data!;
        Console.WriteLine($"  回显: name={echoed.name}, age={echoed.age}, address.city={echoed.address.city}");

        dynamic query = new ExpandoObject();
        query.page = 3;
        query.keyword = "dot net";
        dynamic filter = new ExpandoObject();
        filter.kind = "music";
        query.filter = filter;

        var searchResponse = await http.GetAsync<dynamic>(context.BaseUrl + "search", c => c.Params = query);
        dynamic data = searchResponse.Data!;
        Console.WriteLine($"  query -> {data.query}");
    }
}
