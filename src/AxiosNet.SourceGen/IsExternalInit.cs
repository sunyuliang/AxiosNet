// netstandard2.0 没有 IsExternalInit：补一个空标记类型，让生成器自身可以用 record / init。
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
