using System.Text.Json;
using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>1. 全局静态门面（等同 JS 里的 axios.get(...)）。</summary>
public sealed class StaticFacadeExample : IExample
{
    public int Number => 1;

    public string Title => "全局静态门面 Axios.GetAsync（等同 axios.get）/ Axios.Reset 重置全局状态";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var response = await Axios.GetAsync<JsonElement>(context.BaseUrl);
        Console.WriteLine($"  status = {response.Status} {response.StatusText}");
        Console.WriteLine($"  data   = {SampleConsole.Pretty(response.Data)}");

        // 全局默认值挂在门面的默认实例上：Reset 会释放它，下次访问按全新配置重建。
        Axios.Defaults.Headers.Set("X-Demo", "before-reset");
        var beforeReset = await Axios.GetAsync<Dictionary<string, string>>(context.BaseUrl + "headers");
        Axios.Reset();
        var afterReset = await Axios.GetAsync<Dictionary<string, string>>(context.BaseUrl + "headers");
        Console.WriteLine($"  默认头 X-Demo：Reset 前 = {beforeReset.Data!["x-demo"]}，"
            + $"Reset 后 = {(afterReset.Data!.TryGetValue("x-demo", out var value) ? value : "（已清空）")}");
    }
}
