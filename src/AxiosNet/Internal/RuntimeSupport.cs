namespace AxiosNet.Internal;

/// <summary>
/// 运行时能力探测：Native AOT 下动态代码（反射取属性、Reflection.Emit、XmlSerializer）不可用。
/// 判定条件一律在调用处直接写 <c>!RuntimeSupport.ForceNoDynamicCode &amp;&amp; RuntimeFeature.IsDynamicCodeSupported</c>：
/// 只有 intrinsic 出现在守卫条件里，ILC 才会把它折成常量 false、把「支持」分支里的反射实现裁掉。
/// 测试可临时把 <see cref="ForceNoDynamicCode"/> 置为 true，在 JIT 上复现 AOT 的失败路径。
/// </summary>
internal static class RuntimeSupport
{
    private static volatile bool _forceNoDynamicCode;

    /// <summary>能力切换的附加失效回调：声明式 API 的元数据缓存在首次使用时挂上来（核心不认识它）。</summary>
    internal static Action? CapabilityChanged;

    /// <summary>测试钩子：模拟 Native AOT（只影响本库的守卫，不改变 BCL 行为）。切换时清空按能力缓存的元数据，避免两种结论混用。</summary>
    internal static bool ForceNoDynamicCode
    {
        get => _forceNoDynamicCode;
        set
        {
            if (_forceNoDynamicCode == value)
            {
                return;
            }

            _forceNoDynamicCode = value;
            ShapeCache.InvalidateAll();
            CapabilityChanged?.Invoke();
        }
    }
}
