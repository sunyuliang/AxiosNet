using Microsoft.CodeAnalysis.CSharp;

namespace AxiosNet.SourceGen
{
    /// <summary>生成代码里的字面量：字符串的转义与引号交给 Roslyn，可空值用 <c>null</c> 表示。</summary>
    internal static class CodeLiteral
    {
        /// <summary>把字符串写成带引号并转义的 C# 字面量。</summary>
        /// <param name="value">原始字符串。</param>
        /// <returns>可直接拼进生成代码的字面量。</returns>
        public static string Of(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

        /// <summary>可空字符串的字面量：<c>null</c> 写成 <c>null</c>，其余同 <see cref="Of"/>。</summary>
        /// <param name="value">原始字符串，可为 <c>null</c>。</param>
        /// <returns>可直接拼进生成代码的字面量。</returns>
        public static string OrNull(string? value) => value is null ? "null" : Of(value);
    }
}
