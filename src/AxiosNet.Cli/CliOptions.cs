namespace AxiosNet.Cli;

/// <summary>生成形态：模型式（<c>IApiRequest&lt;T&gt;</c> 请求模型）或接口式（<c>Create&lt;T&gt;()</c> 代理）。</summary>
internal enum ApiStyle
{
    /// <summary>每个操作生成一个可 <c>RequestAsync</c> 的请求模型（默认）。</summary>
    Model,

    /// <summary>每个 tag 生成一个接口，由客户端 <c>Create&lt;T&gt;()</c> 动态代理。</summary>
    Interface
}

/// <summary>命令行参数（手写解析：不接受未知选项，避免拼写错误被静默忽略）。</summary>
internal sealed class CliOptions
{
    /// <summary>文档来源：本地路径、http(s) URL，或 <c>-</c> 表示 stdin。</summary>
    public string? Source { get; private set; }

    /// <summary>生成代码的命名空间，默认 <c>OpenApi.Generated</c>，解析后逐段校验是否为合法标识符。</summary>
    public string Namespace { get; private set; } = "OpenApi.Generated";

    /// <summary>生成代码的输出目录，默认 <c>Generated</c>。</summary>
    public string OutputDirectory { get; private set; } = "Generated";

    /// <summary>默认模型式：生成的每个操作是一个可 <c>RequestAsync</c> 的请求模型。</summary>
    public ApiStyle Style { get; private set; } = ApiStyle.Model;

    /// <summary>接口式且单接口模式下的接口名，默认 <c>IApiClient</c>。</summary>
    public string InterfaceName { get; private set; } = "IApiClient";

    /// <summary>接口名是否由命令行显式给出（模型式下显式给出视为用法错误）。</summary>
    public bool InterfaceNameExplicit { get; private set; }

    /// <summary>是否把所有操作收进一个接口（仅接口式）。</summary>
    public bool SingleInterface { get; private set; }

    /// <summary>是否把全部代码写进单个文件（默认按分组一个文件 + Models.g.cs）。</summary>
    public bool SingleFile { get; private set; }

    /// <summary>接口式是否把返回值包成 <c>AxiosResponse&lt;T&gt;</c>（默认 true；<c>none</c> 时只交付 <c>T</c>）。</summary>
    public bool UseResponseWrapper { get; private set; } = true;

    /// <summary>无 <c>format</c> 的 integer 是否映射成 <c>long</c>（默认 <c>int</c>）。</summary>
    public bool IntegerIsLong { get; private set; }

    /// <summary>拉取远程文档时附加的请求头（<c>--header name:value</c>，可重复）。</summary>
    public List<KeyValuePair<string, string>> Headers { get; } = new();

    /// <summary>远程文档下载超时，默认 30 秒。</summary>
    public TimeSpan Timeout { get; private set; } = TimeSpan.FromSeconds(30);

    /// <summary>只校验输出是否与生成结果一致，不写文件。</summary>
    public bool Check { get; private set; }

    /// <summary>把生成内容打印到 stdout，不写文件。</summary>
    public bool DryRun { get; private set; }

    /// <summary>允许覆盖不带生成标记的文件、删除失联的旧生成文件。</summary>
    public bool Force { get; private set; }

    /// <summary>导入报告（JSON）的输出路径；为空表示不写报告。</summary>
    public string? ReportPath { get; private set; }

    /// <summary>是否在存在警告时以退出码 1 失败（<c>--fail-on warnings</c>）。</summary>
    public bool FailOnWarnings { get; private set; }

    /// <summary>是否请求打印帮助（<c>-h</c> / <c>--help</c>），解析后短路其余校验。</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>是否请求打印版本（<c>-v</c> / <c>--version</c>），解析后短路其余校验。</summary>
    public bool ShowVersion { get; private set; }

    /// <summary>
    /// 解析命令行。每遇到未知选项立即报错；解析完成后统一做互斥校验（<c>--check</c>/<c>--dry-run</c>、
    /// 仅接口式可用的选项等），保证在真正读文档之前就把用法错误拦下来。
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            // "--" 之后一律当作文档来源（允许文件名以 - 开头）。
            if (arg == "--")
            {
                for (i++; i < args.Length; i++)
                {
                    options.SetSource(args[i]);
                }

                break;
            }

            if (arg == "-")
            {
                options.SetSource(arg);
                continue;
            }

