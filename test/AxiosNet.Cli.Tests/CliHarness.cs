using System.Text.Json;

namespace AxiosNet.Cli.Tests;

/// <summary>
/// 测试直接调用 <see cref="OpenApiGenerator.RunAsync"/>（InternalsVisibleTo），比启动子进程快，
/// 退出码 / 报告 / 落盘结果都能拿到；生成过程打印的摘要留在测试输出里，便于失败时排查。
/// </summary>
internal static class CliHarness
{
    /// <summary>生成代码统一使用的命名空间，避免与测试自身类型互相干扰。</summary>
    public const string Namespace = "AxiosNet.Cli.Tests.Generated";

    /// <summary>取得随测试程序集一起复制的夹具文件绝对路径。</summary>
    public static string Fixture(string name)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>用内置夹具跑一次生成，额外参数原样透传给 CLI。</summary>
    public static CliRun Run(string fixtureName, params string[] extra)
        => RunSource(Fixture(fixtureName), extra);

    /// <summary>把内联文本写成临时 *.json / *.yaml 再生成，用于临时构造的文档。</summary>
    public static CliRun RunText(string text, string extension, params string[] extra)
    {
        // 临时文档放在系统临时目录，生成结束后立即删除，不污染仓库。
        var path = Path.Combine(Path.GetTempPath(), "axiosnet-cli-spec-" + Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, text);
        try
        {
            return RunSource(path, extra);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>为单次生成新建独立临时目录；异常时清理目录，避免残留。</summary>
    public static CliRun RunSource(string source, params string[] extra)
    {
        // 每次生成使用唯一目录，测试之间互不影响。
        var directory = Path.Combine(Path.GetTempPath(), "axiosnet-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var exitCode = RunSourceInto(source, directory, extra);
            return new CliRun(directory, exitCode);
        }
        catch
        {
            Delete(directory);
            throw;
        }
    }

    /// <summary>在既有输出目录上运行生成，返回退出码；用于验证 --check / --force 等重复执行场景。</summary>
    public static int RunSourceInto(string source, string directory, params string[] extra)
    {
        // 固定输出目录、命名空间与报告路径，额外参数覆盖在它们之后。
        var args = new List<string>
        {
            source, "-o", directory, "-n", Namespace,
            "--report", Path.Combine(directory, CliRun.ReportName)
        };
        args.AddRange(extra);
        return OpenApiGenerator.RunAsync(CliOptions.Parse(args.ToArray())).GetAwaiter().GetResult();
    }

    /// <summary>尽力删除临时目录；删除失败不应影响测试结论。</summary>
    internal static void Delete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // 临时目录清理失败不该让断言结果变红。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>一次生成的结果：输出目录、退出码，以及报告里的告警。</summary>
internal sealed class CliRun : IDisposable
{
    /// <summary>CLI 写出的机器可读报告文件名，与生成文件同目录。</summary>
    public const string ReportName = "axiosnet-gen.report.json";

    /// <summary>报告按需解析一次并缓存：未传 --report 或生成失败时可能为 null。</summary>
    private readonly Lazy<JsonDocument?> _report;

    /// <summary>绑定一次生成的输出目录与退出码，报告在首次访问时才解析。</summary>
    public CliRun(string directory, int exitCode)
    {
        Directory = directory;
        ExitCode = exitCode;
        _report = new Lazy<JsonDocument?>(LoadReport);
    }

    /// <summary>本次生成的输出目录（临时目录）。</summary>
    public string Directory { get; }

    /// <summary>生成器返回的退出码：0 成功，1 失败 / 不一致，2 用法错误。</summary>
    public int ExitCode { get; }

    /// <summary>把生成产物文件名拼成绝对路径。</summary>
    public string PathOf(string fileName) => Path.Combine(Directory, fileName);

    /// <summary>判断某个生成产物是否落盘。</summary>
    public bool Exists(string fileName) => File.Exists(PathOf(fileName));

    /// <summary>读取生成产物全文，用于文本断言。</summary>
    public string Read(string fileName) => File.ReadAllText(PathOf(fileName));

    /// <summary>输出目录下的全部文件名，按序数排序，便于做确定性比较。</summary>
    public string[] FileNames
        => System.IO.Directory.GetFiles(Directory)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray()!;

    /// <summary>报告里的全部告警码。</summary>
    public string[] Codes => WarningNodes
        .Select(node => node.GetProperty("code").GetString()!)
        .ToArray();

    /// <summary>按告警码查第一条对应消息；没有该告警时返回 null。</summary>
    public string? MessageFor(string code)
        => WarningNodes
            .FirstOrDefault(node => node.GetProperty("code").GetString() == code)
            .GetProperty("message")
            .GetString();

    /// <summary>报告记录的操作数量。</summary>
    public int Operations => _report.Value!.RootElement.GetProperty("operations").GetInt32();

    /// <summary>报告记录的生成形态（model / interface）。</summary>
    public string Style => _report.Value!.RootElement.GetProperty("style").GetString()!;

    /// <summary>报告记录的分组（接口）数量。</summary>
    public int GroupCount => _report.Value!.RootElement.GetProperty("groups").GetArrayLength();

    /// <summary>报告记录的模型类型数量。</summary>
    public int TypeCount => _report.Value!.RootElement.GetProperty("types").GetInt32();

    /// <summary>报告 warnings 数组中的每个元素。</summary>
    private IEnumerable<JsonElement> WarningNodes
        => _report.Value!.RootElement.GetProperty("warnings").EnumerateArray();

    /// <summary>报告文件不存在时返回 null（例如生成在写报告前就失败）。</summary>
    private JsonDocument? LoadReport()
    {
        var path = PathOf(ReportName);
        return File.Exists(path) ? JsonDocument.Parse(File.ReadAllText(path)) : null;
    }

    /// <summary>释放报告文档并删除临时输出目录。</summary>
    public void Dispose()
    {
        _report.Value?.Dispose();
        CliHarness.Delete(Directory);
    }
}
