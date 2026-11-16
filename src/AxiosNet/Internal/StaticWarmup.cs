using System.Text.Json;
using AxiosNet.Reflection;

namespace AxiosNet.Internal;

/// <summary>
/// 进程级静态首用：把延迟初始化的公共设施在第一条请求之前跑热，让首条请求的耗时不掺这些固定成本。
/// </summary>
/// <remarks>
/// 只做与具体请求无关的本地准备——不建连、不发请求、不改任何全局状态。
/// 幂等：重复调用只是重复几次极小的本地调用，「只做一次」由调用方的实例标记决定。
/// </remarks>
internal static class StaticWarmup
{
    /// <summary>跑热静态首用；可重复调用。</summary>
    public static void Run()
    {
        // 1) JSON：选项的校验与转换器缓存在第一次（反）序列化时才建。
        //    走库自己的反射兼容层，AOT 下反射被关掉时它明确拒绝，这里按「该路径不可预热」跳过——
        //    预热是可选的启动优化，不能因为目标平台没有反射路径就让启动失败。
        var options = AxiosJson.Options;
        try
        {
            JsonSerializerJit.EnsureReflectionMetadata(typeof(JsonElement));
            _ = JsonSerializerJit.DeserializeUtf8Legacy(JsonProbe, typeof(JsonElement), options);
            _ = JsonSerializerJit.SerializeLegacy(1, typeof(int), options);
        }
        catch (NotSupportedException)
        {
            // AOT：元数据由包内源生成器提供，没有反射路径可预热。
        }

        // 2) 响应正文解码：严格 UTF-8 合法性判定、charset 解析与 BOM 判定。
        _ = ResponseTextDecoder.IsValidUtf8(JsonProbe);
        _ = ResponseTextDecoder.Decode(JsonProbe, "utf-8", forced: null);

        // 3) 响应装配用的两条静态判定：状态文案与文本 / 二进制媒体类型。
        _ = StatusTextMap.Get(200);
        _ = MediaTypes.IsTextual("application/json");
    }

    /// <summary>预热样本：一份极小的 JSON 文档，只用来触发校验与解析路径，不参与任何业务判断。</summary>
    private static readonly byte[] JsonProbe = Encoding.UTF8.GetBytes("{\"warmup\":1}");
}
