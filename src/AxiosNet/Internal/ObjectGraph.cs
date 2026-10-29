namespace AxiosNet.Internal;

/// <summary>
/// 对象图展平（表单 / multipart / params）的路径式环检测：只有节点重复出现在自己的祖先链里才算环。
/// 进入时登记、退出时注销，共享子对象照常展开。
/// </summary>
internal static class ObjectGraph
{
    /// <summary>
    /// 进入容器节点：登记到当前路径，命中祖先（环）抛 <see cref="DataCycleException"/>。
    /// 返回值是路径集合（首次调用时创建）；离开时必须调用 <see cref="Exit"/>。
    /// </summary>
    public static HashSet<object>? Enter(HashSet<object>? path, object value)
    {
        // 值类型没有引用身份，不可能构成环。
        if (value.GetType().IsValueType)
        {
            return path;
        }

        path ??= new HashSet<object>(ReferenceComparer.Instance);
        if (!path.Add(value))
        {
            throw new DataCycleException(
                "数据存在循环引用（对象图中出现环）：表单 / 查询串需要有限深度的树形结构。"
                + "请打断环（例如忽略反向导航属性）或改用 JSON 编码。");
        }

        return path;
    }

    /// <summary>离开容器节点：从路径注销。与 <see cref="Enter"/> 成对，必须放在 finally 里。</summary>
    public static void Exit(HashSet<object>? path, object value)
    {
        if (path is not null && !value.GetType().IsValueType)
        {
            path.Remove(value);
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceComparer Instance = new();

        bool IEqualityComparer<object>.Equals(object? x, object? y) => ReferenceEquals(x, y);

        int IEqualityComparer<object>.GetHashCode(object obj)
            => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>
/// 展平时发现对象图有环，由 <see cref="FormBodyEncoder"/> / <see cref="MultipartBodyBuilder"/> /
/// <see cref="UrlBuilder"/> 转成 <see cref="AxiosException"/>（<c>ERR_BAD_OPTION_VALUE</c>）。
/// </summary>
internal sealed class DataCycleException : Exception
{
    public DataCycleException(string message)
        : base(message)
    {
    }
}
