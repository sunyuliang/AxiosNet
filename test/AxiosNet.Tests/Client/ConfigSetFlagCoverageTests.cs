using System.Reflection;

namespace AxiosNet.Tests.Client;

/// <summary>
/// 配置模型的对账护栏：<b>每个公共可写属性都必须有一个同名的 <c>*Set</c> 标记</b>。
/// </summary>
/// <remarks>
/// 为什么要机检：<see cref="AxiosConfig"/> 的合并语义靠 <c>*Set</c> 区分「调用方显式写入」与「碰巧等于默认值」。
/// 新增一个可写属性却忘了加标记，编译器不会报错、现有用例也全绿 —— 症状是合并实例默认值时这一项被静默丢弃
/// （调用方明明写了却不生效），排查成本极高。这条用例让「忘加标记」在 CI 上立刻变红。
/// <para>
/// 反向不检查：<c>*Set</c> 可以比属性多（派生标记如 <c>AuthSet</c> 由子对象持有），多出来不影响合并语义。
/// </para>
/// <para>
/// 豁免清单是「确实不需要标记」的成员：整体替换型门面（子配置对象本身）与只读派生视图。
/// 往清单里加名字等于放宽护栏，必须同时说明为什么该项不参与合并。
/// </para>
/// </remarks>
public sealed class ConfigSetFlagCoverageTests
{
    /// <summary>
    /// 不需要 <c>*Set</c> 标记的成员，逐条说明为什么它不参与「显式写过 / 默认值」的区分。
    /// </summary>
    /// <remarks>
    /// 往这里加名字等于放宽护栏：每条都必须写清理由，且 <see cref="WithoutFlagList_OnlyNamesRealFacades"/> 会挡住拼错的名字。
    /// </remarks>
    private static readonly Dictionary<string, string> WithoutFlag = new(StringComparer.Ordinal)
    {
        // ── 容器 / 本次请求专用：合并语义由专门代码负责，不靠标记 ──
        ["AxiosConfig.Headers"] = "容器：合并走 HasHeaders / 逐项合并，不靠标记",
        ["AxiosConfig.ShortCircuit"] = "只描述这一次请求：Clone 会清空，放在 Defaults 上不生效",
        ["AxiosConfig.Url"] = "每次请求的目标地址：MergeConfig 里按 perCall.Url ?? merged.Url 显式处理",
        ["AxiosConfig.BaseAddress"] = "实例级整块替换：库产出的配置沿用来源实例的值，不参与按请求合并",

        // ── 委托 / 事件：合并时整块替换，没有「部分写入」的可能 ──
        ["CircuitSettings.OnRejected"] = "事件回调：非空即整块替换",
        ["RetrySettings.OnRetry"] = "事件回调：非空即整块替换",
        ["IdempotencySettings.KeyFactory"] = "委托：非空即整块替换",

        // ── 子配置门面：自身是懒分配对象，内部各字段各自有标记 ──
        ["ResilienceSettings.Retry"] = "子配置门面：内部 RetrySettings 自管标记",
        ["ResilienceSettings.Circuit"] = "子配置门面：内部 CircuitSettings 自管标记",
        ["ResilienceSettings.Hedging"] = "子配置门面：内部 HedgingSettings 自管标记",
        ["ResilienceSettings.Failover"] = "子配置门面：内部 FailoverSettings 自管标记",
        ["ResilienceSettings.Idempotency"] = "子配置门面：内部 IdempotencySettings 自管标记",
        ["AxiosConfig.Serialization"] = "子配置门面：内部各字段各自有标记（ValueText / QueryEncoder 等）",
        ["AxiosConfig.Transport"] = "子配置门面：内部各字段各自有标记",
        ["AxiosConfig.Observability"] = "子配置门面：回调组有 ClearSetFlags() 统一管理",
        ["AxiosConfig.Limits"] = "子配置门面：内部 LimitsSettings 自管标记",
        ["AxiosConfig.Caching"] = "子配置门面：内部 AxiosCacheOptions 自管标记",

        // ── 整体替换型容器：要么是非空即替换，要么是「对象本身」不参与字段级合并 ──
        ["SerializationSettings.JsonOptions"] = "序列化器选项对象：非空即整块替换",
        ["SerializationSettings.BodySerializers"] = "序列化器表：整体替换（表内按内容协商选择）",
        ["SerializationSettings.QueryEncoder"] = "委托：非空即整块替换",
        ["SerializationSettings.FormFieldName"] = "委托：非空即整块替换",
        ["SerializationSettings.TransformRequest"] = "转换链：按 TransformRequestCount / TransformInheritedRequest 计长度，不用标记",
        ["SerializationSettings.TransformResponse"] = "转换链：按 TransformResponseCount / TransformInheritedResponse 计长度，不用标记",
    };

