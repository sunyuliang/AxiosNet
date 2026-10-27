using System.Text.Json;

namespace AxiosNet;

/// <summary>
/// 默认 JSON 序列化器的全局选项（格式注入点见 <see cref="AxiosContent"/>）。
/// 优先级：单次请求的 <see cref="AxiosConfig.SerializationSettings.JsonSerializerOptions"/> 高于实例 <c>Defaults</c>，
/// 实例 <c>Defaults</c> 高于这里；建议启动期一次性设置，替换时给一个全新实例。
/// </summary>
public static class AxiosJson
{
    private static JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

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

    /// <summary>取本次请求实际生效的 JSON 选项（config → 全局）。</summary>
    internal static JsonSerializerOptions GetOptions(AxiosConfig config)
        => config.SerializationOrNull?.JsonSerializerOptions ?? Options;
}
