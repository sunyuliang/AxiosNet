using AxiosNet.Metadata;

namespace AxiosNet.Internal.Content;

/// <summary>
/// 聚合正文的键名规则：合并到同一层后键名必须唯一，JSON 与表单两条通道共用这一处判定与文案。
/// </summary>
internal static class AggregateKeys
{
    /// <summary>登记一个已出现在当前层的键名；重复即报错（<c>ERR_BAD_OPTION_VALUE</c>）。</summary>
    /// <param name="seen">本次装配已产出的键名集合；<c>null</c> 表示本次装配不可能重复，跳过判定。</param>
    /// <param name="name">待登记的键名（必须是线上形态：JSON 已套命名策略、表单已百分号编码）。</param>
    /// <param name="config">本次请求的生效配置。</param>
    public static void EnsureUnique(HashSet<string>? seen, string name, AxiosConfig config)
    {
        if (seen is not null && !seen.Add(name))
        {
            throw AxiosException.InvalidOption(
                $"请求体合并后出现重复键 \"{name}\"：请改名，或把 [RootBody] 换成 [Body] 包成独立键。", config);
        }
    }
}
