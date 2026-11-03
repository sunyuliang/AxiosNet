namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 单个集成示例的统一抽象。
/// </summary>
public interface IExample
{
    /// <summary>示例序号（同时用于运行顺序与标题编号）。</summary>
    int Number { get; }

    /// <summary>示例标题。</summary>
    string Title { get; }

    /// <summary>执行示例。</summary>
    Task RunAsync(ExampleContext context);
}

/// <summary>
/// 示例运行上下文：所有示例共享的演示服务器信息。
/// </summary>
public sealed class ExampleContext
{
    public ExampleContext(string baseUrl, DemoApiServer server)
    {
        BaseUrl = baseUrl;
        Server = server;
    }

    /// <summary>本地演示服务器根地址（含末尾斜杠）。</summary>
    public string BaseUrl { get; }

    /// <summary>演示服务器实例。</summary>
    public DemoApiServer Server { get; }
}

/// <summary>示例共用的控制台输出辅助方法。</summary>
internal static class ExampleConsole
{
    /// <summary>打印小节标题（编号 + 标题）：WebAPI 这批示例共用它对齐输出格式。</summary>
    public static void Section(int number, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {number}. {title}");
    }

    /// <summary>把对象压成单行 JSON 打印。</summary>
    public static string Pretty(object? value)
        => System.Text.Json.JsonSerializer.Serialize(value);
}
