using System.Text.Json;

namespace AxiosNet.Simple.Examples;

/// <summary>
/// 示例共用的控制台输出辅助方法。
/// </summary>
internal static class SampleConsole
{
    public static void Section(int number, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"-- {number}. {title}");
    }

    public static string Pretty(object? value)
        => JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = false });
}
