using System.Reflection;
using System.Text;
using System.Text.Json;
using AxiosNet.Cli.Generation;
using AxiosNet.Cli.Model;
using AxiosNet.Cli.Spec;

namespace AxiosNet.Cli;

/// <summary>命令行主流程：读文档 → 归一化 → 读成 IR → 生成 → 落地 → 报告。</summary>
internal static class OpenApiGenerator
{
    /// <summary>工具版本：取程序集 InformationalVersion，并去掉 SourceLink 追加的 <c>+commit</c> 后缀；取不到时退回程序集版本或 <c>1.5.0</c>。</summary>
    public static string Version
    {
        get
        {
            var assembly = typeof(OpenApiGenerator).Assembly;
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrEmpty(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }

            return assembly.GetName().Version?.ToString(3) ?? "1.5.0";
        }
    }

    /// <summary>
    /// 完整生成流程。退出码：0 成功；1 落盘 / 校验失败或 <c>--fail-on warnings</c> 命中；用法错误由 <see cref="Program"/> 处理。
    /// </summary>
    public static async Task<int> RunAsync(CliOptions options)
    {
        ValidateNamespace(options.Namespace);

        var version = Version;

        // 1) 读入：本地文件 / URL / stdin，统一成一份文本 + 可读来源名。
        var source = await SpecSourceLoader.LoadAsync(options).ConfigureAwait(false);

        var warnings = new WarningCollector();

        // 2) 解析：按内容选 JSON / YAML，得到与格式无关的节点树。
        var root = SpecParser.Parse(source.Content, source.DisplayName);

        // 3) 归一化：Swagger 2.0 就地转换成 OpenAPI 3 形状。
        var normalized = SpecNormalizer.Normalize(root, source.DisplayName, warnings);

        // 4) 读成 IR：分组、参数、正文、响应、授权与类型全部落成生成器自己的模型。
        // 读取失败（例如所有操作都无法表达）时也要先打印已收集的警告，用户才知道哪些操作、为什么被跳过。
        ApiSpec spec;
        try
        {
            spec = new ApiReader(normalized, options, warnings).Read();
        }
        catch (GenerationException)
        {
            PrintWarnings(warnings.Items);
            throw;
        }

        // 5) 排版：IR → 确定性的 C# 源码文件列表。
        var emitter = new CSharpEmitter(spec, options, version);
        var files = emitter.Emit().ToList();

        // --report 会写文件：路径与生成产物（含清单）撞名时先拦下来，
        // 否则报告会直接覆盖掉同名生成文件，产物被静默破坏。
        ValidateReportPath(options, files);

        PrintSummary(version, source, spec);

        // 6) 落地：覆盖保护、清理失联文件、--check / --dry-run 都在 OutputWriter 里。
        var exitCode = OutputWriter.Apply(
            options,
            files,
            tracked => new GeneratedFile(OutputWriter.ManifestName, BuildManifest(version, tracked)),
            spec.Warnings);

        // --dry-run / --check 约定不写任何文件：报告也是文件，必须一并跳过，
        // 否则 --check 会写出一份带 STALE_FILE 的报告，让校验结果自相矛盾。
        if (!options.DryRun && !options.Check && options.ReportPath is { } reportPath)
        {
            WriteReport(reportPath, version, source, spec, files);
        }

        PrintWarnings(spec.Warnings);

        if (exitCode != 0)
        {
            return 1;
        }

        return options.FailOnWarnings && spec.Warnings.Count > 0 ? 1 : 0;
    }

    /// <summary>校验 <c>--report</c> 的绝对路径不与本次写入生成目录的文件（含清单）同名。</summary>
    private static void ValidateReportPath(CliOptions options, IReadOnlyList<GeneratedFile> files)
    {
        // --check / --dry-run 一律不写报告，路径冲突不影响结果，不必报错。
        if (options.DryRun || options.Check || options.ReportPath is not { } reportPath)
        {
            return;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var reportFullPath = Path.GetFullPath(reportPath);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);

        foreach (var name in files.Select(file => file.Name).Append(OutputWriter.ManifestName))
        {
            var generatedPath = Path.GetFullPath(Path.Combine(outputDirectory, name));
            if (string.Equals(generatedPath, reportFullPath, comparison))
            {
                throw new CliUsageException(
                    $"--report 不能指向生成产物 {name}：{reportFullPath}（请换一个输出路径）。");
            }
        }
    }

