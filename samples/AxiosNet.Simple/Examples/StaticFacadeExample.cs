using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 1. 全局静态门面（等同 JS 里的 axios.get(...)）。
/// </summary>
public sealed class StaticFacadeExample : IExample
{
    public int Number => 1;

    public string Title => "全局静态门面 Axios.GetAsync（等同 axios.get）";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var response = await Axios.GetAsync<JsonElement>(context.BaseUrl);
        Console.WriteLine($"  status = {response.Status} {response.StatusText}");
        Console.WriteLine($"  data   = {SampleConsole.Pretty(response.Data)}");
    }
}
