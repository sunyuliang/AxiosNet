using System.Runtime.CompilerServices;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>
/// 运行时能力探测：Native AOT 下动态代码（反射取属性、Reflection.Emit、XmlSerializer）不可用。
/// 测试可临时把 <see cref="ForceNoDynamicCode"/> 置为 true，在 JIT 上复现 AOT 的失败路径。
/// </summary>
internal static class RuntimeSupport
{
    private static volatile bool _forceNoDynamicCode;

    /// <summary>当前运行时是否支持动态代码；Native AOT 下为 false。</summary>
    internal static bool IsDynamicCodeSupported => !_forceNoDynamicCode && RuntimeFeature.IsDynamicCodeSupported;

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
            RequestModelMetadata.InvalidateAll();
            ShapeCache.InvalidateAll();
        }
    }
}
