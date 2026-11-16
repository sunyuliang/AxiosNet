using System.Reflection;

namespace AxiosNet.Tests.Client;

/// <summary>
/// 配置合并的对账护栏之三：<b>每个分组 <c>MergeFrom</c> 必须逐叶子覆盖</b>。
/// </summary>
/// <remarks>
/// 与另外两条护栏分工：
/// <list type="bullet">
/// <item><see cref="ConfigSetFlagCoverageTests"/> 管「有没有标记」（写了属性却忘加 <c>*Set</c>）。</item>
/// <item>本条管「标记有没有被合并逻辑读到」——加了标记但 <c>MergeFrom</c> 里漏了那个
/// <c>if (incoming.XSet)</c>，症状同样静默：调用方在按请求配置里写了这一项，
/// 实例默认值却原样盖回去，既不报错也没有日志。</item>
/// </list>
/// <para>
/// 判定手法：不构造「合法值」（这些叶子大多有区间校验，造值只会先撞校验），
/// 而是<b>只翻转源对象上的 <c>*Set</c> 标记</b>，合并后断言目标对象上的同名标记为真。
/// 标记跟过来了 ⇔ <c>MergeFrom</c> 里有这个分支。
/// </para>
/// <para>
/// 豁免清单是「本来就不参与逐叶子合并」的成员（对象型容器、回调），逐条写明理由；
/// <see cref="MergeExemptions_OnlyNameRealLeaves"/> 挡住拼错与过期条目。
/// </para>
/// </remarks>
public sealed class ConfigMergeCoverageTests
{
    /// <summary>参与逐叶子合并的分组类型名。</summary>
    private static readonly string[] GroupNames =
    {
        "LimitsSettings",
        "SerializationSettings",
        "TransportSettings",
        "ObservabilitySettings",
        "RetrySettings",
        "CircuitSettings",
        "HedgingSettings",
        "FailoverSettings",
        "IdempotencySettings"
    };

    /// <summary>
    /// 不参与 <c>MergeFrom</c> 逐叶子合并的成员：键为「类型名.属性名」，值为理由。
    /// </summary>
    /// <remarks>往这里加名字等于放宽护栏，每条都必须写清「为什么它不需要标记搬运」。</remarks>
    private static readonly Dictionary<string, string> Exempt = new(StringComparer.Ordinal)
    {
        // 对象型容器：合并层按「非空即整块替换」处理，没有逐叶子标记可搬。
        ["TransportSettings.Proxy"] = "代理对象：整块替换，靠引用为空判断",
        ["TransportSettings.Credentials"] = "凭据对象：整块替换，靠引用为空判断",
        ["TransportSettings.Pool"] = "连接池对象：非空即整块替换",
        ["SerializationSettings.JsonOptions"] = "序列化器选项对象：非空即整块替换",
        ["SerializationSettings.BodySerializers"] = "序列化器表：整体替换（表内按内容协商选择）",
        ["TransportSettings.Auth"] = "凭据四选一：标记由 AxiosCredential 自身承载，合并走 TryCopyFrom",
        ["TransportSettings.HttpVersion"] = "由 MergeConfig 显式按 *Set 合并（AxiosClient.Config.cs:198/203）；端到端行为见 Connectivity/HttpVersionTests，MergeFrom 只补其余项",
        ["TransportSettings.VersionPolicy"] = "由 MergeConfig 显式按 *Set 合并（AxiosClient.Config.cs:198/203）；端到端行为见 Connectivity/HttpVersionTests，MergeFrom 只补其余项",
        ["FailoverSettings.Endpoints"] = "端点表：不可变快照，与 EndpointsSet 一起搬",
        ["SerializationSettings.ValueText"] = "实例级项：按请求写它会抛 ERR_BAD_OPTION_VALUE，合并层不搬运（见 README「值格式化」）",
        ["SerializationSettings.TransformRequest"] = "转换链：按 TransformRequestCount / TransformInheritedRequest 计长度，不用标记",
        ["SerializationSettings.TransformResponse"] = "转换链：按 TransformResponseCount / TransformInheritedResponse 计长度，不用标记",
        ["RetrySettings.Policy"] = "策略对象：非空即整块替换",
        ["CircuitSettings.Policy"] = "策略对象：非空即整块替换",

        // 回调 / 委托：非空即整块替换，不存在「部分写入」。
        ["RetrySettings.OnRetry"] = "事件回调：非空即整块替换",
        ["CircuitSettings.OnRejected"] = "事件回调：非空即整块替换",
        ["IdempotencySettings.KeyFactory"] = "委托：非空即整块替换",
        ["SerializationSettings.QueryEncoder"] = "委托：非空即整块替换",
        ["SerializationSettings.FormFieldName"] = "委托：非空即整块替换",
        ["ObservabilitySettings.OnUploadProgress"] = "回调：非空即整块替换",
        ["ObservabilitySettings.OnDownloadProgress"] = "回调：非空即整块替换",
        ["ObservabilitySettings.OnRawResponse"] = "回调：非空即整块替换",
        ["ObservabilitySettings.OnSendingRequest"] = "回调：非空即整块替换",
        ["ObservabilitySettings.TimeoutMessage"] = "回调：非空即整块替换"
    };

