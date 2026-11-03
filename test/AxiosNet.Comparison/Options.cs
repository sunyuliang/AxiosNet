using System.Globalization;
using AxiosNet.Comparison.Targets;

namespace AxiosNet.Comparison;

/// <summary>命令行参数。无参数时按默认口径跑全量对比，不设门禁（只出数字）。</summary>
internal sealed class Options
{
    /// <summary>每个实现同时压测的 worker 数（<c>--concurrency</c>，默认 8）；与轮次无关，恒为常驻连接数。</summary>
    public int Concurrency { get; private set; } = 8;

    /// <summary>每轮测量窗口的秒数（<c>--duration</c>，默认 1）；下限 0.2，窗口再短 p99 只是在测抖动。</summary>
    public double DurationSeconds { get; private set; } = 1;

    /// <summary>测量轮数（<c>--rounds</c>，默认 10）；每轮轮转实现顺序，汇总取轮中位数。</summary>
    public int Rounds { get; private set; } = 10;

    /// <summary>每个实现的预热秒数（<c>--warmup</c>，默认 3）：让分层编译、连接池与 TLS 先到位，预热数据不计入结果。</summary>
    public double WarmupSeconds { get; private set; } = 3;

    /// <summary>预热后等待分层编译在后台落地的秒数（首次运行尤其需要；跑热以后可以不看）。</summary>
    public double CooldownSeconds { get; private set; } = 2;

    /// <summary>参与对比的实现（已归一成 <see cref="LibraryCatalog"/> 里的标准名）；RawSocket 基线由目录自动追加。</summary>
    public IReadOnlyList<string> Libraries { get; private set; } = LibraryCatalog.DefaultLibraries;

    /// <summary>AxiosNet 吞吐 / HttpClient 吞吐的下限；null 表示不做门禁。</summary>
    public double? MinRatio { get; private set; }

    /// <summary>AxiosNet 净分配 / HttpClient 净分配的上限；null 表示不做门禁。</summary>
    public double? MaxAllocRatio { get; private set; }

    /// <summary>报告输出目录（<c>--out</c>，默认 results，相对当前工作目录）。</summary>
    public string OutputDirectory { get; private set; } = "results";

    /// <summary>报告文件名后缀（<c>--tag</c>）：同一个输出目录要归档多组结果时用来区分，非法文件名字符会被替换。</summary>
    public string? Tag { get; private set; }

    /// <summary>只打印用法就返回（<c>-h</c> / <c>--help</c>），不跑任何测量。</summary>
    public bool ShowHelp { get; private set; }

    /// <summary>解析命令行；<c>--name=value</c> 与 <c>--name value</c> 两种写法都认，值非法统一抛 <see cref="ArgumentException"/>（外层翻成退出码 3）。</summary>
    public static Options Parse(string[] args)
    {
        var options = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            var (name, inlineValue) = Split(args[i]);
            string Value() => inlineValue ?? (i + 1 < args.Length
                ? args[++i]
                : throw new ArgumentException($"{name} 缺少值。"));

            switch (name)
            {
                case "-h":
                case "--help":
                    options.ShowHelp = true;
                    break;
                case "--concurrency":
                    options.Concurrency = ParseInt(name, Value(), min: 1);
                    break;
                case "--duration":
                    options.DurationSeconds = ParseDouble(name, Value(), min: 0.2);
                    break;
                case "--rounds":
                    options.Rounds = ParseInt(name, Value(), min: 1);
                    break;
                case "--warmup":
                    options.WarmupSeconds = ParseDouble(name, Value(), min: 0);
                    break;
                case "--cooldown":
                    options.CooldownSeconds = ParseDouble(name, Value(), min: 0);
                    break;
                case "--libs":
                    options.Libraries = ParseLibraries(Value());
                    break;
                case "--min-ratio":
                    options.MinRatio = ParseDouble(name, Value(), min: 0);
                    break;
                case "--max-alloc-ratio":
                    options.MaxAllocRatio = ParseDouble(name, Value(), min: 0);
                    break;
                case "--out":
                    // 空串经 Path.GetFullPath 会落到当前目录，等于用户以为自己指定了目录、其实写进了 cwd。
                    options.OutputDirectory = Value().Trim() is { Length: > 0 } directory
                        ? directory
                        : throw new ArgumentException("--out 需要一个非空目录。");
                    break;
                case "--tag":
                    options.Tag = Value();
                    break;
                default:
                    throw new ArgumentException($"未知参数：{args[i]}");
            }
        }

