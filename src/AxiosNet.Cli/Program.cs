namespace AxiosNet.Cli;

/// <summary>命令行入口：只负责参数解析 → 调用生成流程 → 把异常翻译成退出码，业务逻辑都在 <see cref="OpenApiGenerator"/>。</summary>
internal static class Program
{
    /// <summary>
    /// 进程入口。退出码约定：0 成功；1 生成失败 / <c>--check</c> 不一致 / 命中 <c>--fail-on warnings</c>；
    /// 2 用法错误（参数解析失败或缺文档）。
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        // 先单独解析参数：用法错误需要打印帮助，不能混进生成流程的错误通道。
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            Console.Error.WriteLine();
            PrintUsage(Console.Error);
            return 2;
        }

        if (options.ShowHelp)
        {
            PrintUsage(Console.Out);
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine(OpenApiGenerator.Version);
            return 0;
        }

        // 参数合法后进入生成主流程；这里只做异常 → 退出码的翻译。
        try
        {
            return await OpenApiGenerator.RunAsync(options).ConfigureAwait(false);
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 2;
        }
        catch (GenerationException ex)
        {
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            // 兜底异常只打印消息，不把堆栈刷给用户；需要排查时用 --report 或调试器复现。
            Console.Error.WriteLine("error: " + ex.Message);
            return 1;
        }
    }

    /// <summary>打印全部用法说明；<c>--help</c> 走 stdout，用法错误走 stderr。</summary>
    private static void PrintUsage(TextWriter writer)
    {
        writer.WriteLine("axiosnet-gen —— 从 OpenAPI 3.0 / 3.1、Swagger 2.0 文档生成 AxiosNet 声明式 C# 客户端");
        writer.WriteLine();
        writer.WriteLine("用法:");
        writer.WriteLine("  axiosnet-gen <spec> [options]");
        writer.WriteLine();
        writer.WriteLine("参数:");
        writer.WriteLine("  <spec>                       文档来源：本地路径、http(s) URL，或 - 表示 stdin");
        writer.WriteLine();
        writer.WriteLine("选项:");
        writer.WriteLine("  -n, --namespace <ns>         生成代码的命名空间（默认 OpenApi.Generated）");
        writer.WriteLine("  -o, --output <dir>           输出目录（默认 Generated）");
        writer.WriteLine("      --style <mode>           model：每个操作为一个 IApiRequest<T> 请求模型（默认）；");
        writer.WriteLine("                               interface：按 tag 生成 Create<T>() 接口代理");
        writer.WriteLine("      --single-file            全部代码写进一个文件（默认每个分组一个文件 + Models.g.cs）");
        writer.WriteLine("      --single-interface       全部操作放进一个接口（仅接口式）");
        writer.WriteLine("  -i, --interface-name <name>  单接口模式下的接口名（默认 IApiClient，仅接口式）");
        writer.WriteLine("  -w, --response-wrapper <m>   response：Task<AxiosResponse<T>>；none：Task<T>（默认 response，仅接口式）");
        writer.WriteLine("      --integer-type <m>       integer 无 format 时的类型：int / long（默认 int）");
        writer.WriteLine("      --header <name:value>    拉取远程文档时附带的请求头，可重复");
        writer.WriteLine("      --timeout <seconds>      远程文档下载超时（默认 30）");
        writer.WriteLine("      --check                  只校验输出是否与生成结果一致，不写文件；不一致退出码 1");
        writer.WriteLine("      --dry-run                把生成内容打印到 stdout，不写文件");
        writer.WriteLine("  -f, --force                  允许覆盖不带生成标记的文件、删除失联的旧生成文件");
        writer.WriteLine("      --report <path>          写出导入报告（JSON）");
        writer.WriteLine("      --fail-on <mode>         warnings：有警告时退出码 1；none：不因此失败（默认 none）");
        writer.WriteLine("  -h, --help                   显示帮助");
        writer.WriteLine("  -v, --version                显示版本");
        writer.WriteLine();
        writer.WriteLine("退出码: 0 成功；1 生成失败 / 校验不一致 / 命中 --fail-on warnings；2 用法错误");
    }
}
