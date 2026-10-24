using System.Text.Json;

namespace AxiosNet.Internal;

/// <summary>
/// 把响应 JSON 展开为 .NET 动态对象图：对象 → <see cref="AxiosDynamic"/>，数组 → <c>List&lt;object?&gt;</c>，
/// 字符串 / 布尔保持原类型，整数 → long，浮点 → double，null → null。
/// 行为对齐 JS/axios：<c>data.name</c>、<c>data["name"]</c>、<c>data.items[0].id</c> 可直接访问，
/// 缺省成员返回 null（不抛），且对象节点仍可当 <see cref="IDictionary{TKey,TValue}"/> 用。
/// </summary>
internal static class DynamicJson
{
    public static object? Parse(string text)
    {
        using var document = JsonDocument.Parse(text);
        return FromElement(document.RootElement);
    }

    private static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var dynamicObject = new AxiosDynamic();
                foreach (var property in element.EnumerateObject())
                {
                    dynamicObject[property.Name] = FromElement(property.Value);
                }

                return dynamicObject;
            }

            case JsonValueKind.Array:
            {
                var list = new List<object?>(element.GetArrayLength());
                foreach (var item in element.EnumerateArray())
                {
                    list.Add(FromElement(item));
                }

                return list;
            }

            case JsonValueKind.String:
                return element.GetString();

            case JsonValueKind.True:
                return BoxedBoolean.True;

            case JsonValueKind.False:
                return BoxedBoolean.False;

            case JsonValueKind.Number:
                // 整数保持 long，其余退化为 double（与 JS number 语义接近）。
                // 注意不能用三元表达式：long/double 条件表达式会统一提升为 double。
                if (element.TryGetInt64(out var integer))
                {
                    return integer;
                }

                return element.GetDouble();

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
            default:
                return null;
        }
    }

    private static class BoxedBoolean
    {
        public static readonly object True = true;
        public static readonly object False = false;
    }
}
