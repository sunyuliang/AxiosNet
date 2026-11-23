namespace AxiosNet.Comparison.Targets;

/// <summary>被测实现使用的 API 形态：强类型直调、请求模型对象、声明式接口。</summary>
internal enum ApiStyle
{
    /// <summary>强类型入口：把模型直接交给库的泛型方法，返回类型由调用处指定。</summary>
    Typed = 0,

    /// <summary>模型式入口：用一个实现 <c>IApiRequest&lt;T&gt;</c> 的模型对象描述路由与正文。</summary>
    Model = 1,

    /// <summary>接口式入口：声明接口方法，由代理按方法与参数上的特性绑定路由与正文。</summary>
    Interface = 2
}

/// <summary><see cref="ApiStyle"/> 的显示名、CSV 记号与解析。</summary>
internal static class ApiStyles
{
    /// <summary>全部形态，顺序即启动菜单的 1 / 2 / 3。</summary>
    public static IReadOnlyList<ApiStyle> All { get; } = [ApiStyle.Typed, ApiStyle.Model, ApiStyle.Interface];

    /// <summary>表与提示里的中文名；null 表示不区分形态（服务端基线），显示为 —。</summary>
    public static string Describe(ApiStyle? style) => style switch
    {
        ApiStyle.Typed => "强类型",
        ApiStyle.Model => "模型式",
        ApiStyle.Interface => "接口式",
        _ => "—"
    };

    /// <summary>CSV 列的 ASCII 记号；null 写 -。</summary>
    public static string Token(ApiStyle? style) => style switch
    {
        ApiStyle.Typed => "typed",
        ApiStyle.Model => "model",
        ApiStyle.Interface => "interface",
        _ => "-"
    };

    /// <summary>该形态在 AxiosNet 里的调用形状，用于启动菜单。</summary>
    public static string Example(ApiStyle style) => style switch
    {
        ApiStyle.Typed => "client.PostAsync<User>(route, model)",
        ApiStyle.Model => "client.RequestAsync(new SaveUserRequest { Body = model })",
        _ => "client.Create<ISaveUserApi>().SaveUserAsync(model)"
    };

    /// <summary>解析命令行与菜单输入：接受 1 / 2 / 3、typed / model / interface 与中文名，大小写不敏感。</summary>
    public static bool TryParse(string text, out ApiStyle style)
    {
        switch (text.Trim().ToUpperInvariant())
        {
            case "1":
            case "TYPED":
            case "STRONG":
            case "强类型":
                style = ApiStyle.Typed;
                return true;
            case "2":
            case "MODEL":
            case "模型式":
            case "模型":
                style = ApiStyle.Model;
                return true;
            case "3":
            case "INTERFACE":
            case "接口式":
            case "接口":
                style = ApiStyle.Interface;
                return true;
            default:
                style = default;
                return false;
        }
    }

    /// <summary>
    /// 解析多选输入：逗号 / 空格 / 顿号 / 分号分隔，每段按 <see cref="TryParse"/> 归一，重复项只保留第一次出现；
    /// <c>all</c> / <c>*</c> / 全部 展开成三种。任一段不认识或整体为空时返回 false。
    /// </summary>
    public static bool TryParseSelection(string text, out IReadOnlyList<ApiStyle> styles)
    {
        var result = new List<ApiStyle>();
        var parts = text.Split(
            new[] { ',', ';', ' ', '\t', '、' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            if (IsAll(part))
            {
                foreach (var style in All)
                {
                    Add(result, style);
                }

                continue;
            }

            if (!TryParse(part, out var parsed))
            {
                styles = [];
                return false;
            }

            Add(result, parsed);
        }

        styles = result;
        return result.Count > 0;
    }

    /// <summary>整段是否表示「三种全选」。</summary>
    private static bool IsAll(string text)
        => text.Trim().ToUpperInvariant() is "ALL" or "*" or "全部" or "都";

    /// <summary>按首次出现顺序追加，已选过的不重复加。</summary>
    private static void Add(List<ApiStyle> styles, ApiStyle style)
    {
        if (!styles.Contains(style))
        {
            styles.Add(style);
        }
    }
}
