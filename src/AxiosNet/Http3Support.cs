using System.Reflection;

namespace AxiosNet;

/// <summary>
/// 本机能不能真的跑 HTTP/3（结果在首次访问时算一次，之后是只读字段）。
/// 结论只覆盖本地能力：服务端支不支持 h3 只能发请求才知道，真实协议版本一律看 <see cref="AxiosResponse.ProtocolVersion"/>。
/// 探测只读不写，不会去改进程级的 h3 开关。
/// </summary>
public static class Http3Support
{
    /// <summary>BCL 的 HTTP/3 开关名：.NET 6 上默认关闭，.NET 7 起默认打开。</summary>
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

    /// <summary>不支持时的人类可读原因（支持时为 <c>null</c>），直接写进错误信息。</summary>
    public static string? UnsupportedReason { get; }

    /// <summary>探测顺序：先看开关，再看 QUIC。</summary>
    private static bool Probe(out string? reason)
    {
        reason = null;

        var hasSwitch = AppContext.TryGetSwitch(SwitchName, out var switchEnabled);
        if (hasSwitch && !switchEnabled)
        {
            reason = $"HTTP/3 已被显式关闭（AppContext 开关 {SwitchName} = false）";
            return false;
        }

        // .NET 6 运行时 h3 是预览特性，开关不写就是关，必须报「不支持」。
        if (!hasSwitch && Environment.Version.Major < 7)
        {
            reason = $".NET {Environment.Version.Major} 运行时默认不启用 HTTP/3："
                + $"请在 runtimeconfig.json 的 configProperties 里写 \"{SwitchName}\": true，"
                + "或在进程启动前设置环境变量 DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=1";
            return false;
        }

        // 反射读取：目标框架固定 net6.0，直接引用 .NET 7 才有的成员编译不过；探测失败不下结论。
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

        // 只能确认「开关打开、QUIC 没被明确否定」，缺组件时会在发送阶段报错。
        return true;
    }
}