    /// <summary>公共可写实例属性（排除索引器与 <c>init</c>）。</summary>
    /// <param name="type">要枚举的类型。</param>
    /// <returns>属性列表。</returns>
    private static IEnumerable<PropertyInfo> WritableProperties(Type type)
        => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetSetMethod() is not null)
            .Where(property => property.GetIndexParameters().Length == 0)
            .Where(property => !property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers()
                .Any(modifier => modifier.FullName == "System.Runtime.CompilerServices.IsExternalInit"));

    /// <summary>
    /// 按组名取门面实例。<paramref name="config"/> 是承载门面的配置对象。
    /// </summary>
    /// <param name="config">承载门面的配置。</param>
    /// <param name="group">组名。</param>
    /// <returns>该组的分组对象。</returns>
    private static object GroupOf(AxiosConfig config, string group) => group switch
    {
        "LimitsSettings" => config.Limits,
        "SerializationSettings" => config.Serialization,
        "TransportSettings" => config.Transport,
        "ObservabilitySettings" => config.Observability,
        "RetrySettings" => config.Resilience.Retry,
        "CircuitSettings" => config.Resilience.Circuit,
        "HedgingSettings" => config.Resilience.Hedging,
        "FailoverSettings" => config.Resilience.Failover,
        "IdempotencySettings" => config.Resilience.Idempotency,
        "ConcurrencySettings" => config.Resilience.Concurrency,
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "未知配置分组。")
    };

    /// <summary>按组名生成「源 → 目标」的合并动作。</summary>
    /// <param name="source">源配置。</param>
    /// <param name="target">目标配置。</param>
    /// <param name="group">组名。</param>
    /// <returns>执行一次合并的委托。</returns>
    private static Action MergeOf(AxiosConfig source, AxiosConfig target, string group) => group switch
    {
        "LimitsSettings" => () => target.Limits.MergeFrom(source.Limits),
        "SerializationSettings" => () => target.Serialization.MergeFrom(source.Serialization),
        "TransportSettings" => () => target.Transport.MergeFrom(source.Transport),
        "ObservabilitySettings" => () => target.Observability.MergeFrom(source.Observability),
        "RetrySettings" => () => target.Resilience.Retry.MergeFrom(source.Resilience.Retry),
        "CircuitSettings" => () => target.Resilience.Circuit.MergeFrom(source.Resilience.Circuit),
        "HedgingSettings" => () => target.Resilience.Hedging.MergeFrom(source.Resilience.Hedging),
        "FailoverSettings" => () => target.Resilience.Failover.MergeFrom(source.Resilience.Failover),
        "IdempotencySettings" => () => target.Resilience.Idempotency.MergeFrom(source.Resilience.Idempotency),
        // 并发三项的标记挂在根配置的私有状态上，由 Resilience 门面统一转交。
        "ConcurrencySettings" => () => target.Resilience.MergeFrom(source.Resilience),
        _ => throw new ArgumentOutOfRangeException(nameof(group), group, "未知配置分组。")
    };

    /// <summary>
    /// 标记的持有者候选：门面自己、门面持有的配置、以及配置上那些「懒分配的状态对象」
    /// （并发三项的标记就写在根配置的私有 <c>ConcurrencyState</c> 里）。
    /// </summary>
    /// <param name="instance">起点对象（通常是分组的门面）。</param>
    /// <returns>按优先级排列的候选持有者。</returns>
    private static List<object> Holders(object instance)
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var holders = new List<object> { instance };

        // 自己身上的懒分配状态对象（ValueText / Transform 链等把标记写在私有嵌套类里）。
        foreach (var stateField in instance.GetType().GetFields(All))
        {
            if (stateField.FieldType.IsClass
                && stateField.FieldType.IsNested
                && stateField.GetValue(instance) is { } state)
            {
                holders.Add(state);
            }
        }

        foreach (var field in instance.GetType().GetFields(All))
        {
            if (!typeof(AxiosConfig).IsAssignableFrom(field.FieldType))
            {
                continue;
            }

            if (field.GetValue(instance) is not AxiosConfig owner)
            {
                continue;
            }

            holders.Add(owner);

            // 根配置上的懒分配状态对象：类型是配置的私有嵌套类，标记字段写在它身上。
            foreach (var stateField in owner.GetType().GetFields(All))
            {
                if (stateField.FieldType.IsClass
                    && stateField.FieldType.IsNested
                    && stateField.GetValue(owner) is { } state)
                {
                    holders.Add(state);
                }
            }
        }

        return holders;
    }

    /// <summary>
    /// 在持有者候选里找 <c>name + "Set"</c> 标记，并返回「成员 + 真正持有它的对象」。
    /// </summary>
    /// <param name="instance">起点对象。</param>
    /// <param name="name">叶子属性名。</param>
    /// <returns>标记成员与持有者；找不到返回 <c>null</c>。</returns>
    /// <remarks>
    /// 三种形态都要认（与 <see cref="ConfigSetFlagCoverageTests"/> 的踩坑记录同源）：
    /// 自动属性、普通字段、私有嵌套状态类里的字段；私有字段还常带 <c>_</c> 前缀且大小写不同。
    /// </remarks>
    private static (MemberInfo Flag, object Holder)? FindFlag(object instance, string name)
    {
        const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        var wanted = name + "Set";

        foreach (var holder in Holders(instance))
        {
            foreach (var property in holder.GetType().GetProperties(All))
            {
                if (property.PropertyType == typeof(bool)
                    && string.Equals(property.Name.TrimStart('_'), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return (property, holder);
                }
            }

            foreach (var field in holder.GetType().GetFields(All))
            {
                if (field.FieldType == typeof(bool)
                    && string.Equals(field.Name.TrimStart('_'), wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return (field, holder);
                }
            }
        }

        return null;
    }

    /// <summary>读标记当前值。</summary>
    /// <param name="instance">起点对象。</param>
    /// <param name="name">叶子属性名。</param>
    /// <returns>标记值；找不到时返回 <c>false</c>。</returns>
    private static bool ReadFlag(object instance, string name)
    {
        if (FindFlag(instance, name) is not { } found)
        {
            return false;
        }

        return found.Flag switch
        {
            PropertyInfo property => (bool)property.GetValue(found.Holder)!,
            FieldInfo field => (bool)field.GetValue(found.Holder)!,
            _ => false
        };
    }

    /// <summary>把标记置为 <c>true</c>（模拟「调用方写过这个叶子」）。</summary>
    /// <param name="instance">起点对象。</param>
    /// <param name="name">叶子属性名。</param>
    /// <returns>找到并置位返回 <c>true</c>。</returns>
    private static bool SetFlag(object instance, string name)
    {
        if (FindFlag(instance, name) is not { } found)
        {
            return false;
        }

        switch (found.Flag)
        {
            case PropertyInfo property when property.GetSetMethod(nonPublic: true) is not null:
                property.SetValue(found.Holder, true);
                return true;

            case FieldInfo field:
                field.SetValue(found.Holder, true);
                return true;

            case PropertyInfo property:
                // 只有 getter 的标记：先找编译器生成的 backing field，再退回「同名的私有字段」。
                // `internal bool TextEncodingSet => _textEncodingSet;` 这类表达式属性两样都不满足，
                // 必须靠第二步才翻得动。
                var declaring = property.DeclaringType!;
                var backing = declaring.GetField(
                    "<" + property.Name + ">k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                if (backing is not null)
                {
                    backing.SetValue(found.Holder, true);
                    return true;
                }

                foreach (var field in declaring.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (field.FieldType == typeof(bool)
                        && string.Equals(field.Name.TrimStart('_'), property.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        field.SetValue(found.Holder, true);
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }

    /// <summary>
    /// 主护栏：分组里每个叶子属性的 <c>*Set</c> 标记，都必须能被 <c>MergeFrom</c> 搬到目标对象上。
    /// </summary>
    [Fact]
    public void EveryLeafFlag_IsCarriedOverByMergeFrom()
    {
        var offenders = new List<string>();

        foreach (var group in GroupNames)
        {
            var sourceType = GroupOf(new AxiosConfig(), group).GetType();

            foreach (var property in WritableProperties(sourceType))
            {
                var key = $"{sourceType.Name}.{property.Name}";
                if (Exempt.ContainsKey(key))
                {
                    continue;
                }

                // 子配置门面（Retry / Circuit …）本身不是叶子：它们的叶子已经单独列在 GroupNames 里。
                if (GroupNames.Contains(property.PropertyType.Name))
                {
                    continue;
                }

                // 每个叶子一对全新配置：源上只翻这一个标记，避免上一轮残留把结论变成恒真。
                var source = new AxiosConfig();
                var target = new AxiosConfig();
                var sourceGroup = GroupOf(source, group);
                var targetGroup = GroupOf(target, group);

                if (!SetFlag(sourceGroup, property.Name))
                {
                    offenders.Add(key + "（找不到对应的 *Set 标记）");
                    continue;
                }

                MergeOf(source, target, group)();

                if (!ReadFlag(targetGroup, property.Name))
                {
                    offenders.Add(key + "（标记没被 MergeFrom 搬过去）");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "以下配置叶子在 MergeFrom 里漏了分支或没有标记 —— 调用方在按请求配置里写这一项时会被实例默认值静默盖掉；"
            + "确实不参与逐叶子合并的请加进 Exempt 并注明原因：\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// 并发三项单独按「值」验证：它们的标记写在根配置的私有懒分配状态里，
    /// 新建配置时那个状态对象还不存在（正是这三项「默认请求不付这几十字节」的实现方式），
    /// 因此没法像其他叶子那样只翻标记。这里用合法值走一遍真实合并。
    /// </summary>
    [Fact]
    public void ConcurrencyLeaves_AreCarriedOverByMergeFrom()
    {
        var source = new AxiosConfig();
        source.Resilience.Concurrency.MaxInFlight = 4;
        source.Resilience.Concurrency.MaxQueue = 8;
        source.Resilience.Concurrency.QueueTimeout = TimeSpan.FromSeconds(2);

        var target = new AxiosConfig();
        Assert.Null(target.Resilience.Concurrency.MaxInFlight);

        target.Resilience.MergeFrom(source.Resilience);

        Assert.Equal(4, target.Resilience.Concurrency.MaxInFlight);
        Assert.Equal(8, target.Resilience.Concurrency.MaxQueue);
        Assert.Equal(TimeSpan.FromSeconds(2), target.Resilience.Concurrency.QueueTimeout);
    }

    /// <summary>豁免清单不能藏污纳垢：每条都必须在分组上真实存在。</summary>
    [Fact]
    public void MergeExemptions_OnlyNameRealLeaves()
    {
        var leaves = new HashSet<string>(StringComparer.Ordinal);

        foreach (var group in GroupNames)
        {
            var type = GroupOf(new AxiosConfig(), group).GetType();
            foreach (var property in WritableProperties(type))
            {
                leaves.Add($"{type.Name}.{property.Name}");
            }
        }

        var stale = Exempt.Keys.Where(key => !leaves.Contains(key)).ToList();

        Assert.True(stale.Count == 0, "豁免清单里有不存在的成员：\n  " + string.Join("\n  ", stale));
    }
}
