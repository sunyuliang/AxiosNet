using System.Text.Json;
using AxiosNet.Serialization;

namespace AxiosNet;

/// <summary>
/// JSON 序列化的全局入口，对应「整个应用统一的 JSON 行为」：
/// <list type="bullet">
/// <item><see cref="Serializer"/>：默认序列化器（System.Text.Json），可整体替换为自定义实现；</item>
/// <item><see cref="Options"/>：默认 <see cref="JsonSerializerOptions"/>（Web 风格：camelCase 输出、输入大小写不敏感）。</item>
/// </list>
/// <para>
/// 优先级：单次请求的 <see cref="AxiosConfig.JsonSerializerOptions"/> / <see cref="AxiosConfig.JsonSerializer"/>
/// 高于实例 <c>Defaults</c>，实例 <c>Defaults</c> 高于这里的全局值。
/// </para>
/// <para>
/// 建议在启动期一次性设置。替换 <see cref="Options"/> 时请给它一个全新的实例，
/// 不要修改已经被序列化使用过的实例（System.Text.Json 会拒绝修改并抛
/// <see cref="InvalidOperationException"/>）。
/// </para>
/// </summary>
public static class AxiosJson
{
    private static IAxiosJsonSerializer _serializer = SystemTextJsonSerializer.Instance;
    private static JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    /// <summary>全局默认序列化器（默认 <see cref="SystemTextJsonSerializer.Instance"/>）。</summary>
    public static IAxiosJsonSerializer Serializer
    {
        get => Volatile.Read(ref _serializer);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _serializer, value);
        }
    }

    /// <summary>全局默认 JSON 选项，默认 <see cref="JsonSerializerDefaults.Web"/>。</summary>
    public static JsonSerializerOptions Options
    {
        get => Volatile.Read(ref _options);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _options, value);
        }
    }

    /// <summary>取本次请求实际生效的序列化器（config → 全局）。</summary>
    internal static IAxiosJsonSerializer Get(AxiosConfig config)
        => config.JsonSerializer ?? Serializer;

    /// <summary>取本次请求实际生效的 JSON 选项（config → 全局）。</summary>
    internal static JsonSerializerOptions GetOptions(AxiosConfig config)
        => config.JsonSerializerOptions ?? Options;
}
