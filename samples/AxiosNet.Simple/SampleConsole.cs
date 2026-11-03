using System.Text.Json;

namespace AxiosNet.Simple;

/// <summary>
/// 示例共用的控制台输出辅助方法。
/// </summary>
internal static class SampleConsole
{
    /// <summary>打印小节标题（编号 + 标题）：所有示例都走这里，输出格式才会一致。</summary>
    public static void Section(int number, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {number}. {title}");
    }

    /// <summary>把对象压成单行 JSON 打印（不缩进，免得多字段对象把示例输出撑成几十行）。</summary>
    public static string Pretty(object? value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
}
