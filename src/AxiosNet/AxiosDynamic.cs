using System.Collections;
using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;

namespace AxiosNet;

/// <summary>
/// dynamic 响应（<see cref="ResponseType.Dynamic"/>）的对象节点：支持 <c>data.name</c> / <c>data["name"]</c>，
/// 缺省成员返回 <c>null</c>，运行期可增删属性。
/// 同时实现 <see cref="IDictionary{TKey,TValue}"/>，可原样当 <c>Params</c> / 请求体发出，也会被 <c>System.Text.Json</c>
/// 序列化成普通 JSON 对象。
/// </summary>
/// <remarks>
/// 值映射：字符串/布尔原样，整数为 <see cref="long"/>，浮点为 <see cref="double"/>，JSON <c>null</c> 保持 <c>null</c>，
/// 数组节点是 <c>List&lt;object?&gt;</c>。
/// 读无锁、写串行；枚举取键快照、值实时读取，枚举期间被删除的键值为 <c>null</c>。
/// 动态成员优先于 CLR 成员：<c>data.Count</c> 读的是 JSON 里名为 <c>Count</c> 的字段。
/// </remarks>
public sealed class AxiosDynamic : IDynamicMetaObjectProvider, IDictionary<string, object?>
{
    private static readonly FieldInfo s_shapeField = typeof(AxiosDynamic).GetField(nameof(_shape), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo s_valuesField = typeof(AxiosDynamic).GetField(nameof(_values), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo s_getValue = typeof(AxiosDynamic).GetMethod(nameof(GetValue), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo s_setValue = typeof(AxiosDynamic).GetMethod(nameof(SetValue), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>写锁：写入串行化，读不加锁。</summary>
    private readonly object _gate = new();

    /// <summary>当前形状（不可变，同形状实例共享同一实例）。</summary>
    private Shape _shape = Shape.Empty;

    /// <summary>按槽位存值；扩容走整数组替换，永不原地改长度。</summary>
    private object?[] _values = Array.Empty<object?>();

    /// <summary>创建空对象。</summary>
    public AxiosDynamic()
    {
    }

    /// <summary>按源字典浅拷贝创建（键序决定枚举顺序）。</summary>
    public AxiosDynamic(IDictionary<string, object?> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var pair in source)
        {
            this[pair.Key] = pair.Value;
        }
    }

    /// <summary>索引访问：键不存在返回 <c>null</c>；赋值对已存在的键改值、对不存在的键新增槽位。</summary>
    public object? this[string key]
    {
        get => GetValue(key);
        set => SetValue(key, value);
    }

    /// <summary>属性个数（按 JSON 字段计，不含 CLR 成员）。</summary>
    public int Count => Volatile.Read(ref _shape).Count;

    /// <summary>键集合（按插入顺序的快照）。</summary>
    public ICollection<string> Keys
    {
        get
        {
            var shape = Volatile.Read(ref _shape);
            var keys = new string[shape.Count];
            shape.Slots.Keys.CopyTo(keys, 0);
            return keys;
        }
    }

    /// <summary>值集合（按插入顺序的快照）。</summary>
    public ICollection<object?> Values
    {
        get
        {
            var shape = Volatile.Read(ref _shape);
            var values = Volatile.Read(ref _values);
            var snapshot = new object?[shape.Count];
            var index = 0;
            foreach (var slot in shape.Slots.Values)
            {
                snapshot[index++] = slot < values.Length ? values[slot] : null;
            }

            return snapshot;
        }
    }

    /// <summary>恒为 false（本类型支持增删）。</summary>
    public bool IsReadOnly => false;

    /// <summary>新增键；键已存在抛 <see cref="ArgumentException"/>（与 <see cref="Dictionary{TKey,TValue}"/> 一致）。</summary>
    public void Add(string key, object? value)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_gate)
        {
            if (Volatile.Read(ref _shape).Slots.ContainsKey(key))
            {
                throw new ArgumentException($"键“{key}”已存在。", nameof(key));
            }

            SetValue(key, value);
        }
    }

    /// <summary>新增键值对。</summary>
    public void Add(KeyValuePair<string, object?> item) => Add(item.Key, item.Value);

    /// <summary>清空全部属性。</summary>
    public void Clear()
    {
        lock (_gate)
        {
            // 先换空 shape，再回收值数组。
            Volatile.Write(ref _shape, Shape.Empty);
            Volatile.Write(ref _values, Array.Empty<object?>());
        }
    }

    /// <summary>判断键值对是否完全一致（键存在且值相等）。</summary>
    public bool Contains(KeyValuePair<string, object?> item)
        => TryGetValue(item.Key, out var value) && EqualityComparer<object?>.Default.Equals(value, item.Value);

    /// <summary>键是否存在。</summary>
    public bool ContainsKey(string key) => Volatile.Read(ref _shape).Slots.ContainsKey(key);

    /// <summary>按枚举顺序拷贝键值对。</summary>
    public void CopyTo(KeyValuePair<string, object?>[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        foreach (var pair in this)
        {
            array[arrayIndex++] = pair;
        }
    }

    /// <summary>删除键；返回是否真的删掉了。槽位不复用，被删槽位永久作废。</summary>
    public bool Remove(string key)
    {
        lock (_gate)
        {
            var shape = Volatile.Read(ref _shape);
            if (!shape.Slots.TryGetValue(key, out var slot))
            {
                return false;
            }

            var values = Volatile.Read(ref _values);

            // 先撤销可见性，再断开值引用。
            Volatile.Write(ref _shape, shape.WithRemoved(key));

            if (slot < values.Length)
            {
                // 断开引用；已捕获旧 shape 的枚举器读到 null。
                Volatile.Write(ref values[slot], null);
            }

            return true;
        }
    }

    /// <summary>删除键值对。</summary>
    public bool Remove(KeyValuePair<string, object?> item) => Contains(item) && Remove(item.Key);

    /// <summary>取值；键不存在返回 false（不区分「不存在」与「值为 null」请用 <see cref="ContainsKey"/>）。</summary>
    public bool TryGetValue(string key, out object? value)
    {
        var shape = Volatile.Read(ref _shape);
        if (shape.Slots.TryGetValue(key, out var slot))
        {
            var values = Volatile.Read(ref _values);
            value = slot < values.Length ? values[slot] : null;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>按插入顺序枚举键值对：键集合是快照，值不是（枚举期间被删除的键值为 <c>null</c>）。</summary>
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        var shape = Volatile.Read(ref _shape);
        var values = Volatile.Read(ref _values);
        foreach (var pair in shape.Slots)
        {
            yield return new KeyValuePair<string, object?>(pair.Key, pair.Value < values.Length ? values[pair.Value] : null);
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    DynamicMetaObject IDynamicMetaObjectProvider.GetMetaObject(Expression parameter) => new Meta(parameter, this);

    /// <summary>慢路径取值（索引访问、shape 不匹配时使用）。</summary>
    private object? GetValue(string name)
    {
        var shape = Volatile.Read(ref _shape);
        if (!shape.Slots.TryGetValue(name, out var slot))
        {
            return null;
        }

        var values = Volatile.Read(ref _values);
        return slot < values.Length ? values[slot] : null;
    }

    /// <summary>改值或新增槽位。必须先在锁内取槽再索引：扩容会换掉整个值数组。</summary>
    private void SetValue(string name, object? value)
    {
        lock (_gate)
        {
            var shape = Volatile.Read(ref _shape);
            if (shape.Slots.TryGetValue(name, out var existing))
            {
                // 值数组原地可写，写入走 Volatile。
                var current = Volatile.Read(ref _values);
                Volatile.Write(ref current[existing], value);
                return;
            }

            var next = shape.WithAdded(name);
            var slot = next.Slots[name];
            EnsureCapacity(next.NextSlot);

            var values = Volatile.Read(ref _values);
            Volatile.Write(ref values[slot], value);
            Volatile.Write(ref _shape, next); // 值先就位，再发布 shape。
        }
    }

    private void EnsureCapacity(int slots)
    {
        var values = Volatile.Read(ref _values);
        if (values.Length >= slots)
        {
            return;
        }

        var grown = new object?[Math.Max(slots, Math.Max(8, values.Length * 2))];
        Array.Copy(values, grown, values.Length);
        Volatile.Write(ref _values, grown);
    }

    /// <summary>
    /// 形状：名字 → 槽位的映射 + 有界迁移缓存。不可变，同形状的实例共享同一个对象；
    /// 单节点缓存到 <see cref="MaxTransitions"/> 条后停止缓存。
    /// </summary>
    private sealed class Shape
    {
        /// <summary>单个形状节点最多缓存多少个「加字段 / 减字段」迁移。</summary>
        private const int MaxTransitions = 256;

        internal static readonly Shape Empty = new(new Dictionary<string, int>(StringComparer.Ordinal), 0);

        /// <summary>名字 → 槽位；构造后不再修改（增删都换新实例）。</summary>
        internal readonly Dictionary<string, int> Slots;

        /// <summary>下一个可用槽位（只增不减，删除不复用旧槽）。</summary>
        internal readonly int NextSlot;

        /// <summary>迁移缓存；<c>null</c> 表示已停止缓存（派生出的形状也不再缓存）。</summary>
        private readonly Dictionary<string, Shape>? _added;
        private readonly Dictionary<string, Shape>? _removed;

        internal Shape(Dictionary<string, int> slots, int nextSlot, bool cacheable = true)
        {
            Slots = slots;
            NextSlot = nextSlot;

            if (cacheable)
            {
                _added = new Dictionary<string, Shape>(StringComparer.Ordinal);
                _removed = new Dictionary<string, Shape>(StringComparer.Ordinal);
            }
        }

        internal int Count => Slots.Count;

        /// <summary>迁移到「多一个键」的形状；同名字序列在全进程内复用同一个实例。</summary>
        internal Shape WithAdded(string name)
        {
            if (_added is null)
            {
                return DeriveAdded(name, cacheable: false);
            }

            lock (_added)
            {
                if (_added.TryGetValue(name, out var next))
                {
                    return next;
                }

                // 已经缓存满：本次照常派生，但结果与它的后代都不再登记。
                var cacheable = _added.Count < MaxTransitions;
                next = DeriveAdded(name, cacheable);
                if (cacheable)
                {
                    _added[name] = next;
                }

                return next;
            }
        }

        /// <summary>迁移到「少一个键」的形状；<see cref="NextSlot"/> 保持不变。</summary>
        internal Shape WithRemoved(string name)
        {
            if (_removed is null)
            {
                return DeriveRemoved(name, cacheable: false);
            }

            lock (_removed)
            {
                if (_removed.TryGetValue(name, out var next))
                {
                    return next;
                }

                var cacheable = _removed.Count < MaxTransitions;
                next = DeriveRemoved(name, cacheable);
                if (cacheable)
                {
                    _removed[name] = next;
                }

                return next;
            }
        }

        private Shape DeriveAdded(string name, bool cacheable)
        {
            var slots = new Dictionary<string, int>(Slots, StringComparer.Ordinal) { [name] = NextSlot };
            return new Shape(slots, NextSlot + 1, cacheable);
        }

        private Shape DeriveRemoved(string name, bool cacheable)
        {
            var slots = new Dictionary<string, int>(Slots, StringComparer.Ordinal);
            slots.Remove(name);
            return new Shape(slots, NextSlot, cacheable);
        }
    }

    /// <summary>DLR 绑定：把成员名解析成常量槽位，并用 shape 引用做校验。</summary>
    private sealed class Meta : DynamicMetaObject
    {
        public Meta(Expression expression, AxiosDynamic value)
            : base(expression, BindingRestrictions.Empty, value)
        {
        }

        public override DynamicMetaObject BindGetMember(GetMemberBinder binder)
        {
            var self = Expression.Convert(Expression, typeof(AxiosDynamic));
            var shape = ((AxiosDynamic)Value!)._shape; // 绑定必然拿到实例本身，Value 不会为 null

            // 名字 → 槽位在绑定期解析成常量；shape 是缓存键的一部分，同形状的实例共享规则。
            var restrictions = BindingRestrictions.GetTypeRestriction(Expression, typeof(AxiosDynamic))
                .Merge(BindingRestrictions.GetExpressionRestriction(
                    Expression.ReferenceEqual(Expression.Field(self, s_shapeField), Expression.Constant(shape))));

            Expression body = shape.Slots.TryGetValue(binder.Name, out var slot)
                ? Expression.ArrayIndex(Expression.Field(self, s_valuesField), Expression.Constant(slot))
                : Expression.Constant(null, typeof(object));

            return new DynamicMetaObject(body, restrictions);
        }

        public override DynamicMetaObject BindSetMember(SetMemberBinder binder, DynamicMetaObject value)
        {
            var self = Expression.Convert(Expression, typeof(AxiosDynamic));
            var assigned = Expression.Variable(typeof(object), "value");
            var body = Expression.Block(
                new[] { assigned },
                Expression.Assign(assigned, Expression.Convert(value.Expression, typeof(object))),
                Expression.Call(self, s_setValue, Expression.Constant(binder.Name), assigned),
                assigned);

            return new DynamicMetaObject(body, BindingRestrictions.GetTypeRestriction(Expression, typeof(AxiosDynamic)));
        }

        public override DynamicMetaObject BindGetIndex(GetIndexBinder binder, DynamicMetaObject[] indexes)
        {
            if (indexes.Length != 1)
            {
                return binder.FallbackGetIndex(this, indexes);
            }

            var body = Expression.Call(Expression.Convert(Expression, typeof(AxiosDynamic)), s_getValue, Expression.Convert(indexes[0].Expression, typeof(string)));

            return new DynamicMetaObject(body, BindingRestrictions.GetTypeRestriction(Expression, typeof(AxiosDynamic)));
        }

        public override DynamicMetaObject BindSetIndex(SetIndexBinder binder, DynamicMetaObject[] indexes, DynamicMetaObject value)
        {
            if (indexes.Length != 1)
            {
                return binder.FallbackSetIndex(this, indexes, value);
            }

            var self = Expression.Convert(Expression, typeof(AxiosDynamic));
            var assigned = Expression.Variable(typeof(object), "value");
            var body = Expression.Block(
                new[] { assigned },
                Expression.Assign(assigned, Expression.Convert(value.Expression, typeof(object))),
                Expression.Call(self, s_setValue, Expression.Convert(indexes[0].Expression, typeof(string)), assigned),
                assigned);

            return new DynamicMetaObject(body, BindingRestrictions.GetTypeRestriction(Expression, typeof(AxiosDynamic)));
        }
    }
}
