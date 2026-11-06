using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// JSON 树展平的接收方：遍历只写一份，表单与 multipart 各自决定数组命名与叶子落点。
/// 用结构体实现并按 <c>ref</c> 传递，避免引入闭包 / 装箱，也不比原来的两份递归多分配。
/// </summary>
internal interface IJsonFlattenSink
{
    /// <summary>数组元素的名字：表单编码用 <c>a[]</c>（根数组 <c>[i]</c>），multipart 沿用父名。</summary>
    string? ArrayItemName(string? prefix, int index);

    /// <summary>标量叶子；<paramref name="name"/> 可能为 <c>null</c>（根数组），是否跳过 / 报错由接收方决定。</summary>
    void Leaf(string? name, JsonElement value);
}

/// <summary>把 <see cref="JsonElement"/> 展平成键值序列：对象按 <c>a[b]</c> 下钻、数组交给接收方命名、null 跳过。</summary>
internal static class JsonFlattener
{
    public static void Flatten<TSink>(
        string? prefix,
        JsonElement element,
        Func<string?, string, string>? nameSelector,
        ref TSink sink)
        where TSink : struct, IJsonFlattenSink
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Flatten(
                        BodyFields.CombineName(nameSelector, prefix, property.Name),
                        property.Value,
                        nameSelector,
                        ref sink);
                }

                break;

            case JsonValueKind.Array:
            {
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Flatten(sink.ArrayItemName(prefix, index), item, nameSelector, ref sink);
                    index++;
                }

                break;
            }

            case JsonValueKind.String:
            case JsonValueKind.Number:
            case JsonValueKind.True:
            case JsonValueKind.False:
                sink.Leaf(prefix, element);
                break;

            default:
                // null / undefined：跳过。
                break;
        }
    }
}
