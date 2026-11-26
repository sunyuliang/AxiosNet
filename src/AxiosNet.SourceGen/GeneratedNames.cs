using System.Text;
using Microsoft.CodeAnalysis.CSharp;

namespace AxiosNet.SourceGen;

/// <summary>生成代码里的标识符工具：类名合成、关键字转义、稳定哈希。</summary>
/// <remarks>
/// 类名 = 前缀 + 净化后的种子（截到 <c>maxPrefix</c> 长度）+ 64 位哈希。
/// 净化与哈希的输入可以不同：接口代理名按显示名净化、按全限定名取哈希，两者混用会让产物在两台机器上不一致。
/// </remarks>
internal static class GeneratedNames
{
    /// <summary>拼一个生成类的名字：非字母数字字符换下划线，尾部拼 64 位 FNV-1a 哈希区分同前缀类型。</summary>
    /// <param name="prefix">名字前缀（如 <c>__AxiosProxy_</c>），原样拼在最前面。</param>
    /// <param name="source">用于净化成名字主体的字符串。</param>
    /// <param name="hashKey">参与哈希的字符串；与 <paramref name="source"/> 相同即为同一份输入。</param>
    /// <param name="maxPrefix">名字主体的长度上限，超出部分直接截断。</param>
    /// <returns>可直接写进源码的标识符。</returns>
    internal static string Make(string prefix, string source, string hashKey, int maxPrefix)
    {
        var builder = new StringBuilder(source.Length);
        foreach (var character in source)
        {
            if (builder.Length >= maxPrefix)
            {
                break;
            }

            builder.Append(char.IsLetterOrDigit(character) || character == '_' ? character : '_');
        }

        return prefix + builder + "_" + Fnv1a(hashKey).ToString("x16");
    }

    /// <summary>关键字标识符加 <c>@</c> 前缀，非关键字原样返回。</summary>
    /// <param name="name">原始标识符。</param>
    /// <returns>可直接写进源码的标识符。</returns>
    internal static string EscapeIdentifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None
           || SyntaxFacts.GetContextualKeywordKind(name) != SyntaxKind.None
            ? "@" + name
            : name;

    /// <summary>FNV-1a 64 位哈希：给类名拼稳定后缀，同名不同命名空间不会撞车。</summary>
    /// <param name="value">参与哈希的字符串。</param>
    /// <returns>64 位哈希值。</returns>
    private static ulong Fnv1a(string value)
    {
        var hash = 14695981039346656037UL;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 1099511628211UL;
        }

        return hash;
    }
}
