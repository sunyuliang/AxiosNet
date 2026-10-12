using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 默认 JSON 选项：Web 风格（camelCase 输出、输入大小写不敏感），
/// 对齐 JS/axios 生态最常见的 JSON 命名习惯；用户可在 config.JsonSerializerOptions 覆盖。
/// </summary>
internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
}
