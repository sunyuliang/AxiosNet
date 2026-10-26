using System.Collections;
using System.Dynamic;
using System.Linq.Expressions;
using System.Reflection;

namespace AxiosNet;

/// <summary>
/// dynamic 响应（<see cref="ResponseType.Dynamic"/>）的**对象节点**：无需 DTO 即可 <c>data.name</c>、
/// <c>data["name"]</c> 访问，缺省成员返回 <c>null</c>（不抛，对齐 JS 的 <c>undefined</c>），运行期可随时增删属性。
/// <para>
/// 同时实现 <see cref="IDictionary{TKey,TValue}"/>：可以原样再当 <c>Params</c>/请求体发出去，
/// <c>System.Text.Json</c> 也把它序列化成普通 JSON 对象（<c>{"name":"alice"}</c>）而不是键值对数组。
/// </para>
/// <para>
/// 值映射（与 <see cref="Internal.DynamicJson"/> 一致）：字符串/布尔原样，整数为 <see cref="long"/>，
/// 浮点为 <see cref="double"/>，JSON <c>null</c> 保持 <c>null</c>；数组节点是 <c>List&lt;object?&gt;</c>。
/// </para>
/// <para>
/// 性能：成员访问在 DLR 绑定期把「名字 → 槽位」固化成常量，命中后热路径只有一次 shape 引用比较 + 一次数组下标，
/// 与 <c>ExpandoObject</c> 同构（实测持平或略快）。shape 是 DLR 规则的缓存键：同形状的实例共用一条规则，
/// 不同形状各自一条，因此运行期增删属性、异构数组都不会让已有规则失效。
/// </para>
/// <para>
/// <b>新增字段是 O(N²)</b>：每次新增都要复制一份「名字 → 槽位」字典并扩容值数组，逐条建 N 个字段合计复制 N 次。
/// 实测<b>冷启动</b>建 100 字段约 232 KB、1000 字段约 <b>15.6 MB</b> 垃圾；同一条字段名链第二次建只付 108 KB
/// （形状迁移缓存命中），所以这笔代价是「每种字段名组合一次」而不是「每个对象一次」。
/// 字段名随数据变化（把 ID 当键）时会撞上迁移缓存上限、每次都按冷启动付 —— 字段数上百请改用 DTO / 字典，
/// 本类型面向的是「响应结构不固定、字段数十个以内」的场景。
/// </para>
/// <para>
/// <b>并发语义是「键快照 + 值实时」，不是整体快照</b>：shape 走写时复制 + 原子发布，读路径完全无锁；
/// 但<b>值数组是原地可写的</b>（DLR 把槽位固化成常量下标，数组必须稳定，不能每次写都换新数组）。
/// 后果有两条，都是刻意的：一是另一个线程删除了某个键之后，<b>已经捕获旧 shape 的枚举器</b>仍会枚举到该键，
/// 但值为 <c>null</c>（删除时主动断开引用，否则被删的大对象会被永不回收的槽位长期持有）；
/// 二是「换数组」的写入（<see cref="Clear"/>、扩容）对在途枚举器不可见，它继续读自己捕获的旧数组。
/// 写入本身不会抛异常，也不会读到撕裂的值——整数/引用都是整体替换。
/// </para>
/// <para>
/// 注意：动态成员优先于 CLR 成员——<c>data.Count</c> 读的是 JSON 里名为 <c>Count</c> 的字段（不存在则为 <c>null</c>），
/// 要字典语义请显式转换：<c>((IDictionary&lt;string, object?&gt;)data).Count</c>。
/// </para>
/// </summary>
public sealed class AxiosDynamic : IDynamicMetaObjectProvider, IDictionary<string, object?>
{
    private static readonly FieldInfo s_shapeField =
        typeof(AxiosDynamic).GetField(nameof(_shape), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo s_valuesField =
        typeof(AxiosDynamic).GetField(nameof(_values), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo s_getValue =
        typeof(AxiosDynamic).GetMethod(nameof(GetValue), BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly MethodInfo s_setValue =
        typeof(AxiosDynamic).GetMethod(nameof(SetValue), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>写入串行化；读不加锁，靠「先发布值数组、再发布 shape」的顺序保证不越界。</summary>
    private readonly object _gate = new();

    /// <summary>当前形状（不可变，同形状实例共享同一个 <see cref="Shape"/>，因此 DLR 规则可跨实例复用）。</summary>
    private Shape _shape = Shape.Empty;

    /// <summary>按槽位存值；扩容走整数组替换（COW），永不原地改长度。</summary>
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

    /// <summary>
    /// 索引访问：键不存在返回 <c>null</c>；赋值对已存在的键改值、对不存在的键新增槽位。
    /// </summary>
    public object? this[string key]
    {
        get => GetValue(key);
        set => SetValue(key, value);
    }

    /// <summary>属性个数（按 JSON 字段计，不含 CLR 成员）。</summary>
    public int Count => Volatile.Read(ref _shape).Count;

    /// <summary>
    /// 键集合（按插入顺序的快照）。
    /// <para>不能直接把 <c>Shape.Slots.Keys</c> 交出去：那是共享形状里的可变视图，
    /// 调用方一个 <c>Remove</c> 就会改坏同形状的所有实例。与 <see cref="Values"/> 一样给快照。</para>
    /// </summary>
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
            // 顺序与 Remove 一致：先撤销可见性（shape 换成空），再回收值数组。
            // 反序的话，「所有键都还在、但每个值都是 null」会成为可观测的中间态。
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

    /// <summary>删除键；返回是否真的删掉了。槽位不复用，被删槽位永久作废（避免与已绑定的常量槽位冲突）。</summary>
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

            // 与「新增」相反的顺序：先撤销可见性，再回收引用。
            //
            // 这不能把「键存在但值为 null」彻底消灭（读者在两次读之间被抢占时仍可能撞上），
            // 但能把触发条件从「跨一个事件」收紧到「同时跨两个事件」：
            // 反序时（先置 null、后换 shape）只要读者的 shape 读落在 [置 null, 换 shape] 这段里，
            // 哪怕两次读紧挨着也会读到 null；正序时，紧挨着的两次读不可能既早于换 shape 又晚于置 null。
            Volatile.Write(ref _shape, shape.WithRemoved(key));

            if (slot < values.Length)
            {
                // 断开引用，避免已删除的值被数组继续持有。代价是「已捕获旧 shape 的枚举器」
                // 之后读这个键会得到 null 而不是旧值——值数组不是快照，见类注释的「并发」一节。
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

    /// <summary>
    /// 按插入顺序枚举键值对（取 shape 快照，枚举期间写入不会抛）。
    /// <para>
    /// <b>键集合是快照，值不是</b>：枚举期间被删除的键仍会出现，但值为 <c>null</c>
    /// （删除会主动断开引用）。需要「键值一起定格」请在枚举前自行 <see cref="CopyTo"/>。
    /// </para>
    /// <para>
    /// 反过来，<b>换数组的写入对在途枚举器不可见</b>：枚举器在首次 <c>MoveNext</c> 时连值数组引用一起捕获了，
    /// 所以 <see cref="Clear"/> 之后它看到的仍是完整的旧内容（键和值都是旧的），而不是一堆 <c>null</c>。
    /// 能看见的只有原地写入 —— 改值与删除断引用；两者都不改变数组长度，因此不会越界。
    /// </para>
    /// </summary>
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
                // 值数组是「原地可写」的（DLR 把槽位固化成常量下标，数组必须稳定），
                // 但写必须是 Volatile：读侧全部走 Volatile.Read，普通写没有 release 语义，
                // 读者能否看到这次更新在形式上没有任何保证。
                var current = Volatile.Read(ref _values);
                Volatile.Write(ref current[existing], value);
                return;
            }

            var next = shape.WithAdded(name);
            var slot = next.Slots[name];
            EnsureCapacity(next.NextSlot);

            var values = Volatile.Read(ref _values);
            Volatile.Write(ref values[slot], value);
            Volatile.Write(ref _shape, next); // 值先就位，再发布 shape：并发读不会看到「新 shape + 旧数组」
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
    /// 形状：名字 → 槽位的映射 + 迁移缓存。不可变，同形状的实例共享同一个对象，
    /// 因此 DLR 规则里固化的 shape 常量与槽位对同形状的所有实例都成立。
    /// <para>
    /// <b>迁移缓存必须有界</b>：<see cref="Empty"/> 是静态根，它缓存的每个子形状又会缓存自己的子形状，
    /// 整张图从静态根永久可达、GC 收不掉。字段名有限（正常 JSON）时图很小；但如果字段名是数据本身
    /// （把 ID 当键、逐条响应各不同），无界缓存会把每条响应都钉在内存里。
    /// 因此单节点缓存到 <see cref="MaxTransitions"/> 条后<b>停止缓存并传递下去</b>：
    /// 之后派生的形状不再登记，图规模就此截断。
    /// </para>
    /// <para>
    /// 停止缓存的代价只是「这些形状的实例各自持有一条 DLR 规则」，不影响正确性；
    /// 而正常场景（字段名重复出现）永远碰不到上限，跨实例复用照旧。
    /// </para>
    /// </summary>
    private sealed class Shape
    {
        /// <summary>单个形状节点最多缓存多少个「加字段 / 减字段」迁移。</summary>
        private const int MaxTransitions = 256;

        internal static readonly Shape Empty = new(new Dictionary<string, int>(StringComparer.Ordinal), 0);

        /// <summary>名字 → 槽位；构造后不再修改（增删都换新实例）。</summary>
        internal readonly Dictionary<string, int> Slots;

        /// <summary>下一个可用槽位（只增不减；删除不复用旧槽，避免已绑定的常量槽位读到别的字段）。</summary>
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

    /// <summary>
    /// DLR 绑定：把成员名解析成常量槽位，并用 shape 引用做校验。
    /// </summary>
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

            // 名字 → 槽位在绑定期解析成常量。形状（shape）是缓存键的一部分：同形状的实例共享规则，
            // 不同形状各自拥有一条，因此「运行期新增属性」「异构数组（有的对象少几个字段）」都只需
            // 首次多绑一次，之后各自命中——不会出现「一条规则被别的形状覆盖」或「defer 层层嵌套」。
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

            var body = Expression.Call(
                Expression.Convert(Expression, typeof(AxiosDynamic)),
                s_getValue,
                Expression.Convert(indexes[0].Expression, typeof(string)));

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
                Expression.Call(
                    self,
                    s_setValue,
                    Expression.Convert(indexes[0].Expression, typeof(string)),
                    assigned),
                assigned);

            return new DynamicMetaObject(body, BindingRestrictions.GetTypeRestriction(Expression, typeof(AxiosDynamic)));
        }
    }
}
