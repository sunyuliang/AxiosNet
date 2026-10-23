using System.Text.Json;

namespace AxiosNet;

/// <summary>
/// 默认 JSON 序列化器的全局选项（格式注入点见 <see cref="AxiosContent"/>）。
/// <para>
/// 优先级：单次请求的 <see cref="AxiosConfig.SerializationSettings.JsonSerializerOptions"/> 高于实例 <c>Defaults</c>，
/// 实例 <c>Defaults</c> 高于这里的全局值。
/// </para>
/// <para>
/// 建议在启动期一次性设置；替换时给一个全新的实例，不要修改已经被序列化使用过的实例
/// （System.Text.Json 会拒绝修改并抛 <see cref="InvalidOperationException"/>）。
/// </para>
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
