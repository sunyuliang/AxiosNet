using System.Collections;
using System.Text.Json;
using AxiosNet.Internal;

namespace AxiosNet.WebAPI.Internal;

/// <summary>multipart 声明装配的共用取值逻辑：类模型与接口参数走同一套规则。</summary>
internal static class MultipartValues
{
    /// <summary>声明了覆盖项的集合在绑定期就枚举并物化成 <see cref="List{T}"/>：部件清单必须在装配开始前固定下来，延迟枚举会被重试二次枚举。单个文件部件、调用方自带 <see cref="HttpContent"/>、要按 <c>a[b]</c> 展平的字典都原样透传。</summary>
    public static object MaterializeDeclaredCollection(object value)
    {
        if (value is string or byte[] or Stream or FormFile or HttpContent or JsonElement
            or Memory<byte> or ReadOnlyMemory<byte> or ArraySegment<byte>
            or IDictionary or IReadOnlyDictionary<string, object?>
            || value is not IEnumerable items)
        {
            return value;
        }

        var materialized = new List<object?>();
        try
        {
            foreach (var item in items)
            {
                materialized.Add(item);
            }
        }
        catch
        {
            // 迭代器自己抛错：已经产出的项还没交给 builder，容器回滚覆盖不到，就地释放后再上抛
            MultipartOwnership.DisposeOwned(materialized);
            throw;
        }

        return materialized;
    }
}
