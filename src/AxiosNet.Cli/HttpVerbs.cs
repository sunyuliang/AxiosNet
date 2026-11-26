namespace AxiosNet.Cli;

/// <summary>OpenAPI path item 下的 HTTP 方法键（小写）。读取器与 Swagger 2.0 归一化共用同一份顺序。</summary>
internal static class HttpVerbs
{
    /// <summary>按遍历顺序固定的 HTTP 方法键；顺序会影响生成文件里操作的排列。</summary>
    public static readonly string[] All = { "get", "put", "post", "delete", "options", "head", "patch", "trace" };
}
