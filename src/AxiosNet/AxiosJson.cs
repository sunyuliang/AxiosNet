using System.Text.Json;

namespace AxiosNet;

/// <summary>
/// 默认 JSON 序列化器的全局选项（格式注入点见 <see cref="AxiosContent"/>）。
/// </summary>
/// <remarks>
/// 优先级：单次请求的 <see cref="AxiosConfig.SerializationSettings.JsonOptions"/> 高于实例 <c>Defaults</c>，实例 <c>Defaults</c> 高于这里。
/// 全局共享：启动期一次性设置，替换时给一个全新实例。
/// </remarks>
public static class AxiosJson
{
    // 默认 JSON 选项（Web 默认：camelCase 命名 + 忽略大小写读取），可整体替换。
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
        => config.SerializationOrNull?.JsonOptions ?? Options;
}