        return options;
    }

    /// <summary>打印用法、退出码约定与测量口径提示（2 = 有失败请求、1 = 门禁未过、3 = 参数错误）。</summary>
    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            跨实现横向基准：同一回环服务器、同一负载，对比 HTTP 客户端的 RPS / 分配 / p99。

            用法：
              dotnet run -c Release -- [选项]

            选项：
              --concurrency <n>        每实现的并发 worker 数（默认 8）
              --duration <秒>          每轮测量窗口（默认 1）
              --rounds <n>             测量轮数，每轮轮转实现顺序（默认 10）
              --warmup <秒>            每实现预热窗口（默认 3）
              --cooldown <秒>          预热后等待分层编译（默认 2）
              --libs <a,b,...>         参与对比的实现（默认 HttpClient,Refit,RestSharp,Flurl,AxiosNet）
                                       RawSocket 会自动追加为服务端基线
              --min-ratio <比值>       RPS 门禁：AxiosNet / HttpClient 低于该值退出码 1（0.85 = 85%）
              --max-alloc-ratio <x>    分配门禁：AxiosNet / HttpClient 净分配高于该值退出码 1
              --out <目录>             报告输出目录（默认 results）
              --tag <名字>             报告文件名后缀，便于归档
              -h, --help               显示本帮助

            退出码：0 正常；1 门禁未通过；2 存在失败请求（数字不可信）；3 参数错误。

            示例：
              dotnet run -c Release
              dotnet run -c Release -- --rounds 7 --min-ratio 0.85 --max-alloc-ratio 2
              dotnet run -c Release -- --libs AxiosNet,HttpClient --duration 2 --tag quick

            口径提示：
              · 每轮轮转实现顺序；预热后用 RawSocket 校准机器状态（最近 3 窗 RPS 极差 ≤ 15% 才开测）。
              · 分配是进程级（含同进程回环服务端），报告里的「净分配」已减掉 RawSocket 基线。
              · 绝对值随机器负载漂移，跨版本比较请看比值与同一窗口内的相对关系。
            """);
    }

    private static (string Name, string? Value) Split(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq < 0 ? (arg, null) : (arg[..eq], arg[(eq + 1)..]);
    }

    /// <summary>解析整数参数并检查下界；不满足就抛出，由入口转成用法提示。</summary>
    private static int ParseInt(string name, string text, int min)
        => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= min
            ? value
            : throw new ArgumentException($"{name} 需要 ≥ {min} 的整数，收到「{text}」。");

    /// <summary>解析浮点参数：先拒掉非有限值（<c>Infinity</c> 会让后续换算溢出），再检查下界。</summary>
    private static double ParseDouble(string name, string text, double min)
        // TryParse 会接受 "Infinity"，而 TimeSpan.FromSeconds(∞) 会抛 OverflowException；
        // 这类值必须在参数层拦掉，否则用户拿到的是未处理异常而不是用法提示（退出码 3）。
        => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && double.IsFinite(value)
            && value >= min
            ? value
            : throw new ArgumentException($"{name} 需要 ≥ {min:0.##} 的有限数，收到「{text}」。");

    /// <summary>解析逗号分隔的实现名列表并逐个规范化；有未知名字时报错并列出可选值。</summary>
    private static IReadOnlyList<string> ParseLibraries(string text)
    {
        var result = new List<string>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!LibraryCatalog.TryCanonicalize(part, out var canonical))
            {
                throw new ArgumentException(
                    $"未知实现「{part}」。可选：{string.Join(", ", LibraryCatalog.Names)}");
            }

            if (!result.Contains(canonical, StringComparer.Ordinal))
            {
                result.Add(canonical);
            }
        }

        if (result.Count == 0)
        {
            throw new ArgumentException("--libs 至少要有一个实现。");
        }

        return result;
    }
}
