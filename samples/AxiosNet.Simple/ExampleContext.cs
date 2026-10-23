namespace AxiosNet.Simple;

/// <summary>
/// 示例运行上下文：所有示例共享的演示服务器信息。
/// </summary>
public sealed class ExampleContext
{
    public ExampleContext(string baseUrl, DemoServer server)
    {
        BaseUrl = baseUrl;
        Server = server;
    }

    /// <summary>本地演示服务器根地址（含末尾斜杠）。</summary>
    public string BaseUrl { get; }

    /// <summary>演示服务器实例（连接池示例需要读取连接统计）。</summary>
    public DemoServer Server { get; }
}
