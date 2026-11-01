using System.Collections;
using System.Globalization;

namespace AxiosNet.Cli.Spec;

/// <summary>
/// 与格式无关的文档节点：JSON 与 YAML 统一成同一棵树，后续归一化、读取与生成只依赖它。
/// 标量按字符串 / 数字 / 布尔 / null 保存，数字统一为 <see cref="double"/>（文档里的数值只用于元信息与枚举字面量）。
/// </summary>
internal sealed class SpecNode
{
    /// <summary>节点种类，决定 <see cref="_value"/> 的实际类型。</summary>
    private enum Kind
    {
        /// <summary>JSON / YAML 的 null。</summary>
        Null,

        /// <summary>对象（字符串键 → 子节点）。</summary>
        Object,

        /// <summary>数组。</summary>
        Array,

        /// <summary>字符串标量。</summary>
        String,

        /// <summary>数字标量（统一为 double）。</summary>
        Number,

        /// <summary>布尔标量。</summary>
        Boolean
    }

    /// <summary>节点种类标签。</summary>
    private readonly Kind _kind;

    /// <summary>节点负载：字典 / 列表 / string / double / bool，Null 时为 null。</summary>
    private readonly object? _value;

    /// <summary>私有构造：节点一经创建即不可变，外部只能通过工厂方法创建。</summary>
    private SpecNode(Kind kind, object? value)
    {
        _kind = kind;
        _value = value;
    }

    /// <summary>共享的 null 节点（无状态，可安全复用）。</summary>
    public static SpecNode Null { get; } = new(Kind.Null, null);

    /// <summary>创建对象节点。</summary>
    public static SpecNode Obj(IReadOnlyDictionary<string, SpecNode> value) => new(Kind.Object, value);

    /// <summary>创建数组节点。</summary>
    public static SpecNode Arr(IReadOnlyList<SpecNode> value) => new(Kind.Array, value);

    /// <summary>创建字符串节点。</summary>
    public static SpecNode Str(string value) => new(Kind.String, value);

    /// <summary>创建数字节点。</summary>
    public static SpecNode Num(double value) => new(Kind.Number, value);

    /// <summary>创建布尔节点。</summary>
    public static SpecNode Bool(bool value) => new(Kind.Boolean, value);

    /// <summary>是否对象。</summary>
    public bool IsObject => _kind == Kind.Object;

    /// <summary>是否数组。</summary>
    public bool IsArray => _kind == Kind.Array;

    /// <summary>是否字符串。</summary>
    public bool IsString => _kind == Kind.String;

    /// <summary>是否数字。</summary>
    public bool IsNumber => _kind == Kind.Number;

    /// <summary>是否布尔。</summary>
    public bool IsBoolean => _kind == Kind.Boolean;

    /// <summary>对象子节点；非对象返回空字典（调用方无需先判类型）。</summary>
    public IReadOnlyDictionary<string, SpecNode> Object
        => _kind == Kind.Object ? (IReadOnlyDictionary<string, SpecNode>)_value! : EmptyObject;

    /// <summary>数组元素；非数组返回空列表。</summary>
    public IReadOnlyList<SpecNode> Items
        => _kind == Kind.Array ? (IReadOnlyList<SpecNode>)_value! : Array.Empty<SpecNode>();

    /// <summary>对象的键值对序列；非对象返回空序列。</summary>
    public IEnumerable<KeyValuePair<string, SpecNode>> Properties
        => _kind == Kind.Object ? ((IReadOnlyDictionary<string, SpecNode>)_value!) : Enumerable.Empty<KeyValuePair<string, SpecNode>>();

    /// <summary>共享的空对象，避免非对象节点反复分配。</summary>
    private static readonly IReadOnlyDictionary<string, SpecNode> EmptyObject =
        new Dictionary<string, SpecNode>(StringComparer.Ordinal);

    /// <summary>按键取子节点；非对象或键不存在时返回 null。</summary>
    public SpecNode? this[string key]
        => _kind == Kind.Object && ((IReadOnlyDictionary<string, SpecNode>)_value!).TryGetValue(key, out var node) ? node : null;

    /// <summary>按字符串读取标量：字符串原样返回，数字按往返格式、布尔转小写；对象 / 数组 / null 返回 null。</summary>
    public string? AsString()
        => _kind switch
        {
            Kind.String => (string)_value!,
            Kind.Number => ((double)_value!).ToString("R", CultureInfo.InvariantCulture),
            Kind.Boolean => (bool)_value! ? "true" : "false",
            _ => null
        };

    /// <summary>按数字读取：数字直接返回，字符串尝试不变文化解析；其余返回 null。</summary>
    public double? AsNumber()
    {
        switch (_kind)
        {
            case Kind.Number:
                return (double)_value!;
            case Kind.String:
                return double.TryParse((string)_value!, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? parsed
                    : null;
            default:
                return null;
        }
    }

    /// <summary>按 int 读取：超出 int 范围或非数字时返回 null（避免静默截断）。</summary>
    public int? AsInt()
    {
        var number = AsNumber();
        if (number is null || number < int.MinValue || number > int.MaxValue)
        {
            return null;
        }

        return (int)number.Value;
    }

    /// <summary>按布尔读取：布尔直接返回，字符串只接受 true / false（忽略大小写），其余返回 null。</summary>
    public bool? AsBool()
    {
        switch (_kind)
        {
            case Kind.Boolean:
                return (bool)_value!;
            case Kind.String:
                var text = (string)_value!;
                if (text.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (text.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return null;
            default:
                return null;
        }
    }

    /// <summary>取子节点的字符串值（键不存在时返回 null）。</summary>
    public string? GetString(string key) => this[key]?.AsString();

    /// <summary>取子节点的布尔值（键不存在或类型不符时返回 null）。</summary>
    public bool? GetBool(string key) => this[key]?.AsBool();

    /// <summary>取子节点的 int 值（键不存在或越界时返回 null）。</summary>
    public int? GetInt(string key) => this[key]?.AsInt();

    /// <summary>把 YAML 反序列化得到的 CLR 对象图（字典 / 列表 / 标量）转成节点。</summary>
    public static SpecNode FromPlain(object? value)
    {
        switch (value)
        {
            case null:
                return Null;
            case SpecNode node:
                return node;
            case string text:
                return Str(text);
            case bool flag:
                return Bool(flag);
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal:
                return Num(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case DateTime dateTime:
                return Str(dateTime.ToString("o", CultureInfo.InvariantCulture));
            case DateTimeOffset dateTimeOffset:
                return Str(dateTimeOffset.ToString("o", CultureInfo.InvariantCulture));
            case IDictionary dictionary:
                // YAML 的键可能是数字或布尔，统一转成字符串键，保持与 JSON 一致。
                var map = new Dictionary<string, SpecNode>(StringComparer.Ordinal);
                foreach (DictionaryEntry entry in dictionary)
                {
                    var key = Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty;
                    map[key] = FromPlain(entry.Value);
                }

                return Obj(map);
            case IEnumerable sequence:
                var list = new List<SpecNode>();
                foreach (var item in sequence)
                {
                    list.Add(FromPlain(item));
                }

                return Arr(list);
            default:
                return Str(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
        }
    }
}
