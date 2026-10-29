using System.Reflection;

namespace AxiosNet;

/// <summary>
/// 本机能不能跑 HTTP/3（首次访问时算一次，之后是只读字段）；只覆盖本地能力，真实协议版本看 <see cref="AxiosResponse.ProtocolVersion"/>。
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

        // 开关未设置且运行时低于 .NET 7：视为不支持。
        if (!hasSwitch && Environment.Version.Major < 7)
        {
            reason = $".NET {Environment.Version.Major} 运行时默认不启用 HTTP/3："
                + $"请在 runtimeconfig.json 的 configProperties 里写 \"{SwitchName}\": true，"
                + "或在进程启动前设置环境变量 DOTNET_SYSTEM_NET_HTTP_SOCKETSHTTPHANDLER_HTTP3SUPPORT=1";
            return false;
        }

        // 反射读取 .NET 7 才有的成员；探测失败不下结论。
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
            // 探测失败：不下结论，reason 保持 null。
        }

        // 只确认开关打开且 QUIC 未被否定。
        return true;
    }
}