    /// <summary>逐段校验命名空间是否由合法 C# 标识符组成（点分形式）。</summary>
    private static void ValidateNamespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new CliUsageException("--namespace 不能为空。");
        }

        foreach (var segment in value.Split('.'))
        {
            if (!Naming.IsIdentifier(segment))
            {
                throw new CliUsageException($"--namespace 不是合法的 C# 命名空间：{value}（{segment} 不是合法标识符）。");
            }
        }
    }

    /// <summary>生成清单（<c>axiosnet-gen.manifest.json</c>）：记录工具版本与本次跟踪的文件集合，供下次识别失联文件。</summary>
    private static string BuildManifest(string version, IReadOnlyCollection<string> tracked)
    {
        var payload = new
        {
            generator = "axiosnet-gen/" + version,
            files = tracked.OrderBy(name => name, StringComparer.Ordinal).ToArray()
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })
            .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";
    }

    /// <summary>打印来源、标题、生成形态与规模，作为每次运行的一行式摘要。</summary>
    private static void PrintSummary(string version, SpecSource source, ApiSpec spec)
    {
        Console.WriteLine($"axiosnet-gen {version}");
        Console.WriteLine($"  source: {source.DisplayName} ({spec.OpenApiVersion})");

        var title = spec.Title;
        if (spec.SpecVersion.Length > 0)
        {
            title += " " + spec.SpecVersion;
        }

        Console.WriteLine($"  title: {title}");
        Console.WriteLine($"  style: {(spec.Style == ApiStyle.Model ? "model" : "interface")}");
        Console.WriteLine(
            $"  operations: {spec.OperationCount}, groups: {spec.Groups.Count}, types: {spec.Types.Count}");
    }

    /// <summary>写出 <c>--report</c> 的 JSON：来源、版本、规模、分组与全部警告（含落盘阶段追加的 STALE_FILE / MANIFEST_INVALID）。</summary>
    private static void WriteReport(
        string reportPath,
        string version,
        SpecSource source,
        ApiSpec spec,
        IReadOnlyList<GeneratedFile> files)
    {
        var report = new
        {
            generator = "axiosnet-gen/" + version,
            source = source.DisplayName,
            openApiVersion = spec.OpenApiVersion,
            title = spec.Title,
            version = spec.SpecVersion,
            serverUrl = spec.ServerUrl,
            style = spec.Style == ApiStyle.Model ? "model" : "interface",
            operations = spec.OperationCount,
            groups = spec.Groups
                .Select(group => new
                {
                    name = spec.Style == ApiStyle.Model ? group.RequestFileName : group.InterfaceName,
                    tag = group.Tag,
                    operations = group.Operations.Count
                })
                .ToArray(),
            types = spec.Types.Count,
            files = files.Select(file => file.Name).Append(OutputWriter.ManifestName).ToArray(),
            warnings = spec.Warnings
                .Select(warning => new
                {
                    code = warning.Code,
                    location = warning.Location,
                    message = warning.Message
                })
                .ToArray()
        };

        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true })
                .Replace("\r\n", "\n", StringComparison.Ordinal) + "\n",
            new UTF8Encoding(false));

        Console.WriteLine($"  report: {fullPath}");
    }

    /// <summary>把警告打到控制台：最多打印 50 条，其余提示去 <c>--report</c> 查看，避免病态文档刷屏。</summary>
    private static void PrintWarnings(IReadOnlyList<ApiWarning> warnings)
    {
        if (warnings.Count == 0)
        {
            return;
        }

        Console.WriteLine($"  warnings: {warnings.Count}");
        var limit = Math.Min(warnings.Count, 50);
        for (var index = 0; index < limit; index++)
        {
            var warning = warnings[index];
            Console.WriteLine($"    [{warning.Code}] {warning.Location}: {warning.Message}");
        }

        if (warnings.Count > limit)
        {
            Console.WriteLine($"    … 其余 {warnings.Count - limit} 条见 --report。");
        }
    }
}
