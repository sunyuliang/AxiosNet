// netstandard2.0 没有 IsExternalInit：补一个空标记类型，让生成器自身可以用 record / init。
namespace System.Runtime.CompilerServices
{
    /// <summary>netstandard2.0 缺 <c>IsExternalInit</c>：补空标记类型，生成器自身才可用 <c>init</c>。</summary>
    internal static class IsExternalInit
    {
    }
}
