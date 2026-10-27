using System.Reflection;

namespace AxiosNet.Tests.Connectivity;

/// <summary>
/// 本机 HTTP/3 能力探测：结论是进程级只读快照，但探测规则（开关优先、再看 QUIC）可以单独驱动。
/// <para>
/// 静态构造只跑一次，且受运行时默认值影响；要压到「开关被显式关闭」这条分支，
/// 只能直接驱动私有 <c>Probe</c> 并临时改 <see cref="AppContext"/> 开关（结束即还原）。
/// </para>
/// </summary>
public sealed class Http3SupportTests
{
    [Fact]
    public void PublicSurface_MatchesProbeResult()
    {
        // 支持时不带原因，不支持时必须给出可读原因：两处结论不能自相矛盾
        if (Http3Support.IsSupported)
        {
            Assert.Null(Http3Support.UnsupportedReason);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(Http3Support.UnsupportedReason));
        }
    }

    [Fact]
    public void Probe_WhenSwitchExplicitlyDisabled_ReportsTheSwitch()
    {
        var (supported, reason) = RunProbeWithSwitch(switchEnabled: false);

        Assert.False(supported);
        Assert.Contains("显式关闭", reason);
        Assert.Contains(Http3Support.SwitchName, reason);
    }

    [Fact]
    public void Probe_WhenSwitchEnabled_EitherSupportsOrBlamesQuic()
    {
        var (supported, reason) = RunProbeWithSwitch(switchEnabled: true);

        if (supported)
        {
            // 开关打开且 QUIC 没被明确否定：不下否定结论，也不留原因
            Assert.Null(reason);
        }
        else
        {
            Assert.Contains("QUIC", reason);
        }
    }

    /// <summary>临时把开关设成指定值后驱动私有 <c>Probe</c>，退出时还原。</summary>
    private static (bool Supported, string? Reason) RunProbeWithSwitch(bool switchEnabled)
    {
        // 先摸一下公开属性，确保静态构造按「环境真实状态」算完并缓存；
        // 否则并行跑到这里的其它用例可能撞上我们临时改过的开关。
        _ = Http3Support.IsSupported;

        var hadSwitch = AppContext.TryGetSwitch(Http3Support.SwitchName, out var original);
        AppContext.SetSwitch(Http3Support.SwitchName, switchEnabled);
        try
        {
            var probe = typeof(Http3Support).GetMethod("Probe", BindingFlags.NonPublic | BindingFlags.Static)!;
            var arguments = new object?[] { null };
            var supported = (bool)probe.Invoke(null, arguments)!;

            return (supported, (string?)arguments[0]);
        }
        finally
        {
            // SetSwitch 只能写值不能删键；原本没有这个开关时写回原运行时的默认语义（.NET 7+ 默认开）
            AppContext.SetSwitch(Http3Support.SwitchName, hadSwitch ? original : Environment.Version.Major >= 7);
        }
    }
}
