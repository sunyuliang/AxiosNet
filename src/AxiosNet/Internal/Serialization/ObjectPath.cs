using System.Buffers;

namespace AxiosNet.Internal.Serialization;

/// <summary>
/// 对象图展平（表单 / multipart / params）的路径式环检测：只有节点重复出现在自己的祖先链里才算环。
/// 祖先链是租自 <see cref="ArrayPool{T}"/> 的数组（稳态不分配），进入时压栈、退出时弹栈，共享子对象照常展开。
/// </summary>
internal ref struct ObjectPath
{
    /// <summary>祖先链数组；首次 <see cref="Enter"/> 时才租。</summary>
    private object?[]? _chain;

    /// <summary>已压栈的节点数。</summary>
    private int _count;

    /// <summary>压栈：命中祖先（环）抛 <see cref="DataCycleException"/>；与 <see cref="Exit"/> 成对。</summary>
    public void Enter(object value)
    {
        // 值类型没有引用身份，不可能构成环，也不占祖先链。
        if (value.GetType().IsValueType)
        {
            return;
        }

        var chain = _chain;
        if (chain is null)
        {
            chain = _chain = ArrayPool<object?>.Shared.Rent(8);
        }
        else if (_count == chain.Length)
        {
            var bigger = ArrayPool<object?>.Shared.Rent(chain.Length * 2);
            Array.Copy(chain, bigger, _count);
            ArrayPool<object?>.Shared.Return(chain, clearArray: true);
            chain = _chain = bigger;
        }

        // 祖先链通常只有几层：线性扫描比哈希更省。
        for (var i = 0; i < _count; i++)
        {
            if (ReferenceEquals(chain[i], value))
            {
                throw new DataCycleException(
                    "数据存在循环引用（对象图中出现环）：表单 / 查询串需要有限深度的树形结构。"
                    + "请打断环（例如忽略反向导航属性）或改用 JSON 编码。");
            }
        }

        chain[_count++] = value;
    }

    /// <summary>弹栈：与 <see cref="Enter"/> 成对，必须放在 finally 里（值类型的压栈是空操作，弹出同样跳过）。</summary>
    public void Exit(object value)
    {
        if (value.GetType().IsValueType)
        {
            return;
        }

        _count--;
        _chain![_count] = null;
    }

    /// <summary>归还租下的数组并清空引用，避免池里长期持有业务对象。</summary>
    public void Dispose()
    {
        var chain = _chain;
        if (chain is not null)
        {
            _chain = null;
            _count = 0;
            ArrayPool<object?>.Shared.Return(chain, clearArray: true);
        }
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
