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
        public static readonly EquatableArray<T> Empty = new EquatableArray<T>(ImmutableArray<T>.Empty);

        private readonly ImmutableArray<T> _items;

        public EquatableArray(ImmutableArray<T> items)
        {
            _items = items;
        }

        public int Length => _items.IsDefault ? 0 : _items.Length;

        public T this[int index] => _items[index];

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

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

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

        public IEnumerator<T> GetEnumerator()
        {
            var items = _items.IsDefault ? ImmutableArray<T>.Empty : _items;
            for (var i = 0; i < items.Length; i++)
            {
                yield return items[i];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public static implicit operator EquatableArray<T>(ImmutableArray<T> items) => new EquatableArray<T>(items);
    }
}