    /// <summary>反射遍历整个配置面（<see cref="AxiosConfig"/> 及其递归嵌套的公共设置类）。</summary>
    /// <returns>按类型名排序的配置面类型。</returns>
    private static List<Type> ConfigSurface()
    {
        var seen = new List<Type>();
        void Walk(Type type)
        {
            if (seen.Contains(type))
            {
                return;
            }

            seen.Add(type);
            foreach (var nested in type.GetNestedTypes(BindingFlags.Public))
            {
                if (nested.IsClass && !nested.IsAbstract)
                {
                    Walk(nested);
                }
            }
        }

        Walk(typeof(AxiosConfig));
        return seen.OrderBy(type => type.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>公共可写实例属性（排除索引器与 <c>init</c>）。</summary>
    /// <param name="type">要枚举的类型。</param>
    /// <returns>属性列表。</returns>
    private static IEnumerable<PropertyInfo> WritableProperties(Type type)
        => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetSetMethod() is not null)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property => !property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
                .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit"));

    /// <summary>类型（含其递归嵌套的私有类型）上声明的全部 <c>*Set</c> 标记名，属性与字段都算。</summary>
    /// <param name="type">要枚举的类型。</param>
    /// <returns>标记名集合。</returns>
    /// <remarks>
    /// 三件事都得做对，否则会得到一堆假缺口：
    /// <list type="number">
    /// <item><b>字段也算</b>：一部分标记写成自动属性（<c>internal bool MethodSet { get; private set; }</c>），
    /// 另一部分直接是字段（<c>internal bool QueueTimeoutSet;</c>）。</item>
    /// <item><b>递归进私有嵌套类型</b>：并发闸门三项的存储与标记放在私有的 <c>ConcurrencyState</c> 里
    /// （懒分配，默认请求不付这几十字节），只看外层类型会把它们全判成缺标记。</item>
    /// <item><b>访问性放开</b>：标记本身是 <c>internal</c> / <c>private</c>，公共反射看不到。</item>
    /// </list>
    /// </remarks>
    private static HashSet<string> SetFlags(Type type)
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<Type>();

        void Collect(Type current)
        {
            if (!seen.Add(current))
            {
                return;
            }

            foreach (var property in current.GetProperties(All))
            {
                if (property.PropertyType == typeof(bool) && property.Name.EndsWith("Set", StringComparison.Ordinal))
                {
                    flags.Add(Normalize(property.Name));
                }
            }

            foreach (var field in current.GetFields(All))
            {
                if (field.FieldType == typeof(bool) && field.Name.EndsWith("Set", StringComparison.Ordinal))
                {
                    flags.Add(Normalize(field.Name));
                }
            }

            foreach (var nested in current.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                Collect(nested);
            }
        }

        Collect(type);
        return flags;
    }

    /// <summary>标记名归一：私有字段常写成 <c>_allowUnsafeRetrySet</c>，去掉前导下划线才与属性名 <c>AllowUnsafeRetry</c> 对得上。</summary>
    /// <param name="name">原始成员名。</param>
    /// <returns>去掉前导下划线后的名字。</returns>
    private static string Normalize(string name) => name.TrimStart('_');

    /// <summary>
    /// 主护栏：公共可写属性集合与 <c>*Set</c> 标记集合必须一一对齐（豁免清单除外）。
    /// </summary>
    [Fact]
    public void EveryWritableProperty_HasACompanionSetFlag()
    {
        var offenders = new List<string>();

        foreach (var type in ConfigSurface())
        {
            // 两级查找：门面委托型属性（如 ConcurrencySettings.QueueTimeout）的标记声明在根配置上，
            // 不查根就全成了「缺标记」。
            var flags = type == typeof(AxiosConfig)
                ? SetFlags(type)
                : new HashSet<string>(SetFlags(type).Concat(SetFlags(typeof(AxiosConfig))), StringComparer.OrdinalIgnoreCase);

            foreach (var property in WritableProperties(type))
            {
                var key = $"{type.Name}.{property.Name}";
                if (WithoutFlag.ContainsKey(key))
                {
                    continue;
                }

                if (!flags.Contains(property.Name + "Set"))
                {
                    offenders.Add(key);
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "以下公共可写属性缺少同名的 *Set 标记（合并配置时会静默丢失调用方写入的值）；"
            + "确实不需要标记的请加进 WithoutFlag 并注明原因：\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 豁免清单不能无限膨胀：每一条都必须在配置面上真实存在，且确实是「整体替换型门面」。
    /// 拼错名字或属性被删掉时这条会红，防止清单变成藏污纳垢的地方。
    /// </summary>
    [Fact]
    public void WithoutFlagList_OnlyNamesRealFacades()
    {
        var surface = ConfigSurface();
        var stale = WithoutFlag.Keys
            .Where(key =>
            {
                var parts = key.Split('.');
                var type = surface.FirstOrDefault(candidate => candidate.Name == parts[0]);
                return type is null || type.GetProperty(parts[1], BindingFlags.Instance | BindingFlags.Public) is null;
            })
            .ToList();

        Assert.True(stale.Count == 0, "豁免清单里有不存在的成员：\n  " + string.Join("\n  ", stale));
    }

    /// <summary>
    /// 反向抽查：<c>Method</c> / <c>Timeout</c> 这类真实参与合并的叶子属性，标记必须真的会翻转 ——
    /// 只靠反射名对齐还不够（标记写了但从不赋值，等于没有）。
    /// </summary>
    [Fact]
    public void WritingALeafProperty_FlipsItsSetFlag()
    {
        var config = new AxiosConfig();

        Assert.False(config.MethodSet, "新建配置不该带 Method 标记。");
        config.Method = HttpMethod.Post;
        Assert.True(config.MethodSet, "写入 Method 之后标记必须翻转。");

        Assert.False(config.TimeoutSet, "新建配置不该带 Timeout 标记。");
        config.Timeout = TimeSpan.FromSeconds(3);
        Assert.True(config.TimeoutSet, "写入 Timeout 之后标记必须翻转。");
    }
}
