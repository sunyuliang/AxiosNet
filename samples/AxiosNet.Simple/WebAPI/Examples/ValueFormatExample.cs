using System.Globalization;
using AxiosNet.WebAPI;
using AxiosNet.WebAPI.Attributes;

namespace AxiosNet.Simple.WebAPI.Examples;

/// <summary>
/// 17. 值格式化：成员级 <c>[Format]</c>（格式串 / 静态函数）与实例级 <c>ValueFormatter</c>。
/// 优先级是「成员声明 &gt; 实例钩子 &gt; 通道默认」，集合与字典逐元素 / 逐值应用；
/// 路径 / 查询 / 请求头 / 表单字段 / multipart 文本部件 / 裸标量正文走的是同一条转换路径。
/// </summary>
public sealed class ValueFormatExample : IExample
{
    public int Number => 17;

    public string Title => "值格式化：[Format] 格式串 / 静态函数 + ValueFormatter 优先级";

    /// <summary>示例入口：值格式化：[Format] 格式串 / 静态函数 + ValueFormatter 优先级。</summary>
    public async Task RunAsync(ExampleContext context)
    {
        ExampleConsole.Section(Number, Title);

        using var client = new ApiClient(context.BaseUrl)
        {
            // 实例级钩子：成员自己没有声明时生效；返回 null 表示「本次不处理」，回落到默认规则。
            ValueFormatter = value => value is DateTime date
                ? "hook" + date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)
                : null
        };

        var api = client.Create<IFormatApi>();

        // 1) 路径段：[Format("0000")] 把 2 写成 0002。
        var user = await api.GetAsync(2);
        Console.WriteLine($"  GET  {user.Request?.RequestUri?.PathAndQuery} -> {user.Status} {ExampleConsole.Pretty(user.Data)}");

        // 2) 查询串：格式串 / 静态函数 / 集合逐元素 / 字典逐值，与实例钩子的优先级一眼可见。
        var list = await api.ListAsync(
            since: new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            updatedAfter: new DateTime(2024, 3, 4, 0, 0, 0, DateTimeKind.Utc),
            codes: [11, 255],
            amounts: new Dictionary<string, decimal> { ["min"] = 12.5m });

        Console.WriteLine($"  GET  {list.Request?.RequestUri?.PathAndQuery}");
        Console.WriteLine($"    -> {list.Status}，{list.Data?.Length ?? 0} 个用户");
        Console.WriteLine("     since=成员格式串（压过实例钩子）、updatedAfter=实例钩子、codes[]=逐元素、amounts[min]=逐值");
    }
}

/// <summary>示例用格式化函数：签名必须是 <c>public static string? M(object? value)</c>，返回 null 表示回落默认规则。</summary>
public static class SampleFormats
{
    /// <summary>金额固定两位小数（区域固定为不变文化，与库的默认规则一致）。</summary>
    public static string? Money(object? value)
        => value is decimal amount ? amount.ToString("0.00", CultureInfo.InvariantCulture) : null;
}

/// <summary>格式化声明样例：路径 / 查询参数上的成员级格式化。</summary>
public interface IFormatApi
{
    [Get("api/users/{id}")]
    Task<AxiosResponse<UserProfile>> GetAsync(
        [Path, Format("0000")] int id,
        CancellationToken cancellationToken = default);

    [Get("api/users")]
    Task<AxiosResponse<UserProfile[]>> ListAsync(
        [Query, Format("yyyy-MM-dd")] DateTime since,
        [Query] DateTime updatedAfter,
        [Query, Format("X2")] int[] codes,
        [Query("amounts"), Format(typeof(SampleFormats), nameof(SampleFormats.Money))] Dictionary<string, decimal> amounts,
        CancellationToken cancellationToken = default);
}
