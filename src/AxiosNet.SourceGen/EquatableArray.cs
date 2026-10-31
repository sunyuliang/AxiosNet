using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace AxiosNet.SourceGen
{
    /// <summary>增量管线的值相等数组：按元素比较，避免每次编译都因为数组引用不同而重新生成。</summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        /// <summary>空实例；与 <c>default</c> 在相等性上等价（比较时 <c>default</c> 的 <see cref="ImmutableArray{T}"/> 按空数组处理）。</summary>
        public static readonly EquatableArray<T> Empty = new EquatableArray<T>(ImmutableArray<T>.Empty);

        private readonly ImmutableArray<T> _items;

        /// <summary>直接包裹传入数组：不拷贝、不规范化；<c>default(ImmutableArray&lt;T&gt;)</c> 被当作空数组。</summary>
        public EquatableArray(ImmutableArray<T> items)
        {
            _items = items;
        }

        /// <summary>元素数；底层是默认数组时返回 0（不同于 <see cref="ImmutableArray{T}.Length"/> 会抛）。</summary>
        public int Length => _items.IsDefault ? 0 : _items.Length;

        /// <summary>按 0 基下标取元素；底层是默认数组时会抛，先用 <see cref="Length"/> 判空。</summary>
        public T this[int index] => _items[index];

        /// <summary>按顺序逐元素比较；<c>default</c> 与空实例相等。</summary>
        public bool Equals(EquatableArray<T> other)
        {
            var left = _items.IsDefault ? ImmutableArray<T>.Empty : _items;
            var right = other._items.IsDefault ? ImmutableArray<T>.Empty : other._items;
            if (left.Length != right.Length)
            {
                return false;
            }

            for (var i = 0; i < left.Length; i++)
            {
                if (!left[i].Equals(right[i]))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>类型匹配后转发给 <see cref="Equals(EquatableArray{T})"/>。</summary>
        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        /// <summary>按 17/31 顺序累积；底层是默认数组时返回 0（<c>default</c> 与 <see cref="Empty"/> 相等，哈希不同是已知取舍）。</summary>
        public override int GetHashCode()
        {
            if (_items.IsDefault)
            {
                return 0;
            }

            var hash = 17;
            foreach (var item in _items)
            {
                hash = (hash * 31) + item.GetHashCode();
            }

            return hash;
        }

        /// <summary>逐元素产出；对 <c>default</c> 也能安全 <c>foreach</c>。</summary>
        public IEnumerator<T> GetEnumerator()
        {
            var items = _items.IsDefault ? ImmutableArray<T>.Empty : _items;
            for (var i = 0; i < items.Length; i++)
            {
                yield return items[i];
            }
        }

        /// <summary>非泛型枚举桥接。</summary>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>隐式包裹 <see cref="ImmutableArray{T}"/>（不拷贝）。</summary>
        public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new EquatableArray<T>(items);
    }
}
