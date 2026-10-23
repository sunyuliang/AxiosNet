using AxiosNet.WebAPI;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 7. 请求签名 + 响应验签：SigningSerializer 装饰内置 JSON——
/// 请求出口对正文算 HMAC 写 X-Signature，响应入口先验签再解析；服务端做对称校验。
/// </summary>
public sealed class SigningExample : IExample
{
    /// <summary>本示例专用客户端（签名序列化器）：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _signing;

    /// <summary>本示例专用客户端（不带签名，反例用）：配置固定，首次运行创建后跨运行复用。</summary>
    private static ApiClient? _bare;

    public int Number => 7;

    public string Title => "签名/验签：序列化器装饰器（请求体 HMAC 签名 + 响应验签）";

    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        // 挂载方式：实例默认 ContentSerializer 换成装饰器（本客户端的全部请求/响应都过签名）
        // 静态复用：首次运行创建，之后所有次运行复用
        var client = _signing ??= new ApiClient(
            context.BaseUrl,
            defaults => defaults.Serialization.ContentSerializer = new SigningSerializer(
                AxiosNet.Serialization.SystemTextJsonSerializer.Instance,
                DemoApiServer.SigningKey));

        var response = await client.RequestAsync(new SignedEchoRequest
        {
            Payload = new UserPayload("carol", 25)
        });

        Console.WriteLine($"  {response.Status}（请求已签名、响应已验签）");
        Console.WriteLine($"  Data = {ExampleConsole.Pretty(response.Data)}");
        Console.WriteLine($"  响应 X-Signature = {response.GetHeader("X-Signature")?[..24]}…");

        // 反例：不带签名的客户端调同一端点 -> 服务器 401（静态复用：首次运行创建，之后所有次运行复用）
        var bare = _bare ??= new ApiClient(context.BaseUrl);

        try
        {
            await bare.RequestAsync(new SignedEchoRequest { Payload = new UserPayload("mallory", 1) });
        }
        catch (AxiosException ex)
        {
            Console.WriteLine($"  未签名请求 -> {ex.Status} {ex.Code}（服务器拒绝，签名是完整性边界）");
        }
    }
}
