using System.Reflection;

namespace AxiosNet;

/// <summary>
/// 本机能不能真的跑 HTTP/3（结果在首次访问时算一次，之后是只读字段）。
/// <para>
/// 「<c>HttpVersion = Version30</c> 能编译」不等于「运行时能跑 h3」：h3 只能跑在 QUIC（TLS 1.3 over UDP）上，
/// 而 QUIC 要运行时、系统组件（MsQuic / libmsquic）与网络三者同时具备才有。这个类型把「本机到底行不行」
/// 变成一次可查询的判断，让调用方在<b>发请求之前</b>决定走 h3 还是老实走 h2，
/// 而不是拿一次连接超时去试探。
/// </para>
/// <para>
/// 结论只覆盖<b>本地能力</b>：服务端支不支持 h3 只能发请求才知道（本库不做 Alt-Svc 发现），
/// 所以 <see cref="IsSupported"/> 为 <c>true</c> 也不保证这次一定跑 h3 ——
/// 真实协议版本一律看 <see cref="AxiosResponse.ProtocolVersion"/>。
/// </para>
/// <para>
/// 边界：.NET 7+ 能用 <c>QuicConnection.IsSupported</c> 问出 QUIC 组件在不在；<b>.NET 6 运行时没有这个 API</b>，
/// 只能确认到「开关已打开」，因此那种宿主上返回 <c>true</c> 但缺 MsQuic 时仍会在发送阶段失败（<c>Exact</c> 报
/// <c>ERR_NETWORK</c>、<c>OrLower</c> 降级）。</para>
/// <para>
/// 探测只读不写：h3 的开关是进程级全局状态（见 <see cref="SwitchName"/>），库去改它会让
/// 「谁在什么时候改了它」变成隐式依赖，也让同一个进程里别的 HTTP 客户端跟着变。
/// </para>
/// </summary>
public static class Http3Support
{
    /// <summary>
    /// BCL 的 HTTP/3 开关名：.NET 6 上默认关闭（当时 h3 还是预览特性），.NET 7 起默认打开。
    /// 消费方可以在 runtimeconfig.json 的 <c>configProperties</c> 或环境变量里显式设置。
    /// </summary>
    internal const string SwitchName = "System.Net.SocketsHttpHandler.Http3Support";

    /// <summary>QUIC 能力的权威来源（<c>QuicConnection.IsSupported</c> 是 .NET 7 才有的 API）。</summary>
    private const string QuicConnectionTypeName = "System.Net.Quic.QuicConnection, System.Net.Quic";

    static Http3Support()
    {
        IsSupported = Probe(out var reason);
        UnsupportedReason = reason;
    }

    /// <summary>本机能否跑 HTTP/3。</summary>
    public static bool IsSupported { get; }

    /// <summary>
    /// 不支持时的人类可读原因（支持时为 <c>null</c>）：直接写进错误信息，省得调用方自己猜
    /// 是缺 MsQuic、缺开关，还是被谁显式关掉了。
    /// </summary>
    public static string? UnsupportedReason { get; }

    /// <summary>
    /// 探测顺序是「先看开关、再看 QUIC」：开关关着时 QUIC 再齐全也跑不了 h3，
    /// 反过来 QUIC 缺组件时开关打开也只是让 BCL 去尝试然后失败。
    /// </summary>
    private static bool Probe(out string? reason)
    {
        reason = null;

        var hasSwitch = AppContext.TryGetSwitch(SwitchName, out var switchEnabled);
        if (hasSwitch && !switchEnabled)
        {
            reason = $"HTTP/3 已被显式关闭（AppContext 开关 {SwitchName} = false）";
            return false;
        }

        // .NET 6 运行时：h3 是预览特性，开关不写就是关。这里必须报「不支持」而不是「大概可以」——
        // 报 true 会让调用方以为自己跑在 h3 上，实际被 BCL 静默降级成 h2。
        if (!hasSwitch && Environment.Version.Major < 7)
        {
            reason = $".NET {Environment.Version.Major} 运行时默认不启用 HTTP/3："
                + $"请在 runtimeconfig.json 的 configProperties 里写 \"{SwitchName}\": true，"
                + "或在进程启动前设置环境变量 DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=1";
            return false;
        }

        // h3 没有 QUIC 就是空中楼阁：IsSupported 为 false 说明 MsQuic / libmsquic 没装好（或被系统策略禁用）。
        // 用反射而不是直接调用，是因为目标框架固定 net6.0，直接引用 .NET 7 才有的成员编译不过；
        // .NET 6 的 System.Net.Quic 没有这个属性（找不到就跳过这一步，见下面的返回值）。
        //
        // 整段包 try/catch：这是「探测」，不是业务逻辑。取属性会触发 QUIC 类型的静态构造，理论上可能
        // 抛 DllNotFoundException / TypeInitializationException（MsQuic 装了一半、系统策略禁用等）——
        // 那种情况下探测失败不该变成库的崩溃（静态构造抛异常会让 Http3Support 永久不可用），
        // 一律按「不否定」处理，由 BCL 在发送阶段给出真实错误。
        try
        {
            var quic = Type.GetType(QuicConnectionTypeName, throwOnError: false);
            var isSupportedProperty = quic?.GetProperty("IsSupported", BindingFlags.Public | BindingFlags.Static);
            if (isSupportedProperty?.PropertyType == typeof(bool)
                && isSupportedProperty.GetValue(null) is bool quicSupported
                && !quicSupported)
            {
                reason = "运行时的 QUIC 实现不可用：Windows 10 需要单独安装 MsQuic（Windows 11 / Server 2022+ 自带），"
                    + "多数 Linux 发行版需要 libmsquic，容器里还要放行 UDP 443";
                return false;
            }
        }
        catch
        {
            // 探测本身失败：不下结论（见上），reason 保持 null
        }

        // 到这儿能确认的只有「开关打开、QUIC 没被明确否定」。.NET 6 上无法再往下查（没有 IsSupported），
        // 缺 MsQuic 时会在发送阶段报错（OrLower 则降级）—— 探测不做猜测。
        return true;
    }
}
