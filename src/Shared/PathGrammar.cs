using System.Collections.Generic;

namespace AxiosNet.Internal.Spec;

/// <summary>
/// 路径模板的占位符文法，三个消费方共用同一份源文件（运行期绑定、源生成器、CLI 读取器）：
/// 名字由 ASCII 字母或下划线开头，后续为字母、数字或下划线；<c>{{</c> 与 <c>}}</c> 是字面花括号。
/// </summary>
/// <remarks>三个消费方共用同一份扫描实现，保证「编译期不报的模板，运行期也不会报」。</remarks>
internal static class PathGrammar
{
    /// <summary>占位符名的正则片段。</summary>
    public const string Name = "[A-Za-z_][A-Za-z0-9_]*";

    /// <summary>匹配整个占位符的正则模式（<c>{名}</c>），捕获组 1 为名字。</summary>
    public const string Placeholder = "\\{(" + Name + ")\\}";

    /// <summary>能否作为占位符名的首字符。</summary>
    /// <param name="c">待判定的字符。</param>
    public static bool IsNameStart(char c) => c == '_' || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    /// <summary>能否作为占位符名的后续字符（首字符集合加数字）。</summary>
    /// <param name="c">待判定的字符。</param>
    public static bool IsNameChar(char c) => IsNameStart(c) || (c >= '0' && c <= '9');

    /// <summary>模板里的一段是否正好是一个合法的占位符名。</summary>
    /// <param name="template">路由 / 路径模板。</param>
    /// <param name="start">名字起点。</param>
    /// <param name="length">名字长度。</param>
    /// <returns>合法返回 <c>true</c>；空名 / 约束表达式等非法形态返回 <c>false</c>。</returns>
    public static bool IsName(string template, int start, int length)
    {
        if (length <= 0 || !IsNameStart(template[start]))
        {
            return false;
        }

        for (var i = start + 1; i < start + length; i++)
        {
            if (!IsNameChar(template[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>按本语法扫描模板：<c>{名字}</c> 是占位符，<c>{{</c> / <c>}}</c> 是字面花括号，其余花括号视为无法识别。</summary>
    /// <param name="template">路由 / 路径模板。</param>
    /// <param name="tokens">收集到的占位符，按出现顺序追加（含无法识别花括号之后的占位符）。</param>
    /// <returns>第一个无法识别的花括号所在下标；<c>-1</c> 表示模板里没有裸花括号。</returns>
    public static int Scan(string template, List<Token> tokens)
    {
        var index = 0;
        var stray = -1;

        while (index < template.Length)
        {
            var c = template[index];

            if (c == '{')
            {
                // "{{" 是字面花括号，连同后面那个字符一起跳过。
                if (index + 1 < template.Length && template[index + 1] == '{')
                {
                    index += 2;
                    continue;
                }

                var close = template.IndexOf('}', index + 1);
                var length = close < 0 ? -1 : close - index - 1;
                if (length > 0 && IsName(template, index + 1, length))
                {
                    tokens.Add(new Token(template.Substring(index + 1, length), index, close - index + 1));
                    index = close + 1;
                    continue;
                }

                // 认不出来（空名 / 约束表达式 / 没有配对的右花括号）：记下第一处，继续扫后面的占位符。
                stray = stray < 0 ? index : stray;
                index++;
                continue;
            }

            if (c == '}')
            {
                if (index + 1 < template.Length && template[index + 1] == '}')
                {
                    index += 2;
                    continue;
                }

                stray = stray < 0 ? index : stray;
                index++;
                continue;
            }

            index++;
        }

        return stray;
    }

    /// <summary>模板里的一个占位符：名字与它在模板中的区间（含两朵花括号）。</summary>
    public readonly struct Token
    {
        /// <summary>记一个占位符。</summary>
        /// <param name="name">占位符名。</param>
        /// <param name="start">起点下标（指向左花括号）。</param>
        /// <param name="length">总长度（含两朵花括号）。</param>
        public Token(string name, int start, int length)
        {
            Name = name;
            Start = start;
            Length = length;
        }

        /// <summary>占位符名（不含花括号）。</summary>
        public string Name { get; }

        /// <summary>在模板中的起点（指向左花括号）。</summary>
        public int Start { get; }

        /// <summary>在模板中的总长度（含两朵花括号）。</summary>
        public int Length { get; }
    }
}
