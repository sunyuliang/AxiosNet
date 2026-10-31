using AxiosNet;

namespace AxiosNet.Simple.Examples;

/// <summary>9. 其他 responseType：text / arraybuffer。</summary>
public sealed class ResponseTypesExample : IExample
{
    public int Number => 9;

    public string Title => "responseType：text / arraybuffer";

    public async Task RunAsync(ExampleContext context)
    {
        SampleConsole.Section(Number, Title);

        var textResponse = await Axios.GetAsync(context.BaseUrl, c => c.ResponseType = ResponseType.Text);
        Console.WriteLine($"  text: {textResponse.Data}");

        var bytesResponse = await Axios.PostAsync<byte[]>(context.BaseUrl + "echo", new byte[] { 1, 2, 3, 250, 255 }, c => c.ResponseType = ResponseType.ArrayBuffer);
        Console.WriteLine($"  arraybuffer 回显: {string.Join(",", bytesResponse.Data!)}");
    }
}