            if (!arg.StartsWith("-", StringComparison.Ordinal) || arg == "-")
            {
                options.SetSource(arg);
                continue;
            }

            var (name, inlineValue) = SplitOption(arg);

            switch (name)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;

                case "-v":
                case "--version":
                    options.ShowVersion = true;
                    break;

                case "-n":
                case "--namespace":
                    options.Namespace = TakeValue(name, inlineValue, args, ref i);
                    break;

                case "-o":
                case "--output":
                    options.OutputDirectory = TakeValue(name, inlineValue, args, ref i);
                    break;

                case "--style":
                    options.Style = TakeValue(name, inlineValue, args, ref i) switch
                    {
                        "model" => ApiStyle.Model,
                        "interface" => ApiStyle.Interface,
                        var value => throw new CliUsageException($"--style 只支持 model / interface（收到 {value}）。")
                    };
                    break;

                case "-i":
                case "--interface-name":
                    var interfaceName = TakeValue(name, inlineValue, args, ref i);
                    // 接口名同时是单文件模式下的输出文件名：必须是合法标识符，否则会写出目录外的路径。
                    if (!Generation.Naming.IsIdentifier(interfaceName))
                    {
                        throw new CliUsageException($"--interface-name 不是合法的 C# 标识符：{interfaceName}。");
                    }

                    options.InterfaceName = interfaceName;
                    options.InterfaceNameExplicit = true;
                    break;

                case "--single-interface":
                    options.SingleInterface = true;
                    break;

                case "--single-file":
                    options.SingleFile = true;
                    break;

                case "-w":
                case "--response-wrapper":
                    options.UseResponseWrapper = TakeValue(name, inlineValue, args, ref i) switch
                    {
                        "response" => true,
                        "none" => false,
                        var value => throw new CliUsageException($"--response-wrapper 只支持 response / none（收到 {value}）。")
                    };
                    break;

                case "--integer-type":
                    options.IntegerIsLong = TakeValue(name, inlineValue, args, ref i) switch
                    {
                        "int" => false,
                        "long" => true,
                        var value => throw new CliUsageException($"--integer-type 只支持 int / long（收到 {value}）。")
                    };
                    break;

                case "--header":
                    var header = TakeValue(name, inlineValue, args, ref i);
                    var colon = header.IndexOf(':');
                    if (colon <= 0)
                    {
                        throw new CliUsageException($"--header 需要 name:value 形式（收到 {header}）。");
                    }

                    // 头名必须是非空 token：HttpClient.TryAddWithoutValidation 对非法名字会静默丢弃，
                    // 与其让下载悄悄少一个头，不如在这里报用法错误。
                    var headerName = header[..colon].Trim();
                    if (!IsHeaderToken(headerName))
                    {
                        throw new CliUsageException($"--header 的请求头名不合法：{headerName}。");
                    }

                    options.Headers.Add(new KeyValuePair<string, string>(
                        headerName,
                        header[(colon + 1)..].Trim()));
                    break;

