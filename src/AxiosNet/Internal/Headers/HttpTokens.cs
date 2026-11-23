namespace AxiosNet.Internal.Headers;

/// <summary>RFC 7230 <c>tchar</c> 字符判定：头名与媒体类型 token 共用一份定义。</summary>
internal static class HttpTokens
{
    /// <summary>是否为 token 字符（ASCII 字母 / 数字与 <c>!#$%&amp;'*+-.^_`|~</c>）；非 ASCII 一律为 <c>false</c>。</summary>
    internal static bool IsChar(char c) => c < 128 && Chars[c];

    /// <summary><c>tchar</c> 查表（128 项）。</summary>
    private static readonly bool[] Chars = CreateTable();

    /// <summary>按 RFC 7230 <c>tchar</c> 造 128 项查表。</summary>
    private static bool[] CreateTable()
    {
        var table = new bool[128];

        for (var c = 'a'; c <= 'z'; c++)
        {
            table[c] = true;
        }

        for (var c = 'A'; c <= 'Z'; c++)
        {
            table[c] = true;
        }

        for (var c = '0'; c <= '9'; c++)
        {
            table[c] = true;
        }

        foreach (var c in "!#$%&'*+-.^_`|~")
        {
            table[c] = true;
        }

        return table;
    }
}