                case "--timeout":
                    var timeoutText = TakeValue(name, inlineValue, args, ref i);
                    // IsFinite 拦住 NaN / Infinity：TimeSpan.FromSeconds 对它们直接抛异常，会绕过用法错误的统一处理；
                    // 上限 86400 秒（一天）既覆盖实际需求，也避免溢出 TimeSpan 表示范围。
                    if (!double.TryParse(timeoutText, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                        || !double.IsFinite(seconds)
                        || seconds <= 0
                        || seconds > 86400)
                    {
                        throw new CliUsageException($"--timeout 需要 (0, 86400] 之间的秒数（收到 {timeoutText}）。");
                    }

                    var timeout = TimeSpan.FromSeconds(seconds);
                    // 极小的秒数（如 1e-9）能通过 (0, 86400] 检查，但会被 TimeSpan 舍入成 0，
                    // HttpClient 随后抛 ArgumentOutOfRangeException，绕过用法错误（退出码变成 1）。
                    if (timeout <= TimeSpan.Zero)
                    {
                        throw new CliUsageException($"--timeout 过小，无法表示成正的时长（收到 {timeoutText}）。");
                    }

                    options.Timeout = timeout;
                    break;

                case "--check":
                    options.Check = true;
                    break;

                case "--dry-run":
                    options.DryRun = true;
                    break;

                case "-f":
                case "--force":
                    options.Force = true;
                    break;

                case "--report":
                    options.ReportPath = TakeValue(name, inlineValue, args, ref i);
                    break;

                case "--fail-on":
                    options.FailOnWarnings = TakeValue(name, inlineValue, args, ref i) switch
                    {
                        "none" => false,
                        "warnings" => true,
                        var value => throw new CliUsageException($"--fail-on 只支持 none / warnings（收到 {value}）。")
                    };
                    break;

                default:
                    throw new CliUsageException($"未知选项：{name}。");
            }
        }

        // 帮助 / 版本是终态动作：不要求文档参数，也不再校验其它选项组合。
        if (options.ShowHelp || options.ShowVersion)
        {
            return options;
        }

        if (options.Source is null)
        {
            throw new CliUsageException("缺少 OpenAPI 文档参数（本地路径 / URL / -）。");
        }

        // 空路径会让 Path.GetFullPath 抛原始异常、绕过用法错误的统一处理，这里先拦下来。
        if (string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            throw new CliUsageException("--output 不能为空。");
        }

        if (options.ReportPath is not null && string.IsNullOrWhiteSpace(options.ReportPath))
        {
            throw new CliUsageException("--report 不能为空。");
        }

        if (options.Check && options.DryRun)
        {
            throw new CliUsageException("--check 与 --dry-run 不能同时使用。");
        }

        // 模型式的请求类固定继承 ApiRequest<T>、固定返回 AxiosResponse<T>，下列选项没有落点。
        if (options.Style == ApiStyle.Model)
        {
            if (options.SingleInterface)
            {
                throw new CliUsageException("--single-interface 只对接口式生效：模型式请改用 --style interface。");
            }

            if (options.InterfaceNameExplicit)
            {
                throw new CliUsageException("--interface-name 只对接口式生效：模型式请改用 --style interface。");
            }

            if (!options.UseResponseWrapper)
            {
                throw new CliUsageException("--response-wrapper none 只对接口式生效：请求模型固定返回 AxiosResponse<T>。");
            }
        }

        if (options.InterfaceNameExplicit && !options.SingleInterface)
        {
            throw new CliUsageException("--interface-name 需要与 --single-interface 一起使用。");
        }

        return options;
    }

    /// <summary>请求头名是否合法：RFC 7230 的 token（非空、仅字母数字与 <c>!#$%&amp;'*+-.^_`|~</c>）。</summary>
    private static bool IsHeaderToken(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>把 <c>--name=value</c> 拆成名字与内联取值；没有 <c>=</c> 时值返回 null，表示从下一个参数取。</summary>
    private static (string Name, string? Value) SplitOption(string arg)
    {
        var equals = arg.IndexOf('=');
        return equals < 0 ? (arg, null) : (arg[..equals], arg[(equals + 1)..]);
    }

    /// <summary>
    /// 取选项的取值：优先内联值，否则吃掉下一个参数；到末尾或以另一个选项（<c>-x</c> / <c>--x</c>）开头时
    /// 报用法错误——否则 <c>--output --check</c> 会把 <c>--check</c> 当成目录名，校验被悄悄取消。
    /// </summary>
    private static string TakeValue(string name, string? inlineValue, string[] args, ref int index)
    {
        if (inlineValue is not null)
        {
            return inlineValue;
        }

        if (index + 1 >= args.Length)
        {
            throw new CliUsageException($"{name} 缺少取值。");
        }

        var next = args[index + 1];

        // 下一个参数看起来是选项（--x / -n）时视为缺值；负数之类的取值仍交给各自选项做范围校验，
        // 这样 --timeout -1 报的是范围错误，而不是笼统的「缺少取值」。
        var looksLikeOption = next == "--"
            || (next.Length > 1
                && next[0] == '-'
                && !double.TryParse(
                    next,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out _));
        if (looksLikeOption)
        {
            throw new CliUsageException($"{name} 缺少取值（下一个参数是选项 {next}）。");
        }

        index++;
        return next;
    }

    /// <summary>登记文档来源；显式只允许一份，重复出现直接报用法错误（避免误把选项值当成第二份文档）。</summary>
    private void SetSource(string value)
    {
        if (Source is not null)
        {
            throw new CliUsageException($"只能指定一份文档（已有 {Source}，又收到 {value}）。");
        }

        Source = value;
    }
}
